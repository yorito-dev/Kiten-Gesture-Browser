using System;
using System.Collections.Generic;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerWebMessageDispatchCallbacks(
    Action<Guid, bool> BringToFront,
    Action<YouTubeMiniPlayerState, bool> SetToolbarVisibility,
    Action<YouTubeMiniPlayerRuntime> KeepToolbarVisible,
    Func<Point> GetCursorScreenPoint,
    Func<bool> IsLeftMouseButtonDown,
    Action<YouTubeMiniPlayerState, FrameworkElement, bool> Clamp,
    Action<YouTubeMiniPlayerRuntime> SyncPopupOffsets,
    Action<YouTubeMiniPlayerRuntime> ReapplyContentTransform,
    Action QueueSave,
    Action<YouTubeMiniPlayerState, bool> SetTitleBandVisibility,
    Action<YouTubeMiniPlayerRuntime> BeginGesture,
    Action<YouTubeMiniPlayerRuntime, string> ExecuteGesture,
    Action<YouTubeMiniPlayerRuntime> EndGesture,
    Action<YouTubeMiniPlayerRuntime, YouTubeMiniPlayerSideButton> ExecuteSideButton,
    Action<YouTubeMiniPlayerRuntime> BeginHoldSeek,
    Action<YouTubeMiniPlayerRuntime> EndHoldSeek,
    Action<YouTubeMiniPlayerRuntime, double, double, double> HandleRightWheel,
    Action<YouTubeMiniPlayerRuntime> EndRightWheel,
    Action<YouTubeMiniPlayerRuntime, double, double, double> UpdateContentTransform,
    Action<Guid> Close,
    Action<YouTubeMiniPlayerState, YouTubePlaybackDiagnosticMessage> ReportPlaybackDiagnostic);

internal static class YouTubeMiniPlayerWebMessageDispatchService
{
    public static void AttachHandler(
        YouTubeMiniPlayerRuntime runtime,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerWebMessageDispatchCallbacks callbacks)
    {
        YouTubeMiniPlayerRuntimeLifecycleService.AttachWebMessageHandler(
            runtime,
            (state, args) => Handle(
                state,
                args.Source,
                args.WebMessageAsJson,
                runtimes,
                callbacks));
    }

    public static void Handle(
        YouTubeMiniPlayerState state,
        string source,
        string messageJson,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerWebMessageDispatchCallbacks callbacks)
    {
        if (YouTubePlaybackDiagnosticService.TryParse(messageJson, out var playbackDiagnostic))
        {
            if (IsTrustedYouTubeSource(source)
                && runtimes.TryGetValue(state.Id, out var playbackRuntime)
                && string.Equals(
                    playbackRuntime.WebMessageToken,
                    playbackDiagnostic.Token,
                    StringComparison.Ordinal))
            {
                callbacks.ReportPlaybackDiagnostic(state, playbackDiagnostic);
            }

            return;
        }

        if (!YouTubeMiniPlayerWebMessageService.TryParse(messageJson, out var message))
        {
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.Drag)
        {
            HandleDrag(state, message.DragPhase, message.IsResize, runtimes, callbacks);
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.Hover)
        {
            callbacks.SetToolbarVisibility(state, message.Hovered);
            callbacks.SetTitleBandVisibility(state, message.TitleHovered);
            return;
        }

        if (!IsTrustedYouTubeSource(source)
            || !runtimes.TryGetValue(state.Id, out var runtime)
            || !string.Equals(runtime.WebMessageToken, message.Token, StringComparison.Ordinal))
        {
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.LiveContinuation)
        {
            if (!runtime.IsLiveContinuationEnabled || runtime.IsDisposed || runtime.IsNavigationLoading
                || runtime.WebView?.CoreWebView2 is not { } core
                || !string.Equals(source, core.Source, StringComparison.Ordinal)
                || !YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
                    state, core.Source, message.SourceVideoId, message.NextVideoId, out var uri))
            {
                return;
            }

            var previousPaused = state.IsPaused;
            var previousLiveResolved = runtime.IsInitialLivePlaybackResolved;
            state.IsPaused = false;
            runtime.IsInitialLivePlaybackResolved = false;
            try
            {
                YouTubeMiniPlayerWebViewService.NavigateWithYouTubeReferer(core, uri);
            }
            catch
            {
                state.IsPaused = previousPaused;
                runtime.IsInitialLivePlaybackResolved = previousLiveResolved;
            }
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.HideToolbar)
        {
            YouTubeMiniPlayerOverlayVisibilityService.HideToolbarImmediately(runtime);
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.ToggleFullScreen)
        {
            YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.CloseGesture)
        {
            callbacks.Close(state.Id);
            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.SideButton)
        {
            if (message.SideButton != YouTubeMiniPlayerSideButton.Unknown)
            {
                callbacks.ExecuteSideButton(runtime, message.SideButton);
            }

            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.Gesture)
        {
            if (message.GesturePhase == YouTubeMiniPlayerGesturePhase.Start)
            {
                callbacks.BeginGesture(runtime);
            }
            else if (message.GesturePhase == YouTubeMiniPlayerGesturePhase.Complete)
            {
                callbacks.EndGesture(runtime);
                if (!string.IsNullOrWhiteSpace(message.Route))
                {
                    callbacks.ExecuteGesture(runtime, message.Route);
                }
            }
            else if (message.GesturePhase == YouTubeMiniPlayerGesturePhase.Cancel)
            {
                callbacks.EndGesture(runtime);
            }

            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.HoldSeek)
        {
            if (message.RightInputPhase == YouTubeMiniPlayerRightInputPhase.Start)
            {
                callbacks.BeginHoldSeek(runtime);
            }
            else if (message.RightInputPhase == YouTubeMiniPlayerRightInputPhase.End)
            {
                callbacks.EndHoldSeek(runtime);
            }

            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.RightWheel)
        {
            if (message.RightInputPhase == YouTubeMiniPlayerRightInputPhase.Wheel)
            {
                callbacks.HandleRightWheel(
                    runtime,
                    message.WheelDelta,
                    message.ContentAnchorX,
                    message.ContentAnchorY);
            }
            else if (message.RightInputPhase == YouTubeMiniPlayerRightInputPhase.End)
            {
                callbacks.EndRightWheel(runtime);
            }

            return;
        }

        if (message.Kind == YouTubeMiniPlayerWebMessageKind.ContentTransform)
        {
            callbacks.UpdateContentTransform(
                runtime,
                message.ContentZoomFactor,
                message.ContentPanX,
                message.ContentPanY);
        }
    }

    private static bool IsTrustedYouTubeSource(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && YouTubeUrlService.IsYouTubeUrl(source);
    }

    private static void HandleDrag(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerDragPhase phase,
        bool isResize,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerWebMessageDispatchCallbacks callbacks)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        YouTubeMiniPlayerWebDragService.HandlePhase(
            runtime,
            phase,
            isResize,
            new YouTubeMiniPlayerWebDragCallbacks(
                callbacks.BringToFront,
                callbacks.SetToolbarVisibility,
                callbacks.KeepToolbarVisible,
                callbacks.GetCursorScreenPoint,
                callbacks.IsLeftMouseButtonDown,
                callbacks.Clamp,
                callbacks.SyncPopupOffsets,
                callbacks.ReapplyContentTransform,
                callbacks.QueueSave));
    }
}
