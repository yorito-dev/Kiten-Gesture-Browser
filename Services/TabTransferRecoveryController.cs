using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ITabTransferRecoveryHost
{
    IList<BrowserTab> Tabs { get; }
    Guid WindowId { get; }
    BrowserTab? ActiveTab { get; }
    void CleanupTransferredTargetTab(BrowserTab tab);
    void InsertPreservedTab(BrowserTab tab, int index, bool activate);
    void ParkFolderTabContent(BrowserTab tab);
    void AttachTransferredWebView(BrowserTab tab);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void PersistSplitViewState();
    void RefreshSplitViewTabVisualStates();
    void RefreshVerticalTabViews();
    void QueueSessionSave();
    void QueueWorkspaceOpenTabsRefresh();
    void RecordRollbackErrors(string operation, IReadOnlyList<Exception> errors);
    void RecordRuntimeTransitionFailure(BrowserTab tab, string operation, TabRuntimeTransitionResult result);
}

// Runtime移管の開始・確定・取消と転送失敗時の復旧順序を所有する。
// 状態遷移は既存RuntimeCoordinator、実配置の復元はhostへ接続し、同期処理だけを行う。
internal sealed class TabTransferRecoveryController(
    ITabTransferRecoveryHost host,
    SplitViewService splitViewService,
    TabRuntimeCoordinator runtimeCoordinator)
{
    private readonly ITabTransferRecoveryHost _host = host;
    private readonly SplitViewService _splitViewService = splitViewService;
    private readonly TabRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator;

    internal bool TryBeginTabRuntimeTransfers(
        ITabTransferRecoveryHost sourceWindow,
        IEnumerable<BrowserTab> tabs,
        out IReadOnlyList<PendingTabRuntimeTransfer> transfers)
    {
        var started = new List<PendingTabRuntimeTransfer>();
        foreach (var tab in tabs.Where(static tab => !tab.IsFolderTab))
        {
            var result = _runtimeCoordinator.BeginTransfer(
                tab,
                sourceWindow.WindowId,
                _host.WindowId);
            if (!result.Succeeded || result.TransferToken is not { } token)
            {
                _host.RecordRuntimeTransitionFailure(tab, "begin-transfer", result);
                RollbackTabRuntimeTransfers(started, sourceWindow, restorePhysicalOwnership: false);
                transfers = [];
                return false;
            }

            started.Add(new PendingTabRuntimeTransfer(
                tab,
                token,
                Math.Max(0, sourceWindow.Tabs.IndexOf(tab))));
        }

        transfers = started;
        return true;
    }

    internal void CommitTabRuntimeTransfersOrThrow(IReadOnlyList<PendingTabRuntimeTransfer> transfers)
    {
        var result = _runtimeCoordinator.CommitTransfersAtomically(transfers
            .Select(static transfer => new TabRuntimeTransferRequest(transfer.Tab, transfer.Token))
            .ToList());
        if (result.Succeeded)
        {
            return;
        }

        var failedIndex = result.FailedTransferIndex.GetValueOrDefault();
        var failedTransfer = transfers[Math.Clamp(failedIndex, 0, transfers.Count - 1)];
        var failure = result.Failure ?? new TabRuntimeTransitionResult(
            TabRuntimeTransitionStatus.Rejected,
            _runtimeCoordinator.GetOwnershipSnapshot(failedTransfer.Tab),
            "batch-commit-failed");
        _host.RecordRuntimeTransitionFailure(failedTransfer.Tab, "commit-transfer", failure);
        throw new InvalidOperationException(
            $"TabRuntime transfer commit failed: {failure.FailureReason ?? "unknown"}");
    }

    internal void RollbackTabRuntimeTransfers(
        IEnumerable<PendingTabRuntimeTransfer> transfers,
        ITabTransferRecoveryHost sourceWindow,
        bool restorePhysicalOwnership = true)
    {
        foreach (var transfer in transfers.Reverse())
        {
            var result = _runtimeCoordinator.RollbackTransfer(transfer.Tab, transfer.Token);
            if (!result.Succeeded)
            {
                _host.RecordRuntimeTransitionFailure(transfer.Tab, "rollback-transfer", result);
                continue;
            }

            if (!restorePhysicalOwnership)
            {
                continue;
            }

            if (sourceWindow.Tabs.Contains(transfer.Tab))
            {
                sourceWindow.AttachTransferredWebView(transfer.Tab);
            }
            else
            {
                sourceWindow.InsertPreservedTab(
                    transfer.Tab,
                    transfer.SourceInsertionIndex,
                    activate: false);
            }
        }
    }

    internal bool AreTabRuntimeTransfersRestored(
        IReadOnlyList<PendingTabRuntimeTransfer> transfers,
        ITabTransferRecoveryHost source)
        => transfers.All(transfer =>
        {
            var snapshot = _runtimeCoordinator.GetOwnershipSnapshot(transfer.Tab);
            return snapshot.State == TabRuntimeState.Attached
                && snapshot.OwnerWindowId == source.WindowId;
        });

    internal IReadOnlyList<Exception> CleanupPrepared(
        IReadOnlyList<TabTransferEntry> preparedTabs)
    {
        if (preparedTabs.Count == 0)
        {
            return [];
        }

        return new TabTransferTransaction(preparedTabs)
            .RemoveTabsPresentInTarget(_host.Tabs, _host.CleanupTransferredTargetTab);
    }

    internal bool Rollback(
        TabTransferRecoveryController source,
        TabTransferTransaction transaction,
        Action rollbackRuntime,
        Func<bool> runtimeRestored)
    {
        transaction.CancelDeferredActions();
        var cleanupErrors = transaction.RemoveTabsPresentInTarget(
            _host.Tabs,
            _host.CleanupTransferredTargetTab);
        rollbackRuntime();
        IReadOnlyList<Exception> restoreErrors;
        try
        {
            restoreErrors = source.RestoreTransferredSourceTabs(transaction);
        }
        catch (Exception ex)
        {
            restoreErrors = [ex];
        }

        try
        {
            RestoreTransferTargetSelection(transaction);
        }
        catch (Exception ex)
        {
            cleanupErrors = [.. cleanupErrors, ex];
        }

        _host.RecordRollbackErrors("target-cleanup", cleanupErrors);
        source._host.RecordRollbackErrors("source-restore", restoreErrors);
        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
        return cleanupErrors.Count == 0
            && restoreErrors.Count == 0
            && transaction.Entries.All(entry => source._host.Tabs.Contains(entry.SourceTab))
            && transaction.Entries.All(entry => !_host.Tabs.Contains(entry.TransferredTab))
            && runtimeRestored();
    }

    private void RestoreTransferTargetSelection(TabTransferTransaction transaction)
    {
        if (transaction.TargetActiveTab is { } targetActiveTab
            && _host.Tabs.Contains(targetActiveTab))
        {
            _host.SelectTab(targetActiveTab);
            _host.QueueSelectedTabActivation();
            return;
        }

        if (_host.ActiveTab is not null && !_host.Tabs.Contains(_host.ActiveTab) && _host.Tabs.Count > 0)
        {
            _host.SelectTab(_host.Tabs[0]);
            _host.QueueSelectedTabActivation();
        }
    }

    private IReadOnlyList<Exception> RestoreTransferredSourceTabs(
        TabTransferTransaction transaction)
    {
        var errors = transaction.RestoreTabsMissingFromSource(
            _host.Tabs,
            (tab, index) => _host.InsertPreservedTab(tab, index, activate: false));
        List<Exception>? allErrors = errors.Count > 0 ? [.. errors] : null;

        foreach (var folderTab in transaction.Entries
            .Select(static entry => entry.SourceTab)
            .Where(tab => tab.IsFolderTab && _host.Tabs.Contains(tab)))
        {
            try
            {
                _host.ParkFolderTabContent(folderTab);
            }
            catch (Exception ex)
            {
                allErrors ??= [];
                allErrors.Add(ex);
            }
        }

        foreach (var webTab in transaction.Entries
            .Select(static entry => entry.SourceTab)
            .Where(tab => !tab.IsFolderTab && _host.Tabs.Contains(tab)))
        {
            try
            {
                _host.AttachTransferredWebView(webTab);
            }
            catch (Exception ex)
            {
                allErrors ??= [];
                allErrors.Add(ex);
            }
        }

        if (transaction.SourceSplitView is { } splitView)
        {
            var leftTab = transaction.Entries
                .Select(static entry => entry.SourceTab)
                .FirstOrDefault(tab => tab.Id == splitView.LeftTabId);
            var rightTab = transaction.Entries
                .Select(static entry => entry.SourceTab)
                .FirstOrDefault(tab => tab.Id == splitView.RightTabId);
            if (!_splitViewService.TryRestoreSnapshot(
                    _host.WindowId,
                    leftTab,
                    rightTab,
                    _host.Tabs,
                    splitView,
                    out _))
            {
                allErrors ??= [];
                allErrors.Add(new InvalidOperationException("分割ペアの元の状態を復元できませんでした。"));
            }
            else
            {
                _host.PersistSplitViewState();
                _host.RefreshSplitViewTabVisualStates();
                _host.RefreshVerticalTabViews();
            }

            var activePaneTab = string.Equals(
                splitView.ActivePane,
                SplitViewPane.Right,
                StringComparison.OrdinalIgnoreCase)
                    ? rightTab
                    : leftTab;
            if (activePaneTab is not null && _host.Tabs.Contains(activePaneTab))
            {
                _host.SelectTab(activePaneTab);
                _host.QueueSelectedTabActivation();
            }
        }
        else if (transaction.SourceActiveTab is { } sourceActiveTab
            && _host.Tabs.Contains(sourceActiveTab))
        {
            _host.SelectTab(sourceActiveTab);
            _host.QueueSelectedTabActivation();
        }

        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
        return allErrors ?? [];
    }
}
