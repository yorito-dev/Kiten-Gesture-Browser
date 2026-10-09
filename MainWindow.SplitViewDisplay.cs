using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : ISplitViewDisplayHost
{
    IList<BrowserTab> ISplitViewDisplayHost.Tabs => _tabs;
    BrowserTab? ISplitViewDisplayHost.DisplayedTab => _displayedTab;
    Guid ISplitViewDisplayHost.WindowId => _workspaceWindowId;
    bool ISplitViewDisplayHost.IsClosing => _isClosingWindow;
    bool ISplitViewDisplayHost.CanOpenInSplitView(BrowserTab? tab) => CanOpenInSplitView(tab);
    bool ISplitViewDisplayHost.IsSplitPaneContentReady(BrowserTab? tab) => IsSplitPaneContentReady(tab);
    Task ISplitViewDisplayHost.EnsureTabLoadedAsync(BrowserTab tab, string? initialUrl) => EnsureTabLoadedAsync(tab, initialUrl);
    WebView2? ISplitViewDisplayHost.GetWebView(BrowserTab? tab) => GetTabWebView(tab);
    Func<string?>? ISplitViewDisplayHost.GetWebViewCoreSourceReader(BrowserTab tab)
    {
        var core = GetTabWebView(tab)?.CoreWebView2;
        return core is null ? null : () => core.Source;
    }
    UIElement? ISplitViewDisplayHost.GetHostedContent(BrowserTab tab) => GetTabHostedContent(tab) as UIElement;
    bool ISplitViewDisplayHost.EnsureWebViewHostedIn(WebView2 webView, Panel host) => EnsureWebViewHostedIn(webView, host);
    void ISplitViewDisplayHost.DetachHostedContent(UIElement content) => DetachHostedContent(content);
    void ISplitViewDisplayHost.MoveWebViewToBackground(BrowserTab tab) => MoveWebViewToBackground(tab);
    void ISplitViewDisplayHost.MoveTabContentToBackground(BrowserTab tab) => MoveTabContentToBackground(tab);
    void ISplitViewDisplayHost.ResumeInactiveYouTubeTabForDisplay(BrowserTab tab) => ResumeInactiveYouTubeTabForDisplay(tab);
    void ISplitViewDisplayHost.HidePlaceholder() => HidePlaceholder();
    void ISplitViewDisplayHost.CloseBookmarkPreview() => CloseBookmarkPreview(clearNavigation: true);
    void ISplitViewDisplayHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void ISplitViewDisplayHost.ShowSingleTabContent(BrowserTab tab) => ShowSingleTabContent(tab);
    void ISplitViewDisplayHost.SetAddressText(string text) => SetAddressText(text);
    void ISplitViewDisplayHost.SetDisplayedTab(BrowserTab? tab) => _displayedTab = tab;
    void ISplitViewDisplayHost.SetLastActiveBrowserTabId(Guid tabId) => _lastActiveBrowserTabId = tabId;
    void ISplitViewDisplayHost.QueueFiveChLiveReadabilityActivation(BrowserTab tab, bool isActive) => QueueFiveChLiveReadabilityActivation(tab, isActive);
    void ISplitViewDisplayHost.UpdateSplitYouTubeControlPanels(string reason) => UpdateSplitYouTubeControlPanels(reason);
    void ISplitViewDisplayHost.HideSplitYouTubeControlPopups() => HideSplitYouTubeControlPopups();
    void ISplitViewDisplayHost.QueueSplitYouTubeControlPanelRefresh() => QueueSplitYouTubeControlPanelRefresh();
    void ISplitViewDisplayHost.RefreshVerticalTabViews() => RefreshVerticalTabViews();
    void ISplitViewDisplayHost.UpdateToolbarState() => UpdateToolbarState();
    void ISplitViewDisplayHost.QueueSessionSave() => QueueSessionSave();
    void ISplitViewDisplayHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();
    void ISplitViewDisplayHost.PersistSplitViewState() => PersistSplitViewState();
    void ISplitViewDisplayHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void ISplitViewDisplayHost.AppendDiagnostic(string operation, BrowserTab? leftTab, BrowserTab? rightTab, BrowserTab? tab, string? pane, string? detail)
        => AppendSplitViewDiagnostic(operation, leftTab, rightTab, tab, pane, detail);
    string ISplitViewDisplayHost.SummarizeDiagnosticUrl(string url) => SummarizeDiagnosticUrl(url);
    void ISplitViewDisplayHost.NavigateCore(BrowserTab tab, string url) => NavigateCore(tab, url);
    Size ISplitViewDisplayHost.GetRawWebHostSize() => new(WebHost.ActualWidth, WebHost.ActualHeight);
    string ISplitViewDisplayHost.NormalizeYouTubeShortsUrl(string url) => NormalizeYouTubeShortsUrl(url);
    bool ISplitViewDisplayHost.IsNewTabUrl(string? url) => IsNewTabUrl(url);
    bool ISplitViewDisplayHost.IsBlankUrl(string? url) => IsBlankUrl(url);
    bool ISplitViewDisplayHost.IsGeneratedNewTabSource(string? url) => IsGeneratedNewTabSource(url);
    void ISplitViewDisplayHost.FocusFolderTabShortcutScope(BrowserTab tab)
    {
        if (GetTabHostedContent(tab) is Views.FolderTabView folderTabView)
        {
            folderTabView.FocusShortcutScope();
        }
    }
    void ISplitViewDisplayHost.RunSupervised(string operation, Func<CancellationToken, Task> action)
        => _windowTaskSupervisor.TryRun(operation, action);
}
