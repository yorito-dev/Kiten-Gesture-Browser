using System.Windows;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow
{
    private static readonly RuntimeDiagnosticService RuntimeDiagnosticService = new();
    private static readonly ReleaseInformationService ReleaseInformationService = new(typeof(MainWindow).Assembly);

    private Task OpenRuntimeDiagnosticsFromCommandPaletteAsync()
    {
        ShowRuntimeDiagnosticsWindow();
        return Task.CompletedTask;
    }

    private void ShowRuntimeDiagnosticsWindow()
    {
        if (ModelessWindowService.TryGetOwned<RuntimeDiagnosticsWindow>(this, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new RuntimeDiagnosticsWindow(
            CaptureRuntimeDiagnosticSnapshot,
            () => App.StartupPerformance.GetSnapshot(),
            CaptureReleaseInformation,
            _clipboardWriteService.SetTextAsync,
            OpenReleaseInformationPage)
        {
            Owner = this
        };
        ModelessWindowService.Show(window);
    }

    private void CloseRuntimeDiagnosticsWindow()
    {
        ModelessWindowService.CloseOwned<RuntimeDiagnosticsWindow>(this);
    }

    private ReleaseInformationSnapshot CaptureReleaseInformation()
    {
        IReadOnlyList<string> webView2RuntimeVersions;
        try
        {
            webView2RuntimeVersions = GetOpenMainWindows()
                .SelectMany(static window =>
                    window._webViewEnvironmentService.GetCreatedBrowserVersionStrings())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static version => version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            webView2RuntimeVersions = [];
        }

        return ReleaseInformationService.Capture(
            ApplicationChannelService.Current,
            webView2RuntimeVersions);
    }

    private ExternalBrowserLaunchResult OpenReleaseInformationPage(Uri uri)
    {
        if (_isClosingWindow)
        {
            return new ExternalBrowserLaunchResult(false, 0, "Windowを終了しています");
        }

        return _externalBrowserService.Open(
            ExternalBrowserKind.WindowsDefault,
            [uri.AbsoluteUri]);
    }

    private RuntimeDiagnosticSnapshot CaptureRuntimeDiagnosticSnapshot()
    {
        var capturedAt = DateTimeOffset.Now;
        var openWindows = GetOpenMainWindows();
        var windows = openWindows
            .Select(static window => window.CaptureRuntimeDiagnosticWindowState())
            .ToList();
        var processMetrics = RuntimeDiagnosticService.CaptureCurrentProcessMetrics();
        var webViewProcessMetrics = RuntimeDiagnosticService.CaptureWebViewProcessMetrics(
            openWindows.SelectMany(static window =>
                window._webViewEnvironmentService.GetProcessDiagnosticInfos()));
        var applicationOwnership = CaptureRuntimeApplicationOwnershipState();
        var playbackDiagnostics = YouTubePlaybackDiagnosticController.CaptureDiagnosticHistory(
            openWindows.Select(static window => window._youTubePlaybackDiagnosticController));
        var miniPlayerFrameSamples = openWindows
            .SelectMany(static window => window.CaptureRuntimeMiniPlayerFrameSamples())
            .ToList();
        return RuntimeDiagnosticService.CreateSnapshot(
            capturedAt,
            windows,
            s_inputInteractionHub.GetCurrent(capturedAt),
            s_inputInteractionHub.GetDiagnosticHistory(),
            processMetrics,
            applicationOwnership,
            _sessionService.CaptureRecoveryDiagnosticSnapshot()) with
        {
            WebViewProcessRecoveryHistory = s_webViewProcessRecoveryCoordinator.GetDiagnosticHistory(),
            YouTubePlaybackDiagnostics = playbackDiagnostics,
            YouTubeMiniPlayerFrameSamples = miniPlayerFrameSamples,
            WebViewProcesses = webViewProcessMetrics
        };
    }

    private IReadOnlyList<RuntimeMiniPlayerFrameSample> CaptureRuntimeMiniPlayerFrameSamples()
        => _youTubePlaybackDiagnosticController.CaptureMiniPlayerFrameSamples();

    private RuntimeDiagnosticWindowState CaptureRuntimeDiagnosticWindowState()
    {
        var tabStates = _tabs
            .Select(tab =>
            {
                var lifecycle = _webViewLifecycleService.GetSnapshot(tab);
                return new RuntimeDiagnosticTabState(tab.Id, lifecycle.State, lifecycle.InvariantViolation);
            })
            .ToList();
        var tabIds = _tabs.Select(static tab => tab.Id).ToHashSet();
        var splitViews = _splitViewService.Snapshot();
        var invalidSplitReferenceCount = splitViews.Count(split =>
            split.LeftTabId == split.RightTabId
            || !tabIds.Contains(split.LeftTabId)
            || !tabIds.Contains(split.RightTabId));
        var miniPlayerController = _youTubeMiniPlayerController.CaptureSnapshot();
        var miniPlayerRuntimes = _youTubeMiniPlayerController.Runtimes.Values.ToList();
        var miniPlayerHealth = miniPlayerRuntimes
            .Select(static runtime => runtime.DisplayHealth.GetSnapshot().State)
            .ToList();
        var openPopupCount = CountOpenRuntimeDiagnosticPopups(miniPlayerRuntimes);
        var inactiveYouTubeTabWork = _inactiveYouTubeTabWorkCoordinator.CaptureSnapshot();
        var tabMemoryCounters = _tabMemoryManagementService.CaptureCounters();

        return new RuntimeDiagnosticWindowState(
            _workspaceWindowId,
            _workspaceWindowIndex,
            _isClosingWindow,
            _tabs.Count,
            _tabs.Count(static tab => tab.IsWorkspaceTab),
            _tabs.Count(static tab => GetTabWebView(tab) is not null),
            tabStates,
            splitViews.Count,
            invalidSplitReferenceCount,
            miniPlayerController.StateCount,
            miniPlayerRuntimes.Count(static runtime => runtime.WebView is not null),
            miniPlayerRuntimes.Count(static runtime => runtime.IsDisposed),
            miniPlayerHealth.Count(static state => state == YouTubeMiniPlayerDisplayHealthState.Healthy),
            miniPlayerHealth.Count(static state => state == YouTubeMiniPlayerDisplayHealthState.Suspected),
            miniPlayerHealth.Count(static state => state == YouTubeMiniPlayerDisplayHealthState.ReloadRecommended),
            openPopupCount,
            CountAuxiliaryRuntimeDiagnosticWebViews(),
            _backgroundTabWorkCoordinator.IsTabLoading,
            _backgroundTabWorkCoordinator.IsThumbnailWarmupRunning)
        {
            Ownership = CaptureRuntimeWindowOwnershipState(openPopupCount),
            MiniPlayerStateWithoutRuntimeCount = miniPlayerController.StateWithoutRuntimeCount,
            MiniPlayerRuntimeWithoutStateCount = miniPlayerController.RuntimeWithoutStateCount,
            SuspendedTabCount = _tabs.Count(static tab => tab.IsSuspended),
            MemoryReleasedTabCount = _tabs.Count(static tab => tab.IsMemoryReleased),
            UninitializedWebTabCount = _tabs.Count(static tab =>
                !tab.IsNativeContentTab && !tab.IsLoaded && !tab.IsMemoryReleased),
            TabMemoryReleaseCount = tabMemoryCounters.ReleaseCount,
            TabMemoryRestoreCount = tabMemoryCounters.RestoreCount,
            TabMemoryReleaseSkipCount = tabMemoryCounters.ReleaseSkipCount,
            InactiveYouTubeTabSuspendCount = inactiveYouTubeTabWork.SuspendCount,
            InactiveYouTubeTabLoadStopCount = inactiveYouTubeTabWork.LoadStopCount,
            InactiveYouTubeTabResumeCount = inactiveYouTubeTabWork.ResumeCount,
            InactiveYouTubeMiniPlayerSuspendCount = _youTubeMiniPlayerActivityController.InactiveSuspendCount,
            InactiveYouTubeMiniPlayerLoadStopCount = _youTubeMiniPlayerActivityController.InactiveLoadStopCount,
            InactiveYouTubeMiniPlayerResumeCount = _youTubeMiniPlayerActivityController.InactiveResumeCount
        };
    }

    private RuntimeWindowOwnershipState CaptureRuntimeWindowOwnershipState(int openPopupCount)
    {
        var folderTabViews = _tabs
            .Select(static tab => GetTabHostedContent(tab))
            .OfType<FolderTabView>()
            .ToList();
        var hostedFolderTabViews = FolderTabHost.Children
            .OfType<FolderTabView>()
            .ToList();
        var quickLookSnapshots = _tabs
            .SelectMany(static tab => GetTabHostedContent(tab) switch
            {
                FolderTabView folderView => [folderView.GetQuickLookDiagnosticSnapshot()],
                WorkspaceOSView workspaceView => workspaceView.GetQuickLookDiagnosticSnapshots(),
                _ => []
            })
            .ToList();
        var liveChatOwnership = _youTubeLiveChatController.GetDiagnosticSnapshot();
        var isHiddenFloatingMediaHostActive = IsHiddenFloatingMediaHostActive();
        var ownerUnavailable = _isClosingWindow
            || !IsLoaded
            || (!isHiddenFloatingMediaHostActive
                && (!IsVisible || WindowState == WindowState.Minimized));

        return new RuntimeWindowOwnershipState(
            folderTabViews.Count,
            hostedFolderTabViews.Count,
            hostedFolderTabViews.Count(view => !folderTabViews.Contains(view)),
            folderTabViews.Count(static view => view.IsDisposedForDiagnostics),
            quickLookSnapshots.Count(static snapshot => snapshot.IsOpen),
            quickLookSnapshots.Count(static snapshot => snapshot.HasHiddenActiveLoad),
            quickLookSnapshots.Count(static snapshot => snapshot.HasActivityAfterDispose),
            ownerUnavailable ? openPopupCount : 0,
            liveChatOwnership.LoadedCount,
            liveChatOwnership.OrphanedWindowCount,
            liveChatOwnership.OrphanedPendingCount)
        {
            FloatingMediaHostLifetimeState = _floatingMediaHostLifetime.State.ToString(),
            IsHiddenFloatingMediaHostActive = isHiddenFloatingMediaHostActive
        };
    }

    private static RuntimeApplicationOwnershipState CaptureRuntimeApplicationOwnershipState()
    {
        var modeless = ModelessWindowService.GetDiagnosticSnapshot();
        var quickLookPdfEnvironment = QuickLookPdfEnvironmentProvider.Shared.GetDiagnosticSnapshot();
        var independentWorkspaceWindows = Application.Current?.Windows
            .OfType<Window>()
            .Where(static window => window is WorkspaceOSWindow or WorkspaceFolderWindow)
            .ToList() ?? [];
        var quickLookSnapshots = independentWorkspaceWindows
            .SelectMany(static window => window switch
            {
                WorkspaceOSWindow workspaceWindow => workspaceWindow.GetQuickLookDiagnosticSnapshots(),
                WorkspaceFolderWindow folderWindow => [folderWindow.GetQuickLookDiagnosticSnapshot()],
                _ => []
            })
            .ToList();

        return new RuntimeApplicationOwnershipState(
            modeless.RegisteredWindowCount,
            modeless.StaleWindowCount,
            modeless.ClosedOwnerWindowCount,
            independentWorkspaceWindows.Count,
            quickLookSnapshots.Count(static snapshot => snapshot.IsOpen),
            quickLookSnapshots.Count(static snapshot => snapshot.HasHiddenActiveLoad),
            quickLookSnapshots.Count(static snapshot => snapshot.HasActivityAfterDispose))
        {
            QuickLookPdfEnvironmentOwner = quickLookPdfEnvironment.Owner,
            QuickLookPdfEnvironmentState = quickLookPdfEnvironment.State,
            QuickLookPdfEnvironmentCreationAttemptCount = quickLookPdfEnvironment.CreationAttemptCount
        };
    }

    private int CountOpenRuntimeDiagnosticPopups(IReadOnlyList<YouTubeMiniPlayerRuntime> miniPlayerRuntimes)
    {
        var openCount = new[]
        {
            _extensionPopup?.IsOpen == true,
            _bookmarkPreviewController.IsOpen,
            ClosedTabHistoryPopup.IsOpen,
            YouTubeMiniPlayerListPopup.IsOpen,
            YouTubeMiniPlayerSpeedPopup.IsOpen,
            YouTubeMiniPlayerVolumePopup.IsOpen,
            _pageGestureVisualController.IsOverlayVisible,
            StatusPopup.IsOpen,
            CommandPaletteOverlay.IsOpen,
            LinkPreviewPopup.IsOpen,
            YouTubeControlPopup.IsOpen,
            SplitLeftYouTubeControlPopup.IsOpen,
            SplitRightYouTubeControlPopup.IsOpen,
            ChatGptReadabilityPopup.IsOpen,
            DownloadShelfPopup.IsOpen,
            AddressSuggestionPopup.IsOpen,

            TabDragPopup.IsOpen
        }.Count(static isOpen => isOpen);

        return openCount + miniPlayerRuntimes.Sum(static runtime =>
            (runtime.Popup.IsOpen ? 1 : 0)
            + (runtime.TitleBandPopup.IsOpen ? 1 : 0)
            + (runtime.ToolbarPopup.IsOpen ? 1 : 0));
    }

    private int CountAuxiliaryRuntimeDiagnosticWebViews()
    {
        return (_extensionPopupWebView is null ? 0 : 1)
            + (_bookmarkPreviewController.HasWebView ? 1 : 0)
            + (_urlThumbnailCaptureController.HasWebView ? 1 : 0);
    }

    private void AppendWebViewInitializationFailureResourceDiagnostic(
        BrowserTab tab,
        string? initialUrl,
        Exception exception,
        string stage,
        CoreWebView2Environment? targetEnvironment)
    {
        try
        {
            var snapshot = CaptureRuntimeDiagnosticSnapshot();
            var targetMetrics = RuntimeDiagnosticService.CaptureWebViewProcessMetrics(
                WebViewEnvironmentService.GetProcessDiagnosticInfos(targetEnvironment));
            var environmentScope = string.Equals(
                ContainerProfile.NormalizeId(tab.ContainerProfileId),
                ContainerProfile.DefaultId,
                StringComparison.OrdinalIgnoreCase)
                ? "default"
                : "container";
            AppendWebViewPerformanceDiagnostic(
                "webview-runtime-resource-snapshot",
                tab,
                initialUrl,
                detail: RuntimeDiagnosticReportFormatter.FormatWebViewInitializationFailure(
                    stage,
                    exception.HResult,
                    RuntimeDiagnosticService.CaptureCurrentProcessUptime(),
                    environmentScope,
                    snapshot,
                    targetMetrics));
        }
        catch
        {
            // 失敗診断の採取失敗で本来のWebView2例外を隠さない。
        }
    }

    private void HandleYouTubePlaybackDiagnostic(
        string sourceKind,
        Guid targetId,
        string? url,
        YouTubePlaybackDiagnosticMessage message)
        => _youTubePlaybackDiagnosticController.Handle(sourceKind, targetId, url, message);

    private void HandleYouTubePlaybackProcessFailure(
        string sourceKind,
        Guid targetId,
        string? url,
        CoreWebView2ProcessFailedEventArgs args)
        => _youTubePlaybackDiagnosticController.HandleProcessFailure(
            sourceKind,
            targetId,
            url,
            args.ProcessFailedKind);

    private void ResetYouTubePlaybackDiagnostic(string sourceKind, Guid targetId)
        => _youTubePlaybackDiagnosticController.Reset(sourceKind, targetId);

    private bool TryGetYouTubePlaybackActivity(
        string sourceKind,
        Guid targetId,
        out YouTubePlaybackActivitySnapshot? activity)
        => _youTubePlaybackDiagnosticController.TryGetActivity(sourceKind, targetId, out activity);

    bool IYouTubePlaybackDiagnosticHost.IsPrivateWindow => _isPrivateWindow;

    int IYouTubePlaybackDiagnosticHost.WindowIndex => _workspaceWindowIndex;

    void IYouTubePlaybackDiagnosticHost.HandlePlaybackActivityChanged(
        string sourceKind,
        Guid targetId,
        YouTubePlaybackDiagnosticMessage message)
        => HandleYouTubePlaybackActivityChanged(sourceKind, targetId, message);

    void IYouTubePlaybackDiagnosticHost.QueuePerformanceDiagnosticLogLine(
        IReadOnlyList<string> fields)
        => QueuePerformanceDiagnosticLogLine(fields);

    string IYouTubePlaybackDiagnosticHost.NormalizeDiagnosticToken(string value)
        => NormalizeSessionDiagnosticToken(value);
}
