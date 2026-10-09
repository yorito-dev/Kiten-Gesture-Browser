using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record PageContextMenuTarget(
    string? PageUri, string? LinkUri, string? MiniPlayerLinkUri,
    string? LinkText, string? SelectedText, bool IsEditable);

internal interface IPageContextMenuHost
{
    void CopyContextPageUrl(BrowserTab sourceTab, string? pageUri);
    void AddPageContainerContextMenu(ContextMenu menu, BrowserTab sourceTab, string pageUri);
    void AddLinkContextMenuItems(ContextMenu menu, BrowserTab sourceTab, string linkUri, string? linkText, string? miniPlayerLinkUri);
    void OpenCommandPaletteForLink(BrowserTab sourceTab, string linkUri, string? linkText);
    void CopySelectedText(string? selectedText);
    void QueueWebContextPagePaste(BrowserTab sourceTab, ContextMenu menu);
    void OpenContextMenuTranslation(BrowserTab sourceTab, string? selectedText, string? pageUrl);
    bool CanTranslateContextMenuTarget(string? selectedText, string? pageUrl);
    void QueueSearchSelectionCommand(BrowserTab sourceTab, string? selectedText);
    void OpenSelectedTextXSearch(BrowserTab sourceTab, string? selectedText);
    void OpenSelectedTextYouTubeSearch(BrowserTab sourceTab, string? selectedText);
}

internal interface IPageContextMenuSurface
{
    ContextMenu Menu { get; }
    bool IsOpen { get; set; }
    event RoutedEventHandler Closed;
}

// URL許容判定と実コマンドはhostに残し、表示状態・項目構築・入力所有権の解放を同じownerに置く。
internal sealed class PageContextMenuController
{
    private readonly IPageContextMenuHost _host;
    private readonly InputInteractionHub _inputHub;
    private readonly Guid _windowId;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<ContextMenu, IPageContextMenuSurface> _createSurface;
    private DateTimeOffset _lastPageContextMenuOpenedAt = DateTimeOffset.MinValue;
    private BrowserTab? _lastPageContextMenuTab;
    private Point _lastPageContextMenuPoint;
    private IPageContextMenuSurface? _activePageContextMenu;
    private BrowserTab? _activePageContextMenuTab;
    private long? _activePageContextMenuInteractionId;

    public PageContextMenuController(IPageContextMenuHost host, InputInteractionHub inputHub, Guid windowId)
        : this(host, inputHub, windowId, static () => DateTimeOffset.Now,
            static menu => new WpfMenuSurface(menu))
    {
    }

    internal PageContextMenuController(IPageContextMenuHost host, InputInteractionHub inputHub, Guid windowId,
        Func<DateTimeOffset> now, Func<ContextMenu, IPageContextMenuSurface> createSurface)
    {
        _host = host;
        _inputHub = inputHub;
        _windowId = windowId;
        _now = now;
        _createSurface = createSurface;
    }

    public void Show(BrowserTab sourceTab, UIElement placementTarget, Point contextMenuPoint,
        Func<PageContextMenuTarget> resolveTarget)
    {
        var now = _now();
        if (ReferenceEquals(_lastPageContextMenuTab, sourceTab)
            && now - _lastPageContextMenuOpenedAt < TimeSpan.FromMilliseconds(180)
            && Math.Abs(_lastPageContextMenuPoint.X - contextMenuPoint.X)
                + Math.Abs(_lastPageContextMenuPoint.Y - contextMenuPoint.Y) < 4)
        {
            return;
        }

        _lastPageContextMenuTab = sourceTab;
        _lastPageContextMenuPoint = contextMenuPoint;
        _lastPageContextMenuOpenedAt = now;

        if (_activePageContextMenu is { IsOpen: true } activeMenu)
        {
            if (_activePageContextMenuInteractionId is { } activeInteractionId)
            {
                _inputHub.TryComplete(activeInteractionId);
                _activePageContextMenuInteractionId = null;
            }

            activeMenu.IsOpen = false;
        }

        var target = resolveTarget();
        var normalizedPageUri = target.PageUri;
        var normalizedLinkUri = target.LinkUri;
        var miniPlayerLinkUri = target.MiniPlayerLinkUri;
        var linkText = target.LinkText;
        var selectedText = target.SelectedText;
        var isEditable = target.IsEditable;
        var hasLink = normalizedLinkUri is not null;
        var hasSelection = !string.IsNullOrWhiteSpace(selectedText);
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = contextMenuPoint.X,
            VerticalOffset = contextMenuPoint.Y
        };

        if (!hasLink && normalizedPageUri is not null)
        {
            menu.Items.Add(CreateContextCommand("URLをコピー", () => _host.CopyContextPageUrl(sourceTab, normalizedPageUri)));
            _host.AddPageContainerContextMenu(menu, sourceTab, normalizedPageUri);
        }

        if (hasLink)
        {
            _host.AddLinkContextMenuItems(menu, sourceTab, normalizedLinkUri!, linkText, miniPlayerLinkUri);
            menu.Items.Add(CreateContextCommand(
                "このリンクをコマンドパレットで操作",
                () => _host.OpenCommandPaletteForLink(sourceTab, normalizedLinkUri!, linkText)));
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        if (hasSelection)
        {
            menu.Items.Add(CreateContextCommand(
                "コピー",
                () => _host.CopySelectedText(selectedText)));
        }

        if (isEditable)
        {
            menu.Items.Add(CreateContextCommand(
                "ペースト",
                () => _host.QueueWebContextPagePaste(sourceTab, menu)));
        }

        menu.Items.Add(CreateContextCommand(
            "翻訳",
            () => _host.OpenContextMenuTranslation(sourceTab, selectedText, normalizedPageUri),
            _host.CanTranslateContextMenuTarget(selectedText, normalizedPageUri)));
        if (hasSelection)
        {
            menu.Items.Add(CreateContextCommand(
                "選択範囲を検索",
                () => _host.QueueSearchSelectionCommand(sourceTab, selectedText)));
            menu.Items.Add(CreateContextCommand(
                "選択範囲を X で検索",
                () => _host.OpenSelectedTextXSearch(sourceTab, selectedText)));
            menu.Items.Add(CreateContextCommand(
                "選択範囲を YouTube で検索",
                () => _host.OpenSelectedTextYouTubeSearch(sourceTab, selectedText)));
        }

        var surface = _createSurface(menu);
        var interaction = _inputHub.TryBegin(
            InputInteractionKind.PageContextMenu,
            _windowId,
            sourceTab.Id,
            _now());
        if (!interaction.Accepted || interaction.Current is not { } currentInteraction)
        {
            menu.Items.Clear();
            return;
        }

        var menuInteractionId = currentInteraction.Id;
        _activePageContextMenu = surface;
        _activePageContextMenuTab = sourceTab;
        _activePageContextMenuInteractionId = menuInteractionId;
        surface.Closed += (_, _) =>
        {
            menu.Items.Clear();
            _inputHub.TryComplete(menuInteractionId);
            if (ReferenceEquals(_activePageContextMenu, surface))
            {
                _activePageContextMenu = null;
                _activePageContextMenuTab = null;
                _activePageContextMenuInteractionId = null;
            }
        };
        surface.IsOpen = true;
    }

    public bool TryGetOpenMenu(out ContextMenu menu, out BrowserTab sourceTab)
    {
        if (_activePageContextMenu is { IsOpen: true } surface
            && _activePageContextMenuTab is { } tab)
        {
            menu = surface.Menu;
            sourceTab = tab;
            return true;
        }

        menu = null!;
        sourceTab = null!;
        return false;
    }

    public void CloseForTab(BrowserTab tab)
    {
        if (ReferenceEquals(_activePageContextMenuTab, tab)
            && _activePageContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
        }
    }

    public bool CloseForReplacedInteraction(long interactionId)
    {
        if (_activePageContextMenuInteractionId == interactionId
            && _activePageContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
            return true;
        }

        return false;
    }

    internal static MenuItem CreateContextCommand(string header, Action action, bool isEnabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        item.Click += (_, _) => action();
        return item;
    }

    private sealed class WpfMenuSurface(ContextMenu menu) : IPageContextMenuSurface
    {
        public ContextMenu Menu { get; } = menu;
        public bool IsOpen { get => Menu.IsOpen; set => Menu.IsOpen = value; }
        public event RoutedEventHandler Closed
        {
            add => Menu.Closed += value;
            remove => Menu.Closed -= value;
        }
    }
}
