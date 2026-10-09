using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public enum WebViewLifecycleState
{
    NotApplicable,
    Uninitialized,
    Initializing,
    Created,
    Loading,
    Ready,
    Suspended,
    Failed,
    Recovering,
    Inconsistent
}

public readonly record struct WebViewLifecycleSnapshot(
    WebViewLifecycleState State,
    string? InvariantViolation)
{
    public bool HasInvariantViolation => !string.IsNullOrWhiteSpace(InvariantViolation);
}

public readonly record struct WebViewRecoveryPlan(
    long PendingOperationGeneration,
    TimeSpan Delay);

public readonly record struct WebViewInitializationResult(
    bool Started,
    bool Superseded,
    Exception? Failure,
    string? FailureMessage)
{
    public bool Succeeded => Started && !Superseded && Failure is null;
}

public readonly struct WebViewInitializationLease
{
    private readonly TabRuntimeCoordinator? _coordinator;
    private readonly BrowserTab? _tab;
    private readonly long _generation;

    internal WebViewInitializationLease(
        TabRuntimeCoordinator coordinator,
        BrowserTab tab,
        long generation)
    {
        _coordinator = coordinator;
        _tab = tab;
        _generation = generation;
    }

    public bool IsCurrent => _coordinator is not null
        && _tab is not null
        && _coordinator.IsInitializationCurrent(_tab, _generation);
}

public sealed class WebViewLifecycleService
{
    private const int ProcessMaxAutoRecoverAttempts = 1;
    private static readonly TimeSpan ProcessAutoRecoverDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ProcessAutoRecoverStagger = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ProcessAutoRecoverMaximumDelay = TimeSpan.FromMilliseconds(3000);
    private const int RenderMaxReloadAttempts = 1;
    private static readonly TimeSpan RenderReloadDelay = TimeSpan.FromMilliseconds(500);
    private readonly TabRuntimeCoordinator _runtimeCoordinator;
    private readonly HashSet<Guid> _failedBackgroundLoads = [];
    private readonly Dictionary<Guid, string> _loadFailureMessages = [];
    private readonly Dictionary<Guid, int> _processRecoveryAttempts = [];
    private readonly Dictionary<Guid, int> _renderReloadAttempts = [];
    private readonly Dictionary<Guid, string> _recoveryScopeByTab = [];
    private readonly Dictionary<string, int> _recoveryReservationsByScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _recoveringProcesses = [];

    public WebViewLifecycleService()
        : this(TabRuntimeCoordinator.Shared)
    {
    }

    internal WebViewLifecycleService(TabRuntimeRegistry runtimeRegistry)
        : this(new TabRuntimeCoordinator(runtimeRegistry))
    {
    }

    internal WebViewLifecycleService(TabRuntimeCoordinator runtimeCoordinator)
    {
        _runtimeCoordinator = runtimeCoordinator
            ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
    }

    public static CoreWebView2EnvironmentOptions CreateEnvironmentOptions(bool enableBrowserExtensions)
    {
        return new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = enableBrowserExtensions
        };
    }

    public static void ApplyDefaultCoreSettings(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
    }

    public static void ApplyPreviewCoreSettings(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
    }

    public static string FormatNavigationError(CoreWebView2WebErrorStatus status)
    {
        return status switch
        {
            CoreWebView2WebErrorStatus.CannotConnect => "接続できません",
            CoreWebView2WebErrorStatus.ConnectionAborted => "接続が中断されました",
            CoreWebView2WebErrorStatus.ConnectionReset => "接続がリセットされました",
            CoreWebView2WebErrorStatus.Disconnected => "接続が切断されました",
            CoreWebView2WebErrorStatus.HostNameNotResolved => "ホスト名を解決できません",
            CoreWebView2WebErrorStatus.OperationCanceled => "読み込みがキャンセルされました",
            CoreWebView2WebErrorStatus.Timeout => "タイムアウトしました",
            CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "認証が必要です",
            CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => "プロキシ認証が必要です",
            _ => status.ToString()
        };
    }

    public static string FormatProcessFailure(CoreWebView2ProcessFailedEventArgs args)
    {
        return FormatProcessFailure(args.ProcessFailedKind, args.Reason, args.ExitCode);
    }

    public static string FormatProcessFailure(
        CoreWebView2ProcessFailedKind kind,
        CoreWebView2ProcessFailedReason reason,
        int exitCode)
    {
        var text = kind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => "ブラウザープロセスが終了しました",
            CoreWebView2ProcessFailedKind.RenderProcessExited => "ページ描画プロセスが終了しました",
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => "ページ描画プロセスが応答していません",
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited => "フレーム描画プロセスが終了しました",
            CoreWebView2ProcessFailedKind.UtilityProcessExited => "補助プロセスが終了しました",
            CoreWebView2ProcessFailedKind.SandboxHelperProcessExited => "サンドボックス補助プロセスが終了しました",
            CoreWebView2ProcessFailedKind.GpuProcessExited => "GPU プロセスが終了しました",
            CoreWebView2ProcessFailedKind.PpapiPluginProcessExited => "プラグインプロセスが終了しました",
            CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited => "プラグイン仲介プロセスが終了しました",
            CoreWebView2ProcessFailedKind.UnknownProcessExited => "不明なプロセスが終了しました",
            _ => kind.ToString()
        };

        return $"{text} / reason={reason} / exit={exitCode}";
    }

    public bool HasLoadedRuntime(BrowserTab tab)
    {
        return _runtimeCoordinator.HasLoadedRuntime(tab);
    }

    public bool CanPreserveRuntime(BrowserTab tab)
    {
        return _runtimeCoordinator.CanPreserveRuntime(tab);
    }

    public bool TryBeginInitialization(BrowserTab tab)
    {
        return _runtimeCoordinator.TryBeginInitialization(tab, out _);
    }

    public void EndInitialization(BrowserTab tab)
    {
        _runtimeCoordinator.CancelInitialization(tab);
    }

    public bool IsInitializing(BrowserTab tab)
    {
        return _runtimeCoordinator.GetTransientSnapshot(tab).IsInitializing;
    }

    public Task WaitForInitializationAsync(BrowserTab tab)
    {
        return _runtimeCoordinator.WaitForInitializationAsync(tab);
    }

    public async Task<WebViewInitializationResult> TryRunInitializationAsync(
        BrowserTab tab,
        Func<WebViewInitializationLease, Task> initializeAsync,
        Func<Exception, string> createFailureMessage)
    {
        if (!_runtimeCoordinator.TryBeginInitialization(tab, out var initializationGeneration))
        {
            return default;
        }

        var lease = new WebViewInitializationLease(
            _runtimeCoordinator,
            tab,
            initializationGeneration);
        try
        {
            await initializeAsync(lease);
            if (!lease.IsCurrent)
            {
                return new WebViewInitializationResult(
                    Started: true,
                    Superseded: true,
                    Failure: null,
                    FailureMessage: null);
            }

            return new WebViewInitializationResult(
                Started: true,
                Superseded: false,
                Failure: null,
                FailureMessage: null);
        }
        catch (Exception ex)
        {
            if (!lease.IsCurrent)
            {
                return new WebViewInitializationResult(
                    Started: true,
                    Superseded: true,
                    Failure: null,
                    FailureMessage: null);
            }

            var failureMessage = createFailureMessage(ex);
            MarkLoadFailure(tab, failureMessage);
            return new WebViewInitializationResult(
                Started: true,
                Superseded: false,
                Failure: ex,
                FailureMessage: failureMessage);
        }
        finally
        {
            _ = _runtimeCoordinator.EndInitialization(tab, initializationGeneration);
        }
    }

    public WebViewLifecycleSnapshot GetSnapshot(BrowserTab tab)
    {
        var invariantViolation = GetInvariantViolation(tab);
        if (invariantViolation is not null)
        {
            return new WebViewLifecycleSnapshot(
                WebViewLifecycleState.Inconsistent,
                invariantViolation);
        }

        if (tab.IsNativeContentTab)
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.NotApplicable, null);
        }

        if (_recoveringProcesses.Contains(tab.Id))
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Recovering, null);
        }

        if (IsInitializing(tab))
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Initializing, null);
        }

        if (_loadFailureMessages.ContainsKey(tab.Id))
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Failed, null);
        }

        if (tab.IsSuspended)
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Suspended, null);
        }

        if (tab.IsLoading)
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Loading, null);
        }

        if (tab.IsLoaded)
        {
            return new WebViewLifecycleSnapshot(WebViewLifecycleState.Ready, null);
        }

        return new WebViewLifecycleSnapshot(
            _runtimeCoordinator.GetWebView(tab) is null
                ? WebViewLifecycleState.Uninitialized
                : WebViewLifecycleState.Created,
            null);
    }

    private string? GetInvariantViolation(BrowserTab tab)
    {
        if (tab.IsNativeContentTab)
        {
            return _runtimeCoordinator.GetWebView(tab) is not null
                || tab.IsLoaded || tab.IsLoading || tab.IsSuspended
                ? (tab.IsWorkspaceTab
                    ? "WorkspaceタブがWebView2 Runtime状態を保持しています"
                    : "ネイティブ表示タブがWebView2 Runtime状態を保持しています")
                : null;
        }

        if (_runtimeCoordinator.GetWebView(tab) is null
            && (tab.IsLoaded || tab.IsLoading || tab.IsSuspended))
        {
            return "WebView2がないタブにRuntime状態が残っています";
        }

        if (tab.IsSuspended && tab.IsLoading)
        {
            return "読み込み中のWebView2が休止状態になっています";
        }

        if (tab.IsSuspended && !tab.IsLoaded)
        {
            return "未初期化のWebView2が休止状態になっています";
        }

        return null;
    }

    public void MarkLoadFailure(BrowserTab tab, string message)
    {
        _failedBackgroundLoads.Add(tab.Id);
        _loadFailureMessages[tab.Id] = message;
    }

    public void ClearLoadFailure(BrowserTab tab)
    {
        _failedBackgroundLoads.Remove(tab.Id);
        _loadFailureMessages.Remove(tab.Id);
    }

    public bool HasLoadFailure(BrowserTab tab)
    {
        return _loadFailureMessages.ContainsKey(tab.Id);
    }

    public bool TryGetLoadFailure(BrowserTab tab, out string message)
    {
        return _loadFailureMessages.TryGetValue(tab.Id, out message!);
    }

    public bool ShouldDisposeRuntimeForRetry(BrowserTab tab)
    {
        return HasLoadFailure(tab);
    }

    public void MarkNavigationSuccess(BrowserTab tab)
    {
        _processRecoveryAttempts.Remove(tab.Id);
        _renderReloadAttempts.Remove(tab.Id);
    }

    public void ResetRecoveryAttempts(BrowserTab tab)
    {
        _processRecoveryAttempts.Remove(tab.Id);
    }

    public bool TryBeginRenderReload(BrowserTab tab, int maxAttempts)
    {
        if (tab.IsNativeContentTab || maxAttempts <= 0)
        {
            return false;
        }

        _renderReloadAttempts.TryGetValue(tab.Id, out var attempts);
        if (attempts >= maxAttempts)
        {
            return false;
        }

        _renderReloadAttempts[tab.Id] = attempts + 1;
        return true;
    }

    public bool TryCreateRenderReloadPlan(
        BrowserTab tab,
        string failureMessage,
        out WebViewRecoveryPlan plan)
    {
        if (!TryBeginRenderReload(tab, RenderMaxReloadAttempts))
        {
            InvalidatePendingOperations(tab);
            MarkLoadFailure(tab, failureMessage);
            plan = default;
            return false;
        }

        plan = new WebViewRecoveryPlan(
            CapturePendingOperationGeneration(tab),
            RenderReloadDelay);
        return true;
    }

    public void PrepareForProcessGroupRecovery(BrowserTab tab, string failureMessage)
    {
        InvalidatePendingOperations(tab);
        CompleteAutoRecovery(tab);
        ResetRecoveryAttempts(tab);
        _renderReloadAttempts.Remove(tab.Id);
        MarkLoadFailure(tab, failureMessage);
    }

    public void PrepareForRuntimeTransfer(BrowserTab tab)
    {
        EndInitialization(tab);
        ClearLoadFailure(tab);
    }

    public void PrepareForRuntimeRecovery(BrowserTab tab, bool resetRecoveryAttempts)
    {
        if (resetRecoveryAttempts)
        {
            ResetRecoveryAttempts(tab);
        }

        ClearLoadFailure(tab);
        EndInitialization(tab);
    }

    public bool TryBeginAutoRecovery(BrowserTab tab, bool isClosingWindow, int maxAttempts)
    {
        if (isClosingWindow
            || tab.IsNativeContentTab
            || string.IsNullOrWhiteSpace(tab.Url)
            || _recoveringProcesses.Contains(tab.Id))
        {
            return false;
        }

        _processRecoveryAttempts.TryGetValue(tab.Id, out var attempts);
        if (attempts >= maxAttempts)
        {
            return false;
        }

        _processRecoveryAttempts[tab.Id] = attempts + 1;
        _recoveringProcesses.Add(tab.Id);
        return true;
    }

    public bool TryCreateAutoRecoveryPlan(
        BrowserTab tab,
        bool isClosingWindow,
        bool prioritize,
        out WebViewRecoveryPlan plan)
    {
        if (!TryBeginAutoRecovery(tab, isClosingWindow, ProcessMaxAutoRecoverAttempts))
        {
            plan = default;
            return false;
        }

        plan = new WebViewRecoveryPlan(
            CapturePendingOperationGeneration(tab),
            ReserveAutoRecoveryDelay(
                tab,
                prioritize,
                ProcessAutoRecoverDelay,
                ProcessAutoRecoverStagger,
                ProcessAutoRecoverMaximumDelay));
        return true;
    }

    public void CompleteAutoRecovery(BrowserTab tab)
    {
        _recoveringProcesses.Remove(tab.Id);
        if (!_recoveryScopeByTab.Remove(tab.Id, out var scope))
        {
            return;
        }

        if (!_recoveryReservationsByScope.TryGetValue(scope, out var reservations) || reservations <= 1)
        {
            _recoveryReservationsByScope.Remove(scope);
            return;
        }

        _recoveryReservationsByScope[scope] = reservations - 1;
    }

    public TimeSpan ReserveAutoRecoveryDelay(
        BrowserTab tab,
        bool prioritize,
        TimeSpan baseDelay,
        TimeSpan staggerDelay,
        TimeSpan maximumDelay)
    {
        if (baseDelay < TimeSpan.Zero || staggerDelay < TimeSpan.Zero || maximumDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay));
        }

        if (_recoveryScopeByTab.ContainsKey(tab.Id))
        {
            return baseDelay;
        }

        var scope = ContainerProfile.NormalizeId(tab.ContainerProfileId);
        _recoveryReservationsByScope.TryGetValue(scope, out var reservations);
        _recoveryReservationsByScope[scope] = reservations + 1;
        _recoveryScopeByTab[tab.Id] = scope;

        if (prioritize)
        {
            return baseDelay;
        }

        var staggerTicks = Math.Min(
            maximumDelay.Ticks - baseDelay.Ticks,
            staggerDelay.Ticks * (long)(reservations + 1));
        return baseDelay + TimeSpan.FromTicks(staggerTicks);
    }

    public long CapturePendingOperationGeneration(BrowserTab tab)
    {
        return _runtimeCoordinator.CapturePendingOperationGeneration(tab);
    }

    public bool IsPendingOperationCurrent(BrowserTab tab, long generation)
    {
        return _runtimeCoordinator.IsPendingOperationCurrent(tab, generation);
    }

    public bool CanUseRuntimeInWindow(BrowserTab tab, Guid windowId, bool isTabInWindow)
    {
        return _runtimeCoordinator.CanUseInWindow(tab, windowId, isTabInWindow);
    }

    public void InvalidatePendingOperations(BrowserTab tab)
    {
        _runtimeCoordinator.InvalidatePendingOperations(tab);
    }

    public void ResetRuntimeState(BrowserTab tab)
    {
        InvalidatePendingOperations(tab);
        EndInitialization(tab);
        CompleteAutoRecovery(tab);
        ClearLoadFailure(tab);
        ResetRecoveryAttempts(tab);
        _renderReloadAttempts.Remove(tab.Id);

        _ = _runtimeCoordinator.SetLoaded(tab, false);
        _ = _runtimeCoordinator.SetLoading(tab, false);
        _ = _runtimeCoordinator.SetSuspended(tab, false);
    }

    public bool IsBackgroundLoadCandidate(
        BrowserTab tab,
        BrowserTab? activeTab,
        Func<BrowserTab, bool> shouldDeferBackgroundMediaLoad)
    {
        return tab != activeTab
            && !tab.IsNativeContentTab
            && !tab.IsLoaded
            && !IsInitializing(tab)
            && !_failedBackgroundLoads.Contains(tab.Id)
            && !shouldDeferBackgroundMediaLoad(tab)
            && !string.IsNullOrWhiteSpace(tab.Url);
    }

    public bool IsBackgroundThumbnailCandidate(BrowserTab tab, BrowserTab? activeTab)
    {
        return IsBackgroundThumbnailStateCandidate(tab, activeTab)
            && _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is not null;
    }

    public bool IsBackgroundThumbnailStateCandidate(BrowserTab tab, BrowserTab? activeTab)
    {
        return tab != activeTab
            && !tab.IsNativeContentTab
            && tab.IsLoaded
            && !tab.IsLoading
            && tab.Thumbnail is null
            && !IsInitializing(tab);
    }

    public bool ShouldPauseBackgroundThumbnailWarmup(BrowserTab? activeTab)
    {
        return activeTab is not null
            && !activeTab.IsNativeContentTab
            && (activeTab.IsLoading || IsInitializing(activeTab));
    }

    public bool ShouldPauseBackgroundLoad(BrowserTab? activeTab)
    {
        return activeTab is not null
            && !activeTab.IsNativeContentTab
            && (activeTab.IsLoading || IsInitializing(activeTab));
    }

    public bool CanSuspendInactiveTab(
        BrowserTab tab,
        BrowserTab activeTab,
        bool isVisibleSplitTab,
        bool hasActiveMedia = false)
    {
        return tab != activeTab
            && !tab.IsNativeContentTab
            && !tab.IsPinned
            && !tab.IsPlayingAudio
            && !hasActiveMedia
            && !isVisibleSplitTab
            && _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is not null
            && !tab.IsLoading
            && !IsInitializing(tab);
    }

    public static bool IsSafeToSuspendInactiveYouTubePlayback(
        YouTubePlaybackActivitySnapshot? activity,
        bool? isPausedFallback)
    {
        return activity?.State switch
        {
            YouTubePlaybackActivityState.Paused => true,
            YouTubePlaybackActivityState.Ended => true,
            YouTubePlaybackActivityState.Failed => true,
            YouTubePlaybackActivityState.Unknown => isPausedFallback == true,
            null => isPausedFallback == true,
            _ => false
        };
    }

    public static bool IsSafeToStopInactiveYouTubeLoad(
        YouTubePlaybackActivitySnapshot? activity)
    {
        return activity?.State != YouTubePlaybackActivityState.Playing;
    }

    public int CountFailedRestoredTabs(IEnumerable<BrowserTab> tabs, IReadOnlySet<Guid> restoredSessionTabIds)
    {
        return tabs.Count(tab =>
            restoredSessionTabIds.Contains(tab.Id)
            && HasLoadFailure(tab));
    }

}
