using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

[Collection(WorkspaceNoteEditWindowStaCollection.Name)]
public sealed class YouTubeMiniPlayerMinimumViewportTests
{
    public static IEnumerable<object[]> ScaleCases()
    {
        foreach (var ratio in new[] { 16.0 / 9, 9.0 / 16, 21.0 / 9 })
            foreach (var dpi in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25 })
                foreach (var zoom in new[] { 0.67, 1.0, 1.25, 2.0 })
                    yield return [ratio, dpi, zoom];
    }

    [Theory]
    [MemberData(nameof(ScaleCases))]
    public void MinimumSurvivesPhysicalPixelRounding(double ratio, double dpi, double zoom)
    {
        var state = new YouTubeMiniPlayerState { VideoAspectRatio = ratio };
        var size = YouTubeMiniPlayerLayoutService.GetMinimumSize(state, zoom, dpi, dpi);
        // 両側の枠が各々切り上げられ、Window外寸が切り下げられる場合も検証する。
        var framePixels = Math.Ceiling(YouTubeMiniPlayerLayoutService.GetChromeSingleInset() * dpi) * 2;
        var cssWidth = (Math.Floor(size.Width * dpi + 1e-9) - framePixels) / dpi / zoom;
        var cssHeight = (Math.Floor(size.Height * dpi + 1e-9) - framePixels) / dpi / zoom;
        Assert.True(cssWidth >= 200, $"{cssWidth} CSS px、DPI={dpi}、zoom={zoom}");
        Assert.True(cssHeight >= 200, $"{cssHeight} CSS px、DPI={dpi}、zoom={zoom}");
        Assert.Equal(ratio, (size.Width - 18) / (size.Height - 18), 9);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, true)]
    public void EveryResizeEdgeKeepsMinimumAndOppositeAnchor(bool left, bool top, bool right, bool bottom)
    {
        var state = new YouTubeMiniPlayerState { VideoAspectRatio = 16.0 / 9 };
        var minimum = YouTubeMiniPlayerLayoutService.GetMinimumSize(state, 1.5, 1.25, 1.25);
        var bounds = YouTubeMiniPlayerResizeService.CalculateResizeBounds(
            state, 40, 60, 1000, 570.375, left ? 2000 : -2000, top ? 2000 : -2000,
            left, top, right, bottom, minimum);
        Assert.True(bounds.Width >= minimum.Width - 1e-9);
        Assert.True(bounds.Height >= minimum.Height - 1e-9);
        Assert.Equal(left ? 1040 : 40, left ? bounds.Right : bounds.Left, 7);
        Assert.Equal(top ? 630.375 : 60, top ? bounds.Bottom : bounds.Top, 7);
    }

    [Theory]
    [InlineData(16, 9)]
    [InlineData(9, 16)]
    public void CtrlResizeUsesTheSameMinimum(double width, double height)
    {
        RunOnSta(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 800, Height = 800, VideoAspectRatio = width / height };
            var minimum = YouTubeMiniPlayerLayoutService.GetMinimumSize(state, 2, 1.5, 1.5);
            YouTubeMiniPlayerResizeService.ApplyCtrlDragResize(state, new Border(), 40, 60,
                800, 800, -2000, -2000, (_, _, _) => { }, minimum);
            Assert.True(state.Width >= minimum.Width - 1e-9);
            Assert.True(state.Height >= minimum.Height - 1e-9);
            Assert.Equal(40, state.Left);
            Assert.Equal(60, state.Top);
        });
    }

    [Theory]
    [InlineData(YouTubeMiniPlayerArrangeMode.Grid)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Horizontal)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Vertical)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Random)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Focus)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Monitor)]
    [InlineData(YouTubeMiniPlayerArrangeMode.NormalAndShorts)]
    [InlineData(YouTubeMiniPlayerArrangeMode.Chat)]
    public void ArrangementPlanningRetainsEachPlayersMinimum(YouTubeMiniPlayerArrangeMode mode)
    {
        var candidates = new[] { 16.0 / 9, 9.0 / 16 }.Select(ratio =>
        {
            var state = new YouTubeMiniPlayerState { VideoAspectRatio = ratio };
            return new YouTubeMiniPlayerArrangementCandidate(state.Id, false, false, ratio < 1,
                800, 800, ratio, true, YouTubeMiniPlayerLayoutService.GetMinimumSize(state, 1.25, 1.5, 1.5));
        }).ToArray();
        foreach (var area in new[] { new Rect(0, 0, 1920, 1080), new Rect(-300, 80, 300, 150) })
        {
            var plan = YouTubeMiniPlayerArrangementPlanningService.CreatePlan(mode, area, candidates,
                [], false, YouTubeMiniPlayerLinearPlacement.Second, new Random(1));
            // Focusは配置不能なら既存仕様どおり変更しない。
            if (plan.Layout.Count == 0) { Assert.Equal(YouTubeMiniPlayerArrangeMode.Focus, mode); continue; }
            Assert.Equal(2, plan.Layout.Count);
            foreach (var result in plan.Layout)
            {
                var candidate = candidates.Single(item => item.Id == result.Id);
                Assert.True(result.Bounds.Width >= candidate.MinimumSize!.Value.Width - 1e-9);
                Assert.True(result.Bounds.Height >= candidate.MinimumSize.Value.Height - 1e-9);
            }
        }
    }

    [Fact]
    public void LocalVideoMinimumIsUnchanged()
    {
        var state = new YouTubeMiniPlayerState { SourceKind = MiniPlayerSourceKind.LocalFile };
        Assert.Equal(new Size(260, 154.125), YouTubeMiniPlayerLayoutService.GetMinimumSize(state, 2, 1.5, 1.5));
    }

    [Fact]
    public void ZoomChangeCanRaiseMinimumButFullScreenIsUntouched()
    {
        RunOnSta(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 260, Height = 154.125 };
            using var webView = new WebView2();
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(state, new Style(typeof(Button)),
                "寸法検証", new Border(), webView, null).Runtime;
            try
            {
                webView.ZoomFactor = 2;
                YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
                Assert.True((state.Height - 18) / 2 >= 200);
                Assert.Equal(state.Height, runtime.Root.Height);
                Assert.Equal(state.Height, runtime.TaskbarWindow.Height);
                var saved = new Size(state.Width, state.Height);
                runtime.IsFullScreen = true;
                runtime.Root.Width = runtime.Root.Height = double.NaN;
                webView.ZoomFactor = 3;
                YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
                Assert.True(double.IsNaN(runtime.Root.Width));
                Assert.True(double.IsNaN(runtime.Root.Height));
                Assert.Equal(saved, new Size(state.Width, state.Height));
                runtime.IsFullScreen = false;
                YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
                Assert.True((state.Height - 18) / 3 >= 200);
                runtime.IsDisposed = true;
                saved = new Size(state.Width, state.Height);
                webView.ZoomFactor = 4;
                YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
                Assert.Equal(saved, new Size(state.Width, state.Height));
            }
            finally { runtime.TaskbarWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(16, 9, 1)]
    [InlineData(4, 3, 1)]
    [InlineData(9, 16, 2)]
    [InlineData(21, 9, 1.25)]
    public void RestoredSmallPlayerHasAtLeastTwoHundredCssPixelsInBothDirections(
        double width, double height, double zoom)
    {
        RunOnSta(() =>
        {
            var state = new YouTubeMiniPlayerState
            {
                Width = 260,
                Height = 154.125,
                VideoAspectRatio = width / height
            };
            using var webView = new WebView2 { ZoomFactor = zoom };
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "寸法検証", new Border(), webView, null).Runtime;
            try
            {
                var callbacks = YouTubeMiniPlayerPositionCallbackFactory.Create(
                    new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [state.Id] = runtime },
                    runtime.Root.Dispatcher, () => new Point(), () => new Rect(0, 0, 1920, 1080),
                    _ => { }, new YouTubeMiniPlayerOverlayPlacementCallbacks(
                        _ => { }, _ => { }, _ => Task.CompletedTask, _ => { }));
                callbacks.ClampToHost(state, runtime.Root, true);
                runtime.Root.Measure(new Size(state.Width, state.Height));
                runtime.Root.Arrange(new Rect(0, 0, state.Width, state.Height));
                Assert.True(runtime.WebHost.ActualWidth / zoom >= 200,
                    $"表示幅 {runtime.WebHost.ActualWidth / zoom} CSS px");
                Assert.True(runtime.WebHost.ActualHeight / zoom >= 200,
                    $"表示高 {runtime.WebHost.ActualHeight / zoom} CSS px");
                Assert.Equal(width / height, runtime.WebHost.ActualWidth / runtime.WebHost.ActualHeight, 7);
            }
            finally { runtime.TaskbarWindow.Close(); }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "寸法検証の終了待ちが期限超過しました");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
