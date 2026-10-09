using System.IO;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public sealed class WorkspaceService
{
    public const int DefaultWorkspaceCount = 9;

    private static readonly WorkspaceChangeHub ChangeHub = new();
    private static readonly SemaphoreSlim DefaultWorkspaceInitializationGate = new(1, 1);
    private readonly IWorkspaceRepository _repository;

    public WorkspaceService()
        : this(new SQLiteWorkspaceRepository())
    {
    }

    public WorkspaceService(IWorkspaceRepository repository)
    {
        _repository = repository;
    }

    public async Task<Workspace> CreateWorkspaceAsync(
        string name,
        int index,
        CancellationToken cancellationToken = default)
    {
        return await _repository.CreateWorkspaceAsync(new Workspace
        {
            Name = name,
            Index = index
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var workspaces = await _repository.GetWorkspacesAsync(cancellationToken);
        if (workspaces.Count >= DefaultWorkspaceCount)
        {
            return workspaces;
        }

        // Repositoryをワーカー化しても、複数Windowの初回生成が同じ番号を重複作成しない。
        await DefaultWorkspaceInitializationGate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureDefaultWorkspacesAsync(cancellationToken);
        }
        finally { DefaultWorkspaceInitializationGate.Release(); }
    }

    private async Task<IReadOnlyList<Workspace>> EnsureDefaultWorkspacesAsync(CancellationToken cancellationToken)
    {
        var workspaces = await _repository.GetWorkspacesAsync(cancellationToken);

        var existingIndexes = workspaces
            .Select(static x => x.Index)
            .ToHashSet();
        var result = workspaces.ToList();

        for (var index = 1; index <= DefaultWorkspaceCount; index++)
        {
            if (existingIndexes.Contains(index))
            {
                continue;
            }

            result.Add(await CreateWorkspaceAsync($"Workspace {index}", index, cancellationToken));
        }

        return result
            .OrderBy(static x => x.Index)
            .ThenBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task UpdateWorkspaceNameAsync(
        Guid workspaceId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Workspace名を入力してください。", nameof(name));
        }

        await _repository.UpdateWorkspaceNameAsync(workspaceId, normalizedName, cancellationToken);
        ChangeHub.Publish(new WorkspaceNameChangedChange(workspaceId, normalizedName));
    }

    public Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetWorkspaceItemsAsync(workspaceId, cancellationToken);
    }

    public Task<WorkspaceItem?> GetItemByIdAsync(Guid itemId, CancellationToken cancellationToken = default)
        => _repository.GetItemByIdAsync(itemId, cancellationToken);

    public async Task<BookmarkItem> AddBookmarkAsync(
        Guid workspaceId,
        string title,
        string url,
        double x,
        double y,
        Guid? parentFolderId = null,
        string note = "",
        string faviconPngBase64 = "",
        string thumbnailPngBase64 = "",
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default,
        string? containerProfileId = null)
    {
        var item = await _repository.AddBookmarkAsync(new BookmarkItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = title,
            Url = url,
            ContainerProfileId = containerProfileId,
            Note = note,
            FaviconPngBase64 = faviconPngBase64,
            ThumbnailPngBase64 = thumbnailPngBase64,
            ThumbnailUpdatedAt = string.IsNullOrWhiteSpace(thumbnailPngBase64) ? null : DateTimeOffset.Now,
            Tags = tags?.ToList() ?? [],
            X = x,
            Y = y
        }, cancellationToken);
        PublishItemsChanged([item]);
        return item;
    }

    public async Task<FolderItem> AddFolderAsync(
        Guid workspaceId,
        string title,
        double x,
        double y,
        Guid? parentFolderId = null,
        string note = "",
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var item = await _repository.AddFolderAsync(new FolderItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = title,
            Note = note,
            Tags = tags?.ToList() ?? [],
            X = x,
            Y = y
        }, cancellationToken);
        PublishItemsChanged([item]);
        return item;
    }

    public async Task<SessionGroupItem> AddSessionGroupAsync(
        Guid workspaceId,
        string title,
        IEnumerable<SessionTab> tabs,
        double x,
        double y,
        Guid? parentFolderId = null,
        string note = "",
        string thumbnailPngBase64 = "",
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var tabList = tabs.ToList();
        var item = await _repository.AddSessionGroupAsync(new SessionGroupItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = title,
            Note = note,
            Tags = tags?.ToList() ?? [],
            FaviconPngBase64 = tabList.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x.FaviconPngBase64))?.FaviconPngBase64 ?? string.Empty,
            ThumbnailPngBase64 = thumbnailPngBase64,
            ThumbnailUpdatedAt = string.IsNullOrWhiteSpace(thumbnailPngBase64) ? null : DateTimeOffset.Now,
            Tabs = tabList,
            X = x,
            Y = y
        }, cancellationToken);
        PublishItemsChanged([item]);
        return item;
    }

    internal async Task<IReadOnlyList<WorkspaceItem>> SaveMixedTabBatchAsync(WorkspaceTabSavePlan plan,
        Guid workspaceId, Guid? parentFolderId, double x, double y, string groupTitle)
    {
        var items = WorkspaceTabSavePlanner.CreateMixedSaveItems(plan, workspaceId, parentFolderId, x, y, groupTitle);
        await _repository.AddItemsAsync(items);
        PublishItemsChanged(items);
        return items;
    }

    public async Task<NoteItem> AddNoteAsync(
        Guid workspaceId,
        string title,
        string content,
        double x,
        double y,
        Guid? parentFolderId = null,
        IEnumerable<string>? tags = null,
        Guid? itemId = null,
        CancellationToken cancellationToken = default,
        double fontSize = WorkspaceNoteFontSizeProfile.DefaultFontSize)
    {
        var item = await _repository.AddNoteAsync(new NoteItem
        {
            Id = itemId is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = title,
            Content = content,
            FontSize = fontSize,
            Tags = tags?.ToList() ?? [],
            X = x,
            Y = y
        }, cancellationToken);
        PublishItemsChanged([item]);
        return item;
    }

    public async Task<YouTubeMiniPlayerSetItem> AddYouTubeMiniPlayerSetAsync(
        Guid workspaceId,
        string title,
        IEnumerable<YouTubeMiniPlayerWorkspaceState> players,
        double x,
        double y,
        Guid? parentFolderId = null,
        string note = "",
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var playerList = players.ToList();
        var item = await _repository.AddYouTubeMiniPlayerSetAsync(new YouTubeMiniPlayerSetItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = title,
            Url = playerList.FirstOrDefault()?.Url ?? string.Empty,
            Note = note,
            Tags = tags?.ToList() ?? [],
            Players = playerList,
            X = x,
            Y = y
        }, cancellationToken);
        PublishItemsChanged([item]);
        return item;
    }

    // FolderTabからの保存手順。設定と取消tokenは従来の各参照時点で取得する。
    internal static async Task SaveFolderTabPathsAsync(
        IReadOnlyList<string> paths,
        Func<Guid?> getPreferredWorkspaceId,
        Func<CancellationToken> getCancellationToken,
        Action<Workspace, int> completed,
        Func<WorkspaceService>? createService = null)
    {
        try
        {
            var existingPaths = paths
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (existingPaths.Length == 0)
            {
                throw new FileNotFoundException("保存する項目が見つかりません。");
            }

            var service = createService is null ? new WorkspaceService() : createService();
            var workspaces = await service.GetWorkspacesAsync(getCancellationToken());
            var workspace = workspaces.FirstOrDefault(item => item.Id == getPreferredWorkspaceId())
                ?? workspaces.OrderBy(item => item.Index).FirstOrDefault()
                ?? throw new InvalidOperationException("保存先Workspaceが見つかりません。");
            var currentItems = await service.GetWorkspaceItemsAsync(
                workspace.Id,
                getCancellationToken());
            var startIndex = currentItems.Count;
            var shellItemService = new WorkspaceShellItemService();
            var items = existingPaths.Select((path, index) =>
            {
                var positionIndex = startIndex + index;
                return new ShellItem
                {
                    WorkspaceId = workspace.Id,
                    Title = WorkspaceShellItemService.GetDisplayName(path),
                    TargetPath = WorkspaceShellItemService.NormalizePath(path),
                    Kind = shellItemService.Classify(path),
                    OpenInFolderTab = Directory.Exists(path),
                    X = 32 + positionIndex % 6 * 176,
                    Y = 32 + positionIndex / 6 * 112
                };
            }).ToArray();
            await service.AddShellItemsAsync(items, getCancellationToken());
            completed(workspace, items.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<ShellItem>> AddShellItemsAsync(
        IReadOnlyList<ShellItem> items,
        CancellationToken cancellationToken = default)
    {
        var savedItems = await _repository.AddShellItemsAsync(items, cancellationToken);
        PublishItemsChanged(savedItems.Cast<WorkspaceItem>().ToList());
        return savedItems;
    }

    public async Task<NoteItem> ConvertShellItemToNoteAsync(
        ShellItem source,
        string title,
        string content,
        double fontSize = WorkspaceNoteFontSizeProfile.DefaultFontSize,
        CancellationToken cancellationToken = default)
    {
        var replacement = await _repository.ConvertShellItemToNoteAsync(new NoteItem
        {
            Id = source.Id,
            WorkspaceId = source.WorkspaceId,
            ParentFolderId = source.ParentFolderId,
            Title = title,
            Content = content,
            FontSize = fontSize,
            Tags = [.. source.Tags],
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            ZIndex = source.ZIndex,
            CreatedAt = source.CreatedAt,
            UpdatedAt = DateTimeOffset.Now
        }, cancellationToken);
        PublishItemsChanged([replacement], [source.Id]);
        return replacement;
    }

    public async Task<WorkspaceItem> DuplicateWorkspaceItemAsync(
        WorkspaceItem source,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        var duplicate = CreateWorkspaceItemCopy(source, x, y);
        WorkspaceItem savedItem = duplicate switch
        {
            BookmarkItem item => await _repository.AddBookmarkAsync(item, cancellationToken),
            FolderItem item => await _repository.AddFolderAsync(item, cancellationToken),
            SessionGroupItem item => await _repository.AddSessionGroupAsync(item, cancellationToken),
            YouTubeMiniPlayerSetItem item => await _repository.AddYouTubeMiniPlayerSetAsync(item, cancellationToken),
            NoteItem item => await _repository.AddNoteAsync(item, cancellationToken),
            ShellItem item => (await _repository.AddShellItemsAsync([item], cancellationToken)).Single(),
            _ => throw new NotSupportedException($"コピーできないカード種別です: {source.Type}")
        };
        PublishItemsChanged([savedItem]);
        return savedItem;
    }

    public async Task UpdateItemPositionAsync(
        Guid itemId,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemPositionAsync(itemId, x, y, cancellationToken);
        ChangeHub.Publish(new WorkspaceItemPositionsChangedChange(
            Guid.NewGuid(),
            [new WorkspaceItemPosition(itemId, x, y)]));
    }

    public async Task UpdateItemPositionsAsync(
        IReadOnlyList<WorkspaceItemPosition> positions,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemPositionsAsync(positions, cancellationToken);
        if (positions.Count > 0)
        {
            ChangeHub.Publish(new WorkspaceItemPositionsChangedChange(
                Guid.NewGuid(),
                positions));
        }
    }

    public async Task<IReadOnlyList<WorkspaceItem>> MoveItemsToWorkspaceAsync(
        IReadOnlyList<WorkspaceItemMove> moves,
        CancellationToken cancellationToken = default)
    {
        var movedItems = await _repository.MoveItemsToWorkspaceAsync(moves, cancellationToken);
        ChangeHub.Publish(new WorkspaceItemsMovedChange(
            Guid.NewGuid(),
            moves,
            movedItems));
        return movedItems;
    }

    internal void SubscribeToChanges(IWorkspaceChangeListener listener)
    {
        ChangeHub.Subscribe(listener);
    }

    public void PublishItemsChanged(
        IReadOnlyList<WorkspaceItem> upsertedItems,
        IReadOnlyCollection<Guid>? deletedItemIds = null)
    {
        if (upsertedItems.Count == 0 && deletedItemIds is not { Count: > 0 })
        {
            return;
        }

        ChangeHub.Publish(new WorkspaceItemsChangedChange(
            Guid.NewGuid(),
            upsertedItems,
            deletedItemIds?.ToList() ?? []));
    }

    public async Task UpdateItemFaviconAsync(
        Guid itemId,
        string faviconPngBase64,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemFaviconAsync(itemId, faviconPngBase64, cancellationToken);
        ChangeHub.Publish(new WorkspaceItemFaviconChangedChange(itemId, faviconPngBase64));
    }

    public async Task UpdateItemFaviconsAsync(
        IReadOnlyDictionary<Guid, string> favicons,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemFaviconsAsync(favicons, cancellationToken);
        foreach (var favicon in favicons)
        {
            ChangeHub.Publish(new WorkspaceItemFaviconChangedChange(favicon.Key, favicon.Value));
        }
    }

    public async Task UpdateItemThumbnailAsync(
        Guid itemId,
        string thumbnailPngBase64,
        DateTimeOffset thumbnailUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemThumbnailAsync(itemId, thumbnailPngBase64, thumbnailUpdatedAt, cancellationToken);
        ChangeHub.Publish(new WorkspaceItemThumbnailChangedChange(
            itemId,
            thumbnailPngBase64,
            thumbnailUpdatedAt));
    }

    public async Task UpdateItemThumbnailsAsync(
        IReadOnlyDictionary<Guid, (string ThumbnailPngBase64, DateTimeOffset ThumbnailUpdatedAt)> thumbnails,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateItemThumbnailsAsync(thumbnails, cancellationToken);
        foreach (var thumbnail in thumbnails)
        {
            ChangeHub.Publish(new WorkspaceItemThumbnailChangedChange(
                thumbnail.Key,
                thumbnail.Value.ThumbnailPngBase64,
                thumbnail.Value.ThumbnailUpdatedAt));
        }
    }

    public async Task UpdateSessionTabThumbnailsAsync(
        Guid sessionGroupItemId,
        IReadOnlyDictionary<Guid, string> thumbnails,
        CancellationToken cancellationToken = default)
    {
        await _repository.UpdateSessionTabThumbnailsAsync(
            sessionGroupItemId,
            thumbnails,
            cancellationToken);
        ChangeHub.Publish(new WorkspaceSessionTabThumbnailsChangedChange(
            sessionGroupItemId,
            thumbnails));
    }

    public async Task UpdateShellItemTargetAsync(
        Guid itemId,
        string targetPath,
        ShellItemKind kind,
        CancellationToken cancellationToken = default,
        string? expectedTargetPath = null)
    {
        await _repository.UpdateShellItemTargetAsync(itemId, targetPath, kind, cancellationToken, expectedTargetPath);
        ChangeHub.Publish(new WorkspaceShellItemTargetChangedChange(itemId, targetPath, kind));
    }

    public async Task RenameItemAsync(
        Guid itemId,
        string title,
        CancellationToken cancellationToken = default)
    {
        await _repository.RenameItemAsync(itemId, title, cancellationToken);
        ChangeHub.Publish(new WorkspaceItemTitleChangedChange(itemId, title));
    }

    public async Task UpdateNoteAsync(
        Guid itemId,
        string title,
        string content,
        double fontSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedFontSize = WorkspaceNoteFontSizeProfile.Normalize(fontSize);
        await _repository.UpdateNoteAsync(itemId, title, content, normalizedFontSize, cancellationToken);
        ChangeHub.Publish(new WorkspaceNoteChangedChange(itemId, title, content, normalizedFontSize));
    }

    public Task<IReadOnlyList<Guid>> DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return DeleteItemsAsync([itemId], cancellationToken);
    }

    public async Task DeleteShellItemForUndoAsync(Guid itemId, Guid workspaceId, string expectedTargetPath,
        ShellItemKind expectedKind, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteShellItemForUndoAsync(itemId, workspaceId, expectedTargetPath,
            expectedKind, cancellationToken);
        PublishItemsChanged([], [itemId]);
    }

    public async Task<IReadOnlyList<Guid>> DeleteItemsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteItemsAsync(itemIds, cancellationToken);
        PublishItemsChanged([], deleted);
        return deleted;
    }

    public Task<IReadOnlyList<WorkspaceItem>> GetDeletedItemsAsync(CancellationToken cancellationToken = default)
        => _repository.GetDeletedItemsAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkspaceItem>> RestoreItemAsync(Guid itemId, Guid workspaceId, Guid? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        var restored = await _repository.RestoreItemAsync(itemId, workspaceId, parentFolderId, cancellationToken);
        PublishItemsChanged(restored);
        return restored;
    }

    public Task PermanentlyDeleteItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, DateTimeOffset>? expectedDeletionTimes = null)
        => _repository.PermanentlyDeleteItemsAsync(itemIds, cancellationToken, expectedDeletionTimes);

    public Task<IReadOnlyList<WorkspaceItem>> SearchItemsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        return _repository.SearchItemsAsync(query, cancellationToken);
    }

    private static WorkspaceItem CreateWorkspaceItemCopy(WorkspaceItem source, double x, double y)
    {
        WorkspaceItem duplicate = source switch
        {
            BookmarkItem => new BookmarkItem(),
            FolderItem => new FolderItem(),
            SessionGroupItem sessionGroup => new SessionGroupItem
            {
                Tabs = sessionGroup.Tabs.Select(static tab => new SessionTab
                {
                    Url = tab.Url,
                    Title = tab.Title,
                    FaviconPngBase64 = tab.FaviconPngBase64,
                    ThumbnailPngBase64 = tab.ThumbnailPngBase64,
                    SourceTabId = tab.SourceTabId,
                    SortOrder = tab.SortOrder,
                    IsFolderTab = tab.IsFolderTab,
                    ContainerProfileId = tab.ContainerProfileId,
                    SplitGroupId = tab.SplitGroupId,
                    SplitPane = tab.SplitPane,
                    SplitOrientation = tab.SplitOrientation,
                    SplitRatio = tab.SplitRatio,
                    SplitActivePane = tab.SplitActivePane,
                    SavedAt = tab.SavedAt
                }).ToList()
            },
            YouTubeMiniPlayerSetItem playerSet => new YouTubeMiniPlayerSetItem
            {
                Players = playerSet.Players.Select(CloneYouTubeMiniPlayerState).ToList()
            },
            NoteItem noteItem => new NoteItem
            {
                FontSize = noteItem.FontSize
            },
            ShellItem shellItem => new ShellItem
            {
                TargetPath = shellItem.TargetPath,
                Kind = shellItem.Kind,
                OpenInFolderTab = shellItem.OpenInFolderTab
            },
            _ => throw new NotSupportedException($"コピーできないカード種別です: {source.Type}")
        };

        duplicate.WorkspaceId = source.WorkspaceId;
        duplicate.ParentFolderId = source.ParentFolderId;
        duplicate.Title = source.Title;
        duplicate.Url = source.Url;
        duplicate.ContainerProfileId = source.ContainerProfileId;
        duplicate.Note = source.Note;
        duplicate.FaviconPngBase64 = source.FaviconPngBase64;
        duplicate.ThumbnailPngBase64 = source.ThumbnailPngBase64;
        duplicate.ThumbnailUpdatedAt = source.ThumbnailUpdatedAt;
        duplicate.Tags = [.. source.Tags];
        duplicate.X = x;
        duplicate.Y = y;
        duplicate.Width = source.Width;
        duplicate.Height = source.Height;
        duplicate.ZIndex = source.ZIndex;
        return duplicate;
    }

    private static YouTubeMiniPlayerWorkspaceState CloneYouTubeMiniPlayerState(
        YouTubeMiniPlayerWorkspaceState source)
    {
        return new YouTubeMiniPlayerWorkspaceState
        {
            Url = source.Url,
            Title = source.Title,
            ContainerProfileId = source.ContainerProfileId,
            IsShorts = source.IsShorts,
            Left = source.Left,
            Top = source.Top,
            Width = source.Width,
            Height = source.Height,
            IsMuted = source.IsMuted,
            Volume = source.Volume,
            PlaybackRate = source.PlaybackRate,
            LastPositionSeconds = source.LastPositionSeconds,
            DurationSeconds = source.DurationSeconds,
            IsPaused = source.IsPaused,
            IsLooping = source.IsLooping,
            IsMinimized = source.IsMinimized,
            IsTopmost = source.IsTopmost
        };
    }
}

public interface IWorkspaceChangeListener
{
    void OnWorkspaceNameChanged(WorkspaceNameChangedChange change);

    void OnWorkspaceItemsMoved(WorkspaceItemsMovedChange change);

    void OnWorkspaceItemPositionsChanged(WorkspaceItemPositionsChangedChange change);

    void OnWorkspaceItemsChanged(WorkspaceItemsChangedChange change);

    void OnWorkspaceItemContentChanged(WorkspaceItemContentChange change);
}

public sealed record WorkspaceNameChangedChange(Guid WorkspaceId, string Name);

public sealed record WorkspaceItemsMovedChange(
    Guid OperationId,
    IReadOnlyList<WorkspaceItemMove> Moves,
    IReadOnlyList<WorkspaceItem> Items);

public sealed record WorkspaceItemPositionsChangedChange(
    Guid OperationId,
    IReadOnlyList<WorkspaceItemPosition> Positions);

public sealed record WorkspaceItemsChangedChange(
    Guid OperationId,
    IReadOnlyList<WorkspaceItem> UpsertedItems,
    IReadOnlyList<Guid> DeletedItemIds);

public abstract record WorkspaceItemContentChange(Guid ItemId);

public sealed record WorkspaceItemTitleChangedChange(Guid ChangedItemId, string Title)
    : WorkspaceItemContentChange(ChangedItemId);

public sealed record WorkspaceNoteChangedChange(
    Guid ChangedItemId,
    string Title,
    string Content,
    double FontSize)
    : WorkspaceItemContentChange(ChangedItemId);

public sealed record WorkspaceItemFaviconChangedChange(Guid ChangedItemId, string FaviconPngBase64)
    : WorkspaceItemContentChange(ChangedItemId);

public sealed record WorkspaceItemThumbnailChangedChange(
    Guid ChangedItemId,
    string ThumbnailPngBase64,
    DateTimeOffset ThumbnailUpdatedAt)
    : WorkspaceItemContentChange(ChangedItemId);

public sealed record WorkspaceSessionTabThumbnailsChangedChange(
    Guid ChangedItemId,
    IReadOnlyDictionary<Guid, string> Thumbnails)
    : WorkspaceItemContentChange(ChangedItemId);

public sealed record WorkspaceShellItemTargetChangedChange(
    Guid ChangedItemId,
    string TargetPath,
    ShellItemKind Kind)
    : WorkspaceItemContentChange(ChangedItemId);

internal sealed class WorkspaceChangeHub
{
    private readonly object _syncRoot = new();
    private readonly List<WeakReference<IWorkspaceChangeListener>> _listeners = [];

    public void Subscribe(IWorkspaceChangeListener listener)
    {
        lock (_syncRoot)
        {
            if (_listeners.Any(reference => reference.TryGetTarget(out var target)
                && ReferenceEquals(target, listener)))
            {
                return;
            }

            _listeners.Add(new WeakReference<IWorkspaceChangeListener>(listener));
        }
    }

    public void Publish(WorkspaceItemsMovedChange change)
    {
        foreach (var listener in GetListeners())
        {
            try
            {
                listener.OnWorkspaceItemsMoved(change);
            }
            catch
            {
                // 1つの表示更新失敗で、確定済み移動や他Windowへの通知を失敗扱いにしません。
            }
        }
    }

    public void Publish(WorkspaceNameChangedChange change)
    {
        foreach (var listener in GetListeners())
        {
            try
            {
                listener.OnWorkspaceNameChanged(change);
            }
            catch
            {
                // 1つの表示更新失敗で、保存済みの名前変更を他のWindowへ通知できなくしない。
            }
        }
    }

    public void Publish(WorkspaceItemPositionsChangedChange change)
    {
        foreach (var listener in GetListeners())
        {
            try
            {
                listener.OnWorkspaceItemPositionsChanged(change);
            }
            catch
            {
                // DB確定済みの位置変更を、1つの表示更新失敗で失敗扱いにしません。
            }
        }
    }

    public void Publish(WorkspaceItemsChangedChange change)
    {
        foreach (var listener in GetListeners())
        {
            try
            {
                listener.OnWorkspaceItemsChanged(change);
            }
            catch
            {
                // 1つの表示更新失敗で、確定済み変更の他Windowへの通知を止めない。
            }
        }
    }

    public void Publish(WorkspaceItemContentChange change)
    {
        foreach (var listener in GetListeners())
        {
            try
            {
                listener.OnWorkspaceItemContentChanged(change);
            }
            catch
            {
                // 1つの表示更新失敗で、保存済みの内容変更を他Windowへ通知できなくしません。
            }
        }
    }

    private List<IWorkspaceChangeListener> GetListeners()
    {
        lock (_syncRoot)
        {
            var listeners = new List<IWorkspaceChangeListener>();
            for (var index = _listeners.Count - 1; index >= 0; index--)
            {
                if (_listeners[index].TryGetTarget(out var listener))
                {
                    listeners.Add(listener);
                }
                else
                {
                    _listeners.RemoveAt(index);
                }
            }

            return listeners;
        }
    }
}
