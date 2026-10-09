using System.Diagnostics;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow
{
    private readonly RecentItemThumbnailController _recentItemThumbnailController;

    private async Task EditWorkspaceNoteAsync(Guid workspaceId, Guid noteId, Window? owner)
        => await _workspaceNoteEditController.EditAsync(workspaceId, noteId, owner);

    private async Task OpenRecentItemAsync(RecentItem recentItem)
        => await _recentItemsController.OpenAsync(recentItem);

    private void RecordRecentItem(RecentItem item)
        => _recentItemsController.Record(item);

    private void RecordRecentTabSelection(BrowserTab tab)
        => _recentItemsController.RecordTabSelection(tab);

    private void UpdateRecentTabThumbnail(BrowserTab tab)
        => _recentItemsController.UpdateTabThumbnail(tab);

    private Task<string> FetchRecentItemThumbnailAsync(RecentItem recentItem)
        => _recentItemThumbnailController.FetchRecentItemThumbnailAsync(recentItem);

    private IEnumerable<RecentItemThumbnailCandidate> GetRecentItemOpenTabs()
        => GetOpenMainWindows()
            .SelectMany(window => window._tabs.Select(tab =>
                new RecentItemThumbnailCandidate(tab, window._isPrivateWindow, window._isClosingWindow)));

    Task IRecentItemsHost.EditWorkspaceNoteAsync(NoteItem note)
        => EditWorkspaceNoteAsync(note.WorkspaceId, note.Id, this);

    void IRecentItemsHost.OpenWorkspaceFolder(FolderItem folder)
        => OpenWorkspaceFolderWindow(folder, selectedItemId: null);

    Task<int> IRecentItemsHost.OpenWorkspaceSessionAsync(SessionGroupItem session)
        => OpenWorkspaceSessionTabsAsync(session.Tabs);

    Task IRecentItemsHost.OpenWorkspaceMediaSetAsync(YouTubeMiniPlayerSetItem playerSet)
        => OpenWorkspaceMediaSetAsync(playerSet);

    bool IRecentItemsHost.OpenLocalVideoMiniPlayer(string path)
        => OpenLocalVideoMiniPlayer(path);

    void IRecentItemsHost.OpenShellFolderInTab(string path)
        => OpenShellFolderInTab(path);

    void IRecentItemsHost.OpenShellPath(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    void IRecentItemsHost.OpenWorkspaceShellItem(ShellItem item)
        => new WorkspaceShellItemService().Open(item);

    Task IRecentItemsHost.OpenWorkspaceUrlAsync(string url)
        => OpenWorkspaceUrlAsync(url);

    void IRecentItemsHost.SetStatus(string message)
        => SetStatus(message, autoClear: true);

    System.Windows.Threading.Dispatcher IWorkspaceNoteEditHost.UiDispatcher => Dispatcher;

    Window IWorkspaceNoteEditHost.DefaultOwner => this;

    bool IWorkspaceNoteEditHost.IsPrivateWindow => _isPrivateWindow;

    string IWorkspaceNoteEditHost.NoteDataScope => _privateBrowsingContext is { } privateContext
        ? $"{AppPaths.WorkspaceDatabaseFile}|private:{privateContext.Id:N}"
        : AppPaths.WorkspaceDatabaseFile;

    bool IWorkspaceNoteEditHost.IsClosing => _isClosingWindow;

    CancellationToken IWorkspaceNoteEditHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;

    void IWorkspaceNoteEditHost.RecordRecentItem(RecentItem item)
        => _recentItemsController.Record(item);

    void IWorkspaceNoteEditHost.SetStatus(string message)
        => SetStatus(message, autoClear: true);
}
