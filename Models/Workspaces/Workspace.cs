namespace TabBrowser.Models.Workspaces;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
