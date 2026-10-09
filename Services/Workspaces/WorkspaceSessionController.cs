using System.IO;
using TabBrowser.Models;
using WorkspaceSessionTab = TabBrowser.Models.Workspaces.SessionTab;

namespace TabBrowser.Services.Workspaces;

internal interface IWorkspaceSessionHost
{
    IList<BrowserTab> Tabs { get; }
    Guid WindowId { get; }
    string ResolveUrl(string url);
    bool HasContainerProfile(string containerProfileId);
    BrowserTab CreateFolderTab(string url, string title, string containerProfileId);
    BrowserTab OpenTab(string url, string title, string containerProfileId);
    void SetStatus(string message, bool autoClear);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void QueueBackgroundTabLoading();
    void PersistSplitViewState();
    void RefreshVerticalTabViews();
    void QueueSessionSave();
    void QueueWorkspaceOpenTabsRefresh();
}

/// <summary>Workspace保存タブの展開・分割復元・選択と、別Window用の状態変換を所有する。</summary>
internal sealed class WorkspaceSessionController(IWorkspaceSessionHost host, SplitViewService splitViewService)
{
    private readonly IWorkspaceSessionHost _host = host;
    private readonly SplitViewService _splitViewService = splitViewService;

    public string? DeferredRestoreMessage { get; private set; }

    public int OpenTabs(IReadOnlyList<WorkspaceSessionTab> tabs)
    {
        DeferredRestoreMessage = null;
        var openedTabs = new List<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)>();
        foreach (var tab in tabs.Where(static x => !string.IsNullOrWhiteSpace(x.Url)))
        {
            if (!TryResolveContainerProfileId(tab.ContainerProfileId, out var containerProfileId))
            {
                continue;
            }

            try
            {
                var browserTab = tab.IsFolderTab
                    ? _host.CreateFolderTab(tab.Url, tab.Title, containerProfileId)
                    : _host.OpenTab(_host.ResolveUrl(tab.Url), tab.Title, containerProfileId);
                if (tab.IsFolderTab)
                {
                    _host.Tabs.Add(browserTab);
                }

                openedTabs.Add((tab, browserTab));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _host.SetStatus("Workspaceの保存タブを開けませんでした: " + ex.Message, autoClear: true);
            }
        }

        if (openedTabs.Count == 0)
        {
            return 0;
        }

        RegisterWorkspaceSplitSessions(openedTabs);
        var activationTab = GetWorkspaceSessionActivationTab(openedTabs) ?? openedTabs[0].BrowserTab;
        _host.SelectTab(activationTab);
        _host.QueueSelectedTabActivation();
        _host.QueueBackgroundTabLoading();
        _host.PersistSplitViewState();
        _host.RefreshVerticalTabViews();
        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
        return openedTabs.Count;
    }

    public void OpenInNewWindow(
        IReadOnlyList<WorkspaceSessionTab> tabs,
        Action<IReadOnlyList<(SessionTabState First, WorkspaceSessionTab Second)>> openWindow)
    {
        DeferredRestoreMessage = null;
        var restorableTabs = new List<(WorkspaceSessionTab Tab, string ContainerProfileId)>();
        foreach (var tab in tabs.Where(static tab => !string.IsNullOrWhiteSpace(tab.Url)))
        {
            if (TryResolveContainerProfileId(tab.ContainerProfileId, out var containerProfileId))
            {
                restorableTabs.Add((tab, containerProfileId));
            }
        }

        var splitGroupIds = tabs
            .Where(static tab => Guid.TryParse(tab.SplitGroupId, out _))
            .Select(static tab => tab.SplitGroupId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static splitToken => splitToken,
                static _ => Guid.NewGuid().ToString("N"),
                StringComparer.OrdinalIgnoreCase);
        var initialTabPairs = restorableTabs
            .Select(pair => new SessionTabState
            {
                Id = Guid.NewGuid(),
                Url = pair.Tab.IsFolderTab ? pair.Tab.Url : _host.ResolveUrl(pair.Tab.Url),
                Title = pair.Tab.Title,
                IsFolderTab = pair.Tab.IsFolderTab,
                ContainerProfileId = pair.ContainerProfileId,
                SplitViewId = splitGroupIds.GetValueOrDefault(pair.Tab.SplitGroupId),
                SplitViewOrientation = string.IsNullOrWhiteSpace(pair.Tab.SplitGroupId)
                    ? null
                    : SplitViewModelProfile.NormalizeOrientation(pair.Tab.SplitOrientation)
            })
            .Zip(restorableTabs.Select(static pair => pair.Tab))
            .ToList();
        if (initialTabPairs.Count == 0)
        {
            return;
        }

        openWindow(initialTabPairs);
    }

    // 別Windowが設定を読み直した後にも確認し、共通復元の既定fallbackへ不明IDを渡さない。
    public IReadOnlyList<SessionTabState> FilterRestorableInitialTabs(IReadOnlyList<SessionTabState> tabs)
    {
        DeferredRestoreMessage = null;
        return tabs.Where(tab => TryResolveContainerProfileId(tab.ContainerProfileId, out _)).ToList();
    }

    private bool TryResolveContainerProfileId(string? savedId, out string containerProfileId)
    {
        if (ContainerProfile.TryResolveSavedId(savedId, _host.HasContainerProfile, out containerProfileId)) return true;

        DeferredRestoreMessage = "保存時のコンテナが見つからないため、対象タブの復元を保留しました。Workspaceの保存内容は保持されています。";
        _host.SetStatus(DeferredRestoreMessage, autoClear: false);
        return false;
    }

    public void CompleteNewWindowRestore(
        IReadOnlyList<(SessionTabState First, WorkspaceSessionTab Second)> initialTabPairs)
    {
        var openedTabs = initialTabPairs
            .Select(pair => (
                SavedTab: pair.Second,
                BrowserTab: _host.Tabs.FirstOrDefault(tab => tab.Id == pair.First.Id)))
            .Where(static pair => pair.BrowserTab is not null)
            .Select(static pair => (pair.SavedTab, pair.BrowserTab!))
            .ToList();
        ApplyWorkspaceSplitSessionDetails(openedTabs);
    }

    private void RegisterWorkspaceSplitSessions(
        IReadOnlyList<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)> openedTabs)
    {
        foreach (var group in GetCompleteWorkspaceSplitGroups(openedTabs))
        {
            var left = group.Single(static pair => pair.SavedTab.SplitPane == SplitViewPane.Left);
            var right = group.Single(static pair => pair.SavedTab.SplitPane == SplitViewPane.Right);
            var orientation = SplitViewModelProfile.NormalizeOrientation(left.SavedTab.SplitOrientation);
            if (!_splitViewService.TryStart(
                    _host.WindowId,
                    left.BrowserTab,
                    right.BrowserTab,
                    _host.Tabs,
                    orientation,
                    out _))
            {
                continue;
            }

            _splitViewService.TrySetRatio(
                left.SavedTab.SplitRatio ?? SplitViewModelProfile.DefaultRatio,
                _host.Tabs);
            _splitViewService.TrySetActivePane(NormalizeWorkspaceSplitPane(left.SavedTab.SplitActivePane));
        }
    }

    private void ApplyWorkspaceSplitSessionDetails(
        IReadOnlyList<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)> openedTabs)
    {
        foreach (var group in GetCompleteWorkspaceSplitGroups(openedTabs))
        {
            var left = group.Single(static pair => pair.SavedTab.SplitPane == SplitViewPane.Left);
            if (!_splitViewService.TryActivateForTab(left.BrowserTab))
            {
                continue;
            }

            if (_splitViewService.ActiveSplitView?.LeftTabId != left.BrowserTab.Id)
            {
                _splitViewService.TrySwapPanes();
            }

            _splitViewService.TrySetRatio(
                left.SavedTab.SplitRatio ?? SplitViewModelProfile.DefaultRatio,
                _host.Tabs);
            _splitViewService.TrySetActivePane(NormalizeWorkspaceSplitPane(left.SavedTab.SplitActivePane));
        }

        if (GetWorkspaceSessionActivationTab(openedTabs) is { } activationTab)
        {
            _splitViewService.TryActivateForTab(activationTab);
            _host.SelectTab(activationTab);
        }

        _host.PersistSplitViewState();
        _host.RefreshVerticalTabViews();
    }

    private static List<List<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)>>
        GetCompleteWorkspaceSplitGroups(
            IReadOnlyList<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)> openedTabs)
    {
        return openedTabs
            .Where(static pair => Guid.TryParse(pair.SavedTab.SplitGroupId, out _))
            .GroupBy(static pair => pair.SavedTab.SplitGroupId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.ToList())
            .Where(static group => group.Count == 2
                && group.Any(static pair => pair.SavedTab.SplitPane == SplitViewPane.Left)
                && group.Any(static pair => pair.SavedTab.SplitPane == SplitViewPane.Right))
            .ToList();
    }

    private static BrowserTab? GetWorkspaceSessionActivationTab(
        IReadOnlyList<(WorkspaceSessionTab SavedTab, BrowserTab BrowserTab)> openedTabs)
    {
        var first = openedTabs.FirstOrDefault();
        if (first.SavedTab is null || string.IsNullOrWhiteSpace(first.SavedTab.SplitGroupId))
        {
            return first.BrowserTab;
        }

        var activePane = NormalizeWorkspaceSplitPane(first.SavedTab.SplitActivePane);
        return openedTabs.FirstOrDefault(pair =>
                pair.SavedTab.SplitGroupId.Equals(
                    first.SavedTab.SplitGroupId,
                    StringComparison.OrdinalIgnoreCase)
                && pair.SavedTab.SplitPane == activePane)
            .BrowserTab ?? first.BrowserTab;
    }

    private static string NormalizeWorkspaceSplitPane(string? pane)
    {
        return pane == SplitViewPane.Right ? SplitViewPane.Right : SplitViewPane.Left;
    }
}
