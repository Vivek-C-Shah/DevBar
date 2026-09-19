using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevBar.Core;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Speech;

namespace DevBar.Modules.Jarvis;

/// <summary>
/// A normal, focusable window — the bar itself is WS_EX_NOACTIVATE and can
/// never take keyboard input, which shortcut capture and key entry need.
/// </summary>
public partial class JarvisSettingsWindow : Window
{
    private static readonly (string Id, string Label)[] AuraVoices =
    {
        ("aura-2-draco-en", "Draco · British"),
        ("aura-2-pandora-en", "Pandora · British"),
        ("aura-2-hyperion-en", "Hyperion · Australian"),
        ("aura-2-orpheus-en", "Orpheus"),
        ("aura-2-zeus-en", "Zeus · deep"),
        ("aura-2-athena-en", "Athena"),
    };

    private static readonly (string Name, string Label, string Hint)[] Keys =
    {
        ("deepgram", "Deepgram", "hearing + Aura voice"),
        ("groq", "Groq", "free · fast brain"),
        ("gemini", "Gemini", "free · fallback brain"),
        ("openai", "OpenAI", "optional, paid"),
        ("anthropic", "Anthropic", "optional, paid"),
        ("openrouter", "OpenRouter", "optional"),
        ("cerebras", "Cerebras", "optional, free tier"),
    };

    private readonly JarvisModule _module;
    private readonly Dictionary<string, PasswordBox> _keyBoxes = new();
    private string _engine;
    private string _auraVoice;
    private string _kokoroVoice;
    private string _piperVoice;
    private string _pendingHotkey;
    private CancellationTokenSource? _downloadCts;

    internal JarvisSettingsWindow(JarvisModule module)
    {
        _module = module;
        InitializeComponent();

        var s = module.Settings;
        _engine = s.TtsEngine;
        _auraVoice = s.AuraVoice;
        _kokoroVoice = s.KokoroVoice;
        _piperVoice = s.PiperVoice;
        _pendingHotkey = s.Hotkey;

        HotkeyBox.Text = s.Hotkey;
        NameBox.Text = s.UserName;
        HomeBox.Text = s.HomeLocation;
        UseLocationBox.IsChecked = s.UseLocation;
        LearnBox.IsChecked = s.LearnFromConversations;
        BargeInBox.IsChecked = s.BargeIn;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _ = ShowLocationAsync();
        BrainChain.Text = string.Join(" → ", s.Llm);
        BuildKeyRows();

        (s.TtsEngine switch { "piper" => EnginePiper, "kokoro" => EngineKokoro, "windows" => EngineWindows, _ => EngineAura }).IsChecked = true;
        Closed += (_, _) => _downloadCts?.Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocused) Close();
        };
    }

    // ---------------- shortcut capture ----------------

    private void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        HotkeyStatus.Text = "Press the combination now (e.g. Ctrl+Alt+Space)…";

    private void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        HotkeyStatus.Text = _pendingHotkey == _module.Settings.Hotkey ? "Click the box, then press the new combination." : "Press Apply to use it.";

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab) { e.Handled = false; return; }
        var text = GlobalHotkey.Format(Keyboard.Modifiers, key);
        if (text is null) return;
        if (!text.Contains('+') && !(key >= Key.F1 && key <= Key.F24))
        {
            HotkeyStatus.Text = "Use at least one modifier (Ctrl / Alt / Shift / Win), or an F-key.";
            return;
        }
        _pendingHotkey = text;
        HotkeyBox.Text = text;
        HotkeyStatus.Text = "Press Apply to use it.";
    }

    private void ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_module.ApplyHotkey(_pendingHotkey))
            HotkeyStatus.Text = $"{_pendingHotkey} is live.";
        else
        {
            HotkeyStatus.Text = $"{_pendingHotkey} is taken by another app — try another.";
            HotkeyBox.Text = _module.Settings.Hotkey;
            _pendingHotkey = _module.Settings.Hotkey;
        }
    }

    // ---------------- voice ----------------

    private void Engine_Checked(object sender, RoutedEventArgs e)
    {
        _engine = sender == EnginePiper ? "piper" : sender == EngineKokoro ? "kokoro" : sender == EngineWindows ? "windows" : "aura";
        EngineNote.Text = _engine switch
        {
            "aura" => "Deepgram Aura-2 — most natural. Uses your Deepgram credit (~$0.03 per 1,000 characters).",
            "piper" => "Free and offline. Renders ~20x faster than real-time on this laptop — the snappiest option.",
            "kokoro" => "Free and offline, more natural than Piper — but only about real-time on this CPU, so replies start ~1s later.",
            _ => "Windows' built-in voice. Always available, no setup, but robotic.",
        };

        VoicePanel.Children.Clear();
        IEnumerable<(string Id, string Label)> voices = _engine switch
        {
            "aura" => AuraVoices,
            "piper" or "kokoro" => LocalTts.Voices.Where(v => v.Engine == _engine).Select(v => (v.Id, v.Label)),
            _ => Array.Empty<(string, string)>(),
        };
        foreach (var (id, label) in voices)
        {
            var rb = new RadioButton
            {
                GroupName = "voice",
                Style = (Style)FindResource("Seg"),
                Content = label,
                IsChecked = id == CurrentVoice,
            };
            rb.Checked += (_, _) =>
            {
                switch (_engine)
                {
                    case "aura": _auraVoice = id; break;
                    case "piper": _piperVoice = id; break;
                    case "kokoro": _kokoroVoice = id; break;
                }
                RenderLocalState();
            };
            VoicePanel.Children.Add(rb);
        }
        RenderLocalState();
    }

    private string CurrentVoice => _engine switch { "aura" => _auraVoice, "piper" => _piperVoice, "kokoro" => _kokoroVoice, _ => "" };

    private LocalVoice? SelectedLocal => _engine is "piper" or "kokoro" ? LocalTts.Find(_engine, CurrentVoice) : null;

    private void RenderLocalState()
    {
        var v = SelectedLocal;
        LocalPanel.Visibility = v is null ? Visibility.Collapsed : Visibility.Visible;
        TestVoiceBtn.IsEnabled = v?.IsInstalled ?? true;
        if (v is null) return;
        LocalDownloadBtn.Visibility = v.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        LocalDownloadBtn.IsEnabled = true;
        LocalDownloadText.Text = $"Download {v.Label} ({v.ApproxMb} MB)";
        LocalStatus.Text = v.IsInstalled ? "Installed — runs entirely on this PC." : "";
    }

    private async void LocalDownload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLocal is not { } voice) return;
        LocalDownloadBtn.IsEnabled = false;
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => LocalStatus.Text = p < 0.95 ? $"Downloading… {p:P0}" : "Unpacking…");
            await LocalTts.DownloadAsync(voice, progress, _downloadCts.Token);
            RenderLocalState();
        }
        catch (Exception ex)
        {
            LocalStatus.Text = "Download failed: " + ex.Message;
            LocalDownloadBtn.IsEnabled = true;
        }
    }

    private async void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        SaveVoice();
        TestVoiceBtn.IsEnabled = false;
        try
        {
            var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : ", " + NameBox.Text.Trim();
            await JarvisSession.AnnounceAsync(_module.Settings, $"Good evening{name}. All systems are running.");
        }
        finally { TestVoiceBtn.IsEnabled = true; }
    }

    private void SaveVoice()
    {
        SaveKeys(); // an Aura test needs the Deepgram key just typed
        var s = _module.Settings;
        s.TtsEngine = _engine;
        s.AuraVoice = _auraVoice;
        s.KokoroVoice = _kokoroVoice;
        s.PiperVoice = _piperVoice;
    }

    // ---------------- keys ----------------

    private void BuildKeyRows()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            var (name, label, hint) = Keys[i];
            KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock { Text = label, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(title, i);

            var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = hint };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            _keyBoxes[name] = box;

            var saved = SecretStore.Get(name);
            var status = new TextBlock
            {
                Text = saved != null ? SecretStore.Mask(saved) : hint.StartsWith("free") ? "free" : "",
                Style = (Style)FindResource(saved != null ? "Mono" : "MonoMuted"),
                FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(status, i);
            Grid.SetColumn(status, 2);

            KeysGrid.Children.Add(title);
            KeysGrid.Children.Add(box);
            KeysGrid.Children.Add(status);
        }
    }

    /// <summary>Only fields you typed into are saved; blank fields leave the stored key alone.</summary>
    private void SaveKeys()
    {
        foreach (var (name, box) in _keyBoxes)
            if (box.Password.Trim().Length > 0)
            {
                SecretStore.Set(name, box.Password.Trim());
                box.Clear();
            }
    }

    // ---------------- window ----------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveVoice();
        _module.Settings.UserName = NameBox.Text.Trim();
        bool placeChanged = _module.Settings.HomeLocation != HomeBox.Text.Trim() || _module.Settings.UseLocation != (UseLocationBox.IsChecked == true);
        _module.Settings.HomeLocation = HomeBox.Text.Trim();
        _module.Settings.UseLocation = UseLocationBox.IsChecked == true;
        _module.Settings.LearnFromConversations = LearnBox.IsChecked == true;
        _module.Settings.BargeIn = BargeInBox.IsChecked == true;
        if (placeChanged) { LocationService.Invalidate(); _ = ShowLocationAsync(); }
        _module.SaveSettings();
        if (_pendingHotkey != _module.Settings.Hotkey) ApplyHotkey_Click(sender, e);
        KeysGrid.Children.Clear();
        KeysGrid.RowDefinitions.Clear();
        BuildKeyRows();
        SavedText.Text = "Saved.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Memory_Click(object sender, RoutedEventArgs e) => _module.OpenMemory();

    private async Task ShowLocationAsync()
    {
        LocationStatus.Text = "Finding you...";
        var place = await LocationService.GetAsync(_module.Settings);
        LocationStatus.Text = !_module.Settings.UseLocation ? "Location is off."
            : place is null ? "Couldn't detect a location. Type your city above."
            : $"Currently: {place.Describe()} (from {place.Source}).";
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();
}
