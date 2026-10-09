namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerListInteractionCoordinator
{
    private Guid? _hoveredItemId;
    private Guid? _pendingHoverPreviewId;
    private int _lastPopupMouseDownTimestamp = -1;
    private bool _isPopupPositionClampQueued;

    public Guid? HoveredItemId => _hoveredItemId;

    public Guid? PendingHoverPreviewId => _pendingHoverPreviewId;

    public void SetHoveredItem(Guid id)
    {
        _hoveredItemId = id;
    }

    public bool IsHoveredItem(Guid id)
    {
        return _hoveredItemId == id;
    }

    public void ClearHoveredItem()
    {
        _hoveredItemId = null;
    }

    public void SetPendingHoverPreview(Guid id)
    {
        _pendingHoverPreviewId = id;
    }

    public bool IsPendingHoverPreview(Guid id)
    {
        return _pendingHoverPreviewId == id;
    }

    public void ClearPendingHoverPreview()
    {
        _pendingHoverPreviewId = null;
    }

    public bool IsDuplicatePopupMouseDown(int timestamp)
    {
        return timestamp == _lastPopupMouseDownTimestamp;
    }

    public void RecordPopupMouseDown(int timestamp)
    {
        _lastPopupMouseDownTimestamp = timestamp;
    }

    public bool TryQueuePopupPositionClamp()
    {
        if (_isPopupPositionClampQueued)
        {
            return false;
        }

        _isPopupPositionClampQueued = true;
        return true;
    }

    public void CompletePopupPositionClamp()
    {
        _isPopupPositionClampQueued = false;
    }
}
