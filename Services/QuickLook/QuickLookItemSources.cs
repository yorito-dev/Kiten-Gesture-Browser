using System.IO;
using System.Windows.Media;
using TabBrowser.Models.QuickLook;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.QuickLook;

public sealed class FolderTabQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : DelegateQuickLookItemSource(sourceId, getItems, getSelectedItems, getCurrentIndex, selectIndex);

public sealed class WorkspaceQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : DelegateQuickLookItemSource(sourceId, getItems, getSelectedItems, getCurrentIndex, selectIndex);

public sealed class SearchQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : DelegateQuickLookItemSource(sourceId, getItems, getSelectedItems, getCurrentIndex, selectIndex);

public sealed class RecentQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : DelegateQuickLookItemSource(sourceId, getItems, getSelectedItems, getCurrentIndex, selectIndex);

public sealed class InboxQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : DelegateQuickLookItemSource(sourceId, getItems, getSelectedItems, getCurrentIndex, selectIndex);

public abstract class DelegateQuickLookItemSource(
    string sourceId,
    Func<IReadOnlyList<QuickLookItem>> getItems,
    Func<IReadOnlyList<QuickLookItem>> getSelectedItems,
    Func<int> getCurrentIndex,
    Action<int> selectIndex)
    : IQuickLookItemSource
{
    public string SourceId { get; } = sourceId;

    public QuickLookItem? Current
    {
        get
        {
            var items = getItems();
            var index = getCurrentIndex();
            return index >= 0 && index < items.Count ? items[index] : null;
        }
    }

    public IReadOnlyList<QuickLookItem> SelectedItems => getSelectedItems();

    public bool CanMoveNext
    {
        get
        {
            var items = getItems();
            var index = getCurrentIndex();
            return index >= 0 && index < items.Count - 1;
        }
    }

    public bool CanMovePrevious => getCurrentIndex() > 0;

    public QuickLookItem? MoveNext()
    {
        return Move(1);
    }

    public QuickLookItem? MovePrevious()
    {
        return Move(-1);
    }

    private QuickLookItem? Move(int offset)
    {
        var items = getItems();
        var nextIndex = getCurrentIndex() + offset;
        if (nextIndex < 0 || nextIndex >= items.Count)
        {
            return null;
        }

        selectIndex(nextIndex);
        return items[nextIndex];
    }
}

public static class QuickLookItemFactory
{
    public static QuickLookItem FromWorkspaceItem(
        WorkspaceItem item,
        string sourceId,
        string? displayName = null,
        IReadOnlyList<QuickLookAction>? actions = null)
    {
        var title = string.IsNullOrWhiteSpace(displayName)
            ? GetWorkspaceItemTitle(item)
            : displayName.Trim();
        return item switch
        {
            ShellItem shellItem => new QuickLookItem
            {
                DisplayName = title,
                FilePath = shellItem.TargetPath,
                Kind = shellItem.Kind == ShellItemKind.Directory
                    ? QuickLookItemKind.Directory
                    : QuickLookItemKind.File,
                ModifiedAt = shellItem.UpdatedAt,
                SourceId = sourceId,
                ThumbnailPngBase64 = shellItem.ThumbnailPngBase64,
                Actions = actions ?? []
            },
            NoteItem note => new QuickLookItem
            {
                DisplayName = title,
                Kind = QuickLookItemKind.Text,
                InlineText = note.Content,
                ModifiedAt = note.UpdatedAt,
                SourceId = sourceId,
                ThumbnailPngBase64 = note.ThumbnailPngBase64,
                Actions = actions ?? []
            },
            BookmarkItem bookmark => new QuickLookItem
            {
                DisplayName = title,
                Uri = bookmark.Url,
                Kind = QuickLookItemKind.Uri,
                ModifiedAt = bookmark.UpdatedAt,
                SourceId = sourceId,
                Description = bookmark.Note,
                ThumbnailPngBase64 = bookmark.ThumbnailPngBase64,
                FaviconPngBase64 = bookmark.FaviconPngBase64,
                Actions = actions ?? []
            },
            FolderItem folder => new QuickLookItem
            {
                DisplayName = title,
                Kind = QuickLookItemKind.Information,
                ModifiedAt = folder.UpdatedAt,
                SourceId = sourceId,
                Description = "Workspace内の論理フォルダー",
                ThumbnailPngBase64 = folder.ThumbnailPngBase64,
                Actions = actions ?? []
            },
            _ => new QuickLookItem
            {
                DisplayName = title,
                Uri = string.IsNullOrWhiteSpace(item.Url) ? null : item.Url,
                Kind = QuickLookItemKind.Information,
                ModifiedAt = item.UpdatedAt,
                SourceId = sourceId,
                Description = item.Type.ToString(),
                ThumbnailPngBase64 = item.ThumbnailPngBase64,
                FaviconPngBase64 = item.FaviconPngBase64,
                Actions = actions ?? []
            }
        };
    }

    public static QuickLookItem FromAddress(
        string displayName,
        string? address,
        string sourceId,
        string? description = null,
        IReadOnlyList<QuickLookAction>? actions = null,
        string? thumbnailPngBase64 = null,
        ImageSource? thumbnail = null,
        string? faviconPngBase64 = null,
        ImageSource? favicon = null)
    {
        if (TryGetFilePath(address, out var filePath))
        {
            return new QuickLookItem
            {
                DisplayName = displayName,
                FilePath = filePath,
                Kind = Directory.Exists(filePath)
                    ? QuickLookItemKind.Directory
                    : QuickLookItemKind.File,
                SourceId = sourceId,
                Description = description,
                Thumbnail = thumbnail,
                ThumbnailPngBase64 = thumbnailPngBase64 ?? string.Empty,
                Favicon = favicon,
                FaviconPngBase64 = faviconPngBase64 ?? string.Empty,
                Actions = actions ?? []
            };
        }

        return new QuickLookItem
        {
            DisplayName = displayName,
            Uri = string.IsNullOrWhiteSpace(address) ? null : address,
            Kind = string.IsNullOrWhiteSpace(address)
                ? QuickLookItemKind.Information
                : QuickLookItemKind.Uri,
            SourceId = sourceId,
            Description = description,
            Thumbnail = thumbnail,
            ThumbnailPngBase64 = thumbnailPngBase64 ?? string.Empty,
            Favicon = favicon,
            FaviconPngBase64 = faviconPngBase64 ?? string.Empty,
            Actions = actions ?? []
        };
    }

    private static string GetWorkspaceItemTitle(WorkspaceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title.Trim();
        }

        return item switch
        {
            ShellItem shellItem when !string.IsNullOrWhiteSpace(shellItem.TargetPath) => Path.GetFileName(shellItem.TargetPath),
            FolderItem => "Workspaceフォルダー",
            NoteItem => "メモ",
            BookmarkItem bookmark when !string.IsNullOrWhiteSpace(bookmark.Url) => bookmark.Url,
            _ => item.Type.ToString()
        };
    }

    private static bool TryGetFilePath(string? address, out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            if (Path.IsPathFullyQualified(address))
            {
                filePath = Path.GetFullPath(address);
                return true;
            }

            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                filePath = Path.GetFullPath(uri.LocalPath);
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return false;
    }
}
