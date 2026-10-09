using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;
using BrowserSplitViewOrientation = TabBrowser.Models.SplitViewOrientation;
using BrowserSplitViewPane = TabBrowser.Models.SplitViewPane;

namespace TabBrowser.Tests;

public sealed class WorkspaceRepositoryTests
{
    [Fact]
    public async Task NoteUpdateAfterOtherConnectionDeletesTargetDoesNotSucceed()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "保存済み" });
        await using (var other = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await other.OpenAsync();
            using var command = other.CreateCommand();
            command.CommandText = "DELETE FROM workspace_items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", note.Id.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<WorkspaceNoteMissingException>(() =>
            repository.UpdateNoteAsync(note.Id, "編集中", "保持すべき本文", 18));
        Assert.Empty(await new SQLiteWorkspaceRepository(database.DatabasePath).GetWorkspaceItemsAsync(workspace.Id));
    }

    [Fact]
    public async Task NoteUpdateAndIdenticalResaveSurviveRepositoryRecreation()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "保存前" });
        for (var i = 0; i < 2; i++)
        {
            await repository.UpdateNoteAsync(note.Id, "保存後", "同じ本文\n2行目", 20);
            var loaded = Assert.IsType<NoteItem>(Assert.Single(await
                new SQLiteWorkspaceRepository(database.DatabasePath).GetWorkspaceItemsAsync(workspace.Id)));
            Assert.Equal(note.Id, loaded.Id);
            Assert.Equal("同じ本文\n2行目", loaded.Content);
            Assert.Equal(20, loaded.FontSize);
        }
    }

    [Fact]
    public async Task NoteWriteFailureAndCancellationRetainCommittedTextAndDoNotResurrectTargets()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "検証", Index = 1 });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Content = "直前の正常本文" });
        await using (var other = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await other.OpenAsync();
            using var command = other.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_note_update BEFORE UPDATE ON workspace_items BEGIN SELECT RAISE(ABORT, '検証用書き込み障害'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateNoteAsync(note.Id, "変更", "失敗する本文", 18));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.UpdateNoteAsync(note.Id, "変更", "中止する本文", 18, cancellation.Token));
        var loaded = Assert.IsType<NoteItem>(Assert.Single(await
            new SQLiteWorkspaceRepository(database.DatabasePath).GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal("直前の正常本文", loaded.Content);
    }

    [Fact]
    public async Task FolderTabPathSavePreservesSourceOrderRawDeduplicationGridAndChangeNotification()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspaces = await service.GetWorkspacesAsync();
        var workspace = workspaces.Single(item => item.Index == 7);
        for (var index = 0; index < 5; index++)
        {
            await service.AddNoteAsync(workspace.Id, "既存", "本文", index, 0);
        }
        var file = Path.Combine(database.DirectoryPath, "note.txt");
        await File.WriteAllTextAsync(file, "ファイル本文は変更しない");
        var alternateFile = Path.Combine(database.DirectoryPath, ".", "note.txt");
        var trace = new List<string>();
        var listener = new FolderTabSaveListener(items =>
        {
            if (items.Any(item => item.WorkspaceId == workspace.Id)) trace.Add("changed");
        });
        service.SubscribeToChanges(listener);
        var tokenReads = 0;
        await WorkspaceService.SaveFolderTabPathsAsync(
            [file, file.ToUpperInvariant(), database.DirectoryPath, alternateFile,
                Path.Combine(database.DirectoryPath, "missing"), ""],
            () =>
            {
                Assert.Equal(1, tokenReads);
                return workspace.Id;
            },
            () =>
            {
                trace.Add("token:" + ++tokenReads);
                return CancellationToken.None;
            },
            (completedWorkspace, count) =>
            {
                Assert.Equal(workspace.Id, completedWorkspace.Id);
                Assert.Equal(3, count);
                trace.Add("complete");
            },
            () =>
            {
                trace.Add("factory");
                return service;
            });

        var shells = (await repository.GetWorkspaceItemsAsync(workspace.Id))
            .OfType<ShellItem>().OrderBy(item => item.Y).ThenBy(item => item.X).ToArray();
        Assert.Equal(3, shells.Length);
        Assert.Equal([Path.GetFullPath(file), database.DirectoryPath, Path.GetFullPath(file)], shells.Select(item => item.TargetPath));
        Assert.Equal([912d, 32d, 208d], shells.Select(item => item.X));
        Assert.Equal([32d, 144d, 144d], shells.Select(item => item.Y));
        Assert.Equal([ShellItemKind.File, ShellItemKind.Directory, ShellItemKind.File], shells.Select(item => item.Kind));
        Assert.Equal([false, true, false], shells.Select(item => item.OpenInFolderTab));
        Assert.All(shells, item => Assert.Null(item.ParentFolderId));
        Assert.Equal("note.txt", shells[0].Title);
        Assert.Equal("ファイル本文は変更しない", await File.ReadAllTextAsync(file));
        Assert.Equal(["factory", "token:1", "token:2", "token:3", "changed", "complete"], trace);
        GC.KeepAlive(listener);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task FolderTabPathSaveUsesLivePreferredIdAfterWorkspaceLookupOrLowestIndexFallback(bool? missingPreferred)
    {
        using var database = new TempDatabase();
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var workspaces = await service.GetWorkspacesAsync();
        var preferred = workspaces.Single(item => item.Index == 8).Id;
        var expected = missingPreferred != false ? workspaces.Single(item => item.Index == 1).Id : preferred;
        Guid completedId = Guid.Empty;
        var tokenReads = 0;
        await WorkspaceService.SaveFolderTabPathsAsync([database.DirectoryPath],
            () =>
            {
                Assert.Equal(1, tokenReads);
                return missingPreferred is null ? null : missingPreferred.Value ? Guid.Empty : preferred;
            },
            () =>
            {
                tokenReads++;
                return CancellationToken.None;
            },
            (workspace, _) => completedId = workspace.Id,
            () => service);
        Assert.Equal(expected, completedId);
        Assert.Single((await service.GetWorkspaceItemsAsync(expected)).OfType<ShellItem>());
        Assert.Equal(3, tokenReads);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("null")]
    public async Task FolderTabPathSaveRejectsInvalidInputBeforeServiceOrSettingsAccess(string input)
    {
        var calls = 0;
        IReadOnlyList<string> paths = input switch
        {
            "empty" => [],
            "missing" => [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing")],
            _ => null!
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceService.SaveFolderTabPathsAsync(
            paths,
            () => { calls++; return Guid.Empty; },
            () => { calls++; return CancellationToken.None; },
            (_, _) => calls++,
            () => { calls++; throw new InvalidOperationException("factory must not run"); }));
        Assert.Equal(0, calls);
        if (input == "null") Assert.IsType<ArgumentNullException>(failure.InnerException);
        else Assert.IsType<FileNotFoundException>(failure.InnerException);
    }

    [Theory]
    [InlineData("factory", false)]
    [InlineData("factory", true)]
    [InlineData("token:1", false)]
    [InlineData("token:1", true)]
    [InlineData("preferred", false)]
    [InlineData("preferred", true)]
    [InlineData("token:2", false)]
    [InlineData("token:2", true)]
    [InlineData("token:3", false)]
    [InlineData("token:3", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    public async Task FolderTabPathSaveKeepsExceptionBoundaryAndAlreadyCommittedData(string failingStep, bool cancel)
    {
        using var database = new TempDatabase();
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var workspace = (await service.GetWorkspacesAsync()).First();
        Exception expected = cancel ? new OperationCanceledException("cancel") : new IOException("failure");
        var trace = new List<string>();
        var tokenReads = 0;
        void Step(string step)
        {
            trace.Add(step);
            if (step == failingStep) throw expected;
        }
        var failure = await Record.ExceptionAsync(() => WorkspaceService.SaveFolderTabPathsAsync(
            [database.DirectoryPath],
            () => { Step("preferred"); return workspace.Id; },
            () => { Step("token:" + ++tokenReads); return CancellationToken.None; },
            (_, _) => Step("complete"),
            () => { Step("factory"); return service; }));
        if (cancel) Assert.Same(expected, failure);
        else
        {
            var wrapper = Assert.IsType<InvalidOperationException>(failure);
            Assert.Same(expected, wrapper.InnerException);
            Assert.Equal(expected.Message, wrapper.Message);
        }
        Assert.Equal(failingStep, trace.Last());
        Assert.Equal(failingStep == "complete" ? 1 : 0,
            (await service.GetWorkspaceItemsAsync(workspace.Id)).Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FolderTabPathSavePassesCancellationToEachRepositoryOperation(int cancelAtRead)
    {
        using var database = new TempDatabase();
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var workspace = (await service.GetWorkspacesAsync()).First();
        var reads = 0;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var failure = await Record.ExceptionAsync(() => WorkspaceService.SaveFolderTabPathsAsync(
            [database.DirectoryPath],
            () => workspace.Id,
            () => ++reads == cancelAtRead ? canceled.Token : CancellationToken.None,
            (_, _) => Assert.Fail("取消後に完了通知を行わない"),
            () => service));
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(cancelAtRead, reads);
        Assert.Empty(await service.GetWorkspaceItemsAsync(workspace.Id));
    }

    private sealed class FolderTabSaveListener(Action<IReadOnlyList<WorkspaceItem>> changed) : IWorkspaceChangeListener
    {
        public void OnWorkspaceNameChanged(WorkspaceNameChangedChange change) { }
        public void OnWorkspaceItemsMoved(WorkspaceItemsMovedChange change) { }
        public void OnWorkspaceItemPositionsChanged(WorkspaceItemPositionsChangedChange change) { }
        public void OnWorkspaceItemsChanged(WorkspaceItemsChangedChange change) => changed(change.UpsertedItems);
        public void OnWorkspaceItemContentChanged(WorkspaceItemContentChange change) { }
    }

    [Fact]
    public async Task IntegrityCheckRunsOnlyWhenExplicitlyRequestedForExistingDatabase()
    {
        using var database = new TempDatabase();
        var setup = new SQLiteWorkspaceRepository(database.DatabasePath);
        await setup.GetWorkspacesAsync();

        var normal = new SQLiteWorkspaceRepository(database.DatabasePath, runIntegrityCheck: false);
        await normal.GetWorkspacesAsync();
        Assert.False(normal.IntegrityCheckWasRun);

        var recovered = new SQLiteWorkspaceRepository(database.DatabasePath, runIntegrityCheck: true);
        await recovered.GetWorkspacesAsync();
        Assert.True(recovered.IntegrityCheckWasRun);
    }

    [Fact]
    public async Task WorkspaceNameCanBeUpdatedWithoutRecreatingWorkspace()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Workspace 1",
            Index = 1
        });

        await repository.UpdateWorkspaceNameAsync(workspace.Id, "調査");

        var stored = Assert.Single(await repository.GetWorkspacesAsync());
        Assert.Equal(workspace.Id, stored.Id);
        Assert.Equal(1, stored.Index);
        Assert.Equal("調査", stored.Name);
    }

    [Fact]
    public async Task RepositoryCrudOperationsPersistItems()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "調査",
            Index = 1
        });

        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "初期タイトル",
            Url = "https://example.com/article",
            Note = "あとで読む",
            ThumbnailPngBase64 = "dGh1bWJuYWls",
            ThumbnailUpdatedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            X = 10,
            Y = 20,
            Tags = ["research"]
        });
        var folder = await repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = workspace.Id,
            Title = "資料",
            X = 100,
            Y = 120
        });
        var note = await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            Title = "メモ",
            Content = "確認事項",
            X = 200,
            Y = 220
        });

        await repository.UpdateItemPositionAsync(bookmark.Id, 40, 50);
        await repository.RenameItemAsync(bookmark.Id, "更新済みタイトル");
        await repository.DeleteItemAsync(note.Id);

        var items = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var storedBookmark = Assert.IsType<BookmarkItem>(Assert.Single(items, x => x.Id == bookmark.Id));

        Assert.Contains(items, x => x.Id == folder.Id);
        Assert.DoesNotContain(items, x => x.Id == note.Id);
        Assert.Equal("更新済みタイトル", storedBookmark.Title);
        Assert.Equal(40, storedBookmark.X);
        Assert.Equal(50, storedBookmark.Y);
        Assert.Equal("https://example.com/article", storedBookmark.Url);
        Assert.Equal("dGh1bWJuYWls", storedBookmark.ThumbnailPngBase64);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), storedBookmark.ThumbnailUpdatedAt);
        Assert.Contains("research", storedBookmark.Tags);
    }

    [Fact]
    public async Task WorkspaceServicePreservesRequestedNoteId()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "メモ",
            Index = 1
        });
        var service = new WorkspaceService(repository);
        var noteId = Guid.NewGuid();

        var note = await service.AddNoteAsync(
            workspace.Id,
            "個別配置",
            "本文",
            40,
            60,
            itemId: noteId);

        Assert.Equal(noteId, note.Id);
        var stored = Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(noteId, stored.Id);
    }

    [Fact]
    public async Task TextFileDropCanCreateEitherANoteOrAFileCard()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "テキスト取込",
            Index = 1
        });
        var notePath = Path.Combine(database.DirectoryPath, "note.txt");
        var filePath = Path.Combine(database.DirectoryPath, "file.txt");
        await File.WriteAllTextAsync(notePath, "先頭行\n2行目");
        await File.WriteAllTextAsync(filePath, "ファイル参照のまま");
        using var viewModel = CreateWorkspaceViewModel(new WorkspaceService(repository), workspace.Id);
        await viewModel.InitializeAsync();

        var saveCommand = Assert.IsType<AsyncRelayCommand>(viewModel.SaveDroppedShellItemsCommand);
        await saveCommand.ExecuteAsync(new WorkspaceShellDropRequest(
            [notePath],
            new WorkspaceCanvasPoint(120, 140),
            TargetFolderId: null,
            WorkspaceTextFileDropMode.ImportAsNote)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(viewModel.Items, item => item.Item is NoteItem);

        await saveCommand.ExecuteAsync(new WorkspaceShellDropRequest(
            [filePath],
            new WorkspaceCanvasPoint(320, 340),
            TargetFolderId: null,
            WorkspaceTextFileDropMode.PlaceAsFile)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, viewModel.Items.Count);
        Assert.False(viewModel.IsWorkspaceSaving);

        var note = Assert.IsType<NoteItem>(Assert.Single(viewModel.Items, item => item.Item is NoteItem).Item);
        var file = Assert.IsType<ShellItem>(Assert.Single(viewModel.Items, item => item.Item is ShellItem).Item);
        Assert.Equal("先頭行\n2行目", note.Content);
        Assert.Equal("先頭行", note.Title);
        Assert.Equal(Path.GetFullPath(filePath), file.TargetPath);
        Assert.Equal("ファイル参照のまま", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task ExistingTextFileCardConvertsToNoteWithoutChangingTheSourceFileOrPlacement()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "変換", Index = 1 });
        var folder = await repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = workspace.Id,
            Title = "格納先"
        });
        var path = Path.Combine(database.DirectoryPath, "document.txt");
        await File.WriteAllTextAsync(path, "変換後の先頭行\n本文");
        var createdAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var shellItem = Assert.Single(await repository.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                ParentFolderId = folder.Id,
                Title = "document.txt",
                TargetPath = path,
                Kind = ShellItemKind.File,
                Tags = ["保持するタグ"],
                X = 240,
                Y = 360,
                Width = 88,
                Height = 100,
                ZIndex = 7,
                CreatedAt = createdAt
            }
        ]));
        using var viewModel = CreateWorkspaceViewModel(
            new WorkspaceService(repository),
            workspace.Id,
            folder.Id);
        await viewModel.InitializeAsync();
        var card = Assert.Single(viewModel.Items);

        Assert.True(viewModel.ConvertShellTextFileToNoteCommand.CanExecute(card));
        viewModel.ConvertShellTextFileToNoteCommand.Execute(card);
        await WaitForAsync(() => viewModel.Items.Any(item => item.Id == shellItem.Id && item.Item is NoteItem));

        var converted = Assert.IsType<NoteItem>(Assert.Single(viewModel.Items).Item);
        Assert.Equal(shellItem.Id, converted.Id);
        Assert.Equal(folder.Id, converted.ParentFolderId);
        Assert.Equal((240d, 360d), (converted.X, converted.Y));
        Assert.Equal((88d, 100d, 7), (converted.Width, converted.Height, converted.ZIndex));
        Assert.Equal(createdAt, converted.CreatedAt);
        Assert.Equal(["保持するタグ"], converted.Tags);
        Assert.Equal("変換後の先頭行\n本文", converted.Content);
        Assert.Equal("変換後の先頭行", converted.Title);
        Assert.Equal("変換後の先頭行\n本文", await File.ReadAllTextAsync(path));

        var stored = Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == shellItem.Id));
        Assert.Equal(converted.Content, stored.Content);
        Assert.Equal(folder.Id, stored.ParentFolderId);
        Assert.DoesNotContain(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item is ShellItem && item.Id == shellItem.Id);
    }

    [Fact]
    public void LargeNoteKeepsFullContentButLimitsCanvasCardPreview()
    {
        var content = new string('長', WorkspaceItemViewModel.MaximumNoteCardPreviewCharacters + 80);
        var item = new WorkspaceItemViewModel(new NoteItem { Content = content });

        Assert.Equal(content, item.Note);
        Assert.Equal(content, item.ToolTipNote);
        Assert.Equal(WorkspaceItemViewModel.MaximumNoteCardPreviewCharacters + 1, item.CardPrimaryText.Length);
        Assert.EndsWith("…", item.CardPrimaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionGroupPersistsMultipleTabs()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Session",
            Index = 1
        });

        var splitGroupId = Guid.NewGuid().ToString("N");
        var containerA = Guid.NewGuid().ToString("N");
        var containerB = Guid.NewGuid().ToString("N");
        var sessionGroup = await repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = workspace.Id,
            Title = "読む記事",
            Tabs =
            [
                new SessionTab
                {
                    Url = "https://example.com/1",
                    Title = "記事1",
                    ContainerProfileId = containerA,
                    ThumbnailPngBase64 = "dGh1bWJuYWlsMQ==",
                    SplitGroupId = splitGroupId,
                    SplitPane = BrowserSplitViewPane.Left,
                    SplitOrientation = BrowserSplitViewOrientation.TopBottom,
                    SplitRatio = 0.65,
                    SplitActivePane = BrowserSplitViewPane.Right
                },
                new SessionTab
                {
                    Url = "C:\\work",
                    Title = "記事2",
                    ContainerProfileId = containerB,
                    IsFolderTab = true,
                    ThumbnailPngBase64 = "dGh1bWJuYWlsMg==",
                    SplitGroupId = splitGroupId,
                    SplitPane = BrowserSplitViewPane.Right,
                    SplitOrientation = BrowserSplitViewOrientation.TopBottom,
                    SplitRatio = 0.65,
                    SplitActivePane = BrowserSplitViewPane.Right
                }
            ]
        });

        var items = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var storedGroup = Assert.IsType<SessionGroupItem>(Assert.Single(items, x => x.Id == sessionGroup.Id));

        Assert.Equal(2, storedGroup.Tabs.Count);
        Assert.Equal([containerA, containerB], storedGroup.Tabs.Select(tab => tab.ContainerProfileId));
        Assert.Equal("https://example.com/1", storedGroup.Tabs[0].Url);
        Assert.Equal("記事1", storedGroup.Tabs[0].Title);
        Assert.Equal(0, storedGroup.Tabs[0].SortOrder);
        Assert.Equal("dGh1bWJuYWlsMQ==", storedGroup.Tabs[0].ThumbnailPngBase64);
        Assert.Equal(splitGroupId, storedGroup.Tabs[0].SplitGroupId);
        Assert.Equal(BrowserSplitViewPane.Left, storedGroup.Tabs[0].SplitPane);
        Assert.Equal(BrowserSplitViewOrientation.TopBottom, storedGroup.Tabs[0].SplitOrientation);
        Assert.Equal(0.65, storedGroup.Tabs[0].SplitRatio);
        Assert.Equal(BrowserSplitViewPane.Right, storedGroup.Tabs[0].SplitActivePane);
        Assert.Equal("C:\\work", storedGroup.Tabs[1].Url);
        Assert.Equal("記事2", storedGroup.Tabs[1].Title);
        Assert.Equal(1, storedGroup.Tabs[1].SortOrder);
        Assert.True(storedGroup.Tabs[1].IsFolderTab);
        Assert.Equal("dGh1bWJuYWlsMg==", storedGroup.Tabs[1].ThumbnailPngBase64);

        await repository.UpdateSessionTabThumbnailsAsync(
            storedGroup.Id,
            new Dictionary<Guid, string>
            {
                [storedGroup.Tabs[0].Id] = "dXBkYXRlZDE=",
                [storedGroup.Tabs[1].Id] = "dXBkYXRlZDI="
            });
        var updatedGroup = Assert.IsType<SessionGroupItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == sessionGroup.Id));
        Assert.Equal("dXBkYXRlZDE=", updatedGroup.Tabs[0].ThumbnailPngBase64);
        Assert.Equal("dXBkYXRlZDI=", updatedGroup.Tabs[1].ThumbnailPngBase64);
        Assert.Equal([containerA, containerB], updatedGroup.Tabs.Select(tab => tab.ContainerProfileId));

        var duplicate = Assert.IsType<SessionGroupItem>(await new WorkspaceService(repository)
            .DuplicateWorkspaceItemAsync(updatedGroup, 20, 30));
        var reloaded = Assert.IsType<SessionGroupItem>(Assert.Single(
            await new SQLiteWorkspaceRepository(database.DatabasePath).GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == duplicate.Id));
        Assert.Equal([containerA, containerB], reloaded.Tabs.Select(tab => tab.ContainerProfileId));
        Assert.Equal(updatedGroup.Tabs.Select(tab => tab.SplitGroupId), reloaded.Tabs.Select(tab => tab.SplitGroupId));
        Assert.All(reloaded.Tabs, tab => Assert.DoesNotContain(updatedGroup.Tabs, old => old.Id == tab.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerColumnMigrationPreservesLegacyRowsAndRetriesAfterRollback(bool failFirst)
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "legacy", Index = 1 });
        var token = Guid.NewGuid().ToString("N");
        var group = await repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = workspace.Id,
            Tabs = [new SessionTab { Url = "https://example.test/legacy", SplitGroupId = token,
                SplitPane = BrowserSplitViewPane.Left, SplitRatio = 0.65 }]
        });
        var oldTab = Assert.Single(group.Tabs);
        await using var keeper = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false
        }.ToString());
        await keeper.OpenAsync();
        await using var command = keeper.CreateCommand();
        // 専用DBを直前の列構成へ戻し、WALを持つ接続がある状態で追加移行を検証する。
        command.CommandText = "ALTER TABLE workspace_session_tabs DROP COLUMN container_profile_id; PRAGMA user_version = 0;";
        await command.ExecuteNonQueryAsync();
        var attempts = 0;
        var migration = new SQLiteWorkspaceRepository(database.DatabasePath, false, () =>
        {
            attempts++;
            if (failFirst && attempts == 1) throw new IOException("テスト用の移行確定失敗");
        });
        if (failFirst)
        {
            await Assert.ThrowsAsync<IOException>(() => migration.GetWorkspacesAsync());
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('workspace_session_tabs') WHERE name = 'container_profile_id';";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT url FROM workspace_session_tabs;";
            Assert.Equal(oldTab.Url, await command.ExecuteScalarAsync());
        }

        Assert.Equal(workspace.Id, Assert.Single(await migration.GetWorkspacesAsync()).Id);
        var restored = Assert.IsType<SessionGroupItem>(Assert.Single(await migration.GetWorkspaceItemsAsync(workspace.Id)));
        var restoredTab = Assert.Single(restored.Tabs);
        Assert.Equal(group.Id, restored.Id);
        Assert.Equal(oldTab.Id, restoredTab.Id);
        Assert.Equal(token, restoredTab.SplitGroupId);
        Assert.Equal(0.65, restoredTab.SplitRatio);
        Assert.Null(restoredTab.ContainerProfileId);
        Assert.Equal(oldTab.SavedAt, restoredTab.SavedAt);
        await migration.GetWorkspacesAsync();
        await new SQLiteWorkspaceRepository(database.DatabasePath, false, () => Assert.Fail("移行の二重実行"))
            .GetWorkspacesAsync();
        Assert.Equal(failFirst ? 2 : 1, attempts);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('workspace_session_tabs') WHERE name = 'container_profile_id';";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());

        // 明示された不明IDも保存値として保持し、復元時の保留判断へ渡す。
        restoredTab.ContainerProfileId = "deleted-or-unknown";
        var unknownCopy = await new WorkspaceService(migration).DuplicateWorkspaceItemAsync(restored, 0, 0);
        var unknown = Assert.IsType<SessionGroupItem>(Assert.Single(await migration.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == unknownCopy.Id));
        Assert.Equal("deleted-or-unknown", Assert.Single(unknown.Tabs).ContainerProfileId);
    }

    [Fact]
    public async Task YouTubeMiniPlayerSetPersistsPlaybackAndLayoutState()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "YouTube",
            Index = 1
        });

        var item = await repository.AddYouTubeMiniPlayerSetAsync(new YouTubeMiniPlayerSetItem
        {
            WorkspaceId = workspace.Id,
            Title = "比較動画",
            Players =
            [
                new YouTubeMiniPlayerWorkspaceState
                {
                    Url = "https://www.youtube.com/watch?v=abcdefghijk",
                    Title = "通常動画",
                    ContainerProfileId = "0123456789abcdef0123456789abcdef",
                    Left = 120,
                    Top = 160,
                    Width = 380,
                    Height = 221.625,
                    Volume = 0.4,
                    AudioBoostDecibels = 6,
                    PlaybackRate = 1.5,
                    LastPositionSeconds = 125.25,
                    DurationSeconds = 600,
                    IsPaused = true,
                    IsLooping = true,
                    IsTopmost = false
                },
                new YouTubeMiniPlayerWorkspaceState
                {
                    Url = "https://www.youtube.com/shorts/lmnopqrstuv",
                    Title = "Shorts",
                    IsShorts = true,
                    Left = 540,
                    Top = 120,
                    Width = 300,
                    Height = 519.333
                }
            ]
        });

        var items = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var stored = Assert.IsType<YouTubeMiniPlayerSetItem>(Assert.Single(items, x => x.Id == item.Id));

        Assert.Equal(2, stored.Players.Count);
        Assert.Equal(125.25, stored.Players[0].LastPositionSeconds);
        Assert.Equal(1.5, stored.Players[0].PlaybackRate);
        Assert.Equal(6, stored.Players[0].AudioBoostDecibels);
        Assert.True(stored.Players[0].IsLooping);
        Assert.False(stored.Players[0].IsTopmost);
        Assert.Equal(120, stored.Players[0].Left);
        Assert.True(stored.Players[1].IsShorts);
        Assert.Equal("0123456789abcdef0123456789abcdef", stored.Players[0].ContainerProfileId);

        var restored = stored.Players[0].CreatePlayerState(
            windowId: 7,
            new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(7, restored.WindowId);
        Assert.Equal(Guid.Empty, restored.SourceTabId);
        Assert.Equal(125.25, restored.LastPositionSeconds);
        Assert.Equal(6, restored.AudioBoostDecibels);
        Assert.Equal(380, restored.Width);
    }

    [Fact]
    public void YouTubeMiniPlayerWorkspaceStatePreservesSupportedAudioBoost()
    {
        var player = new TabBrowser.Models.YouTubeMiniPlayerState
        {
            AudioBoostDecibels = 9
        };

        var workspaceState = YouTubeMiniPlayerWorkspaceState.FromPlayerState(player);
        var restored = workspaceState.CreatePlayerState(1, DateTimeOffset.UtcNow);

        Assert.Equal(9, workspaceState.AudioBoostDecibels);
        Assert.Equal(9, restored.AudioBoostDecibels);

        workspaceState.AudioBoostDecibels = 8;
        Assert.Equal(0, workspaceState.CreatePlayerState(1, DateTimeOffset.UtcNow).AudioBoostDecibels);
    }

    [Fact]
    public async Task SearchItemsMatchesTitleUrlAndNote()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Search",
            Index = 1
        });

        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "OpenAI Docs",
            Url = "https://example.com/docs",
            Note = "research memo",
            Tags = ["api"]
        });
        var note = await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            Title = "読書メモ",
            Content = "QuickLook 設計メモ"
        });

        var titleMatches = await repository.SearchItemsAsync("openai");
        var urlMatches = await repository.SearchItemsAsync("example.com");
        var noteMatches = await repository.SearchItemsAsync("research memo");
        var contentMatches = await repository.SearchItemsAsync("QuickLook");

        Assert.Contains(titleMatches, x => x.Id == bookmark.Id);
        Assert.Contains(urlMatches, x => x.Id == bookmark.Id);
        Assert.Contains(noteMatches, x => x.Id == bookmark.Id);
        Assert.Contains(contentMatches, x => x.Id == note.Id);
    }

    [Fact]
    public async Task DeleteFolderKeepsChildrenInTrashAndRestoresParentMembership()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Folder",
            Index = 1
        });

        var folder = await repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = workspace.Id,
            Title = "親フォルダ"
        });
        var childBookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            ParentFolderId = folder.Id,
            Title = "子ブックマーク",
            Url = "https://example.com/child"
        });

        await repository.DeleteItemAsync(folder.Id);

        Assert.Empty(await repository.GetWorkspaceItemsAsync(workspace.Id));
        var trash = await new SQLiteWorkspaceRepository(database.DatabasePath).GetDeletedItemsAsync();
        Assert.Equal(2, trash.Count);
        var storedBookmark = Assert.IsType<BookmarkItem>(Assert.Single(trash, x => x.Id == childBookmark.Id));
        Assert.Equal(folder.Id, storedBookmark.ParentFolderId);
        Assert.Equal(2, (await repository.RestoreItemAsync(folder.Id, workspace.Id, null)).Count);
        var items = await repository.GetWorkspaceItemsAsync(workspace.Id);
        Assert.Contains(items, x => x.Id == folder.Id);
        Assert.Equal(folder.Id, Assert.Single(items, x => x.Id == childBookmark.Id).ParentFolderId);
    }

    [Fact]
    public async Task BatchMoveAndDeletePersistInSingleOperations()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Batch",
            Index = 1
        });
        var first = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "First",
            Url = "https://example.com/first"
        });
        var second = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "Second",
            Url = "https://example.com/second"
        });
        var remaining = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "Remaining",
            Url = "https://example.com/remaining"
        });

        await repository.UpdateItemPositionsAsync(
        [
            new WorkspaceItemPosition(first.Id, 120, 140),
            new WorkspaceItemPosition(second.Id, 260, 280)
        ]);

        var movedItems = await repository.GetWorkspaceItemsAsync(workspace.Id);
        Assert.Equal(120, Assert.Single(movedItems, item => item.Id == first.Id).X);
        Assert.Equal(280, Assert.Single(movedItems, item => item.Id == second.Id).Y);

        await repository.DeleteItemsAsync([first.Id, second.Id]);

        var storedItems = await repository.GetWorkspaceItemsAsync(workspace.Id);
        Assert.DoesNotContain(storedItems, item => item.Id == first.Id || item.Id == second.Id);
        Assert.Contains(storedItems, item => item.Id == remaining.Id);
    }

    [Fact]
    public async Task BatchPositionUpdateRollsBackWhenAnyCardIsMissing()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "Atomic", Index = 1 });
        var item = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "Stored",
            X = 40,
            Y = 60
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateItemPositionsAsync(
        [
            new WorkspaceItemPosition(item.Id, 400, 600),
            new WorkspaceItemPosition(Guid.NewGuid(), 800, 900)
        ]));

        var stored = Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id));
        Assert.Equal(40, stored.X);
        Assert.Equal(60, stored.Y);
    }

    [Fact]
    public async Task WorkspaceMoveKeepsFolderDescendantsTogether()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var source = await repository.CreateWorkspaceAsync(new Workspace { Name = "Source", Index = 1 });
        var target = await repository.CreateWorkspaceAsync(new Workspace { Name = "Target", Index = 2 });
        var folder = await repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = source.Id,
            Title = "資料",
            X = 20,
            Y = 30
        });
        var child = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = source.Id,
            ParentFolderId = folder.Id,
            Title = "子カード",
            Url = "https://example.com/child"
        });

        await repository.MoveItemsToWorkspaceAsync(
        [
            new WorkspaceItemMove(folder.Id, source.Id, null, target.Id, null, 240, 180),
            new WorkspaceItemMove(child.Id, source.Id, folder.Id, target.Id, folder.Id, child.X, child.Y)
        ]);

        Assert.Empty(await repository.GetWorkspaceItemsAsync(source.Id));
        var targetItems = await repository.GetWorkspaceItemsAsync(target.Id);
        var movedFolder = Assert.IsType<FolderItem>(Assert.Single(targetItems, item => item.Id == folder.Id));
        var movedChild = Assert.IsType<BookmarkItem>(Assert.Single(targetItems, item => item.Id == child.Id));
        Assert.Equal(240, movedFolder.X);
        Assert.Equal(180, movedFolder.Y);
        Assert.Equal(movedFolder.Id, movedChild.ParentFolderId);
    }

    [Fact]
    public async Task WorkspaceMovePreservesAllCardDisplayData()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var source = await repository.CreateWorkspaceAsync(new Workspace { Name = "Source", Index = 1 });
        var target = await repository.CreateWorkspaceAsync(new Workspace { Name = "Target", Index = 2 });
        var targetFolder = await repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = target.Id,
            Title = "Target folder"
        });
        var createdAt = new DateTimeOffset(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);
        var thumbnailUpdatedAt = createdAt.AddDays(1);
        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = source.Id,
            Title = "Bookmark title",
            Url = "https://example.com/article",
            Note = "Bookmark note",
            FaviconPngBase64 = "ZmF2aWNvbg==",
            ThumbnailPngBase64 = "dGh1bWJuYWls",
            ThumbnailUpdatedAt = thumbnailUpdatedAt,
            Tags = ["tag-a", "tag-b"],
            X = 10,
            Y = 20,
            Width = 240,
            Height = 140,
            ZIndex = 7,
            CreatedAt = createdAt
        });
        var session = await repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = source.Id,
            Title = "Session title",
            Tabs =
            [
                new SessionTab
                {
                    Url = "https://example.com/tab",
                    Title = "Session tab",
                    FaviconPngBase64 = "dGFiLWZhdmljb24=",
                    SortOrder = 1
                }
            ]
        });
        var playerSet = await repository.AddYouTubeMiniPlayerSetAsync(new YouTubeMiniPlayerSetItem
        {
            WorkspaceId = source.Id,
            Title = "Player set",
            Players =
            [
                new YouTubeMiniPlayerWorkspaceState
                {
                    Url = "https://www.youtube.com/watch?v=abcdefghijk",
                    Title = "Video",
                    LastPositionSeconds = 123,
                    Volume = 45,
                    IsTopmost = true
                }
            ]
        });
        var shell = Assert.Single(await repository.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = source.Id,
                Title = "Local file",
                TargetPath = Path.Combine(database.DirectoryPath, "video.webm"),
                Kind = ShellItemKind.File,
                ThumbnailPngBase64 = "c2hlbGwtdGh1bWI="
            }
        ]));

        var sourceItems = await repository.GetWorkspaceItemsAsync(source.Id);
        var moves = sourceItems.Select((item, index) => new WorkspaceItemMove(
            item.Id,
            source.Id,
            item.ParentFolderId,
            target.Id,
            targetFolder.Id,
            400 + (index * 120),
            300 + (index * 40))).ToList();
        var movedItems = await repository.MoveItemsToWorkspaceAsync(moves);

        Assert.Empty(await repository.GetWorkspaceItemsAsync(source.Id));
        Assert.Equal(4, movedItems.Count);
        var movedBookmark = Assert.IsType<BookmarkItem>(Assert.Single(movedItems, item => item.Id == bookmark.Id));
        Assert.Equal(targetFolder.Id, movedBookmark.ParentFolderId);
        Assert.Equal("Bookmark title", movedBookmark.Title);
        Assert.Equal("https://example.com/article", movedBookmark.Url);
        Assert.Equal("Bookmark note", movedBookmark.Note);
        Assert.Equal("ZmF2aWNvbg==", movedBookmark.FaviconPngBase64);
        Assert.Equal("dGh1bWJuYWls", movedBookmark.ThumbnailPngBase64);
        Assert.Equal(thumbnailUpdatedAt, movedBookmark.ThumbnailUpdatedAt);
        Assert.Equal(["tag-a", "tag-b"], movedBookmark.Tags);
        Assert.Equal(240, movedBookmark.Width);
        Assert.Equal(140, movedBookmark.Height);
        Assert.Equal(7, movedBookmark.ZIndex);
        Assert.Equal(createdAt, movedBookmark.CreatedAt);

        var movedSession = Assert.IsType<SessionGroupItem>(Assert.Single(movedItems, item => item.Id == session.Id));
        Assert.Equal("Session tab", Assert.Single(movedSession.Tabs).Title);
        var movedPlayerSet = Assert.IsType<YouTubeMiniPlayerSetItem>(Assert.Single(movedItems, item => item.Id == playerSet.Id));
        Assert.Equal(123, Assert.Single(movedPlayerSet.Players).LastPositionSeconds);
        var movedShell = Assert.IsType<ShellItem>(Assert.Single(movedItems, item => item.Id == shell.Id));
        Assert.Equal(shell.TargetPath, movedShell.TargetPath);
        Assert.Equal("c2hlbGwtdGh1bWI=", movedShell.ThumbnailPngBase64);
    }

    [Fact]
    public async Task WorkspaceMoveRollsBackWhenAnyItemIsMissing()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var source = await repository.CreateWorkspaceAsync(new Workspace { Name = "Source", Index = 1 });
        var target = await repository.CreateWorkspaceAsync(new Workspace { Name = "Target", Index = 2 });
        var bookmark = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = source.Id,
            Title = "Keep",
            X = 10,
            Y = 20
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.MoveItemsToWorkspaceAsync(
        [
            new WorkspaceItemMove(bookmark.Id, source.Id, null, target.Id, null, 500, 400),
            new WorkspaceItemMove(Guid.NewGuid(), source.Id, null, target.Id, null, 620, 400)
        ]));

        var stored = Assert.Single(await repository.GetWorkspaceItemsAsync(source.Id));
        Assert.Equal(bookmark.Id, stored.Id);
        Assert.Equal(10, stored.X);
        Assert.Equal(20, stored.Y);
        Assert.Empty(await repository.GetWorkspaceItemsAsync(target.Id));
    }

    [Fact]
    public void WorkspaceCardDragPayloadIncludesFolderDescendantsAndRelativePositions()
    {
        var workspaceId = Guid.NewGuid();
        var folder = new FolderItem
        {
            WorkspaceId = workspaceId,
            Title = "Folder",
            X = 100,
            Y = 80
        };
        var sibling = new NoteItem
        {
            WorkspaceId = workspaceId,
            Title = "Note",
            X = 220,
            Y = 150
        };
        var child = new BookmarkItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = folder.Id,
            Title = "Child"
        };

        var payload = WorkspaceCardDragPayload.Create(
            workspaceId,
            [folder, sibling, child],
            [folder, sibling],
            new WorkspaceCanvasPoint(125, 105));

        Assert.NotNull(payload);
        Assert.Equal(2, payload.Roots.Count);
        Assert.Equal(3, payload.Items.Count);
        Assert.Equal(25, payload.GrabOffsetX);
        Assert.Equal(25, payload.GrabOffsetY);
        Assert.Equal(120, Assert.Single(payload.Roots, root => root.Item.Id == sibling.Id).OffsetX);
        Assert.Equal(70, Assert.Single(payload.Roots, root => root.Item.Id == sibling.Id).OffsetY);
        Assert.Contains(payload.Items, item => item.Id == child.Id);
    }

    [Fact]
    public void WorkspaceCardDragFindsLogicalFolderUnderDropPoint()
    {
        var workspaceId = Guid.NewGuid();
        var folder = new FolderItem
        {
            WorkspaceId = workspaceId,
            Title = "Folder",
            X = 100,
            Y = 80
        };
        var bookmark = new BookmarkItem
        {
            WorkspaceId = workspaceId,
            Title = "Bookmark",
            X = 220,
            Y = 150
        };

        Assert.Equal(
            folder.Id,
            WorkspaceCardDragPayload.FindFolderDropTargetId(
                [folder, bookmark],
                new HashSet<Guid>(),
                new WorkspaceCanvasPoint(120, 100)));
        Assert.Null(WorkspaceCardDragPayload.FindFolderDropTargetId(
            [folder, bookmark],
            new HashSet<Guid> { folder.Id },
            new WorkspaceCanvasPoint(120, 100)));
        Assert.Null(WorkspaceCardDragPayload.FindFolderDropTargetId(
            [folder, bookmark],
            new HashSet<Guid>(),
            new WorkspaceCanvasPoint(210, 100)));
    }

    [Fact]
    public void WorkspaceCardDropRequestUsesFolderDefaultPositionOnlyWhenRequested()
    {
        var workspaceId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var item = new BookmarkItem
        {
            WorkspaceId = workspaceId,
            Title = "Bookmark",
            X = 100,
            Y = 80
        };
        var payload = Assert.IsType<WorkspaceCardDragPayload>(WorkspaceCardDragPayload.Create(
            workspaceId,
            [item],
            [item],
            new WorkspaceCanvasPoint(125, 105)));
        var folderDefaultPoint = new WorkspaceCanvasPoint(32, 32);

        var cursorRequest = new WorkspaceCardDropRequest(
            payload,
            workspaceId,
            new WorkspaceCanvasPoint(500, 400),
            folderId);
        var folderRequest = new WorkspaceCardDropRequest(
            payload,
            workspaceId,
            new WorkspaceCanvasPoint(500, 400),
            folderId,
            UseFolderDefaultPosition: true);

        Assert.Equal(new WorkspaceCanvasPoint(475, 375), cursorRequest.ResolveAnchor(folderDefaultPoint));
        Assert.Equal(folderDefaultPoint, folderRequest.ResolveAnchor(folderDefaultPoint));
    }

    [Fact]
    public void WorkspaceCardPlacementKeepsGroupInsideCanvasAndAvoidsExistingCards()
    {
        var sourceWorkspaceId = Guid.NewGuid();
        var targetWorkspaceId = Guid.NewGuid();
        var first = new BookmarkItem
        {
            WorkspaceId = sourceWorkspaceId,
            Title = "First",
            X = 100,
            Y = 80
        };
        var second = new NoteItem
        {
            WorkspaceId = sourceWorkspaceId,
            Title = "Second",
            X = 220,
            Y = 150
        };
        var payload = Assert.IsType<WorkspaceCardDragPayload>(WorkspaceCardDragPayload.Create(
            sourceWorkspaceId,
            [first, second],
            [first, second],
            new WorkspaceCanvasPoint(125, 105)));
        var existing = new BookmarkItem
        {
            WorkspaceId = targetWorkspaceId,
            X = 4592,
            Y = 3030
        };

        var moves = WorkspaceCardPlacement.CreateMoves(
            payload,
            targetWorkspaceId,
            null,
            new WorkspaceCanvasPoint(6000, 4000),
            [existing]);
        var firstMove = Assert.Single(moves, move => move.ItemId == first.Id);
        var secondMove = Assert.Single(moves, move => move.ItemId == second.Id);

        Assert.Equal(120, secondMove.X - firstMove.X);
        Assert.Equal(70, secondMove.Y - firstMove.Y);
        Assert.InRange(firstMove.X, 0, WorkspaceCardPlacement.CanvasWidth - 208);
        Assert.InRange(firstMove.Y, 0, WorkspaceCardPlacement.CanvasHeight - 170);
        Assert.False(firstMove.X < existing.X + 88
            && firstMove.X + 88 > existing.X
            && firstMove.Y < existing.Y + 100
            && firstMove.Y + 100 > existing.Y);
    }

    [Fact]
    public async Task WorkspaceMoveSynchronizesOpenViewsWithoutReplacingCardViewModels()
    {
        using var database = new TempDatabase();
        var setupRepository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await setupRepository.CreateWorkspaceAsync(new Workspace { Name = "Workspace", Index = 1 });
        var folder = await setupRepository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = workspace.Id,
            Title = "Folder"
        });
        var bookmark = await setupRepository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "Bookmark",
            FaviconPngBase64 = "ZmF2aWNvbg==",
            ThumbnailPngBase64 = "dGh1bWJuYWls",
            X = 40,
            Y = 60
        });
        var firstService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var secondService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var folderService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var firstRootView = CreateWorkspaceViewModel(firstService, workspace.Id);
        var secondRootView = CreateWorkspaceViewModel(secondService, workspace.Id);
        var folderView = CreateWorkspaceViewModel(folderService, workspace.Id, folder.Id);
        await firstRootView.InitializeAsync();
        await secondRootView.InitializeAsync();
        await folderView.InitializeAsync();
        var firstCard = Assert.Single(firstRootView.Items, item => item.Id == bookmark.Id);
        var secondCard = Assert.Single(secondRootView.Items, item => item.Id == bookmark.Id);

        await firstService.MoveItemsToWorkspaceAsync(
        [
            new WorkspaceItemMove(bookmark.Id, workspace.Id, null, workspace.Id, folder.Id, 120, 140)
        ]);

        Assert.DoesNotContain(firstRootView.Items, item => item.Id == bookmark.Id);
        Assert.DoesNotContain(secondRootView.Items, item => item.Id == bookmark.Id);
        Assert.Single(folderView.Items, item => item.Id == bookmark.Id);

        await secondService.MoveItemsToWorkspaceAsync(
        [
            new WorkspaceItemMove(bookmark.Id, workspace.Id, folder.Id, workspace.Id, null, 300, 320)
        ]);

        var restoredFirstCard = Assert.Single(firstRootView.Items, item => item.Id == bookmark.Id);
        var restoredSecondCard = Assert.Single(secondRootView.Items, item => item.Id == bookmark.Id);
        Assert.Same(firstCard, restoredFirstCard);
        Assert.Same(secondCard, restoredSecondCard);
        Assert.Equal(300, restoredFirstCard.X);
        Assert.Equal(320, restoredFirstCard.Y);
        Assert.DoesNotContain(folderView.Items, item => item.Id == bookmark.Id);
    }

    [Fact]
    public async Task WorkspaceMovePreservesWarmCollectionsAndUnchangedCardsAcrossRoundTrips()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var source = await repository.CreateWorkspaceAsync(new Workspace { Name = "Source", Index = 1 });
        var target = await repository.CreateWorkspaceAsync(new Workspace { Name = "Target", Index = 2 });
        var moving = await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = source.Id,
            Title = "移動するメモ",
            Content = new string('あ', 18000),
            FontSize = 20
        });
        await repository.AddNoteAsync(new NoteItem { WorkspaceId = source.Id, Title = "残すカード" });
        await repository.AddNoteAsync(new NoteItem { WorkspaceId = target.Id, Title = "移動先のカード" });
        var service = new WorkspaceService(repository);
        using var view = CreateWorkspaceViewModel(service, source.Id);
        using var otherView = CreateWorkspaceViewModel(service, target.Id);
        await view.InitializeAsync();
        await otherView.InitializeAsync();
        var sourceItems = view.Items;
        var unchangedSource = Assert.Single(sourceItems, item => item.Id != moving.Id);
        var otherItems = otherView.Items;
        var unchangedOther = Assert.Single(otherItems);
        await view.LoadWorkspaceAsync(target.Id);
        var targetItems = view.Items;
        var unchangedTarget = Assert.Single(targetItems);
        await view.LoadWorkspaceAsync(source.Id);
        view.SelectedItem = unchangedSource;
        var resetCount = 0;
        sourceItems.CollectionChanged += (_, args) => resetCount += args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;
        targetItems.CollectionChanged += (_, args) => resetCount += args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        for (var index = 0; index < 3; index++)
        {
            await service.MoveItemsToWorkspaceAsync(
                [new WorkspaceItemMove(moving.Id, source.Id, null, target.Id, null, 200, 300)]);
            Assert.Same(sourceItems, view.Items);
            Assert.Same(unchangedSource, Assert.Single(view.Items));
            if (index == 0)
            {
                Assert.Same(unchangedSource, view.SelectedItem);
            }

            Assert.Same(otherItems, otherView.Items);
            Assert.Contains(unchangedOther, otherView.Items);
            await view.LoadWorkspaceAsync(target.Id);
            Assert.Same(targetItems, view.Items);
            Assert.Contains(unchangedTarget, view.Items);
            var moved = Assert.IsType<NoteItem>(Assert.Single(view.Items, item => item.Id == moving.Id).Item);
            Assert.Equal(moving.Content, moved.Content);
            Assert.Equal(20, moved.FontSize);
            Assert.Equal(200, moved.X);
            Assert.Equal(300, moved.Y);

            await service.MoveItemsToWorkspaceAsync(
                [new WorkspaceItemMove(moving.Id, target.Id, null, source.Id, null, 40, 60)]);
            Assert.Same(targetItems, view.Items);
            Assert.Same(unchangedTarget, Assert.Single(view.Items));
            await view.LoadWorkspaceAsync(source.Id);
            Assert.Same(sourceItems, view.Items);
            Assert.Equal(2, view.Items.Count);
            Assert.Equal(40, Assert.Single(view.Items, item => item.Id == moving.Id).X);
        }

        Assert.Equal(0, resetCount);
    }

    [Fact]
    public async Task WorkspaceMoveIntoFolderPreservesBothCachedCollections()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "Workspace", Index = 1 });
        var folder = await repository.AddFolderAsync(new FolderItem { WorkspaceId = workspace.Id, Title = "Folder" });
        var note = await repository.AddNoteAsync(new NoteItem { WorkspaceId = workspace.Id, Title = "Note" });
        var service = new WorkspaceService(repository);
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var rootItems = view.Items;
        var card = Assert.Single(rootItems, item => item.Id == note.Id);
        await view.LoadWorkspaceAsync(workspace.Id, folderId: folder.Id);
        var folderItems = view.Items;
        await view.LoadWorkspaceAsync(workspace.Id);

        await service.MoveItemsToWorkspaceAsync(
            [new WorkspaceItemMove(note.Id, workspace.Id, null, workspace.Id, folder.Id, 80, 90)]);
        Assert.Same(rootItems, view.Items);
        Assert.DoesNotContain(card, rootItems);
        Assert.Same(card, Assert.Single(folderItems));
        await view.LoadWorkspaceAsync(workspace.Id, folderId: folder.Id);
        Assert.Same(folderItems, view.Items);

        await service.MoveItemsToWorkspaceAsync(
            [new WorkspaceItemMove(note.Id, workspace.Id, folder.Id, workspace.Id, null, 30, 40)]);
        Assert.Empty(folderItems);
        Assert.Contains(card, rootItems);
        await view.LoadWorkspaceAsync(workspace.Id);
        Assert.Same(rootItems, view.Items);
    }

    [Fact]
    public async Task WorkspaceContentChangesSynchronizeAcrossOpenViews()
    {
        using var database = new TempDatabase();
        var setupRepository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await setupRepository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Workspace",
            Index = 1
        });
        var firstService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var secondService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        using var firstView = CreateWorkspaceViewModel(firstService, workspace.Id);
        using var secondView = CreateWorkspaceViewModel(secondService, workspace.Id);
        await firstView.InitializeAsync();
        await secondView.InitializeAsync();
        var firstItems = firstView.Items;
        var secondItems = secondView.Items;

        var note = await firstService.AddNoteAsync(
            workspace.Id,
            "最初のメモ",
            "1行目",
            40,
            60);
        var firstCard = Assert.Single(firstView.Items, item => item.Id == note.Id);
        var secondCard = Assert.Single(secondView.Items, item => item.Id == note.Id);
        Assert.Same(firstItems, firstView.Items);
        Assert.Same(secondItems, secondView.Items);

        await firstService.UpdateNoteAsync(note.Id, "更新後のメモ", "1行目\n2行目", 20);

        Assert.Same(firstCard, Assert.Single(firstView.Items, item => item.Id == note.Id));
        Assert.Same(secondCard, Assert.Single(secondView.Items, item => item.Id == note.Id));
        Assert.Equal("更新後のメモ", firstCard.Title);
        Assert.Equal("更新後のメモ", secondCard.Title);
        Assert.Equal("1行目\n2行目", firstCard.Note);
        Assert.Equal("1行目\n2行目", secondCard.Note);
        Assert.Equal(20, firstCard.NoteFontSize);
        Assert.Equal(20, secondCard.NoteFontSize);

        await firstService.RenameItemAsync(note.Id, "同期したタイトル");
        Assert.Equal("同期したタイトル", firstCard.Title);
        Assert.Equal("同期したタイトル", secondCard.Title);

        await firstService.UpdateItemFaviconAsync(note.Id, "ZmF2aWNvbg==");
        var thumbnailUpdatedAt = DateTimeOffset.Now;
        await firstService.UpdateItemThumbnailAsync(note.Id, "dGh1bWJuYWls", thumbnailUpdatedAt);

        Assert.Equal("ZmF2aWNvbg==", firstCard.Item.FaviconPngBase64);
        Assert.Equal("ZmF2aWNvbg==", secondCard.Item.FaviconPngBase64);
        Assert.Equal("dGh1bWJuYWls", firstCard.Item.ThumbnailPngBase64);
        Assert.Equal("dGh1bWJuYWls", secondCard.Item.ThumbnailPngBase64);

        await firstService.DeleteItemAsync(note.Id);

        Assert.DoesNotContain(firstView.Items, item => item.Id == note.Id);
        Assert.DoesNotContain(secondView.Items, item => item.Id == note.Id);
        Assert.Same(firstItems, firstView.Items);
        Assert.Same(secondItems, secondView.Items);
    }

    [Fact]
    public async Task InactiveWorkspaceDefersVisibleCollectionChangesUntilReactivated()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Workspace",
            Index = 1
        });
        var service = new WorkspaceService(repository);
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var visibleItems = view.Items;

        view.SetViewActive(false);
        var note = await service.AddNoteAsync(
            workspace.Id,
            "非表示中に追加",
            "本文",
            40,
            60);

        Assert.DoesNotContain(visibleItems, item => item.Id == note.Id);

        view.SetViewActive(true);

        Assert.Same(visibleItems, view.Items);
        Assert.Contains(visibleItems, item => item.Id == note.Id);

        view.SetViewActive(false);
        await service.DeleteItemAsync(note.Id);
        Assert.Contains(visibleItems, item => item.Id == note.Id);

        view.SetViewActive(true);

        Assert.Same(visibleItems, view.Items);
        Assert.DoesNotContain(visibleItems, item => item.Id == note.Id);
    }

    [Fact]
    public async Task WorkspaceChangeUpdatesOnlyTheActiveVisibleCollectionAcrossManyWindows()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Workspace",
            Index = 1
        });
        var service = new WorkspaceService(repository);
        var views = Enumerable.Range(0, 10)
            .Select(_ => CreateWorkspaceViewModel(service, workspace.Id))
            .ToList();
        try
        {
            foreach (var view in views)
            {
                await view.InitializeAsync();
            }

            foreach (var view in views.Skip(1))
            {
                view.SetViewActive(false);
            }

            var collectionChanges = new int[views.Count];
            for (var index = 0; index < views.Count; index++)
            {
                var capturedIndex = index;
                views[index].Items.CollectionChanged += (_, _) => collectionChanges[capturedIndex]++;
            }

            var note = await service.AddNoteAsync(
                workspace.Id,
                "複数Windowの同期確認",
                "本文",
                40,
                60);

            Assert.Equal(1, collectionChanges[0]);
            Assert.All(collectionChanges.Skip(1), count => Assert.Equal(0, count));
            Assert.Contains(views[0].Items, item => item.Id == note.Id);
            Assert.All(views.Skip(1), view =>
                Assert.DoesNotContain(view.Items, item => item.Id == note.Id));

            foreach (var view in views.Skip(1))
            {
                view.SetViewActive(true);
                Assert.Contains(view.Items, item => item.Id == note.Id);
            }
        }
        finally
        {
            foreach (var view in views)
            {
                view.Dispose();
            }
        }
    }

    [Fact]
    public async Task WorkspaceDerivedCardChangesSynchronizeAcrossOpenViews()
    {
        using var database = new TempDatabase();
        var setupRepository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await setupRepository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Workspace",
            Index = 1
        });
        var firstService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var secondService = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        using var firstView = CreateWorkspaceViewModel(firstService, workspace.Id);
        using var secondView = CreateWorkspaceViewModel(secondService, workspace.Id);
        await firstView.InitializeAsync();
        await secondView.InitializeAsync();

        var shell = Assert.Single(await firstService.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "参照先",
                TargetPath = @"C:\old",
                Kind = ShellItemKind.Directory
            }
        ]));
        await firstService.UpdateShellItemTargetAsync(shell.Id, @"C:\new", ShellItemKind.File);

        var firstShell = Assert.Single(firstView.Items, item => item.Id == shell.Id);
        var secondShell = Assert.Single(secondView.Items, item => item.Id == shell.Id);
        Assert.Equal(@"C:\new", Assert.IsType<ShellItem>(firstShell.Item).TargetPath);
        Assert.Equal(@"C:\new", Assert.IsType<ShellItem>(secondShell.Item).TargetPath);
        Assert.Equal(ShellItemKind.File, Assert.IsType<ShellItem>(firstShell.Item).Kind);
        Assert.Equal(ShellItemKind.File, Assert.IsType<ShellItem>(secondShell.Item).Kind);

        var sessionTab = new SessionTab
        {
            Url = "https://example.com/",
            Title = "Example"
        };
        var session = await firstService.AddSessionGroupAsync(
            workspace.Id,
            "Session",
            [sessionTab],
            120,
            140);
        await firstService.UpdateSessionTabThumbnailsAsync(
            session.Id,
            new Dictionary<Guid, string> { [sessionTab.Id] = "dGh1bWJuYWls" });

        var firstSession = Assert.IsType<SessionGroupItem>(
            Assert.Single(firstView.Items, item => item.Id == session.Id).Item);
        var secondSession = Assert.IsType<SessionGroupItem>(
            Assert.Single(secondView.Items, item => item.Id == session.Id).Item);
        Assert.Equal("dGh1bWJuYWls", Assert.Single(firstSession.Tabs).ThumbnailPngBase64);
        Assert.Equal("dGh1bWJuYWls", Assert.Single(secondSession.Tabs).ThumbnailPngBase64);
    }

    [Fact]
    public async Task WorkspaceWheelQueueCoalescesRapidInputToLatestWorkspace()
    {
        using var database = new TempDatabase();
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var firstWorkspace = await service.CreateWorkspaceAsync("Workspace 1", 1);
        var viewModel = CreateWorkspaceViewModel(service, firstWorkspace.Id);
        await viewModel.InitializeAsync();

        Assert.Equal(1, viewModel.CurrentWorkspace?.Index);
        Assert.True(viewModel.QueueWorkspaceWheelSwitch(1));
        Assert.True(viewModel.QueueWorkspaceWheelSwitch(1));
        Assert.True(viewModel.QueueWorkspaceWheelSwitch(1));

        await WaitForAsync(() => viewModel.CurrentWorkspace?.Index == 4 && !viewModel.IsLoading);

        Assert.Equal(4, viewModel.CurrentWorkspace?.Index);
        Assert.Equal(4, Assert.Single(viewModel.Workspaces, workspace => workspace.IsSelected).Index);
    }

    [Fact]
    public async Task CachedWorkspaceSwitchReusesCardsAndUpdatesWheelContentImmediately()
    {
        using var database = new TempDatabase();
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var firstWorkspace = await service.CreateWorkspaceAsync("Workspace 1", 1);
        var secondWorkspace = await service.CreateWorkspaceAsync("Workspace 2", 2);
        await service.AddBookmarkAsync(firstWorkspace.Id, "First", "https://first.example/", 40, 60);
        await service.AddBookmarkAsync(secondWorkspace.Id, "Second", "https://second.example/", 80, 100);
        using var viewModel = CreateWorkspaceViewModel(service, firstWorkspace.Id);
        await viewModel.InitializeAsync();

        var firstItems = viewModel.Items;
        var firstCard = Assert.Single(firstItems);
        await viewModel.LoadWorkspaceAsync(secondWorkspace.Id);
        var secondItems = viewModel.Items;
        var secondCard = Assert.Single(secondItems);

        await viewModel.LoadWorkspaceAsync(firstWorkspace.Id);
        Assert.Same(firstItems, viewModel.Items);
        Assert.Same(firstCard, Assert.Single(viewModel.Items));

        Assert.True(viewModel.QueueWorkspaceWheelSwitch(1));
        Assert.Equal(secondWorkspace.Id, viewModel.CurrentWorkspace?.Id);
        Assert.Same(secondItems, viewModel.Items);
        Assert.Same(secondCard, Assert.Single(viewModel.Items));
    }

    [Fact]
    public async Task ArrangementKeepsFirstPositionsAcrossModesAndRestoresThemAtomically()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "Arrange", Index = 1 });
        var first = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "C",
            X = 120,
            Y = 104
        });
        var second = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "A",
            X = 320,
            Y = 104
        });
        var later = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "B",
            X = 520,
            Y = 208
        });
        var viewModel = CreateWorkspaceViewModel(new WorkspaceService(repository), workspace.Id);
        await viewModel.InitializeAsync();
        var firstCard = Assert.Single(viewModel.Items, item => item.Id == first.Id);
        var secondCard = Assert.Single(viewModel.Items, item => item.Id == second.Id);
        viewModel.SetSelectedItems([firstCard, secondCard], firstCard);

        viewModel.ArrangeItemsCommand.Execute(new WorkspaceArrangeRequest(
            WorkspaceArrangeMode.CurrentPosition,
            WorkspaceArrangeScope.Selection,
            new WorkspaceCanvasRect(0, 0, 800, 600)));
        await WaitForAsync(() => viewModel.HasArrangeSnapshot && !viewModel.IsWorkspaceSaving);

        viewModel.ArrangeItemsCommand.Execute(new WorkspaceArrangeRequest(
            WorkspaceArrangeMode.Title,
            WorkspaceArrangeScope.All,
            new WorkspaceCanvasRect(0, 0, 800, 600)));
        await WaitForAsync(() => viewModel.StatusText.Contains("名前順", StringComparison.Ordinal));
        Assert.True(viewModel.HasArrangeSnapshot);

        viewModel.RestoreLastArrangeCommand.Execute(null);
        await WaitForAsync(() => !viewModel.HasArrangeSnapshot && !viewModel.IsWorkspaceSaving);

        Assert.Equal((120d, 104d), (firstCard.X, firstCard.Y));
        Assert.Equal((320d, 104d), (secondCard.X, secondCard.Y));
        var laterCard = Assert.Single(viewModel.Items, item => item.Id == later.Id);
        Assert.Equal((520d, 208d), (laterCard.X, laterCard.Y));
        var stored = (await repository.GetWorkspaceItemsAsync(workspace.Id)).ToDictionary(static item => item.Id);
        Assert.Equal((120d, 104d), (stored[first.Id].X, stored[first.Id].Y));
        Assert.Equal((320d, 104d), (stored[second.Id].X, stored[second.Id].Y));
        Assert.Equal((520d, 208d), (stored[later.Id].X, stored[later.Id].Y));
    }

    [Fact]
    public async Task UnavailableSelectionArrangementDoesNotChangeUiOrDatabase()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = "Blocked", Index = 1 });
        var first = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "A",
            X = 120,
            Y = 160
        });
        var second = await repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "B",
            X = 120,
            Y = 160
        });
        var viewModel = CreateWorkspaceViewModel(new WorkspaceService(repository), workspace.Id);
        await viewModel.InitializeAsync();
        viewModel.SetSelectedItems(viewModel.Items, viewModel.Items[0]);

        viewModel.ArrangeItemsCommand.Execute(new WorkspaceArrangeRequest(
            WorkspaceArrangeMode.Title,
            WorkspaceArrangeScope.Selection,
            new WorkspaceCanvasRect(0, 0, 800, 600)));

        Assert.Equal(WorkspaceArrangementService.SelectionAreaUnavailableMessage, viewModel.StatusText);
        Assert.False(viewModel.HasArrangeSnapshot);
        Assert.All(viewModel.Items, item => Assert.Equal((120d, 160d), (item.X, item.Y)));
        var stored = await repository.GetWorkspaceItemsAsync(workspace.Id);
        Assert.All(stored, item => Assert.Equal((120d, 160d), (item.X, item.Y)));
    }

    [Fact]
    public async Task PositionChangesSynchronizeOpenViewsAndManualMoveRemovesOnlyThatCardFromHistory()
    {
        using var database = new TempDatabase();
        var setupRepository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await setupRepository.CreateWorkspaceAsync(new Workspace { Name = "Sync", Index = 1 });
        var first = await setupRepository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "B",
            X = 200,
            Y = 200
        });
        await setupRepository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspace.Id,
            Title = "A",
            X = 400,
            Y = 200
        });
        var firstView = CreateWorkspaceViewModel(
            new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath)),
            workspace.Id);
        var secondView = CreateWorkspaceViewModel(
            new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath)),
            workspace.Id);
        await firstView.InitializeAsync();
        await secondView.InitializeAsync();

        firstView.ArrangeItemsCommand.Execute(new WorkspaceArrangeRequest(
            WorkspaceArrangeMode.Title,
            WorkspaceArrangeScope.All,
            new WorkspaceCanvasRect(0, 0, 800, 600)));
        await WaitForAsync(() => firstView.HasArrangeSnapshot && !firstView.IsWorkspaceSaving);
        var firstCard = Assert.Single(firstView.Items, item => item.Id == first.Id);
        var synchronizedCard = Assert.Single(secondView.Items, item => item.Id == first.Id);
        await WaitForAsync(() => synchronizedCard.X == firstCard.X && synchronizedCard.Y == firstCard.Y);

        firstCard.X += 80;
        firstView.MoveItemCommand.Execute(firstCard);
        await WaitForAsync(() => !firstView.IsWorkspaceSaving
            && synchronizedCard.X == firstCard.X
            && synchronizedCard.Y == firstCard.Y);
        Assert.True(firstView.HasArrangeSnapshot);
        Assert.Equal(firstCard.X, synchronizedCard.X);
        Assert.Equal(firstCard.Y, synchronizedCard.Y);

        var manuallyMovedPosition = (firstCard.X, firstCard.Y);
        firstView.RestoreLastArrangeCommand.Execute(null);
        await WaitForAsync(() => !firstView.HasArrangeSnapshot && !firstView.IsWorkspaceSaving);
        Assert.Equal(manuallyMovedPosition, (firstCard.X, firstCard.Y));
    }

    [Fact]
    public async Task WorkspaceMoveKeepsOpenFolderViewWhenFolderChangesWorkspace()
    {
        using var database = new TempDatabase();
        var setupRepository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var source = await setupRepository.CreateWorkspaceAsync(new Workspace { Name = "Source", Index = 1 });
        var target = await setupRepository.CreateWorkspaceAsync(new Workspace { Name = "Target", Index = 2 });
        var folder = await setupRepository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = source.Id,
            Title = "Folder",
            X = 20,
            Y = 30
        });
        var bookmark = await setupRepository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = source.Id,
            ParentFolderId = folder.Id,
            Title = "Child",
            ThumbnailPngBase64 = "dGh1bWJuYWls"
        });
        var service = new WorkspaceService(new SQLiteWorkspaceRepository(database.DatabasePath));
        var folderView = CreateWorkspaceViewModel(service, source.Id, folder.Id);
        await folderView.InitializeAsync();
        var childCard = Assert.Single(folderView.Items, item => item.Id == bookmark.Id);

        await service.MoveItemsToWorkspaceAsync(
        [
            new WorkspaceItemMove(folder.Id, source.Id, null, target.Id, null, 500, 400),
            new WorkspaceItemMove(bookmark.Id, source.Id, folder.Id, target.Id, folder.Id, bookmark.X, bookmark.Y)
        ]);

        Assert.Equal(target.Id, folderView.CurrentWorkspace?.Id);
        Assert.Equal(folder.Id, folderView.CurrentFolderId);
        Assert.Same(childCard, Assert.Single(folderView.Items, item => item.Id == bookmark.Id));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void WorkspaceCardWindowDragStartsOutsideCanvasOrOverDifferentWindow(
        bool isPointerInsideCanvas,
        bool isPointerOverDifferentWindow,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkspaceCardDragPayload.ShouldStartWindowDrag(
                isPointerInsideCanvas,
                isPointerOverDifferentWindow));
    }

    [Fact]
    public async Task RepositoryUsesWalJournalMode()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);

        await repository.GetWorkspacesAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = await command.ExecuteScalarAsync();

        Assert.Equal("wal", Assert.IsType<string>(journalMode), ignoreCase: true);
    }

    [Fact]
    public async Task ShellItemsPersistPathKindAndSupportPathSearch()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "Desktop",
            Index = 1
        });
        var originalPath = Path.Combine(database.DirectoryPath, "資料.txt");
        var changedPath = Path.Combine(database.DirectoryPath, "資料-更新.txt");
        await File.WriteAllTextAsync(originalPath, "test");

        var saved = await repository.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "資料",
                TargetPath = originalPath,
                Kind = ShellItemKind.File,
                OpenInFolderTab = true,
                X = 20,
                Y = 30
            }
        ]);

        var stored = Assert.IsType<ShellItem>(Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Single(saved);
        Assert.Equal(Path.GetFullPath(originalPath), stored.TargetPath);
        Assert.Equal(ShellItemKind.File, stored.Kind);
        Assert.True(stored.OpenInFolderTab);
        Assert.Contains(await repository.SearchItemsAsync("資料.txt"), item => item.Id == stored.Id);

        await repository.UpdateShellItemTargetAsync(stored.Id, changedPath, ShellItemKind.Shortcut);
        var updated = Assert.IsType<ShellItem>(Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(Path.GetFullPath(changedPath), updated.TargetPath);
        Assert.Equal(ShellItemKind.Shortcut, updated.Kind);
    }

    [Fact]
    public async Task ExistingWorkspaceDatabaseAddsCurrentItemColumnsWithoutRecreatingItemsTable()
    {
        using var database = new TempDatabase();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE workspace_items (
                    id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    parent_folder_id TEXT NULL,
                    type TEXT NOT NULL,
                    title TEXT NOT NULL,
                    url TEXT NOT NULL,
                    note TEXT NOT NULL,
                    favicon_png_base64 TEXT NOT NULL DEFAULT '',
                    thumbnail_png_base64 TEXT NOT NULL DEFAULT '',
                    thumbnail_updated_at TEXT NULL,
                    youtube_mini_player_state_json TEXT NOT NULL DEFAULT '',
                    x REAL NOT NULL,
                    y REAL NOT NULL,
                    width REAL NOT NULL,
                    height REAL NOT NULL,
                    z_index INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        await repository.GetWorkspacesAsync();

        var columns = new List<string>();
        await using (var verifyConnection = new SqliteConnection(connectionString))
        {
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "PRAGMA table_info(workspace_items);";
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("target_path", columns);
        Assert.Contains("shell_item_kind", columns);
        Assert.Contains("open_in_folder_tab", columns);
        Assert.Contains("note_font_size", columns);
    }

    [Fact]
    public async Task ExistingWorkspaceDatabaseAddsSplitSessionColumnsWithoutRecreatingSessionTable()
    {
        using var database = new TempDatabase();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE workspace_session_tabs (
                    id TEXT PRIMARY KEY,
                    session_group_item_id TEXT NOT NULL,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL,
                    favicon_png_base64 TEXT NOT NULL DEFAULT '',
                    source_tab_id TEXT NULL,
                    sort_order INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        await repository.GetWorkspacesAsync();

        var columns = new List<string>();
        await using (var verifyConnection = new SqliteConnection(connectionString))
        {
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "PRAGMA table_info(workspace_session_tabs);";
            await using var reader = await verifyCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("thumbnail_png_base64", columns);
        Assert.Contains("is_folder_tab", columns);
        Assert.Contains("split_group_id", columns);
        Assert.Contains("split_pane", columns);
        Assert.Contains("split_orientation", columns);
        Assert.Contains("split_ratio", columns);
        Assert.Contains("split_active_pane", columns);
    }

    [Fact]
    public async Task FolderTabSnapshotSavesAsShellItemAndReopensInFolderTab()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var folderPath = Path.Combine(database.DirectoryPath, "folder");
        Directory.CreateDirectory(folderPath);
        string? openedFolderPath = null;
        var viewModel = CreateWorkspaceViewModel(
            service,
            workspace.Id,
            openShellFolderInTab: path => openedFolderPath = path);
        await viewModel.InitializeAsync();

        viewModel.SaveDroppedTabsCommand.Execute(new WorkspaceTabDropRequest(
        [
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                Title = "Folder tab",
                Url = folderPath,
                TabIndex = 0,
                IsFolderTab = true
            }
        ],
        new WorkspaceCanvasPoint(120, 160)));

        await WaitForAsync(() => !viewModel.IsWorkspaceSaving && viewModel.Items.Count == 1);

        var stored = Assert.IsType<ShellItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(Path.GetFullPath(folderPath), stored.TargetPath);
        Assert.Equal(ShellItemKind.Directory, stored.Kind);
        Assert.True(stored.OpenInFolderTab);

        viewModel.OpenItemCommand.Execute(Assert.Single(viewModel.Items));
        Assert.Equal(Path.GetFullPath(folderPath), openedFolderPath);
    }

    [Fact]
    public async Task DuplicateItemCommandCreatesBookmarkBesideSource()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var source = await service.AddBookmarkAsync(
            workspace.Id,
            "Source",
            "https://example.com/",
            120,
            160,
            note: "Note",
            faviconPngBase64: "ZmF2aWNvbg==",
            thumbnailPngBase64: "dGh1bWJuYWls",
            tags: ["tag"]);
        var viewModel = CreateWorkspaceViewModel(service, workspace.Id);
        await viewModel.InitializeAsync();

        viewModel.DuplicateItemCommand.Execute(Assert.Single(viewModel.Items));
        for (var attempt = 0; attempt < 100 && viewModel.IsWorkspaceSaving; attempt++)
        {
            await Task.Delay(20);
        }

        var storedItems = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var duplicate = Assert.IsType<BookmarkItem>(Assert.Single(
            storedItems,
            item => item.Id != source.Id));
        Assert.Equal(source.Title, duplicate.Title);
        Assert.Equal(source.Url, duplicate.Url);
        Assert.Equal(source.Note, duplicate.Note);
        Assert.Equal(source.Tags, duplicate.Tags);
        Assert.Equal(source.FaviconPngBase64, duplicate.FaviconPngBase64);
        Assert.Equal(source.ThumbnailPngBase64, duplicate.ThumbnailPngBase64);
        Assert.Equal(224, duplicate.X);
        Assert.Equal(160, duplicate.Y);
        Assert.Equal(duplicate.Id, viewModel.SelectedItem?.Id);
    }

    [Fact]
    public async Task DuplicateShellCardKeepsReferenceWithoutCopyingPhysicalTarget()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var source = Assert.Single(await service.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "Folder tab",
                TargetPath = Path.Combine(database.DirectoryPath, "target"),
                Kind = ShellItemKind.Directory,
                OpenInFolderTab = true,
                X = 40,
                Y = 60
            }
        ]));

        var duplicate = Assert.IsType<ShellItem>(await service.DuplicateWorkspaceItemAsync(
            source,
            144,
            60));

        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.Equal(source.TargetPath, duplicate.TargetPath);
        Assert.Equal(source.Kind, duplicate.Kind);
        Assert.Equal(source.OpenInFolderTab, duplicate.OpenInFolderTab);
        Assert.Equal(2, (await repository.GetWorkspaceItemsAsync(workspace.Id)).Count);
        Assert.False(Directory.Exists(source.TargetPath));
    }

    [Fact]
    public async Task MixedFolderAndWebTabDropKeepsWebTabsInSessionGroup()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var folderPath = Path.Combine(database.DirectoryPath, "folder");
        Directory.CreateDirectory(folderPath);
        var viewModel = CreateWorkspaceViewModel(service, workspace.Id);
        await viewModel.InitializeAsync();

        viewModel.SaveDroppedTabsCommand.Execute(new WorkspaceTabDropRequest(
        [
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                Title = "Folder tab",
                Url = folderPath,
                TabIndex = 0,
                IsFolderTab = true
            },
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                Title = "Web 1",
                Url = "https://example.com/1",
                TabIndex = 1
            },
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                Title = "Web 2",
                Url = "https://example.com/2",
                TabIndex = 2
            }
        ],
        new WorkspaceCanvasPoint(120, 160)));

        await WaitForAsync(() => !viewModel.IsWorkspaceSaving && viewModel.Items.Count == 2);

        var storedItems = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var folderItem = Assert.IsType<ShellItem>(Assert.Single(
            storedItems,
            static item => item.Type == WorkspaceItemType.ShellItem));
        Assert.True(folderItem.OpenInFolderTab);
        Assert.Equal(Path.GetFullPath(folderPath), folderItem.TargetPath);

        var sessionGroup = Assert.IsType<SessionGroupItem>(Assert.Single(
            storedItems,
            static item => item.Type == WorkspaceItemType.SessionGroup));
        Assert.Equal(2, sessionGroup.Tabs.Count);
        Assert.Equal(
            ["https://example.com/1", "https://example.com/2"],
            sessionGroup.Tabs.Select(static tab => tab.Url));
    }

    [Fact]
    public async Task MixedWebAndFolderSplitDropCreatesOneSplitSessionCard()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var folderPath = Path.Combine(database.DirectoryPath, "folder");
        Directory.CreateDirectory(folderPath);
        var viewModel = CreateWorkspaceViewModel(service, workspace.Id);
        await viewModel.InitializeAsync();
        var windowId = Guid.NewGuid();
        var splitGroupId = Guid.NewGuid().ToString("N");

        viewModel.SaveDroppedTabsCommand.Execute(new WorkspaceTabDropRequest(
        [
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                WindowId = windowId,
                Title = "Web",
                Url = "https://example.com/",
                TabIndex = 0,
                SplitGroupId = splitGroupId,
                SplitPane = BrowserSplitViewPane.Left,
                SplitOrientation = BrowserSplitViewOrientation.LeftRight,
                SplitRatio = 0.6,
                SplitActivePane = BrowserSplitViewPane.Left
            },
            new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                WindowId = windowId,
                Title = "Folder",
                Url = folderPath,
                TabIndex = 1,
                IsFolderTab = true,
                SplitGroupId = splitGroupId,
                SplitPane = BrowserSplitViewPane.Right,
                SplitOrientation = BrowserSplitViewOrientation.LeftRight,
                SplitRatio = 0.6,
                SplitActivePane = BrowserSplitViewPane.Left
            }
        ],
        new WorkspaceCanvasPoint(120, 160)));

        await WaitForAsync(() => !viewModel.IsWorkspaceSaving && viewModel.Items.Count == 1);

        var sessionGroup = Assert.IsType<SessionGroupItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(2, sessionGroup.Tabs.Count);
        Assert.False(sessionGroup.Tabs[0].IsFolderTab);
        Assert.True(sessionGroup.Tabs[1].IsFolderTab);
        Assert.All(sessionGroup.Tabs, tab => Assert.Equal(splitGroupId, tab.SplitGroupId));
        Assert.Contains("左右分割", sessionGroup.Title);
    }

    [Fact]
    public async Task NormalNotePreservesMultilineContentAcrossAddAndEdit()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var workspace = await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "通常メモ",
            Index = 1
        });
        var note = await repository.AddNoteAsync(new NoteItem
        {
            WorkspaceId = workspace.Id,
            Title = "最初の行",
            Content = "最初の行\n二行目",
            FontSize = 18
        });

        await repository.UpdateNoteAsync(
            note.Id,
            "最初の行",
            "最初の行\n二行目\n末尾への追記",
            22);

        var restored = Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal("最初の行\n二行目\n末尾への追記", restored.Content);
        Assert.Equal(22, restored.FontSize);
    }

    [Theory]
    [InlineData(double.NaN, WorkspaceNoteFontSizeProfile.DefaultFontSize)]
    [InlineData(1, WorkspaceNoteFontSizeProfile.MinimumFontSize)]
    [InlineData(17.6, 18)]
    [InlineData(99, WorkspaceNoteFontSizeProfile.MaximumFontSize)]
    public void WorkspaceNoteFontSizeNormalizesInvalidAndOutOfRangeValues(
        double value,
        double expected)
    {
        var note = new NoteItem { FontSize = value };

        Assert.Equal(expected, note.FontSize);
    }

    [Fact]
    public async Task DuplicateNotePreservesItsFontSize()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("Workspace", 1);
        var source = await service.AddNoteAsync(
            workspace.Id,
            "メモ",
            "大きい文字",
            40,
            60,
            fontSize: 20);

        var duplicate = Assert.IsType<NoteItem>(await service.DuplicateWorkspaceItemAsync(
            source,
            140,
            60));

        Assert.Equal(20, duplicate.FontSize);
        Assert.Equal(20, Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == duplicate.Id)).FontSize);
    }

    [Fact]
    public void NormalNoteEditorTitleUsesTheFirstSentence()
    {
        var sentence = new WorkspaceItemViewModel(new NoteItem
        {
            Content = "  最初の文章です。続きです。\n三行目"
        });
        var firstLine = new WorkspaceItemViewModel(new NoteItem
        {
            Content = "最初の行\n二行目"
        });
        var longSentence = new WorkspaceItemViewModel(new NoteItem
        {
            Content = new string('長', 81)
        });
        var empty = new WorkspaceItemViewModel(new NoteItem());

        Assert.Equal("最初の文章です。", sentence.NoteEditorTitle);
        Assert.Equal("最初の行", firstLine.NoteEditorTitle);
        Assert.Equal($"{new string('長', 80)}…", longSentence.NoteEditorTitle);
        Assert.Equal("メモ", empty.NoteEditorTitle);
    }

    [Fact]
    public async Task NewWorkspaceDatabaseDoesNotCreateRemovedQuickNoteStackTable()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        await repository.CreateWorkspaceAsync(new Workspace
        {
            Name = "通常メモ用Workspace",
            Index = 1
        });

        await using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'workspace_note_entries';
            """;

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MissingOrDeletedCardUpdatesDoNotSucceedOrPublishChanges()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var shell = Assert.Single(await service.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "参照先",
                TargetPath = @"C:\old",
                Kind = ShellItemKind.Directory
            }
        ]));
        var changes = new List<WorkspaceItemContentChange>();
        var listener = new ItemContentListener(changes.Add);
        service.SubscribeToChanges(listener);

        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() => service.RenameItemAsync(Guid.NewGuid(), "変更"));
        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() =>
            service.UpdateShellItemTargetAsync(Guid.NewGuid(), @"C:\new", ShellItemKind.File));
        await service.DeleteItemAsync(shell.Id);
        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() => service.RenameItemAsync(shell.Id, "削除済み"));
        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() =>
            service.UpdateShellItemTargetAsync(shell.Id, @"C:\new", ShellItemKind.File));

        Assert.Empty(changes);
        Assert.Equal(@"C:\old", Assert.IsType<ShellItem>(Assert.Single(await service.GetDeletedItemsAsync())).TargetPath);
    }

    [Fact]
    public async Task ShellTargetUpdateRejectsWrongCardTypeButAcceptsSameValue()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var bookmark = await service.AddBookmarkAsync(workspace.Id, "URL", "https://example.com/", 0, 0);
        var shell = Assert.Single(await service.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "参照先",
                TargetPath = @"C:\same",
                Kind = ShellItemKind.Directory
            }
        ]));

        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() =>
            service.UpdateShellItemTargetAsync(bookmark.Id, @"C:\other", ShellItemKind.File));
        await service.RenameItemAsync(shell.Id, shell.Title);
        await service.UpdateShellItemTargetAsync(shell.Id, shell.TargetPath, shell.Kind);

        var items = await service.GetWorkspaceItemsAsync(workspace.Id);
        Assert.Equal("https://example.com/", Assert.IsType<BookmarkItem>(Assert.Single(items, item => item.Id == bookmark.Id)).Url);
        Assert.Equal(@"C:\same", Assert.IsType<ShellItem>(Assert.Single(items, item => item.Id == shell.Id)).TargetPath);
    }

    [Fact]
    public async Task ShellTargetCompareAndSwapRejectsStaleRequestWithoutOverwritingNewerTarget()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var shell = Assert.Single(await service.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "参照先",
                TargetPath = @"C:\old",
                Kind = ShellItemKind.Directory
            }
        ]));

        await service.UpdateShellItemTargetAsync(shell.Id, @"C:\new", ShellItemKind.File,
            expectedTargetPath: @"C:\old");
        await Assert.ThrowsAsync<WorkspaceItemTargetConflictException>(() =>
            service.UpdateShellItemTargetAsync(shell.Id, @"C:\stale", ShellItemKind.File,
                expectedTargetPath: @"C:\old"));

        var stored = Assert.IsType<ShellItem>(Assert.Single(await service.GetWorkspaceItemsAsync(workspace.Id)));
        Assert.Equal(@"C:\new", stored.TargetPath);
        Assert.Equal(ShellItemKind.File, stored.Kind);
    }

    [Fact]
    public async Task StaleCardRenameAndRelinkDoNotShowSuccessOrMutateTheCard()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var originalPath = Path.Combine(database.DirectoryPath, "original.txt");
        var newPath = Path.Combine(database.DirectoryPath, "new.txt");
        File.WriteAllText(originalPath, "元");
        File.WriteAllText(newPath, "新");
        var shell = Assert.Single(await service.AddShellItemsAsync(
        [
            new ShellItem
            {
                WorkspaceId = workspace.Id,
                Title = "元の名前",
                TargetPath = originalPath,
                Kind = ShellItemKind.File
            }
        ]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);
        await repository.DeleteItemAsync(shell.Id);

        await Assert.IsType<AsyncRelayCommand>(view.RenameItemCommand)
            .ExecuteAsync(new WorkspaceRenameRequest(card, "新しい名前"));
        Assert.Contains("変更できません", view.StatusText);
        Assert.Equal("元の名前", card.Title);

        await Assert.IsType<AsyncRelayCommand>(view.RelinkShellItemCommand)
            .ExecuteAsync(new WorkspaceShellPathRequest(card, newPath));
        Assert.Contains("更新できません", view.StatusText);
        Assert.Equal(originalPath, card.TargetPath);
        Assert.Equal(originalPath, Assert.IsType<ShellItem>(Assert.Single(await service.GetDeletedItemsAsync())).TargetPath);
    }

    [Fact]
    public async Task ShellRenameDatabaseFailureCompensatesOnlyWhenFileIsUnchanged()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        fault.Fault = (method, _) => method.Name == nameof(IWorkspaceRepository.UpdateShellItemTargetAsync)
            ? Task.FromException(new IOException("DB書込み失敗")) : null;
        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, Title = "元", TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);

        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(card, "renamed.txt"));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(database.DirectoryPath, "renamed.txt")));
        Assert.Equal(source, Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("元へ戻しました", view.WorkspaceErrorText);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellRenameFileFailureDoesNotStartDatabaseUpdate()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        var collision = Path.Combine(database.DirectoryPath, "collision.txt");
        File.WriteAllText(source, "元");
        File.WriteAllText(collision, "別");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();

        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == shell.Id), "collision.txt"));

        Assert.Equal("元", File.ReadAllText(source));
        Assert.Equal("別", File.ReadAllText(collision));
        Assert.Equal(source, Assert.IsType<ShellItem>(await repository.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("実ファイル名の変更中に失敗", view.WorkspaceErrorText);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellRenameKeepsChangedDestinationWhenDatabaseAndCompensationFail()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fault.Fault = (method, _) => method.Name == nameof(IWorkspaceRepository.UpdateShellItemTargetAsync)
            ? FailUpdateAsync() : null;
        async Task FailUpdateAsync()
        {
            entered.SetResult();
            await release.Task;
            throw new IOException("DB書込み失敗");
        }

        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        var destination = Path.Combine(database.DirectoryPath, "renamed.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, Title = "元", TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);

        var operation = Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(card, "renamed.txt"));
        await entered.Task;
        File.Delete(destination);
        File.WriteAllText(Path.Combine(database.DirectoryPath, "occupier.txt"), "占有");
        File.WriteAllText(destination, "後から変更");
        release.SetResult();
        await operation;

        Assert.False(File.Exists(source));
        Assert.Equal("後から変更", File.ReadAllText(destination));
        Assert.Equal(source, Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("補償が両方失敗", view.WorkspaceErrorText);
        Assert.Contains(destination, view.WorkspaceErrorText);
    }

    [Fact]
    public async Task ShellRenameCompensatesAfterCardWasDeletedDuringDatabaseWait()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fault.Fault = (method, args) => method.Name == nameof(IWorkspaceRepository.UpdateShellItemTargetAsync)
            ? WaitThenUpdateAsync(args!) : null;
        async Task WaitThenUpdateAsync(object?[] args)
        {
            entered.SetResult();
            await release.Task;
            await actual.UpdateShellItemTargetAsync((Guid)args[0]!, (string)args[1]!,
                (ShellItemKind)args[2]!, (CancellationToken)args[3]!, (string?)args[4]);
        }

        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);

        var rename = Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(card, "renamed.txt"));
        await entered.Task;
        await actual.DeleteItemAsync(shell.Id);
        release.SetResult();
        await rename;

        Assert.Equal("本文", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(database.DirectoryPath, "renamed.txt")));
        var deleted = Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(shell.Id));
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal(source, deleted.TargetPath);
        Assert.Contains("元へ戻しました", view.WorkspaceErrorText);
    }

    [Fact]
    public async Task ShellRenameAfterCommitViewFailureKeepsFileAndCanUndo()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        var destination = Path.Combine(database.DirectoryPath, "renamed.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, Title = "元", TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);
        System.ComponentModel.PropertyChangedEventHandler? failOnce = null;
        failOnce = (_, e) =>
        {
            if (e.PropertyName != nameof(view.CanUndoShellOperation)) return;
            view.PropertyChanged -= failOnce;
            throw new InvalidOperationException("表示失敗");
        };
        view.PropertyChanged += failOnce;

        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(card, "renamed.txt"));

        Assert.False(File.Exists(source));
        Assert.Equal("本文", File.ReadAllText(destination));
        Assert.Equal(destination, Assert.IsType<ShellItem>(await repository.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("DB保存は完了", view.WorkspaceErrorText);
        Assert.True(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellUndoRetriesOnlyDatabaseAfterItsUpdateFails()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, Title = "元", TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var card = Assert.Single(view.Items, item => item.Id == shell.Id);
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(card, "renamed.txt"));
        var destination = Path.Combine(database.DirectoryPath, "renamed.txt");
        Assert.True(File.Exists(destination));
        await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_undo BEFORE UPDATE OF target_path ON workspace_items BEGIN SELECT RAISE(FAIL, 'test failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
        Assert.Equal(destination, Assert.IsType<ShellItem>(await repository.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.True(view.CanUndoShellOperation);
        Assert.Contains("カードの更新は未完了", view.WorkspaceErrorText);
        await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_undo;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);
        Assert.Equal("本文", File.ReadAllText(source));
        Assert.Equal(source, Assert.IsType<ShellItem>(await repository.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellRenamePostCommitExceptionKeepsCommittedFileAndDatabase()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        fault.Fault = (method, args) => method.Name == nameof(IWorkspaceRepository.UpdateShellItemTargetAsync)
            ? CommitThenThrowAsync(args!) : null;
        async Task CommitThenThrowAsync(object?[] args)
        {
            await actual.UpdateShellItemTargetAsync((Guid)args[0]!, (string)args[1]!,
                (ShellItemKind)args[2]!, (CancellationToken)args[3]!, (string?)args[4]);
            throw new IOException("保存後に応答を失いました");
        }

        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        var destination = Path.Combine(database.DirectoryPath, "renamed.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == shell.Id), "renamed.txt"));

        Assert.False(File.Exists(source));
        Assert.Equal("本文", File.ReadAllText(destination));
        Assert.Equal(destination, Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("DB保存は完了", view.WorkspaceErrorText);
        Assert.True(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellRenameDoesNotCompensateWhenDatabaseStateCannotBeRead()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        fault.Fault = (method, _) => method.Name switch
        {
            nameof(IWorkspaceRepository.UpdateShellItemTargetAsync) => Task.FromException(new IOException("DB書込み失敗")),
            nameof(IWorkspaceRepository.GetItemByIdAsync) => Task.FromException<WorkspaceItem?>(new IOException("DB照合失敗")),
            _ => null
        };
        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        var destination = Path.Combine(database.DirectoryPath, "renamed.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == shell.Id), "renamed.txt"));

        Assert.False(File.Exists(source));
        Assert.Equal("本文", File.ReadAllText(destination));
        Assert.Equal(source, Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Contains("DB: 不明", view.WorkspaceErrorText);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellUndoAfterWorkspaceSwitchUsesStableCardId()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("元", 1);
        var other = await service.CreateWorkspaceAsync("別", 2);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == shell.Id), "renamed.txt"));
        await view.LoadWorkspaceAsync(other.Id);
        Assert.True(view.CanUndoShellOperation);

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);

        Assert.Equal("本文", File.ReadAllText(source));
        Assert.Equal(source, Assert.IsType<ShellItem>(await repository.GetItemByIdAsync(shell.Id)).TargetPath);
        Assert.Equal(other.Id, view.CurrentWorkspace?.Id);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ShellUndoDoesNotDeleteCreatedFolderWhoseCardMovedElsewhere()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("元", 1);
        var other = await service.CreateWorkspaceAsync("別", 2);
        var parent = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.CreateShellDirectoryCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == parent.Id), "created"));
        var created = Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id), item => item.Id != parent.Id);
        await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workspace_items SET workspace_id = $workspace WHERE id = $id;";
            command.Parameters.AddWithValue("$workspace", other.Id.ToString("D"));
            command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);

        Assert.True(Directory.Exists(Path.Combine(database.DirectoryPath, "created")));
        Assert.Equal(other.Id, (await repository.GetItemByIdAsync(created.Id))?.WorkspaceId);
        Assert.Contains("Undoは完了していません", view.WorkspaceErrorText);
        Assert.True(view.CanUndoShellOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShellUndoDoesNotDeleteCardChangedAfterReadBeforeDelete(bool changeWorkspace)
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fault.Fault = (method, args) => method.Name == nameof(IWorkspaceRepository.DeleteShellItemForUndoAsync)
            ? PauseThenDeleteAsync(args!) : null;
        async Task PauseThenDeleteAsync(object?[] args)
        {
            entered.TrySetResult();
            await release.Task;
            await actual.DeleteShellItemForUndoAsync((Guid)args[0]!, (Guid)args[1]!,
                (string)args[2]!, (ShellItemKind)args[3]!, (CancellationToken)args[4]!);
        }

        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("元", 1);
        var other = await service.CreateWorkspaceAsync("別", 2);
        var parent = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.CreateShellDirectoryCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == parent.Id), "created"));
        var created = Assert.IsType<ShellItem>(Assert.Single(
            await actual.GetWorkspaceItemsAsync(workspace.Id), item => item.Id != parent.Id));
        var changedPath = Path.Combine(database.DirectoryPath, "changed");

        var undo = Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = changeWorkspace
                ? "UPDATE workspace_items SET workspace_id = $value WHERE id = $id;"
                : "UPDATE workspace_items SET target_path = $value WHERE id = $id;";
            command.Parameters.AddWithValue("$value", changeWorkspace ? other.Id.ToString("D") : changedPath);
            command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        finally { release.TrySetResult(); }
        await undo;

        var stored = Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(created.Id));
        Assert.Null(stored.DeletedAt);
        Assert.Equal(changeWorkspace ? other.Id : workspace.Id, stored.WorkspaceId);
        Assert.Equal(changeWorkspace ? created.TargetPath : changedPath, stored.TargetPath);
        Assert.False(Directory.Exists(created.TargetPath));
        Assert.True(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task ConditionalShellUndoDeletionCommitsOnlyMatchingCard()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }]));

        await service.DeleteShellItemForUndoAsync(shell.Id, workspace.Id,
            shell.TargetPath, ShellItemKind.Directory);

        Assert.NotNull((await repository.GetItemByIdAsync(shell.Id))?.DeletedAt);
        await Assert.ThrowsAsync<WorkspaceItemUnavailableException>(() =>
            service.DeleteShellItemForUndoAsync(shell.Id, workspace.Id,
                shell.TargetPath, ShellItemKind.Directory));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("target")]
    [InlineData("kind")]
    public async Task ConditionalShellUndoDeletionRejectsChangedCard(string changedField)
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var other = await service.CreateWorkspaceAsync("別", 2);
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }]));
        await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = changedField switch
            {
                "workspace" => "UPDATE workspace_items SET workspace_id = $value WHERE id = $id;",
                "target" => "UPDATE workspace_items SET target_path = $value WHERE id = $id;",
                _ => "UPDATE workspace_items SET shell_item_kind = $value WHERE id = $id;"
            };
            command.Parameters.AddWithValue("$value", changedField switch
            {
                "workspace" => other.Id.ToString("D"),
                "target" => Path.Combine(database.DirectoryPath, "changed"),
                _ => ShellItemKind.File.ToString()
            });
            command.Parameters.AddWithValue("$id", shell.Id.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<WorkspaceItemTargetConflictException>(() =>
            service.DeleteShellItemForUndoAsync(shell.Id, workspace.Id,
                shell.TargetPath, ShellItemKind.Directory));

        Assert.Null((await repository.GetItemByIdAsync(shell.Id))?.DeletedAt);
    }

    [Fact]
    public async Task ShellUndoDoesNotRestoreDeletedWorkspaceCard()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        File.WriteAllText(source, "本文");
        var shell = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == shell.Id), "renamed.txt"));
        await service.DeleteItemAsync(shell.Id);

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);

        Assert.Equal("本文", File.ReadAllText(source));
        Assert.NotNull((await repository.GetItemByIdAsync(shell.Id))?.DeletedAt);
        Assert.Contains("削除済み", view.WorkspaceErrorText);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task CreatedFolderUndoReportsPreviouslyDeletedCardAsPartialCompletion()
    {
        using var database = new TempDatabase();
        var repository = new SQLiteWorkspaceRepository(database.DatabasePath);
        var service = new WorkspaceService(repository);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        var parent = Assert.Single(await service.AddShellItemsAsync(
            [new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }]));
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        await Assert.IsType<AsyncRelayCommand>(view.CreateShellDirectoryCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(Assert.Single(view.Items, item => item.Id == parent.Id), "created"));
        var created = Assert.Single(await repository.GetWorkspaceItemsAsync(workspace.Id), item => item.Id != parent.Id);
        await service.DeleteItemAsync(created.Id);

        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(database.DirectoryPath, "created")));
        Assert.NotNull((await repository.GetItemByIdAsync(created.Id))?.DeletedAt);
        Assert.Contains("削除済み", view.WorkspaceErrorText);
        Assert.DoesNotContain("直前のファイル操作を元に戻しました", view.StatusText);
        Assert.False(view.CanUndoShellOperation);
    }

    [Fact]
    public async Task OlderUndoCompletionDoesNotClearNewerShellOperation()
    {
        using var database = new TempDatabase();
        var actual = new SQLiteWorkspaceRepository(database.DatabasePath);
        var proxy = DispatchProxy.Create<IWorkspaceRepository, FaultingWorkspaceRepositoryProxy>();
        var fault = (FaultingWorkspaceRepositoryProxy)proxy;
        fault.Inner = actual;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Path.Combine(database.DirectoryPath, "source.txt");
        fault.Fault = (method, args) => method.Name == nameof(IWorkspaceRepository.UpdateShellItemTargetAsync)
            && string.Equals((string?)args?[1], source, StringComparison.OrdinalIgnoreCase)
            ? WaitThenUpdateAsync(args!) : null;
        async Task WaitThenUpdateAsync(object?[] args)
        {
            entered.SetResult();
            await release.Task;
            await actual.UpdateShellItemTargetAsync((Guid)args[0]!, (string)args[1]!,
                (ShellItemKind)args[2]!, (CancellationToken)args[3]!, (string?)args[4]);
        }

        var service = new WorkspaceService(proxy);
        var workspace = await service.CreateWorkspaceAsync("検証", 1);
        File.WriteAllText(source, "本文");
        var items = await service.AddShellItemsAsync(
        [
            new ShellItem { WorkspaceId = workspace.Id, TargetPath = source, Kind = ShellItemKind.File },
            new ShellItem { WorkspaceId = workspace.Id, TargetPath = database.DirectoryPath, Kind = ShellItemKind.Directory }
        ]);
        using var view = CreateWorkspaceViewModel(service, workspace.Id);
        await view.InitializeAsync();
        var fileCard = Assert.Single(view.Items, item => item.Id == items[0].Id);
        var folderCard = Assert.Single(view.Items, item => item.Id == items[1].Id);
        await Assert.IsType<AsyncRelayCommand>(view.RenameShellTargetCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(fileCard, "renamed.txt"));

        var undo = Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);
        await entered.Task;
        await Assert.IsType<AsyncRelayCommand>(view.CreateShellDirectoryCommand)
            .ExecuteAsync(new WorkspaceShellNameRequest(folderCard, "new-folder"));
        release.SetResult();
        await undo;

        Assert.True(Directory.Exists(Path.Combine(database.DirectoryPath, "new-folder")));
        Assert.True(view.CanUndoShellOperation);
        await Assert.IsType<AsyncRelayCommand>(view.UndoShellOperationCommand).ExecuteAsync(null);
        Assert.False(Directory.Exists(Path.Combine(database.DirectoryPath, "new-folder")));
        Assert.Equal(source, Assert.IsType<ShellItem>(await actual.GetItemByIdAsync(items[0].Id)).TargetPath);
    }

    public class FaultingWorkspaceRepositoryProxy : DispatchProxy
    {
        public IWorkspaceRepository Inner { get; set; } = null!;
        public Func<MethodInfo, object?[]?, object?>? Fault { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException();
            if (Fault?.Invoke(method, args) is { } fault) return fault;
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }

    private sealed class ItemContentListener(Action<WorkspaceItemContentChange> changed) : IWorkspaceChangeListener
    {
        public void OnWorkspaceNameChanged(WorkspaceNameChangedChange change) { }
        public void OnWorkspaceItemsMoved(WorkspaceItemsMovedChange change) { }
        public void OnWorkspaceItemPositionsChanged(WorkspaceItemPositionsChangedChange change) { }
        public void OnWorkspaceItemsChanged(WorkspaceItemsChangedChange change) { }
        public void OnWorkspaceItemContentChanged(WorkspaceItemContentChange change) => changed(change);
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "TabBrowserWorkspaceTests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(DirectoryPath, "workspace.db");

        public void Dispose()
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(DirectoryPath); attempt++)
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("非同期Workspace操作が時間内に完了しませんでした。");
    }

    private static WorkspaceOSViewModel CreateWorkspaceViewModel(
        WorkspaceService service,
        Guid workspaceId,
        Guid? folderId = null,
        Action<string>? openShellFolderInTab = null)
    {
        return new WorkspaceOSViewModel(
            service,
            workspaceId,
            new WorkspaceTabPort(_ => Task.CompletedTask, _ => Task.FromResult(0),
                () => [], () => null, _ => Task.CompletedTask, openShellFolderInTab ?? (_ => { })),
            new WorkspaceMiniPlayerPort(() => [],
                () => Task.FromResult<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>>([]), _ => Task.CompletedTask),
            new WorkspaceWindowPort(_ => Task.CompletedTask, (_, _) => { }, _ => { }),
            new WorkspaceThumbnailPort((_, _) => Task.FromResult<System.Windows.Media.ImageSource?>(null)),
            folderId);
    }
}
