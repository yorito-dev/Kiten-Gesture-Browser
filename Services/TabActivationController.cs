using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ITabActivationHost
{
    BrowserTab? ActiveTab { get; }
    CancellationToken LifetimeToken { get; }
    void UpdateYouTubePlaybackStateTracking(BrowserTab? tab);
    void PrepareFolderTabContentTransition(BrowserTab? tab);
    void HideSplitViewForSingleTab(BrowserTab? tab);
    void CancelInactiveFolderTabGestures(Guid? activeTabId);
    void CloseBookmarkPreview(bool clearNavigation);
    void ShowBlankView();
    void SetAddressText(string text);
    void UpdateToolbarState();
    void ShowWorkspaceTab(BrowserTab tab);
    void ShowFolderTab(BrowserTab tab);
    void QueueSessionSave();
    void RefreshSplitViewTabVisualStates();
    bool ShowActiveSplitView();
    bool IsNewTabUrl(string? url);
    bool IsBlankUrl(string? url);
    void SetLastActiveBrowserTabId(Guid tabId);
    void QueueBackgroundTabLoading();
    void QueueBackgroundThumbnailWarmup();
    void ShowWebViewRecoveryPlaceholder(BrowserTab tab, string title, string detail);
    Task<bool> EnsureSplitViewTabsLoadedAsync(CancellationToken cancellationToken);
    void QueueTabAudioPanApply(BrowserTab tab);
    bool HasLoadedRuntime(BrowserTab tab);
    void ShowWebView(BrowserTab tab);
    void ShowMemoryRestorePlaceholder(BrowserTab tab);
    bool CanUseTabRuntimeInCurrentWindow(BrowserTab tab);
    void PlaceWebViewForCurrentActivation(BrowserTab tab);
    Task InitializeTabWebViewRuntimeAsync(BrowserTab tab, string? initialUrl, WebViewInitializationLease lease);
    void NavigateCore(BrowserTab tab, string url);
    void SetStatus(string message);
}

// 選択後の表示分岐、読込・初期化待機と結果適用を所有する。
// 選択世代／split／Lifecycleは既存実体を共有し、実WPF／Runtime資源はhostに残す。
internal sealed class TabActivationController
{
    private readonly ITabActivationHost _host;
    private readonly TabActivationWorkCoordinator _tabActivationWorkCoordinator;
    private readonly WebViewLifecycleService _webViewLifecycleService;
    private readonly SplitViewService _splitViewService;
    private readonly SplitViewWindowStateCoordinator _splitViewWindowStateCoordinator;
    private readonly Func<DateTimeOffset> _utcNow;

    internal TabActivationController(
        ITabActivationHost host,
        TabActivationWorkCoordinator activation,
        WebViewLifecycleService lifecycle,
        SplitViewService splitViewService,
        SplitViewWindowStateCoordinator splitWindowState,
        Func<DateTimeOffset>? utcNow = null)
    {
        _host = host;
        _tabActivationWorkCoordinator = activation;
        _webViewLifecycleService = lifecycle;
        _splitViewService = splitViewService;
        _splitViewWindowStateCoordinator = splitWindowState;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task ActivateSelectedTabAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var activationVersion = _tabActivationWorkCoordinator.BeginSelectedActivation();
        var tab = _host.ActiveTab;
        _host.UpdateYouTubePlaybackStateTracking(tab);
        _host.PrepareFolderTabContentTransition(tab);
        if (_splitViewService.IsActive
            && (tab is null || !_splitViewService.Contains(tab)))
        {
            _host.HideSplitViewForSingleTab(tab);
        }

        _host.CancelInactiveFolderTabGestures(tab?.Id);
        if (tab is null)
        {
            _host.CloseBookmarkPreview(clearNavigation: true);
            _host.ShowBlankView();
            _host.SetAddressText(string.Empty);
            _host.UpdateToolbarState();
            return;
        }

        tab.LastActivatedAt = _utcNow();

        if (tab.IsWorkspaceTab)
        {
            _host.CloseBookmarkPreview(clearNavigation: true);
            _host.ShowWorkspaceTab(tab);
            _host.UpdateToolbarState();
            _host.QueueSessionSave();
            return;
        }

        if (tab.IsFolderTab)
        {
            _host.CloseBookmarkPreview(clearNavigation: true);
            if (!_splitViewWindowStateCoordinator.IsPreparing && _splitViewService.TryActivateForTab(tab))
            {
                _splitViewService.TryGetPane(tab, out var folderPane);
                _splitViewService.TrySetActivePane(folderPane);
                _host.RefreshSplitViewTabVisualStates();
                if (_host.ShowActiveSplitView())
                {
                    _host.UpdateToolbarState();
                    _host.QueueSessionSave();
                    return;
                }
            }

            _host.ShowFolderTab(tab);
            _host.UpdateToolbarState();
            _host.QueueSessionSave();
            return;
        }

        if (!_host.IsNewTabUrl(tab.Url))
        {
            _host.CloseBookmarkPreview(clearNavigation: true);
        }

        if (!_splitViewWindowStateCoordinator.IsPreparing && _splitViewService.TryActivateForTab(tab))
        {
            _host.RefreshSplitViewTabVisualStates();
        }

        if (_splitViewService.TryGetPane(tab, out var selectedSplitPane))
        {
            _splitViewService.TrySetActivePane(selectedSplitPane);
        }

        _host.SetAddressText(_host.IsNewTabUrl(tab.Url)
            ? string.Empty
            : tab.Url ?? string.Empty);
        _host.SetLastActiveBrowserTabId(tab.Id);

        if (_host.IsBlankUrl(tab.Url))
        {
            _host.ShowBlankView();
            _host.UpdateToolbarState();
            _host.QueueBackgroundTabLoading();
            _host.QueueBackgroundThumbnailWarmup();
            _host.QueueSessionSave();
            return;
        }

        if (_webViewLifecycleService.TryGetLoadFailure(tab, out var processFailureMessage))
        {
            _host.ShowWebViewRecoveryPlaceholder(tab, "WebView2 の読み込みに失敗しました", processFailureMessage);
            _host.UpdateToolbarState();
            _host.QueueSessionSave();
            return;
        }

        if (_splitViewService.Contains(tab) && !_splitViewWindowStateCoordinator.IsPreparing)
        {
            var splitViewTabsLoaded = await _host.EnsureSplitViewTabsLoadedAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!IsCurrentTabActivation(tab, activationVersion))
            {
                return;
            }

            if (splitViewTabsLoaded)
            {
                _host.ShowActiveSplitView();
                _host.QueueTabAudioPanApply(tab);
                _host.UpdateToolbarState();
                _host.QueueBackgroundTabLoading();
                _host.QueueBackgroundThumbnailWarmup();
                _host.QueueSessionSave();
                return;
            }
        }

        var isRestoringReleasedMemory = tab.IsMemoryReleased;
        if (_host.HasLoadedRuntime(tab))
        {
            _host.ShowWebView(tab);
        }
        else if (!string.IsNullOrWhiteSpace(tab.Url))
        {
            if (isRestoringReleasedMemory)
            {
                _host.ShowMemoryRestorePlaceholder(tab);
            }
            else
            {
                _host.ShowBlankView();
            }

            await EnsureTabLoadedAsync(tab, tab.MemoryRestoreUrl ?? tab.Url);
            if (cancellationToken.IsCancellationRequested
                || !IsCurrentTabActivation(tab, activationVersion))
            {
                return;
            }

            if (tab.IsMemoryReleased && !_webViewLifecycleService.HasLoadFailure(tab))
            {
                _host.ShowMemoryRestorePlaceholder(tab);
            }
        }
        else
        {
            _host.ShowBlankView();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _host.QueueTabAudioPanApply(tab);
        _host.UpdateToolbarState();
        _host.QueueBackgroundTabLoading();
        _host.QueueBackgroundThumbnailWarmup();
        _host.QueueSessionSave();
    }

    private bool IsCurrentTabActivation(BrowserTab tab, int activationVersion)
    {
        return _tabActivationWorkCoordinator.IsCurrentSelectedActivation(activationVersion)
            && ReferenceEquals(_host.ActiveTab, tab);
    }

    public async Task EnsureTabLoadedAsync(BrowserTab tab, string? initialUrl)
    {
        if (tab.IsWorkspaceTab)
        {
            if (_host.ActiveTab == tab && tab.IsWorkspaceTab)
            {
                _host.ShowWorkspaceTab(tab);
            }
            else if (_host.ActiveTab == tab && tab.IsFolderTab)
            {
                _host.ShowFolderTab(tab);
            }

            return;
        }

        if (!_host.CanUseTabRuntimeInCurrentWindow(tab))
        {
            return;
        }

        if (_host.HasLoadedRuntime(tab))
        {
            _host.PlaceWebViewForCurrentActivation(tab);
            return;
        }

        var initializationResult = await _webViewLifecycleService.TryRunInitializationAsync(
            tab,
            async initializationLease =>
            {
                await _host.InitializeTabWebViewRuntimeAsync(tab, initialUrl, initializationLease);

                if (initializationLease.IsCurrent
                    && !string.IsNullOrWhiteSpace(initialUrl))
                {
                    _host.NavigateCore(tab, initialUrl);
                }
            },
            CreateWebViewInitializationFailureMessage);
        if (!initializationResult.Started)
        {
            if (!_host.CanUseTabRuntimeInCurrentWindow(tab))
            {
                return;
            }

            await WaitForTabInitializationAsync(tab);
            if (_host.CanUseTabRuntimeInCurrentWindow(tab)
                && _host.HasLoadedRuntime(tab))
            {
                _host.PlaceWebViewForCurrentActivation(tab);
            }

            return;
        }

        if (initializationResult.Superseded)
        {
            if (!_host.CanUseTabRuntimeInCurrentWindow(tab))
            {
                return;
            }

            await WaitForTabInitializationAsync(tab);
            if (_host.CanUseTabRuntimeInCurrentWindow(tab)
                && _host.HasLoadedRuntime(tab))
            {
                _host.PlaceWebViewForCurrentActivation(tab);
            }

            return;
        }

        if (initializationResult.Succeeded)
        {
            return;
        }

        if (!_host.CanUseTabRuntimeInCurrentWindow(tab))
        {
            return;
        }

        var message = initializationResult.FailureMessage ?? string.Empty;
        if (initializationResult.Failure is WebView2RuntimeNotFoundException)
        {
            _host.SetStatus("WebView2 Runtime が見つかりません");
        }
        else
        {
            _host.SetStatus("WebView2 初期化失敗");
        }

        if (_host.ActiveTab == tab)
        {
            _host.ShowWebViewRecoveryPlaceholder(tab, "WebView2 の読み込みに失敗しました", message);
        }
    }

    private static string CreateWebViewInitializationFailureMessage(Exception exception)
    {
        return exception is WebView2RuntimeNotFoundException
            ? "WebView2 Runtime が見つかりません。Microsoft Edge WebView2 Runtime をインストールしてください。"
            : "WebView2 の初期化に失敗しました。\n" + exception.Message;
    }

    private async Task WaitForTabInitializationAsync(BrowserTab tab)
    {
        try
        {
            await _webViewLifecycleService
                .WaitForInitializationAsync(tab)
                .WaitAsync(_host.LifetimeToken);
        }
        catch (OperationCanceledException) when (_host.LifetimeToken.IsCancellationRequested)
        {
        }
    }

}
