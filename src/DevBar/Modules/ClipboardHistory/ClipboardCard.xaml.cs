using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevBar.Core;

namespace DevBar.Modules.ClipboardHistory;

public partial class ClipboardCard : UserControl
{
    private readonly ClipboardModule _module;

    public ClipboardCard(ClipboardModule module)
    {
        _module = module;
        InitializeComponent();
        _module.Entries.CollectionChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        Chips.Children.Clear();
        EmptyHint.Visibility = _module.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in _module.Entries)
            Chips.Children.Add(BuildChip(entry, _module.RemoveEntry));
    }

    private static Border BuildChip(ClipboardEntry entry, Action<ClipboardEntry> onRemove)
    {
        var border = new Border
        {
            Width = 150,
            Height = 150,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10),
            Background = (Brush)Application.Current.Resources["BrushBgElevated"],
            BorderBrush = (Brush)Application.Current.Resources["BrushHairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            Tag = entry,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var kind = new TextBlock { Text = entry.Kind.ToString(), Style = (Style)Application.Current.Resources["Overline"] };
        Grid.SetRow(kind, 0);

        var remove = new Button
        {
            Content = "",
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 20,
            Height = 18,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(remove, 0);
        remove.Click += (_, e) =>
        {
            e.Handled = true; // don't let the click bubble up and re-copy the entry
            onRemove(entry);
        };

        var text = new TextBlock
        {
            Text = entry.Text,
            Style = (Style)Application.Current.Resources["Mono"],
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(text, 1);

        var ago = new TextBlock
        {
            Text = Fmt.Ago(entry.CopiedAt),
            Style = (Style)Application.Current.Resources["BodyMuted"],
            FontSize = 9.5,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(ago, 2);

        grid.Children.Add(kind);
        grid.Children.Add(remove);
        grid.Children.Add(text);
        grid.Children.Add(ago);
        border.Child = grid;

        border.MouseLeftButtonUp += (_, _) =>
        {
            ClipboardMonitor.SuppressNext = true;
            Clipboard.SetText(entry.Text);
        };

        return border;
    }
}
