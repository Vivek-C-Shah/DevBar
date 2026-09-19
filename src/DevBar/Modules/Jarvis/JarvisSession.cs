using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Modules.Jarvis.Audio;
using DevBar.Modules.Jarvis.Brain;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;
using DevBar.Modules.Jarvis.Speech;
using DevBar.Modules.Jarvis.Tools;

namespace DevBar.Modules.Jarvis;

internal enum JarvisState { Idle, Connecting, Listening, Thinking, Speaking, Confirming }

/// <summary>
/// One hotkey-to-silence conversation: mic + Deepgram socket open for its
/// whole life, then everything is torn down so idle cost returns to zero.
/// All logic runs on the UI dispatcher (awaits resume there), which keeps
/// tool calls safe to touch WPF state; audio/network callbacks marshal in.
/// </summary>
internal sealed partial class JarvisSession
{
    private const int MaxToolSteps = 5;
    private static readonly TimeSpan FirstListenTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);

    public event Action<JarvisState>? StateChanged;
    /// <summary>What Jarvis heard (live while you talk).</summary>
    public event Action<string>? Heard;
    /// <summary>The reply so far, as it streams.</summary>
    public event Action<string>? Reply;
    /// <summary>Tool chip: label, and null while pending / true ok / false declined-or-failed.</summary>
    public event Action<string, bool?>? ToolActivity;
    public event Action<string>? Error;
    public event Action<float>? Level;
    public event Action? Ended;

    public JarvisState State { get; private set; } = JarvisState.Idle;

    private readonly JarvisConfig _cfg;
    private readonly List<JarvisTool> _tools;
    private readonly ProviderRouter _router;
    private readonly ConversationMemory _memory;
    private readonly Dispatcher _ui;

    private readonly CancellationTokenSource _sessionCts = new();
    private CancellationTokenSource? _turnCts;
    private MicCapture? _mic;
    private DeepgramStt? _stt;
    private readonly AudioPlayer _player = new();
    private ITextToSpeech _tts = new WindowsTts();

    private TaskCompletionSource<string?>? _nextUtterance;
    private DateTime _lastHeard = DateTime.MinValue;
    private TaskCompletionSource<bool>? _uiConfirm;

    // Barge-in: while Jarvis thinks/speaks the mic stays live (if enabled) and
    // real speech — not Jarvis's own voice leaking from the speakers — cancels
    // the turn. What you said then becomes the next utterance.
    private string _replyText = "";
    private string _currentUserText = "";
    private bool _bargedIn;
    private bool _awaitingConfirm;
    private string? _pendingUtterance;
    private string? _carryOver;
    private bool _audioStarted;

    public JarvisSession(JarvisConfig cfg, List<JarvisTool> tools, ProviderRouter router, ConversationMemory memory, Dispatcher ui)
    {
        _cfg = cfg;
        _tools = tools;
        _router = router;
        _memory = memory;
        _ui = ui;
        _player.Level += l => Post(() => { if (State == JarvisState.Speaking) Level?.Invoke(l); });
    }

    // ---------------- lifecycle ----------------

    public async Task RunAsync()
    {
        var ct = _sessionCts.Token;
        try
        {
            SetState(JarvisState.Connecting);
            _tts = TtsFactory.Create(_cfg, out var ttsWarning);
            if (ttsWarning != null) Error?.Invoke(ttsWarning);

            var key = SecretStore.Get("deepgram");
            if (key is null)
            {
                Error?.Invoke("Add your Deepgram key in Jarvis settings (gear icon) so I can hear you.");
                return;
            }

            _stt = new DeepgramStt();
            _stt.Interim += text => Post(() => OnInterim(text));
            _stt.Utterance += text => Post(() => OnUtterance(text));
            _stt.Failed += msg => Post(() => { Error?.Invoke(msg); Stop(); });

            // Mic first so the first syllable after the hotkey isn't lost while the socket connects.
            var early = new List<byte[]>();
            bool connected = false;
            _mic = new MicCapture();
            _mic.Data += pcm =>
            {
                lock (early)
                {
                    if (!connected) { early.Add(pcm); return; }
                }
                _stt.Send(pcm);
            };
            _mic.Level += l => Post(() => { if (State is JarvisState.Listening or JarvisState.Confirming) Level?.Invoke(l); });
            _mic.Start();

            await _stt.ConnectAsync(key, _cfg.SttModel, _cfg.SttLanguage, MicCapture.SampleRate, _cfg.SttEndpointingMs, ct);
            lock (early)
            {
                foreach (var chunk in early) _stt.Send(chunk);
                early.Clear();
                connected = true;
            }

            SetState(JarvisState.Listening);
            var timeout = FirstListenTimeout;
            while (!ct.IsCancellationRequested)
            {
                var text = await NextUtteranceAsync(timeout, ct);
                if (text is null) break; // silence — conversation's over
                timeout = TimeSpan.FromSeconds(Math.Max(2, _cfg.FollowUpSeconds));

                if (Dismissal().IsMatch(text.Trim()))
                    break;
                if (WakePrefix().Replace(text, "").Trim().Length == 0)
                    continue; // just "Jarvis" — keep listening for the actual request

                _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _bargedIn = false;
                try
                {
                    await RunTurnAsync(text, _turnCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _player.Stop(); // interrupted (hotkey or talking over) — straight back to listening
                    if (_bargedIn && !_audioStarted)
                    {
                        // You kept talking before Jarvis said anything: treat both parts as one request.
                        _memory.DropLastExchange();
                        _carryOver = _currentUserText;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _player.Stop();
                    Error?.Invoke(ex.Message);
                    await SpeakOnceAsync(ex.Message.StartsWith("No brain configured")
                        ? "I don't have a brain yet. Add a Groq or Gemini key in my settings."
                        : "Sorry, I couldn't reach my brain just then.", ct);
                }
                finally
                {
                    _turnCts.Dispose();
                    _turnCts = null;
                }

                if (ct.IsCancellationRequested) break;
                _stt.Muted = false;
                SetState(JarvisState.Listening);
                if (_bargedIn) timeout = TimeSpan.FromSeconds(8); // you're mid-sentence; don't time out on them
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            Error?.Invoke(ex is System.Net.WebSockets.WebSocketException
                ? "Couldn't connect to Deepgram — check the key and your connection."
                : ex.Message);
        }
        finally
        {
            await TeardownAsync();
        }
    }

    /// <summary>The global hotkey while a session is live: interrupt if busy, otherwise end.</summary>
    public void HotkeyPressed()
    {
        if (State is JarvisState.Thinking or JarvisState.Speaking && _turnCts != null)
        {
            _turnCts.Cancel();
            _player.Stop();
        }
        else Stop();
    }

    public void Stop()
    {
        if (!_sessionCts.IsCancellationRequested) _sessionCts.Cancel();
        _player.Stop();
        _nextUtterance?.TrySetResult(null);
        _uiConfirm?.TrySetResult(false);
    }

    public void ConfirmFromUi(bool yes) => _uiConfirm?.TrySetResult(yes);

    /// <summary>Debug: behave as if this was just heard.</summary>
    public void InjectUtterance(string text) => _nextUtterance?.TrySetResult(text);

    /// <summary>Debug: behave as if the mic just heard this (partial, then final) — exercises barge-in.</summary>
    public void SimulateHeard(string text)
    {
        OnInterim(text);
        OnUtterance(text);
    }

    private async Task TeardownAsync()
    {
        _player.Stop();
        _mic?.Dispose();
        _mic = null;
        if (_stt != null) await _stt.DisposeAsync();
        _stt = null;
        Level?.Invoke(0);
        SetState(JarvisState.Idle);
        Ended?.Invoke();
    }

    // ---------------- listening ----------------

    /// <summary>Next complete utterance, or null after <paramref name="timeout"/> of silence (extended while you're mid-sentence).</summary>
    private async Task<string?> NextUtteranceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nextUtterance = tcs;
        if (_pendingUtterance is { } pending)
        {
            _pendingUtterance = null;
            tcs.TrySetResult(pending);
        }
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var wait = Task.Delay(250, ct);
            if (await Task.WhenAny(tcs.Task, wait) == tcs.Task)
                return await tcs.Task;
            ct.ThrowIfCancellationRequested();
            if (_nextUtterance != tcs) { Trace("listen superseded"); return null; } // e.g. confirmed by click
            if (DateTime.UtcNow - _lastHeard < TimeSpan.FromSeconds(1.5))
                deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2); // still talking
            if (DateTime.UtcNow > deadline) { Trace("listen timeout"); return null; }
        }
    }

    // ---------------- a turn ----------------

    private async Task RunTurnAsync(string userText, CancellationToken ct)
    {
        if (_carryOver != null)
        {
            userText = _carryOver + " " + userText;
            _carryOver = null;
        }
        userText = WakePrefix().Replace(userText, "").Trim();
        if (userText.Length > 0) userText = char.ToUpperInvariant(userText[0]) + userText[1..];
        // Without barge-in, mute during the turn so we never transcribe our own voice.
        _stt!.Muted = !_cfg.BargeIn;
        _currentUserText = userText;
        _audioStarted = false;
        SetState(JarvisState.Thinking);
        Heard?.Invoke(userText);
        SetReply("");
        _memory.AddUser(userText);
        MemoryStore.LogTurn("user", userText);

        var schemas = new JsonArray(_tools.Select(t => (JsonNode)t.Schema()).ToArray());
        var speech = new SpeechQueue(_tts, _player, () => Post(() => { _audioStarted = true; SetState(JarvisState.Speaking); }), msg => Post(() => Error?.Invoke(msg)), ct);
        var shown = new StringBuilder();

        for (int step = 0; step < MaxToolSteps; step++)
        {
            var splitter = new SentenceSplitter(speech.Say);
            var reply = await _router.ChatAsync(BuildMessages(), schemas, piece =>
            {
                splitter.Push(piece);
                shown.Append(piece);
                SetReply(SentenceSplitter.Clean(shown.ToString()));
            }, ct);
            splitter.Flush();
            Trace($"llm {reply.Provider} ({reply.PromptTokens} tok): \"{reply.Text}\" tools=[{string.Join(", ", reply.ToolCalls.Select(c => c.Name + c.ArgumentsJson))}]");

            if (reply.ToolCalls.Count == 0)
            {
                _memory.AddAssistant(reply.Text);
                MemoryStore.LogTurn("assistant", reply.Text);
                break;
            }

            _memory.AddAssistantToolCalls(reply.Text, reply.ToolCalls);
            foreach (var call in reply.ToolCalls)
            {
                var result = await ExecuteToolAsync(call, speech, ct);
                _memory.AddToolResult(call.Id, result);
            }
            if (shown.Length > 0) shown.Append(' ');
            SetState(JarvisState.Thinking);
        }

        await speech.FinishAsync();
        _player.Stop(); // release the device; next reply reopens on the current default (headphones vs speakers)
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, SpeechQueue speech, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null) return $"Unknown tool {call.Name}.";

        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement; }
        catch { args = JsonDocument.Parse("{}").RootElement; }

        var label = Chip(tool, args);
        if (tool.IsSlow && !_audioStarted && _replyText.Length == 0)
        {
            SetReply("One moment.");
            speech.Say("One moment.");
        }
        if (tool.RiskOf(args) == Risk.Destructive)
        {
            ToolActivity?.Invoke(label, null);
            bool ok = await ConfirmAsync(tool.Describe(args), speech, ct);
            if (!ok)
            {
                ToolActivity?.Invoke(label, false);
                return $"{_cfg.UserName} said no — do not do it. Acknowledge briefly.";
            }
        }

        try
        {
            var result = await tool.RunAsync(args);
            ToolActivity?.Invoke(label, true);
            return result;
        }
        catch (Exception ex)
        {
            ToolActivity?.Invoke(label, false);
            return $"Tool failed: {ex.Message}";
        }
    }

    /// <summary>Asks out loud, then waits for a spoken yes/no (or a click on the card).</summary>
    private async Task<bool> ConfirmAsync(string action, SpeechQueue speech, CancellationToken ct)
    {
        var question = $"Shall I {action}?";
        SetReply(question);
        _awaitingConfirm = true; // a "yes" said over the question counts, and isn't a barge-in
        _pendingUtterance = null;
        _uiConfirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = _uiConfirm.Task;
        speech.Say(question);
        await Task.WhenAny(speech.DrainAsync(), clicked);

        SetState(JarvisState.Confirming);
        _stt!.Muted = false;
        var spoken = clicked.IsCompleted ? Task.FromResult<string?>(null) : NextUtteranceAsync(ConfirmTimeout, ct);
        var first = clicked.IsCompleted ? clicked : await Task.WhenAny(spoken, clicked);
        _stt.Muted = !_cfg.BargeIn;
        _awaitingConfirm = false;

        bool yes;
        if (first == clicked) yes = await clicked;
        else
        {
            var answer = await spoken;
            if (answer != null) Heard?.Invoke(answer);
            yes = answer != null && Affirmative().IsMatch(answer) && !Negative().IsMatch(answer);
        }
        _uiConfirm = null;
        Trace($"confirm '{action}': {(first == clicked ? "click" : "voice")} -> {yes}");
        SetState(JarvisState.Thinking);
        return yes;
    }

    private async Task SpeakOnceAsync(string text, CancellationToken ct)
    {
        try
        {
            SetReply(text);
            var q = new SpeechQueue(_tts, _player, () => Post(() => SetState(JarvisState.Speaking)), _ => { }, ct);
            q.Say(text);
            await q.FinishAsync();
        }
        catch { /* best effort */ }
        finally { _player.Stop(); }
    }

    /// <summary>Short rising two-note chime: "I heard you" after the wake word.</summary>
    public static async Task ChimeAsync()
    {
        using var player = new AudioPlayer();
        var pcm = new List<byte>();
        foreach (var (freq, ms) in new[] { (660.0, 90), (990.0, 120) })
        {
            int n = AudioPlayer.SampleRate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / (AudioPlayer.SampleRate * 0.01)); // 10ms fades, no clicks
                short s = (short)(Math.Sin(2 * Math.PI * freq * i / AudioPlayer.SampleRate) * env * 0.18 * short.MaxValue);
                pcm.Add((byte)s);
                pcm.Add((byte)(s >> 8));
            }
        }
        player.Enqueue(pcm.ToArray());
        try { await player.WaitDrainedAsync(CancellationToken.None); } catch { }
    }

    /// <summary>Speaks outside a conversation (timers). Standalone — owns its own player.</summary>
    public static async Task AnnounceAsync(JarvisConfig cfg, string text)
    {
        using var player = new AudioPlayer();
        var tts = TtsFactory.Create(cfg, out _);
        var q = new SpeechQueue(tts, player, () => { }, _ => { }, CancellationToken.None);
        q.Say(text);
        try { await q.FinishAsync(); } catch { }
    }

    // ---------------- prompt ----------------

    private JsonArray BuildMessages()
    {
        var name = string.IsNullOrWhiteSpace(_cfg.UserName) ? "the user" : _cfg.UserName;
        var place = LocationService.Current;
        var facts = SafeFacts();
        var profile = facts.Count == 0
            ? $"You don't know anything about {name} yet."
            : $"What you know about {name} (from past conversations; use naturally, don't recite):\n" + string.Join("\n", facts.Select(f => "- " + f.Text));
        var firstToday = _cfg.LastConversation is not { } last || last.Date < DateTime.Today;
        var greeting = firstToday
            ? $"This is {name}'s first conversation today. If they open with a greeting, greet them back and offer a quick daily brief (the daily_brief tool) in one short question."
            : "";
        var curiosity = facts.Count < 12
            ? $"You're still getting to know {name}. When a conversation reaches a natural pause, you may ask ONE brief, friendly question about them " +
              "(their work, projects, routine, preferences) — at most one per conversation, never when they're busy or mid-task. Use the remember tool for what they tell you."
            : "";
        var system = $"""
            You are JARVIS, a voice assistant built into DevBar on {name}'s Windows PC. {name} is a software developer.
            Your words are spoken aloud by a text-to-speech voice, so:
            - Reply in one or two short sentences unless asked for more. No markdown, lists, emoji, code blocks or URLs read out.
            - Say numbers and times the way a person would ("half past three", "port three thousand").
            Personality: calm, competent, quietly witty, British understatement; never sycophantic, never gushing. Address {name} by name occasionally, not every reply.
            You're a companion as much as a tool: chat, stories, explanations, opinions and general questions are all welcome — answer them.
            Use tools to act or check real state instead of guessing. After a tool runs, confirm the outcome in a few words.
            If the request is ambiguous, ask one short question. If you can't do something, say so plainly.
            Destructive tools (killing processes, stopping containers, running commands) are confirmed with {name} automatically — just call them.
            When {name} tells you something lasting about themselves, save it with the remember tool (never passwords or keys).
            Current time: {DateTime.Now:dddd d MMMM yyyy, h:mm tt} ({TimeZoneInfo.Local.StandardName}).
            {(place is null ? "Location unknown." : $"{name} is in {place.Describe()} (from {place.Source}).")}
            Foreground window: "{ForegroundTitle()}".
            Match {name}'s energy: terse and fast when they sound rushed or are mid-task, a little more conversational when they're chatty.
            {profile}
            {curiosity}
            {greeting}
            """;
        return _memory.BuildMessages(system);
    }

    private static List<Fact> SafeFacts()
    {
        try { return MemoryStore.Facts(25); }
        catch { return new List<Fact>(); }
    }

    // ---------------- barge-in ----------------

    private void SetReply(string text)
    {
        _replyText = text;
        Reply?.Invoke(text);
    }

    private bool TurnRunning => _turnCts != null && State is JarvisState.Thinking or JarvisState.Speaking;

    private void OnInterim(string text)
    {
        if (TurnRunning && !_awaitingConfirm)
        {
            if (!_cfg.BargeIn) return;
            if (_bargedIn) { _lastHeard = DateTime.UtcNow; Heard?.Invoke(text); return; }
            if (!IsRealSpeech(text)) return;

            Trace($"barge-in: {text}");
            _bargedIn = true;
            _lastHeard = DateTime.UtcNow;
            _turnCts!.Cancel();
            _player.Stop();
            Heard?.Invoke(text);
            return;
        }
        if (_awaitingConfirm && !IsRealSpeech(text)) return;
        _lastHeard = DateTime.UtcNow;
        Heard?.Invoke(text);
    }

    private void OnUtterance(string text)
    {
        Trace($"utterance: {text}");
        if (_nextUtterance is { Task.IsCompleted: false } waiting)
        {
            if (_awaitingConfirm && !IsRealSpeech(text)) return; // our own question, heard through the speakers
            waiting.TrySetResult(text);
        }
        else if (_bargedIn || (_awaitingConfirm && IsRealSpeech(text)))
            _pendingUtterance = _pendingUtterance is null ? text : _pendingUtterance + " " + text;
    }

    /// <summary>
    /// True if this transcript is the user, not Jarvis's own voice picked up by
    /// the mic: at least two words, and mostly words Jarvis isn't currently saying.
    /// </summary>
    private bool IsRealSpeech(string heard) => IsRealSpeech(heard, _replyText);

    internal static bool IsRealSpeech(string heard, string jarvisSaying)
    {
        var words = Words(heard);
        if (words.Count < 2) return false;
        var saying = Words(jarvisSaying).ToHashSet();
        if (saying.Count == 0) return true;
        double echo = words.Count(saying.Contains) / (double)words.Count;
        return echo < 0.5;
    }

    private static List<string> Words(string s) =>
        WordSplit().Split(s.ToLowerInvariant()).Where(w => w.Length > 0).ToList();

    [GeneratedRegex(@"^\s*(hey|ok|okay|hi)?[\s,]*jarvis[\s,.!?]*", RegexOptions.IgnoreCase)]
    private static partial Regex WakePrefix();

    [GeneratedRegex(@"[^a-z0-9']+")]
    private static partial Regex WordSplit();

    private static string Chip(JarvisTool tool, JsonElement args)
    {
        var detail = args.ValueKind == JsonValueKind.Object
            ? string.Join(" ", args.EnumerateObject().Select(p => p.Value.ToString()).Where(v => v.Length is > 0 and < 40))
            : "";
        return detail.Length > 0 ? $"{tool.Name} {detail}" : tool.Name;
    }

    // ---------------- helpers ----------------

    /// <summary>Set DEVBAR_JARVIS_TRACE=1 to log the conversation state machine to jarvis-trace.log.</summary>
    private static readonly bool Tracing = Environment.GetEnvironmentVariable("DEVBAR_JARVIS_TRACE") == "1";

    internal static void Trace(string msg)
    {
        if (!Tracing) return;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Config.Dir, "jarvis-trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); }
        catch { }
    }

    private void SetState(JarvisState s)
    {
        if (State == s) return;
        Trace($"state {State} -> {s}");
        State = s;
        StateChanged?.Invoke(s);
    }

    private void Post(Action a) => _ui.BeginInvoke(a);

    [GeneratedRegex(@"^(stop|cancel|never ?mind|nothing|no thanks|that'?s all|that is all|thanks,? that'?s all|goodbye|bye|go to sleep|shut up)[.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Dismissal();

    [GeneratedRegex(@"\b(yes|yeah|yep|yup|sure|do it|go ahead|confirm(ed)?|affirmative|please do|ok(ay)?|proceed|kill it|go for it)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Affirmative();

    [GeneratedRegex(@"\b(no|nope|don'?t|do not|stop|cancel|wait|hold on)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negative();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static string ForegroundTitle()
    {
        var sb = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString().Replace("\"", "'");
    }
}

/// <summary>
/// Sentences in, audio out, pipelined: up to two sentences synthesize ahead
/// while earlier ones play, and a single feeder hands audio to the player in
/// order. If the configured voice fails mid-reply the rest of the reply uses
/// the Windows voice rather than going silent.
/// </summary>
internal sealed class SpeechQueue
{
    private readonly Channel<Channel<byte[]>> _ordered = Channel.CreateUnbounded<Channel<byte[]>>();
    private readonly SemaphoreSlim _ahead = new(2);
    private readonly AudioPlayer _player;
    private readonly Action<string> _onError;
    private readonly CancellationToken _ct;
    private readonly Task _feeder;
    private ITextToSpeech _tts;
    private int _pending;

    /// <param name="onAudio">Raised as each sentence starts playing (not just the first) — a
    /// confirmation question in between moves the state away from Speaking.</param>
    public SpeechQueue(ITextToSpeech tts, AudioPlayer player, Action onAudio, Action<string> onError, CancellationToken ct)
    {
        _tts = tts;
        _player = player;
        _onError = onError;
        _ct = ct;
        _feeder = Task.Run(async () =>
        {
            await foreach (var sentence in _ordered.Reader.ReadAllAsync(ct))
            {
                try
                {
                    bool first = true;
                    await foreach (var pcm in sentence.Reader.ReadAllAsync(ct))
                    {
                        if (first) { first = false; onAudio(); }
                        player.Enqueue(pcm);
                    }
                }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }, ct);
    }

    public void Say(string sentence)
    {
        Interlocked.Increment(ref _pending);
        var chunks = Channel.CreateUnbounded<byte[]>();
        _ordered.Writer.TryWrite(chunks);
        _ = Task.Run(async () =>
        {
            await _ahead.WaitAsync(_ct);
            try
            {
                try
                {
                    await _tts.SpeakAsync(sentence, pcm => chunks.Writer.TryWrite(pcm), _ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && _tts is not WindowsTts)
                {
                    _onError($"{_tts.Name} failed ({ex.Message}) — switching to the Windows voice.");
                    _tts = new WindowsTts();
                    await _tts.SpeakAsync(sentence, pcm => chunks.Writer.TryWrite(pcm), _ct);
                }
            }
            catch { /* cancelled or even the fallback failed — skip this sentence */ }
            finally
            {
                chunks.Writer.TryComplete();
                _ahead.Release();
            }
        });
    }

    /// <summary>Waits until everything said so far is synthesized and heard; the queue stays open.</summary>
    public async Task DrainAsync()
    {
        while (Volatile.Read(ref _pending) > 0) await Task.Delay(30, _ct);
        await _player.WaitDrainedAsync(_ct);
    }

    public async Task FinishAsync()
    {
        _ordered.Writer.TryComplete();
        await _feeder;
        await _player.WaitDrainedAsync(_ct);
    }
}

/// <summary>Cuts streaming tokens into speakable sentences so TTS can start before the model finishes.</summary>
internal sealed partial class SentenceSplitter(Action<string> emit)
{
    private readonly StringBuilder _buf = new();

    public void Push(string piece)
    {
        _buf.Append(piece);
        while (true)
        {
            int cut = FindBoundary();
            if (cut < 0) return;
            Emit(_buf.ToString(0, cut + 1));
            _buf.Remove(0, cut + 1);
        }
    }

    public void Flush()
    {
        Emit(_buf.ToString());
        _buf.Clear();
    }

    private int FindBoundary()
    {
        for (int i = 0; i < _buf.Length - 1; i++)
        {
            char c = _buf[i];
            if (c == '\n' && i > 0) return i;
            if (c is '.' or '!' or '?' or ';' && char.IsWhiteSpace(_buf[i + 1]) && i >= 12) return i;
        }
        return -1;
    }

    private void Emit(string raw)
    {
        var s = Clean(raw);
        if (s.Any(char.IsLetterOrDigit)) emit(s);
    }

    public static string Clean(string s) => Spaces().Replace(Markdown().Replace(s, ""), " ").Trim();

    [GeneratedRegex(@"[*_`#>]|\[(?=[^\]]*\]\()|\]\([^)]*\)")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
