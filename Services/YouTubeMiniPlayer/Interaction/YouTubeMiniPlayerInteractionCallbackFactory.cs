using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerInteractionCallbackFactory
{
    public static YouTubeMiniPlayerResizeThumbCallbacks CreateResizeThumbCallbacks(
        Action<Guid, bool> bringToFront,
        Func<Point> getCursorScreenPoint,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp,
        Action<YouTubeMiniPlayerRuntime> syncPopupOffsets,
        Action queueSave)
    {
        return new YouTubeMiniPlayerResizeThumbCallbacks(
            bringToFront,
            getCursorScreenPoint,
            clamp,
            syncPopupOffsets,
            queueSave);
    }

    public static YouTubeMiniPlayerDragWiringCallbacks CreateDragWiringCallbacks(
        Action<Guid, bool> bringToFront,
        Func<Point> getCursorScreenPoint,
        Action<YouTubeMiniPlayerRuntime> keepToolbarVisible,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp,
        Action<YouTubeMiniPlayerRuntime> syncPopupOffsets,
        Action<YouTubeMiniPlayerRuntime> reapplyContentTransform,
        Action queueSave)
    {
        return new YouTubeMiniPlayerDragWiringCallbacks(
            bringToFront,
            getCursorScreenPoint,
            keepToolbarVisible,
            clamp,
            syncPopupOffsets,
            reapplyContentTransform,
            queueSave);
    }
}
