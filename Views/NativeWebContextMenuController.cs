using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface INativeWebContextMenuHost : IPageContextMenuHost, IPageContextMenuItemsHost
{
    bool HasWpfWebView(BrowserTab tab);
    bool ShouldSuppressRightWheelContextMenu();
    string? GetCurrentPageUrl(BrowserTab tab);
    bool IsSplitViewLinkUrl(string? url);
    bool CanUseContextPageUrl(string? url);
    string NormalizeYouTubeShortsUrl(string url);
    void ShowPageContextMenu(
        BrowserTab sourceTab,
        string? pageUri,
        string? linkUri,
        string? linkText,
        string? selectedText,
        bool isEditable,
        double? clientX,
        double? clientY);
}

// WebView2 nativeメニューの項目、要求ごとの対象、クリック処理を同じownerに置く。
internal sealed class NativeWebContextMenuController
{
    private readonly INativeWebContextMenuHost _host;
    private readonly BrowserTab _tab;
    private readonly CoreWebView2 _core;
    private readonly CoreWebView2ContextMenuItem _translateMenuItem;
    private readonly CoreWebView2ContextMenuItem _copySelectedTextMenuItem;
    private readonly CoreWebView2ContextMenuItem _selectedTextSearchMenuItem;
    private readonly CoreWebView2ContextMenuItem _xSearchMenuItem;
    private readonly CoreWebView2ContextMenuItem _youTubeSearchMenuItem;
    private readonly CoreWebView2ContextMenuItem _copyPageUrlMenuItem;
    private readonly CoreWebView2ContextMenuItem _youTubeMiniPlayerMenuItem;
    private readonly CoreWebView2ContextMenuItem _openPageInContainerMenuItem;
    private readonly CoreWebView2ContextMenuItem _openLinkInNewTabMenuItem;
    private readonly CoreWebView2ContextMenuItem _openLinkInBackgroundTabMenuItem;
    private readonly CoreWebView2ContextMenuItem _openLinkInNewWindowMenuItem;
    private readonly CoreWebView2ContextMenuItem _openLinkInContainerMenuItem;
    private readonly CoreWebView2ContextMenuItem _copyLinkUrlMenuItem;
    private readonly CoreWebView2ContextMenuItem _copyLinkMarkdownMenuItem;
    private readonly CoreWebView2ContextMenuItem _splitLinkMenuItem;
    private readonly CoreWebView2ContextMenuItem _splitLinkRightMenuItem;
    private readonly CoreWebView2ContextMenuItem _splitLinkBottomMenuItem;
    private readonly CoreWebView2ContextMenuItem _replaceSplitPaneMenuItem;
    private readonly CoreWebView2ContextMenuItem _replaceCurrentSplitPaneMenuItem;
    private readonly CoreWebView2ContextMenuItem _replaceOppositeSplitPaneMenuItem;
    private string? _translateSelectionText;
    private string? _translatePageUri;
    private string? _contextLinkUri;
    private string? _contextLinkText;
    private string? _contextPageUri;

    private NativeWebContextMenuController(INativeWebContextMenuHost host, BrowserTab tab, CoreWebView2 core)
    {
        _host = host;
        _tab = tab;
        _core = core;
        var environment = core.Environment;
        _translateMenuItem = CreateItem(environment, "翻訳");
        _copySelectedTextMenuItem = CreateItem(environment, "コピー");
        _selectedTextSearchMenuItem = CreateItem(environment, "選択範囲を検索");
        _xSearchMenuItem = CreateItem(environment, "選択範囲を X で検索");
        _youTubeSearchMenuItem = CreateItem(environment, "選択範囲を YouTube で検索");
        _copyPageUrlMenuItem = CreateItem(environment, "URLをコピー");
        _youTubeMiniPlayerMenuItem = CreateItem(environment, "YouTubeミニプレイヤーで開く");
        _openPageInContainerMenuItem = CreateItem(
            environment,
            "現在ページを別コンテナで開く",
            CoreWebView2ContextMenuItemKind.Submenu);
        _openLinkInNewTabMenuItem = CreateItem(environment, "新しいタブで開く");
        _openLinkInBackgroundTabMenuItem = CreateItem(environment, "裏タブで開く");
        _openLinkInNewWindowMenuItem = CreateItem(environment, "新しいウィンドウで開く");
        _openLinkInContainerMenuItem = CreateItem(
            environment,
            "別コンテナで開く",
            CoreWebView2ContextMenuItemKind.Submenu);
        _copyLinkUrlMenuItem = CreateItem(environment, "リンクURLをコピー");
        _copyLinkMarkdownMenuItem = CreateItem(environment, "Markdownリンクでコピー");
        _splitLinkMenuItem = CreateItem(
            environment,
            "分割で開く",
            CoreWebView2ContextMenuItemKind.Submenu);
        _splitLinkRightMenuItem = CreateItem(environment, "右分割で開く");
        _splitLinkBottomMenuItem = CreateItem(environment, "下分割で開く");
        _splitLinkMenuItem.Children.Insert(0, _splitLinkRightMenuItem);
        _splitLinkMenuItem.Children.Insert(1, _splitLinkBottomMenuItem);
        _replaceSplitPaneMenuItem = CreateItem(
            environment,
            "分割ペインを置き換え",
            CoreWebView2ContextMenuItemKind.Submenu);
        _replaceCurrentSplitPaneMenuItem = CreateItem(environment, "このペインをリンク先に置き換え");
        _replaceOppositeSplitPaneMenuItem = CreateItem(environment, "反対側ペインをリンク先に置き換え");
        _replaceSplitPaneMenuItem.Children.Insert(0, _replaceCurrentSplitPaneMenuItem);
        _replaceSplitPaneMenuItem.Children.Insert(1, _replaceOppositeSplitPaneMenuItem);

        _translateMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab))
            {
                _host.OpenContextMenuTranslation(_tab, _translateSelectionText, _translatePageUri);
            }
        };
        _copySelectedTextMenuItem.CustomItemSelected += (_, _) => _host.CopySelectedText(_translateSelectionText);
        _selectedTextSearchMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab))
            {
                _host.QueueSearchSelectionCommand(_tab, _translateSelectionText);
            }
        };
        _xSearchMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab))
            {
                _host.OpenSelectedTextXSearch(_tab, _translateSelectionText);
            }
        };
        _youTubeSearchMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab))
            {
                _host.OpenSelectedTextYouTubeSearch(_tab, _translateSelectionText);
            }
        };
        _copyPageUrlMenuItem.CustomItemSelected += (_, _) => _host.CopyContextPageUrl(_tab, _contextPageUri);
        _youTubeMiniPlayerMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab))
            {
                _host.OpenPageInYouTubeMiniPlayer(_tab);
            }
        };
        _openLinkInNewTabMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab)
                && _contextLinkUri is { } linkUri
                && _host.IsSplitViewLinkUrl(linkUri))
            {
                var openedTab = _host.OpenBackgroundTabNextTo(
                    _tab,
                    _host.NormalizeYouTubeShortsUrl(linkUri),
                    title: _contextLinkText);
                _host.SelectTab(openedTab);
                _host.QueueSelectedTabActivation();
                _host.SetStatus("リンクを新しいタブで開きました", autoClear: true);
            }
        };
        _openLinkInBackgroundTabMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab)
                && _contextLinkUri is { } linkUri
                && _host.IsSplitViewLinkUrl(linkUri))
            {
                _host.OpenBackgroundTabNextTo(
                    _tab,
                    _host.NormalizeYouTubeShortsUrl(linkUri),
                    title: _contextLinkText);
                _host.SetStatus("リンクを裏タブで開きました", autoClear: true);
            }
        };
        _openLinkInNewWindowMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab)
                && _contextLinkUri is { } linkUri
                && _host.IsSplitViewLinkUrl(linkUri))
            {
                _host.OpenLinkInNewWindow(_tab, linkUri, _contextLinkText);
            }
        };
        _copyLinkUrlMenuItem.CustomItemSelected += (_, _) =>
            _host.CopyContextLink(
                LinkCopyFormat.Url,
                _contextLinkUri,
                _contextLinkText,
                "リンクURLをコピーしました");
        _copyLinkMarkdownMenuItem.CustomItemSelected += (_, _) =>
            _host.CopyContextLink(
                LinkCopyFormat.Markdown,
                _contextLinkUri,
                _contextLinkText,
                "リンクをMarkdown形式でコピーしました");
        _splitLinkRightMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab) && _contextLinkUri is { } linkUri)
            {
                _host.QueueWebContextRightSplitOpen(_tab, linkUri);
            }
        };
        _splitLinkBottomMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab) && _contextLinkUri is { } linkUri)
            {
                _host.QueueWebContextBottomSplitOpen(_tab, linkUri);
            }
        };
        _replaceCurrentSplitPaneMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab) && _contextLinkUri is { } linkUri)
            {
                _host.QueueWebContextSplitPaneReplacement(_tab, linkUri, replaceOppositePane: false);
            }
        };
        _replaceOppositeSplitPaneMenuItem.CustomItemSelected += (_, _) =>
        {
            if (_host.ContainsTab(_tab) && _contextLinkUri is { } linkUri)
            {
                _host.QueueWebContextSplitPaneReplacement(_tab, linkUri, replaceOppositePane: true);
            }
        };
    }

    public static EventHandler<CoreWebView2ContextMenuRequestedEventArgs> CreateRequestedHandler(
        INativeWebContextMenuHost host,
        BrowserTab tab,
        CoreWebView2 core)
    {
        var controller = new NativeWebContextMenuController(host, tab, core);
        return controller.HandleRequested;
    }

    private void HandleRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (!_host.ContainsTab(_tab))
        {
            return;
        }

        if (_host.ShouldSuppressRightWheelContextMenu())
        {
            args.Handled = true;
            return;
        }

        var target = args.ContextMenuTarget;
        _translateSelectionText = target.HasSelection ? target.SelectionText : null;
        _translatePageUri = !string.IsNullOrWhiteSpace(target.PageUri)
            ? target.PageUri
            : _host.GetCurrentPageUrl(_tab);
        _contextPageUri = _translatePageUri;
        _contextLinkUri = !string.IsNullOrWhiteSpace(target.LinkUri) ? target.LinkUri : null;
        _contextLinkText = !string.IsNullOrWhiteSpace(target.LinkText) ? target.LinkText : null;

        if (_host.HasWpfWebView(_tab))
        {
            args.Handled = true;
            _host.ShowPageContextMenu(
                _tab,
                _contextPageUri,
                _contextLinkUri,
                _contextLinkText,
                _translateSelectionText,
                target.IsEditable,
                args.Location.X,
                args.Location.Y);
            return;
        }

        var hasLink = _host.IsSplitViewLinkUrl(_contextLinkUri);
        var canUseCurrentPage = _host.CanUseContextPageUrl(_contextPageUri);
        var hasSelection = !string.IsNullOrWhiteSpace(_translateSelectionText);
        _translateMenuItem.IsEnabled = _host.CanTranslateContextMenuTarget(_translateSelectionText, _translatePageUri);
        _copyPageUrlMenuItem.IsEnabled = canUseCurrentPage;
        PopulatePageContainerMenu(_core.Environment, _openPageInContainerMenuItem, _contextPageUri);
        _openPageInContainerMenuItem.IsEnabled = !hasLink
            && canUseCurrentPage
            && _openPageInContainerMenuItem.Children.Count > 0;
        _openLinkInNewTabMenuItem.IsEnabled = hasLink;
        _openLinkInBackgroundTabMenuItem.IsEnabled = _openLinkInNewTabMenuItem.IsEnabled;
        _openLinkInNewWindowMenuItem.IsEnabled = _openLinkInNewTabMenuItem.IsEnabled;
        _copyLinkUrlMenuItem.IsEnabled = _openLinkInNewTabMenuItem.IsEnabled;
        _copyLinkMarkdownMenuItem.IsEnabled = _openLinkInNewTabMenuItem.IsEnabled;
        PopulateLinkContainerMenu(_core.Environment, _openLinkInContainerMenuItem, _contextLinkUri);
        _openLinkInContainerMenuItem.IsEnabled = _openLinkInNewTabMenuItem.IsEnabled
            && _openLinkInContainerMenuItem.Children.Count > 0;
        var canOpenLinkInSplit = _host.CanOpenInSplitView(_tab)
            && _host.IsSplitViewLinkUrl(_contextLinkUri);
        _splitLinkMenuItem.IsEnabled = canOpenLinkInSplit;
        _splitLinkRightMenuItem.IsEnabled = canOpenLinkInSplit;
        _splitLinkBottomMenuItem.IsEnabled = canOpenLinkInSplit;
        var canReplaceCurrentPane = hasLink
            && _host.CanReplaceSplitPaneWithLink(_tab, replaceOppositePane: false);
        var canReplaceOppositePane = hasLink
            && _host.CanReplaceSplitPaneWithLink(_tab, replaceOppositePane: true);
        _replaceSplitPaneMenuItem.IsEnabled = canReplaceCurrentPane || canReplaceOppositePane;
        _replaceCurrentSplitPaneMenuItem.IsEnabled = canReplaceCurrentPane;
        _replaceOppositeSplitPaneMenuItem.IsEnabled = canReplaceOppositePane;

        while (args.MenuItems.Count > 0)
        {
            args.MenuItems.RemoveAt(0);
        }
        if (!hasLink && _copyPageUrlMenuItem.IsEnabled)
        {
            if (YouTubeMiniPlayerOpenRequestService.TryCreateFromTab(_contextPageUri, null, out _))
            {
                args.MenuItems.Insert(args.MenuItems.Count, _youTubeMiniPlayerMenuItem);
            }

            args.MenuItems.Insert(args.MenuItems.Count, _copyPageUrlMenuItem);
        }

        if (_openPageInContainerMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _openPageInContainerMenuItem);
        }

        if (_openLinkInNewTabMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _openLinkInNewTabMenuItem);
            args.MenuItems.Insert(args.MenuItems.Count, _openLinkInBackgroundTabMenuItem);
            args.MenuItems.Insert(args.MenuItems.Count, _openLinkInNewWindowMenuItem);
        }

        if (_openLinkInContainerMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _openLinkInContainerMenuItem);
        }

        if (_splitLinkMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _splitLinkMenuItem);
        }

        if (_replaceSplitPaneMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _replaceSplitPaneMenuItem);
        }

        if (_copyLinkUrlMenuItem.IsEnabled)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _copyLinkUrlMenuItem);
            args.MenuItems.Insert(args.MenuItems.Count, _copyLinkMarkdownMenuItem);
        }

        if (hasSelection)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _copySelectedTextMenuItem);
        }

        args.MenuItems.Insert(args.MenuItems.Count, _translateMenuItem);
        if (hasSelection)
        {
            args.MenuItems.Insert(args.MenuItems.Count, _selectedTextSearchMenuItem);
            args.MenuItems.Insert(args.MenuItems.Count, _xSearchMenuItem);
            args.MenuItems.Insert(args.MenuItems.Count, _youTubeSearchMenuItem);
        }
    }

    private void PopulateLinkContainerMenu(
        CoreWebView2Environment environment,
        CoreWebView2ContextMenuItem menuItem,
        string? linkUri)
    {
        while (menuItem.Children.Count > 0)
        {
            menuItem.Children.RemoveAt(0);
        }
        if (!_host.IsSplitViewLinkUrl(linkUri))
        {
            return;
        }

        var normalizedLinkUri = _host.NormalizeYouTubeShortsUrl(linkUri!);
        foreach (var profile in _host.ReloadContainerProfiles())
        {
            var profileId = profile.Id;
            var profileName = _host.GetContainerProfileMenuHeader(profile);
            var item = CreateItem(environment, profileName);
            item.CustomItemSelected += (_, _) =>
            {
                if (!_host.ContainsTab(_tab))
                {
                    return;
                }

                _host.OpenBackgroundTabNextTo(_tab, normalizedLinkUri, containerProfileId: profileId);
                _host.SetStatus($"リンクを{profileName}で開きました", autoClear: true);
            };
            menuItem.Children.Insert(menuItem.Children.Count, item);
        }
    }

    private void PopulatePageContainerMenu(
        CoreWebView2Environment environment,
        CoreWebView2ContextMenuItem menuItem,
        string? pageUri)
    {
        while (menuItem.Children.Count > 0)
        {
            menuItem.Children.RemoveAt(0);
        }
        if (!_host.CanUseContextPageUrl(pageUri))
        {
            return;
        }

        var normalizedPageUri = _host.NormalizeYouTubeShortsUrl(pageUri!);
        foreach (var profile in _host.ReloadContainerProfiles())
        {
            if (string.Equals(_tab.ContainerProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var profileId = profile.Id;
            var profileName = _host.GetContainerProfileMenuHeader(profile);
            var item = CreateItem(environment, profileName);
            item.CustomItemSelected += (_, _) =>
            {
                if (!_host.ContainsTab(_tab))
                {
                    return;
                }

                var openedTab = _host.OpenBackgroundTabNextTo(
                    _tab,
                    normalizedPageUri,
                    title: _host.GetCurrentPageTitle(_tab, normalizedPageUri),
                    containerProfileId: profileId);
                _host.SelectTab(openedTab);
                _host.QueueSelectedTabActivation();
                _host.SetStatus($"現在ページを{profileName}で開きました", autoClear: true);
            };
            menuItem.Children.Insert(menuItem.Children.Count, item);
        }
    }

    private static CoreWebView2ContextMenuItem CreateItem(
        CoreWebView2Environment environment,
        string header,
        CoreWebView2ContextMenuItemKind kind = CoreWebView2ContextMenuItemKind.Command)
        => environment.CreateContextMenuItem(header, null!, kind);
}
