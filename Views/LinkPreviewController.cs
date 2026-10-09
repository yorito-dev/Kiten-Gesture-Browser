using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TabBrowser.Services;

namespace TabBrowser.Views;

// 表示可能なタブをホストが確認してから渡す、一回の操作用の値。
internal sealed record LinkPreviewSource(Guid Id, string? Url, FrameworkElement? Content);

internal sealed record LinkPreviewElements(
    FrameworkElement Root,
    Popup Popup,
    TextBlock Title,
    TextBlock Url,
    Image Image,
    TextBlock Loading);

// タブ構成・共有画像取得・ステータス表示だけを借りる。Window全体は公開しない。
internal interface ILinkPreviewHost
{
    bool IsClosingWindow { get; }

    bool IsLinkHoverPreviewEnabled { get; }

    LinkPreviewSource? ResolveVisibleSource(Guid tabId);

    Task<ImageSource> CaptureThumbnailAsync(string url, string title, CancellationToken cancellationToken);

    bool TryGetCachedThumbnail(string url, out ImageSource thumbnail);

    void StoreCachedThumbnail(string url, ImageSource thumbnail);

    void StopPreviewPlayback();

    void ShowHoverStatus(string text);

    void ClearHoverStatusIfCurrent(string text);
}

internal sealed class LinkPreviewController : IDisposable
{
    private const double LinkPreviewPopupWidth = 320;
    private const double LinkPreviewPopupHeight = 235;
    private const double LinkPreviewPopupMargin = 12;
    private const double LinkPreviewPointerOffset = 24;

    private readonly LinkPreviewElements _elements;
    private readonly OwnedDispatcherTimer _linkPreviewHoverTimer;
    private readonly WindowTaskSupervisor _windowTaskSupervisor;
    private readonly ILinkPreviewHost _host;
    private readonly FiveChLiveReadabilityService _fiveChLiveReadabilityService;
    private readonly ReadabilityController _readabilityController;
    private readonly LinkPreviewInteractionCoordinator _linkPreviewInteractionCoordinator = new();
    private bool _disposed;

    internal LinkPreviewController(
        LinkPreviewElements elements,
        OwnedDispatcherTimer hoverTimer,
        WindowTaskSupervisor taskSupervisor,
        ILinkPreviewHost host,
        FiveChLiveReadabilityService fiveChLiveReadabilityService,
        ReadabilityController readabilityController)
    {
        _elements = elements;
        _linkPreviewHoverTimer = hoverTimer;
        _windowTaskSupervisor = taskSupervisor;
        _host = host;
        _fiveChLiveReadabilityService = fiveChLiveReadabilityService;
        _readabilityController = readabilityController;
        _linkPreviewHoverTimer.Tick += HoverTimer_Tick;
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _linkPreviewHoverTimer.Stop();
        QueuePendingLinkPreviewShow();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _linkPreviewHoverTimer.Tick -= HoverTimer_Tick;
        HideLinkPreview(stopPreviewPlayback: true);
    }

    internal void ScheduleLinkPreview(
        LinkPreviewSource? sourceTab,
        string? rawUrl,
        string? rawTitle,
        double? clientX,
        double? clientY)
    {
        if (sourceTab is null || _disposed)
        {
            return;
        }

        if (_fiveChLiveReadabilityService.IsReplyAnchorLink(
                sourceTab.Url,
                rawUrl,
                rawTitle))
        {
            ClearLinkHoverStatus();
            HideLinkPreview(stopPreviewPlayback: true);
            return;
        }

        var url = NormalizeLinkPreviewUrl(rawUrl);
        if (url is null)
        {
            ClearLinkHoverStatus();
            HideLinkPreview(stopPreviewPlayback: true);
            return;
        }

        ShowLinkHoverStatus(sourceTab, url);
        if (!_host.IsLinkHoverPreviewEnabled)
        {
            return;
        }

        if (IsLinkPreviewExcludedSite(url)
            || IsLinkPreviewExcludedSite(sourceTab.Url))
        {
            HideLinkPreview(stopPreviewPlayback: true);
            return;
        }

        _linkPreviewInteractionCoordinator.SetPending(
            sourceTab.Id,
            url,
            NormalizeLinkPreviewTitle(rawTitle, url),
            clientX ?? 0,
            clientY ?? 0);
        _linkPreviewHoverTimer.Stop();
        _linkPreviewHoverTimer.Start();
    }

    private void QueuePendingLinkPreviewShow()
    {
        _windowTaskSupervisor.TryRun(
            "link-preview-show",
            cancellationToken => ShowPendingLinkPreviewAsync(cancellationToken));
    }

    private async Task ShowPendingLinkPreviewAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || _host.IsClosingWindow
            || _disposed
            || !_linkPreviewInteractionCoordinator.TryCapturePending(out var pending)
            || _host.ResolveVisibleSource(pending.SourceTabId) is not { } tab)
        {
            return;
        }

        var url = pending.Url;
        var title = pending.Title;
        var requestVersion = _linkPreviewInteractionCoordinator.BeginDisplay();

        ShowLinkPreviewShell(tab, title, url, new Point(pending.ClientX, pending.ClientY));
        if (_host.TryGetCachedThumbnail(url, out var cachedThumbnail))
        {
            ApplyLinkPreviewThumbnail(requestVersion, url, cachedThumbnail);
            return;
        }

        var thumbnail = await _host.CaptureThumbnailAsync(url, title, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ApplyLinkPreviewThumbnail(requestVersion, url, thumbnail);
    }

    private void ShowLinkPreviewShell(LinkPreviewSource tab, string title, string url, Point clientPoint)
    {
        _elements.Title.Text = title;
        _elements.Url.Text = url;
        _elements.Image.Source = null;
        _elements.Image.Visibility = Visibility.Collapsed;
        _elements.Loading.Visibility = Visibility.Visible;

        PositionLinkPreviewPopup(tab, clientPoint);
        _elements.Popup.IsOpen = true;
    }

    private void ApplyLinkPreviewThumbnail(int requestVersion, string url, ImageSource thumbnail)
    {
        if (!_linkPreviewInteractionCoordinator.IsDisplayCurrent(requestVersion, url)
            || !_elements.Popup.IsOpen)
        {
            return;
        }

        _host.StoreCachedThumbnail(url, thumbnail);
        _elements.Image.Source = thumbnail;
        _elements.Image.Visibility = Visibility.Visible;
        _elements.Loading.Visibility = Visibility.Collapsed;
    }

    private void PositionLinkPreviewPopup(LinkPreviewSource tab, Point clientPoint)
    {
        var anchorPoint = clientPoint;
        if (tab.Content is { } webView)
        {
            try
            {
                anchorPoint = webView.TranslatePoint(clientPoint, _elements.Root);
            }
            catch
            {
                anchorPoint = clientPoint;
            }
        }

        var hostWidth = Math.Max(1, _elements.Root.ActualWidth);
        var hostHeight = Math.Max(1, _elements.Root.ActualHeight);
        var position = CalculateLinkPreviewPopupPosition(anchorPoint, hostWidth, hostHeight);
        _elements.Popup.HorizontalOffset = position.X;
        _elements.Popup.VerticalOffset = position.Y;
    }

    private static Point CalculateLinkPreviewPopupPosition(Point anchorPoint, double hostWidth, double hostHeight)
    {
        var maxX = Math.Max(
            LinkPreviewPopupMargin,
            hostWidth - LinkPreviewPopupWidth - LinkPreviewPopupMargin);
        var maxY = Math.Max(
            LinkPreviewPopupMargin,
            hostHeight - LinkPreviewPopupHeight - LinkPreviewPopupMargin);

        var rightX = anchorPoint.X + LinkPreviewPointerOffset;
        var leftX = anchorPoint.X - LinkPreviewPopupWidth - LinkPreviewPointerOffset;
        var x = rightX > maxX && leftX >= LinkPreviewPopupMargin
            ? leftX
            : rightX;

        var lowerY = anchorPoint.Y + LinkPreviewPointerOffset;
        var upperY = anchorPoint.Y - LinkPreviewPopupHeight - LinkPreviewPointerOffset;
        var y = lowerY > maxY && upperY >= LinkPreviewPopupMargin
            ? upperY
            : lowerY;

        return new Point(
            Math.Clamp(
                x,
                LinkPreviewPopupMargin,
                maxX),
            Math.Clamp(
                y,
                LinkPreviewPopupMargin,
                maxY));
    }

    internal void HideLinkPreview(bool stopPreviewPlayback = false)
    {
        _linkPreviewHoverTimer.Stop();
        _linkPreviewInteractionCoordinator.ClearPendingAndInvalidate();
        _elements.Popup.IsOpen = false;
        _elements.Image.Source = null;
        _elements.Image.Visibility = Visibility.Collapsed;
        _elements.Loading.Visibility = Visibility.Visible;

        if (stopPreviewPlayback)
        {
            _host.StopPreviewPlayback();
        }
    }

    internal void DismissLinkPreview()
    {
        ClearLinkHoverStatus();
        HideLinkPreview(stopPreviewPlayback: true);
    }

    internal void HandleLinkHoverEnd(Guid sourceTabId)
    {
        var ownsPreview = _linkPreviewInteractionCoordinator.OwnsPending(sourceTabId);
        var ownsStatus = _linkPreviewInteractionCoordinator.OwnsHoverStatus(sourceTabId);
        if (!ownsPreview && !ownsStatus)
        {
            return;
        }

        ClearLinkHoverStatus(sourceTabId);
        if (ownsPreview)
        {
            HideLinkPreview(stopPreviewPlayback: true);
        }
    }

    private void ShowLinkHoverStatus(LinkPreviewSource sourceTab, string url)
    {
        if (!_linkPreviewInteractionCoordinator.TrySetHoverStatus(sourceTab.Id, url))
        {
            return;
        }

        _host.ShowHoverStatus(url);
    }

    internal void ClearLinkHoverStatus(Guid? sourceTabId = null)
    {
        if (!_linkPreviewInteractionCoordinator.TryClearHoverStatus(
                sourceTabId,
                out var hoverStatusText))
        {
            return;
        }

        if (hoverStatusText is null)
        {
            return;
        }

        _host.ClearHoverStatusIfCurrent(hoverStatusText);
    }

    private static string? NormalizeLinkPreviewUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var url = rawUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static string NormalizeLinkPreviewTitle(string? rawTitle, string url)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return url;
        }

        var title = Regex.Replace(rawTitle, @"\s+", " ").Trim();
        return title.Length > 120
            ? title[..120]
            : title;
    }

    private bool IsLinkPreviewExcludedSite(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && (YouTubeUrlService.IsYouTubeUrl(url)
                || BrowserUrlRulesService.IsXOrTwitterUrl(url)
                || _readabilityController.IsChatGptUrl(url));
    }

}
