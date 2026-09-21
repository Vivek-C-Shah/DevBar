using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace DevBar.Modules.Jarvis.Audio;

/// <summary>
/// 16 kHz mono PCM16 from the Windows default recording device. Only exists
/// while a Jarvis session (or the opt-in wake word) is live - the mic is never
/// open otherwise.
///
/// Two paths:
///  - default: WAVE_MAPPER at 16 kHz, with Windows' usual mic processing.
///    Fine for Deepgram, which is robust to it.
///  - raw: WASAPI with AUDCLNT_STREAMOPTIONS_RAW, which bypasses the driver's
///    effects (noise suppression / AGC / gating from Realtek, Nahimic etc.).
///    Those effects zero out 20-40% of samples and flatten the voice so far
///    that small on-device models (the wake word) can't recognise it. Measured
///    on a Realtek array mic: default path gated ~25% of samples to exact zero
///    and sat ~25 dB lower; raw had a normal noise floor. Falls back to the
///    default path if the device can't do raw.
/// </summary>
internal sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>Raw PCM16 chunks, ~20-50ms each. Raised on an audio worker thread.</summary>
    public event Action<byte[]>? Data;
    /// <summary>0..1 loudness per chunk, for the orb.</summary>
    public event Action<float>? Level;

    private readonly bool _raw;
    private readonly object _gate = new();
    private WaveInEvent? _wave;
    private WasapiCapture? _wasapi;
    private WdlResampler? _resampler;
    private MMDeviceEnumerator? _devices;
    private DefaultDeviceWatcher? _watcher;
    private bool _disposed;

    public MicCapture(bool raw = false) => _raw = raw;

    /// <summary>True when the raw (unprocessed) path is actually in use.</summary>
    public bool IsRaw => _wasapi != null;

    public void Start()
    {
        lock (_gate)
        {
            if (_raw && TryStartRaw()) return;
            StartWaveIn();
        }
    }

    private void StartWaveIn()
    {
        _wave = new WaveInEvent
        {
            DeviceNumber = -1, // WAVE_MAPPER: follows the default input device
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _wave.DataAvailable += OnWaveIn;
        _wave.StartRecording();
    }

    private void OnWaveIn(object? sender, WaveInEventArgs e)
    {
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        Emit(chunk);
    }

    private void Emit(byte[] chunk)
    {
        Data?.Invoke(chunk);
        Level?.Invoke(Pcm.Rms(chunk, chunk.Length));
    }

    // ---------- raw WASAPI path ----------

    private bool TryStartRaw()
    {
        WasapiCapture? cap = null;
        try
        {
            _devices ??= new MMDeviceEnumerator();
            var device = _devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            cap = new WasapiCapture(device, false, 50);
            if (!EnableRawMode(cap)) { cap.Dispose(); return false; }
            var fmt = cap.WaveFormat;
            // Shared-mode mix formats are float32 in practice; also take 16-bit PCM.
            bool supported = fmt.BitsPerSample == 32
                ? fmt.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible
                : fmt.BitsPerSample == 16 && fmt.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.Extensible;
            if (!supported) { cap.Dispose(); return false; }
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(fmt.SampleRate, SampleRate);
            cap.DataAvailable += OnRaw;
            cap.RecordingStopped += OnRawStopped;
            cap.StartRecording();
            _wasapi = cap;
            if (_watcher is null)
            {
                _watcher = new DefaultDeviceWatcher(Restart);
                _devices.RegisterEndpointNotificationCallback(_watcher);
            }
            JarvisSession.Trace($"mic: raw capture on {device.FriendlyName} ({fmt})");
            return true;
        }
        catch (Exception ex)
        {
            JarvisSession.Trace("mic: raw capture unavailable, using default path: " + ex.Message);
            try { cap?.Dispose(); } catch { }
            _wasapi = null;
            return false;
        }
    }

    /// <summary>
    /// IAudioClient2::SetClientProperties(RAW). Must run before Initialize, i.e.
    /// before StartRecording. Called through the vtable because NAudio's
    /// IAudioClient2 declaration doesn't re-declare the IAudioClient methods, so
    /// its slot numbers are off (the call lands on GetBufferSize).
    /// </summary>
    private static unsafe bool EnableRawMode(WasapiCapture cap)
    {
        const System.Reflection.BindingFlags priv = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var client = typeof(WasapiCapture).GetField("audioClient", priv)?.GetValue(cap);
        var itf = client is null ? null : typeof(AudioClient).GetField("audioClientInterface", priv)?.GetValue(client);
        if (itf is null) return false;

        var unk = Marshal.GetIUnknownForObject(itf);
        try
        {
            var iid = new Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"); // IID_IAudioClient2
            if (Marshal.QueryInterface(unk, ref iid, out var client2) != 0) return false;
            try
            {
                // AudioClientProperties { cbSize, bIsOffload, eCategory, Options }
                int* props = stackalloc int[] { 16, 0, 9 /* AudioCategory_Speech */, 1 /* AUDCLNT_STREAMOPTIONS_RAW */ };
                var vtable = *(IntPtr**)client2;
                var setClientProperties = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtable[16];
                return setClientProperties(client2, props) == 0;
            }
            finally { Marshal.Release(client2); }
        }
        finally { Marshal.Release(unk); }
    }

    private void OnRaw(object? sender, WaveInEventArgs e)
    {
        var cap = sender as WasapiCapture;
        var resampler = _resampler;
        if (cap is null || resampler is null || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        int channels = fmt.Channels, bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        bool isFloat = fmt.BitsPerSample == 32;

        // Downmix to mono straight into the resampler's input buffer.
        resampler.ResamplePrepare(frames, 1, out var inBuf, out var inOff);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            int at = f * channels * bytesPerSample;
            for (int c = 0; c < channels; c++, at += bytesPerSample)
                sum += isFloat ? BitConverter.ToSingle(e.Buffer, at) : BitConverter.ToInt16(e.Buffer, at) / 32768f;
            inBuf[inOff + f] = sum / channels;
        }
        var outBuf = new float[(int)((long)frames * SampleRate / fmt.SampleRate) + 32];
        int got = resampler.ResampleOut(outBuf, 0, frames, outBuf.Length, 1);
        if (got <= 0) return;

        var chunk = new byte[got * 2];
        for (int i = 0; i < got; i++)
        {
            short s = (short)Math.Clamp(outBuf[i] * 32767f, short.MinValue, short.MaxValue);
            chunk[i * 2] = (byte)s;
            chunk[i * 2 + 1] = (byte)(s >> 8);
        }
        Emit(chunk);
    }

    // Device unplugged / invalidated: pick up whatever is now the default.
    private void OnRawStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Restart();
    }

    /// <summary>Re-open on the current default device (headset plugged in, etc.).</summary>
    private void Restart() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopDevices();
            JarvisSession.Trace("mic: default device changed, reopening");
            try { Start(); } catch (Exception ex) { JarvisSession.Trace("mic: reopen failed: " + ex.Message); }
        }
    });

    private void StopDevices()
    {
        if (_wave != null)
        {
            _wave.DataAvailable -= OnWaveIn;
            try { _wave.StopRecording(); } catch { /* device yanked */ }
            _wave.Dispose();
            _wave = null;
        }
        if (_wasapi != null)
        {
            _wasapi.DataAvailable -= OnRaw;
            _wasapi.RecordingStopped -= OnRawStopped;
            try { _wasapi.StopRecording(); } catch { /* device yanked */ }
            _wasapi.Dispose();
            _wasapi = null;
        }
        _resampler = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopDevices();
            if (_watcher != null && _devices != null)
                try { _devices.UnregisterEndpointNotificationCallback(_watcher); } catch { }
            _watcher = null;
            _devices?.Dispose();
            _devices = null;
        }
    }

    private sealed class DefaultDeviceWatcher(Action onChange) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console) onChange();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

internal static class Pcm
{
    /// <summary>RMS of PCM16 mono, scaled so normal speech lands ~0.3–0.8.</summary>
    public static float Rms(byte[] buf, int count)
    {
        int samples = count / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(buf, i * 2);
            sum += (double)s * s;
        }
        double rms = Math.Sqrt(sum / samples) / 32768.0;
        return (float)Math.Clamp(rms * 6, 0, 1);
    }

    public static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)s;
            bytes[i * 2 + 1] = (byte)(s >> 8);
        }
        return bytes;
    }
}
