using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IBookmarkControllerHost
{
    BrowserTab? GetActiveBrowserCommandTarget();

    string? GetCurrentPageUrl(BrowserTab tab);

    string GetCurrentPageTitle(BrowserTab tab, string url);

    BrowserTab? ActiveTab { get; }

    bool IsNewTab(BrowserTab tab);

    Task NavigateTabToUrlAsync(
        BrowserTab tab,
        string url,
        string title,
        CancellationToken cancellationToken);

    void OpenTab(string url, string title);

    void CloseAddressSuggestions();

    void RefreshLoadedNewTabPages();

    void QueueSessionSave();

    void SetStatus(string message);

    void SetPersistentStatus(string message);
}

internal interface IBookmarkStore
{
    BookmarkLoadResult LoadData();

    Task SaveDataAsync(BookmarkData data);
}

internal sealed class BookmarkStore(BookmarkService service) : IBookmarkStore
{
    public BookmarkLoadResult LoadData() => service.LoadDataWithResult();

    public Task SaveDataAsync(BookmarkData data) => service.SaveDataAsync(data);
}

public sealed class BookmarkController : IDisposable
{
    private readonly IBookmarkControllerHost _host;
    private readonly IList<BookmarkItem> _bookmarks;
    private readonly IList<BookmarkFolderItem> _folders;
    private readonly BookmarkStateStore _stateStore;
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _recordOpenUsage;
    private bool _isSubscribed;

    internal BookmarkController(
        IBookmarkControllerHost host,
        IList<BookmarkItem> bookmarks,
        IList<BookmarkFolderItem> folders,
        IBookmarkStore? store = null,
        Func<DateTimeOffset>? now = null,
        bool recordOpenUsage = true)
        : this(
            host,
            bookmarks,
            folders,
            new BookmarkStateStore(store ?? new BookmarkStore(new BookmarkService())),
            now,
            recordOpenUsage)
    {
    }

    internal BookmarkController(
        IBookmarkControllerHost host,
        IList<BookmarkItem> bookmarks,
        IList<BookmarkFolderItem> folders,
        BookmarkStateStore stateStore,
        Func<DateTimeOffset>? now = null,
        bool recordOpenUsage = true)
    {
        _host = host;
        _bookmarks = bookmarks;
        _folders = folders;
        _stateStore = stateStore;
        _now = now ?? (() => DateTimeOffset.Now);
        _recordOpenUsage = recordOpenUsage;
    }

    public BookmarkSortMode SortMode { get; private set; } = BookmarkSortMode.Manual;

    public bool IsAvailable { get; private set; } = true;

    public bool CanPersistChanges { get; private set; } = true;

    public void Load()
    {
        var result = _stateStore.Initialize();
        if (!result.CanUseData)
        {
            IsAvailable = false;
            CanPersistChanges = false;
            _host.SetPersistentStatus("ブックマーク本体とバックアップを読み込めません。元ファイルを確認してください");
            return;
        }

        ApplySnapshot(result.Data);
        IsAvailable = true;
        CanPersistChanges = result.CanPersistChanges;
        Subscribe();

        if (result.Status == BookmarkLoadStatus.RecoveredFromBackup)
        {
            _host.SetStatus("ブックマークをバックアップから復旧しました");
        }
        else if (result.Status == BookmarkLoadStatus.RecoveredFromBackupButPrimaryRepairFailed)
        {
            _host.SetPersistentStatus("ブックマークはバックアップから読み込みましたが、本体を修復できませんでした");
        }
    }

    public async Task AddCurrentPageAsync(BrowserTab? target = null)
    {
        if (!EnsureWritable())
        {
            return;
        }

        if ((target ?? _host.GetActiveBrowserCommandTarget()) is not { } tab)
        {
            _host.SetStatus("ブックマークに追加するページがありません");
            return;
        }

        var url = _host.GetCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(url) || !IsBookmarkableUrl(url))
        {
            _host.SetStatus("このページはブックマークに追加できません");
            return;
        }

        var normalizedUrl = BookmarkService.NormalizeUrl(url);
        var title = _host.GetCurrentPageTitle(tab, normalizedUrl);
        var added = false;
        var result = await _stateStore.UpdateAsync(data =>
        {
            var existing = data.Bookmarks.FirstOrDefault(bookmark =>
                BookmarkService.UrlComparer.Equals(bookmark.Url, normalizedUrl));
            if (existing is null)
            {
                data.Bookmarks.Insert(0, new BookmarkItem
                {
                    Url = normalizedUrl,
                    Title = title,
                    SortOrder = 0,
                    CreatedAt = _now()
                });
                added = true;
            }
            else
            {
                existing.Title = title;
                existing.CreatedAt = existing.CreatedAt == default ? _now() : existing.CreatedAt;
            }

            return true;
        });
        ReportMutationResult(
            result,
            added ? "ブックマークに追加しました" : "既存のブックマークを更新しました");
    }

    public async Task OpenAsync(
        BookmarkItem bookmark,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(bookmark.Url))
        {
            return;
        }

        await RecordOpenAsync(bookmark);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var activeTab = _host.ActiveTab;
        if (activeTab is not null && _host.IsNewTab(activeTab))
        {
            await _host.NavigateTabToUrlAsync(
                activeTab,
                bookmark.Url,
                bookmark.Title,
                cancellationToken);
        }
        else
        {
            _host.OpenTab(bookmark.Url, bookmark.Title);
        }
    }

    public async Task RecordOpenAsync(BookmarkItem bookmark)
    {
        if (!_recordOpenUsage)
        {
            return;
        }

        if (!EnsureWritable())
        {
            return;
        }

        var result = await _stateStore.UpdateAsync(data =>
        {
            var current = data.Bookmarks.FirstOrDefault(item => item.Id == bookmark.Id);
            if (current is null)
            {
                return false;
            }

            current.OpenCount++;
            current.LastOpenedAt = _now();
            return true;
        });
        if (!result.Persisted)
        {
            ReportMutationResult(result, string.Empty);
        }
    }

    public async Task DeleteAsync(Guid bookmarkId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!EnsureWritable())
        {
            return;
        }

        var removed = false;
        var result = await _stateStore.UpdateAsync(data =>
        {
            var bookmark = data.Bookmarks.FirstOrDefault(item => item.Id == bookmarkId);
            if (bookmark is null)
            {
                return false;
            }

            data.Bookmarks.Remove(bookmark);
            removed = true;
            return true;
        });
        if (!removed)
        {
            _host.SetStatus("削除するブックマークが見つかりません");
            return;
        }

        if (!result.Persisted)
        {
            ReportMutationResult(result, string.Empty);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _host.CloseAddressSuggestions();
        _host.QueueSessionSave();
        if (result.Persisted)
        {
            _host.SetStatus("ブックマークを削除しました");
        }
    }

    internal async Task ApplyManagerChangesAsync(BookmarkChangeSet changeSet)
    {
        if (!EnsureWritable())
        {
            return;
        }

        var result = await _stateStore.UpdateAsync(changeSet.ApplyTo);
        ReportMutationResult(result, "ブックマークを更新しました");
    }

    public void Dispose()
    {
        if (!_isSubscribed)
        {
            return;
        }

        _stateStore.Changed -= StateStore_Changed;
        _isSubscribed = false;
    }

    private void Subscribe()
    {
        if (_isSubscribed)
        {
            return;
        }

        _stateStore.Changed += StateStore_Changed;
        _isSubscribed = true;
    }

    private void StateStore_Changed(object? sender, BookmarkStateChangedEventArgs e)
    {
        ApplySnapshot(e.Snapshot);
        _host.RefreshLoadedNewTabPages();
    }

    private void ApplySnapshot(BookmarkData data)
    {
        SynchronizeBookmarks(data.Bookmarks);
        SynchronizeFolders(data.Folders);
        SortMode = data.SortMode;
    }

    private void SynchronizeBookmarks(IReadOnlyList<BookmarkItem> source)
    {
        var sourceIds = source.Select(static item => item.Id).ToHashSet();
        foreach (var existing in _bookmarks.Where(item => !sourceIds.Contains(item.Id)).ToList())
        {
            _bookmarks.Remove(existing);
        }

        for (var index = 0; index < source.Count; index++)
        {
            var sourceItem = source[index];
            var existing = _bookmarks.FirstOrDefault(item => item.Id == sourceItem.Id);
            if (existing is null)
            {
                _bookmarks.Insert(index, BookmarkDataSnapshot.Clone(sourceItem));
                continue;
            }

            CopyBookmark(sourceItem, existing);
            var currentIndex = _bookmarks.IndexOf(existing);
            if (currentIndex != index)
            {
                _bookmarks.RemoveAt(currentIndex);
                _bookmarks.Insert(index, existing);
            }
        }
    }

    private void SynchronizeFolders(IReadOnlyList<BookmarkFolderItem> source)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var sourcePaths = source.Select(static folder => folder.Path).ToHashSet(comparer);
        foreach (var existing in _folders.Where(folder => !sourcePaths.Contains(folder.Path)).ToList())
        {
            _folders.Remove(existing);
        }

        for (var index = 0; index < source.Count; index++)
        {
            var sourceItem = source[index];
            var existing = _folders.FirstOrDefault(folder => comparer.Equals(folder.Path, sourceItem.Path));
            if (existing is null)
            {
                _folders.Insert(index, BookmarkDataSnapshot.Clone(sourceItem));
                continue;
            }

            existing.Path = sourceItem.Path;
            existing.SortOrder = sourceItem.SortOrder;
            existing.CreatedAt = sourceItem.CreatedAt;
            var currentIndex = _folders.IndexOf(existing);
            if (currentIndex != index)
            {
                _folders.RemoveAt(currentIndex);
                _folders.Insert(index, existing);
            }
        }
    }

    private static void CopyBookmark(BookmarkItem source, BookmarkItem target)
    {
        target.Url = source.Url;
        target.Title = source.Title;
        target.FolderPath = source.FolderPath;
        target.SortOrder = source.SortOrder;
        target.CreatedAt = source.CreatedAt;
        target.LastOpenedAt = source.LastOpenedAt;
        target.OpenCount = source.OpenCount;
    }

    private void ReportMutationResult(BookmarkMutationResult result, string successMessage)
    {
        if (!result.Persisted)
        {
            _host.SetPersistentStatus(
                "ブックマークの変更を保存できませんでした。変更はメモリに保持し、次の変更時に再試行します");
            return;
        }

        if (result.Applied && !string.IsNullOrWhiteSpace(successMessage))
        {
            _host.SetStatus(successMessage);
        }
    }

    private bool EnsureWritable()
    {
        if (CanPersistChanges)
        {
            return true;
        }

        _host.SetStatus("ブックマークを読み込めないため、変更を保存できません");
        return false;
    }

    private static bool IsBookmarkableUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
    }
}
