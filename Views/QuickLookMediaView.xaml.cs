using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;

namespace TabBrowser.Views;

public partial class QuickLookMediaView : UserControl, IDisposable
{
    private readonly YouTubeMiniPlayerState _state;
    private readonly LocalVideoPlaybackController _controller;
    private readonly bool _autoPlay;
    private bool _isDisposed;
    private bool _isSeeking;
    private bool _isUpdatingControls;

    public QuickLookMediaView(string filePath, bool autoPlay)
    {
        InitializeComponent();
        _autoPlay = autoPlay;
        _state = new YouTubeMiniPlayerState
        {
            SourceKind = MiniPlayerSourceKind.LocalFile,
            LocalFilePath = Path.GetFullPath(filePath),
            Title = Path.GetFileNameWithoutExtension(filePath),
            IsPaused = !autoPlay,
            IsLooping = true,
            Volume = 0.7,
            PlaybackRate = 1
        };
        _controller = new LocalVideoPlaybackController(Player, _state, QuickLookMediaFormats.IsSupported);
        _controller.StateChanged += Controller_StateChanged;
        _controller.LoadFailed += Controller_LoadFailed;
        MediaTitleText.Text = Path.GetFileName(filePath);
        VolumeSlider.Value = 70;
        Loaded += QuickLookMediaView_Loaded;
        UpdateControls();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= QuickLookMediaView_Loaded;
        _controller.StateChanged -= Controller_StateChanged;
        _controller.LoadFailed -= Controller_LoadFailed;
        _controller.Dispose();
    }

    private void QuickLookMediaView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_autoPlay)
        {
            StartLoading(playWhenReady: true);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsLoaded)
        {
            _controller.TogglePlayPause();
            return;
        }

        StartLoading(playWhenReady: true);
    }

    private void StartLoading(bool playWhenReady)
    {
        _state.IsPaused = !playWhenReady;
        StateText.Text = "メディアを読み込んでいます...";
        if (!_controller.Load())
        {
            StateText.Text = "メディアの読み込みを開始できませんでした。";
        }
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.SeekRelative(-10);
    }

    private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.SeekRelative(10);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.ToggleMuted();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUpdatingControls)
        {
            _controller.SetVolume(e.NewValue / 100);
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        _controller.SeekToRatio(SeekSlider.Value / 100);
    }

    private void Controller_StateChanged(bool queueSave)
    {
        UpdateControls();
    }

    private void Controller_LoadFailed(string message)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateText.Text = message;
        StateText.Foreground = System.Windows.Media.Brushes.IndianRed;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_isDisposed)
        {
            return;
        }

        _isUpdatingControls = true;
        try
        {
            if (_controller.IsLoaded)
            {
                StatePanel.Visibility = Visibility.Collapsed;
            }

            PlayPauseButton.Content = _controller.IsPlaying ? "一時停止" : "再生";
            MuteButton.Content = _state.IsMuted ? "ミュート解除" : "ミュート";
            VolumeSlider.Value = Math.Clamp((_state.Volume ?? 0.7) * 100, 0, 100);
            var position = Math.Max(0, _state.LastPositionSeconds ?? 0);
            var duration = Math.Max(0, _state.DurationSeconds ?? 0);
            if (!_isSeeking)
            {
                SeekSlider.Value = duration > 0
                    ? Math.Clamp(position / duration * 100, 0, 100)
                    : 0;
            }

            SeekSlider.IsEnabled = _controller.IsLoaded && duration > 0;
            TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }
}
