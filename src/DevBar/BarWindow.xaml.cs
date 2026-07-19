using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevBar.Core;
using DevBar.Sdk;
using static DevBar.Core.NativeMethods;

namespace DevBar;

public partial class BarWindow : Window
{
    private readonly Config _config;
    private readonly StartupArgs _args;
    private readonly List<IDevBarModule> _modules;
    private readonly Dictionary<string, UserControl> _cardCache = new();

    private TrayIcon? _tray;
    private HwndSource? _source;

    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _hideTimer;
    private bool _expanded;
    private bool _pinned;
    private int _current;

    // A small floating toolbar, not a taskbar-style edge strip: the window is
    // always sized to exactly its visible content, idle or expanded, so there's
    // never a dead invisible zone eating clicks meant for whatever's underneath.
    private const double IdleWidth = 124;
    private const double IdleHeight = 22;
    private const double ExpandedWidth = 640;
    private const double ExpandedHeight = 220;
    private const int ShowDelayMs = 120;

    // Real mouse/touch hardware (and, empirically, some combination of Windows
    // tooltip/topmost-window plumbing) can produce a spurious one-frame
    // MouseLeave even while the cursor is still sitting still over the bar.
    // Debouncing the actual collapse means a leave has to persist briefly
    // before it's trusted — cheap insurance against the bar vanishing under
    // a developer's cursor while they're still looking at it.
    private const int HideDebounceMs = 220;

    public BarWindow(Config config, StartupArgs args)
    {
        _config = config;
        _args = args;
        InitializeComponent();

        _modules = ModuleHost.Build(config, args);

        _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShowDelayMs) };
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); Expand(); };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDebounceMs) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!_pinned && !Panel.IsMouseOver) Collapse(); };

        // The Window itself is fixed at the max size and centered, always —
        // only the inner Panel border resizes. See WM_NCHITTEST in WndProc for
        // why the dead margin around a small idle Panel doesn't eat clicks.
        Top = 0;
        Width = ExpandedWidth;
        Height = ExpandedHeight;
        Left = (SystemParameters.PrimaryScreenWidth - ExpandedWidth) / 2;
        Panel.Width = IdleWidth;
        Panel.Height = IdleHeight;

        BuildDots();
        SourceInitialized += BarWindow_SourceInitialized;
        Closed += (_, _) => Teardown();
    }

    private void BarWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source!.AddHook(WndProc);

        // Tool window + no-activate: never appears alt-tab, never steals focus on hover.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        ClipboardMonitor.Attach(hwnd);
        ClipboardMonitor.TextCopied += OnClipboardText;

        _tray = new TrayIcon(hwnd) { Menu = BuildTrayMenu() };
        _tray.LeftClick += () => { if (!_expanded) Expand(); };

        RestoreLastModule();

        if (_args.Demo)
        {
            if (_args.DemoModuleId != null)
            {
                int idx = _modules.FindIndex(m => m.Id == _args.DemoModuleId);
                if (idx >= 0) _current = idx;
            }
            _pinned = true;
            Expand();
        }
        else if (_config.StartPinned)
        {
            _pinned = true;
            Expand();
        }
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY) { _tray?.HandleMessage(lParam); handled = true; }
        else if (msg == WM_CLIPBOARDUPDATE) { ClipboardMonitor.HandleMessage(msg); }
        else if (msg == WM_NCHITTEST)
        {
            handled = true;
            return HitTestPanel(lParam);
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            Dispatcher.BeginInvoke(() => Left = (SystemParameters.PrimaryScreenWidth - ExpandedWidth) / 2);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The Window is always fixed at the max (expanded) footprint, but the
    /// visible Panel inside it is usually much smaller (the idle pill). Without
    /// this, the whole fixed-size window would eat every click/hover in that
    /// empty margin — exactly the "gets in the developer's way" failure this
    /// tool exists to avoid. Points outside the Panel's current bounds are
    /// reported transparent so they fall through to whatever's underneath.
    /// Assumes 100% display scaling (physical pixels == DIPs); see README.
    /// </summary>
    private IntPtr HitTestPanel(IntPtr lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        double localX = x - Left;
        double localY = y - Top;

        double panelLeft = (ExpandedWidth - Panel.Width) / 2;
        bool inside = localX >= panelLeft && localX <= panelLeft + Panel.Width
                      && localY >= 0 && localY <= Panel.Height;

        return (IntPtr)(inside ? HTCLIENT : HTTRANSPARENT);
    }

    // ---------------- sizing (only the inner Panel resizes; Window stays fixed) ----------------

    private void SetSize(double width, double height, bool animate)
    {
        if (!animate)
        {
            Panel.Width = width;
            Panel.Height = height;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(width > Panel.Width ? 180 : 100);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Panel.BeginAnimation(WidthProperty, new DoubleAnimation(Panel.Width, width, duration) { EasingFunction = ease });
        Panel.BeginAnimation(HeightProperty, new DoubleAnimation(Panel.Height, height, duration) { EasingFunction = ease });
    }

    // ---------------- hover / expand ----------------

    private void Panel_MouseEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        if (_expanded) return;
        _showTimer.Stop();
        _showTimer.Start();
    }

    private void Panel_MouseLeave(object sender, MouseEventArgs e)
    {
        _showTimer.Stop();
        if (_pinned) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Panel_DragEnter(object sender, DragEventArgs e)
    {
        // Dragging a file over the idle pill should reveal the bar immediately —
        // that's how you get something onto the Shelf.
        if (!_expanded) { _showTimer.Stop(); Expand(); }
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;

        SetSize(ExpandedWidth, ExpandedHeight, animate: true);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        { BeginTime = TimeSpan.FromMilliseconds(70) };
        PanelContent.BeginAnimation(OpacityProperty, fade);
        AccentRail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(70) });
        IdleMark.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90)));

        ShowCurrentModule();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;

        SetSize(IdleWidth, IdleHeight, animate: true);

        PanelContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(80)));
        AccentRail.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(80)));
        IdleMark.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(50) });

        _current = Math.Clamp(_current, 0, Math.Max(0, _modules.Count - 1));
        if (_current < _modules.Count)
            _modules[_current].OnCollapsed();

        if (_config.RememberLastModule && _current < _modules.Count)
        {
            _config.LastModuleId = _modules[_current].Id;
            _config.Save();
        }
    }

    private void RestoreLastModule()
    {
        if (!_config.RememberLastModule || _config.LastModuleId is null) return;
        int idx = _modules.FindIndex(m => m.Id == _config.LastModuleId);
        if (idx >= 0) _current = idx;
    }

    // ---------------- carousel ----------------

    private void BuildDots()
    {
        DotsPanel.Children.Clear();
        for (int i = 0; i < _modules.Count; i++)
        {
            var dot = new Border
            {
                Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5),
                Margin = new Thickness(3, 0, 3, 0),
                Background = i == _current
                    ? (Brush)FindResource("BrushAccent")
                    : (Brush)FindResource("BrushHairline"),
            };
            DotsPanel.Children.Add(dot);
        }
    }

    private void ShowCurrentModule()
    {
        if (_modules.Count == 0)
        {
            ModName.Text = "No modules";
            ModGlyph.Text = "";
            return;
        }

        _current = Math.Clamp(_current, 0, _modules.Count - 1);
        var mod = _modules[_current];

        ModName.Text = mod.DisplayName;
        ModGlyph.Text = mod.IconGlyph;
        BuildDots();

        Track.Children.Clear();
        var card = GetOrBuildCard(mod);
        card.Width = Viewport.ActualWidth > 0 ? Viewport.ActualWidth : ExpandedWidth - 27;
        Track.Children.Add(card);
        TrackTx.X = 0;

        mod.OnExpanded();
    }

    private UserControl GetOrBuildCard(IDevBarModule mod)
    {
        if (_cardCache.TryGetValue(mod.Id, out var cached)) return cached;
        var built = mod.BuildCard();
        _cardCache[mod.Id] = built;
        return built;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (UserControl child in Track.Children)
            child.Width = Viewport.ActualWidth;
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e) => Page(-1);
    private void NextBtn_Click(object sender, RoutedEventArgs e) => Page(1);

    private void Page(int dir)
    {
        if (_modules.Count == 0) return;
        _modules[_current].OnCollapsed();
        _current = (_current + dir + _modules.Count) % _modules.Count;
        ShowCurrentModule();
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinBtn.Foreground = _pinned
            ? (Brush)FindResource("BrushAccent")
            : (Brush)FindResource("BrushMuted");
        if (!_pinned && !Panel.IsMouseOver) Collapse();
    }

    // ---------------- tray ----------------

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { Style = null };

        var pin = new MenuItem { Header = "Pin bar open" };
        pin.Click += (_, _) => PinBtn_Click(this, new RoutedEventArgs());
        menu.Items.Add(pin);

        menu.Items.Add(new Separator());

        var openConfig = new MenuItem { Header = "Open config folder" };
        openConfig.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Config.Dir);
            System.Diagnostics.Process.Start("explorer.exe", Config.Dir);
        };
        menu.Items.Add(openConfig);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit DevBar" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    // ---------------- module wiring ----------------

    private void OnClipboardText(string text)
    {
        var clip = _modules.OfType<Modules.ClipboardHistory.ClipboardModule>().FirstOrDefault();
        clip?.AddEntry(text);
    }

    private void Teardown()
    {
        _showTimer.Stop();
        ClipboardMonitor.TextCopied -= OnClipboardText;
        ClipboardMonitor.Detach();
        _tray?.Dispose();
        _source?.RemoveHook(WndProc);
        foreach (var m in _modules) m.OnCollapsed();
    }
}
