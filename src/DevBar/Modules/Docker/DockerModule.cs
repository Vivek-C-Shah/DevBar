using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Sdk;

namespace DevBar.Modules.Docker;

public enum DockerState { Ok, NotFound, NotRunning }

/// <summary>
/// Local Docker Desktop containers via `docker ps -a` (shelled out - no
/// Docker.DotNet dependency, matching this app's bias toward fewer NuGet
/// packages over a lighter footprint). Polls on a timer while expanded, same
/// pattern as Ports/Claude, rather than streaming `docker events`: this
/// project's other system-state modules all use a proven poll-off-UI-thread
/// approach, and staying consistent with that beats a marginally more
/// real-time but harder-to-verify event stream for a first release.
/// </summary>
public sealed class DockerModule : IDevBarModule
{
    public string Id => "docker";
    public string DisplayName => "Docker";
    public string IconGlyph => "";

    public ObservableCollection<ContainerInfo> Containers { get; } = new();
    public DockerState State { get; private set; } = DockerState.Ok;
    public event Action? StateChanged;

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public UserControl BuildCard() => new DockerCard(this);

    public void OnExpanded()
    {
        _ = RefreshAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public void OnCollapsed()
    {
        _timer?.Stop();
        _timer = null;
    }

    public async Task ToggleAsync(ContainerInfo info)
    {
        var verb = info.IsRunning ? "stop" : "start";
        await ShellOut.RunAsync("docker", $"{verb} {info.Id}", timeout: TimeSpan.FromSeconds(10));
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var (state, found) = await QueryAsync();
            SetState(state);
            if (state != DockerState.Ok)
            {
                Containers.Clear();
                return;
            }

            for (int i = Containers.Count - 1; i >= 0; i--)
                if (!found.Any(f => f.Id == Containers[i].Id))
                    Containers.RemoveAt(i);

            foreach (var f in found)
            {
                int idx = -1;
                for (int i = 0; i < Containers.Count; i++)
                    if (Containers[i].Id == f.Id) { idx = i; break; }

                if (idx < 0) Containers.Add(f);
                else if (Containers[idx].Status != f.Status) Containers[idx] = f;
            }
        }
        finally { _refreshing = false; }
    }

    internal static async Task<(DockerState State, List<ContainerInfo> Containers)> QueryAsync()
    {
        // {{.ID}}\t{{.Names}}\t{{.Image}}\t{{.Status}} - tab-delimited is
        // trivial to split and Docker's own JSON-per-line format needs no
        // extra parsing dependency for four flat fields.
        var result = await ShellOut.RunAsync("docker", "ps -a --format \"{{.ID}}\\t{{.Names}}\\t{{.Image}}\\t{{.Status}}\"");

        if (!result.Started)
            return (DockerState.NotFound, new());

        if (result.ExitCode != 0 || result.StdErr.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase)
                                   || result.StdErr.Contains("error during connect", StringComparison.OrdinalIgnoreCase))
            return (DockerState.NotRunning, new());

        var found = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 4)
            .Select(parts => new ContainerInfo { Id = parts[0], Name = parts[1], Image = parts[2], Status = parts[3].TrimEnd('\r') })
            .ToList();
        return (DockerState.Ok, found);
    }

    private void SetState(DockerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }
}
