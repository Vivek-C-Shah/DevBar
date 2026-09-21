using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevBar.Modules.Jarvis.Tools;

/// <summary>
/// How much Jarvis may do without asking. Read and Reversible run straight
/// away; Destructive is spoken back and waits for a "yes". There is no level
/// that bypasses the check for destructive actions.
/// </summary>
internal enum Risk { Read, Reversible, Destructive }

internal abstract class JarvisTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    protected virtual (string Name, string Type, string Description)[] Params => Array.Empty<(string, string, string)>();

    public virtual Risk RiskOf(JsonElement args) => Risk.Read;

    /// <summary>Takes seconds (web, vision): Jarvis says "One moment" first rather than going silent.</summary>
    public virtual bool IsSlow => false;

    /// <summary>
    /// Returns text written by other people (emails, web pages, screens). After such a
    /// tool runs, every further action in that turn needs the user's yes - so a
    /// prompt injection inside an email can't quietly make Jarvis act.
    /// </summary>
    public virtual bool ReadsUntrusted => false;

    /// <summary>What Jarvis says before a Destructive call, e.g. "kill node on port 3000".</summary>
    public virtual string Describe(JsonElement args) => Name.Replace('_', ' ');

    /// <summary>Runs on the UI thread. Returns a short plain-text result for the model.</summary>
    public abstract Task<string> RunAsync(JsonElement args);

    /// <summary>OpenAI "tools" entry. A param name ending in '?' is optional; "a|b|c" as type makes an enum.</summary>
    public JsonObject Schema()
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (rawName, type, desc) in Params)
        {
            bool optional = rawName.EndsWith('?');
            var name = rawName.TrimEnd('?');
            var prop = new JsonObject { ["description"] = desc };
            if (type.Contains('|'))
            {
                prop["type"] = "string";
                prop["enum"] = new JsonArray(type.Split('|').Select(v => (JsonNode)v).ToArray());
            }
            else prop["type"] = type;
            props[name] = prop;
            if (!optional) required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required,
                },
            },
        };
    }

    protected static string Str(JsonElement args, string name, string fallback = "") =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()
            : fallback;

    protected static int? Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        if (v.ValueKind == JsonValueKind.Number) return (int)Math.Round(v.GetDouble());
        return int.TryParse(v.GetString(), out int p) ? p : null;
    }
}
