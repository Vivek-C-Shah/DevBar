using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using DevBar.Sdk;

namespace DevBar.Modules.Claude;

/// <summary>
/// Tracks active Claude Code CLI sessions. Two detection tiers, cheapest first:
///
/// 1. Status files — if the CLI (or a shell wrapper/hook) drops JSON files under
///    %LOCALAPPDATA%\DevBar\claude-sessions\*.json, we read those directly
///    (see README "Claude Code integration" for the tiny shape). No polling cost
///    beyond a directory listing.
/// 2. Process enumeration fallback — look for terminal-hosted processes whose
///    window title mentions Claude/claude. Best-effort; only runs while this
///    module is actually in view, and never runs while collapsed.
///
/// Both tiers run off the UI thread: Process.GetProcesses() over a busy dev
/// machine's full process list is not guaranteed fast, and this module must
/// never be the reason the bar's animation stutters.
/// </summary>
public sealed class ClaudeModule : IDevBarModule
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";
    public string IconGlyph => "";

    public ObservableCollection<ClaudeSession> Sessions { get; } = new();

    private static readonly string StatusDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBar", "claude-sessions");

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public UserControl BuildCard() => new ClaudeCard(this);

    public void OnExpanded()
    {
        _ = RefreshAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public void OnCollapsed()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var found = await Task.Run(() =>
            {
                var list = ReadStatusFiles().ToList();
                if (list.Count == 0) list.AddRange(ScanProcesses());
                return list;
            });

            for (int i = Sessions.Count - 1; i >= 0; i--)
                if (!found.Any(f => f.ProcessId == Sessions[i].ProcessId))
                    Sessions.RemoveAt(i);

            foreach (var f in found)
            {
                var existing = Sessions.FirstOrDefault(s => s.ProcessId == f.ProcessId);
                if (existing is null) Sessions.Add(f);
                else { existing.State = f.State; existing.LastSeen = f.LastSeen; }
            }
        }
        finally { _refreshing = false; }
    }

    internal static IEnumerable<ClaudeSession> ReadStatusFiles()
    {
        if (!Directory.Exists(StatusDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(StatusDir, "*.json"))
        {
            if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromHours(12)) continue; // abandoned session
            ClaudeSession? s = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var label = root.TryGetProperty("project", out var p) ? p.GetString() ?? "session" : "session";
                var pid = root.TryGetProperty("pid", out var pv) ? pv.GetInt32() : 0;
                var stateStr = root.TryGetProperty("state", out var sv) ? sv.GetString() : "working";
                var state = stateStr?.ToLowerInvariant() switch
                {
                    "waiting" => SessionState.WaitingOnYou,
                    "idle" => SessionState.Idle,
                    _ => SessionState.Working,
                };
                s = new ClaudeSession { Label = label, ProcessId = pid, State = state };
            }
            catch { /* skip malformed status file */ }
            if (s != null) yield return s;
        }
    }

    internal static IEnumerable<ClaudeSession> ScanProcesses()
    {
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { yield break; }

        foreach (var proc in procs)
        {
            string title;
            int pid;
            try
            {
                title = proc.MainWindowTitle;
                pid = proc.Id;
            }
            catch { continue; }
            finally { proc.Dispose(); }

            if (string.IsNullOrEmpty(title)) continue;
            if (!title.Contains("claude", StringComparison.OrdinalIgnoreCase)) continue;

            yield return new ClaudeSession { Label = title, ProcessId = pid, State = SessionState.Working };
        }
    }
}
