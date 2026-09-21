using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using static DevBar.Core.NativeMethods;

namespace DevBar.Core;

/// <summary>
/// Hand-rolled Shell_NotifyIcon tray icon - avoids pulling the whole WinForms
/// stack into memory for one icon. Icon comes from the exe's own embedded .ico.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;

    public event Action? LeftClick;
    public ContextMenu? Menu { get; set; }

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;

        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null)
        {
            var small = new IntPtr[1];
            ExtractIconEx(exe, 0, null, small, 1);
            _hIcon = small[0];
        }

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _hIcon;
        data.szTip = "DevBar";
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    public void HandleMessage(IntPtr lParam)
    {
        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                break;
            case WM_RBUTTONUP:
                if (Menu is null) break;
                // NOACTIVATE window needs an explicit foreground poke or the
                // menu won't dismiss when the user clicks elsewhere.
                SetForegroundWindow(_hwnd);
                Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                Menu.IsOpen = true;
                break;
        }
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }
}
