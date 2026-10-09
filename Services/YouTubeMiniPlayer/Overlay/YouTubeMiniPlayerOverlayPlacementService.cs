using System.Windows;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerOverlayPlacementService
{
    public static double GetMinimizedTitleBandWidth(double stateWidth, double minimizedWidth)
    {
        return Math.Min(
            minimizedWidth,
            Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, stateWidth));
    }

    public static double GetOverlayWidth(double hostWidth, double fallbackWidth, double edgeMargin)
    {
        return Math.Max(fallbackWidth, hostWidth - (edgeMargin * 2));
    }

    public static double GetWebHostWidth(YouTubeMiniPlayerRuntime runtime)
    {
        return runtime.WebHost.ActualWidth > 1
            ? runtime.WebHost.ActualWidth
            : YouTubeMiniPlayerLayoutService.GetVideoWidth(runtime.State.Width);
    }

    public static double GetWebHostHeight(YouTubeMiniPlayerRuntime runtime)
    {
        return runtime.WebHost.ActualHeight > 1
            ? runtime.WebHost.ActualHeight
            : YouTubeMiniPlayerLayoutService.GetVideoHeight(runtime.State.Height);
    }

    public static Point GetDefaultToolbarOffset(
        double hostWidth,
        double hostHeight,
        (double Width, double Height) panelSize,
        double bottomMargin)
    {
        return new Point(
            (hostWidth - panelSize.Width) / 2,
            hostHeight - panelSize.Height - bottomMargin);
    }

    public static Point ClampToolbarOffset(
        double horizontalOffset,
        double verticalOffset,
        double hostWidth,
        double hostHeight,
        (double Width, double Height) panelSize,
        double edgeMargin)
    {
        var minHorizontalOffset = edgeMargin;
        var maxHorizontalOffset = Math.Max(
            minHorizontalOffset,
            hostWidth - panelSize.Width - edgeMargin);
        var minVerticalOffset = edgeMargin;
        var maxVerticalOffset = Math.Max(
            minVerticalOffset,
            hostHeight - panelSize.Height - edgeMargin);

        return new Point(
            Math.Max(minHorizontalOffset, Math.Min(horizontalOffset, maxHorizontalOffset)),
            Math.Max(minVerticalOffset, Math.Min(verticalOffset, maxVerticalOffset)));
    }

    public static Point GetWebHostScreenOrigin(Point rootOrigin, double inset)
    {
        return new Point(rootOrigin.X + inset, rootOrigin.Y + inset);
    }

    public static Point GetOverlayScreenPosition(Point hostOrigin, double horizontalOffset, double verticalOffset)
    {
        return new Point(hostOrigin.X + horizontalOffset, hostOrigin.Y + verticalOffset);
    }
}
