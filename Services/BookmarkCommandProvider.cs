using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class BookmarkCommandProvider
{
    public const string OpenBookmarkActionId = "bookmark.open";

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<BookmarkItem> bookmarks)
    {
        return bookmarks
            .Where(static bookmark => !string.IsNullOrWhiteSpace(bookmark.Url))
            .Select(CreateItem)
            .ToList();
    }

    private static CommandPaletteItem CreateItem(BookmarkItem bookmark)
    {
        var url = bookmark.Url.Trim();
        var title = string.IsNullOrWhiteSpace(bookmark.Title)
            ? GetUrlTitle(url, "ブックマーク")
            : bookmark.Title.Trim();
        var folderPath = bookmark.FolderPath?.Trim() ?? string.Empty;

        return new CommandPaletteItem
        {
            Id = "bookmark:" + bookmark.Id.ToString("D"),
            Type = CommandPaletteItemType.Bookmark,
            Title = title,
            Subtitle = string.IsNullOrWhiteSpace(folderPath) ? url : folderPath + " - " + url,
            Url = url,
            ActionId = OpenBookmarkActionId,
            Payload = bookmark,
            Keywords = [title, url, folderPath, "bookmark", "bookmarks"]
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
