using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerOpenWorkflowCallbacks(
    Func<BrowserTab, string?> GetCurrentPageUrl,
    Func<BrowserTab, string, string?> GetCurrentPageTitle,
    Func<YouTubeMiniPlayerState, YouTubeMiniPlayerRuntime> CreateRuntime,
    Action<YouTubeMiniPlayerState, UIElement> SetPosition,
    Action<Guid> BringToFront,
    Func<YouTubeMiniPlayerState, Uri, Task> LoadRuntimeAsync,
    Action QueueSave,
    Action<Guid> Close,
    Action<string> SetStatus,
    Func<BrowserTab, YouTubeMiniPlayerOpenRequest, Task<YouTubeMiniPlayerOpenRequest?>>? CapturePositionAsync = null);

internal static class YouTubeMiniPlayerOpenWorkflowService
{
    public static async Task OpenFromTabAsync(
        BrowserTab sourceTab,
        IReadOnlyCollection<BrowserTab> tabs,
        int windowId,
        double hostWidth,
        double hostHeight,
        YouTubeMiniPlayerController controller,
        YouTubeMiniPlayerOpenWorkflowCallbacks callbacks)
    {
        if (!tabs.Contains(sourceTab) || sourceTab.IsWorkspaceTab)
        {
            return;
        }

        var pageUrl = callbacks.GetCurrentPageUrl(sourceTab) ?? sourceTab.Url;
        var title = callbacks.GetCurrentPageTitle(sourceTab, pageUrl ?? string.Empty);
        if (!YouTubeMiniPlayerOpenRequestService.TryCreateFromTab(pageUrl, title, out var request))
        {
            callbacks.SetStatus("YouTube動画ページでのみミニプレイヤーを開けます");
            return;
        }

        if (callbacks.CapturePositionAsync is { } capturePositionAsync)
        {
            var capturedRequest = await capturePositionAsync(sourceTab, request);
            if (capturedRequest is null || !tabs.Contains(sourceTab))
            {
                return;
            }

            request = capturedRequest;
        }

        await OpenAsync(
            sourceTab,
            request,
            windowId,
            hostWidth,
            hostHeight,
            controller,
            callbacks);
    }

    public static async Task OpenFromLinkAsync(
        BrowserTab sourceTab,
        string linkUri,
        string? linkText,
        IReadOnlyCollection<BrowserTab> tabs,
        int windowId,
        double hostWidth,
        double hostHeight,
        YouTubeMiniPlayerController controller,
        YouTubeMiniPlayerOpenWorkflowCallbacks callbacks)
    {
        if (!tabs.Contains(sourceTab))
        {
            return;
        }

        if (!YouTubeMiniPlayerOpenRequestService.TryCreateFromLink(linkUri, linkText, out var request))
        {
            callbacks.SetStatus("YouTube動画リンクでのみミニプレイヤーを開けます");
            return;
        }

        await OpenAsync(
            sourceTab,
            request,
            windowId,
            hostWidth,
            hostHeight,
            controller,
            callbacks);
    }

    private static async Task OpenAsync(
        BrowserTab sourceTab,
        YouTubeMiniPlayerOpenRequest request,
        int windowId,
        double hostWidth,
        double hostHeight,
        YouTubeMiniPlayerController controller,
        YouTubeMiniPlayerOpenWorkflowCallbacks callbacks)
    {
        if (YouTubeUrlService.TryGetVideoId(request.Url, out var requestedVideoId))
        {
            var existingState = controller.States.FirstOrDefault(state =>
                state.SourceKind == MiniPlayerSourceKind.YouTube
                && controller.Runtimes.ContainsKey(state.Id)
                && YouTubeUrlService.TryGetVideoId(state.Url, out var existingVideoId)
                && string.Equals(existingVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase));
            if (existingState is not null)
            {
                callbacks.BringToFront(existingState.Id);
                callbacks.SetStatus("同じYouTube動画のミニプレイヤーを前面に表示しました");
                return;
            }
        }

        var navigationUrl = YouTubeUrlService.WithPlaybackPosition(request.Url, request.PositionSeconds);
        if (!YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(navigationUrl, out var uri))
        {
            callbacks.SetStatus("ミニプレイヤーで開けるYouTube URLではありません");
            return;
        }

        var bounds = YouTubeMiniPlayerLayoutService.GetNextAvailableBounds(
            request.IsShorts,
            controller.States.Select(static state =>
                new Rect(state.Left, state.Top, state.Width, state.Height)).ToList(),
            hostWidth,
            hostHeight);
        var runtime = YouTubeMiniPlayerOpenService.CreateAndRegister(
            sourceTab,
            windowId,
            request.Url,
            request.Title,
            request.IsShorts,
            bounds,
            controller,
            callbacks.CreateRuntime,
            callbacks.SetPosition,
            callbacks.BringToFront,
            request.PositionSeconds);

        try
        {
            await callbacks.LoadRuntimeAsync(runtime.State, uri);
            callbacks.QueueSave();
            callbacks.SetStatus(YouTubeMiniPlayerStatusService.CreateOpenedStatus(controller.States.Count));
        }
        catch (Exception ex)
        {
            callbacks.Close(runtime.State.Id);
            callbacks.SetStatus("YouTubeミニプレイヤーを開けませんでした: " + ex.Message);
        }
    }
}
