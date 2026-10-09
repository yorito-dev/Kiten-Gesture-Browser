using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class FolderTabService
{
    public const int DefaultMaximumItemCount = 5000;
    public const int DefaultMaximumRecursiveSearchResultCount = 5000;
    public const int MaximumNavigationHistoryCount = 100;

    private static readonly string[] DefaultDetailColumnOrder =
    [
        "Name",
        "TypeText",
        "LastWriteTime",
        "SizeBytes"
    ];

    private const uint ShopFilePath = 0x00000002;
    private static readonly TimeSpan SearchProgressInterval = TimeSpan.FromMilliseconds(100);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(
        nint ownerWindowHandle,
        uint objectType,
        string objectName,
        string? propertyPage);

    public Task<FolderTabListing> EnumerateAsync(
        string folderPath,
        int maximumItemCount = DefaultMaximumItemCount,
        CancellationToken cancellationToken = default,
        bool showHiddenItems = false)
    {
        if (maximumItemCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItemCount));
        }

        var normalizedPath = NormalizeExistingDirectory(folderPath);
        return Task.Run(
            () => EnumerateCore(normalizedPath, maximumItemCount, showHiddenItems, cancellationToken),
            cancellationToken);
    }

    public Task<FolderTabChangeStamp> GetChangeStampAsync(
        string folderPath,
        CancellationToken cancellationToken = default,
        bool showHiddenItems = false)
    {
        var normalizedPath = NormalizeExistingDirectory(folderPath);
        return Task.Run(
            () => CreateChangeStamp(
                EnumerateCore(
                    normalizedPath,
                    DefaultMaximumItemCount,
                    showHiddenItems,
                    cancellationToken).Entries),
            cancellationToken);
    }

    public Task<FolderTabSearchResult> SearchDescendantsAsync(
        string folderPath,
        string query,
        int maximumResultCount = DefaultMaximumRecursiveSearchResultCount,
        bool showHiddenItems = false,
        IProgress<FolderTabSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumResultCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResultCount));
        }

        var normalizedPath = NormalizeExistingDirectory(folderPath);
        var criteria = FolderTabSearchCriteria.Parse(query);
        if (!criteria.HasCriteria)
        {
            throw new ArgumentException("検索語または条件を入力してください。", nameof(query));
        }

        return Task.Run(
            () => SearchDescendantsCore(
                normalizedPath,
                criteria,
                maximumResultCount,
                showHiddenItems,
                progress,
                cancellationToken),
            cancellationToken);
    }

    public static FolderTabChangeStamp CreateChangeStamp(IEnumerable<FolderTabEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        var count = 0;
        foreach (var entry in entries.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            count++;
            foreach (var character in entry.FullPath.ToUpperInvariant())
            {
                hash ^= character;
                hash *= prime;
            }

            hash ^= unchecked((ulong)(entry.SizeBytes ?? -1));
            hash *= prime;
            hash ^= unchecked((ulong)(entry.LastWriteTime?.Ticks ?? 0));
            hash *= prime;
        }

        return new FolderTabChangeStamp(count, hash);
    }

    internal static List<string> NormalizeDetailColumnOrder(IEnumerable<string>? columnOrder)
    {
        var validColumnIds = new HashSet<string>(DefaultDetailColumnOrder, StringComparer.Ordinal);
        var normalized = new List<string>(DefaultDetailColumnOrder.Length);
        if (columnOrder is not null)
        {
            foreach (var columnId in columnOrder)
            {
                var trimmedColumnId = columnId?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedColumnId)
                    && validColumnIds.Contains(trimmedColumnId)
                    && !normalized.Contains(trimmedColumnId, StringComparer.Ordinal))
                {
                    normalized.Add(trimmedColumnId);
                }
            }
        }

        foreach (var columnId in DefaultDetailColumnOrder)
        {
            if (!normalized.Contains(columnId, StringComparer.Ordinal))
            {
                normalized.Add(columnId);
            }
        }

        return normalized;
    }

    public static long? GetAvailableFreeSpace(string folderPath)
    {
        try
        {
            var root = Path.GetPathRoot(NormalizeExistingDirectory(folderPath));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static string NormalizeExistingDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("フォルダーのパスが空です。", nameof(folderPath));
        }

        var normalizedPath = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"フォルダーが見つかりません: {normalizedPath}");
        }

        return normalizedPath;
    }

    public static string ResolveRestoredDirectory(
        string? requestedPath,
        FolderTabSessionState? state = null)
    {
        var candidates = new List<string?> { requestedPath };
        if (state?.NavigationHistory is { Count: > 0 } history)
        {
            var currentIndex = state.NavigationHistoryIndex >= 0
                ? Math.Clamp(state.NavigationHistoryIndex, 0, history.Count - 1)
                : history.Count - 1;
            candidates.Add(history[currentIndex]);
            for (var distance = 1; distance < history.Count; distance++)
            {
                if (currentIndex - distance >= 0)
                {
                    candidates.Add(history[currentIndex - distance]);
                }

                if (currentIndex + distance < history.Count)
                {
                    candidates.Add(history[currentIndex + distance]);
                }
            }
        }

        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var examined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var normalized = Path.GetFullPath(candidate!.Trim());
                if (examined.Add(normalized) && Directory.Exists(normalized))
                {
                    return normalized;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                // 切断済みドライブや旧形式の不正なパスは、次の候補へフォールバックします。
            }
        }

        throw new DirectoryNotFoundException("復元できるフォルダーが見つかりません。");
    }

    internal static void TrimNavigationHistory(List<string> history, ref int historyIndex)
    {
        if (history.Count <= MaximumNavigationHistoryCount)
        {
            return;
        }

        var effectiveIndex = historyIndex >= 0
            ? Math.Clamp(historyIndex, 0, history.Count - 1)
            : history.Count - 1;
        var maximumStart = history.Count - MaximumNavigationHistoryCount;
        var start = Math.Clamp(
            effectiveIndex - MaximumNavigationHistoryCount / 2,
            0,
            maximumStart);
        history.RemoveRange(start + MaximumNavigationHistoryCount, history.Count - start - MaximumNavigationHistoryCount);
        history.RemoveRange(0, start);
        if (historyIndex >= 0)
        {
            historyIndex = effectiveIndex - start;
        }
    }

    public static string GetDisplayName(string folderPath)
    {
        var normalizedPath = Path.GetFullPath(folderPath);
        var name = Path.GetFileName(
            normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
    }

    public static ProcessStartInfo CreateOpenStartInfo(string path)
    {
        return new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = true
        };
    }

    public static ProcessStartInfo CreateExplorerStartInfo(string folderPath)
    {
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(NormalizeExistingDirectory(folderPath));
        return startInfo;
    }

    public void Open(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new FileNotFoundException("対象が見つかりません。", path);
        }

        Process.Start(CreateOpenStartInfo(path));
    }

    public void OpenInExplorer(string folderPath)
    {
        Process.Start(CreateExplorerStartInfo(folderPath));
    }

    public void ShowProperties(string path, nint ownerWindowHandle = default)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new FileNotFoundException("対象が見つかりません。", path);
        }

        if (!SHObjectProperties(
                ownerWindowHandle,
                ShopFilePath,
                Path.GetFullPath(path),
                propertyPage: null))
        {
            throw new InvalidOperationException("Windows Shellがプロパティ画面を開けませんでした。");
        }
    }

    private static FolderTabListing EnumerateCore(
        string normalizedPath,
        int maximumItemCount,
        bool showHiddenItems,
        CancellationToken cancellationToken)
    {
        var entries = new List<FolderTabEntry>();
        var isTruncated = false;

        foreach (var path in Directory.EnumerateFileSystemEntries(normalizedPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!ShouldShowEntry(attributes, showHiddenItems))
            {
                continue;
            }

            if (entries.Count >= maximumItemCount)
            {
                isTruncated = true;
                break;
            }

            entries.Add(CreateEntry(path, attributes: attributes));
        }

        SortEntries(entries, FolderTabSortMode.Name, FolderTabSortDirection.Ascending);

        return new FolderTabListing(normalizedPath, entries, isTruncated);
    }

    private static FolderTabSearchResult SearchDescendantsCore(
        string rootPath,
        FolderTabSearchCriteria criteria,
        int maximumResultCount,
        bool showHiddenItems,
        IProgress<FolderTabSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<FolderTabEntry>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);
        var scannedCount = 0;
        var excludedCount = 0;
        var isTruncated = false;
        var lastProgressTimestamp = Stopwatch.GetTimestamp();

        void ReportProgress(string currentPath, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            var currentTimestamp = Stopwatch.GetTimestamp();
            if (!force
                && Stopwatch.GetElapsedTime(lastProgressTimestamp, currentTimestamp) < SearchProgressInterval)
            {
                return;
            }

            lastProgressTimestamp = currentTimestamp;
            progress.Report(new FolderTabSearchProgress(
                scannedCount,
                results.Count,
                excludedCount,
                currentPath));
        }

        while (pendingDirectories.Count > 0 && !isTruncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedCount++;
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        excludedCount++;
                        ReportProgress(path);
                        continue;
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (!ShouldShowEntry(attributes, showHiddenItems))
                    {
                        continue;
                    }

                    var entry = CreateEntry(path, rootPath, attributes);
                    if (criteria.Matches(entry))
                    {
                        results.Add(entry);
                        if (results.Count >= maximumResultCount)
                        {
                            isTruncated = true;
                            break;
                        }
                    }

                    if (isDirectory && !isReparsePoint)
                    {
                        pendingDirectories.Push(path);
                    }

                    ReportProgress(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                excludedCount++;
                ReportProgress(currentDirectory);
            }
        }

        SortEntries(results, FolderTabSortMode.Name, FolderTabSortDirection.Ascending);
        ReportProgress(rootPath, force: true);
        return new FolderTabSearchResult(
            rootPath,
            results,
            scannedCount,
            excludedCount,
            isTruncated);
    }

    public static void SortEntries(
        List<FolderTabEntry> entries,
        string? sortMode,
        string? sortDirection)
    {
        var normalizedMode = FolderTabSortMode.Normalize(sortMode);
        var descending = string.Equals(
            FolderTabSortDirection.Normalize(sortDirection),
            FolderTabSortDirection.Descending,
            StringComparison.Ordinal);
        entries.Sort((left, right) => CompareEntries(left, right, normalizedMode, descending));
    }

    private static FolderTabEntry CreateEntry(
        string path,
        string? relativeRoot = null,
        FileAttributes? attributes = null)
    {
        var isDirectory = Directory.Exists(path);
        long? size = null;
        DateTime? lastWriteTime = null;

        try
        {
            if (isDirectory)
            {
                lastWriteTime = Directory.GetLastWriteTime(path);
            }
            else
            {
                var file = new FileInfo(path);
                size = file.Length;
                lastWriteTime = file.LastWriteTime;
            }
        }
        catch (IOException)
        {
            // 列挙中に対象が変化した場合も、名前とパスだけは表示します。
        }
        catch (UnauthorizedAccessException)
        {
            // 属性を取得できない対象も一覧から除外しません。
        }

        return new FolderTabEntry(
            Path.GetFileName(path),
            path,
            isDirectory,
            GetTypeText(path, isDirectory),
            FormatByteSize(size),
            lastWriteTime?.ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty,
            size,
            lastWriteTime,
            string.IsNullOrWhiteSpace(relativeRoot) ? null : Path.GetRelativePath(relativeRoot, path),
            ((attributes ?? File.GetAttributes(path)) & FileAttributes.Hidden) != 0);
    }

    internal static bool ShouldShowEntry(FileAttributes attributes, bool showHiddenItems)
    {
        if ((attributes & FileAttributes.System) != 0)
        {
            return false;
        }

        return showHiddenItems || (attributes & FileAttributes.Hidden) == 0;
    }

    private static int CompareEntries(
        FolderTabEntry left,
        FolderTabEntry right,
        string sortMode,
        bool descending)
    {
        var directoryOrder = right.IsDirectory.CompareTo(left.IsDirectory);
        if (directoryOrder != 0)
        {
            return directoryOrder;
        }

        var primaryOrder = sortMode switch
        {
            FolderTabSortMode.Type => StringComparer.CurrentCultureIgnoreCase.Compare(
                left.TypeText,
                right.TypeText),
            FolderTabSortMode.LastWriteTime => CompareNullable(
                left.LastWriteTime,
                right.LastWriteTime,
                descending),
            FolderTabSortMode.Size => CompareNullable(left.SizeBytes, right.SizeBytes, descending),
            _ => CompareNames(left.Name, right.Name)
        };
        if (primaryOrder != 0)
        {
            return sortMode is FolderTabSortMode.LastWriteTime or FolderTabSortMode.Size
                ? primaryOrder
                : descending ? -primaryOrder : primaryOrder;
        }

        return CompareNames(left.Name, right.Name);
    }

    private static int CompareNullable<T>(T? left, T? right, bool descending)
        where T : struct, IComparable<T>
    {
        if (!left.HasValue)
        {
            return right.HasValue ? 1 : 0;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        var result = left.Value.CompareTo(right.Value);
        return descending ? -result : result;
    }

    private static int CompareNames(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftRunStart = leftIndex;
                var rightRunStart = rightIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                {
                    leftIndex++;
                }

                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                {
                    rightIndex++;
                }

                var leftSignificantStart = leftRunStart;
                var rightSignificantStart = rightRunStart;
                while (leftSignificantStart < leftIndex - 1 && left[leftSignificantStart] == '0')
                {
                    leftSignificantStart++;
                }

                while (rightSignificantStart < rightIndex - 1 && right[rightSignificantStart] == '0')
                {
                    rightSignificantStart++;
                }

                var leftLength = leftIndex - leftSignificantStart;
                var rightLength = rightIndex - rightSignificantStart;
                if (leftLength != rightLength)
                {
                    return leftLength.CompareTo(rightLength);
                }

                for (var index = 0; index < leftLength; index++)
                {
                    var digitOrder = left[leftSignificantStart + index]
                        .CompareTo(right[rightSignificantStart + index]);
                    if (digitOrder != 0)
                    {
                        return digitOrder;
                    }
                }

                var runLengthOrder = (leftIndex - leftRunStart).CompareTo(rightIndex - rightRunStart);
                if (runLengthOrder != 0)
                {
                    return runLengthOrder;
                }

                continue;
            }

            var leftRunEnd = leftIndex;
            var rightRunEnd = rightIndex;
            while (leftRunEnd < left.Length && !char.IsDigit(left[leftRunEnd]))
            {
                leftRunEnd++;
            }

            while (rightRunEnd < right.Length && !char.IsDigit(right[rightRunEnd]))
            {
                rightRunEnd++;
            }

            var textOrder = StringComparer.CurrentCultureIgnoreCase.Compare(
                left[leftIndex..leftRunEnd],
                right[rightIndex..rightRunEnd]);
            if (textOrder != 0)
            {
                return textOrder;
            }

            leftIndex = leftRunEnd;
            rightIndex = rightRunEnd;
        }

        var lengthOrder = left.Length.CompareTo(right.Length);
        return lengthOrder != 0
            ? lengthOrder
            : StringComparer.Ordinal.Compare(left, right);
    }

    private static string GetTypeText(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "フォルダー";
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension)
            ? "ファイル"
            : extension.TrimStart('.').ToUpperInvariant() + " ファイル";
    }

    public static string FormatByteSize(long? size)
    {
        if (size is null)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)size.Value;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size.Value:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }
}

public sealed record FolderTabEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    string TypeText,
    string SizeText,
    string LastWriteTimeText,
    long? SizeBytes = null,
    DateTime? LastWriteTime = null,
    string? RelativePath = null,
    bool IsHidden = false);

public sealed record FolderTabListing(
    string FolderPath,
    IReadOnlyList<FolderTabEntry> Entries,
    bool IsTruncated);

public readonly record struct FolderTabChangeStamp(int EntryCount, ulong ContentHash);

public sealed record FolderTabSearchResult(
    string RootPath,
    IReadOnlyList<FolderTabEntry> Entries,
    int ScannedCount,
    int ExcludedCount,
    bool IsTruncated);

public readonly record struct FolderTabSearchProgress(
    int ScannedCount,
    int ResultCount,
    int ExcludedCount,
    string CurrentPath);

public sealed class FolderTabSearchCriteria
{
    private FolderTabSearchCriteria(
        IReadOnlyList<string> textTerms,
        long? minimumSizeBytes,
        bool minimumSizeInclusive,
        long? maximumSizeBytes,
        bool maximumSizeInclusive,
        DateTime? minimumDate,
        bool minimumDateInclusive,
        DateTime? maximumDate,
        bool maximumDateInclusive)
    {
        TextTerms = textTerms;
        MinimumSizeBytes = minimumSizeBytes;
        MinimumSizeInclusive = minimumSizeInclusive;
        MaximumSizeBytes = maximumSizeBytes;
        MaximumSizeInclusive = maximumSizeInclusive;
        MinimumDate = minimumDate;
        MinimumDateInclusive = minimumDateInclusive;
        MaximumDate = maximumDate;
        MaximumDateInclusive = maximumDateInclusive;
    }

    public IReadOnlyList<string> TextTerms { get; }

    public long? MinimumSizeBytes { get; }

    public bool MinimumSizeInclusive { get; }

    public long? MaximumSizeBytes { get; }

    public bool MaximumSizeInclusive { get; }

    public DateTime? MinimumDate { get; }

    public bool MinimumDateInclusive { get; }

    public DateTime? MaximumDate { get; }

    public bool MaximumDateInclusive { get; }

    public bool HasCriteria => TextTerms.Count > 0
        || MinimumSizeBytes is not null
        || MaximumSizeBytes is not null
        || MinimumDate is not null
        || MaximumDate is not null;

    public static FolderTabSearchCriteria Parse(string? query)
    {
        var textTerms = new List<string>();
        long? minimumSize = null;
        var minimumSizeInclusive = true;
        long? maximumSize = null;
        var maximumSizeInclusive = true;
        DateTime? minimumDate = null;
        var minimumDateInclusive = true;
        DateTime? maximumDate = null;
        var maximumDateInclusive = true;

        foreach (var token in (query ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseComparison(token, "size", out var sizeOperator, out var sizeValue)
                && TryParseByteSize(sizeValue, out var bytes))
            {
                ApplyRange(
                    sizeOperator,
                    bytes,
                    ref minimumSize,
                    ref minimumSizeInclusive,
                    ref maximumSize,
                    ref maximumSizeInclusive);
                continue;
            }

            if (TryParseComparison(token, "date", out var dateOperator, out var dateValue)
                && DateTime.TryParse(
                    dateValue,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var date))
            {
                ApplyRange(
                    dateOperator,
                    date.Date,
                    ref minimumDate,
                    ref minimumDateInclusive,
                    ref maximumDate,
                    ref maximumDateInclusive);
                continue;
            }

            textTerms.Add(token);
        }

        return new FolderTabSearchCriteria(
            textTerms,
            minimumSize,
            minimumSizeInclusive,
            maximumSize,
            maximumSizeInclusive,
            minimumDate,
            minimumDateInclusive,
            maximumDate,
            maximumDateInclusive);
    }

    public bool Matches(FolderTabEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (TextTerms.Any(term => !entry.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            && !Path.GetExtension(entry.Name).Contains(term, StringComparison.CurrentCultureIgnoreCase)
            && !entry.TypeText.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
        {
            return false;
        }

        if ((MinimumSizeBytes is not null || MaximumSizeBytes is not null)
            && entry.SizeBytes is not { } size)
        {
            return false;
        }

        if (entry.SizeBytes is { } sizeBytes
            && (!MatchesMinimum(sizeBytes, MinimumSizeBytes, MinimumSizeInclusive)
                || !MatchesMaximum(sizeBytes, MaximumSizeBytes, MaximumSizeInclusive)))
        {
            return false;
        }

        if ((MinimumDate is not null || MaximumDate is not null)
            && entry.LastWriteTime is not { } lastWriteTime)
        {
            return false;
        }

        return entry.LastWriteTime is not { } value
            || MatchesMinimum(value.Date, MinimumDate, MinimumDateInclusive)
            && MatchesMaximum(value.Date, MaximumDate, MaximumDateInclusive);
    }

    private static bool TryParseComparison(
        string token,
        string key,
        out string comparisonOperator,
        out string value)
    {
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
        {
            var prefix = key + candidate;
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && token.Length > prefix.Length)
            {
                comparisonOperator = candidate;
                value = token[prefix.Length..];
                return true;
            }
        }

        comparisonOperator = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool TryParseByteSize(string value, out long bytes)
    {
        var normalized = value.Trim().ToUpperInvariant();
        var multiplier = 1d;
        foreach (var unit in new[] { "TB", "GB", "MB", "KB", "B" })
        {
            if (!normalized.EndsWith(unit, StringComparison.Ordinal))
            {
                continue;
            }

            normalized = normalized[..^unit.Length];
            multiplier = unit switch
            {
                "TB" => 1024d * 1024d * 1024d * 1024d,
                "GB" => 1024d * 1024d * 1024d,
                "MB" => 1024d * 1024d,
                "KB" => 1024d,
                _ => 1d
            };
            break;
        }

        if (!double.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number)
            || number < 0
            || number * multiplier > long.MaxValue)
        {
            bytes = 0;
            return false;
        }

        bytes = (long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero);
        return true;
    }

    private static void ApplyRange<T>(
        string comparisonOperator,
        T value,
        ref T? minimum,
        ref bool minimumInclusive,
        ref T? maximum,
        ref bool maximumInclusive)
        where T : struct, IComparable<T>
    {
        if (comparisonOperator is ">" or ">=" or "=")
        {
            minimum = value;
            minimumInclusive = comparisonOperator != ">";
        }

        if (comparisonOperator is "<" or "<=" or "=")
        {
            maximum = value;
            maximumInclusive = comparisonOperator != "<";
        }
    }

    private static bool MatchesMinimum<T>(T value, T? minimum, bool inclusive)
        where T : struct, IComparable<T>
    {
        if (minimum is null)
        {
            return true;
        }

        var comparison = value.CompareTo(minimum.Value);
        return inclusive ? comparison >= 0 : comparison > 0;
    }

    private static bool MatchesMaximum<T>(T value, T? maximum, bool inclusive)
        where T : struct, IComparable<T>
    {
        if (maximum is null)
        {
            return true;
        }

        var comparison = value.CompareTo(maximum.Value);
        return inclusive ? comparison <= 0 : comparison < 0;
    }
}
