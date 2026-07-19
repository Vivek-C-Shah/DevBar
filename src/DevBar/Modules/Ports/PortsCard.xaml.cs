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
        => EmptyHint.Visibility = _module.Ports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PortInfo info }) _module.TryKill(info);
    }
}
