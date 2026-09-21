using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevBar.Sdk;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DevBar.Modules.Media;

/// <summary>
/// Now-playing + transport, sourced from Windows' own SMTC session manager -
/// the same data feeding the Windows 11 volume flyout. We don't touch any
/// app's audio pipeline directly, just read/command the OS-level session.
/// </summary>
public sealed class MediaModule : IDevBarModule
{
    public string Id => "media";
    public string DisplayName => "Now Playing";
    public string IconGlyph => "";

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public bool HasSession { get; private set; }
    public BitmapImage? Thumbnail { get; private set; }

    public event Action? Changed;

    private DispatcherTimer? _timer;
    private MediaCard? _card;

    public UserControl BuildCard()
    {
        _card = new MediaCard(this);
        return _card;
    }

    public void OnExpanded()
    {
        _ = RefreshAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public void OnCollapsed()
    {
        _timer?.Stop();
        _timer = null;
    }

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task RefreshAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager.GetCurrentSession();
            if (session is null)
            {
                HasSession = false;
                Changed?.Invoke();
                return;
            }

            HasSession = true;
            var props = await session.TryGetMediaPropertiesAsync();
            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (props.Thumbnail != null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var netStream = new MemoryStream();
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                netStream.Write(bytes, 0, bytes.Length);
                netStream.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = netStream;
                bmp.EndInit();
                bmp.Freeze();
                Thumbnail = bmp;
            }
            else Thumbnail = null;
        }
        catch
        {
            HasSession = false;
        }
        Changed?.Invoke();
    }

    public void TogglePlay() => _manager?.GetCurrentSession()?.TryTogglePlayPauseAsync();
    public void Next() => _manager?.GetCurrentSession()?.TrySkipNextAsync();
    public void Previous() => _manager?.GetCurrentSession()?.TrySkipPreviousAsync();
}
