using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;

namespace DevBar.Modules.Jarvis;

/// <summary>
/// The user's window into Jarvis's long-term memory — everything it has been
/// told or has learned, each deletable. Learning you can't see or undo is
/// creepy; this makes it a feature.
/// </summary>
public partial class JarvisMemoryWindow : Window
{
    private readonly JarvisModule _module;
    private bool _confirmForgetAll;

    internal JarvisMemoryWindow(JarvisModule module)
    {
        _module = module;
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Render();
        _ = RenderLocationAsync();
    }

    private async Task RenderLocationAsync()
    {
        if (!_module.Settings.UseLocation)
        {
            LocationText.Text = "Location: off (Jarvis settings).";
            return;
        }
        LocationText.Text = "Location: looking…";
        var place = await LocationService.GetAsync(_module.Settings);
        LocationText.Text = place is null
            ? "Location: unknown — set a home location in Jarvis settings."
            : $"Location: {place.Describe()}  ·  from {place.Source}";
    }

    private void Render()
    {
        FactList.Children.Clear();
        List<Fact> facts;
        try { facts = MemoryStore.Facts(); }
        catch (Exception ex)
        {
            CountText.Text = "Memory unavailable: " + ex.Message;
            return;
        }

        CountText.Text = facts.Count switch
        {
            0 => "Nothing yet — talk to me, or add something below",
            1 => "1 thing",
            _ => $"{facts.Count} things",
        };

        foreach (var f in facts)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = f.Text, Style = (Style)FindResource("Body"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = $"{(f.Source == "told" ? "you told me" : "learned")} · {f.Updated:d MMM yyyy}",
                Style = (Style)FindResource("MonoMuted"),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
            });

            var del = new Button { Style = (Style)FindResource("DangerIconButton"), Content = "", VerticalAlignment = VerticalAlignment.Top };
            long id = f.Id;
            del.Click += (_, _) =>
            {
                MemoryStore.DeleteFact(id);
                Render();
            };
            Grid.SetColumn(del, 1);

            row.Children.Add(text);
            row.Children.Add(del);
            FactList.Children.Add(new Border
            {
                Background = (Brush)FindResource("BrushBgElevated"),
                BorderBrush = (Brush)FindResource("BrushHairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 6, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row,
            });
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var text = NewFactBox.Text.Trim();
        if (!ProfileLearner.IsKeepable(text)) return;
        MemoryStore.AddFact(text, "told");
        NewFactBox.Clear();
        Render();
    }

    private void NewFactBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
    }

    private void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmForgetAll)
        {
            _confirmForgetAll = true;
            ForgetAllText.Text = "Click again to confirm";
            return;
        }
        MemoryStore.ForgetEverything();
        _module.ForgetConversation();
        _confirmForgetAll = false;
        ForgetAllText.Text = "Forget everything";
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();
}
