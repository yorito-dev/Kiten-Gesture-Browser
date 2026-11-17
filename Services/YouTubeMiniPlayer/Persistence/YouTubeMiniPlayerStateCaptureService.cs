using TabBrowser.Models;
using Microsoft.Web.WebView2.Wpf;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerStateCaptureService
{
    public static async Task<bool> CaptureLoadedAsync(
        IEnumerable<YouTubeMiniPlayerState> states,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        var changed = false;
        foreach (var state in states.ToList())
        {
            changed |= await CaptureAsync(state, runtimes);
        }

        return changed;
    }

    public static async Task<bool> CaptureFallbackAsync(
        IEnumerable<YouTubeMiniPlayerState> states,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        DateTimeOffset observedAt,
        TimeSpan eventStateFreshness)
    {
        var changed = false;
        foreach (var state in states.ToList())
        {
            if (state.SourceKind == MiniPlayerSourceKind.YouTube
                && runtimes.TryGetValue(state.Id, out var runtime)
                && runtime.LastPlaybackStateMessageAt is { } lastMessageAt
                && observedAt - lastMessageAt <= eventStateFreshness)
            {
                continue;
            }

            changed |= await CaptureAsync(state, runtimes);
        }

        return changed;
    }

    public static async Task<bool> CaptureAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.LiveContinuationNavigation is not null)
        {
            return false;
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            return runtime.LocalVideoView?.Controller?.CaptureState() == true;
        }

        if (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is null)
        {
            return false;
        }

        var before = YouTubeMiniPlayerStateService.CreatePlaybackStateSnapshot(state);
        var sourceUrl = state.Url;
        var sourceWebView = runtime.WebView;
        var result = await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildPlaybackStateReadScript(),
            showStatus: false,
            _ => { });
        return ApplyCaptureResult(state, runtime, runtimes, sourceUrl, sourceWebView, before, result, cancellationToken);
    }

    internal static bool ApplyCaptureResult(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes, string sourceUrl, WebView2? sourceWebView,
        YouTubeMiniPlayerPlaybackStateSnapshot before, string? result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (runtime.LiveContinuationNavigation is not null || runtime.IsDisposed || state.Url != sourceUrl
            || !ReferenceEquals(runtime.WebView, sourceWebView)
            || !runtimes.TryGetValue(state.Id, out var current) || !ReferenceEquals(current, runtime))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(
            runtime,
            state.IsLiveBroadcast,
            state.LiveViewerCountText);
        var after = YouTubeMiniPlayerStateService.CreatePlaybackStateSnapshot(state);
        return YouTubeMiniPlayerStateService.HasPlaybackStateChanged(before, after);
    }
}
