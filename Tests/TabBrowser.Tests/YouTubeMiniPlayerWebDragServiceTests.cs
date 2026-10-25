using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerWebDragServiceTests
{
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
            Assert.False(f.Runtime.IsWebDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.Null(f.Runtime.WebDragFollowTimer);
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
            Assert.False(f.Runtime.IsWebDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.False(f.Runtime.WebDragFollowTimer!.IsEnabled);
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
            Assert.False(f.Runtime.IsWebDragging);
            Assert.Equal(1, f.Saves);
            f.Cursor = new Point(240, 280);
            f.Send(YouTubeMiniPlayerDragPhase.Move, resize);
            Assert.False(f.Runtime.IsWebDragging);
            Assert.False(f.Runtime.WebDragFollowTimer!.IsEnabled);
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
            Assert.False(f.Runtime.IsWebDragging);
            Assert.False(f.Runtime.WebDragFollowTimer!.IsEnabled);
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
            Assert.True(f.Runtime.IsWebDragging);
            Assert.Equal(resize, f.Runtime.IsResizing);
            if (resize)
            {
                Assert.Equal(new Point(50, 60), f.Bounds.TopLeft);
                Assert.True(f.Bounds.Width > 640);
            }
            else Assert.Equal(new Point(70, 70), f.Bounds.TopLeft);
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            Assert.True(f.Runtime.IsWebDragging);
            Assert.Equal(0, f.Saves);
            f.Down = false;
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            f.Send(YouTubeMiniPlayerDragPhase.End, resize);
            Assert.False(f.Runtime.IsWebDragging);
            Assert.False(f.Runtime.IsResizing);
            Assert.Equal(1, f.Saves);
        });

    [Fact]
    public void HeldMoveStillRecoversMissingStart()
        => Run(f =>
        {
            f.Send(YouTubeMiniPlayerDragPhase.Move);
            Assert.True(f.Runtime.IsWebDragging);
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
            Assert.False(f.Runtime.IsWebDragging);
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
        public int Saves, BroughtToFront, CursorReads, ButtonReads;
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

        public void Dispose()
        {
            YouTubeMiniPlayerWebDragService.Stop(Runtime);
            Runtime.TaskbarWindow.Close();
        }
    }
}
