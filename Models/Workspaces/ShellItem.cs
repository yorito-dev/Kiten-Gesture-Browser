namespace TabBrowser.Models.Workspaces;

public enum ShellItemKind
{
    File,
    Directory,
    Shortcut,
    Application
}

public sealed class ShellItem : WorkspaceItem
{
    public override WorkspaceItemType Type => WorkspaceItemType.ShellItem;

    public string TargetPath { get; set; } = string.Empty;

    public ShellItemKind Kind { get; set; }

    public bool OpenInFolderTab { get; set; }
}
