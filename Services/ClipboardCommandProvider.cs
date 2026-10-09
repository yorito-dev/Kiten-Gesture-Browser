using TabBrowser.Models;

namespace TabBrowser.Services;

public static class ClipboardCommandProvider
{
    public const string CopyUrlActionId = "clipboard.copyUrl";
    public const string CopyTitleActionId = "clipboard.copyTitle";
    public const string CopyTitleAndUrlActionId = "clipboard.copyTitleAndUrl";
    public const string CopyMarkdownActionId = "clipboard.copyMarkdown";
    public const string CopyHtmlActionId = "clipboard.copyHtml";
    public const string CopyAllTabsMarkdownActionId = "clipboard.copyAllTabsMarkdown";

    public static IEnumerable<CommandAction> CreateActions(
        Func<LinkCopyFormat, Task> copyCurrentPageAsync,
        Func<Task> copyAllTabsMarkdownAsync)
    {
        yield return new CommandAction(
            CopyUrlActionId,
            "URLをコピー",
            _ => copyCurrentPageAsync(LinkCopyFormat.Url),
            ["copy", "url", "リンク"],
            category: "コピー",
            gestureCommandIds: ["CopyUrl"],
            getAvailability: RequireWebPage);
        yield return new CommandAction(
            CopyTitleActionId,
            "タイトルをコピー",
            _ => copyCurrentPageAsync(LinkCopyFormat.Title),
            ["copy", "title", "ページ名"],
            category: "コピー",
            getAvailability: RequireWebPage);
        yield return new CommandAction(
            CopyTitleAndUrlActionId,
            "タイトル + URLをコピー",
            _ => copyCurrentPageAsync(LinkCopyFormat.TitleAndUrl),
            ["copy", "title", "url"],
            category: "コピー",
            getAvailability: RequireWebPage);
        yield return new CommandAction(
            CopyMarkdownActionId,
            "Markdownリンクでコピー",
            _ => copyCurrentPageAsync(LinkCopyFormat.Markdown),
            ["copy", "markdown", "md"],
            category: "コピー",
            getAvailability: RequireWebPage);
        yield return new CommandAction(
            CopyHtmlActionId,
            "HTMLリンクでコピー",
            _ => copyCurrentPageAsync(LinkCopyFormat.Html),
            ["copy", "html", "a"],
            category: "コピー",
            getAvailability: RequireWebPage);
        yield return new CommandAction(
            CopyAllTabsMarkdownActionId,
            "全タブをMarkdownリストでコピー",
            _ => copyAllTabsMarkdownAsync(),
            ["copy", "tabs", "markdown", "list"],
            category: "コピー");
    }

    private static CommandAvailability RequireWebPage(CommandInvocationContext context)
    {
        return context.Surface == CommandSurface.Web
            ? CommandAvailability.Available
            : CommandAvailability.Disabled("Webページで使用できます");
    }
}
