using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IFolderTabOpenHost
{
    private void NotifyVisibleFolderTabsActivated()
    {
        foreach (var view in _tabs
            .Select(tab => GetTabHostedContent(tab))
            .OfType<FolderTabView>()
            .Where(view => view.IsVisible)
            .Distinct())
        {
            view.NotifyHostActivated();
        }
    }

    private void OpenShellFolderInTab(string folderPath)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => OpenShellFolderInTab(folderPath));
            return;
        }

        _folderTabOpenController.OpenShellFolderInTab(folderPath);
    }

    private BrowserTab CreateFolderTab(
        string folderPath,
        SessionTabState? sessionState = null,
        string? initialViewMode = null,
        string? initialSortMode = null,
        string? initialSortDirection = null,
        string? title = null,
        FolderTabSessionState? initialState = null,
        bool useRestoreFallback = false,
        BrowserTabState? persistentState = null)
    {
        var restoredState = initialState ?? sessionState?.FolderTabState;
        var normalizedPath = sessionState is not null || restoredState is not null || useRestoreFallback
            ? FolderTabService.ResolveRestoredDirectory(folderPath, restoredState)
            : FolderTabService.NormalizeExistingDirectory(folderPath);
        var rememberedSort = FolderTabSortPreferenceSettingsProfile.Find(
            _settings.FolderTabSortPreferences,
            normalizedPath);
        var view = new FolderTabView(
            normalizedPath,
            initialViewMode ?? sessionState?.FolderTabViewMode ?? _settings.FolderTabViewMode,
            _settings.QuickLook,
            initialSortMode
                ?? sessionState?.FolderTabSortMode
                ?? rememberedSort?.SortMode
                ?? _settings.FolderTabSortMode,
            initialSortDirection
                ?? sessionState?.FolderTabSortDirection
                ?? rememberedSort?.SortDirection
                ?? _settings.FolderTabSortDirection,
            restoredState,
            _settings.ShowHiddenFolderTabItems);
        BrowserTab? registeredRuntimeTab = null;
        try
        {
            view.ConfigureLocalVideoPlayer(path => OpenLocalVideoMiniPlayer(path));
            view.ConfigureWorkspaceSave(SaveFolderTabPathsToWorkspaceAsync);
            view.ConfigurePlaces(
                CreateFolderTabPlacesSnapshot,
                CreateFolderTabPathSuggestions,
                SetFolderTabPlacePinned,
                MoveFolderTabPinnedPlace);
            if (!_isPrivateWindow)
            {
                view.ConfigureRecentItemRecording(path =>
                    App.RecentItems.Record(RecentItemFactory.FromFile(path)));
            }
            view.QuickLookPaneLayoutChanged += QuickLookPaneLayoutChanged;
            view.QuickLookTextDisplayChanged += QuickLookTextDisplayChanged;
            var tab = persistentState is null
                ? new BrowserTab
                {
                    Id = sessionState?.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
                    IsPinned = sessionState?.IsPinned ?? false,
                    SplitViewId = sessionState?.SplitViewId,
                    SplitViewOrientation = sessionState?.SplitViewOrientation,
                    SplitViewRatio = sessionState?.SplitViewRatio
                }
                : BrowserTabStateMapper.CreateBrowserTab(
                    persistentState,
                    normalizedPath,
                    DateTimeOffset.UtcNow);
            tab.Url = normalizedPath;
            tab.Title = string.IsNullOrWhiteSpace(title) ? FolderTabService.GetDisplayName(normalizedPath) : title;
            tab.IsFolderTab = true;
            s_tabRuntimeCoordinator.RegisterHostedContent(tab, view);
            registeredRuntimeTab = tab;
            view.ConfigureSplitFolderOpening(() => GetFolderTabSplitOpenMode(tab, view));
            view.ConfigureCommandExecution(
                (actionId, parameter) => ExecuteRegisteredCommandAsync(
                    actionId,
                    tab,
                    parameter));
            view.ConfigureGestures(
                _settings,
                (command, selectedText) => ExecuteFolderTabGestureCommandAsync(tab, view, command, selectedText),
                () => TryBeginFolderTabGesture(tab),
                CompleteWorkspaceGesture,
                () => TryBeginWorkspaceRightWheel(tab.Id));

            view.NavigationChanged += (_, args) =>
            {
                if (!_tabs.Contains(tab))
                {
                    return;
                }

                tab.Url = args.FolderPath;
                tab.Title = args.Title;
                if (ReferenceEquals(ActiveTab, tab))
                {
                    SetAddressText(args.FolderPath);
                    UpdateToolbarState();
                }

                if (!_isPrivateWindow)
                {
                    App.RecentItems.Record(RecentItemFactory.FromFolder(args.FolderPath));
                }

                QueueSessionSave();
            };
            view.OpenFolderRequested += (_, args) =>
                OpenFolderFromFolderTab(tab, view, args);
            view.ViewModeChanged += (_, args) => SaveFolderTabViewMode(args.ViewMode);
            view.HiddenItemsVisibilityChanged += (_, args) =>
                SaveFolderTabHiddenItemsVisibility(args.ShowHiddenItems);
            view.SortChanged += (_, args) =>
            {
                SaveFolderTabSort(view.CurrentPath, args.SortMode, args.SortDirection);
                QueueSessionSave();
            };
            view.DetailColumnOrderChanged += (_, _) => QueueSessionSave();
            return tab;
        }
        catch (Exception operationException)
        {
            try
            {
                if (registeredRuntimeTab is not null
                    && ReferenceEquals(GetTabHostedContent(registeredRuntimeTab), view))
                {
                    s_tabRuntimeCoordinator.Terminate(registeredRuntimeTab, static _ => { });
                }
                else
                {
                    view.Dispose();
                }
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "FolderTabの生成と後始末に失敗しました。",
                    operationException,
                    cleanupException);
            }

            throw;
        }
    }

    private async Task SaveFolderTabPathsToWorkspaceAsync(IReadOnlyList<string> paths)
        => await WorkspaceService.SaveFolderTabPathsAsync(
            paths,
            () => _settings.LastWorkspaceId,
            () => _windowTaskSupervisor.LifetimeToken,
            (workspace, itemCount) =>
            {
                ReloadVisibleWorkspaceViews();
                SetStatus($"{itemCount:N0} 件をWorkspace {workspace.Index}へ保存しました", autoClear: true);
            });

    private BrowserTab CreateTransferredFolderTab(BrowserTab sourceTab)
    {
        return CreateTransferredFolderTab(CaptureTransferredFolderTabSnapshot(sourceTab));
    }

    private FolderTabTransferSnapshot CaptureTransferredFolderTabSnapshot(BrowserTab sourceTab)
    {
        var sourceView = GetTabHostedContent(sourceTab) as FolderTabView;
        return new FolderTabTransferSnapshot(
            sourceTab.Id,
            sourceView?.CurrentPath ?? sourceTab.Url ?? string.Empty,
            sourceTab.Title,
            sourceView?.ViewMode,
            sourceView?.SortMode,
            sourceView?.SortDirection,
            sourceView?.CaptureSessionState(),
            sourceTab.IsPinned);
    }

    private BrowserTab CreateTransferredFolderTab(FolderTabTransferSnapshot snapshot)
    {
        var state = snapshot.CreateSessionTabState();
        return CreateFolderTab(
            state.Url ?? string.Empty,
            state,
            title: state.Title);
    }

    private BrowserTab CreateRestoredFolderTab(ClosedTabState closedTab)
    {
        var tab = CreateFolderTab(
                closedTab.Url ?? string.Empty,
                initialViewMode: closedTab.FolderTabViewMode,
                initialSortMode: closedTab.FolderTabSortMode,
                initialSortDirection: closedTab.FolderTabSortDirection,
                title: closedTab.Title,
                initialState: closedTab.FolderTabState,
                useRestoreFallback: true,
                persistentState: BrowserTabStateMapper.FromClosedTabState(closedTab));
        ApplyContainerProfileToTab(tab);
        return tab;
    }

    private void ShowFolderTab(BrowserTab tab)
    {
        if (_displayedTab is not null)
        {
            MoveWebViewToBackground(_displayedTab);
            _displayedTab = null;
        }

        if (GetTabHostedContent(tab) is UIElement content)
        {
            SplitViewHost.Visibility = Visibility.Collapsed;
            HidePlaceholder();
            ShowOnlyFolderTabContent(content);
            if (content is FolderTabView folderTabView)
            {
                folderTabView.FocusShortcutScope();
            }
        }
        else
        {
            ShowBlankView();
        }

        SetAddressText((GetTabHostedContent(tab) as FolderTabView)?.CurrentPath ?? tab.Url ?? string.Empty);
        UpdateToolbarState();
    }

    private void OpenFolderFromFolderTab(
        BrowserTab sourceTab,
        FolderTabView sourceView,
        FolderTabOpenRequestedEventArgs args)
        => _folderTabOpenController.OpenFolderFromFolderTab(sourceTab, sourceView, args);

    private void OpenFolderInAdjacentTab(
        BrowserTab sourceTab,
        FolderTabView sourceView,
        string folderPath,
        bool activate,
        FolderTabSessionState? initialState = null)
        => _folderTabOpenController.OpenFolderInAdjacentTab(sourceTab, sourceView, folderPath, activate, initialState);

    private FolderTabSplitOpenMode GetFolderTabSplitOpenMode(BrowserTab sourceTab, FolderTabView sourceView)
        => _folderTabOpenController.GetFolderTabSplitOpenMode(sourceTab, sourceView);

    IList<BrowserTab> IFolderTabOpenHost.Tabs => _tabs;
    BrowserTab? IFolderTabOpenHost.ActiveTab => ActiveTab;
    IFolderTabOpenView? IFolderTabOpenHost.GetFolderView(BrowserTab tab)
        => GetTabHostedContent(tab) as FolderTabView;
    BrowserTab IFolderTabOpenHost.CreateFolderTab(string folderPath, string? initialViewMode,
        string? initialSortMode, string? initialSortDirection, FolderTabSessionState? initialState)
        => CreateFolderTab(folderPath, initialViewMode: initialViewMode, initialSortMode: initialSortMode,
            initialSortDirection: initialSortDirection, initialState: initialState);
    IEnumerable<int> IFolderTabOpenHost.GetSplitViewGroupIndices(string splitViewId)
        => GetSplitViewGroupIndices(splitViewId);
    void IFolderTabOpenHost.InsertPreservedTab(BrowserTab tab, int insertionIndex, bool activate)
        => InsertPreservedTab(tab, insertionIndex, activate);
    void IFolderTabOpenHost.QueueSplitOpen(Func<CancellationToken, Task> operation)
        => _windowTaskSupervisor.TryRun("folder-tab-split-open", operation);
    Task IFolderTabOpenHost.StartSplitViewAsync(
        BrowserTab leftTab, BrowserTab rightTab, string orientation, CancellationToken cancellationToken)
        => StartSplitViewAsync(leftTab, rightTab, orientation, cancellationToken);
    void IFolderTabOpenHost.OpenWindow(SessionTabState initialTab)
    {
        var position = new Point(
            Math.Max(SystemParameters.VirtualScreenLeft, Left + 32),
            Math.Max(SystemParameters.VirtualScreenTop, Top + 32));
        var newWindow = new MainWindow(initialTab, position, _isPrivateWindow);
        newWindow.Show();
        newWindow.Activate();
    }
    void IFolderTabOpenHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IFolderTabOpenHost.SelectTab(BrowserTab tab) => SelectTab(tab);
    void IFolderTabOpenHost.QueueSelectedTabActivation() => QueueSelectedTabActivation();
    void IFolderTabOpenHost.QueueSessionSave() => QueueSessionSave();
    void IFolderTabOpenHost.QueueWorkspaceOpenTabsRefresh() => QueueWorkspaceOpenTabsRefresh();

    private bool TryHandleFolderTabShortcutKey(KeyEventArgs e)
    {
        if (IsCommandPaletteOpen
            || GetTabHostedContent(ActiveTab) is not FolderTabView folderTabView)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.L && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ShowTopToolbar();
            if (AddressBox.Visibility == Visibility.Visible)
            {
                SetAddressText(folderTabView.CurrentPath);
                AddressBox.Focus();
                AddressBox.SelectAll();
            }
            else
            {
                folderTabView.FocusPathBox();
            }

            return true;
        }

        if ((key == Key.Left && modifiers == ModifierKeys.Alt)
            || (key == Key.BrowserBack && modifiers == ModifierKeys.None))
        {
            e.Handled = true;
            QueueFolderTabNavigateBack();
            return true;
        }

        if ((key == Key.Right && modifiers == ModifierKeys.Alt)
            || (key == Key.BrowserForward && modifiers == ModifierKeys.None))
        {
            e.Handled = true;
            QueueFolderTabNavigateForward();
            return true;
        }

        if (key == Key.F5 && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            QueueFolderTabReload();
            return true;
        }

        return false;
    }

    private void QueueFolderTabNavigateBack()
    {
        _windowTaskSupervisor.TryRun(
            "folder-tab-navigate-back",
            cancellationToken => NavigateBackAsync(cancellationToken: cancellationToken));
    }

    private void QueueFolderTabNavigateForward()
    {
        _windowTaskSupervisor.TryRun(
            "folder-tab-navigate-forward",
            cancellationToken => NavigateForwardAsync(cancellationToken: cancellationToken));
    }

    private void QueueFolderTabReload()
    {
        _windowTaskSupervisor.TryRun(
            "folder-tab-reload",
            cancellationToken => ReloadActiveTabAsync(cancellationToken: cancellationToken));
    }

    private void PrepareFolderTabContentTransition(BrowserTab? targetTab)
    {
        var keepActiveSplitContent = targetTab is not null && _splitViewService.Contains(targetTab);
        foreach (var folderTab in _tabs.Where(static tab => tab.IsFolderTab))
        {
            if (ReferenceEquals(folderTab, targetTab)
                || (keepActiveSplitContent && _splitViewService.Contains(folderTab))
                || GetTabHostedContent(folderTab) is not UIElement content)
            {
                continue;
            }

            HideFolderTabContent(content);
        }
    }

    private void ShowOnlyFolderTabContent(UIElement content)
    {
        foreach (var folderTab in _tabs.Where(static tab => tab.IsFolderTab))
        {
            if (GetTabHostedContent(folderTab) is UIElement otherContent
                && !ReferenceEquals(otherContent, content))
            {
                HideFolderTabContent(otherContent);
            }
        }

        EnsureFolderTabHosted(content);
        content.IsHitTestVisible = true;
        content.Visibility = Visibility.Visible;
        Panel.SetZIndex(content, 1);
        FolderTabHost.Visibility = Visibility.Visible;
    }

    private void HideAllFolderTabContents()
    {
        foreach (var folderTab in _tabs.Where(static tab => tab.IsFolderTab))
        {
            if (GetTabHostedContent(folderTab) is UIElement content)
            {
                HideFolderTabContent(content);
            }
        }

        FolderTabHost.Visibility = Visibility.Collapsed;
    }

    private void HideFolderTabContent(UIElement content)
    {
        content.IsHitTestVisible = false;
        content.Visibility = Visibility.Collapsed;
        if (!FolderTabHost.Children.OfType<UIElement>().Any(static child => child.Visibility == Visibility.Visible))
        {
            FolderTabHost.Visibility = Visibility.Collapsed;
        }
    }

    private void ParkFolderTabContent(UIElement content)
    {
        EnsureFolderTabHosted(content);
        HideFolderTabContent(content);
    }

    private void EnsureFolderTabHosted(UIElement content)
    {
        if (ReferenceEquals((content as FrameworkElement)?.Parent, FolderTabHost))
        {
            return;
        }

        DetachHostedContent(content);
        if (!FolderTabHost.Children.Contains(content))
        {
            FolderTabHost.Children.Add(content);
        }
    }

    private static bool IsReadyFolderTab(BrowserTab? tab)
    {
        return tab is { IsFolderTab: true }
            && GetTabHostedContent(tab) is FolderTabView;
    }

    private long? TryBeginFolderTabGesture(BrowserTab tab)
    {
        if (!tab.IsFolderTab
            || !_tabs.Contains(tab))
        {
            return null;
        }

        if (!ReferenceEquals(ActiveTab, tab))
        {
            if (SplitViewHost.Visibility != Visibility.Visible
                || !_splitViewService.TryGetPane(tab, out _))
            {
                return null;
            }

            ActivateSplitPaneForTab(tab);
            if (!ReferenceEquals(ActiveTab, tab))
            {
                return null;
            }
        }

        return TryBeginWorkspaceGesture(tab.Id);
    }

    private async Task ExecuteFolderTabGestureCommandAsync(
        BrowserTab tab,
        FolderTabView view,
        string? command,
        string? selectedText)
    {
        if (!_tabs.Contains(tab)
            || !ReferenceEquals(ActiveTab, tab)
            || !ReferenceEquals(GetTabHostedContent(tab), view))
        {
            return;
        }

        if (await view.TryExecuteGestureCommandAsync(command))
        {
            return;
        }

        if (command is "StopLoading" or "SearchSelection" or "CopyUrl" or "AddBookmark")
        {
            return;
        }

        await ExecuteGestureCommandForTargetAsync(command, selectedText, tab);
    }

    private void CancelInactiveFolderTabGestures(Guid? activeTabId)
    {
        foreach (var tab in _tabs.Where(tab => tab.IsFolderTab && tab.Id != activeTabId))
        {
            if (GetTabHostedContent(tab) is FolderTabView view)
            {
                view.CancelGesture();
                if (!_splitViewService.Contains(tab))
                {
                    view.CloseQuickLook();
                }
            }

            CancelWorkspaceInputScope(tab.Id);
        }
    }

    private void SaveFolderTabViewMode(string viewMode)
    {
        var normalized = FolderTabViewMode.Normalize(viewMode);
        if (string.Equals(_settings.FolderTabViewMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settings.FolderTabViewMode = normalized;
        SettingsService.Save(_settings);
    }

    private void SaveFolderTabHiddenItemsVisibility(bool showHiddenItems)
    {
        foreach (var window in GetSettingsSynchronizationTargets())
        {
            window._settings.ShowHiddenFolderTabItems = showHiddenItems;
            foreach (var view in window._tabs
                .Select(tab => GetTabHostedContent(tab))
                .OfType<FolderTabView>())
            {
                view.UpdateShowHiddenItems(showHiddenItems);
            }
        }

        SettingsService.Save(_settings);
    }

    private FolderTabPlacesSnapshot CreateFolderTabPlacesSnapshot()
    {
        return FolderTabPlacesService.CreateSnapshot(
            _settings.FolderTabPinnedPlaces,
            App.RecentItems.GetSnapshot());
    }

    private IReadOnlyList<FolderTabPathSuggestion> CreateFolderTabPathSuggestions(string? query)
    {
        return FolderTabPlacesService.CreatePathSuggestions(
            _settings.FolderTabPinnedPlaces,
            App.RecentItems.GetSnapshot(),
            query);
    }

    private void SetFolderTabPlacePinned(string folderPath, bool isPinned)
    {
        var updated = FolderTabPlacesService.SetPinned(
            _settings.FolderTabPinnedPlaces,
            folderPath,
            isPinned);
        SynchronizeFolderTabPinnedPlaces(updated);
    }

    private void MoveFolderTabPinnedPlace(string folderPath, int offset)
    {
        var updated = FolderTabPlacesService.MovePinned(
            _settings.FolderTabPinnedPlaces,
            folderPath,
            offset);
        SynchronizeFolderTabPinnedPlaces(updated);
    }

    private void SynchronizeFolderTabPinnedPlaces(IReadOnlyList<string> places)
    {
        var normalized = FolderTabPlacesService.NormalizePinnedPlaces(places);
        foreach (var window in GetSettingsSynchronizationTargets())
        {
            window._settings.FolderTabPinnedPlaces = normalized.ToList();
        }

        SettingsService.Save(_settings);
    }

    private void SaveFolderTabSort(string folderPath, string sortMode, string sortDirection)
    {
        if (FolderTabSortPreferenceSettingsProfile.Update(_settings, folderPath, sortMode, sortDirection))
        {
            SettingsService.Save(_settings);
        }
    }

    private void QuickLookPaneLayoutChanged(
        object? sender,
        QuickLookPaneLayoutChangedEventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => QuickLookPaneLayoutChanged(sender, args));
            return;
        }

        var quickLook = QuickLookSettingsProfile.Normalize(_settings.QuickLook);
        quickLook.UseRightSidePane = args.UseRightSidePane;
        quickLook.PanePlacement = args.PanePlacement;
        quickLook.RightPaneWidth = args.RightPaneWidth;
        quickLook.BottomPaneHeight = args.BottomPaneHeight;
        quickLook.FloatingOffsetX = args.FloatingOffsetX;
        quickLook.FloatingOffsetY = args.FloatingOffsetY;
        quickLook.FloatingWidth = args.FloatingWidth;
        quickLook.FloatingHeight = args.FloatingHeight;
        quickLook = QuickLookSettingsProfile.Normalize(quickLook);
        if (!QuickLookSettingsProfile.HasPaneLayoutChanged(_settings.QuickLook, quickLook))
        {
            return;
        }

        ApplyQuickLookSettingsToOpenWindows(
            quickLook,
            updateLayoutInputs: true,
            updateTextInputs: false);
    }

    private void QuickLookTextDisplayChanged(
        object? sender,
        QuickLookTextDisplayChangedEventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => QuickLookTextDisplayChanged(sender, args));
            return;
        }

        var quickLook = QuickLookSettingsProfile.Normalize(_settings.QuickLook);
        quickLook.TextFontSize = args.TextFontSize;
        quickLook.TextWrap = args.TextWrap;
        quickLook = QuickLookSettingsProfile.Normalize(quickLook);
        if (!QuickLookSettingsProfile.HasTextDisplayChanged(_settings.QuickLook, quickLook))
        {
            return;
        }

        ApplyQuickLookSettingsToOpenWindows(
            quickLook,
            updateLayoutInputs: false,
            updateTextInputs: true);
    }

    private void ApplyQuickLookSettingsToOpenWindows(
        QuickLookSettings quickLook,
        bool updateLayoutInputs,
        bool updateTextInputs)
    {
        foreach (var window in GetSettingsSynchronizationTargets())
        {
            var normalized = QuickLookSettingsProfile.Normalize(quickLook);
            window._settings.QuickLook = normalized;
            if (ModelessWindowService.TryGetOwned<SettingsWindow>(window, out var settingsWindow)
                && updateLayoutInputs)
            {
                settingsWindow.UpdateQuickLookLayoutSettings(normalized);
            }

            if (settingsWindow is not null && updateTextInputs)
            {
                settingsWindow.UpdateQuickLookTextDisplaySettings(normalized);
            }

            window.ApplyQuickLookPaneLayoutToOpenHosts();
        }

        SettingsService.Save(_settings);
    }

    private void ApplyQuickLookPaneLayoutToOpenHosts()
    {
        CommandPaletteQuickLookHost.UpdatePaneLayout(_settings.QuickLook);
        foreach (var tab in _tabs)
        {
            if (GetTabHostedContent(tab) is WorkspaceOSView workspaceView)
            {
                workspaceView.UpdateQuickLookPaneLayout(_settings.QuickLook);
            }
            else if (GetTabHostedContent(tab) is FolderTabView folderTabView)
            {
                folderTabView.UpdateQuickLookPaneLayout(_settings.QuickLook);
            }
        }

        _workspaceOSWindow?.UpdateQuickLookPaneLayout(_settings.QuickLook);
        foreach (var window in _workspaceFolderWindows.Values.ToList())
        {
            window.UpdateQuickLookPaneLayout(_settings.QuickLook);
        }
    }
}
