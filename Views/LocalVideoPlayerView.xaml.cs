using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

public partial class LocalVideoPlayerView : UserControl, IDisposable
{
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromSeconds(2.2);
    private static readonly TimeSpan ContentTransformHudHideDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ContentPanEdgeFeedbackDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan FixedGestureUpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SeekPreviewRequestInterval = TimeSpan.FromMilliseconds(250);
    private const double ContentPanThreshold = 6;
    private const double FixedGestureRewindStepSeconds = 0.5;
    private const int SeekPreviewCacheLimit = 24;
    private const double SeekPreviewPopupWidth = 204;

    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _contentTransformHudHideTimer;
    private readonly DispatcherTimer _contentPanEdgeFeedbackTimer;
    private readonly DispatcherTimer _fixedGestureUpdateTimer;
    private readonly DispatcherTimer _seekPreviewRequestTimer;
    private readonly Dictionary<long, BitmapSource> _seekPreviewCache = [];
    private readonly Queue<long> _seekPreviewCacheOrder = [];
    private WorkspaceGestureController? _gestureController;
    private YouTubeMiniPlayerState? _state;
    private LocalVideoPlaybackController? _controller;
    private CancellationTokenSource? _seekPreviewCancellation;
    private long? _pendingSeekPreviewSecond;
    private long? _requestedSeekPreviewSecond;
    private long? _visibleSeekPreviewSecond;
    private int _seekPreviewRequestVersion;
    private bool _isDisposed;
    private bool _isSeeking;
    private bool _isUpdatingControls;
    private bool _hasLoadFailed;
    private bool _isPanningContent;
    private bool _contentPanChanged;
    private bool _contentPanThresholdExceeded;
    private Point _contentPanStartPoint;
    private double _contentPanStartX;
    private double _contentPanStartY;
    private int _contentZoomWheelDelta;
    private MouseButton? _pressedSideButton;
    private MiniPlayerFixedGestureDirection _fixedGestureDirection;
    private Point _fixedGestureDirectionAnchor;
    private double _fixedGestureOriginalPlaybackRate = 1;
    private bool _fixedGestureOriginalPaused;
    private bool _fixedCloseGestureCancelled;

    public LocalVideoPlayerView()
    {
        InitializeComponent();
        _controlsHideTimer = new DispatcherTimer
        {
            Interval = ControlsHideDelay
        };
        _controlsHideTimer.Tick += ControlsHideTimer_Tick;
        _contentTransformHudHideTimer = new DispatcherTimer
        {
            Interval = ContentTransformHudHideDelay
        };
        _contentTransformHudHideTimer.Tick += ContentTransformHudHideTimer_Tick;
        _contentPanEdgeFeedbackTimer = new DispatcherTimer
        {
            Interval = ContentPanEdgeFeedbackDuration
        };
        _contentPanEdgeFeedbackTimer.Tick += ContentPanEdgeFeedbackTimer_Tick;
        _fixedGestureUpdateTimer = new DispatcherTimer
        {
            Interval = FixedGestureUpdateInterval
        };
        _fixedGestureUpdateTimer.Tick += FixedGestureUpdateTimer_Tick;
        _seekPreviewRequestTimer = new DispatcherTimer
        {
            Interval = SeekPreviewRequestInterval
        };
        _seekPreviewRequestTimer.Tick += SeekPreviewRequestTimer_Tick;
        SpeedComboBox.ItemsSource = Enumerable.Range(1, 8)
            .Select(static index => new PlaybackRateOption(index * 0.25, $"{index * 0.25:0.##}x"))
            .ToList();
        SpeedComboBox.DisplayMemberPath = nameof(PlaybackRateOption.Text);
        SpeedComboBox.SelectedValuePath = nameof(PlaybackRateOption.Value);
    }

    public event Action<bool>? PlaybackStateChanged;

    public event Action<double, double>? VideoDimensionsAvailable;

    public event Action? FullScreenRequested;

    public event Action? OverlayHoverRefreshRequested;

    public event Action? RelinkRequested;

    public event Action<string>? LocalFileDropped;

    public event Action? CloseGestureRequested;

    internal LocalVideoPlaybackController? Controller => _controller;

    internal bool HasLoadFailed => _hasLoadFailed;

    public FrameworkElement DragSurface => Surface;

    public void ConfigureGestures(
        AppSettings settings,
        Func<string?, string?, Task> executeCommandAsync,
        Func<long?> tryBeginInteraction,
        Action<long> completeInteraction,
        Func<long?> tryBeginRightWheelInteraction)
    {
        if (_gestureController is null)
        {
            _gestureController = new WorkspaceGestureController(
                Surface,
                settings,
                executeCommandAsync,
                tryBeginInteraction,
                completeInteraction,
                tryBeginRightWheelInteraction,
                canStartTracking: () => !ControlsPanel.IsMouseOver
                    && !PlaceholderPanel.IsMouseOver
                    && !AbRepeatMenu.IsOpen,
                handleHandledMouseEvents: true,
                gestureScope: GestureCommandScope.MiniPlayer,
                executeRightWheelRoute: ApplyContentZoomWheel,
                gestureGuideSupplement: GetMiniPlayerGestureGuideSupplement,
                fallbackCallbacks: new WorkspaceGestureFallbackCallbacks(
                    CanHandleFixedGestureDirection,
                    CanStartFixedGesture,
                    StartFixedGesture,
                    UpdateFixedGesture,
                    CompleteFixedGesture,
                    CancelFixedGesture));
            return;
        }

        _gestureController.UpdateSettings(settings);
    }

    public void UpdateGestureSettings(AppSettings settings)
    {
        _gestureController?.Cancel();
        _gestureController?.UpdateSettings(settings);
    }

    public void CancelGesture()
    {
        _gestureController?.Cancel();
    }

    public void Initialize(YouTubeMiniPlayerState state)
    {
        _state = state;
        PlaceholderTitleText.Text = string.IsNullOrWhiteSpace(state.Title)
            ? Path.GetFileNameWithoutExtension(state.LocalFilePath)
            : state.Title;
        _controller = new LocalVideoPlaybackController(Player, state);
        _controller.StateChanged += Controller_StateChanged;
        _controller.VideoDimensionsAvailable += Controller_VideoDimensionsAvailable;
        _controller.LoadFailed += Controller_LoadFailed;
        UpdateDisplay();
    }

    public bool LoadMedia()
    {
        if (_controller is null)
        {
            return false;
        }

        _hasLoadFailed = false;
        RelinkButton.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Visible;
        PlaceholderMessageText.Text = "動画を読み込んでいます...";
        return _controller.Load();
    }

    public bool ReplaceFile(string filePath)
    {
        if (_state is null
            || string.IsNullOrWhiteSpace(filePath)
            || !Path.IsPathFullyQualified(filePath)
            || !File.Exists(filePath)
            || !WorkspaceShellItemService.IsVideoPath(filePath))
        {
            return false;
        }

        ResetSeekPreview();
        _state.LocalFilePath = Path.GetFullPath(filePath);
        _state.Title = Path.GetFileNameWithoutExtension(_state.LocalFilePath);
        _state.LastPositionSeconds = 0;
        _state.DurationSeconds = null;
        _state.VideoAspectRatio = null;
        YouTubeMiniPlayerAbRepeatState.Clear(_state);
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        _hasLoadFailed = false;
        PlaceholderTitleText.Text = _state.Title;
        return _controller?.Reload(captureState: false) == true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        FinishContentPan();
        _gestureController?.Dispose();
        _gestureController = null;
        _controlsHideTimer.Stop();
        _controlsHideTimer.Tick -= ControlsHideTimer_Tick;
        _contentTransformHudHideTimer.Stop();
        _contentTransformHudHideTimer.Tick -= ContentTransformHudHideTimer_Tick;
        _contentPanEdgeFeedbackTimer.Stop();
        _contentPanEdgeFeedbackTimer.Tick -= ContentPanEdgeFeedbackTimer_Tick;
        CancelFixedGesture();
        _fixedGestureUpdateTimer.Stop();
        _fixedGestureUpdateTimer.Tick -= FixedGestureUpdateTimer_Tick;
        ResetSeekPreview();
        _seekPreviewRequestTimer.Tick -= SeekPreviewRequestTimer_Tick;
        if (_controller is not null)
        {
            _controller.StateChanged -= Controller_StateChanged;
            _controller.VideoDimensionsAvailable -= Controller_VideoDimensionsAvailable;
            _controller.LoadFailed -= Controller_LoadFailed;
            _controller.Dispose();
            _controller = null;
        }
    }

    private void Controller_StateChanged(bool queueSave)
    {
        if (_controller?.IsLoaded == true)
        {
            _hasLoadFailed = false;
        }

        UpdateDisplay();
        PlaybackStateChanged?.Invoke(queueSave);
    }

    private void Controller_VideoDimensionsAvailable(double width, double height)
    {
        VideoDimensionsAvailable?.Invoke(width, height);
    }

    private void Controller_LoadFailed(string message)
    {
        HideSeekPreview();
        _hasLoadFailed = true;
        PlaceholderPanel.Visibility = Visibility.Visible;
        PlaceholderMessageText.Text = message;
        RelinkButton.Visibility = Visibility.Visible;
        ControlsPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateDisplay()
    {
        if (_state is null || _controller is null)
        {
            return;
        }

        _isUpdatingControls = true;
        try
        {
            PlaceholderPanel.Visibility = _controller.IsLoaded
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateControlsVisibility();
            PlayPauseButton.Content = _controller.IsPlaying ? "一時停止" : "再生";
            MuteButton.Content = _state.IsMuted ? "ミュート解除" : "ミュート";
            LoopButton.Content = _state.IsLooping == true ? "ループ解除" : "ループ";
            UpdateAbRepeatDisplay();
            VolumeSlider.Value = Math.Clamp((_state.Volume ?? 0.7) * 100, 0, 100);
            SpeedComboBox.SelectedValue = Math.Clamp(_state.PlaybackRate ?? 1, 0.25, 2);
            ZoomResetButton.Content = YouTubeMiniPlayerContentTransformState.FormatPercentage(
                _state.ContentZoomFactor);
            var position = Math.Max(0, _state.LastPositionSeconds ?? 0);
            var duration = Math.Max(0, _state.DurationSeconds ?? 0);
            if (!_isSeeking)
            {
                SeekSlider.Value = duration > 0
                    ? Math.Clamp(position / duration * 100, 0, 100)
                    : 0;
            }

            TimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadMedia();
    }

    private void RelinkButton_Click(object sender, RoutedEventArgs e)
    {
        RelinkRequested?.Invoke();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.TogglePlayPause();
        KeepControlsVisible();
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.SeekRelative(-10);
        KeepControlsVisible();
    }

    private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.SeekRelative(10);
        KeepControlsVisible();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.ToggleMuted();
        KeepControlsVisible();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUpdatingControls)
        {
            _controller?.SetVolume(e.NewValue / 100);
        }
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingControls && SpeedComboBox.SelectedValue is double playbackRate)
        {
            _controller?.SetPlaybackRate(playbackRate);
        }
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.ToggleLoop();
        KeepControlsVisible();
    }

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null)
        {
            return;
        }

        var changed = YouTubeMiniPlayerContentTransformState.Reset(_state);
        _controller?.ApplyContentTransform();
        UpdateDisplay();
        UpdateContentPanCursor();
        ShowContentTransformHud();
        if (changed)
        {
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            PlaybackStateChanged?.Invoke(true);
        }

        KeepControlsVisible();
    }

    private void AbRepeatButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        AbRepeatMenu.PlacementTarget = AbRepeatButton;
        AbRepeatMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        AbRepeatMenu.IsOpen = !AbRepeatMenu.IsOpen;
        KeepControlsVisible();
    }

    private void AbRepeatMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateAbRepeatDisplay();
        KeepControlsVisible();
    }

    private void AbRepeatMenu_Closed(object sender, RoutedEventArgs e)
    {
        QueueControlsHide();
    }

    private void AbRepeatSetStartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.SetAbRepeatStart() != true)
        {
            AbRepeatStatusMenuItem.Header = "動画を読み込んでから設定してください";
            return;
        }

        AbRepeatMenu.IsOpen = false;
    }

    private void AbRepeatSetEndMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.SetAbRepeatEnd() != true)
        {
            AbRepeatStatusMenuItem.Header = "B点はA点から0.5秒以上後に設定してください";
            return;
        }

        AbRepeatMenu.IsOpen = false;
    }

    private void AbRepeatToggleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ToggleAbRepeat() != true)
        {
            AbRepeatStatusMenuItem.Header = "A点とB点を設定してください";
            return;
        }

        AbRepeatMenu.IsOpen = false;
    }

    private void AbRepeatClearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _controller?.ClearAbRepeat();
        AbRepeatMenu.IsOpen = false;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        FullScreenRequested?.Invoke();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        ResetSeekPreview();
        _controller?.Reload();
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
        KeepControlsVisible();
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        _controller?.SeekToRatio(SeekSlider.Value / 100);
        KeepControlsVisible();
    }

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!TryGetSeekPreviewPosition(e, out var previewSecond, out var pointerX, out var durationSeconds))
        {
            HideSeekPreview();
            return;
        }

        _visibleSeekPreviewSecond = previewSecond;
        SeekPreviewPopup.HorizontalOffset = SeekSlider.ActualWidth >= SeekPreviewPopupWidth
            ? Math.Clamp(pointerX - (SeekPreviewPopupWidth / 2), 0, SeekSlider.ActualWidth - SeekPreviewPopupWidth)
            : (SeekSlider.ActualWidth - SeekPreviewPopupWidth) / 2;
        SeekPreviewTimeText.Text = $"{FormatTime(previewSecond)} / {FormatTime(durationSeconds)}";
        SeekPreviewPopup.IsOpen = true;
        KeepControlsVisible();

        if (_seekPreviewCache.TryGetValue(previewSecond, out var cachedFrame))
        {
            SeekPreviewImage.Source = cachedFrame;
            SeekPreviewStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        SeekPreviewImage.Source = null;
        SeekPreviewStatusText.Text = "プレビュー取得中";
        SeekPreviewStatusText.Visibility = Visibility.Visible;
        _pendingSeekPreviewSecond = previewSecond;
        if (_requestedSeekPreviewSecond != previewSecond && !_seekPreviewRequestTimer.IsEnabled)
        {
            _seekPreviewRequestTimer.Start();
        }
    }

    private void SeekSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        HideSeekPreview();
        QueueControlsHide();
    }

    private void SeekPreviewRequestTimer_Tick(object? sender, EventArgs e)
    {
        _seekPreviewRequestTimer.Stop();
        if (_pendingSeekPreviewSecond is not { } previewSecond
            || _requestedSeekPreviewSecond == previewSecond
            || _controller?.IsLoaded != true)
        {
            return;
        }

        _pendingSeekPreviewSecond = null;
        _requestedSeekPreviewSecond = previewSecond;
        _seekPreviewCancellation?.Cancel();
        _seekPreviewCancellation?.Dispose();
        _seekPreviewCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var requestVersion = ++_seekPreviewRequestVersion;
        _ = LoadSeekPreviewFrameAsync(
            previewSecond,
            requestVersion,
            _seekPreviewCancellation.Token);
    }

    private async Task LoadSeekPreviewFrameAsync(
        long previewSecond,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        BitmapSource? frame = null;
        try
        {
            if (_controller is not null)
            {
                frame = await _controller.GetPreviewFrameAsync(previewSecond, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (_isDisposed || requestVersion != _seekPreviewRequestVersion)
        {
            return;
        }

        _requestedSeekPreviewSecond = null;
        if (frame is not null)
        {
            AddSeekPreviewToCache(previewSecond, frame);
        }

        if (_visibleSeekPreviewSecond == previewSecond && SeekPreviewPopup.IsOpen)
        {
            SeekPreviewImage.Source = frame;
            SeekPreviewStatusText.Text = frame is null
                ? "プレビューを取得できません"
                : string.Empty;
            SeekPreviewStatusText.Visibility = frame is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_pendingSeekPreviewSecond.HasValue && !_seekPreviewRequestTimer.IsEnabled)
        {
            _seekPreviewRequestTimer.Start();
        }
    }

    private bool TryGetSeekPreviewPosition(
        MouseEventArgs e,
        out long previewSecond,
        out double pointerX,
        out double durationSeconds)
    {
        durationSeconds = Math.Max(0, _state?.DurationSeconds ?? 0);
        pointerX = e.GetPosition(SeekSlider).X;
        if (_controller?.IsLoaded != true
            || durationSeconds <= 0
            || SeekSlider.ActualWidth <= 0)
        {
            previewSecond = 0;
            return false;
        }

        var ratio = Math.Clamp(pointerX / SeekSlider.ActualWidth, 0, 1);
        previewSecond = (long)Math.Round(durationSeconds * ratio);
        return true;
    }

    private void AddSeekPreviewToCache(long previewSecond, BitmapSource frame)
    {
        if (_seekPreviewCache.ContainsKey(previewSecond))
        {
            _seekPreviewCache[previewSecond] = frame;
            return;
        }

        _seekPreviewCache[previewSecond] = frame;
        _seekPreviewCacheOrder.Enqueue(previewSecond);
        while (_seekPreviewCacheOrder.Count > SeekPreviewCacheLimit)
        {
            _seekPreviewCache.Remove(_seekPreviewCacheOrder.Dequeue());
        }
    }

    private void HideSeekPreview()
    {
        SeekPreviewPopup.IsOpen = false;
        SeekPreviewImage.Source = null;
        _seekPreviewRequestTimer.Stop();
        _pendingSeekPreviewSecond = null;
        _requestedSeekPreviewSecond = null;
        _visibleSeekPreviewSecond = null;
        _seekPreviewRequestVersion++;
        _seekPreviewCancellation?.Cancel();
        _seekPreviewCancellation?.Dispose();
        _seekPreviewCancellation = null;
        _controller?.StopFramePreview();
    }

    private void ResetSeekPreview()
    {
        HideSeekPreview();
        _seekPreviewCache.Clear();
        _seekPreviewCacheOrder.Clear();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        OverlayHoverRefreshRequested?.Invoke();
        UpdateContentPanCursor();

        if (_isPanningContent && _state is not null)
        {
            var point = e.GetPosition(Surface);
            var deltaX = point.X - _contentPanStartPoint.X;
            var deltaY = point.Y - _contentPanStartPoint.Y;
            if (!_contentPanThresholdExceeded
                && ((deltaX * deltaX) + (deltaY * deltaY)) < ContentPanThreshold * ContentPanThreshold)
            {
                e.Handled = true;
                return;
            }

            _contentPanThresholdExceeded = true;
            UpdateContentPanCursor();
            var changed = YouTubeMiniPlayerContentTransformState.MovePan(
                _state,
                _contentPanStartX,
                _contentPanStartY,
                deltaX,
                deltaY,
                Surface.ActualWidth,
                Surface.ActualHeight,
                out var reachedBoundary);
            if (reachedBoundary)
            {
                ShowContentPanEdgeFeedback();
            }

            if (changed)
            {
                _contentPanChanged = true;
                _controller?.ApplyContentTransform();
                ZoomResetButton.Content = YouTubeMiniPlayerContentTransformState.FormatPercentage(
                    _state.ContentZoomFactor);
            }

            ShowContentTransformHud(keepVisible: true);

            e.Handled = true;
            return;
        }

        if (_controller?.IsLoaded != true)
        {
            return;
        }

        if (!_controller.IsPlaying)
        {
            KeepControlsVisible();
            return;
        }

        if (YouTubeMiniPlayerInputService.IsToolbarHotZone(
                e.GetPosition(Surface),
                Surface.ActualHeight))
        {
            ShowControls();
            return;
        }

        QueueControlsHide();
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e)
    {
        OverlayHoverRefreshRequested?.Invoke();
        if (!_isPanningContent)
        {
            Surface.Cursor = null;
        }
        QueueControlsHide();
    }

    private void Surface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && e.ClickCount >= 2
            && _controller?.IsLoaded == true
            && !ControlsPanel.IsMouseOver)
        {
            FullScreenRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void Surface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            _pressedSideButton = e.ChangedButton;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle
            || _state is null
            || _controller?.IsLoaded != true
            || _state.ContentZoomFactor <= YouTubeMiniPlayerContentTransformState.MinimumZoomFactor
            || ControlsPanel.IsMouseOver
            || PlaceholderPanel.IsMouseOver
            || AbRepeatMenu.IsOpen)
        {
            return;
        }

        _isPanningContent = true;
        _contentPanChanged = false;
        _contentPanThresholdExceeded = false;
        _contentPanStartPoint = e.GetPosition(Surface);
        _contentPanStartX = _state.ContentPanX;
        _contentPanStartY = _state.ContentPanY;
        InputManager.Current.PreProcessInput += ContentPanInputManager_PreProcessInput;
        Surface.CaptureMouse();
        UpdateContentPanCursor();
        e.Handled = true;
    }

    private void Surface_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            var shouldExecute = _pressedSideButton == e.ChangedButton;
            _pressedSideButton = null;
            if (shouldExecute)
            {
                if (e.ChangedButton == MouseButton.XButton1)
                {
                    _controller?.TogglePlayPause();
                }
                else
                {
                    _controller?.ToggleMuted();
                }
            }

            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && _isPanningContent)
        {
            FinishContentPan();
            e.Handled = true;
        }
    }

    private void Surface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishContentPan(releaseCapture: false);
    }

    private bool ApplyContentZoomWheel(int wheelDelta)
    {
        if (_state is null || wheelDelta == 0)
        {
            return false;
        }

        _contentZoomWheelDelta += wheelDelta;
        var stepCount = _contentZoomWheelDelta / 120;
        _contentZoomWheelDelta %= 120;
        if (stepCount == 0)
        {
            return true;
        }

        var anchor = GetContentZoomAnchor(Mouse.GetPosition(Surface));
        if (YouTubeMiniPlayerContentTransformState.ChangeZoomAtAnchor(
            _state,
            stepCount,
            anchor.X,
            anchor.Y))
        {
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            _controller?.ApplyContentTransform();
            UpdateDisplay();
            UpdateContentPanCursor();
            PlaybackStateChanged?.Invoke(true);
        }

        ShowContentTransformHud();

        return true;
    }

    private void FinishContentPan(bool releaseCapture = true)
    {
        if (!_isPanningContent)
        {
            return;
        }

        _isPanningContent = false;
        InputManager.Current.PreProcessInput -= ContentPanInputManager_PreProcessInput;
        if (releaseCapture && Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }

        if (_contentPanChanged && _state is not null)
        {
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            PlaybackStateChanged?.Invoke(true);
        }

        if (_contentPanThresholdExceeded)
        {
            ShowContentTransformHud();
        }

        _contentPanChanged = false;
        _contentPanThresholdExceeded = false;
        UpdateContentPanCursor();
    }

    private void UpdateContentPanCursor()
    {
        if (_state is null
            || _state.ContentZoomFactor <= YouTubeMiniPlayerContentTransformState.MinimumZoomFactor
            || ControlsPanel.IsMouseOver
            || PlaceholderPanel.IsMouseOver
            || AbRepeatMenu.IsOpen)
        {
            Surface.Cursor = null;
            return;
        }

        Surface.Cursor = _isPanningContent && _contentPanThresholdExceeded
            ? Cursors.SizeAll
            : Cursors.Hand;
    }

    private string GetMiniPlayerGestureGuideSupplement()
    {
        var percentage = YouTubeMiniPlayerContentTransformState.FormatPercentage(
            _state?.ContentZoomFactor ?? YouTubeMiniPlayerContentTransformState.MinimumZoomFactor);
        var panAvailability = _state?.ContentZoomFactor > YouTubeMiniPlayerContentTransformState.MinimumZoomFactor
            ? string.Empty
            : "（拡大時）";
        return "右ドラッグ  ←巻き戻し  →2x  ↓閉じる\n"
            + $"右＋ホイール  ズーム ({percentage})\n"
            + $"ホイールボタン＋ドラッグ  表示位置{panAvailability}";
    }

    private static bool CanHandleFixedGestureDirection(string direction)
    {
        return direction is "←" or "→" or "↓";
    }

    private bool CanStartFixedGesture(Point startPoint, Point currentPoint)
    {
        var direction = YouTubeMiniPlayerInputService.GetFixedGestureDirection(startPoint, currentPoint);
        return direction == MiniPlayerFixedGestureDirection.Close
            || (direction is MiniPlayerFixedGestureDirection.Rewind
                or MiniPlayerFixedGestureDirection.FastForward
                && _controller?.IsLoaded == true);
    }

    private void StartFixedGesture(Point startPoint, Point currentPoint)
    {
        if (_state is null || _controller is null)
        {
            return;
        }

        _fixedGestureDirection = YouTubeMiniPlayerInputService.GetFixedGestureDirection(
            startPoint,
            currentPoint);
        _fixedGestureDirectionAnchor = currentPoint;
        _fixedGestureOriginalPlaybackRate = Math.Clamp(_state.PlaybackRate ?? 1, 0.25, 2);
        _fixedGestureOriginalPaused = !_controller.IsPlaying;
        _fixedCloseGestureCancelled = false;
        ApplyFixedGestureDirection();
        if (_fixedGestureDirection is MiniPlayerFixedGestureDirection.Rewind
            or MiniPlayerFixedGestureDirection.FastForward)
        {
            _fixedGestureUpdateTimer.Start();
        }
    }

    private void UpdateFixedGesture(Point currentPoint)
    {
        if (_fixedCloseGestureCancelled
            || _fixedGestureDirection == MiniPlayerFixedGestureDirection.None)
        {
            return;
        }

        var nextDirection = YouTubeMiniPlayerInputService.GetFixedGestureDirection(
            _fixedGestureDirectionAnchor,
            currentPoint);
        if (_fixedGestureDirection == MiniPlayerFixedGestureDirection.Close)
        {
            if (nextDirection == MiniPlayerFixedGestureDirection.Close)
            {
                _fixedGestureDirectionAnchor = currentPoint;
                return;
            }

            if (GetMaximumDistance(_fixedGestureDirectionAnchor, currentPoint)
                >= YouTubeMiniPlayerInputService.FixedGestureThreshold)
            {
                _fixedGestureDirection = MiniPlayerFixedGestureDirection.None;
                _fixedCloseGestureCancelled = true;
                HideFixedGestureHud();
            }

            return;
        }

        var isOppositeHorizontalDirection =
            _fixedGestureDirection == MiniPlayerFixedGestureDirection.FastForward
                && nextDirection == MiniPlayerFixedGestureDirection.Rewind
            || _fixedGestureDirection == MiniPlayerFixedGestureDirection.Rewind
                && nextDirection == MiniPlayerFixedGestureDirection.FastForward;
        if (!isOppositeHorizontalDirection)
        {
            return;
        }

        _fixedGestureDirection = nextDirection;
        _fixedGestureDirectionAnchor = currentPoint;
        ApplyFixedGestureDirection();
    }

    private void ApplyFixedGestureDirection()
    {
        if (_controller is null)
        {
            return;
        }

        switch (_fixedGestureDirection)
        {
            case MiniPlayerFixedGestureDirection.FastForward:
                _controller.SetPlaybackRateTransient(Math.Max(2, _fixedGestureOriginalPlaybackRate));
                _controller.SetPausedTransient(false);
                ShowFixedGestureHud("2x", includePlaybackTime: true);
                break;
            case MiniPlayerFixedGestureDirection.Rewind:
                _controller.SetPlaybackRateTransient(_fixedGestureOriginalPlaybackRate);
                _controller.SetPausedTransient(true);
                ShowFixedGestureHud("巻き戻し", includePlaybackTime: true);
                break;
            case MiniPlayerFixedGestureDirection.Close:
                ShowFixedGestureHud("閉じる", includePlaybackTime: false);
                break;
        }
    }

    private void FixedGestureUpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_fixedGestureDirection == MiniPlayerFixedGestureDirection.Rewind)
        {
            _controller?.SeekRelativeTransient(-FixedGestureRewindStepSeconds);
        }

        if (_fixedGestureDirection == MiniPlayerFixedGestureDirection.FastForward)
        {
            ShowFixedGestureHud("2x", includePlaybackTime: true);
        }
        else if (_fixedGestureDirection == MiniPlayerFixedGestureDirection.Rewind)
        {
            ShowFixedGestureHud("巻き戻し", includePlaybackTime: true);
        }
    }

    private void CompleteFixedGesture()
    {
        var shouldClose = _fixedGestureDirection == MiniPlayerFixedGestureDirection.Close
            && !_fixedCloseGestureCancelled;
        RestoreFixedGesturePlayback();
        if (shouldClose)
        {
            CloseGestureRequested?.Invoke();
        }
    }

    private void CancelFixedGesture()
    {
        RestoreFixedGesturePlayback();
    }

    private void RestoreFixedGesturePlayback()
    {
        _fixedGestureUpdateTimer.Stop();
        var hadPlaybackOverride = _fixedGestureDirection is MiniPlayerFixedGestureDirection.Rewind
            or MiniPlayerFixedGestureDirection.FastForward;
        if (hadPlaybackOverride && _controller is not null)
        {
            _controller.SetPlaybackRateTransient(_fixedGestureOriginalPlaybackRate);
            _controller.SetPausedTransient(_fixedGestureOriginalPaused);
            _controller.CommitTransientPlaybackState();
        }

        _fixedGestureDirection = MiniPlayerFixedGestureDirection.None;
        _fixedCloseGestureCancelled = false;
        HideFixedGestureHud();
    }

    private void ShowFixedGestureHud(string modeText, bool includePlaybackTime)
    {
        var timeText = string.Empty;
        if (includePlaybackTime && _state is not null)
        {
            timeText = $"\n{FormatTime(_state.LastPositionSeconds ?? 0)}";
            if (_state.DurationSeconds is > 0)
            {
                timeText += $" / {FormatTime(_state.DurationSeconds.Value)}";
            }
        }

        FixedGestureHudText.Text = modeText + timeText;
        FixedGestureHud.Visibility = Visibility.Visible;
    }

    private void HideFixedGestureHud()
    {
        FixedGestureHud.Visibility = Visibility.Collapsed;
    }

    private static double GetMaximumDistance(Point first, Point second)
    {
        return Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));
    }

    private void ContentPanInputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!_isPanningContent
            || e.StagingItem.Input is not KeyEventArgs { Key: Key.Escape, IsDown: true } keyEventArgs)
        {
            return;
        }

        if (_state is not null && _contentPanThresholdExceeded)
        {
            YouTubeMiniPlayerContentTransformState.Set(
                _state,
                _state.ContentZoomFactor,
                _contentPanStartX,
                _contentPanStartY);
            _controller?.ApplyContentTransform();
            ZoomResetButton.Content = YouTubeMiniPlayerContentTransformState.FormatPercentage(
                _state.ContentZoomFactor);
            _contentPanChanged = false;
        }

        keyEventArgs.Handled = true;
        FinishContentPan();
    }

    private Point GetContentZoomAnchor(Point point)
    {
        var width = Math.Max(1, Surface.ActualWidth);
        var height = Math.Max(1, Surface.ActualHeight);
        var contentLeft = 0d;
        var contentTop = 0d;
        var contentWidth = width;
        var contentHeight = height;
        var aspectRatio = _state?.VideoAspectRatio;
        if (aspectRatio is { } ratio && double.IsFinite(ratio) && ratio > 0)
        {
            if (width / height > ratio)
            {
                contentWidth = height * ratio;
                contentLeft = (width - contentWidth) / 2;
            }
            else
            {
                contentHeight = width / ratio;
                contentTop = (height - contentHeight) / 2;
            }
        }

        return new Point(
            Math.Clamp(((point.X - contentLeft) / Math.Max(1, contentWidth) * 2) - 1, -1, 1),
            Math.Clamp(((point.Y - contentTop) / Math.Max(1, contentHeight) * 2) - 1, -1, 1));
    }

    private void ShowContentTransformHud(bool keepVisible = false)
    {
        if (_state is null)
        {
            return;
        }

        const double mapWidth = 70;
        const double mapHeight = 38;
        var zoomFactor = Math.Clamp(
            _state.ContentZoomFactor,
            YouTubeMiniPlayerContentTransformState.MinimumZoomFactor,
            YouTubeMiniPlayerContentTransformState.MaximumZoomFactor);
        var viewportWidth = mapWidth / zoomFactor;
        var viewportHeight = mapHeight / zoomFactor;
        ContentTransformHudText.Text = YouTubeMiniPlayerContentTransformState.FormatPercentage(zoomFactor);
        ContentTransformHudViewport.Width = viewportWidth;
        ContentTransformHudViewport.Height = viewportHeight;
        Canvas.SetLeft(
            ContentTransformHudViewport,
            (mapWidth - viewportWidth) * (1 - _state.ContentPanX) / 2);
        Canvas.SetTop(
            ContentTransformHudViewport,
            (mapHeight - viewportHeight) * (1 - _state.ContentPanY) / 2);
        ContentTransformHud.Visibility = Visibility.Visible;
        _contentTransformHudHideTimer.Stop();
        if (!keepVisible)
        {
            _contentTransformHudHideTimer.Start();
        }
    }

    private void ContentTransformHudHideTimer_Tick(object? sender, EventArgs e)
    {
        _contentTransformHudHideTimer.Stop();
        ContentTransformHud.Visibility = Visibility.Collapsed;
    }

    private void ShowContentPanEdgeFeedback()
    {
        ContentPanEdgeFeedback.Visibility = Visibility.Visible;
        _contentPanEdgeFeedbackTimer.Stop();
        _contentPanEdgeFeedbackTimer.Start();
    }

    private void ContentPanEdgeFeedbackTimer_Tick(object? sender, EventArgs e)
    {
        _contentPanEdgeFeedbackTimer.Stop();
        ContentPanEdgeFeedback.Visibility = Visibility.Collapsed;
    }

    private void KeepControlsVisible()
    {
        if (_controller?.IsLoaded != true)
        {
            return;
        }

        ShowControls();
        _controlsHideTimer.Start();
    }

    private void ShowControls()
    {
        ControlsPanel.Visibility = Visibility.Visible;
        _controlsHideTimer.Stop();
    }

    private void QueueControlsHide()
    {
        if (_controller?.IsLoaded == true
            && ControlsPanel.Visibility == Visibility.Visible
            && !_controlsHideTimer.IsEnabled)
        {
            _controlsHideTimer.Start();
        }
    }

    private void ControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        _controlsHideTimer.Stop();
        if (_controller?.IsLoaded == true
            && !AbRepeatMenu.IsOpen
            && !_isSeeking
            && !IsPointerInControlsHotZone()
            && !ControlsPanel.IsMouseOver)
        {
            ControlsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateControlsVisibility()
    {
        if (_controller?.IsLoaded != true)
        {
            _controlsHideTimer.Stop();
            ControlsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (AbRepeatMenu.IsOpen
            || _isSeeking
            || ControlsPanel.IsMouseOver
            || IsPointerInControlsHotZone())
        {
            ShowControls();
            return;
        }

        QueueControlsHide();
    }

    private bool IsPointerInControlsHotZone()
    {
        return Surface.IsMouseOver
            && YouTubeMiniPlayerInputService.IsToolbarHotZone(
                Mouse.GetPosition(Surface),
                Surface.ActualHeight);
    }

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedVideoPath(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedVideoPath(e.Data, out var filePath))
        {
            LocalFileDropped?.Invoke(filePath);
            e.Handled = true;
        }
    }

    private static bool TryGetDroppedVideoPath(IDataObject data, out string filePath)
    {
        filePath = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths)
        {
            return false;
        }

        var candidate = paths[0];
        if (!File.Exists(candidate) || !WorkspaceShellItemService.IsVideoPath(candidate))
        {
            return false;
        }

        filePath = Path.GetFullPath(candidate);
        return true;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private void UpdateAbRepeatDisplay()
    {
        if (_state is null || _controller is null)
        {
            return;
        }

        var hasStart = _state.AbRepeatStartSeconds is { } start;
        var hasRange = YouTubeMiniPlayerAbRepeatState.HasValidRange(_state);
        AbRepeatButton.Content = _state.IsAbRepeatEnabled
            ? "A-B中"
            : hasStart
                ? "A設定"
                : "A-B";
        AbRepeatStatusMenuItem.Header = hasRange
            ? $"A {FormatTime(_state.AbRepeatStartSeconds!.Value)} / B {FormatTime(_state.AbRepeatEndSeconds!.Value)}"
            : hasStart
                ? $"A {FormatTime(_state.AbRepeatStartSeconds!.Value)} / B 未設定"
                : "A-B未設定";
        AbRepeatSetStartMenuItem.IsEnabled = _controller.IsLoaded;
        AbRepeatSetEndMenuItem.IsEnabled = _controller.IsLoaded && hasStart;
        AbRepeatToggleMenuItem.IsEnabled = hasRange;
        AbRepeatToggleMenuItem.Header = _state.IsAbRepeatEnabled
            ? "A-B再生を一時解除"
            : "A-B再生を開始";
        AbRepeatClearMenuItem.IsEnabled = hasStart || _state.AbRepeatEndSeconds.HasValue;
    }

    private sealed record PlaybackRateOption(double Value, string Text);
}
