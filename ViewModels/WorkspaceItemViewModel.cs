using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class WorkspaceItemViewModel : INotifyPropertyChanged
{
    internal const int MaximumNoteCardPreviewCharacters = 320;
    private static readonly Brush FolderIconBackground = CreateSolidBrush("#3A2F12");

    private static readonly string[] IconPalette =
    [
        "#2563EB",
        "#059669",
        "#D97706",
        "#7C3AED",
        "#DC2626",
        "#0891B2",
        "#4F46E5",
        "#BE185D"
    ];

    private string _title;
    private double _x;
    private double _y;
    private bool _isSelected;
    private bool _isDragGuideVisible;
    private bool _isBeingDragged;
    private bool _isShellItemMissing;
    private int _childItemCount;
    private ImageSource? _favicon;
    private ImageSource? _thumbnail;
    private ImageSource? _splitLeftFavicon;
    private ImageSource? _splitRightFavicon;
    private ImageSource? _splitLeftThumbnail;
    private ImageSource? _splitRightThumbnail;

    public WorkspaceItemViewModel(WorkspaceItem item)
    {
        Item = item;
        _title = item.Title;
        _x = item.X;
        _y = item.Y;
        RefreshShellState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceItem Item { get; }

    public Guid Id => Item.Id;

    public WorkspaceItemType Type => Item.Type;

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? TypeLabel : _title;
        set
        {
            var normalized = value.Trim();
            if (_title == normalized)
            {
                return;
            }

            _title = normalized;
            Item.Title = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardPrimaryText));
            OnPropertyChanged(nameof(CardSecondaryText));
        }
    }

    public string Url => Item.Url;

    public string Note
    {
        get => Item.Note;
        set
        {
            var normalized = value.Trim();
            if (Item.Note == normalized)
            {
                return;
            }

            Item.Note = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardPrimaryText));
            OnPropertyChanged(nameof(CardSecondaryText));
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HasFooterText));
            OnPropertyChanged(nameof(ToolTipNote));
            OnPropertyChanged(nameof(HasToolTipNote));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(NoteEditorTitle));
        }
    }

    public double NoteFontSize
    {
        get => Item is NoteItem noteItem
            ? noteItem.FontSize
            : WorkspaceNoteFontSizeProfile.DefaultFontSize;
        set
        {
            if (Item is not NoteItem noteItem)
            {
                return;
            }

            var normalized = WorkspaceNoteFontSizeProfile.Normalize(value);
            if (Math.Abs(noteItem.FontSize - normalized) < 0.1)
            {
                return;
            }

            noteItem.FontSize = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoteLineHeight));
        }
    }

    public double NoteLineHeight => Math.Round(NoteFontSize * 1.4, 1);

    public string NoteEditorTitle
    {
        get
        {
            var content = Item.Note.Trim();
            if (content.Length == 0)
            {
                return "メモ";
            }

            var sentenceEnd = content.IndexOfAny(['\r', '\n', '。', '！', '？', '!', '?']);
            var firstSentence = sentenceEnd >= 0
                ? content[..(sentenceEnd + 1)].Trim()
                : content;

            const int maxTitleLength = 80;
            return firstSentence.Length <= maxTitleLength
                ? firstSentence
                : $"{firstSentence[..maxTitleLength].TrimEnd()}…";
        }
    }

    public ImageSource? Favicon => _favicon ??= CreateImageSource(GetPrimaryFaviconPngBase64());

    public bool HasFavicon => Favicon is not null;

    public bool HasDisplayFavicon => !IsFolder && !IsSplitSession && HasFavicon;

    public ImageSource? Thumbnail => _thumbnail ??= CreateImageSource(Item.ThumbnailPngBase64, 160);

    public bool HasThumbnail => !IsFolder && Thumbnail is not null;

    public bool ShowFallbackIcon => !IsFolder && !IsSplitSession && !HasDisplayFavicon;

    public IReadOnlyList<SessionTab> PrimarySplitTabs => GetPrimarySplitTabs();

    public bool IsSplitSession => PrimarySplitTabs.Count == 2;

    public bool IsTopBottomSplitSession => IsSplitSession
        && PrimarySplitTabs.Any(static tab => string.Equals(
            tab.SplitOrientation,
            TabBrowser.Models.SplitViewOrientation.TopBottom,
            StringComparison.OrdinalIgnoreCase));

    public bool IsLeftRightSplitSession => IsSplitSession && !IsTopBottomSplitSession;

    public SessionTab? SplitLeftTab => PrimarySplitTabs
        .FirstOrDefault(static tab => tab.SplitPane == TabBrowser.Models.SplitViewPane.Left);

    public SessionTab? SplitRightTab => PrimarySplitTabs
        .FirstOrDefault(static tab => tab.SplitPane == TabBrowser.Models.SplitViewPane.Right);

    public ImageSource? SplitLeftFavicon => _splitLeftFavicon ??=
        CreateImageSource(SplitLeftTab?.FaviconPngBase64 ?? string.Empty);

    public ImageSource? SplitRightFavicon => _splitRightFavicon ??=
        CreateImageSource(SplitRightTab?.FaviconPngBase64 ?? string.Empty);

    public bool HasSplitLeftFavicon => SplitLeftFavicon is not null;

    public bool HasSplitRightFavicon => SplitRightFavicon is not null;

    public ImageSource? SplitLeftThumbnail => _splitLeftThumbnail ??=
        CreateImageSource(SplitLeftTab?.ThumbnailPngBase64 ?? string.Empty, 240);

    public ImageSource? SplitRightThumbnail => _splitRightThumbnail ??=
        CreateImageSource(SplitRightTab?.ThumbnailPngBase64 ?? string.Empty, 240);

    public bool HasSplitLeftThumbnail => SplitLeftThumbnail is not null;

    public bool HasSplitRightThumbnail => SplitRightThumbnail is not null;

    public string SplitLeftFallbackText => SplitLeftTab?.IsFolderTab == true ? "DIR" : "WEB";

    public string SplitRightFallbackText => SplitRightTab?.IsFolderTab == true ? "DIR" : "WEB";

    public string SplitLeftTitle => SplitLeftTab?.TitleOrUrl() ?? string.Empty;

    public string SplitRightTitle => SplitRightTab?.TitleOrUrl() ?? string.Empty;

    public string SplitSessionSummary => IsTopBottomSplitSession
        ? "上下分割 · 2 tabs"
        : "左右分割 · 2 tabs";

    public bool IsFolder => Type == WorkspaceItemType.Folder;

    public bool IsNote => Type == WorkspaceItemType.Note;

    public bool CanConvertShellTextFileToNote => Item is ShellItem
    {
        Kind: ShellItemKind.File
    } shellItem && WorkspaceShellItemService.IsTextDocumentPath(shellItem.TargetPath);

    public bool IsShellItem => Item is ShellItem;

    public bool IsShellDirectory => Item is ShellItem { Kind: ShellItemKind.Directory };

    public bool ShowPhysicalDirectoryBadge => Item is ShellItem
    {
        Kind: ShellItemKind.Directory,
        OpenInFolderTab: false
    };

    public bool ShowFolderTabDirectoryBadge => Item is ShellItem
    {
        Kind: ShellItemKind.Directory,
        OpenInFolderTab: true
    };

    public bool IsShellMedia => Item is ShellItem { Kind: ShellItemKind.File } shellItem
        && WorkspaceShellItemService.IsMediaPath(shellItem.TargetPath);

    public bool IsShellVideo => Item is ShellItem { Kind: ShellItemKind.File } shellItem
        && WorkspaceShellItemService.IsVideoPath(shellItem.TargetPath);

    public bool HasShellMediaThumbnail => IsShellMedia && HasThumbnail;

    public bool HasCardThumbnail => HasThumbnail
        && (IsShellMedia || Type == WorkspaceItemType.YouTubeMiniPlayerSet);

    public bool ShowShellVideoOverlay => IsShellVideo && HasThumbnail;

    public string TargetPath => Item is ShellItem shellItem ? shellItem.TargetPath : string.Empty;

    public bool IsShellItemMissing
    {
        get => _isShellItemMissing;
        private set
        {
            if (_isShellItemMissing == value)
            {
                return;
            }

            _isShellItemMissing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShellStateText));
            OnPropertyChanged(nameof(MetaText));
        }
    }

    public string ShellStateText => IsShellItemMissing ? "見つかりません" : string.Empty;

    public bool IsRecent => DateTimeOffset.Now - Item.CreatedAt <= TimeSpan.FromHours(24);

    public string CreatedAtText => Item.CreatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    public string SiteIconText => GetPrimaryHost() is { Length: > 0 } host
        ? char.ToUpperInvariant(NormalizeHost(host)[0]).ToString()
        : Type switch
        {
            WorkspaceItemType.Folder => "F",
            WorkspaceItemType.SessionGroup => "S",
            WorkspaceItemType.YouTubeMiniPlayerSet => "YT",
            WorkspaceItemType.Note => "N",
            WorkspaceItemType.ShellItem => Item is ShellItem { Kind: ShellItemKind.Directory } ? "D" : "F",
            _ => "L"
        };

    public Brush SiteIconBackground => IsFolder
        ? FolderIconBackground
        : CreateBrush(GetPrimaryHost() is { } host ? NormalizeHost(host) : TypeLabel);

    public double X
    {
        get => _x;
        set
        {
            var normalized = Math.Max(0, Math.Round(value, 1));
            if (Math.Abs(_x - normalized) < 0.1)
            {
                return;
            }

            _x = normalized;
            Item.X = normalized;
            OnPropertyChanged();
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            var normalized = Math.Max(0, Math.Round(value, 1));
            if (Math.Abs(_y - normalized) < 0.1)
            {
                return;
            }

            _y = normalized;
            Item.Y = normalized;
            OnPropertyChanged();
        }
    }

    public double Width => Math.Clamp(Item.Width, 76, 88);

    public double Height => Math.Clamp(Item.Height, 92, 100);

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

    public bool IsDragGuideVisible
    {
        get => _isDragGuideVisible;
        set
        {
            if (_isDragGuideVisible == value)
            {
                return;
            }

            _isDragGuideVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsBeingDragged
    {
        get => _isBeingDragged;
        set
        {
            if (_isBeingDragged == value)
            {
                return;
            }

            _isBeingDragged = value;
            OnPropertyChanged();
        }
    }

    public int ChildItemCount
    {
        get => _childItemCount;
        set
        {
            var normalized = Math.Max(0, value);
            if (_childItemCount == normalized)
            {
                return;
            }

            _childItemCount = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(CardSecondaryText));
        }
    }

    public string TypeLabel => Type switch
    {
        WorkspaceItemType.Bookmark => "Bookmark",
        WorkspaceItemType.Folder => "論理フォルダー",
        WorkspaceItemType.SessionGroup => "Session",
        WorkspaceItemType.YouTubeMiniPlayerSet => "YouTube Set",
        WorkspaceItemType.Note => "Note",
        WorkspaceItemType.ShellItem => Item is ShellItem shellItem ? GetShellKindLabel(shellItem) : "File",
        _ => "Item"
    };

    public string CompactTypeLabel => Type switch
    {
        WorkspaceItemType.Bookmark => "Link",
        WorkspaceItemType.Folder => "論理フォルダー",
        WorkspaceItemType.SessionGroup => "Sess",
        WorkspaceItemType.YouTubeMiniPlayerSet => "YT Set",
        WorkspaceItemType.Note => "Note",
        WorkspaceItemType.ShellItem => Item is ShellItem shellItem ? GetShellKindLabel(shellItem) : "File",
        _ => "Item"
    };

    public string MetaText
    {
        get
        {
            if (Item is SessionGroupItem sessionGroup)
            {
                return IsSplitSession ? SplitSessionSummary : $"{sessionGroup.Tabs.Count} tabs";
            }

            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                return $"{playerSet.Players.Count} players";
            }

            if (Type == WorkspaceItemType.Folder)
            {
                return $"論理フォルダー · {ChildItemCount}件";
            }

            if (Type == WorkspaceItemType.Bookmark && TryGetHost(Item.Url) is { } host)
            {
                return host;
            }

            if (Item is ShellItem shellItem)
            {
                return IsShellItemMissing ? "見つかりません" : GetShellKindLabel(shellItem);
            }

            return TypeLabel;
        }
    }

    public string CardPrimaryText
    {
        get
        {
            if (Type == WorkspaceItemType.Note)
            {
                return GetNotePreviewText();
            }

            return !string.IsNullOrWhiteSpace(_title)
                ? _title
                : GetPrimaryHost() is { } host
                    ? NormalizeHost(host)
                    : TypeLabel;
        }
    }

    public string CardSecondaryText
    {
        get
        {
            if (Item is SessionGroupItem sessionGroup)
            {
                return IsSplitSession ? SplitSessionSummary : $"{sessionGroup.Tabs.Count} tabs";
            }

            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                var position = FormatPlaybackTime(playerSet.Players.FirstOrDefault()?.LastPositionSeconds);
                return string.IsNullOrWhiteSpace(position)
                    ? $"{playerSet.Players.Count} players"
                    : $"{playerSet.Players.Count} players · {position}";
            }

            if (Type == WorkspaceItemType.Folder)
            {
                return $"論理フォルダー · {ChildItemCount}件";
            }

            if (Type == WorkspaceItemType.Bookmark)
            {
                return Title;
            }

            if (Item is ShellItem shellItem)
            {
                return GetShellKindLabel(shellItem);
            }

            return string.IsNullOrWhiteSpace(Item.Note)
                ? CompactTypeLabel
                : Item.Note;
        }
    }

    public void SetFaviconPngBase64(string faviconPngBase64)
    {
        var normalized = faviconPngBase64.Trim();
        if (Item.FaviconPngBase64 == normalized)
        {
            return;
        }

        Item.FaviconPngBase64 = normalized;
        _favicon = null;
        OnPropertyChanged(nameof(Favicon));
        OnPropertyChanged(nameof(HasFavicon));
        OnPropertyChanged(nameof(HasDisplayFavicon));
        OnPropertyChanged(nameof(ShowFallbackIcon));
        OnPropertyChanged(nameof(SiteIconText));
        OnPropertyChanged(nameof(SiteIconBackground));
    }

    public void SetThumbnailPngBase64(string thumbnailPngBase64, DateTimeOffset thumbnailUpdatedAt)
    {
        var normalized = thumbnailPngBase64.Trim();
        if (Item.ThumbnailPngBase64 == normalized)
        {
            Item.ThumbnailUpdatedAt = thumbnailUpdatedAt;
            return;
        }

        Item.ThumbnailPngBase64 = normalized;
        Item.ThumbnailUpdatedAt = thumbnailUpdatedAt;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(HasShellMediaThumbnail));
        OnPropertyChanged(nameof(HasCardThumbnail));
        OnPropertyChanged(nameof(ShowShellVideoOverlay));
    }

    public void RefreshSplitPreviewImages()
    {
        _splitLeftFavicon = null;
        _splitRightFavicon = null;
        _splitLeftThumbnail = null;
        _splitRightThumbnail = null;
        OnPropertyChanged(nameof(SplitLeftFavicon));
        OnPropertyChanged(nameof(SplitRightFavicon));
        OnPropertyChanged(nameof(HasSplitLeftFavicon));
        OnPropertyChanged(nameof(HasSplitRightFavicon));
        OnPropertyChanged(nameof(SplitLeftThumbnail));
        OnPropertyChanged(nameof(SplitRightThumbnail));
        OnPropertyChanged(nameof(HasSplitLeftThumbnail));
        OnPropertyChanged(nameof(HasSplitRightThumbnail));
    }

    public void UpdateSessionTabThumbnails(IReadOnlyDictionary<Guid, string> thumbnails)
    {
        if (Item is not SessionGroupItem sessionGroup || thumbnails.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var tab in sessionGroup.Tabs)
        {
            if (thumbnails.TryGetValue(tab.Id, out var thumbnailPngBase64)
                && tab.ThumbnailPngBase64 != thumbnailPngBase64)
            {
                tab.ThumbnailPngBase64 = thumbnailPngBase64;
                changed = true;
            }
        }

        if (changed)
        {
            RefreshSplitPreviewImages();
        }
    }

    public void RefreshDisplayBindings()
    {
        OnPropertyChanged(string.Empty);
    }

    public void UpdateShellTarget(string targetPath, ShellItemKind kind)
    {
        if (Item is not ShellItem shellItem)
        {
            return;
        }

        shellItem.TargetPath = targetPath;
        shellItem.Kind = kind;
        shellItem.ThumbnailPngBase64 = string.Empty;
        shellItem.ThumbnailUpdatedAt = null;
        _thumbnail = null;
        RefreshShellState();
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(IsShellDirectory));
        OnPropertyChanged(nameof(ShowPhysicalDirectoryBadge));
        OnPropertyChanged(nameof(ShowFolderTabDirectoryBadge));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(CompactTypeLabel));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(CardSecondaryText));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(ToolTipUrl));
        OnPropertyChanged(nameof(HasToolTipUrl));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(SiteIconText));
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(IsShellMedia));
        OnPropertyChanged(nameof(IsShellVideo));
        OnPropertyChanged(nameof(HasShellMediaThumbnail));
        OnPropertyChanged(nameof(HasCardThumbnail));
        OnPropertyChanged(nameof(ShowShellVideoOverlay));
        OnPropertyChanged(nameof(CanConvertShellTextFileToNote));
    }

    public void RefreshShellState()
    {
        IsShellItemMissing = Item is ShellItem shellItem
            && !(shellItem.Kind == ShellItemKind.Directory
                ? Directory.Exists(shellItem.TargetPath)
                : File.Exists(shellItem.TargetPath));
    }

    public void ApplyCommittedLocation(WorkspaceItem committedItem)
    {
        if (committedItem.Id != Id)
        {
            throw new ArgumentException("The committed item ID does not match.", nameof(committedItem));
        }

        Item.WorkspaceId = committedItem.WorkspaceId;
        Item.ParentFolderId = committedItem.ParentFolderId;
        Item.UpdatedAt = committedItem.UpdatedAt;
        X = committedItem.X;
        Y = committedItem.Y;
    }

    public string PreviewText
    {
        get
        {
            if (Item is SessionGroupItem sessionGroup)
            {
                var titles = sessionGroup.Tabs
                    .Take(3)
                    .Select(static x => x.TitleOrUrl())
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                return titles.Count == 0
                    ? Item.Note
                    : string.Join(" / ", titles);
            }

            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                return string.Join(
                    " / ",
                    playerSet.Players
                        .Take(3)
                        .Select(static player => string.IsNullOrWhiteSpace(player.Title) ? player.Url : player.Title));
            }

            if (!string.IsNullOrWhiteSpace(Item.Url))
            {
                return Item.Url;
            }

            if (Item is ShellItem shellItem)
            {
                return shellItem.TargetPath;
            }

            return Item.Note;
        }
    }

    public string FooterText
    {
        get
        {
            if (Item.Tags.Count > 0)
            {
                return string.Join("  ", Item.Tags.Select(static x => "#" + x));
            }

            return Item is SessionGroupItem
                ? Item.Note
                : string.Empty;
        }
    }

    public bool HasFooterText => !string.IsNullOrWhiteSpace(FooterText);

    public string ToolTipTitle => Type == WorkspaceItemType.Note
            ? "メモ"
            : Title;

    public string ToolTipUrl => Item is ShellItem shellItem ? shellItem.TargetPath : Item.Url;

    public bool HasToolTipUrl => !string.IsNullOrWhiteSpace(ToolTipUrl);

    public string ToolTipNote => Item.Note;

    public bool HasToolTipNote => !string.IsNullOrWhiteSpace(ToolTipNote);

    public string ToolTipTags => Item.Tags.Count == 0
        ? string.Empty
        : string.Join("  ", Item.Tags.Select(static x => "#" + x));

    public bool HasToolTipTags => !string.IsNullOrWhiteSpace(ToolTipTags);

    public string ToolTipSessionTabs
    {
        get
        {
            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                return string.Join(
                    Environment.NewLine,
                    playerSet.Players
                        .Take(12)
                        .Select(static (player, index) =>
                        {
                            var title = string.IsNullOrWhiteSpace(player.Title) ? player.Url : player.Title;
                            var position = FormatPlaybackTime(player.LastPositionSeconds);
                            return string.IsNullOrWhiteSpace(position)
                                ? $"{index + 1}. {title}"
                                : $"{index + 1}. {title} ({position})";
                        }));
            }

            if (Item is not SessionGroupItem sessionGroup)
            {
                return string.Empty;
            }

            var tabs = sessionGroup.Tabs
                .OrderBy(static x => x.SortOrder)
                .Take(12)
                .Select(static x => x.TitleOrUrl())
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Select(static (x, index) => $"{index + 1}. {x}")
                .ToList();
            if (tabs.Count == 0)
            {
                return string.Empty;
            }

            if (sessionGroup.Tabs.Count > tabs.Count)
            {
                tabs.Add($"ほか {sessionGroup.Tabs.Count - tabs.Count} タブ");
            }

            return string.Join(Environment.NewLine, tabs);
        }
    }

    public bool HasToolTipSessionTabs => !string.IsNullOrWhiteSpace(ToolTipSessionTabs);

    public string Subtitle
    {
        get
        {
            if (Item is SessionGroupItem sessionGroup)
            {
                return IsSplitSession ? SplitSessionSummary : $"{sessionGroup.Tabs.Count} tabs";
            }

            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                return $"YouTubeミニプレイヤー {playerSet.Players.Count}件";
            }

            if (!string.IsNullOrWhiteSpace(Item.Url))
            {
                return Item.Url;
            }

            if (Item is ShellItem shellItem)
            {
                return shellItem.TargetPath;
            }

            return Item.Note;
        }
    }

    public string Detail
    {
        get
        {
            if (Item.Tags.Count > 0)
            {
                return string.Join(", ", Item.Tags);
            }

            if (Item is SessionGroupItem sessionGroup)
            {
                return string.Join(", ", sessionGroup.Tabs.Take(3).Select(static x => x.TitleOrUrl()));
            }

            if (Item is YouTubeMiniPlayerSetItem playerSet)
            {
                return string.Join(", ", playerSet.Players.Take(3).Select(static player => player.Title));
            }

            if (Item is ShellItem shellItem)
            {
                return shellItem.TargetPath;
            }

            return Item.Note;
        }
    }

    private static string FormatPlaybackTime(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0)
        {
            return string.Empty;
        }

        var totalSeconds = (int)Math.Floor(value);
        var time = TimeSpan.FromSeconds(totalSeconds);
        return totalSeconds >= 3600
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static string GetShellKindLabel(ShellItem item)
    {
        return item.Kind switch
        {
            ShellItemKind.Directory => item.OpenInFolderTab ? "フォルダタブ" : "PCフォルダー",
            ShellItemKind.Shortcut => "ショートカット",
            ShellItemKind.Application => "アプリ",
            _ => "ファイル"
        };
    }

    private string GetNotePreviewText()
    {
        return Item.Note.Length <= MaximumNoteCardPreviewCharacters
            ? Item.Note
            : string.Concat(
                Item.Note.AsSpan(0, MaximumNoteCardPreviewCharacters).TrimEnd(),
                "…");
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;
        return string.IsNullOrWhiteSpace(normalized)
            ? host
            : normalized;
    }

    private IReadOnlyList<SessionTab> GetPrimarySplitTabs()
    {
        if (Item is not SessionGroupItem sessionGroup)
        {
            return [];
        }

        return sessionGroup.Tabs
            .Where(static tab => Guid.TryParse(tab.SplitGroupId, out _))
            .GroupBy(static tab => tab.SplitGroupId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.ToList())
            .FirstOrDefault(static group => group.Count == 2
                && group.Any(static tab => tab.SplitPane == TabBrowser.Models.SplitViewPane.Left)
                && group.Any(static tab => tab.SplitPane == TabBrowser.Models.SplitViewPane.Right))
            ?? [];
    }

    private string GetPrimaryFaviconPngBase64()
    {
        if (!string.IsNullOrWhiteSpace(Item.FaviconPngBase64))
        {
            return Item.FaviconPngBase64;
        }

        return Item is SessionGroupItem sessionGroup
            ? sessionGroup.Tabs.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x.FaviconPngBase64))?.FaviconPngBase64 ?? string.Empty
            : string.Empty;
    }

    private string? GetPrimaryHost()
    {
        if (TryGetHost(Item.Url) is { } host)
        {
            return host;
        }

        return Item is SessionGroupItem sessionGroup
            ? sessionGroup.Tabs.Select(static x => TryGetHost(x.Url)).FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x))
            : null;
    }

    private static ImageSource? CreateImageSource(string faviconPngBase64, int decodePixelWidth = 28)
    {
        if (string.IsNullOrWhiteSpace(faviconPngBase64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(faviconPngBase64);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static Brush CreateBrush(string seed)
    {
        var hash = 0;
        foreach (var c in seed)
        {
            hash = unchecked((hash * 31) + c);
        }

        var colorText = IconPalette[(hash & int.MaxValue) % IconPalette.Length];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateSolidBrush(string colorText)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
        brush.Freeze();
        return brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static class SessionTabDisplayExtensions
{
    public static string TitleOrUrl(this SessionTab tab)
    {
        return string.IsNullOrWhiteSpace(tab.Title)
            ? tab.Url
            : tab.Title;
    }
}
