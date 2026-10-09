namespace TabBrowser.Services;

internal readonly record struct InactiveYouTubeTabWorkSnapshot(
    int SuspendCount,
    int LoadStopCount,
    int ResumeCount);

internal sealed class InactiveYouTubeTabWorkCoordinator
{
    private readonly Dictionary<Guid, int> _checkGenerations = [];
    private int _suspendCount;
    private int _loadStopCount;
    private int _resumeCount;

    public int BeginCheck(Guid tabId)
    {
        var generation = GetNextGeneration(tabId);
        _checkGenerations[tabId] = generation;
        return generation;
    }

    public void CancelCheck(Guid tabId)
    {
        _checkGenerations[tabId] = GetNextGeneration(tabId);
    }

    public bool IsCurrent(Guid tabId, int generation)
    {
        return _checkGenerations.TryGetValue(tabId, out var currentGeneration)
            && currentGeneration == generation;
    }

    public void Remove(Guid tabId)
    {
        _checkGenerations.Remove(tabId);
    }

    public void RecordSuspended()
    {
        _suspendCount++;
    }

    public void RecordLoadStopped()
    {
        _loadStopCount++;
    }

    public void RecordResumed()
    {
        _resumeCount++;
    }

    public InactiveYouTubeTabWorkSnapshot CaptureSnapshot()
    {
        return new InactiveYouTubeTabWorkSnapshot(
            _suspendCount,
            _loadStopCount,
            _resumeCount);
    }

    private int GetNextGeneration(Guid tabId)
    {
        return _checkGenerations.TryGetValue(tabId, out var currentGeneration)
            ? currentGeneration + 1
            : 1;
    }
}
