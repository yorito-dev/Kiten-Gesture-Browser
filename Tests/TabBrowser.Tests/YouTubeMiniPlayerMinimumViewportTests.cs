using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

[Collection(WorkspaceNoteEditWindowStaCollection.Name)]
public sealed class YouTubeMiniPlayerMinimumViewportTests
{
    [Fact]
    public void GeneratedNativeEdgeBridgeExpandsProductionEdgeWidth()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript("入力境界の検証");
        Assert.DoesNotContain("__TAB_BROWSER_YOUTUBE_MINI_PLAYER_RESIZE_EDGE_INSET__", script);
        Assert.Contains("const edgeInset = 1 / zoom;", script);
        Assert.Contains("const bottomInset = 10 / zoom;", script);
        Assert.Contains("bounds.height - y < bottomInset", script);
        Assert.Contains("playerBounds.bottom - event.clientY < bottomInset", script);
    }

    [Fact]
    public void FramelessNativeVideoOwnsEdgeInputEvenWhenWpfThumbHitTestSucceeds()
    {
        RunOnSta(() =>
        {
            using var nativeVideo = new NativeVideoInputHost();
            var state = new YouTubeMiniPlayerState { Width = 480, Height = 270, VideoAspectRatio = 16.0 / 9 };
            var parts = YouTubeMiniPlayerRuntimeFactory.Create(state, new Style(typeof(Button)), "非表示の入力境界検証", nativeVideo, null, null);
            var runtime = parts.Runtime;
            var runtimes = new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [state.Id] = runtime };
            YouTubeMiniPlayerResizeThumbService.AddThumbs(parts.Chrome, state, runtime.Root, runtimes,
                new((_, _) => { }, () => new Point(), (_, _, _) => { }, _ => { }, () => { }));
            runtime.TaskbarWindow.Content = null;
            // 本体もnative面も画面へ表示せず、実HWNDによる入力先の違いを検証する。
            using var source = new HwndSource(new HwndSourceParameters("非表示の入力境界")
            { WindowStyle = unchecked((int)0x80000000), Width = 480, Height = 270 });
            try
            {
                source.RootVisual = runtime.Root;
                Measure(runtime);
                Assert.False(IsWindowVisible(source.Handle));
                Assert.False(runtime.TaskbarWindow.IsVisible);
                Assert.False(runtime.Popup.IsOpen);
                Assert.NotEqual(IntPtr.Zero, nativeVideo.Handle);
                foreach (var point in new[] { new Point(240, 269.5), new Point(0.5, 269.5), new Point(479.5, 269.5) })
                {
                    DependencyObject? hit = VisualTreeHelper.HitTest(runtime.Root, point)?.VisualHit;
                    while (hit is not null && hit is not Thumb) hit = VisualTreeHelper.GetParent(hit);
                    Assert.IsType<Thumb>(hit);
                    Assert.Equal(nativeVideo.Handle, ChildWindowFromPointEx(source.Handle, new NativeInputPoint((int)point.X, (int)point.Y), 0));
                }
                Assert.Equal(1, YouTubeMiniPlayerInputService.VideoResizeEdgeInset);
            }
            finally
            {
                source.RootVisual = null;
                runtime.IsClosingTaskbarWindow = true;
                runtime.TaskbarWindow.Close();
            }
        });
    }

    private sealed class NativeVideoInputHost : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef parent) => new(this,
            CreateWindowEx(0, "static", "", 0x50000000, 0, 0, 480, 270, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        protected override void DestroyWindowCore(HandleRef window) => DestroyWindow(window.Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeInputPoint(int X, int Y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativeInputPoint point, uint flags);

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void FramelessHighlightDoesNotChangeVideoBoundsAndOwnerDisposalReleasesPopup(MiniPlayerSourceKind kind)
    {
        WithFrameRuntime(kind, 16.0 / 9, (runtime, _) =>
        {
            var original = new Rect(runtime.WebHost.RenderSize);
            Assert.Equal(new Rect(0, 0, 480, 270), original);
            foreach (var selected in new[] { true, false, true })
            {
                YouTubeMiniPlayerChromeFactory.ApplyListWindowHighlight(runtime, selected, false, Brushes.Cyan, Brushes.Yellow);
                Measure(runtime);
                Assert.Equal(original, new Rect(runtime.WebHost.RenderSize));
                Assert.Equal(new Size(480, 270), new Size(runtime.State.Width, runtime.State.Height));
                Assert.Equal(new Thickness(0), ((Border)runtime.Root).BorderThickness);
            }
            runtime.TaskbarWindow.WindowState = WindowState.Minimized;
            YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
            Assert.False(runtime.Popup.IsOpen);
            runtime.TaskbarWindow.WindowState = WindowState.Normal;
            YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);
            Assert.Equal(Visibility.Visible, runtime.ListHighlightBorder.Visibility);
            runtime.State.Width = runtime.Root.Width = 640;
            runtime.State.Height = runtime.Root.Height = 360;
            Measure(runtime);
            YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);
            Assert.Equal(648, runtime.ListHighlightBorder.Width);
            Assert.Equal(368, runtime.ListHighlightBorder.Height);
            YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            Assert.Null(runtime.Popup.Child);
            Assert.False(runtime.Popup.IsOpen);
            YouTubeMiniPlayerChromeFactory.ApplyListWindowHighlight(runtime, true, true, Brushes.Cyan, Brushes.Yellow);
            Assert.False(runtime.Popup.IsOpen);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
        });
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void FramelessNativeBorderSuppressionSucceedsWithoutShowingWindow(MiniPlayerSourceKind kind)
    {
        WithFrameRuntime(kind, 16.0 / 9, (runtime, _) =>
        {
            new WindowInteropHelper(runtime.TaskbarWindow).EnsureHandle();
            // BORDER_COLORは公式に設定用とされるため、実native呼び出しの成功を検査する。
            Assert.Equal(0, WindowThemeService.HideBorder(runtime.TaskbarWindow));
            Assert.False(runtime.TaskbarWindow.IsVisible);
            Assert.False(runtime.Popup.IsOpen);
        });
    }

    [Fact]
    public void DisposingRuntimeClosesItsNativeWindowEvenIfItHasNeverBeenShown()
    {
        WithFrameRuntime(MiniPlayerSourceKind.YouTube, 16.0 / 9, (runtime, _) =>
        {
            var window = runtime.TaskbarWindow;
            var closed = 0;
            window.Closed += (_, _) => closed++;
            Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(window).EnsureHandle());
            Assert.False(window.IsVisible);

            YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);

            Assert.Equal(1, closed);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x00080080)]
    [InlineData(0x00000008)]
    public void HighlightNativeInputTransparencyPreservesExistingWindowStyles(int original)
    {
        var styles = YouTubeMiniPlayerPopupService.GetInputTransparentStyle(original);
        Assert.Equal(original, styles & original);
        Assert.Equal(0x08000020, styles & 0x08000020);
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube, true, false)]
    [InlineData(MiniPlayerSourceKind.YouTube, false, true)]
    [InlineData(MiniPlayerSourceKind.YouTube, true, true)]
    [InlineData(MiniPlayerSourceKind.LocalFile, true, false)]
    [InlineData(MiniPlayerSourceKind.LocalFile, false, true)]
    [InlineData(MiniPlayerSourceKind.LocalFile, true, true)]
    public void ListHighlightFourSidesStayVisibleOutsideOpaqueVideo(MiniPlayerSourceKind kind, bool selected, bool hovered)
    {
        WithFrameRuntime(kind, 16.0 / 9, (runtime, chrome) =>
        {
            var cyan = new SolidColorBrush(Color.FromRgb(34, 211, 238));
            var yellow = new SolidColorBrush(Color.FromRgb(250, 204, 21));
            // 強調枠を動画と別のPopupへ置き、動画と通常レイアウトから分離する。
            Assert.Same(runtime.ListHighlightBorder, runtime.Popup.Child);
            Assert.DoesNotContain(runtime.ListHighlightBorder, chrome.Children.Cast<UIElement>());
            YouTubeMiniPlayerChromeFactory.ApplyListWindowHighlight(runtime, selected, hovered, cyan, yellow);
            Measure(runtime);
            var border = runtime.ListHighlightBorder;
            Assert.Equal(new Thickness(4), border.BorderThickness);
            Assert.Equal(488, border.Width, 7);
            Assert.Equal(278, border.Height, 7);
            Assert.Equal(-4, runtime.Popup.HorizontalOffset);
            Assert.Equal(-4, runtime.Popup.VerticalOffset);
            border.Measure(new Size(488, 278));
            border.Arrange(new Rect(0, 0, 488, 278));
            var bitmap = new RenderTargetBitmap(488, 278, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(border);
            var expected = hovered ? yellow.Color : cyan.Color;
            foreach (var point in Enumerable.Range(0, 4).SelectMany(i => new[] {
                new Point(244, i), new Point(244, 277 - i), new Point(i, 139), new Point(487 - i, 139) }))
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                Assert.Equal(expected, Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
            }
            var center = new byte[4];
            bitmap.CopyPixels(new Int32Rect(244, 139, 1, 1), center, 4, 0);
            Assert.Equal(0, center[3]);
            Assert.False(runtime.ListHighlightBorder.IsHitTestVisible);
            Assert.False(runtime.Popup.IsHitTestVisible);
            foreach (var point in new[] { new Point(240, 0.5), new Point(240, 269.5), new Point(0.5, 135), new Point(479.5, 135) })
            {
                DependencyObject? hit = null;
                VisualTreeHelper.HitTest(runtime.Root,
                    value => value is UIElement element && !element.IsHitTestVisible
                        ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                    result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
                while (hit is not null && hit is not Thumb) hit = VisualTreeHelper.GetParent(hit);
                Assert.IsType<Thumb>(hit);
            }
        });
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void ListHighlightTransitionsHideNormalFrameAndRestoreOnlyCurrentSelection(MiniPlayerSourceKind kind)
    {
        WithFrameRuntime(kind, 16.0 / 9, (runtime, _) =>
        {
            var cyan = new SolidColorBrush(Color.FromRgb(34, 211, 238));
            var yellow = new SolidColorBrush(Color.FromRgb(250, 204, 21));
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)((Border)runtime.Root).BorderBrush).Color);
            Apply(true, false);
            Assert.Equal(cyan.Color, BrushColor());
            Assert.Equal(Visibility.Visible, runtime.ListHighlightBorder.Visibility);
            Apply(true, true);
            Assert.Equal(yellow.Color, BrushColor());
            Apply(true, false);
            Assert.Equal(cyan.Color, BrushColor());
            Apply(false, false);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
            Apply(true, false);
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
            Apply(true, true);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Assert.Equal(Visibility.Visible, runtime.ListHighlightBorder.Visibility);
            Assert.Equal(yellow.Color, BrushColor());
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Apply(false, false);
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Assert.Equal(Visibility.Collapsed, runtime.ListHighlightBorder.Visibility);
            Assert.Equal(new Thickness(0), ((Border)runtime.Root).BorderThickness);
            void Apply(bool selected, bool hovered) => YouTubeMiniPlayerChromeFactory.ApplyListWindowHighlight(runtime, selected, hovered, cyan, yellow);
            Color BrushColor() => ((SolidColorBrush)runtime.ListHighlightBorder.BorderBrush).Color;
        });
    }

    [Fact]
    public void LocalVideoInnerResizeUsesProductionSurfaceAndExcludesControlsAndFullscreen()
    {
        RunOnSta(() =>
        {
            using var view = new LocalVideoPlayerView();
            var state = new YouTubeMiniPlayerState { SourceKind = MiniPlayerSourceKind.LocalFile, Width = 480, Height = 270.875 };
            var parts = YouTubeMiniPlayerRuntimeFactory.Create(state, new Style(typeof(Button)), "端判定", view, null, view);
            var runtime = parts.Runtime;
            try
            {
                Measure(runtime);
                // LibVLCの別Windowを開かず、入力面を本番の動画領域と同じDIP寸法へ配置する。
                view.DragSurface.Measure(new Size(478, 268.875));
                view.DragSurface.Arrange(new Rect(0, 0, 478, 268.875));
                Assert.Equal(MiniPlayerResizeEdges.Left, Hit(view.DragSurface));
                var controls = (DependencyObject)view.FindName("ControlsPanel");
                Assert.Equal(MiniPlayerResizeEdges.None, Hit(controls));
                Assert.Equal(MiniPlayerResizeEdges.None, Hit((DependencyObject)view.FindName("SeekSlider")));
                runtime.IsFullScreen = true;
                Assert.Equal(MiniPlayerResizeEdges.None, Hit(view.DragSurface));
                runtime.IsFullScreen = false;
                runtime.IsDisposed = true;
                Assert.Equal(MiniPlayerResizeEdges.None, Hit(view.DragSurface));
            }
            finally { runtime.IsClosingTaskbarWindow = true; runtime.TaskbarWindow.Close(); }
            MiniPlayerResizeEdges Hit(DependencyObject source) => YouTubeMiniPlayerDragWiringService.GetSurfaceResizeEdges(
                runtime, view.DragSurface, new Point(2, 120), source);
        });
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube, 16.0 / 9)]
    [InlineData(MiniPlayerSourceKind.YouTube, 9.0 / 16)]
    [InlineData(MiniPlayerSourceKind.LocalFile, 16.0 / 9)]
    [InlineData(MiniPlayerSourceKind.LocalFile, 9.0 / 16)]
    public void ProductionThinFrameKeepsRestoredWidthPositionAndFullscreenRoundTrip(MiniPlayerSourceKind kind, double ratio)
    {
        WithFrameRuntime(kind, ratio, (runtime, chrome) =>
        {
            Assert.Equal(480, runtime.State.Width, 7);
            Assert.Equal(40, runtime.State.Left);
            Assert.Equal(60, runtime.State.Top);
            AssertFrame();
            var thumbs = chrome.Children.OfType<Thumb>().ToArray();
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Assert.Equal(new Thickness(0), ((Border)runtime.Root).BorderThickness);
            Assert.Equal(new Thickness(0), ((Grid)runtime.WebHost.Parent).Margin);
            Assert.All(thumbs, thumb => Assert.False(thumb.IsHitTestVisible));
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            Measure(runtime);
            AssertFrame();
            Assert.All(thumbs, thumb => Assert.True(thumb.IsHitTestVisible));

            void AssertFrame()
            {
                var border = (Border)runtime.Root;
                Assert.Equal(new Thickness(0), border.BorderThickness);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)border.BorderBrush).Color);
                Assert.Equal(new CornerRadius(8), border.CornerRadius);
                Assert.Equal(new Thickness(0), ((Grid)runtime.WebHost.Parent).Margin);
                var bounds = runtime.WebHost.TransformToAncestor(runtime.Root).TransformBounds(new Rect(runtime.WebHost.RenderSize));
                Assert.Equal(0, bounds.Left, 7);
                Assert.Equal(0, bounds.Top, 7);
                Assert.Equal(0, runtime.Root.ActualWidth - bounds.Right, 7);
                Assert.Equal(0, runtime.Root.ActualHeight - bounds.Bottom, 7);
                Assert.Equal(ratio, bounds.Width / bounds.Height, 7);
            }
        });
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void FramelessResizeRegionsStayWithinOneDipEdgesAndKeepBothCornerLegs(MiniPlayerSourceKind kind)
    {
        WithFrameRuntime(kind, 16.0 / 9, (runtime, chrome) =>
        {
            var thumbs = chrome.Children.OfType<Thumb>().ToArray();
            Assert.Equal(8, thumbs.Length);
            foreach (var thumb in thumbs)
            {
                var videoTopLeft = runtime.WebHost.TranslatePoint(new Point(), thumb);
                var videoBottomRight = runtime.WebHost.TranslatePoint(new Point(runtime.WebHost.ActualWidth, runtime.WebHost.ActualHeight), thumb);
                var video = new RectangleGeometry(new Rect(videoTopLeft, videoBottomRight));
                var corner = double.IsFinite(thumb.Width) && double.IsFinite(thumb.Height);
                var inputRegion = thumb.Clip ?? new RectangleGeometry(new Rect(thumb.RenderSize));
                var inner = video.Rect;
                inner.Inflate(-1, -1);
                // 枠0のため判定だけ動画端の1 DIPへ置き、中央には入力領域を広げない。
                Assert.InRange(Geometry.Combine(inputRegion, new RectangleGeometry(inner), GeometryCombineMode.Intersect, null).GetArea(), 0, 1e-7);
                if (corner)
                {
                    Assert.Equal(16, thumb.Width);
                    Assert.Equal(16, thumb.Height);
                    Assert.NotNull(thumb.Clip);
                    Assert.False(inputRegion.FillContains(new Point(8, 8)));
                    var verticalLeg = new Point(thumb.HorizontalAlignment == HorizontalAlignment.Left ? 0.5 : 15.5, 8);
                    var horizontalLeg = new Point(8, thumb.VerticalAlignment == VerticalAlignment.Top ? 0.5 : 15.5);
                    Assert.True(inputRegion.FillContains(verticalLeg));
                    Assert.True(inputRegion.FillContains(horizontalLeg));
                    AssertHit(verticalLeg);
                    AssertHit(horizontalLeg);
                }
                else
                {
                    Assert.Equal(1, double.IsFinite(thumb.Width) ? thumb.Width : thumb.Height);
                    AssertHit(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
                }

                void AssertHit(Point point)
                {
                    var rootPoint = thumb.TranslatePoint(point, runtime.Root);
                    var onVerticalFrame = rootPoint.X < 1 || rootPoint.X > runtime.Root.ActualWidth - 1;
                    var onHorizontalFrame = rootPoint.Y < 1 || rootPoint.Y > runtime.Root.ActualHeight - 1;
                    Assert.True(onVerticalFrame || onHorizontalFrame, $"枠線から外れた判定: {rootPoint}");
                    // 非表示の検証WindowでWPFのVisual hit testを通し、動画に被らず到達できることを確認する。
                    DependencyObject? hit = VisualTreeHelper.HitTest(runtime.Root, rootPoint)?.VisualHit;
                    while (hit is not null && hit is not Thumb) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(thumb, hit);
                }
            }
        });
    }

    private static void WithFrameRuntime(MiniPlayerSourceKind kind, double ratio, Action<YouTubeMiniPlayerRuntime, Grid> inspect)
    {
        RunOnSta(() =>
        {
            // 旧9 DIP枠の保存寸法を、本番の位置補正経路で新しい表示へ復元する。
            var state = new YouTubeMiniPlayerState { SourceKind = kind, Width = 480, Height = 462 / ratio + 18, Left = 40, Top = 60, VideoAspectRatio = ratio };
            var parts = YouTubeMiniPlayerRuntimeFactory.Create(state, new Style(typeof(Button)), "細枠の検証", new Border { Background = Brushes.Black }, null, null);
            var runtime = parts.Runtime;
            var runtimes = new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [state.Id] = runtime };
            try
            {
                YouTubeMiniPlayerResizeThumbService.AddThumbs(parts.Chrome, state, runtime.Root, runtimes,
                    new((_, _) => { }, () => new Point(), (_, _, _) => { }, _ => { }, () => { }));
                YouTubeMiniPlayerPositionService.ClampToHost(state, runtime.Root, false, () => new Point(),
                    () => new Rect(0, 0, 1920, 1080), (_, _) => { }, YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
                Measure(runtime);
                inspect(runtime, parts.Chrome);
            }
            finally { runtime.IsClosingTaskbarWindow = true; runtime.TaskbarWindow.Close(); }
        });
    }

    private static void Measure(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.Root.Measure(new Size(runtime.State.Width, runtime.State.Height));
        runtime.Root.Arrange(new Rect(0, 0, runtime.State.Width, runtime.State.Height));
        runtime.Root.UpdateLayout();
    }

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
        Assert.Equal(ratio, size.Width / size.Height, 9);
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
                Assert.True(state.Height / 2 >= 200);
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
                Assert.True(state.Height / 3 >= 200);
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
