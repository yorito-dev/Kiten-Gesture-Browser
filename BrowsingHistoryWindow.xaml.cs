using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;

namespace TabBrowser;

public partial class BrowsingHistoryWindow : Window
{
    private const int PageSize = 200;
    private readonly BrowsingHistoryService _historyService;
    private readonly Func<BrowsingHistoryEntry, BrowsingHistoryOpenMode, Task> _openAsync;
    private readonly Func<Guid, IReadOnlyList<BrowsingHistoryEntry>, Task> _saveWorkspaceAsync;
    private readonly Dictionary<string, string> _containerNames;
    private readonly DispatcherTimer _searchTimer;
    private readonly List<BrowsingHistoryItemViewModel> _loadedItems = [];
    private CancellationTokenSource? _loadCancellation;
    private Func<Task<int>>? _pendingDelete;
    private int _offset;
    private bool _hasMore;
    private bool _isLoading;

    public BrowsingHistoryWindow(
        BrowsingHistoryService historyService,
        IReadOnlyList<ContainerProfile> containers,
        IReadOnlyList<Workspace> workspaces,
        Func<BrowsingHistoryEntry, BrowsingHistoryOpenMode, Task> openAsync,
        Func<Guid, IReadOnlyList<BrowsingHistoryEntry>, Task> saveWorkspaceAsync)
    {
        InitializeComponent();
        _historyService = historyService;
        _openAsync = openAsync;
        _saveWorkspaceAsync = saveWorkspaceAsync;
        _containerNames = containers.ToDictionary(
            static profile => profile.Id,
            static profile => profile.Name,
            StringComparer.OrdinalIgnoreCase);
        _searchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _searchTimer.Tick += SearchTimer_Tick;

        ContainerFilterBox.ItemsSource = new[]
        {
            new HistoryFilterOption("すべてのコンテナ", string.Empty)
        }.Concat(containers.Select(static profile => new HistoryFilterOption(profile.Name, profile.Id))).ToList();
        ContainerFilterBox.SelectedIndex = 0;
        PeriodFilterBox.ItemsSource = new[]
        {
            new HistoryFilterOption("全期間", "all"),
            new HistoryFilterOption("今日", "today"),
            new HistoryFilterOption("過去7日", "7days"),
            new HistoryFilterOption("過去30日", "30days")
        };
        PeriodFilterBox.SelectedIndex = 0;
        WorkspaceBox.ItemsSource = workspaces;
        WorkspaceBox.SelectedItem = workspaces.FirstOrDefault();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await ReloadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimer_Tick;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        base.OnClosed(e);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        await ReloadAsync();
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await ReloadAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(reset: false);
    }

    private async Task ReloadAsync()
    {
        await LoadAsync(reset: true);
    }

    private async Task LoadAsync(bool reset)
    {
        if (_isLoading || !reset && !_hasMore)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        _isLoading = true;
        LoadingText.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;

        try
        {
            if (reset)
            {
                _offset = 0;
                _loadedItems.Clear();
            }

            var (from, to) = GetSelectedPeriodRange();
            var containerId = (ContainerFilterBox.SelectedItem as HistoryFilterOption)?.Value ?? string.Empty;
            var entries = await _historyService.SearchAsync(
                new BrowsingHistoryQuery(
                    SearchBox.Text,
                    containerId,
                    from,
                    to,
                    _offset,
                    PageSize),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _loadedItems.AddRange(entries.Select(CreateItemViewModel));
            _offset += entries.Count;
            _hasMore = entries.Count == PageSize;
            RebuildGroups();
            StatusText.Text = $"{_loadedItems.Count}件を表示";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "履歴を読み込めません: " + ex.Message;
        }
        finally
        {
            _isLoading = false;
            LoadingText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = _loadedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreButton.IsEnabled = _hasMore;
            UpdateSelectionButtons();
        }
    }

    private BrowsingHistoryItemViewModel CreateItemViewModel(BrowsingHistoryEntry entry)
    {
        return new BrowsingHistoryItemViewModel(
            entry,
            entry.VisitedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            _containerNames.GetValueOrDefault(entry.ContainerProfileId, entry.ContainerProfileId));
    }

    private void RebuildGroups()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var groups = _loadedItems
            .GroupBy(item => GetDateGroup(item.Entry.VisitedAt.ToLocalTime(), today))
            .OrderBy(static group => group.Key.SortOrder)
            .Select(group => new BrowsingHistoryDayGroupViewModel(
                group.Key.Label,
                new ObservableCollection<BrowsingHistorySiteGroupViewModel>(
                    group.GroupBy(static item => item.Entry.Host, StringComparer.OrdinalIgnoreCase)
                        .Select(site => new BrowsingHistorySiteGroupViewModel(
                            site.Key,
                            new ObservableCollection<BrowsingHistoryItemViewModel>(site))))))
            .ToList();
        DayGroupsControl.ItemsSource = groups;
    }

    private static (int SortOrder, string Label) GetDateGroup(DateTimeOffset visitedAt, DateOnly today)
    {
        var date = DateOnly.FromDateTime(visitedAt.Date);
        var days = today.DayNumber - date.DayNumber;
        return days switch
        {
            <= 0 => (0, $"今日 ({date:yyyy/MM/dd})"),
            1 => (1, $"昨日 ({date:yyyy/MM/dd})"),
            2 => (2, $"2日前 ({date:yyyy/MM/dd})"),
            <= 6 => (3, $"過去7日 ({date:yyyy/MM/dd})"),
            <= 29 => (4, $"過去30日 ({date:yyyy/MM/dd})"),
            _ => (5, $"それ以前 ({date:yyyy/MM/dd})")
        };
    }

    private async void HistoryEntry_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || sender is not FrameworkElement { DataContext: BrowsingHistoryItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        await OpenEntryAsync(item.Entry, BrowsingHistoryOpenMode.CurrentTab);
    }

    private async void OpenEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem
            {
                DataContext: BrowsingHistoryItemViewModel item,
                Tag: string modeText
            }
            || !Enum.TryParse<BrowsingHistoryOpenMode>(modeText, out var mode))
        {
            return;
        }

        await OpenEntryAsync(item.Entry, mode);
    }

    private async Task OpenEntryAsync(BrowsingHistoryEntry entry, BrowsingHistoryOpenMode mode)
    {
        try
        {
            await _openAsync(entry, mode);
            StatusText.Text = $"開きました: {entry.Title}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "履歴を開けません: " + ex.Message;
        }
    }

    private void EntrySelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectionButtons();
    }

    private void UpdateSelectionButtons()
    {
        var hasSelection = _loadedItems.Any(static item => item.IsSelected);
        SaveWorkspaceButton.IsEnabled = hasSelection && WorkspaceBox.SelectedItem is Workspace;
        DeleteSelectedButton.IsEnabled = hasSelection;
    }

    private async void SaveWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceBox.SelectedItem is not Workspace workspace)
        {
            StatusText.Text = "保存先Workspaceを選択してください";
            return;
        }

        var entries = GetSelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        SaveWorkspaceButton.IsEnabled = false;
        try
        {
            await _saveWorkspaceAsync(workspace.Id, entries);
            StatusText.Text = $"{entries.Count}件を{workspace.Name}へ保存しました";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Workspaceへ保存できません: " + ex.Message;
        }
        finally
        {
            UpdateSelectionButtons();
        }
    }

    private async void DeleteEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BrowsingHistoryItemViewModel item })
        {
            await ShowDeleteConfirmationAsync(
                new BrowsingHistoryDeleteSummary(1, item.Entry.VisitedAt, item.Entry.VisitedAt),
                () => _historyService.DeleteIdsAsync([item.Entry.Id]));
        }
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = GetSelectedEntries();
        if (entries.Count == 0)
        {
            return;
        }

        await ShowDeleteConfirmationAsync(
            CreateSummary(entries),
            () => _historyService.DeleteIdsAsync(entries.Select(static entry => entry.Id).ToList()));
    }

    private async void DeleteSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string host)
        {
            return;
        }

        try
        {
            var summary = await _historyService.GetHostSummaryAsync(host);
            await ShowDeleteConfirmationAsync(
                summary,
                () => _historyService.DeleteHostAsync(host));
        }
        catch (Exception ex)
        {
            StatusText.Text = "削除対象を確認できません: " + ex.Message;
        }
    }

    private async void DeletePeriodButton_Click(object sender, RoutedEventArgs e)
    {
        var (from, to) = GetSelectedPeriodRange();
        if (from is null || to is null)
        {
            StatusText.Text = "期間削除には「今日」「過去7日」「過去30日」のいずれかを選択してください";
            return;
        }

        try
        {
            var summary = await _historyService.GetRangeSummaryAsync(from.Value, to.Value);
            await ShowDeleteConfirmationAsync(
                summary,
                () => _historyService.DeleteRangeAsync(from.Value, to.Value));
        }
        catch (Exception ex)
        {
            StatusText.Text = "削除対象を確認できません: " + ex.Message;
        }
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = await _historyService.GetAllSummaryAsync();
            await ShowDeleteConfirmationAsync(summary, () => _historyService.DeleteAllAsync());
        }
        catch (Exception ex)
        {
            StatusText.Text = "削除対象を確認できません: " + ex.Message;
        }
    }

    private Task ShowDeleteConfirmationAsync(
        BrowsingHistoryDeleteSummary summary,
        Func<Task<int>> delete)
    {
        if (summary.Count <= 0)
        {
            StatusText.Text = "削除する履歴はありません";
            return Task.CompletedTask;
        }

        _pendingDelete = delete;
        var oldest = summary.OldestVisitedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "-";
        var newest = summary.NewestVisitedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "-";
        DeleteConfirmationText.Text = $"{summary.Count}件を削除します。対象期間: {oldest} ～ {newest}";
        DeleteConfirmationPanel.Visibility = Visibility.Visible;
        return Task.CompletedTask;
    }

    private async void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var delete = _pendingDelete;
        CancelDeleteConfirmation();
        if (delete is null)
        {
            return;
        }

        try
        {
            var deleted = await delete();
            await ReloadAsync();
            StatusText.Text = $"{deleted}件を削除しました";
        }
        catch (Exception ex)
        {
            StatusText.Text = "履歴を削除できません: " + ex.Message;
        }
    }

    private void CancelDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDeleteConfirmation();
    }

    private void CancelDeleteConfirmation()
    {
        _pendingDelete = null;
        DeleteConfirmationPanel.Visibility = Visibility.Collapsed;
    }

    private IReadOnlyList<BrowsingHistoryEntry> GetSelectedEntries()
    {
        return _loadedItems
            .Where(static item => item.IsSelected)
            .Select(static item => item.Entry)
            .ToList();
    }

    private static BrowsingHistoryDeleteSummary CreateSummary(
        IReadOnlyList<BrowsingHistoryEntry> entries)
    {
        return entries.Count == 0
            ? new BrowsingHistoryDeleteSummary(0, null, null)
            : new BrowsingHistoryDeleteSummary(
                entries.Count,
                entries.Min(static entry => entry.VisitedAt),
                entries.Max(static entry => entry.VisitedAt));
    }

    private (DateTimeOffset? From, DateTimeOffset? To) GetSelectedPeriodRange()
    {
        var period = (PeriodFilterBox.SelectedItem as HistoryFilterOption)?.Value ?? "all";
        if (period == "all")
        {
            return (null, null);
        }

        var today = DateTime.Today;
        var offset = TimeZoneInfo.Local.GetUtcOffset(today);
        var to = new DateTimeOffset(today.AddDays(1), offset);
        var from = period switch
        {
            "today" => new DateTimeOffset(today, offset),
            "7days" => new DateTimeOffset(today.AddDays(-6), offset),
            "30days" => new DateTimeOffset(today.AddDays(-29), offset),
            _ => (DateTimeOffset?)null
        };
        return from is null ? (null, null) : (from, to);
    }

    private sealed record HistoryFilterOption(string Name, string Value);
}

public sealed class BrowsingHistoryItemViewModel(
    BrowsingHistoryEntry entry,
    string visitedDisplay,
    string containerDisplay)
{
    public BrowsingHistoryEntry Entry { get; } = entry;

    public string VisitedDisplay { get; } = visitedDisplay;

    public string ContainerDisplay { get; } = containerDisplay;

    public string SourceDisplay { get; } = entry.SourceKind == BrowsingHistorySourceKind.YouTubeMiniPlayer
        ? "YTミニプレイヤー"
        : string.Empty;

    public Visibility SourceBadgeVisibility { get; } =
        entry.SourceKind == BrowsingHistorySourceKind.YouTubeMiniPlayer
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool IsSelected { get; set; }
}

public sealed class BrowsingHistorySiteGroupViewModel(
    string host,
    ObservableCollection<BrowsingHistoryItemViewModel> items)
{
    public string Host { get; } = host;

    public ObservableCollection<BrowsingHistoryItemViewModel> Items { get; } = items;

    public int Count => Items.Count;

    public bool IsExpanded { get; set; } = true;
}

public sealed record BrowsingHistoryDayGroupViewModel(
    string Label,
    ObservableCollection<BrowsingHistorySiteGroupViewModel> SiteGroups);
