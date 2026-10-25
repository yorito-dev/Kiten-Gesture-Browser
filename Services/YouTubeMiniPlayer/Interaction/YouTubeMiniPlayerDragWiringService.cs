using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerDragWiringCallbacks(
    Action<Guid, bool> BringToFront,
    Func<Point> GetCursorScreenPoint,
    Action<YouTubeMiniPlayerRuntime> KeepToolbarVisible,
    Action<YouTubeMiniPlayerState, FrameworkElement, bool> Clamp,
    Action<YouTubeMiniPlayerRuntime> SyncPopupOffsets,
    Action<YouTubeMiniPlayerRuntime> ReapplyContentTransform,
    Action QueueSave);

internal static class YouTubeMiniPlayerDragWiringService
{
    public static void Attach(
        UIElement dragSurface,
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerDragWiringCallbacks callbacks)
    {
        var isDragging = false;
        var isResizing = false;
        var dragStartScreen = new Point();
        var startLeft = 0.0;
        var startTop = 0.0;
        var startWidth = 0.0;
        var startHeight = 0.0;

        dragSurface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (IsInteractiveElement(source))
            {
                return;
            }

            if (!runtimes.TryGetValue(state.Id, out var runtime))
            {
                return;
            }

            if (runtime.IsFullScreen)
            {
                return;
            }

            callbacks.BringToFront(state.Id, false);
            isDragging = true;
            isResizing = YouTubeMiniPlayerInputService.GetLeftDragAction(Keyboard.Modifiers)
                == MiniPlayerLeftDragAction.Resize;
            runtime.IsSurfaceDragging = !isResizing;
            runtime.IsResizing = isResizing;
            if (isResizing)
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

            dragStartScreen = callbacks.GetCursorScreenPoint();
            startLeft = state.Left;
            startTop = state.Top;
            startWidth = Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, state.Width);
            startHeight = Math.Max(YouTubeMiniPlayerLayoutService.MinimumHeight, state.Height);
            dragSurface.CaptureMouse();
            e.Handled = true;
        };
        dragSurface.MouseMove += (_, _) =>
        {
            if (!isDragging || !runtimes.TryGetValue(state.Id, out var runtime))
            {
                return;
            }

            if (runtime.IsFullScreen)
            {
                isDragging = false;
                runtime.IsSurfaceDragging = false;
                runtime.IsResizing = false;
                dragSurface.ReleaseMouseCapture();
                return;
            }

            var currentScreen = callbacks.GetCursorScreenPoint();
            if (isResizing)
            {
                YouTubeMiniPlayerResizeService.ApplyCtrlDragResize(
                    state,
                    runtime.Root,
                    startLeft,
                    startTop,
                    startWidth,
                    startHeight,
                    currentScreen.X - dragStartScreen.X,
                    currentScreen.Y - dragStartScreen.Y,
                    callbacks.Clamp,
                    YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
            }
            else
            {
                YouTubeMiniPlayerDragService.ApplyDrag(
                    state,
                    runtime.Root,
                    startLeft,
                    startTop,
                    dragStartScreen,
                    currentScreen,
                    callbacks.Clamp);
                callbacks.KeepToolbarVisible(runtime);
            }
        };
        dragSurface.MouseLeftButtonUp += (_, _) =>
        {
            if (!isDragging)
            {
                return;
            }

            isDragging = false;
            isResizing = false;
            dragSurface.ReleaseMouseCapture();
            if (runtimes.TryGetValue(state.Id, out var runtime))
            {
                runtime.IsSurfaceDragging = false;
                runtime.IsResizing = false;
                if (runtime.IsFullScreen)
                {
                    return;
                }

                callbacks.Clamp(state, runtime.Root, true);
                callbacks.SyncPopupOffsets(runtime);
                callbacks.ReapplyContentTransform(runtime);
                callbacks.KeepToolbarVisible(runtime);
            }

            callbacks.QueueSave();
        };
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        return FindVisualParent<Button>(source) is not null
            || FindVisualParent<Slider>(source) is not null
            || FindVisualParent<ComboBox>(source) is not null
            || FindVisualParent<Thumb>(source) is not null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
