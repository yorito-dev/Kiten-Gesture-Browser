using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using TabBrowser.Views;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;
using static TabBrowser.Tests.YouTubeMiniPlayerListViewControllerTests;

namespace TabBrowser.Tests;

// WindowChromeのWPF共有キャッシュを複数STAで同時更新しない。
[CollectionDefinition("YouTubeMiniPlayerListWindow", DisableParallelization = true)]
public sealed class YouTubeMiniPlayerListWindowCollection;

[Collection("YouTubeMiniPlayerListWindow")]
public sealed class YouTubeMiniPlayerListWindowTests
{
    [Fact]
    public void ActiveTitleTracksChangesBehindResultsAndClearsWhenNoTargetRemains() => RunInSta(() =>
    {
        long now = 0;
        using var window = new YouTubeMiniPlayerListWindow(() => now);
        window.ShowActivePlayerTitle("動画A");
        Assert.Equal("動画A", window.InformationText);
        window.ShowOperationStatus("ミュート：2件");
        window.ShowActivePlayerTitle("動画B");
        Assert.Equal("ミュート：2件", window.InformationText);
        now = 5 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Equal("動画B", window.InformationText);
        window.ShowActivePlayerTitle("動画Bの更新後タイトル");
        Assert.Equal("動画Bの更新後タイトル", window.InformationText);
        window.ShowOperationStatus("閉じました");
        window.ShowActivePlayerTitle(string.Empty);
        Assert.Equal("閉じました", window.InformationText);
        now = 10 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Empty(window.InformationText);
        window.ShowActivePlayerTitle("終了前の動画");
        window.Dispose();
        window.ShowActivePlayerTitle("終了後の動画");
        Assert.Empty(window.InformationText);
        Assert.False(window.IsVisible);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("最初の結果")]
    [InlineData("新しい結果")]
    public void OperationStatusExpiresFiveSecondsAfterTheLatestNotification(string nextMessage) => RunInSta(() =>
    {
        long now = 0;
        using var window = new YouTubeMiniPlayerListWindow(() => now);
        window.ShowOperationStatus("最初の結果");
        now = 4 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Equal("最初の結果", window.OperationStatus);
        window.ShowOperationStatus(nextMessage);
        now = 5 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Equal(nextMessage, window.OperationStatus);
        now = 9 * Stopwatch.Frequency - Stopwatch.Frequency / 1000;
        window.ClearExpiredOperationStatus();
        Assert.Equal(nextMessage, window.OperationStatus);
        now = 9 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Empty(window.OperationStatus);
        Assert.False(window.IsVisible);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredResultDoesNotReappearAfterHidingOrMinimizing(bool minimize) => RunInSta(() =>
    {
        long now = 0;
        using var window = new YouTubeMiniPlayerListWindow(() => now) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        window.ShowOperationStatus("再生しました");
        window.ShowOrActivate();
        if (minimize) window.WindowState = WindowState.Minimized;
        else window.Hide();
        now = 5 * Stopwatch.Frequency;
        window.ShowOrActivate();
        Assert.Empty(window.OperationStatus);
        Assert.False(window.IsActive);
        return Task.CompletedTask;
    });

    [Fact]
    public void ExplicitClearAndDisposalDoNotRestoreAnExpiredResult() => RunInSta(() =>
    {
        long now = 0;
        using var window = new YouTubeMiniPlayerListWindow(() => now);
        window.ShowOperationStatus("以前の結果");
        window.ShowOperationStatus(string.Empty);
        now = 10 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Empty(window.OperationStatus);
        window.ShowOperationStatus("終了前の結果");
        window.Dispose();
        now = 20 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        window.ShowOperationStatus("終了後の結果");
        Assert.Empty(window.OperationStatus);
        return Task.CompletedTask;
    });

    [Fact]
    public void SavedPositionSurvivesInitialHeightCorrectionAndReopening() => RunInSta(() =>
    {
        using var original = CreateWindow();
        original.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
        original.ShowOrActivate();
        original.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
        original.Top = Math.Max(SystemParameters.WorkArea.Top + 20, SystemParameters.WorkArea.Bottom - 250);
        WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(original);
        var saved = Assert.IsType<YouTubeMiniPlayerListPositionState>(original.CapturePosition());
        original.Hide();
        using var restored = CreateWindow();
        restored.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
        restored.RestorePosition(saved);
        restored.ShowOrActivate();
        // 仮の680 DIP高では画面内へ押し上げられても、内容高の確定後に保存位置へ戻す。
        WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(restored);
        restored.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
        WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(restored);
        var actual = restored.PointToScreen(new Point());
        Assert.InRange(Math.Abs(saved.Left - actual.X), 0, 1);
        Assert.InRange(Math.Abs(saved.Top - actual.Y), 0, 1);
        restored.Close();
        restored.ShowOrActivate();
        Assert.Equal(actual, restored.PointToScreen(new Point()));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinimizingOrHidingPreservesLastNormalDevicePosition(bool minimize) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.ShowOrActivate();
        window.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
        window.Left += 40;
        window.Top += 25;
        var expected = window.PointToScreen(new Point());
        if (minimize) window.WindowState = WindowState.Minimized;
        else window.Hide();
        var saved = Assert.IsType<YouTubeMiniPlayerListPositionState>(window.CapturePosition());
        Assert.Equal(expected.X, saved.Left);
        Assert.Equal(expected.Y, saved.Top);
        window.ShowOrActivate();
        Assert.Equal(expected, window.PointToScreen(new Point()));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(2147483648d, 0)]
    public void InvalidSavedPositionDoesNotReplaceDefaultPosition(double left, double top) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.RestorePosition(new() { Left = left, Top = top });
        Assert.Null(window.CapturePosition());
        Assert.Equal(100, window.Left);
        Assert.Equal(100, window.Top);
        return Task.CompletedTask;
    });

    [Fact]
    public void RemovedMonitorPositionIsConstrainedAfterContentHeightIsKnown() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
        window.RestorePosition(new() { Left = -100000, Top = 100000 });
        window.ShowOrActivate();
        window.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
        WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(window);
        var saved = Assert.IsType<YouTubeMiniPlayerListPositionState>(window.CapturePosition());
        Assert.True(Math.Abs(saved.Left + 100000) > 1000);
        Assert.True(Math.Abs(saved.Top - 100000) > 1000);
        Assert.Equal(window.PointToScreen(new Point()), new Point(saved.Left, saved.Top));
        return Task.CompletedTask;
    });

    [Fact]
    public void DefaultPositionMenuOverridesSavedPositionEvenBeforeFirstMeasurement() => RunInSta(() =>
    {
        var anchor = new Button { Width = 80, Height = 30 };
        var canvas = new Canvas();
        Canvas.SetLeft(anchor, 740);
        Canvas.SetTop(anchor, 20);
        canvas.Children.Add(anchor);
        var browser = new Window { Content = canvas, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 1000, Height = 220, Left = SystemParameters.WorkArea.Left + 40, Top = SystemParameters.WorkArea.Top + 40 };
        using var window = new YouTubeMiniPlayerListWindow { InitialPlacementTarget = anchor, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            browser.Show();
            window.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
            window.RestorePosition(new() { Left = 100, Top = 100 });
            window.ShowOrActivate();
            // 画面が大きい環境でも、初期の仮の高さによる画面内補正を必ず発生させる。
            window.Height = SystemParameters.WorkArea.Height;
            var menu = (ContextMenu)window.Resources["CaptionContextMenu"];
            Assert.IsType<MenuItem>(menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
            WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(window);
            var expected = YouTubeMiniPlayerListWindow.CalculateInitialDevicePosition(anchor.PointToScreen(new Point(0, anchor.ActualHeight)), VisualTreeHelper.GetDpi(anchor));
            // リセット後は保存位置を初期計測で再適用しない。
            var actual = window.PointToScreen(new Point());
            Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1);
            Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1);
            Assert.Equal(actual, new Point(window.CapturePosition()!.Left, window.CapturePosition()!.Top));
        }
        finally { browser.Close(); }
        return Task.CompletedTask;
    });

    private static YouTubeMiniPlayerListWindow CreateWindow() => new()
    {
        ShowActivated = false,
        ShowInTaskbar = false,
        Opacity = 0,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = 100,
        Top = 100
    };

    [Theory]
    [InlineData("visible")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("disposed")]
    public void OperationStatusReachesOpenListsAndRetainsHiddenSourceWithoutActivatingIt(string sourceState) => RunInSta(() =>
    {
        using var source = CreateWindow();
        using var open = CreateWindow();
        using var hidden = CreateWindow();
        using var minimized = CreateWindow();
        foreach (var window in new[] { source, open, hidden, minimized })
        {
            window.AttachContent(new Border { Height = 220 }, new ResourceDictionary());
            window.ShowOperationStatus("前の結果");
        }
        source.ShowOrActivate();
        open.ShowOrActivate();
        minimized.ShowOrActivate();
        minimized.WindowState = WindowState.Minimized;
        if (sourceState == "hidden") source.Hide();
        if (sourceState == "minimized") source.WindowState = WindowState.Minimized;
        if (sourceState == "disposed") source.Dispose();
        var position = new Point(source.Left, source.Top);
        YouTubeMiniPlayerListWindow.PublishOperationStatus("ライブ配信を最新位置へ移動：2件／スキップ・失敗：1件", source,
            [source, open, hidden, minimized]);
        Assert.Equal(sourceState == "disposed" ? string.Empty : "ライブ配信を最新位置へ移動：2件／スキップ・失敗：1件", source.OperationStatus);
        Assert.Equal(sourceState == "disposed" ? "前の結果" : source.OperationStatus, open.OperationStatus);
        Assert.Equal("前の結果", hidden.OperationStatus);
        Assert.Equal("前の結果", minimized.OperationStatus);
        Assert.False(source.IsActive);
        Assert.False(hidden.IsVisible);
        Assert.Equal(WindowState.Minimized, minimized.WindowState);
        Assert.Equal(position, new Point(source.Left, source.Top));
        return Task.CompletedTask;
    });

    [Fact]
    public void WindowStartsWithoutNativeResourcesAndUsesIndependentTaskbarConfiguration() => RunInSta(() =>
    {
        using var window = new YouTubeMiniPlayerListWindow();
        Assert.True(window.ShowInTaskbar);
        Assert.Null(window.Owner);
        Assert.True(window.Topmost);
        Assert.Equal(ResizeMode.CanMinimize, window.ResizeMode);
        Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.False(window.IsOpen);
        Assert.False(window.IsCollapsed);
        return Task.CompletedTask;
    });

    [Fact]
    public void CloseHidesAndReopeningRetainsContentBoundsAndSelection() => RunInSta(() =>
    {
        using var window = CreateWindow();
        var list = new ListBox { ItemsSource = new[] { "一つ目", "二つ目" }, SelectedIndex = 1 };
        window.AttachContent(list, new ResourceDictionary());
        var changes = new List<bool>();
        window.PresentationChanged += (_, _) => changes.Add(window.IsOpen);
        window.ShowOrActivate();
        Assert.NotEqual(0, GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64() & 0x00000008); // WS_EX_TOPMOST
        window.Width = 900;
        window.Height = 500;
        window.Topmost = false;
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Equal(1, list.SelectedIndex);
        window.ShowOrActivate();
        window.ShowOrActivate();
        Assert.Equal(new[] { true, false, true }, changes);
        Assert.Equal(900, window.Width);
        Assert.Equal(500, window.Height);
        Assert.False(window.Topmost);
        window.WindowState = WindowState.Minimized;
        window.ShowOrActivate();
        Assert.False(window.Topmost);
        Assert.Equal(0, GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64() & 0x00000008); // WS_EX_TOPMOST
        Assert.Same(window, Window.GetWindow(list));
        return Task.CompletedTask;
    });

    [Fact]
    public void MinimizingBrowserDoesNotMinimizeListAndListRestoresNormally() => RunInSta(() =>
    {
        using var window = CreateWindow();
        var browser = new Window { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            browser.Show();
            window.ShowOrActivate();
            browser.WindowState = WindowState.Minimized;
            Assert.True(window.IsOpen);
            Assert.Null(window.Owner);
            window.WindowState = WindowState.Minimized;
            Assert.False(window.IsOpen);
            window.ShowOrActivate();
            Assert.True(window.IsOpen);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally { browser.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void NativeResizeAndMaximizeAreDisabledWhileMinimizeRemainsAvailable() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border(), new ResourceDictionary());
        window.ShowOrActivate();
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, -16).ToInt64();
        Assert.Equal(0, style & (0x00040000 | 0x00010000)); // WS_THICKFRAME / WS_MAXIMIZEBOX
        Assert.NotEqual(0, style & 0x00020000); // WS_MINIMIZEBOX
        var size = new Size(window.Width, window.Height);
        SendMessage(handle, 0x0112, new IntPtr(0xf030), IntPtr.Zero);
        SendMessage(handle, 0x0112, new IntPtr(0xf008), IntPtr.Zero);
        SendMessage(handle, 0x00a3, new IntPtr(2), IntPtr.Zero); // タイトルのダブルクリック
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(size, new Size(window.Width, window.Height));
        Assert.Equal(1, HitTestWindow(window, new Point(1, window.ActualHeight / 2)));
        window.WindowState = WindowState.Maximized;
        Assert.Equal(WindowState.Normal, window.WindowState);
        return Task.CompletedTask;
    });

    [Fact]
    public void NativeCaptionCursorShowsMoveOnlyOverTheDraggableTitle() => RunInSta(() =>
    {
        using var window = CreateWindow();
        var button = new Button { Width = 70, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        var content = new Grid();
        content.Children.Add(button);
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.UpdateLayout();
        Assert.Equal(2, HitTestWindow(window, new Point(30, 16)));
        Assert.Equal(1, HitTestWindow(window, new Point(window.ActualWidth - 35, 16)));
        Assert.Equal(1, HitTestWindow(window, new Point(30, 100)));
        var handle = new WindowInteropHelper(window).Handle;
        var previousCursor = GetCursor();
        try
        {
            // 座標を動かさず、上位ワードに発生元の通知を含む実際の形式で検証する。
            var result = SendMessage(handle, 0x0020, handle, new IntPtr((0x00a0 << 16) | 2));
            Assert.Equal(new IntPtr(1), result);
            Assert.Equal(LoadCursor(IntPtr.Zero, new IntPtr(32646)), GetCursor()); // IDC_SIZEALL
            foreach (var hitTest in new[] { 1, 8, 20 }) // クライアント領域・最小化・閉じる
            {
                object[] arguments = [handle, 0x0020, handle, new IntPtr((0x0200 << 16) | hitTest), false];
                typeof(YouTubeMiniPlayerListWindow).GetMethod("WindowMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, arguments);
                Assert.False((bool)arguments[4]);
            }
        }
        finally { SetCursor(previousCursor); }
        return Task.CompletedTask;
    });

    [Fact]
    public void NativeCaptionHoverShowsCurrentHelpWithoutChangingWindowBounds() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 220 }, new ResourceDictionary());
        window.ShowOrActivate();
        var help = Assert.IsType<ToolTip>(window.Resources["CaptionHelp"]);
        help.Opacity = 0;
        var bounds = new Rect(window.Left, window.Top, window.Width, window.Height);
        var point = new Point(30, 16);
        Assert.Equal(2, HitTestWindow(window, point));
        HoverCaption(window, point);
        Assert.True(help.IsOpen);
        Assert.Same(window, help.PlacementTarget);
        Assert.Equal(PlacementMode.RelativePoint, help.Placement);
        Assert.InRange(Math.Abs(help.HorizontalOffset - 42), 0, 1);
        Assert.InRange(Math.Abs(help.VerticalOffset - 36), 0, 1);
        Assert.Equal("ドラッグして移動（他のディスプレイへ移動可能）。ダブルクリックで表示、非表示。右クリックメニューで位置を戻す",
            Assert.IsType<TextBlock>(help.Content).Text);
        Assert.Equal(Color.FromRgb(17, 20, 26), Assert.IsType<SolidColorBrush>(help.Background).Color);
        Assert.Equal(bounds, new Rect(window.Left, window.Top, window.Width, window.Height));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0x0200)] // クライアント領域へ移動
    [InlineData(0x02a2)] // 非クライアント領域から離脱
    [InlineData(0x0231)] // Windowの移動開始
    [InlineData(0x0201)] // マウス操作
    [InlineData(0x0100)] // キー入力
    public void CaptionHelpClosesWhenTheCaptionInteractionEnds(int message) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.ShowOrActivate();
        var help = Assert.IsType<ToolTip>(window.Resources["CaptionHelp"]);
        help.Opacity = 0;
        var handle = new WindowInteropHelper(window).Handle;
        HoverCaption(window, new Point(30, 16));
        Assert.True(help.IsOpen);
        SendMessage(handle, message, IntPtr.Zero, IntPtr.Zero);
        Assert.False(help.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("minimize")]
    [InlineData("deactivate")]
    [InlineData("dispose")]
    public void CaptionHelpClosesWithItsWindow(string action) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.ShowOrActivate();
        var help = Assert.IsType<ToolTip>(window.Resources["CaptionHelp"]);
        help.Opacity = 0;
        var handle = new WindowInteropHelper(window).Handle;
        HoverCaption(window, new Point(30, 16));
        Assert.True(help.IsOpen);
        switch (action)
        {
            case "hide": window.Close(); break;
            case "minimize": window.WindowState = WindowState.Minimized; break;
            case "dispose": window.Dispose(); break;
            default:
                typeof(YouTubeMiniPlayerListWindow).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                break;
        }
        Assert.False(help.IsOpen);
        if (action != "dispose")
        {
            SendMessage(handle, 0x02a0, new IntPtr(2), PackScreenPoint(window, new Point(30, 16)));
            Assert.False(help.IsOpen); // 閉じた後の古い通知では再表示しない。
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void CaptionHelpDoesNotOpenForClientHoverOrWhileMoving() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.ShowOrActivate();
        var help = Assert.IsType<ToolTip>(window.Resources["CaptionHelp"]);
        help.Opacity = 0;
        var handle = new WindowInteropHelper(window).Handle;
        SendMessage(handle, 0x02a0, new IntPtr(2), PackScreenPoint(window, new Point(30, 16)));
        Assert.False(help.IsOpen); // 追跡していない通知を受け付けない。
        SendMessage(handle, 0x02a0, new IntPtr(1), PackScreenPoint(window, new Point(30, 16)));
        Assert.False(help.IsOpen);
        SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        HoverCaption(window, new Point(30, 16));
        Assert.False(help.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void InitialOffsetUsesTheTriggerDpiIncludingNegativeMonitorCoordinates(double scale)
    {
        var position = YouTubeMiniPlayerListWindow.CalculateInitialDevicePosition(new Point(-200, 300), new DpiScale(scale, scale));
        Assert.Equal(-200 - 696 * scale, position.X);
        Assert.Equal(300 + 4 * scale, position.Y);
    }

    [Fact]
    public void FirstShowMatchesLegacyPopupAndReopeningKeepsTheMovedPosition() => RunInSta(async () =>
    {
        var anchor = new Button { Width = 80, Height = 30 };
        var canvas = new Canvas();
        Canvas.SetLeft(anchor, 740);
        Canvas.SetTop(anchor, 20);
        canvas.Children.Add(anchor);
        var browser = new Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 40,
            Top = SystemParameters.WorkArea.Top + 40,
            Width = 1000,
            Height = 220,
            Content = canvas
        };
        using var window = new YouTubeMiniPlayerListWindow
        {
            InitialPlacementTarget = anchor,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0
        };
        var legacy = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -696,
            VerticalOffset = 4,
            AllowsTransparency = true,
            Child = new Border { Width = 780, Height = 200, Opacity = 0 }
        };
        try
        {
            browser.Show();
            legacy.IsOpen = true;
            window.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
            window.ShowOrActivate();
            window.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var expected = legacy.Child.PointToScreen(new Point());
            var actual = window.PointToScreen(new Point());
            Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1);
            Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1);
            window.Left += 40;
            window.Top += 25;
            var moved = window.PointToScreen(new Point());
            window.Close();
            browser.Left += 100;
            window.ShowOrActivate();
            Assert.Equal(moved, window.PointToScreen(new Point()));
        }
        finally
        {
            legacy.IsOpen = false;
            browser.Close();
        }
    });

    [Theory]
    [InlineData("visible")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    public void CaptionContextMenuReturnsToTheAvailableAnchorAndPreservesContent(string browserState) => RunInSta(async () =>
    {
        var anchor = new Button { Width = 80, Height = 30 };
        var canvas = new Canvas();
        Canvas.SetLeft(anchor, 740);
        Canvas.SetTop(anchor, 20);
        canvas.Children.Add(anchor);
        var browser = new Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 40,
            Top = SystemParameters.WorkArea.Top + 40,
            Width = 1000,
            Height = 220,
            Content = canvas
        };
        using var window = new YouTubeMiniPlayerListWindow
        {
            InitialPlacementTarget = anchor,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0
        };
        try
        {
            browser.Show();
            var list = new ListBox { Height = 240, ItemsSource = new[] { "一つ目", "二つ目" }, SelectedIndex = 1 };
            window.AttachContent(list, new ResourceDictionary());
            window.ShowOrActivate();
            window.FitInitialHeight(SystemParameters.WorkArea.Height - 16);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var initialPosition = window.PointToScreen(new Point());
            var size = new Size(window.Width, window.Height);
            window.Topmost = false;
            window.Left += 90;
            window.Top += 40;
            browser.Left += 40;
            var expected = browserState == "visible"
                ? YouTubeMiniPlayerListWindow.CalculateInitialDevicePosition(anchor.PointToScreen(new Point(0, anchor.ActualHeight)), VisualTreeHelper.GetDpi(anchor))
                : initialPosition;
            if (browserState == "hidden") browser.Hide();
            if (browserState == "minimized") browser.WindowState = WindowState.Minimized;
            var moved = window.PointToScreen(new Point());
            var started = 0;
            var completed = 0;
            window.InteractionStarted += (_, _) => started++;
            window.InteractionCompleted += (_, _) => completed++;
            var menu = (ContextMenu)window.Resources["CaptionContextMenu"];
            menu.Opacity = 0;
            RightClickCaption(window, 1);
            Assert.False(menu.IsOpen);
            Assert.Equal(moved, window.PointToScreen(new Point()));
            RightClickCaption(window);
            Assert.True(menu.IsOpen);
            Assert.Same(window, menu.PlacementTarget);
            Assert.False(menu.StaysOpen);
            var item = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal("デフォルトのウィンドウ位置に戻す", item.Header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(menu.IsOpen);
            var actual = window.PointToScreen(new Point());
            Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1);
            Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1);
            Assert.Equal(size, new Size(window.Width, window.Height));
            Assert.Equal(1, list.SelectedIndex);
            Assert.Same(window, Window.GetWindow(list));
            Assert.False(window.Topmost);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(2, started);
            Assert.Equal(1, completed);
            if (browserState == "hidden") Assert.False(browser.IsVisible);
            if (browserState == "minimized") Assert.Equal(WindowState.Minimized, browser.WindowState);
        }
        finally { browser.Close(); }
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void CaptionHoverPreviewMenuShowsCurrentSettingAndRequestsToggleOnlyWhenAvailable(bool enabled, bool canToggle) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 220 }, new ResourceDictionary());
        window.UpdateHoverPreviewMenu(enabled, canToggle);
        window.ShowOrActivate();
        var menu = (ContextMenu)window.Resources["CaptionContextMenu"];
        menu.Opacity = 0;
        RightClickCaption(window);
        Assert.True(menu.IsOpen);
        Assert.Equal(3, menu.Items.Count);
        Assert.IsType<Separator>(menu.Items[1]);
        var item = Assert.IsType<MenuItem>(menu.Items[2]);
        Assert.Equal(enabled ? "一時視聴：オン" : "一時視聴：オフ", item.Header);
        Assert.Equal(canToggle, item.IsEnabled);
        Assert.Contains(enabled ? "オフに" : "オンに", (string)item.ToolTip);
        Assert.Equal(enabled ? "一時視聴をオフにする" : "一時視聴をオンにする",
            System.Windows.Automation.AutomationProperties.GetName(item));
        Assert.True(ToolTipService.GetShowOnDisabled(item));
        var requests = 0;
        window.HoverPreviewToggleRequested += (_, _) =>
        {
            Assert.False(menu.IsOpen);
            requests++;
        };
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(canToggle ? 1 : 0, requests);
        window.UpdateHoverPreviewMenu(!enabled, true);
        Assert.Equal(enabled ? "一時視聴：オフ" : "一時視聴：オン", item.Header);
        Assert.True(item.IsEnabled);
        window.Hide();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(canToggle ? 1 : 0, requests);
        return Task.CompletedTask;
    });

    [Fact]
    public void CaptionDoubleClickRequestsToggleWithoutResettingPositionOrMaximizing() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 220 }, new ResourceDictionary());
        window.ShowOrActivate();
        window.FitInitialHeight(600);
        window.Left += 70;
        window.Top += 30;
        var position = window.PointToScreen(new());
        var requests = 0;
        window.CollapseRequested += (_, _) => requests++;
        DoubleClickCaption(window, 1);
        Assert.Equal(0, requests);
        DoubleClickCaption(window);
        DoubleClickCaption(window);
        Assert.Equal(2, requests);
        Assert.Equal(position, window.PointToScreen(new()));
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Hide();
        DoubleClickCaption(window);
        Assert.Equal(2, requests);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("minimize")]
    [InlineData("deactivate")]
    [InlineData("move")]
    [InlineData("dispose")]
    public void CaptionContextMenuClosesWithItsWindow(string action) => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 220 }, new ResourceDictionary());
        window.ShowOrActivate();
        var menu = (ContextMenu)window.Resources["CaptionContextMenu"];
        menu.Opacity = 0;
        RightClickCaption(window);
        Assert.True(menu.IsOpen);
        Assert.Equal(Color.FromRgb(17, 20, 26), Assert.IsType<SolidColorBrush>(menu.Background).Color);
        switch (action)
        {
            case "hide": window.Hide(); break;
            case "minimize": window.WindowState = WindowState.Minimized; break;
            case "deactivate":
                typeof(YouTubeMiniPlayerListWindow).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);
                break;
            case "move": SendMessage(new WindowInteropHelper(window).Handle, 0x0231, IntPtr.Zero, IntPtr.Zero); break;
            case "dispose": window.Dispose(); break;
        }
        Assert.False(menu.IsOpen);
        if (action == "hide")
        {
            RightClickCaption(window);
            Assert.False(menu.IsOpen);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void RehostingPreservesElementBindingsResourcesAndExistingEvents() => RunInSta(async () =>
    {
        using var window = CreateWindow();
        var resources = new ResourceDictionary { ["ListTestBrush"] = Brushes.Cyan };
        var grid = new Grid();
        var first = new Button { Name = "First", Content = "更新前" };
        var second = new Button { Name = "Second" };
        second.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Button.Content)) { ElementName = "First" });
        second.SetResourceReference(Control.ForegroundProperty, "ListTestBrush");
        var calls = 0;
        first.Click += (_, _) => calls++;
        grid.Children.Add(first);
        grid.Children.Add(second);
        window.AttachContent(grid, resources);
        window.ShowOrActivate();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        Assert.Equal("更新前", second.Content);
        first.Content = "更新後";
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        Assert.Equal("更新後", second.Content);
        Assert.Same(Brushes.Cyan, second.Foreground);
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, calls);
    });

    [Fact]
    public void NativeMoveDoesNotApplyInitialAutoHeightAndSignalsCompletion() => RunInSta(() =>
    {
        using var window = CreateWindow();
        window.AttachContent(new Border { Height = 200 }, new ResourceDictionary());
        var started = 0;
        var completed = 0;
        window.InteractionStarted += (_, _) => started++;
        window.InteractionCompleted += (_, _) => completed++;
        window.ShowOrActivate();
        window.Height = 500;
        var handle = new WindowInteropHelper(window).Handle;
        SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        Assert.True(window.IsManipulating);
        window.FitInitialHeight(900);
        Assert.Equal(500, window.Height);
        SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
        Assert.False(window.IsManipulating);
        Assert.Equal(1, started);
        Assert.Equal(1, completed);
        return Task.CompletedTask;
    });

    [Fact]
    public void ActualDisposalReleasesNativeWindowAndCannotReopenIt() => RunInSta(() =>
    {
        var window = CreateWindow();
        window.ShowOrActivate();
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        Assert.NotNull(source);
        window.Dispose();
        Assert.True(source.IsDisposed);
        window.ShowOrActivate();
        window.Dispose();
        Assert.False(window.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public void ContentHeightTracksGrowthAndShrinkWithinTheExistingLimits() => RunInSta(() =>
    {
        using var window = CreateWindow();
        var content = new Border { Height = 900 };
        window.MaxHeight = 500;
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.FitInitialHeight(500);
        var position = new Point(window.Left, window.Top);
        var width = window.Width;
        content.Height = 700;
        window.FitToContentHeight();
        Assert.Equal(500, window.Height);
        content.Height = 280;
        window.FitToContentHeight();
        Assert.InRange(window.Height, 280, 282);
        content.Height = 0;
        window.FitToContentHeight();
        Assert.Equal(window.MinHeight, window.Height);
        content.Height = 400;
        window.FitToContentHeight();
        Assert.InRange(window.Height, 400, 402);
        content.Height = 900;
        window.FitToContentHeight();
        Assert.Equal(500, window.Height);
        Assert.Equal(position, new Point(window.Left, window.Top));
        Assert.Equal(width, window.Width);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(250)]
    [InlineData(550)]
    public void HeightChangeDuringWindowMoveIsAppliedAfterTheMoveCompletes(double targetHeight) => RunInSta(() =>
    {
        using var window = CreateWindow();
        var content = new Border { Height = 500 };
        window.MaxHeight = 600;
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.FitInitialHeight(600);
        var initialHeight = window.Height;
        var handle = new WindowInteropHelper(window).Handle;
        SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        content.Height = targetHeight;
        window.FitToContentHeight();
        Assert.Equal(initialHeight, window.Height);
        SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
        Assert.InRange(window.Height, targetHeight, targetHeight + 2);
        return Task.CompletedTask;
    });

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    internal static void DoubleClickCaption(YouTubeMiniPlayerListWindow window, int hitTest = 2)
        => SendMessage(new WindowInteropHelper(window).Handle, 0x00a3, new IntPtr(hitTest), IntPtr.Zero);

    private static void RightClickCaption(YouTubeMiniPlayerListWindow window, int hitTest = 2)
        => SendMessage(new WindowInteropHelper(window).Handle, 0x00a5, new IntPtr(hitTest), PackScreenPoint(window, new(30, 16)));

    [DllImport("user32.dll")]
    private static extern IntPtr GetCursor();

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    internal static bool IsNativeTopmost(Window window)
        => (GetWindowLongPtr(new WindowInteropHelper(window).Handle, -20).ToInt64() & 0x00000008) != 0;

    internal static int HitTestWindow(Window window, Point point)
    {
        var screen = window.PointToScreen(point);
        var packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16);
        return (int)SendMessage(new WindowInteropHelper(window).Handle, 0x0084, IntPtr.Zero, new IntPtr(packed));
    }

    private static IntPtr PackScreenPoint(Window window, Point point)
    {
        var screen = window.PointToScreen(point);
        return new IntPtr(((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16));
    }

    private static void HoverCaption(YouTubeMiniPlayerListWindow window, Point point)
    {
        // 実際のカーソルを動かさず、OSがホバー追跡を開始した状態から通知する。
        typeof(YouTubeMiniPlayerListWindow).GetField("_captionHoverTracking", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, true);
        SendMessage(new WindowInteropHelper(window).Handle, 0x02a0, new IntPtr(2), PackScreenPoint(window, point));
    }
}
