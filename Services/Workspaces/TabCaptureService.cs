using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Services.Workspaces;

public sealed class TabCaptureService
{
    private readonly TabRuntimeCoordinator _runtimeCoordinator;

    public TabCaptureService()
        : this(TabRuntimeCoordinator.Shared)
    {
    }

    internal TabCaptureService(TabRuntimeCoordinator runtimeCoordinator)
    {
        _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
    }

    public static Guid? ResolveActiveBrowserTabId(
        IEnumerable<BrowserTab> tabs,
        BrowserTab? activeTab,
        Guid? lastActiveBrowserTabId)
    {
        if (activeTab is { IsNativeContentTab: false })
        {
            return activeTab.Id;
        }

        return lastActiveBrowserTabId is { } previousId
            && tabs.Any(tab => tab.Id == previousId && !tab.IsNativeContentTab)
                ? previousId
                : tabs.FirstOrDefault(static tab => !tab.IsNativeContentTab)?.Id;
    }

    public IReadOnlyList<CapturedTabSnapshot> CaptureTabs(
        IReadOnlyList<BrowserTab> tabs,
        Guid? activeTabId,
        Guid windowId,
        string windowGroupTitle,
        int windowSortOrder,
        bool isForeignWindow,
        IReadOnlyList<SplitViewModel>? splitViews = null)
    {
        var result = new List<CapturedTabSnapshot>();
        for (var index = 0; index < tabs.Count; index++)
        {
            if (CreateSnapshot(
                    tabs[index],
                    index,
                    activeTabId,
                    windowId,
                    windowGroupTitle,
                    windowSortOrder,
                    isForeignWindow,
                    splitViews) is { } snapshot)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    public CapturedTabSnapshot? CaptureActiveTab(
        IReadOnlyList<BrowserTab> tabs,
        Guid? activeTabId,
        Guid windowId,
        string windowGroupTitle,
        int windowSortOrder,
        bool isForeignWindow,
        IReadOnlyList<SplitViewModel>? splitViews = null)
    {
        if (activeTabId is null)
        {
            return null;
        }

        var index = tabs
            .Select((tab, tabIndex) => new { Tab = tab, Index = tabIndex })
            .FirstOrDefault(x => x.Tab.Id == activeTabId.Value);

        return index is null
            ? null
            : CreateSnapshot(
                index.Tab,
                index.Index,
                activeTabId,
                windowId,
                windowGroupTitle,
                windowSortOrder,
                isForeignWindow,
                splitViews);
    }

    private CapturedTabSnapshot? CreateSnapshot(
        BrowserTab tab,
        int index,
        Guid? activeTabId,
        Guid windowId,
        string windowGroupTitle,
        int windowSortOrder,
        bool isForeignWindow,
        IReadOnlyList<SplitViewModel>? splitViews)
    {
        if (tab.IsWorkspaceTab)
        {
            return null;
        }

        var url = GetCurrentUrl(tab);
        if (string.IsNullOrWhiteSpace(url) || IsInternalBlankUrl(url))
        {
            return null;
        }

        var title = GetCurrentTitle(tab, url);
        var splitView = splitViews?.FirstOrDefault(candidate =>
            candidate.LeftTabId == tab.Id || candidate.RightTabId == tab.Id);
        return new CapturedTabSnapshot
        {
            TabId = tab.Id,
            WindowId = windowId,
            WindowGroupTitle = windowGroupTitle,
            WindowSortOrder = windowSortOrder,
            IsForeignWindow = isForeignWindow,
            Title = title,
            Url = url,
            TabIndex = index,
            IsActive = activeTabId == tab.Id,
            IsPinned = false,
            IsFolderTab = tab.IsFolderTab,
            ContainerProfileId = tab.ContainerProfileId,
            SplitGroupId = splitView?.Id.ToString("N") ?? string.Empty,
            SplitPane = splitView is null
                ? string.Empty
                : splitView.RightTabId == tab.Id ? SplitViewPane.Right : SplitViewPane.Left,
            SplitOrientation = splitView?.Orientation ?? string.Empty,
            SplitRatio = splitView?.Ratio,
            SplitActivePane = splitView?.ActivePane ?? string.Empty,
            Favicon = tab.Favicon,
            Thumbnail = tab.Thumbnail,
            FaviconPngBase64 = tab.FaviconPngBase64
        };
    }

    private string GetCurrentUrl(BrowserTab tab)
    {
        var source = _runtimeCoordinator.GetWebView(tab)?.CoreWebView2?.Source;
        if (!string.IsNullOrWhiteSpace(source) && !IsInternalBlankUrl(source))
        {
            return source.Trim();
        }

        return tab.Url?.Trim() ?? string.Empty;
    }

    private string GetCurrentTitle(BrowserTab tab, string url)
    {
        var documentTitle = _runtimeCoordinator.GetWebView(tab)?.CoreWebView2?.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(documentTitle))
        {
            return documentTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(tab.Title))
        {
            return tab.Title.Trim();
        }

        return TryGetHost(url) ?? url;
    }

    private static bool IsInternalBlankUrl(string url)
    {
        return url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("tabbrowser://", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }
}
