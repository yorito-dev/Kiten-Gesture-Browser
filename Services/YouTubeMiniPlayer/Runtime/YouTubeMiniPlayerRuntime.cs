using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerWebDragState
{
    public DispatcherTimer? FollowTimer { get; set; }
    public bool IsDragging { get; set; }
    public double StartLeft { get; set; }
    public double StartTop { get; set; }
    public double StartWidth { get; set; }
    public double StartHeight { get; set; }
    public Point StartScreenPoint { get; set; }
    public MiniPlayerResizeEdges ResizeEdges { get; set; }
    public bool IsCancelled { get; set; }
    public Action? CancelCtrlResize { get; set; }
}

internal sealed class YouTubeMiniPlayerRuntime(
    YouTubeMiniPlayerState state,
    Popup popup,
    FrameworkElement root,
    Border listHighlightBorder,
    Border audioStatusBadge,
    TextBlock audioStatusText,
    Window taskbarWindow,
    TextBlock taskbarTitleText,
    Popup titleBandPopup,
    FrameworkElement titleBand,
    TextBlock titleBandTitleText,
    Button titleBandBackButton,
    Button titleBandForwardButton,
    Button titleBandLiveViewerCountButton,
    TextBlock titleBandLiveViewerCountText,
    Button titleBandChatButton,
    Button titleBandTopmostButton,
    Button titleBandMinimizeButton,
    Button titleBandSourceButton,
    Button titleBandCloseButton,
    Popup toolbarPopup,
    FrameworkElement toolbar,
    Button toolbarZoomResetButton,
    Grid webHost,
    WebView2? webView,
    LocalVideoPlayerView? localVideoView)
{
    public YouTubeMiniPlayerState State { get; } = state;

    public Popup Popup { get; } = popup;

    public FrameworkElement Root { get; } = root;

    public Border ListHighlightBorder { get; } = listHighlightBorder;

    public bool IsListHighlightRequested { get; set; }

    public Border AudioStatusBadge { get; } = audioStatusBadge;

    public TextBlock AudioStatusText { get; } = audioStatusText;

    public Window TaskbarWindow { get; } = taskbarWindow;
    public long LastActivatedTimestamp { get; set; }

    public TextBlock TaskbarTitleText { get; } = taskbarTitleText;

    public Popup TitleBandPopup { get; } = titleBandPopup;

    public FrameworkElement TitleBand { get; } = titleBand;

    public TextBlock TitleBandTitleText { get; } = titleBandTitleText;

    public Button TitleBandBackButton { get; } = titleBandBackButton;

    public Button TitleBandForwardButton { get; } = titleBandForwardButton;

    public Button TitleBandLiveViewerCountButton { get; } = titleBandLiveViewerCountButton;

    public TextBlock TitleBandLiveViewerCountText { get; } = titleBandLiveViewerCountText;

    public Button TitleBandChatButton { get; } = titleBandChatButton;

    public Button TitleBandTopmostButton { get; } = titleBandTopmostButton;

    public Button TitleBandMinimizeButton { get; } = titleBandMinimizeButton;

    public Button TitleBandSourceButton { get; } = titleBandSourceButton;

    public Button TitleBandCloseButton { get; } = titleBandCloseButton;

    public Popup ToolbarPopup { get; } = toolbarPopup;

    public FrameworkElement Toolbar { get; } = toolbar;

    public Button ToolbarZoomResetButton { get; } = toolbarZoomResetButton;

    public Grid WebHost { get; } = webHost;

    private WebView2? _webView = webView;

    public WebView2? WebView
    {
        get => _webView;
        set => _webView = value;
    }

    public LocalVideoPlayerView? LocalVideoView { get; } = localVideoView;

    public bool IsDisposed { get; set; }

    public bool IsInputTransparent { get; set; }

    public YouTubeMiniPlayerRuntimeRegistrations Registrations { get; } = new();

    public bool IsApplyingMinimumSize { get; set; }

    public YouTubeMiniPlayerNavigationHistoryController NavigationHistory { get; } = new();

    public bool IsLiveEdgeSeekInProgress { get; set; }

    public Action<bool>? FullScreenStateChanged { get; set; }

    public string WebMessageToken { get; } = Guid.NewGuid().ToString("N");

    public OwnedDispatcherTimer? SizeBasedQualityUpdateTimer { get; set; }

    public string? SizeBasedQualityAppliedSignature { get; set; }

    public YouTubeMiniPlayerWebDragState WebDrag { get; } = new();

    public bool IsSurfaceDragging { get; set; }
    public Action? CancelSurfaceCtrlResize { get; set; }

    private bool _isResizing;

    public bool IsResizing
    {
        get => _isResizing;
        set
        {
            if (_isResizing == value)
            {
                return;
            }

            _isResizing = value;
            InteractiveResizeStateChanged?.Invoke(value);
        }
    }

    public Action<bool>? InteractiveResizeStateChanged { get; set; }

    public int InteractiveResizeGeneration { get; set; }

    public bool IsFullScreen { get; set; }

    public bool IsLiveContinuationEnabled { get; set; } = true;

    public YouTubeMiniPlayerLiveContinuationNavigation? LiveContinuationNavigation { get; set; }

    public OwnedDispatcherTimer? LiveContinuationTimeoutTimer { get; set; }

    public string? LastLiveContinuationSourceVideoId { get; set; }

    public DateTimeOffset? LastPlaybackStateMessageAt { get; set; }

    public int InactiveSuspendGeneration { get; set; }
    public (int Generation, bool StopStalledLoad, DateTimeOffset QueuedAt, TimeSpan Delay)? PendingInactiveSuspension { get; set; }

    public bool IsNavigationLoading { get; set; }

    public bool IsSuspendedForInactivity { get; set; }

    public bool IsLoadStoppedForInactivity { get; set; }

    public YouTubeMiniPlayerLiveChatState LiveChat { get; } = new();

    public YouTubeMiniPlayerOverlayState Overlay { get; } = new();

    public bool IsTaskbarCloseRequested { get; set; }

    public bool IsTaskbarActivationInProgress { get; set; }

    public bool IsClosingTaskbarWindow { get; set; }

    public string? LastRecordedBrowsingHistoryUrl { get; set; }

    public long? HoldSeekInteractionId { get; set; }

    public long? GestureInteractionId { get; set; }

    public long? RightWheelInteractionId { get; set; }

    public double ContentZoomWheelDelta { get; set; }

    public Rect FullScreenRestoreBounds { get; set; } = Rect.Empty;

    public YouTubeMiniPlayerDisplayHealthTracker DisplayHealth { get; } = new();

    public YouTubeMiniPlayerRestorationState Restoration { get; } = new();

    // 従来と同じく、復旧のロック内で現在のWebViewを読み取る。
    public void CompleteRecoveryNavigation(WebView2 webView, bool isHealthy)
        => Restoration.CompleteRecoveryNavigation(in _webView, webView, isHealthy);
}

internal sealed class YouTubeMiniPlayerRuntimeRegistrations
{
    public IDisposable? Transparency { get; set; }
    public EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived { get; set; }
    public EventHandler<object>? DocumentTitleChanged { get; set; }
    public EventHandler<object>? ContainsFullScreenElementChanged { get; set; }
}

public enum YouTubeMiniPlayerDisplayHealthState
{
    NotInitialized,
    Applying,
    Healthy,
    Suspected,
    ReloadRecommended
}

public readonly record struct YouTubeMiniPlayerDisplayHealthSnapshot(
    YouTubeMiniPlayerDisplayHealthState State,
    int AttemptCount,
    DateTimeOffset UpdatedAt);

public sealed class YouTubeMiniPlayerDisplayHealthTracker
{
    private readonly object _sync = new();
    private long _generation;
    private YouTubeMiniPlayerDisplayHealthState _state = YouTubeMiniPlayerDisplayHealthState.NotInitialized;
    private int _attemptCount;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    public long BeginApply()
    {
        lock (_sync)
        {
            _generation++;
            _attemptCount = 0;
            SetState(YouTubeMiniPlayerDisplayHealthState.Applying);
            return _generation;
        }
    }

    public bool TryBeginAttempt(long generation, int maxAttempts)
    {
        lock (_sync)
        {
            if (generation != _generation
                || maxAttempts <= 0
                || _attemptCount >= maxAttempts)
            {
                return false;
            }

            _attemptCount++;
            SetState(YouTubeMiniPlayerDisplayHealthState.Applying);
            return true;
        }
    }

    public bool ReportAttemptResult(long generation, bool isReady, bool isFinalAttempt)
    {
        lock (_sync)
        {
            if (generation != _generation)
            {
                return false;
            }

            SetState(isReady
                ? YouTubeMiniPlayerDisplayHealthState.Healthy
                : isFinalAttempt
                    ? YouTubeMiniPlayerDisplayHealthState.ReloadRecommended
                    : YouTubeMiniPlayerDisplayHealthState.Suspected);
            return true;
        }
    }

    public bool IsCurrentGeneration(long generation)
    {
        lock (_sync)
        {
            return generation == _generation;
        }
    }

    public void ReportFailure()
    {
        lock (_sync)
        {
            _generation++;
            SetState(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _attemptCount = 0;
            SetState(YouTubeMiniPlayerDisplayHealthState.NotInitialized);
        }
    }

    public YouTubeMiniPlayerDisplayHealthSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new YouTubeMiniPlayerDisplayHealthSnapshot(_state, _attemptCount, _updatedAt);
        }
    }

    private void SetState(YouTubeMiniPlayerDisplayHealthState state)
    {
        _state = state;
        _updatedAt = DateTimeOffset.UtcNow;
    }
}
