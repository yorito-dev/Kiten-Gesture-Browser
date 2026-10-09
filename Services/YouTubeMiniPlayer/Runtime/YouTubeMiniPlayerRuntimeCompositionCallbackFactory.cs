using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerRuntimeCompositionCallbacks(
    Func<YouTubeMiniPlayerState, WebView2> CreateWebView,
    Func<YouTubeMiniPlayerState, FrameworkElement> CreatePlaceholder,
    Func<YouTubeMiniPlayerState, LocalVideoPlayerView> CreateLocalVideoView,
    YouTubeMiniPlayerRuntimeWiringCallbacks WiringCallbacks);

internal static class YouTubeMiniPlayerRuntimeCompositionCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeCompositionCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Style buttonStyle,
        Action queueSave,
        Func<YouTubeMiniPlayerState, Task<long?>> applyWatchPageModeAsync,
        Func<YouTubeMiniPlayerState, long, Task> applySavedStateAsync,
        Func<YouTubeMiniPlayerState, Uri, Task> loadRuntimeAsync,
        Action<string> setStatus,
        Func<Guid, long?> tryBeginGestureInteraction,
        Action<long> completeGestureInteraction,
        YouTubeMiniPlayerPositionCallbacks positionCallbacks,
        YouTubeMiniPlayerCommandCallbacks commandCallbacks,
        YouTubeMiniPlayerOverlayVisibilityCallbacks overlayVisibilityCallbacks,
        YouTubeMiniPlayerOverlayPlacementCallbacks overlayPlacementCallbacks,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Func<YouTubeMiniPlayerState, LocalVideoPlayerView> createLocalVideoView,
        Action<FrameworkElement, YouTubeMiniPlayerState> attachDrag,
        double speedStep,
        Action<YouTubeMiniPlayerState, string> requestAutoRecovery,
        Action<YouTubeMiniPlayerState, CoreWebView2ProcessFailedEventArgs> reportProcessFailure,
        Action<YouTubeMiniPlayerState> beginLiveChatTransition,
        Func<YouTubeMiniPlayerState, YouTubeLiveChatAvailabilityResult, Task> applyLiveChatAvailabilityAsync,
        Action<YouTubeMiniPlayerRuntime> recordBrowsingHistory,
        Action<YouTubeMiniPlayerState, bool> queueInactiveSuspension)
    {
        return new YouTubeMiniPlayerRuntimeCompositionCallbacks(
            state => YouTubeMiniPlayerRuntimeContentFactory.CreateWebView(
                state,
                runtimes,
                queueSave,
                applyWatchPageModeAsync,
                applySavedStateAsync,
                setStatus,
                requestAutoRecovery,
                reportProcessFailure,
                beginLiveChatTransition,
                applyLiveChatAvailabilityAsync,
                recordBrowsingHistory,
                queueInactiveSuspension),
            state => YouTubeMiniPlayerRuntimeContentFactory.CreatePlaceholder(
                state,
                buttonStyle,
                loadRuntimeAsync,
                setStatus,
                () => tryBeginGestureInteraction(state.Id),
                completeGestureInteraction,
                () => commandCallbacks.Close(state.Id)),
            createLocalVideoView,
            YouTubeMiniPlayerRuntimeWiringCallbackFactory.Create(
                runtimes,
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
                setSpeedResetContent,
                setLoopContent,
                setStatus,
                queueSave,
                overlayVisibilityCallbacks.SetToolbarVisibility,
                overlayPlacementCallbacks.UpdateToolbarPlacement,
                speedStep));
    }
}
