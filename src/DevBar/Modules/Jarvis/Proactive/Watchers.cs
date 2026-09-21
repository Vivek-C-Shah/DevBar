using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Proactive;

/// <summary>
/// Claude Code session state, pushed by the DevBar hook (see ClaudeHook) into
/// %LOCALAPPDATA%\DevBar\claude-sessions. A FileSystemWatcher is an OS
/// notification - zero cost until a file actually changes.
///   anything → waiting : "Claude Code in X needs you."
///   working  → idle    : "Claude Code in X has finished."
/// </summary>
internal sealed class ClaudeSessionWatcher : IDisposable
{
    public static string Dir => Path.Combine(Config.Dir, "claude-sessions");

    private readonly FileSystemWatcher _fsw;
    private readonly Dictionary<string, string> _lastState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _ui;
    private readonly Action<string> _notify;
    private readonly Dictionary<string, DispatcherTimer> _debounce = new();

    public ClaudeSessionWatcher(Dispatcher ui, Action<string> notify)
    {
        _ui = ui;
        _notify = notify;
        Directory.CreateDirectory(Dir);
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            if (Read(f) is { } s) _lastState[f] = s.State;

        _fsw = new FileSystemWatcher(Dir, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        _fsw.Changed += (_, e) => _ui.BeginInvoke(() => Debounced(e.FullPath));
        _fsw.Created += (_, e) => _ui.BeginInvoke(() => Debounced(e.FullPath));
        _fsw.Deleted += (_, e) => _ui.BeginInvoke(() => _lastState.Remove(e.FullPath));
        _fsw.EnableRaisingEvents = true;
    }

    // Writers often touch a file twice in quick succession; look once it settles.
    private void Debounced(string path)
    {
        if (!_debounce.TryGetValue(path, out var t))
        {
            t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) => { t.Stop(); _debounce.Remove(path); Evaluate(path); };
            _debounce[path] = t;
        }
        t.Stop();
        t.Start();
    }

    private void Evaluate(string path)
    {
        if (Read(path) is not { } s) return;
        _lastState.TryGetValue(path, out var prev);
        _lastState[path] = s.State;
        if (prev == s.State) return;

        if (s.State == "waiting") _notify($"Claude Code in {s.Project} needs you.");
        else if (s.State == "idle" && prev is "working" or "waiting") _notify($"Claude Code in {s.Project} has finished.");
    }

    private static (string Project, string State)? Read(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return (r.TryGetProperty("project", out var p) ? p.GetString() ?? "a session" : "a session",
                    r.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    public void Dispose() => _fsw.Dispose();
}

/// <summary>
/// Build Pulse targets (config CiWatchTargets): when files under a target stop
/// changing for a few seconds after a burst of writes, the build is done.
/// Only exists if targets are configured.
/// </summary>
internal sealed class BuildWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    private readonly List<FileSystemWatcher> _watchers = new();

    public BuildWatcher(Config config, Dispatcher ui, Action<string> notify)
    {
        foreach (var target in config.CiWatchTargets)
        {
            try
            {
                bool isDir = Directory.Exists(target.Path);
                var dir = isDir ? target.Path : Path.GetDirectoryName(target.Path);
                if (dir is null || !Directory.Exists(dir)) continue;

                var fsw = new FileSystemWatcher(dir, isDir ? "*" : Path.GetFileName(target.Path))
                {
                    IncludeSubdirectories = isDir,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                };
                var name = string.IsNullOrWhiteSpace(target.Name) ? Path.GetFileName(target.Path.TrimEnd('\\')) : target.Name;
                DispatcherTimer? settle = null;
                void OnChange(object? _, FileSystemEventArgs __) => ui.BeginInvoke(() =>
                {
                    if (settle is null)
                    {
                        settle = new DispatcherTimer { Interval = Settle };
                        settle.Tick += (_, _) => { settle!.Stop(); notify($"The {name} build just finished."); };
                    }
                    settle.Stop();
                    settle.Start();
                });
                fsw.Changed += OnChange;
                fsw.Created += OnChange;
                fsw.EnableRaisingEvents = true;
                _watchers.Add(fsw);
            }
            catch { /* unreadable target - skip */ }
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
