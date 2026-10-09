using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IInactiveTabWorkHost
{
    bool IsInitialized { get; }
    bool IsClosing { get; }
    bool IsCpuSaverEnabled { get; }
    double CpuSaverDelaySeconds { get; }
    int WindowIndex { get; }
    BrowserTab? ActiveTab { get; }
    IList<BrowserTab> Tabs { get; }
    IReadOnlySet<Guid> GetGlobalHotTabIds();
    bool IsYouTubeVideoTab(BrowserTab tab);
    bool IsVisibleSplitTab(BrowserTab tab);
    bool HasCore(BrowserTab tab);
    bool IsInitializing(BrowserTab tab);
    Action? GetStopLoadAction(BrowserTab tab);
    Action? GetReloadAction(BrowserTab tab);
    YouTubePlaybackActivitySnapshot? GetPlaybackActivity(BrowserTab tab);
    bool? GetCurrentPlaybackPaused(BrowserTab tab);
    bool CanSuspendInactiveTab(BrowserTab tab, BrowserTab activeTab, bool isVisibleSplitTab);
    void ClearLoading(BrowserTab tab);
    Task<bool> TrySuspendTabAsync(BrowserTab tab);
    bool ResumeTabIfSuspended(BrowserTab tab);
    void RunSupervised(string operation, Func<CancellationToken, Task> action);
    void StopFocusedViewingTimer();
    void StartFocusedViewingTimer(TimeSpan interval);
    void StopAutomaticBackgroundWork();
    void QueueBackgroundTabLoading();
    void QueueBackgroundThumbnailWarmup();
    void QueuePerformanceDiagnosticLogLine(IReadOnlyList<string> fields);
}

// 非active確認と省CPU移行・復帰の手順を所有する。
// Core操作の実体と終了順付きTimerはhost、世代と診断集計は既存Coordinatorを共有する。
internal sealed class InactiveTabWorkController
{
    private const int InactiveYouTubeLoadStopDelayMilliseconds = 30000;
    private readonly IInactiveTabWorkHost _host;
    private readonly InactiveYouTubeTabWorkCoordinator _inactiveYouTubeTabWorkCoordinator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal InactiveTabWorkController(
        IInactiveTabWorkHost host,
        InactiveYouTubeTabWorkCoordinator coordinator,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _inactiveYouTubeTabWorkCoordinator = coordinator;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public bool IsCpuSaverActive { get; private set; }

    public void NoteExplorationActivity()
    {
        var wasCpuSaverActive = IsCpuSaverActive;
        IsCpuSaverActive = false;
        RestartFocusedViewingCpuSaverTimer();

        if (wasCpuSaverActive)
        {
            _host.QueueBackgroundTabLoading();
            _host.QueueBackgroundThumbnailWarmup();
        }
    }

    public void RestartFocusedViewingCpuSaverTimer()
    {
        _host.StopFocusedViewingTimer();

        if (!_host.IsInitialized || !_host.IsCpuSaverEnabled)
        {
            return;
        }

        _host.StartFocusedViewingTimer(GetFocusedViewingCpuSaverDelay());
    }

    public TimeSpan GetFocusedViewingCpuSaverDelay()
    {
        var seconds = Math.Clamp(_host.CpuSaverDelaySeconds, 15, 3600);
        return TimeSpan.FromSeconds(seconds);
    }

    public void HandleYouTubePlaybackActivityChanged(
        string sourceKind,
        Guid targetId,
        YouTubePlaybackDiagnosticMessage message)
    {
        if (!string.Equals(sourceKind, "tab", StringComparison.Ordinal)
            || _host.Tabs.FirstOrDefault(tab => tab.Id == targetId) is not { } tab)
        {
            return;
        }

        if (message.EventName is "waiting" or "stalled" or "emptied" or "error")
        {
            QueueInactiveYouTubeTabCheck(tab, stopStalledLoad: true);
            return;
        }

        if (message.IsEnded == true
            || message.IsPaused == true
            || string.Equals(message.EventName, "pause", StringComparison.Ordinal))
        {
            QueueInactiveYouTubeTabCheck(tab, stopStalledLoad: false);
            return;
        }

        if (message.IsPaused == false
            || message.EventName is "playing" or "canplay")
        {
            CancelInactiveYouTubeTabCheck(tab);
        }
    }

    public void QueueInactiveYouTubeTabCheck(
        BrowserTab tab,
        bool stopStalledLoad)
    {
        if (!_host.IsCpuSaverEnabled
            || _host.IsClosing
            || !_host.Tabs.Contains(tab)
            || !_host.IsYouTubeVideoTab(tab)
            || ReferenceEquals(_host.ActiveTab, tab)
            || _host.GetGlobalHotTabIds().Contains(tab.Id)
            || tab.IsPinned
            || _host.IsVisibleSplitTab(tab)
            || !_host.HasCore(tab))
        {
            return;
        }

        _host.RunSupervised(
            "inactive-youtube-tab-check",
            cancellationToken =>
            {
                var generation = _inactiveYouTubeTabWorkCoordinator.BeginCheck(tab.Id);
                var queuedAt = _utcNow();
                var delay = stopStalledLoad
                    ? TimeSpan.FromMilliseconds(InactiveYouTubeLoadStopDelayMilliseconds)
                    : GetFocusedViewingCpuSaverDelay();
                return RunInactiveYouTubeTabCheckAsync(
                    tab,
                    generation,
                    queuedAt,
                    delay,
                    stopStalledLoad,
                    cancellationToken);
            });
    }

    private async Task RunInactiveYouTubeTabCheckAsync(
        BrowserTab tab,
        int generation,
        DateTimeOffset queuedAt,
        TimeSpan delay,
        bool stopStalledLoad,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || _host.IsClosing
            || !_host.Tabs.Contains(tab)
            || !_inactiveYouTubeTabWorkCoordinator.IsCurrent(tab.Id, generation)
            || ReferenceEquals(_host.ActiveTab, tab)
            || _host.GetGlobalHotTabIds().Contains(tab.Id)
            || tab.IsPinned
            || _host.IsVisibleSplitTab(tab)
            || !_host.IsYouTubeVideoTab(tab)
            || _host.GetStopLoadAction(tab) is not { } stopLoad
            || _host.IsInitializing(tab))
        {
            return;
        }

        var activity = _host.GetPlaybackActivity(tab);
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

            var shouldStopLoad = tab.IsLoading
                || activity?.State is YouTubePlaybackActivityState.Waiting
                    or YouTubePlaybackActivityState.Failed;
            if (!shouldStopLoad)
            {
                return;
            }

            try
            {
                stopLoad();
                _host.ClearLoading(tab);
                tab.IsYouTubeLoadStoppedForInactivity = true;
                _inactiveYouTubeTabWorkCoordinator.RecordLoadStopped();
                _host.QueuePerformanceDiagnosticLogLine(
                [
                    "runner=youtube-inactive",
                    "operation=tab-load-stop",
                    $"window={_host.WindowIndex}",
                    $"target={tab.Id}"
                ]);
            }
            catch
            {
                return;
            }
        }
        else
        {
            var pausedFallback = _host.GetCurrentPlaybackPaused(tab);
            if (!WebViewLifecycleService.IsSafeToSuspendInactiveYouTubePlayback(
                    activity,
                    pausedFallback))
            {
                return;
            }
        }

        if (!_host.CanSuspendInactiveTab(
                tab,
                _host.ActiveTab ?? tab,
                isVisibleSplitTab: false))
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var suspended = await _host.TrySuspendTabAsync(tab);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (suspended)
        {
            _inactiveYouTubeTabWorkCoordinator.RecordSuspended();
            _host.QueuePerformanceDiagnosticLogLine(
            [
                "runner=youtube-inactive",
                "operation=tab-suspend",
                $"window={_host.WindowIndex}",
                $"target={tab.Id}"
            ]);
        }
    }

    public void CancelInactiveYouTubeTabCheck(BrowserTab tab)
    {
        _inactiveYouTubeTabWorkCoordinator.CancelCheck(tab.Id);
    }

    public void ResumeInactiveYouTubeTabForDisplay(BrowserTab tab)
    {
        CancelInactiveYouTubeTabCheck(tab);
        var resumed = _host.ResumeTabIfSuspended(tab);
        var reloading = false;
        if (tab.IsYouTubeLoadStoppedForInactivity
            && _host.GetReloadAction(tab) is { } reload)
        {
            try
            {
                tab.IsYouTubeLoadStoppedForInactivity = false;
                reload();
                reloading = true;
            }
            catch
            {
                tab.IsYouTubeLoadStoppedForInactivity = true;
            }
        }

        if (!resumed && !reloading)
        {
            return;
        }

        _inactiveYouTubeTabWorkCoordinator.RecordResumed();
        _host.QueuePerformanceDiagnosticLogLine(
        [
            "runner=youtube-inactive",
            reloading ? "operation=tab-resume-reload" : "operation=tab-resume",
            $"window={_host.WindowIndex}",
            $"target={tab.Id}"
        ]);
    }

    public void QueueFocusedViewingCpuSaver()
    {
        _host.RunSupervised(
            "focused-viewing-cpu-saver",
            EnterFocusedViewingCpuSaverAsync);
    }

    internal async Task EnterFocusedViewingCpuSaverAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!_host.IsCpuSaverEnabled || IsCpuSaverActive)
        {
            return;
        }

        var activeTab = _host.ActiveTab;
        if (activeTab is null)
        {
            RestartFocusedViewingCpuSaverTimer();
            return;
        }

        IsCpuSaverActive = true;
        _host.StopAutomaticBackgroundWork();
        var hotTabIds = _host.GetGlobalHotTabIds();

        foreach (var tab in _host.Tabs.ToList())
        {
            var isVisibleSplitTab = _host.IsVisibleSplitTab(tab);
            if (hotTabIds.Contains(tab.Id)
                || !_host.CanSuspendInactiveTab(tab, activeTab, isVisibleSplitTab))
            {
                continue;
            }

            if (_host.IsYouTubeVideoTab(tab))
            {
                var activity = _host.GetPlaybackActivity(tab);
                if (!WebViewLifecycleService.IsSafeToSuspendInactiveYouTubePlayback(
                        activity,
                        _host.GetCurrentPlaybackPaused(tab)))
                {
                    continue;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var suspended = await _host.TrySuspendTabAsync(tab);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (suspended && _host.IsYouTubeVideoTab(tab))
            {
                _inactiveYouTubeTabWorkCoordinator.RecordSuspended();
            }
        }
    }

}
