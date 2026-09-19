using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Modules.Jarvis.Brain;
using DevBar.Modules.Claude;
using DevBar.Modules.ClipboardHistory;
using DevBar.Modules.Docker;
using DevBar.Modules.GitStatus;
using DevBar.Modules.Ports;
using DevBar.Sdk;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace DevBar.Modules.Jarvis.Tools;

internal static class BuiltInTools
{
    public static List<JarvisTool> Create(Config config, Func<IReadOnlyList<IDevBarModule>> modules, ProviderRouter vision) => new()
    {
        new RememberTool(),
        new RecallTool(),
        new ForgetTool(),
        new WeatherTool(config.Jarvis),
        new SystemStatusTool(),
        new LookAtScreenTool(vision),
        new TypeTextTool(),
        new RunCommandTool(config),
        new SetReminderTool(),
        new ListRemindersTool(),
        new CancelReminderTool(),
        new ListPortsTool(),
        new KillPortTool(),
        new DockerListTool(),
        new DockerContainerTool(),
        new GitStatusTool(config),
        new ClaudeSessionsTool(),
        new MediaTool(),
        new VolumeTool(),
        new OpenAppTool(),
        new OpenUrlTool(),
        new WebSearchTool(),
        new ClipboardRecentTool(modules),
        new CopyToClipboardTool(),
    };
}

// ---------------- ports ----------------

internal sealed class ListPortsTool : JarvisTool
{
    public override string Name => "list_ports";
    public override string Description => "List TCP ports listening on this PC with the owning process (dev servers, databases).";

    // Things a developer starts on purpose. Everything else listening (Adobe, NVIDIA,
    // VS Code's internal ports…) is summarised so a spoken answer stays short.
    private static readonly string[] DevProcesses =
    {
        "node", "bun", "deno", "python", "pythonw", "uvicorn", "java", "dotnet", "go", "ruby", "php", "rails",
        "postgres", "mysqld", "mongod", "redis-server", "nginx", "httpd", "caddy", "docker", "com.docker.backend",
        "wslrelay", "vite", "next-server", "esbuild", "cargo",
    };

    public override async Task<string> RunAsync(JsonElement args)
    {
        var ports = await Task.Run(PortsModule.ReadListeners);
        var user = ports.Where(p => p.Pid > 4 && p.ProcessName is not ("svchost" or "System" or "lsass" or "wininit" or "services" or "spoolsv"))
                        .DistinctBy(p => p.Port).OrderBy(p => p.Port).ToList();
        var dev = user.Where(p => DevProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
                                  || p.ProcessName.EndsWith(".dev", StringComparison.OrdinalIgnoreCase)).ToList();
        var other = user.Except(dev).Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var parts = new List<string>();
        parts.Add(dev.Count == 0 ? "No dev servers or databases are listening."
            : "Dev servers: " + string.Join("; ", dev.Select(p => $"{p.Port} {p.ProcessName} (pid {p.Pid})")));
        if (other.Count > 0)
            parts.Add($"Also {other.Count} background apps listening ({string.Join(", ", other.Take(8))}) — only mention if asked.");
        return string.Join(" ", parts);
    }
}

internal sealed class KillPortTool : JarvisTool
{
    public override string Name => "kill_port";
    public override string Description => "Kill the process listening on a TCP port (e.g. a stuck dev server on 3000).";
    protected override (string, string, string)[] Params => new[] { ("port", "integer", "Port number") };
    public override Risk RiskOf(JsonElement args) => Risk.Destructive;

    public override string Describe(JsonElement args)
    {
        int port = Int(args, "port") ?? 0;
        var owner = PortsModule.ReadListeners().FirstOrDefault(p => p.Port == port);
        return owner is null ? $"kill whatever is on port {port}" : $"kill {owner.ProcessName} on port {port}";
    }

    public override async Task<string> RunAsync(JsonElement args)
    {
        int port = Int(args, "port") ?? 0;
        var owners = (await Task.Run(PortsModule.ReadListeners)).Where(p => p.Port == port).DistinctBy(p => p.Pid).ToList();
        if (owners.Count == 0) return $"Nothing is listening on port {port}.";
        var killed = new List<string>();
        foreach (var o in owners)
        {
            try { using var p = Process.GetProcessById(o.Pid); p.Kill(entireProcessTree: true); killed.Add($"{o.ProcessName} ({o.Pid})"); }
            catch (Exception ex) { return $"Couldn't kill {o.ProcessName} ({o.Pid}): {ex.Message}"; }
        }
        return $"Killed {string.Join(", ", killed)} on port {port}.";
    }
}

// ---------------- docker ----------------

internal sealed class DockerListTool : JarvisTool
{
    public override string Name => "docker_list";
    public override string Description => "List Docker containers and whether each is running.";

    public override async Task<string> RunAsync(JsonElement args)
    {
        var (state, containers) = await DockerModule.QueryAsync();
        return state switch
        {
            DockerState.NotFound => "Docker isn't installed.",
            DockerState.NotRunning => "Docker Desktop isn't running.",
            _ when containers.Count == 0 => "No containers exist.",
            _ => string.Join("; ", containers.Select(c => $"{c.Name} ({c.Image}): {c.Status}")),
        };
    }
}

internal sealed class DockerContainerTool : JarvisTool
{
    public override string Name => "docker_container";
    public override string Description => "Start, stop or restart a Docker container by (partial) name.";
    protected override (string, string, string)[] Params => new[]
    {
        ("name", "string", "Container name or part of it"),
        ("action", "start|stop|restart", "What to do"),
    };
    public override Risk RiskOf(JsonElement args) => Str(args, "action") == "start" ? Risk.Reversible : Risk.Destructive;
    public override string Describe(JsonElement args) => $"{Str(args, "action")} the {Str(args, "name")} container";

    public override async Task<string> RunAsync(JsonElement args)
    {
        var name = Str(args, "name");
        var action = Str(args, "action");
        if (action is not ("start" or "stop" or "restart")) return "Action must be start, stop or restart.";

        var (state, containers) = await DockerModule.QueryAsync();
        if (state != DockerState.Ok) return state == DockerState.NotFound ? "Docker isn't installed." : "Docker Desktop isn't running.";

        var match = containers.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? containers.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    ?? containers.FirstOrDefault(c => c.Image.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return $"No container matches '{name}'. Containers: {string.Join(", ", containers.Select(c => c.Name))}.";

        var r = await ShellOut.RunAsync("docker", $"{action} {match.Id}", timeout: TimeSpan.FromSeconds(30));
        return r.ExitCode == 0 ? $"Container {match.Name}: {action} done." : $"docker {action} failed: {r.StdErr.Trim()}";
    }
}

// ---------------- git / claude ----------------

internal sealed class GitStatusTool(Config config) : JarvisTool
{
    public override string Name => "git_status";
    public override string Description => "Branch, uncommitted changes and ahead/behind for the user's watched git repos, or one given path.";
    protected override (string, string, string)[] Params => new[] { ("path?", "string", "Repo folder; omit for all watched repos") };

    public override async Task<string> RunAsync(JsonElement args)
    {
        var path = Str(args, "path");
        var repos = path.Length > 0 ? new List<string> { path } : config.GitWatchedRepos;
        if (repos.Count == 0) return "No repos are watched. Vivek can add folders to GitWatchedRepos in DevBar's config.json.";

        var lines = new List<string>();
        foreach (var repo in repos)
        {
            var s = await GitStatusModule.ReadRepoAsync(repo);
            if (s.Error) { lines.Add($"{s.Name}: not a readable git repo"); continue; }
            var sync = s.HasUpstream ? $", {s.Ahead} ahead / {s.Behind} behind" : ", no upstream";
            lines.Add($"{s.Name} on {s.Branch}: {(s.IsDirty ? "uncommitted changes" : "clean")}{sync}");
        }
        return string.Join("; ", lines);
    }
}

internal sealed class ClaudeSessionsTool : JarvisTool
{
    public override string Name => "claude_sessions";
    public override string Description => "Which Claude Code CLI sessions are running and whether each is working, idle or waiting on the user.";

    public override async Task<string> RunAsync(JsonElement args)
    {
        var sessions = await Task.Run(() =>
        {
            var list = ClaudeModule.ReadStatusFiles().ToList();
            if (list.Count == 0) list.AddRange(ClaudeModule.ScanProcesses());
            return list;
        });
        if (sessions.Count == 0) return "No Claude Code sessions are running.";
        return string.Join("; ", sessions.Select(s => $"{s.Label}: {s.State switch
        {
            SessionState.WaitingOnYou => "waiting on you",
            SessionState.Idle => "idle",
            _ => "working",
        }}"));
    }
}

// ---------------- media / volume ----------------

internal sealed class MediaTool : JarvisTool
{
    public override string Name => "media";
    public override string Description => "Control or inspect whatever is playing system-wide (Spotify, YouTube in a browser, etc).";
    protected override (string, string, string)[] Params => new[] { ("action", "now_playing|play_pause|play|pause|next|previous", "What to do") };
    public override Risk RiskOf(JsonElement args) => Str(args, "action") == "now_playing" ? Risk.Read : Risk.Reversible;

    public override async Task<string> RunAsync(JsonElement args)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        if (session is null) return "Nothing is playing.";

        switch (Str(args, "action"))
        {
            case "play_pause": await session.TryTogglePlayPauseAsync(); break;
            case "play": await session.TryPlayAsync(); break;
            case "pause": await session.TryPauseAsync(); break;
            case "next": await session.TrySkipNextAsync(); break;
            case "previous": await session.TrySkipPreviousAsync(); break;
        }

        await Task.Delay(400); // let the player publish its new track/state
        var props = await session.TryGetMediaPropertiesAsync();
        var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        return $"{(playing ? "Playing" : "Paused")}: {props.Title}{(string.IsNullOrEmpty(props.Artist) ? "" : " by " + props.Artist)}.";
    }
}

internal sealed class VolumeTool : JarvisTool
{
    public override string Name => "system_volume";
    public override string Description => "Get or change the Windows master volume.";
    protected override (string, string, string)[] Params => new[]
    {
        ("action", "get|set|up|down|mute|unmute", "What to do"),
        ("level?", "integer", "0-100, for set"),
    };
    public override Risk RiskOf(JsonElement args) => Str(args, "action") == "get" ? Risk.Read : Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var vol = device.AudioEndpointVolume;
        switch (Str(args, "action"))
        {
            case "set": vol.MasterVolumeLevelScalar = Math.Clamp((Int(args, "level") ?? 50) / 100f, 0, 1); vol.Mute = false; break;
            case "up": vol.MasterVolumeLevelScalar = Math.Min(1, vol.MasterVolumeLevelScalar + 0.1f); vol.Mute = false; break;
            case "down": vol.MasterVolumeLevelScalar = Math.Max(0, vol.MasterVolumeLevelScalar - 0.1f); break;
            case "mute": vol.Mute = true; break;
            case "unmute": vol.Mute = false; break;
        }
        return Task.FromResult($"Volume {(int)Math.Round(vol.MasterVolumeLevelScalar * 100)}%{(vol.Mute ? ", muted" : "")} on {device.FriendlyName}.");
    }
}

// ---------------- apps / web ----------------

internal sealed class OpenAppTool : JarvisTool
{
    public override string Name => "open_app";
    public override string Description => "Open an installed application by name (e.g. 'VS Code', 'Chrome', 'Spotify', 'Terminal', 'Settings').";
    protected override (string, string, string)[] Params => new[] { ("name", "string", "App name as the user said it") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var name = Str(args, "name").Trim();
        if (name.Length == 0) return Task.FromResult("No app name given.");

        var shortcut = FindShortcut(name);
        try
        {
            var target = shortcut ?? Alias(name);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return Task.FromResult($"Opened {(shortcut != null ? Path.GetFileNameWithoutExtension(shortcut) : name)}.");
        }
        catch
        {
            return Task.FromResult($"Couldn't find an app called '{name}'.");
        }
    }

    private static string Alias(string name) => name.ToLowerInvariant() switch
    {
        "vs code" or "vscode" or "code" or "visual studio code" => "code",
        "terminal" or "windows terminal" => "wt",
        "settings" => "ms-settings:",
        "explorer" or "file explorer" or "files" => "explorer",
        "calculator" => "calc",
        "task manager" => "taskmgr",
        _ => name,
    };

    private static string? FindShortcut(string query)
    {
        var q = query.ToLowerInvariant();
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        string? best = null;
        int bestScore = 0;
        foreach (var dir in dirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (n.Contains("uninstall") || n.Contains("readme") || n.Contains("help")) continue;
                int score = n == q ? 100
                    : n.StartsWith(q) ? 80 - Math.Min(20, n.Length - q.Length)
                    : n.Contains(q) ? 60 - Math.Min(20, n.Length - q.Length)
                    : q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(n.Contains) ? 40
                    : 0;
                if (score > bestScore) { bestScore = score; best = f; }
            }
        }
        return best;
    }
}

internal sealed class OpenUrlTool : JarvisTool
{
    public override string Name => "open_url";
    public override string Description => "Open a website in the default browser.";
    protected override (string, string, string)[] Params => new[] { ("url", "string", "Full URL or domain, e.g. github.com") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var url = Str(args, "url").Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Task.FromResult("That isn't a web address.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.FromResult($"Opened {uri.Host}.");
    }
}

internal sealed class WebSearchTool : JarvisTool
{
    public override string Name => "web_search";
    public override string Description => "Open a Google search in the browser (for things you can't answer yourself or the user wants to see).";
    protected override (string, string, string)[] Params => new[] { ("query", "string", "Search terms") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var q = Str(args, "query");
        Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true });
        return Task.FromResult($"Searched for {q}.");
    }
}

// ---------------- clipboard ----------------

internal sealed class ClipboardRecentTool(Func<IReadOnlyList<IDevBarModule>> modules) : JarvisTool
{
    public override string Name => "clipboard_recent";
    public override string Description => "Read the user's most recent clipboard copies (newest first).";
    protected override (string, string, string)[] Params => new[] { ("count?", "integer", "How many, default 3") };

    public override Task<string> RunAsync(JsonElement args)
    {
        var clip = modules().OfType<ClipboardModule>().FirstOrDefault();
        if (clip is null || clip.Entries.Count == 0) return Task.FromResult("The clipboard history is empty.");
        int n = Math.Clamp(Int(args, "count") ?? 3, 1, 10);
        return Task.FromResult(string.Join("\n", clip.Entries.Take(n).Select((e, i) =>
            $"{i + 1}. [{e.Kind}] {(e.Text.Length > 300 ? e.Text[..300] + "…" : e.Text)}")));
    }
}

internal sealed class CopyToClipboardTool : JarvisTool
{
    public override string Name => "copy_to_clipboard";
    public override string Description => "Put text on the clipboard (e.g. a snippet, command or answer the user asked for).";
    protected override (string, string, string)[] Params => new[] { ("text", "string", "Exact text to copy") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        Clipboard.SetText(Str(args, "text"));
        return Task.FromResult("Copied.");
    }
}
