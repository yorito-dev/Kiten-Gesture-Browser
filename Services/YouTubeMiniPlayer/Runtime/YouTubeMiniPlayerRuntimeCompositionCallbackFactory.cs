using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerRuntimeCompositionCallbacks(
    Func<YouTubeMiniPlayerState, WebView2> CreateWebView,
    Func<YouTubeMiniPlayerState, FrameworkElement> CreatePlaceholder,
    Func<YouTubeMiniPlayerState, LocalVideoPlayerView> CreateLocalVideoView,
    YouTubeMiniPlayerRuntimeWiringCallbacks WiringCallbacks);

internal sealed class YouTubeMiniPlayerRuntimePlaceholderCallbacks
{
    public required Style ButtonStyle { get; init; }
    public required Func<YouTubeMiniPlayerState, Uri, Task> LoadRuntimeAsync { get; init; }
    public required Func<Guid, long?> TryBeginGestureInteraction { get; init; }
    public required Action<long> CompleteGestureInteraction { get; init; }
}

internal static class YouTubeMiniPlayerRuntimeCompositionCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeCompositionCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimePlaceholderCallbacks placeholderCallbacks,
        YouTubeMiniPlayerRuntimeWebViewCallbacks webViewCallbacks,
        YouTubeMiniPlayerPositionCallbacks positionCallbacks,
        YouTubeMiniPlayerCommandCallbacks commandCallbacks,
        YouTubeMiniPlayerOverlayVisibilityCallbacks overlayVisibilityCallbacks,
        YouTubeMiniPlayerOverlayPlacementCallbacks overlayPlacementCallbacks,
        YouTubeMiniPlayerRuntimePlaybackControls playbackControls,
        Func<YouTubeMiniPlayerState, LocalVideoPlayerView> createLocalVideoView,
        Action<FrameworkElement, YouTubeMiniPlayerState> attachDrag)
    {
        return new YouTubeMiniPlayerRuntimeCompositionCallbacks(
            state => YouTubeMiniPlayerRuntimeContentFactory.CreateWebView(
                state,
                runtimes,
                webViewCallbacks.QueueSave,
                webViewCallbacks.ApplyWatchPageModeAsync,
                webViewCallbacks.ApplySavedStateAsync,
                webViewCallbacks.SetStatus,
                webViewCallbacks.OpenLinkInTab,
                webViewCallbacks.RequestAutoRecovery,
                webViewCallbacks.ReportProcessFailure,
                webViewCallbacks.BeginLiveChatTransition,
                webViewCallbacks.ApplyLiveChatAvailabilityAsync,
                webViewCallbacks.RecordBrowsingHistory,
                webViewCallbacks.QueueInactiveSuspension,
                webViewCallbacks.IsClosing),
            state => YouTubeMiniPlayerRuntimeContentFactory.CreatePlaceholder(
                state,
                placeholderCallbacks.ButtonStyle,
                placeholderCallbacks.LoadRuntimeAsync,
                webViewCallbacks.SetStatus,
                () => placeholderCallbacks.TryBeginGestureInteraction(state.Id),
                placeholderCallbacks.CompleteGestureInteraction,
                () => commandCallbacks.Close(state.Id)),
            createLocalVideoView,
            YouTubeMiniPlayerRuntimeWiringCallbackFactory.Create(
                runtimes,
                positionCallbacks,
                commandCallbacks,
                overlayVisibilityCallbacks,
                overlayPlacementCallbacks,
                playbackControls,
                attachDrag,
                setStatus: webViewCallbacks.SetStatus,
                queueSave: webViewCallbacks.QueueSave));
    }
}
