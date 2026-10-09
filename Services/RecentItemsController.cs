using System.IO;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using WorkspaceBookmarkItem = TabBrowser.Models.Workspaces.BookmarkItem;

namespace TabBrowser.Services;

internal interface IRecentItemsHost
{
    Task EditWorkspaceNoteAsync(NoteItem note);

    void OpenWorkspaceFolder(FolderItem folder);

    Task<int> OpenWorkspaceSessionAsync(SessionGroupItem session);

    Task OpenWorkspaceMediaSetAsync(YouTubeMiniPlayerSetItem playerSet);

    bool OpenLocalVideoMiniPlayer(string path);

    void OpenShellFolderInTab(string path);

    void OpenShellPath(string path);

    void OpenWorkspaceShellItem(ShellItem item);

    Task OpenWorkspaceUrlAsync(string url);

    void SetStatus(string message);
}

internal interface IRecentItemsWorkspaceStore
{
    Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId);

    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync();
}

internal sealed class RecentItemsWorkspaceStore(WorkspaceService service) : IRecentItemsWorkspaceStore
{
    public Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(Guid workspaceId)
        => service.GetWorkspaceItemsAsync(workspaceId);

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync()
        => service.GetWorkspacesAsync();
}

public sealed class RecentItemsController
{
    private readonly IRecentItemsHost _host;
    private readonly RecentItemsService _recentItems;
    private readonly bool _isPrivateWindow;
    private readonly Func<IRecentItemsWorkspaceStore> _createWorkspaceStore;

    internal RecentItemsController(
        IRecentItemsHost host,
        RecentItemsService recentItems,
        bool isPrivateWindow,
        Func<IRecentItemsWorkspaceStore>? createWorkspaceStore = null)
    {
        _host = host;
        _recentItems = recentItems;
        _isPrivateWindow = isPrivateWindow;
        _createWorkspaceStore = createWorkspaceStore
            ?? (() => new RecentItemsWorkspaceStore(new WorkspaceService()));
    }

    public async Task OpenAsync(RecentItem recentItem)
    {
        try
        {
            if (recentItem.WorkspaceId is { } workspaceId
                && recentItem.WorkspaceItemId is { } workspaceItemId)
            {
                var store = _createWorkspaceStore();
                var workspaceItems = await store.GetWorkspaceItemsAsync(workspaceId);
                var workspaceItem = workspaceItems.FirstOrDefault(item => item.Id == workspaceItemId);
                if (workspaceItem is null)
                {
                    _host.SetStatus("最近使ったWorkspace項目が見つかりません");
                    return;
                }

                var opened = await OpenWorkspaceItemAsync(workspaceItem);
                if (opened && !_isPrivateWindow)
                {
                    var workspaceName = (await store.GetWorkspacesAsync())
                        .FirstOrDefault(workspace => workspace.Id == workspaceId)?.Name ?? recentItem.WorkspaceName;
                    _recentItems.Record(RecentItemFactory.FromWorkspaceItem(workspaceItem, workspaceName));
                }

                return;
            }

            if (Path.IsPathFullyQualified(recentItem.Target))
            {
                if (WorkspaceShellItemService.IsVideoPath(recentItem.Target))
                {
                    if (!_host.OpenLocalVideoMiniPlayer(recentItem.Target))
                    {
                        return;
                    }
                }
                else if (Directory.Exists(recentItem.Target))
                {
                    _host.OpenShellFolderInTab(recentItem.Target);
                }
                else
                {
                    _host.OpenShellPath(recentItem.Target);
                }

                Record(recentItem with { LastUsedAt = DateTimeOffset.Now });
                return;
            }

            if (Uri.TryCreate(recentItem.Target, UriKind.Absolute, out _))
            {
                await _host.OpenWorkspaceUrlAsync(recentItem.Target);
                Record(recentItem with { LastUsedAt = DateTimeOffset.Now });
            }
        }
        catch (Exception ex)
        {
            _host.SetStatus("最近使った項目を開けません: " + ex.Message);
        }
    }

    public void Record(RecentItem item)
    {
        if (!_isPrivateWindow)
        {
            _recentItems.Record(item);
        }
    }

    public void RecordTabSelection(BrowserTab tab)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        if (tab.IsFolderTab && !string.IsNullOrWhiteSpace(tab.Url))
        {
            _recentItems.Record(RecentItemFactory.FromFolder(tab.Url));
            return;
        }

        if (!tab.IsNativeContentTab
            && !IsBlankUrl(tab.Url))
        {
            _recentItems.Record(RecentItemFactory.FromWeb(
                tab.Url!,
                tab.DisplayTitle,
                CapturedTabSnapshot.CreateThumbnailPngBase64(tab.Thumbnail),
                faviconPngBase64: tab.FaviconPngBase64,
                pageDescription: tab.PageDescription,
                containerProfileId: tab.ContainerProfileId));
        }
    }

    public void UpdateTabThumbnail(BrowserTab tab)
    {
        if (_isPrivateWindow
            || tab.IsNativeContentTab
            || IsBlankUrl(tab.Url)
            || tab.Thumbnail is null)
        {
            return;
        }

        var thumbnailPngBase64 = CapturedTabSnapshot.CreateThumbnailPngBase64(tab.Thumbnail);
        if (string.IsNullOrWhiteSpace(thumbnailPngBase64))
        {
            return;
        }

        var recentItem = RecentItemFactory.FromWeb(tab.Url!, tab.DisplayTitle, containerProfileId: tab.ContainerProfileId);
        _recentItems.UpdateWebPreview(
            recentItem,
            thumbnailPngBase64,
            tab.FaviconPngBase64,
            tab.PageDescription);
    }

    private async Task<bool> OpenWorkspaceItemAsync(WorkspaceItem item)
    {
        if (WorkspaceTabSavePlanner.CreateSavedSingleTab(item) is { } savedTab)
        {
            return await _host.OpenWorkspaceSessionAsync(new SessionGroupItem { Tabs = [savedTab] }) > 0;
        }

        switch (item)
        {
            case NoteItem note:
                await _host.EditWorkspaceNoteAsync(note);
                return true;
            case FolderItem folder:
                _host.OpenWorkspaceFolder(folder);
                return true;
            case SessionGroupItem session:
                return await _host.OpenWorkspaceSessionAsync(session) > 0;
            case YouTubeMiniPlayerSetItem playerSet:
                await _host.OpenWorkspaceMediaSetAsync(playerSet);
                return true;
            case ShellItem shellItem when WorkspaceShellItemService.IsVideoPath(shellItem.TargetPath):
                return _host.OpenLocalVideoMiniPlayer(shellItem.TargetPath);
            case ShellItem { Kind: ShellItemKind.Directory, OpenInFolderTab: true } shellDirectory:
                _host.OpenShellFolderInTab(shellDirectory.TargetPath);
                return true;
            case ShellItem shellItem:
                _host.OpenWorkspaceShellItem(shellItem);
                return true;
            case WorkspaceBookmarkItem bookmark when !string.IsNullOrWhiteSpace(bookmark.Url):
                await _host.OpenWorkspaceUrlAsync(bookmark.Url);
                return true;
            default:
                return false;
        }
    }

    private static bool IsBlankUrl(string? url)
        => string.IsNullOrWhiteSpace(url)
            || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
}
