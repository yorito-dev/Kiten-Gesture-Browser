using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

internal sealed record YouTubeMiniPlayerListElements(
    Popup YouTubeMiniPlayerListPopup,
    ListBox YouTubeMiniPlayerList,
    TextBlock YouTubeMiniPlayerListEmptyText,
    TextBlock YouTubeMiniPlayerListSummaryText,
    Button YouTubeMiniPlayerMuteAllToggleButton,
    Button YouTubeMiniPlayerMinimizeRestoreAllButton,
    Button YouTubeMiniPlayerGlobalActionsButton,
    Button YouTubeMiniPlayerArrangeMenuButton,
    Button YouTubeMiniPlayerRestoreArrangementButton,
    Button YouTubeMiniPlayerPlayPauseAllButton,
    Button YouTubeMiniPlayerRestoreClosedButton);

internal sealed record YouTubeMiniPlayerListOwnerSource(
    MainWindow Owner, IReadOnlyList<YouTubeMiniPlayerState> States);

internal sealed record YouTubeMiniPlayerListRuntimeSnapshot(
    YouTubeMiniPlayerDisplayHealthSnapshot? DisplayHealth,
    bool IsLocalVideoLoaded, bool HasLocalVideoLoadFailure, bool IsRuntimeLoaded,
    YouTubeMiniPlayerAudioFocusMode? FocusMode, bool IsFocusTarget, bool IsPreviewTarget);

internal interface IYouTubeMiniPlayerListHost
{
    bool IsClosingWindow { get; }
    bool IsPreviewActive { get; }
    Guid? PreviewTargetId { get; }
    bool HasArrangementPreview { get; }
    bool HasInitialArrangement { get; }
    bool HasRestorablePlayer { get; }
    bool IsRestoringClosedPlayer { get; }
    ModifierKeys Modifiers { get; }
    IReadOnlyList<YouTubeMiniPlayerListOwnerSource> GetOwnerSources();
    Task CaptureStatesAsync(MainWindow owner);
    YouTubeMiniPlayerListRuntimeSnapshot CaptureRuntimeSnapshot(YouTubeMiniPlayerListItem item);
    bool IsItemLoaded(YouTubeMiniPlayerListItem item);
    bool ShouldPlayAllLoadedPlayers();
    void RefreshLoadedWorkspaceOpenTabs();
    void UpdateAudioFocusBar();
    void UpdateWindowHighlights();
    void UpdateAudioIndicators();
    void CancelArrangementPreview(bool showStatus);
    void QueueSave();
    void RefreshOtherOpenLists();
    void SetStatus(string message);
    void RestoreAndBringToFront(YouTubeMiniPlayerListItem item);
    void CancelPendingHoverPreview();
    void ScheduleHoverPreview(Guid id);
    Task StopHoverPreviewAsync();
    bool TryGetCursorDevicePoint(out Point point);
    void DoDragDrop(DependencyObject source, IDataObject data, DragDropEffects effects);
}

internal sealed class YouTubeMiniPlayerListViewController
{
    private const string YouTubeMiniPlayerListDragDataFormat = "TabBrowser.YouTubeMiniPlayerListItem";
    private readonly ObservableCollection<YouTubeMiniPlayerListItem> _youTubeMiniPlayerListItems = [];
    private readonly YouTubeMiniPlayerThumbnailLoadCoordinator _youTubeMiniPlayerThumbnailLoadCoordinator = new();
    private readonly YouTubeMiniPlayerListDragCoordinator _youTubeMiniPlayerListDragCoordinator = new();
    private readonly YouTubeMiniPlayerListInteractionCoordinator _youTubeMiniPlayerListInteractionCoordinator = new();
    private readonly YouTubeMiniPlayerStateSyncCoordinator _youTubeMiniPlayerStateSyncCoordinator;
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerListRefreshTimer;
    private readonly YouTubeMiniPlayerListElements _elements;
    private readonly IYouTubeMiniPlayerListHost _host;
    private readonly Func<YouTubeMiniPlayerState, CancellationToken, Task<ImageSource?>> _loadThumbnail;
    private bool _isStopped;

    public YouTubeMiniPlayerListViewController(
        YouTubeMiniPlayerListElements elements,
        YouTubeMiniPlayerStateSyncCoordinator stateSync,
        OwnedDispatcherTimer refreshTimer,
        IYouTubeMiniPlayerListHost host,
        Func<YouTubeMiniPlayerState, CancellationToken, Task<ImageSource?>>? loadThumbnail = null)
    {
        _elements = elements;
        _youTubeMiniPlayerStateSyncCoordinator = stateSync;
        _youTubeMiniPlayerListRefreshTimer = refreshTimer;
        _host = host;
        _loadThumbnail = loadThumbnail ?? LoadThumbnailAsync;
    }

    public IReadOnlyList<YouTubeMiniPlayerListItem> Items => _youTubeMiniPlayerListItems;
    public YouTubeMiniPlayerListInteractionCoordinator Interaction => _youTubeMiniPlayerListInteractionCoordinator;
    private Popup YouTubeMiniPlayerListPopup => _elements.YouTubeMiniPlayerListPopup;
    private ListBox YouTubeMiniPlayerList => _elements.YouTubeMiniPlayerList;
    private TextBlock YouTubeMiniPlayerListEmptyText => _elements.YouTubeMiniPlayerListEmptyText;
    private TextBlock YouTubeMiniPlayerListSummaryText => _elements.YouTubeMiniPlayerListSummaryText;
    private Button YouTubeMiniPlayerMuteAllToggleButton => _elements.YouTubeMiniPlayerMuteAllToggleButton;
    private Button YouTubeMiniPlayerMinimizeRestoreAllButton => _elements.YouTubeMiniPlayerMinimizeRestoreAllButton;
    private Button YouTubeMiniPlayerGlobalActionsButton => _elements.YouTubeMiniPlayerGlobalActionsButton;
    private Button YouTubeMiniPlayerArrangeMenuButton => _elements.YouTubeMiniPlayerArrangeMenuButton;
    private Button YouTubeMiniPlayerRestoreArrangementButton => _elements.YouTubeMiniPlayerRestoreArrangementButton;
    private Button YouTubeMiniPlayerPlayPauseAllButton => _elements.YouTubeMiniPlayerPlayPauseAllButton;
    private Button YouTubeMiniPlayerRestoreClosedButton => _elements.YouTubeMiniPlayerRestoreClosedButton;

    public void InitializeItems() => YouTubeMiniPlayerList.ItemsSource = _youTubeMiniPlayerListItems;

    public void InitializeRefreshTimer()
    {
        _youTubeMiniPlayerListRefreshTimer.Interval = TimeSpan.FromSeconds(1);
        _youTubeMiniPlayerListRefreshTimer.Tick += RefreshTimer_Tick;
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
        => await RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);

    public void StartRefreshTimer()
    {
        if (!_isStopped) _youTubeMiniPlayerListRefreshTimer.Start();
    }

    public void StopRefreshTimer() => _youTubeMiniPlayerListRefreshTimer.Stop();

    public void StopRefreshTracking()
    {
        _isStopped = true;
        _youTubeMiniPlayerListRefreshTimer.Stop();
        _youTubeMiniPlayerListRefreshTimer.Tick -= RefreshTimer_Tick;
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(
        YouTubeMiniPlayerState state, CancellationToken cancellationToken)
        => state.SourceKind == MiniPlayerSourceKind.LocalFile
            ? await WorkspaceShellItemService.GetMediaThumbnailImageAsync(
                state.LocalFilePath, 176, 100, cancellationToken)
            : await YouTubeThumbnailImageService.Shared.GetAsync(state.Url, cancellationToken);

    public async Task RefreshYouTubeMiniPlayerListAsync(bool capturePlaybackState)
    {
        if ((_isStopped || _host.IsClosingWindow)
            || _youTubeMiniPlayerStateSyncCoordinator.IsListRefreshRunning
            || _youTubeMiniPlayerListDragCoordinator.IsDragActive)
        {
            return;
        }

        if (_host.IsPreviewActive)
        {
            _host.UpdateAudioIndicators();
            return;
        }

        if (!_youTubeMiniPlayerStateSyncCoordinator.TryEnterListRefresh(out var listRefreshLease))
        {
            return;
        }

        try
        {
            var windows = _host.GetOwnerSources();
            if (capturePlaybackState && !_host.IsPreviewActive)
            {
                foreach (var window in windows)
                {
                    await _host.CaptureStatesAsync(window.Owner);
                    if (_isStopped || _host.IsClosingWindow)
                    {
                        return;
                    }
                }

                windows = _host.GetOwnerSources();
            }

            var selectedIds = YouTubeMiniPlayerList.SelectedItems
                .OfType<YouTubeMiniPlayerListItem>()
                .Select(static item => item.State.Id)
                .ToHashSet();
            var items = SynchronizeYouTubeMiniPlayerListItems(windows);
            var workspaceShelfSignature = string.Join(
                '|',
                items
                    .Where(static item => item.State.SourceKind == MiniPlayerSourceKind.YouTube)
                    .Select(static item => item.State.Id)
                    .OrderBy(static id => id));
            if (_youTubeMiniPlayerStateSyncCoordinator.UpdateWorkspaceShelfSignature(
                    workspaceShelfSignature))
            {
                _host.RefreshLoadedWorkspaceOpenTabs();
            }

            foreach (var item in items.Where(item =>
                         selectedIds.Contains(item.State.Id)
                         && !YouTubeMiniPlayerList.SelectedItems.Contains(item)))
            {
                YouTubeMiniPlayerList.SelectedItems.Add(item);
            }

            YouTubeMiniPlayerListEmptyText.Visibility = items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            YouTubeMiniPlayerList.Visibility = items.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateYouTubeMiniPlayerListToolbarState(items);
            _host.UpdateAudioFocusBar();
            _host.UpdateWindowHighlights();
            _host.UpdateAudioIndicators();
        }
        finally
        {
            listRefreshLease!.Dispose();
        }
    }

    private IReadOnlyList<YouTubeMiniPlayerListItem> SynchronizeYouTubeMiniPlayerListItems(
        IReadOnlyList<YouTubeMiniPlayerListOwnerSource> windows)
    {
        var desiredItems = windows
            .SelectMany(window => window.States.Select(state => (Owner: window.Owner, State: state)))
            .OrderBy(static item => item.State.ListOrder ?? int.MaxValue)
            .ThenBy(static item => item.State.CreatedAt)
            .ToList();

        for (var targetIndex = 0; targetIndex < desiredItems.Count; targetIndex++)
        {
            var desired = desiredItems[targetIndex];
            var currentIndex = -1;
            for (var index = targetIndex; index < _youTubeMiniPlayerListItems.Count; index++)
            {
                if (_youTubeMiniPlayerListItems[index].State.Id == desired.State.Id)
                {
                    currentIndex = index;
                    break;
                }
            }

            YouTubeMiniPlayerListItem item;
            if (currentIndex >= 0
                && ReferenceEquals(_youTubeMiniPlayerListItems[currentIndex].Owner, desired.Owner)
                && ReferenceEquals(_youTubeMiniPlayerListItems[currentIndex].State, desired.State))
            {
                if (currentIndex != targetIndex)
                {
                    _youTubeMiniPlayerListItems.Move(currentIndex, targetIndex);
                }

                item = _youTubeMiniPlayerListItems[targetIndex];
            }
            else
            {
                if (currentIndex >= 0)
                {
                    _youTubeMiniPlayerListItems.RemoveAt(currentIndex);
                }

                item = new YouTubeMiniPlayerListItem(desired.Owner, desired.State);
                _youTubeMiniPlayerListItems.Insert(targetIndex, item);
            }

            var runtime = _host.CaptureRuntimeSnapshot(item);
            item.UpdateRuntimeState(
                runtime.DisplayHealth, runtime.IsLocalVideoLoaded, runtime.HasLocalVideoLoadFailure,
                runtime.IsRuntimeLoaded, runtime.FocusMode, runtime.IsFocusTarget, runtime.IsPreviewTarget);
        }

        while (_youTubeMiniPlayerListItems.Count > desiredItems.Count)
        {
            _youTubeMiniPlayerListItems.RemoveAt(_youTubeMiniPlayerListItems.Count - 1);
        }

        if (YouTubeMiniPlayerListPopup.IsOpen
            && _youTubeMiniPlayerThumbnailLoadCoordinator.TryGetCurrentBatchToken(
                out var cancellationToken))
        {
            foreach (var item in _youTubeMiniPlayerListItems)
            {
                _ = EnsureYouTubeMiniPlayerListThumbnailAsync(item, cancellationToken);
            }
        }

        return _youTubeMiniPlayerListItems.ToList();
    }

    public void BeginYouTubeMiniPlayerThumbnailLoads()
    {
        CancelYouTubeMiniPlayerThumbnailLoads();
        var cancellationToken = _youTubeMiniPlayerThumbnailLoadCoordinator.BeginBatch();
        foreach (var item in _youTubeMiniPlayerListItems)
        {
            item.AllowThumbnailRetry();
            _ = EnsureYouTubeMiniPlayerListThumbnailAsync(item, cancellationToken);
        }
    }

    public void CancelYouTubeMiniPlayerThumbnailLoads()
    {
        _youTubeMiniPlayerThumbnailLoadCoordinator.CancelBatch();
        foreach (var item in _youTubeMiniPlayerListItems)
        {
            item.CancelThumbnailLoad();
        }
    }

    private async Task EnsureYouTubeMiniPlayerListThumbnailAsync(
        YouTubeMiniPlayerListItem item,
        CancellationToken cancellationToken)
    {
        if (!item.TryBeginThumbnailLoad(out var sourceKey, out var generation))
        {
            return;
        }

        try
        {
            using var thumbnailLoadLease =
                await _youTubeMiniPlayerThumbnailLoadCoordinator.EnterAsync(cancellationToken);
            var thumbnail = await _loadThumbnail(item.State, cancellationToken);
            item.CompleteThumbnailLoad(sourceKey, generation, thumbnail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.CancelThumbnailLoad(sourceKey, generation);
        }
        catch
        {
            item.CompleteThumbnailLoad(sourceKey, generation, null);
        }
    }

    public void YouTubeMiniPlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host.HasArrangementPreview
            && !_youTubeMiniPlayerStateSyncCoordinator.IsListRefreshRunning)
        {
            _host.CancelArrangementPreview(showStatus: true);
        }

        _host.UpdateAudioFocusBar();
        _host.UpdateWindowHighlights();
    }

    public void YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        ResetYouTubeMiniPlayerListDragState();
        if (sender is not ListBoxItem { DataContext: YouTubeMiniPlayerListItem listItem } item
            || e.ClickCount > 1
            || _host.Modifiers != ModifierKeys.None
            || FindYouTubeMiniPlayerListVisualParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var startPoint = e.GetPosition(YouTubeMiniPlayerList);
        _youTubeMiniPlayerListDragCoordinator.BeginCandidate(
            listItem.State.Id,
            item.IsSelected,
            startPoint.X,
            startPoint.Y);
    }

    public void YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: YouTubeMiniPlayerListItem listItem } item)
        {
            ResetYouTubeMiniPlayerListDragState();
            return;
        }

        var candidate = _youTubeMiniPlayerListDragCoordinator.CaptureCandidate();
        var shouldDeselect = !_youTubeMiniPlayerListDragCoordinator.IsDragActive
            && candidate.SourceWasSelected
            && candidate.SourceId == listItem.State.Id
            && e.ClickCount <= 1
            && _host.Modifiers == ModifierKeys.None
            && FindYouTubeMiniPlayerListVisualParent<ButtonBase>(e.OriginalSource as DependencyObject) is null;
        ResetYouTubeMiniPlayerListDragState();
        if (!shouldDeselect)
        {
            return;
        }

        item.IsSelected = false;
        e.Handled = true;
    }

    public void YouTubeMiniPlayerList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var candidate = _youTubeMiniPlayerListDragCoordinator.CaptureCandidate();
        if (_youTubeMiniPlayerListDragCoordinator.IsDragActive
            || e.LeftButton != MouseButtonState.Pressed
            || candidate.SourceId is not { } sourceId)
        {
            return;
        }

        var currentPoint = e.GetPosition(YouTubeMiniPlayerList);
        if (!_youTubeMiniPlayerListDragCoordinator.HasExceededDragThreshold(
                currentPoint.X,
                currentPoint.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        StartYouTubeMiniPlayerListDrag(sourceId, e);
    }

    private void StartYouTubeMiniPlayerListDrag(Guid sourceId, MouseEventArgs e)
    {
        var sourceItem = _youTubeMiniPlayerListItems.FirstOrDefault(item => item.State.Id == sourceId);
        var sourceContainer = sourceItem is null
            ? null
            : YouTubeMiniPlayerList.ItemContainerGenerator.ContainerFromItem(sourceItem) as ListBoxItem;
        if (sourceContainer is null)
        {
            ResetYouTubeMiniPlayerListDragState();
            return;
        }

        _youTubeMiniPlayerListDragCoordinator.BeginDrag();
        _host.CancelPendingHoverPreview();
        ClearYouTubeMiniPlayerListDropCue();
        e.Handled = true;
        try
        {
            var data = new DataObject(
                YouTubeMiniPlayerListDragDataFormat,
                sourceId.ToString("D"));
            _host.DoDragDrop(sourceContainer, data, DragDropEffects.Move);
        }
        finally
        {
            _youTubeMiniPlayerListDragCoordinator.EndDrag();
            ResetYouTubeMiniPlayerListDragState();
            ClearYouTubeMiniPlayerListDropCue();
        }
    }

    public void YouTubeMiniPlayerList_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetYouTubeMiniPlayerListDragSourceId(e.Data, out var sourceId)
            || _youTubeMiniPlayerListItems.All(item => item.State.Id != sourceId))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearYouTubeMiniPlayerListDropCue();
            return;
        }

        var targetContainer = FindYouTubeMiniPlayerListVisualParent<ListBoxItem>(
            e.OriginalSource as DependencyObject);
        var targetItem = targetContainer?.DataContext as YouTubeMiniPlayerListItem;
        if (targetItem?.State.Id == sourceId)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearYouTubeMiniPlayerListDropCue();
            return;
        }

        var insertAfter = targetContainer is not null
            && e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
        SetYouTubeMiniPlayerListDropCue(targetItem, insertAfter);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    public void YouTubeMiniPlayerList_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetYouTubeMiniPlayerListDragSourceId(e.Data, out var sourceId))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearYouTubeMiniPlayerListDropCue();
            return;
        }

        var targetContainer = FindYouTubeMiniPlayerListVisualParent<ListBoxItem>(
            e.OriginalSource as DependencyObject);
        var targetItem = targetContainer?.DataContext as YouTubeMiniPlayerListItem;
        var insertAfter = targetContainer is not null
            && e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
        var moved = MoveYouTubeMiniPlayerListItem(
            sourceId,
            targetItem?.State.Id,
            insertAfter);
        ClearYouTubeMiniPlayerListDropCue();
        e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    public bool MoveYouTubeMiniPlayerListItem(
        Guid sourceId,
        Guid? targetId,
        bool insertAfter)
    {
        var currentOrder = _youTubeMiniPlayerListItems
            .Select(static item => item.State.Id)
            .ToArray();
        if (!_youTubeMiniPlayerListDragCoordinator.TryCreateReorderPlan(
                currentOrder,
                sourceId,
                targetId,
                insertAfter,
                out var reorderedIds))
        {
            return false;
        }

        var orderedItems = reorderedIds
            .Select(id => _youTubeMiniPlayerListItems.First(item => item.State.Id == id))
            .ToArray();

        for (var index = 0; index < orderedItems.Length; index++)
        {
            orderedItems[index].State.ListOrder = index;
            var currentIndex = _youTubeMiniPlayerListItems.IndexOf(orderedItems[index]);
            if (currentIndex != index)
            {
                _youTubeMiniPlayerListItems.Move(currentIndex, index);
            }
        }

        _host.QueueSave();
        _host.RefreshOtherOpenLists();
        _host.SetStatus("YouTubeミニプレイヤー一覧の順序を変更しました");
        return true;
    }

    private static bool TryGetYouTubeMiniPlayerListDragSourceId(
        IDataObject data,
        out Guid sourceId)
    {
        sourceId = Guid.Empty;
        return data.GetDataPresent(YouTubeMiniPlayerListDragDataFormat)
            && data.GetData(YouTubeMiniPlayerListDragDataFormat) is string value
            && Guid.TryParse(value, out sourceId);
    }

    public void SetYouTubeMiniPlayerListDropCue(
        YouTubeMiniPlayerListItem? targetItem,
        bool insertAfter)
    {
        if (!_youTubeMiniPlayerListDragCoordinator.TryReplaceDropCue(
                targetItem?.State.Id,
                insertAfter,
                out var previous))
        {
            return;
        }

        ClearYouTubeMiniPlayerListDropCueVisual(previous);
        targetItem?.SetDropCue(!insertAfter, insertAfter);
    }

    public void ClearYouTubeMiniPlayerListDropCue()
    {
        ClearYouTubeMiniPlayerListDropCueVisual(
            _youTubeMiniPlayerListDragCoordinator.ClearDropCue());
    }

    private void ClearYouTubeMiniPlayerListDropCueVisual(
        YouTubeMiniPlayerDropCueSnapshot dropCue)
    {
        if (dropCue.TargetId is { } targetId)
        {
            _youTubeMiniPlayerListItems
                .FirstOrDefault(item => item.State.Id == targetId)?
                .SetDropCue(isBefore: false, isAfter: false);
        }
    }

    public void ResetYouTubeMiniPlayerListDragState()
    {
        _youTubeMiniPlayerListDragCoordinator.ResetCandidate();
    }

    public void YouTubeMiniPlayerListItem_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not YouTubeMiniPlayerListItem item
            || FindYouTubeMiniPlayerListVisualParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _host.RestoreAndBringToFront(item);
        e.Handled = true;
    }

    public void YouTubeMiniPlayerListItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        _youTubeMiniPlayerListInteractionCoordinator.SetHoveredItem(item.State.Id);
        _host.ScheduleHoverPreview(item.State.Id);
        _host.UpdateWindowHighlights();
    }

    public void YouTubeMiniPlayerListItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem itemContainer
            && itemContainer.DataContext is YouTubeMiniPlayerListItem item
            && _youTubeMiniPlayerListInteractionCoordinator.IsHoveredItem(item.State.Id))
        {
            StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft(itemContainer);
        }
    }

    public void StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft(
        ListBoxItem? itemContainer = null)
    {
        if (_youTubeMiniPlayerListInteractionCoordinator.HoveredItemId is not { } targetId)
        {
            return;
        }

        itemContainer ??= _youTubeMiniPlayerListItems
            .FirstOrDefault(item => item.State.Id == targetId) is { } hoveredItem
                ? YouTubeMiniPlayerList.ItemContainerGenerator.ContainerFromItem(hoveredItem) as ListBoxItem
                : null;
        if (itemContainer is not null
            && IsCursorWithinYouTubeMiniPlayerListItem(itemContainer))
        {
            return;
        }

        _youTubeMiniPlayerListInteractionCoordinator.ClearHoveredItem();
        _host.CancelPendingHoverPreview();
        if (_host.PreviewTargetId == targetId)
        {
            _ = _host.StopHoverPreviewAsync();
        }

        _host.UpdateWindowHighlights();
    }

    private bool IsCursorWithinYouTubeMiniPlayerListItem(FrameworkElement itemContainer)
    {
        if (!itemContainer.IsLoaded
            || !itemContainer.IsVisible
            || itemContainer.ActualWidth <= 0
            || itemContainer.ActualHeight <= 0
            || !_host.TryGetCursorDevicePoint(out var cursorPoint))
        {
            return false;
        }

        try
        {
            var itemPoint = itemContainer.PointFromScreen(cursorPoint);
            return itemPoint.X >= 0
                && itemPoint.Y >= 0
                && itemPoint.X <= itemContainer.ActualWidth
                && itemPoint.Y <= itemContainer.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void UpdateYouTubeMiniPlayerListToolbarState(
        IReadOnlyList<YouTubeMiniPlayerListItem> items)
    {
        var states = items.Select(static item => item.State).ToList();
        var hasPlayers = states.Count > 0;
        var allMuted = hasPlayers && states.All(static state => state.IsMuted);
        var allMinimized = hasPlayers && states.All(static state => state.IsMinimized);
        var loadedItems = items.Where(_host.IsItemLoaded).ToList();
        var playingCount = loadedItems.Count(static item => item.State.IsPaused == false);
        var audioCount = loadedItems.Count(static item =>
            item.State.IsPaused == false && !item.State.IsMuted);

        YouTubeMiniPlayerListSummaryText.Text =
            $"YTミニプレイヤー  {states.Count}件｜再生{playingCount}｜音声{audioCount}  ⋮⋮";
        YouTubeMiniPlayerMuteAllToggleButton.Content = allMuted
            ? "全ミュート解除"
            : "全ミュート";
        YouTubeMiniPlayerMuteAllToggleButton.ToolTip = allMuted
            ? "すべてのミニプレイヤーのミュートを解除"
            : "すべてのミニプレイヤーをミュート";
        YouTubeMiniPlayerMuteAllToggleButton.IsEnabled = hasPlayers;
        YouTubeMiniPlayerMinimizeRestoreAllButton.Content = allMinimized
            ? "全復帰"
            : "全最小";
        YouTubeMiniPlayerMinimizeRestoreAllButton.ToolTip = allMinimized
            ? "すべてのミニプレイヤーを復帰"
            : "すべてのミニプレイヤーを最小化";
        YouTubeMiniPlayerMinimizeRestoreAllButton.IsEnabled = hasPlayers;
        YouTubeMiniPlayerGlobalActionsButton.IsEnabled = hasPlayers;
        YouTubeMiniPlayerArrangeMenuButton.IsEnabled = hasPlayers;
        YouTubeMiniPlayerRestoreArrangementButton.Visibility =
            _host.HasInitialArrangement
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateYouTubeMiniPlayerPlayPauseAllButton();
        YouTubeMiniPlayerPlayPauseAllButton.IsEnabled = loadedItems.Count > 0;
        UpdateYouTubeMiniPlayerRestoreClosedButtonState();
    }

    public void UpdateYouTubeMiniPlayerPlayPauseAllButton()
    {
        var shouldPlayAll = _host.ShouldPlayAllLoadedPlayers();
        YouTubeMiniPlayerPlayPauseAllButton.Content = shouldPlayAll ? "全再生" : "全一時停止";
        YouTubeMiniPlayerPlayPauseAllButton.ToolTip = shouldPlayAll
            ? "読み込み済みのミニプレイヤーをすべて再生"
            : "読み込み済みのミニプレイヤーをすべて一時停止";
    }

    public void UpdateYouTubeMiniPlayerRestoreClosedButtonState()
    {
        var hasRestorablePlayer = _host.HasRestorablePlayer;
        YouTubeMiniPlayerRestoreClosedButton.Visibility = hasRestorablePlayer
            ? Visibility.Visible
            : Visibility.Collapsed;
        YouTubeMiniPlayerRestoreClosedButton.IsEnabled =
            hasRestorablePlayer && !_host.IsRestoringClosedPlayer;
    }

    public static T? FindYouTubeMiniPlayerListVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T result)
            {
                return result;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    public void ClearYouTubeMiniPlayerListWindowHighlights()
    {
        _youTubeMiniPlayerListInteractionCoordinator.ClearHoveredItem();
        _host.UpdateWindowHighlights();
    }
}
