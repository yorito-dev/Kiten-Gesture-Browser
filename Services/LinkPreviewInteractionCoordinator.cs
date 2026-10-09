namespace TabBrowser.Services;

internal readonly record struct LinkPreviewPendingSnapshot(
    Guid SourceTabId,
    string Url,
    string Title,
    double ClientX,
    double ClientY);

internal sealed class LinkPreviewInteractionCoordinator
{
    private Guid? _pendingSourceTabId;
    private string? _pendingUrl;
    private string _pendingTitle = string.Empty;
    private double _pendingClientX;
    private double _pendingClientY;
    private int _generation;
    private Guid? _hoverStatusSourceTabId;
    private string? _hoverStatusText;

    public void SetPending(
        Guid sourceTabId,
        string url,
        string title,
        double clientX,
        double clientY)
    {
        _pendingSourceTabId = sourceTabId;
        _pendingUrl = url;
        _pendingTitle = title;
        _pendingClientX = clientX;
        _pendingClientY = clientY;
    }

    public bool TryCapturePending(out LinkPreviewPendingSnapshot snapshot)
    {
        if (_pendingSourceTabId is not { } sourceTabId
            || string.IsNullOrWhiteSpace(_pendingUrl))
        {
            snapshot = default;
            return false;
        }

        snapshot = new LinkPreviewPendingSnapshot(
            sourceTabId,
            _pendingUrl,
            _pendingTitle,
            _pendingClientX,
            _pendingClientY);
        return true;
    }

    public int BeginDisplay()
    {
        return ++_generation;
    }

    public bool IsDisplayCurrent(int generation, string url)
    {
        return generation == _generation
            && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase);
    }

    public bool OwnsPending(Guid sourceTabId)
    {
        return _pendingSourceTabId == sourceTabId;
    }

    public void ClearPendingAndInvalidate()
    {
        _pendingSourceTabId = null;
        _pendingUrl = null;
        _pendingTitle = string.Empty;
        _pendingClientX = 0;
        _pendingClientY = 0;
        _generation++;
    }

    public bool TrySetHoverStatus(Guid sourceTabId, string statusText)
    {
        if (_hoverStatusSourceTabId == sourceTabId
            && string.Equals(_hoverStatusText, statusText, StringComparison.Ordinal))
        {
            return false;
        }

        _hoverStatusSourceTabId = sourceTabId;
        _hoverStatusText = statusText;
        return true;
    }

    public bool OwnsHoverStatus(Guid sourceTabId)
    {
        return _hoverStatusSourceTabId == sourceTabId;
    }

    public bool TryClearHoverStatus(Guid? sourceTabId, out string? statusText)
    {
        if (sourceTabId is not null && _hoverStatusSourceTabId != sourceTabId)
        {
            statusText = null;
            return false;
        }

        statusText = _hoverStatusText;
        _hoverStatusSourceTabId = null;
        _hoverStatusText = null;
        return true;
    }
}
