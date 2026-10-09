using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ITabDragOperation
{
    bool IsSourceOriginal { get; }
    DragDropEffects Run(ListBox dragList);
    bool TryTransferOriginalSourceAtCursor();
    void TryTransferCurrentSourceAtCursor();
    void DetachOriginalTab(BrowserTab tab);
}

internal interface ITabDragHost
{
    BrowserTab? FindTabFromOriginalSource(object originalSource);
    ListBox GetActiveTabDragList();
    ITabDragOperation CreateOperation(BrowserTab tab);
    void CompleteOperation();
    void SelectTab(BrowserTab tab);
    void SetStatus(string message, bool autoClear = false);
    void ClearDragStatus();
    void ClearSourceDrivenPreview();
    void ResetWorkspaceHover();
    bool ShouldSuppressDetachForWorkspaceDrop();
    bool IsCursorOverOtherWindow();
    bool IsPointOutsideWindow(Point point);
    Point GetMousePositionInWindow();
    Point GetCursorScreenPoint();
    bool TryGetCursorPointInTabList(ListBox tabList, out Point point);
    bool HasLoadedRuntime(BrowserTab tab);
    void QueueThumbnailCapture(BrowserTab tab);
}

internal sealed record TabDragElements(
    FrameworkElement InputSurface,
    ListBox DefaultList,
    Popup Popup,
    Border Preview,
    TextBlock PreviewText,
    FrameworkElement WindowPreviewBody);

// 入力・ドラッグ表示・終了判断を所有し、実データ形式とWindow転送はhostへ残す。
internal sealed class TabDragController : IDisposable
{
    private const double TabDetachTolerance = 140;
    private const double TabDetachPreviewDistance = 32;
    internal const double TabReorderDropTolerance = 260;
    private const int TabReorderDropGraceMilliseconds = 700;
    private const string TabDragStatusPrefix = "ドラッグ中: ";
    private readonly TabDragElements _elements;
    private readonly ObservableCollection<BrowserTab> _tabs;
    private readonly TabStripController _tabStrip;
    private readonly TabReorderController _reorder;
    private readonly TabDragDropService _dragDrop;
    private readonly ITabDragHost _host;
    private readonly TabDragSessionCoordinator _tabDragSessionCoordinator = new();
    private BrowserTab? _dragSourceTab;
    private ListBox? _dragSourceList;
    private BrowserTab? _activeDragTab;
    private ListBox? _activeDragList;
    private List<BrowserTab>? _dragOriginalTabs;
    private Point _dragStartPoint;
    private bool _disposed;

    internal TabDragController(TabDragElements elements, ObservableCollection<BrowserTab> tabs,
        TabStripController tabStrip, TabReorderController reorder, TabDragDropService dragDrop, ITabDragHost host)
    {
        _elements = elements;
        _tabs = tabs;
        _tabStrip = tabStrip;
        _reorder = reorder;
        _dragDrop = dragDrop;
        _host = host;
    }

    internal BrowserTab? ActiveTab => _activeDragTab;
    internal ListBox? ActiveList => _activeDragList;
    internal IReadOnlyList<BrowserTab>? OriginalOrder => _dragOriginalTabs;
    internal bool IsDetachPreviewActive => _tabDragSessionCoordinator.IsDetachPreviewActive;
    private Popup TabDragPopup => _elements.Popup;
    private Border TabDragPreview => _elements.Preview;
    private TextBlock TabDragPreviewText => _elements.PreviewText;
    private FrameworkElement TabDragWindowPreviewBody => _elements.WindowPreviewBody;
    private double TabItemWidth => _tabStrip.TabItemWidth;

    internal void MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_disposed) return;
        _reorder.ClearDropPreview();
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            _dragSourceTab = null;
            _dragSourceList = null;
            return;
        }
        BeginCandidate(e.GetPosition(_elements.InputSurface), sender as ListBox ?? _elements.DefaultList,
            _host.FindTabFromOriginalSource(e.OriginalSource));
    }

    internal void BeginCandidate(Point point, ListBox list, BrowserTab? candidate)
    {
        if (_disposed) return;
        _dragStartPoint = point;
        _dragSourceList = list;
        _dragSourceTab = candidate is not null && _dragDrop.CanStartTabDrag(candidate.IsWorkspaceTab) ? candidate : null;
    }

    internal void MouseMove(object sender, MouseEventArgs e)
    {
        if (_disposed || _dragSourceTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        ContinueCandidate(e.GetPosition(_elements.InputSurface), leftButtonPressed: true, sender as ListBox);
    }

    internal void ContinueCandidate(Point point, bool leftButtonPressed, ListBox? fallbackList = null)
    {
        if (_disposed || _dragSourceTab is null || !leftButtonPressed) return;
        if (Math.Abs(point.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tab = _dragSourceTab;
        var dragList = _dragSourceList ?? fallbackList ?? _elements.DefaultList;
        _dragSourceTab = null;
        _dragSourceList = null;
        _activeDragTab = tab;
        _activeDragList = dragList;
        _dragOriginalTabs = _tabs.ToList();
        _tabDragSessionCoordinator.BeginInteractiveDrag();
        foreach (var draggedTab in _reorder.GetTabDragGroup(tab)) draggedTab.IsDragSource = true;
        ShowTabDragPreview(tab);
        _host.SetStatus($"{TabDragStatusPrefix}{tab.DisplayTitle}");

        var result = DragDropEffects.None;
        var workspaceDropRouteActive = false;
        var detachPreviewAtRelease = false;
        var operation = _host.CreateOperation(tab);
        try
        {
            result = operation.Run(dragList);
        }
        finally
        {
            if (!_disposed)
            {
                workspaceDropRouteActive = _host.ShouldSuppressDetachForWorkspaceDrop();
                detachPreviewAtRelease = _tabDragSessionCoordinator.IsDetachPreviewActive;
                ClearTabDragVisualState();
            }
        }
        if (_disposed) return;

        if (_tabDragSessionCoordinator.IsCancelled)
        {
            RestoreOriginalTabOrder();
            if (_tabs.Contains(tab)) _host.SelectTab(tab);
            _host.SetStatus("タブ移動をキャンセルしました", autoClear: true);
            CompleteTabDragCleanup();
            return;
        }
        if (_tabDragSessionCoordinator.IsDetachedToWindow)
        {
            if (result == DragDropEffects.None && !operation.IsSourceOriginal) operation.TryTransferCurrentSourceAtCursor();
            CompleteTabDragCleanup();
            return;
        }
        if (result == DragDropEffects.None && workspaceDropRouteActive && _tabs.Contains(tab))
        {
            RestoreOriginalTabOrder();
            CompleteTabDragCleanup();
            return;
        }
        if (result == DragDropEffects.None && _tabs.Contains(tab) && operation.TryTransferOriginalSourceAtCursor())
        {
            CompleteTabDragCleanup();
            return;
        }
        if (result == DragDropEffects.None && _tabs.Contains(tab)
            && (_tabDragSessionCoordinator.ShouldCompleteAsNewWindow || detachPreviewAtRelease
                || _host.IsPointOutsideWindow(_host.GetMousePositionInWindow())))
        {
            RestoreOriginalTabOrder();
            operation.DetachOriginalTab(tab);
        }
        else if (result == DragDropEffects.None && _tabs.Contains(tab))
        {
            RestoreOriginalTabOrder();
            _host.SelectTab(tab);
            _host.SetStatus("タブ移動をキャンセルしました", autoClear: true);
        }
        if (result == DragDropEffects.Copy && _tabs.Contains(tab)) RestoreOriginalTabOrder();
        CompleteTabDragCleanup();
    }

    internal void QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (_disposed || !e.EscapePressed || _activeDragTab is null) return;
        _tabDragSessionCoordinator.RequestCancel();
        e.Action = DragAction.Cancel;
        e.Handled = true;
    }

    private void CompleteTabDragCleanup()
    {
        _dragOriginalTabs = null;
        _activeDragList = null;
        _tabDragSessionCoordinator.ClearDetachedToWindow();
        _tabDragSessionCoordinator.ClearCompleteAsNewWindow();
        _tabDragSessionCoordinator.TrySetDetachPreviewActive(false);
        _host.CompleteOperation();
    }

    internal void RestoreOriginalTabOrder()
    {
        if (!_disposed && _dragOriginalTabs is not null) _reorder.RestoreTabOrder(_dragOriginalTabs);
    }

    private void ClearTabDragVisualState()
    {
        _host.ClearDragStatus();
        _host.ClearSourceDrivenPreview();
        HideTabDragPreview();
        ResetTabDetachCandidate();
        ResetTabReorderCandidate();
        _host.ResetWorkspaceHover();
        ClearTabFlags();
        _activeDragTab = null;
        _reorder.QueueDropPreviewClear();
    }

    private void ClearTabFlags()
    {
        foreach (var tab in _tabs)
        {
            tab.IsDragSource = false;
            tab.IsDropPreviewBefore = false;
            tab.IsDropPreviewAfter = false;
        }
    }

    internal void BeginTabDetachCandidate()
    {
        if (_disposed) return;
        var list = _host.GetActiveTabDragList();
        if (!_host.TryGetCursorPointInTabList(list, out var point))
        {
            ResetTabDetachCandidate();
            return;
        }
        UpdateTabDetachPreview(list, point);
    }

    internal void ResetTabDetachCandidate()
    {
        if (_disposed || !_tabDragSessionCoordinator.TrySetDetachPreviewActive(false)) return;
        ShowTabDragPreviewMode(showWindowPreview: false);
    }

    internal void UpdateTabDetachPreview(ListBox tabList, Point tabListPoint)
    {
        if (_disposed) return;
        if (_activeDragTab is not { } tab || !_tabs.Contains(tab))
        {
            ResetTabDetachCandidate();
            return;
        }
        var shouldShow = _dragDrop.ShouldShowDetachPreview(
            tab.IsWorkspaceTab, TabStripController.IsVerticalTabList(tabList), tabListPoint.X, tabListPoint.Y,
            tabList.ActualWidth, tabList.ActualHeight, TabDetachPreviewDistance,
            _host.IsCursorOverOtherWindow(), _host.ShouldSuppressDetachForWorkspaceDrop());
        if (!_tabDragSessionCoordinator.TrySetDetachPreviewActive(shouldShow)) return;
        ShowTabDragPreviewMode(showWindowPreview: shouldShow);
    }

    internal void MarkTabReorderCandidate()
    {
        if (!_disposed) _tabDragSessionCoordinator.MarkReorderCandidate(DateTimeOffset.Now);
    }

    internal void ResetTabReorderCandidate() => _tabDragSessionCoordinator.ClearReorderCandidate();
    internal void RequestCompleteAsNewWindow() { if (!_disposed) _tabDragSessionCoordinator.RequestCompleteAsNewWindow(); }
    internal void MarkDetachedToWindow() { if (!_disposed) _tabDragSessionCoordinator.MarkDetachedToWindow(); }
    internal void ClearDetachedToWindow() => _tabDragSessionCoordinator.ClearDetachedToWindow();

    internal bool ShouldConfirmReorderFromRootDrop(ListBox tabList, Point tabListPoint)
    {
        return !_disposed && _dragDrop.ShouldConfirmReorderFromRootDrop(
            tabListPoint.X, tabListPoint.Y, tabList.ActualWidth, tabList.ActualHeight,
            TabDetachTolerance, TabReorderDropTolerance, _tabDragSessionCoordinator.ReorderCandidateAt,
            DateTimeOffset.Now, TabReorderDropGraceMilliseconds);
    }

    internal void ShowTabDragPreview(BrowserTab tab)
    {
        if (_disposed) return;
        TabDragPreviewText.Text = _reorder.GetTabDragGroup(tab).Count == 2 ? $"分割ペア: {tab.DisplayTitle}" : tab.DisplayTitle;
        TabDragWindowPreviewBody.DataContext = tab;
        if (!tab.HasThumbnail && _host.HasLoadedRuntime(tab) && !tab.IsLoading) _host.QueueThumbnailCapture(tab);
        ShowTabDragPreviewMode(showWindowPreview: false);
        TabDragPopup.IsOpen = true;
        MoveTabDragPreview();
    }

    internal void QueueTabDragPreviewMove()
    {
        if (_disposed || !TabDragPopup.IsOpen || !_tabDragSessionCoordinator.TryQueuePreviewMove()) return;
        _ = _elements.InputSurface.Dispatcher.BeginInvoke(new Action(() =>
        {
            _tabDragSessionCoordinator.CompletePreviewMove();
            MoveTabDragPreview();
        }), DispatcherPriority.Render);
    }

    internal void MoveTabDragPreview()
    {
        if (_disposed || !TabDragPopup.IsOpen) return;
        var previewWidth = Math.Max(TabItemWidth, TabDragPreview.ActualWidth);
        var cursor = _host.GetCursorScreenPoint();
        var left = cursor.X - Math.Min(48, previewWidth / 3);
        var top = cursor.Y - (_tabDragSessionCoordinator.IsDetachPreviewActive ? 28 : 18);
        TabDragPopup.BeginAnimation(Popup.HorizontalOffsetProperty, null);
        TabDragPopup.BeginAnimation(Popup.VerticalOffsetProperty, null);
        TabDragPopup.HorizontalOffset = left;
        TabDragPopup.VerticalOffset = top;
    }

    internal void HideTabDragPreview()
    {
        TabDragPopup.BeginAnimation(Popup.HorizontalOffsetProperty, null);
        TabDragPopup.BeginAnimation(Popup.VerticalOffsetProperty, null);
        TabDragPopup.IsOpen = false;
        TabDragPreviewText.Text = string.Empty;
        TabDragWindowPreviewBody.DataContext = null;
        _tabDragSessionCoordinator.ClearPreviewMoveQueue();
    }

    private void ShowTabDragPreviewMode(bool showWindowPreview)
    {
        TabDragPreview.Width = showWindowPreview ? 320 : TabItemWidth;
        TabDragPreview.Height = showWindowPreview ? 180 : 32;
        TabDragPreview.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(showWindowPreview ? "#38BDF8" : "#FACC15"));
        TabDragWindowPreviewBody.Visibility = showWindowPreview ? Visibility.Visible : Visibility.Collapsed;
        QueueTabDragPreviewMove();
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T found) return found;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HideTabDragPreview();
        _host.ClearSourceDrivenPreview();
        _host.ResetWorkspaceHover();
        ClearTabFlags();
        _dragSourceTab = null;
        _dragSourceList = null;
        _activeDragTab = null;
        CompleteTabDragCleanup();
    }
}
