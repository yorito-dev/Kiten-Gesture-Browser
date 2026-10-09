using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class TabOperationService
{
    public const int ClosedTabHistoryLimit = 100;
    public const int ClosedTabRuntimeRetentionLimit = 10;
    public const int ClosedWindowHistoryLimit = 20;
    public static readonly TimeSpan ClosedTabHistoryRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan ClosedWindowHistoryRetention = TimeSpan.FromDays(7);

    public int ClampInsertionIndex(int insertionIndex, int tabCount)
    {
        return Math.Clamp(insertionIndex, 0, Math.Max(0, tabCount));
    }

    public int ClampInsertionIndexAfterLeadingPinnedTab(
        int insertionIndex,
        int tabCount,
        bool hasLeadingPinnedTab)
    {
        var clampedIndex = ClampInsertionIndex(insertionIndex, tabCount);
        return hasLeadingPinnedTab && tabCount > 0
            ? Math.Max(1, clampedIndex)
            : clampedIndex;
    }

    public int GetAdjacentInsertionIndex(int tabCount, int sourceIndex, int activeIndex)
    {
        var anchorIndex = sourceIndex >= 0 ? sourceIndex : activeIndex;
        return anchorIndex >= 0
            ? ClampInsertionIndex(anchorIndex + 1, tabCount)
            : Math.Max(0, tabCount);
    }

    public int GetInsertionIndexAfterGroup(int tabCount, int fallbackInsertionIndex, IEnumerable<int> groupIndices)
    {
        var lastGroupIndex = groupIndices
            .Where(index => index >= 0 && index < tabCount)
            .DefaultIfEmpty(-1)
            .Max();
        return lastGroupIndex >= 0
            ? ClampInsertionIndex(lastGroupIndex + 1, tabCount)
            : ClampInsertionIndex(fallbackInsertionIndex, tabCount);
    }

    public int GetSelectionIndexAfterRemoval(int removedIndex, int remainingCount)
    {
        if (remainingCount <= 0)
        {
            return -1;
        }

        return Math.Clamp(removedIndex, 0, remainingCount - 1);
    }

    public int GetCycledSelectionIndex(int currentIndex, int tabCount, int offset)
    {
        if (tabCount <= 0 || currentIndex < 0)
        {
            return -1;
        }

        return (currentIndex + offset + tabCount) % tabCount;
    }

    public int GetCycledSelectionIndexSkippingCandidate(
        int currentIndex,
        int tabCount,
        int offset,
        int skippedIndex)
    {
        var nextIndex = GetCycledSelectionIndex(currentIndex, tabCount, offset);
        if (nextIndex < 0 || nextIndex != skippedIndex)
        {
            return nextIndex;
        }

        if (tabCount <= 2)
        {
            return currentIndex;
        }

        var nextAfterSkippedIndex = GetCycledSelectionIndex(nextIndex, tabCount, offset);
        return nextAfterSkippedIndex < 0 ? currentIndex : nextAfterSkippedIndex;
    }

    public int GetReorderTargetIndex(int oldIndex, int insertionIndex, int tabCount)
    {
        if (tabCount <= 0 || oldIndex < 0 || oldIndex >= tabCount)
        {
            return -1;
        }

        var targetIndex = ClampInsertionIndex(insertionIndex, tabCount);
        if (oldIndex < targetIndex)
        {
            targetIndex--;
        }

        return Math.Clamp(targetIndex, 0, tabCount - 1);
    }

    public IReadOnlyList<int> GetReorderedIndicesForGroup(
        int tabCount,
        IEnumerable<int> groupIndices,
        int insertionIndex)
    {
        var allIndices = Enumerable.Range(0, Math.Max(0, tabCount)).ToList();
        var normalizedGroup = groupIndices
            .Where(index => index >= 0 && index < tabCount)
            .Distinct()
            .Order()
            .ToList();
        if (normalizedGroup.Count == 0)
        {
            return allIndices;
        }

        var normalizedInsertionIndex = ClampInsertionIndex(insertionIndex, tabCount);
        var removedBeforeInsertion = normalizedGroup.Count(index => index < normalizedInsertionIndex);
        var targetIndex = Math.Clamp(
            normalizedInsertionIndex - removedBeforeInsertion,
            0,
            tabCount - normalizedGroup.Count);
        var groupSet = normalizedGroup.ToHashSet();
        var reordered = allIndices.Where(index => !groupSet.Contains(index)).ToList();
        reordered.InsertRange(targetIndex, normalizedGroup);
        return reordered;
    }

    public IReadOnlyList<ClosedTabState> GetSharedClosedTabsFromSession(SessionData session)
    {
        if (session.ClosedTabs is { Count: > 0 })
        {
            return session.ClosedTabs;
        }

        return session.Windows?.SelectMany(static window => window.ClosedTabs ?? []).ToList() ?? [];
    }

    public Guid ResolveClosedItemRestoreWindowId(
        Guid? sourceWindowId,
        Guid currentWindowId,
        IEnumerable<Guid>? openWindowIds)
    {
        return sourceWindowId is { } sourceId
            && sourceId != Guid.Empty
            && openWindowIds?.Contains(sourceId) == true
                ? sourceId
                : currentWindowId;
    }

    public bool ShouldRestoreClosedWindowFirst(
        ClosedWindowState? closedWindow,
        ClosedTabState? closedTab)
    {
        if (closedWindow is null)
        {
            return false;
        }

        if (closedTab is null || closedTab.ClosedWindowId == closedWindow.Id)
        {
            return true;
        }

        return (closedWindow.ClosedAt ?? DateTimeOffset.MinValue)
            >= (closedTab.ClosedAt ?? DateTimeOffset.MinValue);
    }

    public List<ClosedTabState> NormalizeClosedTabHistory(
        IEnumerable<ClosedTabState>? closedTabs,
        DateTimeOffset? referenceTime = null)
    {
        var now = referenceTime?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var cutoff = now - ClosedTabHistoryRetention;

        return (closedTabs ?? [])
            .Where(tab => ShouldKeepClosedTabInHistory(tab, now, cutoff))
            .Take(ClosedTabHistoryLimit)
            .ToList();
    }

    public bool ShouldKeepClosedTabInHistory(ClosedTabState closedTab)
    {
        var now = DateTimeOffset.UtcNow;
        return ShouldKeepClosedTabInHistory(closedTab, now, now - ClosedTabHistoryRetention);
    }

    public List<ClosedWindowState> NormalizeClosedWindowHistory(
        IEnumerable<ClosedWindowState>? closedWindows,
        DateTimeOffset? referenceTime = null)
    {
        var now = referenceTime?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var cutoff = now - ClosedWindowHistoryRetention;

        return (closedWindows ?? [])
            .Where(window => ShouldKeepClosedWindowInHistory(window, now, cutoff))
            .Take(ClosedWindowHistoryLimit)
            .ToList();
    }

    public string GetClosedTabHistoryGroupLabel(
        DateTimeOffset? closedAt,
        DateTimeOffset? referenceTime = null)
    {
        var now = referenceTime ?? DateTimeOffset.Now;
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var localClosedAt = TimeZoneInfo.ConvertTime(closedAt ?? now, TimeZoneInfo.Local);
        var elapsedDays = Math.Max(0, (localNow.Date - localClosedAt.Date).Days);

        var relativeLabel = elapsedDays switch
        {
            0 => "今日",
            1 => "昨日",
            >= 7 => "1週間前",
            _ => $"{elapsedDays}日前"
        };

        return $"{relativeLabel}（{localClosedAt.Month}月{localClosedAt.Day}日）";
    }

    private static bool ShouldKeepClosedTabInHistory(
        ClosedTabState closedTab,
        DateTimeOffset referenceTime,
        DateTimeOffset cutoff)
    {
        if (string.IsNullOrWhiteSpace(closedTab.Url))
        {
            return false;
        }

        var closedAt = NormalizeClosedAt(closedTab.ClosedAt, referenceTime);
        closedTab.ClosedAt = closedAt;
        return closedAt >= cutoff;
    }

    private static bool ShouldKeepClosedWindowInHistory(
        ClosedWindowState closedWindow,
        DateTimeOffset referenceTime,
        DateTimeOffset cutoff)
    {
        if (closedWindow.WindowState?.Tabs is not { Count: > 0 })
        {
            return false;
        }

        if (closedWindow.Id == Guid.Empty)
        {
            closedWindow.Id = Guid.NewGuid();
        }

        var closedAt = NormalizeClosedAt(closedWindow.ClosedAt, referenceTime);
        closedWindow.ClosedAt = closedAt;
        return closedAt >= cutoff;
    }

    private static DateTimeOffset NormalizeClosedAt(DateTimeOffset? closedAt, DateTimeOffset fallback)
    {
        return closedAt is null || closedAt.Value == default
            ? fallback
            : closedAt.Value.ToUniversalTime();
    }
}
