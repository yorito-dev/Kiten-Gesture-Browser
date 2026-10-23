using System.Collections.ObjectModel;
using System.Windows.Data;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class BookmarkStateStoreTests
{
    [Fact]
    public async Task RealLockedBookmarkFileIsSavedOnCloseAfterUnlockWithoutFurtherEditing()
    {
        var root = Path.Combine(Path.GetTempPath(), "Kiten-bookmark-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "bookmarks.json");
            var service = new BookmarkService(path);
            service.SaveData(new BookmarkData());
            var store = new BookmarkStateStore(new BookmarkStore(service));
            store.Initialize();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False((await store.UpdateAsync(data => Add(data, "https://after-unlock.example/"))).Persisted);
                await Assert.ThrowsAnyAsync<IOException>(store.FlushAsync);
            }
            await store.FlushAsync();
            Assert.Equal("https://after-unlock.example/", Assert.Single(new BookmarkService(path).LoadData().Bookmarks).Url);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CloseRetriesFailedSaveWithoutAnotherEditAndFailureStillBlocksClose()
    {
        var persistence = new ControllableStore(new BookmarkData()) { RemainingFailures = 2 };
        var store = new BookmarkStateStore(persistence);
        Assert.False((await store.UpdateAsync(data => Add(data, "https://pending.example/"))).Persisted);
        var closing = new ApplicationWindowCloseCoordinator();
        var targets = new[] { new WindowPersistenceTarget("bookmarks", "ブックマーク", store.FlushAsync) };
        Assert.False((await closing.SaveBeforeCloseAsync(targets)).Succeeded);
        Assert.True((await closing.SaveBeforeCloseAsync(targets)).Succeeded);
        var reopened = new BookmarkStateStore(persistence);
        Assert.Equal("https://pending.example/", Assert.Single(reopened.Initialize().Data.Bookmarks).Url);
    }

    [Fact]
    public async Task CloseWaitsForSavingAndQueuedLatestUpdate()
    {
        var persistence = new ControllableStore(new BookmarkData()) { HoldFirstSave = true };
        var store = new BookmarkStateStore(persistence);
        var first = store.UpdateAsync(data => Add(data, "https://first.example/"));
        await persistence.FirstSaveStarted.Task;
        var second = store.UpdateAsync(data => Add(data, "https://second.example/"));
        var closing = store.FlushAsync();
        Assert.False(closing.IsCompleted);
        persistence.ReleaseFirstSave.SetResult();
        await Task.WhenAll(first, second, closing);
        Assert.Equal(2, persistence.Current.Bookmarks.Count);
    }

    [Fact]
    public async Task HeldSaveSerializesLaterUpdateAndPersistsNewestSnapshotLast()
    {
        var persistence = new ControllableStore(CreateData("https://initial.example/"))
        {
            HoldFirstSave = true
        };
        var store = new BookmarkStateStore(persistence);
        store.Initialize();

        var firstUpdate = store.UpdateAsync(data => Add(data, "https://first.example/"));
        await persistence.FirstSaveStarted.Task;
        var secondUpdate = store.UpdateAsync(data => Add(data, "https://second.example/"));
        await Task.Yield();

        Assert.Equal(1, persistence.SaveCallCount);
        Assert.False(secondUpdate.IsCompleted);

        persistence.ReleaseFirstSave.TrySetResult();
        var results = await Task.WhenAll(firstUpdate, secondUpdate);

        Assert.All(results, result => Assert.True(result.Persisted));
        Assert.Equal(2, persistence.SaveCallCount);
        Assert.Equal(
            ["https://initial.example/", "https://first.example/", "https://second.example/"],
            persistence.Current.Bookmarks.Select(static item => item.Url));
    }

    [Fact]
    public async Task FailedSaveKeepsMemoryStateAndNextUpdateRetriesCombinedSnapshot()
    {
        var persistence = new ControllableStore(CreateData("https://initial.example/"))
        {
            RemainingFailures = 1
        };
        var store = new BookmarkStateStore(persistence);
        var notifications = new List<BookmarkData>();
        store.Changed += (_, args) => notifications.Add(BookmarkDataSnapshot.Clone(args.Snapshot));
        store.Initialize();

        var failed = await store.UpdateAsync(data => Add(data, "https://pending.example/"));

        Assert.True(failed.Applied);
        Assert.False(failed.Persisted);
        Assert.True(failed.HasPendingChanges);
        Assert.Single(notifications);
        Assert.Contains(
            store.Initialize().Data.Bookmarks,
            item => item.Url == "https://pending.example/");
        Assert.DoesNotContain(
            persistence.Current.Bookmarks,
            item => item.Url == "https://pending.example/");

        var retried = await store.UpdateAsync(data => Add(data, "https://next.example/"));

        Assert.True(retried.Persisted);
        Assert.False(retried.HasPendingChanges);
        Assert.Equal(2, persistence.SaveCallCount);
        Assert.Equal(3, persistence.Current.Bookmarks.Count);
        Assert.Contains(persistence.Current.Bookmarks, item => item.Url == "https://pending.example/");
        Assert.Contains(persistence.Current.Bookmarks, item => item.Url == "https://next.example/");
    }

    [Fact]
    public async Task SeparateStateStoresDoNotMixDifferentPersistenceTargets()
    {
        var firstPersistence = new ControllableStore(new BookmarkData());
        var secondPersistence = new ControllableStore(new BookmarkData());
        var first = new BookmarkStateStore(firstPersistence);
        var second = new BookmarkStateStore(secondPersistence);

        await first.UpdateAsync(data => Add(data, "https://first.example/"));
        await second.UpdateAsync(data => Add(data, "https://second.example/"));

        Assert.Equal("https://first.example/", Assert.Single(firstPersistence.Current.Bookmarks).Url);
        Assert.Equal("https://second.example/", Assert.Single(secondPersistence.Current.Bookmarks).Url);
    }

    [Fact]
    public async Task NotificationSnapshotCannotMutatePersistenceSnapshot()
    {
        var persistence = new ControllableStore(CreateData("https://initial.example/"));
        var store = new BookmarkStateStore(persistence);
        store.Changed += (_, args) =>
        {
            foreach (var bookmark in args.Snapshot.Bookmarks)
            {
                bookmark.Title = "通知側の変更";
            }
        };

        var result = await store.UpdateAsync(data => Add(data, "https://added.example/"));

        Assert.True(result.Persisted);
        Assert.All(
            persistence.Current.Bookmarks,
            bookmark => Assert.NotEqual("通知側の変更", bookmark.Title));
    }

    [Fact]
    public void StaleManagerChangeSetPreservesConcurrentDeleteAndUnrelatedEdit()
    {
        var deleted = CreateBookmark("https://deleted.example/", "deleted-before");
        var first = CreateBookmark("https://first.example/", "first-before");
        var second = CreateBookmark("https://second.example/", "second-before");
        var managerBefore = new BookmarkData { Bookmarks = [deleted, first, second] };
        var managerAfter = BookmarkDataSnapshot.Clone(managerBefore);
        managerAfter.Bookmarks.Single(item => item.Id == second.Id).Title = "second-after";
        var current = BookmarkDataSnapshot.Clone(managerBefore);
        current.Bookmarks.RemoveAll(item => item.Id == deleted.Id);
        current.Bookmarks.Single(item => item.Id == first.Id).Title = "first-after";

        var changed = BookmarkChangeSet.Create(managerBefore, managerAfter).ApplyTo(current);

        Assert.True(changed);
        Assert.DoesNotContain(current.Bookmarks, item => item.Id == deleted.Id);
        Assert.Equal("first-after", current.Bookmarks.Single(item => item.Id == first.Id).Title);
        Assert.Equal("second-after", current.Bookmarks.Single(item => item.Id == second.Id).Title);
    }

    [Fact]
    public void ManagerOrderingKeepsConcurrentItemsAndAppliesKnownRelativeOrder()
    {
        var first = CreateBookmark("https://first.example/", "first");
        var second = CreateBookmark("https://second.example/", "second");
        var concurrent = CreateBookmark("https://concurrent.example/", "concurrent");
        var managerBefore = new BookmarkData { Bookmarks = [first, second] };
        var managerAfter = new BookmarkData
        {
            Bookmarks =
            [
                BookmarkDataSnapshot.Clone(second),
                BookmarkDataSnapshot.Clone(first)
            ]
        };
        var current = new BookmarkData
        {
            Bookmarks =
            [
                concurrent,
                BookmarkDataSnapshot.Clone(first),
                BookmarkDataSnapshot.Clone(second)
            ]
        };

        BookmarkChangeSet.Create(managerBefore, managerAfter).ApplyTo(current);

        Assert.Equal(
            [concurrent.Id, second.Id, first.Id],
            current.Bookmarks.Select(static item => item.Id));
    }

    [Fact]
    public void ManagerImportAndFolderRenamePreserveConcurrentBookmarkAndFolder()
    {
        var original = CreateBookmark("https://original.example/", "original");
        original.FolderPath = "旧";
        var managerBefore = new BookmarkData
        {
            Bookmarks = [original],
            Folders = [new BookmarkFolderItem { Path = "旧" }]
        };
        var managerAfter = BookmarkDataSnapshot.Clone(managerBefore);
        managerAfter.Bookmarks[0].FolderPath = "新";
        managerAfter.Bookmarks.Add(CreateBookmark("https://imported.example/", "imported"));
        managerAfter.Folders[0].Path = "新";
        var current = BookmarkDataSnapshot.Clone(managerBefore);
        current.Bookmarks.Add(CreateBookmark("https://concurrent.example/", "concurrent"));
        current.Folders.Add(new BookmarkFolderItem { Path = "並行" });

        BookmarkChangeSet.Create(managerBefore, managerAfter).ApplyTo(current);

        Assert.Equal("新", current.Bookmarks.Single(item => item.Id == original.Id).FolderPath);
        Assert.Contains(current.Bookmarks, item => item.Url == "https://imported.example/");
        Assert.Contains(current.Bookmarks, item => item.Url == "https://concurrent.example/");
        Assert.DoesNotContain(current.Folders, folder => folder.Path == "旧");
        Assert.Contains(current.Folders, folder => folder.Path == "新");
        Assert.Contains(current.Folders, folder => folder.Path == "並行");
    }

    [Fact]
    public void ManagerReplacementDeletesBaselineItemsWithoutDeletingConcurrentAddition()
    {
        var original = CreateBookmark("https://original.example/", "original");
        var concurrent = CreateBookmark("https://concurrent.example/", "concurrent");
        var managerBefore = new BookmarkData { Bookmarks = [original] };
        var managerAfter = new BookmarkData
        {
            Bookmarks = [CreateBookmark("https://restored.example/", "restored")]
        };
        var current = new BookmarkData
        {
            Bookmarks = [BookmarkDataSnapshot.Clone(original), concurrent]
        };

        BookmarkChangeSet.Create(managerBefore, managerAfter).ApplyTo(current);

        Assert.DoesNotContain(current.Bookmarks, item => item.Id == original.Id);
        Assert.Contains(current.Bookmarks, item => item.Id == concurrent.Id);
        Assert.Contains(current.Bookmarks, item => item.Url == "https://restored.example/");
    }

    [Fact]
    public void LaterChangeToSamePropertyWinsWithoutOverwritingOtherProperties()
    {
        var original = CreateBookmark("https://example.com/", "before");
        var managerBefore = new BookmarkData { Bookmarks = [original] };
        var managerAfter = BookmarkDataSnapshot.Clone(managerBefore);
        managerAfter.Bookmarks[0].Title = "manager-later";
        var current = BookmarkDataSnapshot.Clone(managerBefore);
        current.Bookmarks[0].Title = "window-first";
        current.Bookmarks[0].OpenCount = 4;

        BookmarkChangeSet.Create(managerBefore, managerAfter).ApplyTo(current);

        Assert.Equal("manager-later", current.Bookmarks[0].Title);
        Assert.Equal(4, current.Bookmarks[0].OpenCount);
    }

    [Fact]
    public void SnapshotsAndCollectionViewsRemainWindowLocal()
    {
        var persistence = new ControllableStore(CreateData("https://example.com/"));
        var store = new BookmarkStateStore(persistence);

        var first = store.Initialize().Data;
        var second = store.Initialize().Data;

        Assert.NotSame(first, second);
        Assert.NotSame(Assert.Single(first.Bookmarks), Assert.Single(second.Bookmarks));
        RunOnStaThread(() =>
        {
            var firstCollection = new ObservableCollection<BookmarkItem>(first.Bookmarks);
            var secondCollection = new ObservableCollection<BookmarkItem>(second.Bookmarks);
            var firstView = CollectionViewSource.GetDefaultView(firstCollection);
            var secondView = CollectionViewSource.GetDefaultView(secondCollection);
            firstView.Filter = static _ => false;
            firstView.Refresh();

            Assert.NotSame(firstView, secondView);
            Assert.True(firstView.IsEmpty);
            Assert.False(secondView.IsEmpty);
        });
    }

    private static bool Add(BookmarkData data, string url)
    {
        data.Bookmarks.Add(CreateBookmark(url, url));
        return true;
    }

    private static BookmarkData CreateData(string url)
        => new() { Bookmarks = [CreateBookmark(url, url)] };

    private static BookmarkItem CreateBookmark(string url, string title)
        => new() { Url = url, Title = title };

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private sealed class ControllableStore(BookmarkData initial) : IBookmarkStore
    {
        public BookmarkData Current { get; private set; } = BookmarkDataSnapshot.Clone(initial);

        public bool HoldFirstSave { get; init; }

        public int RemainingFailures { get; set; }

        public int SaveCallCount { get; private set; }

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BookmarkLoadResult LoadData()
            => new(BookmarkDataSnapshot.Clone(Current), BookmarkLoadStatus.LoadedPrimary);

        public async Task SaveDataAsync(BookmarkData data)
        {
            SaveCallCount++;
            if (HoldFirstSave && SaveCallCount == 1)
            {
                FirstSaveStarted.TrySetResult();
                await ReleaseFirstSave.Task;
            }

            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new IOException("意図した保存失敗");
            }

            Current = BookmarkDataSnapshot.Clone(data);
        }
    }
}
