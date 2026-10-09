namespace TabBrowser.Services;

internal enum BackgroundWorkDeferralState
{
    None,
    Scheduled,
    Expired
}

internal enum ActiveTabBackgroundLoadDecision
{
    Ready,
    Retry,
    MaximumWaitReached
}

internal sealed class BackgroundTabWorkCoordinator
{
    private readonly Queue<Guid> _userRequestedTabLoadQueue = [];
    private readonly HashSet<Guid> _userRequestedTabLoadIds = [];
    private DateTimeOffset? _deferredUntil;
    private DateTimeOffset? _activeTabWaitStartedAt;
    private bool _activeTabWaitExceeded;

    public bool IsTabLoading { get; private set; }

    public bool IsThumbnailWarmupRunning { get; private set; }

    public bool IsTabLoadRequested { get; private set; }

    public bool IsThumbnailWarmupRequested { get; private set; }

    public bool IsAutomaticWorkDeferredForMediaPlayback { get; private set; }

    public void RequestTabLoad()
    {
        IsTabLoadRequested = true;
    }

    public void RequestThumbnailWarmup()
    {
        IsThumbnailWarmupRequested = true;
    }

    public void RequestBoth()
    {
        RequestTabLoad();
        RequestThumbnailWarmup();
    }

    public bool EnqueueUserRequestedTabLoad(Guid tabId)
    {
        if (!_userRequestedTabLoadIds.Add(tabId))
        {
            return false;
        }

        _userRequestedTabLoadQueue.Enqueue(tabId);
        return true;
    }

    public bool TryPeekUserRequestedTabLoad(out Guid tabId)
    {
        return _userRequestedTabLoadQueue.TryPeek(out tabId);
    }

    public bool ContainsUserRequestedTabLoad(Guid tabId)
    {
        return _userRequestedTabLoadIds.Contains(tabId);
    }

    public void DiscardUserRequestedTabLoad(Guid tabId)
    {
        RemoveUserRequestedTabLoad(tabId);
    }

    public void CompleteUserRequestedTabLoad(Guid tabId)
    {
        RemoveUserRequestedTabLoad(tabId);
    }

    public bool TryBeginTabLoading()
    {
        if (IsTabLoading)
        {
            return false;
        }

        IsTabLoading = true;
        return true;
    }

    public void BeginTabLoadingPass()
    {
        IsTabLoadRequested = false;
    }

    public void SetTabLoadRetry(bool shouldRetry)
    {
        IsTabLoadRequested = shouldRetry;
    }

    public void ClearTabLoadRequest()
    {
        IsTabLoadRequested = false;
    }

    public bool CompleteTabLoading()
    {
        IsTabLoading = false;
        return IsTabLoadRequested;
    }

    public bool TryBeginThumbnailWarmup()
    {
        if (IsThumbnailWarmupRunning)
        {
            return false;
        }

        IsThumbnailWarmupRunning = true;
        return true;
    }

    public void BeginThumbnailWarmupPass()
    {
        IsThumbnailWarmupRequested = false;
    }

    public void SetThumbnailWarmupRetry(bool shouldRetry)
    {
        IsThumbnailWarmupRequested = shouldRetry;
    }

    public void ClearThumbnailWarmupRequest()
    {
        IsThumbnailWarmupRequested = false;
    }

    public bool CompleteThumbnailWarmup()
    {
        IsThumbnailWarmupRunning = false;
        return IsThumbnailWarmupRequested;
    }

    public void Defer(DateTimeOffset now, TimeSpan duration)
    {
        var deferredUntil = now.Add(duration);
        if (_deferredUntil is null || deferredUntil > _deferredUntil.Value)
        {
            _deferredUntil = deferredUntil;
        }

        RequestBoth();
    }

    public BackgroundWorkDeferralState EvaluateDeferral(
        DateTimeOffset now,
        out TimeSpan remaining)
    {
        if (_deferredUntil is not { } deferredUntil)
        {
            remaining = TimeSpan.Zero;
            return BackgroundWorkDeferralState.None;
        }

        remaining = deferredUntil - now;
        if (remaining > TimeSpan.Zero)
        {
            return BackgroundWorkDeferralState.Scheduled;
        }

        _deferredUntil = null;
        remaining = TimeSpan.Zero;
        return BackgroundWorkDeferralState.Expired;
    }

    public ActiveTabBackgroundLoadDecision EvaluateActiveTabWait(
        bool shouldPause,
        DateTimeOffset now,
        TimeSpan maximumWait)
    {
        if (!shouldPause)
        {
            _activeTabWaitStartedAt = null;
            _activeTabWaitExceeded = false;
            return ActiveTabBackgroundLoadDecision.Ready;
        }

        if (_activeTabWaitExceeded)
        {
            return ActiveTabBackgroundLoadDecision.MaximumWaitReached;
        }

        _activeTabWaitStartedAt ??= now;
        if (now - _activeTabWaitStartedAt.Value < maximumWait)
        {
            return ActiveTabBackgroundLoadDecision.Retry;
        }

        _activeTabWaitExceeded = true;
        return ActiveTabBackgroundLoadDecision.MaximumWaitReached;
    }

    public bool ShouldPauseCurrentTabLoadingPass(bool shouldPause)
    {
        return shouldPause && !_activeTabWaitExceeded;
    }

    public void MarkAutomaticWorkDeferredForMediaPlayback()
    {
        IsAutomaticWorkDeferredForMediaPlayback = true;
    }

    public bool TryResumeAutomaticWorkAfterMediaPlayback(
        out bool requestTabLoad,
        out bool requestThumbnailWarmup)
    {
        if (!IsAutomaticWorkDeferredForMediaPlayback)
        {
            requestTabLoad = false;
            requestThumbnailWarmup = false;
            return false;
        }

        IsAutomaticWorkDeferredForMediaPlayback = false;
        requestTabLoad = IsTabLoadRequested;
        requestThumbnailWarmup = IsThumbnailWarmupRequested;
        return true;
    }

    public void ClearAutomaticRequests()
    {
        IsTabLoadRequested = false;
        IsThumbnailWarmupRequested = false;
    }

    private void RemoveUserRequestedTabLoad(Guid tabId)
    {
        if (_userRequestedTabLoadQueue.TryPeek(out var queuedTabId) && queuedTabId == tabId)
        {
            _userRequestedTabLoadQueue.Dequeue();
        }

        _userRequestedTabLoadIds.Remove(tabId);
    }
}
