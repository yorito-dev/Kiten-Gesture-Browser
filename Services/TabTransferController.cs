using System.IO;
using TabBrowser.Models;
using TabBrowser.ViewModels;

namespace TabBrowser.Services;

internal interface ITabTransferPayload
{
    BrowserTab Tab { get; }
    TabTransferController Source { get; }
    void UpdateSource(ITabTransferHost target, BrowserTab tab);
}

internal interface ITabTransferHost : ITabTransferRecoveryHost
{
    bool IsLoaded { get; }
    void CloseTabContextMenuForTab(BrowserTab tab);
    Func<TabTransferController> CaptureNewTransferWindowFactory();
    Action CaptureDefaultTransferLayoutAnimation();
    void PositionNearDragCursor();
    void ShowWindow();
    bool IsLeftMouseButtonDown();
    void StartFollowingDragCursor();
    void RestoreUnpreparedTabRuntimeTransfers(IReadOnlyList<PendingTabRuntimeTransfer> transfers);
    IReadOnlyList<BrowserTab> GetTabDragGroup(BrowserTab tab);
    SplitViewModel? GetSplitViewSnapshotForTab(BrowserTab tab);
    FolderTabTransferSnapshot CaptureTransferredFolderTabSnapshot(BrowserTab tab);
    BrowserTab CreateTransferredFolderTab(FolderTabTransferSnapshot snapshot);
    bool TryBeginTabRuntimeTransfers(ITabTransferHost source, IEnumerable<BrowserTab> tabs,
        out IReadOnlyList<PendingTabRuntimeTransfer> transfers);
    void CommitTabRuntimeTransfersOrThrow(IReadOnlyList<PendingTabRuntimeTransfer> transfers);
    void RollbackTabRuntimeTransfers(IReadOnlyList<PendingTabRuntimeTransfer> transfers, ITabTransferHost source);
    bool AreTabRuntimeTransfersRestored(IReadOnlyList<PendingTabRuntimeTransfer> transfers, ITabTransferHost source);
    void RegisterExternalTabMoveUndo(ITabTransferPayload payload, BrowserTab targetTab, bool closeTargetWindowAfterUndo = false);
    void SetTransferStatus(string message);
    bool PersistSession { get; }
    void ApplyContainerProfileToTab(BrowserTab tab);
    int ClampNormalTabInsertionIndex(int index);
    bool HasLoadedRuntime(BrowserTab tab);
    void ShowWebView(BrowserTab tab);
    void MoveWebViewToBackground(BrowserTab tab);
    void QueueBackgroundTabLoading();
    void RestoreOriginalTabOrder();
    void MarkDetachedToWindow();
    void TerminateTabRuntime(BrowserTab tab);
    void AddNewTab(bool activate);
    void CloseWindow();
    void QueueCloseAfterTransferredTabsRemoved();
    void ClearSplitViewForSingleTab(BrowserTab tab);
    void DetachTransferredFolderContent(BrowserTab tab);
    void RemoveWebViewFromHosts(BrowserTab tab);
    void PrepareForRuntimeTransfer(BrowserTab tab);
    void VerifyRemovedTransferredTabRuntime(BrowserTab tab);
    void ForgetTabThumbnailCapture(Guid tabId);
    void ClearYouTubeDisplayFallback(Guid tabId);
    void RemoveYouTubeControlTab(BrowserTab tab);
    void HideDetachedSplitView();
    void HideSplitYouTubeControlPopups();
    TabTransferController? FindShelfTargetWindow(Guid windowId);
    ITabTransferHost? FindShelfSourceWindow(Guid tabId);
    bool CanTransferShelfTab(ITabTransferHost source, BrowserTab tab);
    bool InsertExternalShelfTab(ITabTransferHost source, BrowserTab tab, int insertionIndex);
    void ClearDetachedToWindow();
    void RefreshLoadedWorkspaceOpenTabs();
}

// 転送の準備、挿入、元Windowからの除去、commit後の確定と失敗復旧をまとめる。
// transactionの可逆区間と、FolderTab破棄・空Window処理の確定区間を分けたまま扱う。
internal sealed class TabTransferController(
    ITabTransferHost host,
    SplitViewService splitViewService,
    TabOperationService tabOperationService,
    TabRuntimeCoordinator runtimeCoordinator)
{
    private readonly ITabTransferHost _host = host;
    private readonly SplitViewService _splitViewService = splitViewService;
    private readonly TabOperationService _tabOperationService = tabOperationService;

    internal TabTransferRecoveryController Recovery { get; } = new(host, splitViewService, runtimeCoordinator);

    internal bool TryTransferShelfItem(TabShelfWindowTransferRequest request)
    {
        var draggedSnapshot = request.Tabs.FirstOrDefault(tab => tab.TabId == request.PrimaryTabId);
        if (draggedSnapshot is null)
        {
            _host.SetTransferStatus("移動元のタブ情報を確認できませんでした");
            return false;
        }

        if (request.TargetWindowId is { } targetWindowId)
        {
            var targetWindow = _host.FindShelfTargetWindow(targetWindowId);
            if (targetWindow is null)
            {
                _host.SetTransferStatus("移動先のウィンドウは既に閉じられています");
                _host.QueueWorkspaceOpenTabsRefresh();
                return false;
            }

            if (!ReferenceEquals(targetWindow, this))
            {
                return targetWindow.TryTransferShelfItem(request);
            }
        }

        var sourceWindow = _host.FindShelfSourceWindow(request.PrimaryTabId);
        var sourceTab = sourceWindow?.Tabs.FirstOrDefault(tab => tab.Id == request.PrimaryTabId);
        if (sourceWindow is null || sourceTab is null)
        {
            _host.SetTransferStatus("移動元のタブは既に閉じられています");
            _host.QueueWorkspaceOpenTabsRefresh();
            return false;
        }

        if (!_host.CanTransferShelfTab(sourceWindow, sourceTab))
        {
            return false;
        }

        var insertionIndex = _host.Tabs.Count;
        if (request.TargetWindowId == _host.WindowId
            && request.TargetTabId is { } targetTabId
            && _host.Tabs.FirstOrDefault(tab => tab.Id == targetTabId) is { } targetTab)
        {
            insertionIndex = _host.Tabs.IndexOf(targetTab) + (request.InsertAfterTarget ? 1 : 0);
        }

        insertionIndex = _host.ClampNormalTabInsertionIndex(insertionIndex);
        var transferred = _host.InsertExternalShelfTab(sourceWindow, sourceTab, insertionIndex);
        if (transferred)
        {
            sourceWindow.ClearDetachedToWindow();
            _host.RefreshLoadedWorkspaceOpenTabs();
        }

        return transferred;
    }

    internal bool InsertAcceptedSnapshotTab(BrowserTab tab, int insertionIndex, Func<Action> captureLayoutAnimation)
    {
        var animateLayout = captureLayoutAnimation();
        _host.Tabs.Insert(_host.ClampNormalTabInsertionIndex(insertionIndex), tab);
        animateLayout();
        _host.SelectTab(tab);
        _host.QueueSelectedTabActivation();
        _host.QueueBackgroundTabLoading();
        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
        _host.SetTransferStatus("別ウィンドウのタブを統合しました");
        return true;
    }

    internal void DetachAcceptedTabToNewWindow(BrowserTab tab, ITabTransferPayload dragPayload)
    {
        var draggedTabs = _host.GetTabDragGroup(tab);
        if (draggedTabs.Count == 2)
        {
            DetachTabGroupToNewWindow(tab, draggedTabs, dragPayload);
            return;
        }

        _host.CloseTabContextMenuForTab(tab);
        var createWindow = _host.CaptureNewTransferWindowFactory();
        FolderTabTransferSnapshot? folderSnapshot = null;
        try
        {
            if (tab.IsFolderTab)
            {
                folderSnapshot = _host.CaptureTransferredFolderTabSnapshot(tab);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _host.SelectTab(tab);
            _host.SetTransferStatus("フォルダータブを分離できませんでした: " + ex.Message);
            return;
        }

        TabTransferController? newWindow = null;
        BrowserTab? transferredTab = null;
        TabTransferTransaction? transferTransaction = null;
        IReadOnlyList<PendingTabRuntimeTransfer> runtimeTransfers = [];
        try
        {
            newWindow = createWindow();
            transferredTab = folderSnapshot is null
                ? tab
                : newWindow._host.CreateTransferredFolderTab(folderSnapshot);
            transferTransaction = new TabTransferTransaction(
            [
                new TabTransferEntry(
                    tab,
                    transferredTab,
                    _host.Tabs.IndexOf(tab),
                    folderSnapshot)
            ],
            sourceSplitView: _host.GetSplitViewSnapshotForTab(tab),
            sourceActiveTab: _host.ActiveTab,
            targetActiveTab: newWindow._host.ActiveTab);

            if (!newWindow._host.TryBeginTabRuntimeTransfers(_host, [tab], out runtimeTransfers))
            {
                throw new InvalidOperationException("タブの移管状態を開始できませんでした。");
            }

            newWindow.InsertPreservedTab(
                transferredTab,
                insertionIndex: 0,
                activate: true,
                transferTransaction: transferTransaction,
                deferPostInsertWorkUntilCommit: true);
            CompleteExternalTabTransfer(tab, transferTransaction);
            newWindow._host.CommitTabRuntimeTransfersOrThrow(runtimeTransfers);
        }
        catch (Exception ex)
        {
            var rollbackSucceeded = true;
            if (newWindow is not null && transferTransaction is not null)
            {
                rollbackSucceeded = newWindow.RollbackPreparedTransferredTabs(
                    this,
                    transferTransaction,
                    runtimeTransfers);
            }
            else if (runtimeTransfers.Count > 0)
            {
                _host.RestoreUnpreparedTabRuntimeTransfers(runtimeTransfers);
            }

            if (newWindow is not null)
            {
                try
                {
                    newWindow._host.CloseWindow();
                }
                catch (Exception closeException)
                {
                    rollbackSucceeded = false;
                    newWindow._host.RecordRollbackErrors("target-close", [closeException]);
                }
            }

            if (_host.Tabs.Contains(tab))
            {
                _host.SelectTab(tab);
            }

            _host.SetTransferStatus(
                "タブを分離できませんでした: "
                    + ex.Message
                    + (rollbackSucceeded ? string.Empty : "（元の状態を完全には復元できませんでした）"));
            return;
        }

        if (newWindow is null || transferredTab is null || transferTransaction is null)
        {
            _host.SelectTab(tab);
            _host.SetTransferStatus("タブを分離できませんでした");
            return;
        }

        FinalizeTransferredSourceTabs(transferTransaction);
        var completionErrors = transferTransaction.CompleteDeferredActions();
        newWindow._host.RecordRollbackErrors("target-post-commit", completionErrors);
        try
        {
            newWindow._host.PositionNearDragCursor();
            newWindow._host.ShowWindow();
            if (_host.IsLeftMouseButtonDown())
            {
                newWindow._host.StartFollowingDragCursor();
            }

        }
        catch (Exception ex)
        {
            newWindow._host.RecordRollbackErrors("target-show-after-commit", [ex]);
            _host.SetTransferStatus("タブは移動しましたが、新しいウィンドウを表示できませんでした: " + ex.Message);
            return;
        }

        dragPayload.UpdateSource(newWindow._host, transferredTab);
        newWindow._host.RegisterExternalTabMoveUndo(
            dragPayload,
            transferredTab,
            closeTargetWindowAfterUndo: true);

        if (completionErrors.Count > 0)
        {
            _host.SetTransferStatus("タブは移動しましたが、新しいウィンドウの初期表示で問題が発生しました");
        }
        else if (_host.IsLoaded && _host.Tabs.Count > 0)
        {
            _host.SetTransferStatus("タブを新しいウィンドウで開きました");
        }
    }

    private void DetachTabGroupToNewWindow(
        BrowserTab draggedTab,
        IReadOnlyList<BrowserTab> draggedTabs,
        ITabTransferPayload dragPayload)
    {
        var sourceSplitView = _host.GetSplitViewSnapshotForTab(draggedTab);
        if (sourceSplitView is null)
        {
            _host.SelectTab(draggedTab);
            _host.SetTransferStatus("分割ペアの状態を確認できないため移動を中止しました");
            return;
        }

        foreach (var tab in draggedTabs)
        {
            _host.CloseTabContextMenuForTab(tab);
        }

        var createWindow = _host.CaptureNewTransferWindowFactory();
        var newWindow = createWindow();
        if (!newWindow.TryPrepareTransferredTabGroup(
                this,
                draggedTabs,
                sourceSplitView,
                0,
                out var transferredTabs,
                out var transferTransaction,
                out var runtimeTransfers,
                newWindow._host.CaptureDefaultTransferLayoutAnimation))
        {
            newWindow._host.CloseWindow();
            _host.SelectTab(draggedTab);
            _host.SetTransferStatus("分割ペアを新しいウィンドウへ移動できませんでした");
            return;
        }

        var transferredDraggedTab = transferredTabs[draggedTab.Id];
        try
        {
            CompleteExternalTabTransfer(draggedTabs, transferTransaction);
            newWindow._host.CommitTabRuntimeTransfersOrThrow(runtimeTransfers);
        }
        catch (Exception ex)
        {
            newWindow.RollbackPreparedTransferredTabs(this, transferTransaction, runtimeTransfers);
            newWindow._host.CloseWindow();
            _host.SelectTab(draggedTab);
            _host.SetTransferStatus("分割ペアを新しいウィンドウへ移動できませんでした: " + ex.Message);
            return;
        }

        FinalizeTransferredSourceTabs(transferTransaction);
        newWindow.CompleteTransferredTabGroup(
            transferredTabs,
            transferredDraggedTab,
            sourceSplitView);
        newWindow._host.PositionNearDragCursor();
        newWindow._host.ShowWindow();
        if (_host.IsLeftMouseButtonDown())
        {
            newWindow._host.StartFollowingDragCursor();
        }
        dragPayload.UpdateSource(newWindow._host, transferredDraggedTab);
        newWindow._host.RegisterExternalTabMoveUndo(
            dragPayload,
            transferredDraggedTab,
            closeTargetWindowAfterUndo: true);

        if (_host.IsLoaded && _host.Tabs.Count > 0)
        {
            _host.SetTransferStatus("分割ペアを新しいウィンドウで開きました");
        }
    }

    internal bool InsertAcceptedExternalTabAtIndex(
        ITabTransferPayload payload,
        int insertionIndex,
        bool activateTransferredTab,
        Func<BrowserTab, Action> captureSingleAnimation,
        Func<Action> captureGroupAnimation)
    {
        var sourceTabs = payload.Source._host.GetTabDragGroup(payload.Tab);
        if (sourceTabs.Count == 2)
        {
            return InsertExternalTabGroupAtIndex(
                payload,
                sourceTabs,
                insertionIndex,
                activateTransferredTab,
                captureGroupAnimation);
        }

        var animate = captureSingleAnimation(payload.Tab);
        BrowserTab insertedTab;
        FolderTabTransferSnapshot? folderSnapshot = null;
        try
        {
            if (payload.Tab.IsFolderTab)
            {
                folderSnapshot = _host.CaptureTransferredFolderTabSnapshot(payload.Tab);
                insertedTab = _host.CreateTransferredFolderTab(folderSnapshot);
            }
            else
            {
                insertedTab = payload.Tab;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _host.SetTransferStatus("フォルダータブを移動できませんでした: " + ex.Message);
            return false;
        }
        var source = payload.Source;
        var sourceTab = payload.Tab;
        var transferTransaction = new TabTransferTransaction(
        [
            new TabTransferEntry(
                sourceTab,
                insertedTab,
                source._host.Tabs.IndexOf(sourceTab),
                folderSnapshot)
        ],
        sourceActiveTab: source._host.ActiveTab,
        targetActiveTab: _host.ActiveTab);
        if (!_host.TryBeginTabRuntimeTransfers(source._host, [sourceTab], out var runtimeTransfers))
        {
            if (insertedTab.IsFolderTab)
            {
                _host.TerminateTabRuntime(insertedTab);
            }

            _host.SetTransferStatus("タブの移管状態を開始できませんでした");
            return false;
        }

        try
        {
            InsertPreservedTab(
                insertedTab,
                insertionIndex,
                activate: activateTransferredTab,
                transferTransaction: transferTransaction,
                deferPostInsertWorkUntilCommit: true);
            animate();

            source.CompleteExternalTabTransfer(sourceTab, transferTransaction);
            _host.CommitTabRuntimeTransfersOrThrow(runtimeTransfers);
        }
        catch (Exception ex)
        {
            RollbackPreparedTransferredTabs(source, transferTransaction, runtimeTransfers);
            _host.SetTransferStatus("タブを移動できませんでした: " + ex.Message);
            return false;
        }

        source.FinalizeTransferredSourceTabs(transferTransaction);
        var completionErrors = transferTransaction.CompleteDeferredActions();
        _host.RecordRollbackErrors("target-post-commit", completionErrors);
        payload.UpdateSource(_host, insertedTab);
        if (!activateTransferredTab && !insertedTab.IsFolderTab)
        {
            _host.MoveWebViewToBackground(insertedTab);
        }

        _host.RegisterExternalTabMoveUndo(payload, insertedTab);
        if (completionErrors.Count > 0)
        {
            _host.SetTransferStatus("タブは移動しましたが、移管先の初期表示で問題が発生しました");
        }

        return true;
    }

    private bool InsertExternalTabGroupAtIndex(
        ITabTransferPayload payload,
        IReadOnlyList<BrowserTab> sourceTabs,
        int insertionIndex,
        bool activateTransferredTab,
        Func<Action> captureAnimation)
    {
        var source = payload.Source;
        var sourceSplitView = source._host.GetSplitViewSnapshotForTab(payload.Tab);
        if (sourceSplitView is null
            || sourceTabs.Any(static tab => tab.IsWorkspaceTab)
            || sourceTabs.Any(tab => !source._host.Tabs.Contains(tab)))
        {
            return false;
        }

        if (!TryPrepareTransferredTabGroup(
                source,
                sourceTabs,
                sourceSplitView,
                insertionIndex,
                out var transferredTabs,
                out var transferTransaction,
                out var runtimeTransfers,
                captureAnimation))
        {
            return false;
        }

        var transferredDraggedTab = transferredTabs[payload.Tab.Id];
        try
        {
            source.CompleteExternalTabTransfer(sourceTabs, transferTransaction);
            _host.CommitTabRuntimeTransfersOrThrow(runtimeTransfers);
        }
        catch (Exception ex)
        {
            RollbackPreparedTransferredTabs(source, transferTransaction, runtimeTransfers);
            _host.SetTransferStatus("分割ペアを移動できませんでした: " + ex.Message);
            return false;
        }

        source.FinalizeTransferredSourceTabs(transferTransaction);
        CompleteTransferredTabGroup(
            transferredTabs,
            transferredDraggedTab,
            sourceSplitView,
            activateTransferredTab);
        payload.UpdateSource(_host, transferredDraggedTab);
        _host.RegisterExternalTabMoveUndo(payload, transferredDraggedTab);
        return true;
    }

    internal bool TryPrepareTransferredTabGroup(
        TabTransferController source,
        IReadOnlyList<BrowserTab> sourceTabs,
        SplitViewModel sourceSplitView,
        int insertionIndex,
        out IReadOnlyDictionary<Guid, BrowserTab> transferredTabs,
        out TabTransferTransaction transferTransaction,
        out IReadOnlyList<PendingTabRuntimeTransfer> runtimeTransfers,
        Func<Action> captureAnimation)
    {
        transferredTabs = new Dictionary<Guid, BrowserTab>();
        transferTransaction = null!;
        runtimeTransfers = [];
        var preparedTabs = new List<TabTransferEntry>();
        try
        {
            foreach (var sourceTab in sourceTabs)
            {
                FolderTabTransferSnapshot? folderSnapshot = null;
                BrowserTab transferredTab;
                if (sourceTab.IsFolderTab)
                {
                    folderSnapshot = _host.CaptureTransferredFolderTabSnapshot(sourceTab);
                    transferredTab = _host.CreateTransferredFolderTab(folderSnapshot);
                }
                else
                {
                    transferredTab = sourceTab;
                }

                preparedTabs.Add(new TabTransferEntry(
                    sourceTab,
                    transferredTab,
                    source._host.Tabs.IndexOf(sourceTab),
                    folderSnapshot));
            }
        }
        catch (Exception ex)
        {
            var cleanupErrors = CleanupPreparedTransferredTabs(preparedTabs);
            _host.RecordRollbackErrors("target-prepare", [ex, .. cleanupErrors]);
            if (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _host.SetTransferStatus("分割ペアを移動できませんでした: " + ex.Message);
                return false;
            }

            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "分割ペアの準備と後始末に失敗しました。",
                    [ex, .. cleanupErrors]);
            }

            throw;
        }

        if (!_host.TryBeginTabRuntimeTransfers(
                source._host,
                preparedTabs.Select(static preparedTab => preparedTab.SourceTab),
                out runtimeTransfers))
        {
            var cleanupErrors = CleanupPreparedTransferredTabs(preparedTabs);
            _host.RecordRollbackErrors("target-prepare-begin", cleanupErrors);

            _host.SetTransferStatus("分割ペアの移管状態を開始できませんでした");
            return false;
        }

        transferTransaction = new TabTransferTransaction(
            preparedTabs,
            sourceSplitView,
            source._host.ActiveTab,
            _host.ActiveTab);
        var animate = captureAnimation();
        var targetIndex = _host.ClampNormalTabInsertionIndex(insertionIndex);
        try
        {
            foreach (var preparedTab in preparedTabs)
            {
                InsertPreservedTab(
                    preparedTab.TransferredTab,
                    targetIndex++,
                    activate: false,
                    transferTransaction: transferTransaction);
            }

            animate();
            transferredTabs = preparedTabs.ToDictionary(
                static preparedTab => preparedTab.SourceTab.Id,
                static preparedTab => preparedTab.TransferredTab);
            return true;
        }
        catch (Exception ex)
        {
            RollbackPreparedTransferredTabs(source, transferTransaction, runtimeTransfers);
            transferredTabs = new Dictionary<Guid, BrowserTab>();
            transferTransaction = null!;
            runtimeTransfers = [];
            _host.SetTransferStatus("分割ペアを移動できませんでした: " + ex.Message);
            return false;
        }
    }

    private IReadOnlyList<Exception> CleanupPreparedTransferredTabs(IReadOnlyList<TabTransferEntry> preparedTabs)
        => Recovery.CleanupPrepared(preparedTabs);

    internal bool RollbackPreparedTransferredTabs(
        TabTransferController source,
        TabTransferTransaction transaction,
        IReadOnlyList<PendingTabRuntimeTransfer> runtimeTransfers)
        => Recovery.Rollback(
            source.Recovery,
            transaction,
            () => _host.RollbackTabRuntimeTransfers(runtimeTransfers, source._host),
            () => _host.AreTabRuntimeTransfersRestored(runtimeTransfers, source._host));

    internal void CompleteTransferredTabGroup(
        IReadOnlyDictionary<Guid, BrowserTab> transferredTabs,
        BrowserTab transferredDraggedTab,
        SplitViewModel sourceSplitView,
        bool activateTransferredTab = true)
    {
        var tabToKeepActive = activateTransferredTab ? null : _host.ActiveTab;
        if (transferredTabs.TryGetValue(sourceSplitView.LeftTabId, out var leftTab)
            && transferredTabs.TryGetValue(sourceSplitView.RightTabId, out var rightTab)
            && _splitViewService.TryStart(
                _host.WindowId,
                leftTab,
                rightTab,
                _host.Tabs,
                sourceSplitView.Orientation,
                out _))
        {
            _splitViewService.TrySetRatio(sourceSplitView.Ratio, _host.Tabs);
            _splitViewService.TrySetActivePane(sourceSplitView.ActivePane);
            _host.PersistSplitViewState();
            _host.RefreshSplitViewTabVisualStates();
            _host.RefreshVerticalTabViews();
        }

        if (activateTransferredTab)
        {
            _host.SelectTab(transferredDraggedTab);
            _host.QueueSelectedTabActivation();
        }
        else
        {
            _splitViewService.DeactivateActive();
            if (tabToKeepActive is not null)
            {
                _splitViewService.TryActivateForTab(tabToKeepActive);
            }

            foreach (var transferredTab in transferredTabs.Values.Where(static tab => !tab.IsFolderTab))
            {
                _host.MoveWebViewToBackground(transferredTab);
            }

            _host.RefreshSplitViewTabVisualStates();
        }
        _host.QueueBackgroundTabLoading();
        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
    }

    internal void InsertPreservedTab(
        BrowserTab tab,
        int insertionIndex,
        bool activate,
        TabTransferTransaction? transferTransaction = null,
        bool deferPostInsertWorkUntilCommit = false)
    {
        tab.IsDragSource = false;
        tab.IsDropPreviewBefore = false;
        tab.IsDropPreviewAfter = false;
        _host.ApplyContainerProfileToTab(tab);

        var targetIndex = _host.ClampNormalTabInsertionIndex(insertionIndex);
        if (transferTransaction is not null)
        {
            transferTransaction.InsertIntoTarget(
                _host.Tabs,
                tab,
                targetIndex,
                () =>
                {
                    _host.AttachTransferredWebView(tab);
                    if (deferPostInsertWorkUntilCommit)
                    {
                        transferTransaction.DeferUntilCommit(
                            () => CompletePreservedTabInsertionAfterAttach(tab, activate));
                    }
                    else
                    {
                        CompletePreservedTabInsertionAfterAttach(tab, activate);
                    }
                });
            return;
        }

        _host.Tabs.Insert(targetIndex, tab);
        CompletePreservedTabInsertion(tab, activate);
    }

    private void CompletePreservedTabInsertion(BrowserTab tab, bool activate)
    {
        _host.AttachTransferredWebView(tab);
        CompletePreservedTabInsertionAfterAttach(tab, activate);
    }

    private void CompletePreservedTabInsertionAfterAttach(BrowserTab tab, bool activate)
    {
        if (!_host.Tabs.Contains(tab))
        {
            return;
        }

        if (activate)
        {
            _host.SelectTab(tab);
            if (_host.HasLoadedRuntime(tab))
            {
                _host.ShowWebView(tab);
            }
            else
            {
                _host.QueueSelectedTabActivation();
            }
        }

        _host.QueueBackgroundTabLoading();
        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
    }

    internal void CompleteExternalTabTransfer(BrowserTab tab, bool preserveWebView)
    {
        _host.RestoreOriginalTabOrder();
        _host.MarkDetachedToWindow();
        RemoveDetachedTab(tab, preserveWebView);
    }

    internal void CompleteExternalTabTransfer(
        BrowserTab tab,
        TabTransferTransaction transaction)
    {
        _host.RestoreOriginalTabOrder();
        _host.MarkDetachedToWindow();
        RemoveDetachedTab(
            tab,
            preserveWebView: !tab.IsFolderTab,
            transaction: transaction);
    }

    internal void CompleteExternalTabTransfer(IReadOnlyList<BrowserTab> tabs)
    {
        _host.RestoreOriginalTabOrder();
        _host.MarkDetachedToWindow();
        RemoveDetachedTabs(tabs);
    }

    internal void CompleteExternalTabTransfer(
        IReadOnlyList<BrowserTab> tabs,
        TabTransferTransaction transaction)
    {
        _host.RestoreOriginalTabOrder();
        _host.MarkDetachedToWindow();
        RemoveDetachedTabs(tabs, transaction);
    }

    internal void FinalizeTransferredSourceTabs(TabTransferTransaction transaction)
    {
        foreach (var entry in transaction.Entries.Where(entry =>
                     entry.IsFolderTab
                     && entry.SourceRemoved
                     && !_host.Tabs.Contains(entry.SourceTab)))
        {
            try
            {
                _host.TerminateTabRuntime(entry.SourceTab);
            }
            catch (Exception ex)
            {
                _host.RecordRollbackErrors("source-folder-finalize", [ex]);
            }
        }

        try
        {
            if (_host.Tabs.Count == 0)
            {
                if (_host.PersistSession)
                {
                    _host.AddNewTab(activate: true);
                }
                else
                {
                    _host.QueueCloseAfterTransferredTabsRemoved();
                }
            }

            _host.QueueSessionSave();
            _host.QueueWorkspaceOpenTabsRefresh();
        }
        catch (Exception ex)
        {
            _host.RecordRollbackErrors("source-finalize", [ex]);
        }
    }

    internal void CleanupTransferredTargetTab(BrowserTab tab)
    {
        try
        {
            if (tab.IsFolderTab)
            {
                _host.TerminateTabRuntime(tab);
            }
            else
            {
                _host.RemoveWebViewFromHosts(tab);
            }
        }
        finally
        {
            ForgetTransferredTabAuxiliaryState(tab);
        }
    }

    internal void RemoveDetachedTabRuntime(
        BrowserTab tab,
        bool preserveWebView,
        bool deferFolderTermination = false)
    {
        if (deferFolderTermination)
        {
            _host.DetachTransferredFolderContent(tab);
        }
        else if (!preserveWebView)
        {
            _host.TerminateTabRuntime(tab);
        }
        else
        {
            _host.RemoveWebViewFromHosts(tab);
            _host.PrepareForRuntimeTransfer(tab);
            _host.VerifyRemovedTransferredTabRuntime(tab);
        }

        ForgetTransferredTabAuxiliaryState(tab);
    }

    private void ForgetTransferredTabAuxiliaryState(BrowserTab tab)
    {
        _host.ForgetTabThumbnailCapture(tab.Id);
        _host.ClearYouTubeDisplayFallback(tab.Id);
        _host.RemoveYouTubeControlTab(tab);
    }

    internal void ClearSplitViewForDetachedTab(BrowserTab detachedTab)
    {
        if (!_splitViewService.TryClearForTab(detachedTab, _host.Tabs, out _))
        {
            return;
        }

        _host.HideDetachedSplitView();
        _host.HideSplitYouTubeControlPopups();
        _host.PersistSplitViewState();
        _host.RefreshSplitViewTabVisualStates();
        _host.RefreshVerticalTabViews();
        _host.QueueSessionSave();
    }

    private void RemoveDetachedTab(
        BrowserTab tab,
        bool preserveWebView,
        TabTransferTransaction? transaction = null)
    {
        var wasActive = _host.ActiveTab == tab;
        var index = _host.Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActiveSplitTab = _splitViewService.Contains(tab);
        var splitPartnerTab = wasActiveSplitTab ? _splitViewService.GetPartnerTab(_host.Tabs, tab) : null;
        if (wasActiveSplitTab && splitPartnerTab is not null)
        {
            if (preserveWebView)
            {
                ClearSplitViewForDetachedTab(tab);
            }
            else
            {
                _host.ClearSplitViewForSingleTab(splitPartnerTab);
            }
        }
        else if (_splitViewService.ContainsAny(tab)
            && _splitViewService.TryClearForTab(tab, _host.Tabs, out _))
        {
            _host.PersistSplitViewState();
            _host.RefreshVerticalTabViews();
            _host.QueueSessionSave();
        }

        RemoveDetachedTabRuntime(
            tab,
            preserveWebView,
            deferFolderTermination: transaction is not null && tab.IsFolderTab);
        try
        {
            _host.Tabs.Remove(tab);
        }
        finally
        {
            if (transaction is not null && !_host.Tabs.Contains(tab))
            {
                transaction.MarkSourceRemoved(tab);
            }
        }

        if (_host.Tabs.Count == 0)
        {
            if (transaction is null && _host.PersistSession)
            {
                _host.AddNewTab(activate: true);
            }
            else if (transaction is null)
            {
                _host.CloseWindow();
            }
        }
        else if (splitPartnerTab is not null && _host.Tabs.Contains(splitPartnerTab))
        {
            _host.SelectTab(splitPartnerTab);
            _host.QueueSelectedTabActivation();
        }
        else if (wasActive)
        {
            _host.SelectTab(_host.Tabs[_tabOperationService.GetSelectionIndexAfterRemoval(index, _host.Tabs.Count)]);
            _host.QueueSelectedTabActivation();
        }

        if (transaction is null)
        {
            _host.QueueSessionSave();
            _host.QueueWorkspaceOpenTabsRefresh();
        }
    }

    private void RemoveDetachedTabs(
        IReadOnlyList<BrowserTab> tabs,
        TabTransferTransaction? transaction = null)
    {
        var tabsToRemove = tabs
            .Where(_host.Tabs.Contains)
            .Distinct()
            .ToList();
        if (tabsToRemove.Count == 0)
        {
            return;
        }

        var wasActive = _host.ActiveTab is { } activeTab && tabsToRemove.Contains(activeTab);
        var firstIndex = tabsToRemove.Select(_host.Tabs.IndexOf).Where(static index => index >= 0).DefaultIfEmpty(0).Min();
        var activeSplitTab = tabsToRemove.FirstOrDefault(_splitViewService.Contains);
        if (activeSplitTab is not null)
        {
            ClearSplitViewForDetachedTab(activeSplitTab);
        }
        else
        {
            var splitTab = tabsToRemove.FirstOrDefault(_splitViewService.ContainsAny);
            if (splitTab is not null
                && _splitViewService.TryClearForTab(splitTab, _host.Tabs, out _))
            {
                _host.PersistSplitViewState();
                _host.RefreshVerticalTabViews();
                _host.QueueSessionSave();
            }
        }

        foreach (var tab in tabsToRemove)
        {
            RemoveDetachedTabRuntime(
                tab,
                preserveWebView: !tab.IsFolderTab,
                deferFolderTermination: transaction is not null && tab.IsFolderTab);
            try
            {
                _host.Tabs.Remove(tab);
            }
            finally
            {
                if (transaction is not null && !_host.Tabs.Contains(tab))
                {
                    transaction.MarkSourceRemoved(tab);
                }
            }
        }

        if (_host.Tabs.Count == 0)
        {
            if (transaction is null && _host.PersistSession)
            {
                _host.AddNewTab(activate: true);
            }
            else if (transaction is null)
            {
                _host.QueueCloseAfterTransferredTabsRemoved();
            }
        }
        else if (wasActive)
        {
            _host.SelectTab(_host.Tabs[_tabOperationService.GetSelectionIndexAfterRemoval(firstIndex, _host.Tabs.Count)]);
            _host.QueueSelectedTabActivation();
        }

        if (transaction is null)
        {
            _host.QueueSessionSave();
            _host.QueueWorkspaceOpenTabsRefresh();
        }
    }
}
