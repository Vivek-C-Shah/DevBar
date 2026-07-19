using System.Windows;
using static DevBar.Core.NativeMethods;

namespace DevBar.Core;

/// <summary>
/// Event-driven clipboard listener (AddClipboardFormatListener). Zero polling:
/// Windows pushes WM_CLIPBOARDUPDATE into the bar's WndProc, we read once.
/// </summary>
internal static class ClipboardMonitor
{
    public static event Action<string>? TextCopied;

    /// <summary>Set before programmatic copies (re-copy from history) to avoid feedback loops.</summary>
    public static bool SuppressNext { get; set; }

    private static IntPtr _hwnd;

    public static void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        AddClipboardFormatListener(hwnd);
    }

    public static void Detach()
    {
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    public static void HandleMessage(int msg)
    {
        if (msg != WM_CLIPBOARDUPDATE) return;
        if (SuppressNext) { SuppressNext = false; return; }

        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
                TextCopied?.Invoke(text);
        }
        catch
        {
            // clipboard is contended by design; losing one update is fine
        }
    }
}
