namespace TabBrowser.Services;

internal enum FloatingMediaHostLifetimeState
{
    Visible,
    HiddenRetained,
    Closing
}

internal sealed class FloatingMediaHostLifetime
{
    public FloatingMediaHostLifetimeState State { get; private set; } =
        FloatingMediaHostLifetimeState.Visible;

    public bool IsHiddenRetained => State is FloatingMediaHostLifetimeState.HiddenRetained;

    public bool CanRetain(
        bool isClosingAllWindows,
        int activeRuntimeCount,
        bool hasOtherVisibleMainWindow)
    {
        return State is FloatingMediaHostLifetimeState.Visible
            && !isClosingAllWindows
            && activeRuntimeCount > 0
            && hasOtherVisibleMainWindow;
    }

    public bool TryRetain(
        bool isClosingAllWindows,
        int activeRuntimeCount,
        bool hasOtherVisibleMainWindow)
    {
        if (!CanRetain(
            isClosingAllWindows,
            activeRuntimeCount,
            hasOtherVisibleMainWindow))
        {
            return false;
        }

        State = FloatingMediaHostLifetimeState.HiddenRetained;
        return true;
    }

    public bool TryRestore()
    {
        if (State is not FloatingMediaHostLifetimeState.HiddenRetained)
        {
            return false;
        }

        State = FloatingMediaHostLifetimeState.Visible;
        return true;
    }

    public bool TryCommitCloseWhenEmpty(int activeRuntimeCount)
    {
        if (State is not FloatingMediaHostLifetimeState.HiddenRetained
            || activeRuntimeCount > 0)
        {
            return false;
        }

        State = FloatingMediaHostLifetimeState.Closing;
        return true;
    }

    public void MarkClosing()
    {
        State = FloatingMediaHostLifetimeState.Closing;
    }
}
