using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using DevBar.Sdk;
using static DevBar.Core.NativeMethods;

namespace DevBar.Modules.Ports;

/// <summary>
/// Bound TCP listener ports via GetExtendedTcpTable (the managed equivalent of
/// `netstat -ano`, no process shell-out). Only polls while expanded, and the
/// scan itself runs off the UI thread - on a dev box with a lot of listeners
/// and processes, walking the table + resolving every owning PID can take long
/// enough to visibly stall the UI if done inline, which is exactly what this
/// tool is not allowed to do.
/// </summary>
public sealed class PortsModule : IDevBarModule
{
    public string Id => "ports";
    public string DisplayName => "Ports";
    public string IconGlyph => "";

    public ObservableCollection<PortInfo> Ports { get; } = new();

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public UserControl BuildCard() => new PortsCard(this);

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

    public bool TryKill(PortInfo info)
    {
        try
        {
            using var p = Process.GetProcessById(info.Pid);
            p.Kill();
            return true;
        }
        catch { return false; }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var found = await Task.Run(() => ReadListeners().OrderBy(p => p.Port).ToList());

            for (int i = Ports.Count - 1; i >= 0; i--)
                if (!found.Any(f => f.Port == Ports[i].Port && f.Pid == Ports[i].Pid))
                    Ports.RemoveAt(i);

            foreach (var f in found)
                if (!Ports.Any(p => p.Port == f.Port && p.Pid == f.Pid))
                    Ports.Add(f);
        }
        finally { _refreshing = false; }
    }

    internal static List<PortInfo> ReadListeners()
    {
        var results = new List<PortInfo>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size == 0) return results;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != 0) return results;

            int rowCount = Marshal.ReadInt32(buf);
            IntPtr rowPtr = IntPtr.Add(buf, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                int port = NetworkPort(row.localPort);
                string name = "?";
                try { using var p = Process.GetProcessById((int)row.owningPid); name = p.ProcessName; }
                catch { /* process exited between snapshot and lookup */ }
                results.Add(new PortInfo { Port = port, Pid = (int)row.owningPid, ProcessName = name });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return results;
    }
}
