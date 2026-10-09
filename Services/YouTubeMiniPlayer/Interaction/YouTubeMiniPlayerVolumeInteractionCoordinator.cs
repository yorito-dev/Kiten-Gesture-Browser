namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerVolumeInteractionCoordinator
{
    private bool _programmaticVolumeUpdate;
    private bool _hasPendingVolume;
    private double _pendingNormalizedVolume;
    private bool _audioBoostRunning;

    public bool IsProgrammaticVolumeUpdate => _programmaticVolumeUpdate;

    public bool HasPendingVolume => _hasPendingVolume;

    public bool IsAudioBoostRunning => _audioBoostRunning;

    public IDisposable EnterProgrammaticVolumeUpdate()
    {
        _programmaticVolumeUpdate = true;
        return new ProgrammaticVolumeUpdateLease(this);
    }

    public void SetPendingVolumePercent(double value)
    {
        _pendingNormalizedVolume = Math.Clamp(value, 0, 100) / 100;
        _hasPendingVolume = true;
    }

    public bool TryTakePendingVolume(out double normalizedVolume)
    {
        if (!_hasPendingVolume)
        {
            normalizedVolume = 0;
            return false;
        }

        normalizedVolume = _pendingNormalizedVolume;
        _pendingNormalizedVolume = 0;
        _hasPendingVolume = false;
        return true;
    }

    public bool TryEnterAudioBoost(out IDisposable? lease)
    {
        if (_audioBoostRunning)
        {
            lease = null;
            return false;
        }

        _audioBoostRunning = true;
        lease = new AudioBoostLease(this);
        return true;
    }

    private void CompleteProgrammaticVolumeUpdate()
    {
        _programmaticVolumeUpdate = false;
    }

    private void CompleteAudioBoost()
    {
        _audioBoostRunning = false;
    }

    private sealed class ProgrammaticVolumeUpdateLease(
        YouTubeMiniPlayerVolumeInteractionCoordinator coordinator) : IDisposable
    {
        private YouTubeMiniPlayerVolumeInteractionCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.CompleteProgrammaticVolumeUpdate();
        }
    }

    private sealed class AudioBoostLease(
        YouTubeMiniPlayerVolumeInteractionCoordinator coordinator) : IDisposable
    {
        private YouTubeMiniPlayerVolumeInteractionCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.CompleteAudioBoost();
        }
    }
}
