using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerPositionCallbacks(
    Action<YouTubeMiniPlayerState, FrameworkElement, bool> ClampToHost,
    Action<YouTubeMiniPlayerState, UIElement> SetPosition,
    Action<YouTubeMiniPlayerState> ForceNativePosition,
    Action<YouTubeMiniPlayerRuntime> ForceRuntimeNativePosition,
    Action<YouTubeMiniPlayerRuntime> SyncPopupOffsets,
    Action<YouTubeMiniPlayerRuntime> QueueNativePositionRefresh,
    Func<Point> GetHostOrigin,
    Func<YouTubeMiniPlayerState, Point> GetPopupScreenPosition);

internal static class YouTubeMiniPlayerPositionCallbackFactory
{
    public static YouTubeMiniPlayerPositionCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Dispatcher dispatcher,
        Func<Point> getHostOrigin,
        Func<Rect> getScreenBounds,
        Action<YouTubeMiniPlayerRuntime> refreshHover,
        YouTubeMiniPlayerOverlayPlacementCallbacks overlayPlacementCallbacks)
    {
        Point GetPopupScreenPosition(YouTubeMiniPlayerState state)
        {
            return YouTubeMiniPlayerPositionService.GetPopupScreenPosition(
                state,
                getHostOrigin());
        }

        void ForceRuntimeNativePosition(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerPopupCoordinatorService.ForceNativePosition(
                runtime,
                GetPopupScreenPosition);
        }

        void QueueNativePositionRefresh(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerNativePositionRefreshService.Queue(
                runtime,
                runtimes,
                dispatcher,
                ForceRuntimeNativePosition,
                overlayPlacementCallbacks.RefreshOverlayPopupPosition);
        }

        void SetPosition(YouTubeMiniPlayerState state, UIElement root)
        {
            YouTubeMiniPlayerPopupCoordinatorService.SetPosition(
                state,
                runtimes,
                GetPopupScreenPosition,
                QueueNativePositionRefresh,
                overlayPlacementCallbacks.UpdateToolbarPlacement,
                overlayPlacementCallbacks.UpdateTitleBandPlacement,
                overlayPlacementCallbacks.RefreshOverlayPopupPosition);
        }

        void ClampToHost(
            YouTubeMiniPlayerState state,
            FrameworkElement root,
            bool ensureVisible)
        {
            YouTubeMiniPlayerPositionService.ClampToHost(
                state,
                root,
                ensureVisible,
                getHostOrigin,
                getScreenBounds,
                SetPosition);
        }

        void ForceNativePosition(YouTubeMiniPlayerState state)
        {
            YouTubeMiniPlayerPopupCoordinatorService.ForceNativePosition(
                state,
                runtimes,
                GetPopupScreenPosition);
        }

        void SyncPopupOffsets(YouTubeMiniPlayerRuntime runtime)
        {
            YouTubeMiniPlayerPopupCoordinatorService.SyncOffsets(
                runtime,
                GetPopupScreenPosition,
                QueueNativePositionRefresh,
                refreshHover);
        }

        return new YouTubeMiniPlayerPositionCallbacks(
            ClampToHost,
            SetPosition,
            ForceNativePosition,
            ForceRuntimeNativePosition,
            SyncPopupOffsets,
            QueueNativePositionRefresh,
            getHostOrigin,
            GetPopupScreenPosition);
    }
}
