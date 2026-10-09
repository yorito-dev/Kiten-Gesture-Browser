namespace TabBrowser.Services;

public sealed record YouTubeMiniPlayerOpenRequest(
    string Url,
    string? Title,
    bool IsShorts);

public static class YouTubeMiniPlayerOpenRequestService
{
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
