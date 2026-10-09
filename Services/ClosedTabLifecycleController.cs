using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IClosedTabLifecycleHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    Guid SessionWindowId { get; }
    Guid RuntimeWindowId { get; }
    bool PersistSession { get; }
    bool IsApplicationFullscreenActive { get; }
    bool HasFullScreenElement(BrowserTab tab);
    bool CanRememberClosedTab(BrowserTab tab);
    void SilenceTabRuntimeAudio(BrowserTab tab);
    void ClearSplitViewForSingleTab(BrowserTab tab);
    void PersistSplitViewState();
    void RefreshVerticalTabViews();
    void QueueSessionSave();
    void ApplyYouTubeApplicationFullscreen(bool enterFullscreen);
    ClosedTabState CreateClosedTabState(BrowserTab tab, DateTimeOffset closedAt, bool isMuted);
    bool CanPreserveClosedTabRuntime(BrowserTab tab);
    void BeginClosedTabThumbnailCapture(ClosedTabState closedTab, BrowserTab tab);
    void RefreshClosedTabHistoryViews();
    void QueueClosedTabNativeAudioPanReset(BrowserTab tab);
    void RemoveWebViewFromHosts(BrowserTab tab);
    void ClearRemovedTabWindowState(BrowserTab tab);
    void DetachPreservedTabRuntime(BrowserTab tab);
    void ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
    void QueuePreservedClosedTabSuspension(BrowserTab tab);
    void TerminateTabRuntime(BrowserTab tab);
    void AddNewTab(bool activate);
    void CloseWindow();
    void CloseTab(BrowserTab tab);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void QueueWorkspaceOpenTabsRefresh();
    BrowserTab CreateRestoredFolderTab(ClosedTabState closedTab);
    BrowserTab CreateRestoredWebTab(ClosedTabState closedTab);
    void SetStatus(string message, bool autoClear);
    void AttachTransferredWebView(BrowserTab tab);
    void ResumeTabIfSuspended(BrowserTab tab);
    void RestoreTabRuntimeAudioState(BrowserTab tab, bool isMuted);
    void RefreshTabFromCore(BrowserTab tab);
    void QueueRestoredTabBridgeCompatibilityProbe(BrowserTab tab);
    void QueueBackgroundTabLoading();
    Task<bool> TrySuspendTabAsync(BrowserTab tab);
    SessionWindowState CreateWindowSessionState();
    ClosedTabState CreateClosedTabState(SessionTabState tab, DateTimeOffset closedAt);
    void ShowRestoredWindow(SessionWindowState windowState);
    void RecordClosedWindowCaptureFailure(Exception exception);
}

// 閉じる・復元・保持中の休止と解放を、履歴の参照と実BrowserTabの対応を保って管理する。
// private判定、入力／権限取消、実Core／Window操作は既存hostに残す。
internal sealed class ClosedTabLifecycleController(
    IClosedTabLifecycleHost host,
    ClosedTabWindowHistoryStore historyStore,
    SplitViewService splitViewService,
    TabOperationService tabOperationService,
    Func<IReadOnlyList<ClosedTabLifecycleController>> getWindowControllers,
    Func<DateTimeOffset>? utcNow = null,
    TabRuntimeCoordinator? runtimeCoordinator = null)
{
    private readonly IClosedTabLifecycleHost _host = host;
    private readonly ClosedTabWindowHistoryStore _historyStore = historyStore;
    private readonly SplitViewService _splitViewService = splitViewService;
    private readonly TabOperationService _tabOperationService = tabOperationService;
    private readonly Dictionary<ClosedTabState, BrowserTab> _closedTabRuntimeTabs = [];
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly TabRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? TabRuntimeCoordinator.Shared;

    public void CloseOtherTabs(BrowserTab? target = null)
    {
        if ((target ?? _host.ActiveTab) is not { } activeTab)
        {
            return;
        }

        foreach (var tab in _host.Tabs.Where(x => x != activeTab).ToList())
        {
            _host.CloseTab(tab);
        }

        _host.SelectTab(activeTab);
        _host.QueueSelectedTabActivation();
    }

    public void CloseTabsToSide(bool closeRight, BrowserTab? target = null)
    {
        if ((target ?? _host.ActiveTab) is not { } activeTab)
        {
            return;
        }

        var activeIndex = _host.Tabs.IndexOf(activeTab);
        if (activeIndex < 0)
        {
            return;
        }

        var targets = _host.Tabs
            .Select((tab, index) => new { Tab = tab, Index = index })
            .Where(x => closeRight ? x.Index > activeIndex : x.Index < activeIndex)
            .Select(static x => x.Tab)
            .ToList();

        foreach (var tab in targets)
        {
            _host.CloseTab(tab);
        }

        _host.SelectTab(activeTab);
        _host.QueueSelectedTabActivation();
    }

    public void ReplaceClosedTabHistory(IEnumerable<ClosedTabState>? closedTabs)
    {
        DisposePreservedRuntimes();
        _historyStore.ReplaceClosedTabs(closedTabs);
        _host.RefreshClosedTabHistoryViews();
    }

    public void ReplaceClosedWindowHistory(IEnumerable<ClosedWindowState>? closedWindows)
    {
        _historyStore.ReplaceClosedWindows(closedWindows);
        _host.RefreshClosedTabHistoryViews();
    }

    public void RememberClosedWindowAndTabs(bool queueSessionSave)
    {
        try
        {
            var windowState = _host.CreateWindowSessionState();
            if (windowState.Tabs.Count == 0)
            {
                return;
            }

            var closedAt = _utcNow();
            var closedWindow = new ClosedWindowState
            {
                Id = Guid.NewGuid(),
                ClosedAt = closedAt,
                WindowState = windowState
            };
            var sourceTabs = _host.Tabs.ToDictionary(static tab => tab.Id);
            var closedTabs = new List<ClosedTabState>(windowState.Tabs.Count);
            for (var index = 0; index < windowState.Tabs.Count; index++)
            {
                var tabState = windowState.Tabs[index];
                var closedTab = sourceTabs.TryGetValue(tabState.Id, out var sourceTab)
                    ? _host.CreateClosedTabState(sourceTab, closedAt, sourceTab.IsMuted)
                    : _host.CreateClosedTabState(tabState, closedAt);
                closedTab.ClosedWindowId = closedWindow.Id;
                closedTab.SourceWindowId = _host.SessionWindowId;
                closedTab.SourceTabId = tabState.Id;
                closedTab.ClosedWindowTabIndex = index;
                closedTabs.Add(closedTab);
            }

            ApplyHistoryRetention(
                _historyStore.AddClosedWindowAndTabs(closedWindow, closedTabs),
                _historyStore,
                getWindowControllers);
            if (queueSessionSave)
            {
                _host.QueueSessionSave();
            }
            _host.RefreshClosedTabHistoryViews();
        }
        catch (Exception ex)
        {
            _host.RecordClosedWindowCaptureFailure(ex);
        }
    }

    public void RestoreClosedWindow(ClosedWindowState closedWindow)
    {
        if (!_historyStore.ClosedWindows.Contains(closedWindow)
            || _historyStore.HasRestoringTabInWindow(closedWindow.Id))
        {
            return;
        }

        try
        {
            _host.ShowRestoredWindow(closedWindow.WindowState);
            _historyStore.RemoveClosedWindow(closedWindow);
            _host.QueueSessionSave();
            _host.RefreshClosedTabHistoryViews();
            _host.SetStatus("閉じたウィンドウを復元しました", autoClear: true);
        }
        catch (Exception ex)
        {
            _host.SetStatus("閉じたウィンドウを復元できませんでした: " + ex.Message, autoClear: true);
        }
    }

    public void ClosePreparedTab(BrowserTab tab)
    {
        var wasActive = _host.ActiveTab == tab;
        var index = _host.Tabs.IndexOf(tab);
        var closedTabWasMuted = tab.IsMuted;
        _host.SilenceTabRuntimeAudio(tab);
        ClosedTabState? closedTab = null;
        var preserveRuntimeTab = false;
        var wasActiveSplitTab = _splitViewService.Contains(tab);
        var splitPartnerTab = wasActiveSplitTab ? _splitViewService.GetPartnerTab(_host.Tabs, tab) : null;

        if (wasActiveSplitTab && splitPartnerTab is not null)
        {
            _host.ClearSplitViewForSingleTab(splitPartnerTab);
        }
        else if (_splitViewService.ContainsAny(tab)
            && _splitViewService.TryClearForTab(tab, _host.Tabs, out _))
        {
            _host.PersistSplitViewState();
            _host.RefreshVerticalTabViews();
            _host.QueueSessionSave();
        }

        if (_host.IsApplicationFullscreenActive
            && (wasActive || _host.HasFullScreenElement(tab)))
        {
            _host.ApplyYouTubeApplicationFullscreen(enterFullscreen: false);
        }

        if (_host.CanRememberClosedTab(tab))
        {
            closedTab = _host.CreateClosedTabState(tab, _utcNow(), closedTabWasMuted);
            closedTab.SourceWindowId = _host.SessionWindowId;
            if (_host.CanPreserveClosedTabRuntime(tab))
            {
                _closedTabRuntimeTabs[closedTab] = tab;
                preserveRuntimeTab = true;
                _host.BeginClosedTabThumbnailCapture(closedTab, tab);
            }

            ApplyHistoryRetention(
                _historyStore.AddClosedTab(closedTab));
            _host.RefreshClosedTabHistoryViews();
        }

        if (!tab.IsNativeContentTab)
        {
            _host.QueueClosedTabNativeAudioPanReset(tab);
        }

        _host.RemoveWebViewFromHosts(tab);
        _host.ClearRemovedTabWindowState(tab);
        _host.Tabs.Remove(tab);
        if (preserveRuntimeTab)
        {
            _host.DetachPreservedTabRuntime(tab);
        }
        _host.ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();

        if (preserveRuntimeTab)
        {
            _host.QueuePreservedClosedTabSuspension(tab);
        }
        else
        {
            _host.TerminateTabRuntime(tab);
        }

        if (_host.Tabs.Count == 0)
        {
            if (_host.PersistSession)
            {
                _host.AddNewTab(activate: true);
            }
            else
            {
                _host.CloseWindow();
            }
        }
        else if (wasActiveSplitTab && splitPartnerTab is not null && _host.Tabs.Contains(splitPartnerTab))
        {
            _host.SelectTab(splitPartnerTab);
            _host.QueueSelectedTabActivation();
        }
        else if (wasActive)
        {
            _host.SelectTab(_host.Tabs[_tabOperationService.GetSelectionIndexAfterRemoval(index, _host.Tabs.Count)]);
            _host.QueueSelectedTabActivation();
        }

        _host.QueueSessionSave();
        _host.QueueWorkspaceOpenTabsRefresh();
    }

    public bool RestoreClosedTabInCurrentWindow(ClosedTabState closedTab)
    {
        if (!_historyStore.TryBeginTabRestore(closedTab)) return false;
        BrowserTab? restoredTab = null;
        ClosedTabLifecycleController? runtimeOwner = null;
        BrowserTab? previousActiveTab = null;
        var ownsPreparation = false;
        try
        {
            try
            {
                previousActiveTab = _host.ActiveTab;
                if (!closedTab.IsFolderTab && TryGetClosedTabRuntimeOwner(closedTab, out runtimeOwner, out var retained))
                {
                    restoredTab = retained;
                    var state = _runtimeCoordinator.GetOwnershipSnapshot(retained);
                    var canPreserve = false;
                    try { canPreserve = state.State == TabRuntimeState.Detached && _host.CanPreserveClosedTabRuntime(retained); }
                    catch (Exception) { /* 破棄済みCoreへのアクセスなどもURLからの再試行へ切り替える。 */ }
                    if (!canPreserve)
                    {
                        runtimeOwner!._closedTabRuntimeTabs.Remove(closedTab);
                        if (state.State == TabRuntimeState.Detached) _host.TerminateTabRuntime(retained);
                        NotifyRestoreStatus("保持していたタブを利用できません。履歴は保持されています。もう一度実行するとURLから開き直します。");
                        return false;
                    }
                }
                else
                {
                    restoredTab = closedTab.IsFolderTab
                        ? _host.CreateRestoredFolderTab(closedTab)
                        : _host.CreateRestoredWebTab(closedTab);
                }

                // 復元対象そのものだけを予約する。既存タブや他Window所有のRuntimeは変更しない。
                if (_host.Tabs.Contains(restoredTab)
                    || !_runtimeCoordinator.Attach(restoredTab, _host.RuntimeWindowId).Succeeded)
                {
                    throw new InvalidOperationException("復元先がタブのRuntimeを取得できませんでした。");
                }

                ownsPreparation = true;
                if (runtimeOwner is not null)
                {
                    PreparePreservedClosedTab(restoredTab, closedTab);
                }

                _host.Tabs.Add(restoredTab);
                _host.SelectTab(restoredTab);
                if (!_runtimeCoordinator.CanUseInWindow(restoredTab, _host.RuntimeWindowId, _host.Tabs.Contains(restoredTab)))
                {
                    throw new InvalidOperationException("復元中にタブの所属が変更されました。");
                }
            }
            catch (Exception)
            {
                if (ownsPreparation && restoredTab is not null)
                {
                    RollbackTabPreparation(restoredTab, runtimeOwner is not null, previousActiveTab);
                    var state = _runtimeCoordinator.GetOwnershipSnapshot(restoredTab);
                    if (state.State != TabRuntimeState.Detached)
                    {
                        // 移管済み、破棄済み、または除去失敗のタブを保持辞書から再取得しない。
                        runtimeOwner?._closedTabRuntimeTabs.Remove(closedTab);
                    }
                }

                NotifyRestoreStatus("タブを復元できませんでした。履歴は保持されています。再試行できます。");
                return false;
            }

            if (!_historyStore.CommitTabRestore(closedTab)) return false;
            runtimeOwner?._closedTabRuntimeTabs.Remove(closedTab);
            CompleteTabRestore(restoredTab, runtimeOwner is not null, closedTab.IsFolderTab);
            return true;
        }
        finally
        {
            _historyStore.EndTabRestore(closedTab);
        }
    }

    private void PreparePreservedClosedTab(
        BrowserTab tab,
        ClosedTabState closedTab)
    {
        tab.IsDragSource = false;
        tab.IsDropPreviewBefore = false;
        tab.IsDropPreviewAfter = false;

        _host.AttachTransferredWebView(tab);
        _host.ResumeTabIfSuspended(tab);
        _host.RestoreTabRuntimeAudioState(tab, closedTab.IsMuted);
        _host.RefreshTabFromCore(tab);
    }

    private void RollbackTabPreparation(BrowserTab tab, bool preserved, BrowserTab? previousActiveTab)
    {
        var state = _runtimeCoordinator.GetOwnershipSnapshot(tab);
        if (state.State == TabRuntimeState.Transferring
            || (state.State == TabRuntimeState.Attached && state.OwnerWindowId != _host.RuntimeWindowId)) return;

        RunBestEffort(() => _host.RemoveWebViewFromHosts(tab));
        RunBestEffort(() => _host.ClearRemovedTabWindowState(tab));
        RunBestEffort(() => _host.Tabs.Remove(tab));
        if (_host.Tabs.Contains(tab)) return;
        if (preserved && state.State != TabRuntimeState.Disposed)
        {
            RunBestEffort(() => _host.SilenceTabRuntimeAudio(tab));
            RunBestEffort(() => _host.DetachPreservedTabRuntime(tab));
            RunBestEffort(() => _host.QueuePreservedClosedTabSuspension(tab));
        }
        else
        {
            RunBestEffort(() => _host.TerminateTabRuntime(tab));
        }

        if (_host.ActiveTab == tab && previousActiveTab is not null && _host.Tabs.Contains(previousActiveTab))
        {
            RunBestEffort(() => _host.SelectTab(previousActiveTab));
            RunBestEffort(_host.QueueSelectedTabActivation);
        }
    }

    private void CompleteTabRestore(BrowserTab tab, bool preserved, bool folder)
    {
        var succeeded = true;
        if (preserved) succeeded &= RunBestEffort(() => _host.QueueRestoredTabBridgeCompatibilityProbe(tab));
        succeeded &= RunBestEffort(_host.QueueSelectedTabActivation);
        if (!folder) succeeded &= RunBestEffort(_host.QueueBackgroundTabLoading);
        succeeded &= RunBestEffort(_host.QueueSessionSave);
        succeeded &= RunBestEffort(_host.QueueWorkspaceOpenTabsRefresh);
        succeeded &= RunBestEffort(_host.RefreshClosedTabHistoryViews);
        NotifyRestoreStatus(succeeded
            ? folder ? "閉じたフォルダータブを復元しました" : "閉じたタブを復元しました"
            : "タブは復元しましたが、表示更新の一部に失敗しました。");
    }

    private void NotifyRestoreStatus(string message)
        => RunBestEffort(() => _host.SetStatus(message, autoClear: true));

    private static bool RunBestEffort(Action action)
    {
        try { action(); return true; }
        catch (Exception) { return false; }
    }

    public async Task SuspendPreservedClosedTabAsync(BrowserTab tab)
    {
        if (_host.Tabs.Contains(tab)
            || _runtimeCoordinator.GetOwnershipSnapshot(tab).State != TabRuntimeState.Detached)
        {
            return;
        }

        await _host.TrySuspendTabAsync(tab);
        var restoredOwner = getWindowControllers().FirstOrDefault(owner =>
            _runtimeCoordinator.CanUseInWindow(tab, owner._host.RuntimeWindowId, owner._host.Tabs.Contains(tab)));
        if (restoredOwner is not null)
        {
            restoredOwner._host.ResumeTabIfSuspended(tab);
        }
    }

    public bool TryGetPreservedRuntime(ClosedTabState closedTab, out BrowserTab runtimeTab)
        => _closedTabRuntimeTabs.TryGetValue(closedTab, out runtimeTab!);

    private bool TryGetClosedTabRuntimeOwner(ClosedTabState closedTab,
        out ClosedTabLifecycleController? runtimeOwner, out BrowserTab preservedTab)
    {
        foreach (var owner in getWindowControllers())
        {
            if (owner._closedTabRuntimeTabs.TryGetValue(closedTab, out var runtimeTab))
            {
                runtimeOwner = owner;
                preservedTab = runtimeTab;
                return true;
            }
        }

        runtimeOwner = null;
        preservedTab = null!;
        return false;
    }

    public void TrimClosedTabHistory()
        => ApplyHistoryRetention(_historyStore.NormalizeClosedTabs());

    private void ApplyHistoryRetention(IEnumerable<ClosedTabState> removedTabs)
        => ApplyHistoryRetention(removedTabs, _historyStore, getWindowControllers);

    public static void ApplyHistoryRetention(
        IEnumerable<ClosedTabState> removedTabs,
        ClosedTabWindowHistoryStore historyStore,
        Func<IReadOnlyList<ClosedTabLifecycleController>> getWindowControllers)
    {
        foreach (var removedTab in removedTabs)
        {
            DisposeForAllWindows(removedTab);
        }

        foreach (var closedTab in historyStore.ClosedTabs
            .Skip(TabOperationService.ClosedTabRuntimeRetentionLimit)
            .ToList())
        {
            DisposeForAllWindows(closedTab);
        }

        void DisposeForAllWindows(ClosedTabState closedTab)
        {
            // 各履歴の解放時点でWindow集合を取得する。前の解放による変更を隠さない。
            foreach (var owner in getWindowControllers())
            {
                owner.DisposePreservedRuntime(closedTab);
            }
        }
    }

    private void DisposePreservedRuntime(ClosedTabState closedTab)
    {
        if (_historyStore.IsRestoringTab(closedTab)) return;
        if (_closedTabRuntimeTabs.Remove(closedTab, out var runtimeTab))
        {
            if (_runtimeCoordinator.GetOwnershipSnapshot(runtimeTab).State == TabRuntimeState.Detached)
                _host.TerminateTabRuntime(runtimeTab);
        }
    }

    public void DisposePreservedRuntimes()
    {
        foreach (var runtimeTab in _closedTabRuntimeTabs
            .Where(pair => !_historyStore.IsRestoringTab(pair.Key))
            .Select(pair => pair.Value).Distinct().ToList())
        {
            if (_runtimeCoordinator.GetOwnershipSnapshot(runtimeTab).State == TabRuntimeState.Detached)
                _host.TerminateTabRuntime(runtimeTab);
        }

        foreach (var closedTab in _closedTabRuntimeTabs.Keys.Where(tab => !_historyStore.IsRestoringTab(tab)).ToList())
            _closedTabRuntimeTabs.Remove(closedTab);
    }
}
