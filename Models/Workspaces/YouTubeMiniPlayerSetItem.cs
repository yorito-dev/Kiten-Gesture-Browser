using TabBrowser.Models;
using System.Text.Json.Serialization;

namespace TabBrowser.Models.Workspaces;

public sealed class YouTubeMiniPlayerSetItem : WorkspaceItem
{
    public override WorkspaceItemType Type => WorkspaceItemType.YouTubeMiniPlayerSet;

    public List<YouTubeMiniPlayerWorkspaceState> Players { get; set; } = [];
}

public sealed class YouTubeMiniPlayerWorkspaceState
{
    [JsonIgnore]
    public Guid RuntimeId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public bool IsShorts { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public double ContentZoomFactor { get; set; } = 1;

    public double ContentPanX { get; set; }

    public double ContentPanY { get; set; }

    public bool IsMuted { get; set; }

    public double? Volume { get; set; }

    public int AudioBoostDecibels { get; set; }

    public double? PlaybackRate { get; set; }

    public double? LastPositionSeconds { get; set; }

    public double? DurationSeconds { get; set; }

    public bool? IsPaused { get; set; }

    public bool? IsLooping { get; set; }

    public bool IsMinimized { get; set; }

    public bool IsTopmost { get; set; } = true;

    public static YouTubeMiniPlayerWorkspaceState FromPlayerState(YouTubeMiniPlayerState state)
    {
        return new YouTubeMiniPlayerWorkspaceState
        {
            RuntimeId = state.Id,
            Url = state.Url,
            Title = state.Title,
            ContainerProfileId = ContainerProfile.NormalizeId(state.ContainerProfileId),
            IsShorts = state.IsShorts,
            Left = state.Left,
            Top = state.Top,
            Width = state.Width,
            Height = state.Height,
            ContentZoomFactor = state.ContentZoomFactor,
            ContentPanX = state.ContentPanX,
            ContentPanY = state.ContentPanY,
            IsMuted = state.IsMuted,
            Volume = state.Volume,
            AudioBoostDecibels = YouTubeMiniPlayerAudioBoostProfile.Normalize(state.AudioBoostDecibels),
            PlaybackRate = state.PlaybackRate,
            LastPositionSeconds = state.LastPositionSeconds,
            DurationSeconds = state.DurationSeconds,
            IsPaused = state.IsPaused,
            IsLooping = state.IsLooping,
            IsMinimized = state.IsMinimized,
            IsTopmost = state.IsTopmost
        };
    }

    public YouTubeMiniPlayerState CreatePlayerState(int windowId, DateTimeOffset createdAt)
    {
        return new YouTubeMiniPlayerState
        {
            WindowId = windowId,
            SourceTabId = Guid.Empty,
            ContainerProfileId = ContainerProfile.NormalizeId(ContainerProfileId),
            Url = Url,
            Title = Title,
            IsShorts = IsShorts,
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            ContentZoomFactor = ContentZoomFactor,
            ContentPanX = ContentPanX,
            ContentPanY = ContentPanY,
            IsMuted = IsMuted,
            Volume = Volume,
            AudioBoostDecibels = YouTubeMiniPlayerAudioBoostProfile.Normalize(AudioBoostDecibels),
            PlaybackRate = PlaybackRate,
            LastPositionSeconds = LastPositionSeconds,
            DurationSeconds = DurationSeconds,
            IsPaused = IsPaused,
            IsLooping = IsLooping,
            IsMinimized = IsMinimized,
            IsTopmost = IsTopmost,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }
}
