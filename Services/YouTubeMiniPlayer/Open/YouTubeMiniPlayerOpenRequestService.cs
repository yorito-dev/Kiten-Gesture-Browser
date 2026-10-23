using System.Text.Json;

namespace TabBrowser.Services;

public sealed record YouTubeMiniPlayerOpenRequest(
    string Url,
    string? Title,
    bool IsShorts,
    double? PositionSeconds = null);

public static class YouTubeMiniPlayerOpenRequestService
{
    internal static async Task<YouTubeMiniPlayerOpenRequest?> CapturePositionAsync(
        YouTubeMiniPlayerOpenRequest request,
        Func<bool> canOpen,
        Func<string?> getCurrentUrl,
        Func<string, Task<string?>> executeScriptAsync)
    {
        if (!canOpen() || !YouTubeUrlService.TryGetVideoId(request.Url, out var videoId))
        {
            return null;
        }

        double? position = null;
        try
        {
            var json = await executeScriptAsync(YouTubeControlScriptService.BuildMiniPlayerStartPositionReadScript(videoId))
                .WaitAsync(TimeSpan.FromSeconds(2));
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Number
                    && document.RootElement.TryGetDouble(out var value))
                {
                    position = YouTubeControlScriptService.NormalizePlaybackPosition(value);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or TimeoutException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            // 読み取れない場合は、従来どおり開始位置を指定せずに開く。
        }

        return canOpen()
            && YouTubeUrlService.TryGetVideoId(getCurrentUrl() ?? string.Empty, out var currentVideoId)
            && string.Equals(videoId, currentVideoId, StringComparison.Ordinal)
                ? request with { PositionSeconds = position }
                : null;
    }

    public static bool TryCreateFromTab(
        string? pageUrl,
        string? title,
        out YouTubeMiniPlayerOpenRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(pageUrl)
            || !YouTubeMiniPlayerNavigationService.TryCreateMiniPlayerUrl(pageUrl, out var miniPlayerUrl, out var isShorts))
        {
            return false;
        }

        request = new YouTubeMiniPlayerOpenRequest(miniPlayerUrl, title, isShorts);
        return true;
    }

    public static bool TryCreateFromLink(
        string linkUri,
        string? linkText,
        out YouTubeMiniPlayerOpenRequest request)
    {
        request = default!;
        if (!YouTubeMiniPlayerNavigationService.TryCreateMiniPlayerUrl(linkUri, out var miniPlayerUrl, out var isShorts))
        {
            return false;
        }

        request = new YouTubeMiniPlayerOpenRequest(miniPlayerUrl, linkText ?? "YouTube", isShorts);
        return true;
    }

    public static bool TryCreateNavigationUri(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var candidate)
            && YouTubeUrlService.IsYouTubeUrl(url))
        {
            uri = candidate;
            return true;
        }

        uri = default!;
        return false;
    }
}
