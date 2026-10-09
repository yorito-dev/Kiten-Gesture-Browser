using System.IO;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record FolderTabPlace(
    string Path,
    string DisplayName,
    bool IsPinned,
    bool IsAvailable);

public sealed record FolderTabPlacesSnapshot(
    IReadOnlyList<FolderTabPlace> PinnedPlaces,
    IReadOnlyList<FolderTabPlace> RecentPlaces);

public sealed record FolderTabPathSuggestion(
    string Path,
    string DisplayName,
    bool IsPinned);

public static class FolderTabPlacesService
{
    public const int MaximumPinnedPlaceCount = 50;
    public const int MaximumRecentPlaceCount = 20;

    public static List<string> NormalizePinnedPlaces(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? [])
        {
            if (TryNormalizePath(path, out var normalizedPath) && seen.Add(normalizedPath))
            {
                normalized.Add(normalizedPath);
                if (normalized.Count >= MaximumPinnedPlaceCount)
                {
                    break;
                }
            }
        }

        return normalized;
    }

    public static List<string> SetPinned(
        IEnumerable<string>? paths,
        string folderPath,
        bool isPinned)
    {
        var normalized = NormalizePinnedPlaces(paths);
        if (!TryNormalizePath(folderPath, out var targetPath))
        {
            return normalized;
        }

        normalized.RemoveAll(path => string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase));
        if (isPinned)
        {
            normalized.Insert(0, targetPath);
        }

        return normalized.Take(MaximumPinnedPlaceCount).ToList();
    }

    public static List<string> MovePinned(
        IEnumerable<string>? paths,
        string folderPath,
        int offset)
    {
        var normalized = NormalizePinnedPlaces(paths);
        if (offset == 0 || !TryNormalizePath(folderPath, out var targetPath))
        {
            return normalized;
        }

        var sourceIndex = normalized.FindIndex(
            path => string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return normalized;
        }

        var targetIndex = Math.Clamp(sourceIndex + Math.Sign(offset), 0, normalized.Count - 1);
        if (targetIndex == sourceIndex)
        {
            return normalized;
        }

        var item = normalized[sourceIndex];
        normalized.RemoveAt(sourceIndex);
        normalized.Insert(targetIndex, item);
        return normalized;
    }

    public static FolderTabPlacesSnapshot CreateSnapshot(
        IEnumerable<string>? pinnedPaths,
        IEnumerable<RecentItem>? recentItems)
    {
        var pinned = NormalizePinnedPlaces(pinnedPaths);
        var pinnedSet = pinned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinnedPlaces = pinned
            .Select(path => CreatePlace(path, isPinned: true))
            .ToList();
        var recentPlaces = (recentItems ?? [])
            .Where(static item => item.Category == RecentItemCategory.Folder)
            .OrderByDescending(static item => item.LastUsedAt)
            .Select(static item => item.Target)
            .Select(path => TryNormalizePath(path, out var normalizedPath) ? normalizedPath : null)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Where(path => !pinnedSet.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => CreatePlace(path, isPinned: false))
            .Where(static place => place.IsAvailable)
            .Take(MaximumRecentPlaceCount)
            .ToList();
        return new FolderTabPlacesSnapshot(pinnedPlaces, recentPlaces);
    }

    public static bool IsPinned(IEnumerable<string>? paths, string? folderPath)
    {
        return TryNormalizePath(folderPath, out var normalizedPath)
            && NormalizePinnedPlaces(paths).Contains(normalizedPath, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<FolderTabPathSuggestion> CreatePathSuggestions(
        FolderTabPlacesSnapshot snapshot,
        string? query,
        int maximumCount = 12)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        var normalizedQuery = query?.Trim() ?? string.Empty;
        return snapshot.PinnedPlaces
            .Concat(snapshot.RecentPlaces)
            .Where(place => normalizedQuery.Length == 0
                || place.DisplayName.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase)
                || place.Path.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(static place => place.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .Select(static place => new FolderTabPathSuggestion(
                place.Path,
                place.DisplayName,
                place.IsPinned))
            .ToList();
    }

    public static IReadOnlyList<FolderTabPathSuggestion> CreatePathSuggestions(
        IEnumerable<string>? pinnedPaths,
        IEnumerable<RecentItem>? recentItems,
        string? query,
        int maximumCount = 12)
    {
        var pinned = NormalizePinnedPlaces(pinnedPaths);
        var pinnedSet = pinned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var places = pinned.Select(path => new FolderTabPlace(
                path,
                GetDisplayName(path),
                IsPinned: true,
                IsAvailable: true))
            .Concat((recentItems ?? [])
                .Where(static item => item.Category == RecentItemCategory.Folder)
                .OrderByDescending(static item => item.LastUsedAt)
                .Select(static item => item.Target)
                .Select(path => TryNormalizePath(path, out var normalizedPath) ? normalizedPath : null)
                .Where(static path => path is not null)
                .Select(static path => path!)
                .Where(path => !pinnedSet.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FolderTabPlace(
                    path,
                    GetDisplayName(path),
                    IsPinned: false,
                    IsAvailable: true)));
        return CreatePathSuggestions(
            new FolderTabPlacesSnapshot(places.Where(static place => place.IsPinned).ToList(),
                places.Where(static place => !place.IsPinned).ToList()),
            query,
            maximumCount);
    }

    private static FolderTabPlace CreatePlace(string path, bool isPinned)
    {
        return new FolderTabPlace(
            path,
            GetDisplayName(path),
            isPinned,
            IsNetworkPath(path) || Directory.Exists(path));
    }

    private static string GetDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static bool IsNetworkPath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool TryNormalizePath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
