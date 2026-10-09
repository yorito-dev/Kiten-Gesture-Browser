using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal interface ITabThumbnailCaptureHost
{
    CancellationToken LifetimeToken { get; }
    bool ContainsTab(BrowserTab tab);
    bool IsActiveTab(BrowserTab tab);
    void ClearLoadFailure(BrowserTab tab);
    Task EnsureLoadedAsync(BrowserTab tab, string url);
    bool ResumeIfSuspended(BrowserTab tab);
    bool IsInitializing(BrowserTab tab);
    Task WaitForInitializationAsync(BrowserTab tab);
    Func<string, string?, CancellationToken, Task<WebThumbnailCaptureResult>>? GetCaptureAction(BrowserTab tab);
    ImageSource CreateFallbackThumbnail(BrowserTab tab);
    void SetThumbnail(BrowserTab tab, ImageSource image);
    Task TrySuspendAsync(BrowserTab tab);
    void UpdateRecentThumbnail(BrowserTab tab);
    void RecordCaptureDiagnostic(BrowserTab tab, string? url, long elapsedMilliseconds, string detail, bool isBackgroundWarmup);
}

// 実tabの画像取得手順と取得中claimを所有する。実RuntimeとCoreの所有権はhostに残す。
internal sealed class TabThumbnailCaptureController
{
    private const int ThumbnailNavigationWaitMilliseconds = 5000;
    private readonly ITabThumbnailCaptureHost _host;
    private readonly Dictionary<Guid, object> _capturingThumbnails = [];
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Task> _delay;

    internal TabThumbnailCaptureController(
        ITabThumbnailCaptureHost host,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _host = host;
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? (duration => Task.Delay(duration));
    }

    // 移管／rollback時の既存claim解除を維持する。実行中Taskの取消は追加しない。
    public void ForgetCapture(Guid tabId) => _capturingThumbnails.Remove(tabId);

    public async Task PrewarmAsync(BrowserTab tab)
    {
        if (!_host.ContainsTab(tab)
            || tab.IsNativeContentTab
            || (tab.IsLoaded && !tab.IsMemoryReleased && tab.Thumbnail is not null))
        {
            return;
        }

        tab.LastActivatedAt = _now().ToUniversalTime();
        await CaptureAsync(tab);
    }

    public async Task CaptureAsync(BrowserTab tab, bool isBackgroundWarmup = false)
    {
        var claim = new object();
        if (!_host.ContainsTab(tab) || _host.LifetimeToken.IsCancellationRequested
            || !_capturingThumbnails.TryAdd(tab.Id, claim))
        {
            return;
        }

        var shouldResuspendAfterCapture = false;
        var totalStart = Stopwatch.GetTimestamp();
        var result = "skipped";
        long? captureElapsedMilliseconds = null;
        var requestedUrl = tab.Url;
        var requestedContainer = tab.ContainerProfileId;
        bool OwnsClaim() => _capturingThumbnails.TryGetValue(tab.Id, out var current) && ReferenceEquals(current, claim);
        bool IsCurrent() => OwnsClaim() && _host.ContainsTab(tab) && !_host.LifetimeToken.IsCancellationRequested
            && string.Equals(tab.Url, requestedUrl, StringComparison.Ordinal)
            && string.Equals(tab.ContainerProfileId, requestedContainer, StringComparison.Ordinal);

        try
        {
            if (tab.IsNativeContentTab)
            {
                result = "native-content";
                return;
            }

            if (!tab.IsLoaded && !string.IsNullOrWhiteSpace(tab.Url))
            {
                _host.ClearLoadFailure(tab);
                await _host.EnsureLoadedAsync(tab, tab.Url);
            }

            shouldResuspendAfterCapture = !_host.IsActiveTab(tab)
                && _host.ResumeIfSuspended(tab);

            await WaitForTabNavigationToSettleAsync(tab);

            if (!IsCurrent())
            {
                result = "removed";
                return;
            }

            if (_host.GetCaptureAction(tab) is not { } capture)
            {
                _host.SetThumbnail(tab, _host.CreateFallbackThumbnail(tab));
                result = "fallback-no-core";
                return;
            }

            var captureStart = Stopwatch.GetTimestamp();
            var captureResult = await capture(
                tab.DisplayTitle,
                tab.Url,
                _host.LifetimeToken);
            if (!IsCurrent())
            {
                return;
            }

            captureElapsedMilliseconds = GetElapsedMilliseconds(captureStart);
            tab.PageDescription = captureResult.PageDescription;
            var thumbnail = captureResult.Thumbnail;
            if (thumbnail is null)
            {
                _host.SetThumbnail(tab, _host.CreateFallbackThumbnail(tab));
                result = "fallback-low-information";
                return;
            }

            _host.SetThumbnail(tab, thumbnail);
            result = "ok";
        }
        catch
        {
            if (IsCurrent()) _host.SetThumbnail(tab, _host.CreateFallbackThumbnail(tab));
            result = "fallback-error";
        }
        finally
        {
            if (IsCurrent())
            {
                _host.RecordCaptureDiagnostic(
                    tab,
                    tab.Url,
                    GetElapsedMilliseconds(totalStart),
                    $"result={result};captureMs={captureElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}",
                    isBackgroundWarmup);

                if (shouldResuspendAfterCapture && !_host.IsActiveTab(tab))
                {
                    await _host.TrySuspendAsync(tab);
                }
            }

            var publishRecent = IsCurrent();
            if (OwnsClaim()) _capturingThumbnails.Remove(tab.Id);
            if (publishRecent) _host.UpdateRecentThumbnail(tab);
        }
    }

    private async Task WaitForTabNavigationToSettleAsync(BrowserTab tab)
    {
        var deadline = _now().AddMilliseconds(ThumbnailNavigationWaitMilliseconds);

        var initializationWait = deadline - _now();
        if (_host.ContainsTab(tab)
            && _host.IsInitializing(tab)
            && initializationWait > TimeSpan.Zero)
        {
            try
            {
                await _host.WaitForInitializationAsync(tab)
                    .WaitAsync(initializationWait);
            }
            catch (TimeoutException)
            {
            }
        }

        while (_host.ContainsTab(tab)
               && tab.IsLoading
               && _now() < deadline)
        {
            await _delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static long GetElapsedMilliseconds(long startTimestamp)
        => (long)Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
}
