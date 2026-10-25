using System.Windows;
using System.Windows.Media;
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
    public const double MinimumYouTubeViewportCssPixels = 200;

    public static Size GetMinimumSize(
        YouTubeMiniPlayerState state,
        double zoomFactor = 1,
        double dpiScaleX = 1,
        double dpiScaleY = 1)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube)
            return new Size(MinimumWidth, MinimumHeight);

        zoomFactor = double.IsFinite(zoomFactor) && zoomFactor > 0 ? zoomFactor : 1;
        dpiScaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        dpiScaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        // WPFのDIPとCSS pxは別単位。モニター倍率は相殺し、ページズームだけが残る。
        // HWNDと左右/上下の枠の端数丸めに備え、各軸に2物理pxの余裕を含める。
        var inset = GetChromeInset();
        var width = Math.Max(MinimumWidth,
            (Math.Ceiling((MinimumYouTubeViewportCssPixels * zoomFactor + inset) * dpiScaleX) + 2) / dpiScaleX);
        var height = Math.Max(MinimumHeight,
            (Math.Ceiling((MinimumYouTubeViewportCssPixels * zoomFactor + inset) * dpiScaleY) + 2) / dpiScaleY);
        var ratio = GetVideoAspectRatio(state);
        var videoHeight = Math.Max(GetVideoHeight(height), GetVideoWidth(width) / ratio);
        return new Size(videoHeight * ratio + inset, videoHeight + inset);
    }

    internal static Size GetMinimumSize(YouTubeMiniPlayerRuntime runtime)
    {
        var dpi = VisualTreeHelper.GetDpi(runtime.WebView ?? (Visual)runtime.Root);
        return GetMinimumSize(runtime.State, runtime.WebView?.ZoomFactor ?? 1, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    internal static void ApplyMinimumSize(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsDisposed || runtime.IsFullScreen || runtime.IsApplyingMinimumSize
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube) return;
        runtime.IsApplyingMinimumSize = true;
        try
        {
            var minimum = GetMinimumSize(runtime);
            // 下限を満たす既存サイズは維持し、ドラッグ開始等で無関係な寸法変更を起こさない。
            var size = runtime.State.Width >= minimum.Width && runtime.State.Height >= minimum.Height
                ? new Size(runtime.State.Width, runtime.State.Height)
                : NormalizeSizeToVideo(runtime.State.Width, runtime.State.Height, false,
                    GetVideoAspectRatio(runtime.State), minimum);
            runtime.State.Width = size.Width;
            runtime.State.Height = size.Height;
            runtime.Root.MinWidth = runtime.TaskbarWindow.MinWidth = minimum.Width;
            runtime.Root.MinHeight = runtime.TaskbarWindow.MinHeight = minimum.Height;
            runtime.Root.Width = runtime.TaskbarWindow.Width = size.Width;
            runtime.Root.Height = runtime.TaskbarWindow.Height = size.Height;
        }
        finally { runtime.IsApplyingMinimumSize = false; }
    }

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
        double aspectRatio,
        Size? minimumSize = null)
    {
        var minimum = minimumSize ?? new Size(MinimumWidth, MinimumHeight);
        var normalizedWidth = Math.Max(minimum.Width, width);
        var normalizedHeight = Math.Max(minimum.Height, height);

        if (preferHeight)
        {
            normalizedWidth = GetOuterWidthForVideoHeight(normalizedHeight, aspectRatio);
        }
        else
        {
            normalizedHeight = GetOuterHeightForVideoWidth(normalizedWidth, aspectRatio);
        }

        if (normalizedWidth < minimum.Width)
        {
            normalizedWidth = minimum.Width;
            normalizedHeight = GetOuterHeightForVideoWidth(normalizedWidth, aspectRatio);
        }

        if (normalizedHeight < minimum.Height)
        {
            normalizedHeight = minimum.Height;
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
        double aspectRatio,
        Size? minimumSize = null)
    {
        var minimum = minimumSize ?? new Size(MinimumWidth, MinimumHeight);
        var inset = GetChromeInset();
        var requestedVideoWidth = Math.Max(GetVideoWidth(minimum.Width), width - inset);
        var requestedVideoHeight = Math.Max(GetVideoHeight(minimum.Height), height - inset);
        var projectedVideoHeight = ((aspectRatio * requestedVideoWidth) + requestedVideoHeight) / ((aspectRatio * aspectRatio) + 1);
        var projectedVideoWidth = aspectRatio * projectedVideoHeight;

        return NormalizeSizeToVideo(
            projectedVideoWidth + inset,
            projectedVideoHeight + inset,
            preferHeight: false,
            aspectRatio, minimum);
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
