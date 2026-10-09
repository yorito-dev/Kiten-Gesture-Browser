namespace TabBrowser.Services;

internal sealed class StreamingPlaybackTabStateCoordinator
{
    private readonly Dictionary<Guid, StreamingPlaybackTabState> _states = [];

    public int BeginReadinessCheck(Guid tabId)
    {
        var state = GetOrCreate(tabId);
        state.ProbeVersion++;
        state.IsNoticeExpanded = false;
        state.IsNoticeDismissed = false;
        return state.ProbeVersion;
    }

    public void MarkReadinessPending(Guid tabId)
    {
        GetOrCreate(tabId).Readiness = AmazonPrimeVideoReadiness.Pending;
    }

    public void ClearReadiness(Guid tabId)
    {
        if (_states.TryGetValue(tabId, out var state))
        {
            state.Readiness = null;
        }
    }

    public bool IsCurrentReadinessCheck(Guid tabId, int version)
    {
        return _states.TryGetValue(tabId, out var state)
            && state.ProbeVersion == version;
    }

    public bool TryCompleteReadinessCheck(
        Guid tabId,
        int version,
        AmazonPrimeVideoReadiness readiness)
    {
        if (!IsCurrentReadinessCheck(tabId, version))
        {
            return false;
        }

        _states[tabId].Readiness = readiness;
        return true;
    }

    public void MarkNavigationFailed(Guid tabId)
    {
        var state = GetOrCreate(tabId);
        state.ProbeVersion++;
        state.Readiness = AmazonPrimeVideoReadiness.NavigationFailed;
    }

    public void MarkNavigationStarting(Guid tabId, string? targetRetryKey)
    {
        if (!_states.TryGetValue(tabId, out var state))
        {
            return;
        }

        state.IsAmazonCompatibilityActive = false;
        state.IsStreamingCompatibilityActive = false;
        if (!string.Equals(state.CompatibilityAttemptKey, targetRetryKey, StringComparison.Ordinal))
        {
            state.CompatibilityAttemptKey = null;
        }
    }

    public bool TryBeginStreamingCompatibilityRetry(Guid tabId, string retryKey)
    {
        var state = GetOrCreate(tabId);
        if (string.Equals(state.CompatibilityAttemptKey, retryKey, StringComparison.Ordinal))
        {
            return false;
        }

        state.CompatibilityAttemptKey = retryKey;
        return true;
    }

    public AmazonPrimeVideoReadiness GetReadiness(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            ? state.Readiness ?? AmazonPrimeVideoReadiness.Unknown
            : AmazonPrimeVideoReadiness.Unknown;
    }

    public void MarkAmazonCompatibilityActive(Guid tabId)
    {
        GetOrCreate(tabId).IsAmazonCompatibilityActive = true;
    }

    public bool IsAmazonCompatibilityActive(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            && state.IsAmazonCompatibilityActive;
    }

    public void MarkStreamingCompatibilityActive(Guid tabId)
    {
        GetOrCreate(tabId).IsStreamingCompatibilityActive = true;
    }

    public void ClearStreamingCompatibility(Guid tabId)
    {
        if (_states.TryGetValue(tabId, out var state))
        {
            state.IsStreamingCompatibilityActive = false;
        }
    }

    public bool IsStreamingCompatibilityActive(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            && state.IsStreamingCompatibilityActive;
    }

    public void ExpandNotice(Guid tabId)
    {
        GetOrCreate(tabId).IsNoticeExpanded = true;
    }

    public bool IsNoticeExpanded(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            && state.IsNoticeExpanded;
    }

    public void DismissNotice(Guid tabId)
    {
        GetOrCreate(tabId).IsNoticeDismissed = true;
    }

    public bool IsNoticeDismissed(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            && state.IsNoticeDismissed;
    }

    public void Remove(Guid tabId)
    {
        _states.Remove(tabId);
    }

    private StreamingPlaybackTabState GetOrCreate(Guid tabId)
    {
        if (_states.TryGetValue(tabId, out var state))
        {
            return state;
        }

        state = new StreamingPlaybackTabState();
        _states[tabId] = state;
        return state;
    }

    private sealed class StreamingPlaybackTabState
    {
        public int ProbeVersion { get; set; }

        public AmazonPrimeVideoReadiness? Readiness { get; set; }

        public bool IsAmazonCompatibilityActive { get; set; }

        public bool IsStreamingCompatibilityActive { get; set; }

        public string? CompatibilityAttemptKey { get; set; }

        public bool IsNoticeExpanded { get; set; }

        public bool IsNoticeDismissed { get; set; }
    }
}
