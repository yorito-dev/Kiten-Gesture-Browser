using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using Xunit;
using Xunit.Abstractions;

namespace TabBrowser.Tests;

public sealed class WorkspaceDurabilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryWorkspaceWriteUsesFullWalAndUnpooledConnections()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "耐久性", Index = 1 });
        for (var i = 0; i < 3; i++)
        {
            await using var connection = await OpenRepositoryConnectionAsync(i == 2 ? db.Repository() : repository);
            Assert.False(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        // 実際の各書込で使われた接続の値をトリガーで検証する（別の検査用接続の設定だけで代用しない）。
        await db.ExecuteAsync("""
            CREATE TRIGGER require_full_insert BEFORE INSERT ON workspace_items BEGIN
                SELECT CASE WHEN (SELECT synchronous FROM pragma_synchronous) <> 2 THEN RAISE(ABORT, 'FULLが必要') END;
            END;
            CREATE TRIGGER require_full_update BEFORE UPDATE ON workspace_items BEGIN
                SELECT CASE WHEN (SELECT synchronous FROM pragma_synchronous) <> 2 THEN RAISE(ABORT, 'FULLが必要') END;
            END;
            CREATE TRIGGER require_full_delete BEFORE DELETE ON workspace_items BEGIN
                SELECT CASE WHEN (SELECT synchronous FROM pragma_synchronous) <> 2 THEN RAISE(ABORT, 'FULLが必要') END;
            END;
            """);
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "保存" });
        await repository.UpdateNoteAsync(note.Id, "更新", "確定後の本文", 18);
        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem { WorkspaceId = workspace.Id, ContainerProfileId = "default" });
        var group = await repository.AddSessionGroupAsync(Group(workspace.Id, 5));
        await repository.DeleteItemsAsync([note.Id, bookmark.Id, group.Id]);
        await repository.RestoreItemAsync(note.Id, workspace.Id, null);
        await repository.PermanentlyDeleteItemsAsync([bookmark.Id, group.Id]);
        Assert.Equal("確定後の本文", Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id)).Note);
    }

    [Fact]
    public async Task CommitFailureAfterSuccessfulUpdateDoesNotReportSuccessOrLoseCommittedText()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "確定済み" });
        await db.ExecuteAsync("""
            CREATE TABLE commit_parent(id INTEGER PRIMARY KEY);
            CREATE TABLE commit_probe(parent_id INTEGER REFERENCES commit_parent(id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fail_at_commit AFTER UPDATE ON workspace_items BEGIN INSERT INTO commit_probe VALUES(1); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateNoteAsync(note.Id, "変更", "未確定", 18));
        Assert.Equal("確定済み", Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id)).Note);
        Assert.Equal(0L, await db.ScalarAsync("SELECT COUNT(*) FROM commit_probe;"));
    }

    [Fact]
    public void LockedWriteDoesNotBlockDispatcherAndCancellationKeepsText() => WorkspaceTrashWindowTests.RunInSta(async () =>
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "保持する本文" });
        await using var locker = db.Open();
        await locker.OpenAsync();
        using var transaction = locker.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        var pending = repository.UpdateNoteAsync(note.Id, "変更", "キャンセルする本文", 18, cancellation.Token);
        var uiResponded = false;
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => uiResponded = true);
        await Task.Delay(100);
        Assert.True(uiResponded);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        transaction.Rollback();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("保持する本文", Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id)).Note);
    });

    [Fact]
    public async Task MeasureImportantSaveLatencyOnTemporaryDatabase()
    {
        using var db = new TempWorkspaceDatabase();
        var repository = db.Repository();
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "計測", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "初期本文" });
        await using var keeper = await OpenRepositoryConnectionAsync(repository);
        using var pragma = keeper.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous;";
        var synchronous = await pragma.ExecuteScalarAsync();
        output.WriteLine($"環境: {Environment.OSVersion}; .NET {Environment.Version}; SQLite {keeper.ServerVersion}; synchronous={synchronous}; 一時DB; WAL接続維持; Release");
        var body = new string('本', 1000);
        await MeasureAsync("メモ1000文字", 30, i => repository.UpdateNoteAsync(note.Id, "計測", body + i, 18));
        await MeasureAsync("単独タブ", 30, i => repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "計測",
            Url = "https://example.test/" + i,
            ContainerProfileId = "default"
        }));
        await MeasureAsync("グループ25タブ", 30, i => repository.AddSessionGroupAsync(Group(workspace.Id, 25)));
        await MeasureAsync("削除と復元", 30, async _ =>
        {
            await repository.DeleteItemAsync(note.Id);
            await repository.RestoreItemAsync(note.Id, workspace.Id, null);
        });
        await MeasureAsync("大量500タブ", 5, i => repository.AddSessionGroupAsync(Group(workspace.Id, 500)));
        Assert.Equal(body + 29, Assert.Single(await db.Repository().GetWorkspaceItemsAsync(workspace.Id), x => x.Id == note.Id).Note);
        output.WriteLine($"終了時: workspace_items={await db.ScalarAsync("SELECT COUNT(*) FROM workspace_items;")}; session_tabs={await db.ScalarAsync("SELECT COUNT(*) FROM workspace_session_tabs;")}");
    }

    private async Task MeasureAsync(string name, int count, Func<int, Task> action)
    {
        var samples = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var timer = Stopwatch.StartNew();
            await action(i);
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        output.WriteLine($"{name}: n={count}, median={samples[count / 2]:F3}ms, p95={samples[(int)Math.Ceiling(count * .95) - 1]:F3}ms, max={samples[^1]:F3}ms");
    }

    private static SessionGroupItem Group(Guid workspaceId, int count) => new()
    {
        WorkspaceId = workspaceId,
        Title = "計測",
        Tabs = Enumerable.Range(0, count).Select(i => new SessionTab
        {
            Url = "https://example.test/" + i,
            Title = "タブ",
            ContainerProfileId = "default",
            SortOrder = i
        }).ToList()
    };

    internal static async Task<SqliteConnection> OpenRepositoryConnectionAsync(SQLiteWorkspaceRepository repository)
    {
        var method = typeof(SQLiteWorkspaceRepository).GetMethod("OpenConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<SqliteConnection>)method.Invoke(repository, [CancellationToken.None])!;
    }
}
