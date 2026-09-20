using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Brain;

/// <param name="Extra">
/// Provider data that must be echoed back verbatim with the call — Gemini 3.x
/// puts a thought_signature here and rejects the follow-up request without it.
/// </param>
internal sealed record ToolCall(string Id, string Name, string ArgumentsJson, JsonNode? Extra = null);

internal sealed record LlmReply(string Text, List<ToolCall> ToolCalls, string Provider, int PromptTokens);

/// <summary>
/// Thrown before the first token for errors worth failing over on
/// (429 rate limit, 5xx, network, missing key). Once text has streamed to the
/// speaker we can't fail over without repeating ourselves, so later errors propagate.
/// </summary>
internal sealed class ProviderUnavailableException(string message) : Exception(message);

/// <summary>
/// Every provider we care about speaks the OpenAI chat-completions dialect —
/// Groq, Gemini (its /openai endpoint), OpenAI, OpenRouter, Cerebras, Ollama,
/// LM Studio — so one streaming client covers all of them.
/// </summary>
internal sealed class OpenAiCompatibleLlm
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly Dictionary<string, (string BaseUrl, string? KeyName)> Providers = new()
    {
        ["groq"] = ("https://api.groq.com/openai/v1/", "groq"),
        ["gemini"] = ("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini"),
        ["openai"] = ("https://api.openai.com/v1/", "openai"),
        ["openrouter"] = ("https://openrouter.ai/api/v1/", "openrouter"),
        ["cerebras"] = ("https://api.cerebras.ai/v1/", "cerebras"),
        ["anthropic"] = ("https://api.anthropic.com/v1/", "anthropic"),
        ["ollama"] = ("http://localhost:11434/v1/", null),
    };

    public string Provider { get; }
    public string Model { get; }
    public string Label => $"{Provider}:{Model}";

    private OpenAiCompatibleLlm(string provider, string model)
    {
        Provider = provider;
        Model = model;
    }

    /// <summary>"groq:llama-3.3-70b-versatile" → client, or null if the provider is unknown.</summary>
    public static OpenAiCompatibleLlm? Parse(string spec)
    {
        int i = spec.IndexOf(':');
        if (i <= 0) return null;
        var provider = spec[..i].Trim().ToLowerInvariant();
        return Providers.ContainsKey(provider) ? new OpenAiCompatibleLlm(provider, spec[(i + 1)..].Trim()) : null;
    }

    public bool HasKey => Providers[Provider].KeyName is not { } k || SecretStore.Has(k);

    public async Task<LlmReply> ChatAsync(JsonArray messages, JsonArray tools, Action<string> onText, CancellationToken ct)
    {
        var (baseUrl, keyName) = Providers[Provider];
        string? key = keyName is null ? null : SecretStore.Get(keyName);
        if (keyName != null && key is null) throw new ProviderUnavailableException($"No {Provider} API key.");

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages.DeepClone(),
            ["stream"] = true,
            ["temperature"] = 0.5,
            // Reasoning models (gpt-oss, Gemini 2.5+) spend part of this on hidden thinking.
            ["max_tokens"] = 1024,
        };
        if (Provider is "groq" or "openai" or "openrouter" or "cerebras")
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        // Voice needs the first word fast, not deep thought.
        if (Model.StartsWith("openai/gpt-oss", StringComparison.OrdinalIgnoreCase) || Provider == "gemini")
            body["reasoning_effort"] = "low";

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage resp;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(12));
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token);
        }
        catch (HttpRequestException ex) { throw new ProviderUnavailableException($"{Label} unreachable: {ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderUnavailableException($"{Label} timed out."); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                if (err.Length > 300) err = err[..300];
                var msg = $"{Label} returned {(int)resp.StatusCode}: {err}";
                if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized
                        or HttpStatusCode.Forbidden or HttpStatusCode.NotFound || (int)resp.StatusCode >= 500
                    // Groq rejects a malformed tool call from its model with a 400 — another model may do better.
                    || err.Contains("tool_use_failed", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderUnavailableException(msg);
                throw new InvalidOperationException(msg);
            }
            return await ReadStreamAsync(resp, onText, ct);
        }
    }

    private async Task<LlmReply> ReadStreamAsync(HttpResponseMessage resp, Action<string> onText, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args, JsonNode? Extra)>();
        int promptTokens = 0;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var piece = c.GetString()!;
                if (piece.Length > 0)
                {
                    text.Append(piece);
                    onText(piece);
                }
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    // Gemini's compat layer omits "index" and sends each call whole.
                    int index = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : calls.Count;
                    if (!calls.TryGetValue(index, out var entry))
                        entry = ("", "", new StringBuilder(), null);
                    if (tc.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idStr) entry.Id = idStr;
                    if (tc.TryGetProperty("extra_content", out var extra)) entry.Extra = JsonNode.Parse(extra.GetRawText());
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } name) entry.Name = name;
                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) entry.Args.Append(a.GetString());
                    }
                    calls[index] = entry;
                }
            }
        }

        var toolCalls = calls.Values
            .Where(v => v.Name.Length > 0)
            .Select((v, i) => new ToolCall(v.Id.Length > 0 ? v.Id : $"call_{i}", v.Name, v.Args.Length > 0 ? v.Args.ToString() : "{}", v.Extra))
            .ToList();
        return new LlmReply(text.ToString(), toolCalls, Label, promptTokens);
    }
}

/// <summary>
/// Walks the configured chain (Groq → Gemini → …) and uses the first
/// provider that answers. A provider that just rate-limited us is skipped
/// for a minute so every turn doesn't pay the 429 round-trip again.
/// </summary>
internal sealed class ProviderRouter(Func<List<string>> chain)
{
    private readonly Dictionary<string, DateTime> _coolDown = new();

    public async Task<LlmReply> ChatAsync(JsonArray messages, JsonArray tools, Action<string> onText, CancellationToken ct)
    {
        var errors = new List<string>();
        var candidates = chain().Select(OpenAiCompatibleLlm.Parse).OfType<OpenAiCompatibleLlm>().Where(l => l.HasKey).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No brain configured — add a Groq or Gemini key in Jarvis settings.");

        var ready = candidates.Where(l => !_coolDown.TryGetValue(l.Label, out var until) || until < DateTime.UtcNow).ToList();
        if (ready.Count == 0) ready = candidates; // everything cooling: try anyway rather than refuse

        foreach (var llm in ready)
        {
            bool started = false;
            try
            {
                return await llm.ChatAsync(messages, tools, t => { started = true; onText(t); }, ct);
            }
            catch (ProviderUnavailableException ex) when (!started)
            {
                errors.Add(ex.Message);
                _coolDown[llm.Label] = DateTime.UtcNow.AddSeconds(60);
            }
        }
        throw new InvalidOperationException("Every brain failed: " + string.Join(" | ", errors));
    }
}
