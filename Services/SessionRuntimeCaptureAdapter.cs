using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record SessionRuntimeWindowSource(
    int WindowIndex,
    bool IsVisible,
    bool IsInitialized,
    bool IsPrivateWindow,
    bool IsClosingWindow,
    bool PersistSession,
    SessionService SessionService,
    Func<SessionWindowState> CapturePrimary,
    Func<SessionWindowState> CaptureFallback,
    Action<string>? ShowStatus = null);

internal interface ISessionWindowCaptureHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    Guid SessionWindowId { get; }
    bool IsAlwaysOnTop { get; }
    SessionTabState? CreateSessionTabState(BrowserTab tab, bool includeNavigationHistory);
    WindowPlacementSettings? CreateWindowPlacement();
    void RecordTabCaptureFailure(BrowserTab tab, Exception exception);
}

internal sealed class SessionRuntimeCaptureAdapter
{
    internal static SessionWindowState CaptureWindow(ISessionWindowCaptureHost host)
    {
        var tabs = new List<SessionTabState>();
        foreach (var tab in host.Tabs.Where(static x => !x.IsWorkspaceTab && !string.IsNullOrWhiteSpace(x.Url)))
        {
            try
            {
                if (host.CreateSessionTabState(tab, includeNavigationHistory: true) is { } tabState)
                {
                    tabs.Add(tabState);
                }
            }
            catch (Exception ex)
            {
                host.RecordTabCaptureFailure(tab, ex);
                if (host.CreateSessionTabState(tab, includeNavigationHistory: false) is { } fallbackState)
                {
                    tabs.Add(fallbackState);
                }
                else
                {
                    throw new InvalidOperationException("タブの保存状態を取得できませんでした。");
                }
            }
        }

        var lastActiveTabId = host.ActiveTab is { } activeTab && tabs.Any(x => x.Id == activeTab.Id)
            ? activeTab.Id
            : (Guid?)null;

        return new SessionWindowState
        {
            WindowId = host.SessionWindowId,
            LastActiveTabId = lastActiveTabId,
            Tabs = tabs,
            ClosedTabs = [],
            Placement = host.CreateWindowPlacement(),
            IsAlwaysOnTop = host.IsAlwaysOnTop
        };
    }

    internal static SessionWindowState CaptureFallbackWindow(ISessionWindowCaptureHost host)
    {
        var tabs = host.Tabs
            .Where(static x => !x.IsWorkspaceTab && !string.IsNullOrWhiteSpace(x.Url))
            .Select(tab => host.CreateSessionTabState(tab, includeNavigationHistory: false))
            .Where(static tab => tab is not null)
            .Select(static tab => tab!)
            .ToList();

        var lastActiveTabId = host.ActiveTab is { } activeTab && tabs.Any(x => x.Id == activeTab.Id)
            ? activeTab.Id
            : (Guid?)null;

        return new SessionWindowState
        {
            WindowId = host.SessionWindowId,
            LastActiveTabId = lastActiveTabId,
            Tabs = tabs,
            ClosedTabs = [],
            Placement = null,
            IsAlwaysOnTop = host.IsAlwaysOnTop
        };
    }

    private readonly Func<List<ClosedTabState>> _captureClosedTabs;
    private readonly Func<List<ClosedWindowState>> _captureClosedWindows;
    private readonly Func<List<YouTubeMiniPlayerState>> _captureClosedYouTubeMiniPlayers;
    private readonly Action<string, Exception, string?> _reportCaptureFailure;

    public SessionRuntimeCaptureAdapter(
        Func<List<ClosedTabState>> captureClosedTabs,
        Func<List<ClosedWindowState>> captureClosedWindows,
        Func<List<YouTubeMiniPlayerState>> captureClosedYouTubeMiniPlayers,
        Action<string, Exception, string?> reportCaptureFailure)
    {
        ArgumentNullException.ThrowIfNull(captureClosedTabs);
        ArgumentNullException.ThrowIfNull(captureClosedWindows);
        ArgumentNullException.ThrowIfNull(captureClosedYouTubeMiniPlayers);
        ArgumentNullException.ThrowIfNull(reportCaptureFailure);

        _captureClosedTabs = captureClosedTabs;
        _captureClosedWindows = captureClosedWindows;
        _captureClosedYouTubeMiniPlayers = captureClosedYouTubeMiniPlayers;
        _reportCaptureFailure = reportCaptureFailure;
    }

    public SessionPersistenceWorkItem? CaptureLatestPersistenceWorkItem(
        IReadOnlyList<SessionRuntimeWindowSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var owner = ResolvePersistenceOwner(sources);
        if (owner is null)
        {
            return null;
        }

        var snapshot = CreateSnapshot(sources, owner);
        var sessionService = owner.SessionService;
        return new SessionPersistenceWorkItem(
            snapshot,
            () => Task.Run(() => sessionService.SaveAsync(snapshot)),
            () => sessionService.Save(snapshot));
    }

    public SessionData CreateSnapshot(IReadOnlyList<SessionRuntimeWindowSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return CreateSnapshot(sources, ResolvePersistenceOwner(sources));
    }

    public bool TryReportStatusToPersistenceOwner(
        IReadOnlyList<SessionRuntimeWindowSource> sources,
        string status)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var owner = ResolvePersistenceOwner(sources);
        if (owner?.ShowStatus is null)
        {
            return false;
        }

        owner.ShowStatus(status);
        return true;
    }

    internal static SessionRuntimeWindowSource? ResolvePersistenceOwner(
        IReadOnlyList<SessionRuntimeWindowSource> sources)
    {
        return sources
            .Where(static source => source.IsInitialized && !source.IsPrivateWindow)
            .OrderByDescending(static source => !source.IsClosingWindow && source.PersistSession)
            .ThenByDescending(static source => !source.IsClosingWindow)
            .ThenByDescending(static source => source.PersistSession)
            .ThenBy(static source => source.WindowIndex)
            .FirstOrDefault();
    }

    private SessionData CreateSnapshot(
        IReadOnlyList<SessionRuntimeWindowSource> sources,
        SessionRuntimeWindowSource? fallbackOwner)
    {
        var captureSources = sources
            .Where(static source => source.IsVisible && !source.IsPrivateWindow)
            .OrderBy(static source => source.WindowIndex)
            .ToList();
        if (captureSources.Count == 0 && fallbackOwner is not null)
        {
            captureSources.Add(fallbackOwner);
        }

        var windowStates = new List<SessionWindowState>(captureSources.Count);
        foreach (var source in captureSources)
        {
            try
            {
                windowStates.Add(source.CapturePrimary());
            }
            catch (Exception ex)
            {
                _reportCaptureFailure(
                    "window-state-capture-failed",
                    ex,
                    $"window={source.WindowIndex}");
                var fallbackState = source.CaptureFallback();
                if (fallbackState.Tabs.Count > 0)
                {
                    windowStates.Add(fallbackState);
                }
                else
                {
                    throw new InvalidOperationException("Windowの保存状態を取得できませんでした。");
                }
            }
        }

        return SessionSnapshotService.Create(new SessionSnapshotInput(
            windowStates,
            _captureClosedTabs(),
            _captureClosedWindows(),
            _captureClosedYouTubeMiniPlayers()));
    }
}
