using System.Windows;
using System.Windows.Media;
using DevBar.Core;

namespace DevBar;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private BarWindow? _bar;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\DevBar_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            // A module blowing up must never take the bar down.
            System.Diagnostics.Debug.WriteLine(ex.Exception);
            ex.Handled = true;
        };

        ApplyAccent();

        var config = Config.Load();
        var args = new StartupArgs(e.Args);
        _bar = new BarWindow(config, args);
        _bar.Show();
    }

    /// <summary>
    /// Pull the user's Windows accent color and lighten it until it clears
    /// 3:1 contrast against the bar background, per the style lock.
    /// </summary>
    private void ApplyAccent()
    {
        Color accent;
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            accent = Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            accent = Color.FromRgb(0x5E, 0xA1, 0xFF);
        }

        var bg = Color.FromRgb(0x0B, 0x0E, 0x14);
        for (int i = 0; i < 12 && Contrast(accent, bg) < 3.0; i++)
            accent = Lerp(accent, Colors.White, 0.12);

        Resources["BrushAccent"] = new SolidColorBrush(accent);
        Resources["BrushAccentDim"] = new SolidColorBrush(accent) { Opacity = 0.35 };
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static double Contrast(Color a, Color b)
    {
        double La = Luminance(a), Lb = Luminance(b);
        return (Math.Max(La, Lb) + 0.05) / (Math.Min(La, Lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Chan(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Debug/demo command line flags. Documented in CONTRIBUTING notes of the README.</summary>
public sealed class StartupArgs
{
    /// <summary>--demo [moduleId]: start pinned open (optionally on a module) — used for screenshots.</summary>
    public bool Demo { get; }
    public string? DemoModuleId { get; }
    /// <summary>--shelf-seed "path;path": pre-populate the shelf (debug only; shelf is normally drag-in).</summary>
    public IReadOnlyList<string> ShelfSeed { get; }

    public StartupArgs(string[] args)
    {
        var seed = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    Demo = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        DemoModuleId = args[++i];
                    break;
                case "--shelf-seed" when i + 1 < args.Length:
                    seed.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
            }
        }
        ShelfSeed = seed;
    }
}
