using System.Windows;
using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerPositionService
{
    private const double HostOriginChangeThreshold = 0.5;
    public const double MagneticSnapThreshold = 12;

    public static Rect GetScreenBounds(YouTubeMiniPlayerState state, Point hostOrigin)
    {
        return new Rect(
            hostOrigin.X + state.Left,
            hostOrigin.Y + state.Top,
            state.Width,
            state.Height);
    }

    public static bool TryGetHostScreenOrigin(FrameworkElement host, out Point origin)
    {
        origin = default;
        if (!host.IsVisible
            || host.ActualWidth <= 0
            || host.ActualHeight <= 0
            || PresentationSource.FromVisual(host) is null)
        {
            return false;
        }

        origin = GetVisualScreenOrigin(host);
        return true;
    }

    public static Point GetVisualScreenOrigin(Visual visual)
    {
        var screenPoint = visual.PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(visual);
        return source?.CompositionTarget?.TransformFromDevice.Transform(screenPoint)
            ?? screenPoint;
    }

    public static Point GetPopupScreenPosition(YouTubeMiniPlayerState state, Point hostOrigin)
    {
        return new Point(hostOrigin.X + state.Left, hostOrigin.Y + state.Top);
    }

    public static Point GetWebHostScreenOrigin(YouTubeMiniPlayerState state, Point hostOrigin)
    {
        var rootOrigin = GetPopupScreenPosition(state, hostOrigin);
        var inset = YouTubeMiniPlayerLayoutService.GetChromeSingleInset();
        return YouTubeMiniPlayerOverlayPlacementService.GetWebHostScreenOrigin(rootOrigin, inset);
    }

    public static Point CalculateMagneticallySnappedPosition(
        Rect movingBounds,
        Rect screenBounds,
        IEnumerable<Rect> otherBounds,
        double threshold = MagneticSnapThreshold)
    {
        if (movingBounds.IsEmpty || screenBounds.IsEmpty || threshold < 0)
        {
            return movingBounds.TopLeft;
        }

        var horizontalCandidates = new List<double>
        {
            screenBounds.Left,
            screenBounds.Right - movingBounds.Width
        };
        var verticalCandidates = new List<double>
        {
            screenBounds.Top,
            screenBounds.Bottom - movingBounds.Height
        };

        foreach (var bounds in otherBounds)
        {
            if (bounds.IsEmpty)
            {
                continue;
            }

            if (RangesOverlap(movingBounds.Top, movingBounds.Bottom, bounds.Top, bounds.Bottom))
            {
                horizontalCandidates.Add(bounds.Left);
                horizontalCandidates.Add(bounds.Right - movingBounds.Width);
                horizontalCandidates.Add(bounds.Right);
                horizontalCandidates.Add(bounds.Left - movingBounds.Width);
            }

            if (RangesOverlap(movingBounds.Left, movingBounds.Right, bounds.Left, bounds.Right))
            {
                verticalCandidates.Add(bounds.Top);
                verticalCandidates.Add(bounds.Bottom - movingBounds.Height);
                verticalCandidates.Add(bounds.Bottom);
                verticalCandidates.Add(bounds.Top - movingBounds.Height);
            }
        }

        return new Point(
            SnapAxis(movingBounds.Left, horizontalCandidates, threshold),
            SnapAxis(movingBounds.Top, verticalCandidates, threshold));
    }

    public static void EnsureVisibleOnVirtualScreen(
        YouTubeMiniPlayerState state,
        Point hostOrigin,
        Rect screenBounds)
    {
        var bounds = GetScreenBounds(state, hostOrigin);
        if (YouTubeMiniPlayerLayoutService.HasEnoughVisibleArea(bounds, screenBounds))
        {
            return;
        }

        var clampedScreenLeft = YouTubeMiniPlayerLayoutService.ClampToMinimumVisible(
            bounds.Left,
            state.Width,
            screenBounds.Left,
            screenBounds.Width);
        var clampedScreenTop = YouTubeMiniPlayerLayoutService.ClampToMinimumVisible(
            bounds.Top,
            state.Height,
            screenBounds.Top,
            screenBounds.Height);
        state.Left = clampedScreenLeft - hostOrigin.X;
        state.Top = clampedScreenTop - hostOrigin.Y;
    }

    public static void ClampToHost(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        bool ensureVisible,
        Func<Point> getHostOrigin,
        Func<Rect> getScreenBounds,
        Action<YouTubeMiniPlayerState, UIElement> setPosition)
    {
        var fittedSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            state.Width,
            state.Height,
            preferHeight: false,
            aspectRatio: YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(state));
        state.Width = fittedSize.Width;
        state.Height = fittedSize.Height;
        root.Width = state.Width;
        root.Height = state.Height;
        if (ensureVisible)
        {
            EnsureVisibleOnVirtualScreen(state, getHostOrigin(), getScreenBounds());
        }

        setPosition(state, root);
    }

    public static bool HasSignificantHostOriginChange(Point previousOrigin, Point currentOrigin)
    {
        return Math.Abs(currentOrigin.X - previousOrigin.X) >= HostOriginChangeThreshold
            || Math.Abs(currentOrigin.Y - previousOrigin.Y) >= HostOriginChangeThreshold;
    }

    public static void ApplyHostOriginDelta(
        YouTubeMiniPlayerState state,
        Point previousOrigin,
        Point currentOrigin)
    {
        state.Left += previousOrigin.X - currentOrigin.X;
        state.Top += previousOrigin.Y - currentOrigin.Y;
    }

    private static bool RangesOverlap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd)
    {
        return firstStart <= secondEnd && secondStart <= firstEnd;
    }

    private static double SnapAxis(
        double current,
        IEnumerable<double> candidates,
        double threshold)
    {
        var snapped = current;
        var closestDistance = threshold;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate - current);
            if (distance <= closestDistance)
            {
                snapped = candidate;
                closestDistance = distance;
            }
        }

        return snapped;
    }
}
