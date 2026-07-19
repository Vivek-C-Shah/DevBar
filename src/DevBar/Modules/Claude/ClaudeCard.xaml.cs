using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static DevBar.Core.NativeMethods;

namespace DevBar.Modules.Claude;

public partial class ClaudeCard : UserControl
{
    private readonly ClaudeModule _module;

    public ClaudeCard(ClaudeModule module)
    {
        _module = module;
        InitializeComponent();
        List.ItemsSource = _module.Sessions;
        _module.Sessions.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
        => EmptyHint.Visibility = _module.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClaudeSession session }) return;
        try
        {
            var proc = Process.GetProcessById(session.ProcessId);
            var hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
        catch { /* process may have exited between refresh and click */ }
    }
}
