using TabBrowser.Models;

namespace TabBrowser.Services.Workspaces;

internal interface IWorkspaceTabHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    Guid? LastActiveBrowserTabId { get; }
    void MoveToFront(int index, bool animate);
    void InsertAtFront(BrowserTab tab, bool animate);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void QueueSessionSave();
    void SetStatus(string message, bool autoClear);
}

// Workspaceタブへの往復と先頭配置を所有し、実UIの配置・表示はhostへ委ねる。
internal sealed class WorkspaceTabController(IWorkspaceTabHost host)
{
    internal const string WorkspaceTabUrl = "tabbrowser://workspace";
    private readonly IWorkspaceTabHost _host = host;
    private Guid? _toggleReturnTabId;

    internal void ClearReturnTab() => _toggleReturnTabId = null;

    internal void Open()
    {
        if (FindWorkspaceTab() is { } existingTab)
        {
            MoveToFront(existingTab, animate: true);
            _host.SelectTab(existingTab);
            _host.QueueSelectedTabActivation();
            return;
        }

        var tab = CreateTab();
        _host.InsertAtFront(tab, animate: true);
        _host.SelectTab(tab);
        _host.QueueSelectedTabActivation();
        _host.QueueSessionSave();
    }

    internal void Toggle()
    {
        if (_host.ActiveTab is { IsWorkspaceTab: true })
        {
            var returnTab = ResolveReturnTab();
            if (returnTab is null)
            {
                _host.SetStatus("戻るタブがありません", autoClear: true);
                return;
            }

            ClearReturnTab();
            _host.SelectTab(returnTab);
            _host.QueueSelectedTabActivation();
            return;
        }

        if (_host.ActiveTab is { IsWorkspaceTab: false } sourceTab)
        {
            _toggleReturnTabId = sourceTab.Id;
        }

        Open();
    }

    private BrowserTab? ResolveReturnTab()
    {
        if (_toggleReturnTabId is { } returnTabId
            && _host.Tabs.FirstOrDefault(tab => tab.Id == returnTabId && !tab.IsWorkspaceTab) is { } returnTab)
        {
            return returnTab;
        }

        if (_host.LastActiveBrowserTabId is { } lastActiveBrowserTabId
            && _host.Tabs.FirstOrDefault(tab => tab.Id == lastActiveBrowserTabId && !tab.IsWorkspaceTab) is { } lastActiveBrowserTab)
        {
            return lastActiveBrowserTab;
        }

        return _host.Tabs
            .Where(static tab => !tab.IsWorkspaceTab)
            .OrderByDescending(static tab => tab.LastActivatedAt)
            .FirstOrDefault();
    }

    internal BrowserTab EnsureOpen(bool activate)
    {
        if (FindWorkspaceTab() is { } existingTab)
        {
            MoveToFront(existingTab, animate: false);
            if (activate) _host.SelectTab(existingTab);
            return existingTab;
        }

        var tab = CreateTab();
        _host.InsertAtFront(tab, animate: false);
        if (activate) _host.SelectTab(tab);
        return tab;
    }

    internal void EnsureFirst()
    {
        if (FindWorkspaceTab() is { } tab) MoveToFront(tab, animate: false);
    }

    private BrowserTab? FindWorkspaceTab() => _host.Tabs.FirstOrDefault(static tab => tab.IsWorkspaceTab);

    private void MoveToFront(BrowserTab tab, bool animate)
    {
        var index = _host.Tabs.IndexOf(tab);
        if (index > 0) _host.MoveToFront(index, animate);
    }

    private static BrowserTab CreateTab() => new()
    {
        Url = WorkspaceTabUrl,
        Title = "Workspace OS",
        IsWorkspaceTab = true
    };
}
