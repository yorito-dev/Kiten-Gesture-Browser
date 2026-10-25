using System.Runtime.CompilerServices;

namespace TabBrowser.Models;

// 件数上限による脱落を明示削除と区別する。JSONには含めず、保存の捕捉時にコピーする。
internal static class SettingsCollectionEditTracking
{
    private static readonly ConditionalWeakTable<object, HashSet<string>> AutomaticRemovals = new();
    private static readonly ConditionalWeakTable<object, Dictionary<string, Mutation>> Mutations = new();
    private static long _mutationVersion;

    internal sealed record Mutation(long Version, object? Value);

    internal static List<T> Limit<T>(IEnumerable<T>? source, IEnumerable<T> ordered, int limit, Func<T, string> key)
    {
        var all = ordered.ToList();
        var result = all.Take(limit).ToList();
        var removed = Capture(source);
        removed.UnionWith(all.Skip(limit).Select(key));
        removed.ExceptWith(result.Select(key));
        Set(result, removed);
        SetMutations(result, CaptureMutations(source));
        return result;
    }

    internal static HashSet<string> Capture(object? source)
        => source is not null && AutomaticRemovals.TryGetValue(source, out var keys)
            ? new HashSet<string>(keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    internal static void Set(object? source, IEnumerable<string> keys)
    {
        if (source is null) return;
        AutomaticRemovals.Remove(source);
        var snapshot = new HashSet<string>(keys, StringComparer.Ordinal);
        if (snapshot.Count > 0) AutomaticRemovals.Add(source, snapshot);
    }

    internal static void ExplicitlyRemove(object source, string key)
    {
        var keys = Capture(source);
        keys.Remove(key);
        Set(source, keys);
        Record(source, key, null);
    }

    internal static Dictionary<string, Mutation> CaptureMutations(object? source)
        => source is not null && Mutations.TryGetValue(source, out var mutations)
            ? new Dictionary<string, Mutation>(mutations, StringComparer.Ordinal)
            : new Dictionary<string, Mutation>(StringComparer.Ordinal);

    internal static void SetMutations(object? source, IReadOnlyDictionary<string, Mutation> mutations)
    {
        if (source is null) return;
        Mutations.Remove(source);
        if (mutations.Count > 0) Mutations.Add(source, new Dictionary<string, Mutation>(mutations, StringComparer.Ordinal));
    }

    // 並べ替え用日時とは別の世代で、上限から落ちた実編集も保存完了まで保持する。
    internal static void Record(object source, string key, object? value)
    {
        var mutations = CaptureMutations(source);
        mutations[key] = new Mutation(Interlocked.Increment(ref _mutationVersion), value);
        SetMutations(source, mutations);
    }

    internal static void Acknowledge(object? source)
    {
        if (source is null) return;
        AutomaticRemovals.Remove(source);
        Mutations.Remove(source);
    }

    internal static string SiteKey(string containerId, string origin) => containerId + "\n" + origin;
}
