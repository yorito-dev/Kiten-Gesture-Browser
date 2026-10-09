using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public interface IWorkspaceRepository
{
    Task<Workspace> CreateWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    Task UpdateWorkspaceNameAsync(Guid workspaceId, string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<WorkspaceItem?> GetItemByIdAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<BookmarkItem> AddBookmarkAsync(BookmarkItem item, CancellationToken cancellationToken = default);

    Task<FolderItem> AddFolderAsync(FolderItem item, CancellationToken cancellationToken = default);

    Task<SessionGroupItem> AddSessionGroupAsync(SessionGroupItem item, CancellationToken cancellationToken = default);

    Task<YouTubeMiniPlayerSetItem> AddYouTubeMiniPlayerSetAsync(YouTubeMiniPlayerSetItem item, CancellationToken cancellationToken = default);

    Task<NoteItem> AddNoteAsync(NoteItem item, CancellationToken cancellationToken = default);

    Task AddItemsAsync(IReadOnlyList<WorkspaceItem> items, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShellItem>> AddShellItemsAsync(
        IReadOnlyList<ShellItem> items,
        CancellationToken cancellationToken = default);

    Task<NoteItem> ConvertShellItemToNoteAsync(
        NoteItem replacement,
        CancellationToken cancellationToken = default);

    Task UpdateItemPositionAsync(Guid itemId, double x, double y, CancellationToken cancellationToken = default);

    Task UpdateItemPositionsAsync(IReadOnlyList<WorkspaceItemPosition> positions, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceItem>> MoveItemsToWorkspaceAsync(
        IReadOnlyList<WorkspaceItemMove> moves,
        CancellationToken cancellationToken = default);

    Task UpdateItemFaviconAsync(Guid itemId, string faviconPngBase64, CancellationToken cancellationToken = default);

    Task UpdateItemFaviconsAsync(
        IReadOnlyDictionary<Guid, string> favicons,
        CancellationToken cancellationToken = default);

    Task UpdateItemThumbnailAsync(Guid itemId, string thumbnailPngBase64, DateTimeOffset thumbnailUpdatedAt, CancellationToken cancellationToken = default);

    Task UpdateItemThumbnailsAsync(
        IReadOnlyDictionary<Guid, (string ThumbnailPngBase64, DateTimeOffset ThumbnailUpdatedAt)> thumbnails,
        CancellationToken cancellationToken = default);

    Task UpdateSessionTabThumbnailsAsync(
        Guid sessionGroupItemId,
        IReadOnlyDictionary<Guid, string> thumbnails,
        CancellationToken cancellationToken = default);

    Task UpdateShellItemTargetAsync(
        Guid itemId,
        string targetPath,
        ShellItemKind kind,
        CancellationToken cancellationToken = default,
        string? expectedTargetPath = null);

    Task RenameItemAsync(Guid itemId, string title, CancellationToken cancellationToken = default);

    Task UpdateNoteAsync(
        Guid itemId,
        string title,
        string content,
        double fontSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task DeleteShellItemForUndoAsync(Guid itemId, Guid workspaceId, string expectedTargetPath,
        ShellItemKind expectedKind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> DeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceItem>> GetDeletedItemsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceItem>> RestoreItemAsync(Guid itemId, Guid workspaceId, Guid? parentFolderId,
        CancellationToken cancellationToken = default);

    Task PermanentlyDeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, DateTimeOffset>? expectedDeletionTimes = null);

    Task<IReadOnlyList<WorkspaceItem>> SearchItemsAsync(string query, CancellationToken cancellationToken = default);
}

public sealed record WorkspaceItemMove(
    Guid ItemId,
    Guid SourceWorkspaceId,
    Guid? SourceParentFolderId,
    Guid TargetWorkspaceId,
    Guid? TargetParentFolderId,
    double X,
    double Y);

/// <summary>更新対象のメモが存在しない場合。書き込み障害や自動新規作成とは区別する。</summary>
public sealed class WorkspaceNoteMissingException() : InvalidOperationException(
    "保存対象のメモが見つかりません。編集内容は残っています。新しいメモとして保存できます。");

public sealed class WorkspaceItemUnavailableException() : InvalidOperationException(
    "対象のWorkspaceカードが見つからないか、削除済み・種類違いのため更新できません。");

public sealed class WorkspaceItemTargetConflictException() : InvalidOperationException(
    "参照先が別の操作で変更されたため、古い状態からは更新できません。");
