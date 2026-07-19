namespace DevBar.Modules.ClipboardHistory;

public sealed class ClipboardEntry
{
    public required string Text { get; init; }
    public DateTime CopiedAt { get; init; } = DateTime.Now;
    public ClipKind Kind { get; init; }

    public static ClipKind Classify(string text)
    {
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return ClipKind.Url;

        var t = text.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[') || t.StartsWith('<') ||
            t.Contains("=>") || t.Contains("function ") || t.Contains("class ") ||
            (text.Contains('\n') && (text.Contains(';') || text.Contains('{'))))
            return ClipKind.Code;

        return ClipKind.Text;
    }
}

public enum ClipKind { Text, Url, Code }
