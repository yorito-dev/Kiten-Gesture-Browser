using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Services;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;
using RecentItem = TabBrowser.Models.RecentItem;
using RecentItemCategory = TabBrowser.Models.RecentItemCategory;
using RecentItemFactory = TabBrowser.Services.RecentItemFactory;

namespace TabBrowser.ViewModels;

public sealed class WorkspaceOSViewModel : INotifyPropertyChanged, IWorkspaceChangeListener, IDisposable, IAsyncDisposable
{
    private const double ArrangeStartMinimum = 24;
    private const double AvoidanceStepX = 32;
    private const double AvoidanceStepY = 40;
    private const int MaxFaviconBackfillCount = 32;
    private const int MaxShellIconBackfillCount = 32;
    private const int MaxShellMediaThumbnailBackfillCount = 20;
    private const int MaxYouTubeMiniPlayerThumbnailBackfillCount = 20;
    private const int MaxFaviconBytes = 256 * 1024;
    private const int MaxYouTubeThumbnailBytes = 2 * 1024 * 1024;
    private const int MaxThumbnailRefreshesPerSession = 20;
    private static readonly TimeSpan ThumbnailRefreshInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan ThumbnailSaveDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WorkspaceWheelSwitchDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan WorkspaceBackfillDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan WorkspacePreloadDelay = TimeSpan.FromMilliseconds(250);
    private static readonly (int X, int Y)[] AvoidanceDirections =
    [
        (1, 0),
        (1, 1),
        (0, 1),
        (-1, 1),
        (-1, 0),
        (-1, -1),
        (0, -1),
        (1, -1)
    ];
    private static readonly HttpClient FaviconHttpClient = CreateFaviconHttpClient();
    private static readonly HttpClient YouTubeThumbnailHttpClient = CreateYouTubeThumbnailHttpClient();

    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceArrangementService _arrangementService = new();
    private readonly WorkspaceShellItemService _shellItemService = new();
    private readonly Guid? _initialWorkspaceId;
    private readonly WorkspaceTabPort _tabs;
    private readonly WorkspaceMiniPlayerPort _miniPlayers;
    private readonly WorkspaceWindowPort _windows;
    private readonly WorkspaceThumbnailPort _thumbnails;
    private readonly Dictionary<Guid, WorkspaceRuntimeState> _workspaceRuntimeCache = [];
    private List<WorkspaceItemViewModel> _allItems = [];
    private readonly SemaphoreSlim _workspaceLoadGate = new(1, 1);
    private readonly HashSet<Guid> _capturingThumbnailItemIds = [];
    private readonly HashSet<Guid> _shellIconAttemptedItemIds = [];
    private readonly HashSet<Task> _shellIconTasks = [];
    private readonly Dictionary<Guid, (string PngBase64, DateTimeOffset UpdatedAt)> _pendingThumbnailSaves = [];
    private Task? _pendingThumbnailSaveTask;
    private int _thumbnailRefreshCount;
    private List<WorkspaceItemPosition>? _lastArrangePositions;
    private readonly Guid? _initialFolderId;
    private readonly Guid? _initialSelectedItemId;
    private bool _initialized;
    private bool _isLoading;
    private bool _isWorkspaceSaving;
    private bool _isApplyingArrangement;
    private bool _isBackfillingFavicons;
    private bool _isBackfillingShellIcons;
    private bool _isBackfillingShellMediaThumbnails;
    private bool _isBackfillingYouTubeMiniPlayerThumbnails;
    private bool _hasWorkspaceError;
    private Workspace? _currentWorkspace;
    private WorkspaceItemViewModel? _selectedItem;
    private Guid? _currentFolderId;
    private bool _isHyperSearchVisible;
    private bool _isRecentItemsVisible;
    private RecentItemCategory? _recentFilter;
    private RecentItemsService? _recentItemsService;
    private bool _recordRecentItems;
    private string _statusText = "Workspace OS";
    private string _workspaceErrorText = string.Empty;
    private string _searchQuery = string.Empty;
    private WorkspaceShellUndoState? _lastShellOperation;
    private bool _undoShellOperationInProgress;
    private WorkspaceSlotViewModel? _pendingWheelWorkspace;
    private WorkspaceRuntimeState? _currentWorkspaceRuntime;
    private CancellationTokenSource? _workspacePreloadCancellation;
    private Task? _workspacePreloadTask;
    private bool _isProcessingWorkspaceWheelSwitch;
    private bool _isDisposed;
    private bool _isViewActive = true;
    private int _workspaceWheelSwitchVersion;
    private int _workspaceSwitchRequestVersion;
    private int _workspaceActivationVersion;
    private DateTimeOffset _lastWorkspaceWheelInputAt;

    public WorkspaceOSViewModel(
        WorkspaceService workspaceService,
        Guid? initialWorkspaceId,
        WorkspaceTabPort tabs,
        WorkspaceMiniPlayerPort miniPlayers,
        WorkspaceWindowPort windows,
        WorkspaceThumbnailPort thumbnails,
        Guid? initialFolderId = null,
        Guid? initialSelectedItemId = null)
    {
        _workspaceService = workspaceService;
        _initialWorkspaceId = initialWorkspaceId;
        _tabs = tabs;
        _miniPlayers = miniPlayers;
        _windows = windows;
        _thumbnails = thumbnails;
        _initialFolderId = initialFolderId;
        _initialSelectedItemId = initialSelectedItemId;
        _workspaceService.SubscribeToChanges(this);

        Workspaces = [];
        Items = [];
        SearchResults = [];
        RecentItems = [];
        Items.CollectionChanged += Items_CollectionChanged;
        SearchResults.CollectionChanged += SearchResults_CollectionChanged;
        TabShelf = new TabShelfViewModel(
            _workspaceService,
            tabs,
            miniPlayers,
            () => CurrentWorkspace,
            () => CurrentFolderId,
            GetNextCanvasPoint,
            AddSavedItemToCanvas,
            SetStatus);

        SwitchWorkspaceCommand = new AsyncRelayCommand(SwitchWorkspaceAsync, static parameter => parameter is WorkspaceSlotViewModel);
        RenameWorkspaceCommand = new AsyncRelayCommand(
            RenameWorkspaceAsync,
            static parameter => parameter is WorkspaceNameChangeRequest);
        MoveItemCommand = new AsyncRelayCommand(MoveItemAsync, static parameter => parameter is WorkspaceItemViewModel);
        MoveItemsCommand = new AsyncRelayCommand(MoveItemsAsync, static parameter => parameter is IReadOnlyList<WorkspaceItemViewModel> { Count: > 0 });
        MoveDraggedItemsCommand = new AsyncRelayCommand(
            MoveDraggedItemsAsync,
            static parameter => parameter is WorkspaceCardDropRequest { Payload.Roots.Count: > 0 });
        DeleteDraggedItemsCommand = new AsyncRelayCommand(
            DeleteDraggedItemsAsync,
            static parameter => parameter is WorkspaceCardDragPayload { Roots.Count: > 0 });
        OpenItemCommand = new AsyncRelayCommand(OpenItemAsync, static parameter => parameter is WorkspaceItemViewModel);
        OpenItemsCommand = new AsyncRelayCommand(OpenItemsAsync, static parameter => parameter is IReadOnlyList<WorkspaceItemViewModel> { Count: > 0 });
        OpenItemsInNewWindowCommand = new AsyncRelayCommand(OpenItemsInNewWindowAsync, static parameter => parameter is IReadOnlyList<WorkspaceItemViewModel> { Count: > 0 });
        DuplicateItemCommand = new AsyncRelayCommand(DuplicateItemAsync, static parameter => parameter is WorkspaceItemViewModel);
        RenameItemCommand = new AsyncRelayCommand(RenameItemAsync, static parameter => parameter is WorkspaceRenameRequest);
        EditNoteCommand = new AsyncRelayCommand(EditNoteAsync, static parameter => parameter is WorkspaceNoteEditRequest);
        DeleteItemCommand = new AsyncRelayCommand(DeleteItemAsync, static parameter => parameter is WorkspaceItemViewModel);
        DeleteItemsCommand = new AsyncRelayCommand(DeleteItemsAsync, static parameter => parameter is IReadOnlyList<WorkspaceItemViewModel> { Count: > 0 });
        ArrangeItemsCommand = new AsyncRelayCommand(ArrangeItemsAsync, static parameter => parameter is WorkspaceArrangeRequest);
        RestoreLastArrangeCommand = new AsyncRelayCommand(RestoreLastArrangeAsync);
        AddNoteCommand = new AsyncRelayCommand(AddNoteAsync, static parameter => parameter is WorkspaceNoteCreateRequest);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, static parameter => parameter is WorkspaceCanvasPoint);
        SaveDroppedTabsCommand = new AsyncRelayCommand(SaveDroppedTabsAsync, static parameter => parameter is WorkspaceTabDropRequest);
        SaveDroppedShelfItemsCommand = new AsyncRelayCommand(
            SaveDroppedShelfItemsAsync,
            static parameter => parameter is WorkspaceShelfDropRequest request
                && (request.Tabs.Count > 0 || request.YouTubeMiniPlayers.Count > 0));
        SaveDroppedShellItemsCommand = new AsyncRelayCommand(
            SaveDroppedShellItemsAsync,
            static parameter => parameter is WorkspaceShellDropRequest { Paths.Count: > 0 });
        ConvertShellTextFileToNoteCommand = new AsyncRelayCommand(
            ConvertShellTextFileToNoteAsync,
            static parameter => parameter is WorkspaceItemViewModel
            {
                Item: ShellItem { Kind: ShellItemKind.File } shellItem
            } && WorkspaceShellItemService.IsTextDocumentPath(shellItem.TargetPath));
        ShowShellItemInFolderCommand = new RelayCommand(ShowShellItemInFolder, static parameter => parameter is WorkspaceItemViewModel { Item: ShellItem });
        RelinkShellItemCommand = new AsyncRelayCommand(RelinkShellItemAsync, static parameter => parameter is WorkspaceShellPathRequest);
        CreateShellDirectoryCommand = new AsyncRelayCommand(CreateShellDirectoryAsync, static parameter => parameter is WorkspaceShellNameRequest);
        RenameShellTargetCommand = new AsyncRelayCommand(RenameShellTargetAsync, static parameter => parameter is WorkspaceShellNameRequest);
        CopyShellItemCommand = new AsyncRelayCommand(CopyShellItemAsync, static parameter => parameter is WorkspaceShellPathRequest);
        MoveShellItemCommand = new AsyncRelayCommand(MoveShellItemAsync, static parameter => parameter is WorkspaceShellPathRequest);
        RecycleShellItemCommand = new AsyncRelayCommand(RecycleShellItemAsync, static parameter => parameter is WorkspaceItemViewModel { Item: ShellItem });
        UndoShellOperationCommand = new AsyncRelayCommand(UndoShellOperationAsync, _ => CanUndoShellOperation);
        OpenRecycleBinCommand = new RelayCommand(_ => OpenRecycleBin());
        GoToParentFolderCommand = new AsyncRelayCommand(GoToParentFolderAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearSearchCommand = new RelayCommand(_ => ClearSearch());
        ToggleHyperSearchCommand = new RelayCommand(_ => ToggleHyperSearch());
        ToggleRecentItemsCommand = new RelayCommand(_ => ToggleRecentItems());
        SetRecentFilterCommand = new RelayCommand(SetRecentFilter);
        ToggleRecentPinCommand = new RelayCommand(ToggleRecentPin, static parameter => parameter is RecentItem);
        RemoveRecentItemCommand = new RelayCommand(RemoveRecentItem, static parameter => parameter is RecentItem);
        GoToSearchResultCommand = new AsyncRelayCommand(GoToSearchResultAsync, static parameter => parameter is WorkspaceSearchResultViewModel);
        SaveSelectedTabsToFolderCommand = new AsyncRelayCommand(SaveSelectedTabsToFolderAsync, static parameter => parameter is WorkspaceItemViewModel);
        SaveAllTabsToFolderCommand = new AsyncRelayCommand(SaveAllTabsToFolderAsync, static parameter => parameter is WorkspaceItemViewModel);
        ReloadWorkspaceCommand = new AsyncRelayCommand(ReloadWorkspaceAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<ObservableCollection<WorkspaceItemViewModel>>? WorkspaceItemsPreloaded;

    public ObservableCollection<WorkspaceSlotViewModel> Workspaces { get; }

    public ObservableCollection<WorkspaceItemViewModel> Items { get; private set; }

    public ObservableCollection<WorkspaceSearchResultViewModel> SearchResults { get; }

    public ObservableCollection<RecentItem> RecentItems { get; }

    public TabShelfViewModel TabShelf { get; }

    internal void SetViewActive(bool isActive)
    {
        if (_isViewActive == isActive)
        {
            return;
        }

        _isViewActive = isActive;
        if (!isActive
            || !_initialized
            || _currentWorkspaceRuntime is null)
        {
            return;
        }

        var selectedIds = SelectedItems.Select(static item => item.Id).ToHashSet();
        var primaryItemId = SelectedItem?.Id;
        RefreshFolderChildCounts();
        ApplyVisibleItemsPreservingSelection(
            selectedIds,
            primaryItemId,
            reuseVisibleItems: true);
    }

    public ICommand SwitchWorkspaceCommand { get; }

    public ICommand RenameWorkspaceCommand { get; }

    public ICommand MoveItemCommand { get; }

    public ICommand MoveItemsCommand { get; }

    public ICommand MoveDraggedItemsCommand { get; }

    public ICommand DeleteDraggedItemsCommand { get; }

    public ICommand OpenItemCommand { get; }

    public ICommand OpenItemsCommand { get; }

    public ICommand OpenItemsInNewWindowCommand { get; }

    public ICommand DuplicateItemCommand { get; }

    public ICommand RenameItemCommand { get; }

    public ICommand EditNoteCommand { get; }

    public ICommand DeleteItemCommand { get; }

    public ICommand DeleteItemsCommand { get; }

    public ICommand ArrangeItemsCommand { get; }

    public ICommand RestoreLastArrangeCommand { get; }

    public bool HasArrangeSnapshot => _lastArrangePositions is { Count: > 0 };

    public int ArrangeSelectionCount => SelectedItems.Count(item => Items.Contains(item));

    public bool CanArrangeSelection => ArrangeSelectionCount >= 2;

    public string ArrangeMenuText => CanArrangeSelection
        ? $"選択 {ArrangeSelectionCount}件を整列"
        : "全カードを整列";

    public string ArrangeSelectionMenuText => $"選択 {ArrangeSelectionCount}件を整列";

    public ICommand AddNoteCommand { get; }

    public ICommand AddFolderCommand { get; }

    public ICommand SaveDroppedTabsCommand { get; }

    public ICommand SaveDroppedShelfItemsCommand { get; }

    public ICommand SaveDroppedShellItemsCommand { get; }

    public ICommand ConvertShellTextFileToNoteCommand { get; }

    public ICommand ShowShellItemInFolderCommand { get; }

    public ICommand RelinkShellItemCommand { get; }

    public ICommand CreateShellDirectoryCommand { get; }

    public ICommand RenameShellTargetCommand { get; }

    public ICommand CopyShellItemCommand { get; }

    public ICommand MoveShellItemCommand { get; }

    public ICommand RecycleShellItemCommand { get; }

    public ICommand UndoShellOperationCommand { get; }

    public ICommand OpenRecycleBinCommand { get; }

    public bool CanUndoShellOperation => !_undoShellOperationInProgress && _lastShellOperation is { Result.CanUndo: true };

    public ICommand GoToParentFolderCommand { get; }

    public ICommand SearchCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ToggleHyperSearchCommand { get; }

    public ICommand ToggleRecentItemsCommand { get; }

    public ICommand SetRecentFilterCommand { get; }

    public ICommand ToggleRecentPinCommand { get; }

    public ICommand RemoveRecentItemCommand { get; }

    public ICommand GoToSearchResultCommand { get; }

    public ICommand SaveSelectedTabsToFolderCommand { get; }

    public ICommand SaveAllTabsToFolderCommand { get; }

    public ICommand ReloadWorkspaceCommand { get; }

    public async Task EnsureItemThumbnailAsync(WorkspaceItemViewModel item)
    {
        if (item.IsSplitSession)
        {
            await EnsureSplitSessionThumbnailsAsync(item);
            return;
        }

        var now = DateTimeOffset.Now;
        var shellMediaItem = item.Item is ShellItem shellItem
            && WorkspaceShellItemService.IsMediaPath(shellItem.TargetPath)
                ? shellItem
                : null;
        var youTubeMiniPlayerSet = item.Item as YouTubeMiniPlayerSetItem;
        var hasCurrentThumbnail = shellMediaItem is not null
            ? HasCurrentShellMediaThumbnail(item, shellMediaItem, now)
            : youTubeMiniPlayerSet is not null
                ? item.HasThumbnail
                    && !CapturedTabSnapshot.IsMostlyBlackThumbnail(item.Thumbnail)
                    && item.Item.ThumbnailUpdatedAt is { } miniPlayerUpdatedAt
                    && now - miniPlayerUpdatedAt < ThumbnailRefreshInterval
                : item.HasThumbnail
                    && WebThumbnailCaptureService.HasInformationBand(item.Thumbnail)
                    && item.Item.ThumbnailUpdatedAt is { } updatedAt
                    && now - updatedAt < ThumbnailRefreshInterval;
        if (hasCurrentThumbnail
            || (shellMediaItem is null
                && youTubeMiniPlayerSet is null
                && string.IsNullOrWhiteSpace(item.Url))
            || (shellMediaItem is null && item.Type is WorkspaceItemType.Folder or WorkspaceItemType.Note)
            || (shellMediaItem is null
                && youTubeMiniPlayerSet is null
                && _thumbnailRefreshCount >= MaxThumbnailRefreshesPerSession)
            || !_capturingThumbnailItemIds.Add(item.Id))
        {
            return;
        }

        try
        {
            if (shellMediaItem is null && youTubeMiniPlayerSet is null)
            {
                _thumbnailRefreshCount++;
            }
            var thumbnailPngBase64 = shellMediaItem is not null
                ? await _shellItemService.GetMediaThumbnailPngBase64Async(shellMediaItem)
                : youTubeMiniPlayerSet is not null
                    ? await TryFetchYouTubeMiniPlayerThumbnailAsync(youTubeMiniPlayerSet)
                    : CapturedTabSnapshot.CreateThumbnailPngBase64(
                        await _thumbnails.CaptureUrlAsync(item.Url, item.Title));
            if (string.IsNullOrWhiteSpace(thumbnailPngBase64))
            {
                return;
            }

            var thumbnailUpdatedAt = DateTimeOffset.Now;
            item.SetThumbnailPngBase64(thumbnailPngBase64, thumbnailUpdatedAt);
            QueueThumbnailSave(item.Id, thumbnailPngBase64, thumbnailUpdatedAt);
        }
        catch
        {
            // ホバー補完は補助処理なので、失敗してもカード表示を維持します。
        }
        finally
        {
            _capturingThumbnailItemIds.Remove(item.Id);
        }
    }

    private async Task EnsureSplitSessionThumbnailsAsync(WorkspaceItemViewModel item)
    {
        var missingTabs = item.PrimarySplitTabs
            .Where(static tab => !tab.IsFolderTab
                && !WebThumbnailCaptureService.HasInformationBand(tab.ThumbnailPngBase64))
            .ToList();
        if (missingTabs.Count == 0
            || _thumbnailRefreshCount >= MaxThumbnailRefreshesPerSession
            || !_capturingThumbnailItemIds.Add(item.Id))
        {
            return;
        }

        try
        {
            var updates = new Dictionary<Guid, string>();
            foreach (var tab in missingTabs)
            {
                if (_thumbnailRefreshCount >= MaxThumbnailRefreshesPerSession)
                {
                    break;
                }

                _thumbnailRefreshCount++;
                var thumbnail = await _thumbnails.CaptureUrlAsync(tab.Url, tab.Title);
                if (CapturedTabSnapshot.IsMostlyBlackThumbnail(thumbnail))
                {
                    continue;
                }

                var thumbnailPngBase64 = CapturedTabSnapshot.CreateThumbnailPngBase64(thumbnail);
                if (string.IsNullOrWhiteSpace(thumbnailPngBase64))
                {
                    continue;
                }

                tab.ThumbnailPngBase64 = thumbnailPngBase64;
                updates[tab.Id] = thumbnailPngBase64;
            }

            if (updates.Count == 0 || !_allItems.Contains(item))
            {
                return;
            }

            await _workspaceService.UpdateSessionTabThumbnailsAsync(item.Id, updates);
            item.RefreshSplitPreviewImages();
        }
        catch
        {
            // 分割サムネイルは補助表示なので、失敗時はfaviconまたは種別表示を維持します。
        }
        finally
        {
            _capturingThumbnailItemIds.Remove(item.Id);
        }
    }

    public Task EnsureShellItemIconAsync(WorkspaceItemViewModel item)
        => TrackShellIconWork(() => EnsureShellItemIconCoreAsync(item));

    private async Task EnsureShellItemIconCoreAsync(WorkspaceItemViewModel item)
    {
        if (item.Item is not ShellItem shellItem
            || item.HasFavicon
            || !_shellIconAttemptedItemIds.Add(item.Id))
        {
            return;
        }

        try
        {
            item.RefreshShellState();
            var iconPngBase64 = await _shellItemService.GetIconPngBase64Async(shellItem);
            if (_isDisposed || string.IsNullOrWhiteSpace(iconPngBase64))
            {
                return;
            }

            item.SetFaviconPngBase64(iconPngBase64);
            await _workspaceService.UpdateItemFaviconAsync(item.Id, iconPngBase64);
        }
        catch
        {
            // Shell アイコンは補助表示なので、取得失敗時もカード操作を維持します。
        }
    }

    private void QueueThumbnailSave(Guid itemId, string thumbnailPngBase64, DateTimeOffset thumbnailUpdatedAt)
    {
        _pendingThumbnailSaves[itemId] = (thumbnailPngBase64, thumbnailUpdatedAt);
        _pendingThumbnailSaveTask ??= SavePendingThumbnailsAsync();
    }

    private async Task SavePendingThumbnailsAsync()
    {
        await Task.Delay(ThumbnailSaveDebounce);
        var pending = _pendingThumbnailSaves.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        _pendingThumbnailSaves.Clear();
        _pendingThumbnailSaveTask = null;
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await _workspaceService.UpdateItemThumbnailsAsync(pending);
        }
        catch
        {
            // サムネイル保存失敗は次回ホバー更新へ任せます。
        }
    }

    private static bool HasCurrentShellMediaThumbnail(
        WorkspaceItemViewModel item,
        ShellItem shellItem,
        DateTimeOffset now)
    {
        if (!item.HasThumbnail
            || item.Item.ThumbnailUpdatedAt is not { } thumbnailUpdatedAt
            || now - thumbnailUpdatedAt >= ThumbnailRefreshInterval)
        {
            return false;
        }

        try
        {
            var fileUpdatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(shellItem.TargetPath), TimeSpan.Zero);
            return thumbnailUpdatedAt.ToUniversalTime() >= fileUpdatedAt;
        }
        catch
        {
            return false;
        }
    }

    public Workspace? CurrentWorkspace
    {
        get => _currentWorkspace;
        private set
        {
            if (_currentWorkspace == value)
            {
                return;
            }

            _currentWorkspace = value;
            SetLastArrangePositions(null);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentWorkspaceTitle));
        }
    }

    public string CurrentWorkspaceTitle => CurrentWorkspace is null
        ? "Workspace"
        : $"Workspace {CurrentWorkspace.Index}: {CurrentWorkspace.Name}";

    public Guid? CurrentFolderId
    {
        get => _currentFolderId;
        private set
        {
            if (_currentFolderId == value)
            {
                return;
            }

            _currentFolderId = value;
            SetLastArrangePositions(null);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInsideFolder));
            OnPropertyChanged(nameof(CurrentLocationTitle));
            OnPropertyChanged(nameof(CurrentLocationSummary));
        }
    }

    public bool IsInsideFolder => CurrentFolderId is not null;

    public string CurrentLocationTitle
    {
        get
        {
            if (CurrentFolderId is null)
            {
                return "Workspace 直下";
            }

            var folder = _allItems.FirstOrDefault(x => x.Id == CurrentFolderId);
            return folder is null
                ? "Folder"
                : $"Folder: {folder.Title}";
        }
    }

    public string CurrentLocationSummary => CurrentFolderId is null
        ? $"{Items.Count} items"
        : $"{Items.Count} items in folder";

    public WorkspaceItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetSelectedItems(value is null ? [] : [value], value);
    }

    public IReadOnlyList<WorkspaceItemViewModel> SelectedItems => _allItems
        .Where(static item => item.IsSelected)
        .ToList();

    public void SetSelectedItems(
        IEnumerable<WorkspaceItemViewModel> selectedItems,
        WorkspaceItemViewModel? primaryItem = null)
    {
        var selection = selectedItems.ToHashSet();
        selection.IntersectWith(_allItems);

        var nextPrimaryItem = primaryItem is not null && selection.Contains(primaryItem)
            ? primaryItem
            : _selectedItem is not null && selection.Contains(_selectedItem)
                ? _selectedItem
                : Items.FirstOrDefault(selection.Contains)
                    ?? _allItems.FirstOrDefault(selection.Contains);
        var selectionChanged = false;
        foreach (var item in _allItems)
        {
            var isSelected = selection.Contains(item);
            selectionChanged |= item.IsSelected != isSelected;
            item.IsSelected = isSelected;
        }

        var primaryChanged = !ReferenceEquals(_selectedItem, nextPrimaryItem);
        _selectedItem = nextPrimaryItem;
        if (primaryChanged)
        {
            OnPropertyChanged(nameof(SelectedItem));
        }

        if (selectionChanged || primaryChanged)
        {
            OnPropertyChanged(nameof(SelectedItems));
            OnPropertyChanged(nameof(ArrangeSelectionCount));
            OnPropertyChanged(nameof(CanArrangeSelection));
            OnPropertyChanged(nameof(ArrangeMenuText));
            OnPropertyChanged(nameof(ArrangeSelectionMenuText));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    public bool IsWorkspaceSaving
    {
        get => _isWorkspaceSaving;
        private set
        {
            if (_isWorkspaceSaving == value)
            {
                return;
            }

            _isWorkspaceSaving = value;
            OnPropertyChanged();
        }
    }

    public bool HasWorkspaceError
    {
        get => _hasWorkspaceError;
        private set
        {
            if (_hasWorkspaceError == value)
            {
                return;
            }

            _hasWorkspaceError = value;
            OnPropertyChanged();
        }
    }

    public string WorkspaceErrorText
    {
        get => _workspaceErrorText;
        private set
        {
            if (_workspaceErrorText == value)
            {
                return;
            }

            _workspaceErrorText = value;
            OnPropertyChanged();
        }
    }

    public bool HasItems => Items.Count > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool IsHyperSearchVisible
    {
        get => _isHyperSearchVisible;
        private set
        {
            if (_isHyperSearchVisible == value)
            {
                return;
            }

            _isHyperSearchVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHyperSearchHidden));
            OnPropertyChanged(nameof(IsWorkspaceSidePanelVisible));
        }
    }

    public bool IsHyperSearchHidden => !IsHyperSearchVisible;

    public bool IsRecentItemsVisible
    {
        get => _isRecentItemsVisible;
        private set
        {
            if (_isRecentItemsVisible == value)
            {
                return;
            }

            _isRecentItemsVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWorkspaceSidePanelVisible));
        }
    }

    public bool IsWorkspaceSidePanelVisible => IsHyperSearchVisible || IsRecentItemsVisible;

    public RecentItemCategory? RecentFilter
    {
        get => _recentFilter;
        private set
        {
            if (_recentFilter == value)
            {
                return;
            }

            _recentFilter = value;
            OnPropertyChanged();
            RefreshRecentItems();
        }
    }

    public string RecentItemSummary => RecentItems.Count == 0
        ? "項目なし"
        : $"{RecentItems.Count} 件";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var normalized = value ?? string.Empty;
            if (_searchQuery == normalized)
            {
                return;
            }

            _searchQuery = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SearchResultSummary));
        }
    }

    public string SearchResultSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                return "検索待機";
            }

            return SearchResults.Count == 0
                ? "一致なし"
                : $"{SearchResults.Count} 件";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsLoading = true;
        ClearWorkspaceError();
        StatusText = "Workspace を読み込み中";

        try
        {
            var workspaces = await _workspaceService.GetWorkspacesAsync(cancellationToken);
            Workspaces.Clear();
            foreach (var workspace in workspaces.OrderBy(static x => x.Index))
            {
                Workspaces.Add(new WorkspaceSlotViewModel(workspace));
            }

            var target = Workspaces.FirstOrDefault(x => x.Id == _initialWorkspaceId)
                ?? Workspaces.FirstOrDefault();

            if (target is not null)
            {
                await LoadWorkspaceAsync(target.Id, cancellationToken, _initialFolderId, _initialSelectedItemId);
            }
            else
            {
                StatusText = "Workspace がありません";
            }
        }
        catch (Exception ex)
        {
            SetWorkspaceError("Workspace を読み込めません: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public bool QueueWorkspaceWheelSwitch(int offset)
    {
        if (offset == 0 || Workspaces.Count < 2)
        {
            return false;
        }

        var currentSlot = _pendingWheelWorkspace
            ?? Workspaces.FirstOrDefault(workspace => workspace.Id == CurrentWorkspace?.Id)
            ?? Workspaces[0];
        var currentIndex = Workspaces.IndexOf(currentSlot);
        var targetIndex = (currentIndex + offset + Workspaces.Count) % Workspaces.Count;
        _pendingWheelWorkspace = Workspaces[targetIndex];
        _lastWorkspaceWheelInputAt = DateTimeOffset.UtcNow;
        _workspaceWheelSwitchVersion++;
        _workspaceSwitchRequestVersion++;
        SetWorkspaceSelection(_pendingWheelWorkspace.Id);
        if (_workspaceRuntimeCache.TryGetValue(_pendingWheelWorkspace.Id, out var runtime))
        {
            var target = _pendingWheelWorkspace;
            _pendingWheelWorkspace = null;
            ActivateWorkspaceRuntime(target, runtime);
            return true;
        }

        if (!_isProcessingWorkspaceWheelSwitch)
        {
            _ = ProcessQueuedWorkspaceWheelSwitchAsync();
        }

        return true;
    }

    private async Task ProcessQueuedWorkspaceWheelSwitchAsync()
    {
        if (_isProcessingWorkspaceWheelSwitch)
        {
            return;
        }

        _isProcessingWorkspaceWheelSwitch = true;
        try
        {
            while (_pendingWheelWorkspace is { } target)
            {
                var observedVersion = _workspaceWheelSwitchVersion;
                var elapsed = DateTimeOffset.UtcNow - _lastWorkspaceWheelInputAt;
                var remainingDelay = WorkspaceWheelSwitchDelay - elapsed;
                if (remainingDelay > TimeSpan.Zero)
                {
                    await Task.Delay(remainingDelay);
                }

                if (observedVersion != _workspaceWheelSwitchVersion)
                {
                    continue;
                }

                if (CurrentWorkspace?.Id != target.Id)
                {
                    await LoadWorkspaceAsync(target.Id);
                }

                if (observedVersion == _workspaceWheelSwitchVersion
                    && _pendingWheelWorkspace?.Id == target.Id)
                {
                    _pendingWheelWorkspace = null;
                    break;
                }
            }
        }
        finally
        {
            _isProcessingWorkspaceWheelSwitch = false;
            if (_pendingWheelWorkspace is not null)
            {
                _ = ProcessQueuedWorkspaceWheelSwitchAsync();
            }
            else if (CurrentWorkspace is { } currentWorkspace)
            {
                SetWorkspaceSelection(currentWorkspace.Id);
            }
        }
    }

    private void CancelQueuedWorkspaceWheelSwitch()
    {
        _pendingWheelWorkspace = null;
        _workspaceWheelSwitchVersion++;
        if (CurrentWorkspace is { } currentWorkspace)
        {
            SetWorkspaceSelection(currentWorkspace.Id);
        }
    }

    public async Task LoadWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default,
        Guid? folderId = null,
        Guid? selectedItemId = null,
        bool forceReload = false)
    {
        var slot = Workspaces.FirstOrDefault(x => x.Id == workspaceId);
        if (slot is null)
        {
            return;
        }

        var requestVersion = ++_workspaceSwitchRequestVersion;
        if (!forceReload
            && _workspaceRuntimeCache.TryGetValue(workspaceId, out var cachedRuntime))
        {
            ActivateWorkspaceRuntime(slot, cachedRuntime, folderId, selectedItemId);
            return;
        }

        await _workspaceLoadGate.WaitAsync(cancellationToken);
        IsLoading = true;
        ClearWorkspaceError();
        StatusText = $"{slot.Name} を読み込み中";

        try
        {
            if (!forceReload
                && _workspaceRuntimeCache.TryGetValue(workspaceId, out cachedRuntime))
            {
                if (requestVersion == _workspaceSwitchRequestVersion)
                {
                    ActivateWorkspaceRuntime(slot, cachedRuntime, folderId, selectedItemId);
                }

                return;
            }

            var runtime = await ReadWorkspaceRuntimeAsync(workspaceId, cancellationToken);
            _workspaceRuntimeCache[workspaceId] = runtime;
            if (requestVersion != _workspaceSwitchRequestVersion)
            {
                return;
            }

            ActivateWorkspaceRuntime(slot, runtime, folderId, selectedItemId);
            StatusText = $"{slot.Name}: {Items.Count} 件";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (CurrentWorkspace is { } currentWorkspace)
            {
                SetWorkspaceSelection(currentWorkspace.Id);
            }
        }
        catch (Exception ex)
        {
            SetWorkspaceError("Workspace の読み込みに失敗しました: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
            _workspaceLoadGate.Release();
        }
    }

    public void StartWorkspacePreload()
    {
        if (_isDisposed
            || !_initialized
            || _workspacePreloadTask is not null)
        {
            return;
        }

        _workspacePreloadCancellation = new CancellationTokenSource();
        _workspacePreloadTask = PreloadWorkspaceRuntimesAsync(_workspacePreloadCancellation.Token);
    }

    public void Dispose()
    {
        lock (_shellIconTasks)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _workspacePreloadCancellation?.Cancel();
        _workspacePreloadCancellation?.Dispose();
        _workspacePreloadCancellation = null;
        Items.CollectionChanged -= Items_CollectionChanged;
        SearchResults.CollectionChanged -= SearchResults_CollectionChanged;
        if (_recentItemsService is not null)
        {
            _recentItemsService.ItemsChanged -= RecentItemsService_ItemsChanged;
            _recentItemsService = null;
        }
        _workspaceRuntimeCache.Clear();
        _currentWorkspaceRuntime = null;
        _lastShellOperation = null;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] pending;
        lock (_shellIconTasks)
        {
            pending = _shellIconTasks.ToArray();
        }

        // アイコン取得後のDB保存まで所有し、呼出し元がDBを片付ける前に接続の解放を待つ。
        await Task.WhenAll(pending);
    }

    private Task TrackShellIconWork(Func<Task> start)
    {
        lock (_shellIconTasks)
        {
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            var task = start();
            if (!task.IsCompleted)
            {
                _shellIconTasks.Add(task);
                _ = RemoveCompletedShellIconWorkAsync(task);
            }

            return task;
        }
    }

    private async Task RemoveCompletedShellIconWorkAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            lock (_shellIconTasks)
            {
                _shellIconTasks.Remove(task);
            }
        }
    }

    public void ConfigureRecentItems(RecentItemsService service, bool recordActions)
    {
        if (ReferenceEquals(_recentItemsService, service))
        {
            _recordRecentItems = recordActions;
            RefreshRecentItems();
            return;
        }

        if (_recentItemsService is not null)
        {
            _recentItemsService.ItemsChanged -= RecentItemsService_ItemsChanged;
        }

        _recentItemsService = service;
        _recordRecentItems = recordActions;
        _recentItemsService.ItemsChanged += RecentItemsService_ItemsChanged;
        RefreshRecentItems();
    }

    public void ShowRecentItems()
    {
        IsHyperSearchVisible = false;
        IsRecentItemsVisible = true;
        RefreshRecentItems();
        StatusText = "最近使った項目を表示しました";
    }

    public void CloseSidePanels()
    {
        IsHyperSearchVisible = false;
        IsRecentItemsVisible = false;
    }

    public void RecordRecentItem(RecentItem item)
    {
        if (_recordRecentItems)
        {
            _recentItemsService?.Record(item);
        }
    }

    public Task ShowSearchResultAsync(WorkspaceSearchResultViewModel result)
    {
        return GoToSearchResultAsync(result);
    }

    public async Task ShowRecentWorkspaceItemAsync(RecentItem recentItem)
    {
        if (recentItem.WorkspaceId is not { } workspaceId
            || recentItem.WorkspaceItemId is not { } itemId)
        {
            return;
        }

        await LoadWorkspaceAsync(workspaceId);
        var workspaceItem = _allItems.FirstOrDefault(item => item.Id == itemId);
        if (workspaceItem?.Item.ParentFolderId is { } parentFolderId)
        {
            var parentFolder = _allItems.FirstOrDefault(item => item.Id == parentFolderId)?.Item as FolderItem;
            if (parentFolder is not null)
            {
                _windows.OpenFolder(parentFolder, itemId);
                StatusText = $"最近使った項目のフォルダを開きました: {recentItem.Title}";
                return;
            }
        }

        var visibleItem = Items.FirstOrDefault(item => item.Id == itemId);
        if (visibleItem is null)
        {
            StatusText = "最近使ったWorkspace項目を表示できません";
            return;
        }

        SelectedItem = visibleItem;
        StatusText = $"最近使った項目へ移動しました: {recentItem.Title}";
    }

    private async Task PreloadWorkspaceRuntimesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WorkspacePreloadDelay, cancellationToken);
            foreach (var workspace in Workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_workspaceRuntimeCache.ContainsKey(workspace.Id))
                {
                    continue;
                }

                var runtime = await ReadWorkspaceRuntimeAsync(workspace.Id, cancellationToken);
                if (_workspaceRuntimeCache.TryAdd(workspace.Id, runtime))
                {
                    WorkspaceItemsPreloaded?.Invoke(runtime.GetVisibleItems(null));
                }

                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // プリロード失敗時は、対象Workspaceを開いた時の通常読み込みへフォールバックします。
        }
    }

    private async Task<WorkspaceRuntimeState> ReadWorkspaceRuntimeAsync(
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var items = await _workspaceService.GetWorkspaceItemsAsync(workspaceId, cancellationToken);
        return await Task.Run(
            () => CreateWorkspaceRuntime(workspaceId, items),
            cancellationToken);
    }

    private static WorkspaceRuntimeState CreateWorkspaceRuntime(
        Guid workspaceId,
        IReadOnlyList<WorkspaceItem> items)
    {
        var viewModels = items
            .Select(static item => new WorkspaceItemViewModel(item))
            .ToList();
        RefreshFolderChildCounts(viewModels);
        return new WorkspaceRuntimeState(workspaceId, viewModels);
    }

    private void ActivateWorkspaceRuntime(
        WorkspaceSlotViewModel slot,
        WorkspaceRuntimeState runtime,
        Guid? folderId = null,
        Guid? selectedItemId = null)
    {
        SelectedItem = null;
        _currentWorkspaceRuntime = runtime;
        _allItems = runtime.AllItems;
        _shellIconAttemptedItemIds.Clear();
        CurrentWorkspace = slot.Workspace;
        SetWorkspaceSelection(slot.Id);

        var targetFolderId = IsKnownFolder(folderId) ? folderId : null;
        CurrentFolderId = targetFolderId;
        SetItemsCollection(runtime.GetVisibleItems(targetFolderId));
        SelectVisibleItem(selectedItemId);

        IsLoading = false;
        ClearWorkspaceError();
        var activationVersion = ++_workspaceActivationVersion;
        _ = StartWorkspaceBackfillsAfterStableSelectionAsync(slot.Id, activationVersion);
        StatusText = $"{slot.Name}: {Items.Count} 件";
    }

    private void SetItemsCollection(ObservableCollection<WorkspaceItemViewModel> items)
    {
        if (ReferenceEquals(Items, items))
        {
            Items_CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }

        Items.CollectionChanged -= Items_CollectionChanged;
        Items = items;
        Items.CollectionChanged += Items_CollectionChanged;
        OnPropertyChanged(nameof(Items));
        Items_CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private async Task StartWorkspaceBackfillsAfterStableSelectionAsync(
        Guid workspaceId,
        int activationVersion)
    {
        await Task.Delay(WorkspaceBackfillDelay);
        if (_isDisposed
            || activationVersion != _workspaceActivationVersion
            || CurrentWorkspace?.Id != workspaceId)
        {
            return;
        }

        _windows.SaveLastWorkspaceId(workspaceId);
        TabShelf.RefreshTabs();
        _ = BackfillBookmarkFaviconsAsync();
        _ = BackfillShellMediaThumbnailsAsync();
        _ = BackfillYouTubeMiniPlayerThumbnailsAsync();
        _ = BackfillShellItemIconsAsync();
    }

    private void SetWorkspaceSelection(Guid workspaceId)
    {
        foreach (var workspace in Workspaces)
        {
            workspace.IsSelected = workspace.Id == workspaceId;
        }
    }

    private async Task BackfillBookmarkFaviconsAsync()
    {
        if (_isBackfillingFavicons)
        {
            return;
        }

        var targets = _allItems
            .Where(static x => x.Item is BookmarkItem
                && string.IsNullOrWhiteSpace(x.Item.FaviconPngBase64)
                && IsSupportedFaviconUrl(x.Item.Url))
            .Take(MaxFaviconBackfillCount)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _isBackfillingFavicons = true;
        try
        {
            foreach (var target in targets)
            {
                if (!string.IsNullOrWhiteSpace(target.Item.FaviconPngBase64))
                {
                    continue;
                }

                var faviconPngBase64 = await TryFetchFaviconAsync(target.Item.Url);
                if (string.IsNullOrWhiteSpace(faviconPngBase64))
                {
                    continue;
                }

                target.SetFaviconPngBase64(faviconPngBase64);
                if (!target.HasFavicon)
                {
                    target.SetFaviconPngBase64(string.Empty);
                    continue;
                }

                await _workspaceService.UpdateItemFaviconAsync(target.Id, faviconPngBase64);
            }
        }
        catch
        {
            // favicon 補完は補助処理なので、失敗しても Workspace の表示は維持します。
        }
        finally
        {
            _isBackfillingFavicons = false;
        }
    }

    private Task BackfillShellItemIconsAsync()
        => TrackShellIconWork(BackfillShellItemIconsCoreAsync);

    private async Task BackfillShellItemIconsCoreAsync()
    {
        if (_isBackfillingShellIcons)
        {
            return;
        }

        var targets = _allItems
            .Where(static item => item.Item is ShellItem && !item.HasFavicon)
            .Take(MaxShellIconBackfillCount)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _isBackfillingShellIcons = true;
        try
        {
            var pending = new Dictionary<Guid, string>();
            foreach (var target in targets)
            {
                if (target.Item is not ShellItem shellItem || target.HasFavicon)
                {
                    continue;
                }

                if (!_shellIconAttemptedItemIds.Add(target.Id))
                {
                    continue;
                }

                target.RefreshShellState();
                var iconPngBase64 = await _shellItemService.GetIconPngBase64Async(shellItem);
                if (_isDisposed)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(iconPngBase64))
                {
                    continue;
                }

                target.SetFaviconPngBase64(iconPngBase64);
                pending[target.Id] = iconPngBase64;
            }

            if (!_isDisposed)
            {
                await _workspaceService.UpdateItemFaviconsAsync(pending);
            }
        }
        catch
        {
            // Shell アイコンの遅延補完に失敗しても Workspace の表示を維持します。
        }
        finally
        {
            _isBackfillingShellIcons = false;
        }
    }

    private async Task BackfillShellMediaThumbnailsAsync()
    {
        if (_isBackfillingShellMediaThumbnails)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var targets = _allItems
            .Where(item => item.Item is ShellItem shellItem
                && WorkspaceShellItemService.IsMediaPath(shellItem.TargetPath)
                && !HasCurrentShellMediaThumbnail(item, shellItem, now))
            .Take(MaxShellMediaThumbnailBackfillCount)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _isBackfillingShellMediaThumbnails = true;
        try
        {
            foreach (var target in targets)
            {
                await EnsureItemThumbnailAsync(target);
            }
        }
        finally
        {
            _isBackfillingShellMediaThumbnails = false;
        }
    }

    private async Task BackfillYouTubeMiniPlayerThumbnailsAsync()
    {
        if (_isBackfillingYouTubeMiniPlayerThumbnails)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var targets = _allItems
            .Where(item => item.Item is YouTubeMiniPlayerSetItem
                && (!item.HasThumbnail
                    || CapturedTabSnapshot.IsMostlyBlackThumbnail(item.Thumbnail)
                    || item.Item.ThumbnailUpdatedAt is not { } updatedAt
                    || now - updatedAt >= ThumbnailRefreshInterval))
            .Take(MaxYouTubeMiniPlayerThumbnailBackfillCount)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _isBackfillingYouTubeMiniPlayerThumbnails = true;
        try
        {
            foreach (var target in targets)
            {
                await EnsureItemThumbnailAsync(target);
            }
        }
        finally
        {
            _isBackfillingYouTubeMiniPlayerThumbnails = false;
        }
    }

    private static async Task<string> TryFetchYouTubeMiniPlayerThumbnailAsync(
        YouTubeMiniPlayerSetItem playerSet)
    {
        var thumbnailUri = playerSet.Players
            .Select(static player => YouTubeUrlService.CreateThumbnailUri(player.Url))
            .FirstOrDefault(static uri => uri is not null)
            ?? YouTubeUrlService.CreateThumbnailUri(playerSet.Url);
        if (thumbnailUri is null)
        {
            return string.Empty;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, thumbnailUri);
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await YouTubeThumbnailHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            if (!response.IsSuccessStatusCode
                || !IsSupportedImageContentType(response.Content.Headers.ContentType?.MediaType)
                || response.Content.Headers.ContentLength is > MaxYouTubeThumbnailBytes)
            {
                return string.Empty;
            }

            var bytes = await ReadLimitedBytesAsync(
                response.Content,
                MaxYouTubeThumbnailBytes,
                cancellation.Token);
            var thumbnail = CreateYouTubeThumbnailImage(bytes);
            return CapturedTabSnapshot.CreateThumbnailPngBase64(thumbnail);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                return [];
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static ImageSource? CreateYouTubeThumbnailImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryFetchFaviconAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri)
            || !IsSupportedFaviconUri(pageUri))
        {
            return string.Empty;
        }

        foreach (var faviconUri in GetFaviconCandidateUris(pageUri))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, faviconUri);
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await FaviconHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || !IsSupportedImageContentType(response.Content.Headers.ContentType?.MediaType))
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > MaxFaviconBytes)
                {
                    continue;
                }

                return Convert.ToBase64String(bytes);
            }
            catch
            {
                // 次の候補に任せます。
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<Uri> GetFaviconCandidateUris(Uri pageUri)
    {
        var origin = new Uri(pageUri.GetLeftPart(UriPartial.Authority));
        return
        [
            new Uri(origin, "/favicon.ico"),
            new Uri(origin, "/favicon.png"),
            new Uri(origin, "/apple-touch-icon.png")
        ];
    }

    private static bool IsSupportedFaviconUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsSupportedFaviconUri(uri);
    }

    private static bool IsSupportedFaviconUri(Uri uri)
    {
        return uri.Scheme is "http" or "https"
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static bool IsSupportedImageContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateFaviconHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TabBrowser/1.0");
        return client;
    }

    private static HttpClient CreateYouTubeThumbnailHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TabBrowser/1.0");
        return client;
    }

    private async Task SwitchWorkspaceAsync(object? parameter)
    {
        if (parameter is WorkspaceSlotViewModel slot)
        {
            CancelQueuedWorkspaceWheelSwitch();
            if (CurrentWorkspace?.Id == slot.Id)
            {
                return;
            }

            await LoadWorkspaceAsync(slot.Id);
        }
    }

    private async Task RenameWorkspaceAsync(object? parameter)
    {
        if (parameter is not WorkspaceNameChangeRequest request
            || !Workspaces.Contains(request.Workspace))
        {
            return;
        }

        try
        {
            await _workspaceService.UpdateWorkspaceNameAsync(
                request.Workspace.Id,
                request.NewName);
            StatusText = $"Workspace {request.Workspace.Index} の名前を変更しました";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("Workspace名を変更できませんでした: " + ex.Message);
        }
    }

    public WorkspaceCardDragPayload? CreateCardDragPayload(
        IReadOnlyList<WorkspaceItemViewModel> requestedItems,
        WorkspaceCanvasPoint grabPoint)
    {
        if (CurrentWorkspace is null)
        {
            return null;
        }

        var roots = requestedItems
            .Where(_allItems.Contains)
            .Select(static item => item.Item)
            .ToList();
        return WorkspaceCardDragPayload.Create(
            CurrentWorkspace.Id,
            _allItems.Select(static item => item.Item).ToList(),
            roots,
            grabPoint);
    }

    private async Task MoveItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel item)
        {
            return;
        }

        try
        {
            var point = FindAvailablePoint(
                new WorkspaceCanvasPoint(item.X, item.Y),
                item.Item.ParentFolderId,
                item);
            item.X = point.X;
            item.Y = point.Y;
            IsWorkspaceSaving = true;
            StatusText = $"保存中: {item.Title}";
            await _workspaceService.UpdateItemPositionAsync(item.Id, item.X, item.Y);
            StatusText = $"移動しました: {item.Title}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("位置を保存できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task MoveItemsAsync(object? parameter)
    {
        if (parameter is not IReadOnlyList<WorkspaceItemViewModel> requestedItems)
        {
            return;
        }

        var items = requestedItems
            .Where(_allItems.Contains)
            .Distinct()
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count == 1)
        {
            await MoveItemAsync(items[0]);
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = $"{items.Count} 件の位置を保存中";
            await _workspaceService.UpdateItemPositionsAsync(items
                .Select(static item => new WorkspaceItemPosition(item.Id, item.X, item.Y))
                .ToList());
            StatusText = $"{items.Count} 件のカードを移動しました";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("位置を一括保存できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task MoveDraggedItemsAsync(object? parameter)
    {
        if (parameter is not WorkspaceCardDropRequest request)
        {
            return;
        }

        var payload = request.Payload;
        if (Workspaces.All(workspace => workspace.Id != request.TargetWorkspaceId))
        {
            payload.CompleteDrop(moved: false);
            return;
        }

        var targetFolderId = request.TargetFolderId;
        if (targetFolderId is { } requestedFolderId
            && payload.Items.Any(item => item.Id == requestedFolderId))
        {
            payload.CompleteDrop(moved: false);
            StatusText = "移動するフォルダー自身の中には配置できません";
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = payload.Roots.Count == 1
                ? $"移動中: {payload.Roots[0].Item.Title}"
                : $"{payload.Roots.Count} 件のカードを移動中";

            var targetItems = await _workspaceService.GetWorkspaceItemsAsync(request.TargetWorkspaceId);
            if (targetFolderId is { } folderId
                && !targetItems.Any(item => item.Id == folderId && item is FolderItem))
            {
                throw new InvalidOperationException("移動先フォルダーが見つかりません。");
            }

            var folderDefaultPoint = request.UseFolderDefaultPosition && targetFolderId is { } targetId
                ? GetNextCanvasPoint(targetItems, targetId)
                : null;
            var anchor = request.ResolveAnchor(folderDefaultPoint);
            var moves = WorkspaceCardPlacement.CreateMoves(
                payload,
                request.TargetWorkspaceId,
                targetFolderId,
                anchor,
                targetItems);

            await _workspaceService.MoveItemsToWorkspaceAsync(moves);
            payload.CompleteDrop(moved: true);

            if (CurrentWorkspace?.Id == request.TargetWorkspaceId)
            {
                SelectVisibleItem(payload.Roots[0].Item.Id);
            }

            StatusText = payload.Roots.Count == 1
                ? $"移動しました: {payload.Roots[0].Item.Title}"
                : $"{payload.Roots.Count} 件のカードを移動しました";
        }
        catch (Exception ex)
        {
            payload.CompleteDrop(moved: false);
            SetWorkspaceError("カードを移動できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task DeleteDraggedItemsAsync(object? parameter)
    {
        if (parameter is not WorkspaceCardDragPayload payload)
        {
            return;
        }

        var rootIds = payload.Roots
            .Select(static root => root.Item.Id)
            .Distinct()
            .ToList();
        if (rootIds.Count == 0)
        {
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = rootIds.Count == 1
                ? $"削除中: {payload.Roots[0].Item.Title}"
                : $"{rootIds.Count} 件を削除中";
            await _workspaceService.DeleteItemsAsync(rootIds);

            if (CurrentWorkspace?.Id == payload.SourceWorkspaceId)
            {
                await LoadWorkspaceAsync(
                    payload.SourceWorkspaceId,
                    folderId: CurrentFolderId,
                    forceReload: true);
            }

            StatusText = rootIds.Count == 1
                ? $"Workspace から削除しました: {payload.Roots[0].Item.Title}"
                : $"Workspace から {rootIds.Count} 件のカードを削除しました";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("Workspace からカードを削除できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task OpenItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel item)
        {
            return;
        }

        SelectedItem = item;

        if (item.Item is FolderItem)
        {
            _windows.OpenFolder((FolderItem)item.Item, null);
            RecordRecentWorkspaceItem(item.Item);
            StatusText = $"フォルダを開きました: {item.Title}";
            return;
        }

        if (WorkspaceTabSavePlanner.CreateSavedSingleTab(item.Item) is { } savedTab)
        {
            var openedCount = await _tabs.OpenSessionTabsAsync([savedTab]);
            if (_tabs.GetDeferredRestoreMessage?.Invoke() is { } deferred)
            {
                StatusText = deferred;
                return;
            }

            if (openedCount == 0)
            {
                StatusText = "保存タブを開けませんでした";
                return;
            }

            RecordRecentWorkspaceItem(item.Item);
            StatusText = $"保存タブを開きました: {item.Title}";
            return;
        }

        if (item.Item is SessionGroupItem sessionGroup)
        {
            if (sessionGroup.Tabs.Count == 0)
            {
                StatusText = "SessionGroup にタブがありません";
                return;
            }

            var openedCount = await _tabs.OpenSessionTabsAsync(sessionGroup.Tabs);
            if (_tabs.GetDeferredRestoreMessage?.Invoke() is { } deferred)
            {
                StatusText = deferred;
                return;
            }
            if (openedCount == 0)
            {
                StatusText = "SessionGroup のタブを開けませんでした";
                return;
            }
            RecordRecentWorkspaceItem(item.Item);
            StatusText = $"SessionGroup を開きました: {item.Title}";
            return;
        }

        if (item.Item is YouTubeMiniPlayerSetItem playerSet)
        {
            if (playerSet.Players.Count == 0)
            {
                StatusText = "保存されたYouTubeミニプレイヤーがありません";
                return;
            }

            await _miniPlayers.OpenSetAsync(playerSet);
            RecordRecentWorkspaceItem(item.Item);
            StatusText = $"YouTubeミニプレイヤーを復元しました: {playerSet.Players.Count}件";
            return;
        }

        if (item.Item is ShellItem shellItem)
        {
            try
            {
                OpenShellItem(shellItem);
                item.RefreshShellState();
                RecordRecentWorkspaceItem(item.Item);
                StatusText = $"開きました: {item.Title}";
            }
            catch (Exception ex)
            {
                item.RefreshShellState();
                SetWorkspaceError("ファイルを開けません: " + ex.Message);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            StatusText = "開ける URL がありません";
            return;
        }

        await _tabs.OpenUrlAsync(item.Url);
        RecordRecentWorkspaceItem(item.Item);
        StatusText = $"開きました: {item.Title}";
    }

    private async Task OpenItemsAsync(object? parameter)
    {
        if (parameter is not IReadOnlyList<WorkspaceItemViewModel> requestedItems)
        {
            return;
        }

        var items = requestedItems
            .Where(_allItems.Contains)
            .Distinct()
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var openedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        string? deferredMessage = null;
        foreach (var item in items)
        {
            try
            {
                if (item.Item is FolderItem folder)
                {
                    _windows.OpenFolder(folder, null);
                    openedCount++;
                    continue;
                }

                if (WorkspaceTabSavePlanner.CreateSavedSingleTab(item.Item) is { } savedTab)
                {
                    var sessionOpenedCount = await _tabs.OpenSessionTabsAsync([savedTab]);
                    if (_tabs.GetDeferredRestoreMessage?.Invoke() is { } deferred)
                    {
                        deferredMessage = deferred;
                        skippedCount++;
                    }
                    else if (sessionOpenedCount == 0)
                    {
                        failedCount++;
                    }
                    else
                    {
                        openedCount++;
                    }
                    continue;
                }

                if (item.Item is SessionGroupItem sessionGroup)
                {
                    if (sessionGroup.Tabs.Count == 0)
                    {
                        skippedCount++;
                        continue;
                    }

                    var sessionOpenedCount = await _tabs.OpenSessionTabsAsync(sessionGroup.Tabs);
                    if (_tabs.GetDeferredRestoreMessage?.Invoke() is { } deferred)
                    {
                        deferredMessage = deferred;
                        skippedCount++;
                    }
                    else if (sessionOpenedCount == 0)
                    {
                        failedCount++;
                    }
                    else
                    {
                        openedCount++;
                    }
                    continue;
                }

                if (item.Item is YouTubeMiniPlayerSetItem playerSet)
                {
                    if (playerSet.Players.Count == 0)
                    {
                        skippedCount++;
                        continue;
                    }

                    await _miniPlayers.OpenSetAsync(playerSet);
                    openedCount++;
                    continue;
                }

                if (item.Item is ShellItem shellItem)
                {
                    OpenShellItem(shellItem);
                    item.RefreshShellState();
                    openedCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Url))
                {
                    skippedCount++;
                    continue;
                }

                await _tabs.OpenUrlAsync(item.Url);
                openedCount++;
            }
            catch
            {
                failedCount++;
            }
        }

        var statusParts = new List<string> { $"{openedCount} 件のカードを開きました" };
        if (skippedCount > 0)
        {
            statusParts.Add($"対象外 {skippedCount} 件");
        }

        if (failedCount > 0)
        {
            statusParts.Add($"失敗 {failedCount} 件");
        }

        if (deferredMessage is not null) statusParts.Add(deferredMessage);
        StatusText = string.Join(" / ", statusParts);
    }

    private void OpenShellItem(ShellItem shellItem)
    {
        if (shellItem is { Kind: ShellItemKind.Directory, OpenInFolderTab: true })
        {
            if (!Directory.Exists(shellItem.TargetPath))
            {
                throw new DirectoryNotFoundException($"対象が見つかりません: {shellItem.TargetPath}");
            }

            _tabs.OpenShellFolderInTab(shellItem.TargetPath);
            return;
        }

        _shellItemService.Open(shellItem);
    }

    private async Task OpenItemsInNewWindowAsync(object? parameter)
    {
        if (parameter is not IReadOnlyList<WorkspaceItemViewModel> requestedItems)
        {
            return;
        }

        var tabs = requestedItems
            .Where(_allItems.Contains)
            .Distinct()
            .SelectMany(static item => WorkspaceTabSavePlanner.CreateSavedSingleTab(item.Item) is { } savedTab
                ? new List<SessionTab> { savedTab } : item.Item switch
                {
                    SessionGroupItem sessionGroup => sessionGroup.Tabs,
                    YouTubeMiniPlayerSetItem => [],
                    _ when !string.IsNullOrWhiteSpace(item.Url) =>
                    [new SessionTab { Url = item.Url, Title = item.Title }],
                    _ => []
                })
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.Url))
            .ToList();

        if (tabs.Count == 0)
        {
            StatusText = "新しいウィンドウで開けるカードがありません";
            return;
        }

        await _windows.OpenSessionTabsAsync(tabs);
        StatusText = _windows.GetDeferredRestoreMessage?.Invoke() ?? $"新しいウィンドウで {tabs.Count} タブを開きました";
    }

    private async Task DuplicateItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel source || !_allItems.Contains(source))
        {
            return;
        }

        var point = FindAvailablePoint(
            new WorkspaceCanvasPoint(source.X + 104, source.Y),
            source.Item.ParentFolderId);
        try
        {
            IsWorkspaceSaving = true;
            var duplicate = await _workspaceService.DuplicateWorkspaceItemAsync(
                source.Item,
                point.X,
                point.Y);
            AddSavedItemToCanvas(duplicate);
            StatusText = $"カードをコピーしました: {source.Title}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("カードをコピーできません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task RenameItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceRenameRequest request)
        {
            return;
        }

        var title = request.NewTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText = "名前を入力してください";
            return;
        }

        try
        {
            await _workspaceService.RenameItemAsync(request.Item.Id, title);
            request.Item.Title = title;
            StatusText = $"名前を変更しました: {title}";
        }
        catch (Exception ex)
        {
            StatusText = "名前を変更できません: " + ex.Message;
        }
    }

    private async Task EditNoteAsync(object? parameter)
    {
        if (parameter is not WorkspaceNoteEditRequest request)
        {
            return;
        }

        await TryEditNoteAsync(request);
    }

    internal async Task<bool> TryEditNoteAsync(WorkspaceNoteEditRequest request, Action? targetMissing = null)
    {
        if (request.Item.Item is not NoteItem)
        {
            StatusText = "メモだけ編集できます";
            return false;
        }

        var content = request.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            StatusText = "メモを入力してください";
            return false;
        }

        var internalTitle = CreateInternalNoteTitle(content);

        try
        {
            await _workspaceService.UpdateNoteAsync(
                request.Item.Id,
                internalTitle,
                content,
                request.FontSize);
        }
        catch (WorkspaceNoteMissingException ex)
        {
            targetMissing?.Invoke();
            StatusText = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            StatusText = "メモを更新できません: " + ex.Message;
            return false;
        }

        request.Item.Title = internalTitle;
        request.Item.Note = content;
        request.Item.NoteFontSize = request.FontSize;
        StatusText = "メモを更新しました";
        try
        {
            RecordRecentWorkspaceItem(request.Item.Item);
        }
        catch (Exception ex)
        {
            StatusText = "メモは保存しましたが、最近使った項目を更新できません: " + ex.Message;
        }

        return true;
    }

    internal async Task<bool> TrySaveNoteAsNewAsync(WorkspaceNoteEditRequest request)
    {
        try
        {
            await _workspaceService.AddNoteAsync(request.Item.Item.WorkspaceId,
                CreateInternalNoteTitle(request.Content), request.Content, 32, 32, fontSize: request.FontSize);
        }
        catch (Exception ex)
        {
            StatusText = "新しいメモを保存できません: " + ex.Message;
            return false;
        }

        StatusText = "元のWorkspace直下に新しいメモを保存しました";
        return true;
    }

    internal WorkspaceService PersistenceService => _workspaceService;

    private Task DeleteItemAsync(object? parameter)
    {
        return parameter is WorkspaceItemViewModel item
            ? DeleteItemsCoreAsync([item])
            : Task.CompletedTask;
    }

    private Task DeleteItemsAsync(object? parameter)
    {
        return parameter is IReadOnlyList<WorkspaceItemViewModel> items
            ? DeleteItemsCoreAsync(items)
            : Task.CompletedTask;
    }

    private async Task DeleteItemsCoreAsync(IReadOnlyList<WorkspaceItemViewModel> requestedItems)
    {
        var items = requestedItems
            .Where(_allItems.Contains)
            .Distinct()
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = items.Count == 1
                ? $"削除中: {items[0].Title}"
                : $"{items.Count} 件を削除中";
            var deletedIds = (await _workspaceService.DeleteItemsAsync(items.Select(static item => item.Id).ToList())).ToHashSet();

            _allItems.RemoveAll(item => deletedIds.Contains(item.Id));
            RefreshFolderChildCounts();
            ApplyVisibleItems();
            StatusText = deletedIds.Count == 0 ? "対象は既に削除されています"
                : $"{deletedIds.Count} 件をごみ箱へ移動しました。「最近削除した項目」から復元できます";
        }
        catch (Exception ex)
        {
            SetWorkspaceError(items.Count == 1
                ? "削除できません: " + ex.Message
                : "一括削除できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task ArrangeItemsAsync(object? parameter)
    {
        if (parameter is not WorkspaceArrangeRequest request)
        {
            return;
        }

        var targets = request.Scope == WorkspaceArrangeScope.Selection
            ? SelectedItems.Where(Items.Contains).Distinct().ToList()
            : Items.ToList();
        if (request.Scope == WorkspaceArrangeScope.Selection && targets.Count < 2)
        {
            StatusText = "選択カードの整列には2件以上の選択が必要です";
            return;
        }

        if (targets.Count == 0)
        {
            StatusText = "整列するカードがありません";
            return;
        }

        var targetCards = targets.Select(ToArrangementCard).ToList();
        var result = request.Scope == WorkspaceArrangeScope.Selection
            ? _arrangementService.ArrangeSelection(
                targetCards,
                Items.Except(targets).Select(ToArrangementCard).ToList(),
                request.Mode)
            : _arrangementService.ArrangeAll(
                targetCards,
                new WorkspaceArrangementBounds(
                    request.VisibleArea.X,
                    request.VisibleArea.Y,
                    request.VisibleArea.Width,
                    request.VisibleArea.Height),
                request.Mode);
        if (!result.Succeeded)
        {
            StatusText = result.ErrorMessage;
            return;
        }

        var placements = result.Placements.ToDictionary(static placement => placement.ItemId);
        var positions = targets
            .Where(item => placements.ContainsKey(item.Id))
            .Select(item =>
            {
                var placement = placements[item.Id];
                return new WorkspaceItemPosition(item.Id, placement.X, placement.Y);
            })
            .ToList();
        var initialPositions = targets
            .Select(static item => new WorkspaceItemPosition(item.Id, item.X, item.Y))
            .ToList();

        try
        {
            IsWorkspaceSaving = true;
            StatusText = "カードを整列中";
            _isApplyingArrangement = true;
            await _workspaceService.UpdateItemPositionsAsync(positions);
            ApplyPositions(positions);
            CaptureInitialArrangePositions(initialPositions);
            var targetLabel = request.Scope == WorkspaceArrangeScope.Selection
                ? $"選択 {targets.Count}件"
                : $"全 {targets.Count}件";
            StatusText = $"{targetLabel}を整列しました: {GetArrangeModeLabel(request.Mode)}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("カードを整列できません: " + ex.Message);
        }
        finally
        {
            _isApplyingArrangement = false;
            IsWorkspaceSaving = false;
        }
    }

    private async Task RestoreLastArrangeAsync(object? parameter)
    {
        if (_lastArrangePositions is not { Count: > 0 } positions)
        {
            StatusText = "戻せる整列履歴がありません";
            return;
        }

        try
        {
            var restorePositions = positions
                .Where(position => _allItems.Any(item => item.Id == position.ItemId))
                .ToList();
            IsWorkspaceSaving = true;
            StatusText = "整列前の位置へ戻しています";
            _isApplyingArrangement = true;
            await _workspaceService.UpdateItemPositionsAsync(restorePositions);
            ApplyPositions(restorePositions);
            SetLastArrangePositions(null);
            StatusText = "整列前の位置へ戻しました";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("整列前の位置へ戻せません: " + ex.Message);
        }
        finally
        {
            _isApplyingArrangement = false;
            IsWorkspaceSaving = false;
        }
    }

    private static WorkspaceArrangementCard ToArrangementCard(WorkspaceItemViewModel item)
    {
        return new WorkspaceArrangementCard(
            item.Id,
            item.X,
            item.Y,
            item.Width,
            item.Height,
            item.Title,
            item.Type,
            GetArrangeSiteKey(item.Item),
            item.Item.CreatedAt);
    }

    private void ApplyPositions(IReadOnlyList<WorkspaceItemPosition> positions)
    {
        var positionMap = positions.ToDictionary(static position => position.ItemId);
        foreach (var item in _allItems)
        {
            if (positionMap.TryGetValue(item.Id, out var position))
            {
                item.X = position.X;
                item.Y = position.Y;
            }
        }
    }

    private static string GetArrangeSiteKey(WorkspaceItem item)
    {
        if (item is ShellItem shellItem)
        {
            return shellItem.TargetPath;
        }

        var url = item is SessionGroupItem sessionGroup
            ? sessionGroup.Tabs.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x.Url))?.Url
            : item.Url;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
    }

    private static string GetArrangeModeLabel(WorkspaceArrangeMode mode)
    {
        return mode switch
        {
            WorkspaceArrangeMode.CurrentPosition => "コンパクト配置",
            WorkspaceArrangeMode.Title => "名前順",
            WorkspaceArrangeMode.Type => "種類順",
            WorkspaceArrangeMode.Site => "サイト／場所順",
            WorkspaceArrangeMode.CreatedAt => "追加順",
            _ => mode.ToString()
        };
    }

    private static double NormalizeArrangeStart(double value)
    {
        return Math.Max(ArrangeStartMinimum, Math.Round(value / 8) * 8);
    }

    private async Task AddNoteAsync(object? parameter)
    {
        if (parameter is not WorkspaceNoteCreateRequest request)
        {
            return;
        }

        await TryAddNoteAsync(request);
    }

    internal async Task<bool> TryAddNoteAsync(WorkspaceNoteCreateRequest request)
    {
        if (CurrentWorkspace is null)
        {
            return false;
        }

        var content = request.Content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            StatusText = "メモを入力してください";
            return false;
        }

        var internalTitle = CreateInternalNoteTitle(content);
        NoteItem note;

        try
        {
            var point = FindAvailablePoint(request.Point, CurrentFolderId);
            IsWorkspaceSaving = true;
            StatusText = "メモを保存中";
            note = await _workspaceService.AddNoteAsync(
                CurrentWorkspace.Id,
                internalTitle,
                content,
                point.X,
                point.Y,
                CurrentFolderId,
                itemId: request.ItemId,
                fontSize: request.FontSize);
        }
        catch (Exception ex)
        {
            SetWorkspaceError("メモを追加できません: " + ex.Message);
            return false;
        }
        finally
        {
            IsWorkspaceSaving = false;
        }

        try
        {
            AddSavedItemToCanvas(note);
        }
        catch (Exception ex)
        {
            SetWorkspaceError("メモは保存しましたが、表示を更新できません: " + ex.Message);
            return true;
        }

        StatusText = "メモを追加しました";
        try
        {
            RecordRecentWorkspaceItem(note);
        }
        catch (Exception ex)
        {
            StatusText = "メモは保存しましたが、最近使った項目を更新できません: " + ex.Message;
        }

        return true;
    }

    internal static string CreateInternalNoteTitle(string content)
    {
        var remaining = content.AsSpan();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOfAny('\r', '\n');
            var line = (lineEnd >= 0 ? remaining[..lineEnd] : remaining).Trim();
            if (!line.IsEmpty)
            {
                return new string(line[..Math.Min(line.Length, 40)]);
            }

            if (lineEnd < 0)
            {
                break;
            }

            var nextLineStart = lineEnd + 1;
            if (remaining[lineEnd] == '\r'
                && nextLineStart < remaining.Length
                && remaining[nextLineStart] == '\n')
            {
                nextLineStart++;
            }

            remaining = remaining[nextLineStart..];
        }

        return "メモ";
    }

    private async Task AddFolderAsync(object? parameter)
    {
        if (parameter is not WorkspaceCanvasPoint point || CurrentWorkspace is null)
        {
            return;
        }

        try
        {
            var location = FindAvailablePoint(point, CurrentFolderId);
            IsWorkspaceSaving = true;
            StatusText = "フォルダを保存中";
            var folder = await _workspaceService.AddFolderAsync(
                CurrentWorkspace.Id,
                "新規フォルダ",
                location.X,
                location.Y,
                CurrentFolderId);
            AddSavedItemToCanvas(folder);
            StatusText = "フォルダを追加しました";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("フォルダを追加できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CurrentLocationSummary));
        OnPropertyChanged(nameof(ArrangeSelectionCount));
        OnPropertyChanged(nameof(CanArrangeSelection));
        OnPropertyChanged(nameof(ArrangeMenuText));
        OnPropertyChanged(nameof(ArrangeSelectionMenuText));
    }

    private void SearchResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultSummary));
    }

    private async Task SearchAsync(object? parameter)
    {
        var query = SearchQuery.Trim();
        SearchResults.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = "検索キーワードを入力してください";
            return;
        }

        IsLoading = true;
        StatusText = $"検索中: {query}";

        try
        {
            var items = await _workspaceService.SearchItemsAsync(query);
            foreach (var item in items
                         .OrderBy(GetWorkspaceSortIndex)
                         .ThenBy(static x => x.Type)
                         .ThenBy(static x => x.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                SearchResults.Add(new WorkspaceSearchResultViewModel(item, GetWorkspaceLabel(item.WorkspaceId)));
            }

            StatusText = SearchResults.Count == 0
                ? $"検索結果はありません: {query}"
                : $"検索しました: {SearchResults.Count} 件";
        }
        catch (Exception ex)
        {
            StatusText = "検索できません: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        StatusText = "検索をクリアしました";
    }

    private void ToggleHyperSearch()
    {
        IsHyperSearchVisible = !IsHyperSearchVisible;
        if (IsHyperSearchVisible)
        {
            IsRecentItemsVisible = false;
        }

        StatusText = IsHyperSearchVisible
            ? "Hyper Search を表示しました"
            : "Hyper Search を閉じました";
    }

    private void ToggleRecentItems()
    {
        if (IsRecentItemsVisible)
        {
            IsRecentItemsVisible = false;
            StatusText = "最近使った項目を閉じました";
            return;
        }

        ShowRecentItems();
    }

    private void SetRecentFilter(object? parameter)
    {
        RecentFilter = parameter is string value
            && Enum.TryParse<RecentItemCategory>(value, ignoreCase: true, out var category)
                ? category
                : null;
    }

    private void ToggleRecentPin(object? parameter)
    {
        if (parameter is RecentItem item)
        {
            _recentItemsService?.SetPinned(item.Key, !item.IsPinned);
        }
    }

    private void RemoveRecentItem(object? parameter)
    {
        if (parameter is RecentItem item)
        {
            _recentItemsService?.Remove(item.Key);
        }
    }

    private void RecentItemsService_ItemsChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(RefreshRecentItems);
            return;
        }

        RefreshRecentItems();
    }

    private void RefreshRecentItems()
    {
        var items = _recentItemsService?.GetSnapshot() ?? [];
        var filtered = RecentFilter is { } category
            ? items.Where(item => item.Category == category)
            : items;
        RecentItems.Clear();
        foreach (var item in filtered)
        {
            RecentItems.Add(item);
        }

        OnPropertyChanged(nameof(RecentItemSummary));
    }

    private void RecordRecentWorkspaceItem(WorkspaceItem item)
    {
        if (!_recordRecentItems || _recentItemsService is null)
        {
            return;
        }

        _recentItemsService.Record(RecentItemFactory.FromWorkspaceItem(
            item,
            GetWorkspaceLabel(item.WorkspaceId)));
    }

    private async Task GoToSearchResultAsync(object? parameter)
    {
        if (parameter is not WorkspaceSearchResultViewModel result)
        {
            return;
        }

        await LoadWorkspaceAsync(result.WorkspaceId);
        if (result.Item.ParentFolderId is { } parentFolderId)
        {
            var parentFolder = _allItems.FirstOrDefault(x => x.Id == parentFolderId)?.Item as FolderItem;
            if (parentFolder is not null)
            {
                _windows.OpenFolder(parentFolder, result.ItemId);
                StatusText = $"検索結果のフォルダを開きました: {result.Title}";
                return;
            }
        }

        var item = Items.FirstOrDefault(x => x.Id == result.ItemId);
        if (item is null)
        {
            StatusText = "検索結果のアイテムを表示できません";
            return;
        }

        SelectedItem = item;
        StatusText = $"検索結果へ移動しました: {item.Title}";
    }

    private async Task SaveDroppedTabsAsync(object? parameter)
    {
        if (IsWorkspaceSaving || parameter is not WorkspaceTabDropRequest request || request.Tabs.Count == 0)
        {
            return;
        }

        if (CurrentWorkspace is null)
        {
            StatusText = "保存先 Workspace が読み込まれていません";
            return;
        }

        try
        {
            var parentFolderId = request.TargetFolderId ?? CurrentFolderId;
            var preferredPoint = request.TargetFolderId is null
                ? request.Point
                : GetNextCanvasPoint(request.TargetFolderId);
            var point = FindAvailablePoint(preferredPoint, parentFolderId);
            var savePlan = WorkspaceTabSavePlanner.CreatePlan(request.Tabs);
            var folderTabs = savePlan.StandaloneFolderTabs;
            var sessionSnapshots = savePlan.SessionTabs;
            IsWorkspaceSaving = true;
            if (folderTabs.Count > 0 && sessionSnapshots.Count > 0)
            {
                var items = await _workspaceService.SaveMixedTabBatchAsync(savePlan, CurrentWorkspace.Id,
                    parentFolderId, point.X, point.Y, "ドラッグしたタブ");
                foreach (var item in items) AddSavedItemToCanvas(item);
                StatusText = $"フォルダタブを含む {request.Tabs.Count} タブを保存しました";
                return;
            }
            if (folderTabs.Count > 0)
            {
                var folderStart = sessionSnapshots.Count == 0
                    ? point
                    : new WorkspaceCanvasPoint(point.X + 104, point.Y + 40);
                await SaveFolderTabSnapshotsAsync(folderTabs, folderStart, parentFolderId);
            }

            if (sessionSnapshots.Count == 0)
            {
                StatusText = $"フォルダタブを保存しました: {folderTabs.Count} 件";
                return;
            }

            StatusText = request.TargetFolderId is null
                ? "Workspace へ保存中"
                : "フォルダへ保存中";

            if (sessionSnapshots.Count == 1)
            {
                var snapshot = sessionSnapshots[0];
                var title = string.IsNullOrWhiteSpace(snapshot.Title)
                    ? snapshot.Url
                    : snapshot.Title;
                var isDroppedExternalUrl = snapshot.TabIndex < 0;
                var bookmark = await _workspaceService.AddBookmarkAsync(
                    CurrentWorkspace.Id,
                    title,
                    snapshot.Url,
                    point.X,
                    point.Y,
                    parentFolderId,
                    note: isDroppedExternalUrl ? string.Empty : $"Tab {snapshot.TabIndex + 1}",
                    faviconPngBase64: snapshot.FaviconPngBase64,
                    thumbnailPngBase64: snapshot.CreateThumbnailPngBase64(),
                    containerProfileId: isDroppedExternalUrl ? null : snapshot.ContainerProfileId);
                AddSavedItemToCanvas(bookmark);
                var itemLabel = isDroppedExternalUrl ? "ブックマーク" : "タブ";
                StatusText = request.TargetFolderId is null
                    ? $"{itemLabel}を保存しました: {title}"
                    : $"フォルダへ{itemLabel}を保存しました: {title}";
                return;
            }

            var sessionTabs = WorkspaceTabSavePlanner.CreateSessionTabs(sessionSnapshots);
            var firstTitle = sessionTabs.FirstOrDefault()?.Title;
            var groupTitle = savePlan.SplitGroupCount == 1 && sessionTabs.Count == 2
                ? CreateSplitSessionTitle(sessionTabs, firstTitle)
                : string.IsNullOrWhiteSpace(firstTitle)
                    ? $"ドラッグしたタブ ({sessionTabs.Count})"
                    : $"ドラッグしたタブ: {firstTitle} ほか {sessionTabs.Count - 1}";
            var sessionGroup = await _workspaceService.AddSessionGroupAsync(
                CurrentWorkspace.Id,
                groupTitle,
                sessionTabs,
                point.X,
                point.Y,
                parentFolderId,
                note: $"{sessionTabs.Count} tabs",
                thumbnailPngBase64: sessionSnapshots.FirstOrDefault(static x => x.Thumbnail is not null)?.CreateThumbnailPngBase64() ?? string.Empty);
            AddSavedItemToCanvas(sessionGroup);
            StatusText = savePlan.SplitGroupCount > 0
                ? $"分割相手を含む {sessionTabs.Count} タブを保存しました"
                : request.TargetFolderId is null
                    ? $"SessionGroup を保存しました: {sessionTabs.Count} タブ"
                    : $"フォルダへ SessionGroup を保存しました: {sessionTabs.Count} タブ";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("ドラッグしたタブを保存できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private static string CreateSplitSessionTitle(
        IReadOnlyList<SessionTab> tabs,
        string? firstTitle)
    {
        var orientation = tabs.Any(static tab =>
            tab.SplitOrientation == TabBrowser.Models.SplitViewOrientation.TopBottom)
            ? "上下分割"
            : "左右分割";
        return string.IsNullOrWhiteSpace(firstTitle)
            ? $"{orientation} ({tabs.Count})"
            : $"{orientation}: {firstTitle} ほか {tabs.Count - 1}";
    }

    private async Task SaveFolderTabSnapshotsAsync(
        IReadOnlyList<CapturedTabSnapshot> snapshots,
        WorkspaceCanvasPoint start,
        Guid? parentFolderId)
    {
        if (CurrentWorkspace is null || snapshots.Count == 0)
        {
            return;
        }

        var items = snapshots
            .Select((snapshot, index) =>
            {
                var preferredPoint = new WorkspaceCanvasPoint(
                    start.X + (index * 104),
                    start.Y + (index * 40));
                var point = FindAvailablePoint(preferredPoint, parentFolderId);
                return new ShellItem
                {
                    WorkspaceId = CurrentWorkspace.Id,
                    ParentFolderId = parentFolderId,
                    Title = string.IsNullOrWhiteSpace(snapshot.Title)
                        ? WorkspaceShellItemService.GetDisplayName(snapshot.Url)
                        : snapshot.Title,
                    TargetPath = snapshot.Url,
                    Kind = ShellItemKind.Directory,
                    OpenInFolderTab = true,
                    ContainerProfileId = snapshot.ContainerProfileId,
                    X = point.X,
                    Y = point.Y
                };
            })
            .ToList();
        var savedItems = await _workspaceService.AddShellItemsAsync(items);
        foreach (var item in savedItems)
        {
            AddSavedItemToCanvas(item);
        }
    }

    private async Task SaveDroppedShelfItemsAsync(object? parameter)
    {
        if (parameter is not WorkspaceShelfDropRequest request)
        {
            return;
        }

        if (request.Tabs.Count > 0)
        {
            await SaveDroppedTabsAsync(new WorkspaceTabDropRequest(
                request.Tabs,
                request.Point,
                request.TargetFolderId));
        }

        if (request.YouTubeMiniPlayers.Count == 0)
        {
            return;
        }

        var point = request.Tabs.Count == 0
            ? request.Point
            : new WorkspaceCanvasPoint(request.Point.X + 104, request.Point.Y + 40);
        await SaveDroppedYouTubeMiniPlayersAsync(
            request.YouTubeMiniPlayers,
            point,
            request.TargetFolderId);
    }

    private async Task SaveDroppedShellItemsAsync(object? parameter)
    {
        if (parameter is not WorkspaceShellDropRequest request || CurrentWorkspace is null)
        {
            return;
        }

        var paths = request.Paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(WorkspaceShellItemService.NormalizePath)
            .Where(static path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            StatusText = "保存できるファイルまたはフォルダーがありません";
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = $"{paths.Count} 件を Workspace へ登録中";
            var parentFolderId = request.TargetFolderId ?? CurrentFolderId;
            var plannedItems = paths.Select((path, index) =>
            {
                var preferredPoint = new WorkspaceCanvasPoint(
                    request.Point.X + index % 5 * 96,
                    request.Point.Y + index / 5 * 110);
                var point = FindAvailablePoint(preferredPoint, parentFolderId);
                return new WorkspaceShellDropPlanItem(path, point);
            }).ToList();

            var importedNotes = new List<NoteItem>();
            if (request.TextFileMode == WorkspaceTextFileDropMode.ImportAsNote)
            {
                foreach (var plannedItem in plannedItems.Where(static item =>
                             File.Exists(item.Path)
                             && WorkspaceShellItemService.IsTextDocumentPath(item.Path)))
                {
                    var content = await WorkspaceShellItemService.ReadTextDocumentAsync(plannedItem.Path);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidDataException(
                            $"空のテキストファイルはメモへ取り込めません: {WorkspaceShellItemService.GetDisplayName(plannedItem.Path)}");
                    }

                    var note = await _workspaceService.AddNoteAsync(
                        CurrentWorkspace.Id,
                        CreateInternalNoteTitle(content),
                        content,
                        plannedItem.Point.X,
                        plannedItem.Point.Y,
                        parentFolderId);
                    importedNotes.Add(note);
                    AddSavedItemToCanvas(note);
                    RecordRecentWorkspaceItem(note);
                }
            }

            var shellItems = plannedItems
                .Where(item => request.TextFileMode != WorkspaceTextFileDropMode.ImportAsNote
                    || !File.Exists(item.Path)
                    || !WorkspaceShellItemService.IsTextDocumentPath(item.Path))
                .Select(item => new ShellItem
                {
                    WorkspaceId = CurrentWorkspace.Id,
                    ParentFolderId = parentFolderId,
                    Title = WorkspaceShellItemService.GetDisplayName(item.Path),
                    TargetPath = item.Path,
                    Kind = _shellItemService.Classify(item.Path),
                    Tags = ["shell"],
                    X = item.Point.X,
                    Y = item.Point.Y
                })
                .ToList();

            var savedItems = await _workspaceService.AddShellItemsAsync(shellItems);
            foreach (var item in savedItems)
            {
                AddSavedItemToCanvas(item);
            }

            if (savedItems.Count > 0)
            {
                _ = BackfillShellMediaThumbnailsAsync();
                _ = BackfillShellItemIconsAsync();
            }

            StatusText = importedNotes.Count > 0
                ? savedItems.Count > 0
                    ? $"メモ {importedNotes.Count} 件、ファイルとフォルダー {savedItems.Count} 件を登録しました"
                    : $"テキストファイルをメモとして取り込みました: {importedNotes.Count} 件"
                : $"ファイルとフォルダーを登録しました: {savedItems.Count} 件";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("ファイルとフォルダーを登録できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task ConvertShellTextFileToNoteAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel
            {
                Item: ShellItem { Kind: ShellItemKind.File } shellItem
            } item
            || !WorkspaceShellItemService.IsTextDocumentPath(shellItem.TargetPath))
        {
            return;
        }

        try
        {
            IsWorkspaceSaving = true;
            StatusText = $"メモへ変換中: {item.Title}";
            var content = await WorkspaceShellItemService.ReadTextDocumentAsync(shellItem.TargetPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("空のテキストファイルはメモへ変換できません。");
            }

            var note = await _workspaceService.ConvertShellItemToNoteAsync(
                shellItem,
                CreateInternalNoteTitle(content),
                content);
            SelectVisibleItem(note.Id);
            RecordRecentWorkspaceItem(note);
            StatusText = $"テキストファイルをメモへ変換しました: {item.Title}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("メモへ変換できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private void ShowShellItemInFolder(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel { Item: ShellItem shellItem } item)
        {
            return;
        }

        try
        {
            _shellItemService.ShowInFolder(shellItem);
            StatusText = $"Explorer で表示しました: {item.Title}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("Explorer で表示できません: " + ex.Message);
        }
    }

    private async Task RelinkShellItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceShellPathRequest { Item.Item: ShellItem } request)
        {
            return;
        }

        try
        {
            var path = WorkspaceShellItemService.NormalizePath(request.Path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new FileNotFoundException("選択した対象が見つかりません。", path);
            }

            var kind = _shellItemService.Classify(path);
            await _workspaceService.UpdateShellItemTargetAsync(request.Item.Id, path, kind,
                expectedTargetPath: request.Item.TargetPath);
            request.Item.UpdateShellTarget(path, kind);
            request.Item.SetFaviconPngBase64(string.Empty);
            _shellIconAttemptedItemIds.Remove(request.Item.Id);
            _ = EnsureShellItemIconAsync(request.Item);
            StatusText = $"参照先を更新しました: {request.Item.Title}";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("参照先を更新できません: " + ex.Message);
        }
    }

    private async Task CreateShellDirectoryAsync(object? parameter)
    {
        if (parameter is not WorkspaceShellNameRequest { Item.Item: ShellItem { Kind: ShellItemKind.Directory } parent } request
            || CurrentWorkspace is null)
        {
            return;
        }

        var workspaceId = CurrentWorkspace.Id;
        var point = FindAvailablePoint(
            new WorkspaceCanvasPoint(request.Item.X + 96, request.Item.Y),
            request.Item.Item.ParentFolderId);
        ShellFileOperationResult result;
        try
        {
            result = _shellItemService.CreateDirectory(parent.TargetPath, request.Name);
        }
        catch (Exception ex)
        {
            SetWorkspaceError("実フォルダー作成中に失敗しました。途中の実ファイル状態は要確認です: " + ex.Message);
            return;
        }

        var created = new ShellItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = request.Item.Item.ParentFolderId,
            Title = WorkspaceShellItemService.GetDisplayName(result.DestinationPath),
            TargetPath = result.DestinationPath,
            Kind = ShellItemKind.Directory,
            Tags = ["shell"],
            X = point.X,
            Y = point.Y
        };
        await CompleteShellOperationAsync(result, created.Id, workspaceId, createsCard: true,
            () => _workspaceService.AddShellItemsAsync([created]),
            () => AddSavedItemToCanvas(created),
            $"実フォルダーを作成しました: {created.Title}");
    }

    private async Task RenameShellTargetAsync(object? parameter)
    {
        if (parameter is not WorkspaceShellNameRequest { Item.Item: ShellItem shellItem } request)
        {
            return;
        }

        ShellFileOperationResult result;
        try
        {
            result = _shellItemService.Rename(shellItem, request.Name);
        }
        catch (Exception ex)
        {
            SetWorkspaceError("実ファイル名の変更中に失敗しました。途中の実ファイル状態は要確認です: " + ex.Message);
            return;
        }

        var kind = _shellItemService.Classify(result.DestinationPath);
        await CompleteShellOperationAsync(result, request.Item.Id, shellItem.WorkspaceId, createsCard: false,
            () => _workspaceService.UpdateShellItemTargetAsync(request.Item.Id, result.DestinationPath, kind,
                expectedTargetPath: result.SourcePath),
            () => UpdateVisibleShellTarget(request.Item.Id, result.DestinationPath, kind, shellItem.WorkspaceId),
            $"実ファイル名を変更しました: {WorkspaceShellItemService.GetDisplayName(result.DestinationPath)}");
    }

    private Task CopyShellItemAsync(object? parameter)
    {
        return parameter is WorkspaceShellPathRequest request
            ? CopyOrMoveShellItemAsync(request, isMove: false)
            : Task.CompletedTask;
    }

    private Task MoveShellItemAsync(object? parameter)
    {
        return parameter is WorkspaceShellPathRequest request
            ? CopyOrMoveShellItemAsync(request, isMove: true)
            : Task.CompletedTask;
    }

    private async Task CopyOrMoveShellItemAsync(WorkspaceShellPathRequest request, bool isMove)
    {
        if (request.Item.Item is not ShellItem shellItem || CurrentWorkspace is null)
        {
            return;
        }

        var workspaceId = CurrentWorkspace.Id;
        var point = FindAvailablePoint(
            new WorkspaceCanvasPoint(request.Item.X + 96, request.Item.Y),
            request.Item.Item.ParentFolderId);
        ShellFileOperationResult result;
        try
        {
            result = isMove
                ? _shellItemService.Move(shellItem, request.Path)
                : _shellItemService.Copy(shellItem, request.Path);
        }
        catch (Exception ex)
        {
            SetWorkspaceError(isMove
                ? "実ファイルの移動中に失敗しました。途中の実ファイル状態は要確認です: " + ex.Message
                : "実ファイルのコピー中に失敗しました。途中の実ファイル状態は要確認です: " + ex.Message);
            return;
        }

        var kind = _shellItemService.Classify(result.DestinationPath);
        if (isMove)
        {
            await CompleteShellOperationAsync(result, request.Item.Id, shellItem.WorkspaceId, createsCard: false,
                () => _workspaceService.UpdateShellItemTargetAsync(request.Item.Id, result.DestinationPath, kind,
                    expectedTargetPath: result.SourcePath),
                () => UpdateVisibleShellTarget(request.Item.Id, result.DestinationPath, kind, shellItem.WorkspaceId),
                $"実ファイルを移動しました: {request.Item.Title}");
            return;
        }

        var copied = new ShellItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = request.Item.Item.ParentFolderId,
            Title = WorkspaceShellItemService.GetDisplayName(result.DestinationPath),
            TargetPath = result.DestinationPath,
            Kind = kind,
            Tags = ["shell"],
            X = point.X,
            Y = point.Y
        };
        await CompleteShellOperationAsync(result, copied.Id, workspaceId, createsCard: true,
            () => _workspaceService.AddShellItemsAsync([copied]),
            () => AddSavedItemToCanvas(copied),
            $"実ファイルをコピーしました: {copied.Title}");
    }

    private Task RecycleShellItemAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel { Item: ShellItem shellItem } item)
        {
            return Task.CompletedTask;
        }

        try
        {
            _shellItemService.MoveToRecycleBin(shellItem);
            item.RefreshShellState();
            SetLastShellOperation(null);
            StatusText = "ごみ箱へ移動しました。Workspace カードは参照切れとして残しています";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("ごみ箱へ移動できません: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private async Task UndoShellOperationAsync(object? parameter)
    {
        if (_undoShellOperationInProgress || _lastShellOperation is not { Result.CanUndo: true } operation)
        {
            StatusText = "元に戻せるファイル操作がありません";
            return;
        }

        _undoShellOperationInProgress = true;
        OnPropertyChanged(nameof(CanUndoShellOperation));
        try
        {
            if (!operation.FileUndone)
            {
                var current = await _workspaceService.GetItemByIdAsync(operation.ItemId);
                if (current is not ShellItem shell
                    || shell.WorkspaceId != operation.WorkspaceId
                    || shell.DeletedAt is null
                        && !string.Equals(shell.TargetPath, operation.Result.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceItemUnavailableException();
                }

                _shellItemService.Undo(operation.Result);
                operation.FileUndone = true;
                operation.ExpectedKind = shell.Kind;
            }

            if (operation.Result.Kind is ShellFileOperationKind.Rename or ShellFileOperationKind.Move)
            {
                var stored = await _workspaceService.GetItemByIdAsync(operation.ItemId);
                if (stored is ShellItem { DeletedAt: null } shell && shell.WorkspaceId == operation.WorkspaceId)
                {
                    var kind = _shellItemService.Classify(operation.Result.SourcePath);
                    await _workspaceService.UpdateShellItemTargetAsync(shell.Id, operation.Result.SourcePath, kind,
                        expectedTargetPath: operation.Result.DestinationPath);
                    UpdateVisibleShellTarget(shell.Id, operation.Result.SourcePath, kind, operation.WorkspaceId);
                }
                else
                {
                    throw new WorkspaceItemUnavailableException();
                }
            }
            else
            {
                var stored = await _workspaceService.GetItemByIdAsync(operation.ItemId);
                if (stored is ShellItem { DeletedAt: null } shell
                    && shell.WorkspaceId == operation.WorkspaceId
                    && string.Equals(shell.TargetPath, operation.Result.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (operation.ExpectedKind is not { } expectedKind)
                        throw new WorkspaceItemUnavailableException();
                    await _workspaceService.DeleteShellItemForUndoAsync(operation.ItemId,
                        operation.WorkspaceId, operation.Result.DestinationPath, expectedKind);
                }
                else
                {
                    throw new WorkspaceItemUnavailableException();
                }

                if (CurrentWorkspace?.Id == operation.WorkspaceId)
                {
                    _allItems.RemoveAll(item => item.Id == operation.ItemId);
                    RefreshFolderChildCounts();
                    ApplyVisibleItems();
                }
            }

            if (ReferenceEquals(_lastShellOperation, operation))
            {
                SetLastShellOperation(null);
                StatusText = "直前のファイル操作を元に戻しました";
            }
        }
        catch (Exception ex)
        {
            if (operation.FileUndone)
            {
                try
                {
                    var stored = await _workspaceService.GetItemByIdAsync(operation.ItemId);
                    if (stored?.WorkspaceId == operation.WorkspaceId
                        && (stored.DeletedAt is not null
                            || operation.Result.Kind is ShellFileOperationKind.Rename or ShellFileOperationKind.Move
                                && stored is ShellItem shell
                                && string.Equals(shell.TargetPath, operation.Result.SourcePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (ReferenceEquals(_lastShellOperation, operation)) SetLastShellOperation(null);
                        var message = stored.DeletedAt is not null
                            ? "実ファイルは元へ戻りました。Workspaceカードは既に削除済みのため復活させていません。"
                            : "実ファイルとDBは元へ戻りましたが、表示更新に失敗しました: " + ex.Message;
                        var resyncError = await TryResyncShellWorkspaceAsync(operation.WorkspaceId);
                        if (resyncError is not null) message += " 画面の再読み込みにも失敗しました: " + resyncError;
                        SetWorkspaceError(message);
                        return;
                    }
                }
                catch
                {
                    // 確認不能な場合はUndo状態を保持し、実ファイル側を再実行しない。
                }
            }

            SetWorkspaceError(operation.FileUndone
                ? $"実ファイルは元へ戻りましたが、カードの更新は未完了または不明です。再試行時に実ファイルは操作しません。元: {operation.Result.SourcePath} / 変更先: {operation.Result.DestinationPath} / DB: 要確認。{ex.Message}"
                : $"実ファイルのUndoは完了していません。実ファイル状態を確認してください。元: {operation.Result.SourcePath} / 変更先: {operation.Result.DestinationPath} / DB: 要確認。{ex.Message}");
        }
        finally
        {
            _undoShellOperationInProgress = false;
            OnPropertyChanged(nameof(CanUndoShellOperation));
        }
    }

    private void OpenRecycleBin()
    {
        try
        {
            _shellItemService.OpenRecycleBin();
        }
        catch (Exception ex)
        {
            SetWorkspaceError("ごみ箱を開けません: " + ex.Message);
        }
    }

    private void SetLastShellOperation(WorkspaceShellUndoState? operation)
    {
        _lastShellOperation = operation;
        OnPropertyChanged(nameof(CanUndoShellOperation));
    }

    private async Task CompleteShellOperationAsync(
        ShellFileOperationResult result,
        Guid itemId,
        Guid workspaceId,
        bool createsCard,
        Func<Task> persist,
        Action applyView,
        string successMessage)
    {
        try
        {
            await persist();
        }
        catch (Exception dbError)
        {
            WorkspaceItem? stored;
            try
            {
                stored = await _workspaceService.GetItemByIdAsync(itemId);
            }
            catch (Exception inspectError)
            {
                SetWorkspaceError($"DB更新の結果を確認できません。補償はせず実ファイルを保持しました。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: 不明。保存エラー: {dbError.Message} / 確認エラー: {inspectError.Message}");
                return;
            }

            if (dbError is WorkspaceItemTargetConflictException or WorkspaceItemUnavailableException
                && stored is ShellItem { DeletedAt: null } typedTarget
                && string.Equals(typedTarget.TargetPath, result.DestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                SetWorkspaceError($"DB更新対象が変更または削除されたため、実ファイルの補償を保留しました。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: {((stored as ShellItem)?.TargetPath ?? "なし・別種類")}。{dbError.Message}");
                return;
            }

            if (stored is ShellItem { DeletedAt: null } committed
                && committed.WorkspaceId == workspaceId
                && string.Equals(committed.TargetPath, result.DestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                var message = $"DB保存は完了しましたが、通知に失敗しました。実ファイルは保持しています。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: {committed.TargetPath}。{dbError.Message}";
                try { SetLastShellOperation(new WorkspaceShellUndoState(result, itemId, workspaceId)); }
                catch (Exception stateError) { message += " Undo表示も更新できません: " + stateError.Message; }
                var resyncError = await TryResyncShellWorkspaceAsync(workspaceId);
                if (resyncError is not null) message += " 画面の再読み込みにも失敗しました: " + resyncError;
                SetWorkspaceError(message);
                return;
            }

            var unchanged = createsCard
                ? stored is null
                : stored is ShellItem old && old.WorkspaceId == workspaceId
                    && string.Equals(old.TargetPath, result.SourcePath, StringComparison.OrdinalIgnoreCase);
            if (!unchanged || !result.CanUndo)
            {
                var reason = !unchanged ? "DB状態が変化したか安全に確認できません" : "実ファイルの同一性を証明できません";
                SetWorkspaceError($"DB更新に失敗し、{reason}。補償を保留して実ファイルを保持しました。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: {((stored as ShellItem)?.TargetPath ?? "なし・別種類")}。{dbError.Message}");
                return;
            }

            try
            {
                _shellItemService.Undo(result);
                SetWorkspaceError($"DB更新に失敗しましたが、実ファイルは安全確認後に元へ戻しました。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: 変更前。{dbError.Message}");
            }
            catch (Exception undoError)
            {
                SetWorkspaceError($"DB更新と実ファイルの補償が両方失敗しました。実ファイル状態を確認してください。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: 変更前。保存エラー: {dbError.Message} / 補償エラー: {undoError.Message}");
            }

            return;
        }

        try
        {
            SetLastShellOperation(new WorkspaceShellUndoState(result, itemId, workspaceId));
            if (CurrentWorkspace?.Id == workspaceId)
                applyView();
            StatusText = result.CanUndo ? successMessage : successMessage + "（安全なUndoは利用できません）";
        }
        catch (Exception viewError)
        {
            var message = $"DB保存は完了しましたが、画面更新に失敗しました。元: {result.SourcePath} / 変更先: {result.DestinationPath} / DB: 変更先。{viewError.Message}";
            var resyncError = await TryResyncShellWorkspaceAsync(workspaceId);
            if (resyncError is not null) message += " 画面の再読み込みにも失敗しました: " + resyncError;
            SetWorkspaceError(message);
        }
    }

    private void UpdateVisibleShellTarget(Guid itemId, string path, ShellItemKind kind, Guid workspaceId)
    {
        if (CurrentWorkspace?.Id != workspaceId) return;
        var item = _allItems.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null) return;
        item.UpdateShellTarget(path, kind);
        item.SetFaviconPngBase64(string.Empty);
        _shellIconAttemptedItemIds.Remove(itemId);
        _ = EnsureShellItemIconAsync(item);
    }

    private async Task<string?> TryResyncShellWorkspaceAsync(Guid workspaceId)
    {
        if (CurrentWorkspace?.Id != workspaceId) return null;
        try
        {
            await LoadWorkspaceAsync(workspaceId, folderId: CurrentFolderId,
                selectedItemId: SelectedItem?.Id, forceReload: true);
            return HasWorkspaceError ? WorkspaceErrorText : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task SaveDroppedYouTubeMiniPlayersAsync(
        IReadOnlyList<YouTubeMiniPlayerWorkspaceState> players,
        WorkspaceCanvasPoint preferredPoint,
        Guid? targetFolderId)
    {
        if (CurrentWorkspace is null || players.Count == 0)
        {
            StatusText = "保存先Workspaceが読み込まれていません";
            return;
        }

        try
        {
            var parentFolderId = targetFolderId ?? CurrentFolderId;
            var point = FindAvailablePoint(preferredPoint, parentFolderId);
            IsWorkspaceSaving = true;
            StatusText = "YouTubeミニプレイヤーを保存中";
            var firstTitle = players.FirstOrDefault(static player => !string.IsNullOrWhiteSpace(player.Title))?.Title;
            var title = players.Count == 1
                ? firstTitle ?? "YouTubeミニプレイヤー"
                : $"YouTubeミニプレイヤー {players.Count}件";
            var item = await _workspaceService.AddYouTubeMiniPlayerSetAsync(
                CurrentWorkspace.Id,
                title,
                players,
                point.X,
                point.Y,
                parentFolderId,
                note: $"再生位置付き {players.Count}件",
                tags: ["youtube", "mini-player"]);
            AddSavedItemToCanvas(item);
            _ = EnsureItemThumbnailAsync(_allItems.Single(savedItem => savedItem.Id == item.Id));
            StatusText = $"YouTubeミニプレイヤーを保存しました: {players.Count}件";
        }
        catch (Exception ex)
        {
            SetWorkspaceError("YouTubeミニプレイヤーを保存できません: " + ex.Message);
        }
        finally
        {
            IsWorkspaceSaving = false;
        }
    }

    private async Task SaveSelectedTabsToFolderAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel { Item: FolderItem } folder)
        {
            return;
        }

        var selectedTabs = TabShelf.SelectedTabs
            .Select(static x => x.Snapshot)
            .OfType<CapturedTabSnapshot>()
            .OrderBy(static x => x.WindowSortOrder)
            .ThenBy(static x => x.TabIndex)
            .ToList();
        if (selectedTabs.Count == 0)
        {
            StatusText = "フォルダへ保存するタブを左の一覧で選択してください";
            return;
        }

        await SaveTabsToFolderAsync(folder, selectedTabs);
    }

    private async Task SaveAllTabsToFolderAsync(object? parameter)
    {
        if (parameter is not WorkspaceItemViewModel { Item: FolderItem } folder)
        {
            return;
        }

        var tabs = TabShelf.OpenTabs
            .Select(static x => x.Snapshot)
            .OfType<CapturedTabSnapshot>()
            .OrderBy(static x => x.WindowSortOrder)
            .ThenBy(static x => x.TabIndex)
            .ToList();
        if (tabs.Count == 0)
        {
            StatusText = "フォルダへ保存できるタブがありません";
            return;
        }

        await SaveTabsToFolderAsync(folder, tabs);
    }

    private async Task SaveTabsToFolderAsync(WorkspaceItemViewModel folder, IReadOnlyList<CapturedTabSnapshot> tabs)
    {
        await SaveDroppedTabsAsync(new WorkspaceTabDropRequest(
            tabs,
            GetNextCanvasPoint(folder.Id),
            folder.Id));
        SelectedItem = folder;
        RefreshFolderChildCounts();
    }

    public void RefreshOpenTabs()
    {
        TabShelf.RefreshTabs();
    }

    private WorkspaceCanvasPoint GetNextCanvasPoint()
    {
        return GetNextCanvasPoint(CurrentFolderId);
    }

    private WorkspaceCanvasPoint GetNextCanvasPoint(Guid? parentFolderId)
    {
        var index = _allItems.Count(x => x.Item.ParentFolderId == parentFolderId);
        return FindAvailablePoint(new WorkspaceCanvasPoint(
            32 + (index % 6 * 156),
            32 + (index / 6 * 76)),
            parentFolderId);
    }

    private static WorkspaceCanvasPoint GetNextCanvasPoint(
        IReadOnlyList<WorkspaceItem> items,
        Guid? parentFolderId)
    {
        var index = items.Count(item => item.ParentFolderId == parentFolderId);
        return new WorkspaceCanvasPoint(
            32 + (index % 6 * 156),
            32 + (index / 6 * 76));
    }

    private WorkspaceCanvasPoint FindAvailablePoint(
        WorkspaceCanvasPoint preferredPoint,
        Guid? parentFolderId,
        WorkspaceItemViewModel? ignoredItem = null)
    {
        var startX = NormalizeArrangeStart(preferredPoint.X);
        var startY = NormalizeArrangeStart(preferredPoint.Y);
        var width = ignoredItem?.Width ?? 88;
        var height = ignoredItem?.Height ?? 100;
        const int maxAvoidanceRadius = 45;

        if (IsAreaAvailable(startX, startY, width, height, parentFolderId, ignoredItem))
        {
            return new WorkspaceCanvasPoint(startX, startY);
        }

        var overlappingItem = FindFirstOverlappingItem(startX, startY, width, height, parentFolderId, ignoredItem);
        var directions = GetAvoidanceDirections(startX, startY, width, height, overlappingItem);
        for (var radius = 1; radius <= maxAvoidanceRadius; radius++)
        {
            foreach (var direction in directions)
            {
                var candidateX = NormalizeArrangeStart(startX + direction.X * AvoidanceStepX * radius);
                var candidateY = NormalizeArrangeStart(startY + direction.Y * AvoidanceStepY * radius);
                if (IsAreaAvailable(candidateX, candidateY, width, height, parentFolderId, ignoredItem))
                {
                    return new WorkspaceCanvasPoint(candidateX, candidateY);
                }
            }
        }

        return new WorkspaceCanvasPoint(startX, startY);
    }

    private WorkspaceCanvasPoint FindAvailablePointForLocation(
        WorkspaceCanvasPoint preferredPoint,
        Guid workspaceId,
        Guid? parentFolderId)
    {
        if (CurrentWorkspace?.Id == workspaceId)
        {
            return FindAvailablePoint(preferredPoint, parentFolderId);
        }

        var items = _workspaceRuntimeCache.TryGetValue(workspaceId, out var runtime)
            ? runtime.AllItems
            : [];
        var startX = NormalizeArrangeStart(preferredPoint.X);
        var startY = NormalizeArrangeStart(preferredPoint.Y);
        const double width = 88;
        const double height = 100;
        for (var radius = 0; radius <= 45; radius++)
        {
            foreach (var direction in radius == 0 ? [(0, 0)] : AvoidanceDirections)
            {
                var candidateX = NormalizeArrangeStart(startX + direction.X * AvoidanceStepX * radius);
                var candidateY = NormalizeArrangeStart(startY + direction.Y * AvoidanceStepY * radius);
                if (!items.Where(item => item.Item.ParentFolderId == parentFolderId).Any(item =>
                        RectanglesOverlap(
                            candidateX,
                            candidateY,
                            width,
                            height,
                            item.X,
                            item.Y,
                            item.Width,
                            item.Height)))
                {
                    return new WorkspaceCanvasPoint(candidateX, candidateY);
                }
            }
        }

        return new WorkspaceCanvasPoint(startX, startY);
    }

    private bool IsAreaAvailable(
        double x,
        double y,
        double width,
        double height,
        Guid? parentFolderId,
        WorkspaceItemViewModel? ignoredItem)
    {
        return !_allItems
            .Where(item => item.Item.ParentFolderId == parentFolderId && !ReferenceEquals(item, ignoredItem))
            .Any(item => RectanglesOverlap(x, y, width, height, item.X, item.Y, item.Width, item.Height));
    }

    private WorkspaceItemViewModel? FindFirstOverlappingItem(
        double x,
        double y,
        double width,
        double height,
        Guid? parentFolderId,
        WorkspaceItemViewModel? ignoredItem)
    {
        return _allItems
            .Where(item => item.Item.ParentFolderId == parentFolderId && !ReferenceEquals(item, ignoredItem))
            .Where(item => RectanglesOverlap(x, y, width, height, item.X, item.Y, item.Width, item.Height))
            .OrderBy(item => GetCenterDistanceSquared(x, y, width, height, item))
            .FirstOrDefault();
    }

    private static (int X, int Y)[] GetAvoidanceDirections(
        double x,
        double y,
        double width,
        double height,
        WorkspaceItemViewModel? overlappingItem)
    {
        if (overlappingItem is null)
        {
            return AvoidanceDirections;
        }

        var candidateCenterX = x + width / 2;
        var candidateCenterY = y + height / 2;
        var itemCenterX = overlappingItem.X + overlappingItem.Width / 2;
        var itemCenterY = overlappingItem.Y + overlappingItem.Height / 2;
        var offsetX = candidateCenterX - itemCenterX;
        var offsetY = candidateCenterY - itemCenterY;
        var primaryDirection = GetPrimaryAvoidanceDirection(offsetX, offsetY);

        return BuildAvoidanceDirectionOrder(primaryDirection);
    }

    private static (int X, int Y) GetPrimaryAvoidanceDirection(double offsetX, double offsetY)
    {
        const double diagonalThreshold = 0.41421356237309503;
        var absX = Math.Abs(offsetX);
        var absY = Math.Abs(offsetY);
        if (absX < 0.001 && absY < 0.001)
        {
            return (1, 0);
        }

        var directionX = GetDirectionSign(offsetX);
        var directionY = GetDirectionSign(offsetY);
        if (absY <= absX * diagonalThreshold)
        {
            return (directionX, 0);
        }

        if (absX <= absY * diagonalThreshold)
        {
            return (0, directionY);
        }

        return (directionX, directionY);
    }

    private static int GetDirectionSign(double value)
    {
        if (Math.Abs(value) < 0.001)
        {
            return 0;
        }

        return value > 0 ? 1 : -1;
    }

    private static (int X, int Y)[] BuildAvoidanceDirectionOrder((int X, int Y) primaryDirection)
    {
        var primaryIndex = Array.IndexOf(AvoidanceDirections, primaryDirection);
        if (primaryIndex < 0)
        {
            return AvoidanceDirections;
        }

        int[] offsets = [0, 1, -1, 2, -2, 3, -3, 4];
        var orderedDirections = new List<(int X, int Y)>(AvoidanceDirections.Length);
        foreach (var offset in offsets)
        {
            var index = (primaryIndex + offset + AvoidanceDirections.Length) % AvoidanceDirections.Length;
            var direction = AvoidanceDirections[index];
            if (!orderedDirections.Contains(direction))
            {
                orderedDirections.Add(direction);
            }
        }

        return orderedDirections.ToArray();
    }

    private static double GetCenterDistanceSquared(
        double x,
        double y,
        double width,
        double height,
        WorkspaceItemViewModel item)
    {
        var centerX = x + width / 2;
        var centerY = y + height / 2;
        var itemCenterX = item.X + item.Width / 2;
        var itemCenterY = item.Y + item.Height / 2;
        var distanceX = centerX - itemCenterX;
        var distanceY = centerY - itemCenterY;
        return distanceX * distanceX + distanceY * distanceY;
    }

    private static bool RectanglesOverlap(
        double x1,
        double y1,
        double width1,
        double height1,
        double x2,
        double y2,
        double width2,
        double height2)
    {
        const double allowedOverlap = 44;
        return x1 + allowedOverlap < x2 + width2
            && x1 + width1 - allowedOverlap > x2
            && y1 + allowedOverlap < y2 + height2
            && y1 + height1 - allowedOverlap > y2;
    }

    private void SetLastArrangePositions(List<WorkspaceItemPosition>? positions)
    {
        _lastArrangePositions = positions;
        OnPropertyChanged(nameof(HasArrangeSnapshot));
    }

    private void CaptureInitialArrangePositions(IEnumerable<WorkspaceItemPosition> positions)
    {
        var captured = (_lastArrangePositions ?? []).ToDictionary(static position => position.ItemId);
        foreach (var position in positions)
        {
            captured.TryAdd(position.ItemId, position);
        }

        SetLastArrangePositions(captured.Values.ToList());
    }

    private void RemoveArrangePositions(IEnumerable<Guid> itemIds)
    {
        if (_lastArrangePositions is not { Count: > 0 })
        {
            return;
        }

        var removedIds = itemIds.ToHashSet();
        if (_lastArrangePositions.RemoveAll(position => removedIds.Contains(position.ItemId)) > 0)
        {
            SetLastArrangePositions(_lastArrangePositions.Count > 0 ? _lastArrangePositions : null);
        }
    }

    private void AddSavedItemToCanvas(WorkspaceItem item)
    {
        if (_allItems.FirstOrDefault(existing => existing.Id == item.Id) is { } existing)
        {
            if (item is ShellItem)
            {
                _ = EnsureShellItemIconAsync(existing);
            }

            if (item.ParentFolderId == CurrentFolderId && Items.Contains(existing))
            {
                SelectedItem = existing;
            }
            else if (Items.FirstOrDefault(candidate => candidate.Id == item.ParentFolderId) is { } existingParentFolder)
            {
                SelectedItem = existingParentFolder;
            }

            return;
        }

        var viewModel = new WorkspaceItemViewModel(item);
        _allItems.Add(viewModel);
        if (item is ShellItem)
        {
            _ = EnsureShellItemIconAsync(viewModel);
        }

        RefreshFolderChildCounts();

        if (item.ParentFolderId == CurrentFolderId)
        {
            Items.Add(viewModel);
            SelectedItem = viewModel;
            return;
        }

        var parentFolder = Items.FirstOrDefault(x => x.Id == item.ParentFolderId);
        if (parentFolder is not null)
        {
            SelectedItem = parentFolder;
        }
    }

    private void EnterFolder(WorkspaceItemViewModel folder)
    {
        if (folder.Item is not FolderItem)
        {
            return;
        }

        SetCurrentFolder(folder.Id);
        StatusText = $"フォルダを開きました: {folder.Title}";
    }

    private Task GoToParentFolderAsync(object? parameter)
    {
        if (CurrentFolderId is null)
        {
            return Task.CompletedTask;
        }

        var currentFolder = _allItems.FirstOrDefault(x => x.Id == CurrentFolderId);
        SetCurrentFolder(currentFolder?.Item.ParentFolderId);
        StatusText = CurrentFolderId is null
            ? "Workspace 直下へ戻りました"
            : $"上のフォルダへ戻りました: {CurrentLocationTitle}";
        return Task.CompletedTask;
    }

    private void SetCurrentFolder(Guid? folderId)
    {
        CurrentFolderId = folderId;
        ApplyVisibleItems();
    }

    private void ApplyVisibleItems()
    {
        SelectedItem = null;
        var items = new ObservableCollection<WorkspaceItemViewModel>(
            _allItems.Where(item => item.Item.ParentFolderId == CurrentFolderId));
        _currentWorkspaceRuntime?.ReplaceVisibleItems(CurrentFolderId, items);
        SetItemsCollection(items);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CurrentLocationTitle));
        OnPropertyChanged(nameof(CurrentLocationSummary));
    }

    private void SelectVisibleItem(Guid? itemId)
    {
        if (itemId is null)
        {
            return;
        }

        if (Items.FirstOrDefault(x => x.Id == itemId) is { } item)
        {
            SelectedItem = item;
        }
    }

    private bool IsKnownFolder(Guid? folderId)
    {
        return folderId is not null
            && _allItems.Any(x => x.Id == folderId.Value && x.Item is FolderItem);
    }

    private void RefreshFolderChildCounts()
    {
        RefreshFolderChildCounts(_allItems);
    }

    private static void RefreshFolderChildCounts(IReadOnlyList<WorkspaceItemViewModel> items)
    {
        var childCounts = items
            .Where(static item => item.Item.ParentFolderId is not null)
            .GroupBy(static item => item.Item.ParentFolderId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var folder in items.Where(static item => item.Item is FolderItem))
        {
            folder.ChildItemCount = childCounts.GetValueOrDefault(folder.Id);
        }
    }

    void IWorkspaceChangeListener.OnWorkspaceItemsMoved(WorkspaceItemsMovedChange change)
    {
        RemoveArrangePositions(change.Moves.Select(static move => move.ItemId));
        if (!_initialized || CurrentWorkspace is null)
        {
            return;
        }

        var currentWorkspaceId = CurrentWorkspace.Id;
        var originalWorkspaceId = currentWorkspaceId;
        var affectedWorkspaceIds = change.Moves
            .SelectMany(static move => new[] { move.SourceWorkspaceId, move.TargetWorkspaceId })
            .Distinct()
            .ToList();
        var committedItems = change.Items.ToDictionary(static item => item.Id);
        foreach (var workspaceId in affectedWorkspaceIds.Where(id => id != currentWorkspaceId))
        {
            if (_workspaceRuntimeCache.TryGetValue(workspaceId, out var runtime))
            {
                ApplyCommittedMoves(runtime.AllItems, workspaceId, change.Moves, committedItems);
                RefreshFolderChildCounts(runtime.AllItems);
                runtime.RefreshVisibleItems();
            }
        }

        var movedCurrentFolder = CurrentFolderId is { } currentFolderId
            ? change.Moves.FirstOrDefault(move => move.ItemId == currentFolderId)
            : null;
        if (movedCurrentFolder is not null
            && movedCurrentFolder.TargetWorkspaceId != currentWorkspaceId
            && Workspaces.FirstOrDefault(workspace => workspace.Id == movedCurrentFolder.TargetWorkspaceId) is { } targetWorkspace)
        {
            var movedIds = change.Moves.Select(static move => move.ItemId).ToHashSet();
            _allItems.RemoveAll(item => !movedIds.Contains(item.Id));
            CurrentWorkspace = targetWorkspace.Workspace;
            foreach (var workspace in Workspaces)
            {
                workspace.IsSelected = workspace.Id == targetWorkspace.Id;
            }

            currentWorkspaceId = targetWorkspace.Id;
        }

        if (!change.Moves.Any(move => move.SourceWorkspaceId == currentWorkspaceId
            || move.TargetWorkspaceId == currentWorkspaceId))
        {
            return;
        }

        var selectedIds = SelectedItems.Select(static item => item.Id).ToHashSet();
        var primaryItemId = SelectedItem?.Id;
        ApplyCommittedMoves(_allItems, currentWorkspaceId, change.Moves, committedItems);

        if (currentWorkspaceId != originalWorkspaceId)
        {
            _workspaceRuntimeCache.Remove(originalWorkspaceId);
            _currentWorkspaceRuntime = new WorkspaceRuntimeState(currentWorkspaceId, _allItems);
            _workspaceRuntimeCache[currentWorkspaceId] = _currentWorkspaceRuntime;
        }

        RefreshFolderChildCounts();
        ApplyVisibleItemsPreservingSelection(selectedIds, primaryItemId, reuseVisibleItems: true);
    }

    private static void ApplyCommittedMoves(
        List<WorkspaceItemViewModel> items,
        Guid workspaceId,
        IReadOnlyList<WorkspaceItemMove> moves,
        IReadOnlyDictionary<Guid, WorkspaceItem> committedItems)
    {
        foreach (var move in moves)
        {
            var existingItem = items.FirstOrDefault(item => item.Id == move.ItemId);
            if (move.TargetWorkspaceId == workspaceId)
            {
                if (!committedItems.TryGetValue(move.ItemId, out var committedItem))
                {
                    continue;
                }

                if (existingItem is null)
                {
                    items.Add(new WorkspaceItemViewModel(committedItem));
                }
                else
                {
                    existingItem.ApplyCommittedLocation(committedItem);
                }
            }
            else if (move.SourceWorkspaceId == workspaceId && existingItem is not null)
            {
                items.Remove(existingItem);
            }
        }
    }

    void IWorkspaceChangeListener.OnWorkspaceNameChanged(WorkspaceNameChangedChange change)
    {
        if (!_initialized
            || Workspaces.FirstOrDefault(workspace => workspace.Id == change.WorkspaceId) is not { } workspace)
        {
            return;
        }

        workspace.UpdateName(change.Name);
        if (CurrentWorkspace?.Id == change.WorkspaceId)
        {
            OnPropertyChanged(nameof(CurrentWorkspaceTitle));
        }
    }

    void IWorkspaceChangeListener.OnWorkspaceItemPositionsChanged(WorkspaceItemPositionsChangedChange change)
    {
        if (!_initialized)
        {
            return;
        }

        var matchingPositions = change.Positions
            .Where(position => _allItems.Any(item => item.Id == position.ItemId))
            .ToList();
        if (matchingPositions.Count == 0)
        {
            return;
        }

        ApplyPositions(matchingPositions);
        if (!_isApplyingArrangement)
        {
            RemoveArrangePositions(matchingPositions.Select(static position => position.ItemId));
        }
    }

    void IWorkspaceChangeListener.OnWorkspaceItemsChanged(WorkspaceItemsChangedChange change)
    {
        if (!_initialized)
        {
            return;
        }

        var selectedIds = SelectedItems.Select(static item => item.Id).ToHashSet();
        var primaryItemId = SelectedItem?.Id;
        var deletedItemIds = change.DeletedItemIds.ToHashSet();
        var upsertedItemsByWorkspace = change.UpsertedItems
            .GroupBy(static item => item.WorkspaceId)
            .ToDictionary(static group => group.Key, static group => group.ToList());
        var affectedCurrentWorkspace = false;
        foreach (var runtime in _workspaceRuntimeCache.Values)
        {
            var changed = false;
            if (deletedItemIds.Count > 0)
            {
                changed |= runtime.AllItems.RemoveAll(item => deletedItemIds.Contains(item.Id)) > 0;
            }

            if (upsertedItemsByWorkspace.TryGetValue(runtime.WorkspaceId, out var committedItems))
            {
                foreach (var committedItem in committedItems)
                {
                    var existing = runtime.AllItems.FirstOrDefault(item => item.Id == committedItem.Id);
                    if (existing is null)
                    {
                        runtime.AllItems.Add(new WorkspaceItemViewModel(committedItem));
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                continue;
            }

            runtime.MarkVisibleItemsDirty();
            affectedCurrentWorkspace |= runtime.WorkspaceId == CurrentWorkspace?.Id;
        }

        if (affectedCurrentWorkspace)
        {
            RefreshFolderChildCounts();
            if (_isViewActive)
            {
                ApplyVisibleItemsPreservingSelection(
                    selectedIds,
                    primaryItemId,
                    reuseVisibleItems: true);
            }
        }
    }

    void IWorkspaceChangeListener.OnWorkspaceItemContentChanged(WorkspaceItemContentChange change)
    {
        if (!_initialized)
        {
            return;
        }

        foreach (var runtime in _workspaceRuntimeCache.Values)
        {
            if (runtime.AllItems.FirstOrDefault(item => item.Id == change.ItemId) is not { } item)
            {
                continue;
            }

            switch (change)
            {
                case WorkspaceItemTitleChangedChange titleChange:
                    item.Title = titleChange.Title;
                    item.RefreshDisplayBindings();
                    break;
                case WorkspaceNoteChangedChange noteChange:
                    item.Title = noteChange.Title;
                    item.Note = noteChange.Content;
                    item.NoteFontSize = noteChange.FontSize;
                    item.RefreshDisplayBindings();
                    break;
                case WorkspaceItemFaviconChangedChange faviconChange:
                    item.SetFaviconPngBase64(faviconChange.FaviconPngBase64);
                    break;
                case WorkspaceItemThumbnailChangedChange thumbnailChange:
                    item.SetThumbnailPngBase64(
                        thumbnailChange.ThumbnailPngBase64,
                        thumbnailChange.ThumbnailUpdatedAt);
                    break;
                case WorkspaceSessionTabThumbnailsChangedChange sessionChange:
                    item.UpdateSessionTabThumbnails(sessionChange.Thumbnails);
                    break;
                case WorkspaceShellItemTargetChangedChange shellChange:
                    item.UpdateShellTarget(shellChange.TargetPath, shellChange.Kind);
                    item.SetFaviconPngBase64(string.Empty);
                    break;
            }
        }
    }

    private void ApplyVisibleItemsPreservingSelection(
        IReadOnlySet<Guid> selectedItemIds,
        Guid? primaryItemId,
        bool reuseVisibleItems = false)
    {
        ObservableCollection<WorkspaceItemViewModel> items;
        if (reuseVisibleItems && _currentWorkspaceRuntime is { } runtime)
        {
            runtime.RefreshVisibleItems();
            items = runtime.GetVisibleItems(CurrentFolderId);
        }
        else
        {
            items = new ObservableCollection<WorkspaceItemViewModel>(
                _allItems.Where(item => item.Item.ParentFolderId == CurrentFolderId));
            _currentWorkspaceRuntime?.ReplaceVisibleItems(CurrentFolderId, items);
        }

        SetItemsCollection(items);

        var selectedItems = Items.Where(item => selectedItemIds.Contains(item.Id)).ToList();
        var primaryItem = primaryItemId is { } id
            ? selectedItems.FirstOrDefault(item => item.Id == id)
            : null;
        SetSelectedItems(selectedItems, primaryItem);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CurrentLocationTitle));
        OnPropertyChanged(nameof(CurrentLocationSummary));
    }

    private int GetWorkspaceSortIndex(WorkspaceItem item)
    {
        return Workspaces.FirstOrDefault(x => x.Id == item.WorkspaceId)?.Index ?? int.MaxValue;
    }

    private string GetWorkspaceLabel(Guid workspaceId)
    {
        var workspace = Workspaces.FirstOrDefault(x => x.Id == workspaceId);
        return workspace is null
            ? "Workspace ?"
            : workspace.DisplayName;
    }

    private async Task ReloadWorkspaceAsync(object? parameter)
    {
        CancelQueuedWorkspaceWheelSwitch();
        ClearWorkspaceError();
        if (CurrentWorkspace is { } workspace)
        {
            await LoadWorkspaceAsync(
                workspace.Id,
                folderId: CurrentFolderId,
                selectedItemId: SelectedItem?.Id,
                forceReload: true);
            return;
        }

        _initialized = false;
        await InitializeAsync();
    }

    private void SetStatus(string text)
    {
        StatusText = text;
    }

    private void SetWorkspaceError(string text)
    {
        StatusText = text;
        WorkspaceErrorText = text;
        HasWorkspaceError = true;
    }

    private void ClearWorkspaceError()
    {
        WorkspaceErrorText = string.Empty;
        HasWorkspaceError = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record WorkspaceShellUndoState(
        ShellFileOperationResult Result,
        Guid ItemId,
        Guid WorkspaceId)
    {
        public bool FileUndone { get; set; }
        public ShellItemKind? ExpectedKind { get; set; }
    }

    private sealed class WorkspaceRuntimeState
    {
        private ObservableCollection<WorkspaceItemViewModel>? _rootItems;
        private readonly Dictionary<Guid, ObservableCollection<WorkspaceItemViewModel>> _folderItems = [];
        private bool _visibleItemsDirty;

        public WorkspaceRuntimeState(Guid workspaceId, List<WorkspaceItemViewModel> allItems)
        {
            WorkspaceId = workspaceId;
            AllItems = allItems;
        }

        public Guid WorkspaceId { get; }

        public List<WorkspaceItemViewModel> AllItems { get; }

        public ObservableCollection<WorkspaceItemViewModel> GetVisibleItems(Guid? folderId)
        {
            if (_visibleItemsDirty)
            {
                RefreshVisibleItems();
            }

            if (folderId is null)
            {
                return _rootItems ??= CreateVisibleItems(null);
            }

            if (_folderItems.TryGetValue(folderId.Value, out var items))
            {
                return items;
            }

            items = CreateVisibleItems(folderId);
            _folderItems[folderId.Value] = items;
            return items;
        }

        public void ReplaceVisibleItems(
            Guid? folderId,
            ObservableCollection<WorkspaceItemViewModel> items)
        {
            _visibleItemsDirty = false;
            _rootItems = null;
            _folderItems.Clear();
            if (folderId is null)
            {
                _rootItems = items;
            }
            else
            {
                _folderItems[folderId.Value] = items;
            }
        }

        public void MarkVisibleItemsDirty()
        {
            _visibleItemsDirty = true;
        }

        public void RefreshVisibleItems()
        {
            _visibleItemsDirty = false;
            // 移動だけでコレクションを置換すると、キャッシュしたWPF描画層まで再生成される。
            if (_rootItems is not null)
            {
                RefreshVisibleItems(_rootItems, null);
            }

            foreach (var (folderId, items) in _folderItems)
            {
                RefreshVisibleItems(items, folderId);
            }
        }

        private void RefreshVisibleItems(ObservableCollection<WorkspaceItemViewModel> items, Guid? folderId)
        {
            var desired = AllItems.Where(item => item.Item.ParentFolderId == folderId).ToList();
            var retained = desired.ToHashSet();
            for (var index = items.Count - 1; index >= 0; index--)
            {
                if (!retained.Contains(items[index]))
                {
                    items.RemoveAt(index);
                }
            }

            for (var index = 0; index < desired.Count; index++)
            {
                if (index < items.Count && ReferenceEquals(items[index], desired[index]))
                {
                    continue;
                }

                var previousIndex = items.IndexOf(desired[index]);
                if (previousIndex >= 0)
                {
                    items.Move(previousIndex, index);
                }
                else
                {
                    items.Insert(index, desired[index]);
                }
            }
        }

        private ObservableCollection<WorkspaceItemViewModel> CreateVisibleItems(Guid? folderId)
        {
            return new ObservableCollection<WorkspaceItemViewModel>(
                AllItems.Where(item => item.Item.ParentFolderId == folderId));
        }
    }
}

public sealed record WorkspaceCanvasPoint(double X, double Y);

public sealed record WorkspaceArrangeRequest(
    WorkspaceArrangeMode Mode,
    WorkspaceArrangeScope Scope,
    WorkspaceCanvasRect VisibleArea);

public sealed record WorkspaceCanvasRect(double X, double Y, double Width, double Height)
{
    public bool Intersects(double itemX, double itemY, double itemWidth, double itemHeight)
    {
        return Width > 0
            && Height > 0
            && itemWidth > 0
            && itemHeight > 0
            && X <= itemX + itemWidth
            && X + Width >= itemX
            && Y <= itemY + itemHeight
            && Y + Height >= itemY;
    }
}

public sealed record WorkspaceRenameRequest(WorkspaceItemViewModel Item, string NewTitle);

public sealed record WorkspaceNameChangeRequest(WorkspaceSlotViewModel Workspace, string NewName);

public sealed record WorkspaceNoteCreateRequest(
    WorkspaceCanvasPoint Point,
    string Content,
    double FontSize,
    Guid? ItemId = null);

public sealed record WorkspaceNoteEditRequest(
    WorkspaceItemViewModel Item,
    string Content,
    double FontSize);

public enum WorkspaceTextFileDropMode
{
    PlaceAsFile,
    ImportAsNote
}

internal sealed record WorkspaceShellDropPlanItem(
    string Path,
    WorkspaceCanvasPoint Point);

public sealed record WorkspaceShellDropRequest(
    IReadOnlyList<string> Paths,
    WorkspaceCanvasPoint Point,
    Guid? TargetFolderId,
    WorkspaceTextFileDropMode TextFileMode = WorkspaceTextFileDropMode.PlaceAsFile);

public sealed record WorkspaceShellPathRequest(WorkspaceItemViewModel Item, string Path);

public sealed record WorkspaceShellNameRequest(WorkspaceItemViewModel Item, string Name);
