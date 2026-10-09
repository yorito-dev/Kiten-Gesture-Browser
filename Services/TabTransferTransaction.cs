using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct PendingTabRuntimeTransfer(
    BrowserTab Tab,
    TabRuntimeTransferToken Token,
    int SourceInsertionIndex);

internal sealed class TabTransferTransaction
{
    private readonly object _deferredActionsGate = new();
    private List<Action>? _deferredActions = [];
    private readonly IReadOnlyList<TabTransferEntry> _entries;

    public TabTransferTransaction(
        IEnumerable<TabTransferEntry> entries,
        SplitViewModel? sourceSplitView = null,
        BrowserTab? sourceActiveTab = null,
        BrowserTab? targetActiveTab = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToList().AsReadOnly();
        SourceSplitView = CloneSplitView(sourceSplitView);
        SourceActiveTab = sourceActiveTab;
        TargetActiveTab = targetActiveTab;
    }

    public IReadOnlyList<TabTransferEntry> Entries => _entries;

    public SplitViewModel? SourceSplitView { get; }

    public BrowserTab? SourceActiveTab { get; }

    public BrowserTab? TargetActiveTab { get; }

    public void DeferUntilCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_deferredActionsGate)
        {
            if (_deferredActions is null)
            {
                throw new InvalidOperationException("完了済みのtransactionへ処理を追加できません。");
            }

            _deferredActions.Add(action);
        }
    }

    public IReadOnlyList<Exception> CompleteDeferredActions()
    {
        List<Action> actions;
        lock (_deferredActionsGate)
        {
            if (_deferredActions is null)
            {
                return [];
            }

            actions = _deferredActions;
            _deferredActions = null;
        }

        List<Exception>? errors = null;
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        return errors ?? [];
    }

    public void CancelDeferredActions()
    {
        lock (_deferredActionsGate)
        {
            _deferredActions = null;
        }
    }

    public void InsertIntoTarget(
        IList<BrowserTab> targetTabs,
        BrowserTab transferredTab,
        int insertionIndex,
        Action completeInsertion,
        Action? afterCollectionInsert = null)
    {
        ArgumentNullException.ThrowIfNull(targetTabs);
        ArgumentNullException.ThrowIfNull(transferredTab);
        ArgumentNullException.ThrowIfNull(completeInsertion);
        var entry = FindByTransferredTab(transferredTab);
        if (ContainsReference(targetTabs, transferredTab))
        {
            throw new InvalidOperationException("移管先には同じタブを重複挿入できません。");
        }

        try
        {
            targetTabs.Insert(Math.Clamp(insertionIndex, 0, targetTabs.Count), transferredTab);
        }
        finally
        {
            entry.TargetInserted = ContainsReference(targetTabs, transferredTab);
        }

        afterCollectionInsert?.Invoke();
        completeInsertion();
    }

    public void MarkSourceRemoved(BrowserTab sourceTab)
    {
        FindBySourceTab(sourceTab).SourceRemoved = true;
    }

    public IReadOnlyList<Exception> RemoveTabsPresentInTarget(
        IList<BrowserTab> targetTabs,
        Action<BrowserTab> cleanup)
    {
        ArgumentNullException.ThrowIfNull(targetTabs);
        ArgumentNullException.ThrowIfNull(cleanup);
        List<Exception>? errors = null;
        foreach (var entry in _entries.Reverse())
        {
            var targetIndex = FindReferenceIndex(targetTabs, entry.TransferredTab);
            var requiresDetachedTargetCleanup = entry.IsFolderTab
                && !ReferenceEquals(entry.SourceTab, entry.TransferredTab);
            if (targetIndex < 0 && !requiresDetachedTargetCleanup)
            {
                entry.TargetInserted = false;
                continue;
            }

            try
            {
                cleanup(entry.TransferredTab);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }

            while ((targetIndex = FindReferenceIndex(targetTabs, entry.TransferredTab)) >= 0)
            {
                try
                {
                    targetTabs.RemoveAt(targetIndex);
                }
                catch (Exception ex)
                {
                    errors ??= [];
                    errors.Add(ex);
                    break;
                }
            }

            entry.TargetInserted = ContainsReference(targetTabs, entry.TransferredTab);
        }

        return errors ?? [];
    }

    public IReadOnlyList<Exception> RestoreTabsMissingFromSource(
        IList<BrowserTab> sourceTabs,
        Action<BrowserTab, int> insert)
    {
        ArgumentNullException.ThrowIfNull(sourceTabs);
        ArgumentNullException.ThrowIfNull(insert);
        List<Exception>? errors = null;
        foreach (var entry in _entries.OrderBy(static entry => entry.SourceInsertionIndex))
        {
            if (ContainsReference(sourceTabs, entry.SourceTab))
            {
                entry.SourceRemoved = false;
                continue;
            }

            try
            {
                insert(entry.SourceTab, entry.SourceInsertionIndex);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }

            entry.SourceRemoved = !ContainsReference(sourceTabs, entry.SourceTab);
        }

        return errors ?? [];
    }

    private TabTransferEntry FindBySourceTab(BrowserTab tab)
    {
        return _entries.FirstOrDefault(entry => ReferenceEquals(entry.SourceTab, tab))
            ?? throw new InvalidOperationException("移管元タブがtransactionに含まれていません。");
    }

    private TabTransferEntry FindByTransferredTab(BrowserTab tab)
    {
        return _entries.FirstOrDefault(entry => ReferenceEquals(entry.TransferredTab, tab))
            ?? throw new InvalidOperationException("移管先タブがtransactionに含まれていません。");
    }

    private static bool ContainsReference(IList<BrowserTab> tabs, BrowserTab tab)
    {
        return FindReferenceIndex(tabs, tab) >= 0;
    }

    private static int FindReferenceIndex(IList<BrowserTab> tabs, BrowserTab tab)
    {
        for (var index = 0; index < tabs.Count; index++)
        {
            if (ReferenceEquals(tabs[index], tab))
            {
                return index;
            }
        }

        return -1;
    }

    private static SplitViewModel? CloneSplitView(SplitViewModel? source)
    {
        return source is null
            ? null
            : new SplitViewModel
            {
                Id = source.Id,
                WindowId = source.WindowId,
                LeftTabId = source.LeftTabId,
                RightTabId = source.RightTabId,
                ActivePane = source.ActivePane,
                Orientation = source.Orientation,
                Ratio = source.Ratio,
                CreatedAt = source.CreatedAt,
                UpdatedAt = source.UpdatedAt
            };
    }
}

internal sealed class TabTransferEntry
{
    public TabTransferEntry(
        BrowserTab sourceTab,
        BrowserTab transferredTab,
        int sourceInsertionIndex,
        FolderTabTransferSnapshot? folderSnapshot = null)
    {
        SourceTab = sourceTab;
        TransferredTab = transferredTab;
        SourceInsertionIndex = Math.Max(0, sourceInsertionIndex);
        FolderSnapshot = folderSnapshot;
    }

    public BrowserTab SourceTab { get; }

    public BrowserTab TransferredTab { get; }

    public int SourceInsertionIndex { get; }

    public bool IsFolderTab => FolderSnapshot is not null;

    public FolderTabTransferSnapshot? FolderSnapshot { get; }

    public bool TargetInserted { get; internal set; }

    public bool SourceRemoved { get; internal set; }
}

internal sealed class FolderTabTransferSnapshot
{
    private readonly FolderTabSessionState? _folderState;

    public FolderTabTransferSnapshot(
        Guid tabId,
        string folderPath,
        string? title,
        string? viewMode,
        string? sortMode,
        string? sortDirection,
        FolderTabSessionState? folderState,
        bool isPinned)
    {
        TabId = tabId;
        FolderPath = folderPath;
        Title = title;
        ViewMode = viewMode;
        SortMode = sortMode;
        SortDirection = sortDirection;
        _folderState = CloneFolderState(folderState);
        IsPinned = isPinned;
    }

    public Guid TabId { get; }

    public string FolderPath { get; }

    public string? Title { get; }

    public string? ViewMode { get; }

    public string? SortMode { get; }

    public string? SortDirection { get; }

    public bool IsPinned { get; }

    public SessionTabState CreateSessionTabState()
    {
        return new SessionTabState
        {
            Id = TabId,
            Url = FolderPath,
            Title = Title,
            IsFolderTab = true,
            FolderTabViewMode = ViewMode,
            FolderTabSortMode = SortMode,
            FolderTabSortDirection = SortDirection,
            FolderTabState = CloneFolderState(_folderState),
            IsPinned = IsPinned
        };
    }

    private static FolderTabSessionState? CloneFolderState(FolderTabSessionState? state)
    {
        return state is null
            ? null
            : new FolderTabSessionState
            {
                NavigationHistory = [.. state.NavigationHistory ?? []],
                NavigationHistoryIndex = state.NavigationHistoryIndex,
                SelectedPaths = [.. state.SelectedPaths ?? []],
                SelectionAnchorPath = state.SelectionAnchorPath,
                DetailScrollOffset = state.DetailScrollOffset,
                IconScrollOffset = state.IconScrollOffset,
                DetailColumnOrder = [.. state.DetailColumnOrder ?? []]
            };
    }
}
