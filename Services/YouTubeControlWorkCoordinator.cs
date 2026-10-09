namespace TabBrowser.Services;

internal readonly record struct YouTubeApplicationFullscreenRestoreSnapshot(
    int WindowStyle,
    int WindowState,
    int ResizeMode,
    bool Topmost,
    double Left,
    double Top,
    double Width,
    double Height);

internal readonly record struct YouTubePlaybackTrackingTransition(
    Guid? DeactivatedTabId,
    Guid? ActivatedTabId,
    bool ShouldScheduleFallbackCapture);

internal enum YouTubePlaybackStateSaveDisposition
{
    Ignored,
    Deferred,
    Immediate
}

internal sealed class YouTubeControlWorkCoordinator
{
    private readonly HashSet<Guid> _displayFallbackAttemptedTabIds = [];
    private int _splitPanelRefreshGeneration;
    private bool _popupPlacementRunning;
    private bool _windowPlacementQueued;
    private bool _suppressedUntilPointerActivity;
    private DateTimeOffset _lastPanelInteractionAt = DateTimeOffset.MinValue;
    private YouTubeApplicationFullscreenRestoreSnapshot? _applicationFullscreenRestoreSnapshot;
    private Guid? _playbackStateBridgeActiveTabId;
    private Guid? _playbackStateFallbackTabId;
    private bool _hasPendingPlaybackProgressSave;

    public bool IsPopupPlacementRunning => _popupPlacementRunning;

    public bool IsWindowPlacementQueued => _windowPlacementQueued;

    public bool IsSuppressedUntilPointerActivity => _suppressedUntilPointerActivity;

    public bool IsApplicationFullscreenActive => _applicationFullscreenRestoreSnapshot is not null;

    public int BeginSplitPanelRefresh()
    {
        return ++_splitPanelRefreshGeneration;
    }

    public bool IsCurrentSplitPanelRefresh(int generation)
    {
        return generation == _splitPanelRefreshGeneration;
    }

    public IDisposable EnterPopupPlacement()
    {
        _popupPlacementRunning = true;
        return new PopupPlacementLease(this);
    }

    public void MarkWindowPlacementQueued()
    {
        _windowPlacementQueued = true;
    }

    public void CompleteWindowPlacementQueue()
    {
        _windowPlacementQueued = false;
    }

    public void SuppressUntilPointerActivity()
    {
        _suppressedUntilPointerActivity = true;
    }

    public bool ResumeFromPointerSuppression()
    {
        if (!_suppressedUntilPointerActivity)
        {
            return false;
        }

        _suppressedUntilPointerActivity = false;
        return true;
    }

    public void ClearPointerSuppression()
    {
        _suppressedUntilPointerActivity = false;
    }

    public void RecordPanelInteraction(DateTimeOffset now)
    {
        _lastPanelInteractionAt = now;
    }

    public bool HasRecentPanelInteraction(DateTimeOffset now, TimeSpan maximumAge)
    {
        return now - _lastPanelInteractionAt <= maximumAge;
    }

    public bool TryBeginDisplayFallback(Guid tabId)
    {
        return _displayFallbackAttemptedTabIds.Add(tabId);
    }

    public void ClearDisplayFallback(Guid tabId)
    {
        _displayFallbackAttemptedTabIds.Remove(tabId);
    }

    public bool TryBeginApplicationFullscreen(YouTubeApplicationFullscreenRestoreSnapshot snapshot)
    {
        if (_applicationFullscreenRestoreSnapshot is not null)
        {
            return false;
        }

        _applicationFullscreenRestoreSnapshot = snapshot;
        return true;
    }

    public bool TryGetApplicationFullscreenRestoreSnapshot(
        out YouTubeApplicationFullscreenRestoreSnapshot snapshot)
    {
        if (_applicationFullscreenRestoreSnapshot is not { } activeSnapshot)
        {
            snapshot = default;
            return false;
        }

        snapshot = activeSnapshot;
        return true;
    }

    public bool TryUpdateApplicationFullscreenRestoreTopmost(bool topmost)
    {
        if (_applicationFullscreenRestoreSnapshot is not { } snapshot)
        {
            return false;
        }

        _applicationFullscreenRestoreSnapshot = snapshot with { Topmost = topmost };
        return true;
    }

    public bool TryEndApplicationFullscreen(out YouTubeApplicationFullscreenRestoreSnapshot snapshot)
    {
        if (_applicationFullscreenRestoreSnapshot is not { } activeSnapshot)
        {
            snapshot = default;
            return false;
        }

        snapshot = activeSnapshot;
        _applicationFullscreenRestoreSnapshot = null;
        return true;
    }

    public YouTubePlaybackTrackingTransition UpdatePlaybackStateTracking(
        Guid? nextActiveTabId,
        bool scheduleFallbackCapture)
    {
        Guid? deactivatedTabId = null;
        Guid? activatedTabId = null;
        if (_playbackStateBridgeActiveTabId != nextActiveTabId)
        {
            deactivatedTabId = _playbackStateBridgeActiveTabId;
            activatedTabId = nextActiveTabId;
            _playbackStateBridgeActiveTabId = nextActiveTabId;
        }

        _playbackStateFallbackTabId = scheduleFallbackCapture && nextActiveTabId is not null
            ? nextActiveTabId
            : null;
        return new YouTubePlaybackTrackingTransition(
            deactivatedTabId,
            activatedTabId,
            _playbackStateFallbackTabId is not null);
    }

    public bool TryTakePlaybackStateFallback(out Guid tabId)
    {
        if (_playbackStateFallbackTabId is not { } pendingTabId)
        {
            tabId = default;
            return false;
        }

        tabId = pendingTabId;
        _playbackStateFallbackTabId = null;
        return true;
    }

    public bool TryCancelPlaybackStateFallback(Guid tabId)
    {
        if (_playbackStateFallbackTabId != tabId)
        {
            return false;
        }

        _playbackStateFallbackTabId = null;
        return true;
    }

    public void ClearPlaybackStateFallback()
    {
        _playbackStateFallbackTabId = null;
    }

    public YouTubePlaybackStateSaveDisposition QueuePlaybackStateSave(
        string? reason,
        bool canQueueProgressSave)
    {
        if (!string.Equals(reason, "timeupdate", StringComparison.Ordinal))
        {
            _hasPendingPlaybackProgressSave = false;
            return YouTubePlaybackStateSaveDisposition.Immediate;
        }

        if (!canQueueProgressSave)
        {
            return YouTubePlaybackStateSaveDisposition.Ignored;
        }

        _hasPendingPlaybackProgressSave = true;
        return YouTubePlaybackStateSaveDisposition.Deferred;
    }

    public bool TakePendingPlaybackProgressSave()
    {
        var wasPending = _hasPendingPlaybackProgressSave;
        _hasPendingPlaybackProgressSave = false;
        return wasPending;
    }

    public void ClearPendingPlaybackProgressSave()
    {
        _hasPendingPlaybackProgressSave = false;
    }

    private void CompletePopupPlacement()
    {
        _popupPlacementRunning = false;
    }

    private sealed class PopupPlacementLease(YouTubeControlWorkCoordinator coordinator) : IDisposable
    {
        private YouTubeControlWorkCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.CompletePopupPlacement();
        }
    }
}
