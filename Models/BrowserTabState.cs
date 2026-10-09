namespace TabBrowser.Models;

/// <summary>
/// <see cref="BrowserTab"/>から取得した、永続化可能な読み取り専用スナップショットです。
/// </summary>
public sealed class BrowserTabState
{
    internal BrowserTabState(
        Guid id,
        string? url,
        string? title,
        bool isFolderTab,
        IEnumerable<string>? navigationHistory,
        int navigationHistoryIndex,
        TabAudioPan audioPan,
        bool isMuted,
        int audioBoostDecibels,
        bool isPinned,
        DateTimeOffset? lastActivatedAt,
        string containerProfileId,
        string? splitViewId,
        string? splitViewOrientation,
        double? splitViewRatio,
        string? youTubePlaybackVideoId,
        double? youTubePlaybackPositionSeconds,
        double? youTubePlaybackSpeed,
        bool? youTubePlaybackIsPaused,
        bool? youTubePlaybackIsLooping,
        bool? youTubePlaybackIsTheaterMode)
    {
        Id = id;
        Url = url;
        Title = title;
        IsFolderTab = isFolderTab;
        NavigationHistory = Array.AsReadOnly(navigationHistory?.ToArray() ?? []);
        NavigationHistoryIndex = navigationHistoryIndex;
        AudioPan = audioPan;
        IsMuted = isMuted;
        AudioBoostDecibels = audioBoostDecibels;
        IsPinned = isPinned;
        LastActivatedAt = lastActivatedAt;
        ContainerProfileId = containerProfileId;
        SplitViewId = splitViewId;
        SplitViewOrientation = splitViewOrientation;
        SplitViewRatio = splitViewRatio;
        YouTubePlaybackVideoId = youTubePlaybackVideoId;
        YouTubePlaybackPositionSeconds = youTubePlaybackPositionSeconds;
        YouTubePlaybackSpeed = youTubePlaybackSpeed;
        YouTubePlaybackIsPaused = youTubePlaybackIsPaused;
        YouTubePlaybackIsLooping = youTubePlaybackIsLooping;
        YouTubePlaybackIsTheaterMode = youTubePlaybackIsTheaterMode;
    }

    public Guid Id { get; }

    public string? Url { get; }

    public string? Title { get; }

    public bool IsFolderTab { get; }

    public IReadOnlyList<string> NavigationHistory { get; }

    public int NavigationHistoryIndex { get; }

    public TabAudioPan AudioPan { get; }

    public bool IsMuted { get; }

    public int AudioBoostDecibels { get; }

    public bool IsPinned { get; }

    public DateTimeOffset? LastActivatedAt { get; }

    public string ContainerProfileId { get; }

    public string? SplitViewId { get; }

    public string? SplitViewOrientation { get; }

    public double? SplitViewRatio { get; }

    public string? YouTubePlaybackVideoId { get; }

    public double? YouTubePlaybackPositionSeconds { get; }

    public double? YouTubePlaybackSpeed { get; }

    public bool? YouTubePlaybackIsPaused { get; }

    public bool? YouTubePlaybackIsLooping { get; }

    public bool? YouTubePlaybackIsTheaterMode { get; }
}
