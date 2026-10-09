using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

// 環境の選択とWindow共通の代替画像描画だけを借りる。
internal interface IUrlThumbnailCaptureHost
{
    bool IsClosingWindow { get; }

    Task<CoreWebView2Environment> EnsureEnvironmentAsync();

    ImageSource CreateFallbackThumbnail(string title, string? url);
}

// リンク予告・パレット・Workspaceが共有する隠しWebViewと画像取得の所有者。
internal sealed class UrlThumbnailCaptureController
{
    private const double ViewportWidth = 1024;
    private const double ViewportHeight = 576;
    private const int ThumbnailNavigationWaitMilliseconds = 4000;
    private const int BlackFrameRetryDelayMilliseconds = 500;
    private readonly UrlThumbnailCaptureCoordinator _captureCoordinator = new();
    private readonly Panel _webHost;
    private readonly double _parkedWebViewOffset;
    private readonly IUrlThumbnailCaptureHost _host;
    private readonly CancellationToken _lifetimeToken;
    private readonly string _previewProfileName = "KitenPreview-" + Guid.NewGuid().ToString("N");
    private WebView2? _webView;
    private string? _activeUrl;

    internal UrlThumbnailCaptureController(
        Panel webHost,
        double parkedWebViewOffset,
        IUrlThumbnailCaptureHost host,
        CancellationToken lifetimeToken)
    {
        _webHost = webHost;
        _parkedWebViewOffset = parkedWebViewOffset;
        _host = host;
        _lifetimeToken = lifetimeToken;
    }

    internal bool HasWebView => _webView is not null;

    internal bool TryGetCached(string url, out ImageSource thumbnail)
        => _captureCoordinator.TryGetCached(url, out thumbnail);

    internal void StoreCached(string url, ImageSource thumbnail)
        => _captureCoordinator.StoreCached(url, thumbnail);

    internal async Task<ImageSource> CaptureLinkPreviewAsync(
        string url,
        string title,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _host.IsClosingWindow)
        {
            return _host.CreateFallbackThumbnail(title, url);
        }

        if (_captureCoordinator.TryGetCached(url, out var cachedThumbnail))
        {
            return cachedThumbnail;
        }

        if (YouTubeUrlService.CreateThumbnailUri(url) is not null)
        {
            try
            {
                var youTubeThumbnail = await YouTubeThumbnailImageService.Shared.GetAsync(
                    url,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (youTubeThumbnail is not null)
                {
                    _captureCoordinator.StoreCached(url, youTubeThumbnail);
                    return youTubeThumbnail;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return _host.CreateFallbackThumbnail(title, url);
            }
        }

        if (!_captureCoordinator.TryBeginCapture(url))
        {
            return _host.CreateFallbackThumbnail(title, url);
        }

        IDisposable? captureLease = null;
        try
        {
            captureLease = await _captureCoordinator.EnterCaptureAsync(cancellationToken);
            if (_captureCoordinator.TryGetCached(url, out cachedThumbnail))
            {
                return cachedThumbnail;
            }

            var webView = EnsureWebView();
            var environment = await _host.EnsureEnvironmentAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_webView, webView))
            {
                return _host.CreateFallbackThumbnail(title, url);
            }

            await EnsurePreviewCoreAsync(webView, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_webView, webView))
            {
                return _host.CreateFallbackThumbnail(title, url);
            }

            WebViewLifecycleService.ApplyPreviewCoreSettings(webView.CoreWebView2);
            webView.CoreWebView2.IsMuted = true;
            _activeUrl = url;

            await NavigateAsync(webView, url, cancellationToken);

            var thumbnail = await CaptureImageAsync(
                webView,
                title,
                url,
                cancellationToken);
            return thumbnail ?? _host.CreateFallbackThumbnail(title, url);
        }
        catch
        {
            return _host.CreateFallbackThumbnail(title, url);
        }
        finally
        {
            if (captureLease is not null)
            {
                StopPlayback(url);
                captureLease.Dispose();
            }

            _captureCoordinator.EndCapture(url);
        }
    }

    internal async Task CaptureItemAsync(CommandPaletteItem item)
    {
        if (_host.IsClosingWindow || !item.SupportsThumbnail || item.IsTab || item.HasThumbnail)
        {
            return;
        }

        var url = UrlThumbnailCaptureCoordinator.NormalizeUrl(item.Url);
        if (url is null)
        {
            item.Thumbnail = _host.CreateFallbackThumbnail(item.Title, item.Url);
            return;
        }

        if (_captureCoordinator.TryGetCached(url, out var cachedThumbnail))
        {
            item.Thumbnail = cachedThumbnail;
            return;
        }

        if (YouTubeUrlService.CreateThumbnailUri(url) is not null)
        {
            try
            {
                var youTubeThumbnail = await YouTubeThumbnailImageService.Shared.GetAsync(
                    url,
                    _lifetimeToken);
                if (youTubeThumbnail is not null)
                {
                    _captureCoordinator.StoreCached(url, youTubeThumbnail);
                    item.Thumbnail = youTubeThumbnail;
                    return;
                }
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
        }

        _captureCoordinator.TrackCommandPaletteItem(url, item);
        if (!_captureCoordinator.TryBeginCapture(url))
        {
            return;
        }

        IDisposable? captureLease = null;
        try
        {
            var cancellationToken = _lifetimeToken;
            captureLease = await _captureCoordinator.EnterCaptureAsync(cancellationToken);
            if (_captureCoordinator.TryGetCached(url, out cachedThumbnail))
            {
                ApplyThumbnail(url, cachedThumbnail);
                return;
            }

            var webView = EnsureWebView();
            var environment = await _host.EnsureEnvironmentAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_webView, webView))
            {
                return;
            }

            await EnsurePreviewCoreAsync(webView, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_webView, webView))
            {
                return;
            }

            WebViewLifecycleService.ApplyPreviewCoreSettings(webView.CoreWebView2);
            webView.CoreWebView2.IsMuted = true;
            _activeUrl = url;

            await NavigateAsync(webView, url, cancellationToken);

            var thumbnail = await CaptureImageAsync(
                webView,
                item.Title,
                url,
                cancellationToken);
            if (thumbnail is null)
            {
                await Task.Delay(BlackFrameRetryDelayMilliseconds, cancellationToken);
                thumbnail = await CaptureImageAsync(
                    webView,
                    item.Title,
                    url,
                    cancellationToken);
            }

            if (thumbnail is null)
            {
                var fallback = _host.CreateFallbackThumbnail(item.Title, url);
                ApplyThumbnail(url, fallback, cache: false);
                return;
            }

            ApplyThumbnail(url, thumbnail);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!_host.IsClosingWindow)
            {
                var fallback = _host.CreateFallbackThumbnail(item.Title, url);
                ApplyThumbnail(url, fallback, cache: false);
            }
        }
        finally
        {
            if (captureLease is not null)
            {
                StopPlayback(url);
                captureLease.Dispose();
            }

            _captureCoordinator.EndCapture(url);
        }
    }

    internal async Task<ImageSource?> CaptureWorkspaceAsync(string url, string title)
    {
        var item = new CommandPaletteItem
        {
            Id = "workspace-thumbnail:" + url,
            Type = CommandPaletteItemType.History,
            Title = title,
            Url = url
        };
        await CaptureItemAsync(item);
        return item.Thumbnail;
    }

    private void ApplyThumbnail(
        string url,
        ImageSource thumbnail,
        bool cache = true)
    {
        foreach (var item in _captureCoordinator.CompleteCommandPaletteItems(
                     url,
                     thumbnail,
                     cache))
        {
            item.Thumbnail = thumbnail;
        }
    }

    private async Task EnsurePreviewCoreAsync(WebView2 webView, CoreWebView2Environment environment)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _previewProfileName;
        options.IsInPrivateModeEnabled = true;
        await webView.EnsureCoreWebView2Async(environment, options);
        var profile = webView.CoreWebView2.Profile;
        if (!profile.IsInPrivateModeEnabled
            || !string.Equals(profile.ProfileName, _previewProfileName, StringComparison.Ordinal))
        {
            // 未対応Runtimeや初期化設定の不一致で通常プロファイルへ退行した場合は遷移しない。
            throw new InvalidOperationException("サムネイルの分離プロファイルを確認できません。");
        }
    }

    private WebView2 EnsureWebView()
    {
        if (_webView is not null)
        {
            return _webView;
        }

        var webView = new WebView2
        {
            // サムネイル専用。既定／コンテナの認証状態を裏読み込みに使わない。
            CreationProperties = new CoreWebView2CreationProperties
            {
                ProfileName = _previewProfileName,
                IsInPrivateModeEnabled = true
            },
            Width = ViewportWidth,
            Height = ViewportHeight,
            Focusable = false,
            IsHitTestVisible = false
        };
        webView.Margin = new Thickness(_parkedWebViewOffset, 0, 0, 0);
        Panel.SetZIndex(webView, 0);
        _webHost.Children.Add(webView);
        _webView = webView;
        return webView;
    }

    internal void ResetWebView()
    {
        if (_webView is not { } webView)
        {
            return;
        }

        StopPlayback();
        _webHost.Children.Remove(webView);
        webView.Dispose();
        _webView = null;
    }

    internal void StopPlayback(string? expectedUrl = null)
    {
        if (_webView?.CoreWebView2 is not { } core)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(expectedUrl)
            && !string.Equals(_activeUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            core.IsMuted = true;
            core.Stop();
            core.NavigateToString("<!doctype html><html><body></body></html>");
        }
        catch
        {
            // サムネイル用 WebView2 の後始末なので失敗しても無視します。
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(expectedUrl)
                || string.Equals(_activeUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
            {
                _activeUrl = null;
            }
        }
    }

    private static async Task NavigateAsync(
        WebView2 webView,
        string url,
        CancellationToken cancellationToken)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return;
        }

        if (string.Equals(core.Source, url, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
        completed = (_, _) => completion.TrySetResult();
        core.NavigationCompleted += completed;
        try
        {
            core.IsMuted = true;
            core.Navigate(url);
            await Task.WhenAny(
                completion.Task,
                Task.Delay(ThumbnailNavigationWaitMilliseconds, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            core.NavigationCompleted -= completed;
        }
    }

    private static Task<ImageSource?> CaptureImageAsync(
        WebView2 webView,
        string title,
        string url,
        CancellationToken cancellationToken)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return Task.FromResult<ImageSource?>(null);
        }

        return WebThumbnailCaptureService.CaptureAsync(core, title, url, cancellationToken);
    }

}
