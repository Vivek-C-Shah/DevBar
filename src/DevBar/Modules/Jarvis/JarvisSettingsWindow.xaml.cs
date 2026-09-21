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
    private List<string> _brain = new();

    private static readonly string[] BrainSuggestionList =
    {
        "groq:openai/gpt-oss-120b", "groq:openai/gpt-oss-20b", "gemini:gemini-3.5-flash-lite", "gemini:gemini-2.5-flash",
        "openai:gpt-5-mini", "anthropic:claude-haiku-4-5", "cerebras:gpt-oss-120b", "ollama:qwen3:4b",
    };

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
        (s.Proactive switch { "show" => ProactiveShow, "off" => ProactiveOff, _ => ProactiveSpeak }).IsChecked = true;
        QuietBox.Text = s.QuietHours;
        WakeBox.IsChecked = s.WakeWord;
        WakeBatteryBox.IsChecked = s.WakeWordOnBattery;
        (s.WakeWordSensitivity switch { "strict" => WakeStrict, "sensitive" => WakeSensitive, _ => WakeBalanced }).IsChecked = true;
        RenderClaudeHook();
        // Fixed height inside the work area: with SizeToContent the ScrollViewer is measured
        // unbounded and the bottom sections become unreachable.
        Height = Math.Min(SystemParameters.WorkArea.Height - 40, 980);
        _ = ShowLocationAsync();
        _brain = s.Llm.ToList();
        RenderBrain();
        GoogleIdBox.Text = SecretStore.Get(Google.GoogleAuth.ClientIdKey) ?? "";
        RenderGoogle();
        BuildKeyRows();

        (s.TtsEngine switch { "piper" => EnginePiper, "kokoro" => EngineKokoro, "windows" => EngineWindows, _ => EngineAura }).IsChecked = true;
        Closed += (_, _) => _downloadCts?.Cancel();
        // Text boxes swallow the mouse wheel in WPF; scroll the page wherever the cursor is.
        PreviewMouseWheel += (_, e) =>
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
            e.Handled = true;
        };
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
        if (_brain.Count > 0) _module.Settings.Llm = _brain.ToList();
        if (GoogleIdBox.Text.Trim() != (SecretStore.Get(Google.GoogleAuth.ClientIdKey) ?? "")) SecretStore.Set(Google.GoogleAuth.ClientIdKey, GoogleIdBox.Text.Trim());
        if (GoogleSecretBox.Password.Trim().Length > 0) { SecretStore.Set(Google.GoogleAuth.ClientSecretKey, GoogleSecretBox.Password.Trim()); GoogleSecretBox.Clear(); }
        bool placeChanged = _module.Settings.HomeLocation != HomeBox.Text.Trim() || _module.Settings.UseLocation != (UseLocationBox.IsChecked == true);
        _module.Settings.HomeLocation = HomeBox.Text.Trim();
        _module.Settings.UseLocation = UseLocationBox.IsChecked == true;
        _module.Settings.LearnFromConversations = LearnBox.IsChecked == true;
        _module.Settings.BargeIn = BargeInBox.IsChecked == true;
        _module.Settings.Proactive = ProactiveShow.IsChecked == true ? "show" : ProactiveOff.IsChecked == true ? "off" : "speak";
        _module.Settings.QuietHours = QuietBox.Text.Trim();
        _module.Settings.WakeWord = WakeBox.IsChecked == true && Speech.WakeWordListener.IsInstalled;
        _module.Settings.WakeWordOnBattery = WakeBatteryBox.IsChecked == true;
        var sensitivity = WakeStrict.IsChecked == true ? "strict" : WakeSensitive.IsChecked == true ? "sensitive" : "balanced";
        bool rebuildWake = _module.Settings.WakeWordSensitivity != sensitivity;
        _module.Settings.WakeWordSensitivity = sensitivity;
        _module.ApplyWakeWord(rebuildWake);
        WakeStatus.Text = _module.Settings.WakeWord
            ? $"Wake word: {_module.WakeWordStatus}."
            : "Wake word off - press the shortcut to talk.";
        WakeStatus.Text = _module.Settings.WakeWord
            ? $"Wake word: {_module.WakeWordStatus}."
            : "Wake word off - press the shortcut to talk.";
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

    // ---------------- brain order ----------------

    private void RenderBrain()
    {
        BrainList.Children.Clear();
        for (int i = 0; i < _brain.Count; i++)
        {
            int idx = i;
            var spec = _brain[i];
            var llm = Brain.OpenAiCompatibleLlm.Parse(spec);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = $"{i + 1}.", Style = (Style)FindResource("MonoMuted"), VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock
            {
                Text = spec + (llm is null ? "   (unknown provider)" : llm.HasKey ? "" : "   (no key)"),
                Style = (Style)FindResource(llm is { HasKey: true } ? "Mono" : "MonoMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Tag = spec,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 2);
            buttons.Children.Add(IconBtn("\uE70E", idx > 0, () => Swap(idx, idx - 1)));              // up
            buttons.Children.Add(IconBtn("\uE70D", idx < _brain.Count - 1, () => Swap(idx, idx + 1))); // down
            buttons.Children.Add(IconBtn("\uE711", _brain.Count > 1, () => { _brain.RemoveAt(idx); RenderBrain(); }));
            row.Children.Add(buttons);
            BrainList.Children.Add(row);
        }

        BrainSuggestions.Children.Clear();
        foreach (var s in BrainSuggestionList.Where(s => !_brain.Contains(s)))
        {
            var chip = new Button { Style = (Style)FindResource("ChipButton"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
            chip.Content = new TextBlock { Text = "+ " + s, Style = (Style)FindResource("MonoMuted"), FontSize = 10.5 };
            chip.Click += (_, _) => { _brain.Add(s); RenderBrain(); };
            BrainSuggestions.Children.Add(chip);
        }
    }

    private Button IconBtn(string glyph, bool enabled, Action onClick)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, Width = 24, Height = 22, IsEnabled = enabled };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Swap(int a, int b)
    {
        (_brain[a], _brain[b]) = (_brain[b], _brain[a]);
        RenderBrain();
    }

    private void BrainAdd_Click(object sender, RoutedEventArgs e)
    {
        var spec = BrainNewBox.Text.Trim();
        if (Brain.OpenAiCompatibleLlm.Parse(spec) is null)
        {
            BrainStatus.Text = "Use provider:model — providers are groq, gemini, openai, anthropic, openrouter, cerebras, ollama.";
            return;
        }
        if (!_brain.Contains(spec)) _brain.Add(spec);
        BrainNewBox.Clear();
        RenderBrain();
    }

    private void BrainNewBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BrainAdd_Click(sender, e);
    }

    /// <summary>One tiny request per model, in order, so a typo or a missing key shows up here — not mid-conversation.</summary>
    private async void BrainTest_Click(object sender, RoutedEventArgs e)
    {
        SaveKeys();
        BrainTestBtn.IsEnabled = false;
        var results = new List<string>();
        foreach (var spec in _brain)
        {
            var llm = Brain.OpenAiCompatibleLlm.Parse(spec);
            if (llm is null) { results.Add($"{spec}: unknown provider"); continue; }
            BrainStatus.Text = $"Testing {spec}…";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var messages = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Reply with just: ok" },
                };
                var reply = await llm.ChatAsync(messages, new System.Text.Json.Nodes.JsonArray(), _ => { }, CancellationToken.None);
                results.Add($"✓ {spec} {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                var msg = ex.Message.Length > 90 ? ex.Message[..90] + "…" : ex.Message;
                results.Add($"✕ {spec}: {msg}");
            }
        }
        BrainStatus.Text = string.Join("\n", results);
        BrainTestBtn.IsEnabled = true;
        RenderBrain();
    }

    // ---------------- google ----------------

    private void RenderGoogle()
    {
        bool connected = Google.GoogleAuth.IsConnected;
        GoogleBtnText.Text = connected ? "Disconnect Google" : "Connect Google";
        GoogleStatus.Text = connected
            ? $"Connected as {Google.GoogleAuth.Account ?? "your account"}."
            : Google.GoogleAuth.HasClient ? "Ready to connect — your browser will ask for permission."
            : "Paste the client ID and secret from your Google Cloud 'Desktop app' OAuth client.";
    }

    private async void Google_Click(object sender, RoutedEventArgs e)
    {
        GoogleBtn.IsEnabled = false;
        try
        {
            if (Google.GoogleAuth.IsConnected)
            {
                Google.GoogleAuth.Disconnect();
            }
            else
            {
                if (GoogleIdBox.Text.Trim().Length > 0) SecretStore.Set(Google.GoogleAuth.ClientIdKey, GoogleIdBox.Text.Trim());
                if (GoogleSecretBox.Password.Trim().Length > 0) { SecretStore.Set(Google.GoogleAuth.ClientSecretKey, GoogleSecretBox.Password.Trim()); GoogleSecretBox.Clear(); }
                GoogleStatus.Text = "Waiting for you to approve in the browser…";
                var who = await Google.GoogleAuth.ConnectAsync(CancellationToken.None);
                GoogleStatus.Text = $"Connected as {who}.";
            }
        }
        catch (Exception ex)
        {
            GoogleStatus.Text = ex.Message;
            GoogleBtn.IsEnabled = true;
            return;
        }
        GoogleBtn.IsEnabled = true;
        RenderGoogle();
    }

    /// <summary>
    /// Listens for 10s at the selected sensitivity and reports what it heard — the
    /// only honest way to check the wake word against a real voice in a real room.
    /// </summary>
    private async void WakeTest_Click(object sender, RoutedEventArgs e)
    {
        if (!Speech.WakeWordListener.IsInstalled)
        {
            WakeStatus.Text = "Download the wake-word model first (tick the box above).";
            return;
        }
        var sensitivity = WakeStrict.IsChecked == true ? "strict" : WakeSensitive.IsChecked == true ? "sensitive" : "balanced";
        WakeTestBtn.IsEnabled = false;
        bool wasListening = _module.WakeWordListening;
        try
        {
            _module.PauseWakeWordForTest(true); // one mic stream at a time
            using var probe = new Speech.WakeWordListener(acOnly: false, sensitivity);
            var heard = new TaskCompletionSource<string>();
            probe.Detected += kw => heard.TrySetResult(kw);
            probe.Start();
            if (!probe.IsListening)
            {
                WakeStatus.Text = "Couldn't open the microphone for the test.";
                return;
            }
            for (int i = 10; i > 0 && !heard.Task.IsCompleted; i--)
            {
                WakeTestText.Text = $"Listening... {i}s";
                await Task.WhenAny(heard.Task, Task.Delay(1000));
            }
            WakeStatus.Text = heard.Task.IsCompleted
                ? $"Heard it: \"{heard.Task.Result.Replace('_', ' ').ToLowerInvariant()}\" at {sensitivity} sensitivity."
                : $"Nothing caught at {sensitivity} sensitivity. Say \"Hey Jarvis\" a touch slower, or try Sensitive.";
        }
        catch (Exception ex)
        {
            WakeStatus.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            _module.PauseWakeWordForTest(false);
            if (wasListening) _module.ApplyWakeWord();
            WakeTestText.Text = "Test: say \"Hey Jarvis\"";
            WakeTestBtn.IsEnabled = true;
        }
    }

    /// <summary>First time on: fetch the 19MB on-device model, then Save applies it.</summary>
    private async void WakeBox_Click(object sender, RoutedEventArgs e)
    {
        if (WakeBox.IsChecked != true || Speech.WakeWordListener.IsInstalled) return;
        WakeBox.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => WakeStatus.Text = p < 0.95 ? $"Downloading wake-word model… {p:P0}" : "Unpacking…");
            await Speech.WakeWordListener.DownloadAsync(progress, CancellationToken.None);
            WakeStatus.Text = "Ready — press Save to start listening for \"Hey Jarvis\".";
        }
        catch (Exception ex)
        {
            WakeStatus.Text = "Download failed: " + ex.Message;
            WakeBox.IsChecked = false;
        }
        finally { WakeBox.IsEnabled = true; }
    }

    private void RenderClaudeHook()
    {
        bool on = Proactive.ClaudeHook.IsInstalled;
        ClaudeHookText.Text = on ? "Disconnect Claude Code" : "Connect Claude Code";
        if (ClaudeHookStatus.Text.Length == 0)
            ClaudeHookStatus.Text = on ? "Connected — new sessions report to Jarvis." : "Not connected.";
    }

    private async void ClaudeHook_Click(object sender, RoutedEventArgs e)
    {
        ClaudeHookBtn.IsEnabled = false;
        try
        {
            if (Proactive.ClaudeHook.IsInstalled)
            {
                Proactive.ClaudeHook.Uninstall();
                ClaudeHookStatus.Text = "Disconnected.";
            }
            else ClaudeHookStatus.Text = await Proactive.ClaudeHook.InstallAsync() + " Applies to Claude Code sessions started from now on.";
        }
        catch (Exception ex) { ClaudeHookStatus.Text = "Couldn't change ~/.claude/settings.json: " + ex.Message; }
        finally
        {
            ClaudeHookBtn.IsEnabled = true;
            RenderClaudeHook();
        }
    }

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
