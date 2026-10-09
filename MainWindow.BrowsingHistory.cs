using System.Windows;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow
{
    private async void BrowsingHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(BrowserCommandProvider.OpenBrowsingHistoryActionId);
    }

    private async Task OpenBrowsingHistoryWindowAsync()
    {
        if (ModelessWindowService.TryGetOwned<BrowsingHistoryWindow>(this, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var workspaces = await _browsingHistoryController.LoadWorkspacesAsync();

        if (_isClosingWindow)
        {
            return;
        }

        var window = new BrowsingHistoryWindow(
            App.BrowsingHistory,
            ReloadContainerProfiles(applyToTabs: false),
            workspaces,
            OpenBrowsingHistoryEntryAsync,
            SaveBrowsingHistoryEntriesToWorkspaceAsync)
        {
            Owner = this
        };
        ModelessWindowService.Show(window);
    }

    private async Task OpenBrowsingHistoryEntryAsync(
        BrowsingHistoryEntry entry,
        BrowsingHistoryOpenMode mode)
        => await _browsingHistoryController.OpenEntryAsync(entry, mode);

    private async Task SaveBrowsingHistoryEntriesToWorkspaceAsync(
        Guid workspaceId,
        IReadOnlyList<BrowsingHistoryEntry> entries)
        => await _browsingHistoryController.SaveEntriesToWorkspaceAsync(workspaceId, entries);

    private void ReloadVisibleWorkspaceViews()
    {
        foreach (var tab in _tabs)
        {
            if (GetTabHostedContent(tab) is Views.WorkspaceOSView workspaceView)
            {
                workspaceView.ReloadWorkspace();
            }
        }

        _workspaceOSWindow?.ReloadWorkspace();
    }

    System.Windows.Threading.Dispatcher IBrowsingHistoryHost.UiDispatcher => Dispatcher;

    BrowserTab? IBrowsingHistoryHost.GetActiveBrowserCommandTarget()
        => GetActiveBrowserCommandTarget();

    void IBrowsingHistoryHost.ActivateOwner() => Activate();

    Task IBrowsingHistoryHost.NavigateTabToUrlAsync(BrowserTab tab, string url, string title)
        => NavigateTabToUrlAsync(tab, url, title);

    BrowserTab IBrowsingHistoryHost.OpenTab(
        string url,
        bool activate,
        string title,
        bool queueBackgroundLoading,
        string containerProfileId)
        => OpenTab(
            url,
            activate,
            title,
            queueBackgroundLoading: queueBackgroundLoading,
            containerProfileId: containerProfileId);

    void IBrowsingHistoryHost.OpenBackgroundTabNextTo(
        BrowserTab? sourceTab,
        string url,
        string title,
        string containerProfileId)
        => OpenBackgroundTabNextTo(
            sourceTab,
            url,
            title,
            containerProfileId: containerProfileId);

    Task IBrowsingHistoryHost.StartSplitViewWithRightTabAsync(
        BrowserTab rightTab,
        BrowserTab? leftTab)
        => StartSplitViewWithRightTabAsync(rightTab, leftTab);

    void IBrowsingHistoryHost.OpenInNewWindow(BrowsingHistoryEntry entry)
    {
        var state = new SessionTabState
        {
            Id = Guid.NewGuid(),
            Url = entry.Url,
            Title = entry.Title,
            ContainerProfileId = entry.ContainerProfileId
        };
        var position = new Point(Math.Max(0, Left + 32), Math.Max(0, Top + 32));
        var newWindow = new MainWindow(state, position, _isPrivateWindow);
        newWindow.Show();
        newWindow.Activate();
    }

    void IBrowsingHistoryHost.ReloadVisibleWorkspaceViews()
        => ReloadVisibleWorkspaceViews();

    void IBrowsingHistoryHost.SetStatus(string message)
        => SetStatus(message, autoClear: true);
}
