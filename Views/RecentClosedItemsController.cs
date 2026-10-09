using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record RecentClosedItemsElements(
    Button ClosedTabHistoryButton,
    Popup ClosedTabHistoryPopup,
    ListBox ClosedTabHistoryList,
    RadioButton RecentClosedTabsFilter,
    RadioButton RecentClosedWindowsFilter,
    RadioButton RecentClosedYouTubeFilter,
    TextBox RecentClosedSearchBox,
    TextBlock RecentClosedEmptyTextBlock,
    Button RestoreButton);

internal interface IRecentClosedItemsHost
{
    bool IsInitialized { get; }
    bool HasClosedItems { get; }
    IReadOnlyList<ClosedWindowState> GetVisibleClosedWindows();
    IReadOnlyList<YouTubeMiniPlayerState> GetVisibleClosedMiniPlayers();
    void TrimClosedTabHistory();
    void SetStatus(string message, bool autoClear);
    void RestoreClosedWindow(ClosedWindowState state);
    void RestoreClosedTab(ClosedTabState state);
    Task RestoreClosedYouTubeMiniPlayerAsync(Guid id);
    ImageSource CreateClosedWindowThumbnail(ClosedWindowState state);
    Task<ImageSource> GetOrCreateFolderTabThumbnailAsync(ClosedTabState state, string title);
    Func<Task<ImageSource?>>? GetPreservedTabCapture(ClosedTabState state);
    void StartCommandPaletteThumbnailCapture(CommandPaletteItem item);
    void StopCommandPaletteUrlThumbnailPlayback(string? url);
}

// 最近閉じた項目の分類・検索・選択抑止と表示／tooltip手順を所有する。
// 履歴Store、復元先Windowとprivateフィルター、非同期画像取得の寿命は既存ownerへ接続する。
internal sealed class RecentClosedItemsController(
    RecentClosedItemsElements elements,
    IRecentClosedItemsHost host,
    ClosedTabWindowHistoryStore historyStore,
    TabOperationService tabOperationService,
    Func<DateTimeOffset>? now = null)
{
    private readonly RecentClosedItemsElements _elements = elements;
    private readonly IRecentClosedItemsHost _host = host;
    private readonly ClosedTabWindowHistoryStore _historyStore = historyStore;
    private readonly TabOperationService _tabOperationService = tabOperationService;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private bool _suppressClosedTabHistorySelection;
    private RecentClosedItemCategory _recentClosedItemCategory;

    public void RestoreMostRecentItem()
    {
        var closedWindow = _host.GetVisibleClosedWindows().FirstOrDefault();
        var closedTab = _historyStore.ClosedTabs.FirstOrDefault();
        if (_tabOperationService.ShouldRestoreClosedWindowFirst(closedWindow, closedTab))
        {
            _host.RestoreClosedWindow(closedWindow!);
            return;
        }

        if (closedTab is not null)
        {
            _host.RestoreClosedTab(closedTab);
            return;
        }

        _host.SetStatus("最近閉じた項目はありません", autoClear: true);
    }

    public void OpenHistory()
    {
        Refresh();
        if (!_host.HasClosedItems)
        {
            _host.SetStatus("最近閉じた項目はありません", autoClear: true);
            return;
        }

        _elements.ClosedTabHistoryPopup.IsOpen = true;
    }

    public async Task RestoreSelectedItemAsync()
    {
        if (_suppressClosedTabHistorySelection
            || _elements.ClosedTabHistoryList.SelectedItem is not ClosedTabHistoryItem item)
        {
            return;
        }

        _elements.ClosedTabHistoryPopup.IsOpen = false;
        if (item.ClosedWindowState is not null)
        {
            _host.RestoreClosedWindow(item.ClosedWindowState);
            return;
        }

        if (item.TabState is not null)
        {
            _host.RestoreClosedTab(item.TabState);
            return;
        }

        if (item.YouTubeMiniPlayerState is not null)
        {
            await _host.RestoreClosedYouTubeMiniPlayerAsync(item.YouTubeMiniPlayerState.Id);
        }
    }

    public void FilterChecked(object sender)
    {
        if (!_host.IsInitialized)
        {
            return;
        }

        _recentClosedItemCategory = sender switch
        {
            RadioButton button when ReferenceEquals(button, _elements.RecentClosedTabsFilter) =>
                RecentClosedItemCategory.Tabs,
            RadioButton button when ReferenceEquals(button, _elements.RecentClosedWindowsFilter) =>
                RecentClosedItemCategory.Windows,
            RadioButton button when ReferenceEquals(button, _elements.RecentClosedYouTubeFilter) =>
                RecentClosedItemCategory.YouTubeMiniPlayers,
            _ => RecentClosedItemCategory.All
        };
        Refresh();
    }

    public void SearchChanged()
    {
        if (_host.IsInitialized)
        {
            Refresh();
        }
    }

    public async Task OpenToolTipAsync(object sender)
    {
        if (sender is not FrameworkElement row
            || row.DataContext is not ClosedTabHistoryItem item
            || item.PreviewItem.HasThumbnail)
        {
            return;
        }

        if (item.ClosedWindowState is not null)
        {
            item.PreviewItem.Thumbnail = _host.CreateClosedWindowThumbnail(item.ClosedWindowState);
            return;
        }

        if (item.TabState?.RuntimeThumbnail is { } runtimeThumbnail)
        {
            item.PreviewItem.Thumbnail = runtimeThumbnail;
            return;
        }

        if (item.TabState is { IsFolderTab: true } folderTabState)
        {
            item.PreviewItem.Thumbnail = await _host.GetOrCreateFolderTabThumbnailAsync(
                folderTabState,
                item.Title);
            return;
        }

        if (item.TabState is not null
            && _host.GetPreservedTabCapture(item.TabState) is { } capture)
        {
            var thumbnail = await capture();
            if (thumbnail is not null)
            {
                item.PreviewItem.Thumbnail = thumbnail;
                return;
            }
        }

        if (row.IsMouseOver)
        {
            _host.StartCommandPaletteThumbnailCapture(item.PreviewItem);
        }
    }

    public void CloseToolTip(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is ClosedTabHistoryItem item)
        {
            _host.StopCommandPaletteUrlThumbnailPlayback(item.PreviewItem.Url);
        }
    }

    public void Refresh()
    {
        _host.TrimClosedTabHistory();
        _suppressClosedTabHistorySelection = true;
        var referenceTime = _now();
        var closedWindowById = _historyStore.ClosedWindows
            .ToDictionary(static window => window.Id);
        var tabItems = _historyStore.ClosedTabs
            .Where(_tabOperationService.ShouldKeepClosedTabInHistory)
            .Take(TabOperationService.ClosedTabHistoryLimit)
            .Select(x => ClosedTabHistoryItem.FromTab(
                x,
                _tabOperationService.GetClosedTabHistoryGroupLabel(x.ClosedAt, referenceTime),
                x.ClosedWindowId is { } closedWindowId
                    && closedWindowById.TryGetValue(closedWindowId, out var parentWindow)
                    ? parentWindow.WindowState.Tabs.Count
                    : null))
            .ToList();
        var windowItems = _host.GetVisibleClosedWindows()
            .Select(x => ClosedTabHistoryItem.FromWindow(
                x,
                _tabOperationService.GetClosedTabHistoryGroupLabel(x.ClosedAt, referenceTime)))
            .ToList();
        var miniPlayerItems = _host.GetVisibleClosedMiniPlayers()
            .Select(x => ClosedTabHistoryItem.FromYouTubeMiniPlayer(
                x,
                _tabOperationService.GetClosedTabHistoryGroupLabel(x.UpdatedAt, referenceTime)))
            .ToList();
        IEnumerable<ClosedTabHistoryItem> filteredItems = _recentClosedItemCategory switch
        {
            RecentClosedItemCategory.Tabs => tabItems,
            RecentClosedItemCategory.Windows => windowItems,
            RecentClosedItemCategory.YouTubeMiniPlayers => miniPlayerItems,
            _ => windowItems.Concat(tabItems).Concat(miniPlayerItems)
        };
        var searchText = _elements.RecentClosedSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filteredItems = filteredItems.Where(item => item.Matches(searchText));
        }

        var items = filteredItems
            .OrderByDescending(static item => item.ClosedAt)
            .ThenBy(static item => item.ClosedWindowSortOrder)
            .ToList();
        var groupedItems = new ListCollectionView(items);
        groupedItems.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ClosedTabHistoryItem.GroupLabel)));
        _elements.ClosedTabHistoryList.ItemsSource = groupedItems;
        _elements.ClosedTabHistoryList.SelectedItem = null;
        _elements.ClosedTabHistoryList.Visibility = items.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _elements.RecentClosedEmptyTextBlock.Visibility = items.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        _suppressClosedTabHistorySelection = false;
        _elements.ClosedTabHistoryButton.IsEnabled = _host.HasClosedItems;
        _elements.RestoreButton.IsEnabled = _historyStore.ClosedTabs.Count > 0;
    }

    private enum RecentClosedItemCategory
    {
        All,
        Tabs,
        Windows,
        YouTubeMiniPlayers
    }

    internal sealed class ClosedTabHistoryItem
    {
        private ClosedTabHistoryItem(
            ClosedTabState? tabState,
            ClosedWindowState? closedWindowState,
            YouTubeMiniPlayerState? youTubeMiniPlayerState,
            DateTimeOffset closedAt,
            string groupLabel,
            string kindLabel,
            string title,
            string subtitle,
            string previewId,
            int closedWindowSortOrder = 0)
        {
            TabState = tabState;
            ClosedWindowState = closedWindowState;
            YouTubeMiniPlayerState = youTubeMiniPlayerState;
            ClosedAt = closedAt;
            GroupLabel = groupLabel;
            KindLabel = kindLabel;
            Title = title;
            Subtitle = subtitle;
            ClosedWindowSortOrder = closedWindowSortOrder;
            PreviewItem = new CommandPaletteItem
            {
                Id = previewId,
                Type = CommandPaletteItemType.RecentlyClosed,
                Title = title,
                Subtitle = subtitle,
                Url = subtitle
            };
        }

        public ClosedTabState? TabState { get; }

        public ClosedWindowState? ClosedWindowState { get; }

        public YouTubeMiniPlayerState? YouTubeMiniPlayerState { get; }

        public DateTimeOffset ClosedAt { get; }

        public string GroupLabel { get; }

        public string KindLabel { get; }

        public string Title { get; }

        public string Subtitle { get; }

        public CommandPaletteItem PreviewItem { get; }

        public int ClosedWindowSortOrder { get; }

        public string ClosedTimeLabel => ClosedAt.ToLocalTime().ToString("HH:mm");

        public bool IsClosedWindow => ClosedWindowState is not null;

        public bool IsWindowChild => TabState?.ClosedWindowId is not null;

        public Thickness ItemMargin => IsWindowChild
            ? new Thickness(18, 0, 0, 0)
            : new Thickness(0);

        public bool IsYouTubeMiniPlayer => YouTubeMiniPlayerState is not null;

        public bool IsFolderTab => TabState?.IsFolderTab == true;

        public static ClosedTabHistoryItem FromTab(
            ClosedTabState state,
            string groupLabel,
            int? closedWindowTabCount)
        {
            var title = !string.IsNullOrWhiteSpace(state.Title)
                ? state.Title
                : !string.IsNullOrWhiteSpace(state.Url)
                    ? TryGetHost(state.Url) ?? state.Url
                    : "無題のタブ";
            var subtitle = state.Url ?? string.Empty;
            if (closedWindowTabCount is { } tabCount)
            {
                subtitle = $"{tabCount} タブのウィンドウから · {subtitle}";
            }

            var item = new ClosedTabHistoryItem(
                state,
                null,
                null,
                state.ClosedAt ?? DateTimeOffset.Now,
                groupLabel,
                state.IsFolderTab ? "フォルダー" : "タブ",
                title,
                subtitle,
                $"recent-tab:{state.ClosedAt?.UtcTicks ?? 0}:{state.Url}",
                state.ClosedWindowId is null
                    ? 0
                    : (state.ClosedWindowTabIndex ?? 0) + 1);
            return item;
        }

        public static ClosedTabHistoryItem FromWindow(
            ClosedWindowState state,
            string groupLabel)
        {
            var tabs = state.WindowState.Tabs;
            var activeTab = state.WindowState.LastActiveTabId is { } activeTabId
                ? tabs.FirstOrDefault(tab => tab.Id == activeTabId)
                : null;
            var representativeTab = activeTab ?? tabs.FirstOrDefault();
            var title = !string.IsNullOrWhiteSpace(representativeTab?.Title)
                ? representativeTab.Title
                : !string.IsNullOrWhiteSpace(representativeTab?.Url)
                    ? TryGetHost(representativeTab.Url) ?? representativeTab.Url
                    : "閉じたウィンドウ";
            var tabTitles = tabs
                .Take(3)
                .Select(tab => !string.IsNullOrWhiteSpace(tab.Title)
                    ? tab.Title
                    : !string.IsNullOrWhiteSpace(tab.Url)
                        ? TryGetHost(tab.Url) ?? tab.Url
                        : "無題のタブ")
                .ToList();
            var subtitle = $"{tabs.Count} タブ · {string.Join(" / ", tabTitles)}";
            if (tabs.Count > tabTitles.Count)
            {
                subtitle += $" / ほか {tabs.Count - tabTitles.Count} タブ";
            }

            return new ClosedTabHistoryItem(
                null,
                state,
                null,
                state.ClosedAt ?? DateTimeOffset.Now,
                groupLabel,
                "ウィンドウ",
                title,
                subtitle,
                "recent-window:" + state.Id.ToString("D"));
        }

        public static ClosedTabHistoryItem FromYouTubeMiniPlayer(
            YouTubeMiniPlayerState state,
            string groupLabel)
        {
            var subtitle = state.SourceKind == MiniPlayerSourceKind.LocalFile
                ? state.LocalFilePath
                : state.Url;
            var title = state.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = state.SourceKind == MiniPlayerSourceKind.LocalFile
                    ? Path.GetFileName(state.LocalFilePath)
                    : TryGetHost(state.Url) ?? state.Url;
            }

            var item = new ClosedTabHistoryItem(
                null,
                null,
                state,
                state.UpdatedAt,
                groupLabel,
                state.SourceKind == MiniPlayerSourceKind.LocalFile ? "動画ミニ" : "YTミニ",
                string.IsNullOrWhiteSpace(title) ? "無題のミニプレイヤー" : title,
                subtitle,
                "recent-mini:" + state.Id.ToString("D"));
            return item;
        }

        private static string? TryGetHost(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

        public bool Matches(string searchText)
        {
            return Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || Subtitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || KindLabel.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
