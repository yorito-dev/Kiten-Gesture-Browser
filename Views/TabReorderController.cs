using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ITabReorderHost
{
    ListBox ResolveTabList(object originalSource);
    bool CanDropTabOnList(BrowserTab tab, ListBox tabList);
    void EnsureWorkspaceTabIsFirst();
    int ClampNormalTabInsertionIndex(int index);
    void SelectTab(BrowserTab tab);
    void QueueSessionSave();
    void RegisterReorderUndo();
    void SetStatus(string message, bool autoClear);
    bool TryGetCursorPointInTabList(ListBox tabList, out Point point);
}

// 並べ替えと挿入位置表示を所有する。別Windowへの転送とRuntime移管はhostへ残す。
internal sealed class TabReorderController : IDisposable
{
    private readonly ObservableCollection<BrowserTab> _tabs;
    private readonly TabStripController _tabStripController;
    private readonly TabDragDropService _tabDragDropService;
    private readonly TabOperationService _tabOperationService;
    private readonly OwnedDispatcherTimer _dropPreviewClearTimer;
    private readonly ITabReorderHost _host;
    private ListBox? _lastTabDropPreviewList;
    private int? _lastTabDropInsertionIndex;
    private ListBox? _externalTabDropPreviewList;
    private int? _externalTabDropPreviewInsertionIndex;
    private int _externalTabDropPreviewDraggedTabCount;
    private ListBoxItem? _externalTabDropPreviewTrailingItem;
    private Thickness _externalTabDropPreviewTrailingItemMargin;
    private bool _disposed;

    internal TabReorderController(
        ObservableCollection<BrowserTab> tabs,
        TabStripController tabStripController,
        TabDragDropService tabDragDropService,
        TabOperationService tabOperationService,
        OwnedDispatcherTimer clearTimer,
        ITabReorderHost host)
    {
        _tabs = tabs;
        _tabStripController = tabStripController;
        _tabDragDropService = tabDragDropService;
        _tabOperationService = tabOperationService;
        _dropPreviewClearTimer = clearTimer;
        _host = host;
        _dropPreviewClearTimer.Tick += DropPreviewClearTimer_Tick;
    }

    private void DropPreviewClearTimer_Tick(object? sender, EventArgs e)
    {
        _dropPreviewClearTimer.Stop();
        ClearDropPreview();
    }

    internal void ResetDropPreviewTarget()
    {
        _lastTabDropPreviewList = null;
        _lastTabDropInsertionIndex = null;
    }

    internal void ReorderTab(BrowserTab draggedTab, Point dropPoint, object originalSource)
    {
        if (_disposed) return;
        var tabList = _host.ResolveTabList(originalSource);
        var moved = PreviewReorderTab(draggedTab, tabList, dropPoint, originalSource);
        if (!moved)
        {
            _host.SelectTab(draggedTab);
            return;
        }

        _host.SelectTab(draggedTab);
        _host.QueueSessionSave();
        _host.SetStatus("タブ順序を変更しました", autoClear: true);
    }

    internal void ConfirmReorderDrop(BrowserTab draggedTab, Point dropPoint, object originalSource)
    {
        if (_disposed) return;
        ConfirmReorderDrop(draggedTab, _host.ResolveTabList(originalSource), dropPoint, originalSource);
    }

    internal void ConfirmReorderDrop(
        BrowserTab draggedTab,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
    {
        if (_disposed) return;
        PreviewReorderTab(draggedTab, tabList, dropPoint, originalSource);
        _host.SelectTab(draggedTab);
        _host.QueueSessionSave();
        _host.RegisterReorderUndo();
    }

    internal bool PreviewReorderTab(BrowserTab draggedTab, Point dropPoint, object originalSource)
    {
        if (_disposed) return false;
        return PreviewReorderTab(draggedTab, _host.ResolveTabList(originalSource), dropPoint, originalSource);
    }

    internal bool PreviewReorderTab(
        BrowserTab draggedTab,
        ListBox tabList,
        Point dropPoint,
        object originalSource)
    {
        if (_disposed) return false;
        if (!_tabDragDropService.CanStartTabDrag(draggedTab.IsWorkspaceTab))
        {
            return false;
        }

        if (!_host.CanDropTabOnList(draggedTab, tabList))
        {
            return false;
        }

        var insertionIndex = GetInsertionIndexForDrop(tabList, dropPoint, originalSource);
        var draggedTabs = GetTabDragGroup(draggedTab);
        var groupIndices = draggedTabs
            .Select(_tabs.IndexOf)
            .Where(static index => index >= 0)
            .ToList();
        if (groupIndices.Count == 0)
        {
            return false;
        }

        var originalTabs = _tabs.ToList();
        var reorderedIndices = _tabOperationService.GetReorderedIndicesForGroup(
            _tabs.Count,
            groupIndices,
            insertionIndex);
        var reorderedTabs = reorderedIndices.Select(index => originalTabs[index]).ToList();
        if (originalTabs.SequenceEqual(reorderedTabs))
        {
            _host.SelectTab(draggedTab);
            return false;
        }

        var previousPositions = _tabStripController.CaptureTabLayoutPositions(tabList);
        for (var targetIndex = 0; targetIndex < reorderedTabs.Count; targetIndex++)
        {
            var tab = reorderedTabs[targetIndex];
            var currentIndex = _tabs.IndexOf(tab);
            if (currentIndex != targetIndex)
            {
                _tabs.Move(currentIndex, targetIndex);
            }
        }

        _tabStripController.AnimateTabLayoutFrom(tabList, previousPositions);
        _host.SelectTab(draggedTab);
        return true;
    }

    internal IReadOnlyList<BrowserTab> GetTabDragGroup(BrowserTab tab)
    {
        if (!_tabs.Contains(tab) || string.IsNullOrWhiteSpace(tab.SplitViewId))
        {
            return [tab];
        }

        var splitTabs = _tabs
            .Where(candidate => string.Equals(
                candidate.SplitViewId,
                tab.SplitViewId,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        return splitTabs.Count == 2 ? splitTabs : [tab];
    }

    internal int GetDropTargetIndex(BrowserTab draggedTab, Point dropPoint, object originalSource)
    {
        if (_disposed) return 0;
        var insertionIndex = GetInsertionIndexForDrop(dropPoint, originalSource);
        var oldIndex = _tabs.IndexOf(draggedTab);
        if (oldIndex < 0)
        {
            return 0;
        }

        return _tabOperationService.GetReorderTargetIndex(
            oldIndex,
            insertionIndex,
            _tabs.Count);
    }

    internal int GetInsertionIndexForDrop(Point dropPoint, object originalSource)
    {
        if (_disposed) return 0;
        return GetInsertionIndexForDrop(_host.ResolveTabList(originalSource), dropPoint, originalSource);
    }

    internal int GetInsertionIndexForDrop(ListBox tabList, Point dropPoint, object originalSource)
    {
        if (_disposed) return 0;
        _host.EnsureWorkspaceTabIsFirst();
        if (_tabs.Count == 0)
        {
            return 0;
        }

        var visibleTabs = tabList.Items.OfType<BrowserTab>().Where(_tabs.Contains).ToList();
        var targetIndex = visibleTabs.Count > 0
            ? _tabs.IndexOf(visibleTabs[^1]) + 1
            : _tabs.Count;
        var targetItem = FindVisualParent<ListBoxItem>(originalSource as DependencyObject);
        if (targetItem is not null
            && !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(targetItem), tabList))
        {
            targetItem = null;
        }

        targetItem ??= FindNearestTabItem(tabList, dropPoint);

        if (targetItem?.DataContext is BrowserTab targetTab && _tabs.Contains(targetTab))
        {
            targetIndex = _tabs.IndexOf(targetTab);
            var targetOrigin = targetItem.TranslatePoint(new Point(0, 0), tabList);
            var insertAfter = TabStripController.IsVerticalTabList(tabList)
                ? dropPoint.Y > targetOrigin.Y + (targetItem.ActualHeight / 2)
                : dropPoint.X > targetOrigin.X + (targetItem.ActualWidth / 2);
            if (insertAfter)
            {
                targetIndex++;
            }
        }

        return _host.ClampNormalTabInsertionIndex(targetIndex);
    }

    internal bool UpdateDropPreview(Point dropPoint, object originalSource)
    {
        if (_disposed) return false;
        return UpdateDropPreview(_host.ResolveTabList(originalSource), dropPoint, originalSource);
    }

    internal bool UpdateDropPreview(ListBox tabList, Point dropPoint, object originalSource)
    {
        if (_disposed) return false;
        _dropPreviewClearTimer.Stop();
        if (_tabs.Count == 0)
        {
            return false;
        }

        var insertionIndex = GetInsertionIndexForDrop(tabList, dropPoint, originalSource);
        if (ReferenceEquals(_lastTabDropPreviewList, tabList)
            && _lastTabDropInsertionIndex == insertionIndex)
        {
            return false;
        }

        ClearDropPreviewCore(resetTarget: false);
        _lastTabDropPreviewList = tabList;
        _lastTabDropInsertionIndex = insertionIndex;
        var visibleTabs = tabList.Items.OfType<BrowserTab>().Where(_tabs.Contains).ToList();
        if (visibleTabs.Count == 0)
        {
            return true;
        }

        var targetTab = visibleTabs.FirstOrDefault(tab => _tabs.IndexOf(tab) >= insertionIndex);
        if (targetTab is null)
        {
            visibleTabs[^1].IsDropPreviewAfter = true;
            return true;
        }

        targetTab.IsDropPreviewBefore = true;
        return true;
    }

    internal void RestoreTabOrder(IReadOnlyList<BrowserTab> originalOrder)
    {
        if (_disposed) return;
        var orderedTabs = originalOrder.Where(_tabs.Contains).ToList();
        for (var targetIndex = 0; targetIndex < orderedTabs.Count; targetIndex++)
        {
            var currentIndex = _tabs.IndexOf(orderedTabs[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                _tabs.Move(currentIndex, targetIndex);
            }
        }

        _host.EnsureWorkspaceTabIsFirst();
    }

    internal void ClearDropPreview(bool animateExternalPreview = true)
    {
        if (_disposed) return;
        ClearDropPreviewCore(resetTarget: true);
        ClearExternalTabDropPreview(animateExternalPreview);
    }

    internal void ClearDropPreviewWithFollowUp(bool animateExternalPreview = true)
    {
        if (_disposed) return;
        ClearDropPreview(animateExternalPreview);
        QueueDropPreviewClear();
    }

    internal void QueueDropPreviewClear()
    {
        if (_disposed) return;
        _dropPreviewClearTimer.Stop();
        _dropPreviewClearTimer.Start();
    }

    internal void UpdateExternalTabDropPreview(
        ListBox tabList,
        int insertionIndex,
        int draggedTabCount)
    {
        if (_disposed) return;
        if (ReferenceEquals(_externalTabDropPreviewList, tabList)
            && _externalTabDropPreviewInsertionIndex == insertionIndex
            && _externalTabDropPreviewDraggedTabCount == draggedTabCount)
        {
            return;
        }

        if (_externalTabDropPreviewList is not null
            && !ReferenceEquals(_externalTabDropPreviewList, tabList))
        {
            ApplyExternalTabDropPreviewOffsets(
                _externalTabDropPreviewList,
                insertionIndex: null,
                draggedTabCount: 1,
                animate: true);
        }

        _externalTabDropPreviewList = tabList;
        _externalTabDropPreviewInsertionIndex = insertionIndex;
        _externalTabDropPreviewDraggedTabCount = draggedTabCount;
        ApplyExternalTabDropPreviewOffsets(tabList, insertionIndex, draggedTabCount, animate: true);
    }

    internal bool ShouldKeepExternalTabDropPreviewAtCursor()
    {
        return _externalTabDropPreviewList is { } tabList
            && _host.TryGetCursorPointInTabList(tabList, out var point)
            && !_tabDragDropService.IsPointOutsideBounds(
                point.X,
                point.Y,
                tabList.ActualWidth,
                tabList.ActualHeight,
                tolerance: 0);
    }

    private ListBoxItem? FindNearestTabItem(ListBox tabList, Point point)
    {
        ListBoxItem? nearestItem = null;
        var nearestDistance = double.MaxValue;

        foreach (var tab in tabList.Items.OfType<BrowserTab>())
        {
            if (tabList.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item)
            {
                continue;
            }

            var origin = item.TranslatePoint(new Point(0, 0), tabList);
            var centerX = origin.X + item.ActualWidth / 2;
            var centerY = origin.Y + item.ActualHeight / 2;
            var distance = Math.Pow(point.X - centerX, 2) + Math.Pow(point.Y - centerY, 2);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestItem = item;
            }
        }

        return nearestItem;
    }

    private void ClearDropPreviewCore(bool resetTarget)
    {
        foreach (var tab in _tabs)
        {
            tab.IsDropPreviewBefore = false;
            tab.IsDropPreviewAfter = false;
        }

        if (resetTarget)
        {
            _lastTabDropPreviewList = null;
            _lastTabDropInsertionIndex = null;
        }
    }

    private void ClearExternalTabDropPreview(bool animate)
    {
        if (_externalTabDropPreviewList is not { } tabList)
        {
            return;
        }

        _externalTabDropPreviewList = null;
        _externalTabDropPreviewInsertionIndex = null;
        _externalTabDropPreviewDraggedTabCount = 0;
        ApplyExternalTabDropPreviewOffsets(
            tabList,
            insertionIndex: null,
            draggedTabCount: 1,
            animate);
    }

    private void ApplyExternalTabDropPreviewOffsets(
        ListBox tabList,
        int? insertionIndex,
        int draggedTabCount,
        bool animate)
    {
        RestoreExternalTabDropPreviewTrailingGap(animate);
        var visibleTabs = tabList.Items.OfType<BrowserTab>().Where(_tabs.Contains).ToList();
        var isVertical = TabStripController.IsVerticalTabList(tabList);
        var previewExtent = 0d;
        BrowserTab? targetTab = null;

        if (insertionIndex is { } targetIndex)
        {
            targetTab = visibleTabs.FirstOrDefault(tab =>
                _tabDragDropService.ShouldShiftTabForExternalDropPreview(
                    _tabs.IndexOf(tab),
                    targetIndex));
            if (targetTab is not null
                && tabList.ItemContainerGenerator.ContainerFromItem(targetTab) is ListBoxItem targetItem)
            {
                previewExtent = _tabDragDropService.GetExternalDropPreviewDistance(
                    isVertical ? targetItem.ActualHeight : targetItem.ActualWidth,
                    draggedTabCount);
            }
            else if (visibleTabs.LastOrDefault() is { } trailingTab
                && tabList.ItemContainerGenerator.ContainerFromItem(trailingTab) is ListBoxItem trailingItem)
            {
                previewExtent = _tabDragDropService.GetExternalDropPreviewDistance(
                    isVertical ? trailingItem.ActualHeight : trailingItem.ActualWidth,
                    draggedTabCount);
                ApplyExternalTabDropPreviewTrailingGap(
                    trailingItem,
                    isVertical,
                    previewExtent,
                    animate);
            }
        }

        foreach (var tab in visibleTabs)
        {
            if (tabList.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item)
            {
                continue;
            }

            var shouldShift = insertionIndex is { } shiftInsertionIndex
                && previewExtent > 0
                && _tabDragDropService.ShouldShiftTabForExternalDropPreview(
                    _tabs.IndexOf(tab),
                    shiftInsertionIndex);
            TabStripController.AnimateTabItemTranslateTo(
                item,
                isVertical ? 0 : shouldShift ? previewExtent : 0,
                isVertical && shouldShift ? previewExtent : 0,
                animate);
        }
    }

    private void ApplyExternalTabDropPreviewTrailingGap(
        ListBoxItem item,
        bool isVertical,
        double previewExtent,
        bool animate)
    {
        _externalTabDropPreviewTrailingItem = item;
        _externalTabDropPreviewTrailingItemMargin = item.Margin;
        var targetMargin = isVertical
            ? new Thickness(
                item.Margin.Left,
                item.Margin.Top,
                item.Margin.Right,
                item.Margin.Bottom + previewExtent)
            : new Thickness(
                item.Margin.Left,
                item.Margin.Top,
                item.Margin.Right + previewExtent,
                item.Margin.Bottom);
        TabStripController.AnimateTabItemMarginTo(item, targetMargin, animate);
    }

    private void RestoreExternalTabDropPreviewTrailingGap(bool animate)
    {
        if (_externalTabDropPreviewTrailingItem is not { } item)
        {
            return;
        }

        var originalMargin = _externalTabDropPreviewTrailingItemMargin;
        _externalTabDropPreviewTrailingItem = null;
        _externalTabDropPreviewTrailingItemMargin = default;
        TabStripController.AnimateTabItemMarginTo(item, originalMargin, animate);
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target) return target;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dropPreviewClearTimer.Stop();
        _dropPreviewClearTimer.Tick -= DropPreviewClearTimer_Tick;
        ClearDropPreview(animateExternalPreview: false);
        _disposed = true;
    }
}
