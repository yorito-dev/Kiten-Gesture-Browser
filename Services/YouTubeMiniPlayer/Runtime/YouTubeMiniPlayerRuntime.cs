using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

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

    public Border AudioStatusBadge { get; } = audioStatusBadge;

    public TextBlock AudioStatusText { get; } = audioStatusText;

    public Window TaskbarWindow { get; } = taskbarWindow;

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

    public WebView2? WebView { get; set; } = webView;

    public LocalVideoPlayerView? LocalVideoView { get; } = localVideoView;

    public bool IsDisposed { get; set; }

    public YouTubeMiniPlayerNavigationHistoryController NavigationHistory { get; } = new();

    public bool IsLiveEdgeSeekInProgress { get; set; }

    public EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived { get; set; }

    public EventHandler<object>? DocumentTitleChanged { get; set; }

    public EventHandler<object>? ContainsFullScreenElementChanged { get; set; }

    public Action<bool>? FullScreenStateChanged { get; set; }

    public string WebMessageToken { get; } = Guid.NewGuid().ToString("N");

    public DispatcherTimer? TitleBandHideTimer { get; set; }

    public DispatcherTimer? ToolbarHideTimer { get; set; }

    public DispatcherTimer? WebDragFollowTimer { get; set; }

    public OwnedDispatcherTimer? SizeBasedQualityUpdateTimer { get; set; }

    public string? SizeBasedQualityAppliedSignature { get; set; }

    public bool IsWebDragging { get; set; }

    public bool IsSurfaceDragging { get; set; }

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

    public bool IsInitialLivePlaybackResolved { get; set; }

    public bool IsLiveContinuationEnabled { get; set; } = true;

    public YouTubeMiniPlayerLiveContinuationNavigation? LiveContinuationNavigation { get; set; }

    public OwnedDispatcherTimer? LiveContinuationTimeoutTimer { get; set; }

    public string? LastLiveContinuationSourceVideoId { get; set; }

    public DateTimeOffset? LastPlaybackStateMessageAt { get; set; }

    public int InactiveSuspendGeneration { get; set; }

    public bool IsNavigationLoading { get; set; }

    public bool IsSuspendedForInactivity { get; set; }

    public bool IsLoadStoppedForInactivity { get; set; }

    public string? LiveChatAvailabilityAttemptVideoId { get; set; }

    public int LiveChatAvailabilityAttemptCount { get; set; }

    public bool IsToolbarMenuOpen { get; set; }

    public bool IsNativePositionRefreshQueued { get; set; }

    public int OverlayPlacementGeneration { get; set; }

    public int ToolbarPlacementRequestGeneration { get; set; }

    public bool IsTaskbarCloseRequested { get; set; }

    public bool IsTaskbarActivationInProgress { get; set; }

    public bool IsClosingTaskbarWindow { get; set; }

    public string? LastRecordedBrowsingHistoryUrl { get; set; }

    public long? HoldSeekInteractionId { get; set; }

    public long? GestureInteractionId { get; set; }

    public long? RightWheelInteractionId { get; set; }

    public double ContentZoomWheelDelta { get; set; }

    public double WebDragStartLeft { get; set; }

    public double WebDragStartTop { get; set; }

    public double WebDragStartWidth { get; set; }

    public double WebDragStartHeight { get; set; }

    public Point WebDragStartScreenPoint { get; set; }

    public Rect FullScreenRestoreBounds { get; set; } = Rect.Empty;

    public YouTubeMiniPlayerDisplayHealthTracker DisplayHealth { get; } = new();

    private readonly object _recoveryNavigationSync = new();
    private long _recoveryNavigationGeneration;
    private TaskCompletionSource<bool>? _recoveryNavigationCompletion;
    private WebView2? _expectedRecoveryWebView;

    public YouTubeMiniPlayerRecoveryNavigationWait BeginRecoveryNavigation(WebView2? expectedWebView)
    {
        lock (_recoveryNavigationSync)
        {
            _recoveryNavigationGeneration++;
            _recoveryNavigationCompletion?.TrySetResult(false);
            _expectedRecoveryWebView = expectedWebView;
            _recoveryNavigationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new YouTubeMiniPlayerRecoveryNavigationWait(
                _recoveryNavigationGeneration,
                _recoveryNavigationCompletion.Task);
        }
    }

    public void CompleteRecoveryNavigation(WebView2 webView, bool isHealthy)
    {
        lock (_recoveryNavigationSync)
        {
            if (_recoveryNavigationCompletion is null
                || (_expectedRecoveryWebView is not null
                    && !ReferenceEquals(_expectedRecoveryWebView, webView))
                || !ReferenceEquals(WebView, webView))
            {
                return;
            }

            _recoveryNavigationCompletion.TrySetResult(isHealthy);
            _recoveryNavigationCompletion = null;
            _expectedRecoveryWebView = null;
        }
    }

    public void CancelRecoveryNavigation(YouTubeMiniPlayerRecoveryNavigationWait? wait = null)
    {
        lock (_recoveryNavigationSync)
        {
            if (wait is { } expected && expected.Generation != _recoveryNavigationGeneration)
            {
                return;
            }

            _recoveryNavigationCompletion?.TrySetResult(false);
            _recoveryNavigationCompletion = null;
            _expectedRecoveryWebView = null;
        }
    }
}

internal readonly record struct YouTubeMiniPlayerRecoveryNavigationWait(
    long Generation,
    Task<bool> Completion);

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
