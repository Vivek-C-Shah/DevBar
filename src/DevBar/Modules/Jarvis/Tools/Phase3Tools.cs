using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DevBar.Core;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;

namespace DevBar.Modules.Jarvis.Tools;

/// <summary>Live web answers without opening a browser (Gemini + Google Search grounding).</summary>
internal sealed class WebSearchTool(JarvisConfig cfg) : JarvisTool
{
    public override string Name => "web_search";
    public override string Description => "Look something up on the live web and get the answer back (news, versions, prices, scores, docs, anything recent). Runs silently in the background.";
    protected override (string, string, string)[] Params => new[] { ("query", "string", "A precise, self-contained search question") };
    public override bool IsSlow => true;
    public override bool ReadsUntrusted => true;

    public override async Task<string> RunAsync(JsonElement args) =>
        Google.Untrusted.Wrap(await WebSearch.AskAsync(Str(args, "query"), LocationService.Current?.Describe(), CancellationToken.None));
}

/// <summary>Only when the user wants to see results themselves.</summary>
internal sealed class ShowSearchTool : JarvisTool
{
    public override string Name => "show_search_in_browser";
    public override string Description => "Open a Google results page in the browser — only when the user explicitly asks to see/open the results.";
    protected override (string, string, string)[] Params => new[] { ("query", "string", "Search terms") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var q = Str(args, "query");
        Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true });
        return Task.FromResult($"Opened results for {q}.");
    }
}

/// <summary>Everything for a "good morning" in one call; the model turns it into a few spoken sentences.</summary>
internal sealed class DailyBriefTool(Config config, List<JarvisTool> siblings) : JarvisTool
{
    public override string Name => "daily_brief";
    public override string Description => "Gather a start-of-day briefing: weather, reminders, repos, Claude sessions, containers and anything wrong with the PC. Use for 'good morning', 'brief me', 'what's my day'.";
    public override bool IsSlow => true;

    public override async Task<string> RunAsync(JsonElement args)
    {
        async Task<string> RunWith(string name, string json)
        {
            try { return await siblings.First(t => t.Name == name).RunAsync(JsonDocument.Parse(json).RootElement); }
            catch (Exception ex) { return $"({name} unavailable: {ex.Message})"; }
        }
        Task<string> Run(string name) => RunWith(name, "{}");

        var parts = await Task.WhenAll(Run("weather"), Run("list_reminders"), Run("claude_sessions"), DockerSummaryAsync(), Run("system_status"));
        var git = config.GitWatchedRepos.Count > 0 ? await Run("git_status") : "No repos watched.";
        var calendar = siblings.Any(t => t.Name == "calendar_events") ? await Run("calendar_events") : "Google Calendar not connected.";
        var mail = siblings.Any(t => t.Name == "gmail_search")
            ? await RunWith("gmail_search", "{\"query\":\"is:unread in:inbox newer_than:2d\",\"max\":5}") : "Gmail not connected.";
        return $"""
            Date: {DateTime.Now:dddd d MMMM, h:mm tt}.
            Weather: {parts[0]}
            Calendar today: {calendar}
            Unread mail (last 2 days): {mail}
            Reminders: {parts[1]}
            Repos: {git}
            Claude Code: {parts[2]}
            Docker: {parts[3]}
            PC: {parts[4]}
            Brief the user in 3–5 spoken sentences: weather first, then today's meetings, then mail that looks important (not newsletters), then only what needs attention (skip anything empty or normal; mention low disk, low battery or very high RAM).
            """;
    }

    // A brief needs "what's running", not a roll-call of every stopped container.
    private static async Task<string> DockerSummaryAsync()
    {
        var (state, containers) = await Docker.DockerModule.QueryAsync();
        if (state != Docker.DockerState.Ok) return state == Docker.DockerState.NotFound ? "Docker not installed." : "Docker Desktop not running.";
        var running = containers.Where(c => c.IsRunning).Select(c => c.Name).ToList();
        int stopped = containers.Count - running.Count;
        return (running.Count == 0 ? "No containers running" : "Running: " + string.Join(", ", running)) + $"; {stopped} stopped.";
    }
}

/// <summary>
/// Hands a coding task to Claude Code in a new terminal, in the right repo.
/// The Claude module then shows the session; with the DevBar hook connected,
/// Jarvis speaks up when it's waiting on you or done.
/// </summary>
internal sealed class DelegateToClaudeTool(Config config) : JarvisTool
{
    public override string Name => "ask_claude_code";
    public override string Description => "Start a Claude Code session in a new terminal to do a coding task in a repo (fix a bug, write tests, refactor…).";
    protected override (string, string, string)[] Params => new[]
    {
        ("task", "string", "The instruction for Claude Code, complete and specific"),
        ("folder", "string", "Repo path, or a watched repo's name like 'DevBar'"),
    };
    public override Risk RiskOf(JsonElement args) => Risk.Destructive;
    public override string Describe(JsonElement args) => $"start Claude Code in {Path.GetFileName(Resolve(Str(args, "folder")).TrimEnd('\\'))} to {Str(args, "task")}";

    public override Task<string> RunAsync(JsonElement args)
    {
        var dir = Resolve(Str(args, "folder"));
        if (!Directory.Exists(dir)) return Task.FromResult($"Folder not found: {Str(args, "folder")}. Watched repos: {string.Join(", ", config.GitWatchedRepos.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))))}.");
        var task = Str(args, "task").Replace("\"", "'");
        if (task.Length == 0) return Task.FromResult("No task given.");

        // Windows Terminal if present, else a classic console. The session stays open for the user.
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir}\" claude \"{task}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d \"{dir}\" && claude \"{task}\"") { UseShellExecute = true });
        }
        return Task.FromResult($"Started Claude Code in {dir}.");
    }

    private string Resolve(string folder)
    {
        if (Directory.Exists(folder)) return folder;
        return config.GitWatchedRepos.FirstOrDefault(p =>
                   Path.GetFileName(p.TrimEnd('\\', '/')).Equals(folder, StringComparison.OrdinalIgnoreCase))
               ?? folder;
    }
}
