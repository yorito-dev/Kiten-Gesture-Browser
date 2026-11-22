using System.Windows;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerRuntimeWebViewCallbacks
{
    public required Action QueueSave { get; init; }
    public required Action<string> SetStatus { get; init; }
    public required Action<YouTubeMiniPlayerState, Uri> OpenLinkInTab { get; init; }
    public required Func<YouTubeMiniPlayerState, Task<long?>> ApplyWatchPageModeAsync { get; init; }
    public required Func<YouTubeMiniPlayerState, long, Task> ApplySavedStateAsync { get; init; }
    public required Action<YouTubeMiniPlayerState, string> RequestAutoRecovery { get; init; }
    public required Action<YouTubeMiniPlayerState, CoreWebView2ProcessFailedEventArgs> ReportProcessFailure { get; init; }
    public required Action<YouTubeMiniPlayerState> BeginLiveChatTransition { get; init; }
    public required Func<YouTubeMiniPlayerState, YouTubeLiveChatAvailabilityResult, Task> ApplyLiveChatAvailabilityAsync { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> RecordBrowsingHistory { get; init; }
    public required Action<YouTubeMiniPlayerState, bool> QueueInactiveSuspension { get; init; }
    public Func<bool>? IsClosing { get; init; }
}

internal sealed class YouTubeMiniPlayerRuntimeLoadInputCallbacks
{
    public required Func<Point> GetCursorScreenPoint { get; init; }
    public required Func<bool> IsLeftMouseButtonDown { get; init; }
    public required Action<YouTubeMiniPlayerState, FrameworkElement, bool> Clamp { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> ReapplyContentTransform { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> BeginGesture { get; init; }
    public required Action<YouTubeMiniPlayerRuntime, string> ExecuteGesture { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> EndGesture { get; init; }
    public required Action<YouTubeMiniPlayerRuntime, YouTubeMiniPlayerSideButton> ExecuteSideButton { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> BeginHoldSeek { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> EndHoldSeek { get; init; }
    public required Action<YouTubeMiniPlayerRuntime, double, double, double> HandleRightWheel { get; init; }
    public required Action<YouTubeMiniPlayerRuntime> EndRightWheel { get; init; }
    public required Action<YouTubeMiniPlayerRuntime, double, double, double> UpdateContentTransform { get; init; }
}

internal static class YouTubeMiniPlayerRuntimeLoadCallbackFactory
{
    public static YouTubeMiniPlayerRuntimeLoadCallbacks Create(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<string?, Task<CoreWebView2Environment>> ensureEnvironmentAsync,
        YouTubeMiniPlayerRuntimeWebViewCallbacks webViewCallbacks,
        YouTubeMiniPlayerPositionCallbacks positionCallbacks,
        YouTubeMiniPlayerCommandCallbacks commandCallbacks,
        YouTubeMiniPlayerOverlayVisibilityCallbacks overlayVisibilityCallbacks,
        YouTubeMiniPlayerRuntimeLoadInputCallbacks inputCallbacks,
        Action<YouTubeMiniPlayerState, YouTubePlaybackDiagnosticMessage> reportPlaybackDiagnostic,
        Action updateStateCaptureTimer)
    {
        return new YouTubeMiniPlayerRuntimeLoadCallbacks(
            runtime => YouTubeMiniPlayerRuntimeContentFactory.CreateWebView(
                runtime.State,
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
            ensureEnvironmentAsync,
            runtime => YouTubeMiniPlayerTitleService.AttachDocumentTitleHandler(
                runtime,
                webViewCallbacks.QueueSave),
            runtime => YouTubeMiniPlayerWebMessageDispatchService.AttachHandler(
                runtime,
                runtimes,
                YouTubeMiniPlayerWebMessageDispatchCallbackFactory.Create(
                    bringToFront: commandCallbacks.BringToFront,
                    setToolbarVisibility: overlayVisibilityCallbacks.SetToolbarVisibility,
                    keepToolbarVisible: overlayVisibilityCallbacks.KeepToolbarVisible,
                    getCursorScreenPoint: inputCallbacks.GetCursorScreenPoint,
                    isLeftMouseButtonDown: inputCallbacks.IsLeftMouseButtonDown,
                    clamp: inputCallbacks.Clamp,
                    syncPopupOffsets: positionCallbacks.SyncPopupOffsets,
                    reapplyContentTransform: inputCallbacks.ReapplyContentTransform,
                    queueSave: webViewCallbacks.QueueSave,
                    setTitleBandVisibility: overlayVisibilityCallbacks.SetTitleBandVisibility,
                    beginGesture: inputCallbacks.BeginGesture,
                    executeGesture: inputCallbacks.ExecuteGesture,
                    endGesture: inputCallbacks.EndGesture,
                    executeSideButton: inputCallbacks.ExecuteSideButton,
                    beginHoldSeek: inputCallbacks.BeginHoldSeek,
                    endHoldSeek: inputCallbacks.EndHoldSeek,
                    handleRightWheel: inputCallbacks.HandleRightWheel,
                    endRightWheel: inputCallbacks.EndRightWheel,
                    updateContentTransform: inputCallbacks.UpdateContentTransform,
                    close: commandCallbacks.Close,
                    reportPlaybackDiagnostic: reportPlaybackDiagnostic,
                    setStatus: webViewCallbacks.SetStatus)),
            updateStateCaptureTimer,
            webViewCallbacks.IsClosing);
    }
}
