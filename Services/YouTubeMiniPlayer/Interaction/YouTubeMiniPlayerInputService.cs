using System.Windows;
using System.Windows.Input;

namespace TabBrowser.Services;

public enum MiniPlayerFixedGestureDirection
{
    None,
    Rewind,
    FastForward,
    Close
}

public enum MiniPlayerLeftDragAction
{
    Move,
    Resize
}

public static class YouTubeMiniPlayerInputService
{
    public const double FixedGestureThreshold = 24;
    public const double FixedGestureDominanceRatio = 1.5;
    public const double TitleBandHotZoneHeight = 72;
    public const double ToolbarHotZoneMinHeight = 56;
    public const double ToolbarHotZoneMaxHeight = 96;

    public static MiniPlayerLeftDragAction GetLeftDragAction(ModifierKeys modifiers)
    {
        return modifiers.HasFlag(ModifierKeys.Control)
            ? MiniPlayerLeftDragAction.Resize
            : MiniPlayerLeftDragAction.Move;
    }

    public static bool IsTitleBandHotZone(Point point)
    {
        return point.Y >= 0 && point.Y <= TitleBandHotZoneHeight;
    }

    public static bool IsToolbarHotZone(Point point, double height)
    {
        if (!double.IsFinite(height) || height <= 0)
        {
            return false;
        }

        var hotZoneHeight = Math.Min(
            ToolbarHotZoneMaxHeight,
            Math.Max(ToolbarHotZoneMinHeight, height * 0.18));
        return point.Y >= height - hotZoneHeight && point.Y <= height;
    }

    public static MiniPlayerFixedGestureDirection GetFixedGestureDirection(
        Point startPoint,
        Point currentPoint)
    {
        var deltaX = currentPoint.X - startPoint.X;
        var deltaY = currentPoint.Y - startPoint.Y;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);
        if (absoluteX >= FixedGestureThreshold
            && absoluteX >= absoluteY * FixedGestureDominanceRatio)
        {
            return deltaX < 0
                ? MiniPlayerFixedGestureDirection.Rewind
                : MiniPlayerFixedGestureDirection.FastForward;
        }

        return deltaY >= FixedGestureThreshold
            && absoluteY >= absoluteX * FixedGestureDominanceRatio
                ? MiniPlayerFixedGestureDirection.Close
                : MiniPlayerFixedGestureDirection.None;
    }

    public static Point CalculateDragPosition(
        double startLeft,
        double startTop,
        Point dragStartScreenPoint,
        Point currentScreenPoint)
    {
        return new Point(
            startLeft + currentScreenPoint.X - dragStartScreenPoint.X,
            startTop + currentScreenPoint.Y - dragStartScreenPoint.Y);
    }

    public static Rect CalculateResizeBounds(
        bool isShorts,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        return CalculateResizeBounds(
            YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(isShorts),
            startLeft,
            startTop,
            startWidth,
            startHeight,
            totalHorizontalChange,
            totalVerticalChange,
            resizeLeft,
            resizeTop,
            resizeRight,
            resizeBottom);
    }

    public static Rect CalculateResizeBounds(
        double aspectRatio,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom)
    {
        var hasHorizontalResize = resizeLeft || resizeRight;
        var hasVerticalResize = resizeTop || resizeBottom;
        var requestedWidth = startWidth;
        var requestedHeight = startHeight;

        if (resizeLeft)
        {
            requestedWidth = startWidth - totalHorizontalChange;
        }
        else if (resizeRight)
        {
            requestedWidth = startWidth + totalHorizontalChange;
        }

        if (resizeTop)
        {
            requestedHeight = startHeight - totalVerticalChange;
        }
        else if (resizeBottom)
        {
            requestedHeight = startHeight + totalVerticalChange;
        }

        var newWidth = startWidth;
        var newHeight = startHeight;
        if (hasHorizontalResize && hasVerticalResize)
        {
            var fittedSize = YouTubeMiniPlayerLayoutService.NormalizeCornerSizeToVideo(
                requestedWidth,
                requestedHeight,
                aspectRatio);
            newWidth = fittedSize.Width;
            newHeight = fittedSize.Height;
        }
        else if (hasHorizontalResize || hasVerticalResize)
        {
            var fittedSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
                requestedWidth,
                requestedHeight,
                preferHeight: hasVerticalResize,
                aspectRatio: aspectRatio);
            newWidth = fittedSize.Width;
            newHeight = fittedSize.Height;
        }

        if (newWidth < YouTubeMiniPlayerLayoutService.MinimumWidth)
        {
            newWidth = YouTubeMiniPlayerLayoutService.MinimumWidth;
            newHeight = YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(newWidth, aspectRatio);
        }

        if (newHeight < YouTubeMiniPlayerLayoutService.MinimumHeight)
        {
            newHeight = YouTubeMiniPlayerLayoutService.MinimumHeight;
            newWidth = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(newHeight, aspectRatio);
        }

        var left = resizeLeft
            ? startLeft + startWidth - newWidth
            : startLeft;
        var top = resizeTop
            ? startTop + startHeight - newHeight
            : startTop;
        return new Rect(left, top, newWidth, newHeight);
    }

    public static Rect CalculateCtrlDragResizeBounds(
        double aspectRatio,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange)
    {
        var resizeByWidth = Math.Abs(totalHorizontalChange)
            >= Math.Abs(totalVerticalChange * aspectRatio);
        return CalculateResizeBounds(
            aspectRatio,
            startLeft,
            startTop,
            startWidth,
            startHeight,
            totalHorizontalChange,
            totalVerticalChange,
            resizeLeft: false,
            resizeTop: false,
            resizeRight: resizeByWidth,
            resizeBottom: !resizeByWidth);
    }

    public static Rect CalculateConstrainedMoveBounds(
        Rect startBounds,
        double horizontalChange,
        double verticalChange,
        Rect workArea)
    {
        var maxLeft = Math.Max(workArea.Left, workArea.Right - startBounds.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - startBounds.Height);
        return new Rect(
            Math.Clamp(startBounds.Left + horizontalChange, workArea.Left, maxLeft),
            Math.Clamp(startBounds.Top + verticalChange, workArea.Top, maxTop),
            startBounds.Width,
            startBounds.Height);
    }

    public static Rect CalculateConstrainedCornerResizeBounds(
        Rect startBounds,
        double horizontalChange,
        double verticalChange,
        double aspectRatio,
        bool resizeLeft,
        bool resizeTop,
        Rect workArea)
    {
        var requestedBounds = CalculateResizeBounds(
            aspectRatio,
            startBounds.Left,
            startBounds.Top,
            startBounds.Width,
            startBounds.Height,
            horizontalChange,
            verticalChange,
            resizeLeft,
            resizeTop,
            !resizeLeft,
            !resizeTop);

        var anchorX = resizeLeft ? startBounds.Right : startBounds.Left;
        var anchorY = resizeTop ? startBounds.Bottom : startBounds.Top;
        var maxWidth = resizeLeft ? anchorX - workArea.Left : workArea.Right - anchorX;
        var maxHeight = resizeTop ? anchorY - workArea.Top : workArea.Bottom - anchorY;
        var width = requestedBounds.Width;
        var height = requestedBounds.Height;
        if (width > maxWidth)
        {
            width = maxWidth;
            height = YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(width, aspectRatio);
        }

        if (height > maxHeight)
        {
            height = maxHeight;
            width = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(height, aspectRatio);
        }

        if (width > maxWidth)
        {
            width = maxWidth;
            height = YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(width, aspectRatio);
        }

        if (width < YouTubeMiniPlayerLayoutService.MinimumWidth
            || height < YouTubeMiniPlayerLayoutService.MinimumHeight
            || width <= 0
            || height <= 0)
        {
            return startBounds;
        }

        return new Rect(
            resizeLeft ? anchorX - width : anchorX,
            resizeTop ? anchorY - height : anchorY,
            width,
            height);
    }
}
