namespace DevBar.Core;

public static class Fmt
{
    public static string Ago(DateTime utcOrLocal)
    {
        var t = utcOrLocal.Kind == DateTimeKind.Utc ? utcOrLocal.ToLocalTime() : utcOrLocal;
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 5) return "now";
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
