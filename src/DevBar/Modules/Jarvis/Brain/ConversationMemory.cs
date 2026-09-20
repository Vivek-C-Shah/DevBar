using System.Text.Json.Nodes;

namespace DevBar.Modules.Jarvis.Brain;

/// <summary>
/// Short-term memory: the last few exchanges, kept across hotkey presses so
/// "and restart it" works a minute later, forgotten after a quiet spell.
/// Small on purpose — free-tier token-per-minute limits are the real budget.
/// (Long-term "remember that…" facts are Phase 2, in SQLite.)
/// </summary>
internal sealed class ConversationMemory
{
    private const int MaxMessages = 16;
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);

    private readonly List<JsonObject> _messages = new();
    private DateTime _lastActivity = DateTime.MinValue;

    public void AddUser(string text)
    {
        if (DateTime.UtcNow - _lastActivity > ForgetAfter) _messages.Clear();
        Add(new JsonObject { ["role"] = "user", ["content"] = text });
    }

    public void AddAssistant(string text) =>
        Add(new JsonObject { ["role"] = "assistant", ["content"] = text });

    public void AddAssistantToolCalls(string text, List<ToolCall> calls) => Add(new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = text.Length > 0 ? text : null,
        ["tool_calls"] = new JsonArray(calls.Select(c =>
        {
            var call = new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            };
            if (c.Extra != null) call["extra_content"] = c.Extra.DeepClone();
            return (JsonNode)call;
        }).ToArray()),
    });

    public void AddToolResult(string callId, string result) =>
        Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = result });

    public void Clear() => _messages.Clear();

    /// <summary>Removes the last user message and anything after it (a turn that was cut short).</summary>
    public void DropLastExchange()
    {
        int lastUser = _messages.FindLastIndex(m => (string?)m["role"] == "user");
        if (lastUser >= 0) _messages.RemoveRange(lastUser, _messages.Count - lastUser);
    }

    public JsonArray BuildMessages(string systemPrompt)
    {
        var arr = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        foreach (var m in _messages) arr.Add(m.DeepClone());
        return arr;
    }

    private void Add(JsonObject msg)
    {
        _messages.Add(msg);
        _lastActivity = DateTime.UtcNow;

        // Trim from the front, but only ever cut at a user message — an orphaned
        // tool result without its assistant tool_call is rejected by every API.
        while (_messages.Count > MaxMessages)
        {
            int nextUser = _messages.FindIndex(1, m => (string?)m["role"] == "user");
            if (nextUser <= 0) break;
            _messages.RemoveRange(0, nextUser);
        }
    }
}
