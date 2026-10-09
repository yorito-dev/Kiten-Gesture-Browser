using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct YouTubeMiniPlayerSourceTabOpenRequest(
    string Url,
    string Title,
    string VideoId,
    bool IsMuted,
    double? PositionSeconds,
    double? PlaybackSpeed,
    bool? IsPaused,
    bool? IsLooping,
    string ContainerProfileId);

internal static class YouTubeMiniPlayerSourceTabService
{
    public static async Task<bool> ActivateAsync(
        YouTubeMiniPlayerState state,
        IEnumerable<BrowserTab> tabs,
        Func<YouTubeMiniPlayerState, Task> captureStateAsync,
        Func<YouTubeMiniPlayerSourceTabOpenRequest, BrowserTab> openReopenedTab,
        Action<BrowserTab> selectTab,
        Func<Task> activateSelectedTabAsync,
        Action queueSave,
        Action<string> setStatus,
        Action<BrowserTab, YouTubeMiniPlayerState>? prepareTab = null,
        bool captureBeforeReopen = true)
    {
        var tab = YouTubeMiniPlayerNavigationService.FindMatchingSourceTab(state, tabs);
        var reopenedSourceTab = false;
        if (tab is null)
        {
            tab = await ReopenAsync(
                state,
                captureStateAsync,
                openReopenedTab,
                queueSave,
                setStatus,
                captureBeforeReopen);
            if (tab is null)
            {
                return false;
            }

            reopenedSourceTab = true;
        }
        else if (tab.Id != state.SourceTabId)
        {
            ApplyReopenedSourceTab(state, tab.Id, DateTimeOffset.UtcNow);
            queueSave();
        }

        prepareTab?.Invoke(tab, state);
        selectTab(tab);
        await activateSelectedTabAsync();
        setStatus(
            reopenedSourceTab
                ? "元のYouTube URLをタブで開きました"
                : "元のYouTubeタブを表示しました");
        return true;
    }

    public static bool TryGetVideoId(YouTubeMiniPlayerState state, out string videoId)
    {
        return YouTubeUrlService.TryGetVideoId(state.Url, out videoId);
    }

    public static YouTubeMiniPlayerSourceTabOpenRequest CreateReopenRequest(
        YouTubeMiniPlayerState state,
        string videoId)
    {
        var url = YouTubeMiniPlayerNavigationService.CreateReturnTabUrl(state, videoId);
        return new YouTubeMiniPlayerSourceTabOpenRequest(
            url,
            YouTubeMiniPlayerNavigationService.NormalizeTitle(state.Title, url),
            videoId,
            state.IsMuted,
            state.LastPositionSeconds,
            state.PlaybackRate,
            state.IsPaused,
            state.IsLooping,
            state.ContainerProfileId);
    }

    public static void ApplyReopenedSourceTab(
        YouTubeMiniPlayerState state,
        Guid sourceTabId,
        DateTimeOffset updatedAt)
    {
        state.SourceTabId = sourceTabId;
        state.UpdatedAt = updatedAt;
    }

    private static async Task<BrowserTab?> ReopenAsync(
        YouTubeMiniPlayerState state,
        Func<YouTubeMiniPlayerState, Task> captureStateAsync,
        Func<YouTubeMiniPlayerSourceTabOpenRequest, BrowserTab> openReopenedTab,
        Action queueSave,
        Action<string> setStatus,
        bool captureBeforeReopen)
    {
        if (!TryGetVideoId(state, out var videoId))
        {
            setStatus("元タブのYouTube URLを開き直せません");
            return null;
        }

        if (captureBeforeReopen)
        {
            await captureStateAsync(state);
        }
        var request = CreateReopenRequest(state, videoId);
        var tab = openReopenedTab(request);
        ApplyReopenedSourceTab(state, tab.Id, DateTimeOffset.UtcNow);
        queueSave();
        return tab;
    }
}
