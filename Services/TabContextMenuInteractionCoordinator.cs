namespace TabBrowser.Services;

internal sealed class TabContextMenuInteractionCoordinator
{
    private bool _isContinuousMode;
    private bool _isItemExecutionPending;
    private bool _isReopening;
    private int _interactionVersion;

    public bool IsContinuousMode => _isContinuousMode;

    public bool IsItemExecutionPending => _isItemExecutionPending;

    public void BeginInteraction(bool continuousMode)
    {
        AdvanceVersion();
        _isContinuousMode = continuousMode;
        _isItemExecutionPending = false;
        _isReopening = false;
    }

    public int CaptureVersion()
    {
        return _interactionVersion;
    }

    public bool IsCurrent(int version)
    {
        return version == _interactionVersion;
    }

    public void MarkItemExecutionPending()
    {
        _isItemExecutionPending = true;
    }

    public void ClearItemExecutionPending()
    {
        _isItemExecutionPending = false;
    }

    public void MarkReopening()
    {
        _isReopening = true;
    }

    public bool ConsumeReopening()
    {
        var wasReopening = _isReopening;
        _isReopening = false;
        return wasReopening;
    }

    public void Reset()
    {
        AdvanceVersion();
        _isContinuousMode = false;
        _isItemExecutionPending = false;
        _isReopening = false;
    }

    private void AdvanceVersion()
    {
        _interactionVersion = unchecked(_interactionVersion + 1);
    }
}
