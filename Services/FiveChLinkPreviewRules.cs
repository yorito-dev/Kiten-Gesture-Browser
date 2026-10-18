using System.Text.RegularExpressions;

namespace TabBrowser.Services;

// 表示機能を含めず、リンク先プレビューの抑止に必要なURL判定だけを共用する。
public sealed partial class FiveChLiveReadabilityService
{
    public bool IsFiveChThreadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsFiveChHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4
            && segments[0].Equals("test", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("read.cgi", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(segments[2])
            && long.TryParse(segments[3], out _);
    }

    public bool IsReplyAnchorLink(string? currentUrl, string? targetUrl, string? linkText)
    {
        if (!IsFiveChThreadUrl(currentUrl)
            || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
        {
            return false;
        }

        if (Regex.IsMatch(linkText?.Trim() ?? string.Empty, @"^(?:>>|＞＞)\s*\d+"))
        {
            return true;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || !currentUri.Host.Equals(targetUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentSegments = currentUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var targetSegments = targetUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (currentSegments.Length < 4
            || targetSegments.Length < 4
            || !currentSegments.Take(4).SequenceEqual(targetSegments.Take(4), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return targetSegments.Length >= 5 && long.TryParse(targetSegments[4], out _)
            || long.TryParse(targetUri.Fragment.TrimStart('#'), out _);
    }

    private static bool IsFiveChHost(string host)
    {
        return host.Equals("5ch.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".5ch.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("5ch.io", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".5ch.io", StringComparison.OrdinalIgnoreCase);
    }

}
