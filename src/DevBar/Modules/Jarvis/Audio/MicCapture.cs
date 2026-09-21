using NAudio.Wave;

namespace DevBar.Modules.Jarvis.Audio;

/// <summary>
/// 16 kHz mono PCM16 from the Windows default recording device (WAVE_MAPPER,
/// so plugging in a headset mic just works). Only exists while a Jarvis
/// session is live - the mic is never open at idle.
/// </summary>
internal sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>Raw PCM16 chunks, ~50ms each. Raised on a NAudio worker thread.</summary>
    public event Action<byte[]>? Data;
    /// <summary>0..1 loudness per chunk, for the orb.</summary>
    public event Action<float>? Level;

    private WaveInEvent? _wave;

    public void Start()
    {
        _wave = new WaveInEvent
        {
            DeviceNumber = -1, // WAVE_MAPPER: follows the default input device
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _wave.DataAvailable += OnData;
        _wave.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        Data?.Invoke(chunk);
        Level?.Invoke(Pcm.Rms(chunk, chunk.Length));
    }

    public void Dispose()
    {
        if (_wave is null) return;
        _wave.DataAvailable -= OnData;
        try { _wave.StopRecording(); } catch { /* device yanked */ }
        _wave.Dispose();
        _wave = null;
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
