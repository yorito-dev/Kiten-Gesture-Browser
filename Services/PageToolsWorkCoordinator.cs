namespace TabBrowser.Services;

internal sealed class PageToolsWorkCoordinator
{
    private readonly HashSet<Guid> _zoomNavigationTabIds = [];
    private readonly HashSet<Guid> _zoomApplyingTabIds = [];
    private int _pageFindGeneration;

    public int BeginPageFind()
    {
        return ++_pageFindGeneration;
    }

    public void InvalidatePageFind()
    {
        _pageFindGeneration++;
    }

    public bool IsCurrentPageFind(int generation)
    {
        return generation == _pageFindGeneration;
    }

    public void BeginZoomNavigation(Guid tabId)
    {
        _zoomNavigationTabIds.Add(tabId);
    }

    public void EndZoomNavigation(Guid tabId)
    {
        _zoomNavigationTabIds.Remove(tabId);
    }

    public void CancelZoomTracking(Guid tabId)
    {
        _zoomNavigationTabIds.Remove(tabId);
        _zoomApplyingTabIds.Remove(tabId);
    }

    public bool IsZoomFactorChangeSuppressed(Guid tabId)
    {
        return _zoomNavigationTabIds.Contains(tabId)
            || _zoomApplyingTabIds.Contains(tabId);
    }

    public IDisposable EnterZoomApply(Guid tabId)
    {
        _zoomApplyingTabIds.Add(tabId);
        return new ZoomApplyLease(this, tabId);
    }

    public void ClearZoomTracking()
    {
        _zoomNavigationTabIds.Clear();
        _zoomApplyingTabIds.Clear();
    }

    private void EndZoomApply(Guid tabId)
    {
        _zoomApplyingTabIds.Remove(tabId);
    }

    private sealed class ZoomApplyLease(PageToolsWorkCoordinator coordinator, Guid tabId) : IDisposable
    {
        private PageToolsWorkCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.EndZoomApply(tabId);
        }
    }
}
