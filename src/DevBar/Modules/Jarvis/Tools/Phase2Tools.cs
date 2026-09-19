using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Modules.Jarvis.Brain;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;

namespace DevBar.Modules.Jarvis.Tools;

// ---------------- memory ----------------

internal sealed class RememberTool : JarvisTool
{
    public override string Name => "remember";
    public override string Description => "Save a lasting fact about the user when they tell you something about themselves or ask you to remember it.";
    protected override (string, string, string)[] Params => new[] { ("fact", "string", "One short sentence, e.g. 'Vivek's main project is ClientPulse.'") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var fact = Str(args, "fact");
        if (!ProfileLearner.IsKeepable(fact)) return Task.FromResult("Not saved — that looks like a secret or is empty. Never store passwords or keys.");
        MemoryStore.AddFact(fact, "told");
        return Task.FromResult("Remembered.");
    }
}

internal sealed class RecallTool : JarvisTool
{
    public override string Name => "recall";
    public override string Description => "Search long-term memory about the user beyond what's already in your prompt.";
    protected override (string, string, string)[] Params => new[] { ("query", "string", "What to look for, e.g. 'sister' or 'projects'") };

    public override Task<string> RunAsync(JsonElement args)
    {
        var hits = MemoryStore.Search(Str(args, "query"));
        return Task.FromResult(hits.Count == 0 ? "Nothing in memory about that." : string.Join("\n", hits.Select(f => "- " + f.Text)));
    }
}

internal sealed class ForgetTool : JarvisTool
{
    public override string Name => "forget";
    public override string Description => "Delete remembered facts matching a topic when the user asks you to forget something.";
    protected override (string, string, string)[] Params => new[] { ("about", "string", "Topic or words of the fact to forget") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var hits = MemoryStore.Search(Str(args, "about"), 5);
        foreach (var f in hits) MemoryStore.DeleteFact(f.Id);
        return Task.FromResult(hits.Count == 0 ? "I had nothing stored about that." : $"Forgot: {string.Join("; ", hits.Select(f => f.Text))}");
    }
}

// ---------------- reminders ----------------

/// <summary>
/// Persistent reminders. Exactly one DispatcherTimer is armed, for the next
/// one due — no polling. Reminders that came due while the PC was off are
/// spoken shortly after DevBar starts (if under a day late).
/// </summary>
internal static class ReminderScheduler
{
    private static DispatcherTimer? _timer;
    private static Action<string>? _announce;

    public static void Start(Action<string> announce)
    {
        _announce = announce;
        Arm(startup: true);
    }

    public static void Arm(bool startup = false)
    {
        _timer?.Stop();
        List<Reminder> pending;
        try { pending = MemoryStore.PendingReminders(); }
        catch { return; }

        var now = DateTime.Now;
        var overdue = pending.Where(r => r.DueLocal <= now).ToList();
        foreach (var r in overdue)
        {
            MemoryStore.CompleteReminder(r.Id);
            if (now - r.DueLocal < TimeSpan.FromDays(1))
                _announce?.Invoke(startup ? $"While you were away — reminder: {r.Text}." : $"Reminder: {r.Text}.");
        }

        var next = pending.Where(r => r.DueLocal > now).MinBy(r => r.DueLocal);
        if (next is null) return;
        var wait = next.DueLocal - now;
        JarvisSession.Trace($"reminder armed: '{next.Text}' in {wait.TotalSeconds:0}s");
        _timer = new DispatcherTimer { Interval = wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait + TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Arm();
        _timer.Start();
    }
}

internal sealed class SetReminderTool : JarvisTool
{
    public override string Name => "set_reminder";
    public override string Description => "Set a timer or reminder; Jarvis speaks up when it's due, even after a restart. Give either minutes or an exact local time.";
    protected override (string, string, string)[] Params => new[]
    {
        ("text", "string", "What to remind about, e.g. 'push the branch' or 'timer'"),
        ("minutes?", "number", "From now, e.g. 20 or 0.5"),
        ("at?", "string", "Local date-time 'yyyy-MM-dd HH:mm' (24h), for 'at 5pm' or 'tomorrow morning'"),
    };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        DateTime due;
        var at = Str(args, "at");
        if (at.Length > 0 && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) due = parsed;
        else if (args.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number) due = DateTime.Now.AddMinutes(m.GetDouble());
        else if (double.TryParse(Str(args, "minutes"), CultureInfo.InvariantCulture, out var mm)) due = DateTime.Now.AddMinutes(mm);
        else return Task.FromResult("Need either minutes or an 'at' time.");

        if (due <= DateTime.Now) return Task.FromResult("That time has already passed.");
        var text = Str(args, "text", "timer");
        MemoryStore.AddReminder(due, text);
        ReminderScheduler.Arm();
        return Task.FromResult($"Reminder set for {due:ddd h:mm tt}: {text}.");
    }
}

internal sealed class ListRemindersTool : JarvisTool
{
    public override string Name => "list_reminders";
    public override string Description => "List pending reminders and timers.";

    public override Task<string> RunAsync(JsonElement args)
    {
        var list = MemoryStore.PendingReminders();
        return Task.FromResult(list.Count == 0 ? "No reminders pending."
            : string.Join("; ", list.Select(r => $"{r.DueLocal:ddd h:mm tt}: {r.Text}")));
    }
}

internal sealed class CancelReminderTool : JarvisTool
{
    public override string Name => "cancel_reminder";
    public override string Description => "Cancel pending reminders whose text matches.";
    protected override (string, string, string)[] Params => new[] { ("text", "string", "Words from the reminder, or 'all'") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var q = Str(args, "text");
        var hits = MemoryStore.PendingReminders()
            .Where(r => q.Equals("all", StringComparison.OrdinalIgnoreCase) || r.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in hits) MemoryStore.CompleteReminder(r.Id);
        ReminderScheduler.Arm();
        return Task.FromResult(hits.Count == 0 ? "No matching reminder." : $"Cancelled {hits.Count}: {string.Join("; ", hits.Select(r => r.Text))}.");
    }
}

// ---------------- surroundings ----------------

internal sealed class WeatherTool(JarvisConfig cfg) : JarvisTool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public override string Name => "weather";
    public override string Description => "Current weather and today's/tomorrow's forecast for the user's location or a named place.";
    protected override (string, string, string)[] Params => new[] { ("place?", "string", "City, e.g. 'Pune' or 'London, UK'; omit for where the user is") };

    public override async Task<string> RunAsync(JsonElement args)
    {
        var name = Str(args, "place");
        var place = name.Length > 0 ? await LocationService.GeocodeAsync(name) : await LocationService.GetAsync(cfg);
        if (place is null) return name.Length > 0 ? $"Couldn't find {name}." : "I don't know where you are — set Home location in Jarvis settings.";

        var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={place.Lat}&longitude={place.Lon}")
                  + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=2&timezone=auto";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var c = doc.RootElement.GetProperty("current");
        var d = doc.RootElement.GetProperty("daily");
        string Day(int i) =>
            $"{Code(d.GetProperty("weather_code")[i].GetInt32())}, {d.GetProperty("temperature_2m_min")[i].GetDouble():0}–{d.GetProperty("temperature_2m_max")[i].GetDouble():0}°C, " +
            $"{(d.GetProperty("precipitation_probability_max")[i].ValueKind == JsonValueKind.Number ? d.GetProperty("precipitation_probability_max")[i].GetInt32() : 0)}% chance of rain";

        return $"{place.Describe()}: now {c.GetProperty("temperature_2m").GetDouble():0}°C (feels {c.GetProperty("apparent_temperature").GetDouble():0}°C), " +
               $"{Code(c.GetProperty("weather_code").GetInt32())}, humidity {c.GetProperty("relative_humidity_2m").GetDouble():0}%, " +
               $"wind {c.GetProperty("wind_speed_10m").GetDouble():0} km/h. Today: {Day(0)}. Tomorrow: {Day(1)}.";
    }

    private static string Code(int wmo) => wmo switch
    {
        0 => "clear", 1 => "mostly clear", 2 => "partly cloudy", 3 => "overcast",
        45 or 48 => "foggy", 51 or 53 or 55 => "drizzle", 56 or 57 => "freezing drizzle",
        61 => "light rain", 63 => "rain", 65 => "heavy rain", 66 or 67 => "freezing rain",
        71 or 73 or 75 or 77 => "snow", 80 or 81 => "rain showers", 82 => "violent rain showers",
        85 or 86 => "snow showers", 95 => "thunderstorms", 96 or 99 => "thunderstorms with hail",
        _ => "mixed conditions",
    };
}

internal sealed class SystemStatusTool : JarvisTool
{
    public override string Name => "system_status";
    public override string Description => "This PC's battery, memory, disk space, network and uptime.";

    public override Task<string> RunAsync(JsonElement args)
    {
        var parts = new List<string>();

        if (GetSystemPowerStatus(out var power))
        {
            if (power.BatteryFlag == 128) parts.Add("no battery (desktop power)");
            else
            {
                var pct = power.BatteryLifePercent <= 100 ? $"{power.BatteryLifePercent}%" : "unknown";
                var mins = power.BatteryLifeTime > 0 && power.BatteryLifeTime < 24 * 3600 ? $", about {power.BatteryLifeTime / 60} minutes left" : "";
                parts.Add($"battery {pct}, {(power.ACLineStatus == 1 ? "plugged in" : "on battery")}{mins}");
            }
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
            parts.Add($"RAM {mem.dwMemoryLoad}% used ({(mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0:0.0} of {mem.ullTotalPhys / 1073741824.0:0} GB)");

        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            parts.Add($"{sys.Name.TrimEnd('\\')} drive {sys.AvailableFreeSpace / 1073741824.0:0} GB free of {sys.TotalSize / 1073741824.0:0} GB");
        }
        catch { }

        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? $"Wi-Fi ({n.Name})" : n.Name).Take(3).ToList();
        parts.Add(up.Count == 0 ? "no network connection" : "connected via " + string.Join(", ", up));

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        parts.Add($"up for {(uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} days " : "")}{uptime.Hours} hours {uptime.Minutes} minutes");
        return Task.FromResult(string.Join("; ", parts) + ".");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}

internal sealed class LookAtScreenTool(ProviderRouter vision) : JarvisTool
{
    public override string Name => "look_at_screen";
    public override string Description => "See what's in the user's current window (or whole screen) and answer a question about it — errors, code, a page. Only when the user refers to what's on screen.";
    protected override (string, string, string)[] Params => new[]
    {
        ("question", "string", "What to find out, e.g. 'What does the error say and where?'"),
        ("whole_screen?", "boolean", "true for the entire screen instead of the active window"),
    };

    public override Task<string> RunAsync(JsonElement args)
    {
        bool full = args.TryGetProperty("whole_screen", out var w) && w.ValueKind == JsonValueKind.True;
        return ScreenVision.AskAsync(vision, Str(args, "question", "Describe what's shown."), full, CancellationToken.None);
    }
}

// ---------------- acting on the desktop ----------------

/// <summary>Types text into whatever window has focus (DevBar never takes focus, so that's your app). Never presses Enter.</summary>
internal sealed class TypeTextTool : JarvisTool
{
    public override string Name => "type_text";
    public override string Description => "Type text into the user's focused window (dictation, e.g. 'type: ...' or 'write a commit message saying ...'). Does not press Enter.";
    protected override (string, string, string)[] Params => new[] { ("text", "string", "Exact text to type, cleaned up and punctuated") };
    public override Risk RiskOf(JsonElement args) => Risk.Reversible;

    public override Task<string> RunAsync(JsonElement args)
    {
        var text = Str(args, "text").Replace("\r\n", " ").Replace('\n', ' ');
        if (text.Length == 0) return Task.FromResult("Nothing to type.");

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            inputs.Add(Key(ch, 0));
            inputs.Add(Key(ch, KEYEVENTF_KEYUP));
        }
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return Task.FromResult(sent == inputs.Count ? $"Typed {text.Length} characters." : "Typing was blocked (the focused app may be running as administrator).");
    }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_KEYUP = 0x2;

    private static INPUT Key(char ch, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | flags } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}

/// <summary>
/// Runs a shell command — always spoken back and confirmed first, and a
/// short list of catastrophic patterns is refused outright.
/// </summary>
internal sealed partial class RunCommandTool(Config config) : JarvisTool
{
    public override string Name => "run_command";
    public override string Description => "Run a PowerShell command on this PC and return its output (git, npm, dotnet, docker, file listing…). The user confirms first.";
    protected override (string, string, string)[] Params => new[]
    {
        ("command", "string", "The PowerShell command"),
        ("folder?", "string", "Working folder; can be a watched repo name like 'DevBar'"),
    };
    public override Risk RiskOf(JsonElement args) => Risk.Destructive;

    public override string Describe(JsonElement args)
    {
        var folder = Str(args, "folder");
        return $"run {Str(args, "command")}{(folder.Length > 0 ? $" in {Path.GetFileName(ResolveFolder(folder).TrimEnd('\\'))}" : "")}";
    }

    public override async Task<string> RunAsync(JsonElement args)
    {
        var command = Str(args, "command");
        if (command.Length == 0) return "No command given.";
        if (Forbidden().IsMatch(command)) return "Refused: that command could destroy data or the system. Tell the user to run it themselves if they really mean it.";

        var dir = ResolveFolder(Str(args, "folder"));
        var r = await ShellOut.RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"", dir, TimeSpan.FromSeconds(60));
        if (!r.Started) return "Couldn't start PowerShell.";
        var output = (r.StdOut + (r.StdErr.Length > 0 ? "\nERRORS:\n" + r.StdErr : "")).Trim();
        if (output.Length > 1500) output = output[..1500] + "\n…(truncated)";
        return $"Exit code {r.ExitCode} in {dir}.\n{(output.Length == 0 ? "(no output)" : output)}";
    }

    private string ResolveFolder(string folder)
    {
        if (folder.Length == 0) return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(folder)) return folder;
        var repo = config.GitWatchedRepos.FirstOrDefault(p =>
            Path.GetFileName(p.TrimEnd('\\', '/')).Equals(folder, StringComparison.OrdinalIgnoreCase));
        return repo ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    [GeneratedRegex(@"\b(format(-volume)?\s+[a-z]:|diskpart|cipher\s+/w|bcdedit|vssadmin\s+delete|shutdown|Stop-Computer|Restart-Computer|reg\s+delete\s+HKLM|Remove-Item\s+.*-Recurse.*\s[a-z]:\\?\s*$|rm\s+-rf\s+/|del\s+/[sq].*[a-z]:\\\s*$|Clear-Disk|Initialize-Disk)", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
