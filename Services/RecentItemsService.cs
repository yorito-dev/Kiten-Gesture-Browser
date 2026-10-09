using System.IO;
using System.Text.Json;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using WorkspaceBookmarkItem = TabBrowser.Models.Workspaces.BookmarkItem;

namespace TabBrowser.Services;

public sealed class RecentItemsService : IAsyncDisposable
{
    private const int MaximumUnpinnedItems = 100;
    private const int FlushDelayMilliseconds = 30000;
    private const int MaximumThumbnailTextLength = 512 * 1024;
    private const int MaximumFaviconTextLength = 256 * 1024;
    private static readonly TimeSpan DuplicateTouchWindow = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly string _filePath;
    private readonly string _backupFilePath;
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private Dictionary<string, RecentItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _dirty;
    private bool _disposed;

    public RecentItemsService()
        : this(AppPaths.RecentItemsFile)
    {
    }

    public RecentItemsService(string filePath)
    {
        _filePath = filePath;
        _backupFilePath = AtomicFileService.CreateBackupFilePath(filePath);
        _flushTimer = new Timer(
            static state => ((RecentItemsService)state!).OnFlushTimer(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event EventHandler? ItemsChanged;

    public IReadOnlyList<RecentItem> GetSnapshot()
    {
        lock (_syncRoot)
        {
            EnsureLoaded();
            return _items.Values
                .OrderByDescending(static item => item.IsPinned)
                .ThenByDescending(static item => item.LastUsedAt)
                .Select(static item => item with { })
                .ToList();
        }
    }

    public void Record(RecentItem item)
    {
        if (!TryNormalize(item, out var normalized))
        {
            return;
        }

        var changed = false;
        lock (_syncRoot)
        {
            EnsureLoaded();
            if (_disposed)
            {
                return;
            }

            if (_items.TryGetValue(normalized.Key, out var current))
            {
                normalized.IsPinned = current.IsPinned;
                if (HasSamePreviewContext(current, normalized)
                    && string.IsNullOrWhiteSpace(normalized.ThumbnailPngBase64))
                {
                    normalized.ThumbnailPngBase64 = current.ThumbnailPngBase64;
                    normalized.ThumbnailCacheVersion = current.ThumbnailCacheVersion;
                }

                if (HasSamePreviewContext(current, normalized)
                    && string.IsNullOrWhiteSpace(normalized.FaviconPngBase64))
                {
                    normalized.FaviconPngBase64 = current.FaviconPngBase64;
                }

                if (HasSamePreviewContext(current, normalized) && IsStandaloneWebItemKey(normalized.Key)
                    && string.IsNullOrWhiteSpace(normalized.PreviewText))
                {
                    normalized.PreviewText = current.PreviewText;
                }

                if (HasSameContent(current, normalized)
                    && normalized.LastUsedAt - current.LastUsedAt < DuplicateTouchWindow)
                {
                    return;
                }
            }

            _items[normalized.Key] = normalized;
            TrimUnpinnedItems();
            ScheduleFlush();
            changed = true;
        }

        if (changed)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool SetPinned(string key, bool isPinned)
    {
        var changed = false;
        lock (_syncRoot)
        {
            EnsureLoaded();
            if (_disposed
                || !_items.TryGetValue(key, out var current)
                || current.IsPinned == isPinned)
            {
                return false;
            }

            _items[key] = current with { IsPinned = isPinned };
            TrimUnpinnedItems();
            ScheduleFlush();
            changed = true;
        }

        if (changed)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool Remove(string key)
    {
        var removed = false;
        lock (_syncRoot)
        {
            EnsureLoaded();
            if (!_disposed && _items.Remove(key))
            {
                ScheduleFlush();
                removed = true;
            }
        }

        if (removed)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public bool UpdateThumbnail(RecentItem expectedItem, string thumbnailPngBase64)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPngBase64)
            || thumbnailPngBase64.Length > MaximumThumbnailTextLength)
        {
            return false;
        }

        var changed = false;
        lock (_syncRoot)
        {
            EnsureLoaded();
            if (_disposed
                || !_items.TryGetValue(expectedItem.Key, out var current)
                || !HasSamePreviewContext(current, expectedItem)
                || current.LastUsedAt != expectedItem.LastUsedAt
                || string.Equals(current.ThumbnailPngBase64, thumbnailPngBase64, StringComparison.Ordinal))
            {
                return false;
            }

            _items[expectedItem.Key] = current with { ThumbnailPngBase64 = thumbnailPngBase64, ThumbnailCacheVersion = 1 };
            ScheduleFlush();
            changed = true;
        }

        if (changed)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public bool UpdateWebPreview(
        RecentItem expectedItem,
        string thumbnailPngBase64,
        string faviconPngBase64,
        string pageDescription)
    {
        var validThumbnail = !string.IsNullOrWhiteSpace(thumbnailPngBase64)
            && thumbnailPngBase64.Length <= MaximumThumbnailTextLength;
        var validFavicon = !string.IsNullOrWhiteSpace(faviconPngBase64)
            && faviconPngBase64.Length <= MaximumFaviconTextLength;
        var normalizedDescription = WebThumbnailCaptureService.NormalizePageDescription(pageDescription);
        if (!validThumbnail && !validFavicon && string.IsNullOrWhiteSpace(normalizedDescription))
        {
            return false;
        }

        var changed = false;
        lock (_syncRoot)
        {
            EnsureLoaded();
            if (_disposed || !_items.TryGetValue(expectedItem.Key, out var current)
                || !HasSamePreviewContext(current, expectedItem))
            {
                return false;
            }

            var updated = current with
            {
                ThumbnailPngBase64 = validThumbnail
                    ? thumbnailPngBase64
                    : current.ThumbnailPngBase64,
                ThumbnailCacheVersion = validThumbnail || validFavicon ? 1 : current.ThumbnailCacheVersion,
                FaviconPngBase64 = validFavicon
                    ? faviconPngBase64
                    : current.FaviconPngBase64,
                PreviewText = string.IsNullOrWhiteSpace(normalizedDescription)
                    ? current.PreviewText
                    : normalizedDescription
            };
            if (HasSameContent(current, updated))
            {
                return false;
            }

            _items[expectedItem.Key] = updated;
            ScheduleFlush();
            changed = true;
        }

        if (changed)
        {
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RecentItem> snapshot;
            lock (_syncRoot)
            {
                EnsureLoaded();
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                if (!_dirty)
                {
                    return;
                }

                snapshot = _items.Values
                    .OrderByDescending(static item => item.IsPinned)
                    .ThenByDescending(static item => item.LastUsedAt)
                    .ToList();
                _dirty = false;
            }

            try
            {
                await Task.Run(
                        () => AtomicFileService.WriteAsync(
                            _filePath,
                            _backupFilePath,
                            async stream => await JsonSerializer.SerializeAsync(
                                stream,
                                snapshot,
                                JsonOptions,
                                cancellationToken)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_syncRoot)
                {
                    _dirty = true;
                    if (!_disposed)
                    {
                        _flushTimer.Change(FlushDelayMilliseconds, Timeout.Infinite);
                    }
                }

                throw;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // 終了時の最近使った項目の保存失敗でアプリ終了を妨げません。
        }

        _flushTimer.Dispose();
        _flushLock.Dispose();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var loadedItems = TryLoad(_filePath) ?? TryLoad(_backupFilePath) ?? [];
        foreach (var item in loadedItems)
        {
            if (TryNormalize(item, out var normalized))
            {
                _items[normalized.Key] = normalized;
            }
        }

        TrimUnpinnedItems();
    }

    private static IReadOnlyList<RecentItem>? TryLoad(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<List<RecentItem>>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void ScheduleFlush()
    {
        _dirty = true;
        _flushTimer.Change(FlushDelayMilliseconds, Timeout.Infinite);
    }

    private void OnFlushTimer()
    {
        _ = FlushSafeAsync();
    }

    private async Task FlushSafeAsync()
    {
        try
        {
            await FlushAsync();
        }
        catch
        {
            // 次の操作または終了時flushで再試行します。
        }
    }

    private void TrimUnpinnedItems()
    {
        var overflow = _items.Values
            .Where(static item => !item.IsPinned)
            .OrderByDescending(static item => item.LastUsedAt)
            .Skip(MaximumUnpinnedItems)
            .Select(static item => item.Key)
            .ToList();
        foreach (var key in overflow)
        {
            _items.Remove(key);
        }
    }

    private static bool TryNormalize(RecentItem item, out RecentItem normalized)
    {
        normalized = item;
        var key = item.Key.Trim();
        var title = item.Title.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var canUseCachedImages = item.Category is not (RecentItemCategory.Web or RecentItemCategory.Video)
            || Path.IsPathFullyQualified(item.Target) || item.ThumbnailCacheVersion == 1;
        normalized = item with
        {
            Key = key,
            Title = title,
            Target = item.Target.Trim(),
            // 旧Webキャッシュは取得元を証明できない。履歴自体や明示保存先は削除しない。
            ThumbnailPngBase64 = canUseCachedImages
                && (item.ThumbnailPngBase64 ?? string.Empty).Length <= MaximumThumbnailTextLength
                ? item.ThumbnailPngBase64 ?? string.Empty
                : string.Empty,
            FaviconPngBase64 = canUseCachedImages && (item.FaviconPngBase64 ?? string.Empty).Length <= MaximumFaviconTextLength
                ? item.FaviconPngBase64 ?? string.Empty
                : string.Empty,
            WorkspaceName = item.WorkspaceName.Trim(),
            LastUsedAt = item.LastUsedAt == default ? DateTimeOffset.Now : item.LastUsedAt
        };
        return true;
    }

    private static bool HasSameContent(RecentItem left, RecentItem right)
    {
        return HasSamePreviewContext(left, right)
            && left.ThumbnailCacheVersion == right.ThumbnailCacheVersion
            && left.Category == right.Category
            && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && string.Equals(left.Target, right.Target, StringComparison.Ordinal)
            && string.Equals(left.PreviewText, right.PreviewText, StringComparison.Ordinal)
            && string.Equals(left.ThumbnailPngBase64, right.ThumbnailPngBase64, StringComparison.Ordinal)
            && string.Equals(left.FaviconPngBase64, right.FaviconPngBase64, StringComparison.Ordinal)
            && left.WorkspaceId == right.WorkspaceId
            && left.WorkspaceItemId == right.WorkspaceItemId
            && left.ParentFolderId == right.ParentFolderId
            && string.Equals(left.WorkspaceName, right.WorkspaceName, StringComparison.Ordinal)
            && left.WorkspaceItemType == right.WorkspaceItemType;
    }

    private static bool HasSamePreviewContext(RecentItem left, RecentItem right)
        => string.Equals(left.Target, right.Target, StringComparison.Ordinal)
            && string.Equals(left.ContainerProfileId, right.ContainerProfileId, StringComparison.OrdinalIgnoreCase)
            && left.WorkspaceId == right.WorkspaceId && left.WorkspaceItemId == right.WorkspaceItemId;

    private static bool IsStandaloneWebItemKey(string key)
    {
        return key.StartsWith("web:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase);
    }
}

public static class RecentItemFactory
{
    public static RecentItem FromWorkspaceItem(
        WorkspaceItem item,
        string workspaceName,
        DateTimeOffset? usedAt = null)
    {
        var target = item switch
        {
            ShellItem shellItem => shellItem.TargetPath,
            WorkspaceBookmarkItem bookmark => bookmark.Url,
            YouTubeMiniPlayerSetItem playerSet => playerSet.Players.FirstOrDefault()?.Url ?? string.Empty,
            _ => item.Url
        };
        var category = item switch
        {
            NoteItem => RecentItemCategory.Note,
            ShellItem shellItem when WorkspaceShellItemService.IsVideoPath(shellItem.TargetPath) => RecentItemCategory.Video,
            ShellItem { Kind: not ShellItemKind.Directory } => RecentItemCategory.File,
            WorkspaceBookmarkItem bookmark when YouTubeUrlService.TryGetVideoId(bookmark.Url, out _) => RecentItemCategory.Video,
            WorkspaceBookmarkItem => RecentItemCategory.Web,
            YouTubeMiniPlayerSetItem => RecentItemCategory.Video,
            _ => RecentItemCategory.Workspace
        };

        return new RecentItem
        {
            Key = $"workspace:{item.Id:D}",
            Category = category,
            Title = GetTitle(item),
            Target = target,
            PreviewText = item is NoteItem note ? note.Content : item.Note,
            ThumbnailPngBase64 = item.ThumbnailPngBase64,
            ThumbnailCacheVersion = 1,
            FaviconPngBase64 = item.FaviconPngBase64,
            WorkspaceId = item.WorkspaceId,
            WorkspaceItemId = item.Id,
            ContainerProfileId = item.ContainerProfileId,
            ParentFolderId = item.ParentFolderId,
            WorkspaceName = workspaceName,
            WorkspaceItemType = item.Type,
            LastUsedAt = usedAt ?? DateTimeOffset.Now
        };
    }

    public static RecentItem FromWeb(
        string url,
        string? title = null,
        string? thumbnailPngBase64 = null,
        DateTimeOffset? usedAt = null,
        string? faviconPngBase64 = null,
        string? pageDescription = null,
        string? containerProfileId = null)
    {
        var normalizedUrl = url.Trim();
        var isVideo = YouTubeUrlService.TryGetVideoId(normalizedUrl, out var videoId);
        return new RecentItem
        {
            Key = isVideo ? $"youtube:{videoId}" : $"web:{normalizedUrl}",
            Category = isVideo ? RecentItemCategory.Video : RecentItemCategory.Web,
            Title = string.IsNullOrWhiteSpace(title) ? normalizedUrl : title.Trim(),
            Target = normalizedUrl,
            PreviewText = WebThumbnailCaptureService.NormalizePageDescription(pageDescription),
            ThumbnailPngBase64 = thumbnailPngBase64 ?? string.Empty,
            ContainerProfileId = containerProfileId,
            ThumbnailCacheVersion = string.IsNullOrWhiteSpace(containerProfileId) ? 0 : 1,
            FaviconPngBase64 = faviconPngBase64 ?? string.Empty,
            LastUsedAt = usedAt ?? DateTimeOffset.Now
        };
    }

    public static RecentItem FromFile(string path, DateTimeOffset? usedAt = null)
    {
        var normalizedPath = WorkspaceShellItemService.NormalizePath(path);
        return new RecentItem
        {
            Key = $"file:{normalizedPath}",
            Category = WorkspaceShellItemService.IsVideoPath(normalizedPath)
                ? RecentItemCategory.Video
                : RecentItemCategory.File,
            Title = WorkspaceShellItemService.GetDisplayName(normalizedPath),
            Target = normalizedPath,
            LastUsedAt = usedAt ?? DateTimeOffset.Now
        };
    }

    public static RecentItem FromFolder(string path, DateTimeOffset? usedAt = null)
    {
        var normalizedPath = WorkspaceShellItemService.NormalizePath(path);
        return new RecentItem
        {
            Key = $"file:{normalizedPath}",
            Category = RecentItemCategory.Folder,
            Title = WorkspaceShellItemService.GetDisplayName(normalizedPath),
            Target = normalizedPath,
            LastUsedAt = usedAt ?? DateTimeOffset.Now
        };
    }

    private static string GetTitle(WorkspaceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title.Trim();
        }

        return item switch
        {
            NoteItem note when !string.IsNullOrWhiteSpace(note.Content) => note.Content.Trim().Split('\n')[0].Trim(),
            ShellItem shellItem => WorkspaceShellItemService.GetDisplayName(shellItem.TargetPath),
            _ => item.Type.ToString()
        };
    }
}
