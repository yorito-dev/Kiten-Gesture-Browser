using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record SplitViewDisplayElements(
    Grid Host,
    Grid PaneLayoutGrid,
    Grid LeftPaneHost,
    Grid RightPaneHost,
    RowDefinition TopRow,
    RowDefinition DividerRow,
    RowDefinition BottomRow,
    ColumnDefinition LeftColumn,
    ColumnDefinition DividerColumn,
    ColumnDefinition RightColumn,
    Border LeftPaneBorder,
    Border RightPaneBorder,
    Thumb ResizeThumb,
    TextBlock StatusText,
    Button SwapButton,
    Button ToggleOrientationButton,
    Button CloseLeftButton,
    Button CloseRightButton);

internal interface ISplitViewDisplayHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? DisplayedTab { get; }
    Guid WindowId { get; }
    bool IsClosing { get; }
    bool CanOpenInSplitView(BrowserTab? tab);
    bool IsSplitPaneContentReady(BrowserTab? tab);
    Task EnsureTabLoadedAsync(BrowserTab tab, string? initialUrl);
    WebView2? GetWebView(BrowserTab? tab);
    Func<string?>? GetWebViewCoreSourceReader(BrowserTab tab);
    UIElement? GetHostedContent(BrowserTab tab);
    bool EnsureWebViewHostedIn(WebView2 webView, Panel host);
    void DetachHostedContent(UIElement content);
    void MoveWebViewToBackground(BrowserTab tab);
    void MoveTabContentToBackground(BrowserTab tab);
    void ResumeInactiveYouTubeTabForDisplay(BrowserTab tab);
    void HidePlaceholder();
    void CloseBookmarkPreview();
    void SelectTab(BrowserTab tab);
    void ShowSingleTabContent(BrowserTab tab);
    void SetAddressText(string text);
    void SetDisplayedTab(BrowserTab? tab);
    void SetLastActiveBrowserTabId(Guid tabId);

    void UpdateSplitYouTubeControlPanels(string reason);
    void HideSplitYouTubeControlPopups();
    void QueueSplitYouTubeControlPanelRefresh();
    void RefreshVerticalTabViews();
    void UpdateToolbarState();
    void QueueSessionSave();
    void QueueWorkspaceOpenTabsRefresh();
    void PersistSplitViewState();
    void SetStatus(string message, bool autoClear);
    void AppendDiagnostic(string operation, BrowserTab? leftTab = null, BrowserTab? rightTab = null,
        BrowserTab? tab = null, string? pane = null, string? detail = null);
    string SummarizeDiagnosticUrl(string url);
    void NavigateCore(BrowserTab tab, string url);
    Size GetRawWebHostSize();
    string NormalizeYouTubeShortsUrl(string url);
    bool IsNewTabUrl(string? url);
    bool IsBlankUrl(string? url);
    bool IsGeneratedNewTabSource(string? url);
    void FocusFolderTabShortcutScope(BrowserTab tab);
    void RunSupervised(string operation, Func<CancellationToken, Task> action);
}

internal sealed class SplitViewDisplayController(
    ISplitViewDisplayHost host,
    SplitViewDisplayElements elements,
    SplitViewService splitViewService,
    SplitViewWindowStateCoordinator stateCoordinator)
{
    private const int DelayedRefreshMilliseconds = 180;

    public async Task StartAsync(
        BrowserTab leftTab,
        BrowserTab rightTab,
        string orientation,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!host.Tabs.Contains(leftTab) || !host.Tabs.Contains(rightTab))
        {
            host.SetStatus("分割表示するタブが見つかりません", autoClear: true);
            return;
        }

        if (!host.CanOpenInSplitView(leftTab) || !host.CanOpenInSplitView(rightTab))
        {
            host.SetStatus("分割表示は Web タブまたはフォルダータブで使えます", autoClear: true);
            return;
        }

        if (leftTab.Id == rightTab.Id)
        {
            host.SetStatus("同じタブは分割できません", autoClear: true);
            return;
        }

        if (splitViewService.IsActive && elements.Host.Visibility == Visibility.Visible)
        {
            HideForSingleTab(leftTab);
        }

        var normalizedOrientation = SplitViewModelProfile.NormalizeOrientation(orientation);
        if (!splitViewService.TryStart(host.WindowId, leftTab, rightTab, host.Tabs, normalizedOrientation, out _))
        {
            host.SetStatus("同じタブは分割できません", autoClear: true);
            return;
        }

        host.AppendDiagnostic("split-view-start", leftTab, rightTab,
            detail: $"orientation={normalizedOrientation}");
        host.CloseBookmarkPreview();
        host.SelectTab(leftTab);
        host.SetAddressText(leftTab.Url ?? string.Empty);
        host.SetLastActiveBrowserTabId(leftTab.Id);

        if (!await EnsureTabsLoadedAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ClearForSingleTab(leftTab);
            host.SetStatus("分割表示に必要な内容を準備できませんでした", autoClear: true);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ShowActive();
        host.PersistSplitViewState();
        host.RefreshVerticalTabViews();
        host.UpdateToolbarState();
        host.QueueSessionSave();
        host.QueueWorkspaceOpenTabsRefresh();
        host.SetStatus(
            normalizedOrientation == SplitViewOrientation.TopBottom
                ? "上下分割表示にしました"
                : "左右分割表示にしました",
            autoClear: true);
    }

    public async Task<bool> EnsureTabsLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (splitViewService.ActiveSplitView is null)
        {
            return false;
        }

        var leftTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Left);
        var rightTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Right);
        if (leftTab is null || rightTab is null)
        {
            return false;
        }

        host.AppendDiagnostic("split-view-load-begin", leftTab, rightTab);
        using (stateCoordinator.EnterPreparation())
        {
            if (!leftTab.IsFolderTab)
            {
                await host.EnsureTabLoadedAsync(leftTab, leftTab.Url);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            host.AppendDiagnostic("split-view-load-left-ready", leftTab, rightTab);
            if (!rightTab.IsFolderTab)
            {
                await host.EnsureTabLoadedAsync(rightTab, rightTab.Url);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            host.AppendDiagnostic("split-view-load-right-ready", leftTab, rightTab);
        }

        host.AppendDiagnostic("split-view-load-end", leftTab, rightTab);
        return host.IsSplitPaneContentReady(leftTab) && host.IsSplitPaneContentReady(rightTab);
    }

    public void PlaceWebViewForPreparation(BrowserTab tab)
    {
        if (host.GetWebView(tab) is not { } webView
            || !splitViewService.TryGetPane(tab, out var pane))
        {
            return;
        }

        EnsureHostVisibleForPreparation();
        var paneHost = pane == SplitViewPane.Right
            ? elements.RightPaneHost
            : elements.LeftPaneHost;
        host.EnsureWebViewHostedIn(webView, paneHost);
        ParkInViewport(webView);
        Panel.SetZIndex(webView, 1);
        PrepareForegroundWebView(webView);
        ApplyPreparationPaneWebViewSize(webView, pane);
        host.AppendDiagnostic("split-view-prep-place", tab: tab, pane: pane);
    }

    public bool ShowActive()
    {
        if (splitViewService.ActiveSplitView is not { } splitView)
        {
            return false;
        }

        var leftTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Left);
        var rightTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Right);
        if (leftTab is null || rightTab is null
            || !host.IsSplitPaneContentReady(leftTab) || !host.IsSplitPaneContentReady(rightTab))
        {
            return false;
        }

        // 現在の単独表示を退避する対象は、ペインを再配置する前に確定する。
        var previousTab = host.DisplayedTab;
        host.ResumeInactiveYouTubeTabForDisplay(leftTab);
        host.ResumeInactiveYouTubeTabForDisplay(rightTab);
        MoveNonSplitWebViewsToBackground(leftTab, rightTab);
        ApplyRatio();
        elements.Host.Visibility = Visibility.Visible;
        host.HidePlaceholder();
        elements.Host.UpdateLayout();
        RefreshPlacement(kickBlankNavigation: false);

        var activeTab = splitViewService.GetActiveTab(host.Tabs) ?? leftTab;
        host.SetDisplayedTab(activeTab);
        if (previousTab is not null && !ReferenceEquals(previousTab, leftTab) && !ReferenceEquals(previousTab, rightTab))
        {
            host.MoveWebViewToBackground(previousTab);
        }
        QueueDisplayRefresh();
        host.UpdateSplitYouTubeControlPanels("show-active");
        host.QueueSplitYouTubeControlPanelRefresh();
        splitView.UpdatedAt = DateTimeOffset.Now;
        RefreshTabVisualStates();
        host.AppendDiagnostic("split-view-show-active", leftTab, rightTab);
        return true;
    }

    public void RefreshPlacement(bool kickBlankNavigation = false)
    {
        var leftTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Left);
        var rightTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Right);
        if (!host.IsSplitPaneContentReady(leftTab) || !host.IsSplitPaneContentReady(rightTab))
        {
            return;
        }

        if (!EnsurePaneContentHosted(leftTab!, elements.LeftPaneHost)
            || !EnsurePaneContentHosted(rightTab!, elements.RightPaneHost))
        {
            return;
        }

        UpdateActivePaneVisuals();
        ApplyWebViewSizes();
        host.AppendDiagnostic("split-view-refresh-placement", leftTab, rightTab,
            detail: $"kickBlankNavigation={kickBlankNavigation}");
        if (!kickBlankNavigation)
        {
            return;
        }

        if (!leftTab!.IsFolderTab)
        {
            EnsureNavigationStarted(leftTab);
        }
        if (!rightTab!.IsFolderTab)
        {
            EnsureNavigationStarted(rightTab);
        }
    }

    public void QueueDisplayRefresh()
    {
        var generation = stateCoordinator.BeginDisplayRefresh();
        QueueDisplayRefresh(generation, DispatcherPriority.Render);
        QueueDisplayRefresh(generation, DispatcherPriority.Loaded);
        QueueDisplayRefresh(generation, DispatcherPriority.ContextIdle);
        host.RunSupervised("split-view-display-refresh-delay",
            cancellationToken => RefreshAfterDelayAsync(generation, cancellationToken));
    }

    public void HideForSingleTab(BrowserTab? tabToKeepVisible)
    {
        if (elements.Host.Visibility != Visibility.Visible)
        {
            splitViewService.DeactivateActive();
            RefreshTabVisualStates();
            return;
        }

        var splitTabs = GetActiveSplitTabs().ToList();
        elements.Host.Visibility = Visibility.Collapsed;
        host.HideSplitYouTubeControlPopups();
        foreach (var splitTab in splitTabs)
        {
            if (!ReferenceEquals(splitTab, tabToKeepVisible))
            {
                host.MoveTabContentToBackground(splitTab);
            }
        }

        splitViewService.DeactivateActive();
        RefreshTabVisualStates();
    }

    public void ClearForSingleTab(BrowserTab? tabToKeepVisible)
    {
        if (!splitViewService.IsActive && elements.Host.Visibility != Visibility.Visible)
        {
            return;
        }

        var splitTabs = GetActiveSplitTabs().ToList();
        splitViewService.Clear(host.Tabs);
        elements.Host.Visibility = Visibility.Collapsed;
        host.HideSplitYouTubeControlPopups();
        foreach (var splitTab in splitTabs)
        {
            if (!ReferenceEquals(splitTab, tabToKeepVisible))
            {
                host.MoveTabContentToBackground(splitTab);
            }
        }

        host.PersistSplitViewState();
        RefreshTabVisualStates();
        host.RefreshVerticalTabViews();
        host.QueueSessionSave();
    }

    public void Detach(BrowserTab? tabToShow)
    {
        ClearForSingleTab(tabToShow);
        if (tabToShow is null)
        {
            return;
        }

        host.SelectTab(tabToShow);
        host.ShowSingleTabContent(tabToShow);
        host.SetAddressText(host.IsNewTabUrl(tabToShow.Url) ? string.Empty : tabToShow.Url ?? string.Empty);
        host.UpdateToolbarState();
        host.SetStatus("分割表示を解除しました", autoClear: true);
    }

    public void ApplyRatio()
    {
        var ratio = Math.Clamp(
            splitViewService.ActiveSplitView?.Ratio ?? 0.5,
            SplitViewModelProfile.MinimumRatio,
            SplitViewModelProfile.MaximumRatio);
        if (IsTopBottom())
        {
            ApplyTopBottomLayout(ratio);
            return;
        }

        ApplyLeftRightLayout(ratio);
    }

    public void RefreshTabVisualStates()
    {
        var splitViewIds = host.Tabs
            .Select(static tab => tab.SplitViewId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        stateCoordinator.RefreshAccentSlots(
            splitViewIds,
            BrowserTab.SplitViewAccentPaletteSize);

        foreach (var tab in host.Tabs)
        {
            tab.IsActiveSplitViewTab = splitViewService.Contains(tab);
            tab.SplitViewAccentIndex = tab.SplitViewId is { Length: > 0 } splitViewId
                && stateCoordinator.TryGetAccentSlot(splitViewId, out var slot)
                    ? slot
                    : -1;
        }
    }

    public void ApplyWebViewSizes()
    {
        var leftTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Left);
        var rightTab = splitViewService.GetTabForPane(host.Tabs, SplitViewPane.Right);
        var webHostSize = host.GetRawWebHostSize();
        var fallbackWidth = elements.PaneLayoutGrid.ActualWidth > 0
            ? elements.PaneLayoutGrid.ActualWidth
            : Math.Max(1, webHostSize.Width);
        var fallbackHeight = elements.PaneLayoutGrid.ActualHeight > 0
            ? elements.PaneLayoutGrid.ActualHeight
            : Math.Max(1, webHostSize.Height - 42);
        var ratio = splitViewService.ActiveSplitView?.Ratio ?? 0.5;
        if (IsTopBottom())
        {
            var topSize = new Size(
                elements.LeftPaneHost.ActualWidth > 0 ? elements.LeftPaneHost.ActualWidth : fallbackWidth,
                elements.LeftPaneHost.ActualHeight > 0 ? elements.LeftPaneHost.ActualHeight : Math.Max(1, fallbackHeight * ratio - 14));
            var bottomSize = new Size(
                elements.RightPaneHost.ActualWidth > 0 ? elements.RightPaneHost.ActualWidth : fallbackWidth,
                elements.RightPaneHost.ActualHeight > 0 ? elements.RightPaneHost.ActualHeight : Math.Max(1, fallbackHeight * (1 - ratio) - 14));

            if (host.GetWebView(leftTab) is { } leftWebView)
            {
                ApplyWebViewSize(leftWebView, topSize);
            }
            if (host.GetWebView(rightTab) is { } rightWebView)
            {
                ApplyWebViewSize(rightWebView, bottomSize);
            }
            return;
        }

        var leftSize = new Size(
            elements.LeftPaneHost.ActualWidth > 0 ? elements.LeftPaneHost.ActualWidth : Math.Max(1, fallbackWidth * ratio - 14),
            elements.LeftPaneHost.ActualHeight > 0 ? elements.LeftPaneHost.ActualHeight : fallbackHeight);
        var rightSize = new Size(
            elements.RightPaneHost.ActualWidth > 0 ? elements.RightPaneHost.ActualWidth : Math.Max(1, fallbackWidth * (1 - ratio) - 14),
            elements.RightPaneHost.ActualHeight > 0 ? elements.RightPaneHost.ActualHeight : fallbackHeight);

        if (host.GetWebView(leftTab) is { } leftWebView2)
        {
            ApplyWebViewSize(leftWebView2, leftSize);
        }
        if (host.GetWebView(rightTab) is { } rightWebView2)
        {
            ApplyWebViewSize(rightWebView2, rightSize);
        }
    }

    public void ActivatePane(string pane)
    {
        var paneChanged = splitViewService.ActiveSplitView?.ActivePane != pane;
        if (!splitViewService.TrySetActivePane(pane))
        {
            return;
        }

        var tab = splitViewService.GetTabForPane(host.Tabs, pane);
        if (tab is null)
        {
            return;
        }

        host.SelectTab(tab);
        host.SetDisplayedTab(tab);
        host.SetLastActiveBrowserTabId(tab.Id);
        host.SetAddressText(host.IsNewTabUrl(tab.Url) ? string.Empty : tab.Url ?? string.Empty);
        UpdateActivePaneVisuals();
        host.UpdateToolbarState();
        host.FocusFolderTabShortcutScope(tab);
        if (paneChanged)
        {
            host.PersistSplitViewState();
            host.QueueSessionSave();
        }
    }

    public void ToggleOrientation()
    {
        if (splitViewService.ActiveSplitView is null)
        {
            return;
        }

        var nextOrientation = IsTopBottom() ? SplitViewOrientation.LeftRight : SplitViewOrientation.TopBottom;
        if (!splitViewService.TrySetOrientation(nextOrientation, host.Tabs))
        {
            return;
        }

        ApplyRatio();
        elements.Host.UpdateLayout();
        RefreshPlacement();
        QueueDisplayRefresh();
        host.QueueSplitYouTubeControlPanelRefresh();
        host.PersistSplitViewState();
        host.RefreshVerticalTabViews();
        host.UpdateToolbarState();
        host.QueueSessionSave();
        host.QueueWorkspaceOpenTabsRefresh();
        host.SetStatus(nextOrientation == SplitViewOrientation.TopBottom
            ? "上下分割表示に切り替えました" : "左右分割表示に切り替えました", autoClear: true);
    }

    public void SwapPanes()
    {
        if (!splitViewService.TrySwapPanes())
        {
            return;
        }

        ShowActive();
        if (splitViewService.GetActiveTab(host.Tabs) is { } activeTab)
        {
            host.SelectTab(activeTab);
            host.SetAddressText(host.IsNewTabUrl(activeTab.Url) ? string.Empty : activeTab.Url ?? string.Empty);
        }

        host.PersistSplitViewState();
        host.RefreshVerticalTabViews();
        host.UpdateToolbarState();
        host.QueueSessionSave();
    }

    public void ApplyResize(double horizontalChange, double verticalChange)
    {
        if (splitViewService.ActiveSplitView is null)
        {
            return;
        }

        if (IsTopBottom())
        {
            var total = Math.Max(1, elements.TopRow.ActualHeight + elements.BottomRow.ActualHeight);
            splitViewService.TrySetRatio(Math.Clamp(elements.TopRow.ActualHeight + verticalChange,
                total * SplitViewModelProfile.MinimumRatio, total * SplitViewModelProfile.MaximumRatio) / total, host.Tabs);
        }
        else
        {
            var total = Math.Max(1, elements.LeftColumn.ActualWidth + elements.RightColumn.ActualWidth);
            splitViewService.TrySetRatio(Math.Clamp(elements.LeftColumn.ActualWidth + horizontalChange,
                total * SplitViewModelProfile.MinimumRatio, total * SplitViewModelProfile.MaximumRatio) / total, host.Tabs);
        }

        ApplyRatio();
        elements.Host.UpdateLayout();
        ApplyWebViewSizes();
    }

    public bool ResetRatio(Action? onRatioAccepted = null)
    {
        if (!splitViewService.TrySetRatio(SplitViewModelProfile.DefaultRatio, host.Tabs))
        {
            return false;
        }

        onRatioAccepted?.Invoke();
        ApplyRatio();
        elements.Host.UpdateLayout();
        ApplyWebViewSizes();
        return true;
    }

    public bool IsTopBottom()
        => string.Equals(splitViewService.ActiveSplitView?.Orientation, SplitViewOrientation.TopBottom,
            StringComparison.OrdinalIgnoreCase);

    private async Task RefreshAfterDelayAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DelayedRefreshMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested && IsCurrentRefresh(generation))
        {
            RefreshPlacement(kickBlankNavigation: true);
        }
    }

    private void QueueDisplayRefresh(int generation, DispatcherPriority priority)
    {
        elements.Host.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsCurrentRefresh(generation))
            {
                RefreshPlacement();
            }
        }), priority);
    }

    private bool IsCurrentRefresh(int generation)
        => stateCoordinator.IsCurrentDisplayRefresh(generation)
            && !host.IsClosing && splitViewService.IsActive && elements.Host.Visibility == Visibility.Visible;

    private void MoveNonSplitWebViewsToBackground(BrowserTab leftTab, BrowserTab rightTab)
    {
        foreach (var tab in host.Tabs)
        {
            if (!ReferenceEquals(tab, leftTab) && !ReferenceEquals(tab, rightTab) && host.GetWebView(tab) is not null)
            {
                host.MoveWebViewToBackground(tab);
            }
        }
    }

    private bool EnsurePaneContentHosted(BrowserTab tab, Panel paneHost)
    {
        if (tab.IsFolderTab && host.GetHostedContent(tab) is { } content)
        {
            host.DetachHostedContent(content);
            if (!paneHost.Children.Contains(content))
            {
                try
                {
                    paneHost.Children.Add(content);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }
            content.Visibility = Visibility.Visible;
            content.IsHitTestVisible = true;
            Panel.SetZIndex(content, 1);
            return true;
        }

        if (host.GetWebView(tab) is not { } webView || !host.EnsureWebViewHostedIn(webView, paneHost))
        {
            return false;
        }

        ParkInViewport(webView);
        Panel.SetZIndex(webView, 1);
        PrepareForegroundWebView(webView);
        return true;
    }

    private void EnsureHostVisibleForPreparation()
    {
        if (elements.Host.Visibility == Visibility.Visible)
        {
            return;
        }

        ApplyRatio();
        elements.Host.Visibility = Visibility.Visible;
        host.HidePlaceholder();
        elements.Host.UpdateLayout();
        host.AppendDiagnostic("split-view-prep-host-visible");
    }

    private void ApplyPreparationPaneWebViewSize(WebView2 webView, string pane)
    {
        var paneHost = pane == SplitViewPane.Right
            ? elements.RightPaneHost
            : elements.LeftPaneHost;
        var ratio = splitViewService.ActiveSplitView?.Ratio ?? 0.5;
        var rawWebHostSize = host.GetRawWebHostSize();
        var fallbackWidth = elements.PaneLayoutGrid.ActualWidth > 0
            ? elements.PaneLayoutGrid.ActualWidth
            : Math.Max(1, rawWebHostSize.Width);
        var fallbackHeight = elements.PaneLayoutGrid.ActualHeight > 0
            ? elements.PaneLayoutGrid.ActualHeight
            : Math.Max(1, rawWebHostSize.Height - 42);
        Size size;
        if (IsTopBottom())
        {
            var heightRatio = pane == SplitViewPane.Right ? 1 - ratio : ratio;
            size = new Size(
                paneHost.ActualWidth > 0 ? paneHost.ActualWidth : fallbackWidth,
                paneHost.ActualHeight > 0 ? paneHost.ActualHeight : Math.Max(1, fallbackHeight * heightRatio - 14));
        }
        else
        {
            var widthRatio = pane == SplitViewPane.Right ? 1 - ratio : ratio;
            size = new Size(
                paneHost.ActualWidth > 0 ? paneHost.ActualWidth : Math.Max(1, fallbackWidth * widthRatio - 14),
                paneHost.ActualHeight > 0 ? paneHost.ActualHeight : fallbackHeight);
        }

        ApplyWebViewSize(webView, size);
    }

    private void EnsureNavigationStarted(BrowserTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Url))
        {
            host.AppendDiagnostic("split-view-kick-skip", tab: tab, detail: "reason=no-url");
            return;
        }
        var sourceReader = host.GetWebViewCoreSourceReader(tab);
        if (sourceReader is null)
        {
            host.AppendDiagnostic("split-view-kick-skip", tab: tab, detail: "reason=no-core");
            return;
        }
        if (tab.IsLoading)
        {
            host.AppendDiagnostic("split-view-kick-skip", tab: tab, detail: "reason=loading");
            return;
        }
        if (host.IsNewTabUrl(tab.Url) || host.IsBlankUrl(tab.Url) || host.IsGeneratedNewTabSource(tab.Url))
        {
            host.AppendDiagnostic("split-view-kick-skip", tab: tab, detail: "reason=internal-url");
            return;
        }
        var targetUrl = host.NormalizeYouTubeShortsUrl(tab.Url);
        var source = sourceReader();
        if (!string.IsNullOrWhiteSpace(source) && !host.IsBlankUrl(source) && !host.IsGeneratedNewTabSource(source))
        {
            host.AppendDiagnostic("split-view-kick-skip", tab: tab, detail: "reason=source-present");
            return;
        }
        host.AppendDiagnostic("split-view-kick-navigate", tab: tab,
            detail: "target=" + host.SummarizeDiagnosticUrl(targetUrl));
        host.NavigateCore(tab, targetUrl);
    }

    private void UpdateActivePaneVisuals()
    {
        var activePane = splitViewService.ActiveSplitView?.ActivePane ?? SplitViewPane.Left;
        var topBottom = IsTopBottom();
        var activeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(138, 180, 248));
        var inactiveBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81));
        elements.LeftPaneBorder.BorderBrush = activePane == SplitViewPane.Left ? activeBrush : inactiveBrush;
        elements.RightPaneBorder.BorderBrush = activePane == SplitViewPane.Right ? activeBrush : inactiveBrush;
        elements.StatusText.Text = topBottom
            ? activePane == SplitViewPane.Right ? "上下分割: 下ペイン" : "上下分割: 上ペイン"
            : activePane == SplitViewPane.Right ? "左右分割: 右ペイン" : "左右分割: 左ペイン";
        elements.SwapButton.Content = topBottom ? "上下入替" : "左右入替";
        elements.SwapButton.ToolTip = topBottom ? "上下のペインを入れ替え" : "左右のペインを入れ替え";
        elements.ToggleOrientationButton.Content = topBottom ? "左右分割へ" : "上下分割へ";
        elements.ToggleOrientationButton.ToolTip = topBottom ? "左右分割に切り替え" : "上下分割に切り替え";
        elements.CloseLeftButton.Content = topBottom ? "上を閉じる" : "左を閉じる";
        elements.CloseLeftButton.ToolTip = topBottom ? "上ペインのタブを閉じる" : "左ペインのタブを閉じる";
        elements.CloseRightButton.Content = topBottom ? "下を閉じる" : "右を閉じる";
        elements.CloseRightButton.ToolTip = topBottom ? "下ペインのタブを閉じる" : "右ペインのタブを閉じる";
    }

    private void ApplyLeftRightLayout(double ratio)
    {
        elements.TopRow.Height = new GridLength(1, GridUnitType.Star);
        elements.DividerRow.Height = new GridLength(0);
        elements.BottomRow.Height = new GridLength(0);
        elements.LeftColumn.Width = new GridLength(ratio, GridUnitType.Star);
        elements.DividerColumn.Width = new GridLength(6);
        elements.RightColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
        Grid.SetRow(elements.LeftPaneBorder, 0);
        Grid.SetColumn(elements.LeftPaneBorder, 0);
        Grid.SetRowSpan(elements.LeftPaneBorder, 1);
        Grid.SetColumnSpan(elements.LeftPaneBorder, 1);
        elements.LeftPaneBorder.Margin = new Thickness(0, 8, 4, 0);
        Grid.SetRow(elements.ResizeThumb, 0);
        Grid.SetColumn(elements.ResizeThumb, 1);
        Grid.SetRowSpan(elements.ResizeThumb, 1);
        Grid.SetColumnSpan(elements.ResizeThumb, 1);
        elements.ResizeThumb.Margin = new Thickness(0, 8, 0, 0);
        elements.ResizeThumb.Width = double.NaN;
        elements.ResizeThumb.Height = double.NaN;
        elements.ResizeThumb.Cursor = Cursors.SizeWE;
        Grid.SetRow(elements.RightPaneBorder, 0);
        Grid.SetColumn(elements.RightPaneBorder, 2);
        Grid.SetRowSpan(elements.RightPaneBorder, 1);
        Grid.SetColumnSpan(elements.RightPaneBorder, 1);
        elements.RightPaneBorder.Margin = new Thickness(4, 8, 0, 0);
    }

    private void ApplyTopBottomLayout(double ratio)
    {
        elements.TopRow.Height = new GridLength(ratio, GridUnitType.Star);
        elements.DividerRow.Height = new GridLength(6);
        elements.BottomRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
        elements.LeftColumn.Width = new GridLength(1, GridUnitType.Star);
        elements.DividerColumn.Width = new GridLength(0);
        elements.RightColumn.Width = new GridLength(0);
        Grid.SetRow(elements.LeftPaneBorder, 0);
        Grid.SetColumn(elements.LeftPaneBorder, 0);
        Grid.SetRowSpan(elements.LeftPaneBorder, 1);
        Grid.SetColumnSpan(elements.LeftPaneBorder, 3);
        elements.LeftPaneBorder.Margin = new Thickness(0, 8, 0, 4);
        Grid.SetRow(elements.ResizeThumb, 1);
        Grid.SetColumn(elements.ResizeThumb, 0);
        Grid.SetRowSpan(elements.ResizeThumb, 1);
        Grid.SetColumnSpan(elements.ResizeThumb, 3);
        elements.ResizeThumb.Margin = new Thickness(0);
        elements.ResizeThumb.Width = double.NaN;
        elements.ResizeThumb.Height = double.NaN;
        elements.ResizeThumb.Cursor = Cursors.SizeNS;
        Grid.SetRow(elements.RightPaneBorder, 2);
        Grid.SetColumn(elements.RightPaneBorder, 0);
        Grid.SetRowSpan(elements.RightPaneBorder, 1);
        Grid.SetColumnSpan(elements.RightPaneBorder, 3);
        elements.RightPaneBorder.Margin = new Thickness(0, 4, 0, 0);
    }

    private IEnumerable<BrowserTab> GetActiveSplitTabs()
    {
        if (splitViewService.ActiveSplitView is not { } splitView)
        {
            yield break;
        }

        foreach (var tab in host.Tabs)
        {
            if (tab.Id == splitView.LeftTabId || tab.Id == splitView.RightTabId)
            {
                yield return tab;
            }
        }
    }

    private static void PrepareForegroundWebView(WebView2 webView)
    {
        webView.Visibility = Visibility.Visible;
        webView.IsHitTestVisible = true;
        webView.InvalidateMeasure();
        webView.InvalidateArrange();
        webView.UpdateLayout();
    }

    private static void ParkInViewport(WebView2 webView) => webView.Margin = new Thickness(0);

    private static void ApplyWebViewSize(WebView2? webView, Size size)
    {
        if (webView is null)
        {
            return;
        }

        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        if (Math.Abs(webView.Width - width) < 0.5 && Math.Abs(webView.Height - height) < 0.5)
        {
            return;
        }

        webView.Width = width;
        webView.Height = height;
    }
}
