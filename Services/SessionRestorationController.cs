using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ISessionRestorationHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    Guid WindowId { get; }
    int WindowIndex { get; }
    void MarkStartupPerformance(string stage, BrowserTab? tab = null, int? itemCount = null);
    void RestoreSharedClosedTabHistory(IEnumerable<ClosedTabState> closedTabs);
    void RestoreSupplementalClosedItemHistory(SessionData session);
    void RefreshClosedTabHistoryView();
    void AppendSessionDiagnostic(string operation, Exception exception, string? detail = null);
    BrowserTab CreateFolderTab(SessionTabState tabState, BrowserTabState persistentState);
    string? NormalizeSessionTabUrl(string? url, string? title);
    void ApplyContainerProfileToTab(BrowserTab tab);
    void SelectTab(BrowserTab tab);
    void SetStatus(string message, bool autoClear);
    IReadOnlyCollection<SplitViewModel> LoadSplitViews();
    void AppendSplitViewRestoreDiagnostic(SplitViewRestoreResult result, int storedCandidateCount,
        int sessionCandidateCount, int ignoredSessionGroupCount, Guid? activeTabId);
    void RefreshSplitViewTabVisualStates();
    void PersistSplitViewState();
    void ApplySessionWindowIdentity(Guid windowId);
    void ApplyWindowPlacement(WindowPlacementSettings? placement);
    void ApplyAlwaysOnTop(bool? isAlwaysOnTop);
    SessionData CreateSessionData(SessionWindowState window);
    TimeSpan GetRestoredWindowStartupActivationDelay(int windowIndex, int windowCount);
}

internal readonly record struct SessionRestoreResult(
    int RequestedTabCount,
    int RestoredTabCount,
    int FailedTabCount)
{
    public static SessionRestoreResult Empty { get; } = new(0, 0, 0);
}

/// <summary>sessionからのtab・split再構成、Window復元準備と復元結果の状態を所有する。</summary>
internal sealed class SessionRestorationController(
    ISessionRestorationHost host,
    TabOperationService tabOperationService,
    TabNavigationHistoryService navigationHistoryService,
    SplitViewService splitViewService,
    StartupWindowRestoreController startupWindowRestoreController)
{
    private readonly HashSet<Guid> _restoredSessionTabIds = [];

    public int WarningCount { get; private set; }
    public IReadOnlySet<Guid> RestoredSessionTabIds => _restoredSessionTabIds;
    public SessionRestoreResult LastRestoreResult { get; private set; } = SessionRestoreResult.Empty;

    public void RecordWarning() => WarningCount++;

    public void RestoreRememberedWindows<TWindow>(
        SessionData session,
        Func<SessionWindowState, TimeSpan, TWindow> createAndShowWindow,
        Action<SessionData, IReadOnlyDictionary<Guid, TWindow>> restoreFloatingPlaceholders)
    {
        var windows = SessionSnapshotService.CreateRememberedWindowStates(session);
        if (windows.Count == 0)
        {
            host.SetStatus("記憶したウィンドウ構成はありません", autoClear: true);
            return;
        }

        var restoredWindowsBySavedId = new Dictionary<Guid, TWindow>();
        foreach (var item in windows.Select((windowState, index) => new { WindowState = windowState, Index = index }))
        {
            var window = createAndShowWindow(
                item.WindowState,
                host.GetRestoredWindowStartupActivationDelay(item.Index, windows.Count));
            if (item.WindowState.WindowId != Guid.Empty)
            {
                restoredWindowsBySavedId.TryAdd(item.WindowState.WindowId, window);
            }
        }

        restoreFloatingPlaceholders(session, restoredWindowsBySavedId);
        host.SetStatus("記憶したウィンドウ構成を復元しました", autoClear: true);
    }

    private BrowserTab CreateTabFromSessionState(SessionTabState tabState)
    {
        var persistentState = BrowserTabStateMapper.FromSessionTabState(tabState);
        if (persistentState.IsFolderTab)
        {
            return host.CreateFolderTab(tabState, persistentState);
        }

        var url = host.NormalizeSessionTabUrl(tabState.Url, tabState.Title);
        var tab = BrowserTabStateMapper.CreateBrowserTab(persistentState, url);
        navigationHistoryService.RestoreNavigationHistory(
            tab,
            persistentState.NavigationHistory,
            persistentState.NavigationHistoryIndex,
            url);
        return tab;
    }

    public SessionRestoreResult RestoreSession(SessionData session, bool restoreClosedTabs = true)
    {
        LastRestoreResult = SessionRestoreResult.Empty;
        host.MarkStartupPerformance(StartupPerformanceStage.SessionRestoreStarted, itemCount: session.Tabs.Count);
        if (restoreClosedTabs)
        {
            try
            {
                host.RestoreSharedClosedTabHistory(tabOperationService.GetSharedClosedTabsFromSession(session));
            }
            catch (Exception ex)
            {
                RecordWarning();
                host.AppendSessionDiagnostic("closed-tab-history-restore-failed", ex);
                host.RefreshClosedTabHistoryView();
            }

            host.RestoreSupplementalClosedItemHistory(session);
        }
        else
        {
            host.RefreshClosedTabHistoryView();
        }

        var restorableStates = session.Tabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.Url))
            .ToList();
        var restoredTabs = new List<BrowserTab>();
        var skippedTabCount = 0;
        foreach (var tabState in restorableStates)
        {
            try
            {
                restoredTabs.Add(CreateTabFromSessionState(tabState));
            }
            catch (Exception ex)
            {
                skippedTabCount++;
                RecordWarning();
                host.AppendSessionDiagnostic("tab-restore-failed", ex, $"tab={tabState.Id};url={tabState.Url}");
            }
        }

        host.Tabs.Clear();
        var registeredTabs = new List<BrowserTab>();
        foreach (var tab in restoredTabs)
        {
            try
            {
                host.ApplyContainerProfileToTab(tab);
                host.Tabs.Add(tab);
                registeredTabs.Add(tab);
            }
            catch (Exception ex)
            {
                skippedTabCount++;
                RecordWarning();
                host.AppendSessionDiagnostic("tab-registration-failed", ex, $"tab={tab.Id};url={tab.Url}");
            }
        }

        RememberRestoredSessionTabs(registeredTabs);
        RestoreSplitViewsForRestoredTabs(session.LastActiveTabId);

        var result = new SessionRestoreResult(
            restorableStates.Count,
            registeredTabs.Count,
            skippedTabCount);
        LastRestoreResult = result;

        if (skippedTabCount > 0)
        {
            host.SetStatus($"{skippedTabCount} 件のタブ復元をスキップしました", autoClear: true);
        }

        if (host.Tabs.Count == 0)
        {
            host.MarkStartupPerformance(StartupPerformanceStage.SessionRestoreCompleted, itemCount: 0);
            return result;
        }

        var activeTab = session.LastActiveTabId is { } lastActiveId
            ? host.Tabs.FirstOrDefault(x => x.Id == lastActiveId)
            : null;

        host.SelectTab(activeTab ?? host.Tabs[0]);
        host.MarkStartupPerformance(StartupPerformanceStage.SessionRestoreCompleted, host.ActiveTab, registeredTabs.Count);
        return result;
    }

    private void RestoreSplitViewsForRestoredTabs(Guid? activeTabId)
    {
        try
        {
            var hadRestoredSplitIds = host.Tabs.Any(static tab => !string.IsNullOrWhiteSpace(tab.SplitViewId));
            var storedSplitViews = host.LoadSplitViews();
            var splitViewsToRestore = CreateRestorableSplitViews(
                storedSplitViews,
                out var storedCandidateCount,
                out var sessionCandidateCount,
                out var ignoredSessionGroupCount);
            var restoredCount = splitViewService.Restore(
                host.WindowId,
                host.Tabs,
                splitViewsToRestore,
                activeTabId);
            host.AppendSplitViewRestoreDiagnostic(
                splitViewService.LastRestoreResult,
                storedCandidateCount,
                sessionCandidateCount,
                ignoredSessionGroupCount,
                activeTabId);
            host.RefreshSplitViewTabVisualStates();
            if (restoredCount > 0
                || hadRestoredSplitIds
                || splitViewService.LastRestoreResult.CandidateCount > 0)
            {
                host.PersistSplitViewState();
            }
        }
        catch (Exception ex)
        {
            host.AppendSessionDiagnostic("split-view-restore-failed", ex);
            ClearRestoredSplitViewIds();
        }
    }

    private List<SplitViewModel> CreateRestorableSplitViews(
        IReadOnlyCollection<SplitViewModel> storedSplitViews,
        out int storedCandidateCount,
        out int sessionCandidateCount,
        out int ignoredSessionGroupCount)
    {
        var result = new List<SplitViewModel>();
        var storedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        storedCandidateCount = 0;
        sessionCandidateCount = 0;
        ignoredSessionGroupCount = 0;
        foreach (var splitView in storedSplitViews)
        {
            if (SplitViewModelProfile.Normalize(splitView) is not { } normalized)
            {
                continue;
            }

            result.Add(normalized);
            storedIds.Add(normalized.Id.ToString("N"));
            storedCandidateCount++;
        }

        foreach (var group in host.Tabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.SplitViewId))
            .GroupBy(static tab => tab.SplitViewId!, StringComparer.OrdinalIgnoreCase))
        {
            if (storedIds.Contains(group.Key))
            {
                continue;
            }

            if (!Guid.TryParse(group.Key, out var splitViewId))
            {
                ignoredSessionGroupCount++;
                continue;
            }

            var tabs = group.Take(3).ToList();
            if (tabs.Count != 2)
            {
                ignoredSessionGroupCount++;
                continue;
            }

            result.Add(new SplitViewModel
            {
                Id = splitViewId,
                WindowId = host.WindowId,
                LeftTabId = tabs[0].Id,
                RightTabId = tabs[1].Id,
                ActivePane = SplitViewPane.Left,
                Orientation = GetRestoredSplitViewOrientation(tabs),
                Ratio = GetRestoredSplitViewRatio(tabs),
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            });
            sessionCandidateCount++;
        }

        return result;
    }

    private static string GetRestoredSplitViewOrientation(IReadOnlyCollection<BrowserTab> tabs)
    {
        return tabs.Any(static tab => string.Equals(
                tab.SplitViewOrientation,
                SplitViewOrientation.TopBottom,
                StringComparison.OrdinalIgnoreCase))
            ? SplitViewOrientation.TopBottom
            : SplitViewOrientation.LeftRight;
    }

    private static double GetRestoredSplitViewRatio(IEnumerable<BrowserTab> tabs)
    {
        return SplitViewModelProfile.NormalizeRatio(
            tabs.Select(static tab => tab.SplitViewRatio).FirstOrDefault(static ratio => ratio.HasValue));
    }

    private void ClearRestoredSplitViewIds()
    {
        splitViewService.Restore(host.WindowId, host.Tabs, [], activeTabId: null);
        foreach (var tab in host.Tabs)
        {
            tab.SplitViewId = null;
            tab.SplitViewOrientation = null;
            tab.IsActiveSplitViewTab = false;
        }

        host.PersistSplitViewState();
    }

    private void RememberRestoredSessionTabs(IEnumerable<BrowserTab> tabs)
    {
        _restoredSessionTabIds.Clear();
        foreach (var tab in tabs.Where(static tab => !tab.IsNativeContentTab))
        {
            _restoredSessionTabIds.Add(tab.Id);
        }
    }

    public void RestoreWindowSet(SessionData session)
    {
        var windows = session.Windows ?? new List<SessionWindowState>();
        if (windows.Count == 0)
        {
            RestoreSession(session);
            return;
        }

        var primaryWindow = windows[0];
        try
        {
            host.RestoreSharedClosedTabHistory(tabOperationService.GetSharedClosedTabsFromSession(session));
        }
        catch (Exception ex)
        {
            RecordWarning();
            host.AppendSessionDiagnostic("closed-tab-history-restore-failed", ex);
            host.RefreshClosedTabHistoryView();
        }

        host.RestoreSupplementalClosedItemHistory(session);

        host.ApplySessionWindowIdentity(primaryWindow.WindowId);

        try
        {
            host.ApplyWindowPlacement(primaryWindow.Placement);
        }
        catch (Exception ex)
        {
            RecordWarning();
            host.AppendSessionDiagnostic("window-placement-restore-failed", ex, $"window={host.WindowIndex}");
        }

        host.ApplyAlwaysOnTop(primaryWindow.IsAlwaysOnTop);
        RestoreSession(host.CreateSessionData(primaryWindow), restoreClosedTabs: false);

        foreach (var item in windows
            .Select((windowState, index) => new { WindowState = windowState, Index = index })
            .Skip(1))
        {
            startupWindowRestoreController.Queue(
                item.WindowState,
                host.GetRestoredWindowStartupActivationDelay(item.Index, windows.Count));
        }

        if (startupWindowRestoreController.PendingCount > 0)
        {
            host.MarkStartupPerformance(
                StartupPerformanceStage.SecondaryWindowRestoreQueued,
                itemCount: startupWindowRestoreController.PendingCount);
            startupWindowRestoreController.RestoreQueuedWindowShells();
        }
    }
}
