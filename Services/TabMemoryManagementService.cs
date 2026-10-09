namespace TabBrowser.Services;

public enum TabMemoryPressureLevel
{
    None,
    Soft,
    Hard
}

public sealed record TabMemorySnapshot(
    Guid TabId,
    DateTimeOffset LastActivatedAt,
    bool IsActive,
    bool IsLoaded,
    bool HasRuntime,
    bool IsPinned,
    bool IsVisibleSplitTab,
    bool IsPlayingAudio,
    bool IsLoading,
    bool IsInitializing,
    bool IsNativeContentTab,
    bool IsSuspended);

public sealed record TabMemoryManagementCounters(
    int ReleaseCount,
    int RestoreCount,
    int ReleaseSkipCount);

public sealed class TabMemoryManagementService
{
    public const int RecentHotTabLimit = 8;
    public const int SoftLoadedTabLimit = 16;
    public const int HardLoadedTabLimit = 24;

    private static readonly TimeSpan SoftIdleTime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan HardIdleTime = TimeSpan.FromMinutes(5);

    private int _isEvaluationRunning;
    private int _releaseCount;
    private int _restoreCount;
    private int _releaseSkipCount;

    public bool IsEvaluationRunning => Volatile.Read(ref _isEvaluationRunning) != 0;

    public bool TryEnterEvaluation(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _isEvaluationRunning, 1, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new EvaluationLease(this);
        return true;
    }

    public void RecordRelease()
    {
        Interlocked.Increment(ref _releaseCount);
    }

    public void RecordRestore()
    {
        Interlocked.Increment(ref _restoreCount);
    }

    public void RecordReleaseSkip()
    {
        Interlocked.Increment(ref _releaseSkipCount);
    }

    public TabMemoryManagementCounters CaptureCounters()
    {
        return new TabMemoryManagementCounters(
            Volatile.Read(ref _releaseCount),
            Volatile.Read(ref _restoreCount),
            Volatile.Read(ref _releaseSkipCount));
    }

    public IReadOnlySet<Guid> SelectHotTabIds(IEnumerable<TabMemorySnapshot> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        var snapshots = tabs.ToList();
        var result = snapshots
            .Where(static tab => tab.IsActive && !tab.IsNativeContentTab)
            .Select(static tab => tab.TabId)
            .ToHashSet();

        foreach (var tab in snapshots
                     .Where(static tab => !tab.IsActive && !tab.IsNativeContentTab)
                     .OrderByDescending(static tab => tab.LastActivatedAt)
                     .Take(RecentHotTabLimit))
        {
            result.Add(tab.TabId);
        }

        return result;
    }

    public TabMemoryPressureLevel EvaluatePressure(
        long totalAvailableMemoryBytes,
        long webViewPrivateMemoryBytes,
        int loadedTabCount)
    {
        var softThreshold = CalculateSoftMemoryThreshold(totalAvailableMemoryBytes);
        var hardThreshold = CalculateHardMemoryThreshold(totalAvailableMemoryBytes);
        if (webViewPrivateMemoryBytes >= hardThreshold || loadedTabCount > HardLoadedTabLimit)
        {
            return TabMemoryPressureLevel.Hard;
        }

        if (webViewPrivateMemoryBytes >= softThreshold || loadedTabCount > SoftLoadedTabLimit)
        {
            return TabMemoryPressureLevel.Soft;
        }

        return TabMemoryPressureLevel.None;
    }

    public IReadOnlyList<Guid> SelectSuspendCandidateIds(
        IEnumerable<TabMemorySnapshot> tabs,
        IReadOnlySet<Guid> hotTabIds,
        TabMemoryPressureLevel pressure,
        DateTimeOffset now,
        int maximumCount = 2)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(hotTabIds);

        if (pressure == TabMemoryPressureLevel.None || maximumCount <= 0)
        {
            return [];
        }

        var minimumIdleTime = pressure == TabMemoryPressureLevel.Hard
            ? HardIdleTime
            : SoftIdleTime;

        return tabs
            .Where(tab => IsSuspendCandidate(tab, hotTabIds, now, minimumIdleTime))
            .OrderBy(static tab => tab.LastActivatedAt)
            .Take(maximumCount)
            .Select(static tab => tab.TabId)
            .ToList();
    }

    private static bool IsSuspendCandidate(
        TabMemorySnapshot tab,
        IReadOnlySet<Guid> hotTabIds,
        DateTimeOffset now,
        TimeSpan minimumIdleTime)
    {
        return !tab.IsActive
            && tab.IsLoaded
            && tab.HasRuntime
            && !tab.IsPinned
            && !tab.IsVisibleSplitTab
            && !tab.IsPlayingAudio
            && !tab.IsLoading
            && !tab.IsInitializing
            && !tab.IsNativeContentTab
            && !tab.IsSuspended
            && !hotTabIds.Contains(tab.TabId)
            && now - tab.LastActivatedAt >= minimumIdleTime;
    }

    private static long CalculateSoftMemoryThreshold(long totalAvailableMemoryBytes)
    {
        const long minimum = 2L * 1024 * 1024 * 1024;
        const long maximum = 6L * 1024 * 1024 * 1024;
        return Math.Clamp(totalAvailableMemoryBytes / 10, minimum, maximum);
    }

    private static long CalculateHardMemoryThreshold(long totalAvailableMemoryBytes)
    {
        const long minimum = 3L * 1024 * 1024 * 1024;
        const long maximum = 10L * 1024 * 1024 * 1024;
        return Math.Clamp(totalAvailableMemoryBytes / 6, minimum, maximum);
    }

    private void ExitEvaluation()
    {
        Volatile.Write(ref _isEvaluationRunning, 0);
    }

    private sealed class EvaluationLease(TabMemoryManagementService owner) : IDisposable
    {
        private TabMemoryManagementService? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitEvaluation();
        }
    }
}
