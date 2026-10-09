using System;
using System.Collections.Generic;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerHostLayoutService
{
    public static Point UpdateStablePositionsAfterHostOriginChange(
        IEnumerable<YouTubeMiniPlayerRuntime> runtimes,
        Point? previousOrigin,
        Point currentOrigin,
        Action<YouTubeMiniPlayerState, UIElement> setPosition,
        Action<YouTubeMiniPlayerRuntime> syncPopupOffsets)
    {
        if (previousOrigin is not { } origin)
        {
            return currentOrigin;
        }

        if (!YouTubeMiniPlayerPositionService.HasSignificantHostOriginChange(origin, currentOrigin))
        {
            return currentOrigin;
        }

        foreach (var runtime in runtimes)
        {
            if (runtime.IsFullScreen
                || runtime.IsWebDragging
                || runtime.IsSurfaceDragging
                || runtime.IsResizing)
            {
                continue;
            }

            YouTubeMiniPlayerPositionService.ApplyHostOriginDelta(
                runtime.State,
                origin,
                currentOrigin);
            setPosition(runtime.State, runtime.Root);
            syncPopupOffsets(runtime);
        }

        return currentOrigin;
    }
}
