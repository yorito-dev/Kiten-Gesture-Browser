using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal readonly record struct RecentItemThumbnailCandidate(
    BrowserTab Tab, bool IsPrivateWindow, bool IsClosingWindow);

/// <summary>Window単位で最近使った項目の画像取得と実行中要求の集約を所有する。</summary>
internal sealed class RecentItemThumbnailController
{
    private readonly bool _isPrivateWindow;
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<IEnumerable<RecentItemThumbnailCandidate>> _getOpenTabs;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<WorkspaceItem>>> _getWorkspaceItemsAsync;
    private readonly Func<string, int, int, CancellationToken, Task<BitmapSource?>> _getMediaThumbnailAsync;
    private readonly Dictionary<ThumbnailRequest, Task<string>> _recentItemThumbnailFetchTasks = new();

    public RecentItemThumbnailController(
        bool isPrivateWindow,
        CancellationToken lifetimeToken,
        Func<IEnumerable<RecentItemThumbnailCandidate>> getOpenTabs,
        Func<Guid, CancellationToken, Task<IReadOnlyList<WorkspaceItem>>>? getWorkspaceItemsAsync = null,
        Func<string, int, int, CancellationToken, Task<BitmapSource?>>? getMediaThumbnailAsync = null)
    {
        _isPrivateWindow = isPrivateWindow;
        _lifetimeToken = lifetimeToken;
        _getOpenTabs = getOpenTabs;
        _getWorkspaceItemsAsync = getWorkspaceItemsAsync
            ?? ((workspaceId, token) => new WorkspaceService().GetWorkspaceItemsAsync(workspaceId, token));
        _getMediaThumbnailAsync = getMediaThumbnailAsync
            ?? WorkspaceShellItemService.GetMediaThumbnailImageAsync;
    }

    // 呼出元と同じUI threadで登録・除去する。新しいworkerや寿命は作らない。
    public Task<string> FetchRecentItemThumbnailAsync(RecentItem recentItem)
    {
        if (_isPrivateWindow || _lifetimeToken.IsCancellationRequested
            || string.IsNullOrWhiteSpace(recentItem.Key)
            || !string.IsNullOrWhiteSpace(recentItem.ThumbnailPngBase64)
            || recentItem.IsNote)
        {
            return Task.FromResult(string.Empty);
        }

        var request = ThumbnailRequest.From(recentItem);
        if (_recentItemThumbnailFetchTasks.TryGetValue(request, out var pendingTask))
        {
            return pendingTask;
        }

        var fetchTask = FetchRecentItemThumbnailCoreAsync(recentItem, request);
        _recentItemThumbnailFetchTasks[request] = fetchTask;
        _ = RemoveRecentItemThumbnailFetchAsync(request, fetchTask);
        return fetchTask;
    }

    private async Task<string> FetchRecentItemThumbnailCoreAsync(RecentItem recentItem, ThumbnailRequest request)
    {
        try
        {
            if (recentItem.WorkspaceId is { } workspaceId
                && recentItem.WorkspaceItemId is { } workspaceItemId)
            {
                var workspaceItems = await _getWorkspaceItemsAsync(workspaceId, _lifetimeToken);
                var storedThumbnail = workspaceItems
                    .FirstOrDefault(item => item.Id == workspaceItemId)?
                    .ThumbnailPngBase64;
                if (!string.IsNullOrWhiteSpace(storedThumbnail))
                {
                    return IsCurrent(recentItem, request) ? storedThumbnail : string.Empty;
                }
            }

            if (Path.IsPathFullyQualified(recentItem.Target)
                && File.Exists(recentItem.Target)
                && WorkspaceShellItemService.IsMediaPath(recentItem.Target))
            {
                var localThumbnail = await _getMediaThumbnailAsync(
                    recentItem.Target,
                    WebThumbnailCaptureService.ThumbnailWidth,
                    WebThumbnailCaptureService.ThumbnailHeight,
                    _lifetimeToken);
                return IsCurrent(recentItem, request)
                    ? CapturedTabSnapshot.CreateThumbnailPngBase64(localThumbnail) : string.Empty;
            }

            var normalizedUrl = UrlThumbnailCaptureCoordinator.NormalizeUrl(recentItem.Target);
            if (normalizedUrl is null || !IsCurrent(recentItem, request)
                || string.IsNullOrWhiteSpace(request.ContainerProfileId))
            {
                return string.Empty;
            }

            // URLだけの共有キャッシュ／隠しWebViewはコンテナの由来を保証できない。
            // 対応する通常タブがなければ既存の画像なし表示を使い、通信は開始しない。
            var thumbnail = FindRecentItemOpenTabThumbnail(normalizedUrl, request.ContainerProfileId, _getOpenTabs());
            return CapturedTabSnapshot.CreateThumbnailPngBase64(thumbnail);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static ImageSource? FindRecentItemOpenTabThumbnail(
        string normalizedUrl, string containerProfileId, IEnumerable<RecentItemThumbnailCandidate> candidates)
        => candidates.FirstOrDefault(candidate =>
            !candidate.IsPrivateWindow && !candidate.IsClosingWindow
            && !candidate.Tab.IsNativeContentTab && candidate.Tab.Thumbnail is not null
            && string.Equals(candidate.Tab.ContainerProfileId, containerProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(UrlThumbnailCaptureCoordinator.NormalizeUrl(candidate.Tab.Url), normalizedUrl, StringComparison.Ordinal))
            .Tab?.Thumbnail;

    private bool IsCurrent(RecentItem item, ThumbnailRequest request)
        => !_lifetimeToken.IsCancellationRequested && ThumbnailRequest.From(item) == request;

    private readonly record struct ThumbnailRequest(
        string Key, string Target, string? ContainerProfileId, Guid? WorkspaceId, Guid? WorkspaceItemId)
    {
        public static ThumbnailRequest From(RecentItem item)
            => new(item.Key, item.Target, item.ContainerProfileId, item.WorkspaceId, item.WorkspaceItemId);
    }

    private async Task RemoveRecentItemThumbnailFetchAsync(ThumbnailRequest key, Task<string> fetchTask)
    {
        try
        {
            await fetchTask;
        }
        finally
        {
            if (_recentItemThumbnailFetchTasks.TryGetValue(key, out var currentTask)
                && ReferenceEquals(currentTask, fetchTask))
            {
                _recentItemThumbnailFetchTasks.Remove(key);
            }
        }
    }
}
