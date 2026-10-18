using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow
{

    private readonly StreamingPlaybackReadinessController _streamingPlaybackReadinessController;
    private static readonly TimeSpan StreamingPlaybackNoticeWindowChangeDelay =
        TimeSpan.FromMilliseconds(200);
    private readonly StreamingPlaybackTabStateCoordinator _streamingPlaybackTabStateCoordinator = new();
    private readonly StreamingPlaybackNoticeController _streamingPlaybackNoticeController;
    private readonly OwnedDispatcherTimer
        _streamingPlaybackNoticeWindowChangeTimer = new(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.WindowShutdown)
        {
            Interval = StreamingPlaybackNoticeWindowChangeDelay
        };

    private void InitializeStreamingPlaybackNoticeWindowTracking()
    {
        RegisterWindowShutdownPhaseTimer(_streamingPlaybackNoticeWindowChangeTimer);
        _streamingPlaybackNoticeController.InitializeWindowTracking();
    }

    private void QueueStreamingPlaybackNoticeAfterWindowChange()
        => _streamingPlaybackNoticeController.QueueAfterWindowChange();

    private void QueueAmazonPrimeVideoReadinessCheck(BrowserTab tab)
        => _streamingPlaybackReadinessController.Queue(tab);

    private Func<string, Task<string>>? GetStreamingPlaybackReadinessProbeExecutor(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2 is { } core ? core.ExecuteScriptAsync : null;

    private void MarkAmazonPrimeVideoNavigationFailed(BrowserTab tab)
    {
        if (!IsConfirmedAmazonPrimeVideoTab(tab))
        {
            return;
        }

        _streamingPlaybackTabStateCoordinator.MarkNavigationFailed(tab.Id);
        UpdateAmazonPrimeVideoNotice();
    }

    private void MarkAmazonPrimeVideoNavigationStarting(BrowserTab tab, string? targetUrl)
    {

        _streamingPlaybackTabStateCoordinator.MarkNavigationStarting(tab.Id, null);

    }

    private void UpdateAmazonPrimeVideoNotice()
        => _streamingPlaybackNoticeController.UpdateNotice();

    private void AmazonPrimeVideoNotice_Opened(object sender, EventArgs e)
        => PositionAmazonPrimeVideoNotice();

    private void AmazonPrimeVideoNoticePanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => PositionAmazonPrimeVideoNotice();

    private void PositionAmazonPrimeVideoNotice()
        => _streamingPlaybackNoticeController?.PositionNotice();

    private void ClearAmazonPrimeVideoState(BrowserTab tab)
    {
        _streamingPlaybackTabStateCoordinator.Remove(tab.Id);

        UpdateAmazonPrimeVideoNotice();
    }

    private void MarkStreamingPlaybackFailed(
        BrowserTab tab,
        StreamingPlaybackFailureMessage failure)
    {
        _ = failure;

        _streamingPlaybackTabStateCoordinator.MarkPlaybackFailed(tab.Id);
        UpdateAmazonPrimeVideoNotice();

    }

    private void AmazonPrimeVideoDetailsButton_Click(object sender, RoutedEventArgs e)
        => _streamingPlaybackNoticeController.ExpandDetails();

    private void AmazonPrimeVideoDismissButton_Click(object sender, RoutedEventArgs e)
        => _streamingPlaybackNoticeController.Dismiss();

    private bool IsConfirmedAmazonPrimeVideoTab(BrowserTab tab)
    {
        var url = GetCurrentPageUrl(tab) ?? tab.Url;

        if (AmazonPrimeVideoService.IsPlaybackPageUrl(url))
        {
            return true;
        }

        var readiness = _streamingPlaybackTabStateCoordinator.GetReadiness(tab.Id);
        return AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url)
            && readiness is not AmazonPrimeVideoReadiness.Unknown
            and not AmazonPrimeVideoReadiness.Pending;
    }

    private void AmazonPrimeVideoOpenButton_Click(object sender, RoutedEventArgs e)
        => _streamingPlaybackNoticeController.OpenExternally();

    private async void AmazonPrimeVideoReloadButton_Click(object sender, RoutedEventArgs e)
        => await _streamingPlaybackNoticeController.ReloadAsync();

    bool IStreamingPlaybackNoticeHost.IsPrivateWindow => _isPrivateWindow;
    BrowserTab? IStreamingPlaybackNoticeHost.GetActiveBrowserCommandTarget() => GetActiveBrowserCommandTarget();
    bool IStreamingPlaybackNoticeHost.IsConfirmedAmazonPrimeVideoTab(BrowserTab tab) => IsConfirmedAmazonPrimeVideoTab(tab);
    string? IStreamingPlaybackNoticeHost.GetCurrentPageUrl(BrowserTab tab) => GetCurrentPageUrl(tab);
    void IStreamingPlaybackNoticeHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IStreamingPlaybackNoticeHost.QueueAmazonPrimeVideoReadinessCheck(BrowserTab tab) => QueueAmazonPrimeVideoReadinessCheck(tab);
    Task IStreamingPlaybackNoticeHost.ReloadActiveTabAsync(BrowserTab tab) => ReloadActiveTabAsync(tab);
}
