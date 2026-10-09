using TabBrowser.Models;
using Microsoft.Web.WebView2.Wpf;

namespace TabBrowser.Services;

internal interface IYouTubeMiniPlayerActivityHost
{
    bool IsQualityEnabled { get; }
    int LowQualityWidthThreshold { get; }
    int HighQualityWidthThreshold { get; }
    bool IsCpuSaverEnabled { get; }
    TimeSpan CpuSaverDelay { get; }
    bool IsClosingWindow { get; }
    CancellationToken LifetimeToken { get; }
    int WindowIndex { get; }
    bool TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime);
    bool HasReadyCore(YouTubeMiniPlayerRuntime runtime);
    Func<string, Task<string>>? TryGetScriptExecutor(YouTubeMiniPlayerRuntime runtime);
    double GetWebHostWidth(YouTubeMiniPlayerRuntime runtime);
    double GetTaskbarWindowWidth(YouTubeMiniPlayerRuntime runtime);
    bool IsChatVisible(Guid id);
    YouTubePlaybackActivitySnapshot? GetPlaybackActivity(Guid id);
    bool StopLoadForInactivity(YouTubeMiniPlayerRuntime runtime);
    Task<bool> TrySuspendForInactivityAsync(YouTubeMiniPlayerRuntime runtime);
    YouTubeMiniPlayerInactivityResumeResult ResumeFromInactivity(YouTubeMiniPlayerRuntime runtime);
    void QueueLayoutVerification(YouTubeMiniPlayerState state);
    void CancelPendingRecovery(Guid id);
    void QueueDiagnosticLogLine(IReadOnlyList<string> fields);
    void BringLiveChatToFront(Guid id);
    Task RefreshLiveChatAsync(YouTubeMiniPlayerState state, WebView2 webView);
    void QueueSave();
    void MarkEventStateSavePending();
    void ResumeDeferredBackgroundWork();
    void ReportPlaybackDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message);
    void HandleRecoveryDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message);
}

// 表示幅に応じた品質調整と、非アクティブ時の休止・復帰手順をまとめる。
// Timerと世代はRuntimeが保持し、WebViewの停止・破棄は既存Lifecycleに委譲する。
internal sealed class YouTubeMiniPlayerActivityController
{
    private const int YouTubeMiniPlayerSizeBasedQualityResizeDebounceMilliseconds = 300;
    private const int YouTubeMiniPlayerInactiveLoadStopDelayMilliseconds = 30000;
    private readonly IYouTubeMiniPlayerActivityHost _host;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _inactiveYouTubeMiniPlayerSuspendCount;
    private int _inactiveYouTubeMiniPlayerLoadStopCount;
    private int _inactiveYouTubeMiniPlayerResumeCount;

    public YouTubeMiniPlayerActivityController(
        IYouTubeMiniPlayerActivityHost host,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public int InactiveSuspendCount => _inactiveYouTubeMiniPlayerSuspendCount;
    public int InactiveLoadStopCount => _inactiveYouTubeMiniPlayerLoadStopCount;
    public int InactiveResumeCount => _inactiveYouTubeMiniPlayerResumeCount;

    public void SetMinimized(YouTubeMiniPlayerState state, bool isMinimized, Action<YouTubeMiniPlayerState, bool> apply)
    {
        apply(state, isMinimized);
        if (_host.TryGetRuntime(state.Id, out var runtime))
        {
            ApplyMinimizedActivity(state, runtime, isMinimized);
        }
    }

    public void ApplyMinimizedState(YouTubeMiniPlayerRuntime runtime, bool activate, Action<YouTubeMiniPlayerRuntime, bool> apply)
    {
        apply(runtime, activate);
        ApplyMinimizedActivity(runtime.State, runtime, runtime.State.IsMinimized);
    }

    private void ApplyMinimizedActivity(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, bool isMinimized)
    {
        if (!isMinimized)
        {
            var result = ResumeYouTubeMiniPlayerForInteractionCore(runtime);
            if (!result.Resumed && !result.Reloading)
            {
                _host.QueueLayoutVerification(state);
            }
        }
        else if (runtime.IsNavigationLoading)
        {
            QueueYouTubeMiniPlayerInactiveSuspension(state, stopStalledLoad: true);
        }
        else if (state.IsPaused == true)
        {
            QueueYouTubeMiniPlayerInactiveSuspension(state, stopStalledLoad: false);
        }
    }

    public void BringToFront(Guid id, bool reopenPopup, Action<Guid, bool> bringToFront)
    {
        if (_host.TryGetRuntime(id, out var runtime))
        {
            ResumeYouTubeMiniPlayerForInteraction(runtime);
        }

        bringToFront(id, reopenPopup);
        _host.BringLiveChatToFront(id);
    }

    public void HandlePlaybackDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message)
    {
        var observedAt = _utcNow();
        var previousPaused = state.IsPaused;
        var stateChanged = YouTubeMiniPlayerStateService.ApplyPlaybackDiagnosticMessage(state, message, observedAt);
        if (_host.TryGetRuntime(state.Id, out var playbackRuntime))
        {
            if (string.Equals(message.EventName, "state", StringComparison.Ordinal))
            {
                playbackRuntime.LastPlaybackStateMessageAt = observedAt;
            }

            if (message.EventName is "canplay" or "playing" && playbackRuntime.WebView is { } webView)
            {
                _ = _host.RefreshLiveChatAsync(state, webView);
            }

            if (message.EventName is "state" or "canplay" or "playing")
            {
                QueueYouTubeMiniPlayerSizeBasedQualityUpdate(playbackRuntime, immediate: true);
            }
        }

        if (stateChanged && !string.Equals(message.Reason, "timeupdate", StringComparison.Ordinal))
        {
            _host.QueueSave();
        }
        else if (stateChanged)
        {
            _host.MarkEventStateSavePending();
        }

        if (previousPaused != state.IsPaused)
        {
            _host.ResumeDeferredBackgroundWork();
        }

        _host.ReportPlaybackDiagnostic(state, message);
        if (_host.TryGetRuntime(state.Id, out var activityRuntime))
        {
            if (message.IsPaused == false && message.EventName is "playing" or "canplay" or "state")
            {
                activityRuntime.InactiveSuspendGeneration++;
            }
            else if (state.IsMinimized
                && (message.IsEnded == true || message.IsPaused == true
                    || string.Equals(message.Reason, "ended", StringComparison.Ordinal)
                    || string.Equals(message.Reason, "pause", StringComparison.Ordinal)))
            {
                QueueYouTubeMiniPlayerInactiveSuspension(state, stopStalledLoad: false);
            }
        }

        _host.HandleRecoveryDiagnostic(state, message);
    }

    public void QueueYouTubeMiniPlayerSizeBasedQualityUpdate(
        YouTubeMiniPlayerRuntime runtime,
        bool immediate = false,
        bool force = false)
    {
        if (runtime.IsDisposed
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            return;
        }

        if (immediate)
        {
            runtime.SizeBasedQualityUpdateTimer?.Stop();
            _ = ApplyYouTubeMiniPlayerSizeBasedQualityAsync(runtime, force);
            return;
        }

        if (runtime.SizeBasedQualityUpdateTimer is null)
        {
            runtime.SizeBasedQualityUpdateTimer = new OwnedDispatcherTimer(
                DispatcherTimerOwner.MiniPlayerRuntime,
                DispatcherTimerTerminationPhase.RuntimeDispose)
            {
                Interval = TimeSpan.FromMilliseconds(
                    YouTubeMiniPlayerSizeBasedQualityResizeDebounceMilliseconds)
            };
            runtime.SizeBasedQualityUpdateTimer.Tick += (_, _) =>
            {
                runtime.SizeBasedQualityUpdateTimer.Stop();
                _ = ApplyYouTubeMiniPlayerSizeBasedQualityAsync(runtime, force: false);
            };
        }

        runtime.SizeBasedQualityUpdateTimer.Stop();
        runtime.SizeBasedQualityUpdateTimer.Start();
    }

    private async Task ApplyYouTubeMiniPlayerSizeBasedQualityAsync(
        YouTubeMiniPlayerRuntime runtime,
        bool force)
    {
        if (runtime.IsDisposed
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube
            || _host.TryGetScriptExecutor(runtime) is not { } executeScriptAsync)
        {
            return;
        }

        var isEnabled = _host.IsQualityEnabled;
        if (!isEnabled && runtime.SizeBasedQualityAppliedSignature is null)
        {
            return;
        }

        var displayWidth = _host.GetWebHostWidth(runtime);
        if (!double.IsFinite(displayWidth) || displayWidth <= 0)
        {
            displayWidth = _host.GetTaskbarWindowWidth(runtime);
        }
        if (!double.IsFinite(displayWidth) || displayWidth <= 0)
        {
            displayWidth = runtime.State.Width;
        }

        var qualityCap = YouTubeControlScriptService.ResolveMiniPlayerQualityCap(
            displayWidth,
            runtime.IsFullScreen,
            _host.LowQualityWidthThreshold,
            _host.HighQualityWidthThreshold);
        var signature = $"{isEnabled}:{qualityCap}";
        if (!force
            && string.Equals(
                runtime.SizeBasedQualityAppliedSignature,
                signature,
                StringComparison.Ordinal))
        {
            return;
        }

        runtime.SizeBasedQualityAppliedSignature = signature;
        try
        {
            await executeScriptAsync(
                YouTubeControlScriptService.BuildMiniPlayerSizeBasedQualityScript(
                    isEnabled,
                    displayWidth,
                    runtime.IsFullScreen,
                    _host.LowQualityWidthThreshold,
                    _host.HighQualityWidthThreshold));
        }
        catch
        {
            if (string.Equals(
                    runtime.SizeBasedQualityAppliedSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                runtime.SizeBasedQualityAppliedSignature = null;
            }
        }
    }

    public void QueueYouTubeMiniPlayerInactiveSuspension(
        YouTubeMiniPlayerState state,
        bool stopStalledLoad)
    {
        if (!_host.IsCpuSaverEnabled
            || _host.IsClosingWindow
            || state.SourceKind != MiniPlayerSourceKind.YouTube
            || !state.IsMinimized
            || !_host.TryGetRuntime(state.Id, out var runtime)
            || runtime.IsDisposed
            || runtime.IsFullScreen
            || !_host.HasReadyCore(runtime))
        {
            return;
        }

        var generation = ++runtime.InactiveSuspendGeneration;
        var queuedAt = _utcNow();
        var delay = stopStalledLoad
            ? TimeSpan.FromMilliseconds(YouTubeMiniPlayerInactiveLoadStopDelayMilliseconds)
            : _host.CpuSaverDelay;
        _ = RunYouTubeMiniPlayerInactiveSuspensionAsync(
            state,
            runtime,
            generation,
            queuedAt,
            delay,
            stopStalledLoad);
    }

    private async Task RunYouTubeMiniPlayerInactiveSuspensionAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        int generation,
        DateTimeOffset queuedAt,
        TimeSpan delay,
        bool stopStalledLoad)
    {
        try
        {
            await _delay(delay, _host.LifetimeToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_host.IsClosingWindow
            || runtime.IsDisposed
            || runtime.InactiveSuspendGeneration != generation
            || !state.IsMinimized
            || runtime.IsFullScreen
            || !_host.TryGetRuntime(state.Id, out var currentRuntime)
            || !ReferenceEquals(runtime, currentRuntime)
            || _host.IsChatVisible(state.Id))
        {
            return;
        }

        var activity = _host.GetPlaybackActivity(state.Id);
        if (activity?.LastProgressAt is { } lastProgressAt && lastProgressAt > queuedAt)
        {
            return;
        }

        if (stopStalledLoad)
        {
            if (!WebViewLifecycleService.IsSafeToStopInactiveYouTubeLoad(activity))
            {
                return;
            }

            var shouldStopLoad = runtime.IsNavigationLoading
                || activity?.State is YouTubePlaybackActivityState.Waiting
                    or YouTubePlaybackActivityState.Failed
                || runtime.DisplayHealth.GetSnapshot().State
                    is YouTubeMiniPlayerDisplayHealthState.Suspected
                        or YouTubeMiniPlayerDisplayHealthState.ReloadRecommended;
            if (!shouldStopLoad
                || !_host.StopLoadForInactivity(runtime))
            {
                return;
            }

            _inactiveYouTubeMiniPlayerLoadStopCount++;
            _host.QueueDiagnosticLogLine(
            [
                "runner=youtube-inactive",
                "operation=mini-player-load-stop",
                $"window={_host.WindowIndex}",
                $"target={state.Id}"
            ]);
        }
        else if (!WebViewLifecycleService.IsSafeToSuspendInactiveYouTubePlayback(
                     activity,
                     state.IsPaused))
        {
            return;
        }

        _host.CancelPendingRecovery(state.Id);
        if (await _host.TrySuspendForInactivityAsync(runtime))
        {
            _inactiveYouTubeMiniPlayerSuspendCount++;
            _host.QueueDiagnosticLogLine(
            [
                "runner=youtube-inactive",
                "operation=mini-player-suspend",
                $"window={_host.WindowIndex}",
                $"target={state.Id}"
            ]);
        }
    }

    public void ResumeYouTubeMiniPlayerForInteraction(YouTubeMiniPlayerRuntime runtime)
        => ResumeYouTubeMiniPlayerForInteractionCore(runtime);

    private YouTubeMiniPlayerInactivityResumeResult ResumeYouTubeMiniPlayerForInteractionCore(
        YouTubeMiniPlayerRuntime runtime)
    {
        var result = _host.ResumeFromInactivity(runtime);
        if (!result.Resumed && !result.Reloading)
        {
            return result;
        }

        if (result.Resumed && !result.Reloading)
        {
            _host.QueueLayoutVerification(runtime.State);
        }

        _inactiveYouTubeMiniPlayerResumeCount++;
        _host.QueueDiagnosticLogLine(
        [
            "runner=youtube-inactive",
            result.Reloading
                ? "operation=mini-player-resume-reload"
                : "operation=mini-player-resume",
            $"window={_host.WindowIndex}",
            $"target={runtime.State.Id}"
        ]);
        return result;
    }
}
