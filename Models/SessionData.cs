using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TabBrowser.Models;

public sealed class SessionData
{
    public int Version { get; set; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NamedSessionRestorePointMetadata? NamedRestorePoint { get; set; }

    public Guid? LastActiveTabId { get; set; }

    public List<SessionTabState> Tabs { get; set; } = [];

    public List<ClosedTabState> ClosedTabs { get; set; } = [];

    public List<YouTubeMiniPlayerState> ClosedYouTubeMiniPlayers { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<YouTubeMiniPlayerState>? WindowMemoryYouTubeMiniPlayers { get; set; }

    public List<ClosedWindowState> ClosedWindows { get; set; } = [];

    public List<SessionWindowState> Windows { get; set; } = [];
}

public sealed class NamedSessionRestorePointMetadata
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Scope { get; set; } = "normal-window-tabs";
}

public sealed record NamedSessionRestorePointRequest(Guid Id, string Name, DateTimeOffset CreatedAt, SessionData Session);

public sealed record NamedSessionRestorePointInfo(Guid Id, string Name, DateTimeOffset? CreatedAt,
    int TabCount, int WindowCount, string ContentHash, string? Error = null)
{
    public bool CanRestore => Error is null;
    public string ScopeLabel => "全通常Windowのタブセッション（Workspace・メモ・認証情報は対象外）";
}

public sealed class ClosedWindowState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset? ClosedAt { get; set; }

    public SessionWindowState WindowState { get; set; } = new();
}

public sealed class SessionWindowState
{
    public Guid WindowId { get; set; } = Guid.NewGuid();

    public Guid? LastActiveTabId { get; set; }

    public List<SessionTabState> Tabs { get; set; } = [];

    public List<ClosedTabState> ClosedTabs { get; set; } = [];

    public WindowPlacementSettings? Placement { get; set; }

    public bool? IsAlwaysOnTop { get; set; }
}

public sealed class SessionTabState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Url { get; set; }

    public string? Title { get; set; }

    public bool IsFolderTab { get; set; }

    public string? FolderTabViewMode { get; set; }

    public string? FolderTabSortMode { get; set; }

    public string? FolderTabSortDirection { get; set; }

    public FolderTabSessionState? FolderTabState { get; set; }

    public List<string> NavigationHistory { get; set; } = [];

    public int NavigationHistoryIndex { get; set; } = -1;

    public TabAudioPan AudioPan { get; set; } = TabAudioPan.Stereo;

    public bool IsMuted { get; set; }

    public int AudioBoostDecibels { get; set; }

    public bool IsPinned { get; set; }

    public DateTimeOffset? LastActivatedAt { get; set; }

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string? SplitViewId { get; set; }

    public string? SplitViewOrientation { get; set; }

    public double? SplitViewRatio { get; set; }

    public string? YouTubePlaybackVideoId { get; set; }

    public double? YouTubePlaybackPositionSeconds { get; set; }

    public double? YouTubePlaybackSpeed { get; set; }

    public bool? YouTubePlaybackIsPaused { get; set; }

    public bool? YouTubePlaybackIsLooping { get; set; }

    public bool? YouTubePlaybackIsTheaterMode { get; set; }
}

public sealed class ClosedTabState
{
    public Guid? SourceWindowId { get; set; }

    public Guid? ClosedWindowId { get; set; }

    public Guid? SourceTabId { get; set; }

    public int? ClosedWindowTabIndex { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }

    public bool IsFolderTab { get; set; }

    public string? FolderTabViewMode { get; set; }

    public string? FolderTabSortMode { get; set; }

    public string? FolderTabSortDirection { get; set; }

    public FolderTabSessionState? FolderTabState { get; set; }

    [JsonIgnore]
    public ImageSource? RuntimeThumbnail { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public List<string> NavigationHistory { get; set; } = [];

    public int NavigationHistoryIndex { get; set; } = -1;

    public TabAudioPan AudioPan { get; set; } = TabAudioPan.Stereo;

    public bool IsMuted { get; set; }

    public int AudioBoostDecibels { get; set; }

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string? YouTubePlaybackVideoId { get; set; }

    public double? YouTubePlaybackPositionSeconds { get; set; }

    public double? YouTubePlaybackSpeed { get; set; }

    public bool? YouTubePlaybackIsPaused { get; set; }

    public bool? YouTubePlaybackIsLooping { get; set; }

    public bool? YouTubePlaybackIsTheaterMode { get; set; }
}

public sealed class FolderTabSessionState
{
    public List<string> NavigationHistory { get; set; } = [];

    public int NavigationHistoryIndex { get; set; } = -1;

    public List<string> SelectedPaths { get; set; } = [];

    public string? SelectionAnchorPath { get; set; }

    public double DetailScrollOffset { get; set; }

    public double IconScrollOffset { get; set; }

    public List<string> DetailColumnOrder { get; set; } = [];
}
