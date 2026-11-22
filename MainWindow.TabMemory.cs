using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow : ITabMemoryEvaluationHost
{
    private void RestartTabMemoryManagementTimer()
    {
        _tabMemoryManagementTimer.Stop();
        if (!_isInitialized
            || _isClosingWindow
            || _isPrivateWindow
            || !_settings.EnableInactiveTabMemorySaver)
        {
            return;
        }

        _tabMemoryManagementTimer.Start();
    }

    private async Task PrewarmHoveredTabAsync()
    {
        _tabHoverPrewarmTimer.Stop();
        var tab = _pendingTabHoverPrewarm;
        var element = _pendingTabHoverElement;
        _pendingTabHoverPrewarm = null;
        _pendingTabHoverElement = null;

        if (tab is null
            || element?.IsMouseOver != true)
        {
            return;
        }

        await _tabThumbnailCaptureController.PrewarmAsync(tab);
    }

    private Task EvaluateInactiveTabMemoryAsync() => _tabMemoryEvaluationController.EvaluateAsync();

    bool ITabMemoryEvaluationHost.CanStartEvaluation()
        => _settings.EnableInactiveTabMemorySaver
            && !_isClosingWindow
            && !_isPrivateWindow
            && IsPrimaryTabMemoryManagementWindow();
    bool ITabMemoryEvaluationHost.IsMemorySaverEnabled => _settings.EnableInactiveTabMemorySaver;
    bool ITabMemoryEvaluationHost.IsClosing => _isClosingWindow;
    BrowserTab? ITabMemoryEvaluationHost.ActiveTab => ActiveTab;
    long ITabMemoryEvaluationHost.TotalAvailableMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    IReadOnlyList<ITabMemoryEvaluationWindow> ITabMemoryEvaluationHost.GetOpenWindows() => GetOpenMainWindows();
    IReadOnlyList<TabMemorySnapshot> ITabMemoryEvaluationHost.CaptureGlobalTabMemorySnapshots() => CaptureGlobalTabMemorySnapshots();
    long ITabMemoryEvaluationHost.CaptureProcessPrivateMemoryBytes(IEnumerable<WebViewProcessDiagnosticInfo> processes)
        => RuntimeDiagnosticService.CaptureWebViewProcessMetrics(processes).PrivateMemoryBytes;
    bool ITabMemoryEvaluationHost.IsInitializing(BrowserTab tab) => _webViewLifecycleService.IsInitializing(tab);
    bool ITabMemoryEvaluationHost.HasLoadedRuntime(BrowserTab tab) => _webViewLifecycleService.HasLoadedRuntime(tab);
    bool ITabMemoryEvaluationHost.IsVisibleSplitTab(BrowserTab tab)
        => SplitViewHost.Visibility == Visibility.Visible && _splitViewService.Contains(tab);
    IReadOnlySet<Guid> ITabMemoryEvaluationHost.GetGlobalHotTabIds() => GetGlobalHotTabIds();
    Task<bool> ITabMemoryEvaluationHost.TrySuspendTabAsync(BrowserTab tab)
        => TrySuspendTabAsync(tab, WebViewSuspendRoute.MemoryPressure);
    bool ITabMemoryEvaluationHost.ResumeTabIfSuspended(BrowserTab tab) => ResumeTabIfSuspended(tab);
    IList<BrowserTab> ITabMemoryEvaluationWindow.Tabs => _tabs;
    bool ITabMemoryEvaluationWindow.IsRightWheelTabCyclingActive() => IsRightWheelTabCyclingActive();
    bool ITabMemoryEvaluationWindow.HasActiveDownloadForMemoryManagement() => HasActiveDownloadForMemoryManagement();
    IReadOnlyList<WebViewProcessDiagnosticInfo> ITabMemoryEvaluationWindow.GetProcessDiagnosticInfos()
        => _webViewEnvironmentService.GetProcessDiagnosticInfos();
    Task<bool> ITabMemoryEvaluationWindow.TrySuspendColdInactiveTabAsync(BrowserTab tab)
        => _tabMemoryEvaluationController.TrySuspendColdInactiveTabAsync(tab);

    private bool IsPrimaryTabMemoryManagementWindow()
    {
        var windows = GetOpenMainWindows()
            .Where(static window => window._isInitialized && !window._isClosingWindow && !window._isPrivateWindow)
            .ToList();
        var primaryWindow = windows.FirstOrDefault(static window => window._persistSession)
            ?? windows.FirstOrDefault();
        return ReferenceEquals(this, primaryWindow);
    }

    private bool HasActiveDownloadForMemoryManagement()
    {
        return _downloadService.Items.Any(static item => item.State is
            DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused);
    }

    private void QueueMemoryReleasedTabRestore(BrowserTab tab)
    {
        _windowTaskSupervisor.TryRun(
            "tab-memory-restore-after-navigation",
            cancellationToken => RestoreMemoryReleasedTabAfterNavigationAsync(
                tab,
                cancellationToken));
    }

    private async Task RestoreMemoryReleasedTabAfterNavigationAsync(
        BrowserTab tab,
        CancellationToken cancellationToken)
    {
        if (!tab.IsMemoryReleased
            || !_tabs.Contains(tab)
            || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        var currentUrl = GetCurrentPageUrl(tab) ?? tab.Url;
        if (string.IsNullOrWhiteSpace(currentUrl) || IsBlankUrl(currentUrl))
        {
            return;
        }

        try
        {
            await Task.Delay(50, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (tab.MemoryRestoreScrollX is { } scrollX && tab.MemoryRestoreScrollY is { } scrollY)
            {
                var script = string.Create(
                    CultureInfo.InvariantCulture,
                    $"window.scrollTo({{ left: {scrollX}, top: {scrollY}, behavior: 'auto' }});");
                await core.ExecuteScriptAsync(script);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // ページ自体の復元は完了しているため、スクロール復元だけを断念する。
        }

        _ = s_tabRuntimeCoordinator.SetMemoryReleased(tab, false);
        tab.MemoryRestoreUrl = null;
        tab.MemoryRestoreScrollX = null;
        tab.MemoryRestoreScrollY = null;
        _tabMemoryManagementService.RecordRestore();
        if (ReferenceEquals(ActiveTab, tab))
        {
            ShowWebView(tab);
        }
        else
        {
            MoveWebViewToBackground(tab);
        }

        QueueWorkspaceOpenTabsRefresh();
    }

    private void ShowMemoryRestorePlaceholder(BrowserTab tab)
    {
        if (_displayedTab is not null && !ReferenceEquals(_displayedTab, tab))
        {
            MoveWebViewToBackground(_displayedTab);
        }

        var grid = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26))
        };
        if (tab.Thumbnail is not null)
        {
            grid.Children.Add(new Image
            {
                Source = tab.Thumbnail,
                Stretch = Stretch.UniformToFill,
                Opacity = 0.72
            });
        }

        grid.Children.Add(new Border
        {
            Padding = new Thickness(14, 9, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(224, 17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                FontSize = 13,
                Text = "タブを復元しています…"
            }
        });
        ShowPlaceholder(grid);
        _displayedTab = null;
    }
}
