using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

// 既存の階層・並び順と共通メニュースタイルを維持して表示項目を構築する。
internal sealed class BookmarkMenuController(
    MenuItem root,
    IReadOnlyCollection<BookmarkItem> bookmarks,
    IReadOnlyCollection<BookmarkFolderItem> folders,
    Func<BookmarkItem, Task> openBookmark,
    Action<string> setStatus)
{
    private readonly MenuItem _root = root;
    private readonly IReadOnlyCollection<BookmarkItem> _bookmarks = bookmarks;
    private readonly IReadOnlyCollection<BookmarkFolderItem> _bookmarkFolders = folders;
    private readonly Func<BookmarkItem, Task> _openBookmark = openBookmark;
    private readonly Action<string> _setStatus = setStatus;

    internal void Rebuild()
    {
        _root.Items.Clear();

        if (_bookmarks.Count == 0)
        {
            _root.Items.Add(CreateDisabledMenuItem("ブックマークはありません"));
            return;
        }

        var folderPaths = _bookmarkFolders
            .Select(static folder => BookmarkService.NormalizeFolderPath(folder.Path))
            .Concat(_bookmarks.SelectMany(static bookmark => BookmarkService.GetFolderAndParents(bookmark.FolderPath)))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(GetBookmarkFolderDepth)
            .ThenBy(GetBookmarkFolderSortOrder)
            .ThenBy(static path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var folderMenus = new Dictionary<string, MenuItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folderPath in folderPaths)
        {
            folderMenus[folderPath] = new MenuItem
            {
                Header = GetBookmarkFolderDisplayName(folderPath),
                ToolTip = folderPath
            };
        }

        foreach (var folderPath in folderPaths)
        {
            var folderMenu = folderMenus[folderPath];
            var parentPath = GetBookmarkFolderParentPath(folderPath);
            if (!string.IsNullOrWhiteSpace(parentPath) && folderMenus.TryGetValue(parentPath, out var parentMenu))
            {
                parentMenu.Items.Add(folderMenu);
            }
            else
            {
                _root.Items.Add(folderMenu);
            }
        }

        var rootBookmarks = _bookmarks
            .Where(static bookmark => string.IsNullOrWhiteSpace(BookmarkService.NormalizeFolderPath(bookmark.FolderPath)))
            .OrderBy(static bookmark => bookmark.SortOrder)
            .ThenBy(GetBookmarkMenuSortText, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (_root.Items.Count > 0 && rootBookmarks.Count > 0)
        {
            _root.Items.Add(new Separator());
        }

        foreach (var bookmark in rootBookmarks)
        {
            _root.Items.Add(CreateBookmarkMenuItem(bookmark));
        }

        var bookmarksByFolder = _bookmarks
            .Where(static bookmark => !string.IsNullOrWhiteSpace(BookmarkService.NormalizeFolderPath(bookmark.FolderPath)))
            .GroupBy(
                static bookmark => BookmarkService.NormalizeFolderPath(bookmark.FolderPath),
                StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in bookmarksByFolder)
        {
            if (!folderMenus.TryGetValue(group.Key, out var folderMenu))
            {
                continue;
            }

            if (folderMenu.Items.Count > 0)
            {
                folderMenu.Items.Add(new Separator());
            }

            foreach (var bookmark in group
                .OrderBy(static bookmark => bookmark.SortOrder)
                .ThenBy(GetBookmarkMenuSortText, StringComparer.CurrentCultureIgnoreCase))
            {
                folderMenu.Items.Add(CreateBookmarkMenuItem(bookmark));
            }
        }

        foreach (var folderMenu in folderMenus.Values)
        {
            if (folderMenu.Items.Count == 0)
            {
                folderMenu.Items.Add(CreateDisabledMenuItem("空のフォルダ"));
            }
        }
    }

    private MenuItem CreateBookmarkMenuItem(BookmarkItem bookmark)
    {
        var item = new MenuItem
        {
            Header = TrimMenuText(GetBookmarkMenuSortText(bookmark), 72),
            ToolTip = bookmark.Url
        };
        item.Click += async (_, _) =>
        {
            try
            {
                await _openBookmark(bookmark);
            }
            catch (Exception ex)
            {
                _setStatus("ブックマークを開けません: " + ex.Message);
            }
        };
        return item;
    }

    private int GetBookmarkFolderSortOrder(string folderPath)
    {
        var normalizedPath = BookmarkService.NormalizeFolderPath(folderPath);
        var folder = _bookmarkFolders.FirstOrDefault(folder =>
            BookmarkService.NormalizeFolderPath(folder.Path).Equals(normalizedPath, StringComparison.CurrentCultureIgnoreCase));
        return folder?.SortOrder ?? int.MaxValue;
    }

    private static int GetBookmarkFolderDepth(string folderPath)
    {
        return SplitBookmarkFolderPath(folderPath).Count;
    }

    private static string GetBookmarkFolderParentPath(string folderPath)
    {
        var segments = SplitBookmarkFolderPath(folderPath);
        return segments.Count <= 1
            ? string.Empty
            : string.Join(" / ", segments.Take(segments.Count - 1));
    }

    private static string GetBookmarkFolderDisplayName(string folderPath)
    {
        var segments = SplitBookmarkFolderPath(folderPath);
        return TrimMenuText(segments.LastOrDefault() ?? folderPath, 48);
    }

    private static IReadOnlyList<string> SplitBookmarkFolderPath(string folderPath)
    {
        return BookmarkService.NormalizeFolderPath(folderPath)
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();
    }

    private static string GetBookmarkMenuSortText(BookmarkItem bookmark)
    {
        return string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title;
    }

    private static string TrimMenuText(string text, int maxLength)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "(無題)" : text.Trim();
        return value.Length <= maxLength ? value : value[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static MenuItem CreateDisabledMenuItem(string header)
    {
        return new MenuItem
        {
            Header = header,
            IsEnabled = false
        };
    }

}
