using System;
using System.Collections.Generic;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerOverlayVisibilityCallbacks(
    Action<YouTubeMiniPlayerState, bool> SetToolbarVisibility,
    Action<YouTubeMiniPlayerRuntime> KeepToolbarVisible,
    Action<YouTubeMiniPlayerState, bool> SetTitleBandVisibility);

internal static class YouTubeMiniPlayerOverlayVisibilityCallbackFactory
{
    public static YouTubeMiniPlayerOverlayVisibilityCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        TimeSpan hideDelay,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> queueToolbarPlacementRefresh,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        return new YouTubeMiniPlayerOverlayVisibilityCallbacks(
            (state, isVisible) => YouTubeMiniPlayerOverlayVisibilityService.SetToolbarVisibility(
                state,
                isVisible,
                runtimes,
                hideDelay,
                updateToolbarPlacement,
                queueToolbarPlacementRefresh,
                refreshOverlayPosition,
                updateTitleBandPlacement),
            runtime => YouTubeMiniPlayerOverlayVisibilityService.KeepToolbarVisible(
                runtime,
                updateToolbarPlacement,
                refreshOverlayPosition,
                updateTitleBandPlacement),
            (state, isVisible) => YouTubeMiniPlayerOverlayVisibilityService.SetTitleBandVisibility(
                state,
                isVisible,
                runtimes,
                hideDelay,
                updateTitleBandPlacement));
    }
}
