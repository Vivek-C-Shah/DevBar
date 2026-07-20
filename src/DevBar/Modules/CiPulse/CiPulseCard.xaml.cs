using System.Windows;
using System.Windows.Controls;

namespace DevBar.Modules.CiPulse;

public partial class CiPulseCard : UserControl
{
    private readonly CiPulseModule _module;

    public CiPulseCard(CiPulseModule module)
    {
        _module = module;
        InitializeComponent();
        List.ItemsSource = _module.Targets;
        _module.Targets.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = _module.Targets.Count == 0;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }
}
