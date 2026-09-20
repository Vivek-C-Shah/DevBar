using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DevBar.Core;

/// <summary>
/// A system-wide shortcut via RegisterHotKey. Windows delivers WM_HOTKEY to
/// our window only when the chord is pressed — no keyboard hook, no polling,
/// zero idle cost. Text form is "Ctrl+Alt+Space" / "Win+Shift+J" / "F9".
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    public const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    public int Id { get; }
    public bool IsRegistered { get; private set; }

    public GlobalHotkey(IntPtr hwnd, int id)
    {
        _hwnd = hwnd;
        Id = id;
    }

    /// <summary>Replaces any previous binding. False if unparseable or another app owns the chord.</summary>
    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out uint mods, out uint vk)) return false;
        IsRegistered = RegisterHotKey(_hwnd, Id, mods | MOD_NOREPEAT, vk);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (!IsRegistered) return;
        UnregisterHotKey(_hwnd, Id);
        IsRegistered = false;
    }

    public void Dispose() => Unregister();

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (name.Equals("Esc", StringComparison.OrdinalIgnoreCase)) name = "Escape";
                    if (!Enum.TryParse<Key>(name, ignoreCase: true, out var k)) return false;
                    key = k;
                    break;
            }
        }
        if (key is null) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    /// <summary>Formats a captured key press for display and config; null while only modifiers are down.</summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }
}
