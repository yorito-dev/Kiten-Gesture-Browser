namespace TabBrowser.Services;

public static class YouTubeUrlService
{
    private static readonly HashSet<string> NavigationHistoryIgnoredQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "autoplay",
        "embeds_referring_euri",
        "feature",
        "pp",
        "si",
        "source_ve_path"
    };

    public static bool IsYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsYouTubeUrl(url))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length > 1;
        }

        if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            return HasQueryValue(uri.Query, "v");
        }

        return path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsShortsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsYouTubeUrl(url))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return pathSegments.Length >= 2
            && pathSegments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(pathSegments[1]);
    }

    public static bool TryGetVideoId(string? url, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsYouTubeUrl(url))
        {
            return false;
        }

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;
            videoId = Uri.UnescapeDataString(videoId);
            return !string.IsNullOrWhiteSpace(videoId);
        }

        var pathSegments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length >= 2
            && (pathSegments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                || pathSegments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                || pathSegments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
        {
            videoId = Uri.UnescapeDataString(pathSegments[1]);
            return !string.IsNullOrWhiteSpace(videoId);
        }

        if (uri.AbsolutePath.TrimEnd('/').Equals("/watch", StringComparison.OrdinalIgnoreCase)
            && TryGetQueryValue(uri.Query, "v", out videoId))
        {
            return true;
        }

        videoId = string.Empty;
        return false;
    }

    public static Uri? CreateThumbnailUri(string? url)
    {
        return TryGetVideoId(url, out var videoId)
            ? new Uri($"https://i.ytimg.com/vi/{Uri.EscapeDataString(videoId)}/mqdefault.jpg")
            : null;
    }

    public static string NormalizeShortsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsYouTubeUrl(url))
        {
            return url;
        }

        var pathSegments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length < 2 || !pathSegments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var videoId = Uri.UnescapeDataString(pathSegments[1]);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return url;
        }

        return "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);
    }

    public static string WithAutoplayDisabled(string url)
    {
        return SetQueryValue(url, "autoplay", "0");
    }

    public static string WithPlaybackPosition(string url, double? positionSeconds)
    {
        var normalizedPosition = YouTubeControlScriptService.NormalizePlaybackPosition(positionSeconds);
        if (!IsVideoUrl(url) || normalizedPosition is not { } position || position < 1)
        {
            return url;
        }

        return SetQueryValue(
            url,
            "t",
            ((long)Math.Floor(position)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s");
    }

    public static string RemoveNoAutoplayQuery(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsVideoUrl(url)
            || !TryGetQueryValue(uri.Query, "autoplay", out var autoplay)
            || !autoplay.Equals("0", StringComparison.Ordinal))
        {
            return url;
        }

        return RemoveQueryValue(url, "autoplay");
    }

    public static string NormalizeNavigationHistoryUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsVideoUrl(url))
        {
            return url;
        }

        var queryItems = ParseQueryItems(uri.Query);
        if (!queryItems.Any(static item => NavigationHistoryIgnoredQueryNames.Contains(item.Name)))
        {
            return url;
        }

        return RewriteQuery(
            url,
            items => items.RemoveAll(item => NavigationHistoryIgnoredQueryNames.Contains(item.Name)));
    }

    private static bool HasQueryValue(string query, string name)
    {
        return TryGetQueryValue(query, name, out _);
    }

    private static bool TryGetQueryValue(string query, string name, out string result)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            result = string.Empty;
            return false;
        }

        var normalizedQuery = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in normalizedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            var key = separatorIndex < 0 ? part : part[..separatorIndex];
            if (!Uri.UnescapeDataString(key).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = separatorIndex < 0 ? string.Empty : part[(separatorIndex + 1)..];
            result = Uri.UnescapeDataString(value);
            return !string.IsNullOrWhiteSpace(result);
        }

        result = string.Empty;
        return false;
    }

    private static string SetQueryValue(string url, string name, string value)
    {
        return RewriteQuery(url, queryItems =>
        {
            queryItems.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            queryItems.Add((name, value));
        });
    }

    private static string RemoveQueryValue(string url, string name)
    {
        return RewriteQuery(
            url,
            queryItems => queryItems.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string RewriteQuery(string url, Action<List<(string Name, string Value)>> rewrite)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var queryItems = ParseQueryItems(uri.Query);
        rewrite(queryItems);
        var builder = new UriBuilder(uri)
        {
            Query = string.Join(
                "&",
                queryItems.Select(static item =>
                    Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value)))
        };
        return builder.Uri.AbsoluteUri;
    }

    private static List<(string Name, string Value)> ParseQueryItems(string query)
    {
        var result = new List<(string Name, string Value)>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var normalizedQuery = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in normalizedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            var name = separatorIndex < 0 ? part : part[..separatorIndex];
            var value = separatorIndex < 0 ? string.Empty : part[(separatorIndex + 1)..];
            result.Add((Uri.UnescapeDataString(name), Uri.UnescapeDataString(value)));
        }

        return result;
    }
}
