using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface IPageToolsHost
{
    bool IsClosingWindow { get; }
    List<PageZoomPreferenceSettings> PageZoomPreferences { get; set; }
    BrowserTab? GetActiveBrowserCommandTarget();
    WebView2? GetTabWebView(BrowserTab? tab);
    string? GetCurrentPageUrl(BrowserTab tab);
    bool ContainsTab(BrowserTab tab);
    Rect? GetActivePaneBounds();
    void QueueToolbarLayout();
    void SaveSettings();
    void ReportError(string message);
}

internal sealed record PageToolsElements(
    FrameworkElement WebHost,
    Popup PageFindPopup,
    FrameworkElement PageFindPanel,
    TextBox PageFindTextBox,
    TextBlock PageFindCountText,
    CheckBox PageFindCaseSensitiveCheckBox,
    CheckBox PageFindWholeWordCheckBox,
    Popup PageZoomPopup,
    Button PageZoomButton,
    Button PageZoomResetButton);

// 検索セッション、倍率操作とUIの寿命を所有し、対象tabは既存の参照同一性で追跡する。
internal sealed class PageToolsController
{
    private readonly PageToolsElements _elements;
    private readonly IPageToolsHost _host;
    private Dispatcher Dispatcher => _elements.WebHost.Dispatcher;

    internal PageToolsController(PageToolsElements elements, IPageToolsHost host)
    {
        _elements = elements;
        _host = host;
    }

    private const int PageFindInputDelayMilliseconds = 180;
    private const int PageZoomSettingsSaveDelayMilliseconds = 900;

    private readonly OwnedDispatcherTimer _pageFindInputTimer = new(
        DispatcherTimerOwner.PageTools,
        DispatcherTimerTerminationPhase.DisposePageTools);
    private readonly OwnedDispatcherTimer _pageZoomSettingsSaveTimer = new(
        DispatcherTimerOwner.PageTools,
        DispatcherTimerTerminationPhase.DisposePageTools);
    private readonly PageToolsWorkCoordinator _pageToolsWorkCoordinator = new();
    private CoreWebView2Find? _pageFindSession;
    private BrowserTab? _pageFindTab;
    private EventHandler<object>? _pageFindActiveMatchChanged;
    private EventHandler<object>? _pageFindMatchCountChanged;
    private bool _pageToolsInitialized;
    private bool _pageToolsDisposed;

    internal void InitializePageTools()
    {
        if (_pageToolsInitialized)
        {
            return;
        }

        _pageToolsInitialized = true;
        _host.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Normalize(
            _host.PageZoomPreferences);
        _pageFindInputTimer.Interval = TimeSpan.FromMilliseconds(PageFindInputDelayMilliseconds);
        _pageFindInputTimer.Tick += PageFindInputTimer_Tick;
        _pageZoomSettingsSaveTimer.Interval =
            TimeSpan.FromMilliseconds(PageZoomSettingsSaveDelayMilliseconds);
        _pageZoomSettingsSaveTimer.Tick += PageZoomSettingsSaveTimer_Tick;
        UpdatePageToolsForActiveTarget();
    }

    internal void DisposePageTools()
    {
        if (_pageToolsDisposed)
        {
            return;
        }

        _pageToolsDisposed = true;
        _pageFindInputTimer.Stop();
        _pageZoomSettingsSaveTimer.Stop();
        ClosePageFind();
        _elements.PageZoomPopup.IsOpen = false;
        _pageToolsWorkCoordinator.ClearZoomTracking();
    }

    internal void OpenPageFind()
    {
        if (_host.GetActiveBrowserCommandTarget() is not { } target
            || !CanUsePageTools(target))
        {
            _host.ReportError("現在の表示ではページ内検索を利用できません");
            return;
        }

        if (!ReferenceEquals(_pageFindTab, target))
        {
            StopPageFindSession();
            _pageFindTab = target;
        }

        _elements.PageFindPopup.IsOpen = true;
        PositionPageFindPopup();
        _elements.PageFindTextBox.Focus();
        _elements.PageFindTextBox.SelectAll();
        if (!string.IsNullOrEmpty(_elements.PageFindTextBox.Text))
        {
            QueuePageFindRestart();
        }
        else
        {
            _elements.PageFindCountText.Text = "0 / 0";
        }
    }

    internal void ClosePageFind()
    {
        _pageFindInputTimer.Stop();
        StopPageFindSession();
        _pageFindTab = null;
        if (_elements.PageFindPopup.IsOpen)
        {
            _elements.PageFindPopup.IsOpen = false;
        }
    }

    internal void ClosePageFindForTab(BrowserTab tab)
    {
        if (ReferenceEquals(_pageFindTab, tab))
        {
            ClosePageFind();
        }
    }

    internal void QueuePageFindRestart()
    {
        if (!_elements.PageFindPopup.IsOpen || _pageFindTab is null)
        {
            return;
        }

        _pageFindInputTimer.Stop();
        _pageFindInputTimer.Start();
    }

    private async Task RestartPageFindAsync()
    {
        var target = _pageFindTab;
        if (!_elements.PageFindPopup.IsOpen
            || target is null
            || !ReferenceEquals(_host.GetActiveBrowserCommandTarget(), target)
            || _host.GetTabWebView(target)?.CoreWebView2 is not { } core)
        {
            ClosePageFind();
            return;
        }

        var term = _elements.PageFindTextBox.Text;
        StopPageFindSession(clearTarget: false);
        var generation = _pageToolsWorkCoordinator.BeginPageFind();
        if (string.IsNullOrEmpty(term))
        {
            _elements.PageFindCountText.Text = "0 / 0";
            return;
        }

        try
        {
            var find = core.Find;
            _pageFindSession = find;
            _pageFindActiveMatchChanged = (_, _) => UpdatePageFindCount(find, generation);
            _pageFindMatchCountChanged = (_, _) => UpdatePageFindCount(find, generation);
            find.ActiveMatchIndexChanged += _pageFindActiveMatchChanged;
            find.MatchCountChanged += _pageFindMatchCountChanged;

            var options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = _elements.PageFindCaseSensitiveCheckBox.IsChecked == true;
            options.ShouldMatchWord = _elements.PageFindWholeWordCheckBox.IsChecked == true;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await find.StartAsync(options);

            if (_pageToolsWorkCoordinator.IsCurrentPageFind(generation)
                && ReferenceEquals(_pageFindSession, find)
                && _elements.PageFindPopup.IsOpen)
            {
                UpdatePageFindCount(find, generation);
            }
        }
        catch (Exception ex)
        {
            if (_pageToolsWorkCoordinator.IsCurrentPageFind(generation))
            {
                StopPageFindSession(clearTarget: false);
                _elements.PageFindCountText.Text = "検索失敗";
                _host.ReportError("ページ内検索を開始できません: " + ex.Message);
            }
        }
    }

    private void StopPageFindSession(bool clearTarget = true)
    {
        _pageToolsWorkCoordinator.InvalidatePageFind();
        if (_pageFindSession is { } find)
        {
            try
            {
                if (_pageFindActiveMatchChanged is not null)
                {
                    find.ActiveMatchIndexChanged -= _pageFindActiveMatchChanged;
                }

                if (_pageFindMatchCountChanged is not null)
                {
                    find.MatchCountChanged -= _pageFindMatchCountChanged;
                }

                find.Stop();
            }
            catch
            {
                // WebView2終了時の後始末なので失敗しても無視します。
            }
        }

        _pageFindSession = null;
        _pageFindActiveMatchChanged = null;
        _pageFindMatchCountChanged = null;
        if (clearTarget)
        {
            _pageFindTab = null;
        }
    }

    private void UpdatePageFindCount(CoreWebView2Find find, int generation)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(() => UpdatePageFindCount(find, generation)),
                DispatcherPriority.Background);
            return;
        }

        if (!_pageToolsWorkCoordinator.IsCurrentPageFind(generation)
            || !ReferenceEquals(_pageFindSession, find)
            || !_elements.PageFindPopup.IsOpen)
        {
            return;
        }

        var count = Math.Max(0, find.MatchCount);
        var index = count > 0 ? Math.Max(0, find.ActiveMatchIndex) : 0;
        _elements.PageFindCountText.Text = $"{index} / {count}";
    }

    internal void MovePageFind(bool forward)
    {
        if (_pageFindInputTimer.IsEnabled || _pageFindSession is not { } find)
        {
            _pageFindInputTimer.Stop();
            _ = RestartPageFindAsync();
            return;
        }

        try
        {
            if (forward)
            {
                find.FindNext();
            }
            else
            {
                find.FindPrevious();
            }
        }
        catch (Exception ex)
        {
            _host.ReportError("ページ内検索を移動できません: " + ex.Message);
        }
    }

    internal void PositionPageFindPopup()
    {
        if (!_elements.PageFindPopup.IsOpen || _elements.WebHost.ActualWidth <= 0 || _elements.WebHost.ActualHeight <= 0)
        {
            return;
        }

        var left = 0d;
        var top = 0d;
        var width = _elements.WebHost.ActualWidth;
        if (_host.GetActivePaneBounds() is { } bounds)
        {
            left = bounds.X;
            top = bounds.Y;
            width = bounds.Width;
        }

        const double margin = 8;
        var panelWidth = _elements.PageFindPanel.Width;
        _elements.PageFindPopup.HorizontalOffset = Math.Clamp(
            left + width - panelWidth - margin,
            margin,
            Math.Max(margin, _elements.WebHost.ActualWidth - panelWidth - margin));
        _elements.PageFindPopup.VerticalOffset = Math.Clamp(
            top + margin,
            margin,
            Math.Max(margin, _elements.WebHost.ActualHeight - _elements.PageFindPanel.ActualHeight - margin));
    }

    internal void UpdatePageToolsForActiveTarget()
    {
        if (!_pageToolsInitialized || _pageToolsDisposed)
        {
            return;
        }

        var target = _host.GetActiveBrowserCommandTarget();
        if (_pageFindTab is not null && !ReferenceEquals(_pageFindTab, target))
        {
            ClosePageFind();
        }

        var canUsePageTools = CanUsePageTools(target);
        var pageZoomVisibility = canUsePageTools ? Visibility.Visible : Visibility.Collapsed;
        var pageZoomVisibilityChanged = _elements.PageZoomButton.Visibility != pageZoomVisibility;
        _elements.PageZoomButton.Visibility = pageZoomVisibility;
        _elements.PageZoomButton.IsEnabled = canUsePageTools;
        if (!canUsePageTools)
        {
            _elements.PageZoomPopup.IsOpen = false;
        }

        UpdatePageZoomDisplay(target);
        PositionPageFindPopup();
        if (pageZoomVisibilityChanged)
        {
            _host.QueueToolbarLayout();
        }
    }

    private bool CanUsePageTools(BrowserTab? tab)
    {
        return tab is { IsNativeContentTab: false }
            && _host.GetTabWebView(tab)?.CoreWebView2 is not null;
    }

    internal void BeginPageZoomNavigation(BrowserTab tab)
    {
        _pageToolsWorkCoordinator.BeginZoomNavigation(tab.Id);
    }

    internal void CompletePageZoomNavigation(BrowserTab tab, string? url)
    {
        ApplySavedPageZoom(tab, url);
        Dispatcher.BeginInvoke(
            new Action(() => _pageToolsWorkCoordinator.EndZoomNavigation(tab.Id)),
            DispatcherPriority.Background);
    }

    internal void CancelPageZoomNavigation(BrowserTab tab)
    {
        _pageToolsWorkCoordinator.CancelZoomTracking(tab.Id);
    }

    internal void ApplySavedPageZoom(BrowserTab tab, string? url = null)
    {
        if (_host.GetTabWebView(tab) is not { } webView || webView.CoreWebView2 is null)
        {
            return;
        }

        var factor = PageZoomPreferenceSettingsProfile.GetZoomFactor(
            _host.PageZoomPreferences,
            url ?? _host.GetCurrentPageUrl(tab) ?? tab.Url,
            tab.ContainerProfileId);
        ApplyPageZoomFactor(tab, webView, factor);
    }

    internal void HandlePageZoomFactorChanged(BrowserTab tab, WebView2 webView)
    {
        if (!_host.ContainsTab(tab)
            || _pageToolsWorkCoordinator.IsZoomFactorChangeSuppressed(tab.Id))
        {
            return;
        }

        var url = _host.GetCurrentPageUrl(tab) ?? tab.Url;
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(url, out _))
        {
            UpdatePageZoomDisplay(_host.GetActiveBrowserCommandTarget());
            return;
        }

        RecordPageZoom(tab, url, webView.ZoomFactor);
        UpdatePageZoomDisplay(_host.GetActiveBrowserCommandTarget());
    }

    internal void ChangePageZoom(double delta)
    {
        if (_host.GetActiveBrowserCommandTarget() is not { } target
            || _host.GetTabWebView(target) is not { } webView
            || webView.CoreWebView2 is null)
        {
            _host.ReportError("現在の表示ではページ倍率を変更できません");
            return;
        }

        SetPageZoom(webView.ZoomFactor + delta);
    }

    internal void SetPageZoom(double zoomFactor)
    {
        if (_host.GetActiveBrowserCommandTarget() is not { } target
            || _host.GetTabWebView(target) is not { } webView
            || webView.CoreWebView2 is null)
        {
            _host.ReportError("現在の表示ではページ倍率を変更できません");
            return;
        }

        var normalized = Math.Round(
            PageZoomPreferenceSettingsProfile.NormalizeZoomFactor(zoomFactor),
            2,
            MidpointRounding.AwayFromZero);
        ApplyPageZoomFactor(target, webView, normalized);
        RecordPageZoom(target, _host.GetCurrentPageUrl(target) ?? target.Url, normalized);
        UpdatePageZoomDisplay(target);
    }

    private void ApplyPageZoomFactor(BrowserTab tab, WebView2 webView, double zoomFactor)
    {
        var normalized = PageZoomPreferenceSettingsProfile.NormalizeZoomFactor(zoomFactor);
        if (Math.Abs(webView.ZoomFactor - normalized) < 0.001)
        {
            return;
        }

        IDisposable? applyLease = null;
        try
        {
            applyLease = _pageToolsWorkCoordinator.EnterZoomApply(tab.Id);
            webView.ZoomFactor = normalized;
        }
        catch (Exception ex)
        {
            _host.ReportError("ページ倍率を適用できません: " + ex.Message);
        }
        finally
        {
            applyLease?.Dispose();
        }
    }

    private void RecordPageZoom(BrowserTab tab, string? url, double zoomFactor)
    {
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(url, out _))
        {
            return;
        }

        _host.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Record(
            _host.PageZoomPreferences,
            url,
            tab.ContainerProfileId,
            zoomFactor,
            DateTimeOffset.UtcNow);
        QueuePageZoomSettingsSave();
    }

    private void QueuePageZoomSettingsSave()
    {
        _pageZoomSettingsSaveTimer.Stop();
        _pageZoomSettingsSaveTimer.Start();
    }

    private void UpdatePageZoomDisplay(BrowserTab? target)
    {
        var webView = _host.GetTabWebView(target);
        var factor = webView?.CoreWebView2 is not null
            ? PageZoomPreferenceSettingsProfile.NormalizeZoomFactor(webView.ZoomFactor)
            : PageZoomPreferenceSettingsProfile.DefaultZoomFactor;
        var percent = (int)Math.Round(factor * 100, MidpointRounding.AwayFromZero);
        _elements.PageZoomButton.Content = $"{percent}%";
        _elements.PageZoomResetButton.Content = "100%";
        _elements.PageZoomButton.ToolTip = $"ページの表示倍率: {percent}%";
    }

    private void PageFindInputTimer_Tick(object? sender, EventArgs e)
    {
        _pageFindInputTimer.Stop();
        _ = RestartPageFindAsync();
    }

    private void PageZoomSettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _pageZoomSettingsSaveTimer.Stop();
        if (_host.IsClosingWindow)
        {
            return;
        }

        try
        {
            _host.SaveSettings();
        }
        catch (Exception ex)
        {
            _host.ReportError("ページ倍率設定を保存できません: " + ex.Message);
        }
    }

}
