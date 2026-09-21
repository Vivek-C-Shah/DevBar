using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DevBar.Modules.Jarvis;

public partial class JarvisCard : UserControl
{
    private readonly JarvisModule _module;
    private bool _visible;

    internal JarvisCard(JarvisModule module)
    {
        _module = module;
        InitializeComponent();

        _module.StateChanged += OnState;
        _module.Heard += t => HeardText.Text = t;
        _module.Reply += t =>
        {
            ReplyText.Text = t;
            ReplyScroll.ScrollToEnd();
        };
        _module.ToolActivity += OnTool;
        _module.Error += ShowProblem;
        _module.Level += OnLevel;
        _module.SessionStarted += () =>
        {
            HeardText.Text = "";
            ReplyText.Text = "";
            Chips.Children.Clear();
            ProblemText.Visibility = Visibility.Collapsed;
        };
        _module.SettingsChanged += RenderIdle;
        _module.WakeWordChanged += RenderWake;

        RenderIdle();
        OnState(JarvisState.Idle);
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) StopBreathing();
        else OnState(_module.State);
        if (visible && _module.State == JarvisState.Idle) RenderIdle();
    }

    private void RenderWake()
    {
        var status = _module.WakeWordStatus;
        WakeText.Text = status == "off" ? "" : $"hey jarvis · {status}";
        WakeText.Foreground = (Brush)FindResource(status == "listening" ? "BrushGood" : "BrushWarn");
    }

    private void RenderIdle()
    {
        HotkeyText.Text = _module.Settings.Hotkey;
        RenderWake();
        if (_module.State != JarvisState.Idle) return;

        var missing = _module.MissingSetup();
        if (missing.Count > 0)
            ShowProblem("To get going I need " + string.Join(", ", missing) + " - tap the gear.");
        else if (ProblemText.Text.StartsWith("To get going"))
            ProblemText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(ReplyText.Text))
            ReplyText.Text = $"Press {_module.Settings.Hotkey} and talk. Press it again to interrupt me or stop.";
    }

    private void OnState(JarvisState state)
    {
        StatusText.Text = state switch
        {
            JarvisState.Connecting => "Connecting",
            JarvisState.Listening => "Listening",
            JarvisState.Thinking => "Thinking",
            JarvisState.Speaking => "Speaking",
            JarvisState.Confirming => "Waiting for yes / no",
            _ => "Ready",
        };

        bool live = state != JarvisState.Idle;
        StatusText.Foreground = (Brush)FindResource(state switch
        {
            JarvisState.Listening or JarvisState.Confirming => "BrushGood",
            JarvisState.Idle => "BrushMuted",
            _ => "BrushAccent",
        });
        Ring.Opacity = live ? 0.9 : 0.6;
        Core.Opacity = live ? 0.45 : 0.25;
        MicGlyph.Text = state == JarvisState.Speaking ? "" : ""; // Volume : Microphone
        ConfirmPanel.Visibility = state == JarvisState.Confirming ? Visibility.Visible : Visibility.Collapsed;

        if (state == JarvisState.Thinking && _visible) StartBreathing();
        else StopBreathing();

        if (!live) OnLevel(0);
    }

    private void OnLevel(float level)
    {
        double target = 0.8 + Math.Min(1, level) * 0.32;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(90));
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        Halo.Opacity = 0.35 + Math.Min(1, level) * 0.5;
    }

    private void StartBreathing()
    {
        var breathe = new DoubleAnimation(0.9, 1.15, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    private void StopBreathing()
    {
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void OnTool(string label, bool? ok)
    {
        // Update the chip in place if this tool already has one (pending → done).
        foreach (Border existing in Chips.Children)
        {
            if ((string)existing.Tag != label) continue;
            ((TextBlock)existing.Child).Text = ChipText(label, ok);
            ((TextBlock)existing.Child).Foreground = ChipBrush(ok);
            return;
        }

        Chips.Children.Add(new Border
        {
            Tag = label,
            Background = (Brush)FindResource("BrushBgElevated"),
            BorderBrush = (Brush)FindResource("BrushHairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 5, 4),
            Child = new TextBlock
            {
                Text = ChipText(label, ok),
                Style = (Style)FindResource("Mono"),
                FontSize = 10.5,
                Foreground = ChipBrush(ok),
            },
        });
    }

    private static string ChipText(string label, bool? ok) =>
        (ok switch { true => "✓ ", false => "✕ ", null => "… " }) + label;

    private Brush ChipBrush(bool? ok) =>
        (Brush)FindResource(ok switch { true => "BrushText", false => "BrushDanger", null => "BrushWarn" });

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemText.Visibility = Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _module.OpenSettings();
    private void Memory_Click(object sender, RoutedEventArgs e) => _module.OpenMemory();

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        _module.ForgetConversation();
        HeardText.Text = "";
        ReplyText.Text = "";
        Chips.Children.Clear();
        RenderIdle();
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => _module.ConfirmFromUi(true);
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => _module.ConfirmFromUi(false);
}
