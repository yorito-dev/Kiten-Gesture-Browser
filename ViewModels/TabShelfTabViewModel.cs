using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class TabShelfTabViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _youTubeMiniPlayerThumbnailUrl = string.Empty;
    private ImageSource? _youTubeMiniPlayerThumbnail;

    public TabShelfTabViewModel(CapturedTabSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public TabShelfTabViewModel(YouTubeMiniPlayerWorkspaceState playerState)
    {
        PlayerState = playerState;
        UpdateYouTubeMiniPlayerThumbnail(playerState.Url);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CapturedTabSnapshot? Snapshot { get; private set; }

    public YouTubeMiniPlayerWorkspaceState? PlayerState { get; private set; }

    public bool IsYouTubeMiniPlayer => PlayerState is not null;

    public bool IsFolderTab => Snapshot?.IsFolderTab == true;

    public Guid TabId => Snapshot?.TabId ?? PlayerState?.RuntimeId ?? Guid.Empty;

    public Guid WindowId => Snapshot?.WindowId ?? Guid.Empty;

    public string WindowGroupTitle => Snapshot?.WindowGroupTitle ?? "YOUTUBE MINI PLAYERS";

    public int WindowSortOrder => Snapshot?.WindowSortOrder ?? int.MaxValue;

    public bool IsForeignWindow => Snapshot?.IsForeignWindow == true;

    public string Title => Snapshot?.Title ?? PlayerState?.Title ?? string.Empty;

    public string Url => Snapshot?.Url ?? PlayerState?.Url ?? string.Empty;

    public int TabIndex => Snapshot?.TabIndex ?? -1;

    public string DisplayIndex => IsYouTubeMiniPlayer
        ? "YT"
        : IsFolderTab
            ? "DIR"
            : (TabIndex + 1).ToString();

    public bool IsActive => Snapshot?.IsActive == true;

    public bool IsPinned => Snapshot?.IsPinned == true;

    public ImageSource? Favicon => Snapshot?.Favicon;

    public bool HasFavicon => Snapshot?.HasFavicon == true;

    public ImageSource? Thumbnail => Snapshot?.Thumbnail ?? _youTubeMiniPlayerThumbnail;

    public bool HasThumbnail => Snapshot?.HasThumbnail == true || _youTubeMiniPlayerThumbnail is not null;

    public string SecondaryText => IsYouTubeMiniPlayer
        ? CreatePlayerStatusText(PlayerState!)
        : Url;

    public string ThumbnailPlaceholderText => IsYouTubeMiniPlayer
        ? "YouTubeミニプレイヤー"
        : "サムネイル取得中";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool Update(CapturedTabSnapshot snapshot)
        => UpdateContent(snapshot, PlayerState);

    public void Update(YouTubeMiniPlayerWorkspaceState playerState)
    {
        UpdateContent(Snapshot, playerState);
    }

    private void UpdateYouTubeMiniPlayerThumbnail(string url)
    {
        if (string.Equals(_youTubeMiniPlayerThumbnailUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _youTubeMiniPlayerThumbnailUrl = url;
        _youTubeMiniPlayerThumbnail = null;
    }

    public async Task EnsureYouTubeMiniPlayerThumbnailAsync(
        CancellationToken cancellationToken = default)
    {
        var requestedUrl = PlayerState?.Url ?? string.Empty;
        if (!IsYouTubeMiniPlayer
            || _youTubeMiniPlayerThumbnail is not null
            || YouTubeUrlService.CreateThumbnailUri(requestedUrl) is null)
        {
            return;
        }

        var thumbnail = await YouTubeThumbnailImageService.Shared.GetAsync(
            requestedUrl,
            cancellationToken);
        if (thumbnail is null
            || !string.Equals(
                requestedUrl,
                PlayerState?.Url,
                StringComparison.Ordinal))
        {
            return;
        }

        _youTubeMiniPlayerThumbnail = thumbnail;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    private bool UpdateContent(CapturedTabSnapshot? snapshot, YouTubeMiniPlayerWorkspaceState? playerState)
    {
        var previous = (
            Snapshot, PlayerState, IsYouTubeMiniPlayer, IsFolderTab, TabId, WindowId, WindowGroupTitle,
            WindowSortOrder, IsForeignWindow, Title, Url, TabIndex, DisplayIndex, IsActive, IsPinned,
            Favicon, HasFavicon, Thumbnail, HasThumbnail, SecondaryText, ThumbnailPlaceholderText);
        Snapshot = snapshot;
        PlayerState = playerState;
        if (playerState is not null)
        {
            UpdateYouTubeMiniPlayerThumbnail(playerState.Url);
        }

        NotifyIfChanged(previous.Snapshot, Snapshot);
        NotifyIfChanged(previous.PlayerState, PlayerState);
        NotifyIfChanged(previous.IsYouTubeMiniPlayer, IsYouTubeMiniPlayer);
        NotifyIfChanged(previous.IsFolderTab, IsFolderTab);
        NotifyIfChanged(previous.TabId, TabId);
        NotifyIfChanged(previous.WindowId, WindowId);
        NotifyIfChanged(previous.WindowGroupTitle, WindowGroupTitle);
        NotifyIfChanged(previous.WindowSortOrder, WindowSortOrder);
        NotifyIfChanged(previous.IsForeignWindow, IsForeignWindow);
        NotifyIfChanged(previous.Title, Title);
        NotifyIfChanged(previous.Url, Url);
        NotifyIfChanged(previous.TabIndex, TabIndex);
        NotifyIfChanged(previous.DisplayIndex, DisplayIndex);
        NotifyIfChanged(previous.IsActive, IsActive);
        NotifyIfChanged(previous.IsPinned, IsPinned);
        NotifyIfChanged(previous.Favicon, Favicon);
        NotifyIfChanged(previous.HasFavicon, HasFavicon);
        NotifyIfChanged(previous.Thumbnail, Thumbnail);
        NotifyIfChanged(previous.HasThumbnail, HasThumbnail);
        NotifyIfChanged(previous.SecondaryText, SecondaryText);
        NotifyIfChanged(previous.ThumbnailPlaceholderText, ThumbnailPlaceholderText);
        return previous.WindowGroupTitle != WindowGroupTitle;
    }

    private void NotifyIfChanged<T>(T previous, T current, [CallerArgumentExpression(nameof(current))] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private static string CreatePlayerStatusText(YouTubeMiniPlayerWorkspaceState state)
    {
        var kind = state.IsShorts ? "Shorts" : "通常動画";
        if (state.LastPositionSeconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0)
        {
            return kind;
        }

        var time = TimeSpan.FromSeconds(Math.Floor(seconds));
        var position = seconds >= 3600
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
        return $"{kind} · {position}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
