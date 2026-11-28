namespace TabBrowser.Services;

public static class BrowserUrlRulesService
{
    public static bool IsRemoteFileUrl(Uri uri)
    {
        return uri.IsFile
            && (!string.IsNullOrEmpty(uri.Host)
                || uri.IsUnc
                || uri.LocalPath.StartsWith(@"\\", StringComparison.Ordinal)
                || uri.LocalPath.StartsWith("//", StringComparison.Ordinal));
    }

    public static bool IsPageRequestedFileUrl(string? url)
    {
        return url?.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true
            || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile);
    }

    public static string NormalizeNavigationHistoryUrl(string url)
    {
        var normalizedUrl = YouTubeUrlService.NormalizeNavigationHistoryUrl(url);
        return NormalizeAmazonJapanProductNavigationHistoryUrl(normalizedUrl);
    }

    private static string NormalizeAmazonJapanProductNavigationHistoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !HostMatches(uri.Host, "amazon.co.jp"))
        {
            return url;
        }

        var pathSegments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? productId = null;
        for (var index = 0; index + 1 < pathSegments.Length; index++)
        {
            if (!pathSegments[index].Equals("dp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Uri.UnescapeDataString(pathSegments[index + 1]);
            if (candidate.Length == 10 && candidate.All(static value => char.IsAsciiLetterOrDigit(value)))
            {
                productId = candidate.ToUpperInvariant();
                break;
            }
        }

        if (productId is null)
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/dp/" + productId,
            Query = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static bool IsSelectedTextTranslationUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IdnHost.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath == "/"
            && (HasTextParameter(uri.Query) || HasTextParameter(uri.Fragment));

        static bool HasTextParameter(string parameters)
        {
            return parameters.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries).Any(static parameter =>
            {
                var separator = parameter.IndexOf('=');
                return separator >= 0 && separator < parameter.Length - 1
                    && Uri.UnescapeDataString(parameter[..separator]).Equals("text", StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    internal static bool IsBlankPopupFromCurrentWebPage(string? currentSource, string? requestFrameSource)
    {
        return Uri.TryCreate(currentSource, UriKind.Absolute, out var current)
            && current.Scheme is "http" or "https"
            && Uri.TryCreate(requestFrameSource, UriKind.Absolute, out var source)
            && string.Equals(current.AbsoluteUri, source.AbsoluteUri, StringComparison.Ordinal);
    }

    public static bool IsAuthenticationPopupUrl(string? url, bool isUserInitiated = true)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (isUserInitiated && IsFirebasePopupAuthHandler(uri))
        {
            return true;
        }

        if (!IsXOrTwitterHost(host))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.StartsWith("/i/flow/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/i/oauth2/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/oauth/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFirebasePopupAuthHandler(Uri uri)
    {
        return uri.AbsolutePath.Equals("/__/auth/handler", StringComparison.OrdinalIgnoreCase)
            && QueryContainsValue(uri.Query, "authType", "signInViaPopup");
    }

    public static string GetAuthenticationPopupInitialTitle(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "ログイン";
    }

    private static bool QueryContainsValue(string query, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var normalizedQuery = query[0] == '?'
            ? query[1..]
            : query;
        foreach (var parameter in normalizedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = parameter.IndexOf('=');
            var parameterName = separatorIndex >= 0
                ? parameter[..separatorIndex]
                : parameter;
            var parameterValue = separatorIndex >= 0
                ? parameter[(separatorIndex + 1)..]
                : string.Empty;
            if (Uri.UnescapeDataString(parameterName.Replace('+', ' ')).Equals(name, StringComparison.OrdinalIgnoreCase)
                && Uri.UnescapeDataString(parameterValue.Replace('+', ' ')).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsXOrTwitterUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsXOrTwitterHost(uri.Host);
    }

    public static bool IsXOrTwitterHost(string host)
    {
        return host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldDeferBackgroundMediaLoad(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && IsBackgroundMediaPlaybackUrl(url)
            && !YouTubeUrlService.IsVideoUrl(url);
    }

    public static bool IsBackgroundMediaPlaybackUrl(string url)
    {
        if (YouTubeUrlService.IsVideoUrl(url))
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return HostMatches(host, "twitch.tv")
            || HostMatches(host, "nicovideo.jp")
            || HostMatches(host, "niconico.com")
            || HostMatches(host, "vimeo.com")
            || HostMatches(host, "bilibili.com");
    }

    public static bool HostMatches(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}
