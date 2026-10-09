namespace TabBrowser.Models.Workspaces;

public abstract class WorkspaceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid? ParentFolderId { get; set; }

    public abstract WorkspaceItemType Type { get; }

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    // nullは通常のURLブックマーク、またはコンテナ情報を持たない旧保存形式。元の文脈を推測しない。
    public string? ContainerProfileId { get; set; }

    public string Note { get; set; } = string.Empty;

    public string FaviconPngBase64 { get; set; } = string.Empty;

    public string ThumbnailPngBase64 { get; set; } = string.Empty;

    public DateTimeOffset? ThumbnailUpdatedAt { get; set; }

    public List<string> Tags { get; set; } = [];

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 220;

    public double Height { get; set; } = 90;

    public int ZIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletionRootId { get; set; }
}

public sealed record WorkspaceItemPosition(Guid ItemId, double X, double Y);
