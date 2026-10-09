using TabBrowser.Models.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class WorkspaceSearchResultViewModel
{
    public WorkspaceSearchResultViewModel(WorkspaceItem item, string workspaceLabel)
    {
        Item = item;
        WorkspaceLabel = workspaceLabel;
    }

    public WorkspaceItem Item { get; }

    public Guid WorkspaceId => Item.WorkspaceId;

    public Guid ItemId => Item.Id;

    public WorkspaceItemType Type => Item.Type;

    public string WorkspaceLabel { get; }

    public string TypeLabel => Type switch
    {
        WorkspaceItemType.Bookmark => "Bookmark",
        WorkspaceItemType.Folder => "Folder",
        WorkspaceItemType.SessionGroup => "Session",
        WorkspaceItemType.Note => "Note",
        WorkspaceItemType.ShellItem => Item is ShellItem shellItem ? shellItem.Kind.ToString() : "File",
        _ => "Item"
    };

    public string Title => string.IsNullOrWhiteSpace(Item.Title)
        ? TypeLabel
        : Item.Title;

    public string Subtitle
    {
        get
        {
            if (Item is SessionGroupItem sessionGroup)
            {
                return $"{sessionGroup.Tabs.Count} tabs";
            }

            if (!string.IsNullOrWhiteSpace(Item.Url))
            {
                return Item.Url;
            }

            if (Item is ShellItem shellItem)
            {
                return shellItem.TargetPath;
            }

            return Item.Note;
        }
    }

    public string Detail
    {
        get
        {
            if (Item.Tags.Count > 0)
            {
                return string.Join(", ", Item.Tags);
            }

            if (Item is SessionGroupItem sessionGroup)
            {
                return string.Join(", ", sessionGroup.Tabs.Take(3).Select(static x => x.TitleOrUrl()));
            }

            if (Item is ShellItem shellItem)
            {
                return shellItem.TargetPath;
            }

            return Item.Note;
        }
    }
}
