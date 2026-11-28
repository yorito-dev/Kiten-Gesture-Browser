using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerLayoutServiceTests
{
    [Theory]
    [InlineData(1.0, 1.25, 1.0, 2000, 100, 16, 20, 2020, 125)]
    [InlineData(1.25, 1.0, 1.25, 2000, 100, 16, 20, 2016, 120)]
    [InlineData(1.0, 1.25, 1.0, -1700, -900, 24, 32, -1670, -860)]
    [InlineData(1.25, 1.0, 1.25, -1700, -900, 24, 32, -1676, -868)]
    public void OverlayNativePositionUsesPlayerDpiRatherThanMainWindowDpi(
        double mainDpi,
        double playerDpi,
        double previousPopupDpi,
        double ownerDeviceX,
        double ownerDeviceY,
        double offsetX,
        double offsetY,
        double expectedX,
        double expectedY)
    {
        Assert.NotEqual(mainDpi, playerDpi);
        var actual = YouTubeMiniPlayerPopupService.GetDeviceScreenPosition(
            new Point(ownerDeviceX, ownerDeviceY),
            new Point(offsetX, offsetY),
            new Matrix(playerDpi, 0, 0, playerDpi, 0, 0));

        Assert.Equal(expectedX, actual.X);
        Assert.Equal(expectedY, actual.Y);
        Assert.NotEqual(ownerDeviceX + (offsetX * mainDpi), actual.X);
        Assert.NotEqual(ownerDeviceX + (offsetX * previousPopupDpi), actual.X);
    }

    [Fact]
    public void HiddenOverlayUsesPlayerElementAsPlacementTargetBeforeOpening()
    {
        RunOnStaThread(() =>
        {
            var playerHost = new Border();
            var popupRoot = new Border();
            var popup = new Popup
            {
                Placement = PlacementMode.AbsolutePoint,
                Child = popupRoot
            };

            YouTubeMiniPlayerPopupService.SetRelativePosition(
                popup,
                playerHost,
                popupRoot,
                new Point(12, 180));

            Assert.Same(playerHost, popup.PlacementTarget);
            Assert.Equal(PlacementMode.Relative, popup.Placement);
            Assert.Equal(12, popup.HorizontalOffset);
            Assert.Equal(180, popup.VerticalOffset);
            Assert.False(popup.IsOpen);

            // 非表示のまま別モニターへ移した後の初回表示も新しい相対位置を使う。
            YouTubeMiniPlayerPopupService.SetRelativePosition(
                popup,
                playerHost,
                popupRoot,
                new Point(24, 220));
            Assert.Equal(24, popup.HorizontalOffset);
            Assert.Equal(220, popup.VerticalOffset);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierToolbarPlacementResultCannotOverwriteCurrentPlacement(bool invalidatePlacement)
    {
        var oldCompletion = new TaskCompletionSource<(double HorizontalOffset, double VerticalOffset)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newCompletion = new TaskCompletionSource<(double HorizontalOffset, double VerticalOffset)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new YouTubeMiniPlayerOverlayState();
        var applied = new List<Point>();
        var (oldPlacement, oldRequest) = overlay.BeginToolbarPlacementRequest();
        var oldTask = YouTubeMiniPlayerToolbarPlacementRefreshService.ApplyCurrentAsync(
            () => oldCompletion.Task,
            () => overlay.IsCurrentToolbarPlacementRequest(oldPlacement, oldRequest),
            (x, y) => applied.Add(new Point(x, y)));

        if (invalidatePlacement)
        {
            overlay.InvalidatePlacement();
        }

        Assert.Equal(!invalidatePlacement, overlay.IsCurrentToolbarPlacementRequest(oldPlacement, oldRequest));
        var (newPlacement, newRequest) = overlay.BeginToolbarPlacementRequest();
        var newTask = YouTubeMiniPlayerToolbarPlacementRefreshService.ApplyCurrentAsync(
            () => newCompletion.Task,
            () => overlay.IsCurrentToolbarPlacementRequest(newPlacement, newRequest),
            (x, y) => applied.Add(new Point(x, y)));
        newCompletion.SetResult((40, 60));
        await newTask;
        oldCompletion.SetResult((10, 20));
        await oldTask;

        Assert.Equal([new Point(40, 60)], applied);
    }

    [Fact]
    public void OpenToolbarMenuStopsOnlyItsHideTimer()
    {
        RunOnStaThread(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 480, Height = 270 };
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "非表示タイマーの自動検証", new Border(), null, null).Runtime;
            var delay = TimeSpan.FromSeconds(5);
            void NoPlacement(YouTubeMiniPlayerRuntime _) { }

            try
            {
                YouTubeMiniPlayerOverlayVisibilityService.SetTitleBandVisibility(runtime, false, delay, NoPlacement);
                YouTubeMiniPlayerOverlayVisibilityService.SetToolbarVisibility(
                    runtime, false, delay, NoPlacement, NoPlacement, NoPlacement, NoPlacement);
                var titleTimer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(runtime.Overlay.TitleBandHideTimer);
                var toolbarTimer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(runtime.Overlay.ToolbarHideTimer);
                Assert.NotSame(titleTimer, toolbarTimer);
                Assert.Equal(delay, titleTimer.Interval);
                Assert.Equal(delay, toolbarTimer.Interval);
                Assert.True(titleTimer.IsEnabled);
                Assert.True(toolbarTimer.IsEnabled);

                runtime.Overlay.IsToolbarMenuOpen = true;
                YouTubeMiniPlayerOverlayVisibilityService.SetToolbarVisibility(
                    runtime, false, delay, NoPlacement, NoPlacement, NoPlacement, NoPlacement);
                Assert.True(titleTimer.IsEnabled);
                Assert.False(toolbarTimer.IsEnabled);

                runtime.Overlay.IsToolbarMenuOpen = false;
                YouTubeMiniPlayerOverlayVisibilityService.SetToolbarVisibility(
                    runtime, false, delay, NoPlacement, NoPlacement, NoPlacement, NoPlacement);
                Assert.Same(toolbarTimer, runtime.Overlay.ToolbarHideTimer);
                Assert.True(toolbarTimer.IsEnabled);
            }
            finally
            {
                YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            }

            Assert.False(runtime.Overlay.TitleBandHideTimer?.IsEnabled);
            Assert.False(runtime.Overlay.ToolbarHideTimer?.IsEnabled);
        });
    }

    [Fact]
    public void OnlyDraggingTheTitleBandStopsItsHideTimer()
    {
        RunOnStaThread(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 480, Height = 270 };
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "タイトル帯のドラッグ", new Border(), null, null).Runtime;
            var delay = TimeSpan.FromSeconds(5);
            void RequestHide() => YouTubeMiniPlayerOverlayVisibilityService.SetTitleBandVisibility(
                runtime, false, delay, static _ => { });

            try
            {
                runtime.IsSurfaceDragging = true;
                RequestHide();
                var timer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(runtime.Overlay.TitleBandHideTimer);
                Assert.True(timer.IsEnabled);

                runtime.Overlay.IsTitleBandDragging = true;
                RequestHide();
                Assert.False(timer.IsEnabled);

                runtime.Overlay.IsTitleBandDragging = false;
                runtime.IsSurfaceDragging = false;
                RequestHide();
                Assert.Same(timer, runtime.Overlay.TitleBandHideTimer);
                Assert.True(timer.IsEnabled);
            }
            finally
            {
                YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            }
        });
    }

    [Fact]
    public void TitleTooltipShowsFullCurrentTitleWithoutChangingCompactHeader()
    {
        RunOnStaThread(() =>
        {
            var title = string.Concat(Enumerable.Repeat("省略せずに表示する長い動画タイトル", 20));
            var parts = YouTubeMiniPlayerChromeFactory.CreateTitleBand(title, new Style(typeof(Button)));
            var tooltip = Assert.IsType<ToolTip>(parts.TitleText.ToolTip);
            var content = Assert.IsType<TextBlock>(tooltip.Content);

            Assert.Equal(title, content.Text);
            Assert.Equal(TextWrapping.Wrap, content.TextWrapping);
            Assert.Equal(TextTrimming.None, content.TextTrimming);
            Assert.Equal(480, content.MaxWidth);
            Assert.Equal(TextTrimming.CharacterEllipsis, parts.TitleText.TextTrimming);
            Assert.Equal(30000, ToolTipService.GetShowDuration(parts.TitleText));
            Assert.Equal(System.Windows.Media.Color.FromRgb(17, 20, 26),
                Assert.IsType<System.Windows.Media.SolidColorBrush>(tooltip.Background).Color);
            Assert.Same(parts.TitleText.Foreground, tooltip.Foreground);

            parts.TitleText.Text = "次の動画のタイトル";
            Assert.Equal("次の動画のタイトル", content.Text);
            parts.TitleText.Text = string.Empty;
            Assert.Equal(string.Empty, content.Text);
        });
    }

    [Fact]
    public void TitleBandHistoryWiringPreservesTheRuntimeAndNavigationDirection()
    {
        RunOnStaThread(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 480, Height = 270 };
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "履歴ボタンの検証", new Border(), null, null).Runtime;
            var directions = new List<bool>();
            try
            {
                YouTubeMiniPlayerRuntimeWiringService.AttachTitleBandHistoryNavigation(runtime, (source, forward) =>
                {
                    Assert.Same(runtime, source);
                    directions.Add(forward);
                });
                foreach (var button in new[] { runtime.TitleBandBackButton, runtime.TitleBandForwardButton })
                {
                    var click = new RoutedEventArgs(Button.ClickEvent, button);
                    button.RaiseEvent(click);
                    Assert.True(click.Handled);
                }
                Assert.Equal(new[] { false, true }, directions);
            }
            finally
            {
                YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            }
        });
    }

    [Fact]
    public void NavigationHistoryButtonsUseStandardPreviousAndNextIcons()
    {
        RunOnStaThread(() =>
        {
            var parts = YouTubeMiniPlayerChromeFactory.CreateTitleBand(
                "動画タイトル",
                new Style(typeof(Button)));

            Assert.Equal("\uE892", parts.BackButton.Content);
            Assert.Equal("\uE893", parts.ForwardButton.Content);
            Assert.Equal("前の動画へ戻る", parts.BackButton.ToolTip);
            Assert.Equal("次の動画へ進む", parts.ForwardButton.ToolTip);
            Assert.Equal(28, parts.BackButton.Width);
            Assert.Equal(28, parts.ForwardButton.Width);
            Assert.Equal("Segoe Fluent Icons", parts.BackButton.FontFamily.Source);
            Assert.Equal("Segoe Fluent Icons", parts.ForwardButton.FontFamily.Source);
            Assert.Equal(13, parts.BackButton.FontSize);
            Assert.Equal(13, parts.ForwardButton.FontSize);
            Assert.Equal(Visibility.Collapsed, parts.BackButton.Visibility);
            Assert.Equal(Visibility.Collapsed, parts.ForwardButton.Visibility);
        });
    }

    [Fact]
    public void YouTubeTitleBandPrioritizesChatAndCloseAtMinimumWidth()
    {
        var visibility = YouTubeMiniPlayerChromeFactory.GetTitleBandButtonVisibility(
            YouTubeMiniPlayerLayoutService.MinimumWidth,
            MiniPlayerSourceKind.YouTube);

        Assert.False(visibility.ShowMinimize);
        Assert.True(visibility.ShowClose);
        Assert.False(visibility.ShowTopmost);
        Assert.True(visibility.ShowSource);
        Assert.True(visibility.ShowChat);
    }

    [Fact]
    public void TitleBandRestoresOptionalCommandsWhenWidthAllows()
    {
        var visibility = YouTubeMiniPlayerChromeFactory.GetTitleBandButtonVisibility(
            availableWidth: 600,
            MiniPlayerSourceKind.YouTube);

        Assert.True(visibility.ShowMinimize);
        Assert.True(visibility.ShowClose);
        Assert.True(visibility.ShowTopmost);
        Assert.True(visibility.ShowSource);
        Assert.True(visibility.ShowChat);
    }

    [Fact]
    public void LocalVideoTitleBandKeepsUnsupportedCommandsHidden()
    {
        var visibility = YouTubeMiniPlayerChromeFactory.GetTitleBandButtonVisibility(
            availableWidth: 600,
            MiniPlayerSourceKind.LocalFile);

        Assert.True(visibility.ShowMinimize);
        Assert.True(visibility.ShowClose);
        Assert.True(visibility.ShowTopmost);
        Assert.True(visibility.ShowSource);
        Assert.False(visibility.ShowChat);
    }

    [Fact]
    public void LiveViewerCountKeepsChatAndCloseBeforeOptionalButtons()
    {
        var visibility = YouTubeMiniPlayerChromeFactory.GetTitleBandButtonVisibility(
            availableWidth: YouTubeMiniPlayerLayoutService.ShortsDefaultWidth,
            MiniPlayerSourceKind.YouTube,
            hasLiveViewerCount: true);

        Assert.False(visibility.ShowMinimize);
        Assert.True(visibility.ShowClose);
        Assert.False(visibility.ShowTopmost);
        Assert.True(visibility.ShowSource);
        Assert.True(visibility.ShowChat);
    }

    [Fact]
    public void LiveViewerCountDisplayUsesNormalizedViewerCountWhenAvailable()
    {
        var displayText = YouTubeMiniPlayerChromeFactory.GetLiveViewerCountDisplayText(
            isLive: true,
            "  1,234   人が視聴中  ");

        Assert.Equal("1,234 人が視聴中", displayText);
    }

    [Theory]
    [InlineData(244, "12,345 人が視聴しています")]
    [InlineData(330, "987,654 watching now")]
    [InlineData(244, "9,999 人が視聴中")]
    [InlineData(244, "LIVEへ")]
    public void LiveViewerCountRendersTheWholeTextWithinTheTitleBand(double width, string label)
    {
        RunOnStaThread(() =>
        {
            var parts = YouTubeMiniPlayerChromeFactory.CreateTitleBand("動画タイトル", new Style(typeof(Button)));
            var visibility = YouTubeMiniPlayerChromeFactory.GetTitleBandButtonVisibility(width, MiniPlayerSourceKind.YouTube, hasLiveViewerCount: true);
            parts.SourceButton.Visibility = visibility.ShowSource ? Visibility.Visible : Visibility.Collapsed;
            parts.TopmostButton.Visibility = visibility.ShowTopmost ? Visibility.Visible : Visibility.Collapsed;
            parts.MinimizeButton.Visibility = visibility.ShowMinimize ? Visibility.Visible : Visibility.Collapsed;
            parts.LiveViewerCountButton.Visibility = Visibility.Visible;
            var text = parts.LiveViewerCountText;
            text.Text = label;

            parts.Root.Measure(new Size(width, double.PositiveInfinity));
            parts.Root.Arrange(new Rect(0, 0, width, parts.Root.DesiredSize.Height));
            var fullText = new FormattedText(label, CultureInfo.GetCultureInfo("ja-JP"), text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
            Assert.True(text.ActualWidth + 1 >= fullText.Width, $"必要な表示幅 {fullText.Width:F1} DIP、実際は {text.ActualWidth:F1} DIP");

            var button = parts.LiveViewerCountButton;
            var rendered = text.TransformToAncestor(button).TransformBounds(new Rect(text.RenderSize));
            Assert.InRange(rendered.Left, 0, button.ActualWidth);
            Assert.InRange(rendered.Right, rendered.Left, button.ActualWidth + 0.1);
            Assert.InRange(rendered.Width, fullText.Width * 0.75, fullText.Width + 1);
            Assert.InRange(button.ActualWidth, 1, 108);
            Assert.Equal(Visibility.Visible, parts.ChatButton.Visibility);
            Assert.InRange(parts.CloseButton.TranslatePoint(new Point(parts.CloseButton.ActualWidth, 0), parts.Root).X, 0, width + 0.1);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LiveViewerCountDisplayUsesLiveFallbackWhenViewerCountIsUnavailable(
        string? viewerCountText)
    {
        var displayText = YouTubeMiniPlayerChromeFactory.GetLiveViewerCountDisplayText(
            isLive: true,
            viewerCountText);

        Assert.Equal(YouTubeMiniPlayerChromeFactory.LiveViewerCountFallbackText, displayText);
    }

    [Fact]
    public void LiveViewerCountDisplayIsHiddenWhenBroadcastEnds()
    {
        var displayText = YouTubeMiniPlayerChromeFactory.GetLiveViewerCountDisplayText(
            isLive: false,
            "1,234 人が視聴中");

        Assert.Null(displayText);
    }

    [Theory]
    [InlineData(1080, 1920, true)]
    [InlineData(1920, 1080, false)]
    [InlineData(1000, 1000, false)]
    [InlineData(0, 1920, false)]
    public void IsVerticalVideoDimensionsDetectsPortraitVideo(double width, double height, bool expected)
    {
        Assert.Equal(expected, YouTubeMiniPlayerLayoutService.IsVerticalVideoDimensions(width, height));
    }

    [Fact]
    public void NormalizeSizeToVideoKeepsNormalVideoAspectRatio()
    {
        var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            isShorts: false,
            width: YouTubeMiniPlayerLayoutService.DefaultWidth,
            height: 999,
            preferHeight: false);

        Assert.Equal(YouTubeMiniPlayerLayoutService.DefaultWidth, size.Width, precision: 3);
        Assert.Equal(YouTubeMiniPlayerLayoutService.DefaultHeight, size.Height, precision: 3);
    }

    [Fact]
    public void NormalizeSizeToVideoKeepsShortsAspectRatio()
    {
        var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            isShorts: true,
            width: YouTubeMiniPlayerLayoutService.ShortsDefaultWidth,
            height: 999,
            preferHeight: false);

        var videoWidth = YouTubeMiniPlayerLayoutService.GetVideoWidth(size.Width);
        var videoHeight = YouTubeMiniPlayerLayoutService.GetVideoHeight(size.Height);
        Assert.InRange(
            Math.Abs(YouTubeMiniPlayerLayoutService.ShortsVideoAspectRatio - (videoWidth / videoHeight)),
            0,
            0.001);
    }

    [Fact]
    public void NormalizeSizeToVideoKeepsLocalVideoAspectRatio()
    {
        const double aspectRatio = 4.0 / 3.0;
        var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            width: 420,
            height: 999,
            preferHeight: false,
            aspectRatio);

        var actual = YouTubeMiniPlayerLayoutService.GetVideoWidth(size.Width)
            / YouTubeMiniPlayerLayoutService.GetVideoHeight(size.Height);
        Assert.InRange(Math.Abs(aspectRatio - actual), 0, 0.001);
    }

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void GetVideoAspectRatioUsesDetectedRatioForEverySource(MiniPlayerSourceKind sourceKind)
    {
        var state = new YouTubeMiniPlayerState
        {
            SourceKind = sourceKind,
            IsShorts = true,
            VideoAspectRatio = 4.0 / 3.0
        };

        Assert.Equal(4.0 / 3.0, YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(state));
    }

    [Fact]
    public void GetNextBoundsOffsetsEachPlayerWithinHost()
    {
        var first = YouTubeMiniPlayerLayoutService.GetNextBounds(
            isShorts: false,
            existingPlayerCount: 0,
            hostActualWidth: 1000,
            hostActualHeight: 700);
        var second = YouTubeMiniPlayerLayoutService.GetNextBounds(
            isShorts: false,
            existingPlayerCount: 1,
            hostActualWidth: 1000,
            hostActualHeight: 700);

        Assert.Equal(506, first.Left, precision: 3);
        Assert.Equal(62, first.Top, precision: 3);
        Assert.Equal(480, first.Width, precision: 3);
        Assert.Equal(270, first.Height, precision: 3);
        Assert.Equal(first.Left - 28, second.Left, precision: 3);
        Assert.Equal(first.Top + 28, second.Top, precision: 3);
    }

    [Fact]
    public void GetNextBoundsUsesLargerInitialShortsSizeWithoutChangingAspectRatio()
    {
        var bounds = YouTubeMiniPlayerLayoutService.GetNextBounds(
            isShorts: true,
            existingPlayerCount: 0,
            hostActualWidth: 1000,
            hostActualHeight: 800);

        Assert.Equal(330, bounds.Width, precision: 3);
        Assert.Equal(586.667, bounds.Height, precision: 3);
        var videoWidth = YouTubeMiniPlayerLayoutService.GetVideoWidth(bounds.Width);
        var videoHeight = YouTubeMiniPlayerLayoutService.GetVideoHeight(bounds.Height);
        Assert.Equal(
            YouTubeMiniPlayerLayoutService.ShortsVideoAspectRatio,
            videoWidth / videoHeight,
            precision: 3);
    }

    [Fact]
    public void GetNextAvailableBoundsAvoidsExistingPlayerWhenHostHasSpace()
    {
        var existing = YouTubeMiniPlayerLayoutService.GetNextBounds(
            isShorts: false,
            existingPlayerCount: 0,
            hostActualWidth: 1400,
            hostActualHeight: 900);

        var next = YouTubeMiniPlayerLayoutService.GetNextAvailableBounds(
            isShorts: false,
            [existing],
            hostActualWidth: 1400,
            hostActualHeight: 900);

        var overlap = Rect.Intersect(existing, next);
        Assert.True(overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0);
        Assert.True(next.Left >= YouTubeMiniPlayerLayoutService.Margin);
        Assert.True(next.Top >= YouTubeMiniPlayerLayoutService.Margin);
        Assert.True(next.Right <= 1400 - YouTubeMiniPlayerLayoutService.Margin + 0.001);
        Assert.True(next.Bottom <= 900 - YouTubeMiniPlayerLayoutService.Margin + 0.001);
    }

    [Fact]
    public void ClampToMinimumVisibleAllowsPartialOffscreenButKeepsGripArea()
    {
        Assert.Equal(
            -252,
            YouTubeMiniPlayerLayoutService.ClampToMinimumVisible(-280, 300, 0, 1000),
            precision: 3);
        Assert.Equal(
            952,
            YouTubeMiniPlayerLayoutService.ClampToMinimumVisible(980, 300, 0, 1000),
            precision: 3);
    }

    [Fact]
    public void HasEnoughVisibleAreaRequiresMinimumVisibleLength()
    {
        var screenBounds = new Rect(0, 0, 1000, 800);

        Assert.False(YouTubeMiniPlayerLayoutService.HasEnoughVisibleArea(new Rect(-270, 10, 300, 100), screenBounds));
        Assert.True(YouTubeMiniPlayerLayoutService.HasEnoughVisibleArea(new Rect(-250, 10, 300, 100), screenBounds));
    }

    [Fact]
    public void EnsureVisibleOnVirtualScreenLeavesVisibleWindowPositionUnchanged()
    {
        var state = new YouTubeMiniPlayerState
        {
            Left = 120,
            Top = 80,
            Width = 380,
            Height = 221.625
        };
        var hostOrigin = new Point(100, 50);

        YouTubeMiniPlayerPositionService.EnsureVisibleOnVirtualScreen(
            state,
            hostOrigin,
            new Rect(0, 0, 1000, 800));

        Assert.Equal(120, state.Left, precision: 3);
        Assert.Equal(80, state.Top, precision: 3);
    }

    [Theory]
    [InlineData(1920, 1080, 16, 9)]
    [InlineData(1920, 1080, 9, 16)]
    [InlineData(2560, 1440, 4, 3)]
    public void NextVideoKeepsFullScreenSurfaceStretchedUntilFullScreenExit(
        double screenWidth,
        double screenHeight,
        double nextVideoWidth,
        double nextVideoHeight)
    {
        RunOnStaThread(() =>
        {
            var state = new YouTubeMiniPlayerState
            {
                Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                Left = 120,
                Top = 80,
                Width = 480,
                Height = 278,
                VideoAspectRatio = 16.0 / 9,
                ContentZoomFactor = 2,
                IsMuted = true
            };
            // WindowとWebView2を表示せず、製品と同じWPF表示領域と位置調整経路を使う。
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "動画", new Border(), null, null).Runtime;
            try
            {
                runtime.IsFullScreen = true;
                runtime.FullScreenRestoreBounds = new Rect(state.Left, state.Top, state.Width, state.Height);
                var restoreBounds = runtime.FullScreenRestoreBounds;
                runtime.Root.Width = double.NaN;
                runtime.Root.Height = double.NaN;
                var runtimes = new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [state.Id] = runtime };
                var callbacks = YouTubeMiniPlayerPositionCallbackFactory.Create(
                    runtimes,
                    runtime.Root.Dispatcher,
                    () => new Point(),
                    () => new Rect(0, 0, screenWidth, screenHeight),
                    _ => { },
                    new YouTubeMiniPlayerOverlayPlacementCallbacks(
                        _ => { }, _ => { }, _ => Task.CompletedTask, _ => { }));

                void ArrangeSurface()
                {
                    runtime.Root.Measure(new Size(screenWidth, screenHeight));
                    runtime.Root.Arrange(new Rect(0, 0, screenWidth, screenHeight));
                }

                ArrangeSurface();
                Assert.Equal(screenWidth, runtime.Root.ActualWidth);
                Assert.Equal(screenHeight, runtime.Root.ActualHeight);
                Assert.True(YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(
                    state, "https://www.youtube.com/watch?v=aqz-KE-bpKQ"));
                // 動画寸法を取得したときと同じく、通常表示用の保存サイズだけを更新する。
                state.VideoAspectRatio = nextVideoWidth / nextVideoHeight;
                state.IsShorts = nextVideoWidth < nextVideoHeight;
                var nextSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
                    state.Width, state.Height, preferHeight: false, state.VideoAspectRatio.Value);
                state.Width = nextSize.Width;
                state.Height = nextSize.Height;

                // 初回適用だけでなく、同じ動画の寸法通知が再度届いても縮小しない。
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    callbacks.ClampToHost(state, runtime.Root, true);
                    ArrangeSurface();
                    Assert.True(double.IsNaN(runtime.Root.Width));
                    Assert.True(double.IsNaN(runtime.Root.Height));
                    Assert.Equal(screenWidth, runtime.Root.ActualWidth);
                    Assert.Equal(screenHeight, runtime.Root.ActualHeight);
                    Assert.Equal(restoreBounds, runtime.FullScreenRestoreBounds);
                    Assert.Equal(restoreBounds.Width, runtime.TaskbarWindow.Width);
                    Assert.Equal(restoreBounds.Height, runtime.TaskbarWindow.Height);
                    Assert.Equal(restoreBounds.Left, state.Left);
                    Assert.Equal(restoreBounds.Top, state.Top);
                    Assert.Equal(2, state.ContentZoomFactor);
                    Assert.True(state.IsMuted);
                }

                YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
                Assert.False(runtime.IsFullScreen);
                // 通常表示へ戻った時点で、新しい動画比率と通常表示の下限を適用する。
                Assert.Equal(nextSize.Width, runtime.TaskbarWindow.Width);
                Assert.Equal(nextSize.Height, runtime.TaskbarWindow.Height);
                callbacks.ClampToHost(state, runtime.Root, true);
                Assert.Equal(nextSize.Width, runtime.Root.Width);
                Assert.Equal(nextSize.Height, runtime.Root.Height);
                Assert.Equal(nextSize.Width, runtime.TaskbarWindow.Width);
                Assert.Equal(nextSize.Height, runtime.TaskbarWindow.Height);
            }
            finally
            {
                runtime.TaskbarWindow.Close();
            }
        });
    }

    [Fact]
    public void EnsureVisibleOnVirtualScreenMovesOffscreenWindowIntoMinimumVisibleArea()
    {
        var state = new YouTubeMiniPlayerState
        {
            Left = -520,
            Top = 820,
            Width = 380,
            Height = 221.625
        };
        var hostOrigin = new Point(100, 50);
        var screenBounds = new Rect(0, 0, 1000, 800);

        YouTubeMiniPlayerPositionService.EnsureVisibleOnVirtualScreen(
            state,
            hostOrigin,
            screenBounds);

        var restoredScreenBounds = YouTubeMiniPlayerPositionService.GetScreenBounds(
            state,
            hostOrigin);
        Assert.True(YouTubeMiniPlayerLayoutService.HasEnoughVisibleArea(
            restoredScreenBounds,
            screenBounds));
    }

    [Fact]
    public void ApplyHostOriginDeltaKeepsIndependentWindowScreenPosition()
    {
        var state = new YouTubeMiniPlayerState
        {
            Left = 300,
            Top = 160,
            Width = 380,
            Height = 221.625
        };
        var previousOrigin = new Point(100, 50);
        var currentOrigin = new Point(260, 130);
        var previousScreenPosition = YouTubeMiniPlayerPositionService.GetPopupScreenPosition(
            state,
            previousOrigin);

        YouTubeMiniPlayerPositionService.ApplyHostOriginDelta(
            state,
            previousOrigin,
            currentOrigin);

        var currentScreenPosition = YouTubeMiniPlayerPositionService.GetPopupScreenPosition(
            state,
            currentOrigin);
        Assert.Equal(previousScreenPosition.X, currentScreenPosition.X, precision: 3);
        Assert.Equal(previousScreenPosition.Y, currentScreenPosition.Y, precision: 3);
    }

    [Fact]
    public void MagneticSnapAlignsToScreenAndNearbyPlayerEdges()
    {
        var screenBounds = new Rect(0, 0, 1920, 1080);
        var otherBounds = new[] { new Rect(600, 200, 380, 220) };

        var screenSnapped = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(7, 500, 380, 220),
            screenBounds,
            otherBounds);
        var playerSnapped = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(978, 208, 380, 220),
            screenBounds,
            otherBounds);

        Assert.Equal(0, screenSnapped.X, precision: 3);
        Assert.Equal(500, screenSnapped.Y, precision: 3);
        Assert.Equal(980, playerSnapped.X, precision: 3);
        Assert.Equal(200, playerSnapped.Y, precision: 3);
    }

    [Fact]
    public void MagneticSnapAlignsInsideAndOutsideBrowserWindowEdges()
    {
        var screenBounds = new Rect(0, 0, 1920, 1080);
        var browserWindowBounds = new Rect(200, 100, 1000, 700);

        var insideLeft = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(208, 300, 380, 220),
            screenBounds,
            [browserWindowBounds]);
        var outsideRight = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(1191, 300, 380, 220),
            screenBounds,
            [browserWindowBounds]);
        var outsideBottom = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(500, 809, 380, 220),
            screenBounds,
            [browserWindowBounds]);

        Assert.Equal(200, insideLeft.X, precision: 3);
        Assert.Equal(1200, outsideRight.X, precision: 3);
        Assert.Equal(800, outsideBottom.Y, precision: 3);
    }

    [Fact]
    public void MagneticSnapUsesEachPlayersOwnHostOrigin()
    {
        var movingState = new YouTubeMiniPlayerState
        {
            Left = 873,
            Top = 158,
            Width = 380,
            Height = 220
        };
        var otherState = new YouTubeMiniPlayerState
        {
            Left = 20,
            Top = 70,
            Width = 380,
            Height = 220
        };
        var movingBounds = YouTubeMiniPlayerPositionService.GetScreenBounds(
            movingState,
            new Point(100, 50));
        var otherBounds = YouTubeMiniPlayerPositionService.GetScreenBounds(
            otherState,
            new Point(580, 130));

        var snapped = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            movingBounds,
            new Rect(0, 0, 1920, 1080),
            [otherBounds]);

        Assert.Equal(980, snapped.X, precision: 3);
        Assert.Equal(200, snapped.Y, precision: 3);
    }

    [Fact]
    public void MagneticSnapIgnoresDistantEdges()
    {
        var position = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
            new Rect(100, 100, 380, 220),
            new Rect(0, 0, 1920, 1080),
            new[] { new Rect(800, 700, 380, 220) });

        Assert.Equal(100, position.X, precision: 3);
        Assert.Equal(100, position.Y, precision: 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebViewCleanupPreservesResetAndFinalDisposalOrder(bool closing)
    {
        RunOnStaThread(() =>
        {
            var state = new YouTubeMiniPlayerState { Width = 480, Height = 270 };
            var webView = new CleanupTrackingWebView();
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "後始末の自動検証", webView, webView, null).Runtime;
            var dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            var qualityTimer = new OwnedDispatcherTimer(
                DispatcherTimerOwner.MiniPlayerRuntime, DispatcherTimerTerminationPhase.RuntimeDispose)
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            runtime.WebDrag.FollowTimer = dragTimer;
            runtime.SizeBasedQualityUpdateTimer = qualityTimer;
            dragTimer.Start();
            qualityTimer.Start();

            runtime.Registrations.WebMessageReceived = (_, _) => { };
            runtime.Registrations.DocumentTitleChanged = (_, _) => { };
            runtime.Registrations.ContainsFullScreenElementChanged = (_, _) => { };
            var registrationReleases = 0;
            runtime.Registrations.Transparency = new CleanupRegistration(() =>
            {
                registrationReleases++;
                if (!closing)
                {
                    return;
                }

                Assert.False(runtime.IsDisposed);
                Assert.Same(webView, runtime.WebView);
                Assert.True(dragTimer.IsEnabled);
                Assert.True(qualityTimer.IsEnabled);
            });

            var webViewDisposals = 0;
            webView.OnDisposing = () =>
            {
                webViewDisposals++;
                Assert.Equal(closing, runtime.IsDisposed);
                Assert.Equal(closing ? 1 : 0, registrationReleases);
                Assert.Null(runtime.Registrations.WebMessageReceived);
                Assert.Null(runtime.Registrations.DocumentTitleChanged);
                Assert.Null(runtime.Registrations.ContainsFullScreenElementChanged);
                Assert.Null(runtime.WebView);
                Assert.Empty(runtime.WebHost.Children);
                Assert.NotNull(runtime.TaskbarWindow.Content);
                Assert.Equal(!closing, dragTimer.IsEnabled);
                Assert.False(qualityTimer.IsEnabled);
                Assert.Equal(!closing, runtime.TitleBandPopup.Child is not null);
                Assert.Equal(!closing, runtime.ToolbarPopup.Child is not null);
            };

            try
            {
                if (closing)
                {
                    YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
                    YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
                    Assert.Null(runtime.TaskbarWindow.Content);
                }
                else
                {
                    YouTubeMiniPlayerRuntimeLifecycleService.ResetWebView(runtime);
                    Assert.False(runtime.IsDisposed);
                    Assert.NotNull(runtime.TaskbarWindow.Content);
                    Assert.NotNull(runtime.Registrations.Transparency);
                }

                Assert.Equal(1, webViewDisposals);
                Assert.Equal(closing ? 1 : 0, registrationReleases);
            }
            finally
            {
                webView.OnDisposing = null;
                YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
            }
        });
    }

    private sealed class CleanupRegistration(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class CleanupTrackingWebView : WebView2
    {
        public Action? OnDisposing { get; set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                OnDisposing?.Invoke();
            }

            base.Dispose(disposing);
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }
}
