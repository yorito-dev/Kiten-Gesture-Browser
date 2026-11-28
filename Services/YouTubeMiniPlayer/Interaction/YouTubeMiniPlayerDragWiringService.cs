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
        var resizeEdges = MiniPlayerResizeEdges.None;
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
            runtime.WebDrag.CancelCtrlResize = null;
            isDragging = true;
            isResizing = YouTubeMiniPlayerInputService.GetLeftDragAction(Keyboard.Modifiers)
                == MiniPlayerLeftDragAction.Resize;
            resizeEdges = isResizing ? MiniPlayerResizeEdges.None
                : GetSurfaceResizeEdges(runtime, dragSurface, e.GetPosition(dragSurface), source);
            isResizing |= resizeEdges != MiniPlayerResizeEdges.None;
            runtime.IsSurfaceDragging = !isResizing;
            runtime.IsResizing = isResizing;
            runtime.Overlay.IsTitleBandDragging = !isResizing && ReferenceEquals(dragSurface, runtime.TitleBand);
            if (runtime.Overlay.IsTitleBandDragging)
            {
                runtime.Overlay.TitleBandHideTimer?.Stop();
            }
            if (isResizing)
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

            dragStartScreen = callbacks.GetCursorScreenPoint();
            startLeft = state.Left;
            startTop = state.Top;
            startWidth = Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, state.Width);
            startHeight = Math.Max(YouTubeMiniPlayerLayoutService.MinimumHeight, state.Height);
            if (isResizing && resizeEdges == MiniPlayerResizeEdges.None)
            {
                runtime.CancelSurfaceCtrlResize = () => FinishDrag(restoreBounds: true);
            }
            dragSurface.CaptureMouse();
            Mouse.UpdateCursor();
            e.Handled = true;
        };
        dragSurface.MouseMove += (_, _) =>
        {
            if (!isDragging || !runtimes.TryGetValue(state.Id, out var runtime))
            {
                return;
            }

            if (runtime.IsFullScreen || runtime.IsDisposed || runtime.IsClosingTaskbarWindow)
            {
                isDragging = false;
                runtime.IsSurfaceDragging = false;
                runtime.IsResizing = false;
                runtime.CancelSurfaceCtrlResize = null;
                runtime.Overlay.IsTitleBandDragging = false;
                dragSurface.ReleaseMouseCapture();
                return;
            }

            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                FinishDrag();
                return;
            }

            var currentScreen = callbacks.GetCursorScreenPoint();
            if (resizeEdges != MiniPlayerResizeEdges.None)
            {
                var bounds = YouTubeMiniPlayerResizeService.CalculateResizeBounds(state,
                    startLeft, startTop, startWidth, startHeight,
                    currentScreen.X - dragStartScreen.X, currentScreen.Y - dragStartScreen.Y,
                    resizeEdges.HasFlag(MiniPlayerResizeEdges.Left), resizeEdges.HasFlag(MiniPlayerResizeEdges.Top),
                    resizeEdges.HasFlag(MiniPlayerResizeEdges.Right), resizeEdges.HasFlag(MiniPlayerResizeEdges.Bottom),
                    YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
                YouTubeMiniPlayerResizeService.ApplyBounds(state, runtime.Root, bounds, callbacks.Clamp);
            }
            else if (isResizing)
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
        dragSurface.QueryCursor += (_, e) =>
        {
            if (runtimes.TryGetValue(state.Id, out var runtime)
                && !runtime.IsFullScreen && !runtime.IsDisposed
                && ((isDragging && isResizing && resizeEdges == MiniPlayerResizeEdges.None
                        || !isDragging && dragSurface.IsMouseOver && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                            && !IsInteractiveElement(e.OriginalSource as DependencyObject))
                    ? Cursors.SizeNWSE
                    : YouTubeMiniPlayerInputService.GetResizeCursor(isDragging ? resizeEdges
                        : GetSurfaceResizeEdges(runtime, dragSurface, Mouse.GetPosition(dragSurface), e.OriginalSource as DependencyObject))) is { } cursor)
            {
                e.Cursor = cursor;
                e.Handled = true;
            }
        };
        dragSurface.PreviewKeyDown += RefreshModifierCursor;
        dragSurface.PreviewKeyUp += RefreshModifierCursor;
        dragSurface.MouseLeftButtonUp += (_, _) => FinishDrag();
        dragSurface.LostMouseCapture += (_, _) => FinishDrag();

        void RefreshModifierCursor(object sender, KeyEventArgs e)
        {
            if (dragSurface.IsMouseOver && e.Key is Key.LeftCtrl or Key.RightCtrl)
                Mouse.UpdateCursor();
        }

        void FinishDrag(bool restoreBounds = false)
        {
            if (!isDragging)
            {
                return;
            }

            isDragging = false;
            isResizing = false;
            resizeEdges = MiniPlayerResizeEdges.None;
            runtimes.TryGetValue(state.Id, out var runtime);
            if (runtime is not null)
            {
                runtime.IsSurfaceDragging = false;
                runtime.IsResizing = false;
                runtime.CancelSurfaceCtrlResize = null;
                runtime.Overlay.IsTitleBandDragging = false;
            }
            dragSurface.ReleaseMouseCapture();
            Mouse.UpdateCursor();
            if (runtime is not null)
            {
                if (runtime.IsFullScreen || runtime.IsDisposed || runtime.IsClosingTaskbarWindow)
                {
                    return;
                }

                if (restoreBounds)
                {
                    state.Left = startLeft;
                    state.Top = startTop;
                    state.Width = startWidth;
                    state.Height = startHeight;
                }

                callbacks.Clamp(state, runtime.Root, true);
                callbacks.SyncPopupOffsets(runtime);
                callbacks.ReapplyContentTransform(runtime);
                callbacks.KeepToolbarVisible(runtime);
            }

            callbacks.QueueSave();
        }
    }

    internal static MiniPlayerResizeEdges GetSurfaceResizeEdges(YouTubeMiniPlayerRuntime runtime, UIElement surface, Point point, DependencyObject? source)
    {
        if (runtime.IsDisposed || runtime.IsClosingTaskbarWindow || runtime.IsFullScreen || runtime.LocalVideoView is not { } view
            || !ReferenceEquals(surface, view.DragSurface) || view.IsControlsTarget(source) || IsInteractiveElement(source))
            return MiniPlayerResizeEdges.None;
        return YouTubeMiniPlayerInputService.GetVideoResizeEdges(point, new Size(view.DragSurface.ActualWidth, view.DragSurface.ActualHeight));
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
