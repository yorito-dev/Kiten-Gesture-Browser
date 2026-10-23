using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using Xunit;

namespace TabBrowser.Tests;

public sealed class WebThumbnailCaptureServiceTests
{
    [Fact]
    public void CalculateCropRectFocusesVisiblePrimaryColumn()
    {
        var crop = WebThumbnailCaptureService.CalculateCropRect(
            1280,
            720,
            new WebThumbnailCropHint(
                X: 240,
                Y: 0,
                Width: 600,
                Height: 576,
                ViewportWidth: 1024,
                ViewportHeight: 576));

        Assert.True(crop.X > 0);
        Assert.True(crop.Y > 0);
        Assert.True(crop.Width < 1280);
        Assert.True(crop.Height < 720);
        Assert.InRange(
            crop.Width / (double)crop.Height,
            (WebThumbnailCaptureService.ThumbnailWidth
                / (double)WebThumbnailCaptureService.ContentHeight) - 0.01,
            (WebThumbnailCaptureService.ThumbnailWidth
                / (double)WebThumbnailCaptureService.ContentHeight) + 0.01);
    }

    [Fact]
    public void CalculateCropRectFallsBackForInvalidHint()
    {
        var crop = WebThumbnailCaptureService.CalculateCropRect(
            1024,
            576,
            new WebThumbnailCropHint(0, 0, 0, 0, 0, 0));

        Assert.Equal(0, crop.X);
        Assert.True(crop.Y > 0);
        Assert.Equal(1024, crop.Width);
        Assert.True(crop.Height < 576);
    }

    [Fact]
    public void CaptureScriptReadsLayoutAndBoundedStaticMetadata()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(
            Path.Combine(
                root,
                "Services",
                "Workspaces",
                "WebThumbnailCaptureService.cs"));

        Assert.Contains("getBoundingClientRect()", service);
        Assert.Contains("collect('article')", service);
        Assert.Contains("main, [role=\"main\"]", service);
        Assert.Contains("FindInformationDenseCrop", service);
        Assert.Contains("InformationBandColor", service);
        Assert.Contains("WaitForVisualStabilityAsync", service);
        Assert.Contains("MinimumSettleMilliseconds = 1000", service);
        Assert.Contains("MaximumSettleMilliseconds = 3500", service);
        Assert.Contains("document.images", service);
        Assert.Contains("document.fonts?.status", service);
        Assert.Contains("performance.getEntriesByType('resource').length", service);
        Assert.Contains("meta[property=\"og:description\"]", service);
        Assert.Contains("meta[name=\"description\"]", service);
        Assert.Contains("?.slice(0, 600)", service);
        Assert.DoesNotContain("innerText", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("textContent", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document.cookie", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", service, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageDescriptionNormalizationCollapsesWhitespaceAndLimitsLength()
    {
        Assert.Equal(
            "ページの 概要",
            WebThumbnailCaptureService.NormalizePageDescription("  ページの\r\n 概要  "));
        Assert.Equal(
            600,
            WebThumbnailCaptureService.NormalizePageDescription(new string('A', 700)).Length);
    }

    [Fact]
    public async Task CapturePreviewWaitEndsOnCancellationWhenNativeCaptureNeverCompletes()
    {
        // 非表示WebView2のCapturePreviewAsyncは完了しないため、完了しないTaskで代替する。
        var neverCompletedCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = WebThumbnailCaptureService.WaitForCapturePreviewAsync(
            neverCompletedCapture.Task,
            cancellation.Token);
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(neverCompletedCapture.Task.IsCompleted);
    }

    [Fact]
    public async Task ClosedTabCaptureOfHiddenWebViewDoesNotBlockWindowTaskDrain()
    {
        using var supervisor = new WindowTaskSupervisor();
        var neverCompletedCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(supervisor.TryRun(
            "closed-tab-thumbnail-capture",
            cancellationToken => WebThumbnailCaptureService.WaitForCapturePreviewAsync(
                neverCompletedCapture.Task,
                cancellationToken)));

        supervisor.BeginShutdown();
        var result = await supervisor.DrainAsync(TimeSpan.FromSeconds(1));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.PendingTaskCount);
    }

    [Fact]
    public async Task CapturePreviewWaitKeepsCompletedAndFailedCaptureResults()
    {
        using var cancellation = new CancellationTokenSource();
        await WebThumbnailCaptureService.WaitForCapturePreviewAsync(Task.CompletedTask, cancellation.Token);

        var failure = new InvalidOperationException("capture failed");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebThumbnailCaptureService.WaitForCapturePreviewAsync(Task.FromException(failure), cancellation.Token));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public void CapturePreviewIsAwaitedThroughCancellableWait()
    {
        var service = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Services", "Workspaces", "WebThumbnailCaptureService.cs"));

        Assert.Contains("await WaitForCapturePreviewAsync(", service);
        Assert.Contains("core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream),", service);
        Assert.Contains("await capture.WaitAsync(cancellationToken);", service);
        Assert.DoesNotContain("await core.CapturePreviewAsync(", service);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("TabBrowser.csprojが見つかりません。");
    }
}
