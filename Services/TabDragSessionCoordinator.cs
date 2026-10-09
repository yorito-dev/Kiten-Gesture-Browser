namespace TabBrowser.Services;

internal sealed class TabDragSessionCoordinator
{
    private bool _isCancelled;
    private bool _isDetachedToWindow;
    private bool _shouldCompleteAsNewWindow;
    private bool _isDetachPreviewActive;
    private bool _isPreviewMoveQueued;
    private DateTimeOffset? _reorderCandidateAt;

    public bool IsCancelled => _isCancelled;

    public bool IsDetachedToWindow => _isDetachedToWindow;

    public bool ShouldCompleteAsNewWindow => _shouldCompleteAsNewWindow;

    public bool IsDetachPreviewActive => _isDetachPreviewActive;

    public DateTimeOffset? ReorderCandidateAt => _reorderCandidateAt;

    public void BeginInteractiveDrag()
    {
        _isCancelled = false;
        _isDetachedToWindow = false;
        _shouldCompleteAsNewWindow = false;
        _isDetachPreviewActive = false;
    }

    public void RequestCancel()
    {
        _isCancelled = true;
    }

    public void MarkDetachedToWindow()
    {
        _isDetachedToWindow = true;
    }

    public void ClearDetachedToWindow()
    {
        _isDetachedToWindow = false;
    }

    public void RequestCompleteAsNewWindow()
    {
        _shouldCompleteAsNewWindow = true;
    }

    public void ClearCompleteAsNewWindow()
    {
        _shouldCompleteAsNewWindow = false;
    }

    public bool TrySetDetachPreviewActive(bool value)
    {
        if (_isDetachPreviewActive == value)
        {
            return false;
        }

        _isDetachPreviewActive = value;
        return true;
    }

    public bool TryQueuePreviewMove()
    {
        if (_isPreviewMoveQueued)
        {
            return false;
        }

        _isPreviewMoveQueued = true;
        return true;
    }

    public void CompletePreviewMove()
    {
        _isPreviewMoveQueued = false;
    }

    public void ClearPreviewMoveQueue()
    {
        _isPreviewMoveQueued = false;
    }

    public void MarkReorderCandidate(DateTimeOffset now)
    {
        _reorderCandidateAt = now;
    }

    public void ClearReorderCandidate()
    {
        _reorderCandidateAt = null;
    }
}
