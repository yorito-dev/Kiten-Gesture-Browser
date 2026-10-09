using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerOverlayPlacementCallbacks(
    Action<YouTubeMiniPlayerRuntime> UpdateTitleBandPlacement,
    Action<YouTubeMiniPlayerRuntime> UpdateToolbarPlacement,
    Func<YouTubeMiniPlayerRuntime, Task> UpdateToolbarPlacementAsync,
    Action<YouTubeMiniPlayerRuntime> RefreshOverlayPopupPosition);

internal static class YouTubeMiniPlayerOverlayPlacementCallbackFactory
{
    public static YouTubeMiniPlayerOverlayPlacementCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        double minimizedWidth,
        double fallbackWidth,
        double edgeMargin,
        double bottomMargin,
        double fullScreenEdgeMargin,
        double fullScreenBottomMargin,
        Func<FrameworkElement, (double Width, double Height)> getPanelSize,
        YouTubeMiniPlayerToolbarPlacementResolver resolveToolbarPlacementAsync)
    {
        void UpdateTitleBandPlacement(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerOverlayPopupPositionService.UpdateTitleBandPlacement(
                runtime,
                minimizedWidth,
                fallbackWidth,
                edgeMargin);
        }

        void SetToolbarPlacement(
            YouTubeMiniPlayerRuntime runtime,
            double horizontalOffset,
            double verticalOffset)
        {
            YouTubeMiniPlayerOverlayPopupPositionService.SetToolbarPlacement(
                runtime,
                horizontalOffset,
                verticalOffset,
                runtime.IsFullScreen ? fullScreenEdgeMargin : edgeMargin,
                getPanelSize);
        }

        void UpdateToolbarPlacement(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerOverlayPopupPositionService.UpdateToolbarPlacement(
                runtime,
                fallbackWidth,
                runtime.IsFullScreen ? fullScreenEdgeMargin : edgeMargin,
                runtime.IsFullScreen ? fullScreenBottomMargin : bottomMargin,
                getPanelSize);
        }

        Task UpdateToolbarPlacementAsync(YouTubeMiniPlayerRuntime runtime)
        {
            return YouTubeMiniPlayerToolbarPlacementRefreshService.UpdateAsync(
                runtime,
                runtimes,
                resolveToolbarPlacementAsync,
                SetToolbarPlacement);
        }

        void RefreshOverlayPopupPosition(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerOverlayPopupPositionService.RefreshOverlayPopupPosition(
                runtime,
                UpdateTitleBandPlacement,
                UpdateToolbarPlacement);
        }

        return new YouTubeMiniPlayerOverlayPlacementCallbacks(
            UpdateTitleBandPlacement,
            UpdateToolbarPlacement,
            UpdateToolbarPlacementAsync,
            RefreshOverlayPopupPosition);
    }
}
