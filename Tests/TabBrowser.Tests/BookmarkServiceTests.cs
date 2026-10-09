using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class BookmarkServiceTests
{
    [Fact]
    public void UrlComparerRetainsNullAndUnknownValuesWithoutGuessingEquivalence()
    {
        Assert.True(BookmarkService.UrlComparer.Equals(null, null));
        Assert.False(BookmarkService.UrlComparer.Equals(null, string.Empty));
        Assert.Equal(0, BookmarkService.UrlComparer.GetHashCode(null!));
        Assert.False(BookmarkService.UrlComparer.Equals("not a URL", "not a url"));
    }

    [Theory]
    [InlineData("https://example.test/A", "https://example.test/a", 2)]
    [InlineData("https://example.test/?q=Value", "https://example.test/?q=value", 2)]
    [InlineData("https://example.test/#Section", "https://example.test/#section", 2)]
    [InlineData("HTTPS://EXAMPLE.TEST/A", "https://example.test/A", 1)]
    [InlineData("https://example.test/a%2Fb", "https://example.test/a/b", 2)]
    [InlineData("https://example.test/?a=1&b=2", "https://example.test/?b=2&a=1", 2)]
    [InlineData("https://example.test/a", "https://example.test/a/", 2)]
    [InlineData("file:///C:/Docs/A.txt", "FILE:///c:/docs/a.txt", 1)]
    [InlineData("custom:Value", "custom:value", 2)]
    [InlineData("https://User@example.test/A", "https://user@example.test/A", 2)]
    [InlineData("https://example.test/a%2Fb", "https://example.test/a%2fb", 2)]
    [InlineData("https://example.test/a/../b", "https://example.test/b", 2)]
    [InlineData("https://example.test", "https://example.test/", 2)]
    public void UrlIdentityPreservesCaseSensitivePartsThroughSaveAndReload(string first, string second, int count)
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        service.SaveData(new BookmarkData
        {
            Bookmarks = [new() { Url = first, Title = "一件目" }, new() { Url = second, Title = "二件目" }]
        });
        var loaded = service.LoadData();
        Assert.Equal(count, loaded.Bookmarks.Count);
        Assert.Equal(first, loaded.Bookmarks[0].Url);
        if (count == 2) Assert.Equal(second, loaded.Bookmarks[1].Url);
        Assert.Equal(count == 1, BookmarkService.UrlComparer.Equals(first, second));
        if (count == 1) Assert.Equal(BookmarkService.UrlComparer.GetHashCode(first), BookmarkService.UrlComparer.GetHashCode(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCaseVariantsKeepIdsFoldersOrderAndUsageAcrossRecoverySaveAndExport(bool fromBackup)
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        var before = new BookmarkData
        {
            Version = 1,
            Bookmarks =
            [
                new() { Url = "https://example.test/A?q=Value#Section", Title = "大文字", FolderPath = "資料 / 上", OpenCount = 7, LastOpenedAt = DateTimeOffset.UnixEpoch, SortOrder = 0 },
                new() { Url = "https://example.test/a?q=value#section", Title = "小文字", FolderPath = "資料 / 下", OpenCount = 3, LastOpenedAt = DateTimeOffset.UnixEpoch.AddDays(1), SortOrder = 1 }
            ]
        };
        File.WriteAllText(fromBackup ? directory.BookmarksBackupFile : directory.BookmarksFile,
            System.Text.Json.JsonSerializer.Serialize(before, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }));
        var result = service.LoadDataWithResult();
        Assert.Equal(fromBackup ? BookmarkLoadStatus.RecoveredFromBackup : BookmarkLoadStatus.LoadedPrimary, result.Status);
        await service.SaveDataAsync(result.Data);
        service.SaveDataToFile(directory.ExportFile, service.LoadData());
        var reloaded = service.LoadDataFromFile(directory.ExportFile);
        Assert.Equal(2, reloaded.Bookmarks.Count);
        for (var i = 0; i < before.Bookmarks.Count; i++)
        {
            var expected = before.Bookmarks[i];
            var actual = reloaded.Bookmarks[i];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Url, actual.Url);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.FolderPath, actual.FolderPath);
            Assert.Equal(expected.OpenCount, actual.OpenCount);
            Assert.Equal(expected.LastOpenedAt, actual.LastOpenedAt);
            Assert.Equal(expected.SortOrder, actual.SortOrder);
        }
        var html = Path.Combine(directory.DirectoryPath, "bookmarks.html");
        service.ExportHtml(html, reloaded);
        foreach (var item in before.Bookmarks)
            Assert.Contains(System.Net.WebUtility.HtmlEncode(item.Url), File.ReadAllText(html));
    }

    [Fact]
    public void BrowserImportUsesTheSameIdentityWithoutReadingRealBrowserProfiles()
    {
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.DirectoryPath, "Bookmarks");
        File.WriteAllText(file, """
            {"roots":{"bookmark_bar":{"type":"folder","name":"資料","children":[
              {"type":"url","url":"https://example.test/A","name":"大文字"},
              {"type":"url","url":"https://example.test/a","name":"小文字"},
              {"type":"url","url":"HTTPS://EXAMPLE.TEST/A","name":"ホスト表記違い"},
              {"type":"url","url":"https://example.test/?q=Value","name":"検索大文字"},
              {"type":"url","url":"https://example.test/?q=value","name":"検索小文字"}
            ]}}}
            """);
        var existing = new HashSet<string>(["https://example.test/A"], BookmarkService.UrlComparer);
        var profile = BookmarkImportWindow.TryLoadProfile("テスト", "専用データ", file, existing);
        Assert.NotNull(profile);
        Assert.Equal(4, profile.TotalCount);
        Assert.Equal(1, profile.DuplicateCount);
        Assert.Equal(3, profile.NewCount);
        Assert.Contains(profile.NewBookmarks, item => item.Url == "https://example.test/a");
        Assert.All(profile.NewBookmarks, item => Assert.Equal("資料", item.FolderPath));
        var service = directory.CreateBookmarkService();
        service.SaveData(new BookmarkData { Bookmarks = profile.NewBookmarks.ToList() });
        Assert.Equal(profile.NewBookmarks.Select(item => item.Id), service.LoadData().Bookmarks.Select(item => item.Id));
        var html = Path.Combine(directory.DirectoryPath, "imported.html");
        service.ExportHtml(html, service.LoadData());
        foreach (var item in profile.NewBookmarks)
            Assert.Contains(System.Net.WebUtility.HtmlEncode(item.Url), File.ReadAllText(html));
    }

    [Fact]
    public void SaveDataKeepsPreviousBookmarkBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();

        service.SaveData(CreateData("https://example.com/first"));
        service.SaveData(CreateData("https://example.com/second"));

        var loaded = service.LoadData();
        var backup = new BookmarkService(directory.BookmarksBackupFile).LoadData();

        Assert.Equal("https://example.com/second", Assert.Single(loaded.Bookmarks).Url);
        Assert.Equal("https://example.com/first", Assert.Single(backup.Bookmarks).Url);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void LoadDataFallsBackToBookmarkBackupForBrokenJson()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();

        service.SaveData(CreateData("https://example.com/backup"));
        service.SaveData(CreateData("https://example.com/current"));
        File.WriteAllText(directory.BookmarksFile, "{ broken json");

        var result = service.LoadDataWithResult();
        var loaded = result.Data;

        Assert.Equal(BookmarkLoadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Bookmarks).Url);

        File.Delete(directory.BookmarksBackupFile);
        var repaired = service.LoadData();

        Assert.Equal("https://example.com/backup", Assert.Single(repaired.Bookmarks).Url);
    }

    [Fact]
    public void LoadDataRecoversMissingPrimaryFromBookmarkBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        service.SaveDataToFile(directory.BookmarksBackupFile, CreateData("https://example.com/backup"));

        var result = service.LoadDataWithResult();
        var loaded = result.Data;

        Assert.Equal(BookmarkLoadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Bookmarks).Url);
        Assert.True(File.Exists(directory.BookmarksFile));
        Assert.Equal(
            "https://example.com/backup",
            Assert.Single(service.LoadDataFromFile(directory.BookmarksFile).Bookmarks).Url);

        loaded.Bookmarks.Add(new BookmarkItem
        {
            Url = "https://example.com/after-recovery",
            Title = "after-recovery"
        });
        service.SaveData(loaded);

        Assert.Equal(2, service.LoadData().Bookmarks.Count);
    }

    [Fact]
    public void LoadDataReportsNormalFirstRunWithoutCreatingFiles()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();

        var result = service.LoadDataWithResult();

        Assert.Equal(BookmarkLoadStatus.FirstRun, result.Status);
        Assert.True(result.CanUseData);
        Assert.Empty(result.Data.Bookmarks);
        Assert.False(File.Exists(directory.BookmarksFile));
        Assert.False(File.Exists(directory.BookmarksBackupFile));
    }

    [Fact]
    public void LoadDataDoesNotTreatLockedPrimaryAsMissingData()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        service.SaveData(CreateData("https://example.com/current"));
        using var lockStream = new FileStream(
            directory.BookmarksFile,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = service.LoadDataWithResult();

        Assert.Equal(BookmarkLoadStatus.Failed, result.Status);
        Assert.False(result.CanUseData);
        Assert.ThrowsAny<IOException>(() => service.LoadData());
    }

    [Fact]
    public void LoadDataDoesNotReplaceUnreadablePrimaryAndBackupWithEmptyData()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        const string brokenPrimary = "{ broken primary";
        const string brokenBackup = "{ broken backup";
        File.WriteAllText(directory.BookmarksFile, brokenPrimary);
        File.WriteAllText(directory.BookmarksBackupFile, brokenBackup);

        var result = service.LoadDataWithResult();

        Assert.Equal(BookmarkLoadStatus.Failed, result.Status);
        Assert.False(result.CanUseData);
        Assert.ThrowsAny<IOException>(() => service.LoadData());

        Assert.Equal(brokenPrimary, File.ReadAllText(directory.BookmarksFile));
        Assert.Equal(brokenBackup, File.ReadAllText(directory.BookmarksBackupFile));
    }

    [Fact]
    public void LoadDataKeepsValidBackupWhenPrimaryRepairFails()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        service.SaveDataToFile(directory.BookmarksBackupFile, CreateData("https://example.com/backup"));
        Directory.CreateDirectory(directory.BookmarksFile);

        var result = service.LoadDataWithResult();

        Assert.Equal(BookmarkLoadStatus.RecoveredFromBackupButPrimaryRepairFailed, result.Status);
        Assert.True(result.CanUseData);
        Assert.NotNull(result.RepairError);
        Assert.Equal("https://example.com/backup", Assert.Single(result.Data.Bookmarks).Url);
        Assert.True(Directory.Exists(directory.BookmarksFile));
        Assert.Equal(
            "https://example.com/backup",
            Assert.Single(service.LoadDataFromFile(directory.BookmarksBackupFile).Bookmarks).Url);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void SaveDataToFileDoesNotCreateBackupForExplicitExportPath()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateBookmarkService();
        File.WriteAllText(directory.ExportFile, "{\"version\":3,\"bookmarks\":[]}");

        service.SaveDataToFile(directory.ExportFile, CreateData("https://example.com/export"));

        var loaded = service.LoadDataFromFile(directory.ExportFile);

        Assert.Equal("https://example.com/export", Assert.Single(loaded.Bookmarks).Url);
        Assert.False(File.Exists(directory.ExportBackupFile));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    private static BookmarkData CreateData(string url)
    {
        return new BookmarkData
        {
            Bookmarks =
            [
                new BookmarkItem
                {
                    Url = url,
                    Title = url
                }
            ]
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "TabBrowserBookmarkServiceTests",
            Guid.NewGuid().ToString("N"));

        public string BookmarksFile => Path.Combine(DirectoryPath, "bookmarks.json");

        public string BookmarksBackupFile => Path.Combine(DirectoryPath, "bookmarks.backup.json");

        public string ExportFile => Path.Combine(DirectoryPath, "export.json");

        public string ExportBackupFile => Path.Combine(DirectoryPath, "export.backup.json");

        public BookmarkService CreateBookmarkService()
        {
            return new BookmarkService(BookmarksFile);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
