namespace TabBrowser.Services;

internal sealed class WebViewEnvironmentExtensionModeCoordinator
{
    private readonly object _gate = new();
    private bool? _browserExtensionsEnabledForCurrentRun;

    public bool Resolve(bool isPrivateWindow, bool configuredExtensionsEnabled)
    {
        if (isPrivateWindow)
        {
            return false;
        }

        lock (_gate)
        {
            _browserExtensionsEnabledForCurrentRun ??= configuredExtensionsEnabled;
            return _browserExtensionsEnabledForCurrentRun.Value;
        }
    }
}
