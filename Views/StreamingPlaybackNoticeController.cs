using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record StreamingPlaybackNoticeElements(
    Popup AmazonPrimeVideoNotice,
    FrameworkElement AmazonPrimeVideoNoticePanel,
    TextBlock AmazonPrimeVideoNoticeTitle,
    TextBlock AmazonPrimeVideoNoticeText,
    Button AmazonPrimeVideoDetailsButton,
    FrameworkElement AmazonPrimeVideoActionPanel,
    Button AmazonPrimeVideoOpenButton,
    FrameworkElement WebHost);

internal interface IStreamingPlaybackNoticeHost
{
    bool IsPrivateWindow { get; }
    BrowserTab? GetActiveBrowserCommandTarget();
    bool IsConfirmedAmazonPrimeVideoTab(BrowserTab tab);
    string? GetCurrentPageUrl(BrowserTab tab);
    void SetStatus(string message);
    void QueueAmazonPrimeVideoReadinessCheck(BrowserTab tab);
    Task ReloadActiveTabAsync(BrowserTab tab);
}

// 通知の表示・操作・位置更新を所有する。信頼性判定と準備確認は既存Window入口へ委譲する。
internal sealed class StreamingPlaybackNoticeController : IDisposable
{
    private readonly StreamingPlaybackNoticeElements _elements;
    private readonly IStreamingPlaybackNoticeHost _host;
    private readonly StreamingPlaybackTabStateCoordinator _streamingPlaybackTabStateCoordinator;
    private readonly ExternalBrowserService _externalBrowserService;
    private readonly OwnedDispatcherTimer _streamingPlaybackNoticeWindowChangeTimer;
    private bool _isTrackingWindowChanges;
    private bool _disposed;

    internal StreamingPlaybackNoticeController(
        StreamingPlaybackNoticeElements elements,
        IStreamingPlaybackNoticeHost host,
        StreamingPlaybackTabStateCoordinator stateCoordinator,
        ExternalBrowserService externalBrowserService,
        OwnedDispatcherTimer windowChangeTimer)
    {
        _elements = elements;
        _host = host;
        _streamingPlaybackTabStateCoordinator = stateCoordinator;
        _externalBrowserService = externalBrowserService;
        _streamingPlaybackNoticeWindowChangeTimer = windowChangeTimer;
    }

    public void InitializeWindowTracking()
    {
        if (_disposed || _isTrackingWindowChanges) return;
        _isTrackingWindowChanges = true;
        _streamingPlaybackNoticeWindowChangeTimer.Tick += WindowChangeTimer_Tick;
    }

    private void WindowChangeTimer_Tick(object? sender, EventArgs e)
    {
        _streamingPlaybackNoticeWindowChangeTimer.Stop();
        if (!_disposed) UpdateNotice();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _streamingPlaybackNoticeWindowChangeTimer.Stop();
        _streamingPlaybackNoticeWindowChangeTimer.Tick -= WindowChangeTimer_Tick;
    }

    public void QueueAfterWindowChange()
    {
        if (_disposed) return;
        _elements.AmazonPrimeVideoNotice.IsOpen = false;
        _streamingPlaybackNoticeWindowChangeTimer.Stop();
        _streamingPlaybackNoticeWindowChangeTimer.Start();
    }

    public void UpdateNotice()
    {
        if (_disposed) return;
        var tab = _host.GetActiveBrowserCommandTarget();
        var isAmazonPlaybackPage = tab is { IsNativeContentTab: false }
            && _host.IsConfirmedAmazonPrimeVideoTab(tab);
        var readiness = tab is null
            ? AmazonPrimeVideoReadiness.Unknown
            : _streamingPlaybackTabStateCoordinator.GetReadiness(tab.Id);
        var hasAmazonPlaybackFailure = isAmazonPlaybackPage
            && readiness is AmazonPrimeVideoReadiness.SpinnerBlocked
                or AmazonPrimeVideoReadiness.EmeUnavailable
                or AmazonPrimeVideoReadiness.NavigationFailed
                or AmazonPrimeVideoReadiness.ProbeFailed;
        var shouldShow = !_host.IsPrivateWindow
            && tab is { IsNativeContentTab: false }
            && hasAmazonPlaybackFailure
            && !_streamingPlaybackTabStateCoordinator.IsNoticeDismissed(tab.Id);
        _elements.AmazonPrimeVideoNotice.IsOpen = shouldShow;
        if (!shouldShow || tab is null)
        {
            return;
        }

        var detailsExpanded = _streamingPlaybackTabStateCoordinator.IsNoticeExpanded(tab.Id);
        _elements.AmazonPrimeVideoNoticeTitle.Text = AmazonPrimeVideoService.PreparationFailureTitle;
        _elements.AmazonPrimeVideoNoticeText.Text = AmazonPrimeVideoService.PreparationFailureExplanation;
        _elements.AmazonPrimeVideoNoticeText.Visibility = detailsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        _elements.AmazonPrimeVideoDetailsButton.Visibility = detailsExpanded
            ? Visibility.Collapsed
            : Visibility.Visible;
        _elements.AmazonPrimeVideoActionPanel.Visibility = detailsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        var edgeAvailable = _externalBrowserService.IsAvailable(
            ExternalBrowserKind.MicrosoftEdge);
        _elements.AmazonPrimeVideoOpenButton.Content = edgeAvailable
            ? "Edgeコンパクト表示で視聴"
            : "既定ブラウザーで視聴";
        PositionNotice();
    }

    public void PositionNotice()
    {
        if (_disposed) return;
        if (!_elements.AmazonPrimeVideoNotice.IsOpen)
        {
            return;
        }

        var panelWidth = _elements.AmazonPrimeVideoNoticePanel.ActualWidth > 0
            ? _elements.AmazonPrimeVideoNoticePanel.ActualWidth
            : _elements.AmazonPrimeVideoNoticePanel.DesiredSize.Width;
        var panelHeight = _elements.AmazonPrimeVideoNoticePanel.ActualHeight > 0
            ? _elements.AmazonPrimeVideoNoticePanel.ActualHeight
            : _elements.AmazonPrimeVideoNoticePanel.DesiredSize.Height;
        var horizontalOffset = Math.Max(
            12,
            _elements.WebHost.ActualWidth - panelWidth - 12);
        const double verticalOffset = 12;
        _elements.AmazonPrimeVideoNotice.HorizontalOffset = horizontalOffset;
        _elements.AmazonPrimeVideoNotice.VerticalOffset = verticalOffset;
        PopupWindowOrderService.ApplyBounds(
            _elements.AmazonPrimeVideoNotice,
            _elements.AmazonPrimeVideoNoticePanel,
            _elements.WebHost,
            new Rect(
                horizontalOffset,
                verticalOffset,
                Math.Max(1, panelWidth),
                Math.Max(1, panelHeight)),
            PopupWindowOrderPolicy.Normal);
    }

    public void ExpandDetails()
    {
        if (_disposed) return;
        var tab = _host.GetActiveBrowserCommandTarget();
        if (tab is null || !IsStreamingPlaybackFallbackAvailable(tab))
        {
            return;
        }

        _streamingPlaybackTabStateCoordinator.ExpandNotice(tab.Id);
        UpdateNotice();
    }

    public void Dismiss()
    {
        if (_disposed) return;
        var tab = _host.GetActiveBrowserCommandTarget();
        if (tab is null)
        {
            return;
        }

        _streamingPlaybackTabStateCoordinator.DismissNotice(tab.Id);
        UpdateNotice();
    }

    public void OpenExternally()
    {
        if (_disposed) return;
        var tab = _host.GetActiveBrowserCommandTarget();
        var url = tab is null ? null : _host.GetCurrentPageUrl(tab) ?? tab.Url;
        if (tab is null || !IsStreamingPlaybackFallbackAvailable(tab))
        {
            return;
        }

        var edgeAvailable = _externalBrowserService.IsAvailable(
            ExternalBrowserKind.MicrosoftEdge);
        var result = edgeAvailable
            ? _externalBrowserService.OpenApplicationWindow(
                ExternalBrowserKind.MicrosoftEdge,
                url!)
            : _externalBrowserService.Open(
                ExternalBrowserKind.WindowsDefault,
                [url!]);
        _host.SetStatus(
            result.Succeeded
                ? edgeAvailable
                    ? "Edgeコンパクト表示で開きました"
                    : "既定ブラウザーで開きました"
                : result.ErrorMessage);
    }

    public async Task ReloadAsync()
    {
        if (_disposed) return;
        var tab = _host.GetActiveBrowserCommandTarget();
        if (tab is null
            || !IsStreamingPlaybackFallbackAvailable(tab))
        {
            return;
        }

        _host.QueueAmazonPrimeVideoReadinessCheck(tab);
        await _host.ReloadActiveTabAsync(tab);
    }

    private bool IsStreamingPlaybackFallbackAvailable(BrowserTab tab)
    {
        var readiness = _streamingPlaybackTabStateCoordinator.GetReadiness(tab.Id);
        var hasAmazonPlaybackFailure = _host.IsConfirmedAmazonPrimeVideoTab(tab)
            && readiness is AmazonPrimeVideoReadiness.SpinnerBlocked
                or AmazonPrimeVideoReadiness.EmeUnavailable
                or AmazonPrimeVideoReadiness.NavigationFailed
                or AmazonPrimeVideoReadiness.ProbeFailed;
        return hasAmazonPlaybackFailure;
    }
}
