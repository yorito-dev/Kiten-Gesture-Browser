namespace TabBrowser.Services;

internal sealed class TabActivationWorkCoordinator
{
    private int _selectedActivationGeneration;
    private int _rightWheelActivationGeneration;

    public bool IsRightWheelActivationPending { get; private set; }

    public int BeginSelectedActivation()
    {
        return ++_selectedActivationGeneration;
    }

    public bool IsCurrentSelectedActivation(int generation)
    {
        return generation == _selectedActivationGeneration;
    }

    public int BeginRightWheelActivation()
    {
        IsRightWheelActivationPending = true;
        return ++_rightWheelActivationGeneration;
    }

    public bool IsCurrentRightWheelActivation(int generation)
    {
        return generation == _rightWheelActivationGeneration;
    }

    public bool TryCompleteRightWheelActivation(int generation)
    {
        if (!IsCurrentRightWheelActivation(generation))
        {
            return false;
        }

        IsRightWheelActivationPending = false;
        return true;
    }

    public void SupersedeRightWheelActivation()
    {
        IsRightWheelActivationPending = false;
        _rightWheelActivationGeneration++;
    }
}
