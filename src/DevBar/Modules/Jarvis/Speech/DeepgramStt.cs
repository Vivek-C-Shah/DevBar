using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DevBar.Modules.Jarvis.Speech;

/// <summary>
/// Deepgram live transcription over one WebSocket for the whole session.
/// Deepgram's own endpointing decides when you've finished a sentence
/// (speech_final / UtteranceEnd), so there's no local VAD to tune.
/// While Jarvis is talking the mic is muted and KeepAlive holds the socket
/// open, so the follow-up turn needs no reconnect.
/// </summary>
internal sealed class DeepgramStt : IAsyncDisposable
{
    /// <summary>Everything heard so far in the current utterance, including the unstable tail.</summary>
    public event Action<string>? Interim;
    /// <summary>A complete utterance — you stopped talking.</summary>
    public event Action<string>? Utterance;
    public event Action<string>? Failed;

    private readonly ClientWebSocket _ws = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _finals = new();
    private Task? _sendLoop, _recvLoop;
    private volatile bool _muted;

    public async Task ConnectAsync(string apiKey, string model, string language, int sampleRate, int endpointingMs, CancellationToken ct)
    {
        var url = "wss://api.deepgram.com/v1/listen"
                  + $"?model={Uri.EscapeDataString(model)}&language={Uri.EscapeDataString(language)}"
                  + $"&encoding=linear16&sample_rate={sampleRate}&channels=1"
                  + "&interim_results=true&smart_format=true&punctuate=true"
                  + $"&endpointing={Math.Clamp(endpointingMs, 100, 3000)}&utterance_end_ms=1000&vad_events=true"
                  + "&keyterm=Jarvis&keyterm=DevBar";
        _ws.Options.SetRequestHeader("Authorization", "Token " + apiKey);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(8));
        await _ws.ConnectAsync(new Uri(url), linked.Token);

        _sendLoop = Task.Run(SendLoopAsync);
        _recvLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Muted: audio is dropped, the socket is kept alive. Also discards any half-heard utterance.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (value) lock (_finals) _finals.Clear();
        }
    }

    public void Send(byte[] pcm)
    {
        if (!_muted) _outbox.Writer.TryWrite(pcm);
    }

    private async Task SendLoopAsync()
    {
        var keepAlive = Encoding.UTF8.GetBytes("{\"type\":\"KeepAlive\"}");
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var chunk = await _outbox.Reader.ReadAsync(wait.Token);
                    await _ws.SendAsync(chunk, WebSocketMessageType.Binary, true, _cts.Token);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    // nothing to send for 4s (muted while speaking) — Deepgram drops idle sockets at ~10s
                    await _ws.SendAsync(keepAlive, WebSocketMessageType.Text, true, _cts.Token);
                }
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            Failed?.Invoke("Speech connection dropped: " + ex.Message);
        }
        catch { /* shutting down */ }
    }

    private async Task ReceiveLoopAsync()
    {
        var buf = new byte[16 * 1024];
        var msg = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (!_cts.IsCancellationRequested)
                        Failed?.Invoke($"Deepgram closed the connection ({r.CloseStatusDescription ?? r.CloseStatus?.ToString()}).");
                    return;
                }
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                Handle(msg.ToArray());
                msg.SetLength(0);
            }
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            Failed?.Invoke("Speech connection dropped: " + ex.Message);
        }
        catch { /* shutting down */ }
    }

    private void Handle(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "UtteranceEnd")
        {
            Flush();
            return;
        }
        if (type != "Results" || _muted) return;

        var text = root.GetProperty("channel").GetProperty("alternatives")[0].GetProperty("transcript").GetString() ?? "";
        bool isFinal = root.TryGetProperty("is_final", out var f) && f.GetBoolean();
        bool speechFinal = root.TryGetProperty("speech_final", out var sf) && sf.GetBoolean();

        string heard;
        lock (_finals)
        {
            if (isFinal && text.Length > 0) _finals.Add(text);
            heard = string.Join(" ", _finals) + (isFinal || text.Length == 0 ? "" : " " + text);
        }
        if (heard.Trim().Length > 0) Interim?.Invoke(heard.Trim());
        if (speechFinal) Flush();
    }

    private void Flush()
    {
        string utterance;
        lock (_finals)
        {
            utterance = string.Join(" ", _finals).Trim();
            _finals.Clear();
        }
        if (utterance.Length > 0) Utterance?.Invoke(utterance);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch { /* already gone */ }
        _cts.Cancel();
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        foreach (var t in new[] { _sendLoop, _recvLoop })
            if (t != null) try { await t; } catch { }
    }
}
