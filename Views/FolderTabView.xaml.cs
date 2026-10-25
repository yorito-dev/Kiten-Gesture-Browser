using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

public partial class FolderTabView : UserControl, IDisposable, IFolderTabFileDropTarget, IFolderTabOpenView
{
    private const double IconTileOuterWidth = 118;
    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const string FolderFileDragFormat = "TabBrowser.FolderTabFileTransfer";

    private readonly FolderTabService _service = new();
    private readonly ButtonContextMenuController _buttonContextMenuController = new();
    private readonly WorkspaceShellItemService _shellItemService = new();
    private readonly FolderTabIconService _iconService = new();
    private readonly string _initialPath;
    private readonly List<string> _history = [];
    private readonly Dictionary<string, FolderTabItemViewModel> _itemsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderTabItemViewModel> _allItemsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FolderTabItemViewModel, CancellationTokenSource> _visibleIconLoads = [];
    private readonly Dictionary<FolderTabItemViewModel, CancellationTokenSource> _visibleDetailIconLoads = [];
    private readonly DispatcherTimer _iconLayoutTimer;
    private readonly DispatcherTimer _folderWatcherTimer;
    private readonly FolderTabQuickLookItemSource _quickLookItemSource;
    private readonly IReadOnlyList<string> _initialSelectedPaths;
    private readonly string? _initialSelectionAnchorPath;
    private WorkspaceGestureController? _gestureController;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _recursiveSearchCancellation;
    private CancellationTokenSource? _iconLoadCancellation = new();
    private CancellationTokenSource? _fileTransferCancellation;
    private FileSystemWatcher? _folderWatcher;
    private IReadOnlyList<FolderTabIconRow> _iconRows = [];
    private string _viewMode;
    private string _sortMode;
    private string _sortDirection;
    private bool _showHiddenItems;
    private QuickLookSettings _quickLookSettings;
    private string? _selectionAnchorPath;
    private int _historyIndex = -1;
    private int _iconColumnCount = 1;
    private int _pendingIconColumnCount = 1;
    private double _detailScrollOffset;
    private double _iconScrollOffset;
    private bool _isSynchronizingSelection;
    private bool _isInitialLoadStarted;
    private bool _hasLoadedContent;
    private bool _isDisposed;
    private Action<string>? _openLocalVideoInMiniPlayer;
    private Action<string>? _recordRecentItemOpened;
    private Func<string, object?, Task>? _executeCommandAsync;
    private Func<IReadOnlyList<string>, Task>? _savePathsToWorkspaceAsync;
    private Func<FolderTabPlacesSnapshot>? _getPlacesSnapshot;
    private Func<string?, IReadOnlyList<FolderTabPathSuggestion>>? _getPathSuggestions;
    private Action<string, bool>? _setPlacePinned;
    private Action<string, int>? _movePinnedPlace;
    private Func<FolderTabSplitOpenMode>? _getSplitOpenMode;
    private IReadOnlyList<ShellFileOperationResult> _lastFileOperations = [];
    private FolderTabChangeStamp _directoryChangeStamp;
    private long? _availableFreeSpace;
    private bool _isListingTruncated;
    private DateTime _ignoreWatcherEventsUntilUtc;
    private bool _isActivationCheckRunning;
    private bool _isConflictResolutionOpen;
    private bool _isRecursiveSearchMode;
    private bool _hasPreRecursiveState;
    private Point? _fileDragStartPoint;
    private FolderTabItemViewModel? _fileDragStartItem;
    private FolderTabItemViewModel? _dropTargetItem;
    private IReadOnlyList<string> _failedTransferPaths = [];
    private string _failedTransferDestination = string.Empty;
    private bool _failedTransferMove;
    private IReadOnlyList<string> _preRecursiveSelectedPaths = [];
    private string? _preRecursiveSelectionAnchorPath;
    private double _preRecursiveDetailScrollOffset;
    private double _preRecursiveIconScrollOffset;

    public FolderTabView(
        string initialPath,
        string? initialViewMode = null,
        QuickLookSettings? quickLookSettings = null,
        string? initialSortMode = null,
        string? initialSortDirection = null,
        FolderTabSessionState? initialState = null,
        bool initialShowHiddenItems = false)
    {
        _initialPath = FolderTabService.NormalizeExistingDirectory(initialPath);
        _viewMode = FolderTabViewMode.Normalize(initialViewMode);
        _sortMode = FolderTabSortMode.Normalize(initialSortMode);
        _sortDirection = FolderTabSortDirection.Normalize(initialSortDirection);
        _showHiddenItems = initialShowHiddenItems;
        _quickLookSettings = QuickLookSettingsProfile.Normalize(quickLookSettings);
        _history.AddRange(NormalizeSessionPaths(initialState?.NavigationHistory));
        var initialHistoryIndex = initialState?.NavigationHistoryIndex ?? -1;
        _historyIndex = _history.Count == 0
            ? -1
            : initialHistoryIndex >= 0
                ? Math.Clamp(initialHistoryIndex, 0, _history.Count - 1)
                : _history.Count - 1;
        FolderTabService.TrimNavigationHistory(_history, ref _historyIndex);
        _initialSelectedPaths = NormalizeSessionPaths(initialState?.SelectedPaths);
        _initialSelectionAnchorPath = NormalizeOptionalPath(initialState?.SelectionAnchorPath);
        _detailScrollOffset = NormalizeScrollOffset(initialState?.DetailScrollOffset ?? 0);
        _iconScrollOffset = NormalizeScrollOffset(initialState?.IconScrollOffset ?? 0);
        InitializeComponent();
        ApplyDetailColumnOrder(initialState?.DetailColumnOrder);
        UpdateHiddenItemsButton();
        QuickLookPreviewHost.ConfigureDockTarget(FolderItemsContent);
        QuickLookPreviewHost.PaneLayoutChanged += (_, args) =>
            QuickLookPaneLayoutChanged?.Invoke(this, args);
        QuickLookPreviewHost.TextDisplayChanged += (_, args) =>
            QuickLookTextDisplayChanged?.Invoke(this, args);
        QuickLookPreviewHost.Configure(_quickLookSettings);
        _quickLookItemSource = new FolderTabQuickLookItemSource(
            $"folder-tab:{Guid.NewGuid():N}",
            CreateQuickLookItems,
            CreateSelectedQuickLookItems,
            GetQuickLookCurrentIndex,
            SelectQuickLookIndex);
        _iconLayoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _iconLayoutTimer.Tick += IconLayoutTimer_Tick;
        _folderWatcherTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _folderWatcherTimer.Tick += FolderWatcherTimer_Tick;
        ApplyViewMode();
        UpdateSortControls();
        Loaded += FolderTabView_Loaded;
        Unloaded += FolderTabView_Unloaded;
        IsVisibleChanged += FolderTabView_IsVisibleChanged;
    }

    public ObservableCollection<FolderTabItemViewModel> Items { get; } = [];

    public ObservableCollection<FolderTabPathSuggestion> PathSuggestions { get; } = [];

    public string CurrentPath { get; private set; } = string.Empty;

    public string ViewMode => _viewMode;

    public string SortMode => _sortMode;

    public string SortDirection => _sortDirection;

    public int SelectedItemCount => _selectedPaths.Count;

    public bool CanNavigateBack => _historyIndex > 0;

    public bool CanNavigateForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public event EventHandler<FolderTabNavigationEventArgs>? NavigationChanged;

    public event EventHandler<FolderTabOpenRequestedEventArgs>? OpenFolderRequested;

    public event EventHandler<FolderTabViewModeChangedEventArgs>? ViewModeChanged;

    public event EventHandler<FolderTabSortChangedEventArgs>? SortChanged;

    public event EventHandler? DetailColumnOrderChanged;

    public event EventHandler<FolderTabHiddenItemsVisibilityChangedEventArgs>? HiddenItemsVisibilityChanged;

    public event EventHandler<QuickLookPaneLayoutChangedEventArgs>? QuickLookPaneLayoutChanged;

    public event EventHandler<QuickLookTextDisplayChangedEventArgs>? QuickLookTextDisplayChanged;

    public void ConfigureRecentItemRecording(Action<string> recordRecentItemOpened)
    {
        _recordRecentItemOpened = recordRecentItemOpened;
    }

    internal bool IsDisposedForDiagnostics => _isDisposed;

    internal QuickLookHostDiagnosticSnapshot GetQuickLookDiagnosticSnapshot()
    {
        return QuickLookPreviewHost.GetDiagnosticSnapshot();
    }

    public void ConfigureLocalVideoPlayer(Action<string> openLocalVideoInMiniPlayer)
    {
        _openLocalVideoInMiniPlayer = openLocalVideoInMiniPlayer;
    }

    public void ConfigureCommandExecution(Func<string, object?, Task> executeCommandAsync)
    {
        _executeCommandAsync = executeCommandAsync;
    }

    public void ConfigureWorkspaceSave(Func<IReadOnlyList<string>, Task> savePathsToWorkspaceAsync)
    {
        _savePathsToWorkspaceAsync = savePathsToWorkspaceAsync;
    }

    public void ConfigurePlaces(
        Func<FolderTabPlacesSnapshot> getPlacesSnapshot,
        Func<string?, IReadOnlyList<FolderTabPathSuggestion>> getPathSuggestions,
        Action<string, bool> setPlacePinned,
        Action<string, int> movePinnedPlace)
    {
        _getPlacesSnapshot = getPlacesSnapshot;
        _getPathSuggestions = getPathSuggestions;
        _setPlacePinned = setPlacePinned;
        _movePinnedPlace = movePinnedPlace;
    }

    public void ConfigureSplitFolderOpening(Func<FolderTabSplitOpenMode> getSplitOpenMode)
    {
        _getSplitOpenMode = getSplitOpenMode;
    }

    public bool CanAcceptTabHeaderFileDrop => !_isDisposed
        && _fileTransferCancellation is null
        && !_isConflictResolutionOpen;

    public Task<bool> TryHandleTabHeaderFileDropAsync(IReadOnlyList<string> paths, bool move)
    {
        var destinationPath = string.IsNullOrWhiteSpace(CurrentPath) ? _initialPath : CurrentPath;
        return TryHandleFileDropAsync(paths, destinationPath, move);
    }

    public bool ShouldCancelSameLocationFileDrop(
        IReadOnlyList<string> paths,
        string? destinationPath = null)
    {
        try
        {
            var targetPath = string.IsNullOrWhiteSpace(destinationPath)
                ? string.IsNullOrWhiteSpace(CurrentPath) ? _initialPath : CurrentPath
                : destinationPath;
            return WorkspaceShellItemService.IsSameLocationDrop(paths, targetPath);
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex))
        {
            return false;
        }
    }

    public void ConfigureGestures(
        AppSettings settings,
        Func<string?, string?, Task> executeCommandAsync,
        Func<long?>? tryBeginInteraction = null,
        Action<long>? completeInteraction = null,
        Func<long?>? tryBeginRightWheelInteraction = null)
    {
        if (_gestureController is null)
        {
            _gestureController = new WorkspaceGestureController(
                this,
                settings,
                executeCommandAsync,
                tryBeginInteraction,
                completeInteraction,
                tryBeginRightWheelInteraction,
                handleHandledMouseEvents: true,
                gestureScope: GestureCommandScope.FolderTab);
            return;
        }

        _gestureController.UpdateSettings(settings);
    }

    public void UpdateGestureSettings(AppSettings settings)
    {
        _gestureController?.Cancel();
        _gestureController?.UpdateSettings(settings);
    }

    public void CancelGesture()
    {
        _gestureController?.Cancel();
    }

    public void CloseQuickLook()
    {
        QuickLookPreviewHost.Close();
    }

    public void SetSortMode(string sortMode)
    {
        ChangeSort(sortMode, _sortDirection);
    }

    public void SetSort(string sortMode, string sortDirection)
    {
        ChangeSort(sortMode, sortDirection);
    }

    public void UpdateShowHiddenItems(bool showHiddenItems)
    {
        if (_showHiddenItems == showHiddenItems)
        {
            UpdateHiddenItemsButton();
            return;
        }

        _showHiddenItems = showHiddenItems;
        UpdateHiddenItemsButton();
        _ = RefreshAsync();
    }

    public void ToggleSortDirection()
    {
        var direction = string.Equals(
            _sortDirection,
            FolderTabSortDirection.Ascending,
            StringComparison.Ordinal)
                ? FolderTabSortDirection.Descending
                : FolderTabSortDirection.Ascending;
        ChangeSort(_sortMode, direction);
    }

    public void UpdateQuickLookSettings(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        QuickLookPreviewHost.Configure(_quickLookSettings);
    }

    public void UpdateQuickLookPaneLayout(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        QuickLookPreviewHost.UpdatePaneLayout(_quickLookSettings);
    }

    public void FocusShortcutScope()
    {
        _ = Dispatcher.BeginInvoke(FocusShortcutScopeCore, DispatcherPriority.Input);
    }

    public void FocusPathBox()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public Task NavigateToAsync(string folderPath)
    {
        return NavigateWithHistoryAsync(folderPath);
    }

    public FolderTabSessionState CaptureSessionState()
    {
        FolderTabService.TrimNavigationHistory(_history, ref _historyIndex);
        var selectedPaths = _hasLoadedContent
            ? _selectedPaths.ToList()
            : _initialSelectedPaths.ToList();
        return new FolderTabSessionState
        {
            NavigationHistory = _history.ToList(),
            NavigationHistoryIndex = _historyIndex,
            SelectedPaths = selectedPaths,
            SelectionAnchorPath = _hasLoadedContent
                ? _selectionAnchorPath
                : _initialSelectionAnchorPath,
            DetailScrollOffset = NormalizeScrollOffset(_detailScrollOffset),
            IconScrollOffset = NormalizeScrollOffset(_iconScrollOffset),
            DetailColumnOrder = CaptureDetailColumnOrder()
        };
    }

    public async Task<bool> TryExecuteGestureCommandAsync(string? command)
    {
        switch (command)
        {
            case "Back":
            case BrowserCommandProvider.BackActionId:
                await NavigateBackAsync();
                return true;
            case "Forward":
            case BrowserCommandProvider.ForwardActionId:
                await NavigateForwardAsync();
                return true;
            case "Reload":
            case BrowserCommandProvider.ReloadActionId:
                await RefreshAsync();
                return true;
            case "ScrollTop":
            case BrowserCommandProvider.ScrollTopActionId:
                ScrollToBoundary(scrollToBottom: false);
                return true;
            case "ScrollBottom":
            case BrowserCommandProvider.ScrollBottomActionId:
                ScrollToBoundary(scrollToBottom: true);
                return true;
            case "ScrollPageUp":
            case BrowserCommandProvider.ScrollPageUpActionId:
                GetCurrentScrollViewer()?.PageUp();
                return true;
            case "ScrollPageDown":
            case BrowserCommandProvider.ScrollPageDownActionId:
                GetCurrentScrollViewer()?.PageDown();
                return true;
            case ContextCommandProvider.FolderSortNameActionId:
                SetSortMode(FolderTabSortMode.Name);
                return true;
            case ContextCommandProvider.FolderSortTypeActionId:
                SetSortMode(FolderTabSortMode.Type);
                return true;
            case ContextCommandProvider.FolderSortModifiedActionId:
                SetSortMode(FolderTabSortMode.LastWriteTime);
                return true;
            case ContextCommandProvider.FolderSortSizeActionId:
                SetSortMode(FolderTabSortMode.Size);
                return true;
            case ContextCommandProvider.FolderToggleSortDirectionActionId:
                ToggleSortDirection();
                return true;
            case ContextCommandProvider.FolderQuickLookActionId:
                await ShowQuickLookForSelectionAsync();
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _iconLayoutTimer.Stop();
        _iconLayoutTimer.Tick -= IconLayoutTimer_Tick;
        _folderWatcherTimer.Stop();
        _folderWatcherTimer.Tick -= FolderWatcherTimer_Tick;
        DisposeFolderWatcher();
        _gestureController?.Dispose();
        _gestureController = null;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        _recursiveSearchCancellation?.Cancel();
        _recursiveSearchCancellation?.Dispose();
        _recursiveSearchCancellation = null;
        _fileTransferCancellation?.Cancel();
        _fileTransferCancellation?.Dispose();
        _fileTransferCancellation = null;
        CancelIconLoading();
        QuickLookPreviewHost.Dispose();
        Loaded -= FolderTabView_Loaded;
        Unloaded -= FolderTabView_Unloaded;
        IsVisibleChanged -= FolderTabView_IsVisibleChanged;
    }

    private async void FolderTabView_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureIconLoadScope();
        if (_hasLoadedContent || _isInitialLoadStarted)
        {
            ConfigureFolderWatcher();
            if (string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal))
            {
                RebuildIconRows(force: false);
            }

            return;
        }

        _isInitialLoadStarted = true;
        try
        {
            await NavigateWithHistoryAsync(_initialPath);
        }
        finally
        {
            _isInitialLoadStarted = false;
        }
    }

    private void FocusShortcutScopeCore()
    {
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), this);
        Focus();
        Keyboard.Focus(this);
    }

    private async void FolderTabView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && QuickLookPreviewHost.IsOpen)
        {
            QuickLookPreviewHost.Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            e.Handled = true;
            SearchBox.Clear();
            FocusShortcutScopeCore();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.N && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await CreateFolderAsync();
            return;
        }

        if (!IsTextInputFocused())
        {
            if (e.Key == Key.A && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SelectAllItems();
                return;
            }

            if (e.Key == Key.F2 && modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await RenameSelectionAsync();
                return;
            }

            if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await RecycleSelectionAsync();
                return;
            }

            if (e.Key == Key.Z && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await UndoLastFileOperationAsync();
                return;
            }

            if (e.Key == Key.C && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                CopySelectionToClipboard(move: false);
                return;
            }

            if (e.Key == Key.X && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                CopySelectionToClipboard(move: true);
                return;
            }

            if (e.Key == Key.V && modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await PasteClipboardAsync();
                return;
            }
        }

        if (e.Key != Key.Space
            || Keyboard.Modifiers != ModifierKeys.None
            || IsTextInputFocused()
            || !IsQuickLookEnabled())
        {
            return;
        }

        e.Handled = true;
        await ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.FolderQuickLookActionId,
            parameter: null,
            ShowQuickLookForSelectionAsync);
    }

    private async void QuickLookMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!IsQuickLookEnabled()
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            return;
        }

        _selectionAnchorPath = item.FullPath;
        SetSelectedPaths([item.FullPath]);
        RestoreSelectionForCurrentView();
        await ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.FolderQuickLookActionId,
            parameter: null,
            ShowQuickLookForSelectionAsync);
    }

    private void OpenVideoInMiniPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_openLocalVideoInMiniPlayer is null
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || !item.CanOpenInMiniPlayer)
        {
            return;
        }

        _openLocalVideoInMiniPlayer(item.FullPath);
    }

    private void OpenWithMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || item.IsDirectory)
        {
            return;
        }

        try
        {
            _shellItemService.OpenWith(item.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("プログラムから開く画面を表示できませんでした: " + ex.Message);
        }
    }

    private async void OpenContainingFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            return;
        }

        var containingFolder = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(containingFolder))
        {
            return;
        }

        await NavigateWithHistoryAsync(containingFolder);
        if (!string.Equals(CurrentPath, containingFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetSelectedPaths([item.FullPath]);
        _selectionAnchorPath = _selectedPaths.FirstOrDefault();
        RestoreSelectionForCurrentView();
    }

    private async void SaveToWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_savePathsToWorkspaceAsync is null
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            ShowMessage("Workspaceへの保存を開始できません。");
            return;
        }

        var paths = _selectedPaths.Contains(item.FullPath)
            ? Items.Where(candidate => _selectedPaths.Contains(candidate.FullPath))
                .Select(candidate => candidate.FullPath)
                .ToArray()
            : [item.FullPath];
        try
        {
            await _savePathsToWorkspaceAsync(paths);
            ShowOperationStatus($"{paths.Length:N0} 件をWorkspaceへ参照保存しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("Workspaceへ保存できませんでした: " + ex.Message);
        }
    }

    private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            return;
        }

        try
        {
            var owner = Window.GetWindow(this);
            var ownerHandle = owner is null
                ? nint.Zero
                : new WindowInteropHelper(owner).Handle;
            _service.ShowProperties(item.FullPath, ownerHandle);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Win32Exception)
        {
            ShowMessage("プロパティを表示できませんでした: " + ex.Message);
        }
    }

    private async void CreateFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CreateFolderAsync();
    }

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PrepareContextSelection(sender);
        await RenameSelectionAsync();
    }

    private async void RecycleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PrepareContextSelection(sender);
        await RecycleSelectionAsync();
    }

    private async void UndoFileOperationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await UndoLastFileOperationAsync();
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PrepareContextSelection(sender);
        CopySelectionToClipboard(move: false);
    }

    private void CutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PrepareContextSelection(sender);
        CopySelectionToClipboard(move: true);
    }

    private async void PasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await PasteClipboardAsync();
    }

    private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SelectAllItems();
    }

    private void SelectAllItems()
    {
        SetSelectedPaths(Items.Select(item => item.FullPath));
        _selectionAnchorPath = _selectedPaths.FirstOrDefault();
        RestoreSelectionForCurrentView();
    }

    private void BlankViewModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var viewMode = string.Equals(tag, "blank-view-icons", StringComparison.Ordinal)
            ? FolderTabViewMode.Icons
            : FolderTabViewMode.Details;
        ChangeViewMode(viewMode);
    }

    private void ToggleCurrentPlacePinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        _setPlacePinned?.Invoke(CurrentPath, !IsCurrentPlacePinned());
    }

    private void CopyCurrentPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        try
        {
            Clipboard.SetText(CurrentPath);
            ShowOperationStatus("現在のパスをコピーしました。");
        }
        catch (Exception ex) when (IsExpectedClipboardException(ex))
        {
            ShowMessage("現在のパスをコピーできませんでした: " + ex.Message);
        }
    }

    private async void SaveCurrentFolderToWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_savePathsToWorkspaceAsync is null || string.IsNullOrWhiteSpace(CurrentPath))
        {
            ShowMessage("Workspaceへの保存を開始できません。");
            return;
        }

        try
        {
            await _savePathsToWorkspaceAsync([CurrentPath]);
            ShowOperationStatus("現在のフォルダーをWorkspaceへ参照保存しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("Workspaceへ保存できませんでした: " + ex.Message);
        }
    }

    private void CancelFileOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fileTransferCancellation is null)
        {
            return;
        }

        _fileTransferCancellation.Cancel();
        FileOperationTitleText.Text = "キャンセル中...";
        CancelFileOperationButton.IsEnabled = false;
        CancelFileOperationPanelButton.IsEnabled = false;
    }

    private async void RetryFileOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_failedTransferPaths.Count == 0
            || string.IsNullOrWhiteSpace(_failedTransferDestination)
            || _fileTransferCancellation is not null)
        {
            RetryFileOperationButton.Visibility = Visibility.Collapsed;
            return;
        }

        await PrepareAndExecuteTransferAsync(
            _failedTransferPaths,
            _failedTransferDestination,
            _failedTransferMove);
    }

    private async Task CreateFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        var dialog = new WorkspaceTextInputWindow("新しいフォルダー", "フォルダー名", "新しいフォルダー")
        {
            Owner = Window.GetWindow(this)
        };
        _ = dialog.ShowDialog();
        if (!dialog.IsAccepted)
        {
            return;
        }

        try
        {
            QuickLookPreviewHost.Close();
            var operation = _shellItemService.CreateDirectory(CurrentPath, dialog.Value);
            _lastFileOperations = [operation];
            await RefreshAfterFileOperationAsync([operation.DestinationPath]);
            ShowOperationStatus($"フォルダーを作成しました: {Path.GetFileName(operation.DestinationPath)}");
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex))
        {
            ShowMessage("フォルダーを作成できませんでした: " + ex.Message);
        }
    }

    private async Task RenameSelectionAsync()
    {
        var selectedItems = GetSelectedFileOperationItems();
        if (selectedItems.Count != 1)
        {
            ShowMessage("名前を変更する項目を1件選択してください。");
            return;
        }

        var item = selectedItems[0];
        var dialog = new WorkspaceTextInputWindow("名前の変更", "新しい名前", item.Name)
        {
            Owner = Window.GetWindow(this)
        };
        _ = dialog.ShowDialog();
        if (!dialog.IsAccepted)
        {
            return;
        }

        try
        {
            QuickLookPreviewHost.Close();
            var operation = _shellItemService.Rename(CreateShellItem(item.FullPath), dialog.Value);
            _lastFileOperations = [operation];
            await RefreshAfterFileOperationAsync([operation.DestinationPath]);
            ShowOperationStatus($"名前を変更しました: {Path.GetFileName(operation.DestinationPath)}");
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex))
        {
            ShowMessage("名前を変更できませんでした: " + ex.Message);
        }
    }

    private async Task RecycleSelectionAsync()
    {
        var selectedItems = GetSelectedFileOperationItems();
        if (selectedItems.Count == 0)
        {
            ShowMessage("ごみ箱へ移動する項目を選択してください。");
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            selectedItems.Count == 1
                ? $"「{selectedItems[0].Name}」をごみ箱へ移動しますか？"
                : $"選択した {selectedItems.Count:N0} 件をごみ箱へ移動しますか？",
            "ごみ箱へ移動",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        QuickLookPreviewHost.Close();
        var succeeded = 0;
        var failureMessages = new List<string>();
        foreach (var item in selectedItems)
        {
            try
            {
                _ = _shellItemService.MoveToRecycleBin(CreateShellItem(item.FullPath));
                succeeded++;
            }
            catch (Exception ex) when (IsExpectedFileOperationException(ex))
            {
                failureMessages.Add($"{item.Name}: {ex.Message}");
            }
        }

        _lastFileOperations = [];
        await RefreshAfterFileOperationAsync([]);
        var status = $"{succeeded:N0} 件をごみ箱へ移動しました。復元はWindowsのごみ箱から行ってください。";
        if (failureMessages.Count > 0)
        {
            status += $" 失敗 {failureMessages.Count:N0} 件: {failureMessages[0]}";
        }

        ShowOperationStatus(status);
    }

    private async Task UndoLastFileOperationAsync()
    {
        if (_lastFileOperations.Count == 0 || _lastFileOperations.Any(operation => !operation.CanUndo))
        {
            ShowMessage("元に戻せる直前の操作はありません。");
            return;
        }

        try
        {
            QuickLookPreviewHost.Close();
            var operations = _lastFileOperations;
            await Task.Run(() =>
            {
                foreach (var operation in operations.Reverse())
                {
                    _shellItemService.Undo(operation);
                }
            });
            _lastFileOperations = [];
            var reselectPaths = operations
                .Select(operation => operation.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            await RefreshAfterFileOperationAsync(reselectPaths);
            ShowOperationStatus("直前の操作を元に戻しました。");
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex))
        {
            ShowMessage("元に戻せませんでした: " + ex.Message);
        }
    }

    private void CopySelectionToClipboard(bool move)
    {
        var selectedItems = GetSelectedFileOperationItems();
        if (selectedItems.Count == 0)
        {
            ShowMessage(move ? "切り取る項目を選択してください。" : "コピーする項目を選択してください。");
            return;
        }

        try
        {
            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.FileDrop, selectedItems.Select(item => item.FullPath).ToArray());
            dataObject.SetData(
                PreferredDropEffectFormat,
                new MemoryStream(BitConverter.GetBytes(move ? 2 : 1)),
                autoConvert: false);
            Clipboard.SetDataObject(dataObject, copy: true);
            ShowOperationStatus($"{selectedItems.Count:N0} 件を{(move ? "切り取り" : "コピー")}ました。");
        }
        catch (Exception ex) when (IsExpectedClipboardException(ex))
        {
            ShowMessage("クリップボードへ保存できませんでした: " + ex.Message);
        }
    }

    private async Task PasteClipboardAsync()
    {
        if (_fileTransferCancellation is not null)
        {
            ShowMessage("ファイル操作を実行中です。");
            return;
        }

        try
        {
            var dataObject = Clipboard.GetDataObject();
            if (dataObject is null
                || !dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
                || dataObject.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] paths)
            {
                ShowMessage("貼り付け可能なファイルまたはフォルダーがありません。");
                return;
            }

            await PrepareAndExecuteTransferAsync(
                paths,
                CurrentPath,
                move: IsMoveClipboardData(dataObject));
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex) || IsExpectedClipboardException(ex))
        {
            ShowMessage("貼り付けを開始できません: " + ex.Message);
            return;
        }
    }

    private async Task PrepareAndExecuteTransferAsync(
        IReadOnlyList<string> paths,
        string destinationPath,
        bool move)
    {
        if (_isConflictResolutionOpen)
        {
            ShowMessage("同名項目の確認を実行中です。");
            return;
        }

        _isConflictResolutionOpen = true;
        try
        {
            var preparation = await _shellItemService.PrepareTransferAsync(paths, destinationPath, move);
            var conflictResolutions = await ResolveTransferConflictsAsync(preparation.Conflicts);
            if (conflictResolutions is null)
            {
                ShowOperationStatus($"{(move ? "移動" : "コピー")}をキャンセルしました。");
                return;
            }

            var resolution = _shellItemService.ResolveTransferPreparation(
                preparation,
                conflictResolutions);
            if (resolution.Plan.Items.Count == 0)
            {
                ShowOperationStatus(
                    $"{(move ? "移動" : "コピー")} 成功 0 件、スキップ {resolution.SkippedCount:N0} 件、失敗 0 件、未処理 0 件");
                return;
            }

            await ExecuteTransferPlanAsync(resolution.Plan, resolution.SkippedCount);
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex))
        {
            ShowMessage("ファイル操作を開始できません: " + ex.Message);
        }
        finally
        {
            _isConflictResolutionOpen = false;
        }
    }

    private async Task<IReadOnlyDictionary<string, ShellFileConflictResolution>?> ResolveTransferConflictsAsync(
        IReadOnlyList<ShellFileTransferConflict> conflicts)
    {
        var resolutions = new Dictionary<string, ShellFileConflictResolution>(StringComparer.OrdinalIgnoreCase);
        ShellFileConflictResolution? remainingResolution = null;
        for (var index = 0; index < conflicts.Count; index++)
        {
            var conflict = conflicts[index];
            if (remainingResolution is not null)
            {
                resolutions[conflict.SourcePath] = remainingResolution.Value;
                continue;
            }

            var dialog = new FolderFileConflictWindow(conflict, index + 1, conflicts.Count)
            {
                Owner = Window.GetWindow(this)
            };
            _ = await ModelessWindowService.ShowAsync(dialog);
            switch (dialog.Choice)
            {
                case FolderFileConflictChoice.Skip:
                    resolutions[conflict.SourcePath] = ShellFileConflictResolution.Skip;
                    break;
                case FolderFileConflictChoice.KeepBoth:
                    resolutions[conflict.SourcePath] = ShellFileConflictResolution.KeepBoth;
                    break;
                case FolderFileConflictChoice.SkipAll:
                    remainingResolution = ShellFileConflictResolution.Skip;
                    resolutions[conflict.SourcePath] = remainingResolution.Value;
                    break;
                case FolderFileConflictChoice.KeepBothAll:
                    remainingResolution = ShellFileConflictResolution.KeepBoth;
                    resolutions[conflict.SourcePath] = remainingResolution.Value;
                    break;
                default:
                    return null;
            }
        }

        return resolutions;
    }

    private async Task ExecuteTransferPlanAsync(ShellFileTransferPlan plan, int skippedCount = 0)
    {
        QuickLookPreviewHost.Close();
        RetryFileOperationButton.Visibility = Visibility.Collapsed;
        _failedTransferPaths = [];
        _failedTransferDestination = string.Empty;
        _fileTransferCancellation = new CancellationTokenSource();
        var cancellationToken = _fileTransferCancellation.Token;
        CancelFileOperationButton.Visibility = Visibility.Visible;
        ShowFileOperationProgress(plan.Move, plan.Items.Count);
        var succeeded = new List<ShellFileOperationResult>();
        var failureMessage = string.Empty;
        var failedPaths = new List<string>();
        var canceled = false;
        var processed = 0;
        var completedBytes = 0L;
        ShellFileTransferMeasurement? measurement = null;
        try
        {
            try
            {
                measurement = await _shellItemService.MeasureTransferAsync(plan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedFileOperationException(ex))
            {
                FileOperationDetailText.Text = "容量を取得できないため、項目数で進捗を表示します";
            }

            FileOperationProgressBar.IsIndeterminate = false;
            FileOperationProgressBar.Value = 0;
            for (var itemIndex = 0; itemIndex < plan.Items.Count; itemIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = plan.Items[itemIndex];
                var currentNumber = processed + 1;
                var completedItemsBeforeItem = processed;
                var completedBytesBeforeItem = completedBytes;
                var itemBytes = measurement is not null && itemIndex < measurement.ItemBytes.Count
                    ? measurement.ItemBytes[itemIndex]
                    : 0L;
                UpdateFileOperationProgress(
                    item.SourcePath,
                    currentNumber,
                    plan.Items.Count,
                    processed,
                    completedBytes,
                    measurement);
                var progress = new Progress<ShellFileTransferProgress>(value =>
                {
                    var currentItemBytes = itemBytes > 0
                        ? Math.Min(value.BytesTransferred, itemBytes)
                        : value.BytesTransferred;
                    var transferredBytes = AddTransferBytes(completedBytesBeforeItem, currentItemBytes);
                    StatusText.Text = $"{currentNumber:N0}/{plan.Items.Count:N0}  {Path.GetFileName(value.CurrentPath)}  成功 {succeeded.Count:N0} 件";
                    UpdateFileOperationProgress(
                        value.CurrentPath,
                        currentNumber,
                        plan.Items.Count,
                        completedItemsBeforeItem,
                        transferredBytes,
                        measurement);
                });
                try
                {
                    var operation = await _shellItemService.ExecuteTransferAsync(
                        item,
                        plan.Move,
                        progress,
                        cancellationToken);
                    succeeded.Add(operation);
                    processed++;
                    completedBytes = AddTransferBytes(completedBytes, itemBytes);
                    UpdateFileOperationProgress(
                        item.SourcePath,
                        processed,
                        plan.Items.Count,
                        processed,
                        completedBytes,
                        measurement);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex) when (IsExpectedFileOperationException(ex))
                {
                    failureMessage = $"{Path.GetFileName(item.SourcePath)}: {ex.Message}";
                    failedPaths.Add(item.SourcePath);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            CancelFileOperationButton.Visibility = Visibility.Collapsed;
            HideFileOperationProgress();
            _fileTransferCancellation.Dispose();
            _fileTransferCancellation = null;
        }

        if (succeeded.Count > 0)
        {
            _lastFileOperations = succeeded.ToArray();
            await RefreshAfterFileOperationAsync(succeeded.Select(operation => operation.DestinationPath).ToArray());
        }

        var unprocessed = plan.Items.Count - processed - (string.IsNullOrEmpty(failureMessage) ? 0 : 1);
        var failedCount = failedPaths.Count;
        var status = $"{(plan.Move ? "移動" : "コピー")} 成功 {succeeded.Count:N0} 件、スキップ {skippedCount:N0} 件、失敗 {failedCount:N0} 件、未処理 {Math.Max(0, unprocessed):N0} 件";
        if (!string.IsNullOrEmpty(failureMessage))
        {
            status += $" ({failureMessage})";
        }

        if (canceled)
        {
            status += "、キャンセル";
        }

        if (failedPaths.Count > 0)
        {
            _failedTransferPaths = failedPaths;
            _failedTransferDestination = plan.DestinationDirectory;
            _failedTransferMove = plan.Move;
            RetryFileOperationButton.Visibility = Visibility.Visible;
        }

        ShowOperationStatus(status);
    }

    private void ShowFileOperationProgress(bool move, int totalItemCount)
    {
        FileOperationTitleText.Text = move ? "移動中" : "コピー中";
        FileOperationCurrentText.Text = "処理を準備しています...";
        FileOperationCountText.Text = $"0 / {totalItemCount:N0}";
        FileOperationDetailText.Text = "合計容量を計算しています...";
        FileOperationProgressBar.IsIndeterminate = true;
        FileOperationProgressBar.Minimum = 0;
        FileOperationProgressBar.Maximum = 100;
        FileOperationProgressBar.Value = 0;
        CancelFileOperationButton.IsEnabled = true;
        CancelFileOperationPanelButton.IsEnabled = true;
        FileOperationProgressPanel.Visibility = Visibility.Visible;
    }

    private void HideFileOperationProgress()
    {
        FileOperationProgressPanel.Visibility = Visibility.Collapsed;
        FileOperationProgressBar.IsIndeterminate = false;
        FileOperationProgressBar.Value = 0;
        FileOperationCurrentText.Text = string.Empty;
        FileOperationDetailText.Text = string.Empty;
    }

    private static long AddTransferBytes(long current, long added)
    {
        return added > long.MaxValue - current
            ? long.MaxValue
            : current + added;
    }

    private void UpdateFileOperationProgress(
        string currentPath,
        int currentItemNumber,
        int totalItemCount,
        int completedItemCount,
        long transferredBytes,
        ShellFileTransferMeasurement? measurement)
    {
        FileOperationCurrentText.Text = Path.GetFileName(currentPath);
        FileOperationCountText.Text = $"{currentItemNumber:N0} / {totalItemCount:N0} 項目  完了 {completedItemCount:N0}";

        double percent;
        if (measurement is not null && measurement.TotalBytes > 0)
        {
            var cappedBytes = Math.Min(transferredBytes, measurement.TotalBytes);
            percent = cappedBytes * 100d / measurement.TotalBytes;
            FileOperationDetailText.Text = $"{FolderTabService.FormatByteSize(cappedBytes)} / {FolderTabService.FormatByteSize(measurement.TotalBytes)}  ({percent:N0}%)";
        }
        else
        {
            percent = totalItemCount > 0
                ? completedItemCount * 100d / totalItemCount
                : 100d;
            FileOperationDetailText.Text = $"項目進捗 {completedItemCount:N0} / {totalItemCount:N0}  ({percent:N0}%)";
        }

        FileOperationProgressBar.IsIndeterminate = false;
        FileOperationProgressBar.Value = Math.Clamp(percent, 0d, 100d);
    }

    private static bool IsMoveClipboardData(IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(PreferredDropEffectFormat, autoConvert: false))
        {
            return false;
        }

        var data = dataObject.GetData(PreferredDropEffectFormat, autoConvert: false);
        if (data is MemoryStream stream)
        {
            var originalPosition = stream.Position;
            try
            {
                stream.Position = 0;
                var bytes = new byte[4];
                return stream.Read(bytes, 0, bytes.Length) == bytes.Length
                    && (BitConverter.ToInt32(bytes, 0) & 2) != 0;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        return data is byte[] { Length: >= 4 } bytesValue
            && (BitConverter.ToInt32(bytesValue, 0) & 2) != 0;
    }

    private void PrepareContextSelection(object sender)
    {
        if (sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || _selectedPaths.Contains(item.FullPath))
        {
            return;
        }

        _selectionAnchorPath = item.FullPath;
        SetSelectedPaths([item.FullPath]);
        RestoreSelectionForCurrentView();
    }

    private IReadOnlyList<FolderTabItemViewModel> GetSelectedFileOperationItems()
    {
        CaptureSelectionFromCurrentView();
        return _selectedPaths
            .Select(path => _itemsByPath.GetValueOrDefault(path))
            .Where(item => item is not null)
            .Cast<FolderTabItemViewModel>()
            .ToArray();
    }

    private ShellItem CreateShellItem(string path)
    {
        return new ShellItem
        {
            TargetPath = path,
            Kind = _shellItemService.Classify(path)
        };
    }

    private async Task RefreshAfterFileOperationAsync(IReadOnlyList<string> selectedPaths)
    {
        _folderWatcherTimer.Stop();
        _ignoreWatcherEventsUntilUtc = DateTime.UtcNow.AddSeconds(1);
        SetSelectedPaths(selectedPaths.Where(_itemsByPath.ContainsKey));
        await NavigateCoreAsync(CurrentPath);
        SetSelectedPaths(selectedPaths.Where(_itemsByPath.ContainsKey));
        _selectionAnchorPath = _selectedPaths.FirstOrDefault();
        RestoreSelectionForCurrentView();
    }

    private void ShowOperationStatus(string message)
    {
        StatusText.Text = message;
        MessagePanel.Visibility = Visibility.Collapsed;
    }

    private static bool IsExpectedFileOperationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or OperationCanceledException
            or NotSupportedException
            or Win32Exception;
    }

    private static bool IsExpectedClipboardException(Exception exception)
    {
        return exception is System.Runtime.InteropServices.COMException
            or System.Runtime.InteropServices.ExternalException
            or InvalidOperationException;
    }

    private void FolderTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        _navigationCancellation?.Cancel();
        _recursiveSearchCancellation?.Cancel();
        _iconLayoutTimer.Stop();
        _folderWatcherTimer.Stop();
        DisposeFolderWatcher();
        CancelIconLoading();
        QuickLookPreviewHost.Close();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandOrFallbackAsync(
            BrowserCommandProvider.BackActionId,
            parameter: null,
            NavigateBackAsync);
    }

    public async Task NavigateBackAsync()
    {
        if (_historyIndex <= 0)
        {
            return;
        }

        var targetIndex = _historyIndex - 1;
        if (await NavigateCoreAsync(_history[targetIndex]))
        {
            _historyIndex = targetIndex;
            UpdateNavigationButtons();
        }
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandOrFallbackAsync(
            BrowserCommandProvider.ForwardActionId,
            parameter: null,
            NavigateForwardAsync);
    }

    public async Task NavigateForwardAsync()
    {
        if (_historyIndex < 0 || _historyIndex >= _history.Count - 1)
        {
            return;
        }

        var targetIndex = _historyIndex + 1;
        if (await NavigateCoreAsync(_history[targetIndex]))
        {
            _historyIndex = targetIndex;
            UpdateNavigationButtons();
        }
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var parentPath = string.IsNullOrWhiteSpace(CurrentPath)
            ? null
            : Directory.GetParent(CurrentPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            await NavigateWithHistoryAsync(parentPath);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandOrFallbackAsync(
            BrowserCommandProvider.ReloadActionId,
            parameter: null,
            RefreshAsync);
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        var viewMode = string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal)
            ? FolderTabViewMode.Details
            : FolderTabViewMode.Icons;
        ChangeViewMode(viewMode);
    }

    private void ChangeViewMode(string viewMode)
    {
        if (string.Equals(_viewMode, viewMode, StringComparison.Ordinal))
        {
            return;
        }

        CaptureSelectionFromCurrentView();
        _viewMode = viewMode;
        ApplyViewMode();
        ViewModeChanged?.Invoke(this, new FolderTabViewModeChangedEventArgs(_viewMode));
    }

    private async void HiddenItemsButton_Click(object sender, RoutedEventArgs e)
    {
        _showHiddenItems = !_showHiddenItems;
        UpdateHiddenItemsButton();
        HiddenItemsVisibilityChanged?.Invoke(
            this,
            new FolderTabHiddenItemsVisibilityChangedEventArgs(_showHiddenItems));
        await RefreshAsync();
    }

    private void UpdateHiddenItemsButton()
    {
        HiddenItemsButton.Content = _showHiddenItems ? "隠し: ON" : "隠し: OFF";
        HiddenItemsButton.ToolTip = _showHiddenItems
            ? "隠しファイルを表示中（OSシステム項目は除外）"
            : "隠しファイルを表示";
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_buttonContextMenuController.CloseIfOpen(SortButton)) return;
        UpdateSortControls();
        OpenButtonContextMenu(SortButton);
    }

    private async void SortModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string sortMode })
        {
            await ExecuteSortCommandAsync(sortMode, _sortDirection);
        }
    }

    private async void SortDirectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string sortDirection })
        {
            await ExecuteSortCommandAsync(_sortMode, sortDirection);
        }
    }

    private async void ItemsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var sortMode = GetSortModeForColumn(e.Column.SortMemberPath);
        var sortDirection = string.Equals(sortMode, _sortMode, StringComparison.Ordinal)
            && string.Equals(_sortDirection, FolderTabSortDirection.Ascending, StringComparison.Ordinal)
                ? FolderTabSortDirection.Descending
                : FolderTabSortDirection.Ascending;
        await ExecuteSortCommandAsync(sortMode, sortDirection);
    }

    private void ItemsGrid_ColumnReordered(object sender, DataGridColumnEventArgs e)
    {
        DetailColumnOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task ExecuteSortCommandAsync(string sortMode, string sortDirection)
    {
        return ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.FolderApplySortActionId,
            (sortMode, sortDirection),
            () =>
            {
                ChangeSort(sortMode, sortDirection);
                return Task.CompletedTask;
            });
    }

    private Task ExecuteCommandOrFallbackAsync(
        string actionId,
        object? parameter,
        Func<Task> fallback)
    {
        return _executeCommandAsync is null
            ? fallback()
            : _executeCommandAsync(actionId, parameter);
    }

    private void ChangeSort(string? sortMode, string? sortDirection)
    {
        var normalizedMode = FolderTabSortMode.Normalize(sortMode);
        var normalizedDirection = FolderTabSortDirection.Normalize(sortDirection);
        if (string.Equals(_sortMode, normalizedMode, StringComparison.Ordinal)
            && string.Equals(_sortDirection, normalizedDirection, StringComparison.Ordinal))
        {
            UpdateSortControls();
            return;
        }

        CaptureSelectionFromCurrentView();
        _sortMode = normalizedMode;
        _sortDirection = normalizedDirection;
        ApplyCurrentSort();
        UpdateSortControls();
        SortChanged?.Invoke(this, new FolderTabSortChangedEventArgs(_sortMode, _sortDirection));
    }

    private void ApplyCurrentSort()
    {
        if (Items.Count <= 1)
        {
            return;
        }

        var entries = Items.Select(static item => item.Entry).ToList();
        FolderTabService.SortEntries(entries, _sortMode, _sortDirection);
        _isSynchronizingSelection = true;
        try
        {
            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(_itemsByPath[entry.FullPath]);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        RebuildIconRows(force: true);
        RestoreSelectionForCurrentView();
        RefreshOpenQuickLook();
    }

    private void UpdateSortControls()
    {
        var modeText = _sortMode switch
        {
            FolderTabSortMode.Type => "種類",
            FolderTabSortMode.LastWriteTime => "更新日時",
            FolderTabSortMode.Size => "サイズ",
            _ => "名前"
        };
        var isAscending = string.Equals(
            _sortDirection,
            FolderTabSortDirection.Ascending,
            StringComparison.Ordinal);
        SortButton.Content = $"{modeText} {(isAscending ? "↑" : "↓")}";
        SortButton.ToolTip = $"並び替え: {modeText}（{(isAscending ? "昇順" : "降順")}）";
        SortByNameMenuItem.IsChecked = string.Equals(_sortMode, FolderTabSortMode.Name, StringComparison.Ordinal);
        SortByTypeMenuItem.IsChecked = string.Equals(_sortMode, FolderTabSortMode.Type, StringComparison.Ordinal);
        SortByLastWriteTimeMenuItem.IsChecked = string.Equals(
            _sortMode,
            FolderTabSortMode.LastWriteTime,
            StringComparison.Ordinal);
        SortBySizeMenuItem.IsChecked = string.Equals(_sortMode, FolderTabSortMode.Size, StringComparison.Ordinal);
        SortAscendingMenuItem.IsChecked = isAscending;
        SortDescendingMenuItem.IsChecked = !isAscending;

        foreach (var column in ItemsGrid.Columns)
        {
            column.SortDirection = string.Equals(
                GetSortModeForColumn(column.SortMemberPath),
                _sortMode,
                StringComparison.Ordinal)
                    ? isAscending ? ListSortDirection.Ascending : ListSortDirection.Descending
                    : null;
        }
    }

    private static string GetSortModeForColumn(string? sortMemberPath)
    {
        return sortMemberPath switch
        {
            nameof(FolderTabItemViewModel.TypeText) => FolderTabSortMode.Type,
            nameof(FolderTabItemViewModel.LastWriteTime) => FolderTabSortMode.LastWriteTime,
            nameof(FolderTabItemViewModel.SizeBytes) => FolderTabSortMode.Size,
            _ => FolderTabSortMode.Name
        };
    }

    public async Task RefreshAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            await NavigateCoreAsync(CurrentPath);
        }
    }

    private void ExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        try
        {
            _service.OpenInExplorer(CurrentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("Explorer で開けませんでした: " + ex.Message);
        }
    }

    private void HierarchyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buttonContextMenuController.CloseIfOpen(HierarchyButton)) return;
        HierarchyMenu.Items.Clear();
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        var paths = new List<string>();
        var current = new DirectoryInfo(CurrentPath);
        while (current is not null)
        {
            paths.Add(current.FullName);
            current = current.Parent;
        }

        foreach (var path in paths.AsEnumerable().Reverse())
        {
            var menuItem = new MenuItem
            {
                Header = FolderTabService.GetDisplayName(path),
                Tag = path
            };
            menuItem.Click += HierarchyMenuItem_Click;
            HierarchyMenu.Items.Add(menuItem);
        }

        OpenButtonContextMenu(HierarchyButton);
    }

    private void PlacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buttonContextMenuController.CloseIfOpen(PlacesButton)) return;
        RebuildPlacesMenu();
        OpenButtonContextMenu(PlacesButton);
    }

    private void RebuildPlacesMenu()
    {
        PlacesMenu.Items.Clear();
        var snapshot = _getPlacesSnapshot?.Invoke()
            ?? new FolderTabPlacesSnapshot([], []);
        var currentIsPinned = snapshot.PinnedPlaces.Any(place =>
            string.Equals(place.Path, CurrentPath, StringComparison.OrdinalIgnoreCase));
        var toggleCurrent = new MenuItem
        {
            Header = currentIsPinned ? "この場所のピン留めを解除" : "この場所をピン留め",
            Tag = CurrentPath,
            IsEnabled = !string.IsNullOrWhiteSpace(CurrentPath)
        };
        if (currentIsPinned)
        {
            toggleCurrent.Click += UnpinPlaceMenuItem_Click;
        }
        else
        {
            toggleCurrent.Click += PinPlaceMenuItem_Click;
        }
        PlacesMenu.Items.Add(toggleCurrent);
        PlacesMenu.Items.Add(new Separator());
        PlacesMenu.Items.Add(CreatePlacesSection(
            "ピン留め",
            snapshot.PinnedPlaces,
            includeOrderingCommands: true));
        PlacesMenu.Items.Add(CreatePlacesSection(
            "最近開いたフォルダー",
            snapshot.RecentPlaces,
            includeOrderingCommands: false));
        PlacesMenu.Items.Add(new Separator());
        PlacesMenu.Items.Add(CreateSpecialFolderMenuItem("デスクトップ", "Desktop"));
        PlacesMenu.Items.Add(CreateSpecialFolderMenuItem("ダウンロード", "Downloads"));
        PlacesMenu.Items.Add(CreateSpecialFolderMenuItem("ドキュメント", "Documents"));
        PlacesMenu.Items.Add(new Separator());
        var drives = new MenuItem { Header = "ドライブ" };
        drives.SubmenuOpened += DrivesMenuItem_SubmenuOpened;
        PlacesMenu.Items.Add(drives);
    }

    private MenuItem CreatePlacesSection(
        string header,
        IReadOnlyList<FolderTabPlace> places,
        bool includeOrderingCommands)
    {
        var section = new MenuItem { Header = header };
        if (places.Count == 0)
        {
            section.Items.Add(new MenuItem { Header = "ありません", IsEnabled = false });
            return section;
        }

        for (var index = 0; index < places.Count; index++)
        {
            section.Items.Add(CreatePlaceMenuItem(places[index], index, places.Count, includeOrderingCommands));
        }

        return section;
    }

    private MenuItem CreatePlaceMenuItem(
        FolderTabPlace place,
        int index,
        int count,
        bool includeOrderingCommands)
    {
        var placeItem = new MenuItem
        {
            Header = place.IsAvailable ? place.DisplayName : place.DisplayName + "（利用不可）",
            ToolTip = place.Path,
            Opacity = place.IsAvailable ? 1 : 0.58
        };
        placeItem.Items.Add(CreatePlaceCommand(
            "このタブで開く",
            place.Path,
            PlaceOpenCurrentMenuItem_Click,
            place.IsAvailable));
        placeItem.Items.Add(CreatePlaceCommand(
            "新しいFolderTabで開く",
            place.Path,
            PlaceOpenNewTabMenuItem_Click,
            place.IsAvailable));
        placeItem.Items.Add(CreatePlaceCommand(
            "別Windowで開く",
            place.Path,
            PlaceOpenNewWindowMenuItem_Click,
            place.IsAvailable));
        placeItem.Items.Add(new Separator());
        if (includeOrderingCommands)
        {
            placeItem.Items.Add(CreatePlaceCommand(
                "上へ",
                place.Path,
                MovePinnedPlaceUpMenuItem_Click,
                index > 0));
            placeItem.Items.Add(CreatePlaceCommand(
                "下へ",
                place.Path,
                MovePinnedPlaceDownMenuItem_Click,
                index < count - 1));
            placeItem.Items.Add(CreatePlaceCommand(
                "ピン留めを解除",
                place.Path,
                UnpinPlaceMenuItem_Click,
                isEnabled: true));
        }
        else
        {
            placeItem.Items.Add(CreatePlaceCommand(
                "ピン留め",
                place.Path,
                PinPlaceMenuItem_Click,
                isEnabled: true));
        }

        return placeItem;
    }

    private static MenuItem CreatePlaceCommand(
        string header,
        string path,
        RoutedEventHandler handler,
        bool isEnabled)
    {
        var item = new MenuItem
        {
            Header = header,
            Tag = path,
            IsEnabled = isEnabled
        };
        item.Click += handler;
        return item;
    }

    private MenuItem CreateSpecialFolderMenuItem(string header, string location)
    {
        var item = new MenuItem { Header = header, Tag = location };
        item.Click += SpecialFolderMenuItem_Click;
        return item;
    }

    private async void PlaceOpenCurrentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            await NavigateWithHistoryAsync(path);
        }
    }

    private void PlaceOpenNewTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            OpenFolderRequested?.Invoke(
                this,
                new FolderTabOpenRequestedEventArgs(path, FolderTabOpenDisposition.ForegroundTab));
        }
    }

    private void PlaceOpenNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            OpenFolderRequested?.Invoke(
                this,
                new FolderTabOpenRequestedEventArgs(path, FolderTabOpenDisposition.NewWindow));
        }
    }

    private void PinPlaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            _setPlacePinned?.Invoke(path, true);
        }
    }

    private void UnpinPlaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            _setPlacePinned?.Invoke(path, false);
        }
    }

    private void MovePinnedPlaceUpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            _movePinnedPlace?.Invoke(path, -1);
        }
    }

    private void MovePinnedPlaceDownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            _movePinnedPlace?.Invoke(path, 1);
        }
    }

    private async void HierarchyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
        {
            await NavigateWithHistoryAsync(path);
        }
    }

    private async void SpecialFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string location })
        {
            return;
        }

        var path = location switch
        {
            "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"),
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(path))
        {
            await NavigateWithHistoryAsync(path);
        }
    }

    private void DrivesMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem drivesMenuItem)
        {
            return;
        }

        drivesMenuItem.Items.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                var menuItem = new MenuItem
                {
                    Header = $"{drive.Name}  {drive.VolumeLabel}",
                    Tag = drive.RootDirectory.FullName
                };
                menuItem.Click += HierarchyMenuItem_Click;
                drivesMenuItem.Items.Add(menuItem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            drivesMenuItem.Items.Add(new MenuItem { Header = "ドライブを取得できません", IsEnabled = false });
        }
    }

    private void OpenButtonContextMenu(Button button)
        => _buttonContextMenuController.Toggle(button, PlacementMode.Bottom);

    private void ButtonContextMenu_MouseDownOutsideCapturedElement(object sender, MouseButtonEventArgs e)
        => _buttonContextMenuController.OnOutsideCapturedPress(sender, e);

    private void MenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _buttonContextMenuController.OnButtonPreviewPress(sender, e);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRecursiveSearchMode || _recursiveSearchCancellation is not null)
        {
            ExitRecursiveSearch();
            return;
        }

        ApplySearchFilter();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await StartRecursiveSearchAsync();
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        SearchBox.Clear();
        FocusShortcutScopeCore();
    }

    private async void RecursiveSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecursiveSearchMode || _recursiveSearchCancellation is not null)
        {
            ExitRecursiveSearch();
            return;
        }

        await StartRecursiveSearchAsync();
    }

    private async Task StartRecursiveSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (!FolderTabSearchCriteria.Parse(query).HasCriteria)
        {
            ShowMessage("配下検索の検索語または条件を入力してください。");
            return;
        }

        _recursiveSearchCancellation?.Cancel();
        _recursiveSearchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _recursiveSearchCancellation = cancellation;
        _preRecursiveSelectedPaths = _selectedPaths.ToArray();
        _hasPreRecursiveState = true;
        _preRecursiveSelectionAnchorPath = _selectionAnchorPath;
        _preRecursiveDetailScrollOffset = _detailScrollOffset;
        _preRecursiveIconScrollOffset = _iconScrollOffset;
        RecursiveSearchButton.Content = "検索中止";
        RecursiveSearchButton.ToolTip = "サブフォルダー検索をキャンセル";
        StatusText.Text = "サブフォルダーを検索しています...";
        var progress = new Progress<FolderTabSearchProgress>(value =>
        {
            StatusText.Text = $"配下検索: {value.ResultCount:N0} 件  確認 {value.ScannedCount:N0} 件  除外 {value.ExcludedCount:N0} 件";
        });
        try
        {
            var result = await _service.SearchDescendantsAsync(
                CurrentPath,
                query,
                showHiddenItems: _showHiddenItems,
                progress: progress,
                cancellationToken: cancellation.Token);
            if (cancellation.IsCancellationRequested || _isDisposed)
            {
                return;
            }

            ApplyRecursiveSearchResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 新しい検索、一覧復帰、View終了時のキャンセルです。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ExitRecursiveSearch();
            ShowMessage("配下検索を実行できませんでした: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_recursiveSearchCancellation, cancellation))
            {
                _recursiveSearchCancellation.Dispose();
                _recursiveSearchCancellation = null;
                if (!_isRecursiveSearchMode)
                {
                    RecursiveSearchButton.Content = "配下検索";
                    RecursiveSearchButton.ToolTip = "サブフォルダーを含めて検索";
                }
            }
        }
    }

    private void ApplyRecursiveSearchResult(FolderTabSearchResult result)
    {
        ResetIconLoadScope();
        var entries = result.Entries.ToList();
        FolderTabService.SortEntries(entries, _sortMode, _sortDirection);
        _isSynchronizingSelection = true;
        try
        {
            Items.Clear();
            _itemsByPath.Clear();
            _selectedPaths.Clear();
            foreach (var entry in entries)
            {
                var item = new FolderTabItemViewModel(entry);
                Items.Add(item);
                _itemsByPath[item.FullPath] = item;
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        _selectionAnchorPath = null;
        _isRecursiveSearchMode = true;
        RecursiveSearchButton.Content = "一覧に戻る";
        RecursiveSearchButton.ToolTip = "現在フォルダーの一覧表示へ戻る";
        RebuildIconRows(force: true);
        RestoreSelectionForCurrentView();
        MessagePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = Items.Count == 0 ? "配下検索に一致する項目はありません" : string.Empty;
        StatusText.Text = $"配下検索 {Items.Count:N0} 件  確認 {result.ScannedCount:N0} 件  除外 {result.ExcludedCount:N0} 件"
            + (result.IsTruncated ? $"  上限 {FolderTabService.DefaultMaximumRecursiveSearchResultCount:N0} 件で打ち切り" : string.Empty);
    }

    private void ExitRecursiveSearch(bool restoreItems = true)
    {
        _recursiveSearchCancellation?.Cancel();
        _recursiveSearchCancellation?.Dispose();
        _recursiveSearchCancellation = null;
        var hadRecursiveState = _hasPreRecursiveState;
        _isRecursiveSearchMode = false;
        RecursiveSearchButton.Content = "配下検索";
        RecursiveSearchButton.ToolTip = "サブフォルダーを含めて検索";
        if (!restoreItems || !_hasLoadedContent)
        {
            _preRecursiveSelectedPaths = [];
            _preRecursiveSelectionAnchorPath = null;
            _hasPreRecursiveState = false;
            return;
        }

        ResetIconLoadScope();
        ApplySearchFilter();
        if (hadRecursiveState)
        {
            SetSelectedPaths(_preRecursiveSelectedPaths.Where(_itemsByPath.ContainsKey));
            _selectionAnchorPath = _preRecursiveSelectionAnchorPath is not null
                && _selectedPaths.Contains(_preRecursiveSelectionAnchorPath)
                    ? _preRecursiveSelectionAnchorPath
                    : _selectedPaths.FirstOrDefault();
            RestoreSelectionForCurrentView();
            RestoreScrollOffsetsAfterLayout(
                _preRecursiveDetailScrollOffset,
                _preRecursiveIconScrollOffset);
        }

        _preRecursiveSelectedPaths = [];
        _preRecursiveSelectionAnchorPath = null;
        _hasPreRecursiveState = false;
        UpdateStatusSummary();
    }

    private void ApplySearchFilter()
    {
        if (!_hasLoadedContent)
        {
            return;
        }

        var selectedPaths = _selectedPaths.ToArray();
        var query = SearchBox.Text.Trim();
        var criteria = FolderTabSearchCriteria.Parse(query);
        var entries = _allItemsByPath.Values
            .Select(item => item.Entry)
            .Where(criteria.Matches)
            .ToList();
        FolderTabService.SortEntries(entries, _sortMode, _sortDirection);

        _isSynchronizingSelection = true;
        try
        {
            foreach (var item in _allItemsByPath.Values)
            {
                item.IsSelected = false;
            }

            Items.Clear();
            _itemsByPath.Clear();
            _selectedPaths.Clear();
            foreach (var entry in entries)
            {
                var item = _allItemsByPath[entry.FullPath];
                Items.Add(item);
                _itemsByPath[item.FullPath] = item;
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        SetSelectedPaths(selectedPaths.Where(_itemsByPath.ContainsKey));
        _selectionAnchorPath = _selectedPaths.FirstOrDefault();
        RebuildIconRows(force: true);
        RestoreSelectionForCurrentView();
        MessagePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = Items.Count == 0
            ? _allItemsByPath.Count == 0 ? "このフォルダーは空です" : "検索に一致する項目はありません"
            : string.Empty;
        UpdateStatusSummary();
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PathSuggestionsPopup.IsOpen)
        {
            e.Handled = true;
            ClosePathSuggestions();
            return;
        }

        if (PathSuggestionsPopup.IsOpen && e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            var direction = e.Key == Key.Down ? 1 : -1;
            var currentIndex = PathSuggestionsList.SelectedIndex;
            PathSuggestionsList.SelectedIndex = Math.Clamp(
                currentIndex < 0 ? direction > 0 ? 0 : PathSuggestions.Count - 1 : currentIndex + direction,
                0,
                PathSuggestions.Count - 1);
            PathSuggestionsList.ScrollIntoView(PathSuggestionsList.SelectedItem);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var path = PathSuggestionsPopup.IsOpen
            && PathSuggestionsList.SelectedItem is FolderTabPathSuggestion suggestion
                ? suggestion.Path
                : PathBox.Text;
        ClosePathSuggestions();
        await NavigateWithHistoryAsync(path);
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PathBox.IsKeyboardFocusWithin)
        {
            RefreshPathSuggestions();
        }
    }

    private void PathBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RefreshPathSuggestions();
    }

    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!PathBox.IsKeyboardFocusWithin && !PathSuggestionsList.IsKeyboardFocusWithin)
            {
                ClosePathSuggestions();
            }
        }), DispatcherPriority.Background);
    }

    private void RefreshPathSuggestions()
    {
        var suggestions = _getPathSuggestions?.Invoke(PathBox.Text) ?? [];
        PathSuggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            PathSuggestions.Add(suggestion);
        }

        PathSuggestionsList.SelectedIndex = PathSuggestions.Count > 0 ? 0 : -1;
        PathSuggestionsPopup.IsOpen = PathSuggestions.Count > 0 && PathBox.IsKeyboardFocusWithin;
    }

    private void ClosePathSuggestions()
    {
        PathSuggestionsPopup.IsOpen = false;
        PathSuggestionsList.SelectedIndex = -1;
    }

    private void PathSuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PathSuggestionsList.SelectedItem is not null)
        {
            PathSuggestionsList.ScrollIntoView(PathSuggestionsList.SelectedItem);
        }
    }

    private async void PathSuggestionsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (PathSuggestionsList.SelectedItem is not FolderTabPathSuggestion suggestion)
        {
            return;
        }

        e.Handled = true;
        ClosePathSuggestions();
        await NavigateWithHistoryAsync(suggestion.Path);
    }

    private void FolderTabView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!PathSuggestionsPopup.IsOpen
            || ReferenceEquals(e.OriginalSource, PathBox)
            || FindVisualParent<TextBox>(e.OriginalSource as DependencyObject) == PathBox)
        {
            return;
        }

        ClosePathSuggestions();
    }

    private async void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext
            is not FolderTabItemViewModel entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            if (TryRequestModifiedFolderOpen(entry, Keyboard.Modifiers))
            {
                e.Handled = true;
                return;
            }

            await NavigateWithHistoryAsync(entry.FullPath);
            return;
        }

        try
        {
            _service.Open(entry.FullPath);
            _recordRecentItemOpened?.Invoke(entry.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("ファイルを開けませんでした: " + ex.Message);
        }
    }

    private async void ItemsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var item = ItemsGrid.CurrentItem as FolderTabItemViewModel
            ?? ItemsGrid.SelectedItem as FolderTabItemViewModel;
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        await OpenItemAsync(item);
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingSelection)
        {
            CaptureDetailSelection();
            RefreshOpenQuickLook();
        }
    }

    private async void IconTile_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        await OpenItemAsync(item);
    }

    private async void IconTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderTabItemViewModel item } tile)
        {
            return;
        }

        _ = tile.Focus();
        Keyboard.Focus(tile);
        SelectIconItem(item, Keyboard.Modifiers);
        _fileDragStartPoint = e.GetPosition(this);
        _fileDragStartItem = item;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            if (TryRequestModifiedFolderOpen(item, Keyboard.Modifiers))
            {
                return;
            }

            await OpenItemAsync(item);
        }
    }

    private void ItemsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fileDragStartPoint = e.GetPosition(this);
        _fileDragStartItem = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext
            as FolderTabItemViewModel;
    }

    private void ItemsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        TryBeginFileDrag(e);
    }

    private void IconTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        TryBeginFileDrag(e);
    }

    private void TryBeginFileDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _fileDragStartPoint is not { } startPoint
            || _fileDragStartItem is not { } startItem)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - startPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!_selectedPaths.Contains(startItem.FullPath))
        {
            _selectionAnchorPath = startItem.FullPath;
            SetSelectedPaths([startItem.FullPath]);
            RestoreSelectionForCurrentView();
        }

        var paths = _selectedPaths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        _fileDragStartPoint = null;
        _fileDragStartItem = null;
        if (paths.Length == 0)
        {
            return;
        }

        var dataObject = new DataObject();
        dataObject.SetData(DataFormats.FileDrop, paths);
        dataObject.SetData(FolderFileDragFormat, string.Join('\n', paths), autoConvert: false);
        dataObject.SetData(
            PreferredDropEffectFormat,
            new MemoryStream(BitConverter.GetBytes(1)),
            autoConvert: false);
        _ = DragDrop.DoDragDrop(
            this,
            dataObject,
            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    private void FolderItemsContent_DragEnter(object sender, DragEventArgs e)
    {
        UpdateFileDropPreview(e);
    }

    private void FolderItemsContent_DragOver(object sender, DragEventArgs e)
    {
        UpdateFileDropPreview(e);
    }

    private void UpdateFileDropPreview(DragEventArgs e)
    {
        if (_fileTransferCancellation is not null
            || !TryGetDroppedFilePaths(e.Data, out var paths))
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropPreview();
            e.Handled = true;
            return;
        }

        var move = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        var targetItem = FindDropTargetDirectory(e.OriginalSource as DependencyObject);
        SetDropTargetItem(targetItem);
        var destinationPath = targetItem?.FullPath ?? CurrentPath;
        if (ShouldCancelSameLocationFileDrop(paths, destinationPath))
        {
            e.Effects = DragDropEffects.None;
            ClearFileDropPreview();
            e.Handled = true;
            return;
        }

        DropTargetHighlight.Visibility = Visibility.Visible;
        DropActionText.Text = $"{(move ? "移動" : "コピー")} → {FolderTabService.GetDisplayName(destinationPath)}";
        e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void FolderItemsContent_DragLeave(object sender, DragEventArgs e)
    {
        ClearFileDropPreview();
        e.Handled = true;
    }

    private async void FolderItemsContent_Drop(object sender, DragEventArgs e)
    {
        var targetPath = _dropTargetItem?.FullPath ?? CurrentPath;
        var move = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        var effect = move ? DragDropEffects.Move : DragDropEffects.Copy;
        ClearFileDropPreview();
        e.Handled = true;
        if (!TryGetDroppedFilePaths(e.Data, out var paths))
        {
            e.Effects = DragDropEffects.None;
            ShowMessage("ドロップされたファイルまたはフォルダーを取得できませんでした。");
            return;
        }

        var accepted = await TryHandleFileDropAsync(paths, targetPath, move);
        e.Effects = accepted ? effect : DragDropEffects.None;
    }

    private async Task<bool> TryHandleFileDropAsync(
        IReadOnlyList<string> paths,
        string destinationPath,
        bool move)
    {
        if (_fileTransferCancellation is not null)
        {
            ShowMessage("ファイル操作を実行中です。");
            return false;
        }

        if (paths.Count == 0 || string.IsNullOrWhiteSpace(destinationPath))
        {
            ShowMessage("ドロップ先フォルダーの読み込みが完了していません。");
            return false;
        }

        if (ShouldCancelSameLocationFileDrop(paths, destinationPath))
        {
            ShowOperationStatus("同じ場所へのドロップをキャンセルしました。");
            return false;
        }

        try
        {
            await PrepareAndExecuteTransferAsync(paths, destinationPath, move);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileOperationException(ex) || IsExpectedClipboardException(ex))
        {
            ShowMessage("ドロップ操作を開始できません: " + ex.Message);
            return false;
        }
    }

    private static bool TryGetDroppedFilePaths(IDataObject dataObject, out string[] paths)
    {
        paths = [];
        try
        {
            if (dataObject.GetDataPresent(FolderFileDragFormat, autoConvert: false)
                && dataObject.GetData(FolderFileDragFormat, autoConvert: false) is string internalPaths)
            {
                paths = internalPaths
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length > 0)
                {
                    return true;
                }
            }

            if (!dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: true)
                || dataObject.GetData(DataFormats.FileDrop, autoConvert: true) is not string[] fileDropPaths)
            {
                return false;
            }

            paths = fileDropPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return paths.Length > 0;
        }
        catch (Exception ex) when (IsExpectedClipboardException(ex))
        {
            paths = [];
            return false;
        }
    }

    private FolderTabItemViewModel? FindDropTargetDirectory(DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, FolderItemsContent))
        {
            if (current is FrameworkElement
                {
                    DataContext: FolderTabItemViewModel { IsDirectory: true } item
                })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SetDropTargetItem(FolderTabItemViewModel? item)
    {
        if (ReferenceEquals(_dropTargetItem, item))
        {
            return;
        }

        if (_dropTargetItem is not null)
        {
            _dropTargetItem.IsDropTarget = false;
        }

        _dropTargetItem = item;
        if (_dropTargetItem is not null)
        {
            _dropTargetItem.IsDropTarget = true;
        }
    }

    private void ClearFileDropPreview()
    {
        SetDropTargetItem(null);
        DropTargetHighlight.Visibility = Visibility.Collapsed;
        DropActionText.Text = string.Empty;
    }

    private void ItemsGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle
            || FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext
                is not FolderTabItemViewModel { IsDirectory: true } item)
        {
            return;
        }

        e.Handled = true;
        RequestFolderOpen(item, FolderTabOpenDisposition.BackgroundTab);
    }

    private void ItemsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = ItemsGrid.InputHitTest(Mouse.GetPosition(ItemsGrid)) as DependencyObject;
        if (FindVisualParent<DataGridRow>(hit)?.DataContext is not FolderTabItemViewModel item)
        {
            e.Handled = true;
            if (FindResource("FolderTabBlankContextMenu") is ContextMenu blankContextMenu)
            {
                blankContextMenu.PlacementTarget = ItemsGrid;
                blankContextMenu.Placement = PlacementMode.MousePoint;
                blankContextMenu.IsOpen = true;
            }

            return;
        }

        if (!ItemsGrid.SelectedItems.Contains(item))
        {
            ItemsGrid.SelectedItems.Clear();
            ItemsGrid.SelectedItem = item;
        }

        ItemsGrid.CurrentItem = item;
    }

    private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var mode = _getSplitOpenMode?.Invoke() ?? FolderTabSplitOpenMode.Unavailable;
        var isDirectory = menu.DataContext is FolderTabItemViewModel { IsDirectory: true };
        SetMenuItemVisibility(menu, "folder-open-new-tab", isDirectory);
        SetMenuItemVisibility(menu, "folder-open-background-tab", isDirectory);
        SetMenuItemVisibility(menu, "folder-open-new-window", isDirectory);
        SetMenuItemVisibility(
            menu,
            "folder-open-opposite-pane",
            isDirectory && mode == FolderTabSplitOpenMode.OppositeFolderPane);
        SetMenuItemVisibility(
            menu,
            "folder-open-new-split",
            isDirectory && mode == FolderTabSplitOpenMode.NewSplit);
        SetMenuItemVisibility(menu, "folder-open-separator", isDirectory);
    }

    private void BlankContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        SetBlankMenuItemState(menu, "blank-paste", CanPasteClipboardFiles());
        SetBlankMenuItemState(
            menu,
            "blank-undo",
            _lastFileOperations.Count > 0 && _lastFileOperations.All(operation => operation.CanUndo));
        SetBlankMenuItemState(menu, "blank-select-all", Items.Count > 0);
        SetBlankMenuItemState(menu, "blank-copy-path", !string.IsNullOrWhiteSpace(CurrentPath));
        SetBlankMenuItemState(
            menu,
            "blank-save-workspace",
            _savePathsToWorkspaceAsync is not null && !string.IsNullOrWhiteSpace(CurrentPath));
        SetBlankMenuItemState(menu, "blank-open-explorer", !string.IsNullOrWhiteSpace(CurrentPath));

        SetBlankMenuItemChecked(
            menu,
            "blank-view-details",
            string.Equals(_viewMode, FolderTabViewMode.Details, StringComparison.Ordinal));
        SetBlankMenuItemChecked(
            menu,
            "blank-view-icons",
            string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal));
        SetBlankMenuItemChecked(menu, "blank-show-hidden", _showHiddenItems);
        SetBlankMenuItemChecked(menu, FolderTabSortMode.Name, string.Equals(_sortMode, FolderTabSortMode.Name, StringComparison.Ordinal));
        SetBlankMenuItemChecked(menu, FolderTabSortMode.Type, string.Equals(_sortMode, FolderTabSortMode.Type, StringComparison.Ordinal));
        SetBlankMenuItemChecked(
            menu,
            FolderTabSortMode.LastWriteTime,
            string.Equals(_sortMode, FolderTabSortMode.LastWriteTime, StringComparison.Ordinal));
        SetBlankMenuItemChecked(menu, FolderTabSortMode.Size, string.Equals(_sortMode, FolderTabSortMode.Size, StringComparison.Ordinal));
        SetBlankMenuItemChecked(
            menu,
            FolderTabSortDirection.Ascending,
            string.Equals(_sortDirection, FolderTabSortDirection.Ascending, StringComparison.Ordinal));
        SetBlankMenuItemChecked(
            menu,
            FolderTabSortDirection.Descending,
            string.Equals(_sortDirection, FolderTabSortDirection.Descending, StringComparison.Ordinal));

        var pinItem = FindMenuItem(menu, "blank-pin");
        if (pinItem is not null)
        {
            pinItem.IsEnabled = _setPlacePinned is not null && !string.IsNullOrWhiteSpace(CurrentPath);
            pinItem.Header = IsCurrentPlacePinned()
                ? "この場所のピン留めを解除"
                : "この場所をピン留め";
        }
    }

    private bool IsCurrentPlacePinned()
    {
        return !string.IsNullOrWhiteSpace(CurrentPath)
            && (_getPlacesSnapshot?.Invoke().PinnedPlaces.Any(place =>
                string.Equals(place.Path, CurrentPath, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static bool CanPasteClipboardFiles()
    {
        try
        {
            return Clipboard.GetDataObject()?.GetDataPresent(DataFormats.FileDrop, autoConvert: true) == true;
        }
        catch (Exception ex) when (IsExpectedClipboardException(ex))
        {
            return false;
        }
    }

    private static void SetBlankMenuItemState(ItemsControl menu, string tag, bool isEnabled)
    {
        var item = FindMenuItem(menu, tag);
        if (item is not null)
        {
            item.IsEnabled = isEnabled;
        }
    }

    private static void SetBlankMenuItemChecked(ItemsControl menu, string tag, bool isChecked)
    {
        var item = FindMenuItem(menu, tag);
        if (item is not null)
        {
            item.IsChecked = isChecked;
        }
    }

    private static void SetMenuItemVisibility(ItemsControl menu, string tag, bool isVisible)
    {
        var item = FindMenuElement(menu, tag);
        if (item is not null)
        {
            item.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static FrameworkElement? FindMenuElement(ItemsControl menu, string tag)
    {
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            if (item is MenuItem menuItem
                && FindMenuElement(menuItem, tag) is { } nestedItem)
            {
                return nestedItem;
            }
        }

        return null;
    }

    private static MenuItem? FindMenuItem(ItemsControl menu, string tag)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            var nestedItem = FindMenuItem(item, tag);
            if (nestedItem is not null)
            {
                return nestedItem;
            }
        }

        return null;
    }

    private void IconTile_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || !item.IsDirectory)
        {
            return;
        }

        e.Handled = true;
        RequestFolderOpen(item, FolderTabOpenDisposition.BackgroundTab);
    }

    private void OpenFolderInNewTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.ForegroundTab);
    }

    private void OpenFolderInBackgroundTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.BackgroundTab);
    }

    private void OpenFolderInNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.NewWindow);
    }

    private void OpenFolderInOppositePaneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.OppositeFolderPane);
    }

    private void OpenFolderInLeftRightSplitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.LeftRightSplit);
    }

    private void OpenFolderInTopBottomSplitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestFolderOpenFromMenu(sender, FolderTabOpenDisposition.TopBottomSplit);
    }

    private void RequestFolderOpenFromMenu(object sender, FolderTabOpenDisposition disposition)
    {
        if (sender is FrameworkElement { DataContext: FolderTabItemViewModel item }
            && item.IsDirectory)
        {
            RequestFolderOpen(item, disposition);
        }
    }

    private bool TryRequestModifiedFolderOpen(
        FolderTabItemViewModel item,
        ModifierKeys modifiers)
    {
        if (!item.IsDirectory)
        {
            return false;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            RequestFolderOpen(item, FolderTabOpenDisposition.NewWindow);
            return true;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            RequestFolderOpen(item, FolderTabOpenDisposition.BackgroundTab);
            return true;
        }

        return false;
    }

    private void RequestFolderOpen(
        FolderTabItemViewModel item,
        FolderTabOpenDisposition disposition)
    {
        OpenFolderRequested?.Invoke(
            this,
            new FolderTabOpenRequestedEventArgs(item.FullPath, disposition));
    }

    private async void IconTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal)
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || item.Icon is not null
            || _iconLoadCancellation is null)
        {
            return;
        }

        CancelVisibleIconLoad(item);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_iconLoadCancellation.Token);
        _visibleIconLoads[item] = cancellation;
        try
        {
            var visual = await _iconService.GetVisualAsync(item.FullPath, cancellation.Token);
            if (!cancellation.IsCancellationRequested
                && _itemsByPath.TryGetValue(item.FullPath, out var current)
                && ReferenceEquals(current, item))
            {
                item.Icon = visual.Image;
                item.IsThumbnail = visual.IsThumbnail;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_visibleIconLoads.TryGetValue(item, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _visibleIconLoads.Remove(item);
            }

            cancellation.Dispose();
        }
    }

    private void IconTile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            CancelVisibleIconLoad(item);
        }
    }

    private async void DetailTypeIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(_viewMode, FolderTabViewMode.Details, StringComparison.Ordinal)
            || sender is not FrameworkElement { DataContext: FolderTabItemViewModel item }
            || item.DetailIcon is not null
            || _iconLoadCancellation is null)
        {
            return;
        }

        CancelVisibleDetailIconLoad(item);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_iconLoadCancellation.Token);
        _visibleDetailIconLoads[item] = cancellation;
        try
        {
            var icon = await _iconService.GetTypeIconAsync(
                item.FullPath,
                item.IsDirectory,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested
                && _itemsByPath.TryGetValue(item.FullPath, out var current)
                && ReferenceEquals(current, item))
            {
                item.DetailIcon = icon;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_visibleDetailIconLoads.TryGetValue(item, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _visibleDetailIconLoads.Remove(item);
            }

            cancellation.Dispose();
        }
    }

    private void DetailTypeIcon_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderTabItemViewModel item })
        {
            CancelVisibleDetailIconLoad(item);
        }
    }

    private void IconRowsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal))
        {
            return;
        }

        _pendingIconColumnCount = CalculateIconColumnCount();
        if (_pendingIconColumnCount == _iconColumnCount)
        {
            return;
        }

        _iconLayoutTimer.Stop();
        _iconLayoutTimer.Start();
    }

    private void DetailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _detailScrollOffset = NormalizeScrollOffset(e.VerticalOffset);
    }

    private void IconScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _iconScrollOffset = NormalizeScrollOffset(e.VerticalOffset);
    }

    private async Task OpenItemAsync(FolderTabItemViewModel item)
    {
        if (item.IsDirectory)
        {
            await NavigateWithHistoryAsync(item.FullPath);
            return;
        }

        try
        {
            _service.Open(item.FullPath);
            _recordRecentItemOpened?.Invoke(item.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage("ファイルを開けませんでした: " + ex.Message);
        }
    }

    private async Task NavigateWithHistoryAsync(string folderPath)
    {
        if (!await NavigateCoreAsync(folderPath))
        {
            return;
        }

        if (_historyIndex >= 0
            && string.Equals(_history[_historyIndex], CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateNavigationButtons();
            return;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add(CurrentPath);
        _historyIndex = _history.Count - 1;
        FolderTabService.TrimNavigationHistory(_history, ref _historyIndex);
        UpdateNavigationButtons();
    }

    private async Task<bool> NavigateCoreAsync(string folderPath)
    {
        if (_isDisposed)
        {
            return false;
        }

        ExitRecursiveSearch(restoreItems: false);
        QuickLookPreviewHost.Close();
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        var cancellationToken = _navigationCancellation.Token;
        var isInitialLoad = !_hasLoadedContent;
        var shouldPreserveSelection = !string.IsNullOrWhiteSpace(CurrentPath)
            && string.Equals(
                folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        var selectedPaths = shouldPreserveSelection
            ? _selectedPaths.ToArray()
            : isInitialLoad ? _initialSelectedPaths : [];
        var selectionAnchorPath = shouldPreserveSelection
            ? _selectionAnchorPath
            : isInitialLoad ? _initialSelectionAnchorPath : null;
        var detailScrollOffset = shouldPreserveSelection || isInitialLoad ? _detailScrollOffset : 0;
        var iconScrollOffset = shouldPreserveSelection || isInitialLoad ? _iconScrollOffset : 0;
        if (!shouldPreserveSelection && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
        }

        ResetIconLoadScope();

        SetBusyState("フォルダーを読み込んでいます...");
        try
        {
            var listing = await _service.EnumerateAsync(
                folderPath,
                showHiddenItems: _showHiddenItems,
                cancellationToken: cancellationToken);
            if (_isDisposed || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _isSynchronizingSelection = true;
            try
            {
                Items.Clear();
                _itemsByPath.Clear();
                _allItemsByPath.Clear();
                _selectedPaths.Clear();
                var entries = listing.Entries.ToList();
                FolderTabService.SortEntries(entries, _sortMode, _sortDirection);
                foreach (var entry in entries)
                {
                    var item = new FolderTabItemViewModel(entry);
                    Items.Add(item);
                    _itemsByPath[item.FullPath] = item;
                    _allItemsByPath[item.FullPath] = item;
                }
            }
            finally
            {
                _isSynchronizingSelection = false;
            }

            SetSelectedPaths(selectedPaths.Where(_itemsByPath.ContainsKey));
            _selectionAnchorPath = selectionAnchorPath is not null
                && _selectedPaths.Contains(selectionAnchorPath)
                    ? selectionAnchorPath
                    : _selectedPaths.FirstOrDefault();
            CurrentPath = listing.FolderPath;
            _hasLoadedContent = true;
            _isListingTruncated = listing.IsTruncated;
            _directoryChangeStamp = FolderTabService.CreateChangeStamp(listing.Entries);
            _availableFreeSpace = FolderTabService.GetAvailableFreeSpace(CurrentPath);
            RebuildIconRows(force: true);
            RestoreSelectionForCurrentView();
            RestoreScrollOffsetsAfterLayout(detailScrollOffset, iconScrollOffset);

            PathBox.Text = CurrentPath;
            MessagePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MessageText.Text = Items.Count == 0 ? "このフォルダーは空です" : string.Empty;
            UpdateStatusSummary();
            UpdateNavigationButtons();
            ConfigureFolderWatcher();
            NavigationChanged?.Invoke(
                this,
                new FolderTabNavigationEventArgs(CurrentPath, FolderTabService.GetDisplayName(CurrentPath)));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowMessage("フォルダーを表示できませんでした: " + ex.Message);
            return false;
        }
    }

    private void SetBusyState(string message)
    {
        StatusText.Text = message;
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void ShowMessage(string message)
    {
        StatusText.Text = message;
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void UpdateStatusSummary()
    {
        if (!_hasLoadedContent)
        {
            return;
        }

        var selectedItems = _selectedPaths
            .Select(path => _itemsByPath.GetValueOrDefault(path))
            .Where(item => item is not null)
            .Cast<FolderTabItemViewModel>()
            .ToArray();
        var sizeItems = selectedItems.Length > 0 ? selectedItems : Items.ToArray();
        var totalSize = sizeItems.Where(item => !item.IsDirectory).Sum(item => item.SizeBytes ?? 0);
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(SearchBox.Text)
                ? $"{Items.Count:N0} 件"
                : $"{Items.Count:N0} / {_allItemsByPath.Count:N0} 件",
            $"選択 {selectedItems.Length:N0} 件",
            $"合計 {FolderTabService.FormatByteSize(totalSize)}"
        };
        if (_availableFreeSpace is { } freeSpace)
        {
            parts.Add($"空き {FolderTabService.FormatByteSize(freeSpace)}");
        }

        if (_isListingTruncated)
        {
            parts.Add($"表示上限 {FolderTabService.DefaultMaximumItemCount:N0} 件");
        }

        StatusText.Text = string.Join("  |  ", parts);
    }

    public void NotifyHostActivated()
    {
        _ = CheckForExternalChangesAsync();
    }

    private async Task CheckForExternalChangesAsync()
    {
        if (_isDisposed
            || _isActivationCheckRunning
            || !IsVisible
            || string.IsNullOrWhiteSpace(CurrentPath)
            || _fileTransferCancellation is not null)
        {
            return;
        }

        _isActivationCheckRunning = true;
        var checkedPath = CurrentPath;
        try
        {
            var stamp = await _service.GetChangeStampAsync(
                checkedPath,
                showHiddenItems: _showHiddenItems);
            if (string.Equals(CurrentPath, checkedPath, StringComparison.OrdinalIgnoreCase)
                && stamp != _directoryChangeStamp)
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowMessage("フォルダーの変更を確認できませんでした: " + ex.Message);
        }
        finally
        {
            _isActivationCheckRunning = false;
        }
    }

    private void ConfigureFolderWatcher()
    {
        DisposeFolderWatcher();
        if (_isDisposed || !IsVisible || string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        try
        {
            _folderWatcher = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _folderWatcher.Changed += FolderWatcher_Changed;
            _folderWatcher.Created += FolderWatcher_Changed;
            _folderWatcher.Deleted += FolderWatcher_Changed;
            _folderWatcher.Renamed += FolderWatcher_Changed;
            _folderWatcher.Error += FolderWatcher_Error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DisposeFolderWatcher();
        }
    }

    private void DisposeFolderWatcher()
    {
        if (_folderWatcher is null)
        {
            return;
        }

        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Changed -= FolderWatcher_Changed;
        _folderWatcher.Created -= FolderWatcher_Changed;
        _folderWatcher.Deleted -= FolderWatcher_Changed;
        _folderWatcher.Renamed -= FolderWatcher_Changed;
        _folderWatcher.Error -= FolderWatcher_Error;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private void FolderWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        ScheduleFolderWatcherRefresh();
    }

    private void FolderWatcher_Error(object sender, ErrorEventArgs e)
    {
        ScheduleFolderWatcherRefresh();
    }

    private void ScheduleFolderWatcherRefresh()
    {
        if (_isDisposed
            || _fileTransferCancellation is not null
            || DateTime.UtcNow < _ignoreWatcherEventsUntilUtc)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed || !IsVisible)
            {
                return;
            }

            _folderWatcherTimer.Stop();
            _folderWatcherTimer.Start();
        });
    }

    private async void FolderWatcherTimer_Tick(object? sender, EventArgs e)
    {
        _folderWatcherTimer.Stop();
        if (IsVisible && _fileTransferCancellation is null)
        {
            await RefreshAsync();
        }
    }

    private void FolderTabView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ConfigureFolderWatcher();
            NotifyHostActivated();
        }
        else
        {
            _folderWatcherTimer.Stop();
            DisposeFolderWatcher();
        }
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = !string.IsNullOrWhiteSpace(CurrentPath)
            && Directory.GetParent(CurrentPath) is not null;
    }

    private bool IsQuickLookEnabled()
    {
        return _quickLookSettings.IsEnabled && _quickLookSettings.EnableInFolderTabs;
    }

    public async Task ShowQuickLookForSelectionAsync()
    {
        if (!IsQuickLookEnabled())
        {
            return;
        }

        var selectedItems = _quickLookItemSource.SelectedItems;
        var items = selectedItems.Count > 0
            ? selectedItems
            : _quickLookItemSource.Current is { } current ? [current] : [];
        if (items.Count == 0)
        {
            return;
        }

        await QuickLookPreviewHost.ShowAsync(new QuickLookRequest(
            items,
            _quickLookItemSource.SourceId,
            _quickLookItemSource));
    }

    private void RefreshOpenQuickLook()
    {
        if (QuickLookPreviewHost.IsOpen)
        {
            _ = ShowQuickLookForSelectionAsync();
        }
    }

    private IReadOnlyList<QuickLookItem> CreateQuickLookItems()
    {
        return Items.Select(CreateQuickLookItem).ToList();
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedQuickLookItems()
    {
        return Items
            .Where(item => _selectedPaths.Contains(item.FullPath))
            .Select(CreateQuickLookItem)
            .ToList();
    }

    private int GetQuickLookCurrentIndex()
    {
        var path = _selectionAnchorPath ?? _selectedPaths.FirstOrDefault();
        if (path is null || !_itemsByPath.TryGetValue(path, out var item))
        {
            return -1;
        }

        return Items.IndexOf(item);
    }

    private void SelectQuickLookIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        var item = Items[index];
        _selectionAnchorPath = item.FullPath;
        SetSelectedPaths([item.FullPath]);
        RestoreSelectionForCurrentView();
        RefreshOpenQuickLook();
    }

    private QuickLookItem CreateQuickLookItem(FolderTabItemViewModel item)
    {
        return new QuickLookItem
        {
            DisplayName = item.Name,
            FilePath = item.FullPath,
            Kind = item.IsDirectory ? QuickLookItemKind.Directory : QuickLookItemKind.File,
            SourceId = _quickLookItemSource.SourceId
        };
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase
            or PasswordBox
            or ComboBox { IsEditable: true };
    }

    private void ApplyViewMode()
    {
        var showIcons = string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal);
        ItemsGrid.Visibility = showIcons ? Visibility.Collapsed : Visibility.Visible;
        IconRowsList.Visibility = showIcons ? Visibility.Visible : Visibility.Collapsed;
        ViewModeButton.Content = showIcons ? "アイコン" : "詳細";
        ViewModeButton.ToolTip = showIcons ? "詳細表示に切り替え" : "アイコン表示に切り替え";

        if (showIcons)
        {
            CancelAllVisibleDetailIconLoads();
            EnsureIconLoadScope();
            RebuildIconRows(force: true);
        }
        else
        {
            _iconLayoutTimer.Stop();
            CancelAllVisibleTileIconLoads();
            EnsureIconLoadScope();
        }

        RestoreSelectionForCurrentView();
    }

    private void ApplyDetailColumnOrder(IEnumerable<string>? columnOrder)
    {
        var columns = ItemsGrid.Columns
            .Where(static column => !string.IsNullOrWhiteSpace(column.SortMemberPath))
            .ToDictionary(static column => column.SortMemberPath, StringComparer.Ordinal);
        var normalizedOrder = FolderTabService.NormalizeDetailColumnOrder(columnOrder);
        for (var index = 0; index < normalizedOrder.Count; index++)
        {
            if (columns.TryGetValue(normalizedOrder[index], out var column))
            {
                column.DisplayIndex = index;
            }
        }
    }

    private List<string> CaptureDetailColumnOrder()
    {
        return FolderTabService.NormalizeDetailColumnOrder(
            ItemsGrid.Columns
                .OrderBy(static column => column.DisplayIndex)
                .Select(static column => column.SortMemberPath));
    }

    private void CaptureSelectionFromCurrentView()
    {
        if (string.Equals(_viewMode, FolderTabViewMode.Details, StringComparison.Ordinal))
        {
            CaptureDetailSelection();
        }
    }

    private void CaptureDetailSelection()
    {
        SetSelectedPaths(ItemsGrid.SelectedItems
            .OfType<FolderTabItemViewModel>()
            .Select(item => item.FullPath));
        _selectionAnchorPath = (ItemsGrid.CurrentItem as FolderTabItemViewModel
            ?? ItemsGrid.SelectedItem as FolderTabItemViewModel)?.FullPath;
    }

    private void SelectIconItem(FolderTabItemViewModel item, ModifierKeys modifiers)
    {
        var next = new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase);
        FolderTabItemViewModel? anchor = null;
        var useRange = modifiers.HasFlag(ModifierKeys.Shift)
            && _selectionAnchorPath is not null
            && _itemsByPath.TryGetValue(_selectionAnchorPath, out anchor);
        if (useRange)
        {
            if (!modifiers.HasFlag(ModifierKeys.Control))
            {
                next.Clear();
            }

            var startIndex = Items.IndexOf(anchor!);
            var endIndex = Items.IndexOf(item);
            if (startIndex > endIndex)
            {
                (startIndex, endIndex) = (endIndex, startIndex);
            }

            for (var index = startIndex; index <= endIndex; index++)
            {
                next.Add(Items[index].FullPath);
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!next.Add(item.FullPath))
            {
                next.Remove(item.FullPath);
            }

            _selectionAnchorPath = item.FullPath;
        }
        else
        {
            next.Clear();
            next.Add(item.FullPath);
            _selectionAnchorPath = item.FullPath;
        }

        SetSelectedPaths(next);
        RefreshOpenQuickLook();
    }

    private void SetSelectedPaths(IEnumerable<string> paths)
    {
        var next = new HashSet<string>(
            paths.Where(_itemsByPath.ContainsKey),
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in _selectedPaths.Except(next).ToArray())
        {
            if (_itemsByPath.TryGetValue(path, out var item))
            {
                item.IsSelected = false;
            }
        }

        foreach (var path in next.Except(_selectedPaths).ToArray())
        {
            _itemsByPath[path].IsSelected = true;
        }

        _selectedPaths.Clear();
        foreach (var path in next)
        {
            _selectedPaths.Add(path);
        }

        UpdateStatusSummary();
    }

    private void RestoreSelectionForCurrentView()
    {
        if (string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal))
        {
            ScrollSelectedIconIntoView();
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            ItemsGrid.SelectedItems.Clear();
            foreach (var path in _selectedPaths)
            {
                if (_itemsByPath.TryGetValue(path, out var item))
                {
                    ItemsGrid.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        if (_selectedPaths.FirstOrDefault() is { } selectedPath
            && _itemsByPath.TryGetValue(selectedPath, out var selectedItem))
        {
            _ = Dispatcher.BeginInvoke(
                () => ItemsGrid.ScrollIntoView(selectedItem),
                DispatcherPriority.Loaded);
        }
    }

    private void IconLayoutTimer_Tick(object? sender, EventArgs e)
    {
        _iconLayoutTimer.Stop();
        if (string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal)
            && _pendingIconColumnCount != _iconColumnCount)
        {
            RebuildIconRows(force: true);
        }
    }

    private int CalculateIconColumnCount()
    {
        var availableWidth = IconRowsList.ActualWidth;
        if (availableWidth <= 0)
        {
            availableWidth = ActualWidth;
        }

        return Math.Max(1, (int)Math.Floor(availableWidth / IconTileOuterWidth));
    }

    private void RebuildIconRows(bool force)
    {
        if (!string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal))
        {
            return;
        }

        var columnCount = CalculateIconColumnCount();
        _pendingIconColumnCount = columnCount;
        if (!force && columnCount == _iconColumnCount && _iconRows.Count > 0)
        {
            return;
        }

        var scrollViewer = GetIconScrollViewer();
        var previousOffset = scrollViewer?.VerticalOffset ?? 0;
        var hadRows = _iconRows.Count > 0;
        _iconColumnCount = columnCount;
        var rows = new List<FolderTabIconRow>((Items.Count + columnCount - 1) / columnCount);
        for (var index = 0; index < Items.Count; index += columnCount)
        {
            var count = Math.Min(columnCount, Items.Count - index);
            var rowItems = new FolderTabItemViewModel[count];
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                rowItems[itemIndex] = Items[index + itemIndex];
            }

            rows.Add(new FolderTabIconRow(rowItems));
        }

        _iconRows = rows;
        IconRowsList.ItemsSource = _iconRows;
        if (hadRows)
        {
            _ = Dispatcher.BeginInvoke(
                () => GetIconScrollViewer()?.ScrollToVerticalOffset(previousOffset),
                DispatcherPriority.Loaded);
        }
    }

    private void ScrollSelectedIconIntoView()
    {
        var selectedPath = _selectedPaths.FirstOrDefault();
        if (selectedPath is null || !_itemsByPath.TryGetValue(selectedPath, out var selectedItem))
        {
            return;
        }

        var itemIndex = Items.IndexOf(selectedItem);
        var rowIndex = itemIndex / Math.Max(1, _iconColumnCount);
        if (rowIndex < 0 || rowIndex >= _iconRows.Count)
        {
            return;
        }

        var row = _iconRows[rowIndex];
        _ = Dispatcher.BeginInvoke(
            () => IconRowsList.ScrollIntoView(row),
            DispatcherPriority.Loaded);
    }

    private void EnsureIconLoadScope()
    {
        if (_iconLoadCancellation is null && !_isDisposed)
        {
            _iconLoadCancellation = new CancellationTokenSource();
        }
    }

    private void ResetIconLoadScope()
    {
        CancelIconLoading();
        EnsureIconLoadScope();
    }

    private void CancelIconLoading()
    {
        _iconLoadCancellation?.Cancel();
        _iconLoadCancellation?.Dispose();
        _iconLoadCancellation = null;
        CancelAllVisibleIconLoads();
    }

    private void CancelAllVisibleIconLoads()
    {
        CancelAllVisibleTileIconLoads();
        CancelAllVisibleDetailIconLoads();
    }

    private void CancelAllVisibleTileIconLoads()
    {
        var loads = _visibleIconLoads.Values.ToArray();
        _visibleIconLoads.Clear();
        foreach (var cancellation in loads)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelAllVisibleDetailIconLoads()
    {
        var loads = _visibleDetailIconLoads.Values.ToArray();
        _visibleDetailIconLoads.Clear();
        foreach (var cancellation in loads)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelVisibleIconLoad(FolderTabItemViewModel item)
    {
        if (_visibleIconLoads.Remove(item, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelVisibleDetailIconLoad(FolderTabItemViewModel item)
    {
        if (_visibleDetailIconLoads.Remove(item, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void ScrollToBoundary(bool scrollToBottom)
    {
        if (Items.Count == 0)
        {
            return;
        }

        if (string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal))
        {
            if (_iconRows.Count > 0)
            {
                IconRowsList.ScrollIntoView(scrollToBottom ? _iconRows[^1] : _iconRows[0]);
            }

            return;
        }

        ItemsGrid.ScrollIntoView(scrollToBottom ? Items[^1] : Items[0]);
    }

    private ScrollViewer? GetCurrentScrollViewer()
    {
        return string.Equals(_viewMode, FolderTabViewMode.Icons, StringComparison.Ordinal)
            ? GetIconScrollViewer()
            : GetDetailScrollViewer();
    }

    private ScrollViewer? GetDetailScrollViewer()
    {
        ItemsGrid.ApplyTemplate();
        return FindVisualChild<ScrollViewer>(ItemsGrid);
    }

    private ScrollViewer? GetIconScrollViewer()
    {
        IconRowsList.ApplyTemplate();
        return FindVisualChild<ScrollViewer>(IconRowsList);
    }

    private void RestoreScrollOffsetsAfterLayout(double detailOffset, double iconOffset)
    {
        var normalizedDetailOffset = NormalizeScrollOffset(detailOffset);
        var normalizedIconOffset = NormalizeScrollOffset(iconOffset);
        _detailScrollOffset = normalizedDetailOffset;
        _iconScrollOffset = normalizedIconOffset;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed || !_hasLoadedContent)
            {
                return;
            }

            GetDetailScrollViewer()?.ScrollToVerticalOffset(normalizedDetailOffset);
            GetIconScrollViewer()?.ScrollToVerticalOffset(normalizedIconOffset);
        }), DispatcherPriority.ContextIdle);
    }

    private static IReadOnlyList<string> NormalizeSessionPaths(IEnumerable<string>? paths)
    {
        return paths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .ToList() ?? [];
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    private static double NormalizeScrollOffset(double value)
    {
        return double.IsFinite(value) && value >= 0 ? value : 0;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T target)
            {
                return target;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

public sealed class FolderTabItemViewModel(FolderTabEntry entry) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private ImageSource? _detailIcon;
    private bool _isThumbnail;
    private bool _isSelected;
    private bool _isDropTarget;

    public FolderTabEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.RelativePath)
        ? Entry.Name
        : Entry.RelativePath;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public bool CanOpenInMiniPlayer => !IsDirectory && WorkspaceShellItemService.IsVideoPath(FullPath);

    public string TypeText => Entry.TypeText;

    public string SizeText => Entry.SizeText;

    public long? SizeBytes => Entry.SizeBytes;

    public string LastWriteTimeText => Entry.LastWriteTimeText;

    public DateTime? LastWriteTime => Entry.LastWriteTime;

    public bool IsHidden => Entry.IsHidden;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => Icon is not null;

    public ImageSource? DetailIcon
    {
        get => _detailIcon;
        set
        {
            if (ReferenceEquals(_detailIcon, value))
            {
                return;
            }

            _detailIcon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDetailIcon));
        }
    }

    public bool HasDetailIcon => DetailIcon is not null;

    public bool IsThumbnail
    {
        get => _isThumbnail;
        set
        {
            if (_isThumbnail == value)
            {
                return;
            }

            _isThumbnail = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value)
            {
                return;
            }

            _isDropTarget = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class FolderTabIconRow(IReadOnlyList<FolderTabItemViewModel> items)
{
    public IReadOnlyList<FolderTabItemViewModel> Items { get; } = items;
}

public sealed class FolderTabNavigationEventArgs(string folderPath, string title) : EventArgs
{
    public string FolderPath { get; } = folderPath;

    public string Title { get; } = title;
}

public enum FolderTabOpenDisposition
{
    ForegroundTab,
    BackgroundTab,
    NewWindow,
    OppositeFolderPane,
    LeftRightSplit,
    TopBottomSplit
}

public enum FolderTabSplitOpenMode
{
    Unavailable,
    NewSplit,
    OppositeFolderPane
}

public sealed class FolderTabOpenRequestedEventArgs(
    string folderPath,
    FolderTabOpenDisposition disposition) : EventArgs
{
    public string FolderPath { get; } = folderPath;

    public FolderTabOpenDisposition Disposition { get; } = disposition;
}

public sealed class FolderTabViewModeChangedEventArgs(string viewMode) : EventArgs
{
    public string ViewMode { get; } = viewMode;
}

public sealed class FolderTabSortChangedEventArgs(string sortMode, string sortDirection) : EventArgs
{
    public string SortMode { get; } = sortMode;

    public string SortDirection { get; } = sortDirection;
}

public sealed class FolderTabHiddenItemsVisibilityChangedEventArgs(bool showHiddenItems) : EventArgs
{
    public bool ShowHiddenItems { get; } = showHiddenItems;
}
