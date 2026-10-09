using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class SplitViewService
{
    private readonly List<SplitViewModel> splitViews = [];

    public SplitViewRestoreResult LastRestoreResult { get; private set; } = new();

    public SplitViewModel? ActiveSplitView { get; private set; }

    public bool IsActive => ActiveSplitView is not null;

    public bool CanStart(BrowserTab? leftTab, BrowserTab? rightTab)
    {
        return leftTab is not null
            && rightTab is not null
            && leftTab.Id != rightTab.Id
            && !leftTab.IsWorkspaceTab
            && !rightTab.IsWorkspaceTab;
    }

    public bool TryStart(Guid windowId, BrowserTab? leftTab, BrowserTab? rightTab, out SplitViewModel model)
    {
        var tabs = new[] { leftTab, rightTab }.Where(static tab => tab is not null).Cast<BrowserTab>();
        return TryStart(windowId, leftTab, rightTab, tabs, SplitViewOrientation.LeftRight, out model);
    }

    public bool TryStart(
        Guid windowId,
        BrowserTab? leftTab,
        BrowserTab? rightTab,
        IEnumerable<BrowserTab> tabs,
        out SplitViewModel model)
    {
        return TryStart(windowId, leftTab, rightTab, tabs, SplitViewOrientation.LeftRight, out model);
    }

    public bool TryStart(
        Guid windowId,
        BrowserTab? leftTab,
        BrowserTab? rightTab,
        IEnumerable<BrowserTab> tabs,
        string orientation,
        out SplitViewModel model)
    {
        model = new SplitViewModel();
        if (!CanStart(leftTab, rightTab))
        {
            return false;
        }

        model = SplitViewModelProfile.Normalize(new SplitViewModel
        {
            Id = Guid.NewGuid(),
            WindowId = windowId,
            LeftTabId = leftTab!.Id,
            RightTabId = rightTab!.Id,
            ActivePane = SplitViewPane.Left,
            Orientation = SplitViewModelProfile.NormalizeOrientation(orientation),
            Ratio = SplitViewModelProfile.DefaultRatio,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        })!;

        RemoveSplitsContainingTabs(tabs, leftTab!.Id, rightTab!.Id);
        splitViews.Add(model);
        ActiveSplitView = model;
        ApplySplitId(leftTab!, model);
        ApplySplitId(rightTab!, model);
        return true;
    }

    internal bool TryRestoreSnapshot(
        Guid windowId,
        BrowserTab? leftTab,
        BrowserTab? rightTab,
        IEnumerable<BrowserTab> tabs,
        SplitViewModel snapshot,
        out SplitViewModel model)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(snapshot);
        model = new SplitViewModel();
        if (!CanStart(leftTab, rightTab)
            || snapshot.Id == Guid.Empty
            || snapshot.LeftTabId != leftTab!.Id
            || snapshot.RightTabId != rightTab!.Id)
        {
            return false;
        }

        var restored = SplitViewModelProfile.Normalize(new SplitViewModel
        {
            Id = snapshot.Id,
            WindowId = windowId,
            LeftTabId = leftTab.Id,
            RightTabId = rightTab.Id,
            ActivePane = snapshot.ActivePane,
            Orientation = snapshot.Orientation,
            Ratio = snapshot.Ratio,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt
        });
        if (restored is null)
        {
            return false;
        }

        RemoveSplitsContainingTabs(tabs, leftTab.Id, rightTab.Id);
        splitViews.Add(restored);
        ActiveSplitView = restored;
        ApplySplitId(leftTab, restored);
        ApplySplitId(rightTab, restored);
        model = restored;
        return true;
    }

    public void Clear(IEnumerable<BrowserTab> tabs)
    {
        if (ActiveSplitView is { } splitView)
        {
            ClearSplitView(splitView, tabs);
        }

        ActiveSplitView = null;
    }

    public bool Contains(BrowserTab? tab)
    {
        return tab is not null
            && ActiveSplitView is { } splitView
            && IsTabInSplitView(splitView, tab);
    }

    public bool ContainsAny(BrowserTab? tab)
    {
        return tab is not null && FindSplitForTab(tab) is not null;
    }

    public bool TryActivateForTab(BrowserTab? tab)
    {
        var splitView = FindSplitForTab(tab);
        if (splitView is null)
        {
            return false;
        }

        ActiveSplitView = splitView;
        return true;
    }

    public void DeactivateActive()
    {
        ActiveSplitView = null;
    }

    public int Restore(
        Guid windowId,
        IEnumerable<BrowserTab> tabs,
        IEnumerable<SplitViewModel> splitViewsToRestore,
        Guid? activeTabId)
    {
        var tabList = tabs.ToList();
        var restoreCandidates = splitViewsToRestore.ToList();
        var tabById = tabList.ToDictionary(static tab => tab.Id);
        var usedTabIds = new HashSet<Guid>();

        splitViews.Clear();
        ActiveSplitView = null;
        foreach (var tab in tabList)
        {
            tab.SplitViewId = null;
            tab.SplitViewOrientation = null;
            tab.SplitViewRatio = null;
        }

        var result = new SplitViewRestoreResult
        {
            CandidateCount = restoreCandidates.Count
        };

        foreach (var candidate in restoreCandidates)
        {
            var normalized = SplitViewModelProfile.Normalize(candidate);
            if (normalized is null)
            {
                result.InvalidCount++;
                continue;
            }

            if (usedTabIds.Contains(normalized.LeftTabId)
                || usedTabIds.Contains(normalized.RightTabId))
            {
                result.DuplicateTabCount++;
                continue;
            }

            if (!tabById.TryGetValue(normalized.LeftTabId, out var leftTab)
                || !tabById.TryGetValue(normalized.RightTabId, out var rightTab))
            {
                result.MissingTabCount++;
                continue;
            }

            if (!CanStart(leftTab, rightTab))
            {
                result.RejectedTabCount++;
                continue;
            }

            normalized.WindowId = windowId;
            splitViews.Add(normalized);
            usedTabIds.Add(normalized.LeftTabId);
            usedTabIds.Add(normalized.RightTabId);
            ApplySplitId(leftTab, normalized);
            ApplySplitId(rightTab, normalized);

            if (activeTabId is { } selectedId && IsTabInSplitView(normalized, selectedId))
            {
                normalized.ActivePane = selectedId == normalized.RightTabId
                    ? SplitViewPane.Right
                    : SplitViewPane.Left;
            }
        }

        result.RestoredCount = splitViews.Count;
        LastRestoreResult = result;
        return splitViews.Count;
    }

    public bool TryGetPane(BrowserTab? tab, out string pane)
    {
        pane = SplitViewPane.Left;
        if (tab is null || ActiveSplitView is not { } splitView || !IsTabInSplitView(splitView, tab))
        {
            return false;
        }

        pane = tab.Id == splitView.RightTabId
            ? SplitViewPane.Right
            : SplitViewPane.Left;
        return true;
    }

    public BrowserTab? GetTabForPane(IEnumerable<BrowserTab> tabs, string pane)
    {
        var tabId = GetTabIdForPane(pane);
        return tabId is { } id
            ? tabs.FirstOrDefault(tab => tab.Id == id)
            : null;
    }

    public BrowserTab? GetActiveTab(IEnumerable<BrowserTab> tabs)
    {
        return ActiveSplitView is { } splitView
            ? GetTabForPane(tabs, splitView.ActivePane)
            : null;
    }

    public BrowserTab? GetPartnerTab(IEnumerable<BrowserTab> tabs, BrowserTab tab)
    {
        if (FindSplitForTab(tab) is not { } splitView)
        {
            return null;
        }

        var partnerId = tab.Id == splitView.LeftTabId
            ? splitView.RightTabId
            : splitView.LeftTabId;
        return tabs.FirstOrDefault(candidate => candidate.Id == partnerId);
    }

    public bool TrySetActivePane(string pane)
    {
        if (ActiveSplitView is not { } splitView || !IsValidPane(pane))
        {
            return false;
        }

        splitView.ActivePane = pane;
        splitView.UpdatedAt = DateTimeOffset.Now;
        return true;
    }

    public bool TrySetRatio(double ratio, IEnumerable<BrowserTab> tabs)
    {
        if (ActiveSplitView is not { } splitView)
        {
            return false;
        }

        splitView.Ratio = SplitViewModelProfile.NormalizeRatio(ratio);
        splitView.UpdatedAt = DateTimeOffset.Now;
        foreach (var tab in tabs.Where(tab => IsTabInSplitView(splitView, tab)))
        {
            tab.SplitViewRatio = splitView.Ratio;
        }

        return true;
    }

    public bool TrySetOrientation(string orientation, IEnumerable<BrowserTab> tabs)
    {
        if (ActiveSplitView is not { } splitView)
        {
            return false;
        }

        var normalizedOrientation = SplitViewModelProfile.NormalizeOrientation(orientation);
        splitView.Orientation = normalizedOrientation;
        splitView.UpdatedAt = DateTimeOffset.Now;
        foreach (var tab in tabs.Where(tab => IsTabInSplitView(splitView, tab)))
        {
            tab.SplitViewOrientation = normalizedOrientation;
        }

        return true;
    }

    public bool TrySwapPanes()
    {
        if (ActiveSplitView is not { } splitView)
        {
            return false;
        }

        (splitView.LeftTabId, splitView.RightTabId) = (splitView.RightTabId, splitView.LeftTabId);
        splitView.UpdatedAt = DateTimeOffset.Now;
        return true;
    }

    public BrowserTab? RemoveTabFromSplit(BrowserTab tab, IEnumerable<BrowserTab> tabs)
    {
        return TryClearForTab(tab, tabs, out var partner) ? partner : null;
    }

    public bool TryClearForTab(BrowserTab tab, IEnumerable<BrowserTab> tabs, out BrowserTab? partner)
    {
        partner = null;
        if (FindSplitForTab(tab) is not { } splitView)
        {
            return false;
        }

        var partnerId = tab.Id == splitView.LeftTabId
            ? splitView.RightTabId
            : splitView.LeftTabId;
        partner = tabs.FirstOrDefault(candidate => candidate.Id == partnerId);
        ClearSplitView(splitView, tabs);
        return true;
    }

    public IReadOnlyList<SplitViewModel> Snapshot()
    {
        return splitViews
            .Select(SplitViewModelProfile.Normalize)
            .Where(static model => model is not null)
            .Cast<SplitViewModel>()
            .ToList();
    }

    private Guid? GetTabIdForPane(string pane)
    {
        if (ActiveSplitView is not { } splitView)
        {
            return null;
        }

        return pane == SplitViewPane.Right
            ? splitView.RightTabId
            : splitView.LeftTabId;
    }

    private static void ApplySplitId(BrowserTab tab, SplitViewModel model)
    {
        tab.SplitViewId = model.Id.ToString("N");
        tab.SplitViewOrientation = model.Orientation;
        tab.SplitViewRatio = model.Ratio;
    }

    private void RemoveSplitsContainingTabs(IEnumerable<BrowserTab> tabs, params Guid[] tabIds)
    {
        var tabIdSet = tabIds.ToHashSet();
        var targetSplits = splitViews
            .Where(splitView => tabIdSet.Contains(splitView.LeftTabId) || tabIdSet.Contains(splitView.RightTabId))
            .ToList();

        foreach (var splitView in targetSplits)
        {
            ClearSplitView(splitView, tabs);
        }
    }

    private void ClearSplitView(SplitViewModel splitView, IEnumerable<BrowserTab> tabs)
    {
        foreach (var tab in tabs.Where(tab => IsTabInSplitView(splitView, tab)))
        {
            tab.SplitViewId = null;
            tab.SplitViewOrientation = null;
            tab.SplitViewRatio = null;
        }

        splitViews.RemoveAll(candidate => candidate.Id == splitView.Id);
        if (ActiveSplitView?.Id == splitView.Id)
        {
            ActiveSplitView = null;
        }
    }

    private SplitViewModel? FindSplitForTab(BrowserTab? tab)
    {
        return tab is null
            ? null
            : splitViews.FirstOrDefault(splitView => IsTabInSplitView(splitView, tab));
    }

    private static bool IsTabInSplitView(SplitViewModel splitView, BrowserTab tab)
    {
        return tab.Id == splitView.LeftTabId || tab.Id == splitView.RightTabId;
    }

    private static bool IsTabInSplitView(SplitViewModel splitView, Guid tabId)
    {
        return tabId == splitView.LeftTabId || tabId == splitView.RightTabId;
    }

    private static bool IsValidPane(string pane)
    {
        return pane == SplitViewPane.Left || pane == SplitViewPane.Right;
    }
}

public sealed class SplitViewRestoreResult
{
    public int CandidateCount { get; set; }

    public int RestoredCount { get; set; }

    public int InvalidCount { get; set; }

    public int MissingTabCount { get; set; }

    public int DuplicateTabCount { get; set; }

    public int RejectedTabCount { get; set; }

    public int SkippedCount => InvalidCount + MissingTabCount + DuplicateTabCount + RejectedTabCount;
}
