using System.Windows;
using System.Windows.Controls;

namespace DevBar.Modules.Media;

public partial class MediaCard : UserControl
{
    private readonly MediaModule _module;

    public MediaCard(MediaModule module)
    {
        _module = module;
        InitializeComponent();
        _module.Changed += Render;
        Render();
    }

    private void Render()
    {
        Dispatcher.Invoke(() =>
        {
            if (!_module.HasSession)
            {
                EmptyHint.Visibility = Visibility.Visible;
                PlayerPanel.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            PlayerPanel.Visibility = Visibility.Visible;
            TitleText.Text = string.IsNullOrEmpty(_module.Title) ? "Unknown title" : _module.Title;
            ArtistText.Text = _module.Artist;
            Thumb.Source = _module.Thumbnail;
            PlayBtn.Content = _module.IsPlaying ? "" : "";
        });
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => _module.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => _module.TogglePlay();
    private void Next_Click(object sender, RoutedEventArgs e) => _module.Next();
}
