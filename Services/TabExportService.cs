using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class TabExportService
{
    private readonly TabRuntimeCoordinator _runtimeCoordinator;

    public TabExportService()
        : this(TabRuntimeCoordinator.Shared)
    {
    }

    internal TabExportService(TabRuntimeCoordinator runtimeCoordinator)
    {
        _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
    }

    public LinkCopyTarget? CreateLinkCopyTarget(BrowserTab? tab)
    {
        if (tab is null)
        {
            return null;
        }

        var url = GetCurrentUrl(tab);
        return string.IsNullOrWhiteSpace(url)
            ? null
            : new LinkCopyTarget(url, GetCurrentTitle(tab));
    }

    public IReadOnlyList<TabExportItem> ExportTabs(IEnumerable<BrowserTab> tabs)
    {
        return tabs
            .Select(CreateExportItem)
            .Where(static item => item is not null)
            .Cast<TabExportItem>()
            .ToList();
    }

    private TabExportItem? CreateExportItem(BrowserTab tab)
    {
        var url = GetCurrentUrl(tab);
        return string.IsNullOrWhiteSpace(url)
            ? null
            : new TabExportItem(tab.Id.ToString("D"), url, GetCurrentTitle(tab));
    }

    private string GetCurrentUrl(BrowserTab tab)
    {
        if (tab.IsWorkspaceTab)
        {
            return string.Empty;
        }

        var source = _runtimeCoordinator.GetWebView(tab)?.CoreWebView2?.Source;
        if (!IsUnavailableUrl(source))
        {
            return source!.Trim();
        }

        return IsUnavailableUrl(tab.Url)
            ? string.Empty
            : tab.Url!.Trim();
    }

    private string? GetCurrentTitle(BrowserTab tab)
    {
        var documentTitle = _runtimeCoordinator.GetWebView(tab)?.CoreWebView2?.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(documentTitle))
        {
            return documentTitle.Trim();
        }

        return string.IsNullOrWhiteSpace(tab.Title)
            ? null
            : tab.Title.Trim();
    }

    private static bool IsUnavailableUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("tabbrowser://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }
}
