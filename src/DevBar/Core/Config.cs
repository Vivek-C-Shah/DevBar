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
    public List<string> ModuleOrder { get; set; } = new()
        { "jarvis", "clipboard", "shelf", "claude", "ports", "docker", "git", "ci", "media" };
    public int ClipboardHistorySize { get; set; } = 25;

    /// <summary>Repo paths the Git status module watches. Empty by default — opt-in.</summary>
    public List<string> GitWatchedRepos { get; set; } = new();

    /// <summary>Directories/files the Build/CI pulse module watches for a "just built" signal via mtime. Empty by default — opt-in.</summary>
    public List<CiWatchTarget> CiWatchTargets { get; set; } = new();

    /// <summary>Voice assistant settings. API keys are NOT here — see SecretStore.</summary>
    public JarvisConfig Jarvis { get; set; } = new();

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

public sealed class CiWatchTarget
{
    public string Name { get; set; } = "";
    /// <summary>A directory (watches the newest file's mtime) or a single file.</summary>
    public string Path { get; set; } = "";
}

public sealed class JarvisConfig
{
    /// <summary>Global shortcut; press to start listening, press again to stop/interrupt.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>What Jarvis calls you.</summary>
    public string UserName { get; set; } = "Vivek";

    /// <summary>"aura" (Deepgram Aura-2, uses credit) | "piper" (local, free, fast) | "kokoro" (local, free, slower) | "windows" (built-in).</summary>
    public string TtsEngine { get; set; } = "aura";
    public string AuraVoice { get; set; } = "aura-2-draco-en";
    /// <summary>Piper voice id, e.g. en_GB-alan-medium.</summary>
    public string PiperVoice { get; set; } = "en_GB-alan-medium";
    /// <summary>Kokoro speaker name, e.g. bm_george, bm_lewis, am_adam, af_bella.</summary>
    public string KokoroVoice { get; set; } = "bm_george";

    public string SttModel { get; set; } = "nova-3";
    /// <summary>"en", or "multi" for code-switching (e.g. Hinglish).</summary>
    public string SttLanguage { get; set; } = "en";
    /// <summary>
    /// Silence (ms) before Deepgram decides you've finished. Lower answers faster
    /// but may cut you off mid-thought; ~500 suits quick commands, ~800 rambling.
    /// </summary>
    public int SttEndpointingMs { get; set; } = 500;

    /// <summary>
    /// Brain fallback chain, tried in order on rate-limit/outage:
    /// "provider:model" where provider is groq | gemini | openai | anthropic | openrouter | cerebras | ollama.
    /// Groq's free tier limits each model separately (1,000 req/day, 8,000 tokens/min
    /// as of Sept 2026), so two Groq models back to back double the budget.
    /// </summary>
    public List<string> Llm { get; set; } = new()
    {
        "groq:openai/gpt-oss-120b",
        "groq:openai/gpt-oss-20b",
        "gemini:gemini-3.5-flash-lite",
        "gemini:gemini-2.5-flash",
    };

    /// <summary>Models that can see images, for "look at my screen". Tried in order.</summary>
    public List<string> Vision { get; set; } = new()
    {
        "gemini:gemini-3.5-flash-lite",
        "gemini:gemini-2.5-flash",
    };

    /// <summary>Let Jarvis know roughly where you are (Windows location, else IP city).</summary>
    public bool UseLocation { get; set; } = true;
    /// <summary>Optional fixed place, e.g. "Pune, India" — overrides detection (useful on a VPN).</summary>
    public string HomeLocation { get; set; } = "";

    /// <summary>After each conversation, pick out lasting facts about you and remember them.</summary>
    public bool LearnFromConversations { get; set; } = true;

    /// <summary>Talk over Jarvis to interrupt it. Works best with headphones; with speakers it filters out its own voice.</summary>
    public bool BargeIn { get; set; } = true;

    /// <summary>Unprompted heads-ups (Claude Code needs you, build finished): "speak" | "show" | "off".</summary>
    public string Proactive { get; set; } = "speak";
    /// <summary>No speaking (notices still show) during these hours, "HH:mm-HH:mm"; empty = never quiet.</summary>
    public string QuietHours { get; set; } = "23:00-08:00";
    /// <summary>Date of the last conversation — the first one of a day gets offered a brief.</summary>
    public DateTime? LastConversation { get; set; }

    /// <summary>
    /// Say "Hey Jarvis" instead of pressing the hotkey. Off by default: it keeps the
    /// mic open (on-device only, ~0.3% CPU). Listens on AC power only unless WakeWordOnBattery.
    /// </summary>
    public bool WakeWord { get; set; }
    public bool WakeWordOnBattery { get; set; }
    /// <summary>
    /// How eagerly the wake word triggers: "strict" (fewest false wakes),
    /// "balanced" (default), "sensitive" (catches quieter or faster speech, more false wakes).
    /// </summary>
    public string WakeWordSensitivity { get; set; } = "balanced";

    /// <summary>Seconds to keep listening for a follow-up after Jarvis finishes speaking.</summary>
    public int FollowUpSeconds { get; set; } = 6;
}
