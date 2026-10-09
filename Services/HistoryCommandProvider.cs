using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class HistoryCommandProvider
{
    public const string OpenHistoryActionId = "history.open";

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<BrowserTab> tabs)
    {
        var items = new List<CommandPaletteItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in tabs)
        {
            AddUrl(items, seenUrls, tab.Url, tab.DisplayTitle, tab.Favicon);
            foreach (var historyUrl in tab.NavigationHistory)
            {
                AddUrl(items, seenUrls, historyUrl, null, null);
            }
        }

        return items;
    }

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<BrowsingHistoryEntry> entries)
    {
        return entries
            .Where(static entry => entry.Id > 0 && NormalizeUrl(entry.Url) is not null)
            .GroupBy(static entry => entry.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static entry => entry.VisitedAt).First())
            .Select(entry => new CommandPaletteItem
            {
                Id = "browsing-history:" + entry.Id,
                Type = CommandPaletteItemType.History,
                Title = string.IsNullOrWhiteSpace(entry.Title) ? GetUrlTitle(entry.Url, "履歴") : entry.Title,
                Subtitle = entry.Url,
                Url = entry.Url,
                ActionId = OpenHistoryActionId,
                Payload = entry,
                Keywords = [entry.Title, entry.Url, entry.Host, "history", "visited", "閲覧履歴"]
            })
            .ToList();
    }

    private static void AddUrl(
        List<CommandPaletteItem> items,
        HashSet<string> seenUrls,
        string? rawUrl,
        string? title,
        ImageSource? icon)
    {
        var url = NormalizeUrl(rawUrl);
        if (url is null || !seenUrls.Add(url))
        {
            return;
        }

        var itemTitle = string.IsNullOrWhiteSpace(title)
            ? GetUrlTitle(url, "履歴")
            : title.Trim();

        items.Add(new CommandPaletteItem
        {
            Id = "history:" + items.Count.ToString("D"),
            Type = CommandPaletteItemType.History,
            Title = itemTitle,
            Subtitle = url,
            Url = url,
            Icon = icon,
            ActionId = OpenHistoryActionId,
            Payload = url,
            Keywords = [itemTitle, url, "history", "visited"]
        });
    }

    private static string? NormalizeUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var url = rawUrl.Trim();
        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("tabbrowser://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return null;
    }

    private static string GetUrlTitle(string url, string fallback)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return string.IsNullOrWhiteSpace(url) ? fallback : url;
    }
}
