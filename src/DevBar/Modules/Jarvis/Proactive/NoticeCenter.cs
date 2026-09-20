using System.Runtime.InteropServices;
using DevBar.Core;

namespace DevBar.Modules.Jarvis.Proactive;

/// <summary>
/// Everything Jarvis says unprompted goes through here, so the etiquette lives
/// in one place: it respects the user's mode (speak / show / off), quiet
/// hours, full-screen apps and presentations, never talks over a live
/// conversation (notices wait until it ends), and never repeats itself.
/// </summary>
internal sealed class NoticeCenter
{
    private readonly JarvisConfig _cfg;
    private readonly Func<string, bool, bool, Task> _deliver; // (text, speak, show the bar)
    private readonly Func<bool> _conversationActive;
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly Queue<string> _deferred = new();

    public NoticeCenter(JarvisConfig cfg, Func<string, bool, bool, Task> deliver, Func<bool> conversationActive)
    {
        _cfg = cfg;
        _deliver = deliver;
        _conversationActive = conversationActive;
    }

    public void Notify(string text)
    {
        if (_cfg.Proactive == "off") return;
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(text, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _recent[text] = now;

        if (_conversationActive())
        {
            if (!_deferred.Contains(text)) _deferred.Enqueue(text);
            return;
        }
        Deliver(text);
    }

    /// <summary>Call when a conversation ends: say what came up meanwhile.</summary>
    public void FlushDeferred()
    {
        if (_deferred.Count == 0) return;
        var all = string.Join(" ", _deferred);
        _deferred.Clear();
        Deliver(all);
    }

    private void Deliver(string text)
    {
        var state = UserState();
        JarvisSession.Trace($"notice ({state}, mode {_cfg.Proactive}): {text}");
        if (state is QUNS.RunningD3DFullScreen or QUNS.PresentationMode) return; // games and slides: stay out of it

        bool speak = _cfg.Proactive == "speak" && !InQuietHours() && state != QUNS.QuietTime;
        // Full-screen video/browser: a voice is fine, but don't drop the bar over the picture.
        bool show = state != QUNS.BusyFullscreen;
        if (!speak && !show) return;
        _ = _deliver(text, speak, show);
    }

    internal bool InQuietHours()
    {
        var parts = (_cfg.QuietHours ?? "").Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var from) || !TimeSpan.TryParse(parts[1], out var to)) return false;
        var t = DateTime.Now.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to; // spans midnight
    }

    // Windows' own "is the user presenting / in a full-screen game / in Focus quiet time" signal.
    private enum QUNS { NotPresent = 1, BusyFullscreen = 2, RunningD3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7 }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out QUNS state);

    private static QUNS UserState()
    {
        try { return SHQueryUserNotificationState(out var s) == 0 ? s : QUNS.AcceptsNotifications; }
        catch { return QUNS.AcceptsNotifications; }
    }
}
