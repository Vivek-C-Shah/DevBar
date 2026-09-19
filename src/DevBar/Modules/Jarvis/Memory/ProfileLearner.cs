using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevBar.Modules.Jarvis.Brain;

namespace DevBar.Modules.Jarvis.Memory;

/// <summary>
/// After a conversation ends, reads the new transcript lines and updates the
/// fact list — the "learns who you are" part. One small-model request per
/// conversation, in the background, never while you're talking. Anything
/// that looks like a secret is dropped even if the model proposes it.
/// </summary>
internal static partial class ProfileLearner
{
    private static int _running;

    public static async Task LearnAsync(ProviderRouter router, string userName)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var (lines, maxId) = MemoryStore.UnlearnedTurns();
            if (!lines.Any(l => l.Role == "user")) { if (maxId > 0) MemoryStore.MarkLearned(maxId); return; }

            var facts = MemoryStore.Facts(80);
            var (json, provider) = await ProposeAsync(router, userName, lines, facts);
            Apply(json, facts.Where(f => !f.Pinned).Select(f => f.Id).ToHashSet()); // pinned facts are yours, not the learner's
            MemoryStore.MarkLearned(maxId);
            JarvisSession.Trace($"learner ({provider}): {json.Replace('\n', ' ')}");
        }
        catch (Exception ex)
        {
            JarvisSession.Trace("learner failed: " + ex.Message); // turns stay unlearned; next conversation retries
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Asks the model what to change; returns its JSON without applying it.</summary>
    internal static async Task<(string Json, string Provider)> ProposeAsync(
        ProviderRouter router, string userName, List<(string Role, string Text)> lines, List<Fact> facts)
    {
        var name = string.IsNullOrWhiteSpace(userName) ? "the user" : userName;

            var prompt = new StringBuilder();
            prompt.AppendLine("Existing facts (id: text):");
            if (facts.Count == 0) prompt.AppendLine("(none yet)");
            foreach (var f in facts) prompt.AppendLine($"{f.Id}: {f.Text}");
            prompt.AppendLine();
            prompt.AppendLine("New conversation:");
            foreach (var (role, text) in lines)
                prompt.AppendLine($"{(role == "user" ? name : "Assistant")}: {text}");

            var system = $$"""
                You maintain a long-term memory of durable facts about {{name}}, who talks to a voice assistant on their PC.
                From the new conversation, decide what to add, correct or remove. Reply with ONLY this JSON:
                {"add": ["..."], "update": [{"id": 1, "text": "..."}], "remove": [2]}
                Keep: identity, home city/region, job and employer, projects and what they are, languages/frameworks/tools used,
                preferences and dislikes, routines and schedule, people they mention and how they relate, goals.
                Never keep: passwords, API keys, tokens or any secret; one-off commands ("open Chrome", "kill port 3000");
                momentary state (what's playing, which ports are open, the weather); anything about the assistant itself.
                Write each fact as one short sentence starting with "{{name}}". Prefer updating an existing fact over adding a near-duplicate.
                Only record what {{name}} actually said or clearly implied. If nothing is worth keeping, return empty lists.
                """;

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt.ToString() },
            };
            var reply = await router.ChatAsync(messages, new JsonArray(), _ => { }, CancellationToken.None);
            return (reply.Text, reply.Provider);
    }

    internal static void Apply(string json, HashSet<long> knownIds)
    {
        int start = json.IndexOf('{'), end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return;
        using var doc = JsonDocument.Parse(json[start..(end + 1)]);
        var root = doc.RootElement;

        if (root.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.Array)
            foreach (var id in remove.EnumerateArray())
                if (id.TryGetInt64(out long v) && knownIds.Contains(v)) MemoryStore.DeleteFact(v);

        if (root.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Array)
            foreach (var u in update.EnumerateArray())
                if (u.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long id) && knownIds.Contains(id)
                    && u.TryGetProperty("text", out var t) && IsKeepable(t.GetString()))
                    MemoryStore.UpdateFact(id, t.GetString()!);

        if (root.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.Array)
            foreach (var a in add.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && IsKeepable(a.GetString()))
                    MemoryStore.AddFact(a.GetString()!, "learned");
    }

    /// <summary>Belt and braces: short, and nothing shaped like a key/token/password.</summary>
    internal static bool IsKeepable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length is > 5 and < 240 && !SecretLike().IsMatch(text);

    [GeneratedRegex(@"(password|passcode|api[ _-]?key|secret|token)\s*(is|:|=)|\b(sk-|gsk_|AIza|ghp_|xox[bp]-)|[A-Za-z0-9_\-]{28,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLike();
}
