using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Sdk;

namespace DevBar.Modules.GitStatus;

/// <summary>
/// Watches a small user-configured list of repos (Config.GitWatchedRepos —
/// empty by default, opt-in via config.json) for dirty/clean state and
/// ahead/behind counts. Refreshes on a longer interval than Ports/Claude
/// (20s, not 3-4s): `git status`/`rev-list` shelling out per repo is
/// meaningfully slower than the in-process checks those modules do, and nobody
/// needs sub-4-second freshness on "is my repo dirty."
/// </summary>
public sealed class GitStatusModule : IDevBarModule
{
    public string Id => "git";
    public string DisplayName => "Git Status";
    public string IconGlyph => "";

    private readonly Config _config;
    public ObservableCollection<RepoStatus> Repos { get; } = new();

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public GitStatusModule(Config config) => _config = config;

    public UserControl BuildCard() => new GitStatusCard(this);

    public void OnExpanded()
    {
        _ = RefreshAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public void OnCollapsed()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void OpenFolder(RepoStatus repo)
    {
        try { Process.Start("explorer.exe", repo.Path); }
        catch { /* best effort */ }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var results = new List<RepoStatus>();
            foreach (var path in _config.GitWatchedRepos)
                results.Add(await ReadRepoAsync(path));

            for (int i = Repos.Count - 1; i >= 0; i--)
                if (!results.Any(r => r.Path == Repos[i].Path))
                    Repos.RemoveAt(i);

            foreach (var r in results)
            {
                int idx = -1;
                for (int i = 0; i < Repos.Count; i++)
                    if (Repos[i].Path == r.Path) { idx = i; break; }

                if (idx < 0) Repos.Add(r);
                else Repos[idx] = r;
            }
        }
        finally { _refreshing = false; }
    }

    private static async Task<RepoStatus> ReadRepoAsync(string path)
    {
        if (!Directory.Exists(path))
            return new RepoStatus { Path = path, Error = true };

        var branchResult = await ShellOut.RunAsync("git", "branch --show-current", path);
        if (!branchResult.Started || branchResult.ExitCode != 0)
            return new RepoStatus { Path = path, Error = true };

        var statusResult = await ShellOut.RunAsync("git", "status --porcelain", path);

        var status = new RepoStatus
        {
            Path = path,
            Branch = string.IsNullOrWhiteSpace(branchResult.StdOut) ? "(detached)" : branchResult.StdOut.Trim(),
            IsDirty = !string.IsNullOrWhiteSpace(statusResult.StdOut),
        };

        var aheadBehind = await ShellOut.RunAsync("git", "rev-list --left-right --count HEAD...@{u}", path);
        if (aheadBehind.Started && aheadBehind.ExitCode == 0)
        {
            var parts = aheadBehind.StdOut.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int ahead) && int.TryParse(parts[1], out int behind))
            {
                status.Ahead = ahead;
                status.Behind = behind;
                status.HasUpstream = true;
            }
        }

        return status;
    }
}
