namespace TabBrowser.Models;

public static class BrowserTabStateMapper
{
    internal static BrowserTabState FromClosedTabState(ClosedTabState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return new BrowserTabState(
            Guid.NewGuid(), tab.Url, tab.Title, tab.IsFolderTab,
            tab.NavigationHistory, tab.NavigationHistoryIndex,
            tab.AudioPan, tab.IsMuted, tab.AudioBoostDecibels,
            isPinned: false, lastActivatedAt: null,
            containerProfileId: tab.ContainerProfileId ?? ContainerProfile.DefaultId,
            splitViewId: null, splitViewOrientation: null, splitViewRatio: null,
            tab.YouTubePlaybackVideoId, tab.YouTubePlaybackPositionSeconds,
            tab.YouTubePlaybackSpeed, tab.YouTubePlaybackIsPaused,
            tab.YouTubePlaybackIsLooping, tab.YouTubePlaybackIsTheaterMode);
    }

    internal static ClosedTabState ToClosedTabState(
        SessionTabState tab,
        DateTimeOffset closedAt)
    {
        return new ClosedTabState
        {
            SourceTabId = tab.Id,
            Url = tab.Url,
            Title = tab.Title,
            IsFolderTab = tab.IsFolderTab,
            FolderTabViewMode = tab.FolderTabViewMode,
            FolderTabSortMode = tab.FolderTabSortMode,
            FolderTabSortDirection = tab.FolderTabSortDirection,
            FolderTabState = CloneFolderTabSessionState(tab.FolderTabState),
            ClosedAt = closedAt,
            NavigationHistory = tab.NavigationHistory.ToList(),
            NavigationHistoryIndex = tab.NavigationHistoryIndex,
            AudioPan = tab.AudioPan,
            IsMuted = tab.IsMuted,
            AudioBoostDecibels = tab.AudioBoostDecibels,
            ContainerProfileId = tab.ContainerProfileId,
            YouTubePlaybackVideoId = tab.YouTubePlaybackVideoId,
            YouTubePlaybackPositionSeconds = tab.YouTubePlaybackPositionSeconds,
            YouTubePlaybackSpeed = tab.YouTubePlaybackSpeed,
            YouTubePlaybackIsPaused = tab.YouTubePlaybackIsPaused,
            YouTubePlaybackIsLooping = tab.YouTubePlaybackIsLooping,
            YouTubePlaybackIsTheaterMode = tab.YouTubePlaybackIsTheaterMode
        };
    }

    internal static FolderTabSessionState? CloneFolderTabSessionState(FolderTabSessionState? state)
    {
        if (state is null)
        {
            return null;
        }

        return new FolderTabSessionState
        {
            NavigationHistory = state.NavigationHistory.ToList(),
            NavigationHistoryIndex = state.NavigationHistoryIndex,
            SelectedPaths = state.SelectedPaths.ToList(),
            SelectionAnchorPath = state.SelectionAnchorPath,
            DetailScrollOffset = state.DetailScrollOffset,
            IconScrollOffset = state.IconScrollOffset
        };
    }

    public static BrowserTabState Capture(BrowserTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new BrowserTabState(
            tab.Id,
            tab.Url,
            tab.Title,
            tab.IsFolderTab,
            tab.NavigationHistory,
            tab.NavigationHistoryIndex,
            tab.AudioPan,
            tab.IsMuted,
            tab.AudioBoostDecibels,
            tab.IsPinned,
            tab.LastActivatedAt,
            tab.ContainerProfileId,
            tab.SplitViewId,
            tab.SplitViewOrientation,
            tab.SplitViewRatio,
            tab.YouTubePlaybackVideoId,
            tab.YouTubePlaybackPositionSeconds,
            tab.YouTubePlaybackSpeed,
            tab.YouTubePlaybackIsPaused,
            tab.YouTubePlaybackIsLooping,
            tab.YouTubePlaybackIsTheaterMode);
    }

    public static BrowserTabState FromSessionTabState(SessionTabState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new BrowserTabState(
            state.Id,
            state.Url,
            state.Title,
            state.IsFolderTab,
            state.NavigationHistory,
            state.NavigationHistoryIndex,
            state.AudioPan,
            state.IsMuted,
            state.AudioBoostDecibels,
            state.IsPinned,
            state.LastActivatedAt,
            state.ContainerProfileId ?? ContainerProfile.DefaultId,
            state.SplitViewId,
            state.SplitViewOrientation,
            state.SplitViewRatio,
            state.YouTubePlaybackVideoId,
            state.YouTubePlaybackPositionSeconds,
            state.YouTubePlaybackSpeed,
            state.YouTubePlaybackIsPaused,
            state.YouTubePlaybackIsLooping,
            state.YouTubePlaybackIsTheaterMode);
    }

    public static SessionTabState ToSessionTabState(BrowserTabState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new SessionTabState
        {
            Id = state.Id,
            Url = state.Url,
            Title = state.Title,
            IsFolderTab = state.IsFolderTab,
            NavigationHistory = state.NavigationHistory.ToList(),
            NavigationHistoryIndex = state.NavigationHistoryIndex,
            AudioPan = state.AudioPan,
            IsMuted = state.IsMuted,
            AudioBoostDecibels = state.AudioBoostDecibels,
            IsPinned = state.IsPinned,
            LastActivatedAt = state.LastActivatedAt,
            ContainerProfileId = state.ContainerProfileId,
            SplitViewId = state.SplitViewId,
            SplitViewOrientation = state.SplitViewOrientation,
            SplitViewRatio = state.SplitViewRatio,
            YouTubePlaybackVideoId = state.YouTubePlaybackVideoId,
            YouTubePlaybackPositionSeconds = state.YouTubePlaybackPositionSeconds,
            YouTubePlaybackSpeed = state.YouTubePlaybackSpeed,
            YouTubePlaybackIsPaused = state.YouTubePlaybackIsPaused,
            YouTubePlaybackIsLooping = state.YouTubePlaybackIsLooping,
            YouTubePlaybackIsTheaterMode = state.YouTubePlaybackIsTheaterMode
        };
    }

    public static BrowserTab CreateBrowserTab(BrowserTabState state)
    {
        return CreateBrowserTab(state, state.Url);
    }

    public static BrowserTab CreateBrowserTab(BrowserTabState state, string? restoredUrl)
    {
        return CreateBrowserTab(state, restoredUrl, DateTimeOffset.MinValue);
    }

    public static BrowserTab CreateBrowserTab(
        BrowserTabState state,
        string? restoredUrl,
        DateTimeOffset missingLastActivatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new BrowserTab
        {
            Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
            Url = restoredUrl,
            Title = state.Title,
            IsFolderTab = state.IsFolderTab,
            NavigationHistory = state.NavigationHistory.ToList(),
            NavigationHistoryIndex = state.NavigationHistoryIndex,
            AudioPan = state.AudioPan,
            IsMuted = state.IsMuted,
            AudioBoostDecibels = state.AudioBoostDecibels,
            IsPinned = state.IsPinned,
            LastActivatedAt = state.LastActivatedAt ?? missingLastActivatedAt,
            ContainerProfileId = state.ContainerProfileId,
            SplitViewId = state.SplitViewId,
            SplitViewOrientation = state.SplitViewOrientation,
            SplitViewRatio = state.SplitViewRatio,
            YouTubePlaybackVideoId = state.YouTubePlaybackVideoId,
            YouTubePlaybackPositionSeconds = state.YouTubePlaybackPositionSeconds,
            YouTubePlaybackSpeed = state.YouTubePlaybackSpeed,
            YouTubePlaybackIsPaused = state.YouTubePlaybackIsPaused,
            YouTubePlaybackIsLooping = state.YouTubePlaybackIsLooping,
            YouTubePlaybackIsTheaterMode = state.YouTubePlaybackIsTheaterMode
        };
    }
}
