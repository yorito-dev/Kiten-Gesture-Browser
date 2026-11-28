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
            StopWebDrag(runtime, save: true, callbacks, waitForEnd: true);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Start)
        {
            runtime.WebDrag.IsCancelled = false;
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
            if (runtime.WebDrag.IsCancelled) return;
            if (!runtime.WebDrag.IsDragging)
            {
                StartWebDrag(runtime, isResize, callbacks, resizeEdges);
            }

            UpdateWebDrag(runtime, stopWhenReleased: true, callbacks);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Cancel)
        {
            if (!runtime.IsSurfaceDragging && runtime.CancelSurfaceCtrlResize is null)
            {
                runtime.WebDrag.CancelCtrlResize?.Invoke();
            }
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.End)
        {
            if (!cancelled && runtime.WebDrag.IsDragging && callbacks.IsLeftMouseButtonDown())
            {
                return;
            }

            runtime.WebDrag.CancelCtrlResize = null;
            StopWebDrag(runtime, save: true, callbacks);
            if (cancelled) runtime.WebDrag.IsCancelled = true;
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

        runtime.WebDrag.StartLeft = runtime.State.Left;
        runtime.WebDrag.StartTop = runtime.State.Top;
        runtime.WebDrag.StartWidth = Math.Max(
            YouTubeMiniPlayerLayoutService.MinimumWidth,
            runtime.State.Width);
        runtime.WebDrag.StartHeight = Math.Max(
            YouTubeMiniPlayerLayoutService.MinimumHeight,
            runtime.State.Height);
        runtime.WebDrag.StartScreenPoint = cursorPoint;
        runtime.WebDrag.IsDragging = true;
        runtime.WebDrag.ResizeEdges = resizeEdges;
        runtime.WebDrag.CancelCtrlResize = null;
        runtime.IsResizing = isResize || resizeEdges != MiniPlayerResizeEdges.None;
        runtime.WebDrag.FollowTimer ??= createFollowTimer(runtime);
        runtime.WebDrag.FollowTimer.Stop();
        runtime.WebDrag.FollowTimer.Start();
    }

    public static bool IsUpdateActive(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.WebDrag.IsDragging)
        {
            return true;
        }

        runtime.WebDrag.FollowTimer?.Stop();
        return false;
    }

    public static void Stop(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.WebDrag.IsDragging = false;
        runtime.IsResizing = false;
        runtime.WebDrag.ResizeEdges = MiniPlayerResizeEdges.None;
        runtime.WebDrag.CancelCtrlResize = null;
        runtime.WebDrag.FollowTimer?.Stop();
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
        if (runtime.IsResizing && resizeEdges == MiniPlayerResizeEdges.None)
        {
            runtime.WebDrag.CancelCtrlResize = () => StopWebDrag(runtime, save: true, callbacks, restoreBounds: true);
        }
        if (runtime.IsResizing)
        {
            runtime.Overlay.TitleBandHideTimer?.Stop();
            runtime.Overlay.ToolbarHideTimer?.Stop();
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
            StopWebDrag(runtime, save: true, callbacks, waitForEnd: true);
            return;
        }

        var currentScreenPoint = callbacks.GetCursorScreenPoint();
        if (runtime.WebDrag.ResizeEdges != MiniPlayerResizeEdges.None)
        {
            var edges = runtime.WebDrag.ResizeEdges;
            var bounds = YouTubeMiniPlayerResizeService.CalculateResizeBounds(runtime.State,
                runtime.WebDrag.StartLeft, runtime.WebDrag.StartTop, runtime.WebDrag.StartWidth, runtime.WebDrag.StartHeight,
                currentScreenPoint.X - runtime.WebDrag.StartScreenPoint.X, currentScreenPoint.Y - runtime.WebDrag.StartScreenPoint.Y,
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
                runtime.WebDrag.StartLeft,
                runtime.WebDrag.StartTop,
                runtime.WebDrag.StartWidth,
                runtime.WebDrag.StartHeight,
                currentScreenPoint.X - runtime.WebDrag.StartScreenPoint.X,
                currentScreenPoint.Y - runtime.WebDrag.StartScreenPoint.Y,
                callbacks.Clamp,
                YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
        }
        else
        {
            var position = YouTubeMiniPlayerInputService.CalculateDragPosition(
                runtime.WebDrag.StartLeft,
                runtime.WebDrag.StartTop,
                runtime.WebDrag.StartScreenPoint,
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
        YouTubeMiniPlayerWebDragCallbacks callbacks,
        bool waitForEnd = false,
        bool restoreBounds = false)
    {
        if (!runtime.WebDrag.IsDragging && !restoreBounds)
        {
            runtime.WebDrag.FollowTimer?.Stop();
            return;
        }

        var drag = runtime.WebDrag;
        // マウス解放を先に検出した場合も、同じドラッグのWebView通知で取消を受け付ける。
        var cancel = waitForEnd ? drag.CancelCtrlResize : null;
        Stop(runtime);
        drag.CancelCtrlResize = cancel;
        if (restoreBounds)
        {
            drag.IsCancelled = true;
            runtime.State.Left = drag.StartLeft;
            runtime.State.Top = drag.StartTop;
            runtime.State.Width = drag.StartWidth;
            runtime.State.Height = drag.StartHeight;
        }
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
