using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IPageToolsHost
{
    private const double PageZoomStep = 0.1;
    private readonly PageToolsController _pageToolsController;
    private bool _addressBoxTemporarilyShown;

    private BrowserTab? GetActiveBrowserCommandTarget()
    {
        if (_splitViewService.IsActive && SplitViewHost.Visibility == Visibility.Visible)
        {
            return _splitViewService.GetActiveTab(_tabs) ?? ActiveTab;
        }

        return ActiveTab;
    }

    private bool TryHandleBrowserShortcutKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PageFindPopup.IsOpen)
        {
            e.Handled = true;
            ClosePageFind();
            return true;
        }

        if (IsCommandPaletteOpen)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.T && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.RestoreClosedTabActionId);
            return true;
        }

        if (key == Key.T && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.NewTabActionId);
            return true;
        }

        if (key == Key.W && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.CloseTabActionId);
            return true;
        }

        if (key == Key.L && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.FocusLocationActionId);
            return true;
        }

        if (key == Key.F && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.FindInPageActionId);
            return true;
        }

        if (key == Key.Left && modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.BackActionId);
            return true;
        }

        if (key == Key.Right && modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.ForwardActionId);
            return true;
        }

        if (key == Key.F5 && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.ReloadActionId);
            return true;
        }

        if (IsPageZoomInShortcut(key, modifiers))
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.ZoomInActionId);
            return true;
        }

        if (IsPageZoomOutShortcut(key, modifiers))
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.ZoomOutActionId);
            return true;
        }

        if ((key is Key.D0 or Key.NumPad0) && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            QueueBrowserShortcutCommand(BrowserCommandProvider.ResetZoomActionId);
            return true;
        }

        return false;
    }

    private void QueueBrowserShortcutCommand(string actionId)
    {
        _windowTaskSupervisor.TryRun(
            "browser-shortcut-command",
            cancellationToken => ExecuteRegisteredCommandAsync(
                actionId,
                cancellationToken: cancellationToken));
    }

    private static bool IsPageZoomInShortcut(Key key, ModifierKeys modifiers)
    {
        return key == Key.Add && modifiers == ModifierKeys.Control
            || key == Key.OemPlus
            && modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift);
    }

    private static bool IsPageZoomOutShortcut(Key key, ModifierKeys modifiers)
    {
        return (key is Key.Subtract or Key.OemMinus) && modifiers == ModifierKeys.Control;
    }

    private bool TryFocusActiveLocation()
    {
        if (GetActiveBrowserCommandTarget() is not { IsWorkspaceTab: false } target)
        {
            return false;
        }

        ShowTopToolbar();
        if (GetTabHostedContent(target) is Views.FolderTabView folderTabView
            && AddressBox.Visibility != Visibility.Visible)
        {
            folderTabView.FocusPathBox();
            return true;
        }

        if (AddressBox.Visibility != Visibility.Visible)
        {
            AddressBox.Visibility = Visibility.Visible;
            _addressBoxTemporarilyShown = true;
        }

        SetAddressText(GetTabHostedContent(target) is Views.FolderTabView folder
            ? folder.CurrentPath
            : IsNewTabUrl(target.Url)
                ? string.Empty
                : GetCurrentPageUrl(target) ?? target.Url ?? string.Empty);
        AddressBox.Focus();
        AddressBox.SelectAll();
        return true;
    }

    private void RestoreTemporaryAddressBoxVisibility()
    {
        if (!_addressBoxTemporarilyShown)
        {
            return;
        }

        _addressBoxTemporarilyShown = false;
        AddressBox.Visibility = _settings.ToolbarVisibility.ShowAddressBox
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PageFindTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        QueuePageFindRestart();
    }

    private void PageFindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            MovePageFind(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClosePageFind();
        }
    }

    private void PageFindPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TryHandleBrowserShortcutKey(e);
    }

    private void PageFindOption_Changed(object sender, RoutedEventArgs e)
    {
        QueuePageFindRestart();
    }

    private void PageFindPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        MovePageFind(forward: false);
    }

    private void PageFindNextButton_Click(object sender, RoutedEventArgs e)
    {
        MovePageFind(forward: true);
    }

    private void PageFindCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ClosePageFind();
    }

    private void PageZoomButton_Click(object sender, RoutedEventArgs e)
    {
        PageZoomPopup.IsOpen = !PageZoomPopup.IsOpen;
    }

    private void PageZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePageZoom(-PageZoomStep);
    }

    private void PageZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetPageZoom(PageZoomPreferenceSettingsProfile.DefaultZoomFactor);
    }

    private void PageZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePageZoom(PageZoomStep);
    }

    private Task OpenPageFindFromCommandPaletteAsync()
    {
        OpenPageFind();
        return Task.CompletedTask;
    }

    private Task ZoomPageFromCommandPaletteAsync(double delta)
    {
        ChangePageZoom(delta);
        return Task.CompletedTask;
    }

    private Task ResetPageZoomFromCommandPaletteAsync()
    {
        SetPageZoom(PageZoomPreferenceSettingsProfile.DefaultZoomFactor);
        return Task.CompletedTask;
    }

    private void InitializePageTools() => _pageToolsController?.InitializePageTools();

    private void DisposePageTools() => _pageToolsController?.DisposePageTools();

    private void OpenPageFind() => _pageToolsController?.OpenPageFind();

    private void ClosePageFind() => _pageToolsController?.ClosePageFind();

    private void ClosePageFindForTab(BrowserTab tab) => _pageToolsController?.ClosePageFindForTab(tab);

    private void QueuePageFindRestart() => _pageToolsController?.QueuePageFindRestart();

    private void MovePageFind(bool forward) => _pageToolsController?.MovePageFind(forward);

    private void PositionPageFindPopup() => _pageToolsController?.PositionPageFindPopup();

    private void UpdatePageToolsForActiveTarget() => _pageToolsController?.UpdatePageToolsForActiveTarget();

    private void BeginPageZoomNavigation(BrowserTab tab) => _pageToolsController?.BeginPageZoomNavigation(tab);

    private void CompletePageZoomNavigation(BrowserTab tab, string? url) => _pageToolsController?.CompletePageZoomNavigation(tab, url);

    private void CancelPageZoomNavigation(BrowserTab tab) => _pageToolsController?.CancelPageZoomNavigation(tab);

    private void ApplySavedPageZoom(BrowserTab tab, string? url = null) => _pageToolsController?.ApplySavedPageZoom(tab, url);

    private void HandlePageZoomFactorChanged(BrowserTab tab, WebView2 webView) => _pageToolsController?.HandlePageZoomFactorChanged(tab, webView);

    private void ChangePageZoom(double delta) => _pageToolsController?.ChangePageZoom(delta);

    private void SetPageZoom(double zoomFactor) => _pageToolsController?.SetPageZoom(zoomFactor);

    bool IPageToolsHost.IsClosingWindow => _isClosingWindow;

    List<PageZoomPreferenceSettings> IPageToolsHost.PageZoomPreferences
    {
        get => _settings.PageZoomPreferences;
        set => _settings.PageZoomPreferences = value;
    }

    BrowserTab? IPageToolsHost.GetActiveBrowserCommandTarget() => GetActiveBrowserCommandTarget();

    WebView2? IPageToolsHost.GetTabWebView(BrowserTab? tab) => GetTabWebView(tab);

    string? IPageToolsHost.GetCurrentPageUrl(BrowserTab tab) => GetCurrentPageUrl(tab);

    bool IPageToolsHost.ContainsTab(BrowserTab tab) => _tabs.Contains(tab);

    Rect? IPageToolsHost.GetActivePaneBounds()
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            return null;
        }

        var activePane = _splitViewService.ActiveSplitView?.ActivePane ?? SplitViewPane.Left;
        var border = activePane == SplitViewPane.Right ? SplitRightPaneBorder : SplitLeftPaneBorder;
        var position = border.TranslatePoint(new Point(0, 0), WebHost);
        return new Rect(position.X, position.Y, border.ActualWidth, border.ActualHeight);
    }

    void IPageToolsHost.QueueToolbarLayout() => QueueTopToolbarLayoutAfterPageZoomVisibilityChange();

    void IPageToolsHost.SaveSettings() => SettingsService.Save(_settings);

    void IPageToolsHost.ReportError(string message) => SetStatus(message, autoClear: true);
}
