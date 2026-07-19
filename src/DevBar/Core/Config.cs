using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBar.Core;

public sealed class Config
{
    public int ShowDelayMs { get; set; } = 120;
    /// <summary>Height of the idle sliver, in DIPs.</summary>
    public double SliverHeight { get; set; } = 6;
    /// <summary>Height of the expanded panel, in DIPs.</summary>
    public double ExpandedHeight { get; set; } = 185;
    public bool RememberLastModule { get; set; } = true;
    public string? LastModuleId { get; set; }
    public bool StartPinned { get; set; }
    /// <summary>Module ids to hide, e.g. ["media"].</summary>
    public List<string> DisabledModules { get; set; } = new();
    /// <summary>Explicit module order; unknown ids are ignored, missing ones appended.</summary>
    public List<string> ModuleOrder { get; set; } = new() { "clipboard", "shelf", "claude", "ports", "media" };
    public int ClipboardHistorySize { get; set; } = 25;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBar");

    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpts) ?? new Config();
        }
        catch { /* corrupt config falls back to defaults */ }
        var cfg = new Config();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* never crash over config io */ }
    }
}
