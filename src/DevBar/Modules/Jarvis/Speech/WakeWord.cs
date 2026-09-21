using System.IO;
using System.Net.Http;
using DevBar.Core;
using DevBar.Modules.Jarvis.Audio;
using Microsoft.Win32;
using SherpaOnnx;

namespace DevBar.Modules.Jarvis.Speech;

/// <summary>
/// Opt-in "Hey Jarvis" / "Jarvis" wake word, fully on-device (sherpa-onnx
/// zipformer keyword spotter, int8, ~5MB). This is the one Jarvis feature
/// that keeps the mic open while idle, so it is OFF by default, pauses while a
/// conversation is running, and (by default) only listens on AC power.
/// Audio never leaves the PC until the wake word is heard.
/// </summary>
internal sealed class WakeWordListener : IDisposable
{
    public const string ModelName = "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01";
    public const string DownloadUrl = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/{ModelName}.tar.bz2";
    public static string ModelDir => Path.Combine(Config.Dir, "models", ModelName);
    public static bool IsInstalled => File.Exists(Path.Combine(ModelDir, "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx"));

    // BPE tokens for the model's vocabulary (generated with its bpe.model).
    // "#x" is the per-phrase trigger threshold: the bare name is more likely to
    // come up in ordinary speech/video audio, so it needs a stronger match.
    // Labels after '@' must not contain spaces: sherpa-onnx treats them as tokens,
    // and a bad keyword line makes the native library exit the whole process.
    private static string KeywordsFor(string sensitivity)
    {
        // Thresholds were tuned against synthesized voices; a real voice may need
        // "sensitive", which is why this is a setting rather than a constant.
        var (two, one) = sensitivity switch
        {
            "strict" => ("#0.20", "#0.45"),
            "sensitive" => ("#0.06", "#0.20"),
            _ => ("#0.12", "#0.30"),
        };
        return $"▁HE Y ▁JA R VI S :1.5 {two} @HEY_JARVIS\n" +
               $"▁O K ▁JA R VI S :1.5 {two} @OK_JARVIS\n" +
               $"▁JA R VI S :1.2 {one} @JARVIS\n";
    }

    public event Action<string>? Detected;

    private readonly bool _acOnly;
    private KeywordSpotter? _spotter;
    private OnlineStream? _stream;
    private MicCapture? _mic;
    private readonly object _gate = new();
    private bool _paused;

    private readonly string _sensitivity;

    public WakeWordListener(bool acOnly, string sensitivity = "balanced")
    {
        _acOnly = acOnly;
        _sensitivity = sensitivity;
        SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    public bool IsListening => _mic != null;

    public static KeywordSpotter CreateSpotter(string sensitivity = "balanced")
    {
        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = MicCapture.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(ModelDir, "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(ModelDir, "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(ModelDir, "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(ModelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 1; // continuous, tiny model — one thread keeps idle cost lowest
        config.ModelConfig.Provider = "cpu";
        config.MaxActivePaths = 4;
        config.NumTrailingBlanks = 1;
        config.KeywordsScore = 1.0f;
        config.KeywordsThreshold = 0.25f;
        var kwFile = Path.Combine(ModelDir, $"jarvis-keywords-{sensitivity}.txt");
        File.WriteAllText(kwFile, KeywordsFor(sensitivity));
        config.KeywordsFile = kwFile;
        return new KeywordSpotter(config);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!IsInstalled || _mic != null || _paused) return;
            if (_acOnly && SystemInformationOnBattery()) return;
            _spotter ??= CreateSpotter(_sensitivity);
            _stream = _spotter.CreateStream();
            _mic = new MicCapture();
            _mic.Data += OnAudio;
            _mic.Start();
        }
        JarvisSession.Trace("wake word: listening");
    }

    /// <summary>Stops the mic (a conversation needs it, or battery). Keeps the model loaded.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_mic is null) return;
            _mic.Data -= OnAudio;
            _mic.Dispose();
            _mic = null;
            _stream?.Dispose();
            _stream = null;
        }
        JarvisSession.Trace("wake word: stopped");
    }

    public void Pause() { _paused = true; Stop(); }
    public void Resume() { _paused = false; Start(); }

    private void OnAudio(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

        string? hit = null;
        lock (_gate)
        {
            if (_stream is null || _spotter is null) return;
            _stream.AcceptWaveform(MicCapture.SampleRate, samples);
            while (_spotter.IsReady(_stream))
            {
                _spotter.Decode(_stream);
                var kw = _spotter.GetResult(_stream).Keyword;
                if (!string.IsNullOrEmpty(kw))
                {
                    hit = kw;
                    _spotter.Reset(_stream);
                }
            }
        }
        if (hit != null) Detected?.Invoke(hit);
    }

    private void OnPowerChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (!_acOnly || e.Mode != PowerModes.StatusChange) return;
        if (SystemInformationOnBattery()) Stop();
        else Start();
    }

    private static bool SystemInformationOnBattery() =>
        GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        var modelsDir = Path.Combine(Config.Dir, "models");
        Directory.CreateDirectory(modelsDir);
        var archive = Path.Combine(modelsDir, ModelName + ".tar.bz2");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(archive);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress.Report(done * 0.95 / total);
                }
            }
            var tar = await ShellOut.RunAsync("tar", $"-xjf \"{archive}\" -C \"{modelsDir}\"", timeout: TimeSpan.FromMinutes(2));
            if (!IsInstalled) throw new InvalidOperationException("Unpacking the wake-word model failed. " + tar.StdErr);
            progress.Report(1);
        }
        finally { try { File.Delete(archive); } catch { } }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        Stop();
        _spotter?.Dispose();
        _spotter = null;
    }
}
