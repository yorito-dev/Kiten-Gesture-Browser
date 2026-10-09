using System.Windows.Controls;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface IPageContextMenuItemsHost
{
    IReadOnlyList<ContainerProfile> ReloadContainerProfiles();
    string GetContainerProfileMenuHeader(ContainerProfile profile);
    object CreateContainerProfileMenuIcon(ContainerProfile profile);
    bool ContainsTab(BrowserTab tab);
    BrowserTab OpenBackgroundTabNextTo(BrowserTab sourceTab, string url, string? title = null, string? containerProfileId = null);
    string GetCurrentPageTitle(BrowserTab tab, string url);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void SetStatus(string message, bool autoClear);
    void OpenLinkInNewWindow(BrowserTab sourceTab, string linkUri, string? linkText);
    void AddMiniPlayerLinkContextMenuItem(ContextMenu menu, BrowserTab sourceTab, string linkUri, string? linkText, string? miniPlayerLinkUri);
    bool CanOpenInSplitView(BrowserTab tab);
    bool CanReplaceSplitPaneWithLink(BrowserTab sourceTab, bool replaceOppositePane);
    void QueueWebContextRightSplitOpen(BrowserTab sourceTab, string url);
    void QueueWebContextBottomSplitOpen(BrowserTab sourceTab, string url);
    void QueueWebContextSplitPaneReplacement(BrowserTab sourceTab, string url, bool replaceOppositePane);
    void CopyContextLink(LinkCopyFormat format, string? linkUri, string? linkText, string successMessage);
}

// メニューごとの表示とクリック時の値を所有し、実Window・コンテナ処理は既存hostへ接続する。
internal static class PageContextMenuItemsBuilder
{
    public static void AddPageContainerContextMenu(IPageContextMenuItemsHost host, ContextMenu menu, BrowserTab sourceTab, string pageUri)
    {
        var containerMenu = new MenuItem
        {
            Header = "現在ページを別コンテナで開く"
        };

        foreach (var profile in host.ReloadContainerProfiles())
        {
            if (string.Equals(sourceTab.ContainerProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var profileId = profile.Id;
            var profileName = host.GetContainerProfileMenuHeader(profile);
            var item = CreateContextCommand(profileName, () =>
            {
                if (!host.ContainsTab(sourceTab))
                {
                    return;
                }

                var openedTab = host.OpenBackgroundTabNextTo(
                    sourceTab,
                    pageUri,
                    title: host.GetCurrentPageTitle(sourceTab, pageUri),
                    containerProfileId: profileId);
                host.SelectTab(openedTab);
                host.QueueSelectedTabActivation();
                host.SetStatus($"現在ページを{profileName}で開きました", autoClear: true);
            });
            item.Icon = host.CreateContainerProfileMenuIcon(profile);
            containerMenu.Items.Add(item);
        }

        if (containerMenu.Items.Count == 0)
        {
            containerMenu.IsEnabled = false;
        }

        menu.Items.Add(containerMenu);
    }

    public static void AddLinkContextMenuItems(
        IPageContextMenuItemsHost host,
        ContextMenu menu,
        BrowserTab sourceTab,
        string linkUri,
        string? linkText,
        string? miniPlayerLinkUri = null)
    {
        menu.Items.Add(CreateContextCommand("新しいタブで開く", () =>
        {
            var openedTab = host.OpenBackgroundTabNextTo(sourceTab, linkUri, title: linkText);
            host.SelectTab(openedTab);
            host.QueueSelectedTabActivation();
            host.SetStatus("リンクを新しいタブで開きました", autoClear: true);
        }));
        menu.Items.Add(CreateContextCommand("裏タブで開く", () =>
        {
            host.OpenBackgroundTabNextTo(sourceTab, linkUri, title: linkText);
            host.SetStatus("リンクを裏タブで開きました", autoClear: true);
        }));
        menu.Items.Add(CreateContextCommand("新しいウィンドウで開く", () => host.OpenLinkInNewWindow(sourceTab, linkUri, linkText)));
        host.AddMiniPlayerLinkContextMenuItem(menu, sourceTab, linkUri, linkText, miniPlayerLinkUri);

        var containerMenu = new MenuItem
        {
            Header = "別コンテナで開く"
        };
        foreach (var profile in host.ReloadContainerProfiles())
        {
            var profileId = profile.Id;
            var profileName = host.GetContainerProfileMenuHeader(profile);
            var item = CreateContextCommand(profileName, () =>
            {
                if (!host.ContainsTab(sourceTab))
                {
                    return;
                }

                host.OpenBackgroundTabNextTo(sourceTab, linkUri, title: linkText, containerProfileId: profileId);
                host.SetStatus($"リンクを{profileName}で開きました", autoClear: true);
            });
            item.Icon = host.CreateContainerProfileMenuIcon(profile);
            containerMenu.Items.Add(item);
        }
        menu.Items.Add(containerMenu);

        var splitMenu = new MenuItem
        {
            Header = "分割で開く",
            IsEnabled = host.CanOpenInSplitView(sourceTab)
        };
        splitMenu.Items.Add(CreateContextCommand("右分割で開く", () => host.QueueWebContextRightSplitOpen(sourceTab, linkUri), splitMenu.IsEnabled));
        splitMenu.Items.Add(CreateContextCommand("下分割で開く", () => host.QueueWebContextBottomSplitOpen(sourceTab, linkUri), splitMenu.IsEnabled));
        menu.Items.Add(splitMenu);

        var canReplaceCurrentPane = host.CanReplaceSplitPaneWithLink(sourceTab, replaceOppositePane: false);
        var canReplaceOppositePane = host.CanReplaceSplitPaneWithLink(sourceTab, replaceOppositePane: true);
        if (canReplaceCurrentPane || canReplaceOppositePane)
        {
            var replaceSplitPaneMenu = new MenuItem
            {
                Header = "分割ペインを置き換え"
            };
            replaceSplitPaneMenu.Items.Add(CreateContextCommand(
                "このペインをリンク先に置き換え",
                () => host.QueueWebContextSplitPaneReplacement(sourceTab, linkUri, replaceOppositePane: false),
                canReplaceCurrentPane));
            replaceSplitPaneMenu.Items.Add(CreateContextCommand(
                "反対側ペインをリンク先に置き換え",
                () => host.QueueWebContextSplitPaneReplacement(sourceTab, linkUri, replaceOppositePane: true),
                canReplaceOppositePane));
            menu.Items.Add(replaceSplitPaneMenu);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateContextCommand(
            "リンクURLをコピー",
            () => host.CopyContextLink(LinkCopyFormat.Url, linkUri, linkText, "リンクURLをコピーしました")));
        menu.Items.Add(CreateContextCommand(
            "Markdownリンクでコピー",
            () => host.CopyContextLink(LinkCopyFormat.Markdown, linkUri, linkText, "リンクをMarkdown形式でコピーしました")));
    }

    private static MenuItem CreateContextCommand(string header, Action action, bool isEnabled = true)
        => PageContextMenuController.CreateContextCommand(header, action, isEnabled);
}
