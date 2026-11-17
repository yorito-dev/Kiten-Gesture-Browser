namespace TabBrowser.Services;

internal sealed class WebMessageExceptionDiagnosticGate(Func<DateTimeOffset> getNow)
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<(string Type, string Exception), DateTimeOffset> _lastLogged = [];

    public bool TryGetFields(string? messageType, Exception exception, out string[] fields)
    {
        var type = NormalizeType(messageType);
        var exceptionType = exception.GetType().Name;
        var now = getNow();
        lock (_gate)
        {
            var key = (type, exceptionType);
            if (_lastLogged.TryGetValue(key, out var lastLogged)
                && now - lastLogged < LogInterval)
            {
                fields = [];
                return false;
            }

            _lastLogged[key] = now;
        }

        fields = ["web_message_error", $"type={type}", $"exception={exceptionType}"];
        return true;
    }

    private static string NormalizeType(string? messageType)
        => messageType is "superDrag"
            or "gestureVisualStart"
            or "gestureVisualState"
            or "gestureVisualEnd"
            or "gesture"
            or "openBackgroundTab"
            or "newTabSearch"
            or "openBookmark"
            or "deleteBookmark"
            or "showBookmarkPreview"
            or "hideBookmarkPreview"
            or "linkHover"
            or "linkHoverEnd"
            or "pageContextMenu"
            or "youtubeControlActivity"
            or "youtubePlaybackState"
            or StreamingPlaybackCompatibilityService.MessageType

            or YouTubePlaybackDiagnosticService.MessageType
            ? messageType
            : "unknown";
}
