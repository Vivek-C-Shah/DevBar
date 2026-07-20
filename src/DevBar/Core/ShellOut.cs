using System.Diagnostics;

namespace DevBar.Core;

internal static class ShellOut
{
    public sealed record Result(bool Started, int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs a command and captures output. Never throws — a missing exe
    /// (docker.exe, git.exe not on PATH) comes back as Started=false rather
    /// than an exception, so callers can distinguish "not installed" from
    /// "installed but returned an error" cleanly. Always call from a
    /// background thread (Task.Run) — this blocks until the process exits.
    /// </summary>
    public static async Task<Result> RunAsync(string exe, string args, string? workingDir = null, TimeSpan? timeout = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? "",
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new Result(false, -1, "", "");

            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new Result(true, -1, "", "timed out");
            }

            return new Result(true, proc.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // exe not found on PATH
            return new Result(false, -1, "", "");
        }
        catch
        {
            return new Result(false, -1, "", "");
        }
    }
}
