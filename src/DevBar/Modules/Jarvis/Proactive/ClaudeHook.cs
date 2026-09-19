using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Proactive;

/// <summary>
/// Optional Claude Code integration: a tiny hook script Claude Code runs on
/// prompt / needs-you / done / exit, which writes the session's state into
/// DevBar's claude-sessions folder. Installing merges four hook entries into
/// ~/.claude/settings.json (after backing it up); removing takes out only
/// ours. Nothing is installed unless the user clicks "Connect" in settings.
/// </summary>
internal static class ClaudeHook
{
    private const string Marker = "devbar-claude-status";
    private static readonly string[] Events = { "UserPromptSubmit", "Notification", "Stop", "SessionEnd" };

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    private static string HookDir => Path.Combine(Config.Dir, "hooks");
    private static string NodeScript => Path.Combine(HookDir, Marker + ".js");
    private static string PsScript => Path.Combine(HookDir, Marker + ".ps1");

    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker); }
            catch { return false; }
        }
    }

    /// <summary>Writes both hook scripts; returns (node script, powershell script) paths.</summary>
    internal static async Task<(string Node, string Ps)> WriteScriptsAsync()
    {
        Directory.CreateDirectory(HookDir);
        await File.WriteAllTextAsync(NodeScript, NodeSource);
        await File.WriteAllTextAsync(PsScript, PsSource);
        return (NodeScript, PsScript);
    }

    public static async Task<string> InstallAsync()
    {
        await WriteScriptsAsync();

        // Node starts in ~40ms; Windows PowerShell takes ~300ms per hook call, so prefer Node when present.
        var node = await ShellOut.RunAsync("where", "node");
        string command = node.Started && node.ExitCode == 0
            ? $"node \"{NodeScript}\""
            : $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{PsScript}\"";

        var root = LoadSettings(out bool existed);
        if (existed) File.Copy(SettingsPath, SettingsPath + ".devbar-backup", overwrite: true);

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var ev in Events)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            RemoveOurs(list);
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = command } },
            });
        }
        Save(root);
        return node.Started && node.ExitCode == 0 ? "Connected (Node hook)." : "Connected (PowerShell hook — install Node for snappier hooks).";
    }

    public static void Uninstall()
    {
        var root = LoadSettings(out bool existed);
        if (!existed || root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in Events)
        {
            if (hooks[ev] is not JsonArray list) continue;
            RemoveOurs(list);
            if (list.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
        Save(root);
    }

    private static void RemoveOurs(JsonArray list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i]?.ToJsonString().Contains(Marker) == true) list.RemoveAt(i);
    }

    private static JsonObject LoadSettings(out bool existed)
    {
        existed = File.Exists(SettingsPath);
        if (!existed) return new JsonObject();
        var node = JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? throw new InvalidOperationException("~/.claude/settings.json isn't a JSON object — not touching it.");
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // Both scripts: read the hook JSON from stdin, map the event to a state, write/delete
    // claude-sessions/<session>.json. They must never fail loudly — a hook error would
    // show up in the user's Claude Code session.
    private const string NodeSource = """
        // DevBar: reports Claude Code session state to %LOCALAPPDATA%\DevBar\claude-sessions (devbar-claude-status)
        const fs = require('fs'), path = require('path'), os = require('os');
        let input = '';
        process.stdin.on('data', d => input += d);
        process.stdin.on('end', () => {
          try {
            const e = JSON.parse(input || '{}');
            const ev = e.hook_event_name || '';
            const dir = path.join(process.env.LOCALAPPDATA || os.homedir(), 'DevBar', 'claude-sessions');
            fs.mkdirSync(dir, { recursive: true });
            const id = String(e.session_id || 'unknown').replace(/[^a-zA-Z0-9-]/g, '');
            const file = path.join(dir, id + '.json');
            if (ev === 'SessionEnd') { try { fs.unlinkSync(file); } catch {} return; }
            // "waiting for your input" is just the idle nag after a finished turn — not news.
            if (ev === 'Notification' && /waiting for your input/i.test(e.message || '')) return;
            const state = ev === 'Notification' ? 'waiting' : ev === 'Stop' ? 'idle' : 'working';
            let pid = 0; for (const ch of id) pid = (pid * 31 + ch.charCodeAt(0)) % 2000000000;
            fs.writeFileSync(file, JSON.stringify({ project: path.basename(e.cwd || process.cwd()), pid, state, updated: new Date().toISOString() }));
          } catch {}
        });
        """;

    private const string PsSource = """
        # DevBar: reports Claude Code session state to %LOCALAPPDATA%\DevBar\claude-sessions (devbar-claude-status)
        try {
          $e = [Console]::In.ReadToEnd() | ConvertFrom-Json
          $ev = "$($e.hook_event_name)"
          $dir = Join-Path $env:LOCALAPPDATA 'DevBar\claude-sessions'
          New-Item -ItemType Directory -Force $dir | Out-Null
          $id = ("$($e.session_id)" -replace '[^a-zA-Z0-9-]', ''); if (-not $id) { $id = 'unknown' }
          $file = Join-Path $dir "$id.json"
          if ($ev -eq 'SessionEnd') { Remove-Item $file -ErrorAction SilentlyContinue; exit 0 }
          if ($ev -eq 'Notification' -and "$($e.message)" -match 'waiting for your input') { exit 0 }
          $state = switch ($ev) { 'Notification' { 'waiting' } 'Stop' { 'idle' } default { 'working' } }
          $pid2 = 0; foreach ($ch in $id.ToCharArray()) { $pid2 = ($pid2 * 31 + [int]$ch) % 2000000000 }
          $project = if ($e.cwd) { Split-Path $e.cwd -Leaf } else { 'session' }
          @{ project = $project; pid = $pid2; state = $state; updated = (Get-Date).ToString('o') } | ConvertTo-Json -Compress | Set-Content -Path $file -Encoding utf8
        } catch { }
        exit 0
        """;
}
