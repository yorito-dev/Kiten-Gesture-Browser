using System.Windows.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TabBrowser.Models;

public enum CommandPaletteItemType
{
    Tab,
    Command,
    RecentlyClosed,
    Bookmark,
    History,
    Workspace,
    MiniPlayer,
    Clipboard,
    Application
}

public enum CommandPaletteExecutionMode
{
    Default,
    NewTab,
    Split
}

public enum CommandSurface
{
    Global,
    Web,
    FolderTab,
    Workspace,
    MiniPlayer
}

public enum CommandAvailabilityState
{
    Available,
    Disabled,
    Hidden
}

public enum CommandExecutionStatus
{
    Succeeded,
    NotFound,
    Unavailable,
    Cancelled,
    Failed
}

public sealed record CommandInvocationContext(
    CommandSurface Surface,
    bool IsSplitView = false,
    bool HasSelection = false,
    bool HasLink = false,
    bool HasMiniPlayers = false,
    int SelectionCount = 0,
    Guid? TargetId = null,
    string SelectedText = "",
    string LinkUrl = "",
    string LinkText = "",
    bool CanNavigateBack = false,
    bool CanNavigateForward = false)
{
    public static CommandInvocationContext Global { get; } = new(CommandSurface.Global);
}

public sealed record CommandAvailability(
    CommandAvailabilityState State,
    string Reason = "")
{
    public static CommandAvailability Available { get; } =
        new(CommandAvailabilityState.Available);

    public static CommandAvailability Disabled(string reason) =>
        new(CommandAvailabilityState.Disabled, reason);

    public static CommandAvailability Hidden(string reason = "") =>
        new(CommandAvailabilityState.Hidden, reason);
}

public sealed class CommandPaletteItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private ImageSource? _thumbnail;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; init; } = string.Empty;

    public CommandPaletteItemType Type { get; init; }

    public string TypeText => Type switch
    {
        CommandPaletteItemType.Tab => "タブ",
        CommandPaletteItemType.Command => "コマンド",
        CommandPaletteItemType.RecentlyClosed => "閉じたタブ",
        CommandPaletteItemType.Bookmark => "ブックマーク",
        CommandPaletteItemType.History => "履歴",
        CommandPaletteItemType.Workspace => "Workspace",
        CommandPaletteItemType.MiniPlayer => "ミニプレイヤー",
        CommandPaletteItemType.Clipboard => "クリップボード",
        CommandPaletteItemType.Application => "アプリ",
        _ => Type.ToString()
    };

    public string Title { get; init; } = string.Empty;

    public string Subtitle { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value)
            {
                return;
            }

            _icon = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail == value)
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    public bool IsTab => Type == CommandPaletteItemType.Tab;

    public bool IsRecentlyClosed => Type == CommandPaletteItemType.RecentlyClosed;

    public bool IsHistory => Type == CommandPaletteItemType.History;

    public bool SupportsThumbnail => IsTab || IsRecentlyClosed || IsHistory;

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public bool SearchTitleOnly { get; init; }

    public string ActionId { get; init; } = string.Empty;

    public object? Payload { get; init; }

    public string Category { get; init; } = string.Empty;

    public string ShortcutText { get; init; } = string.Empty;

    public string GestureText { get; init; } = string.Empty;

    public string AssignmentText => string.Join(
        "  ",
        new[] { ShortcutText, GestureText }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public bool HasAssignment => !string.IsNullOrWhiteSpace(AssignmentText);

    public bool IsAvailable { get; init; } = true;

    public string UnavailableReason { get; init; } = string.Empty;

    public bool CanConfigureGesture { get; init; }

    public string GestureCommandId { get; init; } = string.Empty;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record CommandActionContext(
    Guid? ActiveTabId,
    CommandPaletteItem? SelectedItem,
    CommandPaletteExecutionMode ExecutionMode = CommandPaletteExecutionMode.Default,
    CommandInvocationContext? InvocationContext = null,
    object? Parameter = null);

public sealed record CommandExecutionResult(
    CommandExecutionStatus Status,
    string Message = "",
    Exception? Exception = null)
{
    public bool IsSuccess => Status == CommandExecutionStatus.Succeeded;

    public static CommandExecutionResult Succeeded { get; } =
        new(CommandExecutionStatus.Succeeded);

    public static CommandExecutionResult NotFound(string actionId) =>
        new(CommandExecutionStatus.NotFound, $"コマンドが登録されていません: {actionId}");

    public static CommandExecutionResult Unavailable(string reason) =>
        new(
            CommandExecutionStatus.Unavailable,
            string.IsNullOrWhiteSpace(reason)
                ? "現在の画面では実行できません"
                : reason);

    public static CommandExecutionResult Cancelled { get; } =
        new(CommandExecutionStatus.Cancelled);

    public static CommandExecutionResult Failed(Exception exception) =>
        new(CommandExecutionStatus.Failed, exception.Message, exception);
}

public sealed class CommandAction
{
    public CommandAction(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        IEnumerable<string>? keywords = null,
        bool showInPalette = true,
        string category = "一般",
        IEnumerable<string>? shortcuts = null,
        IEnumerable<string>? gestureCommandIds = null,
        Func<CommandInvocationContext, CommandAvailability>? getAvailability = null,
        string description = "")
    {
        Id = id;
        Title = title;
        RunAsync = runAsync;
        Keywords = (keywords ?? []).ToList();
        ShowInPalette = showInPalette;
        Category = category;
        Shortcuts = (shortcuts ?? []).Where(static value => !string.IsNullOrWhiteSpace(value)).ToList();
        GestureCommandIds = (gestureCommandIds ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        GetAvailability = getAvailability ?? (_ => CommandAvailability.Available);
        Description = description;
    }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> Keywords { get; }

    public bool ShowInPalette { get; }

    public Func<CommandActionContext, Task> RunAsync { get; }

    public string Category { get; }

    public IReadOnlyList<string> Shortcuts { get; }

    public IReadOnlyList<string> GestureCommandIds { get; }

    public Func<CommandInvocationContext, CommandAvailability> GetAvailability { get; }

    public string Description { get; }
}
