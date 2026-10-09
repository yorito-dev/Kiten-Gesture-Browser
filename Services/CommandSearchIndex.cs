using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class CommandSearchIndex
{
    public IReadOnlyList<CommandPaletteItem> Search(
        IEnumerable<CommandPaletteItem> items,
        string? query,
        IEnumerable<CommandPaletteUsageSettings>? usageEntries = null)
    {
        var normalizedQuery = Normalize(query);
        var source = items.ToList();
        var usageById = CommandPaletteUsageSettingsProfile.Normalize(usageEntries)
            .ToDictionary(entry => entry.ItemId, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return source
                .Select((item, index) => new
                {
                    Item = item,
                    Index = index,
                    LastUsedAt = usageById.TryGetValue(item.Id, out var usage)
                        ? usage.LastUsedAt
                        : DateTimeOffset.MinValue
                })
                .OrderByDescending(result => result.LastUsedAt)
                .ThenBy(result => result.Index)
                .Select(result => result.Item)
                .ToList();
        }

        var now = DateTimeOffset.UtcNow;
        return source
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                MatchScore = GetScore(item, normalizedQuery),
                UsageBonus = GetUsageBonus(item.Id, usageById, now)
            })
            .Where(result => result.MatchScore >= 0)
            .OrderByDescending(result => result.MatchScore + result.UsageBonus)
            .ThenBy(result => result.Index)
            .Select(result => result.Item)
            .ToList();
    }

    private static int GetUsageBonus(
        string itemId,
        IReadOnlyDictionary<string, CommandPaletteUsageSettings> usageById,
        DateTimeOffset now)
    {
        if (!usageById.TryGetValue(itemId, out var usage))
        {
            return 0;
        }

        var countBonus = Math.Min(8, usage.UseCount);
        var age = now - usage.LastUsedAt;
        var recencyBonus = age <= TimeSpan.FromDays(1)
            ? 12
            : age <= TimeSpan.FromDays(7)
                ? 8
                : age <= TimeSpan.FromDays(30) ? 4 : 0;
        return countBonus + recencyBonus;
    }

    private static int GetScore(CommandPaletteItem item, string normalizedQuery)
    {
        if (item.SearchTitleOnly)
        {
            var titleScore = ScoreText(
                item.Title,
                normalizedQuery,
                exact: 1000,
                prefix: 800,
                wordPrefix: 700,
                contains: 600);
            return titleScore < 0 ? -1 : titleScore + GetTypeBonus(item.Type);
        }

        var score = Math.Max(
            ScoreText(item.Title, normalizedQuery, exact: 1000, prefix: 800, wordPrefix: 700, contains: 600),
            Math.Max(
                ScoreText(item.Subtitle, normalizedQuery, exact: 500, prefix: 400, wordPrefix: 350, contains: 300),
                ScoreText(item.Url, normalizedQuery, exact: 450, prefix: 375, wordPrefix: 325, contains: 275)));
        foreach (var keyword in item.Keywords)
        {
            score = Math.Max(
                score,
                ScoreText(keyword, normalizedQuery, exact: 550, prefix: 475, wordPrefix: 425, contains: 375));
        }

        return score < 0 ? -1 : score + GetTypeBonus(item.Type);
    }

    private static int ScoreText(
        string? value,
        string normalizedQuery,
        int exact,
        int prefix,
        int wordPrefix,
        int contains)
    {
        var normalizedValue = Normalize(value);
        if (normalizedValue.Length == 0)
        {
            return -1;
        }

        if (normalizedValue.Equals(normalizedQuery, StringComparison.Ordinal))
        {
            return exact;
        }

        if (normalizedValue.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return prefix;
        }

        var matchIndex = normalizedValue.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (matchIndex < 0)
        {
            return -1;
        }

        return IsWordBoundary(normalizedValue[matchIndex - 1]) ? wordPrefix : contains;
    }

    private static bool IsWordBoundary(char value)
    {
        return char.IsWhiteSpace(value) || value is '-' or '_' or '/' or '\\' or '.' or ':';
    }

    private static int GetTypeBonus(CommandPaletteItemType type)
    {
        return type switch
        {
            CommandPaletteItemType.Tab => 40,
            CommandPaletteItemType.MiniPlayer => 35,
            CommandPaletteItemType.Command => 30,
            CommandPaletteItemType.Application => 28,
            CommandPaletteItemType.Workspace => 25,
            CommandPaletteItemType.RecentlyClosed => 20,
            CommandPaletteItemType.Clipboard => 15,
            CommandPaletteItemType.Bookmark => 10,
            CommandPaletteItemType.History => 0,
            _ => 0
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
