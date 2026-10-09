namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerAudioSessionCoordinator
{
    private readonly YouTubeMiniPlayerAudioFocusSession _focusSession = new();
    private readonly YouTubeMiniPlayerAudioPreviewSession _previewSession = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public YouTubeMiniPlayerAudioFocusMode? FocusMode => _focusSession.Mode;

    public bool IsFocusActive => _focusSession.IsActive;

    public bool IsPreviewActive => _previewSession.IsActive;

    public Guid? PreviewTargetId => _previewSession.TargetId;

    public async Task<IDisposable> EnterAsync()
    {
        await _gate.WaitAsync();
        return new GateLease(_gate);
    }

    public bool IsFocusTarget(Guid id)
    {
        return _focusSession.IsTarget(id);
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> StartPreview(
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states,
        Guid targetId)
    {
        return _previewSession.Start(states, targetId);
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> StopPreview()
    {
        return _previewSession.Stop();
    }

    public void ClearPreview()
    {
        _previewSession.Clear();
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> CreateFocusApplyPlan(
        YouTubeMiniPlayerAudioFocusMode mode,
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states,
        IReadOnlyCollection<Guid> targetIds)
    {
        return _focusSession.CreateApplyPlan(mode, states, targetIds);
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> CreateFocusRestorePlan(
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states)
    {
        return _focusSession.CreateRestorePlan(states);
    }

    public void CompleteFocusPlanItem(
        YouTubeMiniPlayerAudioFocusPlanItem item,
        bool mutedApplied,
        bool pausedApplied)
    {
        _focusSession.Complete(item, mutedApplied, pausedApplied);
    }

    public bool ClearFocusIfFullyRestored()
    {
        if (_focusSession.HasAppliedState)
        {
            return false;
        }

        _focusSession.Clear();
        return true;
    }

    public YouTubeMiniPlayerAudioFocusState CreatePersistentState(
        YouTubeMiniPlayerAudioFocusState state)
    {
        return _focusSession.CreatePersistentState(state);
    }

    public bool RemoveFocusPlayer(Guid id)
    {
        var wasTarget = _focusSession.IsTarget(id);
        _focusSession.Remove(id);
        return wasTarget;
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
