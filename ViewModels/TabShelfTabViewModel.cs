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
    {
        var groupingChanged = WindowGroupTitle != snapshot.WindowGroupTitle;
        Snapshot = snapshot;
        NotifyContentChanged();
        return groupingChanged;
    }

    public void Update(YouTubeMiniPlayerWorkspaceState playerState)
    {
        PlayerState = playerState;
        UpdateYouTubeMiniPlayerThumbnail(playerState.Url);
        NotifyContentChanged();
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

    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(PlayerState));
        OnPropertyChanged(nameof(IsYouTubeMiniPlayer));
        OnPropertyChanged(nameof(IsFolderTab));
        OnPropertyChanged(nameof(TabId));
        OnPropertyChanged(nameof(WindowId));
        OnPropertyChanged(nameof(WindowGroupTitle));
        OnPropertyChanged(nameof(WindowSortOrder));
        OnPropertyChanged(nameof(IsForeignWindow));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(TabIndex));
        OnPropertyChanged(nameof(DisplayIndex));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(ThumbnailPlaceholderText));
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
