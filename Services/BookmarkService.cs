using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TabBrowser.Models;

namespace TabBrowser.Services;

public enum BookmarkLoadStatus
{
    LoadedPrimary,
    FirstRun,
    RecoveredFromBackup,
    RecoveredFromBackupButPrimaryRepairFailed,
    Failed
}

public sealed record BookmarkLoadResult(
    BookmarkData Data,
    BookmarkLoadStatus Status,
    Exception? PrimaryError = null,
    Exception? BackupError = null,
    Exception? RepairError = null)
{
    public bool CanUseData => Status != BookmarkLoadStatus.Failed;

    public bool CanPersistChanges => Status is not BookmarkLoadStatus.Failed
        and not BookmarkLoadStatus.RecoveredFromBackupButPrimaryRepairFailed;
}

public sealed class BookmarkDataLoadException(BookmarkLoadResult result)
    : IOException("ブックマーク本体とバックアップを読み込めませんでした。元ファイルは変更していません。",
        result.BackupError ?? result.PrimaryError)
{
    public BookmarkLoadResult Result { get; } = result;
}

public sealed class BookmarkService
{
    public static IEqualityComparer<string> UrlComparer { get; } = new BookmarkUrlComparer();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _bookmarksFile;
    private readonly string _bookmarksBackupFile;

    public BookmarkService()
        : this(AppPaths.BookmarksFile)
    {
    }

    public BookmarkService(string bookmarksFile)
    {
        _bookmarksFile = bookmarksFile;
        _bookmarksBackupFile = AtomicFileService.CreateBackupFilePath(bookmarksFile);
    }

    public List<BookmarkItem> Load()
    {
        return LoadData().Bookmarks;
    }

    public BookmarkData LoadData()
    {
        var result = LoadDataWithResult();
        if (!result.CanUseData)
        {
            throw new BookmarkDataLoadException(result);
        }

        return result.Data;
    }

    public BookmarkLoadResult LoadDataWithResult()
    {
        var primary = TryLoadCandidate(_bookmarksFile);
        if (primary.Data is not null)
        {
            return new BookmarkLoadResult(primary.Data, BookmarkLoadStatus.LoadedPrimary);
        }

        var backup = TryLoadCandidate(_bookmarksBackupFile);
        if (backup.Data is not null)
        {
            try
            {
                SaveDataToFile(_bookmarksFile, backup.Data, backupFilePath: null);
                return new BookmarkLoadResult(
                    backup.Data,
                    BookmarkLoadStatus.RecoveredFromBackup,
                    primary.Error);
            }
            catch (Exception ex) when (IsExpectedFileFailure(ex))
            {
                return new BookmarkLoadResult(
                    backup.Data,
                    BookmarkLoadStatus.RecoveredFromBackupButPrimaryRepairFailed,
                    primary.Error,
                    RepairError: ex);
            }
        }

        if (primary.IsMissing && backup.IsMissing)
        {
            return new BookmarkLoadResult(new BookmarkData(), BookmarkLoadStatus.FirstRun);
        }

        return new BookmarkLoadResult(
            new BookmarkData(),
            BookmarkLoadStatus.Failed,
            primary.Error,
            backup.Error);
    }

    public BookmarkData LoadDataFromFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var data = JsonSerializer.Deserialize<BookmarkData>(stream, JsonOptions);
        if (data is null || data.Bookmarks is null || data.Folders is null
            || data.Bookmarks.Any(static item => item is null)
            || data.Folders.Any(static item => item is null))
        {
            throw new JsonException("ブックマークの配列または要素が不正です。");
        }
        return NormalizeLoadedData(data);
    }

    public async Task SaveAsync(IEnumerable<BookmarkItem> bookmarks)
    {
        await SaveDataAsync(new BookmarkData { Bookmarks = bookmarks.ToList() });
    }

    public void Save(IEnumerable<BookmarkItem> bookmarks)
    {
        SaveData(new BookmarkData { Bookmarks = bookmarks.ToList() });
    }

    public async Task SaveDataAsync(BookmarkData data)
    {
        AtomicFileService.EnsureDirectory(_bookmarksFile);
        await SaveDataToFileAsync(_bookmarksFile, data, _bookmarksBackupFile);
    }

    public void SaveData(BookmarkData data)
    {
        AtomicFileService.EnsureDirectory(_bookmarksFile);
        SaveDataToFile(_bookmarksFile, data, _bookmarksBackupFile);
    }

    public async Task SaveDataToFileAsync(string filePath, BookmarkData data)
    {
        await SaveDataToFileAsync(filePath, data, backupFilePath: null);
    }

    private static async Task SaveDataToFileAsync(string filePath, BookmarkData data, string? backupFilePath)
    {
        var normalizedData = NormalizeForSave(data);
        await AtomicFileService.WriteAsync(
            filePath,
            backupFilePath,
            async stream => await JsonSerializer.SerializeAsync(stream, normalizedData, JsonOptions));
    }

    public void SaveDataToFile(string filePath, BookmarkData data)
    {
        SaveDataToFile(filePath, data, backupFilePath: null);
    }

    private static void SaveDataToFile(string filePath, BookmarkData data, string? backupFilePath)
    {
        var normalizedData = NormalizeForSave(data);
        AtomicFileService.Write(
            filePath,
            backupFilePath,
            stream => JsonSerializer.Serialize(stream, normalizedData, JsonOptions));
    }

    private LoadCandidateResult TryLoadCandidate(string filePath)
    {
        try
        {
            return new LoadCandidateResult(LoadDataFromFile(filePath), IsMissing: false);
        }
        catch (Exception ex) when (IsExpectedFileFailure(ex))
        {
            return new LoadCandidateResult(
                Data: null,
                IsMissing: ex is FileNotFoundException or DirectoryNotFoundException,
                Error: ex);
        }
    }

    private static bool IsExpectedFileFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException;
    }

    private sealed record LoadCandidateResult(
        BookmarkData? Data,
        bool IsMissing,
        Exception? Error = null);

    public void ExportHtml(string filePath, BookmarkData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? AppPaths.Root);
        var normalizedData = NormalizeForSave(data);
        File.WriteAllText(filePath, BuildChromeBookmarksHtml(normalizedData), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static BookmarkData NormalizeLoadedData(BookmarkData data)
    {
        var bookmarks = data.Bookmarks
            .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(NormalizeBookmark)
            .GroupBy(static x => NormalizeUrl(x.Url), UrlComparer)
            .Select(static group => group.First())
            .ToList();

        if (bookmarks.Count > 0 && bookmarks.Any(static x => x.SortOrder != 0))
        {
            bookmarks = bookmarks
                .OrderBy(static x => x.SortOrder)
                .ThenBy(static x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        for (var i = 0; i < bookmarks.Count; i++)
        {
            bookmarks[i].SortOrder = i;
        }

        return new BookmarkData
        {
            Version = Math.Max(3, data.Version),
            Bookmarks = bookmarks,
            Folders = NormalizeFoldersForLoad(data.Folders, bookmarks),
            SortMode = data.SortMode
        };
    }

    private static BookmarkData NormalizeForSave(BookmarkData data)
    {
        var bookmarks = data.Bookmarks
            .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(NormalizeBookmark)
            .GroupBy(static x => NormalizeUrl(x.Url), UrlComparer)
            .Select(static group => group.First())
            .ToList();

        for (var i = 0; i < bookmarks.Count; i++)
        {
            bookmarks[i].SortOrder = i;
        }

        return new BookmarkData
        {
            Version = 3,
            Bookmarks = bookmarks,
            Folders = NormalizeFoldersForSave(data.Folders, bookmarks),
            SortMode = data.SortMode
        };
    }

    private static BookmarkItem NormalizeBookmark(BookmarkItem bookmark)
    {
        bookmark.Url = NormalizeUrl(bookmark.Url);
        bookmark.FolderPath = NormalizeFolderPath(bookmark.FolderPath);
        return bookmark;
    }

    private static List<BookmarkFolderItem> NormalizeFoldersForLoad(
        IEnumerable<BookmarkFolderItem> folders,
        IEnumerable<BookmarkItem> bookmarks)
    {
        var folderMap = new Dictionary<string, BookmarkFolderItem>(StringComparer.CurrentCultureIgnoreCase);
        var orderedFolders = folders
            .Select(NormalizeFolder)
            .Where(static x => !string.IsNullOrWhiteSpace(x.Path))
            .ToList();

        if (orderedFolders.Any(static x => x.SortOrder != 0))
        {
            orderedFolders = orderedFolders
                .OrderBy(static x => x.SortOrder)
                .ThenBy(static x => x.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        foreach (var folder in orderedFolders)
        {
            folderMap.TryAdd(folder.Path, folder);
        }

        AddMissingBookmarkFolders(folderMap, bookmarks);

        var result = folderMap.Values.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            result[i].SortOrder = i;
        }

        return result;
    }

    private static List<BookmarkFolderItem> NormalizeFoldersForSave(
        IEnumerable<BookmarkFolderItem> folders,
        IEnumerable<BookmarkItem> bookmarks)
    {
        var folderMap = new Dictionary<string, BookmarkFolderItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in folders.Select(NormalizeFolder).Where(static x => !string.IsNullOrWhiteSpace(x.Path)))
        {
            folderMap.TryAdd(folder.Path, folder);
        }

        AddMissingBookmarkFolders(folderMap, bookmarks);

        var result = folderMap.Values.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            result[i].SortOrder = i;
        }

        return result;
    }

    private static BookmarkFolderItem NormalizeFolder(BookmarkFolderItem folder)
    {
        folder.Path = NormalizeFolderPath(folder.Path);
        return folder;
    }

    private static void AddMissingBookmarkFolders(Dictionary<string, BookmarkFolderItem> folderMap, IEnumerable<BookmarkItem> bookmarks)
    {
        var missingPaths = bookmarks
            .SelectMany(static bookmark => GetFolderAndParents(bookmark.FolderPath))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(static x => x, StringComparer.CurrentCultureIgnoreCase);

        foreach (var path in missingPaths)
        {
            folderMap.TryAdd(path, new BookmarkFolderItem
            {
                Path = path,
                SortOrder = folderMap.Count
            });
        }
    }

    public static IReadOnlyList<string> GetFolderAndParents(string? folderPath)
    {
        var normalizedPath = NormalizeFolderPath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return [];
        }

        var result = new List<string>();
        var currentPath = string.Empty;
        foreach (var segment in SplitFolderPath(normalizedPath))
        {
            currentPath = string.IsNullOrWhiteSpace(currentPath)
                ? segment
                : $"{currentPath} / {segment}";
            result.Add(currentPath);
        }

        return result;
    }

    private static string BuildChromeBookmarksHtml(BookmarkData data)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        builder.AppendLine("<!-- This is an automatically generated file.");
        builder.AppendLine("     It will be read and overwritten.");
        builder.AppendLine("     DO NOT EDIT! -->");
        builder.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        builder.AppendLine("<TITLE>Bookmarks</TITLE>");
        builder.AppendLine("<H1>Bookmarks</H1>");
        builder.AppendLine("<DL><p>");

        var rootFolders = data.Folders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.Path) && !folder.Path.Contains(" / ", StringComparison.Ordinal))
            .OrderBy(static folder => folder.SortOrder)
            .ThenBy(static folder => folder.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var rootBookmarks = data.Bookmarks
            .Where(static bookmark => string.IsNullOrWhiteSpace(bookmark.FolderPath))
            .OrderBy(static bookmark => bookmark.SortOrder)
            .ToList();

        AppendBookmarks(builder, rootBookmarks, indent: 1);
        foreach (var folder in rootFolders)
        {
            AppendFolder(builder, data, folder.Path, indent: 1);
        }

        builder.AppendLine("</DL><p>");
        return builder.ToString();
    }

    private static void AppendFolder(StringBuilder builder, BookmarkData data, string folderPath, int indent)
    {
        var name = SplitFolderPath(folderPath).LastOrDefault() ?? folderPath;
        var createdAt = ToUnixTimeSeconds(data.Folders.FirstOrDefault(folder =>
            folder.Path.Equals(folderPath, StringComparison.CurrentCultureIgnoreCase))?.CreatedAt);
        AppendLine(builder, indent, $"<DT><H3 ADD_DATE=\"{createdAt}\">{Html(name)}</H3>");
        AppendLine(builder, indent, "<DL><p>");

        var childBookmarks = data.Bookmarks
            .Where(bookmark => NormalizeFolderPath(bookmark.FolderPath).Equals(folderPath, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(static bookmark => bookmark.SortOrder)
            .ToList();
        AppendBookmarks(builder, childBookmarks, indent + 1);

        var childPrefix = folderPath + " / ";
        var childFolders = data.Folders
            .Where(folder =>
            {
                var path = NormalizeFolderPath(folder.Path);
                return path.StartsWith(childPrefix, StringComparison.CurrentCultureIgnoreCase)
                    && !path[childPrefix.Length..].Contains(" / ", StringComparison.Ordinal);
            })
            .OrderBy(static folder => folder.SortOrder)
            .ThenBy(static folder => folder.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var childFolder in childFolders)
        {
            AppendFolder(builder, data, childFolder.Path, indent + 1);
        }

        AppendLine(builder, indent, "</DL><p>");
    }

    private static void AppendBookmarks(StringBuilder builder, IEnumerable<BookmarkItem> bookmarks, int indent)
    {
        foreach (var bookmark in bookmarks)
        {
            var title = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title;
            var createdAt = ToUnixTimeSeconds(bookmark.CreatedAt);
            AppendLine(builder, indent, $"<DT><A HREF=\"{Html(bookmark.Url)}\" ADD_DATE=\"{createdAt}\">{Html(title)}</A>");
        }
    }

    private static void AppendLine(StringBuilder builder, int indent, string value)
    {
        builder.Append(' ', indent * 4);
        builder.AppendLine(value);
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static long ToUnixTimeSeconds(DateTimeOffset? value)
    {
        return (value ?? DateTimeOffset.Now).ToUnixTimeSeconds();
    }

    public static string NormalizeUrl(string? url)
    {
        return url?.Trim() ?? string.Empty;
    }

    // 保存・表示用URLは変えず、比較時だけHTTP(S)のスキームとホストを揃える。
    // Uri.AbsoluteUri等によるパスの正規化、予約文字の復号、クエリの並替えは行わない。
    private sealed class BookmarkUrlComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null) return x is null && y is null;
            var left = GetIdentity(x);
            var right = GetIdentity(y);
            return left.IsFile == right.IsFile && (left.IsFile
                ? StringComparer.OrdinalIgnoreCase.Equals(left.Value, right.Value)
                : StringComparer.Ordinal.Equals(left.Value, right.Value));
        }

        public int GetHashCode(string value)
        {
            if (value is null) return 0;
            var identity = GetIdentity(value);
            return identity.IsFile
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Value)
                : StringComparer.Ordinal.GetHashCode(identity.Value);
        }

        private static (string Value, bool IsFile) GetIdentity(string? value)
        {
            var url = NormalizeUrl(value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (url, false);
            // Windowsのfile URI比較は従来どおり全体の大小文字を同一視する。
            if (uri.IsFile) return (url, true);
            if (uri.Scheme is not ("http" or "https") || url.Contains('\\')) return (url, false);

            var separator = url.IndexOf("://", StringComparison.Ordinal);
            if (separator < 0) return (url, false);
            var authorityStart = separator + 3;
            var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
            if (authorityEnd < 0) authorityEnd = url.Length;
            var authority = url[authorityStart..authorityEnd];
            var hostStart = authority.LastIndexOf('@') + 1;
            // userinfoはホストではないため、その大小文字も保持する。
            return (url[..separator].ToLowerInvariant() + "://" + authority[..hostStart]
                + authority[hostStart..].ToLowerInvariant() + url[authorityEnd..], false);
        }
    }

    public static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }

        return string.Join(
            " / ",
            folderPath
                .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(static x => !string.IsNullOrWhiteSpace(x)));
    }

    private static IReadOnlyList<string> SplitFolderPath(string folderPath)
    {
        return folderPath
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

}
