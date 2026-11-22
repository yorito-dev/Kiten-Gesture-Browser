using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerRuntimePlaybackControls
{
    public required Action<Button, double?> SetSpeedResetContent { get; init; }
    public required Action<Button, bool?> SetLoopContent { get; init; }
    public required double SpeedStep { get; init; }
}

internal static class YouTubeMiniPlayerRuntimeWiringCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeWiringCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerPositionCallbacks positionCallbacks,
        YouTubeMiniPlayerCommandCallbacks commandCallbacks,
        YouTubeMiniPlayerOverlayVisibilityCallbacks overlayVisibilityCallbacks,
        YouTubeMiniPlayerOverlayPlacementCallbacks overlayPlacementCallbacks,
        YouTubeMiniPlayerRuntimePlaybackControls playbackControls,
        Action<FrameworkElement, YouTubeMiniPlayerState> attachDrag,
        Action<string> setStatus,
        Action queueSave)
    {
        return new YouTubeMiniPlayerRuntimeWiringCallbacks(
            positionCallbacks.ForceNativePosition,
            commandCallbacks.BringToFront,
            attachDrag,
            commandCallbacks.ToggleMinimized,
            commandCallbacks.SetMinimized,
            commandCallbacks.ActivateSourceTab,
            commandCallbacks.Close,
            overlayVisibilityCallbacks.SetTitleBandVisibility,
            overlayPlacementCallbacks.UpdateTitleBandPlacement,
            positionCallbacks.QueueNativePositionRefresh,
            positionCallbacks.GetHostOrigin,
            playbackControls.SetSpeedResetContent,
            playbackControls.SetLoopContent,
            YouTubeMiniPlayerPlaybackCommandService.CreateChangeSpeedHandler(
                runtimes,
                setStatus,
                playbackControls.SetSpeedResetContent,
                playbackControls.SetLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateSetSpeedHandler(
                runtimes,
                setStatus,
                playbackControls.SetSpeedResetContent,
                playbackControls.SetLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateToggleLoopHandler(
                runtimes,
                setStatus,
                playbackControls.SetSpeedResetContent,
                playbackControls.SetLoopContent,
                queueSave),
            YouTubeMiniPlayerPlaybackCommandService.CreateRefreshDisplayHandler(
                runtimes,
                setStatus,
                playbackControls.SetSpeedResetContent,
                playbackControls.SetLoopContent),
            YouTubeMiniPlayerPlaybackCommandService.CreateAbRepeatHandler(
                runtimes,
                setStatus,
                queueSave),
            overlayVisibilityCallbacks.SetToolbarVisibility,
            overlayPlacementCallbacks.UpdateToolbarPlacement,
            YouTubeMiniPlayerInputService.IsTitleBandHotZone,
            YouTubeMiniPlayerInputService.IsToolbarHotZone,
            setStatus,
            queueSave,
            playbackControls.SpeedStep);
    }
}
