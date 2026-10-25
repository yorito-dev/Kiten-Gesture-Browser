using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class LocalVideoMiniPlayerCloseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TitleBandStaysAboveLocalVideoInputWindow(bool topmost)
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            var state = new YouTubeMiniPlayerState { SourceKind = MiniPlayerSourceKind.LocalFile, Width = 640, Height = 360 };
            using var view = new LocalVideoPlayerView();
            // 動画のデコードや画面表示はせず、再生方式に応じた本番の操作面を接続する。
            var separateInputSurface = VisualTreeHelper.GetParent(view.DragSurface) is null;
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "ローカル動画",
                separateInputSurface ? new Border() : view, null, view).Runtime;
            using var host = CreateHiddenSource(runtime.Root, topmost);
            runtime.Root.Measure(new Size(state.Width, state.Height));
            runtime.Root.Arrange(new Rect(0, 0, state.Width, state.Height));
            // MediaElementでは操作面が既にhost内にある。LibVLCの独立した操作面だけ別HWNDへ置く。
            using var videoInput = separateInputSurface
                ? CreateHiddenSource(view.DragSurface, topmost, host.Handle)
                : null;
            var videoInputHandle = videoInput?.Handle ?? host.Handle;
            using var titleBand = CreateHiddenSource(runtime.TitleBand, topmost, host.Handle);
            try
            {
                Assert.True(SetWindowPos(videoInputHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0013));
                var orderOwner = YouTubeMiniPlayerOverlayPopupPositionService.GetOverlayOrderOwner(runtime);
                var ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(orderOwner));
                Assert.True(PopupWindowOrderService.ApplyRelativeToOwner(
                    titleBand.Handle, ownerSource.Handle, 0, 0, 0, 0, 0x0013));
                Assert.True(IsAbove(titleBand.Handle, videoInputHandle), "タイトルバーが動画操作面にクリックを遮られています。");
                Assert.Same(view.DragSurface, orderOwner);
            }
            finally { runtime.TaskbarWindow.Close(); }
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube)]
    [InlineData(MiniPlayerSourceKind.LocalFile)]
    public void UnconnectedVideoInputUsesHostForWindowOrder(MiniPlayerSourceKind sourceKind)
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            var state = new YouTubeMiniPlayerState { SourceKind = sourceKind, Width = 640, Height = 360 };
            using var view = new LocalVideoPlayerView();
            var runtime = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), "動画", new Border(), null,
                sourceKind == MiniPlayerSourceKind.LocalFile ? view : null).Runtime;
            try
            {
                Assert.Same(runtime.Root, YouTubeMiniPlayerOverlayPopupPositionService.GetOverlayOrderOwner(runtime));
            }
            finally { runtime.TaskbarWindow.Close(); }
            return Task.CompletedTask;
        });

    private static HwndSource CreateHiddenSource(Visual visual, bool topmost, IntPtr owner = default)
    {
        var source = new HwndSource(new HwndSourceParameters("Kiten非表示回帰テスト")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = topmost ? 0x00000008 : 0,
            ParentWindow = owner,
            Width = 8,
            Height = 8
        });
        source.RootVisual = visual;
        return source;
    }

    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var previous = GetWindow(lower, 3); previous != IntPtr.Zero; previous = GetWindow(previous, 3))
        {
            if (previous == upper) return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [Fact]
    public void CloseButtonReleasesLocalViewAndRemovesPlayer()
        => WorkspaceTrashWindowTests.RunInSta(() =>
        {
            var state = new YouTubeMiniPlayerState
            {
                SourceKind = MiniPlayerSourceKind.LocalFile,
                LocalFilePath = Path.Combine(Path.GetTempPath(), "close-test.mp4"),
                Title = "ローカル動画",
                Width = 640,
                Height = 360
            };
            var view = new LocalVideoPlayerView();
            view.Initialize(state);
            var controller = new YouTubeMiniPlayerController();
            var parts = YouTubeMiniPlayerRuntimeFactory.Create(
                state, new Style(typeof(Button)), state.Title, view, null, view);
            controller.Register(state, parts.Runtime);
            var wiring = YouTubeMiniPlayerRuntimeWiringCallbackFactory.Create(
                controller.Runtimes, _ => { }, (_, _) => { }, (_, _) => { }, _ => { },
                (_, _) => { }, _ => { }, id => Assert.True(controller.TryClose(id)),
                (_, _) => { }, _ => { }, _ => { }, () => new Point(), (_, _) => { },
                (_, _) => { }, _ => { }, () => { }, (_, _) => { }, _ => { }, 0.25);
            YouTubeMiniPlayerRuntimeWiringService.Attach(state, parts, controller.Runtimes, wiring);
            try
            {
                // 画面を表示せず、本番のボタンと終了経路を通す。
                parts.TitleBandParts.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(controller.States);
                Assert.Empty(controller.Runtimes);
                Assert.True(parts.Runtime.IsDisposed);
                Assert.Null(view.Controller);
                Assert.Null(parts.Runtime.TaskbarWindow.Content);
                Assert.Empty(parts.Runtime.WebHost.Children);
            }
            finally
            {
                view.Dispose();
                parts.Runtime.TaskbarWindow.Close();
            }
            return Task.CompletedTask;
        });
}
