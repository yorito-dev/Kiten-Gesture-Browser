using System.Text.Json.Serialization;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Models;

public enum RecentItemCategory
{
    Workspace,
    Note,
    Web,
    File,
    Video,
    Folder
}

public sealed record RecentItem
{
    public string Key { get; set; } = string.Empty;

    public RecentItemCategory Category { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string PreviewText { get; set; } = string.Empty;

    public string ThumbnailPngBase64 { get; set; } = string.Empty;

    // 自動プレビューの由来。nullは旧データ等で文脈が不明な状態を表す。
    public string? ContainerProfileId { get; set; }

    public int ThumbnailCacheVersion { get; set; }

    public string FaviconPngBase64 { get; set; } = string.Empty;

    public Guid? WorkspaceId { get; set; }

    public Guid? WorkspaceItemId { get; set; }

    public Guid? ParentFolderId { get; set; }

    public string WorkspaceName { get; set; } = string.Empty;

    public WorkspaceItemType? WorkspaceItemType { get; set; }

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    public bool IsPinned { get; set; }

    [JsonIgnore]
    public bool IsNote => Category == RecentItemCategory.Note;

    [JsonIgnore]
    public bool IsWorkspaceItem => WorkspaceId is not null && WorkspaceItemId is not null;

    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        RecentItemCategory.Note => "メモ",
        RecentItemCategory.Web => "Web",
        RecentItemCategory.File => "ファイル",
        RecentItemCategory.Video => "動画",
        RecentItemCategory.Folder => "フォルダー",
        _ => "Workspace"
    };

    [JsonIgnore]
    public string SecondaryText
    {
        get
        {
            if (IsNote && !string.IsNullOrWhiteSpace(PreviewText))
            {
                return PreviewText.Replace("\r", " ").Replace("\n", " ").Trim();
            }

            if (!string.IsNullOrWhiteSpace(WorkspaceName))
            {
                return WorkspaceName;
            }

            return Target;
        }
    }

    [JsonIgnore]
    public string PinMenuLabel => IsPinned ? "ピン留めを解除" : "ピン留め";

    [JsonIgnore]
    public string CopyText => !string.IsNullOrWhiteSpace(PreviewText)
        ? PreviewText
        : Target;
}
