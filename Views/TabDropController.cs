using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ITabDropPayload
{
    BrowserTab Tab { get; }
    bool IsFrom(ITabDropHost host);
    int GroupCount { get; }
}

internal interface ITabDropSnapshot
{
    bool IsWorkspaceTab { get; }
}

internal interface ITabDropSiteInformation
{
    Guid SourceWindowId { get; }
    Guid SourceTabId { get; }
}

internal interface ITabDropHost
{
    Guid WindowId { get; }
    Guid? ActiveBrowserCommandTargetId { get; }
    BrowserTab? ActiveTab { get; }
    bool IsClosing { get; }
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    ListBox GetActiveTabDragList();
    BrowserTab? FindTabFromOriginalSource(object originalSource);
    bool CanDropTabOnList(BrowserTab tab, ListBox tabList);
    bool TryGetSiteInformation(DragEventArgs e, out ITabDropSiteInformation source);
    bool TryGetPayload(DragEventArgs e, out ITabDropPayload payload);
    bool TryGetSnapshot(DragEventArgs e, out ITabDropSnapshot snapshot);
    bool TryGetExternalDrop(DragEventArgs e, out ExternalTabDropTarget externalDrop);
    bool HasFileDrop(DragEventArgs e);
    Task<bool> TryHandleSiteInformationDropAsync(DragEventArgs e, bool dropOnTabList);
    bool TrySaveDraggedTabToActiveWorkspace(BrowserTab tab, Point point);
    bool InsertExternalTab(ITabDropPayload payload, ListBox list, Point point, object originalSource);
    bool InsertTransferredTab(ITabDropSnapshot snapshot, ListBox list, Point point, object originalSource);
    void OpenExternalDrop(ExternalTabDropTarget target, ListBox list, Point point, object originalSource);
    void UpdateSourceDrivenExternalPreview();
    bool TryGetCursorPointInTabList(ListBox list, out Point point);
}

internal sealed record TabDropElements(ListBox DefaultList, FrameworkElement RootSurface);

// 受付順序と表示・確定の調停を所有し、データ検証と実転送は既存hostへ残す。
internal sealed class TabDropController : IDisposable
{
    internal const int ExternalTabDragHoverDelayMilliseconds = 600;
    private readonly TabDropElements _elements;
    private readonly ObservableCollection<BrowserTab> _tabs;
    private readonly TabReorderController _reorder;
    private readonly TabDragController _drag;
    private readonly FolderTabFileDropController _folderDrop;
    private readonly TabDragDropService _policy;
    private readonly ITabDropHost _host;
    private readonly OwnedDispatcherTimer _externalHoverTimer;
    private BrowserTab? _externalHoverTarget;
    private bool _workspaceHoverActivated;
    private bool _disposed;

    internal TabDropController(TabDropElements elements, ObservableCollection<BrowserTab> tabs,
        TabReorderController reorder, TabDragController drag, FolderTabFileDropController folderDrop,
        TabDragDropService policy, OwnedDispatcherTimer externalHoverTimer, ITabDropHost host)
    {
        _elements = elements;
        _tabs = tabs;
        _reorder = reorder;
        _drag = drag;
        _folderDrop = folderDrop;
        _policy = policy;
        _host = host;
        _externalHoverTimer = externalHoverTimer;
        _externalHoverTimer.Tick += ExternalHoverTimer_Tick;
    }

    private ListBox TabList => _elements.DefaultList;
    internal bool WorkspaceHoverActivated => _workspaceHoverActivated;

    private bool IsSameSiteInformationSource(ITabDropSiteInformation source, BrowserTab? target)
        => target is not null && source.SourceWindowId == _host.WindowId && source.SourceTabId == target.Id;

    internal bool IsPointOutsideTabReorderDropZone(ListBox list, Point point)
        => _policy.IsPointOutsideBounds(point.X, point.Y, list.ActualWidth, list.ActualHeight, TabDragController.TabReorderDropTolerance);

    internal void TabList_DragOver(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        var tabList = sender as ListBox ?? TabList;
        var tabListPoint = e.GetPosition(tabList);
        if (_folderDrop.TryHandleDragOver(e))
        {
            ResetExternalTabDragHover();
            return;
        }

        if (_host.TryGetSiteInformation(e, out var siteInformationDrag))
        {
            UpdateExternalTabDragHover(tabList, tabListPoint, e.OriginalSource);
            ResetWorkspaceTabDragHover();
            _drag.ResetTabDetachCandidate();
            var targetTab = _host.FindTabFromOriginalSource(e.OriginalSource);
            if (targetTab is { IsNativeContentTab: false })
            {
                _reorder.ClearDropPreview();
            }
            else
            {
                _reorder.UpdateDropPreview(tabList, tabListPoint, e.OriginalSource);
            }

            e.Effects = IsSameSiteInformationSource(siteInformationDrag, targetTab)
                ? DragDropEffects.None
                : DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        if (_host.TryGetPayload(e, out var payload))
        {
            ResetExternalTabDragHover();
            _drag.ResetTabDetachCandidate();
            _drag.QueueTabDragPreviewMove();

            if (!_host.CanDropTabOnList(payload.Tab, tabList))
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var suppressWorkspaceReorder = _policy.ShouldSuppressWorkspaceReorderPreview(
                UpdateWorkspaceTabDragHover(
                    tabList,
                    tabListPoint,
                    e.OriginalSource,
                    payload.Tab.IsWorkspaceTab),
                payload.IsFrom(_host));
            if (suppressWorkspaceReorder)
            {
                _reorder.ClearDropPreview();
            }
            else
            {
                _drag.MarkTabReorderCandidate();
                var targetChanged = _reorder.UpdateDropPreview(tabList, tabListPoint, e.OriginalSource);
                if (!_workspaceHoverActivated
                    && targetChanged
                    && payload.IsFrom(_host)
                    && _tabs.Contains(payload.Tab))
                {
                    _reorder.PreviewReorderTab(payload.Tab, tabList, tabListPoint, e.OriginalSource);
                }
                else if (!_workspaceHoverActivated
                    && targetChanged
                    && !payload.IsFrom(_host))
                {
                    _reorder.UpdateExternalTabDropPreview(
                        tabList,
                        _reorder.GetInsertionIndexForDrop(tabList, tabListPoint, e.OriginalSource),
                        payload.GroupCount);
                }
            }

            e.Effects = DragDropEffects.Move;
        }
        else if (_host.TryGetSnapshot(e, out var snapshot))
        {
            ResetExternalTabDragHover();
            _drag.ResetTabDetachCandidate();
            if (UpdateWorkspaceTabDragHover(tabList, tabListPoint, e.OriginalSource, snapshot.IsWorkspaceTab))
            {
                _reorder.ClearDropPreview();
            }
            else
            {
                _drag.MarkTabReorderCandidate();
                if (_reorder.UpdateDropPreview(tabList, tabListPoint, e.OriginalSource))
                {
                    _reorder.UpdateExternalTabDropPreview(
                        tabList,
                        _reorder.GetInsertionIndexForDrop(tabList, tabListPoint, e.OriginalSource),
                        draggedTabCount: 1);
                }
            }

            e.Effects = DragDropEffects.Move;
        }
        else if (_host.TryGetExternalDrop(e, out _))
        {
            UpdateExternalTabDragHover(tabList, tabListPoint, e.OriginalSource);
            ResetWorkspaceTabDragHover();
            _drag.ResetTabDetachCandidate();
            _reorder.UpdateDropPreview(tabList, tabListPoint, e.OriginalSource);
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            ResetExternalTabDragHover();
            ResetWorkspaceTabDragHover();
            _reorder.ClearDropPreview();
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    internal void TabList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_disposed) return;
        if (_drag.ActiveTab is not null)
        {
            _drag.QueueTabDragPreviewMove();
            _host.UpdateSourceDrivenExternalPreview();
            var tabList = _host.GetActiveTabDragList();
            if (_host.TryGetCursorPointInTabList(tabList, out var tabListPoint))
            {
                _drag.UpdateTabDetachPreview(tabList, tabListPoint);
            }
        }

        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    internal void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        var tabList = _host.GetActiveTabDragList();
        if (_host.TryGetSiteInformation(e, out var siteInformationDrag))
        {
            ResetWorkspaceTabDragHover();
            _drag.ResetTabDetachCandidate();
            var targetTab = _host.FindTabFromOriginalSource(e.OriginalSource);
            var tabListPoint = e.GetPosition(tabList);
            UpdateExternalTabDragHover(tabList, tabListPoint, e.OriginalSource);
            if (targetTab is { IsNativeContentTab: false })
            {
                _reorder.ClearDropPreview();
            }
            else if (!IsPointOutsideTabReorderDropZone(tabList, tabListPoint))
            {
                _reorder.UpdateDropPreview(tabList, tabListPoint, e.OriginalSource);
            }
            else
            {
                _reorder.ClearDropPreview();
            }

            var droppedBackOnSource = IsSameSiteInformationSource(siteInformationDrag, targetTab)
                || targetTab is null
                && siteInformationDrag.SourceWindowId == _host.WindowId
                && _host.ActiveBrowserCommandTargetId == siteInformationDrag.SourceTabId;
            e.Effects = droppedBackOnSource ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }
        else if (_host.TryGetPayload(e, out var payload))
        {
            ResetExternalTabDragHover();
            _drag.QueueTabDragPreviewMove();
            var tabListPoint = e.GetPosition(tabList);

            var suppressWorkspaceReorder = _policy.ShouldSuppressWorkspaceReorderPreview(
                UpdateWorkspaceTabDragHover(
                    tabList,
                    tabListPoint,
                    e.OriginalSource,
                    payload.Tab.IsWorkspaceTab),
                payload.IsFrom(_host));
            if (suppressWorkspaceReorder)
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (payload.IsFrom(_host))
            {
                _drag.UpdateTabDetachPreview(tabList, tabListPoint);
            }

            if (_drag.IsDetachPreviewActive)
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.None;
            }
            else if (!IsPointOutsideTabReorderDropZone(tabList, tabListPoint))
            {
                _drag.MarkTabReorderCandidate();
                var targetChanged = _reorder.UpdateDropPreview(tabList, tabListPoint, tabList);
                if (!_workspaceHoverActivated
                    && targetChanged
                    && payload.IsFrom(_host)
                    && _tabs.Contains(payload.Tab))
                {
                    _reorder.PreviewReorderTab(payload.Tab, tabList, tabListPoint, tabList);
                }
                else if (!_workspaceHoverActivated
                    && targetChanged
                    && !payload.IsFrom(_host))
                {
                    _reorder.UpdateExternalTabDropPreview(
                        tabList,
                        _reorder.GetInsertionIndexForDrop(tabList, tabListPoint, tabList),
                        payload.GroupCount);
                }

                e.Effects = DragDropEffects.Move;
            }
            else
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }
        else if (_host.TryGetSnapshot(e, out var snapshot))
        {
            ResetExternalTabDragHover();
            var tabListPoint = e.GetPosition(tabList);
            if (UpdateWorkspaceTabDragHover(tabList, tabListPoint, e.OriginalSource, snapshot.IsWorkspaceTab))
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (!IsPointOutsideTabReorderDropZone(tabList, tabListPoint))
            {
                _drag.MarkTabReorderCandidate();
                if (_reorder.UpdateDropPreview(tabList, tabListPoint, tabList))
                {
                    _reorder.UpdateExternalTabDropPreview(
                        tabList,
                        _reorder.GetInsertionIndexForDrop(tabList, tabListPoint, tabList),
                        draggedTabCount: 1);
                }
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }
        else if (_host.TryGetExternalDrop(e, out _))
        {
            ResetWorkspaceTabDragHover();
            var tabListPoint = e.GetPosition(tabList);
            UpdateExternalTabDragHover(tabList, tabListPoint, e.OriginalSource);
            if (!IsPointOutsideTabReorderDropZone(tabList, tabListPoint))
            {
                _reorder.UpdateDropPreview(tabList, tabListPoint, tabList);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                _reorder.ClearDropPreview();
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }
        else if (_host.HasFileDrop(e))
        {
            ResetExternalTabDragHover();
            _folderDrop.ResetHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        else
        {
            ResetExternalTabDragHover();
        }
    }

    internal async Task RootGrid_DropAsync(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        ResetExternalTabDragHover();
        var tabList = _host.GetActiveTabDragList();
        ResetWorkspaceTabDragHover();
        _reorder.ClearDropPreviewWithFollowUp(animateExternalPreview: false);
        if (_host.TryGetSiteInformation(e, out _))
        {
            e.Effects = await _host.TryHandleSiteInformationDropAsync(e, dropOnTabList: false)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }
        else if (_host.TryGetPayload(e, out var payload))
        {
            var tabListPoint = e.GetPosition(tabList);
            if (payload.IsFrom(_host)
                && _tabs.Contains(payload.Tab)
                && _host.TrySaveDraggedTabToActiveWorkspace(payload.Tab, e.GetPosition(_elements.RootSurface)))
            {
                _drag.RestoreOriginalTabOrder();
                _drag.ResetTabDetachCandidate();
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            if (_drag.ShouldConfirmReorderFromRootDrop(tabList, tabListPoint))
            {
                if (payload.IsFrom(_host))
                {
                    if (_tabs.Contains(payload.Tab))
                    {
                        _reorder.ConfirmReorderDrop(payload.Tab, tabList, tabListPoint, tabList);
                    }
                    else
                    {
                        e.Effects = DragDropEffects.None;
                        e.Handled = true;
                        return;
                    }
                }
                else
                {
                    if (!_host.InsertExternalTab(payload, tabList, tabListPoint, tabList))
                    {
                        e.Effects = DragDropEffects.None;
                        e.Handled = true;
                        return;
                    }
                }

                _drag.ResetTabDetachCandidate();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (payload.IsFrom(_host) && _drag.IsDetachPreviewActive)
            {
                _drag.RequestCompleteAsNewWindow();
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        else if (_host.TryGetSnapshot(e, out var snapshot))
        {
            var tabListPoint = e.GetPosition(tabList);
            if (_drag.ShouldConfirmReorderFromRootDrop(tabList, tabListPoint)
                && _host.InsertTransferredTab(snapshot, tabList, tabListPoint, tabList))
            {
                _drag.ResetTabDetachCandidate();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        else if (_host.TryGetExternalDrop(e, out var externalDrop))
        {
            var tabListPoint = e.GetPosition(tabList);
            if (!IsPointOutsideTabReorderDropZone(tabList, tabListPoint))
            {
                _host.OpenExternalDrop(externalDrop, tabList, tabListPoint, tabList);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        else if (_host.HasFileDrop(e))
        {
            _folderDrop.ResetHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    internal void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        ResetExternalTabDragHover();
        _folderDrop.ResetHover();
        ResetWorkspaceTabDragHover();
        if (_reorder.ShouldKeepExternalTabDropPreviewAtCursor())
        {
            return;
        }

        _reorder.ClearDropPreviewWithFollowUp();
        _drag.BeginTabDetachCandidate();
    }

    internal void TabList_DragLeave(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        ResetExternalTabDragHover();
        if (_host.HasFileDrop(e))
        {
            _folderDrop.ResetHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ResetWorkspaceTabDragHover();
        if (_reorder.ShouldKeepExternalTabDropPreviewAtCursor())
        {
            e.Handled = true;
            return;
        }

        _reorder.ClearDropPreviewWithFollowUp();
        var tabList = sender as ListBox ?? _host.GetActiveTabDragList();
        _drag.UpdateTabDetachPreview(tabList, e.GetPosition(tabList));

        e.Handled = true;
    }

    internal async Task TabList_DropAsync(object sender, DragEventArgs e)
    {
        if (_disposed) return;
        ResetExternalTabDragHover();
        var tabList = sender as ListBox ?? TabList;
        if (await _folderDrop.TryHandleDropAsync(e))
        {
            return;
        }

        ResetWorkspaceTabDragHover();
        _reorder.ClearDropPreviewWithFollowUp(animateExternalPreview: false);
        if (!_host.TryGetPayload(e, out var payload))
        {
            if (_host.TryGetSiteInformation(e, out _))
            {
                e.Effects = await _host.TryHandleSiteInformationDropAsync(e, dropOnTabList: true)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (_host.TryGetSnapshot(e, out var snapshot)
                && _host.InsertTransferredTab(snapshot, tabList, e.GetPosition(tabList), e.OriginalSource))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (_host.TryGetExternalDrop(e, out var externalDrop))
            {
                _host.OpenExternalDrop(
                    externalDrop,
                    tabList,
                    e.GetPosition(tabList),
                    e.OriginalSource);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (payload.IsFrom(_host))
        {
            if (!_host.CanDropTabOnList(payload.Tab, tabList))
            {
                _drag.RestoreOriginalTabOrder();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (!_tabs.Contains(payload.Tab))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _reorder.ConfirmReorderDrop(payload.Tab, tabList, e.GetPosition(tabList), e.OriginalSource);
        }
        else
        {
            if (!_host.InsertExternalTab(payload, tabList, e.GetPosition(tabList), e.OriginalSource))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
        }

        _reorder.ClearDropPreviewWithFollowUp();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void UpdateExternalTabDragHover(ListBox tabList, Point point, object originalSource)
    {
        var target = _host.FindTabFromOriginalSource(originalSource);
        if (_host.IsClosing
            || point.X < 0 || point.Y < 0 || point.X > tabList.ActualWidth || point.Y > tabList.ActualHeight
            || target is null || !_tabs.Contains(target) || ReferenceEquals(target, _host.ActiveTab))
        {
            ResetExternalTabDragHover();
            return;
        }

        // 同じ見出しのDragOverで待ち時間を延ばさず、対象が変わったときだけ予約し直す。
        if (ReferenceEquals(_externalHoverTarget, target)) return;
        ResetExternalTabDragHover();
        _externalHoverTarget = target;
        _externalHoverTimer.Start();
    }

    private void ExternalHoverTimer_Tick(object? sender, EventArgs e) => ActivateHoveredExternalTab();

    internal void ActivateHoveredExternalTab()
    {
        var target = _externalHoverTarget;
        ResetExternalTabDragHover();
        if (_disposed || _host.IsClosing || target is null || !_tabs.Contains(target)
            || ReferenceEquals(target, _host.ActiveTab)) return;
        _host.SelectTab(target);
        _host.QueueSelectedTabActivation();
    }

    private void ResetExternalTabDragHover()
    {
        _externalHoverTimer.Stop();
        _externalHoverTarget = null;
    }

    private bool UpdateWorkspaceTabDragHover(
        ListBox tabList,
        Point tabListPoint,
        object originalSource,
        bool isDraggingWorkspaceTab)
    {
        if (isDraggingWorkspaceTab
            || !TryGetWorkspaceTabHoverTarget(tabList, tabListPoint, originalSource, out var workspaceTab)
            || !_tabs.Contains(workspaceTab))
        {
            ResetWorkspaceTabDragHover();
            return false;
        }

        ResetWorkspaceTabDragHover();
        return true;
    }

    private bool TryGetWorkspaceTabHoverTarget(
        ListBox tabList,
        Point tabListPoint,
        object originalSource,
        out BrowserTab workspaceTab)
    {
        if (_host.FindTabFromOriginalSource(originalSource) is { IsWorkspaceTab: true } sourceTab)
        {
            workspaceTab = sourceTab;
            return true;
        }

        foreach (var tab in _tabs)
        {
            if (!tab.IsWorkspaceTab
                || tabList.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item
                || item.ActualWidth <= 0
                || item.ActualHeight <= 0)
            {
                continue;
            }

            var origin = item.TranslatePoint(new Point(0, 0), tabList);
            var bounds = new Rect(origin, new Size(item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(tabListPoint))
            {
                workspaceTab = tab;
                return true;
            }
        }

        workspaceTab = default!;
        return false;
    }

    internal void ResetWorkspaceTabDragHover()
    {
        _workspaceHoverActivated = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _externalHoverTimer.Tick -= ExternalHoverTimer_Tick;
        ResetExternalTabDragHover();
        ResetWorkspaceTabDragHover();
    }
}
