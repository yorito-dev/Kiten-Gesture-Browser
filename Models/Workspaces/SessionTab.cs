namespace TabBrowser.Models.Workspaces;

public sealed class SessionTab
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string FaviconPngBase64 { get; set; } = string.Empty;

    public string ThumbnailPngBase64 { get; set; } = string.Empty;

    public Guid? SourceTabId { get; set; }

    public int SortOrder { get; set; }

    public bool IsFolderTab { get; set; }

    // null はコンテナ情報のない旧データ。明示された不明IDは既定へ置き換えない。
    public string? ContainerProfileId { get; set; }

    public string SplitGroupId { get; set; } = string.Empty;

    public string SplitPane { get; set; } = string.Empty;

    public string SplitOrientation { get; set; } = string.Empty;

    public double? SplitRatio { get; set; }

    public string SplitActivePane { get; set; } = string.Empty;

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public bool IsInSplitView => !string.IsNullOrWhiteSpace(SplitGroupId);
}
