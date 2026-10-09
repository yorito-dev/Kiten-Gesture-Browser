using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed class YouTubeLiveChatWindow : Window
{
    private const double DockDetachDistance = 36;
    private const double DockReattachDistance = 48;
    private const double FullScreenOverlayOpacity = 0.85;
    private const double FullScreenOverlayHorizontalMargin = 20;
    private const double FullScreenOverlayTopMargin = 64;
    private const double FullScreenOverlayBottomMargin = 88;
    private const double FullScreenOverlayMaximumWidth = 480;
    private const double FullScreenOverlayMaximumWidthRatio = 0.35;
    private static readonly Brush WindowBackground = new SolidColorBrush(Color.FromRgb(12, 15, 21));
    private static readonly Brush PanelBackground = new SolidColorBrush(Color.FromRgb(19, 24, 33));
    private static readonly Brush ChromeBorderBrush = new SolidColorBrush(Color.FromRgb(52, 61, 75));

    private readonly YouTubeMiniPlayerState _state;
    private readonly CoreWebView2Environment _environment;
    private Uri _chatUri;
    private string _chatDisplayName;
    private bool _isReplayChat;
    private readonly Func<Rect> _getSourceWindowBounds;
    private readonly Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>> _getSourcePlaybackStateAsync;
    private readonly Action _queueSave;
    private readonly Grid _contentHost = new();
    private readonly Border _statusOverlay;
    private readonly TextBlock _statusText;
    private readonly Button _retryButton;
    private readonly Button _dockButton;
    private readonly Button _fullScreenModeButton;
    private readonly Button _topmostButton;
    private readonly Button _minimizeButton;
    private readonly List<Thumb> _resizeThumbs = [];
    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1200)
    };
    private readonly DispatcherTimer _replaySyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private WebView2? _webView;
    private CoreWebView2? _core;
    private EventHandler<CoreWebView2ProcessFailedEventArgs>? _processFailedHandler;
    private EventHandler<CoreWebView2NavigationCompletedEventArgs>? _navigationCompletedHandler;
    private string? _targetVideoId;
    private bool _isAwaitingAvailability;
    private long _loadGeneration;
    private bool _isClosed;
    private bool _isApplyingDock;
    private bool _isResizing;
    private bool _isSynchronizingReplay;
    private bool _isDockAttached;
    private bool _isDetachedDragActive;
    private bool _isSourceFullScreen;
    private bool _isFullScreenOverlay;
    private bool _isFullScreenIndependent;
    private bool _preferFullScreenIndependent;
    private bool _isApplyingFullScreenOverlay;
    private Rect _lastFullScreenSourceBounds;
    private bool _lastFullScreenSourceTopmost;
    private Point? _dockedTitleDragStart;
    private FullScreenOverlaySnapshot? _fullScreenOverlaySnapshot;
    private FullScreenIndependentSnapshot? _lastFullScreenIndependentSnapshot;

    public YouTubeLiveChatWindow(
        YouTubeMiniPlayerState state,
        CoreWebView2Environment environment,
        Uri chatUri,
        Style buttonStyle,
        ImageSource? icon,
        Func<Rect> getSourceWindowBounds,
        Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>> getSourcePlaybackStateAsync,
        Action queueSave)
    {
        _state = state;
        _environment = environment;
        _chatUri = chatUri;
        _isReplayChat = chatUri.AbsolutePath.Equals(
            "/live_chat_replay",
            StringComparison.OrdinalIgnoreCase);
        _chatDisplayName = _isReplayChat
            ? "チャットリプレイ"
            : "ライブチャット";
        YouTubeUrlService.TryGetVideoId(state.Url, out _targetVideoId);
        _getSourceWindowBounds = getSourceWindowBounds;
        _getSourcePlaybackStateAsync = getSourcePlaybackStateAsync;
        _queueSave = queueSave;
        _isDockAttached = state.IsLiveChatWindowDocked;

        Title = YouTubeMiniPlayerNavigationService.NormalizeTitle(state.Title, state.Url)
            + " - "
            + _chatDisplayName;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        MinWidth = YouTubeLiveChatWindowState.MinimumWidth;
        MinHeight = YouTubeLiveChatWindowState.MinimumHeight;
        Background = WindowBackground;
        Icon = icon;

        var screenBounds = GetVirtualScreenBounds();
        var bounds = CreateInitialBounds(state, getSourceWindowBounds(), screenBounds);
        if (state.IsLiveChatWindowDocked)
        {
            bounds = CreateDockedBounds(bounds.Width, bounds.Height, getSourceWindowBounds(), screenBounds);
        }

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        Topmost = state.IsLiveChatWindowTopmost ?? state.IsTopmost;
        state.IsLiveChatWindowTopmost = Topmost;
        CaptureBounds();

        WindowChrome.SetWindowChrome(
            this,
            new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        var windowRoot = new Grid
        {
            Background = WindowBackground
        };
        var layout = new Grid
        {
            Margin = new Thickness(8)
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = CreateTitleBar(
            buttonStyle,
            out _dockButton,
            out _fullScreenModeButton,
            out _topmostButton,
            out _minimizeButton);
        Grid.SetRow(titleBar, 0);
        layout.Children.Add(titleBar);

        _contentHost.Background = WindowBackground;
        _statusOverlay = CreateStatusOverlay(
            buttonStyle,
            _chatDisplayName,
            out _statusText,
            out _retryButton);
        _contentHost.Children.Add(_statusOverlay);
        Grid.SetRow(_contentHost, 1);
        layout.Children.Add(_contentHost);
        windowRoot.Children.Add(layout);
        AddResizeThumbs(windowRoot);
        Content = windowRoot;

        _dockButton.Click += (_, _) => ToggleDock();
        _fullScreenModeButton.Click += (_, _) => ToggleFullScreenPresentation();
        _topmostButton.Click += (_, _) => ToggleTopmost();
        _retryButton.Click += async (_, _) => await RecreateWebViewAsync();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _queueSave();
        };
        _replaySyncTimer.Tick += async (_, _) => await SynchronizeReplayPlaybackAsync();
        LocationChanged += (_, _) =>
        {
            if (_state.IsLiveChatWindowDocked
                && !_isApplyingDock
                && !_isResizing
                && !_isDetachedDragActive)
            {
                SynchronizeWithSourceWindow();
            }

            CaptureBoundsAndQueueSave();
        };
        SizeChanged += (_, _) =>
        {
            if (_state.IsLiveChatWindowDocked
                && !_isApplyingDock
                && !_isResizing
                && !_isDetachedDragActive)
            {
                SynchronizeWithSourceWindow();
            }

            CaptureBoundsAndQueueSave();
        };
        SourceInitialized += (_, _) => WindowTopmostService.Apply(this, Topmost);
        Closed += (_, _) => DisposeWindow();

        UpdateDockButton();
        UpdateTopmostButton();
        ShowLoading();
    }

    public async Task InitializeAsync()
    {
        await RecreateWebViewAsync();
    }

    public void BeginVideoTransition(string videoId)
    {
        if (_isClosed
            || string.Equals(_targetVideoId, videoId, StringComparison.Ordinal))
        {
            return;
        }

        _targetVideoId = videoId;
        _isAwaitingAvailability = true;
        _loadGeneration++;
        _replaySyncTimer.Stop();
        DisposeWebView();
        ShowStatus("次の動画のチャットを確認しています...");
    }

    public void ShowUnavailable(
        string expectedVideoId,
        string message = "この動画ではチャットを利用できません")
    {
        if (_isClosed
            || !_isAwaitingAvailability
            || !string.Equals(_targetVideoId, expectedVideoId, StringComparison.Ordinal))
        {
            return;
        }

        _loadGeneration++;
        _replaySyncTimer.Stop();
        DisposeWebView();
        Title = YouTubeMiniPlayerNavigationService.NormalizeTitle(_state.Title, _state.Url)
            + " - チャット";
        ShowStatus(message);
    }

    public async Task SwitchVideoAsync(string expectedVideoId, Uri chatUri)
    {
        if (_isClosed
            || !_isAwaitingAvailability
            || !string.Equals(_targetVideoId, expectedVideoId, StringComparison.Ordinal))
        {
            return;
        }

        var isReplayChat = chatUri.AbsolutePath.Equals(
            "/live_chat_replay",
            StringComparison.OrdinalIgnoreCase);
        var chatDisplayName = isReplayChat
            ? "チャットリプレイ"
            : "ライブチャット";
        Title = YouTubeMiniPlayerNavigationService.NormalizeTitle(_state.Title, _state.Url)
            + " - "
            + chatDisplayName;
        _chatUri = chatUri;
        _isReplayChat = isReplayChat;
        _chatDisplayName = chatDisplayName;
        _isAwaitingAvailability = false;
        await RecreateWebViewAsync();
    }

    public void BringToFront()
    {
        if (_isClosed)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowTopmostService.Apply(this, Topmost);
        Activate();
    }

    public void FollowSourceToFront()
    {
        if (_isClosed
            || !IsVisible
            || WindowState == WindowState.Minimized)
        {
            return;
        }

        WindowTopmostService.BringToFrontWithoutActivation(this);
    }

    public void EnterFullScreenOverlay(Rect sourceBounds, bool sourceTopmost)
    {
        if (_isClosed
            || !IsVisible
            || WindowState == WindowState.Minimized
            || !IsValidBounds(sourceBounds))
        {
            return;
        }

        if (_fullScreenOverlaySnapshot is null)
        {
            CaptureBounds();
            var restoreBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (!IsValidBounds(restoreBounds))
            {
                return;
            }

            _fullScreenOverlaySnapshot = new FullScreenOverlaySnapshot(
                restoreBounds,
                WindowState,
                Opacity,
                Topmost,
                ResizeMode,
                _isDockAttached);
        }

        _isSourceFullScreen = true;
        _lastFullScreenSourceBounds = sourceBounds;
        _lastFullScreenSourceTopmost = sourceTopmost;
        if (_preferFullScreenIndependent)
        {
            ShowAsIndependentWindow();
            return;
        }

        ShowAsFullScreenOverlay();
    }

    public void UpdateFullScreenSourceBounds(Rect sourceBounds, bool sourceTopmost)
    {
        if (_isClosed || !_isSourceFullScreen || !IsValidBounds(sourceBounds))
        {
            return;
        }

        _lastFullScreenSourceBounds = sourceBounds;
        _lastFullScreenSourceTopmost = sourceTopmost;
        if (_isFullScreenOverlay)
        {
            ApplyFullScreenOverlay();
        }
    }

    private void ToggleFullScreenPresentation()
    {
        if (!_isSourceFullScreen || _fullScreenOverlaySnapshot is null)
        {
            return;
        }

        if (_isFullScreenOverlay)
        {
            _preferFullScreenIndependent = true;
            ShowAsIndependentWindow();
        }
        else
        {
            _preferFullScreenIndependent = false;
            ShowAsFullScreenOverlay();
        }
    }

    private void ShowAsIndependentWindow()
    {
        if (_fullScreenOverlaySnapshot is not { } snapshot)
        {
            return;
        }

        var independentSnapshot = _lastFullScreenIndependentSnapshot;
        var bounds = RestoreFullScreenIndependentBounds(
            independentSnapshot?.Bounds,
            snapshot.Bounds,
            GetVirtualScreenBounds());
        var windowState = independentSnapshot?.WindowState ?? snapshot.WindowState;
        if (windowState == WindowState.Minimized)
        {
            windowState = WindowState.Normal;
        }

        _isApplyingFullScreenOverlay = true;
        try
        {
            _isFullScreenOverlay = false;
            _isFullScreenIndependent = true;
            WindowState = WindowState.Normal;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            Opacity = independentSnapshot?.Opacity ?? snapshot.Opacity;
            ResizeMode = independentSnapshot?.ResizeMode ?? snapshot.ResizeMode;
            SetFullScreenOverlayControlsEnabled(true);
            SetResizeThumbsVisible(true);
            WindowTopmostService.Apply(this, independentSnapshot?.Topmost ?? snapshot.Topmost);
            WindowState = windowState;
            UpdateTopmostButton();
            UpdateFullScreenModeButton();
        }
        finally
        {
            _isApplyingFullScreenOverlay = false;
        }
    }

    private void ShowAsFullScreenOverlay()
    {
        if (_fullScreenOverlaySnapshot is not { } snapshot
            || !IsValidBounds(_lastFullScreenSourceBounds))
        {
            return;
        }

        if (_isFullScreenIndependent)
        {
            CaptureFullScreenIndependentPlacement();
            CaptureBounds();
            var restoreBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (IsValidBounds(restoreBounds))
            {
                snapshot = snapshot with
                {
                    Bounds = restoreBounds,
                    WindowState = WindowState,
                    Opacity = Opacity,
                    Topmost = Topmost,
                    ResizeMode = ResizeMode
                };
                _fullScreenOverlaySnapshot = snapshot;
            }
        }

        _isFullScreenIndependent = false;
        _isFullScreenOverlay = true;
        UpdateFullScreenModeButton();
        ApplyFullScreenOverlay();
    }

    private void ApplyFullScreenOverlay()
    {
        if (_fullScreenOverlaySnapshot is not { } snapshot)
        {
            return;
        }

        var overlayBounds = CreateFullScreenOverlayBounds(
            _lastFullScreenSourceBounds,
            snapshot.Bounds.Width);
        if (overlayBounds.IsEmpty)
        {
            return;
        }

        _isApplyingFullScreenOverlay = true;
        try
        {
            WindowState = WindowState.Normal;
            Left = overlayBounds.Left;
            Top = overlayBounds.Top;
            Width = overlayBounds.Width;
            Height = overlayBounds.Height;
            Opacity = FullScreenOverlayOpacity;
            ResizeMode = ResizeMode.NoResize;
            SetFullScreenOverlayControlsEnabled(false);
            SetResizeThumbsVisible(false);
            WindowTopmostService.Apply(this, _lastFullScreenSourceTopmost || snapshot.Topmost);
            WindowTopmostService.BringToFrontWithoutActivation(this);
        }
        finally
        {
            _isApplyingFullScreenOverlay = false;
        }
    }

    public void ExitFullScreenOverlay()
    {
        if (_isClosed || _fullScreenOverlaySnapshot is not { } snapshot)
        {
            return;
        }

        if (_isFullScreenIndependent)
        {
            CaptureFullScreenIndependentPlacement();
        }

        _isApplyingFullScreenOverlay = true;
        try
        {
            if (_isFullScreenOverlay)
            {
                WindowState = WindowState.Normal;
                Left = snapshot.Bounds.Left;
                Top = snapshot.Bounds.Top;
                Width = snapshot.Bounds.Width;
                Height = snapshot.Bounds.Height;
                Opacity = snapshot.Opacity;
                ResizeMode = snapshot.ResizeMode;
                WindowTopmostService.Apply(this, snapshot.Topmost);
                WindowState = snapshot.WindowState;
            }

            _isDockAttached = snapshot.IsDockAttached;
            _isSourceFullScreen = false;
            _isFullScreenOverlay = false;
            _isFullScreenIndependent = false;
            SetFullScreenOverlayControlsEnabled(true);
            SetResizeThumbsVisible(true);
            UpdateTopmostButton();
            UpdateFullScreenModeButton();
        }
        finally
        {
            _fullScreenOverlaySnapshot = null;
            _isApplyingFullScreenOverlay = false;
        }

        if (_state.IsLiveChatWindowDocked)
        {
            SynchronizeWithSourceWindow();
        }
    }

    public void SynchronizeWithSourceWindow()
    {
        if (_isClosed
            || _isSourceFullScreen
            || !_state.IsLiveChatWindowDocked
            || WindowState == WindowState.Minimized
            || _isApplyingDock
            || _isDetachedDragActive)
        {
            return;
        }

        if (!_isDockAttached)
        {
            TryReattachToSourceWindow();
            return;
        }

        var bounds = CreateDockedBounds(
            Math.Max(YouTubeLiveChatWindowState.MinimumWidth, ActualWidth > 0 ? ActualWidth : Width),
            Math.Max(YouTubeLiveChatWindowState.MinimumHeight, ActualHeight > 0 ? ActualHeight : Height),
            _getSourceWindowBounds(),
            GetVirtualScreenBounds());
        if (Math.Abs(Left - bounds.Left) < 0.5 && Math.Abs(Top - bounds.Top) < 0.5)
        {
            return;
        }

        _isApplyingDock = true;
        try
        {
            Left = bounds.Left;
            Top = bounds.Top;
            CaptureBounds();
        }
        finally
        {
            _isApplyingDock = false;
        }
    }

    public void ApplyArrangement(
        Rect bounds,
        bool isDocked,
        YouTubeLiveChatDockSide side)
    {
        if (_isClosed)
        {
            return;
        }

        _isApplyingDock = true;
        try
        {
            _state.IsLiveChatWindowDocked = isDocked;
            _state.LiveChatWindowDockSide = side;
            _isDockAttached = isDocked;
            Width = Math.Max(YouTubeLiveChatWindowState.MinimumWidth, bounds.Width);
            Height = Math.Max(YouTubeLiveChatWindowState.MinimumHeight, bounds.Height);
            if (!isDocked)
            {
                Left = bounds.Left;
                Top = bounds.Top;
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            UpdateDockButton();
        }
        finally
        {
            _isApplyingDock = false;
        }

        if (isDocked)
        {
            SynchronizeWithSourceWindow();
        }

        CaptureBounds();
    }

    private Border CreateTitleBar(
        Style buttonStyle,
        out Button dockButton,
        out Button fullScreenModeButton,
        out Button topmostButton,
        out Button minimizeButton)
    {
        var titleBar = new Border
        {
            Background = PanelBackground,
            BorderBrush = ChromeBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 3, 4, 3)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = Title,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 246, 250)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        grid.Children.Add(titleText);

        dockButton = CreateButton("吸着", "YouTubeミニプレイヤーへ吸着", 68, buttonStyle);
        Grid.SetColumn(dockButton, 1);
        grid.Children.Add(dockButton);

        fullScreenModeButton = CreateButton(
            "自由配置",
            "チャットを自由に移動できる状態へ切り替え",
            104,
            buttonStyle);
        fullScreenModeButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(fullScreenModeButton, 1);
        grid.Children.Add(fullScreenModeButton);

        topmostButton = CreateButton("最前面", "常に最前面に表示", 68, buttonStyle);
        Grid.SetColumn(topmostButton, 2);
        grid.Children.Add(topmostButton);

        minimizeButton = CreateButton("最小化", "タスクバーへ最小化", 54, buttonStyle);
        minimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        Grid.SetColumn(minimizeButton, 3);
        grid.Children.Add(minimizeButton);

        var closeButton = CreateButton("×", "チャットを閉じる", 26, buttonStyle);
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 4);
        grid.Children.Add(closeButton);

        titleBar.Child = grid;
        WindowChrome.SetIsHitTestVisibleInChrome(titleBar, true);
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (_isFullScreenOverlay)
            {
                return;
            }

            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            if (_isSourceFullScreen && _isFullScreenIndependent)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    return;
                }

                DragMove();
                return;
            }

            if (_state.IsLiveChatWindowDocked)
            {
                if (!_isDockAttached)
                {
                    DragDetachedWindow();
                    return;
                }

                if (e.ClickCount == 1)
                {
                    _dockedTitleDragStart = e.GetPosition(titleBar);
                    titleBar.CaptureMouse();
                }

                return;
            }

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                return;
            }

            DragMove();
        };
        titleBar.MouseMove += (_, e) =>
        {
            if (!_dockedTitleDragStart.HasValue
                || !_state.IsLiveChatWindowDocked
                || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var current = e.GetPosition(titleBar);
            var delta = current - _dockedTitleDragStart.Value;
            if (Math.Abs(delta.X) < DockDetachDistance
                && Math.Abs(delta.Y) < DockDetachDistance)
            {
                return;
            }

            _dockedTitleDragStart = null;
            if (titleBar.IsMouseCaptured)
            {
                titleBar.ReleaseMouseCapture();
            }

            _isDockAttached = false;
            UpdateDockButton();
            DragDetachedWindow();
        };
        titleBar.MouseLeftButtonUp += (_, _) =>
        {
            _dockedTitleDragStart = null;
            if (titleBar.IsMouseCaptured)
            {
                titleBar.ReleaseMouseCapture();
            }
        };
        titleBar.LostMouseCapture += (_, _) => _dockedTitleDragStart = null;
        return titleBar;
    }

    private void DragDetachedWindow()
    {
        _isDetachedDragActive = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            CaptureBoundsAndQueueSave();
        }
        finally
        {
            _isDetachedDragActive = false;
            if (!TryReattachToSourceWindow())
            {
                CaptureBoundsAndQueueSave();
            }
        }
    }

    private void AddResizeThumbs(Grid windowRoot)
    {
        AddResizeThumb(windowRoot, HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE, true, false, false, false);
        AddResizeThumb(windowRoot, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, Cursors.SizeWE, false, false, true, false);
        AddResizeThumb(windowRoot, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 8, Cursors.SizeNS, false, true, false, false);
        AddResizeThumb(windowRoot, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, Cursors.SizeNS, false, false, false, true);
        AddResizeThumb(windowRoot, HorizontalAlignment.Left, VerticalAlignment.Top, 16, 16, Cursors.SizeNWSE, true, true, false, false);
        AddResizeThumb(windowRoot, HorizontalAlignment.Right, VerticalAlignment.Top, 16, 16, Cursors.SizeNESW, false, true, true, false);
        AddResizeThumb(windowRoot, HorizontalAlignment.Left, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNESW, true, false, false, true);
        AddResizeThumb(windowRoot, HorizontalAlignment.Right, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNWSE, false, false, true, true);
    }

    private void AddResizeThumb(
        Grid windowRoot,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        double width,
        double height,
        Cursor cursor,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        var resizeThumb = YouTubeMiniPlayerChromeFactory.CreateResizeThumb(
            horizontalAlignment,
            verticalAlignment,
            width,
            height,
            cursor);
        resizeThumb.DragStarted += (_, _) =>
        {
            _isResizing = WindowState == WindowState.Normal;
        };
        resizeThumb.DragDelta += (_, e) =>
        {
            if (_isResizing)
            {
                ResizeWindow(e, resizeLeft, resizeTop, resizeRight, resizeBottom);
            }
        };
        resizeThumb.DragCompleted += (_, _) =>
        {
            if (!_isResizing)
            {
                return;
            }

            _isResizing = false;
            if (_state.IsLiveChatWindowDocked)
            {
                SynchronizeWithSourceWindow();
            }

            CaptureBoundsAndQueueSave();
        };
        Panel.SetZIndex(resizeThumb, 100);
        _resizeThumbs.Add(resizeThumb);
        windowRoot.Children.Add(resizeThumb);
    }

    private void ResizeWindow(
        DragDeltaEventArgs e,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        var currentWidth = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
        var currentHeight = Math.Max(MinHeight, ActualHeight > 0 ? ActualHeight : Height);
        var nextLeft = Left;
        var nextTop = Top;
        var nextWidth = currentWidth;
        var nextHeight = currentHeight;

        if (resizeLeft)
        {
            nextWidth = Math.Max(MinWidth, currentWidth - e.HorizontalChange);
            nextLeft += currentWidth - nextWidth;
        }
        else if (resizeRight)
        {
            nextWidth = Math.Max(MinWidth, currentWidth + e.HorizontalChange);
        }

        if (resizeTop)
        {
            nextHeight = Math.Max(MinHeight, currentHeight - e.VerticalChange);
            nextTop += currentHeight - nextHeight;
        }
        else if (resizeBottom)
        {
            nextHeight = Math.Max(MinHeight, currentHeight + e.VerticalChange);
        }

        if (_isDockAttached && !_isFullScreenIndependent)
        {
            var dockedBounds = CreateDockedBounds(
                nextWidth,
                nextHeight,
                _getSourceWindowBounds(),
                GetVirtualScreenBounds());
            nextLeft = dockedBounds.Left;
            nextTop = dockedBounds.Top;
        }

        Left = nextLeft;
        Top = nextTop;
        Width = nextWidth;
        Height = nextHeight;
    }

    private static Border CreateStatusOverlay(
        Style buttonStyle,
        string chatDisplayName,
        out TextBlock statusText,
        out Button retryButton)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 300
        };
        statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(211, 218, 230)),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        };
        panel.Children.Add(statusText);

        retryButton = CreateButton(
            "WebView2を再生成",
            chatDisplayName + "表示を再試行",
            128,
            buttonStyle);
        retryButton.HorizontalAlignment = HorizontalAlignment.Center;
        retryButton.Visibility = Visibility.Collapsed;
        panel.Children.Add(retryButton);

        return new Border
        {
            Background = WindowBackground,
            Child = panel
        };
    }

    private static Button CreateButton(string content, string toolTip, double width, Style buttonStyle)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = toolTip,
            Width = width,
            Height = 23,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            FontSize = 11,
            Style = buttonStyle
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private async Task RecreateWebViewAsync()
    {
        if (_isClosed || string.IsNullOrWhiteSpace(_targetVideoId))
        {
            return;
        }

        var expectedVideoId = _targetVideoId;
        var chatUri = _chatUri;
        var chatDisplayName = _chatDisplayName;
        var isReplayChat = _isReplayChat;
        var generation = ++_loadGeneration;
        _replaySyncTimer.Stop();
        ShowLoading();
        DisposeWebView();

        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Hidden,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 15, 15)
        };
        _webView = webView;
        _contentHost.Children.Insert(0, webView);

        try
        {
            await webView.EnsureCoreWebView2Async(_environment);
            if (!IsCurrentLoad(generation, webView, expectedVideoId))
            {
                return;
            }

            var core = webView.CoreWebView2;
            WebViewLifecycleService.ApplyDefaultCoreSettings(core);
            await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                core,
                "youtube-live-chat-dark-theme",
                YouTubeMiniPlayerWebViewService.BuildLiveChatDarkThemeScript());
            if (!IsCurrentLoad(generation, webView, expectedVideoId))
            {
                return;
            }

            if (!isReplayChat)
            {
                await CoreWebViewDocumentScriptRegistry.EnsureFixedAsync(
                    core,
                    "youtube-live-chat-auto-follow",
                    YouTubeMiniPlayerWebViewService.BuildLiveChatAutoFollowScript());
                if (!IsCurrentLoad(generation, webView, expectedVideoId))
                {
                    return;
                }
            }

            _core = core;
            _processFailedHandler = (_, _) => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (IsCurrentLoad(generation, webView, expectedVideoId))
                {
                    _replaySyncTimer.Stop();
                    ShowFailure("WebView2の描画プロセスに問題が発生しました。再生成してください。");
                }
            }));
            _navigationCompletedHandler = (_, args) =>
            {
                if (!IsCurrentLoad(generation, webView, expectedVideoId))
                {
                    return;
                }

                if (args.IsSuccess)
                {
                    webView.Visibility = Visibility.Visible;
                    _statusOverlay.Visibility = Visibility.Collapsed;
                    if (isReplayChat)
                    {
                        _replaySyncTimer.Start();
                        _ = SynchronizeReplayPlaybackAsync();
                    }
                }
                else
                {
                    _replaySyncTimer.Stop();
                    ShowFailure(chatDisplayName + "を読み込めませんでした。再生成してください。");
                }
            };
            core.ProcessFailed += _processFailedHandler;
            webView.NavigationCompleted += _navigationCompletedHandler;
            YouTubeMiniPlayerWebViewService.NavigateWithYouTubeReferer(core, chatUri);
        }
        catch
        {
            if (IsCurrentLoad(generation, webView, expectedVideoId))
            {
                ShowFailure(chatDisplayName + "用WebView2を初期化できませんでした。再生成してください。");
            }
        }
    }

    private async Task SynchronizeReplayPlaybackAsync()
    {
        if (!_isReplayChat
            || _isClosed
            || _isSynchronizingReplay
            || _core is not { } core)
        {
            return;
        }

        _isSynchronizingReplay = true;
        try
        {
            var playbackState = await _getSourcePlaybackStateAsync();
            if (_isClosed
                || !ReferenceEquals(_core, core)
                || playbackState?.LastPositionSeconds is not { } positionSeconds)
            {
                return;
            }

            await core.ExecuteScriptAsync(
                YouTubeMiniPlayerWebViewService.BuildLiveChatReplaySyncScript(
                    positionSeconds,
                    playbackState.Value.IsPaused == true));
        }
        catch
        {
        }
        finally
        {
            _isSynchronizingReplay = false;
        }
    }

    private bool IsCurrentLoad(long generation, WebView2 webView, string expectedVideoId)
    {
        return !_isClosed
            && generation == _loadGeneration
            && string.Equals(_targetVideoId, expectedVideoId, StringComparison.Ordinal)
            && ReferenceEquals(_webView, webView);
    }

    private void ShowLoading()
    {
        _statusText.Text = _chatDisplayName + "を読み込んでいます...";
        _retryButton.Visibility = Visibility.Collapsed;
        _statusOverlay.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string message)
    {
        _statusText.Text = message;
        _retryButton.Visibility = Visibility.Collapsed;
        _statusOverlay.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string message)
    {
        if (_webView is { } webView)
        {
            webView.Visibility = Visibility.Hidden;
        }

        _statusText.Text = message;
        _retryButton.Visibility = Visibility.Visible;
        _statusOverlay.Visibility = Visibility.Visible;
    }

    private void ToggleTopmost()
    {
        if (_isFullScreenOverlay)
        {
            return;
        }

        var isTopmost = !Topmost;
        WindowTopmostService.Apply(this, isTopmost);
        _state.IsLiveChatWindowTopmost = isTopmost;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        CaptureFullScreenIndependentPlacement();
        UpdateTopmostButton();
        _queueSave();
    }

    private void ToggleDock()
    {
        if (_isSourceFullScreen)
        {
            return;
        }

        SetDocked(!_state.IsLiveChatWindowDocked);
    }

    private void SetDocked(bool isDocked)
    {
        if (_state.IsLiveChatWindowDocked == isDocked
            && _isDockAttached == isDocked)
        {
            return;
        }

        if (isDocked)
        {
            var sourceBounds = _getSourceWindowBounds();
            var chatCenter = Left + (ActualWidth > 0 ? ActualWidth : Width) / 2;
            var sourceCenter = sourceBounds.Left + sourceBounds.Width / 2;
            _state.LiveChatWindowDockSide = chatCenter < sourceCenter
                ? YouTubeLiveChatDockSide.Left
                : YouTubeLiveChatDockSide.Right;
        }

        _state.IsLiveChatWindowDocked = isDocked;
        _isDockAttached = isDocked;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        UpdateDockButton();
        if (isDocked)
        {
            SynchronizeWithSourceWindow();
        }

        _queueSave();
    }

    private bool TryReattachToSourceWindow()
    {
        if (_isClosed
            || _isSourceFullScreen
            || !_state.IsLiveChatWindowDocked
            || _isDockAttached
            || _isDetachedDragActive
            || WindowState != WindowState.Normal)
        {
            return false;
        }

        var width = Math.Max(YouTubeLiveChatWindowState.MinimumWidth, ActualWidth > 0 ? ActualWidth : Width);
        var height = Math.Max(YouTubeLiveChatWindowState.MinimumHeight, ActualHeight > 0 ? ActualHeight : Height);
        var sourceBounds = _getSourceWindowBounds();
        var currentBounds = new Rect(Left, Top, width, height);
        var leftBounds = CreateDockedBoundsForSide(
            YouTubeLiveChatDockSide.Left,
            width,
            height,
            sourceBounds);
        var rightBounds = CreateDockedBoundsForSide(
            YouTubeLiveChatDockSide.Right,
            width,
            height,
            sourceBounds);
        var leftDistance = GetDockDistance(currentBounds, leftBounds);
        var rightDistance = GetDockDistance(currentBounds, rightBounds);
        var nearestDistance = Math.Min(leftDistance, rightDistance);
        if (nearestDistance > DockReattachDistance)
        {
            return false;
        }

        _state.LiveChatWindowDockSide = leftDistance <= rightDistance
            ? YouTubeLiveChatDockSide.Left
            : YouTubeLiveChatDockSide.Right;
        _isDockAttached = true;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        UpdateDockButton();
        SynchronizeWithSourceWindow();
        _queueSave();
        return true;
    }

    private static double GetDockDistance(Rect currentBounds, Rect dockedBounds)
    {
        return Math.Max(
            Math.Abs(currentBounds.Left - dockedBounds.Left),
            Math.Abs(currentBounds.Top - dockedBounds.Top));
    }

    private void UpdateDockButton()
    {
        if (!_state.IsLiveChatWindowDocked)
        {
            _dockButton.Content = "吸着";
            _dockButton.ToolTip = "YouTubeミニプレイヤーへ吸着";
            return;
        }

        _dockButton.Content = _isDockAttached ? "吸着解除" : "吸着待機";
        _dockButton.ToolTip = _isDockAttached
            ? "YouTubeミニプレイヤーとの吸着を解除"
            : "本体へ近づけると再吸着。クリックで吸着設定を解除";
    }

    private void UpdateTopmostButton()
    {
        _topmostButton.Content = Topmost ? "最前面解除" : "最前面";
        _topmostButton.ToolTip = Topmost ? "常に最前面を解除" : "常に最前面に表示";
    }

    private void UpdateFullScreenModeButton()
    {
        _dockButton.Visibility = _isSourceFullScreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        _fullScreenModeButton.Visibility = _isSourceFullScreen
            ? Visibility.Visible
            : Visibility.Collapsed;
        _fullScreenModeButton.Content = _isFullScreenOverlay
            ? "自由配置"
            : "動画に固定";
        _fullScreenModeButton.ToolTip = _isFullScreenOverlay
            ? "チャットを自由に移動・リサイズできる状態へ切り替え"
            : "チャットを全画面動画へ自動配置して固定";
    }

    private void CaptureBoundsAndQueueSave()
    {
        if (_isClosed
            || _isFullScreenOverlay
            || _isApplyingFullScreenOverlay
            || WindowState == WindowState.Minimized)
        {
            return;
        }

        CaptureFullScreenIndependentPlacement();
        CaptureBounds();
        if (_isApplyingDock)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void CaptureBounds()
    {
        if (_isFullScreenOverlay || _isApplyingFullScreenOverlay)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.IsEmpty
            || !double.IsFinite(bounds.Left)
            || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width)
            || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return;
        }

        _state.LiveChatWindowLeft = bounds.Left;
        _state.LiveChatWindowTop = bounds.Top;
        _state.LiveChatWindowWidth = Math.Max(YouTubeLiveChatWindowState.MinimumWidth, bounds.Width);
        _state.LiveChatWindowHeight = Math.Max(YouTubeLiveChatWindowState.MinimumHeight, bounds.Height);
        _state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void CaptureFullScreenIndependentPlacement()
    {
        if (!_isSourceFullScreen
            || !_isFullScreenIndependent
            || _isApplyingFullScreenOverlay
            || WindowState == WindowState.Minimized)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (!IsValidBounds(bounds))
        {
            return;
        }

        _lastFullScreenIndependentSnapshot = new FullScreenIndependentSnapshot(
            bounds,
            WindowState,
            Opacity,
            Topmost,
            ResizeMode);
    }

    private void DisposeWindow()
    {
        if (_isClosed)
        {
            return;
        }

        CaptureBounds();
        _state.IsLiveChatWindowTopmost = _isFullScreenOverlay
            ? _fullScreenOverlaySnapshot?.Topmost ?? Topmost
            : Topmost;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        _isClosed = true;
        _loadGeneration++;
        _saveTimer.Stop();
        _replaySyncTimer.Stop();
        DisposeWebView();
        _queueSave();
    }

    private void DisposeWebView()
    {
        var webView = _webView;
        var core = _core ?? webView?.CoreWebView2;
        if (core is not null && _processFailedHandler is not null)
        {
            core.ProcessFailed -= _processFailedHandler;
        }

        if (webView is not null && _navigationCompletedHandler is not null)
        {
            webView.NavigationCompleted -= _navigationCompletedHandler;
        }

        _processFailedHandler = null;
        _navigationCompletedHandler = null;
        _core = null;
        _webView = null;
        if (webView is not null)
        {
            if (core is not null)
            {
                CoreWebViewDocumentScriptRegistry.Clear(core);
            }

            _contentHost.Children.Remove(webView);
            webView.Dispose();
        }
    }

    private static Rect CreateInitialBounds(
        YouTubeMiniPlayerState state,
        Rect sourceBounds,
        Rect screenBounds)
    {
        var width = state.LiveChatWindowWidth ?? YouTubeLiveChatWindowState.DefaultWidth;
        var height = state.LiveChatWindowHeight ?? YouTubeLiveChatWindowState.DefaultHeight;
        width = Math.Clamp(width, YouTubeLiveChatWindowState.MinimumWidth, Math.Max(YouTubeLiveChatWindowState.MinimumWidth, screenBounds.Width));
        height = Math.Clamp(height, YouTubeLiveChatWindowState.MinimumHeight, Math.Max(YouTubeLiveChatWindowState.MinimumHeight, screenBounds.Height));

        var left = state.LiveChatWindowLeft ?? sourceBounds.Right + 12;
        var top = state.LiveChatWindowTop ?? sourceBounds.Top;
        if (!state.LiveChatWindowLeft.HasValue
            && left + width > screenBounds.Right
            && sourceBounds.Left - width - 12 >= screenBounds.Left)
        {
            left = sourceBounds.Left - width - 12;
        }

        const double minimumVisible = 80;
        left = Math.Clamp(left, screenBounds.Left - width + minimumVisible, screenBounds.Right - minimumVisible);
        top = Math.Clamp(top, screenBounds.Top, screenBounds.Bottom - minimumVisible);
        return new Rect(left, top, width, height);
    }

    internal static Rect CreateFullScreenOverlayBounds(
        Rect sourceBounds,
        double preferredWidth)
    {
        if (!IsValidBounds(sourceBounds))
        {
            return Rect.Empty;
        }

        var availableWidth = Math.Max(1, sourceBounds.Width - (FullScreenOverlayHorizontalMargin * 2));
        var minimumWidth = Math.Min(YouTubeLiveChatWindowState.MinimumWidth, availableWidth);
        var maximumWidth = Math.Min(
            availableWidth,
            Math.Max(
                minimumWidth,
                Math.Min(
                    FullScreenOverlayMaximumWidth,
                    sourceBounds.Width * FullScreenOverlayMaximumWidthRatio)));
        var width = Math.Clamp(
            double.IsFinite(preferredWidth) && preferredWidth > 0
                ? preferredWidth
                : YouTubeLiveChatWindowState.DefaultWidth,
            minimumWidth,
            maximumWidth);
        var availableHeight = Math.Max(
            1,
            sourceBounds.Height - FullScreenOverlayTopMargin - FullScreenOverlayBottomMargin);
        var left = sourceBounds.Right - FullScreenOverlayHorizontalMargin - width;
        var top = sourceBounds.Top + FullScreenOverlayTopMargin;
        return new Rect(left, top, width, availableHeight);
    }

    internal static Rect RestoreFullScreenIndependentBounds(
        Rect? savedBounds,
        Rect fallbackBounds,
        Rect screenBounds)
    {
        var bounds = savedBounds is { } saved && IsValidBounds(saved)
            ? saved
            : fallbackBounds;
        if (!IsValidBounds(bounds) || !IsValidBounds(screenBounds))
        {
            return fallbackBounds;
        }

        var width = Math.Clamp(
            bounds.Width,
            Math.Min(YouTubeLiveChatWindowState.MinimumWidth, screenBounds.Width),
            screenBounds.Width);
        var height = Math.Clamp(
            bounds.Height,
            Math.Min(YouTubeLiveChatWindowState.MinimumHeight, screenBounds.Height),
            screenBounds.Height);
        const double minimumVisible = 80;
        var left = Math.Clamp(
            bounds.Left,
            screenBounds.Left - width + minimumVisible,
            screenBounds.Right - minimumVisible);
        var top = Math.Clamp(
            bounds.Top,
            screenBounds.Top,
            screenBounds.Bottom - minimumVisible);
        return new Rect(left, top, width, height);
    }

    private void SetFullScreenOverlayControlsEnabled(bool isEnabled)
    {
        _dockButton.IsEnabled = isEnabled;
        _topmostButton.IsEnabled = isEnabled;
        _minimizeButton.IsEnabled = isEnabled;
    }

    private void SetResizeThumbsVisible(bool isVisible)
    {
        var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var resizeThumb in _resizeThumbs)
        {
            resizeThumb.Visibility = visibility;
            resizeThumb.IsHitTestVisible = isVisible;
        }
    }

    private static bool IsValidBounds(Rect bounds)
    {
        return !bounds.IsEmpty
            && double.IsFinite(bounds.Left)
            && double.IsFinite(bounds.Top)
            && double.IsFinite(bounds.Width)
            && double.IsFinite(bounds.Height)
            && bounds.Width > 0
            && bounds.Height > 0;
    }

    private Rect CreateDockedBounds(
        double width,
        double height,
        Rect sourceBounds,
        Rect screenBounds)
    {
        var preferredSide = _state.LiveChatWindowDockSide;
        var preferred = CreateDockedBoundsForSide(
            preferredSide,
            width,
            height,
            sourceBounds);
        var alternateSide = preferredSide == YouTubeLiveChatDockSide.Right
            ? YouTubeLiveChatDockSide.Left
            : YouTubeLiveChatDockSide.Right;
        var alternate = CreateDockedBoundsForSide(
            alternateSide,
            width,
            height,
            sourceBounds);
        if (GetVisibleWidth(preferred, screenBounds) < Math.Min(80, width)
            && GetVisibleWidth(alternate, screenBounds) > GetVisibleWidth(preferred, screenBounds))
        {
            _state.LiveChatWindowDockSide = alternateSide;
            return alternate;
        }

        return preferred;
    }

    private static Rect CreateDockedBoundsForSide(
        YouTubeLiveChatDockSide side,
        double width,
        double height,
        Rect sourceBounds)
    {
        const double gap = 8;
        var left = side == YouTubeLiveChatDockSide.Right
            ? sourceBounds.Right + gap
            : sourceBounds.Left - width - gap;
        return new Rect(left, sourceBounds.Top, width, height);
    }

    private static double GetVisibleWidth(Rect bounds, Rect screenBounds)
    {
        return Math.Max(0, Math.Min(bounds.Right, screenBounds.Right) - Math.Max(bounds.Left, screenBounds.Left));
    }

    private static Rect GetVirtualScreenBounds()
    {
        var bounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return bounds.Width > 0 && bounds.Height > 0
            ? bounds
            : SystemParameters.WorkArea;
    }

    private readonly record struct FullScreenOverlaySnapshot(
        Rect Bounds,
        WindowState WindowState,
        double Opacity,
        bool Topmost,
        ResizeMode ResizeMode,
        bool IsDockAttached);

    private readonly record struct FullScreenIndependentSnapshot(
        Rect Bounds,
        WindowState WindowState,
        double Opacity,
        bool Topmost,
        ResizeMode ResizeMode);
}
