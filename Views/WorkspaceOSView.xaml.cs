using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;

namespace TabBrowser.Views;

public partial class WorkspaceOSView : UserControl, IDisposable
{
    private void DeletedItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceOSViewModel viewModel)
            ModelessWindowService.Show(new WorkspaceTrashWindow(viewModel.PersistenceService) { Owner = Window.GetWindow(this) });
    }

    private static readonly TimeSpan TabShelfRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WorkspaceDragHoverDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecentHoverPopupDelay = TimeSpan.FromMilliseconds(450);

    private WorkspaceGestureController? _gestureController;
    private Func<string?, string?, Task>? _executeGestureCommandAsync;
    private readonly DispatcherTimer _tabShelfRefreshTimer;
    private readonly DispatcherTimer _workspaceDragHoverTimer;
    private WorkspaceSlotViewModel? _workspaceDragHoverTarget;
    private Button? _workspaceDragHoverButton;
    private bool _workspaceDragHoverSwitching;
    private bool _initialized;
    private QuickLookSettings _quickLookSettings = QuickLookSettingsProfile.Normalize(null);
    private readonly SearchQuickLookItemSource _searchQuickLookSource;
    private readonly InboxQuickLookItemSource _inboxQuickLookSource;
    private readonly RecentQuickLookItemSource _recentQuickLookSource;
    private readonly DispatcherTimer _recentHoverPopupTimer;
    private RecentItem? _recentHoverPopupItem;
    private FrameworkElement? _recentHoverPopupTarget;
    private ToolTip? _recentHoverPopup;
    private RecentItemsService? _recentItemsService;
    private Func<RecentItem, Task>? _openRecentItemAsync;
    private Func<RecentItem, Task<string>>? _fetchRecentItemThumbnailAsync;
    private Func<ExternalBrowserKind, bool>? _isExternalBrowserAvailable;
    private Func<IReadOnlyList<string>, ExternalBrowserKind, Task>? _openExternalBrowserAsync;
    private Func<Guid, Guid, Window?, Task>? _appendWorkspaceNoteAsync;
    private RecentItem? _pendingRecentThumbnailItem;
    private string? _pendingRecentThumbnailPngBase64;
    private string? _activeQuickLookSourceId;
    private bool _suppressWorkspaceLevelQuickLookSelectionRefresh;
    private bool _isDisposed;
    private bool _isActive;
    private bool _isInteractiveResizePreviewActive;
    private bool _resumeTabShelfRefreshAfterInteractiveResize;
    private readonly ClipboardWriteService _clipboardWriteService;

    public event EventHandler<TabShelfTabTransferRequestedEventArgs>? TabShelfTabTransferRequested;

    public WorkspaceOSView()
    {
        _clipboardWriteService = new ClipboardWriteService(Dispatcher);
        InitializeComponent();
        WorkspaceQuickLookPreviewHost.ConfigureDockTarget(WorkspaceContent);
        _searchQuickLookSource = new SearchQuickLookItemSource(
            $"workspace-search:{GetHashCode():X}",
            CreateSearchQuickLookItems,
            CreateSelectedSearchQuickLookItems,
            () => WorkspaceSearchResultsList.SelectedIndex,
            SelectSearchQuickLookIndex);
        _inboxQuickLookSource = new InboxQuickLookItemSource(
            $"workspace-inbox:{GetHashCode():X}",
            CreateInboxQuickLookItems,
            CreateSelectedInboxQuickLookItems,
            TabShelf.GetQuickLookSelectedIndex,
            SelectInboxQuickLookIndex);
        _recentQuickLookSource = new RecentQuickLookItemSource(
            $"workspace-recent:{GetHashCode():X}",
            CreateRecentQuickLookItems,
            CreateSelectedRecentQuickLookItems,
            () => RecentItemsList.SelectedIndex,
            SelectRecentQuickLookIndex);
        WorkspaceCanvas.QuickLookOpening += (_, _) => CloseWorkspaceLevelQuickLook();
        WorkspaceQuickLookPreviewHost.PaneLayoutChanged += (_, args) =>
            QuickLookPaneLayoutChanged?.Invoke(this, args);
        WorkspaceCanvas.QuickLookPaneLayoutChanged += (_, args) =>
            QuickLookPaneLayoutChanged?.Invoke(this, args);
        WorkspaceQuickLookPreviewHost.TextDisplayChanged += (_, args) =>
            QuickLookTextDisplayChanged?.Invoke(this, args);
        WorkspaceCanvas.QuickLookTextDisplayChanged += (_, args) =>
            QuickLookTextDisplayChanged?.Invoke(this, args);
        _tabShelfRefreshTimer = new DispatcherTimer
        {
            Interval = TabShelfRefreshInterval
        };
        _tabShelfRefreshTimer.Tick += (_, _) => RefreshOpenTabs();
        _workspaceDragHoverTimer = new DispatcherTimer
        {
            Interval = WorkspaceDragHoverDelay
        };
        _workspaceDragHoverTimer.Tick += WorkspaceDragHoverTimer_Tick;
        _recentHoverPopupTimer = new DispatcherTimer
        {
            Interval = RecentHoverPopupDelay
        };
        _recentHoverPopupTimer.Tick += RecentHoverPopupTimer_Tick;
        Unloaded += UserControl_Unloaded;
    }

    public void ConfigureGestures(
        AppSettings settings,
        Func<string?, string?, Task> executeCommandAsync,
        Func<long?>? tryBeginInteraction = null,
        Action<long>? completeInteraction = null,
        Func<long?>? tryBeginRightWheelInteraction = null)
    {
        _executeGestureCommandAsync = executeCommandAsync;
        if (_gestureController is null)
        {
            _gestureController = new WorkspaceGestureController(
                this,
                settings,
                ExecuteGestureCommandAsync,
                tryBeginInteraction,
                completeInteraction,
                tryBeginRightWheelInteraction,
                gestureScope: GestureCommandScope.Workspace,
                executeRightWheelRoute: TryHandleControlRightWheel);
            return;
        }

        _gestureController.UpdateSettings(settings);
    }

    private async Task ExecuteGestureCommandAsync(string? command, string? selectedText)
    {
        switch (command)
        {
            case ContextCommandProvider.WorkspaceArrangeCompactActionId:
                ArrangeSelectedCardsCompact();
                return;
            case ContextCommandProvider.WorkspaceRestoreArrangementActionId:
                RestoreArrangement();
                return;
            case ContextCommandProvider.WorkspaceQuickLookActionId:
                await ShowSelectedCardsQuickLookAsync();
                return;
        }

        if (_executeGestureCommandAsync is not null)
        {
            await _executeGestureCommandAsync(command, selectedText);
        }
    }

    public void ConfigureShellFolderTabs(Action<string> openShellFolderInTab)
    {
        WorkspaceCanvas.OpenShellFolderInTab = openShellFolderInTab;
    }

    public void ConfigureLocalVideoPlayer(Action<string> openLocalVideoInMiniPlayer)
    {
        WorkspaceCanvas.OpenLocalVideoInMiniPlayer = openLocalVideoInMiniPlayer;
    }

    public void ConfigureCommandExecution(Func<string, object?, Task> executeCommandAsync)
    {
        WorkspaceCanvas.ConfigureCommandExecution(executeCommandAsync);
    }

    public void ConfigureRecentItems(RecentItemsService service, bool recordActions)
    {
        _recentItemsService = service;
        if (DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.ConfigureRecentItems(service, recordActions);
        }
    }

    public void ConfigureRecentItemOpening(Func<RecentItem, Task> openRecentItemAsync)
    {
        _openRecentItemAsync = openRecentItemAsync;
    }

    public void ConfigureRecentItemThumbnailFetching(
        Func<RecentItem, Task<string>> fetchRecentItemThumbnailAsync)
    {
        _fetchRecentItemThumbnailAsync = fetchRecentItemThumbnailAsync;
    }

    public void ConfigureExternalBrowserOpening(
        Func<ExternalBrowserKind, bool> isExternalBrowserAvailable,
        Func<IReadOnlyList<string>, ExternalBrowserKind, Task> openExternalBrowserAsync)
    {
        _isExternalBrowserAvailable = isExternalBrowserAvailable;
        _openExternalBrowserAsync = openExternalBrowserAsync;
        WorkspaceCanvas.ConfigureExternalBrowserOpening(
            isExternalBrowserAvailable,
            openExternalBrowserAsync);
    }

    public void ConfigureWorkspaceNoteAppend(Func<Guid, Guid, Window?, Task> appendWorkspaceNoteAsync)
    {
        _appendWorkspaceNoteAsync = appendWorkspaceNoteAsync;
        WorkspaceCanvas.EditWorkspaceNoteAsync = appendWorkspaceNoteAsync;
    }

    public void ShowRecentItems()
    {
        if (DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.ShowRecentItems();
        }
    }

    public void ConfigureQuickLook(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        WorkspaceQuickLookPreviewHost.Configure(_quickLookSettings);
        WorkspaceCanvas.ConfigureQuickLook(_quickLookSettings);
    }

    public event EventHandler<QuickLookPaneLayoutChangedEventArgs>? QuickLookPaneLayoutChanged;

    public event EventHandler<QuickLookTextDisplayChangedEventArgs>? QuickLookTextDisplayChanged;

    public void UpdateQuickLookPaneLayout(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        WorkspaceQuickLookPreviewHost.UpdatePaneLayout(_quickLookSettings);
        WorkspaceCanvas.UpdateQuickLookPaneLayout(_quickLookSettings);
    }

    public void UpdateQuickLookSettings(QuickLookSettings settings)
    {
        ConfigureQuickLook(settings);
    }

    public void UpdateGestureSettings(AppSettings settings)
    {
        _gestureController?.UpdateSettings(settings);
    }

    public int SelectedItemCount => WorkspaceCanvas.SelectedItemCount;

    internal IReadOnlyList<QuickLookHostDiagnosticSnapshot> GetQuickLookDiagnosticSnapshots()
    {
        return
        [
            WorkspaceQuickLookPreviewHost.GetDiagnosticSnapshot(),
            WorkspaceCanvas.GetQuickLookDiagnosticSnapshot()
        ];
    }

    public void ArrangeSelectedCardsCompact()
    {
        WorkspaceCanvas.ArrangeSelectedCompact();
    }

    public void ArrangeCards(WorkspaceArrangeRequest request)
    {
        WorkspaceCanvas.ArrangeCards(request);
    }

    public void RestoreArrangement()
    {
        WorkspaceCanvas.RestoreArrangement();
    }

    public Task ShowSelectedCardsQuickLookAsync()
    {
        return WorkspaceCanvas.ShowQuickLookForSelectionAsync();
    }

    public void CancelGesture()
    {
        _gestureController?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        EndInteractiveResizePreview();
        _tabShelfRefreshTimer.Stop();
        _workspaceDragHoverTimer.Stop();
        _workspaceDragHoverTimer.Tick -= WorkspaceDragHoverTimer_Tick;
        StopRecentHoverPopup();
        _recentHoverPopupTimer.Tick -= RecentHoverPopupTimer_Tick;
        _gestureController?.Dispose();
        _gestureController = null;
        WorkspaceQuickLookPreviewHost.Dispose();
        WorkspaceCanvas.Dispose();
        if (DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.Dispose();
        }

        Unloaded -= UserControl_Unloaded;
    }

    public void FocusShortcutScope()
    {
        _ = Dispatcher.BeginInvoke(FocusShortcutScopeCore, DispatcherPriority.Input);
    }

    public void SetActive(bool isActive)
    {
        if (_isDisposed)
        {
            return;
        }

        _isActive = isActive;
        if (!isActive)
        {
            EndInteractiveResizePreview();
        }

        if (DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.SetViewActive(isActive);
        }

        if (!isActive)
        {
            StopInactiveWorkspaceActivity();
            return;
        }

        if (IsLoaded)
        {
            RefreshOpenTabs();
            _tabShelfRefreshTimer.Start();
        }
    }

    internal bool BeginInteractiveResizePreview()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_isInteractiveResizePreviewActive)
        {
            return true;
        }

        _resumeTabShelfRefreshAfterInteractiveResize = _tabShelfRefreshTimer.IsEnabled;
        // 寸法は親の通常レイアウトに任せる。SizeChanged後の寸法書き戻しは
        // 同じ入力で再レイアウトを発生させ、描画の追従を遅らせる。
        _tabShelfRefreshTimer.Stop();
        _isInteractiveResizePreviewActive = true;
        return true;
    }

    internal void EndInteractiveResizePreview()
    {
        if (!_isInteractiveResizePreviewActive)
        {
            return;
        }

        _isInteractiveResizePreviewActive = false;
        if (_resumeTabShelfRefreshAfterInteractiveResize
            && !_isDisposed
            && _isActive
            && IsLoaded)
        {
            _tabShelfRefreshTimer.Start();
        }

        _resumeTabShelfRefreshAfterInteractiveResize = false;
    }

    public void RefreshOpenTabs()
    {
        if (DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.RefreshOpenTabs();
        }
    }

    public void ReloadWorkspace()
    {
        if (DataContext is WorkspaceOSViewModel viewModel
            && viewModel.ReloadWorkspaceCommand.CanExecute(null))
        {
            viewModel.ReloadWorkspaceCommand.Execute(null);
        }
    }

    public bool TrySaveDroppedTabs(IReadOnlyList<CapturedTabSnapshot> tabs, Point pointInView)
    {
        if (tabs.Count == 0
            || !IsPointInside(pointInView, this))
        {
            return false;
        }

        var pointInCanvas = TranslatePoint(pointInView, WorkspaceCanvas);
        return WorkspaceCanvas.TrySaveDroppedTabs(tabs, pointInCanvas);
    }

    private void FocusShortcutScopeCore()
    {
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), this);
        Focus();
        Keyboard.Focus(this);
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isActive)
        {
            FocusShortcutScope();
        }

        if (DataContext is not WorkspaceOSViewModel viewModel)
        {
            return;
        }

        if (!_initialized)
        {
            _initialized = true;
            await viewModel.InitializeAsync();
            if (_isDisposed)
            {
                return;
            }

            viewModel.StartWorkspacePreload();
        }

        if (_isActive)
        {
            RefreshOpenTabs();
            _tabShelfRefreshTimer.Start();
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        StopInactiveWorkspaceActivity();
    }

    private void StopInactiveWorkspaceActivity()
    {
        _tabShelfRefreshTimer.Stop();
        StopWorkspaceDragHover();
        StopRecentHoverPopup();
        CloseWorkspaceLevelQuickLook();
        WorkspaceCanvas.CloseQuickLook();
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WorkspaceQuickLookPreviewHost.IsOpen)
        {
            CloseWorkspaceLevelQuickLook();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not ButtonBase
            && !IsTextInputFocused())
        {
            if (WorkspaceSearchResultsList.IsKeyboardFocusWithin)
            {
                _ = ShowSearchQuickLookAsync();
                e.Handled = true;
                return;
            }

            if (RecentItemsList.IsKeyboardFocusWithin)
            {
                StopRecentHoverPopup();
                _ = ShowRecentQuickLookAsync();
                e.Handled = true;
                return;
            }

            if (TabShelf.IsKeyboardFocusWithin)
            {
                _ = ShowInboxQuickLookAsync();
                e.Handled = true;
                return;
            }
        }

        if (WorkspaceCanvas.TryHandleQuickLookKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (TryHandleShortcutKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void UserControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not WorkspaceOSViewModel viewModel
            || !viewModel.IsWorkspaceSidePanelVisible
            || IsPointerInside(e, HyperSearchPanel)
            || IsPointerInside(e, RecentItemsPanel)
            || IsPointerInside(e, HyperSearchToggleButton)
            || IsPointerInside(e, RecentItemsToggleButton))
        {
            return;
        }

        StopRecentHoverPopup();
        viewModel.CloseSidePanels();
    }

    private async void WorkspaceSearchPreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowSearchQuickLookAsync();
    }

    private void WorkspaceSearchResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
        }
    }

    private void WorkspaceSearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressWorkspaceLevelQuickLookSelectionRefresh
            && WorkspaceQuickLookPreviewHost.IsOpen
            && _activeQuickLookSourceId == _searchQuickLookSource.SourceId)
        {
            _ = ShowSearchQuickLookAsync();
        }
    }

    private void WorkspaceSearchResult_Click(object sender, RoutedEventArgs e)
    {
        CloseWorkspaceLevelQuickLook();
    }

    private void RecentItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressWorkspaceLevelQuickLookSelectionRefresh
            && WorkspaceQuickLookPreviewHost.IsOpen
            && _activeQuickLookSourceId == _recentQuickLookSource.SourceId)
        {
            _ = ShowRecentQuickLookAsync();
        }
    }

    private void RecentItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
        }
    }

    private void RecentItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentItem item)
        {
            return;
        }

        StopRecentHoverPopup();
        _recentHoverPopupItem = item;
        _recentHoverPopupTarget = (FrameworkElement)sender;
        _recentHoverPopupTimer.Start();
    }

    private void RecentItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem item
            && ReferenceEquals(_recentHoverPopupItem, item))
        {
            StopRecentHoverPopup();
        }
    }

    private async void RecentItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2
            || (sender as FrameworkElement)?.DataContext is not RecentItem item)
        {
            return;
        }

        await OpenRecentItemAsync(item);
        e.Handled = true;
    }

    private async void RecentItemAppendButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem item)
        {
            await AppendRecentNoteAsync(item);
            e.Handled = true;
        }
    }

    private async void RecentPreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StopRecentHoverPopup();
        await ShowRecentQuickLookAsync();
    }

    private async void RecentOpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem item)
        {
            await OpenRecentItemAsync(item);
        }
    }

    private void RecentItemsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var browserMenu = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(static item =>
                string.Equals(item.Tag as string, "ExternalBrowserMenu", StringComparison.Ordinal));
        if (browserMenu is null)
        {
            return;
        }

        var canOpen = menu.DataContext is RecentItem item
            && ExternalBrowserService.IsSupportedWebUrl(item.Target);
        browserMenu.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!canOpen)
        {
            return;
        }

        foreach (var choice in browserMenu.Items.OfType<MenuItem>())
        {
            choice.IsEnabled = choice.Tag is string browserName
                && Enum.TryParse<ExternalBrowserKind>(browserName, out var browserKind)
                && _isExternalBrowserAvailable?.Invoke(browserKind) == true;
        }

        browserMenu.IsEnabled = browserMenu.Items
            .OfType<MenuItem>()
            .Any(static item => item.IsEnabled);
    }

    private async void RecentOpenExternalBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string browserName }
            && Enum.TryParse<ExternalBrowserKind>(browserName, out var browserKind)
            && RecentItemsList.SelectedItem is RecentItem item
            && ExternalBrowserService.IsSupportedWebUrl(item.Target)
            && _openExternalBrowserAsync is not null)
        {
            await _openExternalBrowserAsync([item.Target], browserKind);
        }
    }

    private async void RecentAppendMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem item)
        {
            await AppendRecentNoteAsync(item);
        }
    }

    private async void RecentCopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem { CopyText.Length: > 0 } item)
        {
            var result = await _clipboardWriteService.SetTextAsync(item.CopyText);
            if (!result.IsSuccess)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    "クリップボードを使用できませんでした。少し待ってから再試行してください。",
                    "コピー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private async void RecentShowInWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem item
            && DataContext is WorkspaceOSViewModel viewModel)
        {
            await viewModel.ShowRecentWorkspaceItemAsync(item);
        }
    }

    private void RecentTogglePinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem item
            && DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.ToggleRecentPinCommand.Execute(item);
        }
    }

    private void RecentRemoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItemsList.SelectedItem is RecentItem item
            && DataContext is WorkspaceOSViewModel viewModel)
        {
            viewModel.RemoveRecentItemCommand.Execute(item);
        }
    }

    private async void TabShelf_QuickLookRequested(object? sender, EventArgs e)
    {
        await ShowInboxQuickLookAsync();
    }

    private void TabShelf_QuickLookSelectionChanged(object? sender, EventArgs e)
    {
        if (!_suppressWorkspaceLevelQuickLookSelectionRefresh
            && WorkspaceQuickLookPreviewHost.IsOpen
            && _activeQuickLookSourceId == _inboxQuickLookSource.SourceId)
        {
            _ = ShowInboxQuickLookAsync();
        }
    }

    private void TabShelf_TabTransferRequested(object? sender, TabShelfTabTransferRequestedEventArgs e)
    {
        TabShelfTabTransferRequested?.Invoke(this, e);
    }

    private async Task ShowSearchQuickLookAsync()
    {
        if (!_quickLookSettings.IsEnabled
            || !_quickLookSettings.EnableInUnifiedSearch
            || _searchQuickLookSource.Current is null)
        {
            return;
        }

        WorkspaceCanvas.CloseQuickLook();
        _activeQuickLookSourceId = _searchQuickLookSource.SourceId;
        await WorkspaceQuickLookPreviewHost.ShowAsync(new QuickLookRequest(
            [_searchQuickLookSource.Current],
            _searchQuickLookSource.SourceId,
            _searchQuickLookSource));
    }

    private async Task ShowInboxQuickLookAsync()
    {
        if (!_quickLookSettings.IsEnabled
            || !_quickLookSettings.EnableInWorkspaceInbox
            || _inboxQuickLookSource.Current is null)
        {
            return;
        }

        WorkspaceCanvas.CloseQuickLook();
        _activeQuickLookSourceId = _inboxQuickLookSource.SourceId;
        await WorkspaceQuickLookPreviewHost.ShowAsync(new QuickLookRequest(
            [_inboxQuickLookSource.Current],
            _inboxQuickLookSource.SourceId,
            _inboxQuickLookSource));
    }

    private async Task ShowRecentQuickLookAsync()
    {
        if (!_quickLookSettings.IsEnabled
            || !_quickLookSettings.EnableInUnifiedSearch
            || RecentItemsList.SelectedItem is not RecentItem item)
        {
            return;
        }

        WorkspaceCanvas.CloseQuickLook();
        _activeQuickLookSourceId = _recentQuickLookSource.SourceId;
        await WorkspaceQuickLookPreviewHost.ShowAsync(new QuickLookRequest(
            [CreateRecentQuickLookItem(item)],
            _recentQuickLookSource.SourceId,
            _recentQuickLookSource));

        var thumbnailPngBase64 = await FetchRecentItemThumbnailAsync(item);
        if (string.IsNullOrWhiteSpace(thumbnailPngBase64))
        {
            return;
        }

        item.ThumbnailPngBase64 = thumbnailPngBase64;
        if (WorkspaceQuickLookPreviewHost.IsOpen
            && _activeQuickLookSourceId == _recentQuickLookSource.SourceId
            && ReferenceEquals(RecentItemsList.SelectedItem, item))
        {
            await WorkspaceQuickLookPreviewHost.ShowAsync(new QuickLookRequest(
                [CreateRecentQuickLookItem(item)],
                _recentQuickLookSource.SourceId,
                _recentQuickLookSource));
        }

        PersistRecentItemThumbnail(item, thumbnailPngBase64);
    }

    private void CloseWorkspaceLevelQuickLook()
    {
        WorkspaceQuickLookPreviewHost.Close();
        _activeQuickLookSourceId = null;
    }

    private IReadOnlyList<QuickLookItem> CreateSearchQuickLookItems()
    {
        return DataContext is WorkspaceOSViewModel viewModel
            ? viewModel.SearchResults
                .Select(CreateSearchQuickLookItem)
                .ToList()
            : [];
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedSearchQuickLookItems()
    {
        return WorkspaceSearchResultsList.SelectedItem is WorkspaceSearchResultViewModel result
            ? [CreateSearchQuickLookItem(result)]
            : [];
    }

    private QuickLookItem CreateSearchQuickLookItem(WorkspaceSearchResultViewModel result)
    {
        return QuickLookItemFactory.FromWorkspaceItem(
            result.Item,
            _searchQuickLookSource.SourceId,
            result.Title,
            CreateSearchQuickLookActions(result));
    }

    private IReadOnlyList<QuickLookAction> CreateSearchQuickLookActions(WorkspaceSearchResultViewModel result)
    {
        var showInWorkspaceAction = new QuickLookAction(
            "workspace.show",
            "Workspaceで表示",
            () => DataContext is WorkspaceOSViewModel viewModel
                ? viewModel.ShowSearchResultAsync(result)
                : Task.CompletedTask,
            IsPrimary: result.Item.Type != Models.Workspaces.WorkspaceItemType.Note);
        if (result.Item.Type != Models.Workspaces.WorkspaceItemType.Note)
        {
            return [showInWorkspaceAction];
        }

        return
        [
            new QuickLookAction(
                "workspace.note.append",
                "追記",
                () => AppendWorkspaceNoteAsync(result.WorkspaceId, result.ItemId),
                IsPrimary: true),
            showInWorkspaceAction
        ];
    }

    private IReadOnlyList<QuickLookItem> CreateRecentQuickLookItems()
    {
        return DataContext is WorkspaceOSViewModel viewModel
            ? viewModel.RecentItems.Select(CreateRecentQuickLookItem).ToList()
            : [];
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedRecentQuickLookItems()
    {
        return RecentItemsList.SelectedItem is RecentItem item
            ? [CreateRecentQuickLookItem(item)]
            : [];
    }

    private QuickLookItem CreateRecentQuickLookItem(RecentItem item)
    {
        var actions = CreateRecentQuickLookActions(item);
        if (item.IsNote)
        {
            return new QuickLookItem
            {
                DisplayName = item.Title,
                Kind = QuickLookItemKind.Text,
                InlineText = item.PreviewText,
                SourceId = _recentQuickLookSource.SourceId,
                Actions = actions
            };
        }

        return QuickLookItemFactory.FromAddress(
            item.Title,
            item.Target,
            _recentQuickLookSource.SourceId,
            string.IsNullOrWhiteSpace(item.PreviewText)
                ? item.WorkspaceName
                : item.PreviewText,
            actions,
            item.ThumbnailPngBase64,
            faviconPngBase64: item.FaviconPngBase64);
    }

    private IReadOnlyList<QuickLookAction> CreateRecentQuickLookActions(RecentItem item)
    {
        var actions = new List<QuickLookAction>();
        if (item.IsNote)
        {
            actions.Add(new QuickLookAction(
                "workspace.note.append",
                "追記",
                () => AppendRecentNoteAsync(item),
                IsPrimary: true));
        }
        else
        {
            actions.Add(new QuickLookAction(
                "recent.open",
                "開く",
                () => OpenRecentItemAsync(item),
                IsPrimary: true));
        }

        if (item.IsWorkspaceItem)
        {
            actions.Add(new QuickLookAction(
                "workspace.show",
                "Workspaceで表示",
                () => DataContext is WorkspaceOSViewModel viewModel
                    ? viewModel.ShowRecentWorkspaceItemAsync(item)
                    : Task.CompletedTask));
        }

        return actions;
    }

    private void SelectSearchQuickLookIndex(int index)
    {
        if (index < 0 || index >= WorkspaceSearchResultsList.Items.Count)
        {
            return;
        }

        _suppressWorkspaceLevelQuickLookSelectionRefresh = true;
        try
        {
            WorkspaceSearchResultsList.SelectedIndex = index;
            WorkspaceSearchResultsList.ScrollIntoView(WorkspaceSearchResultsList.SelectedItem);
        }
        finally
        {
            _suppressWorkspaceLevelQuickLookSelectionRefresh = false;
        }
    }

    private void SelectRecentQuickLookIndex(int index)
    {
        if (index < 0 || index >= RecentItemsList.Items.Count)
        {
            return;
        }

        _suppressWorkspaceLevelQuickLookSelectionRefresh = true;
        try
        {
            RecentItemsList.SelectedIndex = index;
            RecentItemsList.ScrollIntoView(RecentItemsList.SelectedItem);
        }
        finally
        {
            _suppressWorkspaceLevelQuickLookSelectionRefresh = false;
        }
    }

    private void RecentHoverPopupTimer_Tick(object? sender, EventArgs e)
    {
        _recentHoverPopupTimer.Stop();
        if (_recentHoverPopupItem is not null
            && _recentHoverPopupTarget?.IsMouseOver == true)
        {
            ShowRecentHoverPopup(_recentHoverPopupItem, _recentHoverPopupTarget);
            _ = RefreshRecentHoverThumbnailAsync(
                _recentHoverPopupItem,
                _recentHoverPopupTarget);
        }
    }

    private void ShowRecentHoverPopup(RecentItem item, FrameworkElement target)
    {
        var popup = new ToolTip
        {
            Placement = PlacementMode.Custom,
            PlacementTarget = target,
            CustomPopupPlacementCallback = static (popupSize, targetSize, _) =>
                WorkspaceCanvasView.CreateWorkspaceToolTipPlacements(
                    popupSize,
                    targetSize,
                    preferRight: false),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
            StaysOpen = true,
            Content = CreateRecentHoverPopupContent(item, CreateRecentHoverWebResult(item))
        };
        _recentHoverPopup = popup;
        popup.IsOpen = true;
    }

    private async Task RefreshRecentHoverThumbnailAsync(RecentItem item, FrameworkElement target)
    {
        var thumbnailPngBase64 = await FetchRecentItemThumbnailAsync(item);
        if (string.IsNullOrWhiteSpace(thumbnailPngBase64))
        {
            return;
        }

        item.ThumbnailPngBase64 = thumbnailPngBase64;
        if (ReferenceEquals(_recentHoverPopupItem, item)
            && ReferenceEquals(_recentHoverPopupTarget, target)
            && _recentHoverPopup?.IsOpen == true)
        {
            _recentHoverPopup.Content = CreateRecentHoverPopupContent(
                item,
                CreateRecentHoverWebResult(item));
            _pendingRecentThumbnailItem = item with { };
            _pendingRecentThumbnailPngBase64 = thumbnailPngBase64;
            return;
        }

        PersistRecentItemThumbnail(item, thumbnailPngBase64);
    }

    private QuickLookRenderResult? CreateRecentHoverWebResult(RecentItem item)
    {
        var quickLookItem = CreateRecentQuickLookItem(item);
        return quickLookItem.Uri is not null
            ? QuickLookWebSummaryRenderer.CreateCachedResult(quickLookItem)
            : null;
    }

    private async Task<string> FetchRecentItemThumbnailAsync(RecentItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPngBase64)
            || item.IsNote
            || _fetchRecentItemThumbnailAsync is null)
        {
            return string.Empty;
        }

        try
        {
            return await _fetchRecentItemThumbnailAsync(item);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PersistRecentItemThumbnail(RecentItem item, string thumbnailPngBase64)
    {
        if (!string.IsNullOrWhiteSpace(thumbnailPngBase64))
        {
            _recentItemsService?.UpdateThumbnail(item, thumbnailPngBase64);
        }
    }

    private static FrameworkElement CreateRecentHoverPopupContent(
        RecentItem item,
        QuickLookRenderResult? webResult)
    {
        var border = new Border
        {
            Width = 320,
            MaxHeight = 300,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        var content = new StackPanel();
        if (webResult is not null)
        {
            var thumbnailBorder = new Border
            {
                Height = 160,
                Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x13)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x29, 0x31, 0x3C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };
            thumbnailBorder.Child = webResult.PreviewImage is { } thumbnail
                ? new Image
                {
                    Source = thumbnail,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(5)
                }
                : new TextBlock
                {
                    Text = "サムネイルはまだありません",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            content.Children.Add(thumbnailBorder);
        }

        content.Children.Add(new TextBlock
        {
            Text = webResult?.Title ?? item.Title,
            MaxHeight = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 18,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        content.Children.Add(new TextBlock
        {
            Text = webResult?.Subtitle ?? item.CategoryLabel,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (webResult is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = item.IsNote ? item.PreviewText : item.Target,
                MaxHeight = 120,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                FontSize = 11,
                LineHeight = 17,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap
            });
        }

        border.Child = content;
        return border;
    }

    private void StopRecentHoverPopup()
    {
        _recentHoverPopupTimer.Stop();
        if (_recentHoverPopup is not null)
        {
            _recentHoverPopup.IsOpen = false;
            _recentHoverPopup.Content = null;
            _recentHoverPopup = null;
        }

        _recentHoverPopupItem = null;
        _recentHoverPopupTarget = null;
        var pendingItem = _pendingRecentThumbnailItem;
        var pendingThumbnail = _pendingRecentThumbnailPngBase64;
        _pendingRecentThumbnailItem = null;
        _pendingRecentThumbnailPngBase64 = null;
        if (pendingItem is not null
            && !string.IsNullOrWhiteSpace(pendingThumbnail))
        {
            PersistRecentItemThumbnail(pendingItem, pendingThumbnail);
        }
    }

    private Task OpenRecentItemAsync(RecentItem item)
    {
        StopRecentHoverPopup();
        return item.IsNote
            ? AppendRecentNoteAsync(item)
            : _openRecentItemAsync?.Invoke(item) ?? Task.CompletedTask;
    }

    private Task AppendRecentNoteAsync(RecentItem item)
    {
        return item.WorkspaceId is { } workspaceId && item.WorkspaceItemId is { } itemId
            ? AppendWorkspaceNoteAsync(workspaceId, itemId)
            : Task.CompletedTask;
    }

    private Task AppendWorkspaceNoteAsync(Guid workspaceId, Guid itemId)
    {
        return _appendWorkspaceNoteAsync?.Invoke(workspaceId, itemId, Window.GetWindow(this))
            ?? Task.CompletedTask;
    }

    private void SelectInboxQuickLookIndex(int index)
    {
        _suppressWorkspaceLevelQuickLookSelectionRefresh = true;
        try
        {
            TabShelf.SelectQuickLookIndex(index);
        }
        finally
        {
            _suppressWorkspaceLevelQuickLookSelectionRefresh = false;
        }
    }

    private IReadOnlyList<QuickLookItem> CreateInboxQuickLookItems()
    {
        return TabShelf.GetQuickLookItems().Select(CreateInboxQuickLookItem).ToList();
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedInboxQuickLookItems()
    {
        return TabShelf.GetQuickLookSelectedItem() is { } item
            ? [CreateInboxQuickLookItem(item)]
            : [];
    }

    private QuickLookItem CreateInboxQuickLookItem(TabShelfTabViewModel item)
    {
        return QuickLookItemFactory.FromAddress(
            item.Title,
            item.Url,
            _inboxQuickLookSource.SourceId,
            item.SecondaryText,
            thumbnail: item.Thumbnail,
            faviconPngBase64: item.Snapshot?.FaviconPngBase64,
            favicon: item.Favicon);
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void WorkspaceNumbers_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TrySwitchWorkspaceByWheelDelta(e.Delta))
        {
            return;
        }

        e.Handled = true;
    }

    internal bool TrySwitchWorkspaceByWheelDelta(int wheelDelta)
    {
        if (wheelDelta == 0
            || DataContext is not WorkspaceOSViewModel viewModel
            || viewModel.Workspaces.Count < 2)
        {
            return false;
        }

        var offset = wheelDelta > 0 ? -1 : 1;
        return viewModel.QueueWorkspaceWheelSwitch(offset);
    }

    private bool TryHandleControlRightWheel(int wheelDelta)
    {
        return Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && TrySwitchWorkspaceByWheelDelta(wheelDelta);
    }

    private async void RenameWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: WorkspaceSlotViewModel workspace }
            || DataContext is not WorkspaceOSViewModel viewModel)
        {
            return;
        }

        var dialog = new WorkspaceTextInputWindow(
            "Workspace名を変更",
            $"Workspace {workspace.Index} の名前",
            workspace.Name)
        {
            Owner = Window.GetWindow(this)
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (dialog.IsAccepted
            && IsLoaded
            && viewModel.Workspaces.Contains(workspace))
        {
            viewModel.RenameWorkspaceCommand.Execute(
                new WorkspaceNameChangeRequest(workspace, dialog.Value));
        }
    }

    private void WorkspaceNumber_DragEnter(object sender, DragEventArgs e)
    {
        if (!TryGetWorkspaceCardDrag(sender, e, out _, out var target))
        {
            return;
        }

        StartWorkspaceDragHover((Button)sender, target);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void WorkspaceNumber_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetWorkspaceCardDrag(sender, e, out _, out var target))
        {
            return;
        }

        StartWorkspaceDragHover((Button)sender, target);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void WorkspaceNumber_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceSlotViewModel target }
            && _workspaceDragHoverTarget?.Id == target.Id)
        {
            StopWorkspaceDragHover();
        }
    }

    private void WorkspaceNumber_Drop(object sender, DragEventArgs e)
    {
        StopWorkspaceDragHover();
        if (!TryGetWorkspaceCardDrag(sender, e, out var payload, out var target)
            || DataContext is not WorkspaceOSViewModel viewModel)
        {
            return;
        }

        var point = WorkspaceCanvas.GetDefaultCardDropPoint(payload);
        viewModel.MoveDraggedItemsCommand.Execute(new WorkspaceCardDropRequest(
            payload,
            target.Id,
            point));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void WorkspaceDragHoverTimer_Tick(object? sender, EventArgs e)
    {
        _workspaceDragHoverTimer.Stop();
        var target = _workspaceDragHoverTarget;
        if (target is null
            || DataContext is not WorkspaceOSViewModel viewModel
            || viewModel.CurrentWorkspace?.Id == target.Id)
        {
            return;
        }

        if (_workspaceDragHoverSwitching)
        {
            _workspaceDragHoverTimer.Start();
            return;
        }

        _workspaceDragHoverSwitching = true;
        try
        {
            await viewModel.LoadWorkspaceAsync(target.Id);
        }
        finally
        {
            _workspaceDragHoverSwitching = false;
        }

        if (_workspaceDragHoverTarget is { } nextTarget
            && viewModel.CurrentWorkspace?.Id != nextTarget.Id)
        {
            _workspaceDragHoverTimer.Start();
        }
    }

    private void StartWorkspaceDragHover(Button button, WorkspaceSlotViewModel target)
    {
        if (_workspaceDragHoverTarget?.Id == target.Id)
        {
            if (DataContext is WorkspaceOSViewModel viewModel
                && viewModel.CurrentWorkspace?.Id != target.Id
                && !_workspaceDragHoverTimer.IsEnabled
                && !_workspaceDragHoverSwitching)
            {
                _workspaceDragHoverTimer.Start();
            }

            return;
        }

        StopWorkspaceDragHover();
        _workspaceDragHoverTarget = target;
        _workspaceDragHoverButton = button;
        _workspaceDragHoverButton.Tag = "WorkspaceCardDragOver";
        if (DataContext is WorkspaceOSViewModel currentViewModel
            && currentViewModel.CurrentWorkspace?.Id != target.Id)
        {
            _workspaceDragHoverTimer.Start();
        }
    }

    private void StopWorkspaceDragHover()
    {
        _workspaceDragHoverTimer.Stop();
        _workspaceDragHoverTarget = null;
        _workspaceDragHoverButton?.ClearValue(TagProperty);
        _workspaceDragHoverButton = null;
    }

    private static bool TryGetWorkspaceCardDrag(
        object sender,
        DragEventArgs e,
        out WorkspaceCardDragPayload payload,
        out WorkspaceSlotViewModel target)
    {
        payload = null!;
        target = null!;
        if (sender is not Button { DataContext: WorkspaceSlotViewModel workspace }
            || e.Data.GetData(WorkspaceCardDragPayload.DataFormat) is not WorkspaceCardDragPayload cardPayload)
        {
            return false;
        }

        payload = cardPayload;
        target = workspace;
        return true;
    }

    public bool TryHandleShortcutKey(Key key, bool ignoreTextInputFocus = false)
    {
        if (Keyboard.Modifiers != ModifierKeys.None
            || (!ignoreTextInputFocus && IsTextInputFocused()))
        {
            return false;
        }

        if (key is Key.D0 or Key.NumPad0)
        {
            WorkspaceCanvas.ResetPanOffset();
            return true;
        }

        var workspaceIndex = GetWorkspaceIndex(key);
        if (workspaceIndex is null || DataContext is not WorkspaceOSViewModel viewModel)
        {
            return false;
        }

        var target = viewModel.Workspaces.FirstOrDefault(x => x.Index == workspaceIndex.Value);
        if (target is null || !viewModel.SwitchWorkspaceCommand.CanExecute(target))
        {
            return false;
        }

        viewModel.SwitchWorkspaceCommand.Execute(target);
        return true;
    }

    private static int? GetWorkspaceIndex(Key key)
    {
        return key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => null
        };
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase
            or PasswordBox
            or ComboBox { IsEditable: true };
    }

    private static bool IsPointInside(Point point, FrameworkElement element)
    {
        return point.X >= 0
            && point.Y >= 0
            && point.X <= element.ActualWidth
            && point.Y <= element.ActualHeight;
    }

    private static bool IsPointerInside(MouseButtonEventArgs e, FrameworkElement element)
    {
        return element.IsVisible
            && IsPointInside(e.GetPosition(element), element);
    }
}
