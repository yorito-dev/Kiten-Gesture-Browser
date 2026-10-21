using System.Globalization;
using System.Text.Json;
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

    internal static bool TryCreateLiveContinuationUri(
        YouTubeMiniPlayerState state,
        string currentSource,
        string? sourceVideoId,
        string? channelId,
        string? expectedToken,
        string? token,
        out Uri uri)
    {
        uri = null!;
        if (state.SourceKind != MiniPlayerSourceKind.YouTube
            || state.IsLooping == true || state.IsAbRepeatEnabled
            || string.IsNullOrEmpty(expectedToken)
            || !string.Equals(expectedToken, token, StringComparison.Ordinal)
            || !Uri.TryCreate(currentSource, UriKind.Absolute, out var currentUri)
            || currentUri.Scheme != Uri.UriSchemeHttps
            || !YouTubeUrlService.TryGetVideoId(currentSource, out var currentId)
            || !YouTubeUrlService.TryGetVideoId(state.Url, out var stateId)
            || !string.Equals(currentId, sourceVideoId, StringComparison.Ordinal)
            || !string.Equals(stateId, sourceVideoId, StringComparison.Ordinal)
            || !Regex.IsMatch(sourceVideoId ?? string.Empty, @"\A[A-Za-z0-9_-]{11}\z")
            || !Regex.IsMatch(channelId ?? string.Empty, @"\AUC[A-Za-z0-9_-]{22}\z"))
        {
            return false;
        }

        uri = new Uri("https://www.youtube.com/channel/" + channelId + "/live");
        return true;
    }

    internal static bool IsLiveContinuationDestination(
        string sourceVideoId, string channelId, string destination, string? resultJson)
    {
        var kind = GetLiveContinuationDestinationKind(destination, channelId);
        if (kind == YouTubeLiveContinuationDestinationKind.Unsupported
            || !TryReadLiveContinuationVideoId(resultJson, channelId, out var videoId)
            || string.Equals(sourceVideoId, videoId, StringComparison.Ordinal)
            || (kind == YouTubeLiveContinuationDestinationKind.WatchPage
                && (!YouTubeUrlService.TryGetVideoId(destination, out var urlVideoId)
                    || !string.Equals(urlVideoId, videoId, StringComparison.Ordinal))))
        {
            return false;
        }

        using var document = JsonDocument.Parse(resultJson!);
        var root = document.RootElement;
        return root.TryGetProperty("isLive", out var live) && live.ValueKind == JsonValueKind.True
            && root.TryGetProperty("isPlayable", out var playable) && playable.ValueKind == JsonValueKind.True
            && root.TryGetProperty("isUpcoming", out var upcoming) && upcoming.ValueKind == JsonValueKind.False;
    }

    internal static YouTubeLiveContinuationDestinationKind GetLiveContinuationDestinationKind(
        string destination, string channelId)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return YouTubeLiveContinuationDestinationKind.Unsupported;
        }

        if (Regex.IsMatch(channelId, @"\AUC[A-Za-z0-9_-]{22}\z")
            && string.Equals(uri.AbsolutePath, "/channel/" + channelId + "/live", StringComparison.Ordinal))
        {
            return YouTubeLiveContinuationDestinationKind.ChannelLivePage;
        }

        return string.Equals(uri.AbsolutePath, "/watch", StringComparison.Ordinal)
            && YouTubeUrlService.TryGetVideoId(destination, out var videoId)
            && Regex.IsMatch(videoId, @"\A[A-Za-z0-9_-]{11}\z")
                ? YouTubeLiveContinuationDestinationKind.WatchPage
                : YouTubeLiveContinuationDestinationKind.Unsupported;
    }

    internal static bool TryReadLiveContinuationVideoId(
        string? resultJson, string channelId, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("videoId", out var actualId) && actualId.ValueKind == JsonValueKind.String
                && Regex.IsMatch(actualId.GetString() ?? string.Empty, @"\A[A-Za-z0-9_-]{11}\z")
                && root.TryGetProperty("channelId", out var actualChannel) && actualChannel.ValueKind == JsonValueKind.String
                && string.Equals(actualChannel.GetString(), channelId, StringComparison.Ordinal))
            {
                videoId = actualId.GetString()!;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
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

internal enum YouTubeLiveContinuationDestinationKind { Unsupported, WatchPage, ChannelLivePage }

internal enum YouTubeLiveContinuationAction { Continue, NavigateToWatch, ReturnToOriginal, ReleaseOnly }

internal readonly record struct YouTubeLiveContinuationResolution(
    YouTubeLiveContinuationAction Action, Uri? NavigationUri);

// 通常遷移が完了するまで保存状態を保護する。ファイルへは保存しない。
internal sealed class YouTubeMiniPlayerLiveContinuationNavigation(
    string sourceVideoId, string channelId, Uri returnUri)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ulong? NavigationId { get; set; }

    public string ChannelId => channelId;

    private bool _resolved;
    public bool IsResolutionStarted { get; private set; }
    public string? CandidateVideoId { get; private set; }

    public bool TryBeginResolution()
    {
        if (_resolved || IsResolutionStarted) return false;
        IsResolutionStarted = true;
        return true;
    }

    public bool TryReadCandidate(string? resultJson, out string videoId)
    {
        if (!_resolved && YouTubeMiniPlayerNavigationService.TryReadLiveContinuationVideoId(
                resultJson, channelId, out videoId)
            && !string.Equals(sourceVideoId, videoId, StringComparison.Ordinal))
        {
            CandidateVideoId = videoId;
            return true;
        }
        videoId = string.Empty;
        return false;
    }

    public bool TryReleaseForSourceChange(string currentSource)
    {
        if (_resolved) return false;
        var kind = YouTubeMiniPlayerNavigationService.GetLiveContinuationDestinationKind(currentSource, channelId);
        if (kind == YouTubeLiveContinuationDestinationKind.ChannelLivePage) return false;
        if (kind == YouTubeLiveContinuationDestinationKind.WatchPage
            && YouTubeUrlService.TryGetVideoId(currentSource, out var currentId)
            && !string.Equals(currentId, sourceVideoId, StringComparison.Ordinal)
            && (CandidateVideoId is null || string.Equals(currentId, CandidateVideoId, StringComparison.Ordinal)))
        {
            // 動画IDの読取中のwatch書換えは、読取後のTryResolveで同じ動画か照合する。
            return false;
        }
        _resolved = true;
        return true;
    }

    public bool TryTimeout(TimeSpan elapsed)
    {
        if (_resolved || elapsed < Timeout) return false;
        _resolved = true;
        return true;
    }

    public bool TryResolve(string destination, string currentSource, string? resultJson,
        out YouTubeLiveContinuationResolution resolution)
    {
        resolution = default;
        if (_resolved)
        {
            return false;
        }

        _resolved = true;
        var valid = YouTubeMiniPlayerNavigationService.IsLiveContinuationDestination(
            sourceVideoId, channelId, destination, resultJson);
        YouTubeMiniPlayerNavigationService.TryReadLiveContinuationVideoId(resultJson, channelId, out var videoId);
        var currentKind = YouTubeMiniPlayerNavigationService.GetLiveContinuationDestinationKind(currentSource, channelId);
        if (!string.Equals(destination, currentSource, StringComparison.Ordinal)
            && currentKind != YouTubeLiveContinuationDestinationKind.ChannelLivePage
            && !(currentKind == YouTubeLiveContinuationDestinationKind.WatchPage
                && YouTubeUrlService.TryGetVideoId(currentSource, out var currentId)
                && string.Equals(currentId, CandidateVideoId ?? videoId, StringComparison.Ordinal)))
        {
            resolution = new(YouTubeLiveContinuationAction.ReleaseOnly, null);
        }
        else if (!valid)
        {
            resolution = new(YouTubeLiveContinuationAction.ReturnToOriginal, returnUri);
        }
        else if (currentKind == YouTubeLiveContinuationDestinationKind.ChannelLivePage)
        {
            YouTubeMiniPlayerNavigationService.TryCreateMiniPlayerUrl(
                "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId), out var watchUrl);
            resolution = new(YouTubeLiveContinuationAction.NavigateToWatch, new Uri(watchUrl));
        }
        else
        {
            resolution = new(YouTubeLiveContinuationAction.Continue, null);
        }

        return true;
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
