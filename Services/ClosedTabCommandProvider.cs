using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ClosedTabCommandProvider
{
    public const string RestoreClosedTabActionId = "closedTab.restore";

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<ClosedTabState> closedTabs)
    {
        return closedTabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.Url))
            .Select(CreateItem)
            .ToList();
    }

    private static CommandPaletteItem CreateItem(ClosedTabState closedTab, int index)
    {
        var url = closedTab.Url?.Trim() ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(closedTab.Title)
            ? GetUrlTitle(url, "閉じたタブ")
            : closedTab.Title.Trim();

        return new CommandPaletteItem
        {
            Id = "closed:" + index.ToString("D"),
            Type = CommandPaletteItemType.RecentlyClosed,
            Title = title,
            Subtitle = url,
            Url = url,
            ActionId = RestoreClosedTabActionId,
            Payload = closedTab,
            Keywords = [title, url, "closed", "restore", "reopen", "recent"]
        };
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
