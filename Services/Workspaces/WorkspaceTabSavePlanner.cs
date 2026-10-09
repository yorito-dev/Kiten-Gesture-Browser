using TabBrowser.Models;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public sealed record WorkspaceTabSavePlan(
    IReadOnlyList<CapturedTabSnapshot> StandaloneFolderTabs,
    IReadOnlyList<CapturedTabSnapshot> SessionTabs,
    int SplitGroupCount);

public static class WorkspaceTabSavePlanner
{
    public static SessionTab? CreateSavedSingleTab(WorkspaceItem item)
    {
        if (item.ContainerProfileId is null)
        {
            return null;
        }

        return item switch
        {
            Models.Workspaces.BookmarkItem => new SessionTab { Url = item.Url, Title = item.Title, ContainerProfileId = item.ContainerProfileId },
            ShellItem { OpenInFolderTab: true } shell => new SessionTab
            {
                Url = shell.TargetPath,
                Title = shell.Title,
                IsFolderTab = true,
                ContainerProfileId = shell.ContainerProfileId
            },
            _ => null
        };
    }

    public static bool MatchesSavedTargets(
        IReadOnlyList<CapturedTabSnapshot> saved, IReadOnlyList<CapturedTabSnapshot> current)
    {
        return saved.All(before => current.Any(after =>
            before.TabId == after.TabId && before.WindowId == after.WindowId
            && before.Url == after.Url && before.Title == after.Title
            && before.IsFolderTab == after.IsFolderTab && before.ContainerProfileId == after.ContainerProfileId
            && before.SplitGroupId == after.SplitGroupId && before.SplitPane == after.SplitPane
            && before.SplitOrientation == after.SplitOrientation && before.SplitRatio == after.SplitRatio
            && before.SplitActivePane == after.SplitActivePane));
    }

    public static IReadOnlyList<CapturedTabSnapshot> ExpandSplitPartners(
        IEnumerable<CapturedTabSnapshot> requestedSnapshots,
        IEnumerable<CapturedTabSnapshot> availableSnapshots)
    {
        var available = availableSnapshots
            .DistinctBy(static snapshot => snapshot.TabId)
            .ToList();
        var result = requestedSnapshots
            .DistinctBy(static snapshot => snapshot.TabId)
            .ToDictionary(static snapshot => snapshot.TabId);

        foreach (var requested in result.Values.ToList())
        {
            if (!TryGetSplitKey(requested, out var requestedKey))
            {
                continue;
            }

            var group = available
                .Where(snapshot => TryGetSplitKey(snapshot, out var key) && key == requestedKey)
                .ToList();
            if (!IsCompleteSplitGroup(group))
            {
                continue;
            }

            foreach (var snapshot in group)
            {
                result.TryAdd(snapshot.TabId, snapshot);
            }
        }

        return result.Values
            .OrderBy(static snapshot => snapshot.WindowSortOrder)
            .ThenBy(static snapshot => snapshot.TabIndex)
            .ToList();
    }

    public static WorkspaceTabSavePlan CreatePlan(IEnumerable<CapturedTabSnapshot> snapshots)
    {
        var snapshotList = snapshots
            .DistinctBy(static snapshot => snapshot.TabId)
            .OrderBy(static snapshot => snapshot.WindowSortOrder)
            .ThenBy(static snapshot => snapshot.TabIndex)
            .ToList();
        var completeSplitTabIds = GetCompleteSplitGroups(snapshotList)
            .SelectMany(static group => group)
            .Select(static snapshot => snapshot.TabId)
            .ToHashSet();
        var standaloneFolderTabs = snapshotList
            .Where(snapshot => snapshot.IsFolderTab && !completeSplitTabIds.Contains(snapshot.TabId))
            .ToList();
        var sessionTabs = snapshotList
            .Where(snapshot => !snapshot.IsFolderTab || completeSplitTabIds.Contains(snapshot.TabId))
            .ToList();

        return new WorkspaceTabSavePlan(
            standaloneFolderTabs,
            sessionTabs,
            GetCompleteSplitGroups(snapshotList).Count);
    }

    public static List<SessionTab> CreateSessionTabs(IEnumerable<CapturedTabSnapshot> snapshots)
    {
        var snapshotList = snapshots
            .DistinctBy(static snapshot => snapshot.TabId)
            .OrderBy(static snapshot => snapshot.WindowSortOrder)
            .ThenBy(static snapshot => snapshot.TabIndex)
            .ToList();
        var completeSplitKeys = GetCompleteSplitGroups(snapshotList)
            .Select(static group => GetSplitKey(group[0]))
            .ToHashSet();

        return snapshotList
            .Select((snapshot, index) =>
            {
                var preserveSplit = TryGetSplitKey(snapshot, out var key)
                    && completeSplitKeys.Contains(key);
                return new SessionTab
                {
                    Url = snapshot.Url,
                    Title = snapshot.Title,
                    FaviconPngBase64 = snapshot.FaviconPngBase64,
                    ThumbnailPngBase64 = preserveSplit
                        && !CapturedTabSnapshot.IsMostlyBlackThumbnail(snapshot.Thumbnail)
                            ? snapshot.CreateThumbnailPngBase64()
                        : string.Empty,
                    SourceTabId = snapshot.TabId,
                    SortOrder = index,
                    IsFolderTab = snapshot.IsFolderTab,
                    ContainerProfileId = snapshot.ContainerProfileId,
                    SplitGroupId = preserveSplit ? snapshot.SplitGroupId : string.Empty,
                    SplitPane = preserveSplit ? snapshot.SplitPane : string.Empty,
                    SplitOrientation = preserveSplit
                        ? SplitViewModelProfile.NormalizeOrientation(snapshot.SplitOrientation)
                        : string.Empty,
                    SplitRatio = preserveSplit
                        ? NormalizeSplitRatio(snapshot.SplitRatio)
                        : null,
                    SplitActivePane = preserveSplit
                        ? NormalizeSplitPane(snapshot.SplitActivePane)
                        : string.Empty
                };
            })
            .ToList();
    }

    internal static IReadOnlyList<WorkspaceItem> CreateMixedSaveItems(WorkspaceTabSavePlan plan,
        Guid workspaceId, Guid? parentFolderId, double x, double y, string groupTitle)
    {
        if (plan.StandaloneFolderTabs.Count == 0 || plan.SessionTabs.Count == 0)
            throw new ArgumentException("混在したタブの保存対象が必要です。");
        var items = plan.StandaloneFolderTabs.Select((snapshot, index) => (WorkspaceItem)new ShellItem
        {
            WorkspaceId = workspaceId,
            ParentFolderId = parentFolderId,
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? WorkspaceShellItemService.GetDisplayName(snapshot.Url) : snapshot.Title,
            TargetPath = snapshot.Url,
            Kind = ShellItemKind.Directory,
            OpenInFolderTab = true,
            ContainerProfileId = snapshot.ContainerProfileId,
            X = x + (index + 1) * 104,
            Y = y + (index + 1) * 40
        }).ToList();
        var first = plan.SessionTabs[0];
        var title = string.IsNullOrWhiteSpace(first.Title) ? first.Url : first.Title;
        if (plan.SessionTabs.Count == 1)
        {
            items.Add(new Models.Workspaces.BookmarkItem
            {
                WorkspaceId = workspaceId,
                ParentFolderId = parentFolderId,
                Title = title,
                Url = first.Url,
                ContainerProfileId = first.TabIndex < 0 ? null : first.ContainerProfileId,
                Note = first.TabIndex < 0 ? string.Empty : $"Tab {first.TabIndex + 1}",
                FaviconPngBase64 = first.FaviconPngBase64,
                ThumbnailPngBase64 = first.CreateThumbnailPngBase64(),
                ThumbnailUpdatedAt = first.Thumbnail is null ? null : DateTimeOffset.Now,
                X = x,
                Y = y
            });
        }
        else
        {
            var tabs = CreateSessionTabs(plan.SessionTabs);
            var prefix = plan.SplitGroupCount == 1 && tabs.Count == 2
                ? (tabs.Any(x => x.SplitOrientation == SplitViewOrientation.TopBottom) ? "上下分割" : "左右分割") : groupTitle;
            var thumbnail = plan.SessionTabs.FirstOrDefault(x => x.Thumbnail is not null)?.CreateThumbnailPngBase64() ?? string.Empty;
            items.Add(new SessionGroupItem
            {
                WorkspaceId = workspaceId,
                ParentFolderId = parentFolderId,
                Title = $"{prefix}: {title} ほか {tabs.Count - 1}",
                Tabs = tabs,
                Note = $"{tabs.Count} tabs",
                X = x,
                Y = y,
                FaviconPngBase64 = tabs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.FaviconPngBase64))?.FaviconPngBase64 ?? string.Empty,
                ThumbnailPngBase64 = thumbnail,
                ThumbnailUpdatedAt = thumbnail.Length == 0 ? null : DateTimeOffset.Now
            });
        }
        return items;
    }

    public static bool IsCompleteSplitGroup(IReadOnlyList<CapturedTabSnapshot> snapshots)
    {
        return snapshots.Count == 2
            && snapshots.Select(static snapshot => snapshot.TabId).Distinct().Count() == 2
            && snapshots.Any(static snapshot => snapshot.SplitPane == SplitViewPane.Left)
            && snapshots.Any(static snapshot => snapshot.SplitPane == SplitViewPane.Right);
    }

    private static List<List<CapturedTabSnapshot>> GetCompleteSplitGroups(
        IReadOnlyList<CapturedTabSnapshot> snapshots)
    {
        return snapshots
            .Where(static snapshot => TryGetSplitKey(snapshot, out _))
            .GroupBy(static snapshot => GetSplitKey(snapshot))
            .Select(static group => group.ToList())
            .Where(IsCompleteSplitGroup)
            .ToList();
    }

    private static bool TryGetSplitKey(CapturedTabSnapshot snapshot, out string key)
    {
        key = string.Empty;
        if (snapshot.WindowId == Guid.Empty
            || !Guid.TryParse(snapshot.SplitGroupId, out var splitGroupId))
        {
            return false;
        }

        key = $"{snapshot.WindowId:N}:{splitGroupId:N}";
        return true;
    }

    private static string GetSplitKey(CapturedTabSnapshot snapshot)
    {
        return TryGetSplitKey(snapshot, out var key) ? key : string.Empty;
    }

    private static string NormalizeSplitPane(string? pane)
    {
        return string.Equals(pane, SplitViewPane.Right, StringComparison.OrdinalIgnoreCase)
            ? SplitViewPane.Right
            : SplitViewPane.Left;
    }

    private static double NormalizeSplitRatio(double? ratio)
    {
        return ratio is { } value && double.IsFinite(value)
            ? Math.Clamp(value, SplitViewModelProfile.MinimumRatio, SplitViewModelProfile.MaximumRatio)
            : SplitViewModelProfile.DefaultRatio;
    }
}
