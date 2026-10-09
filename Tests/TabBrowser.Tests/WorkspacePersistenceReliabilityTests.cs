using Microsoft.Data.Sqlite;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using Xunit;

namespace TabBrowser.Tests;

public sealed class WorkspacePersistenceReliabilityTests
{
    [Fact]
    public async Task ConcurrentFirstLoadsDoNotCreateDuplicateDefaultWorkspaces()
    {
        using var db = new TempWorkspaceDatabase();
        var result = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => new WorkspaceService(db.Repository()).GetWorkspacesAsync()));
        Assert.All(result, workspaces => Assert.Equal(9, workspaces.Count));
        var stored = await db.Repository().GetWorkspacesAsync();
        Assert.Equal(9, stored.Count);
        Assert.Equal(Enumerable.Range(1, 9), stored.Select(x => x.Index));
        Assert.Equal(9, stored.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public async Task TrashRetainsNotesBookmarksGroupsOrderImagesContainersAndMembershipAcrossReopen()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var folder = await repository.AddFolderAsync(new FolderItem { WorkspaceId = workspace.Id, Title = "親" });
        var note = await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = folder.Id,
            Title = "検索対象",
            Content = "保護する本文",
            FontSize = 22,
            Tags = ["タグ"]
        });
        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = folder.Id,
            Title = "検索対象",
            Url = "https://example.test/",
            ContainerProfileId = "default",
            ThumbnailPngBase64 = "aW1hZ2U="
        });
        var group = await repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = folder.Id,
            Title = "検索対象",
            Tabs = [new SessionTab { Url = "https://example.test/first", ContainerProfileId = "default", SortOrder = 0 },
                new SessionTab { Url = "https://example.test/second", ContainerProfileId = "missing-container", SortOrder = 1, ThumbnailPngBase64 = "aW1hZ2U=" }]
        });
        var ids = new[] { folder.Id, note.Id, bookmark.Id, group.Id };
        Assert.Equal(ids.Order(), (await repository.DeleteItemsAsync([note.Id, folder.Id, group.Id])).Order());
        repository = db.Repository();
        Assert.Empty(await repository.GetWorkspaceItemsAsync(workspace.Id));
        Assert.Empty(await repository.SearchItemsAsync("検索対象"));
        Assert.Empty(await repository.SearchItemsAsync("example.test"));
        Assert.Empty(await repository.SearchItemsAsync("タグ"));
        Assert.Empty(await repository.SearchItemsAsync("保護する本文"));
        var trash = await repository.GetDeletedItemsAsync();
        Assert.Equal(ids.Order(), trash.Select(x => x.Id).Order());
        Assert.All(trash, x => { Assert.NotNull(x.DeletedAt); Assert.Equal(folder.Id, x.DeletionRootId); });
        await Assert.ThrowsAsync<WorkspaceNoteMissingException>(() => repository.UpdateNoteAsync(note.Id, "上書き", "拒否する本文", 14));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = folder.Id,
            Content = "削除済み親には追加しない"
        }));
        Assert.Equal(4, (await repository.RestoreItemAsync(folder.Id, workspace.Id, null)).Count);
        var loaded = await db.Repository().GetWorkspaceItemsAsync(workspace.Id);
        Assert.Equal(ids.Order(), loaded.Select(x => x.Id).Order());
        Assert.All(loaded.Where(x => x.Id != folder.Id), x => Assert.Equal(folder.Id, x.ParentFolderId));
        Assert.All(loaded, x => Assert.Null(x.DeletedAt));
        var loadedNote = Assert.IsType<NoteItem>(Assert.Single(loaded, x => x.Id == note.Id));
        Assert.Equal("保護する本文", loadedNote.Content); Assert.Equal(22, loadedNote.FontSize); Assert.Equal(["タグ"], loadedNote.Tags);
        var loadedBookmark = Assert.Single(loaded, x => x.Id == bookmark.Id);
        Assert.Equal("default", loadedBookmark.ContainerProfileId); Assert.Equal("aW1hZ2U=", loadedBookmark.ThumbnailPngBase64);
        var loadedGroup = Assert.IsType<SessionGroupItem>(Assert.Single(loaded, x => x.Id == group.Id));
        Assert.Equal(group.Tabs.Select(x => x.Id), loadedGroup.Tabs.Select(x => x.Id));
        Assert.Equal(group.Tabs.Select(x => x.Url), loadedGroup.Tabs.Select(x => x.Url));
        Assert.Equal(group.Tabs.Select(x => x.SortOrder), loadedGroup.Tabs.Select(x => x.SortOrder));
        Assert.Equal(group.Tabs.Select(x => x.ContainerProfileId), loadedGroup.Tabs.Select(x => x.ContainerProfileId));
        Assert.Equal(group.Tabs.Select(x => x.ThumbnailPngBase64), loadedGroup.Tabs.Select(x => x.ThumbnailPngBase64));
        Assert.Empty(await repository.GetDeletedItemsAsync());
    }

    [Fact]
    public async Task MissingParentAndConcurrentRestoreNeverOverwriteOrDuplicateExistingItems()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var folder = await repository.AddFolderAsync(new FolderItem { WorkspaceId = workspace.Id });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, ParentFolderId = folder.Id, Title = "同名", Content = "戻す本文" });
        var existing = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Title = "同名", Content = "既存本文" });
        await repository.DeleteItemAsync(note.Id);
        await repository.DeleteItemAsync(folder.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreItemAsync(note.Id, workspace.Id, folder.Id));
        Assert.Equal(2, (await repository.GetDeletedItemsAsync()).Count);
        var restored = await Task.WhenAll(db.Repository().RestoreItemAsync(note.Id, workspace.Id, null), db.Repository().RestoreItemAsync(note.Id, workspace.Id, null));
        Assert.Equal(1, restored.Sum(x => x.Count));
        var loaded = await db.Repository().GetWorkspaceItemsAsync(workspace.Id);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("戻す本文", Assert.Single(loaded, x => x.Id == note.Id).Note);
        Assert.Equal("既存本文", Assert.Single(loaded, x => x.Id == existing.Id).Note);
        await repository.RestoreItemAsync(folder.Id, workspace.Id, null);
        Assert.Null(Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id), x => x.Id == note.Id).ParentFolderId);
    }

    [Fact]
    public async Task TrashAndRestoreFailuresRollbackWholeSubtreeAndPermanentDeleteRequiresExactTrashSet()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var folder = await repository.AddFolderAsync(new FolderItem { WorkspaceId = workspace.Id });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, ParentFolderId = folder.Id, Content = "子の本文" });
        await db.ExecuteAsync("CREATE TRIGGER fail_change BEFORE UPDATE OF deleted_at ON workspace_items WHEN OLD.type='Note' BEGIN SELECT RAISE(ABORT, '検証用途中障害'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => repository.DeleteItemAsync(folder.Id));
        Assert.Empty(await repository.GetDeletedItemsAsync());
        Assert.Equal(2, (await repository.GetWorkspaceItemsAsync(workspace.Id)).Count);
        await db.ExecuteAsync("DROP TRIGGER fail_change;");
        await repository.DeleteItemAsync(folder.Id);
        await db.ExecuteAsync("CREATE TRIGGER fail_change BEFORE UPDATE OF deleted_at ON workspace_items WHEN OLD.type='Note' BEGIN SELECT RAISE(ABORT, '検証用途中障害'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => repository.RestoreItemAsync(folder.Id, workspace.Id, null));
        Assert.Empty(await repository.GetWorkspaceItemsAsync(workspace.Id));
        Assert.Equal(2, (await repository.GetDeletedItemsAsync()).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PermanentlyDeleteItemsAsync([folder.Id]));
        Assert.Equal(2, (await repository.GetDeletedItemsAsync()).Count);
        await db.ExecuteAsync("DROP TRIGGER fail_change;");
        await repository.PermanentlyDeleteItemsAsync([folder.Id, note.Id]);
        Assert.Empty(await repository.GetDeletedItemsAsync());
        Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM workspace_items;"));
    }

    [Fact]
    public async Task TrashSchemaMigrationRollsBackOnFailureAndKeepsVersionOneBackupWithOriginalData()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var item = await repository.AddBookmarkAsync(new BookmarkItem { WorkspaceId = workspace.Id, Title = "旧形式", ContainerProfileId = "container", Url = "https://example.test/" });
        await db.ExecuteAsync("PRAGMA user_version=1; ALTER TABLE workspace_items DROP COLUMN deleted_at; ALTER TABLE workspace_items DROP COLUMN deletion_root_id;");
        var failing = new SQLiteWorkspaceRepository(db.Path) { BeforeSchemaMigrationCommit = () => throw new IOException("検証用移行確定障害") };
        await Assert.ThrowsAsync<IOException>(() => failing.GetWorkspacesAsync());
        Assert.NotNull(failing.LastMigrationBackupPath);
        Assert.Equal(1L, await db.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('workspace_items') WHERE name='deleted_at';"));
        Assert.Equal("container", await db.ScalarAsync("SELECT container_profile_id FROM workspace_items;"));
        var loaded = Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id));
        Assert.Equal(item.Id, loaded.Id); Assert.Equal(item.Url, loaded.Url); Assert.Equal(item.ContainerProfileId, loaded.ContainerProfileId);
        Assert.Null(loaded.DeletedAt);
    }

    [Fact]
    public async Task VersionOneMigratesToVersionTwoOnceAndRestartDoesNotMigrateAgain()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "version 1", Index = 1 });
        var item = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "保持対象",
            Url = "https://example.test/",
            ContainerProfileId = "container"
        });
        await db.ExecuteAsync(
            "PRAGMA user_version=1; ALTER TABLE workspace_items DROP COLUMN deleted_at; ALTER TABLE workspace_items DROP COLUMN deletion_root_id;");

        var migrated = db.Repository();
        var loaded = Assert.IsType<BookmarkItem>(Assert.Single(await migrated.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(item.Id, loaded.Id);
        Assert.Equal("container", loaded.ContainerProfileId);
        Assert.NotNull(migrated.LastMigrationBackupPath);
        Assert.Equal(2L, await db.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await db.ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('workspace_items') WHERE name='deleted_at';"));
        Assert.Equal(1L, await db.ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('workspace_items') WHERE name='deletion_root_id';"));

        var restarted = db.Repository();
        Assert.Equal(item.Id, Assert.Single(await restarted.GetWorkspaceItemsAsync(workspace.Id)).Id);
        Assert.Null(restarted.LastMigrationBackupPath);
        Assert.Equal(2L, await db.ScalarAsync("PRAGMA user_version;"));
    }

    [Fact]
    public async Task SavedCardOpeningUsesContainerAwareRouteAndReportsDeferral()
    {
        using var db = new TempWorkspaceDatabase();
        await using var recentItems = new RecentItemsService(Path.Combine(db.DirectoryPath, "recent-items.json"));
        var service = new WorkspaceService(db.Repository());
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var saved = await service.AddBookmarkAsync(workspace.Id, "保存タブ", "https://example.test/", 20, 30,
            containerProfileId: "missing-container");
        var calls = new List<SessionTab>();
        var view = new WorkspaceOSViewModel(service, workspace.Id,
            new WorkspaceTabPort(_ => throw new InvalidOperationException("通常URL経路へ流してはいけない"),
                tabs => { calls.AddRange(tabs); return Task.FromResult(0); }, () => [], () => null,
                _ => Task.CompletedTask, _ => { }, () => "コンテナが見つからないため保留しました"),
            new WorkspaceMiniPlayerPort(() => [], () => Task.FromResult<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>>([]), _ => Task.CompletedTask),
            new WorkspaceWindowPort(_ => Task.CompletedTask, (_, _) => { }, _ => { }),
            new WorkspaceThumbnailPort((_, _) => Task.FromResult<System.Windows.Media.ImageSource?>(null)));
        view.ConfigureRecentItems(recentItems, recordActions: true);
        await view.InitializeAsync();
        await ((AsyncRelayCommand)view.OpenItemCommand).ExecuteAsync(Assert.Single(view.Items, x => x.Id == saved.Id));
        Assert.Equal("missing-container", Assert.Single(calls).ContainerProfileId);
        Assert.Contains("保留", view.StatusText);
        Assert.DoesNotContain("開きました", view.StatusText);
        Assert.Empty(recentItems.GetSnapshot());
        Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id));
    }

    [Fact]
    public async Task SavedCardOpeningRecordsRecentItemOnlyAfterSuccessfulRestore()
    {
        using var db = new TempWorkspaceDatabase();
        await using var recentItems = new RecentItemsService(Path.Combine(db.DirectoryPath, "recent-items.json"));
        var service = new WorkspaceService(db.Repository());
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var saved = await service.AddBookmarkAsync(
            workspace.Id,
            "保存タブ",
            "https://example.test/",
            20,
            30,
            containerProfileId: "existing-container");
        var view = new WorkspaceOSViewModel(
            service,
            workspace.Id,
            new WorkspaceTabPort(
                _ => throw new InvalidOperationException("通常URL経路へ流してはいけない"),
                tabs => Task.FromResult(tabs.Count),
                () => [],
                () => null,
                _ => Task.CompletedTask,
                _ => { },
                () => null),
            new WorkspaceMiniPlayerPort(
                () => [],
                () => Task.FromResult<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>>([]),
                _ => Task.CompletedTask),
            new WorkspaceWindowPort(_ => Task.CompletedTask, (_, _) => { }, _ => { }),
            new WorkspaceThumbnailPort((_, _) => Task.FromResult<System.Windows.Media.ImageSource?>(null)));
        view.ConfigureRecentItems(recentItems, recordActions: true);
        await view.InitializeAsync();

        await ((AsyncRelayCommand)view.OpenItemCommand).ExecuteAsync(Assert.Single(view.Items, x => x.Id == saved.Id));

        var recent = Assert.Single(recentItems.GetSnapshot());
        Assert.Equal(saved.Id, recent.WorkspaceItemId);
        Assert.Equal(workspace.Id, recent.WorkspaceId);
        Assert.Contains("開きました", view.StatusText);
    }

    [Fact]
    public async Task SavedSingleTabsKeepDistinctContainersCopiesAndLegacyBookmarks()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var service = new WorkspaceService(repository);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var containers = new string?[] { "default", Guid.NewGuid().ToString("N"), "missing-container", null };
        var savedIds = new List<Guid>();
        foreach (var container in containers)
        {
            var saved = await service.AddBookmarkAsync(workspace.Id, "同じURL", "https://example.test/", 20, 30,
                note: "保存メモ", faviconPngBase64: "aWNvbg==", thumbnailPngBase64: "aW1hZ2U=",
                tags: ["検証"], containerProfileId: container);
            savedIds.Add(saved.Id);
            var duplicate = await service.DuplicateWorkspaceItemAsync(saved, 100, 100);
            Assert.Equal(container, duplicate.ContainerProfileId);
        }
        var reloaded = await db.Repository().GetWorkspaceItemsAsync(workspace.Id);
        for (var index = 0; index < containers.Length; index++)
        {
            var loaded = Assert.IsType<BookmarkItem>(Assert.Single(reloaded, x => x.Id == savedIds[index]));
            Assert.Equal(containers[index], loaded.ContainerProfileId);
            Assert.Equal("保存メモ", loaded.Note);
            Assert.Equal("aWNvbg==", loaded.FaviconPngBase64);
            Assert.Equal("aW1hZ2U=", loaded.ThumbnailPngBase64);
            Assert.Equal(["検証"], loaded.Tags);
            var tab = WorkspaceTabSavePlanner.CreateSavedSingleTab(loaded);
            if (containers[index] is null) Assert.Null(tab);
            else Assert.Equal(containers[index], Assert.IsType<SessionTab>(tab).ContainerProfileId);
        }
    }

    [Fact]
    public async Task MigrationBackupContainsCommittedWalDataAndUnknownVersionIsNotChanged()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "WALの本文" });
        await using var keeper = db.Open();
        await keeper.OpenAsync();
        await db.ExecuteAsync("PRAGMA user_version = 0; ALTER TABLE workspace_items DROP COLUMN container_profile_id;");
        var migrated = db.Repository();
        Assert.Equal(note.Id, Assert.Single(await migrated.GetWorkspaceItemsAsync(workspace.Id)).Id);
        Assert.NotNull(migrated.LastMigrationBackupPath);
        await using (var backup = new SqliteConnection($"Data Source={migrated.LastMigrationBackupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backup.OpenAsync();
            using var command = backup.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT note FROM workspace_items;";
            Assert.Equal("WALの本文", await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('workspace_items') WHERE name='container_profile_id';";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        await db.ExecuteAsync("PRAGMA user_version = 999;");
        await Assert.ThrowsAsync<InvalidDataException>(() => db.Repository().GetWorkspacesAsync());
        Assert.Equal(999L, await db.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal("WALの本文", await db.ScalarAsync("SELECT note FROM workspace_items;"));
    }

    [Fact]
    public async Task BackupFailureAbortsMigrationWithoutChangingOldData()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "変更しない" });
        await db.ExecuteAsync("PRAGMA user_version = 0; ALTER TABLE workspace_items DROP COLUMN container_profile_id;");
        var blocked = new SQLiteWorkspaceRepository(db.Path, false, null,
            () => throw new IOException("検証用のバックアップ障害"));
        await Assert.ThrowsAsync<IOException>(() => blocked.GetWorkspacesAsync());
        Assert.Equal(0L, await db.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('workspace_items') WHERE name='container_profile_id';"));
        Assert.Equal("変更しない", await db.ScalarAsync("SELECT note FROM workspace_items;"));
        Assert.Equal("変更しない", Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id)).Note);
    }

    [Fact]
    public async Task VersionZeroMigrationPreservesEveryItemAndRelatedValueAndVerifiedBackup()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "旧形式検証", Index = 1 });
        var parent = await repository.AddFolderAsync(new FolderItem { WorkspaceId = workspace.Id, Title = "親", X = 10, Y = 20 });
        await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = parent.Id,
            Title = "メモ",
            Content = "移行で保持する本文\n2行目",
            FontSize = 24,
            Tags = ["甲", "乙"],
            ZIndex = 3
        });
        await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = parent.Id,
            Title = "通常ブックマーク",
            Url = "https://example.test/",
            FaviconPngBase64 = "aW1hZ2U=",
            ThumbnailPngBase64 = "aW1hZ2U="
        });
        await repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = parent.Id,
            Title = "グループ",
            Tags = ["保存"],
            Tabs = [new() { Url = "https://example.test/1", SortOrder = 0,
                ContainerProfileId = Guid.NewGuid().ToString("N"), SplitGroupId = "split", SplitPane = "Left", SplitRatio = 0.6 },
                new() { Url = "https://example.test/2", SortOrder = 1, ContainerProfileId = null, ThumbnailPngBase64 = "aW1hZ2U=" }]
        });
        await repository.AddShellItemsAsync([new ShellItem { WorkspaceId = workspace.Id, ParentFolderId = parent.Id,
            TargetPath = db.DirectoryPath, Kind = ShellItemKind.Directory, OpenInFolderTab = true }]);
        await repository.AddYouTubeMiniPlayerSetAsync(new YouTubeMiniPlayerSetItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = parent.Id,
            Title = "ミニプレイヤー",
            Players = [new() { Url = "https://www.youtube.com/watch?v=abcdefghijk",
                ContainerProfileId = "default", LastPositionSeconds = 12.5, Width = 400, Height = 225 }]
        });
        var original = (await repository.GetWorkspaceItemsAsync(workspace.Id)).ToDictionary(x => x.Id,
            x => System.Text.Json.JsonSerializer.Serialize(x, x.GetType()));
        await db.ExecuteAsync("ALTER TABLE workspace_items DROP COLUMN container_profile_id; ALTER TABLE workspace_items DROP COLUMN deleted_at; ALTER TABLE workspace_items DROP COLUMN deletion_root_id; PRAGMA user_version=0;");
        var migrated = db.Repository();
        var loaded = await migrated.GetWorkspaceItemsAsync(workspace.Id);
        Assert.Equal(original.Count, loaded.Count);
        Assert.All(loaded, item => Assert.Equal(original[item.Id], System.Text.Json.JsonSerializer.Serialize(item, item.GetType())));
        Assert.NotNull(migrated.LastMigrationBackupPath);
        await using var backup = new SqliteConnection($"Data Source={migrated.LastMigrationBackupPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        using var command = backup.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM workspace_items;";
        Assert.Equal((long)original.Count, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM workspace_session_tabs;";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}

internal sealed class TempWorkspaceDatabase : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "TabBrowserPersistenceTests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(DirectoryPath, "workspace.db");
    public TempWorkspaceDatabase() => Directory.CreateDirectory(DirectoryPath);
    public SQLiteWorkspaceRepository Repository() => new(Path);
    public SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
