using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerResizeThumbCallbacks(
    Action<Guid, bool> BringToFront,
    Func<Point> GetCursorScreenPoint,
    Action<YouTubeMiniPlayerState, FrameworkElement, bool> Clamp,
    Action<YouTubeMiniPlayerRuntime> SyncPopupOffsets,
    Action QueueSave);

internal static class YouTubeMiniPlayerResizeThumbService
{
    public static void AddThumbs(
        Grid layout,
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerResizeThumbCallbacks callbacks)
    {
        var thickness = Math.Max(YouTubeMiniPlayerInputService.VideoResizeEdgeInset, YouTubeMiniPlayerLayoutService.ResizeBorderThickness);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Left, VerticalAlignment.Stretch, thickness, double.NaN, Cursors.SizeWE,
            resizeLeft: true, resizeTop: false, resizeRight: false, resizeBottom: false);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Right, VerticalAlignment.Stretch, thickness, double.NaN, Cursors.SizeWE,
            resizeLeft: false, resizeTop: false, resizeRight: true, resizeBottom: false);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, thickness, Cursors.SizeNS,
            resizeLeft: false, resizeTop: true, resizeRight: false, resizeBottom: false);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, thickness, Cursors.SizeNS,
            resizeLeft: false, resizeTop: false, resizeRight: false, resizeBottom: true);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Left, VerticalAlignment.Top, 16, 16, Cursors.SizeNWSE,
            resizeLeft: true, resizeTop: true, resizeRight: false, resizeBottom: false);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Right, VerticalAlignment.Top, 16, 16, Cursors.SizeNESW,
            resizeLeft: false, resizeTop: true, resizeRight: true, resizeBottom: false);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Left, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNESW,
            resizeLeft: true, resizeTop: false, resizeRight: false, resizeBottom: true);
        AddThumb(layout, state, root, runtimes, callbacks, HorizontalAlignment.Right, VerticalAlignment.Bottom, 16, 16, Cursors.SizeNWSE,
            resizeLeft: false, resizeTop: false, resizeRight: true, resizeBottom: true);
    }

    private static void AddThumb(
        Grid layout,
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerResizeThumbCallbacks callbacks,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        double width,
        double height,
        Cursor cursor,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        var resizeThumb = YouTubeMiniPlayerChromeFactory.CreateResizeThumb(
            horizontalAlignment,
            verticalAlignment,
            width,
            height,
            cursor);
        var thickness = Math.Max(YouTubeMiniPlayerInputService.VideoResizeEdgeInset, YouTubeMiniPlayerLayoutService.ResizeBorderThickness);
        // 枠・余白0でも端の入力判定は1 DIP残す。透明な判定だけで映像の寸法は変えない。
        resizeThumb.Margin = new Thickness(0);
        if (double.IsFinite(width) && double.IsFinite(height))
        {
            // 角は両辺に沿う16 DIPのつかみしろを残し、動画側の入力領域を切り抜く。
            var horizontal = new Rect(0, verticalAlignment == VerticalAlignment.Top ? 0 : height - thickness, width, thickness);
            var vertical = new Rect(horizontalAlignment == HorizontalAlignment.Left ? 0 : width - thickness, 0, thickness, height);
            var clip = new CombinedGeometry(GeometryCombineMode.Union, new RectangleGeometry(horizontal), new RectangleGeometry(vertical));
            clip.Freeze();
            resizeThumb.Clip = clip;
        }
        var startLeft = 0.0;
        var startTop = 0.0;
        var startWidth = 0.0;
        var startHeight = 0.0;
        var resizeStartScreenPoint = new Point();
        var isResizeActive = false;
        var resizeFrameScheduler = new YouTubeMiniPlayerResizeFrameScheduler(
            queueRender: action =>
            {
                EventHandler? onRendering = null;
                onRendering = (_, _) =>
                {
                    CompositionTarget.Rendering -= onRendering;
                    action();
                };
                CompositionTarget.Rendering += onRendering;
            },
            applyBounds: bounds =>
            {
                if (!isResizeActive
                    || !runtimes.TryGetValue(state.Id, out var runtime)
                    || runtime.IsFullScreen)
                {
                    return;
                }

                YouTubeMiniPlayerResizeService.ApplyBounds(
                    state,
                    root,
                    bounds,
                    callbacks.Clamp);
            });
        resizeThumb.DragStarted += (_, _) =>
        {
            if (!runtimes.TryGetValue(state.Id, out var runtime)
                || runtime.IsFullScreen)
            {
                return;
            }

            resizeFrameScheduler.Cancel();
            isResizeActive = true;
            callbacks.BringToFront(state.Id, false);
            runtime.IsResizing = true;
            runtime.TitleBandHideTimer?.Stop();
            runtime.ToolbarHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.ToolbarPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            startLeft = state.Left;
            startTop = state.Top;
            startWidth = Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, state.Width);
            startHeight = Math.Max(YouTubeMiniPlayerLayoutService.MinimumHeight, state.Height);
            resizeStartScreenPoint = callbacks.GetCursorScreenPoint();
        };
        resizeThumb.DragDelta += (_, _) =>
        {
            if (!isResizeActive)
            {
                return;
            }

            if (!runtimes.TryGetValue(state.Id, out var runtime)
                || runtime.IsFullScreen)
            {
                resizeFrameScheduler.Cancel();
                isResizeActive = false;
                if (runtime is not null)
                {
                    runtime.IsResizing = false;
                }

                return;
            }

            var cursorPoint = callbacks.GetCursorScreenPoint();
            var bounds = YouTubeMiniPlayerResizeService.CalculateResizeBounds(
                state,
                startLeft,
                startTop,
                startWidth,
                startHeight,
                cursorPoint.X - resizeStartScreenPoint.X,
                cursorPoint.Y - resizeStartScreenPoint.Y,
                resizeLeft,
                resizeTop,
                resizeRight,
                resizeBottom,
                YouTubeMiniPlayerLayoutService.GetMinimumSize(runtime));
            resizeFrameScheduler.Queue(bounds);
        };
        resizeThumb.DragCompleted += (_, _) =>
        {
            if (!isResizeActive)
            {
                return;
            }

            resizeFrameScheduler.Flush();
            isResizeActive = false;
            if (runtimes.TryGetValue(state.Id, out var runtime))
            {
                runtime.IsResizing = false;
                if (runtime.IsFullScreen)
                {
                    return;
                }

                callbacks.Clamp(state, root, true);
                callbacks.SyncPopupOffsets(runtime);
            }

            callbacks.QueueSave();
        };
        Panel.SetZIndex(resizeThumb, 100);
        layout.Children.Add(resizeThumb);
    }
}
