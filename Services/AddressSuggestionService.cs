namespace TabBrowser.Services;

internal enum AddressSuggestionKind
{
    OpenTab,
    Bookmark,
    BrowsingHistory
}

internal sealed record AddressSuggestionSource(
    AddressSuggestionKind Kind,
    string StableKey,
    string Url,
    string Title,
    string Host,
    string SecondaryText,
    string ContainerProfileId,
    int UsageCount = 0,
    DateTimeOffset? LastUsedAt = null);

internal sealed record AddressSuggestionResult(
    AddressSuggestionSource Source,
    int Score);

internal static class AddressSuggestionService
{
    public const int MaximumResultCount = 8;

    internal static Dictionary<string, T> CreateDistinctStableKeyLookup<T>(
        IEnumerable<T> items,
        Func<T, string> stableKeySelector)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            result.TryAdd(stableKeySelector(item), item);
        }

        return result;
    }

    public static IReadOnlyList<AddressSuggestionResult> Search(
        string? query,
        IEnumerable<AddressSuggestionSource> sources,
        int maximumResultCount = MaximumResultCount)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0 || maximumResultCount <= 0)
        {
            return [];
        }

        return sources
            .Where(static source => !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => new ScoredSource(source, GetMatchScore(source, normalizedQuery)))
            .GroupBy(
                static scored => CreateDeduplicationKey(scored.Source),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => CreateGroupResult(group))
            .Where(static result => result.Score > 0)
            .OrderByDescending(static result => result.Score)
            .ThenByDescending(static result => GetKindPriority(result.Source.Kind))
            .ThenByDescending(static result => result.Source.UsageCount)
            .ThenByDescending(static result => result.Source.LastUsedAt)
            .ThenBy(static result => result.Source.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximumResultCount)
            .ToList();
    }

    internal static string NormalizeUrl(string? url)
    {
        var value = url?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return value;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.IdnHost.ToLowerInvariant()
        };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    private static AddressSuggestionResult CreateGroupResult(IEnumerable<ScoredSource> group)
    {
        var entries = group.ToList();
        var matchScore = entries.Max(static entry => entry.MatchScore);
        if (matchScore <= 0)
        {
            return new AddressSuggestionResult(entries[0].Source, 0);
        }

        var preferredSource = entries
            .OrderByDescending(static entry => GetKindPriority(entry.Source.Kind))
            .ThenByDescending(static entry => entry.MatchScore)
            .ThenByDescending(static entry => entry.Source.UsageCount)
            .ThenByDescending(static entry => entry.Source.LastUsedAt)
            .First()
            .Source;
        return new AddressSuggestionResult(
            preferredSource,
            matchScore + GetKindPriority(preferredSource.Kind));
    }

    private static int GetMatchScore(AddressSuggestionSource source, string query)
    {
        var score = Math.Max(
            ScoreText(source.Title, query, exact: 1000, prefix: 760, contains: 520),
            ScoreText(source.Host, query, exact: 920, prefix: 700, contains: 470));
        score = Math.Max(
            score,
            ScoreText(source.Url, query, exact: 880, prefix: 660, contains: 430));
        score = Math.Max(
            score,
            ScoreText(source.SecondaryText, query, exact: 600, prefix: 420, contains: 280));
        return score;
    }

    private static int ScoreText(string? value, string query, int exact, int prefix, int contains)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (value.Equals(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return exact;
        }

        if (value.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return prefix;
        }

        return value.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            ? contains
            : 0;
    }

    private static string CreateDeduplicationKey(AddressSuggestionSource source)
    {
        return NormalizeUrl(source.Url) + "\n" + source.ContainerProfileId.Trim();
    }

    private static int GetKindPriority(AddressSuggestionKind kind)
    {
        return kind switch
        {
            AddressSuggestionKind.OpenTab => 300,
            AddressSuggestionKind.Bookmark => 200,
            _ => 100
        };
    }

    private sealed record ScoredSource(AddressSuggestionSource Source, int MatchScore);
}
