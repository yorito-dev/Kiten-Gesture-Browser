namespace TabBrowser.Services;

internal sealed class PopupNavigationRequestCoordinator
{
    private int _requestVersion;
    private string? _currentUrl;

    public int BeginRequest()
    {
        return AdvanceVersion();
    }

    public bool IsCurrent(int version)
    {
        return version == _requestVersion;
    }

    public bool IsCurrentUrl(string url)
    {
        return string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase);
    }

    public void SetCurrentUrl(string url)
    {
        _currentUrl = url;
    }

    public void ClearCurrentUrl()
    {
        _currentUrl = null;
    }

    public void Invalidate()
    {
        AdvanceVersion();
    }

    private int AdvanceVersion()
    {
        _requestVersion = unchecked(_requestVersion + 1);
        return _requestVersion;
    }
}
