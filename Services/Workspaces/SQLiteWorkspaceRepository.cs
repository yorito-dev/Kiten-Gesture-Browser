using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public sealed class SQLiteWorkspaceRepository : IWorkspaceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const int SchemaVersion = 2;

    private readonly string _databasePath;
    private readonly bool _runIntegrityCheck;
    private readonly Action? _beforeContainerMigrationCommit;
    private readonly Action? _beforeMigrationBackup;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SQLiteWorkspaceRepository()
        : this(AppPaths.WorkspaceDatabaseFile, ApplicationStabilityService.PreviousRunEndedUnexpectedly)
    {
    }

    public SQLiteWorkspaceRepository(string databasePath)
        : this(databasePath, runIntegrityCheck: false)
    {
    }

    public SQLiteWorkspaceRepository(string databasePath, bool runIntegrityCheck)
        : this(databasePath, runIntegrityCheck, beforeContainerMigrationCommit: null)
    {
    }

    internal SQLiteWorkspaceRepository(string databasePath, bool runIntegrityCheck, Action? beforeContainerMigrationCommit,
        Action? beforeMigrationBackup = null)
    {
        _databasePath = databasePath;
        _runIntegrityCheck = runIntegrityCheck;
        _beforeContainerMigrationCommit = beforeContainerMigrationCommit;
        _beforeMigrationBackup = beforeMigrationBackup;
    }

    public bool IntegrityCheckWasRun { get; private set; }

    internal string? LastMigrationBackupPath { get; private set; }

    internal Action? BeforeSchemaMigrationCommit { get; init; }

    public Task<Workspace> CreateWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateWorkspaceCoreAsync(workspace, cancellationToken), cancellationToken);

    private async Task<Workspace> CreateWorkspaceCoreAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        NormalizeWorkspace(workspace);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspaces (id, name, sort_order, created_at, updated_at)
            VALUES ($id, $name, $sortOrder, $createdAt, $updatedAt);
            """;
        AddParameter(command, "$id", workspace.Id);
        AddParameter(command, "$name", workspace.Name);
        AddParameter(command, "$sortOrder", workspace.Index);
        AddParameter(command, "$createdAt", workspace.CreatedAt);
        AddParameter(command, "$updatedAt", workspace.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return workspace;
    }

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetWorkspacesCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<Workspace>> GetWorkspacesCoreAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, sort_order, created_at, updated_at
            FROM workspaces
            ORDER BY sort_order, name;
            """;

        var result = new List<Workspace>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadWorkspace(reader));
        }

        return result;
    }

    public Task UpdateWorkspaceNameAsync(
        Guid workspaceId,
        string name,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateWorkspaceNameCoreAsync(workspaceId, name, cancellationToken), cancellationToken);

    private async Task UpdateWorkspaceNameCoreAsync(
        Guid workspaceId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeText(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Workspace名を入力してください。", nameof(name));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspaces
            SET name = $name,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddParameter(command, "$name", normalizedName);
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", workspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.Run(() => GetWorkspaceItemsCoreAsync(workspaceId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsCoreAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadItemsAsync(
            connection,
            "WHERE workspace_id = $workspaceId",
            command => AddParameter(command, "$workspaceId", workspaceId),
            cancellationToken);
    }

    public Task<WorkspaceItem?> GetItemByIdAsync(Guid itemId, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var items = await ReadItemsAsync(connection, "WHERE i.id = $id",
                command => AddParameter(command, "$id", itemId), cancellationToken, includeDeleted: true);
            return items.SingleOrDefault();
        }, cancellationToken);

    public Task<BookmarkItem> AddBookmarkAsync(BookmarkItem item, CancellationToken cancellationToken = default)
        => Task.Run(() => AddBookmarkCoreAsync(item, cancellationToken), cancellationToken);

    private async Task<BookmarkItem> AddBookmarkCoreAsync(BookmarkItem item, CancellationToken cancellationToken = default)
    {
        await AddItemAsync(item, cancellationToken);
        return item;
    }

    public Task<FolderItem> AddFolderAsync(FolderItem item, CancellationToken cancellationToken = default)
        => Task.Run(() => AddFolderCoreAsync(item, cancellationToken), cancellationToken);

    private async Task<FolderItem> AddFolderCoreAsync(FolderItem item, CancellationToken cancellationToken = default)
    {
        await AddItemAsync(item, cancellationToken);
        return item;
    }

    public Task<SessionGroupItem> AddSessionGroupAsync(SessionGroupItem item, CancellationToken cancellationToken = default)
        => Task.Run(() => AddSessionGroupCoreAsync(item, cancellationToken), cancellationToken);

    private async Task<SessionGroupItem> AddSessionGroupCoreAsync(SessionGroupItem item, CancellationToken cancellationToken = default)
    {
        await AddItemAsync(item, cancellationToken);
        return item;
    }

    public Task<YouTubeMiniPlayerSetItem> AddYouTubeMiniPlayerSetAsync(
        YouTubeMiniPlayerSetItem item,
        CancellationToken cancellationToken = default)
        => Task.Run(() => AddYouTubeMiniPlayerSetCoreAsync(item, cancellationToken), cancellationToken);

    private async Task<YouTubeMiniPlayerSetItem> AddYouTubeMiniPlayerSetCoreAsync(
        YouTubeMiniPlayerSetItem item,
        CancellationToken cancellationToken = default)
    {
        await AddItemAsync(item, cancellationToken);
        return item;
    }

    public Task<NoteItem> AddNoteAsync(NoteItem item, CancellationToken cancellationToken = default)
        => Task.Run(() => AddNoteCoreAsync(item, cancellationToken), cancellationToken);

    private async Task<NoteItem> AddNoteCoreAsync(NoteItem item, CancellationToken cancellationToken = default)
    {
        await AddItemAsync(item, cancellationToken);
        return item;
    }

    public Task<IReadOnlyList<ShellItem>> AddShellItemsAsync(
        IReadOnlyList<ShellItem> items,
        CancellationToken cancellationToken = default)
        => Task.Run(() => AddShellItemsCoreAsync(items, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<ShellItem>> AddShellItemsCoreAsync(
        IReadOnlyList<ShellItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return [];
        }

        foreach (var item in items)
        {
            NormalizeItem(item);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var item in items)
            {
                await InsertItemAsync(connection, transaction, item, cancellationToken);
                await ReplaceTagsAsync(connection, transaction, item.Id, item.Tags, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return items;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task<NoteItem> ConvertShellItemToNoteAsync(
        NoteItem replacement,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ConvertShellItemToNoteCoreAsync(replacement, cancellationToken), cancellationToken);

    private async Task<NoteItem> ConvertShellItemToNoteCoreAsync(
        NoteItem replacement,
        CancellationToken cancellationToken = default)
    {
        NormalizeItem(replacement);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET type = $noteType,
                title = $title,
                url = '',
                note = $note,
                note_font_size = $noteFontSize,
                favicon_png_base64 = '',
                thumbnail_png_base64 = '',
                thumbnail_updated_at = NULL,
                youtube_mini_player_state_json = '',
                target_path = '',
                shell_item_kind = '',
                open_in_folder_tab = 0,
                container_profile_id = NULL,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL
              AND type = $shellItemType;
            """;
        AddParameter(command, "$noteType", WorkspaceItemType.Note.ToString());
        AddParameter(command, "$title", replacement.Title);
        AddParameter(command, "$note", replacement.Note);
        AddParameter(command, "$noteFontSize", replacement.FontSize);
        AddParameter(command, "$updatedAt", replacement.UpdatedAt);
        AddParameter(command, "$id", replacement.Id);
        AddParameter(command, "$shellItemType", WorkspaceItemType.ShellItem.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("変換するテキストファイルカードが見つかりません。");
        }

        return replacement;
    }

    public Task UpdateItemPositionAsync(Guid itemId, double x, double y, CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemPositionCoreAsync(itemId, x, y, cancellationToken), cancellationToken);

    private async Task UpdateItemPositionCoreAsync(Guid itemId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET x = $x,
                y = $y,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        AddParameter(command, "$x", NormalizeNumber(x));
        AddParameter(command, "$y", NormalizeNumber(y));
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpdateItemPositionsAsync(
        IReadOnlyList<WorkspaceItemPosition> positions,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemPositionsCoreAsync(positions, cancellationToken), cancellationToken);

    private async Task UpdateItemPositionsCoreAsync(
        IReadOnlyList<WorkspaceItemPosition> positions,
        CancellationToken cancellationToken = default)
    {
        if (positions.Count == 0)
        {
            return;
        }

        if (positions.Select(static position => position.ItemId).Distinct().Count() != positions.Count)
        {
            throw new InvalidOperationException("同じWorkspaceカードの位置が重複して指定されています。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now;
            foreach (var position in positions)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE workspace_items
                    SET x = $x,
                        y = $y,
                        updated_at = $updatedAt
                    WHERE id = $id AND deleted_at IS NULL;
                    """;
                AddParameter(command, "$x", NormalizeNumber(position.X));
                AddParameter(command, "$y", NormalizeNumber(position.Y));
                AddParameter(command, "$updatedAt", updatedAt);
                AddParameter(command, "$id", position.ItemId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException($"Workspaceカードの位置を更新できませんでした: {position.ItemId}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task<IReadOnlyList<WorkspaceItem>> MoveItemsToWorkspaceAsync(
        IReadOnlyList<WorkspaceItemMove> moves,
        CancellationToken cancellationToken = default)
        => Task.Run(() => MoveItemsToWorkspaceCoreAsync(moves, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WorkspaceItem>> MoveItemsToWorkspaceCoreAsync(
        IReadOnlyList<WorkspaceItemMove> moves,
        CancellationToken cancellationToken = default)
    {
        if (moves.Count == 0)
        {
            return [];
        }

        var distinctMoves = moves.DistinctBy(static move => move.ItemId).ToList();
        if (distinctMoves.Count != moves.Count)
        {
            throw new ArgumentException("Duplicate item moves are not allowed.", nameof(moves));
        }

        if (distinctMoves.Any(static move => move.ItemId == Guid.Empty
            || move.SourceWorkspaceId == Guid.Empty
            || move.TargetWorkspaceId == Guid.Empty))
        {
            throw new ArgumentException("Item and workspace IDs are required.", nameof(moves));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            var currentRows = await ReadMoveRowsAsync(
                connection,
                transaction,
                distinctMoves.Select(static move => move.ItemId).ToList(),
                cancellationToken);
            ValidateMoveSources(distinctMoves, currentRows);
            await ValidateMoveTargetsAsync(
                connection,
                transaction,
                distinctMoves,
                currentRows,
                cancellationToken);

            var updatedAt = DateTimeOffset.Now;
            foreach (var move in distinctMoves)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE workspace_items
                    SET workspace_id = $workspaceId,
                        parent_folder_id = $parentFolderId,
                        x = $x,
                        y = $y,
                        updated_at = $updatedAt
                    WHERE id = $id AND deleted_at IS NULL
                      AND workspace_id = $sourceWorkspaceId
                      AND (($sourceParentFolderId IS NULL AND parent_folder_id IS NULL)
                           OR parent_folder_id = $sourceParentFolderId);
                    """;
                AddParameter(command, "$workspaceId", move.TargetWorkspaceId);
                AddParameter(command, "$parentFolderId", move.TargetParentFolderId);
                AddParameter(command, "$x", NormalizeNumber(move.X));
                AddParameter(command, "$y", NormalizeNumber(move.Y));
                AddParameter(command, "$updatedAt", updatedAt);
                AddParameter(command, "$id", move.ItemId);
                AddParameter(command, "$sourceWorkspaceId", move.SourceWorkspaceId);
                AddParameter(command, "$sourceParentFolderId", move.SourceParentFolderId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException($"Workspace item move conflicted: {move.ItemId}");
                }
            }

            var movedItems = await ReadItemsByIdsAsync(
                connection,
                transaction,
                distinctMoves.Select(static move => move.ItemId).ToList(),
                cancellationToken);
            if (movedItems.Count != distinctMoves.Count)
            {
                throw new InvalidOperationException("Not all moved workspace items could be read back.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return movedItems;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void ValidateMoveSources(
        IReadOnlyList<WorkspaceItemMove> moves,
        IReadOnlyDictionary<Guid, WorkspaceMoveRow> currentRows)
    {
        if (currentRows.Count != moves.Count)
        {
            throw new InvalidOperationException("One or more workspace items no longer exist.");
        }

        foreach (var move in moves)
        {
            if (!currentRows.TryGetValue(move.ItemId, out var row)
                || row.WorkspaceId != move.SourceWorkspaceId
                || row.ParentFolderId != move.SourceParentFolderId)
            {
                throw new InvalidOperationException($"Workspace item location changed before move: {move.ItemId}");
            }
        }
    }

    private static async Task ValidateMoveTargetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WorkspaceItemMove> moves,
        IReadOnlyDictionary<Guid, WorkspaceMoveRow> currentRows,
        CancellationToken cancellationToken)
    {
        foreach (var workspaceId in moves.Select(static move => move.TargetWorkspaceId).Distinct())
        {
            await using var workspaceCommand = connection.CreateCommand();
            workspaceCommand.Transaction = transaction;
            workspaceCommand.CommandText = "SELECT COUNT(*) FROM workspaces WHERE id = $id;";
            AddParameter(workspaceCommand, "$id", workspaceId);
            if (Convert.ToInt32(await workspaceCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException($"Target workspace no longer exists: {workspaceId}");
            }
        }

        var movesById = moves.ToDictionary(static move => move.ItemId);
        foreach (var target in moves
                     .Where(static move => move.TargetParentFolderId is not null)
                     .Select(static move => (move.TargetWorkspaceId, FolderId: move.TargetParentFolderId!.Value))
                     .Distinct())
        {
            if (movesById.TryGetValue(target.FolderId, out var folderMove))
            {
                if (!currentRows.TryGetValue(target.FolderId, out var folderRow)
                    || folderRow.Type != WorkspaceItemType.Folder
                    || folderMove.TargetWorkspaceId != target.TargetWorkspaceId)
                {
                    throw new InvalidOperationException($"Invalid moved target folder: {target.FolderId}");
                }

                continue;
            }

            await using var folderCommand = connection.CreateCommand();
            folderCommand.Transaction = transaction;
            folderCommand.CommandText = """
                SELECT COUNT(*)
                FROM workspace_items
                WHERE id = $id AND deleted_at IS NULL
                  AND workspace_id = $workspaceId
                  AND type = $type;
                """;
            AddParameter(folderCommand, "$id", target.FolderId);
            AddParameter(folderCommand, "$workspaceId", target.TargetWorkspaceId);
            AddParameter(folderCommand, "$type", WorkspaceItemType.Folder.ToString());
            if (Convert.ToInt32(await folderCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException($"Target folder no longer exists: {target.FolderId}");
            }
        }
    }

    private async Task<IReadOnlyList<WorkspaceItem>> ReadItemsByIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        var parameterNames = itemIds.Select((_, index) => $"$itemId{index}").ToList();
        return await ReadItemsAsync(
            connection,
            $"WHERE i.id IN ({string.Join(", ", parameterNames)})",
            command =>
            {
                for (var index = 0; index < itemIds.Count; index++)
                {
                    AddParameter(command, parameterNames[index], itemIds[index]);
                }
            },
            cancellationToken,
            transaction);
    }

    private static async Task<IReadOnlyDictionary<Guid, WorkspaceMoveRow>> ReadMoveRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        var parameterNames = itemIds.Select((_, index) => $"$itemId{index}").ToList();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, workspace_id, parent_folder_id, type
            FROM workspace_items
            WHERE deleted_at IS NULL
              AND id IN ({string.Join(", ", parameterNames)});
            """;
        for (var index = 0; index < itemIds.Count; index++)
        {
            AddParameter(command, parameterNames[index], itemIds[index]);
        }

        var result = new Dictionary<Guid, WorkspaceMoveRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = Enum.TryParse<WorkspaceItemType>(reader.GetString(3), out var parsedType)
                ? parsedType
                : WorkspaceItemType.Note;
            var row = new WorkspaceMoveRow(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                ReadNullableGuid(reader, 2),
                type);
            result.Add(row.Id, row);
        }

        return result;
    }

    public Task UpdateItemFaviconAsync(Guid itemId, string faviconPngBase64, CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemFaviconCoreAsync(itemId, faviconPngBase64, cancellationToken), cancellationToken);

    private async Task UpdateItemFaviconCoreAsync(Guid itemId, string faviconPngBase64, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET favicon_png_base64 = $faviconPngBase64,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        AddParameter(command, "$faviconPngBase64", NormalizeBase64Text(faviconPngBase64));
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpdateItemFaviconsAsync(
        IReadOnlyDictionary<Guid, string> favicons,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemFaviconsCoreAsync(favicons, cancellationToken), cancellationToken);

    private async Task UpdateItemFaviconsCoreAsync(
        IReadOnlyDictionary<Guid, string> favicons,
        CancellationToken cancellationToken = default)
    {
        if (favicons.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now;
            foreach (var pair in favicons)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE workspace_items
                    SET favicon_png_base64 = $faviconPngBase64,
                        updated_at = $updatedAt
                    WHERE id = $id AND deleted_at IS NULL;
                    """;
                AddParameter(command, "$faviconPngBase64", NormalizeBase64Text(pair.Value));
                AddParameter(command, "$updatedAt", updatedAt);
                AddParameter(command, "$id", pair.Key);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task UpdateItemThumbnailAsync(Guid itemId, string thumbnailPngBase64, DateTimeOffset thumbnailUpdatedAt, CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemThumbnailCoreAsync(itemId, thumbnailPngBase64, thumbnailUpdatedAt, cancellationToken), cancellationToken);

    private async Task UpdateItemThumbnailCoreAsync(Guid itemId, string thumbnailPngBase64, DateTimeOffset thumbnailUpdatedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET thumbnail_png_base64 = $thumbnailPngBase64,
                thumbnail_updated_at = $thumbnailUpdatedAt,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        AddParameter(command, "$thumbnailPngBase64", NormalizeBase64Text(thumbnailPngBase64));
        AddParameter(command, "$thumbnailUpdatedAt", thumbnailUpdatedAt);
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpdateItemThumbnailsAsync(
        IReadOnlyDictionary<Guid, (string ThumbnailPngBase64, DateTimeOffset ThumbnailUpdatedAt)> thumbnails,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateItemThumbnailsCoreAsync(thumbnails, cancellationToken), cancellationToken);

    private async Task UpdateItemThumbnailsCoreAsync(
        IReadOnlyDictionary<Guid, (string ThumbnailPngBase64, DateTimeOffset ThumbnailUpdatedAt)> thumbnails,
        CancellationToken cancellationToken = default)
    {
        if (thumbnails.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now;
            foreach (var pair in thumbnails)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE workspace_items
                    SET thumbnail_png_base64 = $thumbnailPngBase64,
                        thumbnail_updated_at = $thumbnailUpdatedAt,
                        updated_at = $updatedAt
                    WHERE id = $id AND deleted_at IS NULL;
                    """;
                AddParameter(command, "$thumbnailPngBase64", NormalizeBase64Text(pair.Value.ThumbnailPngBase64));
                AddParameter(command, "$thumbnailUpdatedAt", pair.Value.ThumbnailUpdatedAt);
                AddParameter(command, "$updatedAt", updatedAt);
                AddParameter(command, "$id", pair.Key);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task UpdateSessionTabThumbnailsAsync(
        Guid sessionGroupItemId,
        IReadOnlyDictionary<Guid, string> thumbnails,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateSessionTabThumbnailsCoreAsync(sessionGroupItemId, thumbnails, cancellationToken), cancellationToken);

    private async Task UpdateSessionTabThumbnailsCoreAsync(
        Guid sessionGroupItemId,
        IReadOnlyDictionary<Guid, string> thumbnails,
        CancellationToken cancellationToken = default)
    {
        if (sessionGroupItemId == Guid.Empty || thumbnails.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var thumbnail in thumbnails)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE workspace_session_tabs
                    SET thumbnail_png_base64 = $thumbnailPngBase64
                    WHERE id = $id AND session_group_item_id = $sessionGroupItemId
                      AND EXISTS (SELECT 1 FROM workspace_items WHERE id = $sessionGroupItemId AND deleted_at IS NULL);
                    """;
                AddParameter(command, "$thumbnailPngBase64", NormalizeBase64Text(thumbnail.Value));
                AddParameter(command, "$id", thumbnail.Key);
                AddParameter(command, "$sessionGroupItemId", sessionGroupItemId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task UpdateShellItemTargetAsync(
        Guid itemId,
        string targetPath,
        ShellItemKind kind,
        CancellationToken cancellationToken = default,
        string? expectedTargetPath = null)
        => Task.Run(() => UpdateShellItemTargetCoreAsync(itemId, targetPath, kind, cancellationToken, expectedTargetPath), cancellationToken);

    private async Task UpdateShellItemTargetCoreAsync(
        Guid itemId,
        string targetPath,
        ShellItemKind kind,
        CancellationToken cancellationToken = default,
        string? expectedTargetPath = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET target_path = $targetPath,
                shell_item_kind = $shellItemKind,
                favicon_png_base64 = '',
                thumbnail_png_base64 = '',
                thumbnail_updated_at = NULL,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL
              AND type = $type
              AND ($expectedTargetPath IS NULL OR target_path = $expectedTargetPath);
            """;
        AddParameter(command, "$targetPath", NormalizeShellPath(targetPath));
        AddParameter(command, "$shellItemKind", kind.ToString());
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        AddParameter(command, "$type", WorkspaceItemType.ShellItem.ToString());
        AddParameter(command, "$expectedTargetPath", expectedTargetPath is null ? DBNull.Value : NormalizeShellPath(expectedTargetPath));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return;

        if (expectedTargetPath is not null)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM workspace_items WHERE id = $id AND deleted_at IS NULL AND type = $type;";
            AddParameter(check, "$id", itemId);
            AddParameter(check, "$type", WorkspaceItemType.ShellItem.ToString());
            if (await check.ExecuteScalarAsync(cancellationToken) is not null)
                throw new WorkspaceItemTargetConflictException();
        }

        throw new WorkspaceItemUnavailableException();
    }

    public Task RenameItemAsync(Guid itemId, string title, CancellationToken cancellationToken = default)
        => Task.Run(() => RenameItemCoreAsync(itemId, title, cancellationToken), cancellationToken);

    private async Task RenameItemCoreAsync(Guid itemId, string title, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_items
            SET title = $title,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        AddParameter(command, "$title", NormalizeText(title));
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new WorkspaceItemUnavailableException();
    }

    public Task UpdateNoteAsync(
        Guid itemId,
        string title,
        string content,
        double fontSize,
        CancellationToken cancellationToken = default)
        => Task.Run(() => UpdateNoteCoreAsync(itemId, title, content, fontSize, cancellationToken), cancellationToken);

    private async Task UpdateNoteCoreAsync(
        Guid itemId,
        string title,
        string content,
        double fontSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workspace_items
            SET title = $title,
                note = $note,
                note_font_size = $noteFontSize,
                updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL
              AND type = $type;
            """;
        AddParameter(command, "$title", NormalizeText(title));
        AddParameter(command, "$note", NormalizeText(content));
        AddParameter(command, "$noteFontSize", WorkspaceNoteFontSizeProfile.Normalize(fontSize));
        AddParameter(command, "$updatedAt", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        AddParameter(command, "$type", WorkspaceItemType.Note.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new WorkspaceNoteMissingException();
        }

        // 同一内容の再保存もSQLiteでは一致した1行として数えられる。確定前には成功を返さない。
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public Task<IReadOnlyList<Guid>> DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default)
        => DeleteItemsAsync([itemId], cancellationToken);

    public Task DeleteShellItemForUndoAsync(Guid itemId, Guid workspaceId, string expectedTargetPath,
        ShellItemKind expectedKind, CancellationToken cancellationToken = default)
        => Task.Run(() => DeleteShellItemForUndoCoreAsync(itemId, workspaceId, expectedTargetPath,
            expectedKind, cancellationToken), cancellationToken);

    private async Task DeleteShellItemForUndoCoreAsync(Guid itemId, Guid workspaceId, string expectedTargetPath,
        ShellItemKind expectedKind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workspace_items
            SET deleted_at = $at, deletion_root_id = $id
            WHERE id = $id AND workspace_id = $workspaceId AND deleted_at IS NULL
              AND type = $type AND target_path = $targetPath AND shell_item_kind = $kind;
            """;
        AddParameter(command, "$at", DateTimeOffset.Now);
        AddParameter(command, "$id", itemId);
        AddParameter(command, "$workspaceId", workspaceId);
        AddParameter(command, "$type", WorkspaceItemType.ShellItem.ToString());
        AddParameter(command, "$targetPath", NormalizeShellPath(expectedTargetPath));
        AddParameter(command, "$kind", expectedKind.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT deleted_at FROM workspace_items WHERE id = $id;";
            AddParameter(check, "$id", itemId);
            var found = false;
            var deleted = false;
            using (var reader = await check.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    found = true;
                    deleted = !reader.IsDBNull(0);
                }
            }

            if (!found || deleted) throw new WorkspaceItemUnavailableException();
            throw new WorkspaceItemTargetConflictException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public Task<IReadOnlyList<Guid>> DeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        return Task.Run(() => DeleteItemsCoreAsync(ids, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> DeleteItemsCoreAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var rows = await ReadDeletionRowsAsync(connection, transaction, cancellationToken);
        var requested = itemIds.Where(id => rows.TryGetValue(id, out var row) && row.DeletedAt is null).ToHashSet();
        var roots = requested.Where(id =>
        {
            var visited = new HashSet<Guid> { id };
            var parent = rows[id].ParentFolderId;
            while (parent is { } parentId && rows.TryGetValue(parentId, out var parentRow))
            {
                if (!visited.Add(parentId)) throw new InvalidDataException("フォルダーの所属が循環しています。削除を中止しました。");
                if (requested.Contains(parentId)) return false;
                parent = parentRow.ParentFolderId;
            }
            return true;
        }).ToList();
        var deleted = new List<Guid>();
        var deletedAt = DateTimeOffset.Now;
        foreach (var root in roots)
        {
            foreach (var id in CollectSubtree(rows, root, row => row.DeletedAt is null))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE workspace_items SET deleted_at = $at, deletion_root_id = $root WHERE id = $id AND deleted_at IS NULL;";
                AddParameter(command, "$at", deletedAt);
                AddParameter(command, "$root", root);
                AddParameter(command, "$id", id);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("削除対象が変わりました。ごみ箱への移動を中止しました。");
                deleted.Add(id);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return deleted;
    }

    public Task<IReadOnlyList<WorkspaceItem>> GetDeletedItemsAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await ReadItemsAsync(connection, "WHERE i.deleted_at IS NOT NULL", _ => { },
                cancellationToken, includeDeleted: true);
        }, cancellationToken);

    public Task<IReadOnlyList<WorkspaceItem>> RestoreItemAsync(Guid itemId, Guid workspaceId, Guid? parentFolderId,
        CancellationToken cancellationToken = default)
        => Task.Run(() => RestoreItemCoreAsync(itemId, workspaceId, parentFolderId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WorkspaceItem>> RestoreItemCoreAsync(Guid itemId, Guid workspaceId, Guid? parentFolderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var rows = await ReadDeletionRowsAsync(connection, transaction, cancellationToken);
        if (!rows.TryGetValue(itemId, out var root) || root.DeletedAt is null) return [];
        await ValidateActiveDestinationAsync(connection, transaction, workspaceId, parentFolderId, cancellationToken);
        var ids = CollectSubtree(rows, itemId, row => row.DeletedAt is not null && row.DeletionRootId == root.DeletionRootId);
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE workspace_items SET deleted_at = NULL, deletion_root_id = NULL,
                    workspace_id = $workspace, parent_folder_id = $parent
                WHERE id = $id AND deleted_at IS NOT NULL;
                """;
            AddParameter(command, "$workspace", workspaceId);
            AddParameter(command, "$parent", id == itemId ? parentFolderId : rows[id].ParentFolderId);
            AddParameter(command, "$id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("復元対象が変わりました。復元を中止しました。");
        }
        var result = await ReadItemsByIdsAsync(connection, transaction, ids, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    public Task PermanentlyDeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, DateTimeOffset>? expectedDeletionTimes = null)
    {
        var ids = itemIds.Distinct().ToArray();
        var confirmedTimes = expectedDeletionTimes?.ToDictionary(x => x.Key, x => x.Value);
        return Task.Run(async () =>
        {
            if (ids.Length == 0) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var rows = await ReadDeletionRowsAsync(connection, transaction, cancellationToken);
            var selected = ids.ToHashSet();
            if (ids.Any(id => !rows.TryGetValue(id, out var row) || row.DeletedAt is null)
                || (confirmedTimes is not null && ids.Any(id => !confirmedTimes.TryGetValue(id, out var at) || rows[id].DeletedAt != at))
                || rows.Values.Any(row => row.ParentFolderId is { } parent && selected.Contains(parent) && !selected.Contains(row.Id)))
            {
                throw new InvalidOperationException("完全削除の対象または配下が変わりました。対象と件数を確認し直してください。");
            }
            foreach (var id in ids)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM workspace_items WHERE id = $id AND deleted_at IS NOT NULL;";
                AddParameter(command, "$id", id);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("完全削除の対象が変わりました。処理を中止しました。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }, cancellationToken);
    }

    private static async Task<Dictionary<Guid, DeletionRow>> ReadDeletionRowsAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, workspace_id, parent_folder_id, deleted_at, deletion_root_id FROM workspace_items;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new Dictionary<Guid, DeletionRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new DeletionRow(ReadGuid(reader, 0), ReadGuid(reader, 1), ReadNullableGuid(reader, 2),
                ReadNullableDateTimeOffset(reader, 3), ReadNullableGuid(reader, 4));
            rows.Add(row.Id, row);
        }
        return rows;
    }

    private static List<Guid> CollectSubtree(IReadOnlyDictionary<Guid, DeletionRow> rows, Guid root,
        Func<DeletionRow, bool> include)
    {
        var children = rows.Values.Where(row => row.ParentFolderId is not null).ToLookup(row => row.ParentFolderId!.Value);
        var result = new List<Guid>();
        var pending = new Queue<Guid>();
        var visited = new HashSet<Guid>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !rows.TryGetValue(id, out var row) || !include(row)) continue;
            result.Add(id);
            foreach (var child in children[id]) pending.Enqueue(child.Id);
        }
        return result;
    }

    private static async Task ValidateActiveDestinationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid? parentFolderId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = parentFolderId is null
            ? "SELECT COUNT(*) FROM workspaces WHERE id = $workspace;"
            : "SELECT COUNT(*) FROM workspace_items WHERE id = $parent AND workspace_id = $workspace AND type = 'Folder' AND deleted_at IS NULL;";
        AddParameter(command, "$workspace", workspaceId);
        if (parentFolderId is not null) AddParameter(command, "$parent", parentFolderId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("保存・復元先が存在しないか、ごみ箱へ移動されています。復元先を選び直してください。");
    }

    private sealed record DeletionRow(Guid Id, Guid WorkspaceId, Guid? ParentFolderId,
        DateTimeOffset? DeletedAt, Guid? DeletionRootId);

    public Task<IReadOnlyList<WorkspaceItem>> SearchItemsAsync(string query, CancellationToken cancellationToken = default)
        => Task.Run(() => SearchItemsCoreAsync(query, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<WorkspaceItem>> SearchItemsCoreAsync(string query, CancellationToken cancellationToken = default)
    {
        var value = NormalizeText(query);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var items = await ReadItemsAsync(
            connection,
            """
            WHERE i.title LIKE $query COLLATE NOCASE
               OR i.url LIKE $query COLLATE NOCASE
               OR i.note LIKE $query COLLATE NOCASE
               OR i.target_path LIKE $query COLLATE NOCASE
               OR i.youtube_mini_player_state_json LIKE $query COLLATE NOCASE
               OR EXISTS (
                    SELECT 1
                    FROM workspace_item_tags tags
                    WHERE tags.item_id = i.id
                      AND tags.tag LIKE $query COLLATE NOCASE
               )
               OR EXISTS (
                    SELECT 1
                    FROM workspace_session_tabs tabs
                    WHERE tabs.session_group_item_id = i.id
                      AND (tabs.title LIKE $query COLLATE NOCASE OR tabs.url LIKE $query COLLATE NOCASE)
               )
            """,
            command => AddParameter(command, "$query", "%" + value + "%"),
            cancellationToken);

        return items;
    }

    public Task AddItemsAsync(IReadOnlyList<WorkspaceItem> items, CancellationToken cancellationToken = default)
    {
        var capturedItems = items.ToArray();
        return Task.Run(() => AddItemsCoreAsync(capturedItems, cancellationToken), cancellationToken);
    }

    private Task AddItemAsync(WorkspaceItem item, CancellationToken cancellationToken)
        => AddItemsCoreAsync([item], cancellationToken);

    private async Task AddItemsCoreAsync(IReadOnlyList<WorkspaceItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        foreach (var item in items) NormalizeItem(item);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var item in items)
            {
                await InsertItemAsync(connection, transaction, item, cancellationToken);
                await ReplaceTagsAsync(connection, transaction, item.Id, item.Tags, cancellationToken);
                if (item is SessionGroupItem sessionGroup)
                    await ReplaceSessionTabsAsync(connection, transaction, sessionGroup.Id, sessionGroup.Tabs, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceItem item,
        CancellationToken cancellationToken)
    {
        await ValidateActiveDestinationAsync(connection, transaction, item.WorkspaceId, item.ParentFolderId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_items (
                id,
                workspace_id,
                parent_folder_id,
                type,
                title,
                url,
                note,
                note_font_size,
                favicon_png_base64,
                thumbnail_png_base64,
                thumbnail_updated_at,
                youtube_mini_player_state_json,
                target_path,
                shell_item_kind,
                open_in_folder_tab,
                x,
                y,
                width,
                height,
                z_index,
                created_at,
                updated_at,
                container_profile_id)
            VALUES (
                $id,
                $workspaceId,
                $parentFolderId,
                $type,
                $title,
                $url,
                $note,
                $noteFontSize,
                $faviconPngBase64,
                $thumbnailPngBase64,
                $thumbnailUpdatedAt,
                $youTubeMiniPlayerStateJson,
                $targetPath,
                $shellItemKind,
                $openInFolderTab,
                $x,
                $y,
                $width,
                $height,
                $zIndex,
                $createdAt,
                $updatedAt,
                $containerProfileId);
            """;

        AddParameter(command, "$id", item.Id);
        AddParameter(command, "$workspaceId", item.WorkspaceId);
        AddParameter(command, "$parentFolderId", item.ParentFolderId);
        AddParameter(command, "$type", item.Type.ToString());
        AddParameter(command, "$title", item.Title);
        AddParameter(command, "$url", item.Url);
        AddParameter(command, "$containerProfileId", item.ContainerProfileId);
        AddParameter(command, "$note", item.Note);
        AddParameter(
            command,
            "$noteFontSize",
            item is NoteItem noteItem
                ? noteItem.FontSize
                : WorkspaceNoteFontSizeProfile.DefaultFontSize);
        AddParameter(command, "$faviconPngBase64", item.FaviconPngBase64);
        AddParameter(command, "$thumbnailPngBase64", item.ThumbnailPngBase64);
        AddParameter(command, "$thumbnailUpdatedAt", item.ThumbnailUpdatedAt);
        AddParameter(command, "$youTubeMiniPlayerStateJson", SerializeYouTubeMiniPlayerStates(item));
        AddParameter(command, "$targetPath", item is ShellItem shellItem ? shellItem.TargetPath : string.Empty);
        AddParameter(command, "$shellItemKind", item is ShellItem typedShellItem ? typedShellItem.Kind.ToString() : string.Empty);
        AddParameter(command, "$openInFolderTab", item is ShellItem { OpenInFolderTab: true } ? 1 : 0);
        AddParameter(command, "$x", item.X);
        AddParameter(command, "$y", item.Y);
        AddParameter(command, "$width", item.Width);
        AddParameter(command, "$height", item.Height);
        AddParameter(command, "$zIndex", item.ZIndex);
        AddParameter(command, "$createdAt", item.CreatedAt);
        AddParameter(command, "$updatedAt", item.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM workspace_item_tags WHERE item_id = $itemId;";
        AddParameter(deleteCommand, "$itemId", itemId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        var normalizedTags = tags
            .Select(NormalizeText)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var tag in normalizedTags)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO workspace_item_tags (item_id, tag)
                VALUES ($itemId, $tag);
                """;
            AddParameter(insertCommand, "$itemId", itemId);
            AddParameter(insertCommand, "$tag", tag);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReplaceSessionTabsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionGroupItemId,
        IEnumerable<SessionTab> tabs,
        CancellationToken cancellationToken)
    {
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM workspace_session_tabs WHERE session_group_item_id = $itemId;";
        AddParameter(deleteCommand, "$itemId", sessionGroupItemId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        var orderedTabs = tabs
            .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
            .Select((tab, index) =>
            {
                NormalizeSessionTab(tab, index);
                return tab;
            })
            .ToList();

        foreach (var tab in orderedTabs)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO workspace_session_tabs (
                    id,
                    session_group_item_id,
                    url,
                    title,
                    favicon_png_base64,
                    thumbnail_png_base64,
                    source_tab_id,
                    sort_order,
                    is_folder_tab,
                    split_group_id,
                    split_pane,
                    split_orientation,
                    split_ratio,
                    split_active_pane,
                    saved_at,
                    container_profile_id)
                VALUES (
                    $id,
                    $sessionGroupItemId,
                    $url,
                    $title,
                    $faviconPngBase64,
                    $thumbnailPngBase64,
                    $sourceTabId,
                    $sortOrder,
                    $isFolderTab,
                    $splitGroupId,
                    $splitPane,
                    $splitOrientation,
                    $splitRatio,
                    $splitActivePane,
                    $savedAt,
                    $containerProfileId);
                """;
            AddParameter(insertCommand, "$id", tab.Id);
            AddParameter(insertCommand, "$sessionGroupItemId", sessionGroupItemId);
            AddParameter(insertCommand, "$url", tab.Url);
            AddParameter(insertCommand, "$title", tab.Title);
            AddParameter(insertCommand, "$faviconPngBase64", tab.FaviconPngBase64);
            AddParameter(insertCommand, "$thumbnailPngBase64", tab.ThumbnailPngBase64);
            AddParameter(insertCommand, "$sourceTabId", tab.SourceTabId);
            AddParameter(insertCommand, "$sortOrder", tab.SortOrder);
            AddParameter(insertCommand, "$isFolderTab", tab.IsFolderTab);
            AddParameter(insertCommand, "$containerProfileId", tab.ContainerProfileId);
            AddParameter(insertCommand, "$splitGroupId", tab.SplitGroupId);
            AddParameter(insertCommand, "$splitPane", tab.SplitPane);
            AddParameter(insertCommand, "$splitOrientation", tab.SplitOrientation);
            AddParameter(insertCommand, "$splitRatio", tab.SplitRatio);
            AddParameter(insertCommand, "$splitActivePane", tab.SplitActivePane);
            AddParameter(insertCommand, "$savedAt", tab.SavedAt);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteWorkspaceItemRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM workspace_items WHERE id = $id;";
        AddParameter(command, "$id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<WorkspaceItem>> ReadItemsAsync(
        SqliteConnection connection,
        string whereSql,
        Action<SqliteCommand> configureParameters,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        bool includeDeleted = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            SELECT
                i.id,
                i.workspace_id,
                i.parent_folder_id,
                i.type,
                i.title,
                i.url,
                i.note,
                i.note_font_size,
                i.favicon_png_base64,
                i.thumbnail_png_base64,
                i.thumbnail_updated_at,
                i.youtube_mini_player_state_json,
                i.target_path,
                i.shell_item_kind,
                i.open_in_folder_tab,
                i.x,
                i.y,
                i.width,
                i.height,
                i.z_index,
                i.created_at,
                i.updated_at,
                i.container_profile_id,
                i.deleted_at,
                i.deletion_root_id
            FROM (SELECT * FROM workspace_items {{(includeDeleted ? "" : "WHERE deleted_at IS NULL")}}) i
            {{whereSql}}
            ORDER BY i.z_index, i.updated_at DESC, i.title;
            """;
        configureParameters(command);

        var rows = new List<ItemRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadItemRow(reader));
            }
        }

        var result = new List<WorkspaceItem>();
        foreach (var row in rows)
        {
            var tags = await GetTagsAsync(connection, row.Id, cancellationToken, transaction);
            var tabs = row.Type == WorkspaceItemType.SessionGroup
                ? await GetSessionTabsAsync(connection, row.Id, cancellationToken, transaction)
                : [];

            result.Add(CreateItem(row, tags, tabs));
        }

        return result;
    }

    private static WorkspaceItem CreateItem(
        ItemRow row,
        List<string> tags,
        List<SessionTab> tabs)
    {
        WorkspaceItem item = row.Type switch
        {
            WorkspaceItemType.Bookmark => new BookmarkItem(),
            WorkspaceItemType.Folder => new FolderItem(),
            WorkspaceItemType.SessionGroup => new SessionGroupItem { Tabs = tabs },
            WorkspaceItemType.YouTubeMiniPlayerSet => new YouTubeMiniPlayerSetItem
            {
                Players = DeserializeYouTubeMiniPlayerStates(row.YouTubeMiniPlayerStateJson)
            },
            WorkspaceItemType.Note => new NoteItem(),
            WorkspaceItemType.ShellItem => new ShellItem
            {
                TargetPath = row.TargetPath,
                Kind = ParseShellItemKind(row.ShellItemKind),
                OpenInFolderTab = row.OpenInFolderTab
            },
            _ => new NoteItem()
        };

        item.Id = row.Id;
        item.WorkspaceId = row.WorkspaceId;
        item.ParentFolderId = row.ParentFolderId;
        item.Title = row.Title;
        item.Url = row.Url;
        item.ContainerProfileId = row.ContainerProfileId;
        item.DeletedAt = row.DeletedAt;
        item.DeletionRootId = row.DeletionRootId;
        item.Note = row.Note;
        if (item is NoteItem noteItem)
        {
            noteItem.FontSize = row.NoteFontSize;
        }
        item.FaviconPngBase64 = row.FaviconPngBase64;
        item.ThumbnailPngBase64 = row.ThumbnailPngBase64;
        item.ThumbnailUpdatedAt = row.ThumbnailUpdatedAt;
        item.Tags = tags;
        item.X = row.X;
        item.Y = row.Y;
        item.Width = row.Width;
        item.Height = row.Height;
        item.ZIndex = row.ZIndex;
        item.CreatedAt = row.CreatedAt;
        item.UpdatedAt = row.UpdatedAt;
        return item;
    }

    private static async Task<List<string>> GetTagsAsync(
        SqliteConnection connection,
        Guid itemId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tag
            FROM workspace_item_tags
            WHERE item_id = $itemId
            ORDER BY tag;
            """;
        AddParameter(command, "$itemId", itemId);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<List<SessionTab>> GetSessionTabsAsync(
        SqliteConnection connection,
        Guid itemId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,
                   url,
                   title,
                   favicon_png_base64,
                   thumbnail_png_base64,
                   source_tab_id,
                   sort_order,
                   is_folder_tab,
                   split_group_id,
                   split_pane,
                   split_orientation,
                   split_ratio,
                   split_active_pane,
                   saved_at,
                   container_profile_id
            FROM workspace_session_tabs
            WHERE session_group_item_id = $itemId
            ORDER BY sort_order, saved_at;
            """;
        AddParameter(command, "$itemId", itemId);

        var result = new List<SessionTab>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SessionTab
            {
                Id = ReadGuid(reader, 0),
                Url = reader.GetString(1),
                Title = reader.GetString(2),
                FaviconPngBase64 = reader.GetString(3),
                ThumbnailPngBase64 = reader.GetString(4),
                SourceTabId = ReadNullableGuid(reader, 5),
                SortOrder = reader.GetInt32(6),
                IsFolderTab = reader.GetInt32(7) != 0,
                SplitGroupId = reader.GetString(8),
                SplitPane = reader.GetString(9),
                SplitOrientation = reader.GetString(10),
                SplitRatio = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                SplitActivePane = reader.GetString(12),
                SavedAt = ReadDateTimeOffset(reader, 13),
                ContainerProfileId = reader.IsDBNull(14) ? null : reader.GetString(14)
            });
        }

        return result;
    }

    private async Task<WorkspaceItemType?> GetItemTypeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type FROM workspace_items WHERE id = $id;";
        AddParameter(command, "$id", itemId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && Enum.TryParse<WorkspaceItemType>(text, out var type)
            ? type
            : null;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await OpenConnectionWithoutInitializationAsync(cancellationToken);
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => _initialized ? Task.CompletedTask : Task.Run(() => InitializeCoreAsync(cancellationToken), cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var databaseExisted = File.Exists(_databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? AppPaths.Root);
            await using var connection = await OpenConnectionWithoutInitializationAsync(cancellationToken);
            if (_runIntegrityCheck && databaseExisted)
            {
                await VerifyIntegrityAsync(connection, cancellationToken);
                IntegrityCheckWasRun = true;
            }

            // 書込予約を取った後、別の読取接続で確定済みDBをバックアップする。
            // その間に別Window／プロセスが移行対象を書き換えないようにする。
            using (var transaction = connection.BeginTransaction())
            {
                var version = await ReadSchemaVersionAsync(connection, cancellationToken, transaction);
                if (version < SchemaVersion)
                {
                    if (databaseExisted)
                    {
                        LastMigrationBackupPath = await BackupBeforeMigrationAsync(version, cancellationToken);
                    }

                    await CreateSchemaAsync(connection, transaction, cancellationToken);
                    if (await EnsureColumnAsync(connection, "workspace_session_tabs", "container_profile_id",
                            "TEXT NULL", cancellationToken, transaction))
                    {
                        _beforeContainerMigrationCommit?.Invoke();
                    }

                    await EnsureColumnAsync(connection, "workspace_items", "container_profile_id",
                        "TEXT NULL", cancellationToken, transaction);
                    await EnsureColumnAsync(connection, "workspace_items", "deleted_at",
                        "TEXT NULL", cancellationToken, transaction);
                    await EnsureColumnAsync(connection, "workspace_items", "deletion_root_id",
                        "TEXT NULL", cancellationToken, transaction);
                    BeforeSchemaMigrationCommit?.Invoke();
                    using var versionCommand = connection.CreateCommand();
                    versionCommand.Transaction = transaction;
                    versionCommand.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                    await versionCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionWithoutInitializationAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ReadSchemaVersionAsync(connection, cancellationToken);
            await ApplyConnectionPragmasAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version > SchemaVersion || version < 0)
        {
            throw new InvalidDataException("このWorkspace DBは未対応の新しい形式です。DBを変更せず読み込みを中止しました。");
        }

        return version;
    }

    private async Task<string> BackupBeforeMigrationAsync(int version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _beforeMigrationBackup?.Invoke();
        var backupPath = $"{_databasePath}.migration-v{version}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        var partialPath = backupPath + ".partial";
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = partialPath,
            Pooling = false
        }.ToString()))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            await VerifyIntegrityAsync(destination, cancellationToken);
            using var foreignKeys = destination.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            if (await foreignKeys.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new InvalidDataException("Workspace移行前バックアップの関連データに不整合があります。移行を中止しました。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var file = new FileStream(partialPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Flush(flushToDisk: true);
        }
        File.Move(partialPath, backupPath);
        return backupPath;
    }

    private static async Task ApplyConnectionPragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var commands = new[]
        {
            "PRAGMA foreign_keys = ON;",
            "PRAGMA busy_timeout = 5000;",
            "PRAGMA journal_mode = WAL;",
            "PRAGMA synchronous = FULL;"
        };

        foreach (var commandText in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Workspaceデータベースの整合性確認に失敗しました: "
                + (results.Count == 0 ? "結果なし" : string.Join(" / ", results.Take(8))));
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS workspaces (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_workspaces_sort_order
            ON workspaces(sort_order);
            """,
            """
            CREATE TABLE IF NOT EXISTS workspace_items (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                parent_folder_id TEXT NULL,
                type TEXT NOT NULL,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                note TEXT NOT NULL,
                note_font_size REAL NOT NULL DEFAULT 14,
                favicon_png_base64 TEXT NOT NULL DEFAULT '',
                thumbnail_png_base64 TEXT NOT NULL DEFAULT '',
                thumbnail_updated_at TEXT NULL,
                youtube_mini_player_state_json TEXT NOT NULL DEFAULT '',
                target_path TEXT NOT NULL DEFAULT '',
                shell_item_kind TEXT NOT NULL DEFAULT '',
                open_in_folder_tab INTEGER NOT NULL DEFAULT 0,
                x REAL NOT NULL,
                y REAL NOT NULL,
                width REAL NOT NULL,
                height REAL NOT NULL,
                z_index INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id) ON DELETE CASCADE,
                FOREIGN KEY(parent_folder_id) REFERENCES workspace_items(id) ON DELETE SET NULL
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS ix_workspace_items_workspace_id
            ON workspace_items(workspace_id, z_index);
            """,
            """
            CREATE INDEX IF NOT EXISTS ix_workspace_items_parent_folder_id
            ON workspace_items(parent_folder_id);
            """,
            """
            CREATE TABLE IF NOT EXISTS workspace_item_tags (
                item_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY(item_id, tag),
                FOREIGN KEY(item_id) REFERENCES workspace_items(id) ON DELETE CASCADE
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS ix_workspace_item_tags_tag
            ON workspace_item_tags(tag);
            """,
            """
            CREATE TABLE IF NOT EXISTS workspace_session_tabs (
                id TEXT PRIMARY KEY,
                session_group_item_id TEXT NOT NULL,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                favicon_png_base64 TEXT NOT NULL DEFAULT '',
                thumbnail_png_base64 TEXT NOT NULL DEFAULT '',
                source_tab_id TEXT NULL,
                sort_order INTEGER NOT NULL,
                is_folder_tab INTEGER NOT NULL DEFAULT 0,
                split_group_id TEXT NOT NULL DEFAULT '',
                split_pane TEXT NOT NULL DEFAULT '',
                split_orientation TEXT NOT NULL DEFAULT '',
                split_ratio REAL NULL,
                split_active_pane TEXT NOT NULL DEFAULT '',
                saved_at TEXT NOT NULL,
                FOREIGN KEY(session_group_item_id) REFERENCES workspace_items(id) ON DELETE CASCADE
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS ix_workspace_session_tabs_group
            ON workspace_session_tabs(session_group_item_id, sort_order);
            """
        };

        foreach (var commandText in commands)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "note_font_size",
            "REAL NOT NULL DEFAULT 14",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "favicon_png_base64",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "thumbnail_png_base64",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "thumbnail_updated_at",
            "TEXT NULL",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "youtube_mini_player_state_json",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "target_path",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "shell_item_kind",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_items",
            "open_in_folder_tab",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "favicon_png_base64",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "thumbnail_png_base64",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "is_folder_tab",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "split_group_id",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "split_pane",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "split_orientation",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "split_ratio",
            "REAL NULL",
            cancellationToken, transaction);
        await EnsureColumnAsync(
            connection,
            "workspace_session_tabs",
            "split_active_pane",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken, transaction);
    }

    private static async Task<bool> EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.Transaction = transaction;
            checkCommand.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.Transaction = transaction;
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static Workspace ReadWorkspace(IDataRecord reader)
    {
        return new Workspace
        {
            Id = ReadGuid(reader, 0),
            Name = reader.GetString(1),
            Index = reader.GetInt32(2),
            CreatedAt = ReadDateTimeOffset(reader, 3),
            UpdatedAt = ReadDateTimeOffset(reader, 4)
        };
    }

    private static ItemRow ReadItemRow(IDataRecord reader)
    {
        var typeText = reader.GetString(3);
        var type = Enum.TryParse<WorkspaceItemType>(typeText, out var parsedType)
            ? parsedType
            : WorkspaceItemType.Note;

        return new ItemRow(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            ReadNullableGuid(reader, 2),
            type,
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetDouble(7),
            reader.GetString(8),
            reader.GetString(9),
            ReadNullableDateTimeOffset(reader, 10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt64(14) != 0,
            reader.GetDouble(15),
            reader.GetDouble(16),
            reader.GetDouble(17),
            reader.GetDouble(18),
            reader.GetInt32(19),
            ReadDateTimeOffset(reader, 20),
            ReadDateTimeOffset(reader, 21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            ReadNullableDateTimeOffset(reader, 23),
            ReadNullableGuid(reader, 24));
    }

    private static void NormalizeWorkspace(Workspace workspace)
    {
        if (workspace.Id == Guid.Empty)
        {
            workspace.Id = Guid.NewGuid();
        }

        workspace.Name = string.IsNullOrWhiteSpace(workspace.Name)
            ? $"Workspace {workspace.Index}"
            : workspace.Name.Trim();

        if (workspace.CreatedAt == default)
        {
            workspace.CreatedAt = DateTimeOffset.Now;
        }

        workspace.UpdatedAt = DateTimeOffset.Now;
    }

    private static void NormalizeItem(WorkspaceItem item)
    {
        if (item.Id == Guid.Empty)
        {
            item.Id = Guid.NewGuid();
        }

        if (item.WorkspaceId == Guid.Empty)
        {
            throw new ArgumentException("WorkspaceId is required.", nameof(item));
        }

        item.Title = NormalizeText(item.Title);
        item.Url = NormalizeText(item.Url);
        item.Note = NormalizeText(item.Note);
        if (item is NoteItem noteItem)
        {
            noteItem.FontSize = WorkspaceNoteFontSizeProfile.Normalize(noteItem.FontSize);
        }
        item.FaviconPngBase64 = NormalizeBase64Text(item.FaviconPngBase64);
        item.ThumbnailPngBase64 = NormalizeBase64Text(item.ThumbnailPngBase64);
        item.X = NormalizeNumber(item.X);
        item.Y = NormalizeNumber(item.Y);
        item.Width = Math.Max(60, NormalizeNumber(item.Width));
        item.Height = Math.Max(40, NormalizeNumber(item.Height));
        item.Tags = item.Tags
            .Select(NormalizeText)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (item.CreatedAt == default)
        {
            item.CreatedAt = DateTimeOffset.Now;
        }

        item.UpdatedAt = DateTimeOffset.Now;

        if (item is SessionGroupItem sessionGroup)
        {
            sessionGroup.Tabs = sessionGroup.Tabs
                .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
                .Select((tab, index) =>
                {
                    NormalizeSessionTab(tab, index);
                    return tab;
                })
                .ToList();
        }

        if (item is YouTubeMiniPlayerSetItem playerSet)
        {
            playerSet.Players = playerSet.Players
                .Where(static player => !string.IsNullOrWhiteSpace(player.Url))
                .Select(NormalizeYouTubeMiniPlayerState)
                .ToList();
            item.Url = playerSet.Players.FirstOrDefault()?.Url ?? item.Url;
        }

        if (item is ShellItem shellItem)
        {
            shellItem.TargetPath = NormalizeShellPath(shellItem.TargetPath);
            if (string.IsNullOrWhiteSpace(shellItem.TargetPath))
            {
                throw new ArgumentException("TargetPath is required.", nameof(item));
            }
        }
    }

    private static ShellItemKind ParseShellItemKind(string value)
    {
        return Enum.TryParse<ShellItemKind>(value, out var kind)
            ? kind
            : ShellItemKind.File;
    }

    private static string NormalizeShellPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.GetFullPath(value.Trim());
    }

    private static YouTubeMiniPlayerWorkspaceState NormalizeYouTubeMiniPlayerState(
        YouTubeMiniPlayerWorkspaceState state)
    {
        state.Url = NormalizeText(state.Url);
        state.Title = NormalizeText(state.Title);
        state.ContainerProfileId = TabBrowser.Models.ContainerProfile.NormalizeId(state.ContainerProfileId);
        var size = TabBrowser.Services.YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            state.IsShorts,
            NormalizeNumber(state.Width),
            NormalizeNumber(state.Height),
            preferHeight: false);
        state.Width = size.Width;
        state.Height = size.Height;
        state.Left = NormalizeNumber(state.Left);
        state.Top = NormalizeNumber(state.Top);
        return state;
    }

    private static string SerializeYouTubeMiniPlayerStates(WorkspaceItem item)
    {
        return item is YouTubeMiniPlayerSetItem playerSet
            ? JsonSerializer.Serialize(playerSet.Players, JsonOptions)
            : string.Empty;
    }

    private static List<YouTubeMiniPlayerWorkspaceState> DeserializeYouTubeMiniPlayerStates(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<YouTubeMiniPlayerWorkspaceState>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void NormalizeSessionTab(SessionTab tab, int index)
    {
        if (tab.Id == Guid.Empty)
        {
            tab.Id = Guid.NewGuid();
        }

        tab.Url = NormalizeText(tab.Url);
        tab.Title = NormalizeText(tab.Title);
        tab.FaviconPngBase64 = NormalizeBase64Text(tab.FaviconPngBase64);
        tab.ThumbnailPngBase64 = NormalizeBase64Text(tab.ThumbnailPngBase64);
        tab.SortOrder = index;

        if (!Guid.TryParse(tab.SplitGroupId, out var splitGroupId)
            || (tab.SplitPane != TabBrowser.Models.SplitViewPane.Left
                && tab.SplitPane != TabBrowser.Models.SplitViewPane.Right))
        {
            ClearSessionTabSplitState(tab);
        }
        else
        {
            tab.SplitGroupId = splitGroupId.ToString("N");
            tab.SplitOrientation = TabBrowser.Models.SplitViewModelProfile.NormalizeOrientation(
                tab.SplitOrientation);
            tab.SplitRatio = tab.SplitRatio is { } ratio && double.IsFinite(ratio)
                ? Math.Clamp(
                    ratio,
                    TabBrowser.Models.SplitViewModelProfile.MinimumRatio,
                    TabBrowser.Models.SplitViewModelProfile.MaximumRatio)
                : TabBrowser.Models.SplitViewModelProfile.DefaultRatio;
            tab.SplitActivePane = tab.SplitActivePane == TabBrowser.Models.SplitViewPane.Right
                ? TabBrowser.Models.SplitViewPane.Right
                : TabBrowser.Models.SplitViewPane.Left;
        }

        if (tab.SavedAt == default)
        {
            tab.SavedAt = DateTimeOffset.Now;
        }
    }

    private static void ClearSessionTabSplitState(SessionTab tab)
    {
        tab.SplitGroupId = string.Empty;
        tab.SplitPane = string.Empty;
        tab.SplitOrientation = string.Empty;
        tab.SplitRatio = null;
        tab.SplitActivePane = string.Empty;
    }

    private static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string NormalizeBase64Text(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized.Length <= 350_000
            ? normalized
            : string.Empty;
    }

    private static double NormalizeNumber(double value)
    {
        return double.IsFinite(value)
            ? value
            : 0;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, ToSqliteValue(value));
    }

    private static object ToSqliteValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            Guid guid => guid == Guid.Empty ? DBNull.Value : guid.ToString("D"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static Guid ReadGuid(IDataRecord reader, int ordinal)
    {
        return Guid.TryParse(reader.GetString(ordinal), out var value)
            ? value
            : Guid.Empty;
    }

    private static Guid? ReadNullableGuid(IDataRecord reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) || !Guid.TryParse(reader.GetString(ordinal), out var value)
            ? null
            : value;
    }

    private static DateTimeOffset ReadDateTimeOffset(IDataRecord reader, int ordinal)
    {
        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : DateTimeOffset.Now;
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(IDataRecord reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ReadDateTimeOffset(reader, ordinal);
    }

    private sealed record ItemRow(
        Guid Id,
        Guid WorkspaceId,
        Guid? ParentFolderId,
        WorkspaceItemType Type,
        string Title,
        string Url,
        string Note,
        double NoteFontSize,
        string FaviconPngBase64,
        string ThumbnailPngBase64,
        DateTimeOffset? ThumbnailUpdatedAt,
        string YouTubeMiniPlayerStateJson,
        string TargetPath,
        string ShellItemKind,
        bool OpenInFolderTab,
        double X,
        double Y,
        double Width,
        double Height,
        int ZIndex,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? ContainerProfileId,
        DateTimeOffset? DeletedAt,
        Guid? DeletionRootId);

    private sealed record WorkspaceMoveRow(
        Guid Id,
        Guid WorkspaceId,
        Guid? ParentFolderId,
        WorkspaceItemType Type);
}
