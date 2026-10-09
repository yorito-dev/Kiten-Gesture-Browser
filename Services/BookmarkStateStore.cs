using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record BookmarkMutationResult(
    bool Applied,
    bool Persisted,
    bool HasPendingChanges,
    Exception? Error = null);

internal sealed class BookmarkStateChangedEventArgs(BookmarkData snapshot) : EventArgs
{
    public BookmarkData Snapshot { get; } = snapshot;
}

internal sealed class BookmarkStateStore(IBookmarkStore persistence)
{
    private readonly IBookmarkStore _persistence = persistence;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _stateLock = new();
    private BookmarkData _state = new();
    private BookmarkLoadResult? _loadResult;
    private bool _hasPendingChanges;

    public event EventHandler<BookmarkStateChangedEventArgs>? Changed;

    public BookmarkLoadResult Initialize()
    {
        lock (_stateLock)
        {
            if (_loadResult is null)
            {
                _loadResult = _persistence.LoadData();
                if (_loadResult.CanUseData)
                {
                    _state = BookmarkDataSnapshot.Clone(_loadResult.Data);
                }
            }

            return CloneLoadResult(_loadResult, _state);
        }
    }

    public async Task<BookmarkMutationResult> UpdateAsync(Func<BookmarkData, bool> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var loadResult = Initialize();
        if (!loadResult.CanPersistChanges)
        {
            return new BookmarkMutationResult(
                Applied: false,
                Persisted: false,
                HasPendingChanges: false,
                loadResult.RepairError ?? loadResult.BackupError ?? loadResult.PrimaryError);
        }

        await _saveGate.WaitAsync();
        try
        {
            BookmarkData snapshot;
            bool changed;
            lock (_stateLock)
            {
                changed = update(_state);
                if (changed)
                {
                    _hasPendingChanges = true;
                    UpdateSortOrders(_state);
                }

                if (!changed && !_hasPendingChanges)
                {
                    return new BookmarkMutationResult(
                        Applied: false,
                        Persisted: true,
                        HasPendingChanges: false);
                }

                snapshot = BookmarkDataSnapshot.Clone(_state);
            }

            if (changed)
            {
                Changed?.Invoke(
                    this,
                    new BookmarkStateChangedEventArgs(BookmarkDataSnapshot.Clone(snapshot)));
            }

            try
            {
                await _persistence.SaveDataAsync(snapshot);
                lock (_stateLock)
                {
                    _hasPendingChanges = false;
                }

                return new BookmarkMutationResult(
                    Applied: changed,
                    Persisted: true,
                    HasPendingChanges: false);
            }
            catch (Exception ex)
            {
                return new BookmarkMutationResult(
                    Applied: changed,
                    Persisted: false,
                    HasPendingChanges: true,
                    ex);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static BookmarkLoadResult CloneLoadResult(BookmarkLoadResult result, BookmarkData currentState)
    {
        return new BookmarkLoadResult(
            BookmarkDataSnapshot.Clone(currentState),
            result.Status,
            result.PrimaryError,
            result.BackupError,
            result.RepairError);
    }

    private static void UpdateSortOrders(BookmarkData data)
    {
        for (var i = 0; i < data.Bookmarks.Count; i++)
        {
            data.Bookmarks[i].SortOrder = i;
        }

        for (var i = 0; i < data.Folders.Count; i++)
        {
            data.Folders[i].SortOrder = i;
        }
    }
}

internal sealed class BookmarkChangeSet
{
    private readonly BookmarkData _before;
    private readonly BookmarkData _after;

    private BookmarkChangeSet(BookmarkData before, BookmarkData after)
    {
        _before = BookmarkDataSnapshot.Clone(before);
        _after = BookmarkDataSnapshot.Clone(after);
    }

    public static BookmarkChangeSet Create(BookmarkData before, BookmarkData after)
        => new(before, after);

    public bool ApplyTo(BookmarkData current)
    {
        var changed = ApplyBookmarkChanges(current.Bookmarks);
        changed |= ApplyFolderChanges(current.Folders);
        if (_before.SortMode != _after.SortMode && current.SortMode != _after.SortMode)
        {
            current.SortMode = _after.SortMode;
            changed = true;
        }

        return changed;
    }

    private bool ApplyBookmarkChanges(List<BookmarkItem> current)
    {
        var changed = false;
        var beforeById = _before.Bookmarks.ToDictionary(static item => item.Id);
        var afterById = _after.Bookmarks.ToDictionary(static item => item.Id);

        foreach (var deletedId in beforeById.Keys.Except(afterById.Keys).ToList())
        {
            var currentItem = current.FirstOrDefault(item => item.Id == deletedId);
            if (currentItem is not null)
            {
                current.Remove(currentItem);
                changed = true;
            }
        }

        foreach (var afterItem in _after.Bookmarks)
        {
            if (!beforeById.TryGetValue(afterItem.Id, out var beforeItem))
            {
                if (current.All(item => item.Id != afterItem.Id))
                {
                    current.Add(BookmarkDataSnapshot.Clone(afterItem));
                    changed = true;
                }

                continue;
            }

            var currentItem = current.FirstOrDefault(item => item.Id == afterItem.Id);
            if (currentItem is null)
            {
                continue;
            }

            changed |= ApplyChangedBookmarkValues(beforeItem, afterItem, currentItem);
        }

        changed |= ReorderBookmarks(current);
        return changed;
    }

    private bool ApplyFolderChanges(List<BookmarkFolderItem> current)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var beforeByPath = _before.Folders.ToDictionary(static folder => folder.Path, comparer);
        var afterByPath = _after.Folders.ToDictionary(static folder => folder.Path, comparer);
        var changed = false;

        foreach (var deletedPath in beforeByPath.Keys.Except(afterByPath.Keys, comparer).ToList())
        {
            var currentFolder = current.FirstOrDefault(folder => comparer.Equals(folder.Path, deletedPath));
            if (currentFolder is not null)
            {
                current.Remove(currentFolder);
                changed = true;
            }
        }

        foreach (var afterFolder in _after.Folders)
        {
            if (!beforeByPath.TryGetValue(afterFolder.Path, out var beforeFolder))
            {
                if (current.All(folder => !comparer.Equals(folder.Path, afterFolder.Path)))
                {
                    current.Add(BookmarkDataSnapshot.Clone(afterFolder));
                    changed = true;
                }

                continue;
            }

            var currentFolder = current.FirstOrDefault(folder => comparer.Equals(folder.Path, afterFolder.Path));
            if (currentFolder is null)
            {
                continue;
            }

            if (beforeFolder.CreatedAt != afterFolder.CreatedAt
                && currentFolder.CreatedAt != afterFolder.CreatedAt)
            {
                currentFolder.CreatedAt = afterFolder.CreatedAt;
                changed = true;
            }
        }

        changed |= ReorderFolders(current);
        return changed;
    }

    private static bool ApplyChangedBookmarkValues(
        BookmarkItem before,
        BookmarkItem after,
        BookmarkItem current)
    {
        var changed = false;
        changed |= ApplyChangedValue(before.Url, after.Url, current.Url, value => current.Url = value);
        changed |= ApplyChangedValue(before.Title, after.Title, current.Title, value => current.Title = value);
        changed |= ApplyChangedValue(
            before.FolderPath,
            after.FolderPath,
            current.FolderPath,
            value => current.FolderPath = value);
        changed |= ApplyChangedValue(
            before.CreatedAt,
            after.CreatedAt,
            current.CreatedAt,
            value => current.CreatedAt = value);
        changed |= ApplyChangedValue(
            before.LastOpenedAt,
            after.LastOpenedAt,
            current.LastOpenedAt,
            value => current.LastOpenedAt = value);
        changed |= ApplyChangedValue(
            before.OpenCount,
            after.OpenCount,
            current.OpenCount,
            value => current.OpenCount = value);
        return changed;
    }

    private static bool ApplyChangedValue<T>(T before, T after, T current, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)
            || EqualityComparer<T>.Default.Equals(current, after))
        {
            return false;
        }

        apply(after);
        return true;
    }

    private bool ReorderBookmarks(List<BookmarkItem> current)
    {
        var managedIds = _after.Bookmarks.Select(static item => item.Id).ToHashSet();
        var currentById = current.ToDictionary(static item => item.Id);
        var ordered = _after.Bookmarks
            .Where(item => currentById.ContainsKey(item.Id))
            .Select(item => currentById[item.Id])
            .ToList();
        var slots = current
            .Select((item, index) => new { item, index })
            .Where(entry => managedIds.Contains(entry.item.Id))
            .Select(static entry => entry.index)
            .ToList();
        return ApplyOrder(current, slots, ordered);
    }

    private bool ReorderFolders(List<BookmarkFolderItem> current)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var afterPaths = _after.Folders.Select(static folder => folder.Path).ToHashSet(comparer);
        var currentByPath = current.ToDictionary(static folder => folder.Path, comparer);
        var ordered = _after.Folders
            .Where(folder => currentByPath.ContainsKey(folder.Path))
            .Select(folder => currentByPath[folder.Path])
            .ToList();
        var slots = current
            .Select((folder, index) => new { folder, index })
            .Where(entry => afterPaths.Contains(entry.folder.Path))
            .Select(static entry => entry.index)
            .ToList();
        return ApplyOrder(current, slots, ordered);
    }

    private static bool ApplyOrder<T>(List<T> current, IReadOnlyList<int> slots, IReadOnlyList<T> ordered)
    {
        if (slots.Count != ordered.Count)
        {
            return false;
        }

        var changed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(current[slots[i]], ordered[i]))
            {
                continue;
            }

            current[slots[i]] = ordered[i];
            changed = true;
        }

        return changed;
    }
}

internal static class BookmarkDataSnapshot
{
    public static BookmarkData Clone(BookmarkData data)
    {
        return new BookmarkData
        {
            Version = data.Version,
            SortMode = data.SortMode,
            Bookmarks = data.Bookmarks.Select(Clone).ToList(),
            Folders = data.Folders.Select(Clone).ToList()
        };
    }

    public static BookmarkItem Clone(BookmarkItem item)
    {
        return new BookmarkItem
        {
            Id = item.Id,
            Url = item.Url,
            Title = item.Title,
            FolderPath = item.FolderPath,
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            LastOpenedAt = item.LastOpenedAt,
            OpenCount = item.OpenCount,
            CheckStatusText = item.CheckStatusText,
            CheckStatusDetail = item.CheckStatusDetail
        };
    }

    public static BookmarkFolderItem Clone(BookmarkFolderItem folder)
    {
        return new BookmarkFolderItem
        {
            Path = folder.Path,
            SortOrder = folder.SortOrder,
            CreatedAt = folder.CreatedAt
        };
    }
}
