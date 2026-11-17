using System;
using System.Windows;
using System.Windows.Threading;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerWebDragCallbacks(
    Action<Guid, bool> BringToFront,
    Action<YouTubeMiniPlayerState, bool> SetToolbarVisibility,
    Action<YouTubeMiniPlayerRuntime> KeepToolbarVisible,
    Func<Point> GetCursorScreenPoint,
    Func<bool> IsLeftMouseButtonDown,
    Action<YouTubeMiniPlayerState, FrameworkElement, bool> Clamp,
    Action<YouTubeMiniPlayerRuntime> SyncPopupOffsets,
    Action<YouTubeMiniPlayerRuntime> ReapplyContentTransform,
    Action QueueSave);

internal static class YouTubeMiniPlayerWebDragService
{
    public static void HandlePhase(
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerDragPhase phase,
        bool isResize,
        YouTubeMiniPlayerWebDragCallbacks callbacks,
        MiniPlayerResizeEdges resizeEdges = MiniPlayerResizeEdges.None,
        bool cancelled = false)
    {
        if (runtime.IsDisposed || runtime.IsClosingTaskbarWindow)
        {
            Stop(runtime);
            return;
        }
        if (runtime.IsFullScreen || runtime.IsInputTransparent)
        {
            Stop(runtime);
            return;
        }

        // 遅延したページ通知から、解放後の追従やリサイズを再開しない。
        if (phase is YouTubeMiniPlayerDragPhase.Start or YouTubeMiniPlayerDragPhase.Move
            && !callbacks.IsLeftMouseButtonDown())
        {
            StopWebDrag(runtime, save: true, callbacks);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Start)
        {
            runtime.WebDragCancelled = false;
            callbacks.BringToFront(runtime.State.Id, false);
            if (!isResize && resizeEdges == MiniPlayerResizeEdges.None)
            {
                callbacks.SetToolbarVisibility(runtime.State, true);
            }

            StartWebDrag(runtime, isResize, callbacks, resizeEdges);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Move)
        {
            if (runtime.WebDragCancelled) return;
            if (!runtime.IsWebDragging)
            {
                StartWebDrag(runtime, isResize, callbacks, resizeEdges);
            }

            UpdateWebDrag(runtime, stopWhenReleased: true, callbacks);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.End)
        {
            if (!cancelled && runtime.IsWebDragging && callbacks.IsLeftMouseButtonDown())
            {
                return;
            }

            StopWebDrag(runtime, save: true, callbacks);
            if (cancelled) runtime.WebDragCancelled = true;
        }
    }

    public static void Start(
        YouTubeMiniPlayerRuntime runtime,
        Point cursorPoint,
        bool isResize,
        Func<YouTubeMiniPlayerRuntime, DispatcherTimer> createFollowTimer,
        MiniPlayerResizeEdges resizeEdges = MiniPlayerResizeEdges.None)
    {
        if (runtime.IsDisposed || runtime.IsClosingTaskbarWindow)
        {
            Stop(runtime);
            return;
        }
        if (runtime.IsFullScreen)
        {
            Stop(runtime);
            return;
        }

        runtime.WebDragStartLeft = runtime.State.Left;
        runtime.WebDragStartTop = runtime.State.Top;
        runtime.WebDragStartWidth = Math.Max(
            YouTubeMiniPlayerLayoutService.MinimumWidth,
            runtime.State.Width);
        runtime.WebDragStartHeight = Math.Max(
            YouTubeMiniPlayerLayoutService.MinimumHeight,
            runtime.State.Height);
        runtime.WebDragStartScreenPoint = cursorPoint;
        runtime.IsWebDragging = true;
        runtime.WebDragResizeEdges = resizeEdges;
        runtime.IsResizing = isResize || resizeEdges != MiniPlayerResizeEdges.None;
        runtime.WebDragFollowTimer ??= createFollowTimer(runtime);
        runtime.WebDragFollowTimer.Stop();
        runtime.WebDragFollowTimer.Start();
    }

    public static bool IsUpdateActive(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsWebDragging)
        {
            return true;
        }

        runtime.WebDragFollowTimer?.Stop();
        return false;
    }

    public static void Stop(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.IsWebDragging = false;
        runtime.IsResizing = false;
        runtime.WebDragResizeEdges = MiniPlayerResizeEdges.None;
        runtime.WebDragFollowTimer?.Stop();
    }

    private static void StartWebDrag(
        YouTubeMiniPlayerRuntime runtime,
        bool isResize,
        YouTubeMiniPlayerWebDragCallbacks callbacks,
        MiniPlayerResizeEdges resizeEdges)
    {
        Start(
            runtime,
            callbacks.GetCursorScreenPoint(),
            isResize,
            current => CreateFollowTimer(current, callbacks), resizeEdges);
        if (runtime.IsResizing)
        {
            runtime.TitleBandHideTimer?.Stop();
            runtime.ToolbarHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.ToolbarPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            callbacks.KeepToolbarVisible(runtime);
        }
    }

    private static DispatcherTimer CreateFollowTimer(
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerWebDragCallbacks callbacks)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) => UpdateWebDrag(runtime, stopWhenReleased: true, callbacks);
        return timer;
    }

    private static void UpdateWebDrag(
        YouTubeMiniPlayerRuntime runtime,
        bool stopWhenReleased,
        YouTubeMiniPlayerWebDragCallbacks callbacks)
    {
        if (runtime.IsDisposed || runtime.IsClosingTaskbarWindow)
        {
            Stop(runtime);
            return;
        }
        if (runtime.IsFullScreen)
        {
            Stop(runtime);
            return;
        }

        if (!IsUpdateActive(runtime))
        {
            return;
        }

        if (stopWhenReleased && !callbacks.IsLeftMouseButtonDown())
        {
            StopWebDrag(runtime, save: true, callbacks);
            return;
        }

        var currentScreenPoint = callbacks.GetCursorScreenPoint();
        if (runtime.WebDragResizeEdges != MiniPlayerResizeEdges.None)
        {
            var edges = runtime.WebDragResizeEdges;
            var bounds = YouTubeMiniPlayerResizeService.CalculateResizeBounds(runtime.State,
                runtime.WebDragStartLeft, runtime.WebDragStartTop, runtime.WebDragStartWidth, runtime.WebDragStartHeight,
                currentScreenPoint.X - runtime.WebDragStartScreenPoint.X, currentScreenPoint.Y - runtime.WebDragStartScreenPoint.Y,
                edges.HasFlag(MiniPlayerResizeEdges.Left), edges.HasFlag(MiniPlayerResizeEdges.Top),
                edges.HasFlag(MiniPlayerResizeEdges.Right), edges.HasFlag(MiniPlayerResizeEdges.Bottom),
                YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
            YouTubeMiniPlayerResizeService.ApplyBounds(runtime.State, runtime.Root, bounds, callbacks.Clamp);
        }
        else if (runtime.IsResizing)
        {
            YouTubeMiniPlayerResizeService.ApplyCtrlDragResize(
                runtime.State,
                runtime.Root,
                runtime.WebDragStartLeft,
                runtime.WebDragStartTop,
                runtime.WebDragStartWidth,
                runtime.WebDragStartHeight,
                currentScreenPoint.X - runtime.WebDragStartScreenPoint.X,
                currentScreenPoint.Y - runtime.WebDragStartScreenPoint.Y,
                callbacks.Clamp,
                YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
        }
        else
        {
            var position = YouTubeMiniPlayerInputService.CalculateDragPosition(
                runtime.WebDragStartLeft,
                runtime.WebDragStartTop,
                runtime.WebDragStartScreenPoint,
                currentScreenPoint);
            runtime.State.Left = position.X;
            runtime.State.Top = position.Y;
            runtime.State.UpdatedAt = DateTimeOffset.UtcNow;
            callbacks.Clamp(runtime.State, runtime.Root, false);
            callbacks.KeepToolbarVisible(runtime);
        }
    }

    private static void StopWebDrag(
        YouTubeMiniPlayerRuntime runtime,
        bool save,
        YouTubeMiniPlayerWebDragCallbacks callbacks)
    {
        if (!runtime.IsWebDragging)
        {
            runtime.WebDragFollowTimer?.Stop();
            return;
        }

        Stop(runtime);
        callbacks.Clamp(runtime.State, runtime.Root, true);
        callbacks.SyncPopupOffsets(runtime);
        callbacks.ReapplyContentTransform(runtime);
        callbacks.KeepToolbarVisible(runtime);
        if (save)
        {
            callbacks.QueueSave();
        }
    }
}
