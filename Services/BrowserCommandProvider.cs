using TabBrowser.Models;

namespace TabBrowser.Services;

public static class BrowserCommandProvider
{
    public const string OpenDownloadsActionId = "browser.openDownloads";
    public const string OpenBrowsingHistoryActionId = "browser.openBrowsingHistory";
    public const string OpenUserGuideActionId = "browser.openUserGuide";
    public const string OpenChatGptReadabilityActionId = "browser.openChatGptReadability";
    public const string OpenSettingsActionId = "browser.openSettings";
    public const string FocusLocationActionId = "browser.focusLocation";
    public const string NewTabActionId = "browser.newTab";
    public const string DuplicateTabActionId = "browser.duplicateTab";
    public const string ReloadActionId = "browser.reload";
    public const string BackActionId = "browser.back";
    public const string ForwardActionId = "browser.forward";
    public const string PreviousTabActionId = "browser.previousTab";
    public const string NextTabActionId = "browser.nextTab";
    public const string RestoreClosedTabActionId = "browser.restoreClosedTab";
    public const string FindInPageActionId = "browser.findInPage";
    public const string ZoomInActionId = "browser.zoomIn";
    public const string ZoomOutActionId = "browser.zoomOut";
    public const string ResetZoomActionId = "browser.resetZoom";
    public const string DuplicateTabBackgroundActionId = "browser.duplicateTabBackground";
    public const string CloseTabActionId = "browser.closeTab";
    public const string CloseOtherTabsActionId = "browser.closeOtherTabs";
    public const string CloseTabsToRightActionId = "browser.closeTabsToRight";
    public const string CloseTabsToLeftActionId = "browser.closeTabsToLeft";
    public const string ToggleTabMuteActionId = "browser.toggleTabMute";
    public const string ToggleTabPinnedActionId = "browser.toggleTabPinned";
    public const string StopLoadingActionId = "browser.stopLoading";
    public const string ScrollTopActionId = "browser.scrollTop";
    public const string ScrollBottomActionId = "browser.scrollBottom";
    public const string ScrollPageUpActionId = "browser.scrollPageUp";
    public const string ScrollPageDownActionId = "browser.scrollPageDown";
    public const string AddBookmarkActionId = "browser.addBookmark";
    public const string ShowBookmarksActionId = "browser.showBookmarks";

    public static IEnumerable<CommandAction> CreateActions(
        Func<Task> openDownloadsAsync,
        Func<Task> openBrowsingHistoryAsync,
        Func<Task> openUserGuideAsync,
        Func<Task> openChatGptReadabilityAsync,
        Func<Task> openSettingsAsync,
        Func<Task> newTabAsync,
        Func<Task> duplicateTabAsync,
        Func<Task> reloadAsync,
        Func<Task> backAsync,
        Func<Task> forwardAsync,
        Func<Task> previousTabAsync,
        Func<Task> nextTabAsync,
        Func<Task> restoreClosedTabAsync,
        Func<Task> findInPageAsync,
        Func<Task> zoomInAsync,
        Func<Task> zoomOutAsync,
        Func<Task> resetZoomAsync,
        Func<Task>? focusLocationAsync = null,
        Func<string, CommandActionContext, Task>? executeLegacyCommandAsync = null)
    {
        focusLocationAsync ??= () => Task.CompletedTask;
        Task RunAsync(
            string command,
            CommandActionContext context,
            Func<Task> fallback)
        {
            return executeLegacyCommandAsync is null
                ? fallback()
                : executeLegacyCommandAsync(command, context);
        }

        yield return Create(
            FocusLocationActionId,
            "アドレス／フォルダーパスを選択",
            focusLocationAsync,
            ["address", "location", "path", "url", "アドレス", "パス"],
            shortcuts: ["Ctrl+L"],
            availability: RequirePageOrFolder);
        yield return Create(
            NewTabActionId,
            "新しいタブを開く",
            context => RunAsync("NewTab", context, newTabAsync),
            ["new", "tab", "open", "新規"],
            shortcuts: ["Ctrl+T"],
            gestures: ["NewTab"]);
        yield return Create(
            DuplicateTabActionId,
            "現在のタブを複製",
            context => RunAsync("DuplicateTab", context, duplicateTabAsync),
            ["duplicate", "clone", "tab", "複製"],
            gestures: ["DuplicateTab"],
            availability: RequirePageOrFolder);
        yield return Create(
            DuplicateTabBackgroundActionId,
            "現在のタブを裏で複製",
            context => RunAsync(
                "DuplicateTabBackground",
                context,
                () => Task.CompletedTask),
            ["duplicate", "background", "clone", "tab", "裏タブ"],
            gestures: ["DuplicateTabBackground"],
            availability: RequirePageOrFolder);
        yield return Create(
            CloseTabActionId,
            "現在のタブを閉じる",
            context => RunAsync("CloseTab", context, () => Task.CompletedTask),
            ["close", "tab", "閉じる"],
            shortcuts: ["Ctrl+W"],
            gestures: ["CloseTab"],
            availability: RequireClosableTab);
        yield return Create(
            CloseOtherTabsActionId,
            "他のタブを閉じる",
            context => RunAsync("CloseOtherTabs", context, () => Task.CompletedTask),
            ["close", "other", "tabs", "他のタブ"],
            gestures: ["CloseOtherTabs"],
            availability: RequirePageOrFolder);
        yield return Create(
            CloseTabsToRightActionId,
            "右側のタブを閉じる",
            context => RunAsync("CloseTabsToRight", context, () => Task.CompletedTask),
            ["close", "right", "tabs", "右側"],
            gestures: ["CloseTabsToRight"],
            availability: RequirePageOrFolder);
        yield return Create(
            CloseTabsToLeftActionId,
            "左側のタブを閉じる",
            context => RunAsync("CloseTabsToLeft", context, () => Task.CompletedTask),
            ["close", "left", "tabs", "左側"],
            gestures: ["CloseTabsToLeft"],
            availability: RequirePageOrFolder);
        yield return Create(
            ToggleTabMuteActionId,
            "現在のタブのミュートを切り替える",
            context => RunAsync("ToggleTabMute", context, () => Task.CompletedTask),
            ["mute", "audio", "tab", "ミュート"],
            availability: RequireWebPage);
        yield return Create(
            ToggleTabPinnedActionId,
            "現在のタブの固定を切り替える",
            context => RunAsync("ToggleTabPinned", context, () => Task.CompletedTask),
            ["pin", "tab", "fixed", "固定"],
            availability: RequirePageOrFolder);
        yield return Create(
            ReloadActionId,
            "現在の表示を更新",
            context => RunAsync("Reload", context, reloadAsync),
            ["reload", "refresh", "更新", "再読み込み"],
            shortcuts: ["F5"],
            gestures: ["Reload"],
            availability: RequirePageOrFolder);
        yield return Create(
            StopLoadingActionId,
            "読み込みを停止",
            context => RunAsync("StopLoading", context, () => Task.CompletedTask),
            ["stop", "loading", "読み込み停止"],
            gestures: ["StopLoading"],
            availability: RequireWebPage);
        yield return Create(
            BackActionId,
            "戻る",
            context => RunAsync("Back", context, backAsync),
            ["back", "history", "前へ"],
            shortcuts: ["Alt+Left"],
            gestures: ["Back"],
            availability: RequireBackNavigation);
        yield return Create(
            ForwardActionId,
            "進む",
            context => RunAsync("Forward", context, forwardAsync),
            ["forward", "history", "次へ"],
            shortcuts: ["Alt+Right"],
            gestures: ["Forward"],
            availability: RequireForwardNavigation);
        yield return Create(
            PreviousTabActionId,
            "前のタブへ移動",
            context => RunAsync("PreviousTab", context, previousTabAsync),
            ["previous", "prev", "tab", "前タブ"],
            gestures: ["PreviousTab"]);
        yield return Create(
            NextTabActionId,
            "次のタブへ移動",
            context => RunAsync("NextTab", context, nextTabAsync),
            ["next", "tab", "次タブ"],
            gestures: ["NextTab"]);
        yield return Create(
            RestoreClosedTabActionId,
            "最近閉じた項目を復元",
            context => RunAsync("RestoreClosedTab", context, restoreClosedTabAsync),
            ["closed", "restore", "reopen", "tab", "window", "復元", "ウィンドウ"],
            shortcuts: ["Ctrl+Shift+T"],
            gestures: ["RestoreClosedTab"]);
        yield return Create(
            FindInPageActionId,
            "ページ内を検索",
            findInPageAsync,
            ["find", "search", "page", "検索"],
            shortcuts: ["Ctrl+F"],
            availability: RequireWebPage);
        yield return Create(
            ZoomInActionId,
            "ページを拡大",
            zoomInAsync,
            ["zoom", "in", "拡大"],
            shortcuts: ["Ctrl++"],
            availability: RequireWebPage);
        yield return Create(
            ZoomOutActionId,
            "ページを縮小",
            zoomOutAsync,
            ["zoom", "out", "縮小"],
            shortcuts: ["Ctrl+-"],
            availability: RequireWebPage);
        yield return Create(
            ResetZoomActionId,
            "ページ倍率を100%に戻す",
            resetZoomAsync,
            ["zoom", "reset", "100", "等倍"],
            shortcuts: ["Ctrl+0"],
            availability: RequireWebPage);
        yield return Create(
            ScrollTopActionId,
            "ページ先頭へ移動",
            context => RunAsync("ScrollTop", context, () => Task.CompletedTask),
            ["scroll", "top", "先頭"],
            gestures: ["ScrollTop"],
            availability: RequireWebPage);
        yield return Create(
            ScrollBottomActionId,
            "ページ末尾へ移動",
            context => RunAsync("ScrollBottom", context, () => Task.CompletedTask),
            ["scroll", "bottom", "末尾"],
            gestures: ["ScrollBottom"],
            availability: RequireWebPage);
        yield return Create(
            ScrollPageUpActionId,
            "1ページ上へ移動",
            context => RunAsync("ScrollPageUp", context, () => Task.CompletedTask),
            ["scroll", "page", "up", "ページ上"],
            gestures: ["ScrollPageUp"],
            availability: RequireWebPage);
        yield return Create(
            ScrollPageDownActionId,
            "1ページ下へ移動",
            context => RunAsync("ScrollPageDown", context, () => Task.CompletedTask),
            ["scroll", "page", "down", "ページ下"],
            gestures: ["ScrollPageDown"],
            availability: RequireWebPage);
        yield return Create(
            AddBookmarkActionId,
            "現在ページをブックマーク",
            context => RunAsync("AddBookmark", context, () => Task.CompletedTask),
            ["bookmark", "add", "ブックマーク追加"],
            gestures: ["AddBookmark"],
            availability: RequireWebPage);
        yield return Create(
            ShowBookmarksActionId,
            "ブックマーク一覧を開く",
            context => RunAsync("ShowBookmarks", context, () => Task.CompletedTask),
            ["bookmark", "list", "ブックマーク一覧"],
            gestures: ["ShowBookmarks"],
            category: "管理");
        yield return Create(
            OpenDownloadsActionId,
            "ダウンロード一覧を開く",
            openDownloadsAsync,
            ["download", "downloads", "dl", "ダウンロード"],
            category: "管理");
        yield return Create(
            OpenBrowsingHistoryActionId,
            "閲覧履歴を開く",
            openBrowsingHistoryAsync,
            ["history", "visited", "閲覧履歴"],
            category: "管理");
        yield return Create(
            OpenUserGuideActionId,
            "ユーザーガイドを開く",
            openUserGuideAsync,
            ["help", "manual", "guide", "説明書", "ヘルプ", "操作ガイド"],
            category: "ヘルプ");
        yield return Create(
            OpenChatGptReadabilityActionId,
            "ChatGPT読みやすさ設定を開く",
            openChatGptReadabilityAsync,
            ["chatgpt", "gpt", "readability", "読みやすさ"],
            category: "設定");
        yield return Create(
            OpenSettingsActionId,
            "設定画面を開く",
            openSettingsAsync,
            ["settings", "config", "設定"],
            category: "設定");
    }

    private static CommandAction Create(
        string id,
        string title,
        Func<Task> runAsync,
        IEnumerable<string> keywords,
        string category = "ブラウザー",
        IEnumerable<string>? shortcuts = null,
        IEnumerable<string>? gestures = null,
        Func<CommandInvocationContext, CommandAvailability>? availability = null)
    {
        return new CommandAction(
            id,
            title,
            _ => runAsync(),
            keywords,
            category: category,
            shortcuts: shortcuts,
            gestureCommandIds: gestures,
            getAvailability: availability);
    }

    private static CommandAction Create(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        IEnumerable<string> keywords,
        string category = "ブラウザー",
        IEnumerable<string>? shortcuts = null,
        IEnumerable<string>? gestures = null,
        Func<CommandInvocationContext, CommandAvailability>? availability = null)
    {
        return new CommandAction(
            id,
            title,
            runAsync,
            keywords,
            category: category,
            shortcuts: shortcuts,
            gestureCommandIds: gestures,
            getAvailability: availability);
    }

    private static CommandAvailability RequirePageOrFolder(CommandInvocationContext context)
    {
        return context.Surface is CommandSurface.Web or CommandSurface.FolderTab
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Webページまたはフォルダータブで使用できます");
    }

    private static CommandAvailability RequireBackNavigation(CommandInvocationContext context)
    {
        var surfaceAvailability = RequirePageOrFolder(context);
        return surfaceAvailability.State != CommandAvailabilityState.Available
            ? surfaceAvailability
            : context.CanNavigateBack
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("戻る履歴がありません");
    }

    private static CommandAvailability RequireForwardNavigation(CommandInvocationContext context)
    {
        var surfaceAvailability = RequirePageOrFolder(context);
        return surfaceAvailability.State != CommandAvailabilityState.Available
            ? surfaceAvailability
            : context.CanNavigateForward
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("進む履歴がありません");
    }

    private static CommandAvailability RequireWebPage(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Web
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Webページで使用できます");
    }

    private static CommandAvailability RequireClosableTab(CommandInvocationContext context)
    {
        return context.Surface is CommandSurface.Web or CommandSurface.FolderTab
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("閉じられるタブで使用できます");
    }
}
