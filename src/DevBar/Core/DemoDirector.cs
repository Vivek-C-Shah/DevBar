using System.IO;
using System.IO.Pipes;

namespace DevBar.Core;

/// <summary>
/// Scripted lines for recording demo videos: `DevBar.exe --jarvis-say "text"`
/// hands a line to the running bar, which treats it as if the mic heard it.
/// The pipe only exists when DevBar was started with --demo-director, and is
/// limited to the current user.
/// </summary>
internal static class DemoDirector
{
    private static string PipeName => $"DevBar.DemoDirector.{Environment.UserName}";

    public static void Send(string text)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        pipe.Connect(3000);
        using var w = new StreamWriter(pipe);
        w.WriteLine(text.ReplaceLineEndings(" "));
    }

    public static void Listen(Action<string> onLine, CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var r = new StreamReader(pipe);
                if (await r.ReadLineAsync(ct) is { Length: > 0 } line) onLine(line);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* client hung up early */ }
        }
    }, ct);
}
