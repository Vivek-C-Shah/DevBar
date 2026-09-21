using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Context;

/// <summary>
/// Answers questions that need the live web - no browser window. Gemini with
/// Google Search grounding does the searching and reading (free tier, ~3s,
/// and it reports its sources); Groq's compound-mini is the last resort.
/// </summary>
internal static class WebSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly string[] GeminiModels = { "gemini-3.5-flash-lite", "gemini-2.5-flash-lite", "gemini-2.5-flash" };

    public static async Task<string> AskAsync(string query, string? whereabouts, CancellationToken ct)
    {
        var errors = new List<string>();
        var prompt = $"""
            Search the web and answer for a voice assistant: {query}
            Be accurate and current; give concrete facts (numbers, dates, names). 3 sentences at most, plain text, no markdown or URLs.
            {(whereabouts is null ? "" : $"If location matters, the user is in {whereabouts}.")}
            Today is {DateTime.Now:d MMMM yyyy}.
            """;

        if (SecretStore.Get("gemini") is { } geminiKey)
        {
            foreach (var model in GeminiModels)
            {
                try { return await GeminiGroundedAsync(geminiKey, model, prompt, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"{model}: {ex.Message}"); }
            }
        }
        if (SecretStore.Get("groq") is { } groqKey)
        {
            try { return await GroqCompoundAsync(groqKey, prompt, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"compound-mini: {ex.Message}"); }
        }
        return errors.Count == 0
            ? "Web search needs a Gemini (or Groq) key."
            : "Web search failed: " + string.Join(" | ", errors);
    }

    private static async Task<string> GeminiGroundedAsync(string key, string model, string prompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } } } },
            ["tools"] = new JsonArray { new JsonObject { ["google_search"] = new JsonObject() } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)resp.StatusCode} {Trim(json)}");

        using var doc = JsonDocument.Parse(json);
        var cand = doc.RootElement.GetProperty("candidates")[0];
        var text = string.Concat(cand.GetProperty("content").GetProperty("parts").EnumerateArray()
            .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("empty answer");

        var sources = new List<string>();
        if (cand.TryGetProperty("groundingMetadata", out var gm) && gm.TryGetProperty("groundingChunks", out var chunks))
            foreach (var c in chunks.EnumerateArray())
                if (c.TryGetProperty("web", out var web) && web.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } s)
                    sources.Add(s);
        return text.Trim() + (sources.Count > 0 ? $" (Sources: {string.Join(", ", sources.Distinct().Take(3))})" : "");
    }

    private static async Task<string> GroqCompoundAsync(string key, string prompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = "groq/compound-mini",
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)resp.StatusCode} {Trim(json)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
