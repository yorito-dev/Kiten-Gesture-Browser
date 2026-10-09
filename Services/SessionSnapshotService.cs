using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record SessionSnapshotInput(
    List<SessionWindowState> Windows,
    List<ClosedTabState> ClosedTabs,
    List<ClosedWindowState> ClosedWindows,
    List<YouTubeMiniPlayerState> ClosedYouTubeMiniPlayers);

internal static class SessionSnapshotService
{
    internal static NamedSessionRestorePlan CreateNamedRestorePlan(SessionData session)
    {
        var windows = CreateRememberedWindowStates(SessionService.CloneSession(session));
        var restored = new List<SessionWindowState>();
        foreach (var window in windows)
        {
            var tabs = window.Tabs;
            if (tabs.Count == 0) continue;
            var completeSplits = tabs.Where(tab => Guid.TryParse(tab.SplitViewId, out _))
                .GroupBy(tab => tab.SplitViewId!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 2)
                .ToDictionary(group => group.Key, _ => Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase);
            var active = tabs.FirstOrDefault(tab => tab.Id == window.LastActiveTabId) ?? tabs[0];
            foreach (var tab in tabs)
            {
                tab.Id = Guid.NewGuid();
                if (tab.SplitViewId is { } oldSplit && completeSplits.TryGetValue(oldSplit, out var newSplit)) tab.SplitViewId = newSplit;
                else { tab.SplitViewId = null; tab.SplitViewOrientation = null; tab.SplitViewRatio = null; }
            }
            window.WindowId = Guid.NewGuid();
            window.LastActiveTabId = active.Id;
            window.Tabs = tabs;
            window.ClosedTabs = [];
            restored.Add(window);
        }
        return new NamedSessionRestorePlan(restored);
    }

    internal static string CreateNamedRestoreStatus(int restoredTabCount, int deferredTabCount, int failedTabCount)
    {
        if (restoredTabCount > 0 && deferredTabCount == 0 && failedTabCount == 0)
        {
            return $"{restoredTabCount} タブを新しいWindowへ追加しました。現在のタブとWorkspaceはそのままです";
        }

        var result = restoredTabCount > 0
            ? $"{restoredTabCount} タブを追加しました。"
            : "タブは追加されませんでした。";
        var unavailable = new List<string>();
        if (deferredTabCount > 0)
        {
            unavailable.Add($"{deferredTabCount} タブはコンテナ不明で保留");
        }

        if (failedTabCount > 0)
        {
            unavailable.Add($"{failedTabCount} タブは復元失敗");
        }

        if (unavailable.Count > 0)
        {
            result += string.Join("、", unavailable) + "です。";
        }

        return result + "元の復元点は変更していません";
    }

    internal static List<SessionWindowState> CreateRememberedWindowStates(SessionData session)
    {
        if (session.Windows is { Count: > 0 })
        {
            return session.Windows
                .Where(static window => window.Tabs is { Count: > 0 })
                .ToList();
        }

        if (session.Tabs.Count == 0)
        {
            return [];
        }

        return
        [
            new SessionWindowState
            {
                LastActiveTabId = session.LastActiveTabId,
                Tabs = session.Tabs
            }
        ];
    }

    internal static SessionData FromWindow(SessionWindowState windowState)
    {
        return new SessionData
        {
            LastActiveTabId = windowState.LastActiveTabId,
            Tabs = windowState.Tabs ?? [],
            ClosedTabs = windowState.ClosedTabs ?? []
        };
    }

    internal static SessionData CreateInitial(
        IReadOnlyList<SessionTabState> tabs,
        TabNavigationHistoryService navigationHistoryService,
        Func<string?, string?, string?> normalizeSessionTabUrl)
    {
        var initialTabs = tabs
            .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x =>
            {
                var url = x.IsFolderTab
                    ? x.Url?.Trim()
                    : normalizeSessionTabUrl(x.Url, x.Title);
                var navigationHistory = navigationHistoryService.NormalizeNavigationHistory(x.NavigationHistory);
                return new SessionTabState
                {
                    Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
                    Url = url,
                    Title = x.Title,
                    IsFolderTab = x.IsFolderTab,
                    FolderTabViewMode = x.FolderTabViewMode,
                    FolderTabSortMode = x.FolderTabSortMode,
                    FolderTabSortDirection = x.FolderTabSortDirection,
                    FolderTabState = BrowserTabStateMapper.CloneFolderTabSessionState(x.FolderTabState),
                    NavigationHistory = navigationHistory,
                    NavigationHistoryIndex = navigationHistoryService.NormalizeNavigationHistoryIndex(navigationHistory, x.NavigationHistoryIndex, url),
                    AudioPan = x.AudioPan,
                    IsMuted = x.IsMuted,
                    AudioBoostDecibels = x.AudioBoostDecibels,
                    IsPinned = x.IsPinned,
                    LastActivatedAt = x.LastActivatedAt,
                    ContainerProfileId = x.ContainerProfileId,
                    SplitViewId = x.SplitViewId,
                    SplitViewOrientation = x.SplitViewOrientation,
                    SplitViewRatio = x.SplitViewRatio,
                    YouTubePlaybackVideoId = x.YouTubePlaybackVideoId,
                    YouTubePlaybackPositionSeconds = x.YouTubePlaybackPositionSeconds,
                    YouTubePlaybackSpeed = x.YouTubePlaybackSpeed,
                    YouTubePlaybackIsPaused = x.YouTubePlaybackIsPaused,
                    YouTubePlaybackIsLooping = x.YouTubePlaybackIsLooping,
                    YouTubePlaybackIsTheaterMode = x.YouTubePlaybackIsTheaterMode
                };
            })
            .ToList();

        return new SessionData
        {
            LastActiveTabId = initialTabs.FirstOrDefault()?.Id,
            Tabs = initialTabs
        };
    }

    public static SessionData Create(SessionSnapshotInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var primaryWindow = input.Windows.FirstOrDefault();
        return new SessionData
        {
            Version = 2,
            LastActiveTabId = primaryWindow?.LastActiveTabId,
            Tabs = primaryWindow?.Tabs ?? [],
            ClosedTabs = input.ClosedTabs,
            ClosedWindows = input.ClosedWindows,
            ClosedYouTubeMiniPlayers = input.ClosedYouTubeMiniPlayers,
            Windows = input.Windows
        };
    }
}

internal sealed record NamedSessionRestorePlan(IReadOnlyList<SessionWindowState> Windows);
