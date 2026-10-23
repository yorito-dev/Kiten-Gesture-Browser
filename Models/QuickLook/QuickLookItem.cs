using System.Windows.Media;

namespace TabBrowser.Models.QuickLook;

public enum QuickLookItemKind
{
    File,
    Directory,
    Text,
    Uri,
    Information
}

public sealed record QuickLookAction(
    string Id,
    string Label,
    Func<Task> ExecuteAsync,
    bool IsPrimary = false);

public sealed record QuickLookItem
{
    public required string DisplayName { get; init; }

    public string? FilePath { get; init; }

    public string? Uri { get; init; }

    public QuickLookItemKind Kind { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public required string SourceId { get; init; }

    public string? InlineText { get; init; }

    public string? Description { get; init; }

    public ImageSource? Thumbnail { get; init; }

    public string ThumbnailPngBase64 { get; init; } = string.Empty;

    public ImageSource? Favicon { get; init; }

    public string FaviconPngBase64 { get; init; } = string.Empty;

    public IReadOnlyList<QuickLookAction> Actions { get; init; } = [];
}

public sealed record QuickLookRequest(
    IReadOnlyList<QuickLookItem> Items,
    string SourceId,
    IQuickLookItemSource? ItemSource = null,
    bool IsPrivate = false)
{
    public QuickLookItem? PrimaryItem => Items.FirstOrDefault();
}

public interface IQuickLookItemSource
{
    string SourceId { get; }

    QuickLookItem? Current { get; }

    IReadOnlyList<QuickLookItem> SelectedItems { get; }

    bool CanMoveNext { get; }

    bool CanMovePrevious { get; }

    QuickLookItem? MoveNext();

    QuickLookItem? MovePrevious();
}
