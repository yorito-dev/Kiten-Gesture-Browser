using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class BookmarkImportWindow : Window
{
    private readonly HashSet<string> _existingUrls;

    public BookmarkImportWindow(IEnumerable<BookmarkItem> existingBookmarks)
    {
        InitializeComponent();
        _existingUrls = existingBookmarks
            .Select(static x => BookmarkService.NormalizeUrl(x.Url))
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(BookmarkService.UrlComparer);
        DataContext = this;
    }

    public ObservableCollection<BrowserBookmarkProfile> Profiles { get; } = [];

    public IReadOnlyList<BookmarkItem> ImportedBookmarks { get; private set; } = [];

    public bool IsAccepted { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshProfiles();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshProfiles();
    }

    private void ProfileGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is BrowserBookmarkProfile profile)
        {
            ImportButton.IsEnabled = profile.NewCount > 0;
            StatusText.Text = $"{profile.BrowserName} / {profile.ProfileName}: 新規 {profile.NewCount} 件、重複 {profile.DuplicateCount} 件";
            return;
        }

        ImportButton.IsEnabled = false;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is not BrowserBookmarkProfile profile)
        {
            StatusText.Text = "取り込むプロファイルを選択してください";
            return;
        }

        ImportedBookmarks = profile.NewBookmarks;
        IsAccepted = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshProfiles()
    {
        try
        {
            Profiles.Clear();

            foreach (var profile in LoadProfiles(_existingUrls))
            {
                Profiles.Add(profile);
            }

            if (Profiles.Count > 0)
            {
                ProfileGrid.SelectedIndex = 0;
            }

            StatusText.Text = Profiles.Count == 0
                ? "Chrome/Edge のブックマークファイルが見つかりませんでした"
                : $"{Profiles.Count} 件のプロファイルを見つけました";
        }
        catch (Exception ex)
        {
            StatusText.Text = "ブックマークを読み込めません: " + ex.Message;
        }
    }

    private static IReadOnlyList<BrowserBookmarkProfile> LoadProfiles(HashSet<string> existingUrls)
    {
        var profiles = new List<BrowserBookmarkProfile>();

        foreach (var root in GetBrowserRoots())
        {
            if (!Directory.Exists(root.UserDataRoot))
            {
                continue;
            }

            foreach (var profileDirectory in Directory.EnumerateDirectories(root.UserDataRoot))
            {
                var bookmarkFile = Path.Combine(profileDirectory, "Bookmarks");
                if (!File.Exists(bookmarkFile))
                {
                    continue;
                }

                var profile = TryLoadProfile(root.BrowserName, Path.GetFileName(profileDirectory), bookmarkFile, existingUrls);
                if (profile is not null && profile.TotalCount > 0)
                {
                    profiles.Add(profile);
                }
            }
        }

        return profiles
            .OrderBy(static x => x.BrowserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static x => x.ProfileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static BrowserBookmarkProfile? TryLoadProfile(
        string browserName,
        string profileName,
        string bookmarkFile,
        HashSet<string> existingUrls)
    {
        try
        {
            using var stream = File.OpenRead(bookmarkFile);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("roots", out var roots)
                || roots.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var imported = new List<BookmarkItem>();
            foreach (var root in roots.EnumerateObject())
            {
                var rootPath = GetString(root.Value, "name") is null
                    ? CreateFolderPath([], root.Name)
                    : [];
                CollectBookmarks(root.Value, imported, rootPath);
            }

            var uniqueImported = imported
                .Where(static x => !string.IsNullOrWhiteSpace(x.Url))
                .GroupBy(static x => BookmarkService.NormalizeUrl(x.Url), BookmarkService.UrlComparer)
                .Select(static x => x.First())
                .ToList();

            var newBookmarks = uniqueImported
                .Where(x => !existingUrls.Contains(BookmarkService.NormalizeUrl(x.Url)))
                .ToList();

            return new BrowserBookmarkProfile(
                browserName,
                profileName,
                bookmarkFile,
                uniqueImported.Count,
                uniqueImported.Count - newBookmarks.Count,
                newBookmarks);
        }
        catch
        {
            return null;
        }
    }

    private static void CollectBookmarks(JsonElement node, List<BookmarkItem> bookmarks, IReadOnlyList<string> folderPath)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = GetString(node, "type");
        if (string.Equals(type, "url", StringComparison.OrdinalIgnoreCase))
        {
            var url = GetString(node, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            bookmarks.Add(new BookmarkItem
            {
                Url = BookmarkService.NormalizeUrl(url),
                Title = GetString(node, "name") ?? url,
                FolderPath = BookmarkService.NormalizeFolderPath(string.Join(" / ", folderPath)),
                CreatedAt = ParseChromeTime(GetString(node, "date_added")) ?? DateTimeOffset.Now
            });
            return;
        }

        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var childFolderPath = folderPath;
        if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
        {
            childFolderPath = CreateFolderPath(folderPath, GetString(node, "name"));
        }

        foreach (var child in children.EnumerateArray())
        {
            CollectBookmarks(child, bookmarks, childFolderPath);
        }
    }

    private static IReadOnlyList<string> CreateFolderPath(IReadOnlyList<string> folderPath, string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return folderPath;
        }

        return [.. folderPath, folderName.Trim()];
    }

    private static IEnumerable<BrowserRoot> GetBrowserRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return new BrowserRoot("Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        yield return new BrowserRoot("Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
    }

    private static DateTimeOffset? ParseChromeTime(string? value)
    {
        if (!long.TryParse(value, out var microseconds) || microseconds <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddTicks(microseconds * 10)
                .ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private sealed record BrowserRoot(string BrowserName, string UserDataRoot);

    public sealed class BrowserBookmarkProfile(
        string browserName,
        string profileName,
        string bookmarkFile,
        int totalCount,
        int duplicateCount,
        IReadOnlyList<BookmarkItem> newBookmarks)
    {
        public string BrowserName { get; } = browserName;

        public string ProfileName { get; } = profileName;

        public string BookmarkFile { get; } = bookmarkFile;

        public int TotalCount { get; } = totalCount;

        public int DuplicateCount { get; } = duplicateCount;

        public int NewCount => NewBookmarks.Count;

        public IReadOnlyList<BookmarkItem> NewBookmarks { get; } = newBookmarks;
    }
}
