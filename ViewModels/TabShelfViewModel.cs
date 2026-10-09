using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Data;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class TabShelfViewModel : INotifyPropertyChanged
{
    private readonly WorkspaceService _workspaceService;
    private readonly WorkspaceTabPort _tabs;
    private readonly WorkspaceMiniPlayerPort _miniPlayers;
    private readonly Func<Workspace?> _getCurrentWorkspace;
    private readonly Func<Guid?> _getCurrentFolderId;
    private readonly Func<WorkspaceCanvasPoint> _getNextCanvasPoint;
    private readonly Action<WorkspaceItem> _addSavedItemToCanvas;
    private readonly Action<string> _setStatus;
    private bool _isSaving;

    public TabShelfViewModel(
        WorkspaceService workspaceService,
        WorkspaceTabPort tabs,
        WorkspaceMiniPlayerPort miniPlayers,
        Func<Workspace?> getCurrentWorkspace,
        Func<Guid?> getCurrentFolderId,
        Func<WorkspaceCanvasPoint> getNextCanvasPoint,
        Action<WorkspaceItem> addSavedItemToCanvas,
        Action<string> setStatus)
    {
        _workspaceService = workspaceService;
        _tabs = tabs;
        _miniPlayers = miniPlayers;
        _getCurrentWorkspace = getCurrentWorkspace;
        _getCurrentFolderId = getCurrentFolderId;
        _getNextCanvasPoint = getNextCanvasPoint;
        _addSavedItemToCanvas = addSavedItemToCanvas;
        _setStatus = setStatus;
        OpenTabsView = CollectionViewSource.GetDefaultView(OpenTabs);
        OpenTabsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TabShelfTabViewModel.WindowGroupTitle)));

        SaveCurrentTabCommand = new AsyncRelayCommand(_ => SaveCurrentTabAsync());
        SaveSelectedTabsCommand = new AsyncRelayCommand(_ => SaveSelectedTabsAsync());
        SaveAllTabsCommand = new AsyncRelayCommand(_ => SaveAllTabsAsync());
        SaveAndCloseCurrentTabCommand = new AsyncRelayCommand(_ => SaveAndCloseCurrentTabAsync());
        CloseItemCommand = new AsyncRelayCommand(
            CloseItemAsync,
            parameter => parameter is TabShelfTabViewModel item && OpenTabs.Contains(item));
        RefreshTabsCommand = new RelayCommand(_ => RefreshTabs());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TabShelfTabViewModel> OpenTabs { get; } = [];

    public ICollectionView OpenTabsView { get; }

    public IReadOnlyList<TabShelfTabViewModel> SelectedTabs => OpenTabs
        .Where(static x => x.IsSelected && !x.IsYouTubeMiniPlayer)
        .ToList();

    public IReadOnlyList<TabShelfTabViewModel> SelectedYouTubeMiniPlayers => OpenTabs
        .Where(static x => x.IsSelected && x.IsYouTubeMiniPlayer)
        .ToList();

    public ICommand SaveCurrentTabCommand { get; }

    public ICommand SaveSelectedTabsCommand { get; }

    public ICommand SaveAllTabsCommand { get; }

    public ICommand SaveAndCloseCurrentTabCommand { get; }

    public ICommand CloseItemCommand { get; }

    public ICommand RefreshTabsCommand { get; }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (_isSaving == value)
            {
                return;
            }

            _isSaving = value;
            OnPropertyChanged();
        }
    }

    public bool HasOpenTabs => OpenTabs.Count > 0;

    public void RefreshTabs()
    {
        var targetIndex = 0;
        var groupingChanged = false;
        foreach (var snapshot in _tabs.CaptureOpenTabs())
        {
            groupingChanged |= SynchronizeTab(targetIndex, snapshot);
            targetIndex++;
        }

        foreach (var playerState in _miniPlayers.Capture())
        {
            SynchronizePlayer(targetIndex, playerState);
            targetIndex++;
        }

        while (OpenTabs.Count > targetIndex)
        {
            var removed = OpenTabs[^1];
            removed.PropertyChanged -= Tab_PropertyChanged;
            OpenTabs.RemoveAt(OpenTabs.Count - 1);
        }

        if (groupingChanged)
        {
            OpenTabsView.Refresh();
        }

        OnPropertyChanged(nameof(HasOpenTabs));
        OnPropertyChanged(nameof(SelectedTabs));
        OnPropertyChanged(nameof(SelectedYouTubeMiniPlayers));
    }

    private bool SynchronizeTab(int targetIndex, CapturedTabSnapshot snapshot)
    {
        var currentIndex = FindItemIndex(targetIndex, snapshot.TabId, isYouTubeMiniPlayer: false);
        if (currentIndex < 0)
        {
            var item = new TabShelfTabViewModel(snapshot);
            item.PropertyChanged += Tab_PropertyChanged;
            OpenTabs.Insert(targetIndex, item);
            return false;
        }

        if (currentIndex != targetIndex)
        {
            OpenTabs.Move(currentIndex, targetIndex);
        }

        return OpenTabs[targetIndex].Update(snapshot);
    }

    private void SynchronizePlayer(int targetIndex, YouTubeMiniPlayerWorkspaceState playerState)
    {
        var currentIndex = FindItemIndex(targetIndex, playerState.RuntimeId, isYouTubeMiniPlayer: true);
        if (currentIndex < 0)
        {
            var item = new TabShelfTabViewModel(playerState);
            item.PropertyChanged += Tab_PropertyChanged;
            OpenTabs.Insert(targetIndex, item);
            return;
        }

        if (currentIndex != targetIndex)
        {
            OpenTabs.Move(currentIndex, targetIndex);
        }

        OpenTabs[targetIndex].Update(playerState);
    }

    private int FindItemIndex(int startIndex, Guid tabId, bool isYouTubeMiniPlayer)
    {
        for (var index = startIndex; index < OpenTabs.Count; index++)
        {
            var item = OpenTabs[index];
            if (item.TabId == tabId && item.IsYouTubeMiniPlayer == isYouTubeMiniPlayer)
            {
                return index;
            }
        }

        return -1;
    }

    public async Task<TabShelfDragPayload> CreateDragPayloadAsync(TabShelfTabViewModel draggedItem)
    {
        if (draggedItem.IsYouTubeMiniPlayer)
        {
            var selectedIds = draggedItem.IsSelected
                ? SelectedYouTubeMiniPlayers.Select(static item => item.TabId).ToHashSet()
                : [draggedItem.TabId];
            var players = (await _miniPlayers.CaptureCurrentAsync())
                .Where(player => selectedIds.Contains(player.RuntimeId))
                .ToList();
            return new TabShelfDragPayload([], players);
        }

        var available = _tabs.CaptureOpenTabs();
        var selected = SelectedTabs
            .Select(static x => x.Snapshot!)
            .OrderBy(static x => x.WindowSortOrder)
            .ThenBy(static x => x.TabIndex)
            .ToList();

        if (draggedItem.IsSelected && selected.Any(x => x.TabId == draggedItem.TabId))
        {
            return new TabShelfDragPayload(
                WorkspaceTabSavePlanner.ExpandSplitPartners(selected, available),
                [],
                draggedItem.TabId);
        }

        return draggedItem.Snapshot is { } snapshot
            ? new TabShelfDragPayload(
                WorkspaceTabSavePlanner.ExpandSplitPartners([snapshot], available),
                [],
                draggedItem.TabId)
            : new TabShelfDragPayload([], []);
    }

    private async Task SaveCurrentTabAsync()
    {
        var snapshot = _tabs.CaptureCurrentTab();
        if (snapshot is null)
        {
            _setStatus("保存できる現在タブがありません");
            return;
        }

        var snapshots = WorkspaceTabSavePlanner.ExpandSplitPartners(
            [snapshot],
            _tabs.CaptureOpenTabs());
        await SaveSnapshotsAsync(snapshots, "現在タブ");
        RefreshTabs();
    }

    private async Task SaveSelectedTabsAsync()
    {
        var selected = WorkspaceTabSavePlanner.ExpandSplitPartners(
            SelectedTabs
            .Select(static x => x.Snapshot)
            .OfType<CapturedTabSnapshot>()
            .OrderBy(static x => x.WindowSortOrder)
            .ThenBy(static x => x.TabIndex)
            .ToList(),
            _tabs.CaptureOpenTabs());

        if (selected.Count == 0)
        {
            _setStatus("保存するタブを選択してください");
            return;
        }

        await SaveSnapshotsAsync(selected, "選択タブ");

        RefreshTabs();
    }

    private async Task SaveAllTabsAsync()
    {
        var tabs = _tabs.CaptureOpenTabs()
            .OrderBy(static x => x.WindowSortOrder)
            .ThenBy(static x => x.TabIndex)
            .ToList();

        if (tabs.Count == 0)
        {
            _setStatus("保存できるタブがありません");
            return;
        }

        await SaveSnapshotsAsync(tabs, "全タブ");

        RefreshTabs();
    }

    private async Task SaveAndCloseCurrentTabAsync()
    {
        var snapshot = _tabs.CaptureCurrentTab();
        if (snapshot is null)
        {
            _setStatus("保存して閉じる対象のタブがありません");
            return;
        }

        var snapshots = WorkspaceTabSavePlanner.ExpandSplitPartners(
            [snapshot],
            _tabs.CaptureOpenTabs());
        var saved = await SaveSnapshotsAsync(snapshots, "現在タブ");
        if (!saved)
        {
            return;
        }

        if (!WorkspaceTabSavePlanner.MatchesSavedTargets(snapshots, _tabs.CaptureOpenTabs()))
        {
            _setStatus("保存後に対象タブの状態が変わったため、タブは閉じませんでした。保存内容は保持されています");
            return;
        }

        await _tabs.CloseTabAsync(snapshot.TabId);
        RefreshTabs();
    }

    private async Task CloseItemAsync(object? parameter)
    {
        if (parameter is not TabShelfTabViewModel item
            || item.TabId == Guid.Empty
            || !OpenTabs.Contains(item))
        {
            return;
        }

        await _tabs.CloseTabAsync(item.TabId);
        RefreshTabs();
    }

    private Task<bool> SaveSnapshotsAsync(
        IReadOnlyList<CapturedTabSnapshot> snapshots,
        string groupTitle)
    {
        return snapshots.Count == 1
            ? SaveSingleTabAsync(snapshots[0])
            : SaveSessionGroupAsync(snapshots, groupTitle);
    }

    private async Task<bool> SaveSingleTabAsync(CapturedTabSnapshot snapshot)
    {
        if (snapshot.IsFolderTab)
        {
            return await SaveFolderTabsAsync([snapshot]);
        }

        var workspace = GetCurrentWorkspaceOrReport();
        if (workspace is null)
        {
            return false;
        }

        return await SaveWithStateAsync(async () =>
        {
            var point = _getNextCanvasPoint();
            var title = string.IsNullOrWhiteSpace(snapshot.Title)
                ? snapshot.Url
                : snapshot.Title;
            var item = await _workspaceService.AddBookmarkAsync(
                workspace.Id,
                title,
                snapshot.Url,
                point.X,
                point.Y,
                _getCurrentFolderId(),
                note: $"Tab {snapshot.TabIndex + 1}",
                faviconPngBase64: snapshot.FaviconPngBase64,
                thumbnailPngBase64: snapshot.CreateThumbnailPngBase64(),
                containerProfileId: snapshot.ContainerProfileId);
            _addSavedItemToCanvas(item);
            _setStatus($"現在タブを保存しました: {title}");
        });
    }

    private async Task<bool> SaveFolderTabsAsync(IReadOnlyList<CapturedTabSnapshot> snapshots)
    {
        var workspace = GetCurrentWorkspaceOrReport();
        if (workspace is null || snapshots.Count == 0)
        {
            return false;
        }

        return await SaveWithStateAsync(async () =>
        {
            var start = _getNextCanvasPoint();
            var folderId = _getCurrentFolderId();
            var items = snapshots
                .Select((snapshot, index) => new ShellItem
                {
                    WorkspaceId = workspace.Id,
                    ParentFolderId = folderId,
                    Title = string.IsNullOrWhiteSpace(snapshot.Title)
                        ? WorkspaceShellItemService.GetDisplayName(snapshot.Url)
                        : snapshot.Title,
                    TargetPath = snapshot.Url,
                    Kind = ShellItemKind.Directory,
                    OpenInFolderTab = true,
                    ContainerProfileId = snapshot.ContainerProfileId,
                    X = start.X + (index * 104),
                    Y = start.Y + (index * 40)
                })
                .ToList();
            var savedItems = await _workspaceService.AddShellItemsAsync(items);
            foreach (var item in savedItems)
            {
                _addSavedItemToCanvas(item);
            }

            _setStatus($"フォルダタブを保存しました: {savedItems.Count} 件");
        });
    }

    private async Task<bool> SaveSessionGroupAsync(IReadOnlyList<CapturedTabSnapshot> snapshots, string groupTitle)
    {
        var plan = WorkspaceTabSavePlanner.CreatePlan(snapshots);
        if (plan.StandaloneFolderTabs.Count > 0 && plan.SessionTabs.Count > 0)
        {
            var targetWorkspace = GetCurrentWorkspaceOrReport();
            if (targetWorkspace is null) return false;
            return await SaveWithStateAsync(async () =>
            {
                var start = _getNextCanvasPoint();
                var items = await _workspaceService.SaveMixedTabBatchAsync(plan, targetWorkspace.Id,
                    _getCurrentFolderId(), start.X, start.Y, groupTitle);
                foreach (var item in items) _addSavedItemToCanvas(item);
                _setStatus($"フォルダタブを含む {snapshots.Count} タブを保存しました");
            });
        }
        if (plan.StandaloneFolderTabs.Count > 0
            && !await SaveFolderTabsAsync(plan.StandaloneFolderTabs))
        {
            return false;
        }

        snapshots = plan.SessionTabs;
        if (snapshots.Count == 0)
        {
            return true;
        }

        if (snapshots.Count == 1)
        {
            return await SaveSingleTabAsync(snapshots[0]);
        }

        var workspace = GetCurrentWorkspaceOrReport();
        if (workspace is null)
        {
            return false;
        }

        return await SaveWithStateAsync(async () =>
        {
            var point = _getNextCanvasPoint();
            var tabs = WorkspaceTabSavePlanner.CreateSessionTabs(snapshots);
            var firstTitle = tabs.FirstOrDefault()?.Title;
            var title = plan.SplitGroupCount == 1 && tabs.Count == 2
                ? CreateSplitSessionTitle(tabs, firstTitle)
                : string.IsNullOrWhiteSpace(firstTitle)
                    ? $"{groupTitle} ({tabs.Count})"
                    : $"{groupTitle}: {firstTitle} ほか {tabs.Count - 1}";
            var item = await _workspaceService.AddSessionGroupAsync(
                workspace.Id,
                title,
                tabs,
                point.X,
                point.Y,
                _getCurrentFolderId(),
                note: $"{tabs.Count} tabs",
                thumbnailPngBase64: snapshots.FirstOrDefault(static x => x.Thumbnail is not null)?.CreateThumbnailPngBase64() ?? string.Empty);
            _addSavedItemToCanvas(item);
            _setStatus(plan.SplitGroupCount > 0
                ? $"分割相手を含む {tabs.Count} タブを保存しました"
                : $"SessionGroup を保存しました: {tabs.Count} タブ");
        });
    }

    private static string CreateSplitSessionTitle(
        IReadOnlyList<SessionTab> tabs,
        string? firstTitle)
    {
        var orientation = tabs.Any(static tab => tab.SplitOrientation == SplitViewOrientation.TopBottom)
            ? "上下分割"
            : "左右分割";
        return string.IsNullOrWhiteSpace(firstTitle)
            ? $"{orientation} ({tabs.Count})"
            : $"{orientation}: {firstTitle} ほか {tabs.Count - 1}";
    }

    private Workspace? GetCurrentWorkspaceOrReport()
    {
        var workspace = _getCurrentWorkspace();
        if (workspace is null)
        {
            _setStatus("保存先 Workspace が読み込まれていません");
        }

        return workspace;
    }

    private async Task<bool> SaveWithStateAsync(Func<Task> saveAsync)
    {
        if (IsSaving)
        {
            return false;
        }

        try
        {
            IsSaving = true;
            await saveAsync();
            return true;
        }
        catch (Exception ex)
        {
            _setStatus("Workspace へ保存できません: " + ex.Message);
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabShelfTabViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedTabs));
            OnPropertyChanged(nameof(SelectedYouTubeMiniPlayers));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
