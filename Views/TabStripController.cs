using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ITabStripHost
{
    TabLayoutSettings Settings { get; set; }
    BrowserTab? ActiveTab { get; }
    bool IsChangingSelection { get; }
    void NotifyLayoutPropertyChanged(string propertyName);
    void UpdateHostedWebViewSizes();
    void SaveSettings();
    void SelectAndActivateTab(BrowserTab tab);
    void RunWithSelectionSuppressed(Action action);
}

internal sealed record TabStripElements(
    FrameworkElement TitleBar,
    ListBox TabList,
    ListBox VerticalPinnedTabList,
    ListBox VerticalTabList,
    ColumnDefinition VerticalTabSidebarColumn,
    ColumnDefinition VerticalTabResizeColumn,
    FrameworkElement VerticalTabSidebar,
    Thumb VerticalTabResizeThumb,
    TextBox VerticalTabSearchBox,
    Button TabLayoutModeButton,
    FrameworkElement VerticalPinnedTabsHeader);

// タブの表示と配置だけを所有し、タブ操作・Runtime切替・drop受理は既存hostへ残す。
internal sealed class TabStripController : IDisposable
{
    internal const double PreferredTabWidth = 190;
    private const double MinimumReadableTabWidth = 118;
    private const double CompactMinimumReadableTabWidth = 96;
    private const double TabItemRightMargin = 4;
    private const double TabLayoutWrapSafetyPadding = 16;
    internal const int TabLayoutResizeDelayMilliseconds = 80;
    private const int TabLayoutAnimationMilliseconds = 100;
    private readonly TabStripElements _elements;
    private readonly ITabStripHost _host;
    private readonly ObservableCollection<BrowserTab> _tabs;
    private readonly OwnedDispatcherTimer _tabLayoutUpdateTimer;
    private readonly CollectionViewSource _verticalPinnedTabsSource = new();
    private readonly CollectionViewSource _verticalRegularTabsSource = new();
    private double _tabItemWidth = PreferredTabWidth;
    private double _verticalTabSidebarWidth = TabLayoutSettingsProfile.DefaultVerticalSidebarWidth;
    private bool _initialized;
    private bool _disposed;

    private FrameworkElement TitleBar => _elements.TitleBar;
    private ListBox TabList => _elements.TabList;
    private ListBox VerticalPinnedTabList => _elements.VerticalPinnedTabList;
    private ListBox VerticalTabList => _elements.VerticalTabList;
    private ColumnDefinition VerticalTabSidebarColumn => _elements.VerticalTabSidebarColumn;
    private ColumnDefinition VerticalTabResizeColumn => _elements.VerticalTabResizeColumn;
    private FrameworkElement VerticalTabSidebar => _elements.VerticalTabSidebar;
    private Thumb VerticalTabResizeThumb => _elements.VerticalTabResizeThumb;
    private TextBox VerticalTabSearchBox => _elements.VerticalTabSearchBox;
    private Button TabLayoutModeButton => _elements.TabLayoutModeButton;
    private FrameworkElement VerticalPinnedTabsHeader => _elements.VerticalPinnedTabsHeader;
    private Dispatcher Dispatcher => TabList.Dispatcher;

    internal TabStripController(
        TabStripElements elements,
        ObservableCollection<BrowserTab> tabs,
        OwnedDispatcherTimer layoutUpdateTimer,
        ITabStripHost host)
    {
        _elements = elements;
        _tabs = tabs;
        _tabLayoutUpdateTimer = layoutUpdateTimer;
        _host = host;
    }

    internal double TabItemWidth
    {
        get => _tabItemWidth;
        private set
        {
            if (Math.Abs(_tabItemWidth - value) < 0.1)
            {
                return;
            }

            _tabItemWidth = value;
            _host.NotifyLayoutPropertyChanged(nameof(TabItemWidth));
        }
    }

    internal double VerticalTabSidebarWidth
    {
        get => _verticalTabSidebarWidth;
        set
        {
            var normalized = Math.Clamp(
                value,
                TabLayoutSettingsProfile.MinimumVerticalSidebarWidth,
                TabLayoutSettingsProfile.MaximumVerticalSidebarWidth);
            if (Math.Abs(_verticalTabSidebarWidth - normalized) < 0.1)
            {
                return;
            }

            _verticalTabSidebarWidth = normalized;
            _host.NotifyLayoutPropertyChanged(nameof(VerticalTabSidebarWidth));
        }
    }

    private void TabLayoutUpdateTimer_Tick(object? sender, EventArgs e)
    {
        _tabLayoutUpdateTimer.Stop();
        UpdateTabLayout();
    }

    internal void SynchronizeSelection(BrowserTab tab)
    {
        if (_disposed) return;
        TabList.SelectedItem = tab;
        var showPinnedSection = IsVerticalTabMode()
            && _host.Settings.ShowPinnedTabsSection
            && tab.IsPinned;
        VerticalPinnedTabList.SelectedItem = showPinnedSection ? tab : null;
        VerticalTabList.SelectedItem = showPinnedSection ? null : tab;
    }

    internal void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _tabLayoutUpdateTimer.Tick += TabLayoutUpdateTimer_Tick;
        TabList.ItemsSource = _tabs;
        _verticalPinnedTabsSource.Source = _tabs;
        _verticalRegularTabsSource.Source = _tabs;
        _verticalPinnedTabsSource.Filter += VerticalPinnedTabsSource_Filter;
        _verticalRegularTabsSource.Filter += VerticalRegularTabsSource_Filter;
        VerticalPinnedTabList.ItemsSource = _verticalPinnedTabsSource.View;
        VerticalTabList.ItemsSource = _verticalRegularTabsSource.View;
        ApplyTabLayoutSettings();
        RefreshVerticalTabViews();
    }

    private void VerticalPinnedTabsSource_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BrowserTab tab
            && tab.IsPinned
            && TabMatchesVerticalSearch(tab);
    }

    private void VerticalRegularTabsSource_Filter(object sender, FilterEventArgs e)
    {
        var showPinnedSection = _host.Settings.ShowPinnedTabsSection;
        e.Accepted = e.Item is BrowserTab tab
            && (!showPinnedSection || !tab.IsPinned)
            && TabMatchesVerticalSearch(tab);
    }

    private bool TabMatchesVerticalSearch(BrowserTab tab)
    {
        var query = VerticalTabSearchBox.Text.Trim();
        return string.IsNullOrWhiteSpace(query)
            || tab.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (tab.Url?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    internal void ApplyTabLayoutSettings()
    {
        if (_disposed) return;
        _host.Settings = TabLayoutSettingsProfile.Normalize(_host.Settings);
        VerticalTabSidebarWidth = _host.Settings.VerticalSidebarWidth;
        var useVerticalTabs = IsVerticalTabMode();
        var showVerticalSidebar = useVerticalTabs && !_host.Settings.VerticalSidebarCollapsed;

        TitleBar.MinHeight = useVerticalTabs ? 38 : 38;
        TabList.Visibility = useVerticalTabs ? Visibility.Collapsed : Visibility.Visible;
        VerticalTabSidebarColumn.Width = showVerticalSidebar
            ? new GridLength(VerticalTabSidebarWidth)
            : new GridLength(0);
        VerticalTabResizeColumn.Width = showVerticalSidebar
            ? new GridLength(6)
            : new GridLength(0);
        VerticalTabSidebar.Visibility = showVerticalSidebar ? Visibility.Visible : Visibility.Collapsed;
        VerticalTabResizeThumb.Visibility = showVerticalSidebar ? Visibility.Visible : Visibility.Collapsed;
        VerticalTabSearchBox.Visibility = _host.Settings.ShowTabSearch
            ? Visibility.Visible
            : Visibility.Collapsed;
        TabLayoutModeButton.Content = useVerticalTabs ? "横" : "縦";
        TabLayoutModeButton.ToolTip = useVerticalTabs ? "横タブに切り替え" : "縦タブに切り替え";
        RefreshVerticalTabViews();
        QueueTabLayoutUpdate();
        _host.UpdateHostedWebViewSizes();
    }

    internal bool IsVerticalTabMode()
    {
        return string.Equals(_host.Settings.Mode, TabLayoutSettingsProfile.VerticalMode, StringComparison.Ordinal);
    }

    internal void RefreshVerticalTabViews()
    {
        if (_disposed) return;
        _verticalPinnedTabsSource.View?.Refresh();
        _verticalRegularTabsSource.View?.Refresh();
        var showPinnedSection = IsVerticalTabMode()
            && _host.Settings.ShowPinnedTabsSection
            && _tabs.Any(static tab => tab.IsPinned);
        VerticalPinnedTabsHeader.Visibility = showPinnedSection ? Visibility.Visible : Visibility.Collapsed;
        VerticalPinnedTabList.Visibility = showPinnedSection ? Visibility.Visible : Visibility.Collapsed;

        if (_host.ActiveTab is { } activeTab && TabMatchesVerticalSearch(activeTab))
        {
            if (showPinnedSection && activeTab.IsPinned)
            {
                VerticalPinnedTabList.SelectedItem = activeTab;
                VerticalTabList.SelectedItem = null;
            }
            else
            {
                VerticalPinnedTabList.SelectedItem = null;
                VerticalTabList.SelectedItem = activeTab;
            }
        }
        else
        {
            VerticalPinnedTabList.SelectedItem = null;
            VerticalTabList.SelectedItem = null;
        }
    }

    internal void VerticalTabSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_disposed) return;
        RefreshVerticalTabViews();
    }

    internal void VerticalTabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (_host.IsChangingSelection
            || (sender as ListBox)?.SelectedItem is not BrowserTab tab
            || _host.ActiveTab == tab)
        {
            return;
        }

        _host.SelectAndActivateTab(tab);
    }

    internal void VerticalTabSidebarResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_disposed) return;
        VerticalTabSidebarWidth += e.HorizontalChange;
        _host.Settings.VerticalSidebarWidth = VerticalTabSidebarWidth;
        VerticalTabSidebarColumn.Width = new GridLength(VerticalTabSidebarWidth);
        _host.UpdateHostedWebViewSizes();
    }

    internal void VerticalTabSidebarResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_disposed) return;
        _host.Settings.VerticalSidebarWidth = VerticalTabSidebarWidth;
        _host.SaveSettings();
    }

    internal void TabLayoutModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _host.Settings.Mode = IsVerticalTabMode()
            ? TabLayoutSettingsProfile.HorizontalMode
            : TabLayoutSettingsProfile.VerticalMode;
        _host.Settings.VerticalSidebarCollapsed = false;
        ApplyTabLayoutSettings();
        _host.SaveSettings();
    }

    internal void QueueTabLayoutUpdate()
    {
        if (_disposed) return;
        _tabLayoutUpdateTimer.Stop();
        _tabLayoutUpdateTimer.Start();
    }

    internal void RefreshTabLayoutAfterTabCollectionChanged()
    {
        if (_disposed) return;
        UpdateTabLayout();
        QueueTabLayoutUpdate();
    }

    internal void UpdateTabLayout()
    {
        if (_disposed) return;
        if (_tabs.Count == 0 || TabList.ActualWidth <= 0)
        {
            TabItemWidth = PreferredTabWidth;
            return;
        }

        var availableWidth = Math.Max(1, TabList.ActualWidth - TabLayoutWrapSafetyPadding);
        var standardMaxTabsPerRow = Math.Max(
            1,
            (int)Math.Floor(availableWidth / (MinimumReadableTabWidth + TabItemRightMargin)));
        var minimumTabWidth = _tabs.Count > standardMaxTabsPerRow
            ? CompactMinimumReadableTabWidth
            : MinimumReadableTabWidth;
        var maxTabsPerRow = Math.Max(
            1,
            (int)Math.Floor(availableWidth / (minimumTabWidth + TabItemRightMargin)));
        var tabsInRow = Math.Min(_tabs.Count, maxTabsPerRow);
        var width = Math.Floor((availableWidth / tabsInRow) - TabItemRightMargin);

        TabItemWidth = Math.Clamp(width, minimumTabWidth, PreferredTabWidth);
    }

    internal Dictionary<BrowserTab, Rect> CaptureTabLayoutPositions(ListBox tabList)
    {
        if (_disposed) return [];
        var positions = new Dictionary<BrowserTab, Rect>();

        foreach (var tab in tabList.Items.OfType<BrowserTab>())
        {
            if (tabList.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item
                || item.ActualWidth <= 0
                || item.ActualHeight <= 0)
            {
                continue;
            }

            var origin = item.TranslatePoint(new Point(0, 0), tabList);
            positions[tab] = new Rect(origin, new Size(item.ActualWidth, item.ActualHeight));
        }

        return positions;
    }

    internal void AnimateTabLayoutFrom(
        ListBox tabList,
        IReadOnlyDictionary<BrowserTab, Rect> previousPositions)
    {
        if (_disposed) return;
        if (previousPositions.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_disposed) return;
                tabList.UpdateLayout();

                foreach (var tab in tabList.Items.OfType<BrowserTab>())
                {
                    if (!previousPositions.TryGetValue(tab, out var previous)
                        || tabList.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item)
                    {
                        continue;
                    }

                    var current = item.TranslatePoint(new Point(0, 0), tabList);
                    var deltaX = previous.Left - current.X;
                    var deltaY = previous.Top - current.Y;

                    if (Math.Abs(deltaX) < 0.5 && Math.Abs(deltaY) < 0.5)
                    {
                        continue;
                    }

                    var transform = EnsureTabItemTranslateTransform(item);
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.X = 0;
                    transform.Y = 0;

                    var duration = TimeSpan.FromMilliseconds(TabLayoutAnimationMilliseconds);
                    var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                    transform.BeginAnimation(
                        TranslateTransform.XProperty,
                        new DoubleAnimation(deltaX, 0, duration)
                        {
                            EasingFunction = easing,
                            FillBehavior = FillBehavior.Stop
                        },
                        HandoffBehavior.SnapshotAndReplace);
                    transform.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(deltaY, 0, duration)
                        {
                            EasingFunction = easing,
                            FillBehavior = FillBehavior.Stop
                        },
                        HandoffBehavior.SnapshotAndReplace);
                }
            }),
            DispatcherPriority.Loaded);
    }

    private static TranslateTransform EnsureTabItemTranslateTransform(ListBoxItem item)
    {
        if (item.RenderTransform is TranslateTransform translate)
        {
            return translate;
        }

        translate = new TranslateTransform();
        item.RenderTransform = translate;
        return translate;
    }

    internal static void AnimateTabItemMarginTo(
        ListBoxItem item,
        Thickness targetMargin,
        bool animate)
    {
        var currentMargin = item.Margin;
        item.BeginAnimation(FrameworkElement.MarginProperty, null);
        item.Margin = targetMargin;

        if (!animate || currentMargin == targetMargin)
        {
            return;
        }

        item.BeginAnimation(
            FrameworkElement.MarginProperty,
            new ThicknessAnimation(
                currentMargin,
                targetMargin,
                TimeSpan.FromMilliseconds(TabLayoutAnimationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    internal static void AnimateTabItemTranslateTo(
        ListBoxItem item,
        double targetX,
        double targetY,
        bool animate)
    {
        var transform = EnsureTabItemTranslateTransform(item);
        var currentX = transform.X;
        var currentY = transform.Y;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.X = targetX;
        transform.Y = targetY;

        if (!animate
            || Math.Abs(currentX - targetX) < 0.5
            && Math.Abs(currentY - targetY) < 0.5)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(TabLayoutAnimationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(currentX, targetX, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(currentY, targetY, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    internal ListBox GetVisibleTabListForTab(BrowserTab tab)
    {
        if (!IsVerticalTabMode())
        {
            return TabList;
        }

        return tab.IsPinned && _host.Settings.ShowPinnedTabsSection
            ? VerticalPinnedTabList
            : VerticalTabList;
    }

    internal IEnumerable<ListBox> GetVisibleTabLists()
    {
        if (!IsVerticalTabMode())
        {
            yield return TabList;
            yield break;
        }

        if (VerticalPinnedTabList.Visibility == Visibility.Visible)
        {
            yield return VerticalPinnedTabList;
        }

        yield return VerticalTabList;
    }

    internal static bool IsVerticalTabList(ListBox tabList)
    {
        return string.Equals(tabList.Name, "VerticalPinnedTabList", StringComparison.Ordinal)
            || string.Equals(tabList.Name, "VerticalTabList", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tabLayoutUpdateTimer.Stop();
        _tabLayoutUpdateTimer.Tick -= TabLayoutUpdateTimer_Tick;
        _verticalPinnedTabsSource.Filter -= VerticalPinnedTabsSource_Filter;
        _verticalRegularTabsSource.Filter -= VerticalRegularTabsSource_Filter;
        _host.RunWithSelectionSuppressed(() =>
        {
            TabList.ItemsSource = null;
            VerticalPinnedTabList.ItemsSource = null;
            VerticalTabList.ItemsSource = null;
            _verticalPinnedTabsSource.Source = null;
            _verticalRegularTabsSource.Source = null;
        });
    }
}
