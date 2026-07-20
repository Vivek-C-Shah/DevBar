using System.Windows;
using System.Windows.Controls;

namespace DevBar.Modules.Ports;

public partial class PortsCard : UserControl
{
    private readonly PortsModule _module;

    public PortsCard(PortsModule module)
    {
        _module = module;
        InitializeComponent();
        List.ItemsSource = _module.Ports;
        _module.Ports.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        bool empty = _module.Ports.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HeaderRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PortInfo info }) _module.TryKill(info);
    }
}
