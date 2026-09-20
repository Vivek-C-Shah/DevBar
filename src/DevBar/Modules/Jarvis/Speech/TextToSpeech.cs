using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using DevBar.Core;
using DevBar.Modules.Jarvis.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace DevBar.Modules.Jarvis.Speech;

/// <summary>
/// One sentence in, 24 kHz mono PCM16 out (AudioPlayer's format), streamed in
/// chunks where the engine can. Every engine targets the same format so the
/// player never has to reopen mid-reply.
/// </summary>
internal interface ITextToSpeech
{
    string Name { get; }
    Task SpeakAsync(string sentence, Action<byte[]> onPcm, CancellationToken ct);
}

internal static class TtsFactory
{
    /// <summary>The configured engine, degrading to Windows' built-in voice when it can't run.</summary>
    public static ITextToSpeech Create(JarvisConfig cfg, out string? warning)
    {
        warning = null;
        switch (cfg.TtsEngine)
        {
            case "aura":
                var key = SecretStore.Get("deepgram");
                if (key != null) return new DeepgramAuraTts(key, cfg.AuraVoice);
                warning = "No Deepgram key — using the Windows voice.";
                break;
            case "piper" or "kokoro":
                var voice = LocalTts.Find(cfg.TtsEngine, cfg.TtsEngine == "kokoro" ? cfg.KokoroVoice : cfg.PiperVoice);
                if (voice.IsInstalled) return new LocalTts(voice);
                warning = $"The {voice.Label} voice isn't downloaded yet (Jarvis settings) — using the Windows voice.";
                break;
        }
        return new WindowsTts();
    }
}

/// <summary>Deepgram Aura-2: streamed raw PCM over HTTP, first audio in ~200ms.</summary>
internal sealed class DeepgramAuraTts(string apiKey, string voice) : ITextToSpeech
{
    // Long-lived pooled connection: follow-up turns reuse the warm TLS session.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public string Name => "Aura-2";

    /// <summary>
    /// The TLS handshake to Deepgram measured 0.6–1.1s from here — more than
    /// the synthesis itself. A free request at hotkey time opens the pooled
    /// connection so the first spoken sentence doesn't pay for it.
    /// </summary>
    public static void Warm()
    {
        if (SecretStore.Get("deepgram") is not { } key) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Token", key);
                using var _ = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            }
            catch { /* best effort */ }
        });
    }

    public async Task SpeakAsync(string sentence, Action<byte[]> onPcm, CancellationToken ct)
    {
        // Deepgram closes idle keep-alive connections after a few seconds, and .NET
        // won't transparently retry a POST on a stale pooled connection — so one
        // retry here (nothing has been played yet) turns that into a non-event.
        try
        {
            await SpeakOnceAsync(sentence, onPcm, ct);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            await SpeakOnceAsync(sentence, onPcm, ct);
        }
    }

    private async Task SpeakOnceAsync(string sentence, Action<byte[]> onPcm, CancellationToken ct)
    {
        var url = $"https://api.deepgram.com/v1/speak?model={Uri.EscapeDataString(voice)}" +
                  $"&encoding=linear16&sample_rate={AudioPlayer.SampleRate}&container=none";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { text = sentence }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Aura-2 {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        // Network reads can split a 16-bit sample; hold back an odd trailing byte.
        var buf = new byte[4800]; // ~100ms
        int have = 0, n;
        while ((n = await stream.ReadAsync(buf.AsMemory(have), ct)) > 0)
        {
            have += n;
            int even = have & ~1;
            if (even > 0) onPcm(buf[..even]);
            if (have != even) buf[0] = buf[even];
            have -= even;
        }
    }
}

/// <summary>A downloadable on-device voice model (sherpa-onnx release asset).</summary>
internal sealed record LocalVoice(string Engine, string Id, string Label, string Folder, int Speaker, int ApproxMb)
{
    public string Dir => Path.Combine(LocalTts.ModelsDir, Folder);
    public string DownloadUrl => $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Folder}.tar.bz2";
    public string ModelFile => Engine == "kokoro" ? Path.Combine(Dir, "model.onnx") : Path.Combine(Dir, Id + ".onnx");
    public bool IsInstalled => File.Exists(ModelFile);
}

/// <summary>
/// Free, fully local voices via sherpa-onnx. Two engines, measured on this
/// class of laptop CPU (i5-13450HX):
///   Piper  — ~0.05x real-time (a 3.6s sentence renders in ~180ms). Snappy; the local default.
///   Kokoro — ~1x real-time. Noticeably more natural, but the first sentence
///            lags ~1s and long replies can stutter. (The int8 build was 2.6x
///            slower than real-time here, so the full-precision model is used.)
/// One model is kept loaded (~100–300MB RAM) after first use; switching voice reloads.
/// </summary>
internal sealed class LocalTts : ITextToSpeech
{
    public static string ModelsDir => Path.Combine(Config.Dir, "models");

    public static readonly LocalVoice[] Voices =
    {
        new("piper", "en_GB-alan-medium", "Alan · British", "vits-piper-en_GB-alan-medium", 0, 64),
        new("piper", "en_GB-northern_english_male-medium", "Northern · British", "vits-piper-en_GB-northern_english_male-medium", 0, 64),
        new("piper", "en_GB-jenny_dioco-medium", "Jenny · British", "vits-piper-en_GB-jenny_dioco-medium", 0, 64),
        new("piper", "en_US-ryan-medium", "Ryan · American", "vits-piper-en_US-ryan-medium", 0, 64),
        // Kokoro v0.19 speaker order inside voices.bin
        new("kokoro", "bm_george", "George · British", "kokoro-en-v0_19", 9, 305),
        new("kokoro", "bm_lewis", "Lewis · British", "kokoro-en-v0_19", 10, 305),
        new("kokoro", "bf_emma", "Emma · British", "kokoro-en-v0_19", 7, 305),
        new("kokoro", "am_adam", "Adam · American", "kokoro-en-v0_19", 5, 305),
        new("kokoro", "am_michael", "Michael · American", "kokoro-en-v0_19", 6, 305),
        new("kokoro", "af_bella", "Bella · American", "kokoro-en-v0_19", 1, 305),
    };

    public static LocalVoice Find(string engine, string id) =>
        Voices.FirstOrDefault(v => v.Engine == engine && v.Id == id) ?? Voices.First(v => v.Engine == engine);

    private static OfflineTts? _engine;
    private static string? _loadedDir;
    private static readonly object EngineGate = new();

    private readonly LocalVoice _voice;

    public LocalTts(LocalVoice voice) => _voice = voice;

    public string Name => _voice.Engine == "kokoro" ? "Kokoro" : "Piper";

    private static OfflineTts Engine(LocalVoice v)
    {
        lock (EngineGate)
        {
            if (_engine != null && _loadedDir == v.Dir) return _engine;
            _engine?.Dispose();
            _engine = null;

            var config = new OfflineTtsConfig();
            if (v.Engine == "kokoro")
            {
                config.Model.Kokoro.Model = v.ModelFile;
                config.Model.Kokoro.Voices = Path.Combine(v.Dir, "voices.bin");
                config.Model.Kokoro.Tokens = Path.Combine(v.Dir, "tokens.txt");
                config.Model.Kokoro.DataDir = Path.Combine(v.Dir, "espeak-ng-data");
                config.Model.Kokoro.LengthScale = 1.0f;
            }
            else
            {
                config.Model.Vits.Model = v.ModelFile;
                config.Model.Vits.Tokens = Path.Combine(v.Dir, "tokens.txt");
                config.Model.Vits.DataDir = Path.Combine(v.Dir, "espeak-ng-data");
                config.Model.Vits.NoiseScale = 0.667f;
                config.Model.Vits.NoiseScaleW = 0.8f;
                config.Model.Vits.LengthScale = 1.0f;
            }
            // 4 threads measured fastest; more lands work on E-cores and gets slower.
            config.Model.NumThreads = 4;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;
            _engine = new OfflineTts(config);
            _loadedDir = v.Dir;
            return _engine;
        }
    }

    /// <summary>Frees the loaded model (~100–300MB) — called after a few idle minutes.</summary>
    public static void Unload()
    {
        lock (EngineGate)
        {
            _engine?.Dispose();
            _engine = null;
            _loadedDir = null;
        }
    }

    /// <summary>Loads the model in the background so the first reply isn't slow.</summary>
    public static void Warm(LocalVoice v) => _ = Task.Run(() =>
    {
        try
        {
            lock (EngineGate) Engine(v).Generate("Ready.", 1.0f, v.Speaker).Dispose(); // first inference pays ~0.5s of graph setup
        }
        catch { }
    });

    public Task SpeakAsync(string sentence, Action<byte[]> onPcm, CancellationToken ct) => Task.Run(() =>
    {
        OfflineTtsGeneratedAudio audio;
        lock (EngineGate) audio = Engine(_voice).Generate(sentence, 1.0f, _voice.Speaker);
        try
        {
            ct.ThrowIfCancellationRequested();
            var samples = audio.Samples;
            if (audio.SampleRate != AudioPlayer.SampleRate)
                samples = Resample(samples, audio.SampleRate);
            onPcm(Pcm.FloatToPcm16(samples));
        }
        finally { audio.Dispose(); } // native buffer; not IDisposable in the binding
    }, ct);

    private static float[] Resample(float[] input, int fromRate)
    {
        var resampler = new WdlResamplingSampleProvider(new RawFloatProvider(input, fromRate), AudioPlayer.SampleRate);
        var output = new List<float>((int)((long)input.Length * AudioPlayer.SampleRate / fromRate) + 1024);
        var buf = new float[4096];
        int n;
        while ((n = resampler.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) output.Add(buf[i]);
        return output.ToArray();
    }

    /// <summary>Downloads and unpacks a voice with Windows' built-in tar. Progress is 0..1.</summary>
    public static async Task DownloadAsync(LocalVoice voice, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDir);
        var archive = Path.Combine(ModelsDir, voice.Folder + ".tar.bz2");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var resp = await http.GetAsync(voice.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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

            var tar = await ShellOut.RunAsync("tar", $"-xjf \"{archive}\" -C \"{ModelsDir}\"", timeout: TimeSpan.FromMinutes(5));
            if (!voice.IsInstalled)
                throw new InvalidOperationException("Unpacking the voice failed. " + tar.StdErr);
            progress.Report(1);
        }
        finally
        {
            try { File.Delete(archive); } catch { }
        }
    }

    private sealed class RawFloatProvider(float[] data, int rate) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}

/// <summary>Windows' built-in voice (WinRT) — zero dependencies, the always-works fallback.</summary>
internal sealed class WindowsTts : ITextToSpeech
{
    public string Name => "Windows voice";

    public async Task SpeakAsync(string sentence, Action<byte[]> onPcm, CancellationToken ct)
    {
        using var synth = new global::Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        var male = global::Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
            .FirstOrDefault(v => v.Gender == global::Windows.Media.SpeechSynthesis.VoiceGender.Male
                                 && v.Language.StartsWith("en-GB", StringComparison.OrdinalIgnoreCase));
        if (male != null) synth.Voice = male;

        using var stream = await synth.SynthesizeTextToStreamAsync(sentence);
        ct.ThrowIfCancellationRequested();

        var bytes = new byte[stream.Size];
        using (var reader = new global::Windows.Storage.Streams.DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        using var wav = new WaveFileReader(new MemoryStream(bytes));
        ISampleProvider samples = wav.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = samples.ToMono();
        if (samples.WaveFormat.SampleRate != AudioPlayer.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, AudioPlayer.SampleRate);

        var pcm = new SampleToWaveProvider16(samples);
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = pcm.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        onPcm(ms.ToArray());
    }
}
