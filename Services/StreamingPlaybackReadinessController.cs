using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record StreamingPlaybackReadinessHost(
    Func<BrowserTab, bool> ContainsTab,
    Func<BrowserTab, string?> GetCurrentPageUrl,
    Func<BrowserTab, Func<string, Task<string>>?> GetProbeExecutor,
    Action UpdateNotice);

internal sealed class StreamingPlaybackReadinessController
{
    internal static readonly TimeSpan AmazonPrimeVideoReadinessDelay = TimeSpan.FromSeconds(8);
    private readonly StreamingPlaybackTabStateCoordinator _streamingPlaybackTabStateCoordinator;
    private readonly WindowTaskSupervisor _windowTaskSupervisor;
    private readonly StreamingPlaybackReadinessHost _host;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public StreamingPlaybackReadinessController(
        StreamingPlaybackTabStateCoordinator stateCoordinator,
        WindowTaskSupervisor supervisor,
        StreamingPlaybackReadinessHost host,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _streamingPlaybackTabStateCoordinator = stateCoordinator;
        _windowTaskSupervisor = supervisor;
        _host = host;
        _delay = delay ?? Task.Delay;
    }

    public void Queue(BrowserTab tab)
    {
        var version = _streamingPlaybackTabStateCoordinator.BeginReadinessCheck(tab.Id);
        var url = _host.GetCurrentPageUrl(tab) ?? tab.Url;
        if (!AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url))
        {
            _streamingPlaybackTabStateCoordinator.ClearReadiness(tab.Id);
            _host.UpdateNotice();
            return;
        }

        _streamingPlaybackTabStateCoordinator.MarkReadinessPending(tab.Id);
        _host.UpdateNotice();
        _windowTaskSupervisor.TryRun(
            "amazon-prime-video-readiness",
            cancellationToken => CheckAsync(tab, version, cancellationToken));
    }

    private async Task CheckAsync(
        BrowserTab tab,
        int version,
        CancellationToken cancellationToken)
    {
        await _delay(AmazonPrimeVideoReadinessDelay, cancellationToken);
        if (!_host.ContainsTab(tab)
            || !_streamingPlaybackTabStateCoordinator.IsCurrentReadinessCheck(tab.Id, version)
            || _host.GetProbeExecutor(tab) is not { } executeProbe
            || !AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(_host.GetCurrentPageUrl(tab) ?? tab.Url))
        {
            return;
        }

        AmazonPrimeVideoReadiness readiness;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawResult = await executeProbe(
                AmazonPrimeVideoService.BuildReadinessProbeScript());
            cancellationToken.ThrowIfCancellationRequested();
            readiness = AmazonPrimeVideoService.TryParseProbeResult(rawResult, out var result)
                ? result.Readiness
                : AmazonPrimeVideoReadiness.ProbeFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            readiness = AmazonPrimeVideoReadiness.ProbeFailed;
        }

        if (cancellationToken.IsCancellationRequested
            || !_host.ContainsTab(tab)
            || !_streamingPlaybackTabStateCoordinator.TryCompleteReadinessCheck(
                tab.Id,
                version,
                readiness))
        {
            return;
        }

        _host.UpdateNotice();
    }
}
