using TabBrowser.Models;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services;

public sealed class ContextCommandProvider
{
    public const string OpenWorkspaceItemActionId = "workspace.openItem";
    public const string FocusMiniPlayerActionId = "miniPlayer.focus";
    public const string CopySelectionActionId = "context.copySelection";
    public const string SearchSelectionActionId = "context.searchSelection";
    public const string TranslateSelectionActionId = "context.translateSelection";
    public const string OpenLinkInNewTabActionId = "context.openLinkInNewTab";
    public const string OpenLinkInSplitActionId = "context.openLinkInSplit";
    public const string CopyLinkUrlActionId = "context.copyLinkUrl";
    public const string FolderSortNameActionId = "folder.sortName";
    public const string FolderSortTypeActionId = "folder.sortType";
    public const string FolderSortModifiedActionId = "folder.sortModified";
    public const string FolderSortSizeActionId = "folder.sortSize";
    public const string FolderApplySortActionId = "folder.applySort";
    public const string FolderToggleSortDirectionActionId = "folder.toggleSortDirection";
    public const string FolderQuickLookActionId = "folder.quickLook";
    public const string WorkspaceArrangeCompactActionId = "workspace.arrangeCompact";
    public const string WorkspaceApplyArrangementActionId = "workspace.applyArrangement";
    public const string WorkspaceRestoreArrangementActionId = "workspace.restoreArrangement";
    public const string WorkspaceQuickLookActionId = "workspace.quickLook";

    public IEnumerable<CommandAction> CreateActions(ContextCommandCallbacks callbacks)
    {
        yield return CreateWithContext(
            CopySelectionActionId,
            "選択テキストをコピー",
            callbacks.CopySelectionAsync,
            "選択テキスト",
            RequireSelection,
            null,
            "copy",
            "selection",
            "選択",
            "コピー");
        yield return CreateWithContext(
            SearchSelectionActionId,
            "選択テキストを検索",
            callbacks.SearchSelectionAsync,
            "選択テキスト",
            RequireSelection,
            ["SearchSelection"],
            "search",
            "selection",
            "選択",
            "検索");
        yield return CreateWithContext(
            TranslateSelectionActionId,
            "選択テキストを翻訳",
            callbacks.TranslateSelectionAsync,
            "選択テキスト",
            RequireSelection,
            null,
            "translate",
            "selection",
            "選択",
            "翻訳");
        yield return CreateWithContext(
            OpenLinkInNewTabActionId,
            "対象リンクを新しいタブで開く",
            callbacks.OpenLinkInNewTabAsync,
            "リンク",
            RequireLink,
            null,
            "link",
            "new",
            "tab",
            "リンク");
        yield return CreateWithContext(
            OpenLinkInSplitActionId,
            "対象リンクを分割表示で開く",
            callbacks.OpenLinkInSplitAsync,
            "リンク",
            RequireSplittableLink,
            null,
            "link",
            "split",
            "分割",
            "リンク");
        yield return CreateWithContext(
            CopyLinkUrlActionId,
            "対象リンクのURLをコピー",
            callbacks.CopyLinkUrlAsync,
            "リンク",
            RequireLink,
            null,
            "link",
            "url",
            "copy",
            "リンク");
        yield return CreateFolderSort(
            FolderSortNameActionId,
            "フォルダーを名前順に並び替え",
            context => callbacks.SortFolderAsync(FolderTabSortMode.Name, context),
            "name",
            "名前順");
        yield return CreateFolderSort(
            FolderSortTypeActionId,
            "フォルダーを種類順に並び替え",
            context => callbacks.SortFolderAsync(FolderTabSortMode.Type, context),
            "type",
            "種類順");
        yield return CreateFolderSort(
            FolderSortModifiedActionId,
            "フォルダーを更新日時順に並び替え",
            context => callbacks.SortFolderAsync(FolderTabSortMode.LastWriteTime, context),
            "modified",
            "date",
            "更新日時");
        yield return CreateFolderSort(
            FolderSortSizeActionId,
            "フォルダーをサイズ順に並び替え",
            context => callbacks.SortFolderAsync(FolderTabSortMode.Size, context),
            "size",
            "サイズ");
        yield return new CommandAction(
            FolderApplySortActionId,
            "フォルダーの並び順を適用",
            callbacks.ApplyFolderSortAsync,
            ["folder", "sort"],
            showInPalette: false,
            category: "フォルダータブ",
            getAvailability: RequireFolder);
        yield return CreateWithContext(
            FolderToggleSortDirectionActionId,
            "フォルダーの昇順／降順を切り替える",
            callbacks.ToggleFolderSortDirectionAsync,
            "フォルダータブ",
            RequireFolder,
            null,
            "folder",
            "sort",
            "ascending",
            "descending",
            "昇順",
            "降順");
        yield return CreateWithContext(
            FolderQuickLookActionId,
            "フォルダーの選択項目をQuickLook",
            callbacks.ShowFolderQuickLookAsync,
            "フォルダータブ",
            RequireFolderSelection,
            null,
            "folder",
            "quicklook",
            "preview",
            "プレビュー");
        yield return CreateWithContext(
            WorkspaceArrangeCompactActionId,
            "Workspaceの選択カードをコンパクト整列",
            callbacks.ArrangeWorkspaceSelectionAsync,
            "Workspace",
            RequireWorkspaceSelectionForArrange,
            null,
            "workspace",
            "arrange",
            "compact",
            "整列");
        yield return new CommandAction(
            WorkspaceApplyArrangementActionId,
            "Workspaceのカード整列を適用",
            callbacks.ApplyWorkspaceArrangementAsync,
            ["workspace", "arrange"],
            showInPalette: false,
            category: "Workspace",
            getAvailability: static context => context.Surface == CommandSurface.Workspace
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("Workspaceで使用できます"));
        yield return CreateWithContext(
            WorkspaceRestoreArrangementActionId,
            "Workspaceの整列前の位置へ戻す",
            callbacks.RestoreWorkspaceArrangementAsync,
            "Workspace",
            static context => context.Surface == CommandSurface.Workspace
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("Workspaceで使用できます"),
            null,
            "workspace",
            "arrange",
            "restore",
            "元に戻す");
        yield return CreateWithContext(
            WorkspaceQuickLookActionId,
            "Workspaceの選択カードをQuickLook",
            callbacks.ShowWorkspaceQuickLookAsync,
            "Workspace",
            RequireWorkspaceSelection,
            null,
            "workspace",
            "quicklook",
            "preview",
            "プレビュー");
    }

    public IReadOnlyList<CommandPaletteItem> CreateWorkspaceItems(
        IEnumerable<WorkspaceCommandSource> sources)
    {
        return sources
            .Select(source => new CommandPaletteItem
            {
                Id = "workspace:" + source.Item.Id.ToString("D"),
                Type = CommandPaletteItemType.Workspace,
                Title = GetTitle(source.Item),
                Subtitle = GetWorkspaceSubtitle(source),
                Url = source.Item is ShellItem shellItem ? shellItem.TargetPath : source.Item.Url,
                ActionId = OpenWorkspaceItemActionId,
                Payload = source.Item,
                Keywords = GetWorkspaceKeywords(source),
                SearchTitleOnly = source.Item.Type == WorkspaceItemType.Note
            })
            .ToList();
    }

    public IReadOnlyList<CommandPaletteItem> CreateMiniPlayerItems(
        IEnumerable<MiniPlayerCommandSource> sources)
    {
        return sources.Select(source => new CommandPaletteItem
        {
            Id = "miniPlayer:" + source.Id.ToString("D"),
            Type = CommandPaletteItemType.MiniPlayer,
            Title = string.IsNullOrWhiteSpace(source.Title) ? "YouTubeミニプレイヤー" : source.Title,
            Subtitle = string.Join(" / ", new[]
            {
                source.IsPaused ? "一時停止" : "再生中",
                source.IsMuted ? "ミュート" : "音声あり",
                source.IsMinimized ? "最小化中" : "表示中",
                source.WindowLabel
            }.Where(static value => !string.IsNullOrWhiteSpace(value))),
            Url = source.Url,
            ActionId = FocusMiniPlayerActionId,
            Payload = source.Id,
            Keywords = ["youtube", "mini", "player", "ミニプレイヤー", source.WindowLabel]
        }).ToList();
    }

    private static string GetTitle(WorkspaceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title.Trim();
        }

        return item.Type switch
        {
            WorkspaceItemType.Folder => "Workspaceフォルダー",
            WorkspaceItemType.SessionGroup => "Workspaceセッション",
            WorkspaceItemType.YouTubeMiniPlayerSet => "ミニプレイヤーセット",
            WorkspaceItemType.ShellItem => "ファイルまたはフォルダー",
            _ => string.IsNullOrWhiteSpace(item.Url) ? "Workspace項目" : item.Url
        };
    }

    private static string GetWorkspaceSubtitle(WorkspaceCommandSource source)
    {
        var typeText = source.Item.Type switch
        {
            WorkspaceItemType.Bookmark => "ブックマーク",
            WorkspaceItemType.Note => "メモ",
            WorkspaceItemType.Folder => "フォルダー",
            WorkspaceItemType.SessionGroup => "セッション",
            WorkspaceItemType.YouTubeMiniPlayerSet => "ミニプレイヤーセット",
            WorkspaceItemType.ShellItem => source.Item is ShellItem shellItem
                ? shellItem.Kind switch
                {
                    ShellItemKind.Directory => "実フォルダー",
                    ShellItemKind.Shortcut => "ショートカット",
                    ShellItemKind.Application => "アプリ",
                    _ => "ファイル"
                }
                : "ファイル",
            _ => "項目"
        };
        var location = source.Item is ShellItem typedShellItem
            ? typedShellItem.TargetPath
            : source.Item.Url;
        return string.IsNullOrWhiteSpace(location)
            ? source.WorkspaceName + " / " + typeText
            : source.WorkspaceName + " / " + typeText + " / " + location;
    }

    private static IReadOnlyList<string> GetWorkspaceKeywords(WorkspaceCommandSource source)
    {
        if (source.Item.Type == WorkspaceItemType.Note)
        {
            return [];
        }

        return source.Item.Tags
            .Concat([
                source.WorkspaceName,
                source.Item.Note,
                source.Item.Type.ToString(),
                source.Item is ShellItem typedShellItem ? typedShellItem.TargetPath : string.Empty,
                "workspace"
            ])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static CommandAction Create(
        string id,
        string title,
        Func<Task> runAsync,
        string category,
        Func<CommandInvocationContext, CommandAvailability> availability,
        IEnumerable<string>? gestureCommandIds,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            _ => runAsync(),
            keywords,
            category: category,
            gestureCommandIds: gestureCommandIds,
            getAvailability: availability);
    }

    private static CommandAction CreateWithContext(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        string category,
        Func<CommandInvocationContext, CommandAvailability> availability,
        IEnumerable<string>? gestureCommandIds,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            runAsync,
            keywords,
            category: category,
            gestureCommandIds: gestureCommandIds,
            getAvailability: availability);
    }

    private static CommandAction CreateFolderSort(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        params string[] keywords)
    {
        return CreateWithContext(
            id,
            title,
            runAsync,
            "フォルダータブ",
            RequireFolder,
            null,
            keywords);
    }

    private static CommandAvailability RequireSelection(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Web && context.HasSelection
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Webページでテキストを選択すると使用できます");
    }

    private static CommandAvailability RequireLink(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Web && context.HasLink
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("リンクの右クリックからコマンドパレットを開くと使用できます");
    }

    private static CommandAvailability RequireSplittableLink(CommandInvocationContext context)
    {
        if (context.IsSplitView)
        {
            return CommandAvailability.Disabled("すでに分割表示中です");
        }

        return RequireLink(context);
    }

    private static CommandAvailability RequireFolder(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.FolderTab
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("フォルダータブで使用できます");
    }

    private static CommandAvailability RequireFolderSelection(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.FolderTab && context.SelectionCount > 0
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("フォルダータブで項目を選択すると使用できます");
    }

    private static CommandAvailability RequireWorkspaceSelection(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Workspace && context.SelectionCount > 0
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Workspaceでカードを選択すると使用できます");
    }

    private static CommandAvailability RequireWorkspaceSelectionForArrange(
        CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Workspace && context.SelectionCount >= 2
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Workspaceで2件以上のカードを選択すると使用できます");
    }
}

public sealed record ContextCommandCallbacks(
    Func<CommandActionContext, Task> CopySelectionAsync,
    Func<CommandActionContext, Task> SearchSelectionAsync,
    Func<CommandActionContext, Task> TranslateSelectionAsync,
    Func<CommandActionContext, Task> OpenLinkInNewTabAsync,
    Func<CommandActionContext, Task> OpenLinkInSplitAsync,
    Func<CommandActionContext, Task> CopyLinkUrlAsync,
    Func<string, CommandActionContext, Task> SortFolderAsync,
    Func<CommandActionContext, Task> ApplyFolderSortAsync,
    Func<CommandActionContext, Task> ToggleFolderSortDirectionAsync,
    Func<CommandActionContext, Task> ShowFolderQuickLookAsync,
    Func<CommandActionContext, Task> ArrangeWorkspaceSelectionAsync,
    Func<CommandActionContext, Task> ApplyWorkspaceArrangementAsync,
    Func<CommandActionContext, Task> RestoreWorkspaceArrangementAsync,
    Func<CommandActionContext, Task> ShowWorkspaceQuickLookAsync);

public sealed record WorkspaceCommandSource(WorkspaceItem Item, string WorkspaceName);

public sealed record MiniPlayerCommandSource(
    Guid Id,
    string Title,
    string Url,
    bool IsPaused,
    bool IsMuted,
    bool IsMinimized,
    string WindowLabel);
