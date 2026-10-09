using System.Windows;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRuntimeLoadCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeLoadCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<string?, Task<CoreWebView2Environment>> ensureEnvironmentAsync,
        Action queueSave,
        Action<string> setStatus,
        Func<YouTubeMiniPlayerState, Task<long?>> applyWatchPageModeAsync,
        Func<YouTubeMiniPlayerState, long, Task> applySavedStateAsync,
        Action<Guid, bool> bringToFront,
        Action<YouTubeMiniPlayerState, bool> setToolbarVisibility,
        Action<YouTubeMiniPlayerRuntime> keepToolbarVisible,
        Func<Point> getCursorScreenPoint,
        Func<bool> isLeftMouseButtonDown,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp,
        Action<YouTubeMiniPlayerRuntime> syncPopupOffsets,
        Action<YouTubeMiniPlayerRuntime> reapplyContentTransform,
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
        Action<YouTubeMiniPlayerState, YouTubePlaybackDiagnosticMessage> reportPlaybackDiagnostic,
        Action<YouTubeMiniPlayerState, CoreWebView2ProcessFailedEventArgs> reportProcessFailure,
        Action<YouTubeMiniPlayerState> beginLiveChatTransition,
        Func<YouTubeMiniPlayerState, YouTubeLiveChatAvailabilityResult, Task> applyLiveChatAvailabilityAsync,
        Action<YouTubeMiniPlayerRuntime> recordBrowsingHistory,
        Action<YouTubeMiniPlayerState, string> requestAutoRecovery,
        Action<YouTubeMiniPlayerState, bool> queueInactiveSuspension,
        Action updateStateCaptureTimer)
    {
        return new YouTubeMiniPlayerRuntimeLoadCallbacks(
            runtime => YouTubeMiniPlayerRuntimeContentFactory.CreateWebView(
                runtime.State,
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
            ensureEnvironmentAsync,
            runtime => YouTubeMiniPlayerTitleService.AttachDocumentTitleHandler(
                runtime,
                queueSave),
            runtime => YouTubeMiniPlayerWebMessageDispatchService.AttachHandler(
                runtime,
                runtimes,
                YouTubeMiniPlayerWebMessageDispatchCallbackFactory.Create(
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
                    reportPlaybackDiagnostic)),
            updateStateCaptureTimer);
    }
}
