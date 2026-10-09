using System.Collections.ObjectModel;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class ClosedTabWindowHistoryStore
{
    private readonly TabOperationService _tabOperationService;
    private readonly List<ClosedTabState> _closedTabs = [];
    private readonly List<ClosedWindowState> _closedWindows = [];
    private readonly HashSet<ClosedTabState> _restoringTabs = new(ReferenceEqualityComparer.Instance);
    private readonly ReadOnlyCollection<ClosedTabState> _closedTabsView;
    private readonly ReadOnlyCollection<ClosedWindowState> _closedWindowsView;

    public ClosedTabWindowHistoryStore(TabOperationService? tabOperationService = null)
    {
        _tabOperationService = tabOperationService ?? new TabOperationService();
        _closedTabsView = _closedTabs.AsReadOnly();
        _closedWindowsView = _closedWindows.AsReadOnly();
    }

    public IReadOnlyList<ClosedTabState> ClosedTabs => _closedTabsView;

    public IReadOnlyList<ClosedWindowState> ClosedWindows => _closedWindowsView;

    public bool IsRestoringTab(ClosedTabState tab) => _restoringTabs.Contains(tab);

    public bool HasRestoringTabInWindow(Guid windowId)
        => _restoringTabs.Any(tab => tab.ClosedWindowId == windowId);

    public bool TryBeginTabRestore(ClosedTabState tab)
        => _closedTabs.Contains(tab) && _restoringTabs.Add(tab);

    public bool CommitTabRestore(ClosedTabState tab)
    {
        if (!_restoringTabs.Contains(tab) || !_closedTabs.Remove(tab)) return false;
        UpdateClosedWindowAfterTabRestore(tab);
        return true;
    }

    public void EndTabRestore(ClosedTabState tab) => _restoringTabs.Remove(tab);

    public IReadOnlyList<ClosedTabState> ReplaceClosedTabs(
        IEnumerable<ClosedTabState>? closedTabs,
        DateTimeOffset? referenceTime = null)
    {
        var previousTabs = _closedTabs.ToList();
        var normalizedTabs = _tabOperationService.NormalizeClosedTabHistory(closedTabs, referenceTime);
        PreserveRestoringTabs(previousTabs, normalizedTabs);
        _closedTabs.Clear();
        _closedTabs.AddRange(normalizedTabs);
        return GetRemovedTabs(previousTabs, normalizedTabs);
    }

    public void ReplaceClosedWindows(
        IEnumerable<ClosedWindowState>? closedWindows,
        DateTimeOffset? referenceTime = null)
    {
        var normalizedWindows = _tabOperationService.NormalizeClosedWindowHistory(
            closedWindows,
            referenceTime);
        _closedWindows.Clear();
        _closedWindows.AddRange(normalizedWindows);
        NormalizeClosedWindowTabLinks();
    }

    public IReadOnlyList<ClosedTabState> AddClosedTab(
        ClosedTabState closedTab,
        DateTimeOffset? referenceTime = null)
    {
        ArgumentNullException.ThrowIfNull(closedTab);

        _closedTabs.Insert(0, closedTab);
        return NormalizeClosedTabs(referenceTime);
    }

    public IReadOnlyList<ClosedTabState> AddClosedWindowAndTabs(
        ClosedWindowState closedWindow,
        IEnumerable<ClosedTabState> closedTabs,
        DateTimeOffset? referenceTime = null)
    {
        ArgumentNullException.ThrowIfNull(closedWindow);
        ArgumentNullException.ThrowIfNull(closedTabs);

        _closedWindows.Insert(0, closedWindow);
        _closedTabs.InsertRange(0, closedTabs);
        NormalizeClosedWindows(referenceTime);
        return NormalizeClosedTabs(referenceTime);
    }

    public IReadOnlyList<ClosedTabState> MergeProtectedHistory(
        SessionData session,
        DateTimeOffset? referenceTime = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var closedTabKeys = _closedTabs
            .Select(CreateProtectedClosedTabMergeKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var closedTab in _tabOperationService.NormalizeClosedTabHistory(
            _tabOperationService.GetSharedClosedTabsFromSession(session),
            referenceTime))
        {
            if (closedTabKeys.Add(CreateProtectedClosedTabMergeKey(closedTab)))
            {
                _closedTabs.Add(closedTab);
            }
        }

        var removedTabs = NormalizeClosedTabs(referenceTime);

        var closedWindowIds = _closedWindows.Select(static window => window.Id).ToHashSet();
        foreach (var closedWindow in _tabOperationService.NormalizeClosedWindowHistory(
            session.ClosedWindows,
            referenceTime))
        {
            if (closedWindowIds.Add(closedWindow.Id))
            {
                _closedWindows.Add(closedWindow);
            }
        }

        NormalizeClosedWindows(referenceTime);
        return removedTabs;
    }

    public IReadOnlyList<ClosedTabState> NormalizeClosedTabs(DateTimeOffset? referenceTime = null)
    {
        var previousTabs = _closedTabs.ToList();
        var normalizedTabs = _tabOperationService.NormalizeClosedTabHistory(_closedTabs, referenceTime);
        PreserveRestoringTabs(previousTabs, normalizedTabs);
        _closedTabs.Clear();
        _closedTabs.AddRange(normalizedTabs);
        return GetRemovedTabs(previousTabs, normalizedTabs);
    }

    public void NormalizeClosedWindows(DateTimeOffset? referenceTime = null)
    {
        var normalizedWindows = _tabOperationService.NormalizeClosedWindowHistory(
            _closedWindows,
            referenceTime);
        _closedWindows.Clear();
        _closedWindows.AddRange(normalizedWindows);
        NormalizeClosedWindowTabLinks();
    }

    public bool RemoveClosedTab(ClosedTabState closedTab)
    {
        ArgumentNullException.ThrowIfNull(closedTab);
        return !_restoringTabs.Contains(closedTab) && _closedTabs.Remove(closedTab);
    }

    public bool UpdateClosedWindowAfterTabRestore(ClosedTabState closedTab)
    {
        ArgumentNullException.ThrowIfNull(closedTab);

        if (closedTab.ClosedWindowId is not { } closedWindowId
            || closedTab.SourceTabId is not { } sourceTabId)
        {
            return false;
        }

        var closedWindow = _closedWindows.FirstOrDefault(window => window.Id == closedWindowId);
        if (closedWindow is null)
        {
            return false;
        }

        var removedCount = closedWindow.WindowState.Tabs.RemoveAll(tab => tab.Id == sourceTabId);
        if (removedCount == 0)
        {
            return false;
        }

        if (closedWindow.WindowState.Tabs.Count == 0)
        {
            _closedWindows.Remove(closedWindow);
        }
        else
        {
            if (closedWindow.WindowState.LastActiveTabId == sourceTabId)
            {
                closedWindow.WindowState.LastActiveTabId = closedWindow.WindowState.Tabs[0].Id;
            }

            NormalizeClosedWindowSplitState(closedWindow.WindowState);
        }

        return true;
    }

    public IReadOnlyList<ClosedTabState> RemoveClosedWindow(ClosedWindowState closedWindow)
    {
        ArgumentNullException.ThrowIfNull(closedWindow);

        if (HasRestoringTabInWindow(closedWindow.Id) || !_closedWindows.Remove(closedWindow))
        {
            return [];
        }

        var removedTabs = _closedTabs
            .Where(tab => tab.ClosedWindowId == closedWindow.Id)
            .ToList();
        _closedTabs.RemoveAll(tab => tab.ClosedWindowId == closedWindow.Id);
        return removedTabs;
    }

    public List<ClosedTabState> CreateClosedTabSnapshot(DateTimeOffset? referenceTime = null)
    {
        return _tabOperationService.NormalizeClosedTabHistory(_closedTabs, referenceTime);
    }

    public List<ClosedWindowState> CreateClosedWindowSnapshot(DateTimeOffset? referenceTime = null)
    {
        return _tabOperationService.NormalizeClosedWindowHistory(_closedWindows, referenceTime);
    }

    private void NormalizeClosedWindowTabLinks()
    {
        var windowIds = _closedWindows.Select(static window => window.Id).ToHashSet();
        foreach (var closedTab in _closedTabs)
        {
            if (closedTab.ClosedWindowId is { } closedWindowId
                && windowIds.Contains(closedWindowId)
                && closedTab.SourceTabId is not null)
            {
                continue;
            }

            closedTab.ClosedWindowId = null;
            closedTab.ClosedWindowTabIndex = null;
        }
    }

    private static void NormalizeClosedWindowSplitState(SessionWindowState windowState)
    {
        var validSplitIds = windowState.Tabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.SplitViewId))
            .GroupBy(static tab => tab.SplitViewId!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 2)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in windowState.Tabs.Where(tab =>
                     !string.IsNullOrWhiteSpace(tab.SplitViewId)
                     && !validSplitIds.Contains(tab.SplitViewId)))
        {
            tab.SplitViewId = null;
            tab.SplitViewOrientation = null;
            tab.SplitViewRatio = null;
        }
    }

    private static string CreateProtectedClosedTabMergeKey(ClosedTabState closedTab)
    {
        return string.Join(
            '\u001f',
            closedTab.SourceTabId?.ToString("N") ?? string.Empty,
            closedTab.SourceWindowId?.ToString("N") ?? string.Empty,
            closedTab.ClosedWindowId?.ToString("N") ?? string.Empty,
            closedTab.Url ?? string.Empty,
            closedTab.ClosedAt?.ToUniversalTime().Ticks.ToString() ?? string.Empty,
            closedTab.IsFolderTab.ToString());
    }

    private static IReadOnlyList<ClosedTabState> GetRemovedTabs(
        IReadOnlyList<ClosedTabState> previousTabs,
        IReadOnlyCollection<ClosedTabState> retainedTabs)
    {
        var retainedSet = retainedTabs.ToHashSet(ReferenceEqualityComparer.Instance);
        return previousTabs
            .Where(tab => !retainedSet.Contains(tab))
            .ToList();
    }

    private void PreserveRestoringTabs(IReadOnlyList<ClosedTabState> previous, List<ClosedTabState> normalized)
    {
        for (var index = 0; index < previous.Count; index++)
        {
            var tab = previous[index];
            if (_restoringTabs.Contains(tab) && !normalized.Contains(tab))
            {
                normalized.Insert(Math.Min(index, normalized.Count), tab);
            }
        }
    }
}
