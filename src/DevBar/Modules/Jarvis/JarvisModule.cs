using System.Windows;
using System.Windows.Controls;
using DevBar.Core;
using DevBar.Modules.Jarvis.Brain;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;
using DevBar.Modules.Jarvis.Proactive;
using DevBar.Modules.Jarvis.Speech;
using DevBar.Modules.Jarvis.Tools;
using DevBar.Sdk;

namespace DevBar.Modules.Jarvis;

/// <summary>What Jarvis needs from the bar window: to be seen while talking, and to own the hotkey.</summary>
internal interface IJarvisHost
{
    /// <summary>Expand the bar onto the Jarvis tab and keep it open until <see cref="ReleaseJarvis"/>.</summary>
    void HoldOpenForJarvis();
    void ReleaseJarvis();
    /// <summary>Re-register the global shortcut; false if unparseable or taken by another app.</summary>
    bool RebindJarvisHotkey(string hotkey);
    /// <summary>Tint the idle pill while the wake word keeps the mic open — the user can always see it.</summary>
    void SetMicIndicator(bool on);
}

/// <summary>
/// The voice assistant tab. Exception to the module contract, deliberately:
/// a conversation already in progress keeps running if the bar collapses —
/// but nothing at all runs between conversations (the hotkey is a Windows
/// message, not a hook), so idle cost is still zero.
/// </summary>
internal sealed class JarvisModule : IDevBarModule
{
    public string Id => "jarvis";
    public string DisplayName => "Jarvis";
    public string IconGlyph => ""; // Microphone

    public Config Config { get; }
    public JarvisConfig Settings => Config.Jarvis;
    public bool HotkeyRegistered { get; set; }

    public JarvisState State => _session?.State ?? JarvisState.Idle;

    // Forwarded session events, so the card binds once instead of per conversation.
    public event Action<JarvisState>? StateChanged;
    public event Action<string>? Heard;
    public event Action<string>? Reply;
    public event Action<string, bool?>? ToolActivity;
    public event Action<string>? Error;
    public event Action<float>? Level;
    public event Action? SessionStarted;
    public event Action? SettingsChanged;

    private readonly IJarvisHost _host;
    private readonly ConversationMemory _memory = new();
    private readonly ProviderRouter _router;
    private readonly ProviderRouter _vision;
    // Learning is a background chore: cheapest capable models first, so it
    // doesn't eat the main brain's per-minute token budget.
    private readonly ProviderRouter _learner = new(() => new List<string>
    {
        "groq:openai/gpt-oss-20b", "gemini:gemini-3.5-flash-lite", "groq:openai/gpt-oss-120b",
    });
    private IReadOnlyList<IDevBarModule> _modules = Array.Empty<IDevBarModule>();
    private List<JarvisTool>? _tools;
    private JarvisSession? _session;
    private JarvisCard? _card;
    private JarvisSettingsWindow? _settings;
    private JarvisMemoryWindow? _memoryWindow;
    private NoticeCenter? _notices;
    private ClaudeSessionWatcher? _claudeWatcher;
    private BuildWatcher? _buildWatcher;
    private WakeWordListener? _wake;

    // A local voice model holds 100–300MB; keep it for quick follow-up
    // conversations, drop it once Jarvis has been unused for a while.
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(5);
    private System.Windows.Threading.DispatcherTimer? _unloadTimer;

    public JarvisModule(Config config, IJarvisHost host)
    {
        Config = config;
        _host = host;
        _router = new ProviderRouter(() => Settings.Llm);
        _vision = new ProviderRouter(() => Settings.Vision);

        // Reminders set in earlier runs: arm the next one once the bar is up.
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            ReminderScheduler.Start(text => _ = AnnounceAsync(text));
            StartProactive();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Called once all modules exist — some tools read other modules (clipboard history).</summary>
    public void AttachModules(IReadOnlyList<IDevBarModule> modules) => _modules = modules;

    public UserControl BuildCard() => _card ??= new JarvisCard(this);

    public void OnExpanded() => _card?.SetVisible(true);

    public void OnCollapsed() => _card?.SetVisible(false);

    // ---------------- hotkeys ----------------

    public void OnHotkey()
    {
        if (_session is null) _ = StartAsync();
        else _session.HotkeyPressed();
    }

    public void OnCancelKey() => _session?.Stop();

    private async Task StartAsync()
    {
        _unloadTimer?.Stop();
        _wake?.Pause(); // the conversation owns the mic now
        _host.SetMicIndicator(false);
        _tools = BuiltInTools.Create(Config, () => _modules, _vision); // rebuilt each time: Google may have been connected since
        _ = LocationService.GetAsync(Settings); // cached 30 min; ready by the time you finish your sentence
        // Pay connection/model-load costs while Deepgram's socket is connecting, not on the first reply.
        if (Settings.TtsEngine is "piper" or "kokoro")
        {
            var voice = LocalTts.Find(Settings.TtsEngine, Settings.TtsEngine == "kokoro" ? Settings.KokoroVoice : Settings.PiperVoice);
            if (voice.IsInstalled) LocalTts.Warm(voice);
        }
        else if (Settings.TtsEngine == "aura") DeepgramAuraTts.Warm();

        var session = new JarvisSession(Settings, _tools, _router, _memory, Application.Current.Dispatcher);
        session.StateChanged += s => StateChanged?.Invoke(s);
        session.Heard += t => Heard?.Invoke(t);
        session.Reply += t => Reply?.Invoke(t);
        session.ToolActivity += (l, ok) => ToolActivity?.Invoke(l, ok);
        session.Error += e => Error?.Invoke(e);
        session.Level += l => Level?.Invoke(l);
        _session = session;

        _host.HoldOpenForJarvis();
        SessionStarted?.Invoke();
        try
        {
            await session.RunAsync();
        }
        finally
        {
            _session = null;
            StateChanged?.Invoke(JarvisState.Idle);
            _host.ReleaseJarvis();
            ScheduleUnload();
            if (Settings.LearnFromConversations)
                _ = Task.Run(() => ProfileLearner.LearnAsync(_learner, Settings.UserName));
            if (_wake != null)
            {
                _wake.Resume();
                _host.SetMicIndicator(_wake.IsListening);
            }
            Settings.LastConversation = DateTime.Now;
            Config.Save();
            _notices?.FlushDeferred();
        }
    }

    private void ScheduleUnload()
    {
        _unloadTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = UnloadAfter };
        _unloadTimer.Tick -= OnUnloadTick;
        _unloadTimer.Tick += OnUnloadTick;
        _unloadTimer.Stop();
        _unloadTimer.Start();
    }

    private void OnUnloadTick(object? sender, EventArgs e)
    {
        _unloadTimer!.Stop();
        if (_session != null) return;
        LocalTts.Unload();
        GC.Collect(); // hand the audio/network buffers back too — this runs once, minutes after use
    }

    public void ConfirmFromUi(bool yes) => _session?.ConfirmFromUi(yes);

    // ---------------- proactive ----------------

    /// <summary>Event-driven only (file-change notifications) — nothing polls.</summary>
    private void StartProactive()
    {
        var ui = Application.Current.Dispatcher;
        _notices = new NoticeCenter(Settings, DeliverNoticeAsync, () => _session != null);
        try { _claudeWatcher = new ClaudeSessionWatcher(ui, _notices.Notify); } catch { /* folder unwritable */ }
        if (Config.CiWatchTargets.Count > 0) _buildWatcher = new BuildWatcher(Config, ui, _notices.Notify);
        ApplyWakeWord();
    }

    /// <summary>Starts/stops the wake-word listener to match settings.</summary>
    public void ApplyWakeWord()
    {
        if (Settings.WakeWord && WakeWordListener.IsInstalled)
        {
            if (_wake is null)
            {
                _wake = new WakeWordListener(acOnly: !Settings.WakeWordOnBattery);
                _wake.Detected += kw => Application.Current.Dispatcher.BeginInvoke(() => OnWake(kw));
            }
            if (_session is null)
            {
                try { _wake.Start(); }
                catch (Exception ex) { Error?.Invoke("Wake word couldn't start: " + ex.Message); }
            }
        }
        else
        {
            _wake?.Dispose();
            _wake = null;
        }
        _host.SetMicIndicator(_wake?.IsListening == true);
    }

    private void OnWake(string keyword)
    {
        JarvisSession.Trace($"wake word: {keyword}");
        if (_session != null) return;
        _ = JarvisSession.ChimeAsync();
        _ = StartAsync();
    }

    private async Task DeliverNoticeAsync(string text, bool speak, bool show)
    {
        if (speak && !show)
        {
            JarvisSession.Trace($"announce (voice only): {text}");
            await JarvisSession.AnnounceAsync(Settings, text);
            return;
        }
        if (speak)
        {
            await AnnounceAsync(text);
            return;
        }
        // Show-only: the bar drops down with the notice for a few seconds.
        _host.HoldOpenForJarvis();
        Reply?.Invoke(text);
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (_session is null) _host.ReleaseJarvis();
    }

    /// <summary>Debug: push a notice through the same etiquette as real ones.</summary>
    internal void NotifyForTest(string text) => _notices?.Notify(text);

    /// <summary>Debug: run a real session and hand it <paramref name="text"/> as the first utterance.</summary>
    public async Task InjectForTestAsync(string text)
    {
        // "first||second": say the first, then talk over Jarvis with the second once it starts speaking.
        var parts = text.Split("||", 2);
        OnHotkey();
        for (int i = 0; i < 100 && State != JarvisState.Listening; i++) await Task.Delay(100);
        _session?.InjectUtterance(parts[0]);
        if (parts.Length < 2) return;
        for (int i = 0; i < 200 && State != JarvisState.Speaking; i++) await Task.Delay(50);
        await Task.Delay(1200);
        _session?.SimulateHeard(parts[1]);
    }

    /// <summary>Speaks up unprompted (timer finished). Shows the bar while it talks.</summary>
    public async Task AnnounceAsync(string text)
    {
        JarvisSession.Trace($"announce: {text}");
        if (_session != null) { Reply?.Invoke(text); }
        else
        {
            _host.HoldOpenForJarvis();
            Reply?.Invoke(text);
        }
        try { await JarvisSession.AnnounceAsync(Settings, text); }
        finally { if (_session is null) _host.ReleaseJarvis(); }
    }

    public void ForgetConversation() => _memory.Clear();

    // ---------------- settings ----------------

    public void OpenSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new JarvisSettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void OpenMemory()
    {
        if (_memoryWindow is { IsLoaded: true })
        {
            _memoryWindow.Activate();
            return;
        }
        _memoryWindow = new JarvisMemoryWindow(this);
        _memoryWindow.Closed += (_, _) => _memoryWindow = null;
        _memoryWindow.Show();
        _memoryWindow.Activate();
    }

    public bool ApplyHotkey(string hotkey)
    {
        bool ok = _host.RebindJarvisHotkey(hotkey);
        if (ok)
        {
            Settings.Hotkey = hotkey;
            Config.Save();
        }
        else _host.RebindJarvisHotkey(Settings.Hotkey); // restore the old binding
        HotkeyRegistered = ok || HotkeyRegistered;
        SettingsChanged?.Invoke();
        return ok;
    }

    public void SaveSettings()
    {
        Config.Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>Short, human list of what's missing for Jarvis to fully work. Empty when ready.</summary>
    public List<string> MissingSetup()
    {
        var missing = new List<string>();
        if (!SecretStore.Has("deepgram")) missing.Add("Deepgram key (so I can hear you)");
        var brainReady = Settings.Llm.Select(OpenAiCompatibleLlm.Parse).OfType<OpenAiCompatibleLlm>().Any(l => l.HasKey);
        if (!brainReady) missing.Add("a Groq or Gemini key (my brain)");
        if (!HotkeyRegistered) missing.Add($"a free shortcut ({Settings.Hotkey} is taken)");
        return missing;
    }
}
