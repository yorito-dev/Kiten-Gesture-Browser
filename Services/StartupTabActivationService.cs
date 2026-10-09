namespace TabBrowser.Services;

public sealed class StartupTabActivationService
{
    public TimeSpan GetRestoredWindowStartupActivationDelay(
        int windowIndex,
        int windowCount,
        TimeSpan stepDelay,
        TimeSpan maxDelay)
    {
        if (windowCount <= 1 || windowIndex <= 0 || stepDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var delay = TimeSpan.FromMilliseconds(stepDelay.TotalMilliseconds * windowIndex);
        return maxDelay > TimeSpan.Zero && delay > maxDelay
            ? maxDelay
            : delay;
    }
}
