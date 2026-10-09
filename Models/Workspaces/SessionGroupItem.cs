namespace TabBrowser.Models.Workspaces;

public sealed class SessionGroupItem : WorkspaceItem
{
    public override WorkspaceItemType Type => WorkspaceItemType.SessionGroup;

    public List<SessionTab> Tabs { get; set; } = [];
}
