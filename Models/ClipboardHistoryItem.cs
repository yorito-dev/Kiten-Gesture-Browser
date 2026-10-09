namespace TabBrowser.Models;

public enum ClipboardHistoryItemKind
{
    Text,
    Url,
    Files
}

public sealed record ClipboardHistoryContent(
    ClipboardHistoryItemKind Kind,
    string Text,
    IReadOnlyList<string> FilePaths);

public sealed record ClipboardHistoryItem(
    Guid Id,
    ClipboardHistoryItemKind Kind,
    string Text,
    IReadOnlyList<string> FilePaths,
    DateTimeOffset CapturedAt)
{
    public string TypeText => Kind switch
    {
        ClipboardHistoryItemKind.Url => "URL",
        ClipboardHistoryItemKind.Files => FilePaths.Count == 1
            ? "ファイル／フォルダー"
            : $"ファイル／フォルダー {FilePaths.Count}件",
        _ => "テキスト"
    };

    public string PreviewText
    {
        get
        {
            var source = Kind == ClipboardHistoryItemKind.Files
                ? string.Join(" / ", FilePaths.Take(3))
                : Text;
            var normalized = source
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return normalized.Length > 240 ? normalized[..240] + "..." : normalized;
        }
    }

    public string SearchText => Kind == ClipboardHistoryItemKind.Files
        ? string.Join(" ", FilePaths)
        : Text;

    public string? PrimaryPath => FilePaths.Count > 0 ? FilePaths[0] : null;

    public bool CanOpen => Kind is ClipboardHistoryItemKind.Url or ClipboardHistoryItemKind.Files;
}
