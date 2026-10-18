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

        state.HasPlaybackFailure = false;
        state.Readiness = null;
        state.ProbeVersion++;
        state.IsNoticeExpanded = false;
        state.IsNoticeDismissed = false;

    }

    public AmazonPrimeVideoReadiness GetReadiness(Guid tabId)
    {
        return _states.TryGetValue(tabId, out var state)
            ? state.Readiness ?? AmazonPrimeVideoReadiness.Unknown
            : AmazonPrimeVideoReadiness.Unknown;
    }

    public void MarkPlaybackFailed(Guid tabId) => GetOrCreate(tabId).HasPlaybackFailure = true;

    public bool HasPlaybackFailure(Guid tabId)
        => _states.TryGetValue(tabId, out var state) && state.HasPlaybackFailure;

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

        public bool HasPlaybackFailure { get; set; }

        public bool IsNoticeExpanded { get; set; }

        public bool IsNoticeDismissed { get; set; }
    }
}
