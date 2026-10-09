using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface IBookmarkPreviewHost
{
    bool IsClosingWindow { get; }

    bool HasLoadedNewTabPage { get; }

    string ResolveUrl(string input);

    Task<CoreWebView2Environment> EnsureEnvironmentAsync();

    void ReportError(string message);
}

// Bookmark専用のPopupとWebViewを所有し、Window終了・回復時は既存の入口から破棄する。
internal sealed class BookmarkPreviewController
{
    private const double BookmarkPreviewPopupWidth = 620;
    private const double BookmarkPreviewPopupHeight = 420;
    private readonly FrameworkElement _webHost;
    private readonly IBookmarkPreviewHost _host;
    private readonly WindowTaskSupervisor _windowTaskSupervisor;
    private Popup? _bookmarkPreviewPopup;
    private WebView2? _bookmarkPreviewWebView;
    private readonly PopupNavigationRequestCoordinator _bookmarkPreviewRequestCoordinator = new();
    private TextBlock? _bookmarkPreviewTitleText;
    private TextBlock? _bookmarkPreviewUrlText;

    internal BookmarkPreviewController(
        FrameworkElement webHost,
        IBookmarkPreviewHost host,
        WindowTaskSupervisor taskSupervisor)
    {
        _webHost = webHost;
        _host = host;
        _windowTaskSupervisor = taskSupervisor;
    }

    internal bool IsOpen => _bookmarkPreviewPopup?.IsOpen == true;

    internal bool HasWebView => _bookmarkPreviewWebView is not null;

    internal void QueueBookmarkPreviewShow(
        string? url,
        string? title,
        double? left,
        double? top,
        double? right,
        double? bottom)
    {
        _windowTaskSupervisor.TryRun(
            "bookmark-preview-show",
            cancellationToken => ShowBookmarkPreviewAsync(
                url,
                title,
                left,
                top,
                right,
                bottom,
                cancellationToken));
    }

    private async Task ShowBookmarkPreviewAsync(
        string? url,
        string? title,
        double? left,
        double? top,
        double? right,
        double? bottom,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _host.IsClosingWindow)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(url) || !_host.HasLoadedNewTabPage)
        {
            return;
        }

        try
        {
            var requestVersion = _bookmarkPreviewRequestCoordinator.BeginRequest();
            var target = _host.ResolveUrl(url);
            var popup = EnsureBookmarkPreviewPopup();
            PositionBookmarkPreviewPopup(popup, left, top, right, bottom);
            popup.IsOpen = true;

            if (_bookmarkPreviewTitleText is not null)
            {
                _bookmarkPreviewTitleText.Text = string.IsNullOrWhiteSpace(title) ? target : title;
            }

            if (_bookmarkPreviewUrlText is not null)
            {
                _bookmarkPreviewUrlText.Text = target;
            }

            if (_bookmarkPreviewWebView is null)
            {
                throw new InvalidOperationException("ブックマークプレビュー WebView2 を作成できませんでした");
            }

            var webView = _bookmarkPreviewWebView;
            var environment = await _host.EnsureEnvironmentAsync();
            if (!IsCurrentBookmarkPreviewRequest(requestVersion, webView, popup, cancellationToken))
            {
                return;
            }

            await webView.EnsureCoreWebView2Async(environment);
            if (!IsCurrentBookmarkPreviewRequest(requestVersion, webView, popup, cancellationToken))
            {
                return;
            }

            ConfigureBookmarkPreviewWebView(webView.CoreWebView2);

            if (!_bookmarkPreviewRequestCoordinator.IsCurrentUrl(target))
            {
                _bookmarkPreviewRequestCoordinator.SetCurrentUrl(target);
                webView.CoreWebView2.Navigate(target);
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CloseBookmarkPreview(clearNavigation: true);
            _host.ReportError("WebView2 Runtime が見つかりません");
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CloseBookmarkPreview(clearNavigation: true);
            _host.ReportError("ブックマークプレビューを表示できません: " + ex.Message);
        }
    }

    private bool IsCurrentBookmarkPreviewRequest(
        int requestVersion,
        WebView2 webView,
        Popup popup,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && !_host.IsClosingWindow
            && _bookmarkPreviewRequestCoordinator.IsCurrent(requestVersion)
            && ReferenceEquals(_bookmarkPreviewWebView, webView)
            && ReferenceEquals(_bookmarkPreviewPopup, popup)
            && popup.IsOpen == true;
    }

    private Popup EnsureBookmarkPreviewPopup()
    {
        if (_bookmarkPreviewPopup is not null)
        {
            return _bookmarkPreviewPopup;
        }

        _bookmarkPreviewTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _bookmarkPreviewUrlText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _bookmarkPreviewWebView = new WebView2
        {
            Width = BookmarkPreviewPopupWidth,
            Height = BookmarkPreviewPopupHeight - 47
        };

        var header = new Border
        {
            Padding = new Thickness(10, 8, 10, 7),
            Background = new SolidColorBrush(Color.FromRgb(21, 24, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 58)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Children =
                {
                    _bookmarkPreviewTitleText,
                    _bookmarkPreviewUrlText
                }
            }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_bookmarkPreviewWebView, 1);
        layout.Children.Add(header);
        layout.Children.Add(_bookmarkPreviewWebView);

        var frame = new Border
        {
            Width = BookmarkPreviewPopupWidth,
            Height = BookmarkPreviewPopupHeight,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 77)),
            BorderThickness = new Thickness(1),
            Child = layout
        };

        _bookmarkPreviewPopup = new Popup
        {
            AllowsTransparency = false,
            PlacementTarget = _webHost,
            Placement = PlacementMode.RelativePoint,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Child = frame
        };
        _bookmarkPreviewPopup.Closed += (_, _) => _bookmarkPreviewRequestCoordinator.ClearCurrentUrl();
        return _bookmarkPreviewPopup;
    }

    private void PositionBookmarkPreviewPopup(
        Popup popup,
        double? left,
        double? top,
        double? right,
        double? bottom)
    {
        var hostWidth = Math.Max(1, _webHost.ActualWidth);
        var hostHeight = Math.Max(1, _webHost.ActualHeight);
        var anchorLeft = left ?? 12;
        var anchorTop = top ?? 12;
        var anchorRight = right ?? anchorLeft;
        var anchorBottom = bottom ?? anchorTop;

        var x = anchorRight + 10;
        if (x + BookmarkPreviewPopupWidth > hostWidth - 12)
        {
            x = anchorLeft - BookmarkPreviewPopupWidth - 10;
        }

        var y = anchorTop;
        if (y + BookmarkPreviewPopupHeight > hostHeight - 12)
        {
            y = anchorBottom - BookmarkPreviewPopupHeight;
        }

        popup.HorizontalOffset = Math.Max(12, Math.Min(x, Math.Max(12, hostWidth - BookmarkPreviewPopupWidth - 12)));
        popup.VerticalOffset = Math.Max(12, Math.Min(y, Math.Max(12, hostHeight - BookmarkPreviewPopupHeight - 12)));
    }

    private static void ConfigureBookmarkPreviewWebView(CoreWebView2 core)
    {
        WebViewLifecycleService.ApplyPreviewCoreSettings(core);
    }

    internal void CloseBookmarkPreview(bool clearNavigation = false, bool disposeWebView = false)
    {
        _bookmarkPreviewRequestCoordinator.Invalidate();

        if (_bookmarkPreviewPopup is not null)
        {
            _bookmarkPreviewPopup.IsOpen = false;
        }

        _bookmarkPreviewRequestCoordinator.ClearCurrentUrl();

        if (clearNavigation && _bookmarkPreviewWebView?.CoreWebView2 is { } core)
        {
            try
            {
                core.NavigateToString("<!doctype html><html><body></body></html>");
            }
            catch
            {
                // プレビュー終了時の後始末なので失敗しても無視します。
            }
        }

        if (!disposeWebView)
        {
            return;
        }

        _bookmarkPreviewWebView?.Dispose();
        _bookmarkPreviewWebView = null;
        _bookmarkPreviewPopup = null;
        _bookmarkPreviewTitleText = null;
        _bookmarkPreviewUrlText = null;
    }
}
