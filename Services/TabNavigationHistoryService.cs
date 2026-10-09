using TabBrowser.Models;

namespace TabBrowser.Services;

// タブの保存済み遷移履歴を更新する。実遷移、URLの保存可否、保存予約は呼出元の既存方針を使う。
internal sealed class TabNavigationHistoryService(Func<string?, bool> isPersistableUrl)
{
    private const int TabNavigationHistoryLimit = 50;
    private readonly Func<string?, bool> _isPersistableUrl = isPersistableUrl;

    internal void RestoreNavigationHistory(
        BrowserTab tab,
        IEnumerable<string>? history,
        int historyIndex,
        string? fallbackUrl)
    {
        var normalizedHistory = NormalizeNavigationHistory(history);
        if (normalizedHistory.Count == 0 && _isPersistableUrl(fallbackUrl))
        {
            normalizedHistory.Add(NormalizeTabNavigationHistoryUrl(fallbackUrl!));
        }

        tab.NavigationHistory = normalizedHistory;
        tab.NavigationHistoryIndex = NormalizeNavigationHistoryIndex(normalizedHistory, historyIndex, fallbackUrl);
        tab.PreferSavedNavigationHistory = normalizedHistory.Count > 1;
        tab.PendingSavedNavigationHistoryIndex = -1;
    }

    internal void RecordTabNavigation(BrowserTab tab, string? url, Action queueSessionSave)
    {
        if (tab.PendingSavedNavigationHistoryIndex >= 0
            || !_isPersistableUrl(url))
        {
            return;
        }

        var normalizedUrl = NormalizeTabNavigationHistoryUrl(url!);
        var history = tab.NavigationHistory;
        var index = tab.NavigationHistoryIndex;

        if (index >= 0
            && index < history.Count
            && history[index].Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (index > 0 && history[index - 1].Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            tab.NavigationHistoryIndex = index - 1;
            queueSessionSave();
            return;
        }

        if (index >= 0
            && index + 1 < history.Count
            && history[index + 1].Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            tab.NavigationHistoryIndex = index + 1;
            queueSessionSave();
            return;
        }

        if (index >= 0 && index + 1 < history.Count)
        {
            history.RemoveRange(index + 1, history.Count - index - 1);
        }

        if (history.Count == 0 || !history[^1].Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            history.Add(normalizedUrl);
        }

        tab.NavigationHistoryIndex = history.Count - 1;
        TrimTabNavigationHistory(tab);
        queueSessionSave();
    }

    internal bool TryCompleteSavedNavigationHistory(
        BrowserTab tab,
        string? finalUrl,
        bool isSuccess)
    {
        var targetIndex = tab.PendingSavedNavigationHistoryIndex;
        if (targetIndex < 0)
        {
            return false;
        }

        tab.PendingSavedNavigationHistoryIndex = -1;
        if (!isSuccess
            || targetIndex >= tab.NavigationHistory.Count
            || !_isPersistableUrl(finalUrl))
        {
            return false;
        }

        var normalizedFinalUrl = NormalizeTabNavigationHistoryUrl(finalUrl!);
        tab.NavigationHistory[targetIndex] = normalizedFinalUrl;
        while (targetIndex > 0
            && tab.NavigationHistory[targetIndex - 1].Equals(
                normalizedFinalUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            tab.NavigationHistory.RemoveAt(targetIndex);
            targetIndex--;
        }

        while (targetIndex + 1 < tab.NavigationHistory.Count
            && tab.NavigationHistory[targetIndex + 1].Equals(
                normalizedFinalUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            tab.NavigationHistory.RemoveAt(targetIndex + 1);
        }

        tab.NavigationHistoryIndex = targetIndex;
        return true;
    }

    internal bool CanNavigateSavedNavigationHistory(BrowserTab? tab, int offset)
    {
        if (tab is null || tab.PendingSavedNavigationHistoryIndex >= 0)
        {
            return false;
        }

        var targetIndex = tab.NavigationHistoryIndex + offset;
        return targetIndex >= 0 && targetIndex < tab.NavigationHistory.Count;
    }

    internal bool TryBeginSavedNavigationHistory(
        BrowserTab tab,
        int offset,
        out string targetUrl)
    {
        targetUrl = string.Empty;
        if (!CanNavigateSavedNavigationHistory(tab, offset))
        {
            return false;
        }

        var targetIndex = tab.NavigationHistoryIndex + offset;
        targetUrl = tab.NavigationHistory[targetIndex];
        tab.PendingSavedNavigationHistoryIndex = targetIndex;
        tab.PreferSavedNavigationHistory = true;
        tab.NavigationHistoryIndex = targetIndex;
        tab.Url = targetUrl;
        return true;
    }

    internal bool ShouldPreservePendingSavedNavigationHistory(
        BrowserTab tab,
        string? navigationUrl,
        bool isRedirected)
    {
        var targetIndex = tab.PendingSavedNavigationHistoryIndex;
        if (targetIndex < 0 || targetIndex >= tab.NavigationHistory.Count)
        {
            return false;
        }

        if (isRedirected)
        {
            return true;
        }

        if (!_isPersistableUrl(navigationUrl))
        {
            return false;
        }

        var expectedUrl = NormalizeTabNavigationHistoryUrl(tab.NavigationHistory[targetIndex]);
        var actualUrl = NormalizeTabNavigationHistoryUrl(navigationUrl!);
        if (expectedUrl.Equals(actualUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return YouTubeUrlService.TryGetVideoId(expectedUrl, out var expectedVideoId)
            && YouTubeUrlService.TryGetVideoId(actualUrl, out var actualVideoId)
            && expectedVideoId.Equals(actualVideoId, StringComparison.Ordinal);
    }

    internal List<string> NormalizeNavigationHistory(IEnumerable<string>? history)
    {
        var result = new List<string>();
        if (history is null)
        {
            return result;
        }

        foreach (var url in history)
        {
            if (!_isPersistableUrl(url))
            {
                continue;
            }

            var normalizedUrl = NormalizeTabNavigationHistoryUrl(url);
            if (result.Count == 0 || !result[^1].Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(normalizedUrl);
            }
        }

        if (result.Count > TabNavigationHistoryLimit)
        {
            result.RemoveRange(0, result.Count - TabNavigationHistoryLimit);
        }

        return result;
    }

    internal int NormalizeNavigationHistoryIndex(IReadOnlyList<string> history, int historyIndex, string? fallbackUrl)
    {
        if (history.Count == 0)
        {
            return -1;
        }

        var normalizedFallbackUrl = _isPersistableUrl(fallbackUrl)
            ? NormalizeTabNavigationHistoryUrl(fallbackUrl!)
            : string.Empty;
        if (historyIndex >= 0
            && historyIndex < history.Count
            && (string.IsNullOrWhiteSpace(normalizedFallbackUrl)
                || NormalizeTabNavigationHistoryUrl(history[historyIndex]).Equals(
                    normalizedFallbackUrl,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return historyIndex;
        }

        if (!string.IsNullOrWhiteSpace(normalizedFallbackUrl))
        {
            var fallbackIndex = history
                .Select((url, index) => new { url, index })
                .LastOrDefault(x => NormalizeTabNavigationHistoryUrl(x.url).Equals(
                    normalizedFallbackUrl,
                    StringComparison.OrdinalIgnoreCase))
                ?.index;
            if (fallbackIndex is { } index)
            {
                return index;
            }
        }

        return history.Count - 1;
    }

    internal List<string> CreatePersistedNavigationHistory(
        BrowserTab tab,
        string? currentUrl,
        out int historyIndex)
    {
        var history = NormalizeNavigationHistory(tab.NavigationHistory);
        var normalizedCurrentUrl = _isPersistableUrl(currentUrl)
            ? NormalizeTabNavigationHistoryUrl(currentUrl!)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(normalizedCurrentUrl))
        {
            if (history.Count == 0)
            {
                history.Add(normalizedCurrentUrl);
            }

            if (tab.NavigationHistoryIndex < 0
                || tab.NavigationHistoryIndex >= history.Count
                || !history[tab.NavigationHistoryIndex].Equals(normalizedCurrentUrl, StringComparison.OrdinalIgnoreCase))
            {
                var currentIndex = history
                    .Select((url, index) => new { url, index })
                    .LastOrDefault(x => x.url.Equals(normalizedCurrentUrl, StringComparison.OrdinalIgnoreCase))
                    ?.index;

                if (currentIndex is { } index)
                {
                    tab.NavigationHistoryIndex = index;
                }
                else
                {
                    history.Add(normalizedCurrentUrl);
                    tab.NavigationHistoryIndex = history.Count - 1;
                }
            }
        }

        historyIndex = NormalizeNavigationHistoryIndex(history, tab.NavigationHistoryIndex, currentUrl);
        if (history.Count > TabNavigationHistoryLimit)
        {
            var overflow = history.Count - TabNavigationHistoryLimit;
            history.RemoveRange(0, overflow);
            historyIndex = Math.Max(-1, historyIndex - overflow);
        }

        tab.NavigationHistory = history.ToList();
        tab.NavigationHistoryIndex = historyIndex;
        return history;
    }

    private static string NormalizeTabNavigationHistoryUrl(string url)
    {
        return BrowserUrlRulesService.NormalizeNavigationHistoryUrl(url.Trim());
    }

    private static void TrimTabNavigationHistory(BrowserTab tab)
    {
        if (tab.NavigationHistory.Count <= TabNavigationHistoryLimit)
        {
            return;
        }

        var overflow = tab.NavigationHistory.Count - TabNavigationHistoryLimit;
        tab.NavigationHistory.RemoveRange(0, overflow);
        tab.NavigationHistoryIndex = Math.Max(-1, tab.NavigationHistoryIndex - overflow);
    }

}
