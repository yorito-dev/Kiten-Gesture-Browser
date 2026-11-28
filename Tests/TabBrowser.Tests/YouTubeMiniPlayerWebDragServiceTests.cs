using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerWebDragServiceTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", "current", true)]
    [InlineData("http://www.youtube.com/watch?v=abc123", "current", false)]
    [InlineData("https://example.test/", "current", false)]
    [InlineData("https://www.youtube.com/watch?v=abc123", "wrong", false)]
    [InlineData("https://www.youtube.com/watch?v=abc123", null, false)]
    public void DragAndHoverRequireTheCurrentPlayersTokenAndHttpsSource(string source, string? token, bool accepted)
        => Run(f =>
        {
            var messageToken = token == "current" ? f.Runtime.WebMessageToken : token;
            f.SendNotification(source, new { type = "youtubeMiniPlayerDrag", phase = "start", token = messageToken });
            Assert.Equal(accepted, f.Runtime.WebDrag.IsDragging);

            f.SendNotification(source, new { type = "youtubeMiniPlayerHover", hovered = true, titleHovered = true, token = messageToken });
            Assert.Equal(accepted ? 1 : 0, f.TitleVisibilityUpdates);
        });

    [Fact]
    public void CaptureCancellationStopsHeldResizeAndRejectsStaleMovesUntilANewStart()
        => Run(f =>
        {
            f.SendEdges("start", 1);
            f.SendEdges("end", 1, cancelled: true);
            Assert.True(f.Down);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.WebDrag.FollowTimer!.IsEnabled);
            Assert.Equal(1, f.Saves);
            var bounds = f.Bounds;
            f.Cursor = new Point(50, 220);
            f.SendEdges("move", 1);
            Assert.Equal(bounds, f.Bounds);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            f.SendEdges("start", 4);
            Assert.True(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.WebDrag.IsCancelled);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeRestoresCtrlResizeEvenIfTheReleaseTimerRunsFirst(bool releaseTimerRan)
        => Run(f =>
        {
            var initial = f.Bounds;
            f.SendEdges("start", 0);
            f.Cursor = new Point(220, 240);
            f.SendEdges("move", 0);
            Assert.True(f.Bounds.Width > initial.Width);
            if (releaseTimerRan)
            {
                f.Down = false;
                typeof(YouTubeMiniPlayerWebDragService).GetMethod("UpdateWebDrag", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [f.Runtime, true, f.Callbacks]);
            }

            f.SendEdges("cancel", 0);
            Assert.Equal(initial, f.Bounds);
            Assert.False(f.Runtime.IsResizing);
            Assert.False(f.Runtime.WebDrag.FollowTimer!.IsEnabled);
            f.Cursor = new Point(310, 280);
            f.SendEdges("move", 0);
            f.SendEdges("end", 0);
            f.SendEdges("cancel", 0);
            Assert.Equal(initial, f.Bounds);
            Assert.Equal(releaseTimerRan ? 2 : 1, f.Saves);

            f.Down = true;
            f.SendEdges("start", 0);
            Assert.True(f.Runtime.IsResizing);
        });

    [Fact]
    public void NativeEscapeRestoresCtrlResizeWithoutAPageCancelMessage()
        => Run(f =>
        {
            var initial = f.Bounds;
            f.SendEdges("start", 0);
            f.Cursor = new Point(220, 240);
            f.SendEdges("move", 0);
            Assert.True(f.Bounds.Width > initial.Width);
            var cancel = Assert.IsType<Action>(f.Runtime.WebDrag.CancelCtrlResize);

            cancel();

            Assert.Equal(initial, f.Bounds);
            Assert.Null(f.Runtime.WebDrag.CancelCtrlResize);
            Assert.False(f.Runtime.IsResizing);
            Assert.True(f.Down);
            f.SendEdges("move", 0);
            f.SendEdges("end", 0);
            Assert.Equal(initial, f.Bounds);
            Assert.Equal(1, f.Saves);
        });

    [Fact]
    public void EscapeDoesNotRollBackAnAcknowledgedReleaseOrAnEdgeResize()
        => Run(f =>
        {
            f.SendEdges("start", 0);
            f.Cursor = new Point(220, 240);
            f.SendEdges("move", 0);
            f.Down = false;
            f.SendEdges("end", 0);
            var committed = f.Bounds;
            f.SendEdges("cancel", 0);
            Assert.Equal(committed, f.Bounds);
            Assert.Equal(1, f.Saves);

            f.Down = true;
            f.SendEdges("start", 4);
            f.Cursor = new Point(260, 260);
            f.SendEdges("move", 4);
            var edgeBounds = f.Bounds;
            Assert.True(edgeBounds.Width > committed.Width);
            f.SendEdges("cancel", 0);
            Assert.Equal(edgeBounds, f.Bounds);
            Assert.True(f.Runtime.IsResizing);
            Assert.Equal(1, f.Saves);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposedOrClosingRuntimeDoesNotApplyLateEdgeMessages(bool disposed)
        => Run(f =>
        {
            f.SendEdges("start", 3);
            f.Runtime.IsDisposed = disposed;
            f.Runtime.IsClosingTaskbarWindow = !disposed;
            var bounds = f.Bounds;
            f.Cursor = new Point(50, 100);
            f.SendEdges("move", 3);
            Assert.Equal(bounds, f.Bounds);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(MiniPlayerResizeEdges.None, f.Runtime.WebDrag.ResizeEdges);
            Assert.Equal(0, f.Saves);
        });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    public void EdgeMessageReachesResizeWithFixedOppositeAnchorAndSingleReleaseSave(int value)
        => Run(f =>
        {
            var edges = (MiniPlayerResizeEdges)value;
            var before = f.Bounds;
            f.SendEdges("start", value);
            f.Cursor = new Point(edges.HasFlag(MiniPlayerResizeEdges.Left) ? 80 : 120,
                edges.HasFlag(MiniPlayerResizeEdges.Top) ? 188 : 212);
            // 移動通知の方向が変わっても開始時の方向を保持する。
            f.SendEdges("move", 0);
            Assert.Equal(edges, f.Runtime.WebDrag.ResizeEdges);
            Assert.True(f.Runtime.IsResizing);
            Assert.True(f.Bounds.Width > before.Width);
            Assert.True(f.Bounds.Height > before.Height);
            Assert.Equal(edges.HasFlag(MiniPlayerResizeEdges.Left) ? before.Right : before.Left,
                edges.HasFlag(MiniPlayerResizeEdges.Left) ? f.Bounds.Right : f.Bounds.Left, 7);
            Assert.Equal(edges.HasFlag(MiniPlayerResizeEdges.Top) ? before.Bottom : before.Top,
                edges.HasFlag(MiniPlayerResizeEdges.Top) ? f.Bounds.Bottom : f.Bounds.Top, 7);
            Assert.Equal(16.0 / 9, f.Bounds.Width / f.Bounds.Height, 7);
            f.Down = false;
            f.SendEdges("end", value);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(MiniPlayerResizeEdges.None, f.Runtime.WebDrag.ResizeEdges);
            Assert.Equal(1, f.Saves);
            var final = f.Bounds;
            f.Cursor = new Point(300, 400);
            f.SendEdges("move", value);
            Assert.Equal(final, f.Bounds);
            Assert.Equal(1, f.Saves);
        });

    [Theory]
    [InlineData("Start", false)]
    [InlineData("Start", true)]
    [InlineData("Move", false)]
    [InlineData("Move", true)]
    public void ReleasedInputCannotStartOrRecoverDrag(string phase, bool resize)
        => Run(f =>
        {
            f.Down = false;
            f.Send(Enum.Parse<YouTubeMiniPlayerDragPhase>(phase), resize);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.Null(f.Runtime.WebDrag.FollowTimer);
            Assert.Equal(0, f.BroughtToFront);
            Assert.Equal(0, f.Saves);
            Assert.Equal(0, f.CursorReads);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedMoveEndsBeforeApplyingPositionOrSize(bool resize)
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Start, resize);
            f.Down = false; f.Cursor = new Point(180, 260);
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.False(f.Runtime.WebDrag.FollowTimer!.IsEnabled);
            Assert.Equal(new Rect(50, 60, 640, 360), f.Bounds);
            Assert.Equal(1, f.Saves);
            var reads = f.CursorReads;
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            Assert.Equal(reads, f.CursorReads);
            Assert.Equal(1, f.Saves);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimerReleaseCannotBeUndoneByDelayedMove(bool resize)
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Start, resize);
            f.Down = false;
            // Timerと同じ本番の更新経路を呼び、実時間の16ms待機に依存しない。
            typeof(YouTubeMiniPlayerWebDragService).GetMethod("UpdateWebDrag", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [f.Runtime, true, f.Callbacks]);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(1, f.Saves);
            f.Cursor = new Point(240, 280);
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.WebDrag.FollowTimer!.IsEnabled);
            Assert.Equal(new Rect(50, 60, 640, 360), f.Bounds);
            Assert.Equal(1, f.Saves);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseBetweenMessageGuardAndUpdateDoesNotApplyMovement(bool resize)
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Start, resize);
            f.ReleaseOnRead = f.ButtonReads + 2;
            f.Cursor = new Point(180, 260);
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.WebDrag.FollowTimer!.IsEnabled);
            Assert.Equal(new Rect(50, 60, 640, 360), f.Bounds);
            Assert.Equal(1, f.Saves);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldDragAndResizeKeepExistingEndAndSaveBehavior(bool resize)
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Start, resize);
            f.Cursor = new Point(120, 210);
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            Assert.True(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(resize, f.Runtime.IsResizing);
            if (resize)
            {
                Assert.Equal(new Point(50, 60), f.Bounds.TopLeft);
                Assert.True(f.Bounds.Width > 640);
            }
            else Assert.Equal(new Point(70, 70), f.Bounds.TopLeft);
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            Assert.True(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(0, f.Saves);
            f.Down = false;
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.Equal(1, f.Saves);
        });

    [Fact]
    public void HeldMoveStillRecoversMissingStart()
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Move);
            Assert.True(f.Runtime.WebDrag.IsDragging);
            f.Cursor = new Point(130, 220);
            f.Send(YouTubeMiniPlayerDragPhase.Move);
            Assert.Equal(new Point(80, 80), f.Bounds.TopLeft);
        });

    [Fact]
    public void FullScreenDoesNotStartDrag()
        => Run(f =>
        {
            f.Runtime.IsFullScreen = true;
            f.Send(YouTubeMiniPlayerDragPhase.Start);
            Assert.False(f.Runtime.WebDrag.IsDragging);
            Assert.Equal(0, f.Saves);
        });

    private static void Run(Action<Fixture> test) => WorkspaceTrashWindowTests.RunInSta(() =>
    {
        using var fixture = new Fixture();
        test(fixture);
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        public bool Down = true;
        public Point Cursor = new(100, 200);
        public int Saves, BroughtToFront, CursorReads, ButtonReads, TitleVisibilityUpdates;
        public int ReleaseOnRead = int.MaxValue;
        public YouTubeMiniPlayerRuntime Runtime { get; }
        public YouTubeMiniPlayerWebDragCallbacks Callbacks { get; }
        public Rect Bounds => new(Runtime.State.Left, Runtime.State.Top, Runtime.State.Width, Runtime.State.Height);

        public Fixture()
        {
            var state = new YouTubeMiniPlayerState { Left = 50, Top = 60, Width = 640, Height = 360 };
            Runtime = YouTubeMiniPlayerRuntimeFactory.Create(state, new Style(typeof(Button)), "動画", new Border(), null, null).Runtime;
            Callbacks = new((_, _) => BroughtToFront++, (_, _) => { }, _ => { },
                () => { CursorReads++; return Cursor; }, () => ++ButtonReads < ReleaseOnRead && Down, (_, _, _) => { },
                _ => { }, _ => { }, () => Saves++);
        }

        public void Send(YouTubeMiniPlayerDragPhase phase, bool resize = false)
            => YouTubeMiniPlayerWebDragService.HandlePhase(Runtime, phase, resize, Callbacks);

        public void SendEdges(string phase, int edges, bool cancelled = false)
            => SendNotification("https://www.youtube.com/watch?v=abc123", new
            {
                type = "youtubeMiniPlayerDrag",
                phase,
                resize = true,
                resizeEdges = edges,
                cancelled,
                token = Runtime.WebMessageToken
            });

        public void SendNotification(string source, object data)
        {
            var dispatch = new YouTubeMiniPlayerWebMessageDispatchCallbacks(Callbacks.BringToFront,
                Callbacks.SetToolbarVisibility, Callbacks.KeepToolbarVisible, Callbacks.GetCursorScreenPoint,
                Callbacks.IsLeftMouseButtonDown, Callbacks.Clamp, Callbacks.SyncPopupOffsets,
                Callbacks.ReapplyContentTransform, Callbacks.QueueSave, (_, _) => TitleVisibilityUpdates++, _ => { }, (_, _) => { },
                _ => { }, (_, _) => { }, _ => { }, _ => { }, (_, _, _, _) => { }, _ => { },
                (_, _, _, _) => { }, _ => { }, (_, _) => { }, _ => { });
            typeof(YouTubeMiniPlayerWebMessageDispatchService).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [Runtime.State, source, JsonSerializer.Serialize(data),
                    new Dictionary<Guid, YouTubeMiniPlayerRuntime> { [Runtime.State.Id] = Runtime }, dispatch]);
        }

        public void Dispose()
        {
            YouTubeMiniPlayerWebDragService.Stop(Runtime);
            Runtime.TaskbarWindow.Close();
        }
    }
}
