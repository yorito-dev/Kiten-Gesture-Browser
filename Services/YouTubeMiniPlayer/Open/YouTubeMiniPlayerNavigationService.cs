using System.Globalization;
using System.Text.RegularExpressions;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerNavigationService
{
    public static bool TryCreateMiniPlayerUrl(string url, out string miniPlayerUrl)
    {
        return TryCreateMiniPlayerUrl(url, out miniPlayerUrl, out _);
    }

    public static bool TryCreateMiniPlayerUrl(string url, out string miniPlayerUrl, out bool isShorts)
    {
        miniPlayerUrl = string.Empty;
        isShorts = YouTubeUrlService.IsShortsUrl(url);
        var normalizedUrl = YouTubeUrlService.NormalizeShortsUrl(url);
        if (!YouTubeUrlService.TryGetVideoId(normalizedUrl, out var videoId))
        {
            return false;
        }

        miniPlayerUrl = "https://www.youtube.com/watch?v="
            + Uri.EscapeDataString(videoId)
            + "&autoplay=1";
        return true;
    }

    public static string CreateNavigationUrl(YouTubeMiniPlayerState state)
    {
        var url = TryCreateMiniPlayerUrl(state.Url, out var normalizedUrl)
            ? normalizedUrl
            : state.Url;
        state.Url = url;
        var navigationUrl = YouTubeUrlService.WithPlaybackPosition(url, state.LastPositionSeconds);
        return state.IsPaused == true
            ? YouTubeUrlService.WithAutoplayDisabled(navigationUrl)
            : navigationUrl;
    }

    public static bool TryApplyCurrentVideoUrl(
        YouTubeMiniPlayerState state,
        string? currentUrl)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube
            || string.IsNullOrWhiteSpace(currentUrl)
            || !TryCreateMiniPlayerUrl(currentUrl, out var normalizedUrl, out var isShorts)
            || !YouTubeUrlService.TryGetVideoId(normalizedUrl, out var currentVideoId))
        {
            return false;
        }

        if (YouTubeUrlService.TryGetVideoId(state.Url, out var previousVideoId)
            && previousVideoId.Equals(currentVideoId, StringComparison.Ordinal))
        {
            return false;
        }

        state.Url = normalizedUrl;
        state.IsShorts = isShorts;
        state.VideoAspectRatio = null;
        state.LastPositionSeconds = null;
        state.DurationSeconds = null;
        state.IsLiveBroadcast = false;
        state.IsAtLiveEdge = false;
        state.LiveViewerCountText = null;
        state.AbRepeatStartSeconds = null;
        state.AbRepeatEndSeconds = null;
        state.IsAbRepeatEnabled = false;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public static string CreateReturnTabUrl(YouTubeMiniPlayerState state, string videoId)
    {
        var queryItems = new List<(string Name, string Value)>();
        var position = YouTubeControlScriptService.NormalizePlaybackPosition(state.LastPositionSeconds);
        if (position is { } positionSeconds && positionSeconds >= 1)
        {
            queryItems.Add(("t", ((int)Math.Floor(positionSeconds)).ToString(CultureInfo.InvariantCulture) + "s"));
        }

        if (state.IsPaused == true)
        {
            queryItems.Add(("autoplay", "0"));
        }

        if (state.IsShorts)
        {
            var shortsUrl = "https://www.youtube.com/shorts/" + Uri.EscapeDataString(videoId);
            return queryItems.Count == 0
                ? shortsUrl
                : shortsUrl + "?" + BuildQuery(queryItems);
        }

        queryItems.Insert(0, ("v", videoId));
        return "https://www.youtube.com/watch?" + BuildQuery(queryItems);
    }

    public static bool IsMatchingSourceTab(
        YouTubeMiniPlayerState state,
        BrowserTab tab)
    {
        return YouTubeUrlService.TryGetVideoId(state.Url, out var stateVideoId)
            && YouTubeUrlService.TryGetVideoId(tab.Url, out var tabVideoId)
            && stateVideoId.Equals(tabVideoId, StringComparison.Ordinal)
            && ContainerProfile.NormalizeId(state.ContainerProfileId).Equals(
                ContainerProfile.NormalizeId(tab.ContainerProfileId),
                StringComparison.Ordinal);
    }

    public static BrowserTab? FindMatchingSourceTab(
        YouTubeMiniPlayerState state,
        IEnumerable<BrowserTab> tabs)
    {
        var matchingTabs = tabs
            .Where(tab => IsMatchingSourceTab(state, tab))
            .ToList();
        return matchingTabs.FirstOrDefault(tab => tab.Id == state.SourceTabId)
            ?? matchingTabs.FirstOrDefault();
    }

    public static string NormalizeTitle(string? title, string url)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalizedTitle = Regex.Replace(title, @"\s+", " ").Trim();
            const string youtubeSuffix = " - YouTube";
            if (normalizedTitle.EndsWith(youtubeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedTitle = normalizedTitle[..^youtubeSuffix.Length].Trim();
            }

            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return normalizedTitle;
            }
        }

        return GetFallbackTitle(url);
    }

    public static string GetFallbackTitle(string url)
    {
        return YouTubeUrlService.TryGetVideoId(url, out var videoId)
            ? videoId
            : "YouTube";
    }

    public static YouTubeMiniPlayerState CloneState(YouTubeMiniPlayerState state)
    {
        return new YouTubeMiniPlayerState
        {
            Id = state.Id,
            WindowId = state.WindowId,
            OwnerSessionWindowId = state.OwnerSessionWindowId,
            SourceTabId = state.SourceTabId,
            SourceKind = state.SourceKind,
            ContainerProfileId = state.ContainerProfileId,
            Url = state.Url,
            Title = state.Title,
            LocalFilePath = state.LocalFilePath,
            IsShorts = state.IsShorts,
            VideoAspectRatio = state.VideoAspectRatio,
            ContentZoomFactor = state.ContentZoomFactor,
            ContentPanX = state.ContentPanX,
            ContentPanY = state.ContentPanY,
            Left = state.Left,
            Top = state.Top,
            Width = state.Width,
            Height = state.Height,
            IsMuted = state.IsMuted,
            Volume = state.Volume,
            AudioBoostDecibels = state.AudioBoostDecibels,
            PlaybackRate = state.PlaybackRate,
            LastPositionSeconds = state.LastPositionSeconds,
            DurationSeconds = state.DurationSeconds,
            IsPaused = state.IsPaused,
            IsLooping = state.IsLooping,
            AbRepeatStartSeconds = state.AbRepeatStartSeconds,
            AbRepeatEndSeconds = state.AbRepeatEndSeconds,
            IsAbRepeatEnabled = state.IsAbRepeatEnabled,
            IsMinimized = state.IsMinimized,
            IsTopmost = state.IsTopmost,
            LiveChatWindowLeft = state.LiveChatWindowLeft,
            LiveChatWindowTop = state.LiveChatWindowTop,
            LiveChatWindowWidth = state.LiveChatWindowWidth,
            LiveChatWindowHeight = state.LiveChatWindowHeight,
            IsLiveChatWindowTopmost = state.IsLiveChatWindowTopmost,
            IsLiveChatWindowDocked = state.IsLiveChatWindowDocked,
            LiveChatWindowDockSide = state.LiveChatWindowDockSide,
            ListOrder = state.ListOrder,
            CreatedAt = state.CreatedAt,
            UpdatedAt = state.UpdatedAt
        };
    }

    private static string BuildQuery(IEnumerable<(string Name, string Value)> queryItems)
    {
        return string.Join(
            "&",
            queryItems.Select(static item =>
                Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value)));
    }
}

internal sealed class YouTubeMiniPlayerNavigationHistoryController
{
    public bool CanGoBack { get; private set; }

    public bool CanGoForward { get; private set; }

    public void Update(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
    }

    public bool TryGoBack(Action goBack)
    {
        return TryNavigate(CanGoBack, goBack);
    }

    public bool TryGoForward(Action goForward)
    {
        return TryNavigate(CanGoForward, goForward);
    }

    private static bool TryNavigate(bool canNavigate, Action navigate)
    {
        if (!canNavigate)
        {
            return false;
        }

        navigate();
        return true;
    }
}
