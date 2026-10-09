namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerStateSyncCoordinator
{
    private bool _captureRunning;
    private bool _listRefreshRunning;
    private bool _captureFailureReported;
    private bool _eventStateSavePending;
    private string _workspaceShelfSignature = string.Empty;

    public bool IsCaptureRunning => _captureRunning;

    public bool IsListRefreshRunning => _listRefreshRunning;

    public bool HasPendingEventStateSave => _eventStateSavePending;

    public bool TryEnterCapture(out IDisposable? lease)
    {
        if (_captureRunning)
        {
            lease = null;
            return false;
        }

        _captureRunning = true;
        lease = new RunningLease(this, RunningScope.Capture);
        return true;
    }

    public bool TryEnterListRefresh(out IDisposable? lease)
    {
        if (_listRefreshRunning)
        {
            lease = null;
            return false;
        }

        _listRefreshRunning = true;
        lease = new RunningLease(this, RunningScope.ListRefresh);
        return true;
    }

    public void MarkCaptureSucceeded()
    {
        _captureFailureReported = false;
    }

    public bool TryMarkCaptureFailureReported()
    {
        if (_captureFailureReported)
        {
            return false;
        }

        _captureFailureReported = true;
        return true;
    }

    public void MarkEventStateSavePending()
    {
        _eventStateSavePending = true;
    }

    public void ClearPendingEventStateSave()
    {
        _eventStateSavePending = false;
    }

    public bool UpdateWorkspaceShelfSignature(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (string.Equals(_workspaceShelfSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        _workspaceShelfSignature = signature;
        return true;
    }

    private void Exit(RunningScope scope)
    {
        if (scope == RunningScope.Capture)
        {
            _captureRunning = false;
            return;
        }

        _listRefreshRunning = false;
    }

    private enum RunningScope
    {
        Capture,
        ListRefresh
    }

    private sealed class RunningLease(
        YouTubeMiniPlayerStateSyncCoordinator coordinator,
        RunningScope scope) : IDisposable
    {
        private YouTubeMiniPlayerStateSyncCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            Interlocked.Exchange(ref _coordinator, null)?.Exit(scope);
        }
    }
}
