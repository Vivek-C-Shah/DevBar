using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevBar.Modules.GitStatus;

public partial class GitStatusCard : UserControl
{
    private readonly GitStatusModule _module;

    public GitStatusCard(GitStatusModule module)
    {
        _module = module;
        InitializeComponent();
        List.ItemsSource = _module.Repos;
        _module.Repos.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = _module.Repos.Count == 0;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RepoStatus repo }) _module.OpenFolder(repo);
    }
}
