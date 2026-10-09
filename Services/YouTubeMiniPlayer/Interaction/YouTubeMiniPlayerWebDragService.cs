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
        YouTubeMiniPlayerWebDragCallbacks callbacks)
    {
        if (runtime.IsFullScreen)
        {
            Stop(runtime);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Start)
        {
            callbacks.BringToFront(runtime.State.Id, false);
            if (!isResize)
            {
                callbacks.SetToolbarVisibility(runtime.State, true);
            }

            StartWebDrag(runtime, isResize, callbacks);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.Move)
        {
            if (!runtime.IsWebDragging)
            {
                StartWebDrag(runtime, isResize, callbacks);
            }

            UpdateWebDrag(runtime, stopWhenReleased: false, callbacks);
            return;
        }

        if (phase == YouTubeMiniPlayerDragPhase.End)
        {
            if (runtime.IsWebDragging && callbacks.IsLeftMouseButtonDown())
            {
                return;
            }

            StopWebDrag(runtime, save: true, callbacks);
        }
    }

    public static void Start(
        YouTubeMiniPlayerRuntime runtime,
        Point cursorPoint,
        bool isResize,
        Func<YouTubeMiniPlayerRuntime, DispatcherTimer> createFollowTimer)
    {
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
        runtime.IsResizing = isResize;
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
        runtime.WebDragFollowTimer?.Stop();
    }

    private static void StartWebDrag(
        YouTubeMiniPlayerRuntime runtime,
        bool isResize,
        YouTubeMiniPlayerWebDragCallbacks callbacks)
    {
        Start(
            runtime,
            callbacks.GetCursorScreenPoint(),
            isResize,
            current => CreateFollowTimer(current, callbacks));
        if (isResize)
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
        if (runtime.IsResizing)
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
                callbacks.Clamp);
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
