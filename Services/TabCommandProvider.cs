using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class TabCommandProvider
{
    private readonly TabRuntimeCoordinator _runtimeCoordinator;

    public const string ActivateTabActionId = "tab.activate";

    public TabCommandProvider()
        : this(TabRuntimeCoordinator.Shared)
    {
    }

    internal TabCommandProvider(TabRuntimeCoordinator runtimeCoordinator)
    {
        _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
    }

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<BrowserTab> tabs)
    {
        return tabs.Select(static tab => new TabCommandSource(tab)).Select(CreateItem).ToList();
    }

    public IReadOnlyList<CommandPaletteItem> CreateItems(IEnumerable<TabCommandSource> sources)
    {
        return sources.Select(CreateItem).ToList();
    }

    private CommandPaletteItem CreateItem(TabCommandSource source)
    {
        var tab = source.Tab;
        var url = GetCurrentUrl(tab);
        var title = string.IsNullOrWhiteSpace(tab.DisplayTitle)
            ? string.IsNullOrWhiteSpace(url) ? "無題のタブ" : url
            : tab.DisplayTitle.Trim();
        var windowLabel = source.WindowLabel?.Trim() ?? string.Empty;
        var subtitle = string.IsNullOrWhiteSpace(windowLabel)
            ? url
            : string.IsNullOrWhiteSpace(url) ? windowLabel : windowLabel + " - " + url;

        return new CommandPaletteItem
        {
            Id = "tab:" + tab.Id.ToString("D"),
            Type = CommandPaletteItemType.Tab,
            Title = title,
            Subtitle = subtitle,
            Url = url,
            Icon = tab.Favicon,
            Thumbnail = tab.Thumbnail,
            ActionId = ActivateTabActionId,
            Payload = tab.Id,
            Keywords = string.IsNullOrWhiteSpace(windowLabel)
                ? [title, url]
                : [title, url, windowLabel]
        };
    }

    private string GetCurrentUrl(BrowserTab tab)
    {
        var source = _runtimeCoordinator.GetWebView(tab)?.CoreWebView2?.Source;
        if (!string.IsNullOrWhiteSpace(source) && !source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return source.Trim();
        }

        return tab.Url?.Trim() ?? string.Empty;
    }
}

public sealed record TabCommandSource(BrowserTab Tab, string? WindowLabel = null);
