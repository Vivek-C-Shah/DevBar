using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Sdk;

namespace DevBar.Modules.CiPulse;

/// <summary>
/// A quiet, glanceable status light per watched build target (Config.CiWatchTargets
/// — empty by default, opt-in). Deliberately minimal for v1, on purpose, not
/// as a placeholder: watching a directory/file's last-write time can honestly
/// tell you "something was built recently" (Success) or "nothing recent"
/// (Idle) — it cannot tell you *Running* or *Failed* without a real signal
/// (a CI API, or parsing build output), and showing those states from mtime
/// alone would mean fabricating information this module doesn't actually
/// have. A real CI/GitHub Actions integration is future work, not this.
/// </summary>
public sealed class CiPulseModule : IDevBarModule
{
    public string Id => "ci";
    public string DisplayName => "Build Pulse";
    public string IconGlyph => "";

    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(60);

    private readonly Config _config;
    public ObservableCollection<CiTargetStatus> Targets { get; } = new();

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public CiPulseModule(Config config) => _config = config;

    public UserControl BuildCard() => new CiPulseCard(this);

    public void OnExpanded()
    {
        _ = RefreshAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
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
            var targets = _config.CiWatchTargets;
            var results = await Task.Run(() => targets.Select(t => (t, mtime: GetLastModified(t.Path))).ToList());

            for (int i = Targets.Count - 1; i >= 0; i--)
                if (!targets.Any(t => t.Name == Targets[i].Name && t.Path == Targets[i].Path))
                    Targets.RemoveAt(i);

            foreach (var (cfg, mtime) in results)
            {
                var state = mtime.HasValue && DateTime.Now - mtime.Value < FreshWindow ? CiState.Success : CiState.Idle;
                var existing = Targets.FirstOrDefault(x => x.Name == cfg.Name && x.Path == cfg.Path);
                if (existing is null)
                    Targets.Add(new CiTargetStatus { Name = cfg.Name, Path = cfg.Path, State = state, LastModified = mtime });
                else { existing.State = state; existing.LastModified = mtime; }
            }
        }
        finally { _refreshing = false; }
    }

    private static DateTime? GetLastModified(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTime(path);
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault()?.LastWriteTime;
        }
        catch { /* inaccessible path — treat as no signal */ }
        return null;
    }
}
