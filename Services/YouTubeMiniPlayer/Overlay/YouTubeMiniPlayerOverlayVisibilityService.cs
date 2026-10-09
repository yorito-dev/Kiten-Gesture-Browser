using System.Windows;
using System.Windows.Threading;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerOverlayVisibilityService
{
    public static void SetToolbarVisibility(
        YouTubeMiniPlayerState state,
        bool isVisible,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> queueToolbarPlacementRefresh,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        SetToolbarVisibility(
            runtime,
            isVisible,
            hideDelay,
            updateToolbarPlacement,
            queueToolbarPlacementRefresh,
            refreshOverlayPosition,
            updateTitleBandPlacement);
    }

    public static void SetToolbarVisibility(
        YouTubeMiniPlayerRuntime runtime,
        bool isVisible,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> queueToolbarPlacementRefresh,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (runtime.State.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            runtime.ToolbarHideTimer?.Stop();
            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (runtime.IsResizing)
        {
            runtime.ToolbarHideTimer?.Stop();
            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (runtime.State.IsMinimized)
        {
            runtime.ToolbarHideTimer?.Stop();
            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (!isVisible)
        {
            QueueToolbarHide(
                runtime,
                hideDelay,
                updateToolbarPlacement,
                refreshOverlayPosition,
                updateTitleBandPlacement);
            return;
        }

        runtime.ToolbarHideTimer?.Stop();
        runtime.Toolbar.Visibility = Visibility.Visible;
        updateToolbarPlacement(runtime);
        runtime.ToolbarPopup.IsOpen = true;
        if (!runtime.IsFullScreen)
        {
            queueToolbarPlacementRefresh(runtime);
        }
    }

    public static void KeepToolbarVisible(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (runtime.State.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            runtime.ToolbarHideTimer?.Stop();
            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (runtime.State.IsMinimized)
        {
            runtime.ToolbarHideTimer?.Stop();
            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            runtime.TitleBandHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            return;
        }

        runtime.ToolbarHideTimer?.Stop();
        runtime.Toolbar.Visibility = Visibility.Visible;
        updateToolbarPlacement(runtime);
        runtime.ToolbarPopup.IsOpen = true;
        refreshOverlayPosition(runtime);
    }

    public static void HideToolbarImmediately(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.ToolbarHideTimer?.Stop();
        runtime.ToolbarPopup.IsOpen = false;
        runtime.Toolbar.Visibility = Visibility.Collapsed;
    }

    public static void SetTitleBandVisibility(
        YouTubeMiniPlayerState state,
        bool isVisible,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        SetTitleBandVisibility(
            runtime,
            isVisible,
            hideDelay,
            updateTitleBandPlacement);
    }

    public static void SetTitleBandVisibility(
        YouTubeMiniPlayerRuntime runtime,
        bool isVisible,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (runtime.State.IsMinimized)
        {
            runtime.TitleBandHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            return;
        }

        if (runtime.IsResizing)
        {
            runtime.TitleBandHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            return;
        }

        if (!isVisible)
        {
            QueueTitleBandHide(runtime, hideDelay);
            return;
        }

        runtime.TitleBandHideTimer?.Stop();
        runtime.TitleBand.Visibility = Visibility.Visible;
        updateTitleBandPlacement(runtime);
        runtime.TitleBandPopup.IsOpen = true;
    }

    public static bool IsDragging(YouTubeMiniPlayerRuntime runtime)
    {
        return runtime.IsWebDragging || runtime.IsSurfaceDragging || runtime.IsResizing;
    }

    public static void RefreshFromCursor(
        YouTubeMiniPlayerRuntime runtime,
        Point cursorPoint,
        Point rootOrigin,
        Func<Point, double, bool> isToolbarHotZone,
        Func<Point, bool> isTitleBandHotZone,
        Action<YouTubeMiniPlayerState, bool> setToolbarVisibility,
        Action<YouTubeMiniPlayerState, bool> setTitleBandVisibility)
    {
        var point = new Point(cursorPoint.X - rootOrigin.X, cursorPoint.Y - rootOrigin.Y);
        var rootHeight = runtime.Root.ActualHeight > 0
            ? runtime.Root.ActualHeight
            : runtime.State.Height;
        var rootWidth = runtime.Root.ActualWidth > 0
            ? runtime.Root.ActualWidth
            : runtime.State.Width;
        var isInside = point.X >= 0
            && point.X <= rootWidth
            && point.Y >= 0
            && point.Y <= rootHeight;

        setToolbarVisibility(
            runtime.State,
            isInside && isToolbarHotZone(point, rootHeight));
        setTitleBandVisibility(
            runtime.State,
            isInside && isTitleBandHotZone(point));
    }

    private static void QueueToolbarHide(
        YouTubeMiniPlayerRuntime runtime,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (IsDragging(runtime) || runtime.IsToolbarMenuOpen)
        {
            runtime.ToolbarHideTimer?.Stop();
            return;
        }

        runtime.ToolbarHideTimer ??= CreateToolbarHideTimer(
            runtime,
            hideDelay,
            updateToolbarPlacement,
            refreshOverlayPosition,
            updateTitleBandPlacement);
        runtime.ToolbarHideTimer.Stop();
        runtime.ToolbarHideTimer.Start();
    }

    private static DispatcherTimer CreateToolbarHideTimer(
        YouTubeMiniPlayerRuntime runtime,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        var timer = new DispatcherTimer
        {
            Interval = hideDelay
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsDragging(runtime) || runtime.IsToolbarMenuOpen)
            {
                KeepToolbarVisible(
                    runtime,
                    updateToolbarPlacement,
                    refreshOverlayPosition,
                    updateTitleBandPlacement);
                return;
            }

            runtime.ToolbarPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
        };
        return timer;
    }

    private static void QueueTitleBandHide(YouTubeMiniPlayerRuntime runtime, TimeSpan hideDelay)
    {
        if (runtime.State.IsMinimized)
        {
            runtime.TitleBandHideTimer?.Stop();
            runtime.TitleBandPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            return;
        }

        runtime.TitleBandHideTimer ??= CreateTitleBandHideTimer(runtime, hideDelay);
        runtime.TitleBandHideTimer.Stop();
        runtime.TitleBandHideTimer.Start();
    }

    private static DispatcherTimer CreateTitleBandHideTimer(YouTubeMiniPlayerRuntime runtime, TimeSpan hideDelay)
    {
        var timer = new DispatcherTimer
        {
            Interval = hideDelay
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (runtime.State.IsMinimized)
            {
                runtime.TitleBandPopup.IsOpen = false;
                runtime.TitleBand.Visibility = Visibility.Collapsed;
                return;
            }

            runtime.TitleBandPopup.IsOpen = false;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
        };
        return timer;
    }
}
