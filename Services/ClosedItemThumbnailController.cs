using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal interface IClosedItemThumbnailHost
{
    Func<string, string?, CancellationToken, Task<ImageSource?>>? GetCaptureAction(BrowserTab tab);
    void SetThumbnail(BrowserTab tab, ImageSource image);
    ImageSource RenderFolder(string title, string folderPath, IReadOnlyList<FolderTabThumbnailEntry> entries,
        int itemCount, bool isTruncated, string? statusText = null);
}

// 閉じたtab／Folderの取得、実行中要求の共有、結果適用と失敗時描画を所有する。
// 呼出元と同じUI threadで辞書を扱い、既存Window token以外の寿命を作らない。
internal sealed class ClosedItemThumbnailController
{
    private readonly IClosedItemThumbnailHost _host;
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<Guid, Task<ImageSource?>> _closedTabThumbnailCaptureTasks = [];
    private readonly Dictionary<string, Task<ImageSource>> _folderTabThumbnailCaptureTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int, CancellationToken, Task<FolderTabListing>> _enumerateAsync;
    private readonly Func<Func<string, CancellationToken, Task<FolderTabVisual>>> _createIconReader;

    internal ClosedItemThumbnailController(
        IClosedItemThumbnailHost host,
        CancellationToken lifetimeToken,
        Func<string, int, CancellationToken, Task<FolderTabListing>>? enumerateAsync = null,
        Func<Func<string, CancellationToken, Task<FolderTabVisual>>>? createIconReader = null)
    {
        _host = host;
        _lifetimeToken = lifetimeToken;
        _enumerateAsync = enumerateAsync ?? ((path, maximum, token) => new FolderTabService().EnumerateAsync(path, maximum, token));
        _createIconReader = createIconReader ?? (() => new FolderTabIconService().GetVisualAsync);
    }

    public async Task CaptureForHistoryAsync(
        ClosedTabState closedTab,
        BrowserTab runtimeTab)
    {
        closedTab.RuntimeThumbnail = await GetOrCaptureClosedTabThumbnailAsync(runtimeTab);
    }

    public async Task<ImageSource?> GetOrCaptureClosedTabThumbnailAsync(BrowserTab runtimeTab)
    {
        if (runtimeTab.Thumbnail is { } existingThumbnail)
        {
            return existingThumbnail;
        }

        if (!_closedTabThumbnailCaptureTasks.TryGetValue(runtimeTab.Id, out var captureTask))
        {
            captureTask = CaptureClosedTabThumbnailAsync(runtimeTab);
            _closedTabThumbnailCaptureTasks[runtimeTab.Id] = captureTask;
        }

        try
        {
            return await captureTask;
        }
        finally
        {
            if (_closedTabThumbnailCaptureTasks.TryGetValue(runtimeTab.Id, out var currentTask)
                && ReferenceEquals(currentTask, captureTask))
            {
                _closedTabThumbnailCaptureTasks.Remove(runtimeTab.Id);
            }
        }
    }

    private async Task<ImageSource?> CaptureClosedTabThumbnailAsync(BrowserTab runtimeTab)
    {
        try
        {
            if (_host.GetCaptureAction(runtimeTab) is not { } capture)
            {
                return null;
            }

            var image = await capture(
                runtimeTab.DisplayTitle,
                runtimeTab.Url,
                _lifetimeToken);
            if (image is null)
            {
                return null;
            }

            _host.SetThumbnail(runtimeTab, image);
            return image;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ImageSource> GetOrCreateFolderTabThumbnailAsync(
        ClosedTabState closedTab,
        string title)
    {
        if (closedTab.RuntimeThumbnail is { } existingThumbnail)
        {
            return existingThumbnail;
        }

        var folderPath = closedTab.Url?.Trim() ?? string.Empty;
        if (!_folderTabThumbnailCaptureTasks.TryGetValue(folderPath, out var captureTask))
        {
            captureTask = CreateFolderTabThumbnailAsync(folderPath, title);
            _folderTabThumbnailCaptureTasks[folderPath] = captureTask;
        }

        try
        {
            var thumbnail = await captureTask;
            closedTab.RuntimeThumbnail = thumbnail;
            return thumbnail;
        }
        finally
        {
            if (_folderTabThumbnailCaptureTasks.TryGetValue(folderPath, out var currentTask)
                && ReferenceEquals(currentTask, captureTask))
            {
                _folderTabThumbnailCaptureTasks.Remove(folderPath);
            }
        }
    }

    private async Task<ImageSource> CreateFolderTabThumbnailAsync(string folderPath, string title)
    {
        try
        {
            var cancellationToken = _lifetimeToken;
            var listing = await _enumerateAsync(
                folderPath,
                8,
                cancellationToken);
            var getVisualAsync = _createIconReader();
            var entries = await Task.WhenAll(listing.Entries.Select(async entry =>
            {
                var visual = await getVisualAsync(entry.FullPath, cancellationToken);
                return new FolderTabThumbnailEntry(entry.Name, entry.IsDirectory, visual.Image);
            }));
            return _host.RenderFolder(
                title,
                listing.FolderPath,
                entries,
                entries.Length,
                listing.IsTruncated);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return _host.RenderFolder(title, folderPath, [], 0, isTruncated: false);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException)
        {
            return _host.RenderFolder(
                title,
                folderPath,
                [],
                0,
                isTruncated: false,
                statusText: "内容を取得できません");
        }
        catch
        {
            return _host.RenderFolder(
                title,
                folderPath,
                [],
                0,
                isTruncated: false,
                statusText: "内容を取得できません");
        }
    }
}
