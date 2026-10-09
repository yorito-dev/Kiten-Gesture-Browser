using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerWebMessageDispatchCallbackFactory
{
    public static YouTubeMiniPlayerWebMessageDispatchCallbacks Create(
        Action<Guid, bool> bringToFront,
        Action<YouTubeMiniPlayerState, bool> setToolbarVisibility,
        Action<YouTubeMiniPlayerRuntime> keepToolbarVisible,
        Func<Point> getCursorScreenPoint,
        Func<bool> isLeftMouseButtonDown,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp,
        Action<YouTubeMiniPlayerRuntime> syncPopupOffsets,
        Action<YouTubeMiniPlayerRuntime> reapplyContentTransform,
        Action queueSave,
        Action<YouTubeMiniPlayerState, bool> setTitleBandVisibility,
        Action<YouTubeMiniPlayerRuntime> beginGesture,
        Action<YouTubeMiniPlayerRuntime, string> executeGesture,
        Action<YouTubeMiniPlayerRuntime> endGesture,
        Action<YouTubeMiniPlayerRuntime, YouTubeMiniPlayerSideButton> executeSideButton,
        Action<YouTubeMiniPlayerRuntime> beginHoldSeek,
        Action<YouTubeMiniPlayerRuntime> endHoldSeek,
        Action<YouTubeMiniPlayerRuntime, double, double, double> handleRightWheel,
        Action<YouTubeMiniPlayerRuntime> endRightWheel,
        Action<YouTubeMiniPlayerRuntime, double, double, double> updateContentTransform,
        Action<Guid> close,
        Action<YouTubeMiniPlayerState, YouTubePlaybackDiagnosticMessage> reportPlaybackDiagnostic)
    {
        return new YouTubeMiniPlayerWebMessageDispatchCallbacks(
            bringToFront,
            setToolbarVisibility,
            keepToolbarVisible,
            getCursorScreenPoint,
            isLeftMouseButtonDown,
            clamp,
            syncPopupOffsets,
            reapplyContentTransform,
            queueSave,
            setTitleBandVisibility,
            beginGesture,
            executeGesture,
            endGesture,
            executeSideButton,
            beginHoldSeek,
            endHoldSeek,
            handleRightWheel,
            endRightWheel,
            updateContentTransform,
            close,
            reportPlaybackDiagnostic);
    }
}
