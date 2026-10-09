namespace TabBrowser.Models.Workspaces;

public sealed class FolderItem : WorkspaceItem
{
    public override WorkspaceItemType Type => WorkspaceItemType.Folder;
}
