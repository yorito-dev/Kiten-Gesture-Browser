using System.Net;

namespace TabBrowser.Services;

public enum LinkCopyFormat
{
    Url,
    Title,
    TitleAndUrl,
    Markdown,
    Html
}

public sealed record LinkCopyTarget(string Url, string? Title);

public sealed record TabExportItem(string Id, string Url, string? Title);

public static class LinkFormatters
{
    public static string Format(LinkCopyTarget target, LinkCopyFormat format)
    {
        var url = NormalizeUrl(target.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL が空です。", nameof(target));
        }

        var title = NormalizeTitle(target.Title, url);
        return format switch
        {
            LinkCopyFormat.Url => url,
            LinkCopyFormat.Title => title,
            LinkCopyFormat.TitleAndUrl => title + Environment.NewLine + url,
            LinkCopyFormat.Markdown => $"[{EscapeMarkdownText(title)}]({EscapeMarkdownUrl(url)})",
            LinkCopyFormat.Html => $"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(title)}</a>",
            _ => url
        };
    }

    public static string FormatMarkdownList(IEnumerable<TabExportItem> items)
    {
        var lines = new List<string>
        {
            "## 開いているタブ",
            string.Empty
        };

        foreach (var item in items)
        {
            var target = new LinkCopyTarget(item.Url, item.Title);
            lines.Add("- " + Format(target, LinkCopyFormat.Markdown));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string NormalizeTitle(string? title, string url)
    {
        var normalized = NormalizeSingleLine(title);
        return string.IsNullOrWhiteSpace(normalized)
            ? NormalizeUrl(url)
            : normalized;
    }

    public static string NormalizeUrl(string? url)
    {
        return NormalizeSingleLine(url);
    }

    private static string EscapeMarkdownText(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownUrl(string value)
    {
        return value
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
