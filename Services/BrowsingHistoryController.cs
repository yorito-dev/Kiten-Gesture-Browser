using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal interface IBrowsingHistoryHost
{
    Dispatcher UiDispatcher { get; }

    BrowserTab? GetActiveBrowserCommandTarget();

    void ActivateOwner();

    Task NavigateTabToUrlAsync(BrowserTab tab, string url, string title);

    BrowserTab OpenTab(
        string url,
        bool activate,
        string title,
        bool queueBackgroundLoading,
        string containerProfileId);

    void OpenBackgroundTabNextTo(
        BrowserTab? sourceTab,
        string url,
        string title,
        string containerProfileId);

    Task StartSplitViewWithRightTabAsync(BrowserTab rightTab, BrowserTab? leftTab);

    void OpenInNewWindow(BrowsingHistoryEntry entry);

    void ReloadVisibleWorkspaceViews();

    void SetStatus(string message);
}

internal interface IBrowsingHistoryWorkspaceStore
{
    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken);

    Task AddBookmarkAsync(
        Guid workspaceId,
        string title,
        string url,
        double x,
        double y,
        CancellationToken cancellationToken);
}

internal sealed class BrowsingHistoryWorkspaceStore(WorkspaceService service) : IBrowsingHistoryWorkspaceStore
{
    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken)
        => service.GetWorkspacesAsync(cancellationToken);

    public Task<IReadOnlyList<WorkspaceItem>> GetWorkspaceItemsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken)
        => service.GetWorkspaceItemsAsync(workspaceId, cancellationToken);

    public Task AddBookmarkAsync(
        Guid workspaceId,
        string title,
        string url,
        double x,
        double y,
        CancellationToken cancellationToken)
        => service.AddBookmarkAsync(workspaceId, title, url, x, y, cancellationToken: cancellationToken);
}

public sealed class BrowsingHistoryController
{
    private readonly IBrowsingHistoryHost _host;
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<IBrowsingHistoryWorkspaceStore> _createWorkspaceStore;

    internal BrowsingHistoryController(
        IBrowsingHistoryHost host,
        CancellationToken lifetimeToken,
        Func<IBrowsingHistoryWorkspaceStore>? createWorkspaceStore = null)
    {
        _host = host;
        _lifetimeToken = lifetimeToken;
        _createWorkspaceStore = createWorkspaceStore
            ?? (() => new BrowsingHistoryWorkspaceStore(new WorkspaceService()));
    }

    public async Task<IReadOnlyList<Workspace>> LoadWorkspacesAsync()
    {
        try
        {
            return await _createWorkspaceStore().GetWorkspacesAsync(_lifetimeToken);
        }
        catch (Exception ex)
        {
            _host.SetStatus("Workspace一覧を読み込めません: " + ex.Message);
            return [];
        }
    }

    public async Task OpenEntryAsync(
        BrowsingHistoryEntry entry,
        BrowsingHistoryOpenMode mode)
    {
        if (!_host.UiDispatcher.CheckAccess())
        {
            await _host.UiDispatcher.InvokeAsync(() => OpenEntryAsync(entry, mode)).Task.Unwrap();
            return;
        }

        _host.ActivateOwner();
        switch (mode)
        {
            case BrowsingHistoryOpenMode.CurrentTab:
                if (_host.GetActiveBrowserCommandTarget() is { IsNativeContentTab: false } current)
                {
                    await _host.NavigateTabToUrlAsync(current, entry.Url, entry.Title);
                }
                else
                {
                    _host.OpenTab(
                        entry.Url,
                        activate: true,
                        entry.Title,
                        queueBackgroundLoading: true,
                        entry.ContainerProfileId);
                }

                break;

            case BrowsingHistoryOpenMode.NewTab:
                _host.OpenTab(
                    entry.Url,
                    activate: true,
                    entry.Title,
                    queueBackgroundLoading: true,
                    entry.ContainerProfileId);
                break;

            case BrowsingHistoryOpenMode.BackgroundTab:
                _host.OpenBackgroundTabNextTo(
                    _host.GetActiveBrowserCommandTarget(),
                    entry.Url,
                    entry.Title,
                    entry.ContainerProfileId);
                break;

            case BrowsingHistoryOpenMode.Split:
                var leftTab = _host.GetActiveBrowserCommandTarget();
                var rightTab = _host.OpenTab(
                    entry.Url,
                    activate: false,
                    entry.Title,
                    queueBackgroundLoading: false,
                    entry.ContainerProfileId);
                await _host.StartSplitViewWithRightTabAsync(rightTab, leftTab);
                break;

            case BrowsingHistoryOpenMode.NewWindow:
                _host.OpenInNewWindow(entry);
                break;
        }
    }

    public async Task SaveEntriesToWorkspaceAsync(
        Guid workspaceId,
        IReadOnlyList<BrowsingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var workspaceStore = _createWorkspaceStore();
        var existingItems = await workspaceStore.GetWorkspaceItemsAsync(workspaceId, _lifetimeToken);
        var startIndex = existingItems.Count;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var positionIndex = startIndex + index;
            await workspaceStore.AddBookmarkAsync(
                workspaceId,
                entry.Title,
                entry.Url,
                32 + positionIndex % 6 * 176,
                32 + positionIndex / 6 * 112,
                _lifetimeToken);
        }

        _host.ReloadVisibleWorkspaceViews();
        _host.SetStatus($"{entries.Count}件をWorkspaceへ保存しました");
    }
}
