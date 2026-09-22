using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevBar.Core;

/// <summary>
/// Demo-recording captions (--demo-director only): a subtitle block at the
/// bottom centre for the conversation ("Vivek: ..." / "Jarvis: ..."), a small
/// mono caption bottom-left for the script's on-screen text, and a big title
/// card for cold opens and end cards. Click-through
/// and never takes focus, so it can't disturb what's being recorded.
/// </summary>
internal sealed class CaptionOverlay : Window
{
    private static readonly FontFamily Mono = new("JetBrains Mono, Cascadia Mono, Consolas");
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x5E, 0xA1, 0xFF));
    private static readonly Brush JarvisTint = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

    private readonly Border _subtitle;
    private readonly TextBlock _speaker = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _line = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 1000 };
    private readonly Border _caption;
    private readonly TextBlock _captionText = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)), FontFamily = Mono, FontSize = 17 };
    private readonly DispatcherTimer _subtitleHide = new();
    private readonly Border _title;
    private readonly TextBlock _titleText = new() { Foreground = Brushes.White, FontSize = 54, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 1400 };

    /// <summary>Off for silent loops (the site's hero clip): conversation subtitles aren't drawn.</summary>
    public bool SubtitlesOn { get; set; } = true;

    public CaptionOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        var work = SystemParameters.WorkArea;
        Left = work.Left; Top = work.Top; Width = work.Width; Height = work.Height;

        _speaker.FontSize = _line.FontSize = 26;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_speaker);
        row.Children.Add(_line);
        _subtitle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0E, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 12, 22, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 70),
            Child = row,
            Visibility = Visibility.Collapsed,
        };
        _caption = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0E, 0x14)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(28, 0, 0, 24),
            Child = _captionText,
            Visibility = Visibility.Collapsed,
        };
        _title = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0E, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(40, 22, 40, 26),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 120, 0, 0),
            Child = _titleText,
            Visibility = Visibility.Collapsed,
        };
        var root = new Grid();
        root.Children.Add(_subtitle);
        root.Children.Add(_caption);
        root.Children.Add(_title);
        Content = root;

        _subtitleHide.Tick += (_, _) => { _subtitleHide.Stop(); _subtitle.Visibility = Visibility.Collapsed; };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>A conversation line; stays up <paramref name="holdSeconds"/> after the last update.</summary>
    public void Subtitle(string speaker, string text, double holdSeconds = 4)
    {
        if (!SubtitlesOn || string.IsNullOrWhiteSpace(text)) return;
        _speaker.Text = speaker;
        _speaker.Foreground = speaker == "Jarvis" ? JarvisTint : Accent;
        _line.Text = text.Trim();
        _subtitle.Visibility = Visibility.Visible;
        _subtitleHide.Stop();
        _subtitleHide.Interval = TimeSpan.FromSeconds(holdSeconds);
        _subtitleHide.Start();
    }

    /// <summary>The script's on-screen text; empty clears it.</summary>
    public void Caption(string text)
    {
        _captionText.Text = text;
        _caption.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Big centred title card (cold open / end card); empty clears it. "|" starts a new line.</summary>
    public void Title(string text)
    {
        _titleText.Text = text.Replace('|', '\n');
        _title.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
