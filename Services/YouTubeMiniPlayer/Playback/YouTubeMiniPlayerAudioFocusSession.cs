namespace TabBrowser.Services;

public enum YouTubeMiniPlayerAudioFocusMode
{
    SoloAudio,
    SoloPlayback,
    SelectedPlayback,
    PauseOutsideSelection,
    MuteOutsideSelection
}

public readonly record struct YouTubeMiniPlayerAudioFocusState(
    Guid Id,
    bool IsMuted,
    bool? IsPaused);

public readonly record struct YouTubeMiniPlayerAudioFocusPlanItem(
    Guid Id,
    bool? DesiredMuted,
    bool? DesiredPaused,
    bool TrackMuted,
    bool TrackPaused);

public sealed class YouTubeMiniPlayerAudioFocusSession
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly HashSet<Guid> _targetIds = [];

    public YouTubeMiniPlayerAudioFocusMode? Mode { get; private set; }

    public bool IsActive => Mode.HasValue;

    public bool HasAppliedState => _entries.Values.Any(static entry =>
        entry.AppliedMuted.HasValue || entry.AppliedPaused.HasValue);

    public IReadOnlySet<Guid> TargetIds => _targetIds;

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> CreateApplyPlan(
        YouTubeMiniPlayerAudioFocusMode mode,
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states,
        IReadOnlyCollection<Guid> targetIds)
    {
        var availableIds = states.Select(static state => state.Id).ToHashSet();
        var normalizedTargets = targetIds
            .Where(availableIds.Contains)
            .ToHashSet();
        if (normalizedTargets.Count == 0)
        {
            return [];
        }

        SynchronizeEntries(states);
        Mode = mode;
        _targetIds.Clear();
        _targetIds.UnionWith(normalizedTargets);

        return states
            .Select(state => CreateApplyPlanItem(mode, state, normalizedTargets.Contains(state.Id)))
            .Where(static item => item.DesiredMuted.HasValue || item.DesiredPaused.HasValue)
            .ToList();
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> CreateRestorePlan(
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states)
    {
        SynchronizeEntries(states);
        return states
            .Where(state => _entries.TryGetValue(state.Id, out var entry)
                && (entry.AppliedMuted.HasValue || entry.AppliedPaused.HasValue))
            .Select(state =>
            {
                var entry = _entries[state.Id];
                return new YouTubeMiniPlayerAudioFocusPlanItem(
                    state.Id,
                    entry.AppliedMuted.HasValue ? entry.BaseMuted : null,
                    entry.AppliedPaused.HasValue ? entry.BasePaused : null,
                    TrackMuted: false,
                    TrackPaused: false);
            })
            .ToList();
    }

    public void Complete(
        YouTubeMiniPlayerAudioFocusPlanItem item,
        bool mutedApplied,
        bool pausedApplied)
    {
        if (!_entries.TryGetValue(item.Id, out var entry))
        {
            return;
        }

        if (item.DesiredMuted.HasValue && mutedApplied)
        {
            entry.AppliedMuted = item.TrackMuted ? item.DesiredMuted : null;
        }

        if (item.DesiredPaused.HasValue && pausedApplied)
        {
            entry.AppliedPaused = item.TrackPaused ? item.DesiredPaused : null;
        }

    }

    public YouTubeMiniPlayerAudioFocusState CreatePersistentState(
        YouTubeMiniPlayerAudioFocusState state)
    {
        if (!_entries.TryGetValue(state.Id, out var entry))
        {
            return state;
        }

        var muted = entry.AppliedMuted.HasValue && state.IsMuted == entry.AppliedMuted.Value
            ? entry.BaseMuted
            : state.IsMuted;
        var paused = entry.AppliedPaused.HasValue && state.IsPaused == entry.AppliedPaused
            ? entry.BasePaused
            : state.IsPaused;
        return state with
        {
            IsMuted = muted,
            IsPaused = paused
        };
    }

    public bool IsTarget(Guid id)
    {
        return IsActive && _targetIds.Contains(id);
    }

    public void Remove(Guid id)
    {
        _entries.Remove(id);
        _targetIds.Remove(id);
        if (_entries.Count == 0)
        {
            Clear();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _targetIds.Clear();
        Mode = null;
    }

    private YouTubeMiniPlayerAudioFocusPlanItem CreateApplyPlanItem(
        YouTubeMiniPlayerAudioFocusMode mode,
        YouTubeMiniPlayerAudioFocusState state,
        bool isTarget)
    {
        var entry = _entries[state.Id];
        var controlsMuted = mode == YouTubeMiniPlayerAudioFocusMode.SoloAudio
            || (mode == YouTubeMiniPlayerAudioFocusMode.MuteOutsideSelection && !isTarget);
        var controlsPaused = mode is YouTubeMiniPlayerAudioFocusMode.SoloPlayback
                or YouTubeMiniPlayerAudioFocusMode.SelectedPlayback
            || (mode == YouTubeMiniPlayerAudioFocusMode.PauseOutsideSelection && !isTarget);

        bool? desiredMuted = mode switch
        {
            YouTubeMiniPlayerAudioFocusMode.SoloAudio => !isTarget,
            YouTubeMiniPlayerAudioFocusMode.MuteOutsideSelection when !isTarget => true,
            _ when entry.AppliedMuted.HasValue => entry.BaseMuted,
            _ => null
        };
        bool? desiredPaused = mode switch
        {
            YouTubeMiniPlayerAudioFocusMode.SoloPlayback => !isTarget,
            YouTubeMiniPlayerAudioFocusMode.SelectedPlayback => !isTarget,
            YouTubeMiniPlayerAudioFocusMode.PauseOutsideSelection when !isTarget => true,
            _ when entry.AppliedPaused.HasValue => entry.BasePaused,
            _ => null
        };

        return new YouTubeMiniPlayerAudioFocusPlanItem(
            state.Id,
            desiredMuted,
            desiredPaused,
            controlsMuted,
            controlsPaused);
    }

    private void SynchronizeEntries(IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states)
    {
        foreach (var state in states)
        {
            if (!_entries.TryGetValue(state.Id, out var entry))
            {
                _entries[state.Id] = new Entry(state.IsMuted, state.IsPaused);
                continue;
            }

            if (entry.AppliedMuted.HasValue && state.IsMuted != entry.AppliedMuted.Value)
            {
                entry.BaseMuted = state.IsMuted;
                entry.AppliedMuted = null;
            }

            if (entry.AppliedPaused.HasValue && state.IsPaused != entry.AppliedPaused)
            {
                entry.BasePaused = state.IsPaused;
                entry.AppliedPaused = null;
            }
        }
    }

    private sealed class Entry(bool baseMuted, bool? basePaused)
    {
        public bool BaseMuted { get; set; } = baseMuted;

        public bool? BasePaused { get; set; } = basePaused;

        public bool? AppliedMuted { get; set; }

        public bool? AppliedPaused { get; set; }
    }
}

public sealed class YouTubeMiniPlayerAudioPreviewSession
{
    private readonly Dictionary<Guid, YouTubeMiniPlayerAudioFocusState> _originalStates = [];

    public Guid? TargetId { get; private set; }

    public bool IsActive => TargetId.HasValue;

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> Start(
        IReadOnlyCollection<YouTubeMiniPlayerAudioFocusState> states,
        Guid targetId)
    {
        _originalStates.Clear();
        foreach (var state in states)
        {
            _originalStates[state.Id] = state;
        }

        if (!_originalStates.ContainsKey(targetId))
        {
            return [];
        }

        TargetId = targetId;
        return states
            .Select(state => new YouTubeMiniPlayerAudioFocusPlanItem(
                state.Id,
                DesiredMuted: state.Id != targetId,
                DesiredPaused: state.Id == targetId && state.IsPaused == true ? false : null,
                TrackMuted: false,
                TrackPaused: false))
            .ToList();
    }

    public IReadOnlyList<YouTubeMiniPlayerAudioFocusPlanItem> Stop()
    {
        var targetId = TargetId;
        var restore = _originalStates.Values
            .Select(state => new YouTubeMiniPlayerAudioFocusPlanItem(
                state.Id,
                DesiredMuted: state.IsMuted,
                DesiredPaused: state.Id == targetId && state.IsPaused == true ? true : null,
                TrackMuted: false,
                TrackPaused: false))
            .ToList();
        Clear();
        return restore;
    }

    public void Clear()
    {
        _originalStates.Clear();
        TargetId = null;
    }
}
