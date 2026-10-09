using System.Diagnostics;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IBackgroundTabWorkHost
{
    bool IsInitialized { get; }
    bool IsClosing { get; }
    bool IsFocusedViewingCpuSaverActive { get; }
    BrowserTab? ActiveTab { get; }
    IList<BrowserTab> Tabs { get; }
    CancellationToken LifetimeToken { get; }
    bool IsRightWheelTabCyclingActive();
    bool HasPlayingMediaAcrossWindows();
    bool ShouldPauseBackgroundLoad(BrowserTab? activeTab);
    bool ShouldPauseBackgroundThumbnailWarmup(BrowserTab? activeTab);
    bool IsBackgroundLoadCandidate(BrowserTab tab, BrowserTab? activeTab);
    bool IsBackgroundThumbnailCandidate(BrowserTab tab, BrowserTab? activeTab);
    bool HasLoadFailure(BrowserTab tab);
    IReadOnlySet<Guid> GetGlobalHotTabIds();
    void RunBackgroundTask(string operation, Func<Task> action);
    void StopDelayTimer();
    void RestartDelayTimer(TimeSpan interval);
    Task EnsureLoadedAsync(BrowserTab tab, string? url);
    Task CaptureBackgroundThumbnailAsync(BrowserTab tab);
    void MarkStartupPerformance(string stage, BrowserTab tab);
    void RecordLoadDiagnostic(string category, BrowserTab tab, string? url, long elapsedMilliseconds, string detail);
    string? CreateRestoredTabLoadFailureStatus();
    void SetStatus(string status);
    void QueueWorkspaceOpenTabsRefresh();
}

// 背景読込と画像準備の候補選択・待機・再検証・再予約を所有する。
// Runtime、全Windowの実状態、終了順付きTimerとTask寿命は既存hostを使う。
internal sealed class BackgroundTabWorkController
{
    private const int StartupBackgroundLoadDelayMilliseconds = 5000;
    private const int BackgroundLoadActiveTabRetryMilliseconds = 1000;
    private const int BackgroundLoadActiveTabMaximumWaitMilliseconds = 15000;
    private const int BackgroundTabLoadIntervalMilliseconds = 1200;
    private const int BackgroundThumbnailWarmupIntervalMilliseconds = 1800;
    private readonly IBackgroundTabWorkHost _host;
    private readonly BackgroundTabWorkCoordinator _state;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal BackgroundTabWorkController(
        IBackgroundTabWorkHost host,
        BackgroundTabWorkCoordinator state,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _state = state;
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public void ResumeDeferredAutomaticWork()
    {
        if (!_state.TryResumeAutomaticWorkAfterMediaPlayback(
                out var requestTabLoad,
                out var requestThumbnailWarmup))
        {
            return;
        }

        if (requestTabLoad)
        {
            QueueBackgroundTabLoading();
        }

        if (requestThumbnailWarmup)
        {
            QueueBackgroundThumbnailWarmup();
        }
    }

    private bool ShouldDeferAutomaticBackgroundWorkForMediaPlayback()
    {
        if (!_host.HasPlayingMediaAcrossWindows())
        {
            return false;
        }

        _state.MarkAutomaticWorkDeferredForMediaPlayback();
        return true;
    }

    public void QueueBackgroundTabLoading()
    {
        if (!_host.IsInitialized || _host.IsClosing)
        {
            return;
        }

        _state.RequestTabLoad();
        var hasUserRequestedBackgroundLoad = GetNextUserRequestedBackgroundLoadCandidate() is not null;
        if (!hasUserRequestedBackgroundLoad
            && ShouldDeferAutomaticBackgroundWorkForMediaPlayback())
        {
            return;
        }

        if (_host.IsFocusedViewingCpuSaverActive || _host.IsRightWheelTabCyclingActive())
        {
            return;
        }

        if (!hasUserRequestedBackgroundLoad && TryScheduleDeferredBackgroundWork())
        {
            return;
        }

        var activeTabWaitDecision = _state.EvaluateActiveTabWait(
            _host.ShouldPauseBackgroundLoad(_host.ActiveTab),
            _now(),
            TimeSpan.FromMilliseconds(BackgroundLoadActiveTabMaximumWaitMilliseconds));
        if (activeTabWaitDecision == ActiveTabBackgroundLoadDecision.Retry)
        {
            _host.RestartDelayTimer(TimeSpan.FromMilliseconds(BackgroundLoadActiveTabRetryMilliseconds));
            return;
        }

        if (!_state.IsTabLoading)
        {
            _host.RunBackgroundTask("background-tab-load", LoadBackgroundTabsAsync);
        }
    }

    public void QueueUserRequestedBackgroundTabLoading(BrowserTab tab)
    {
        _state.EnqueueUserRequestedTabLoad(tab.Id);
        QueueBackgroundTabLoading();
    }

    private BrowserTab? GetNextUserRequestedBackgroundLoadCandidate()
    {
        while (_state.TryPeekUserRequestedTabLoad(out var tabId))
        {
            var tab = _host.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab is not null
                && _host.IsBackgroundLoadCandidate(
                    tab,
                    _host.ActiveTab))
            {
                return tab;
            }

            _state.DiscardUserRequestedTabLoad(tabId);
        }

        return null;
    }

    private void CompleteUserRequestedBackgroundTabLoad(BrowserTab tab)
    {
        _state.CompleteUserRequestedTabLoad(tab.Id);
    }

    public void DeferBackgroundTabLoading()
    {
        DeferBackgroundWork(StartupBackgroundLoadDelayMilliseconds);
    }

    public void DeferBackgroundWork(int delayMilliseconds)
    {
        _state.Defer(
            _now(),
            TimeSpan.FromMilliseconds(delayMilliseconds));
        TryScheduleDeferredBackgroundWork();
    }

    private bool TryScheduleDeferredBackgroundWork()
    {
        var deferralState = _state.EvaluateDeferral(
            _now(),
            out var remaining);
        if (deferralState == BackgroundWorkDeferralState.None)
        {
            return false;
        }

        if (deferralState == BackgroundWorkDeferralState.Expired)
        {
            _host.StopDelayTimer();
            return false;
        }

        _host.RestartDelayTimer(remaining);
        return true;
    }

    internal async Task LoadBackgroundTabsAsync()
    {
        if (!_state.TryBeginTabLoading())
        {
            return;
        }

        var cancellationToken = _host.LifetimeToken;

        try
        {
            do
            {
                _state.BeginTabLoadingPass();

                while (true)
                {
                    var userRequestedTab = GetNextUserRequestedBackgroundLoadCandidate();
                    var isUserRequestedBackgroundLoad = userRequestedTab is not null;
                    if (!isUserRequestedBackgroundLoad
                        && ShouldDeferAutomaticBackgroundWorkForMediaPlayback())
                    {
                        _state.RequestTabLoad();
                        return;
                    }

                    if (_host.IsFocusedViewingCpuSaverActive || _host.IsRightWheelTabCyclingActive())
                    {
                        _state.SetTabLoadRetry(_host.IsRightWheelTabCyclingActive());
                        return;
                    }

                    if (!isUserRequestedBackgroundLoad && TryScheduleDeferredBackgroundWork())
                    {
                        _state.RequestTabLoad();
                        return;
                    }

                    if (_state.ShouldPauseCurrentTabLoadingPass(
                            _host.ShouldPauseBackgroundLoad(_host.ActiveTab)))
                    {
                        _state.RequestTabLoad();
                        return;
                    }

                    var hotTabIds = isUserRequestedBackgroundLoad ? null : _host.GetGlobalHotTabIds();
                    var tab = userRequestedTab ?? _host.Tabs.FirstOrDefault(x =>
                        hotTabIds!.Contains(x.Id)
                        && _host.IsBackgroundLoadCandidate(
                            x,
                            _host.ActiveTab));

                    if (tab is null)
                    {
                        break;
                    }

                    await _delay(TimeSpan.FromMilliseconds(BackgroundTabLoadIntervalMilliseconds), cancellationToken);
                    if (_host.IsClosing
                        || (!isUserRequestedBackgroundLoad
                            && ShouldDeferAutomaticBackgroundWorkForMediaPlayback())
                        || _host.IsRightWheelTabCyclingActive()
                        || (!isUserRequestedBackgroundLoad && TryScheduleDeferredBackgroundWork())
                        || !_host.Tabs.Contains(tab)
                        || (isUserRequestedBackgroundLoad
                            && !_state.ContainsUserRequestedTabLoad(tab.Id))
                        || (!isUserRequestedBackgroundLoad
                            && !_host.GetGlobalHotTabIds().Contains(tab.Id))
                        || !_host.IsBackgroundLoadCandidate(
                            tab,
                            _host.ActiveTab))
                    {
                        continue;
                    }

                    if (isUserRequestedBackgroundLoad)
                    {
                        CompleteUserRequestedBackgroundTabLoad(tab);
                    }

                    var loadStart = Stopwatch.GetTimestamp();
                    _host.MarkStartupPerformance(StartupPerformanceStage.BackgroundLoadStarted, tab);
                    await _host.EnsureLoadedAsync(tab, tab.Url);
                    _host.MarkStartupPerformance(StartupPerformanceStage.BackgroundLoadCompleted, tab);
                    _host.RecordLoadDiagnostic(
                        "webview-background-load",
                        tab,
                        tab.Url,
                        GetElapsedMilliseconds(loadStart),
                        $"failed={_host.HasLoadFailure(tab)}");
                    if (_host.HasLoadFailure(tab)
                        && _host.CreateRestoredTabLoadFailureStatus() is { } restoredTabLoadFailureStatus)
                    {
                        _host.SetStatus(restoredTabLoadFailureStatus);
                    }

                    QueueBackgroundThumbnailWarmup();
                }
            }
            while (_state.IsTabLoadRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _state.ClearTabLoadRequest();
        }
        finally
        {
            if (_state.CompleteTabLoading() && !_host.IsClosing)
            {
                QueueBackgroundTabLoading();
            }
        }
    }

    public void QueueBackgroundThumbnailWarmup()
    {
        if (!_host.IsInitialized || _host.IsClosing)
        {
            return;
        }

        _state.RequestThumbnailWarmup();
        if (ShouldDeferAutomaticBackgroundWorkForMediaPlayback()
            || _host.IsFocusedViewingCpuSaverActive
            || _host.IsRightWheelTabCyclingActive())
        {
            return;
        }

        if (_host.ShouldPauseBackgroundThumbnailWarmup(_host.ActiveTab))
        {
            return;
        }

        if (TryScheduleDeferredBackgroundWork())
        {
            return;
        }

        if (!_state.IsThumbnailWarmupRunning)
        {
            _host.RunBackgroundTask("background-thumbnail-warmup", WarmBackgroundThumbnailsAsync);
        }
    }

    internal async Task WarmBackgroundThumbnailsAsync()
    {
        if (!_state.TryBeginThumbnailWarmup())
        {
            return;
        }

        var cancellationToken = _host.LifetimeToken;

        try
        {
            do
            {
                _state.BeginThumbnailWarmupPass();

                while (true)
                {
                    if (ShouldDeferAutomaticBackgroundWorkForMediaPlayback())
                    {
                        _state.RequestThumbnailWarmup();
                        return;
                    }

                    if (_host.IsClosing
                        || _host.IsFocusedViewingCpuSaverActive
                        || _host.IsRightWheelTabCyclingActive())
                    {
                        _state.SetThumbnailWarmupRetry(
                            _host.IsRightWheelTabCyclingActive());
                        return;
                    }

                    if (_host.ShouldPauseBackgroundThumbnailWarmup(_host.ActiveTab))
                    {
                        _state.RequestThumbnailWarmup();
                        return;
                    }

                    if (TryScheduleDeferredBackgroundWork())
                    {
                        _state.RequestThumbnailWarmup();
                        return;
                    }

                    var activeTab = _host.ActiveTab;
                    var tab = _host.Tabs.FirstOrDefault(candidate =>
                        _host.IsBackgroundThumbnailCandidate(candidate, activeTab));

                    if (tab is null)
                    {
                        break;
                    }

                    await _delay(TimeSpan.FromMilliseconds(BackgroundThumbnailWarmupIntervalMilliseconds), cancellationToken);
                    if (ShouldDeferAutomaticBackgroundWorkForMediaPlayback()
                        || _host.IsRightWheelTabCyclingActive()
                        || TryScheduleDeferredBackgroundWork()
                        || _host.ShouldPauseBackgroundThumbnailWarmup(_host.ActiveTab))
                    {
                        _state.RequestThumbnailWarmup();
                        return;
                    }

                    if (!_host.Tabs.Contains(tab)
                        || !_host.IsBackgroundThumbnailCandidate(tab, _host.ActiveTab))
                    {
                        continue;
                    }

                    await _host.CaptureBackgroundThumbnailAsync(tab);
                    _host.QueueWorkspaceOpenTabsRefresh();
                }
            }
            while (_state.IsThumbnailWarmupRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _state.ClearThumbnailWarmupRequest();
        }
        finally
        {
            if (_state.CompleteThumbnailWarmup() && !_host.IsClosing)
            {
                QueueBackgroundThumbnailWarmup();
            }
        }
    }

    private static long GetElapsedMilliseconds(long startTimestamp)
        => (long)Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
}
