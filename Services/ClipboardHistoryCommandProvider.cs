using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ClipboardHistoryCommandProvider
{
    public const string OpenHistoryActionId = "clipboardHistory.open";
    public const string CopyHistoryItemActionId = "clipboardHistory.copyItem";
    public const string ClearHistoryActionId = "clipboardHistory.clear";
    public const string TogglePauseActionId = "clipboardHistory.togglePause";

    public IEnumerable<CommandAction> CreateActions(
        Func<Task> openHistoryAsync,
        Func<CommandActionContext, Task> copyHistoryItemAsync,
        Func<Task> clearHistoryAsync,
        Func<Task> togglePauseAsync)
    {
        yield return new CommandAction(
            OpenHistoryActionId,
            "クリップボード履歴を開く",
            _ => openHistoryAsync(),
            ["clipboard", "clip", "history", "クリップボード", "履歴"]);
        yield return new CommandAction(
            CopyHistoryItemActionId,
            "クリップボード履歴を再コピー",
            copyHistoryItemAsync,
            ["clipboard", "clip", "copy", "クリップボード", "コピー"],
            showInPalette: false);
        yield return new CommandAction(
            ClearHistoryActionId,
            "クリップボード履歴を消去",
            _ => clearHistoryAsync(),
            ["clipboard", "clip", "clear", "delete", "クリップボード", "消去"]);
        yield return new CommandAction(
            TogglePauseActionId,
            "クリップボード履歴の監視を一時停止／再開",
            _ => togglePauseAsync(),
            ["clipboard", "clip", "pause", "resume", "クリップボード", "一時停止", "再開"]);
    }

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<ClipboardHistoryItem> items)
    {
        return items.Select(item => new CommandPaletteItem
        {
            Id = "clipboard:" + item.Id.ToString("D"),
            Type = CommandPaletteItemType.Clipboard,
            Title = string.IsNullOrWhiteSpace(item.PreviewText) ? item.TypeText : item.PreviewText,
            Subtitle = $"{item.TypeText} / {item.CapturedAt:HH:mm:ss}",
            ActionId = CopyHistoryItemActionId,
            Payload = item,
            Keywords = ["clipboard", "clip", "クリップボード", item.TypeText]
        }).ToList();
    }
}
