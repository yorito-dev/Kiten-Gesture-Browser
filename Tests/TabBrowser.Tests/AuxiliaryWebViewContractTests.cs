using Xunit;

namespace TabBrowser.Tests;

// 実WebViewの通信試験とは別に、遷移前の登録と破棄時の解除の接続を守る。
public sealed class AuxiliaryWebViewContractTests
{
    [Fact]
    public void AllThumbnailCaptureEntrypointsRestrictCoreBeforeNavigationAndDetachBeforeDisposal()
    {
        var source = Read("Views/UrlThumbnailCaptureController.cs");
        Assert.Equal(2, source.Split("await EnsurePreviewCoreAsync(webView, environment);").Length - 1);
        Assert.Equal(2, source.Split("await NavigateAsync(webView, url, cancellationToken);").Length - 1);
        Assert.Contains("UpdatePrivatePreviewRestrictions(ref _restrictedCore, webView.CoreWebView2);", source);
        var reset = source[source.IndexOf("internal void ResetWebView", StringComparison.Ordinal)..];
        Assert.True(reset.IndexOf("UpdatePrivatePreviewRestrictions(ref _restrictedCore, null);", StringComparison.Ordinal)
            < reset.IndexOf("webView.Dispose();", StringComparison.Ordinal));
    }

    [Fact]
    public void BookmarkPreviewUsesTheSharedDenialsBeforeNavigationAndDetachesOnDisposal()
    {
        var bookmark = Read("Views/BookmarkPreviewController.cs");
        Assert.Contains("UpdatePrivatePreviewRestrictions(ref _restrictedCore, core);", bookmark);
        Assert.True(bookmark.IndexOf("ConfigureBookmarkPreviewWebView(core);", StringComparison.Ordinal)
            < bookmark.IndexOf("core.Navigate(target);", StringComparison.Ordinal));
        var close = bookmark[bookmark.IndexOf("internal void CloseBookmarkPreview", StringComparison.Ordinal)..];
        Assert.True(close.IndexOf("UpdatePrivatePreviewRestrictions(ref _restrictedCore, null);", StringComparison.Ordinal)
            < close.IndexOf("_bookmarkPreviewWebView?.Dispose();", StringComparison.Ordinal));

        var source = Read("Services/WebViewLifecycleService.cs");
        foreach (var pair in new[] { ("DownloadStarting", "RejectDownload"), ("NewWindowRequested", "RejectNewWindow"),
                     ("PermissionRequested", "RejectPermission"), ("LaunchingExternalUriScheme", "RejectExternalUri") })
        {
            Assert.Contains($"core.{pair.Item1} += {pair.Item2};", source);
            Assert.Contains($"previous.{pair.Item1} -= {pair.Item2};", source);
        }
        var download = source[source.IndexOf("private static void RejectDownload", StringComparison.Ordinal)..source.IndexOf("private static void RejectNewWindow", StringComparison.Ordinal)];
        Assert.Contains("e.Cancel = true;", download);
        Assert.Contains("e.Handled = true;", download);
        Assert.DoesNotContain("if (", download); // 添付の直接応答・リダイレクトをURL条件で取りこぼさない。
        var newWindow = source[source.IndexOf("private static void RejectNewWindow", StringComparison.Ordinal)..source.IndexOf("private static void RejectPermission", StringComparison.Ordinal)];
        Assert.Contains("e.Handled = true;", newWindow);
        var permission = source[source.IndexOf("private static void RejectPermission", StringComparison.Ordinal)..source.IndexOf("private static void RejectExternalUri", StringComparison.Ordinal)];
        Assert.Contains("e.State = CoreWebView2PermissionState.Deny;", permission);
        Assert.Contains("e.SavesInProfile = false;", permission);
        Assert.Contains("e.Handled = true;", permission);
        var externalUri = source[source.IndexOf("private static void RejectExternalUri", StringComparison.Ordinal)..source.IndexOf("public static string FormatNavigationError", StringComparison.Ordinal)];
        Assert.Contains("e.Cancel = true;", externalUri);
    }

    [Fact]
    public void PdfOwnerContextReachesRendererAndProfileIsVerifiedBeforeOpeningDocument()
    {
        Assert.Contains("main.IsPrivateBrowsing", Read("Views/QuickLookHost.xaml.cs"));
        Assert.Contains("new QuickLookPdfView(item.FilePath!, request.IsPrivate)", Read("Services/QuickLook/QuickLookInteractiveRenderers.cs"));
        var source = Read("Views/QuickLookPdfView.xaml.cs");
        Assert.True(source.IndexOf("_environmentScope.VerifyProfile", StringComparison.Ordinal) < source.IndexOf("PdfWebView.Source = _documentUri", StringComparison.Ordinal));
    }

    private static string Read(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
                return File.ReadAllText(Path.Combine(directory.FullName, relative));
        throw new DirectoryNotFoundException("ソースの参照元がありません。");
    }
}
