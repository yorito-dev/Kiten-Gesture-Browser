using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal sealed class LocalVideoPlaybackController : IDisposable
{
    private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromMilliseconds(500);
    private readonly MediaElement _videoView;
    private readonly YouTubeMiniPlayerState _state;
    private readonly Func<string, bool> _isSupportedMediaPath;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _abRepeatTimer;
    private MediaElement? _mediaPlayer;
    private Uri? _openingSource;
    private LocalVideoFramePreviewService? _framePreviewService;
    private bool _isDisposed;
    private bool _loadWhenViewIsReady;
    private bool _playWhenOpened;
    private bool _hasTransientMutedOverride;
    private bool _hasTransientPausedOverride;
    private bool _hasTransientPlaybackRateOverride;
    private uint _videoWidth;
    private uint _videoHeight;

    public LocalVideoPlaybackController(
        MediaElement videoView,
        YouTubeMiniPlayerState state,
        Func<string, bool>? isSupportedMediaPath = null)
    {
        _videoView = videoView;
        _state = state;
        _isSupportedMediaPath = isSupportedMediaPath ?? WorkspaceShellItemService.IsVideoPath;
        _positionTimer = new DispatcherTimer
        {
            Interval = PositionRefreshInterval
        };
        _positionTimer.Tick += PositionTimer_Tick;
        _abRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _abRepeatTimer.Tick += AbRepeatTimer_Tick;
        _videoView.LoadedBehavior = MediaState.Manual;
        _videoView.UnloadedBehavior = MediaState.Manual;
        _videoView.ScrubbingEnabled = true;
        _videoView.Loaded += VideoView_Loaded;
        _videoView.MediaOpened += MediaElement_Opened;
        _videoView.MediaEnded += MediaElement_Ended;
        _videoView.MediaFailed += MediaElement_Failed;
        _videoView.SizeChanged += MediaElement_SizeChanged;
    }

    public event Action<bool>? StateChanged;

    public event Action<double, double>? VideoDimensionsAvailable;

    public event Action<string>? LoadFailed;

    public bool IsLoaded { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool ApplyContentTransform()
    {
        if (_isDisposed || _mediaPlayer is null || _videoWidth == 0 || _videoHeight == 0)
        {
            return false;
        }

        YouTubeMiniPlayerContentTransformState.Normalize(_state);
        var width = _videoView.ActualWidth;
        var height = _videoView.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var zoom = _state.ContentZoomFactor;
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(zoom, zoom, width / 2, height / 2));
        transform.Children.Add(new TranslateTransform(
            width * (zoom - 1) * _state.ContentPanX / 2,
            height * (zoom - 1) * _state.ContentPanY / 2));
        _videoView.RenderTransform = transform;
        return true;
    }

    public bool Load()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (!_videoView.IsLoaded)
        {
            _loadWhenViewIsReady = true;
            return true;
        }

        return LoadCore();
    }

    public bool TogglePlayPause()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (!IsLoaded || _mediaPlayer is null)
        {
            return Load();
        }

        try
        {
            if (IsPlaying)
            {
                SetPlayerPaused(true);
                IsPlaying = false;
                _positionTimer.Stop();
            }
            else
            {
                SetPlayerPaused(false);
                IsPlaying = true;
                _positionTimer.Start();
            }

            UpdateStateFromPlayer();
            NotifyStateChanged(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Pause()
    {
        return SetPaused(true);
    }

    public bool SetPaused(bool isPaused)
    {
        if (!IsLoaded || _isDisposed || _mediaPlayer is null)
        {
            return false;
        }

        _hasTransientPausedOverride = false;
        if (IsPlaying == !isPaused)
        {
            return true;
        }

        try
        {
            SetPlayerPaused(isPaused);
            IsPlaying = !isPaused;
            if (IsPlaying)
            {
                _positionTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
            }

            UpdateStateFromPlayer();
            NotifyStateChanged(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetPausedTransient(bool isPaused)
    {
        if (!IsLoaded || _isDisposed || _mediaPlayer is null)
        {
            return false;
        }

        var previousOverride = _hasTransientPausedOverride;
        _hasTransientPausedOverride = _state.IsPaused.HasValue
            && _state.IsPaused.Value != isPaused;
        if (IsPlaying == !isPaused)
        {
            return true;
        }

        try
        {
            SetPlayerPaused(isPaused);
            IsPlaying = !isPaused;
            if (IsPlaying)
            {
                _positionTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
            }

            NotifyStateChanged(false);
            return true;
        }
        catch
        {
            _hasTransientPausedOverride = previousOverride;
            return false;
        }
    }

    public bool SetMuted(bool isMuted)
    {
        if (_isDisposed)
        {
            return false;
        }

        _hasTransientMutedOverride = false;
        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.IsMuted = isMuted;
            }

            _state.IsMuted = isMuted;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            NotifyStateChanged(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetMutedTransient(bool isMuted)
    {
        if (_isDisposed || _mediaPlayer is null)
        {
            return false;
        }

        var previousOverride = _hasTransientMutedOverride;
        _hasTransientMutedOverride = _state.IsMuted != isMuted;
        try
        {
            _mediaPlayer.IsMuted = isMuted;
            return true;
        }
        catch
        {
            _hasTransientMutedOverride = previousOverride;
            return false;
        }
    }

    public bool ToggleMuted()
    {
        return SetMuted(!_state.IsMuted);
    }

    public bool SetVolume(double volume)
    {
        if (_isDisposed || !double.IsFinite(volume))
        {
            return false;
        }

        var normalizedVolume = Math.Clamp(volume, 0, 1);
        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Volume = normalizedVolume;
            }

            _state.Volume = normalizedVolume;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            NotifyStateChanged(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SeekRelative(double deltaSeconds)
    {
        if (!IsLoaded
            || _isDisposed
            || _mediaPlayer is null
            || !double.IsFinite(deltaSeconds))
        {
            return false;
        }

        return SetPositionSeconds(_mediaPlayer.Position.TotalSeconds + deltaSeconds);
    }

    public bool SeekRelativeTransient(double deltaSeconds)
    {
        if (!IsLoaded
            || _isDisposed
            || _mediaPlayer is null
            || !double.IsFinite(deltaSeconds))
        {
            return false;
        }

        return SetPositionSeconds(_mediaPlayer.Position.TotalSeconds + deltaSeconds, queueSave: false);
    }

    public bool SeekToRatio(double positionRatio)
    {
        if (!IsLoaded
            || _isDisposed
            || _mediaPlayer is null
            || !double.IsFinite(positionRatio))
        {
            return false;
        }

        var durationSeconds = GetDurationSeconds();
        return durationSeconds > 0
            && SetPositionSeconds(durationSeconds * Math.Clamp(positionRatio, 0, 1));
    }

    public Task<BitmapSource?> GetPreviewFrameAsync(
        double positionSeconds,
        CancellationToken cancellationToken)
    {
        if (!IsLoaded
            || _isDisposed
            || string.IsNullOrWhiteSpace(_state.LocalFilePath)
            || !double.IsFinite(positionSeconds))
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        var durationSeconds = GetDurationSeconds();
        var lastPreviewSecond = durationSeconds > 0
            ? Math.Max(0, durationSeconds - 0.1)
            : Math.Max(0, positionSeconds);
        var normalizedPosition = Math.Clamp(positionSeconds, 0, lastPreviewSecond);
        try
        {
            _framePreviewService ??= new LocalVideoFramePreviewService(
                _state.LocalFilePath,
                _videoView.Dispatcher);
            return _framePreviewService.GetFrameAsync(normalizedPosition, cancellationToken);
        }
        catch
        {
            StopFramePreview();
            return Task.FromResult<BitmapSource?>(null);
        }
    }

    public void StopFramePreview()
    {
        _framePreviewService?.Dispose();
        _framePreviewService = null;
    }

    public bool SetPlaybackRate(double playbackRate)
    {
        if (_isDisposed || !double.IsFinite(playbackRate))
        {
            return false;
        }

        _hasTransientPlaybackRateOverride = false;
        var normalizedRate = NormalizePlaybackRate(playbackRate);
        try
        {
            if (_mediaPlayer is not null
                && IsLoaded
                && !TrySetRate(normalizedRate))
            {
                return false;
            }

            _state.PlaybackRate = normalizedRate;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            NotifyStateChanged(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetPlaybackRateTransient(double playbackRate)
    {
        if (_isDisposed || !double.IsFinite(playbackRate))
        {
            return false;
        }

        var normalizedRate = NormalizePlaybackRate(playbackRate);
        var previousOverride = _hasTransientPlaybackRateOverride;
        _hasTransientPlaybackRateOverride = Math.Abs((_state.PlaybackRate ?? 1) - normalizedRate) > 0.001;
        try
        {
            if (_mediaPlayer is not null
                && IsLoaded
                && !TrySetRate(normalizedRate))
            {
                _hasTransientPlaybackRateOverride = previousOverride;
                return false;
            }

            return true;
        }
        catch
        {
            _hasTransientPlaybackRateOverride = previousOverride;
            return false;
        }
    }

    public void CommitTransientPlaybackState()
    {
        if (_isDisposed)
        {
            return;
        }

        _hasTransientPausedOverride = false;
        _hasTransientPlaybackRateOverride = false;
        UpdateStateFromPlayer();
        NotifyStateChanged(true);
    }

    public bool ToggleLoop()
    {
        if (_isDisposed)
        {
            return false;
        }

        _state.IsLooping = _state.IsLooping != true;
        if (_state.IsLooping == true)
        {
            _state.IsAbRepeatEnabled = false;
        }

        _state.UpdatedAt = DateTimeOffset.UtcNow;
        NotifyStateChanged(true);
        return true;
    }

    public bool SetAbRepeatStart()
    {
        if (!TryGetCurrentPosition(out var positionSeconds))
        {
            return false;
        }

        _state.AbRepeatStartSeconds = positionSeconds;
        if (_state.AbRepeatEndSeconds is not { } end
            || end - positionSeconds < YouTubeMiniPlayerAbRepeatState.MinimumDurationSeconds)
        {
            _state.AbRepeatEndSeconds = null;
            _state.IsAbRepeatEnabled = false;
        }

        _state.UpdatedAt = DateTimeOffset.UtcNow;
        NotifyStateChanged(true);
        return true;
    }

    public bool SetAbRepeatEnd()
    {
        if (_state.AbRepeatStartSeconds is not { } start
            || !TryGetCurrentPosition(out var positionSeconds)
            || positionSeconds - start < YouTubeMiniPlayerAbRepeatState.MinimumDurationSeconds)
        {
            return false;
        }

        _state.AbRepeatEndSeconds = positionSeconds;
        _state.IsAbRepeatEnabled = true;
        _state.IsLooping = false;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        NotifyStateChanged(true);
        return true;
    }

    public bool ToggleAbRepeat()
    {
        if (!YouTubeMiniPlayerAbRepeatState.HasValidRange(_state))
        {
            return false;
        }

        _state.IsAbRepeatEnabled = !_state.IsAbRepeatEnabled;
        if (_state.IsAbRepeatEnabled)
        {
            _state.IsLooping = false;
        }

        _state.UpdatedAt = DateTimeOffset.UtcNow;
        NotifyStateChanged(true);
        return true;
    }

    public bool ClearAbRepeat()
    {
        if (_state.AbRepeatStartSeconds is null
            && _state.AbRepeatEndSeconds is null
            && !_state.IsAbRepeatEnabled)
        {
            return false;
        }

        YouTubeMiniPlayerAbRepeatState.Clear(_state);
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        NotifyStateChanged(true);
        return true;
    }

    public bool Reload(bool captureState = true)
    {
        if (_isDisposed)
        {
            return false;
        }

        if (captureState)
        {
            CaptureState();
        }

        StopCurrentMedia();
        return Load();
    }

    public bool CaptureState()
    {
        if (!IsLoaded || _isDisposed || _mediaPlayer is null)
        {
            return false;
        }

        var before = (
            _state.LastPositionSeconds,
            _state.DurationSeconds,
            _state.IsPaused,
            _state.IsMuted,
            _state.Volume,
            _state.PlaybackRate);
        UpdateStateFromPlayer();
        var after = (
            _state.LastPositionSeconds,
            _state.DurationSeconds,
            _state.IsPaused,
            _state.IsMuted,
            _state.Volume,
            _state.PlaybackRate);
        return before != after;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _loadWhenViewIsReady = false;
        _positionTimer.Stop();
        _positionTimer.Tick -= PositionTimer_Tick;
        _abRepeatTimer.Stop();
        _abRepeatTimer.Tick -= AbRepeatTimer_Tick;
        _videoView.Loaded -= VideoView_Loaded;
        StopCurrentMedia();
        _videoView.MediaOpened -= MediaElement_Opened;
        _videoView.MediaEnded -= MediaElement_Ended;
        _videoView.MediaFailed -= MediaElement_Failed;
        _videoView.SizeChanged -= MediaElement_SizeChanged;
        _mediaPlayer = null;
    }

    private bool LoadCore()
    {
        var path = _state.LocalFilePath;
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !_isSupportedMediaPath(path))
        {
            ReportFailure("対応しているローカル動画ファイルではありません");
            return false;
        }

        if (!File.Exists(path))
        {
            ReportFailure("動画ファイルが見つかりません: " + path);
            return false;
        }

        try
        {
            StopCurrentMedia();
            _mediaPlayer = _videoView;
            _loadWhenViewIsReady = false;
            _playWhenOpened = _state.IsPaused != true;
            _openingSource = new Uri(Path.GetFullPath(path));
            _mediaPlayer.Source = _openingSource;
            ApplyAudioAndRate();
            // 一時停止での復元にも、最初のフレームを用意する。
            SetPlayerPaused(!_playWhenOpened);
            NotifyStateChanged(false);
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure("動画ファイルを開けませんでした: " + ex.Message);
            return false;
        }
    }

    private void StopCurrentMedia()
    {
        StopFramePreview();
        _positionTimer.Stop();
        _openingSource = null;
        _abRepeatTimer.Stop();
        IsLoaded = false;
        IsPlaying = false;
        _videoWidth = 0;
        _videoHeight = 0;
        _hasTransientMutedOverride = false;
        _hasTransientPausedOverride = false;
        _hasTransientPlaybackRateOverride = false;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Close();
            _mediaPlayer.Source = null;
            _mediaPlayer.RenderTransform = Transform.Identity;
        }
    }

    private bool SetPositionSeconds(double positionSeconds, bool queueSave = true)
    {
        if (_mediaPlayer is null || !double.IsFinite(positionSeconds))
        {
            return false;
        }

        var duration = GetDurationSeconds();
        var position = Math.Max(0, positionSeconds);
        if (duration > 0)
        {
            position = Math.Min(position, duration);
        }

        try
        {
            _mediaPlayer.Position = TimeSpan.FromSeconds(position);
            UpdateStateFromPlayer();
            StateChanged?.Invoke(queueSave);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetPlayerPaused(bool paused)
    {
        if (paused)
        {
            _mediaPlayer!.Pause();
        }
        else
        {
            _mediaPlayer!.Play();
        }
    }

    private bool TrySetRate(double rate)
    {
        _mediaPlayer!.SpeedRatio = rate;
        return true;
    }

    private void VideoView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isDisposed && _loadWhenViewIsReady)
        {
            LoadCore();
        }
    }

    private void MediaElement_Opened(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || _openingSource is null || _videoView.Source != _openingSource)
        {
            return;
        }

        try
        {
            IsLoaded = true;
            ApplyAudioAndRate();
            if (_state.LastPositionSeconds is { } position && double.IsFinite(position) && position > 0)
            {
                var duration = GetDurationSeconds();
                _mediaPlayer!.Position = TimeSpan.FromSeconds(duration > 0 ? Math.Min(position, duration) : position);
            }

            SetPlayerPaused(!_playWhenOpened);
            IsPlaying = _playWhenOpened;
            if (IsPlaying)
            {
                _positionTimer.Start();
            }

            _videoWidth = (uint)Math.Max(0, _videoView.NaturalVideoWidth);
            _videoHeight = (uint)Math.Max(0, _videoView.NaturalVideoHeight);
            ApplyContentTransform();
            if (_videoWidth > 0 && _videoHeight > 0)
            {
                VideoDimensionsAvailable?.Invoke(_videoWidth, _videoHeight);
            }
            UpdateStateFromPlayer();
            NotifyStateChanged(true);
        }
        catch (Exception ex)
        {
            ReportFailure("動画の再生状態を復元できませんでした: " + ex.Message);
        }
    }

    private void MediaElement_Ended(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || !IsLoaded || _mediaPlayer is null)
        {
            return;
        }

        if (_state.IsAbRepeatEnabled && YouTubeMiniPlayerAbRepeatState.HasValidRange(_state))
        {
            _mediaPlayer.Position = TimeSpan.FromSeconds(_state.AbRepeatStartSeconds!.Value);
            _mediaPlayer.Play();
        }
        else if (_state.IsLooping == true)
        {
            _mediaPlayer.Position = TimeSpan.Zero;
            _mediaPlayer.Play();
        }
        else
        {
            IsPlaying = false;
            _positionTimer.Stop();
            _state.IsPaused = true;
            _state.LastPositionSeconds = _state.DurationSeconds;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            NotifyStateChanged(true);
        }
    }

    private void MediaElement_Failed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!_isDisposed && _openingSource is not null)
        {
            ReportFailure("このWindows環境では動画を再生できませんでした。ファイル形式・コーデック・データを確認してください");
        }
    }

    private void MediaElement_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyContentTransform();
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStateFromPlayer();
        NotifyStateChanged(false);
    }

    private void AbRepeatTimer_Tick(object? sender, EventArgs e)
    {
        if (IsPlaying && _mediaPlayer is not null && !_isDisposed
            && _state.IsAbRepeatEnabled && YouTubeMiniPlayerAbRepeatState.HasValidRange(_state)
            && _mediaPlayer.Position.TotalSeconds + 0.02 >= _state.AbRepeatEndSeconds!.Value)
        {
            _mediaPlayer.Position = TimeSpan.FromSeconds(_state.AbRepeatStartSeconds!.Value);
        }
    }

    private void NotifyStateChanged(bool queueSave)
    {
        // A-Bだけを短い間隔で監視する。状態通知・保存頻度は従来の境界を維持する。
        _abRepeatTimer.IsEnabled = IsLoaded && IsPlaying && !_isDisposed
            && _state.IsAbRepeatEnabled && YouTubeMiniPlayerAbRepeatState.HasValidRange(_state);
        StateChanged?.Invoke(queueSave);
    }

    private void ApplyAudioAndRate()
    {
        _mediaPlayer!.Volume = Math.Clamp(_state.Volume ?? 0.7, 0, 1);
        _mediaPlayer.IsMuted = _state.IsMuted;
        _mediaPlayer.SpeedRatio = NormalizePlaybackRate(_state.PlaybackRate ?? 1);
    }

    private void UpdateStateFromPlayer()
    {
        if (!IsLoaded || _mediaPlayer is null)
        {
            return;
        }

        _state.LastPositionSeconds = Math.Max(0, _mediaPlayer.Position.TotalSeconds);
        var duration = GetDurationSeconds();
        if (duration > 0)
        {
            _state.DurationSeconds = duration;
        }
        if (!_hasTransientPausedOverride)
        {
            _state.IsPaused = !IsPlaying;
        }
        if (!_hasTransientMutedOverride)
        {
            _state.IsMuted = _mediaPlayer.IsMuted;
        }
        _state.Volume = _mediaPlayer.Volume;
        if (!_hasTransientPlaybackRateOverride)
        {
            _state.PlaybackRate = NormalizePlaybackRate(_mediaPlayer.SpeedRatio);
        }
        _state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private double GetDurationSeconds()
    {
        return _mediaPlayer is { NaturalDuration.HasTimeSpan: true } player
            ? Math.Max(0, player.NaturalDuration.TimeSpan.TotalSeconds)
            : Math.Max(0, _state.DurationSeconds ?? 0);
    }

    private static double NormalizePlaybackRate(double rate)
    {
        return Math.Clamp(Math.Round(rate * 4) / 4, 0.25, 2);
    }

    private bool TryGetCurrentPosition(out double positionSeconds)
    {
        positionSeconds = IsLoaded && !_isDisposed && _mediaPlayer is not null
            ? Math.Max(0, _mediaPlayer.Position.TotalSeconds) : double.NaN;
        return double.IsFinite(positionSeconds);
    }

    private void ReportFailure(string message)
    {
        StopCurrentMedia();
        _state.IsPaused = true;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        LoadFailed?.Invoke(message);
        NotifyStateChanged(true);
    }
}
