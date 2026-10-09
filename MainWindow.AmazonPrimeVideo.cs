using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow
{
    private const string StreamingPlaybackCompatibilityScriptKey =
        "streaming-playback-compatibility-retry";
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
        var targetRetryKey = StreamingPlaybackCompatibilityService.TryGetCompatibilityRetryTarget(
                targetUrl,
                out _,
                out var targetKey)
            ? targetKey
            : null;
        _streamingPlaybackTabStateCoordinator.MarkNavigationStarting(tab.Id, targetRetryKey);
    }

    private void MarkAmazonPrimeVideoCompatibilityActive(BrowserTab tab, string? source)
    {
        if (!_tabs.Contains(tab)
            || !AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(source))
        {
            return;
        }

        _streamingPlaybackTabStateCoordinator.MarkAmazonCompatibilityActive(tab.Id);
    }

    private async Task RestoreWebViewBridgeCompatibilityStateAsync(
        BrowserTab tab,
        CancellationToken cancellationToken)
    {
        var url = GetCurrentPageUrl(tab) ?? tab.Url;
        if (!_tabs.Contains(tab)
            || (!AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url)
                && !StreamingPlaybackCompatibilityService.IsEligiblePageUrl(url))
            || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await core.ExecuteScriptAsync(
                AmazonPrimeVideoService.BuildWebViewBridgeSuppressionProbeScript());
            cancellationToken.ThrowIfCancellationRequested();
            if (_tabs.Contains(tab)
                && ReferenceEquals(GetTabWebView(tab)?.CoreWebView2, core)
                && string.Equals(GetCurrentPageUrl(tab) ?? tab.Url, url, StringComparison.Ordinal)
                && AmazonPrimeVideoService.IsWebViewBridgeSuppressedProbeResult(result))
            {
                if (AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url))
                {
                    _streamingPlaybackTabStateCoordinator.MarkAmazonCompatibilityActive(tab.Id);
                }
                else
                {
                    _streamingPlaybackTabStateCoordinator.MarkStreamingCompatibilityActive(tab.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // 復元と同時にページが遷移した場合は、次の文書生成通知に任せる。
        }
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
        RemoveStreamingPlaybackCompatibilityScript(tab);
        UpdateAmazonPrimeVideoNotice();
    }

    private void MarkStreamingPlaybackFailed(
        BrowserTab tab,
        StreamingPlaybackFailureMessage failure)
    {
        _ = failure;
        _windowTaskSupervisor.TryRun(
            "streaming-playback-compatibility-retry",
            cancellationToken => RetryStreamingPlaybackCompatibilityAsync(tab, cancellationToken));
    }

    private async Task RetryStreamingPlaybackCompatibilityAsync(
        BrowserTab tab,
        CancellationToken cancellationToken)
    {
        var pageUrl = GetCurrentPageUrl(tab) ?? tab.Url;
        if (!_tabs.Contains(tab)
            || GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !StreamingPlaybackCompatibilityService.TryGetCompatibilityRetryTarget(
                pageUrl,
                out var origin,
                out var retryKey))
        {
            if (ActiveTab == tab)
            {
                SetStatus(
                    StreamingPlaybackCompatibilityService.RetryUnavailableMessage,
                    autoClear: true);
            }

            return;
        }

        if (!_streamingPlaybackTabStateCoordinator.TryBeginStreamingCompatibilityRetry(
                tab.Id,
                retryKey))
        {
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CoreWebViewDocumentScriptRegistry.UpdateVariableAsync(
                core,
                StreamingPlaybackCompatibilityScriptKey,
                StreamingPlaybackCompatibilityService.BuildCompatibilityRetryScript(
                    GetTabWebMessageToken(tab) ?? string.Empty,
                    origin));
            cancellationToken.ThrowIfCancellationRequested();
            var currentUrl = GetCurrentPageUrl(tab) ?? tab.Url;
            if (!_tabs.Contains(tab)
                || GetTabWebView(tab)?.CoreWebView2 != core
                || !StreamingPlaybackCompatibilityService.TryGetCompatibilityRetryTarget(
                    currentUrl,
                    out _,
                    out var currentKey)
                || !string.Equals(currentKey, retryKey, StringComparison.Ordinal))
            {
                CoreWebViewDocumentScriptRegistry.RemoveVariable(
                    core,
                    StreamingPlaybackCompatibilityScriptKey);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _streamingPlaybackTabStateCoordinator.ClearStreamingCompatibility(tab.Id);
            if (ActiveTab == tab)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetStatus(
                    StreamingPlaybackCompatibilityService.RetryStartingMessage,
                    autoClear: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ReloadActiveTabAsync(tab);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                CoreWebViewDocumentScriptRegistry.RemoveVariable(
                    core,
                    StreamingPlaybackCompatibilityScriptKey);
            }
            catch
            {
                // WebView2終了中は一時スクリプトの解放をRuntime破棄に任せる。
            }
        }
        catch
        {
            try
            {
                CoreWebViewDocumentScriptRegistry.RemoveVariable(
                    core,
                    StreamingPlaybackCompatibilityScriptKey);
            }
            catch
            {
                // WebView2終了中は一時スクリプトの解放をRuntime破棄に任せる。
            }

            if (ActiveTab == tab)
            {
                SetStatus(
                    StreamingPlaybackCompatibilityService.RetryUnavailableMessage,
                    autoClear: true);
            }
        }
    }

    private void MarkStreamingPlaybackCompatibilityActive(BrowserTab tab, string? source)
    {
        var pageUrl = GetCurrentPageUrl(tab) ?? tab.Url;
        if (!_tabs.Contains(tab)
            || !StreamingPlaybackCompatibilityService.IsEligiblePageUrl(pageUrl)
            || !StreamingPlaybackCompatibilityService.IsTrustedTopLevelSource(pageUrl, source))
        {
            return;
        }

        _streamingPlaybackTabStateCoordinator.MarkStreamingCompatibilityActive(tab.Id);
    }

    private void CompleteStreamingPlaybackCompatibilityRetry(
        BrowserTab tab,
        bool navigationSucceeded)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !CoreWebViewDocumentScriptRegistry.ContainsVariable(
                core,
                StreamingPlaybackCompatibilityScriptKey))
        {
            return;
        }

        RemoveStreamingPlaybackCompatibilityScript(tab);
        if (ActiveTab != tab)
        {
            return;
        }

        SetStatus(
            navigationSucceeded
                && _streamingPlaybackTabStateCoordinator.IsStreamingCompatibilityActive(tab.Id)
                ? StreamingPlaybackCompatibilityService.RetryCompletedMessage
                : StreamingPlaybackCompatibilityService.RetryUnavailableMessage,
            autoClear: true);
    }

    private void RemoveStreamingPlaybackCompatibilityScript(BrowserTab tab)
    {
        if (GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            CoreWebViewDocumentScriptRegistry.RemoveVariable(
                core,
                StreamingPlaybackCompatibilityScriptKey);
        }
        catch
        {
            // WebView2終了中は文書生成スクリプトもRuntimeと一緒に破棄される。
        }
    }

    private void AmazonPrimeVideoDetailsButton_Click(object sender, RoutedEventArgs e)
        => _streamingPlaybackNoticeController.ExpandDetails();

    private void AmazonPrimeVideoDismissButton_Click(object sender, RoutedEventArgs e)
        => _streamingPlaybackNoticeController.Dismiss();

    private bool IsConfirmedAmazonPrimeVideoTab(BrowserTab tab)
    {
        var url = GetCurrentPageUrl(tab) ?? tab.Url;
        if (_streamingPlaybackTabStateCoordinator.IsAmazonCompatibilityActive(tab.Id)
            && AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url))
        {
            return true;
        }

        if (AmazonPrimeVideoService.IsPlaybackPageUrl(url))
        {
            return true;
        }

        var readiness = _streamingPlaybackTabStateCoordinator.GetReadiness(tab.Id);
        return AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(url)
            && readiness is not AmazonPrimeVideoReadiness.Unknown
            and not AmazonPrimeVideoReadiness.Pending;
    }

    private bool UsesNativeWebGestureInput(BrowserTab tab)
    {
        return IsConfirmedAmazonPrimeVideoTab(tab)
            || (_streamingPlaybackTabStateCoordinator.IsStreamingCompatibilityActive(tab.Id)
                && StreamingPlaybackCompatibilityService.IsEligiblePageUrl(
                    GetCurrentPageUrl(tab) ?? tab.Url));
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
