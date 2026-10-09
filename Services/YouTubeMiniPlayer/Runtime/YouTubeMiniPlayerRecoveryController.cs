using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IYouTubeMiniPlayerRecoveryHost
{
    bool IsClosingWindow { get; }
    CancellationToken LifetimeToken { get; }
    IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> Runtimes { get; }
    bool HasOperationalCore(YouTubeMiniPlayerRuntime runtime);
    bool TryGetReloadTarget(YouTubeMiniPlayerRuntime runtime, out WebView2 webView, out Action reload);
    Task<string?> ExecuteHealthProbeAsync(YouTubeMiniPlayerState state, string script);
    Task<bool> CaptureStateAsync(YouTubeMiniPlayerState state);
    void ResetWebView(YouTubeMiniPlayerRuntime runtime);
    Task LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri);
    Task<bool> ActivateRecoverySourceTabAsync(YouTubeMiniPlayerState state);
    void QueueInactiveSuspension(YouTubeMiniPlayerState state, bool stopStalledLoad);
    void CloseWithoutHistory(Guid id);
    void QueueLayoutVerification(YouTubeMiniPlayerState state);
    void QueueSave();
    void SetStatus(string message);
    void QueueDiagnosticLogLine(IReadOnlyList<string> fields);
    void AppendDiagnostic(string category, Exception exception, string detail);
    void RecordProcessGroupResult(string containerProfileId, uint browserProcessId, string result);
}

// 復旧要求、障害確認、再読込／再生成の待機と結果反映を機能単位で所有する。
// 実Core・Window操作だけをhostへ接続し、既存RecoveryServiceの取消と段階遷移を共有する。
internal sealed class YouTubeMiniPlayerRecoveryController
{
    private static readonly SemaphoreSlim s_sharedRecoveryGate = new(1, 1);

    private const int YouTubeMiniPlayerProcessRecoveryStaggerMilliseconds = 350;
    private const int YouTubeMiniPlayerRecoveryImmediateDelayMilliseconds = 250;
    private const int YouTubeMiniPlayerGpuRecoveryDelayMilliseconds = 3000;
    private const int YouTubeMiniPlayerRecoveryProcessDelayMilliseconds = 20000;
    private const int YouTubeMiniPlayerRecoveryStallDelayMilliseconds = 12000;
    private const int YouTubeMiniPlayerRecoveryProbeIntervalMilliseconds = 2000;
    private const int YouTubeMiniPlayerRecoveryNavigationTimeoutMilliseconds = 20000;
    private const int YouTubeMiniPlayerRecoveryAttemptLimit = 3;
    private static readonly TimeSpan YouTubeMiniPlayerRecoveryAttemptWindow = TimeSpan.FromMinutes(2);

    private readonly IYouTubeMiniPlayerRecoveryHost _host;
    private readonly YouTubeMiniPlayerRecoveryService _youTubeMiniPlayerRecoveryService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<Task<bool>, TimeSpan, CancellationToken, Task<bool>> _waitForNavigation;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _recoveryAttemptSync = new();
    private readonly Dictionary<Guid, List<DateTimeOffset>> _recoveryAttempts = [];

    public YouTubeMiniPlayerRecoveryController(
        IYouTubeMiniPlayerRecoveryHost host,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Task<bool>, TimeSpan, CancellationToken, Task<bool>>? waitForNavigation = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _host = host;
        _youTubeMiniPlayerRecoveryService = new YouTubeMiniPlayerRecoveryService(s_sharedRecoveryGate);
        _delay = delay ?? Task.Delay;
        _waitForNavigation = waitForNavigation ?? ((completion, timeout, token) => completion.WaitAsync(timeout, token));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool CancelPending(Guid id) => _youTubeMiniPlayerRecoveryService.CancelPending(id);
    public void Cancel(Guid id)
    {
        _youTubeMiniPlayerRecoveryService.Cancel(id);
        lock (_recoveryAttemptSync)
        {
            _recoveryAttempts.Remove(id);
        }
    }

    public void CancelAll()
    {
        _youTubeMiniPlayerRecoveryService.CancelAll();
        lock (_recoveryAttemptSync)
        {
            _recoveryAttempts.Clear();
        }
    }

    public void HandlePlaybackDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message)
    {
        switch (message.EventName)
        {
            case "waiting":
            case "stalled":
            case "emptied":
                if (state.IsMinimized)
                {
                    _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
                    _host.QueueInactiveSuspension(state, stopStalledLoad: true);
                    break;
                }

                QueueYouTubeMiniPlayerAutoRecovery(
                    state,
                    "playback-stalled",
                    TimeSpan.FromMilliseconds(YouTubeMiniPlayerRecoveryStallDelayMilliseconds),
                    requireProgressConfirmation: true);
                break;

            case "error":
                if (_host.Runtimes.TryGetValue(state.Id, out var runtime))
                {
                    runtime.DisplayHealth.ReportFailure();
                }

                _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
                if (state.IsMinimized)
                {
                    _host.QueueInactiveSuspension(state, stopStalledLoad: true);
                    break;
                }

                QueueYouTubeMiniPlayerAutoRecovery(state, "media-error");
                break;

            case "canplay":
            case "playing":
                _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
                break;
        }
    }

    public void HandleProcessFailure(
        YouTubeMiniPlayerState state,
        CoreWebView2ProcessFailedKind processFailedKind)
    {
        var action = WebViewProcessFailurePolicy.GetAction(processFailedKind);
        if (action == WebViewProcessFailureAction.ObserveOnly)
        {
            return;
        }

        _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
        QueueYouTubeMiniPlayerAutoRecovery(
            state,
            "process-failed-" + processFailedKind,
            TimeSpan.FromMilliseconds(action == WebViewProcessFailureAction.RevalidateMediaPlayback
                ? YouTubeMiniPlayerGpuRecoveryDelayMilliseconds
                : YouTubeMiniPlayerRecoveryProcessDelayMilliseconds),
            requireProgressConfirmation: false,
            skipCurrentWebViewReload: action == WebViewProcessFailureAction.RecreateProcessGroup);
    }

    public void QueueYouTubeMiniPlayerAutoRecovery(
        YouTubeMiniPlayerState state,
        string reason)
    {
        _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
        QueueYouTubeMiniPlayerAutoRecovery(
            state,
            reason,
            TimeSpan.FromMilliseconds(YouTubeMiniPlayerRecoveryImmediateDelayMilliseconds),
            requireProgressConfirmation: false);
    }

    public void QueueYouTubeMiniPlayerAutoRecovery(
        YouTubeMiniPlayerState state,
        string reason,
        TimeSpan confirmationDelay,
        bool requireProgressConfirmation,
        bool skipCurrentWebViewReload = false)
    {
        if (_host.IsClosingWindow
            || state.SourceKind != MiniPlayerSourceKind.YouTube
            || !_host.Runtimes.ContainsKey(state.Id))
        {
            return;
        }

        if (state.IsMinimized)
        {
            _youTubeMiniPlayerRecoveryService.CancelPending(state.Id);
            _host.QueueInactiveSuspension(state, stopStalledLoad: true);
            return;
        }

        _ = RunYouTubeMiniPlayerAutoRecoveryAsync(
            state,
            reason,
            confirmationDelay,
            requireProgressConfirmation,
            skipCurrentWebViewReload,
            allowSourceTabFallback: false);
    }

    public async Task<bool> RunYouTubeMiniPlayerAutoRecoveryAsync(
        YouTubeMiniPlayerState state,
        string reason,
        TimeSpan confirmationDelay,
        bool requireProgressConfirmation,
        bool skipCurrentWebViewReload,
        bool allowSourceTabFallback = false)
    {
        if (IsRecoveryAttemptLimited(state.Id))
        {
            if (_host.Runtimes.TryGetValue(state.Id, out var limitedRuntime))
            {
                limitedRuntime.DisplayHealth.ReportFailure();
            }

            _host.SetStatus("YouTubeミニプレイヤーの自動復旧を繰り返したため停止しました。一覧の表示修復を使用してください");
            return false;
        }

        var result = await _youTubeMiniPlayerRecoveryService.RequestAsync(
            state.Id,
            reason,
            confirmationDelay,
            skipCurrentWebViewReload,
            new YouTubeMiniPlayerRecoveryCallbacks(
                token => ConfirmYouTubeMiniPlayerFailureAsync(
                    state,
                    requireProgressConfirmation,
                    token),
                token => CaptureYouTubeMiniPlayerRecoverySnapshotAsync(state, token),
                (snapshot, token) => ReloadCurrentYouTubeMiniPlayerWebViewAsync(
                    state,
                    snapshot,
                    token),
                (snapshot, token) => RecreateYouTubeMiniPlayerWebViewAsync(
                    state,
                    snapshot,
                    token),
                (snapshot, token) => OpenYouTubeMiniPlayerRecoverySourceTabAsync(
                    state,
                    snapshot,
                    token),
                (stage, currentReason) =>
                    SetYouTubeMiniPlayerRecoveryStatus(stage, currentReason)),
            allowSourceTabFallback,
            _host.LifetimeToken);

        if (result is YouTubeMiniPlayerRecoveryResult.RecoveredByReload
            or YouTubeMiniPlayerRecoveryResult.RecoveredByRecreate
            or YouTubeMiniPlayerRecoveryResult.Failed)
        {
            RecordRecoveryAttempt(state.Id);
        }

        switch (result)
        {
            case YouTubeMiniPlayerRecoveryResult.NotNeeded:
                _host.QueueLayoutVerification(state);
                break;

            case YouTubeMiniPlayerRecoveryResult.RecoveredByReload:
                _host.QueueSave();
                _host.SetStatus("YouTubeミニプレイヤーを再読み込みして復旧しました");
                break;

            case YouTubeMiniPlayerRecoveryResult.RecoveredByRecreate:
                _host.QueueSave();
                _host.SetStatus("YouTubeミニプレイヤーのWebView2を再生成して復旧しました");
                break;

            case YouTubeMiniPlayerRecoveryResult.SourceTabOpened:
                if (_host.Runtimes.ContainsKey(state.Id))
                {
                    _host.CloseWithoutHistory(state.Id);
                }

                _host.SetStatus("ミニプレイヤーを復旧できなかったため通常のYouTubeタブで開きました");
                break;

            case YouTubeMiniPlayerRecoveryResult.Failed:
                if (_host.Runtimes.TryGetValue(state.Id, out var runtime))
                {
                    runtime.DisplayHealth.ReportFailure();
                }

                _host.SetStatus("YouTubeミニプレイヤーを自動復旧できませんでした。一覧の表示修復を使用してください");
                break;

        }

        return result is YouTubeMiniPlayerRecoveryResult.RecoveredByReload
            or YouTubeMiniPlayerRecoveryResult.RecoveredByRecreate
            or YouTubeMiniPlayerRecoveryResult.SourceTabOpened;
    }

    private async Task<bool> ConfirmYouTubeMiniPlayerFailureAsync(
        YouTubeMiniPlayerState state,
        bool requireProgressConfirmation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_host.Runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.IsDisposed
            || !_host.HasOperationalCore(runtime))
        {
            return true;
        }

        var first = await CaptureYouTubeMiniPlayerHealthProbeAsync(state, cancellationToken);
        var displayFailure = runtime.DisplayHealth.GetSnapshot().State
            == YouTubeMiniPlayerDisplayHealthState.ReloadRecommended;
        if (first is null)
        {
            // 再生停止だけでは、検査不能を故障の確証として再読込しない。
            return displayFailure || !requireProgressConfirmation;
        }

        if (displayFailure || YouTubeMiniPlayerRecoveryService.IsDefiniteFailure(first.Value))
        {
            return true;
        }

        if (!requireProgressConfirmation || first.Value.IsPaused || first.Value.IsEnded)
        {
            return false;
        }

        await _delay(
            TimeSpan.FromMilliseconds(YouTubeMiniPlayerRecoveryProbeIntervalMilliseconds),
            cancellationToken);
        var second = await CaptureYouTubeMiniPlayerHealthProbeAsync(state, cancellationToken);
        return second is { } current
            && YouTubeMiniPlayerRecoveryService.IsPersistentPlaybackFailure(
                first.Value,
                current);
    }

    private async Task<YouTubeMiniPlayerPlaybackHealthProbe?> CaptureYouTubeMiniPlayerHealthProbeAsync(
        YouTubeMiniPlayerState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _host.ExecuteHealthProbeAsync(
            state, YouTubeControlScriptService.BuildPlaybackHealthProbeScript());
        cancellationToken.ThrowIfCancellationRequested();
        return YouTubeMiniPlayerRecoveryService.TryParseHealthProbe(result, out var probe)
            ? probe
            : null;
    }

    private async Task<YouTubeMiniPlayerPlaybackStateSnapshot> CaptureYouTubeMiniPlayerRecoverySnapshotAsync(
        YouTubeMiniPlayerState state,
        CancellationToken cancellationToken)
    {
        var fallback = YouTubeMiniPlayerStateService.CreatePlaybackStateSnapshot(state);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = await _host.CaptureStateAsync(state);
            cancellationToken.ThrowIfCancellationRequested();
            return captured
                ? YouTubeMiniPlayerStateService.CreatePlaybackStateSnapshot(state)
                : fallback;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _host.AppendDiagnostic(
                "youtube-mini-player-recovery-capture-failed",
                ex,
                $"state={state.Id}");
            return fallback;
        }
    }

    private async Task<bool> ReloadCurrentYouTubeMiniPlayerWebViewAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_host.Runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.IsDisposed
            || !_host.TryGetReloadTarget(runtime, out var webView, out var reload))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyPlaybackStateSnapshot(state, snapshot);
        runtime.DisplayHealth.Reset();
        var wait = runtime.BeginRecoveryNavigation(webView);
        try
        {
            reload();
            RecordRecoveryOperation(state.Id, "webview-reload-requested");
            return await WaitForYouTubeMiniPlayerRecoveryNavigationAsync(
                state,
                runtime,
                webView,
                wait,
                snapshot,
                cancellationToken);
        }
        finally
        {
            runtime.CancelRecoveryNavigation(wait);
        }
    }

    private async Task<bool> RecreateYouTubeMiniPlayerWebViewAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_host.Runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.IsDisposed
            || !YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(state.Url, out var uri))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyPlaybackStateSnapshot(state, snapshot);
        _host.ResetWebView(runtime);
        RecordRecoveryOperation(state.Id, "webview-recreate-requested");
        var wait = runtime.BeginRecoveryNavigation(expectedWebView: null);
        try
        {
            await _host.LoadRuntimeAsync(state, uri);
            if (runtime.WebView is not { } webView)
            {
                return false;
            }

            return await WaitForYouTubeMiniPlayerRecoveryNavigationAsync(
                state,
                runtime,
                webView,
                wait,
                snapshot,
                cancellationToken);
        }
        finally
        {
            runtime.CancelRecoveryNavigation(wait);
        }
    }

    private async Task<bool> WaitForYouTubeMiniPlayerRecoveryNavigationAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        WebView2 webView,
        YouTubeMiniPlayerRecoveryNavigationWait wait,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var isHealthy = await _waitForNavigation(wait.Completion,
                TimeSpan.FromMilliseconds(YouTubeMiniPlayerRecoveryNavigationTimeoutMilliseconds),
                cancellationToken);
            if (!isHealthy || !IsCurrentRecoveryTarget(state, runtime, webView))
            {
                return false;
            }

            return await ConfirmRecoveredPlaybackAsync(
                state,
                runtime,
                webView,
                snapshot,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> ConfirmRecoveredPlaybackAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        WebView2 webView,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.IsPaused is null)
        {
            return true;
        }

        var first = await CaptureYouTubeMiniPlayerHealthProbeAsync(state, cancellationToken);
        if (first is null
            || !IsCurrentRecoveryTarget(state, runtime, webView)
            || YouTubeMiniPlayerRecoveryService.IsDefiniteFailure(first.Value))
        {
            return false;
        }

        var requireProgress = snapshot.IsPaused.Value is false;
        if (!requireProgress)
        {
            return YouTubeMiniPlayerRecoveryService.IsRecoveredPlaybackHealthy(
                first.Value,
                second: null,
                requireProgress: false);
        }

        await _delay(
            TimeSpan.FromMilliseconds(YouTubeMiniPlayerRecoveryProbeIntervalMilliseconds),
            cancellationToken);
        var second = await CaptureYouTubeMiniPlayerHealthProbeAsync(state, cancellationToken);
        return second is not null
            && IsCurrentRecoveryTarget(state, runtime, webView)
            && YouTubeMiniPlayerRecoveryService.IsRecoveredPlaybackHealthy(
                first.Value,
                second.Value,
                requireProgress: true);
    }

    private bool IsCurrentRecoveryTarget(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        WebView2 webView)
    {
        return !runtime.IsDisposed
            && _host.Runtimes.TryGetValue(state.Id, out var currentRuntime)
            && ReferenceEquals(runtime, currentRuntime)
            && ReferenceEquals(runtime.WebView, webView);
    }

    private void RecordRecoveryOperation(Guid id, string operation)
    {
        try
        {
            _host.QueueDiagnosticLogLine(
            [
                "runner=youtube-recovery",
                $"operation={operation}",
                $"target={id}"
            ]);
        }
        catch
        {
            // 診断記録の失敗で再生復旧の成否を変えない。
        }
    }

    private bool IsRecoveryAttemptLimited(Guid miniPlayerId)
    {
        lock (_recoveryAttemptSync)
        {
            if (!_recoveryAttempts.TryGetValue(miniPlayerId, out var attempts))
            {
                return false;
            }

            PruneRecoveryAttempts(attempts, _utcNow());
            if (attempts.Count == 0)
            {
                _recoveryAttempts.Remove(miniPlayerId);
                return false;
            }

            return attempts.Count >= YouTubeMiniPlayerRecoveryAttemptLimit;
        }
    }

    private void RecordRecoveryAttempt(Guid miniPlayerId)
    {
        lock (_recoveryAttemptSync)
        {
            var now = _utcNow();
            if (!_recoveryAttempts.TryGetValue(miniPlayerId, out var attempts))
            {
                attempts = [];
                _recoveryAttempts.Add(miniPlayerId, attempts);
            }

            PruneRecoveryAttempts(attempts, now);
            attempts.Add(now);
        }
    }

    private static void PruneRecoveryAttempts(
        List<DateTimeOffset> attempts,
        DateTimeOffset now)
    {
        attempts.RemoveAll(attempt => now - attempt >= YouTubeMiniPlayerRecoveryAttemptWindow);
    }

    private async Task<bool> OpenYouTubeMiniPlayerRecoverySourceTabAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        YouTubeMiniPlayerStateService.ApplyPlaybackStateSnapshot(state, snapshot);
        var opened = await _host.ActivateRecoverySourceTabAsync(state);
        cancellationToken.ThrowIfCancellationRequested();
        return opened;
    }

    private void SetYouTubeMiniPlayerRecoveryStatus(
        YouTubeMiniPlayerRecoveryStage stage,
        string reason)
    {
        var message = stage switch
        {
            YouTubeMiniPlayerRecoveryStage.ReloadingCurrentWebView =>
                "YouTubeミニプレイヤーを再読み込みしています",
            YouTubeMiniPlayerRecoveryStage.RecreatingWebView =>
                "YouTubeミニプレイヤーのWebView2を再生成しています",
            YouTubeMiniPlayerRecoveryStage.OpeningSourceTab =>
                "通常のYouTubeタブへ切り替えています",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(message))
        {
            _host.SetStatus(message);
        }
    }

    public async Task RecoverFloatingMediaProcessGroupAsync(
        string containerProfileId,
        uint browserProcessId)
    {
        var affectedRuntimes = _host.Runtimes.Values
            .Where(runtime => !runtime.IsDisposed
                && runtime.WebView is not null
                && string.Equals(
                    ContainerProfile.NormalizeId(runtime.State.ContainerProfileId),
                    containerProfileId,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var runtime in affectedRuntimes)
        {
            if (_host.IsClosingWindow || _host.LifetimeToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(runtime.State.Url, out var uri))
                {
                    continue;
                }

                _host.ResetWebView(runtime);
                await _host.LoadRuntimeAsync(runtime.State, uri);
                _host.RecordProcessGroupResult(
                    containerProfileId, browserProcessId, "mini-player-recreated");
            }
            catch (Exception ex)
            {
                _host.AppendDiagnostic(
                    "mini-player-process-group-recover-failed",
                    ex,
                    $"state={runtime.State.Id}");
                runtime.DisplayHealth.ReportFailure();
                _host.RecordProcessGroupResult(
                    containerProfileId, browserProcessId, "mini-player-recreate-failed");
            }

            if (affectedRuntimes.Count > 1)
            {
                try
                {
                    await _delay(
                        TimeSpan.FromMilliseconds(YouTubeMiniPlayerProcessRecoveryStaggerMilliseconds),
                        _host.LifetimeToken);
                }
                catch (OperationCanceledException) when (_host.LifetimeToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
