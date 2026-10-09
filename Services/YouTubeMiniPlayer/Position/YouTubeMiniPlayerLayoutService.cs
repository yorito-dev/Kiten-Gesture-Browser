using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerLayoutService
{
    public const double VideoAspectRatio = 16.0 / 9.0;
    public const double ShortsVideoAspectRatio = 9.0 / 16.0;
    public const double DefaultWidth = 480;
    public const double DefaultHeight = 277.875;
    public const double ShortsDefaultWidth = 330;
    public const double MinimumWidth = 260;
    public const double MinimumHeight = 154.125;
    public const double Margin = 14;
    public const double FrameBorderThickness = 1;
    public const double ResizeBorderThickness = 8;
    public const double MinimumVisibleLength = 48;
    public const double InitialTopOffset = 48;

    public static Rect GetNextBounds(
        bool isShorts,
        int existingPlayerCount,
        double hostActualWidth,
        double hostActualHeight)
    {
        var defaultWidth = isShorts ? ShortsDefaultWidth : DefaultWidth;
        var defaultHeight = GetOuterHeightForVideoWidth(defaultWidth, GetVideoAspectRatio(isShorts));
        var hostWidth = Math.Max(defaultWidth + (Margin * 2), hostActualWidth);
        var hostHeight = Math.Max(defaultHeight + (Margin * 2), hostActualHeight);
        var maxWidth = Math.Max(MinimumWidth, hostWidth - (Margin * 2));
        var maxHeight = Math.Max(MinimumHeight, hostHeight - (Margin * 2));
        var aspectRatio = GetVideoAspectRatio(isShorts);
        var width = Math.Min(defaultWidth, maxWidth);
        var height = GetOuterHeightForVideoWidth(width, aspectRatio);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = Math.Min(maxWidth, GetOuterWidthForVideoHeight(height, aspectRatio));
        }

        var offset = existingPlayerCount * 28;
        var left = Math.Max(Margin, hostWidth - width - Margin - offset);
        var top = Math.Max(Margin, Margin + InitialTopOffset + offset);
        return new Rect(left, top, width, height);
    }

    public static Rect GetNextAvailableBounds(
        bool isShorts,
        IReadOnlyCollection<Rect> existingBounds,
        double hostActualWidth,
        double hostActualHeight)
    {
        var initialBounds = GetNextBounds(
            isShorts,
            existingPlayerCount: 0,
            hostActualWidth,
            hostActualHeight);
        if (existingBounds.Count == 0)
        {
            return initialBounds;
        }

        var hostWidth = Math.Max(initialBounds.Width + (Margin * 2), hostActualWidth);
        var hostHeight = Math.Max(initialBounds.Height + (Margin * 2), hostActualHeight);
        var maximumLeft = Math.Max(Margin, hostWidth - initialBounds.Width - Margin);
        var maximumTop = Math.Max(
            Margin + InitialTopOffset,
            hostHeight - initialBounds.Height - Margin);
        const double searchStep = 28;
        for (var top = Margin + InitialTopOffset; top <= maximumTop + 0.1; top += searchStep)
        {
            for (var left = maximumLeft; left >= Margin - 0.1; left -= searchStep)
            {
                var candidate = new Rect(left, top, initialBounds.Width, initialBounds.Height);
                if (!OverlapsAny(candidate, existingBounds))
                {
                    return candidate;
                }
            }
        }

        return GetNextBounds(
            isShorts,
            existingBounds.Count,
            hostActualWidth,
            hostActualHeight);
    }

    private static bool OverlapsAny(Rect candidate, IReadOnlyCollection<Rect> existingBounds)
    {
        var paddedCandidate = candidate;
        paddedCandidate.Inflate(Margin / 2, Margin / 2);
        foreach (var existing in existingBounds)
        {
            if (!existing.IsEmpty
                && existing.Width > 0
                && existing.Height > 0
                && paddedCandidate.IntersectsWith(existing))
            {
                return true;
            }
        }

        return false;
    }

    public static double GetChromeInset()
    {
        return GetChromeSingleInset() * 2;
    }

    public static double GetChromeSingleInset()
    {
        return ResizeBorderThickness + FrameBorderThickness;
    }

    public static double GetVideoWidth(double outerWidth)
    {
        return Math.Max(1, outerWidth - GetChromeInset());
    }

    public static double GetVideoHeight(double outerHeight)
    {
        return Math.Max(1, outerHeight - GetChromeInset());
    }

    public static double GetVideoAspectRatio(bool isShorts)
    {
        return isShorts ? ShortsVideoAspectRatio : VideoAspectRatio;
    }

    public static double GetVideoAspectRatio(YouTubeMiniPlayerState state)
    {
        if (state.VideoAspectRatio is { } ratio
            && double.IsFinite(ratio)
            && ratio > 0)
        {
            return ratio;
        }

        return GetVideoAspectRatio(state.IsShorts);
    }

    public static bool IsVerticalVideoDimensions(double width, double height)
    {
        return double.IsFinite(width)
            && double.IsFinite(height)
            && width > 0
            && height > width;
    }

    public static double GetOuterHeightForVideoWidth(double outerWidth, double aspectRatio)
    {
        return GetVideoWidth(outerWidth) / aspectRatio + GetChromeInset();
    }

    public static double GetOuterWidthForVideoHeight(double outerHeight, double aspectRatio)
    {
        return GetVideoHeight(outerHeight) * aspectRatio + GetChromeInset();
    }

    public static Size NormalizeSizeToVideo(
        bool isShorts,
        double width,
        double height,
        bool preferHeight)
    {
        return NormalizeSizeToVideo(width, height, preferHeight, GetVideoAspectRatio(isShorts));
    }

    public static Size NormalizeSizeToVideo(
        double width,
        double height,
        bool preferHeight,
        double aspectRatio)
    {
        var normalizedWidth = Math.Max(MinimumWidth, width);
        var normalizedHeight = Math.Max(MinimumHeight, height);

        if (preferHeight)
        {
            normalizedWidth = GetOuterWidthForVideoHeight(normalizedHeight, aspectRatio);
        }
        else
        {
            normalizedHeight = GetOuterHeightForVideoWidth(normalizedWidth, aspectRatio);
        }

        if (normalizedWidth < MinimumWidth)
        {
            normalizedWidth = MinimumWidth;
            normalizedHeight = GetOuterHeightForVideoWidth(normalizedWidth, aspectRatio);
        }

        if (normalizedHeight < MinimumHeight)
        {
            normalizedHeight = MinimumHeight;
            normalizedWidth = GetOuterWidthForVideoHeight(normalizedHeight, aspectRatio);
        }

        return new Size(normalizedWidth, normalizedHeight);
    }

    public static Size NormalizeCornerSizeToVideo(
        bool isShorts,
        double width,
        double height)
    {
        return NormalizeCornerSizeToVideo(width, height, GetVideoAspectRatio(isShorts));
    }

    public static Size NormalizeCornerSizeToVideo(
        double width,
        double height,
        double aspectRatio)
    {
        var inset = GetChromeInset();
        var requestedVideoWidth = Math.Max(GetVideoWidth(MinimumWidth), width - inset);
        var requestedVideoHeight = Math.Max(GetVideoHeight(MinimumHeight), height - inset);
        var projectedVideoHeight = ((aspectRatio * requestedVideoWidth) + requestedVideoHeight) / ((aspectRatio * aspectRatio) + 1);
        var projectedVideoWidth = aspectRatio * projectedVideoHeight;

        return NormalizeSizeToVideo(
            projectedVideoWidth + inset,
            projectedVideoHeight + inset,
            preferHeight: false,
            aspectRatio);
    }

    public static bool HasEnoughVisibleArea(Rect bounds, Rect screenBounds)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var visibleBounds = Rect.Intersect(bounds, screenBounds);
        if (visibleBounds.IsEmpty)
        {
            return false;
        }

        return visibleBounds.Width >= Math.Min(bounds.Width, MinimumVisibleLength)
            && visibleBounds.Height >= Math.Min(bounds.Height, MinimumVisibleLength);
    }

    public static double ClampToMinimumVisible(
        double value,
        double length,
        double screenStart,
        double screenLength)
    {
        if (!double.IsFinite(value)
            || !double.IsFinite(length)
            || !double.IsFinite(screenStart)
            || !double.IsFinite(screenLength)
            || length <= 0
            || screenLength <= 0)
        {
            return screenStart;
        }

        var requiredVisibleLength = Math.Min(length, Math.Min(MinimumVisibleLength, screenLength));
        var min = screenStart + requiredVisibleLength - length;
        var max = screenStart + screenLength - requiredVisibleLength;
        return min <= max ? Math.Clamp(value, min, max) : screenStart;
    }
}
