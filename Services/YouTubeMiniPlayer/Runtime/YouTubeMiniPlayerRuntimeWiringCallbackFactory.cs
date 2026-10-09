using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRuntimeWiringCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeWiringCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<YouTubeMiniPlayerState> forcePopupNativePosition,
        Action<Guid, bool> bringToFront,
        Action<FrameworkElement, YouTubeMiniPlayerState> attachDrag,
        Action<YouTubeMiniPlayerState> toggleMinimized,
        Action<YouTubeMiniPlayerState, bool> setMinimized,
        Action<YouTubeMiniPlayerState> activateSourceTab,
        Action<Guid> close,
        Action<YouTubeMiniPlayerState, bool> setTitleBandVisibility,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerRuntime> queueOverlayPositionRefresh,
        Func<Point> getHostOrigin,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action<string> setStatus,
        Action queueSave,
        Action<YouTubeMiniPlayerState, bool> setToolbarVisibility,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        double speedStep)
    {
        return new YouTubeMiniPlayerRuntimeWiringCallbacks(
            forcePopupNativePosition,
            bringToFront,
            attachDrag,
            toggleMinimized,
            setMinimized,
            activateSourceTab,
            close,
            setTitleBandVisibility,
            updateTitleBandPlacement,
            queueOverlayPositionRefresh,
            getHostOrigin,
            setSpeedResetContent,
            setLoopContent,
            YouTubeMiniPlayerPlaybackCommandService.CreateChangeSpeedHandler(
                runtimes,
                setStatus,
                setSpeedResetContent,
                setLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateSetSpeedHandler(
                runtimes,
                setStatus,
                setSpeedResetContent,
                setLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateToggleLoopHandler(
                runtimes,
                setStatus,
                setSpeedResetContent,
                setLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateRefreshDisplayHandler(
                runtimes,
                setStatus,
                setSpeedResetContent,
                setLoopContent),
            YouTubeMiniPlayerPlaybackCommandService.CreateAbRepeatHandler(
                runtimes,
                setStatus,
                queueSave),
            setToolbarVisibility,
            updateToolbarPlacement,
            YouTubeMiniPlayerInputService.IsTitleBandHotZone,
            YouTubeMiniPlayerInputService.IsToolbarHotZone,
            setStatus,
            queueSave,
            speedStep);
    }
}
