using NAudio.Wave;

namespace DevBar.Modules.Jarvis.Audio;

/// <summary>
/// Streaming playback of 24 kHz mono PCM16 to the Windows default output
/// device. A fresh output is opened per reply, so if headphones were plugged
/// in (and Windows made them default) the next reply goes to them; otherwise
/// speakers. Stop() is instant - that's what makes interrupting Jarvis work.
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    public const int SampleRate = 24000;
    private static readonly WaveFormat Format = new(SampleRate, 16, 1);

    /// <summary>0..1 loudness of what's currently audible, for the orb.</summary>
    public event Action<float>? Level;

    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buffer;
    private readonly object _gate = new();

    public bool IsPlaying
    {
        get { lock (_gate) return _buffer is { BufferedBytes: > 0 }; }
    }

    public void Enqueue(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            if (_out is null) Open();
            _buffer!.AddSamples(pcm, 0, pcm.Length);
        }
    }

    private void Open()
    {
        _buffer = new BufferedWaveProvider(Format)
        {
            BufferDuration = TimeSpan.FromMinutes(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // emit silence while waiting for the next sentence instead of stopping
        };
        _out = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 120 };
        _out.Init(new MeteringProvider(_buffer, l => Level?.Invoke(l)));
        _out.Play();
    }

    /// <summary>Resolves once everything queued so far has been heard.</summary>
    public async Task WaitDrainedAsync(CancellationToken ct)
    {
        while (IsPlaying) await Task.Delay(40, ct);
        // WaveOut still holds ~DesiredLatency of audio after the buffer empties.
        await Task.Delay(150, ct);
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _out?.Stop(); } catch { /* device yanked */ }
            _out?.Dispose();
            _out = null;
            _buffer = null;
        }
        Level?.Invoke(0);
    }

    public void Dispose() => Stop();

    private sealed class MeteringProvider(IWaveProvider source, Action<float> onLevel) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            var slice = offset == 0 ? buffer : buffer[offset..(offset + n)];
            onLevel(Pcm.Rms(slice, n));
            return n;
        }
    }
}
