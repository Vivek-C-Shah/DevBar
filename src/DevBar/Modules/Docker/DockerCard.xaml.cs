using System.Windows;
using System.Windows.Controls;

namespace DevBar.Modules.Docker;

public partial class DockerCard : UserControl
{
    private readonly DockerModule _module;

    public DockerCard(DockerModule module)
    {
        _module = module;
        InitializeComponent();
        List.ItemsSource = _module.Containers;
        _module.Containers.CollectionChanged += (_, _) => Render();
        _module.StateChanged += Render;
        Render();
    }

    private void Render()
    {
        Dispatcher.Invoke(() =>
        {
            bool showList = _module.State == DockerState.Ok && _module.Containers.Count > 0;
            List.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
            if (showList) return;

            switch (_module.State)
            {
                case DockerState.NotFound:
                    EmptyGlyph.Text = "";
                    EmptyTitle.Text = "Docker not found";
                    EmptySubtitle.Text = "install Docker Desktop to use this module";
                    break;
                case DockerState.NotRunning:
                    EmptyGlyph.Text = "";
                    EmptyTitle.Text = "Docker isn't running";
                    EmptySubtitle.Text = "start Docker Desktop";
                    break;
                default:
                    EmptyGlyph.Text = "";
                    EmptyTitle.Text = "No containers";
                    EmptySubtitle.Text = "nothing to show, running or stopped";
                    break;
            }
        });
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ContainerInfo info })
            await _module.ToggleAsync(info);
    }
}
