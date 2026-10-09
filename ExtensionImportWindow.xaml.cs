using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabBrowser;

public partial class ExtensionImportWindow : Window
{
    public ObservableCollection<ExtensionImportCandidate> Candidates { get; } = [];

    public string? SelectedExtensionFolder { get; private set; }

    public bool IsAccepted { get; private set; }

    public ExtensionImportWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshCandidates();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCandidates();
    }

    private void CandidateGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectedView();
    }

    private void CandidateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ImportSelectedCandidate();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportSelectedCandidate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshCandidates()
    {
        try
        {
            Candidates.Clear();

            foreach (var candidate in LoadCandidates())
            {
                Candidates.Add(candidate);
            }

            if (Candidates.Count > 0)
            {
                CandidateGrid.SelectedIndex = 0;
            }

            UpdateSelectedView();
            StatusText.Text = Candidates.Count == 0
                ? "Chrome/Edge の拡張機能が見つかりませんでした"
                : $"{Candidates.Count} 件の拡張機能候補を見つけました";
        }
        catch (Exception ex)
        {
            StatusText.Text = "拡張機能候補を取得できません: " + ex.Message;
        }
    }

    private void ImportSelectedCandidate()
    {
        if (CandidateGrid.SelectedItem is not ExtensionImportCandidate candidate)
        {
            StatusText.Text = "取り込む拡張機能を選択してください";
            return;
        }

        SelectedExtensionFolder = candidate.ExtensionFolder;
        IsAccepted = true;
        Close();
    }

    private void UpdateSelectedView()
    {
        if (CandidateGrid.SelectedItem is ExtensionImportCandidate candidate)
        {
            SelectedNameText.Text = $"{candidate.Name} ({candidate.BrowserName} / {candidate.ProfileName})";
            SelectedPathText.Text = candidate.ExtensionFolder;
            SelectedDescriptionText.Text = string.IsNullOrWhiteSpace(candidate.Description)
                ? "説明はありません"
                : candidate.Description;
            ImportButton.IsEnabled = true;
            return;
        }

        SelectedNameText.Text = "未選択";
        SelectedPathText.Text = "一覧から取り込む拡張機能を選択してください";
        SelectedDescriptionText.Text = string.Empty;
        ImportButton.IsEnabled = false;
    }

    private static IReadOnlyList<ExtensionImportCandidate> LoadCandidates()
    {
        var candidates = new List<ExtensionImportCandidate>();

        foreach (var root in GetBrowserRoots())
        {
            if (!Directory.Exists(root.UserDataRoot))
            {
                continue;
            }

            foreach (var profileDirectory in Directory.EnumerateDirectories(root.UserDataRoot))
            {
                var extensionsRoot = Path.Combine(profileDirectory, "Extensions");
                if (!Directory.Exists(extensionsRoot))
                {
                    continue;
                }

                var profileName = Path.GetFileName(profileDirectory);
                foreach (var idDirectory in Directory.EnumerateDirectories(extensionsRoot))
                {
                    var candidate = LoadLatestCandidate(root.BrowserName, profileName, idDirectory);
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates
            .OrderBy(static x => x.BrowserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static x => x.ProfileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ExtensionImportCandidate? LoadLatestCandidate(
        string browserName,
        string profileName,
        string idDirectory)
    {
        var versionCandidates = Directory
            .EnumerateDirectories(idDirectory)
            .Select(versionDirectory =>
            {
                var manifestPath = Path.Combine(versionDirectory, "manifest.json");
                var manifest = File.Exists(manifestPath)
                    ? ExtensionManifestInfo.TryLoadManifest(manifestPath)
                    : null;

                return manifest is null
                    ? null
                    : new
                    {
                        Directory = versionDirectory,
                        LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(versionDirectory),
                        Manifest = manifest
                    };
            })
            .Where(static x => x is not null)
            .Select(static x => x!)
            .OrderByDescending(static x => TryParseVersion(x.Manifest.Version), ExtensionVersionComparer.Instance)
            .ThenByDescending(static x => x.LastWriteTimeUtc)
            .ToList();

        var latest = versionCandidates.FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        return new ExtensionImportCandidate(
            browserName,
            profileName,
            Path.GetFileName(idDirectory),
            latest.Manifest,
            latest.Directory);
    }

    private static IEnumerable<BrowserExtensionRoot> GetBrowserRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return new BrowserExtensionRoot(
            "Chrome",
            Path.Combine(localAppData, "Google", "Chrome", "User Data"));
        yield return new BrowserExtensionRoot(
            "Edge",
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
    }

    private static Version TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Version(0, 0);
        }

        var parts = value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? Math.Max(0, number)
                : 0)
            .Take(4)
            .ToList();

        while (parts.Count < 2)
        {
            parts.Add(0);
        }

        return parts.Count switch
        {
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };
    }

    private static ImageSource? LoadExtensionIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 24;
            image.UriSource = new Uri(iconPath);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private sealed record BrowserExtensionRoot(string BrowserName, string UserDataRoot);

    private sealed class ExtensionVersionComparer : IComparer<Version>
    {
        public static ExtensionVersionComparer Instance { get; } = new();

        public int Compare(Version? x, Version? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return x.CompareTo(y);
        }
    }

    public sealed class ExtensionImportCandidate
    {
        public ExtensionImportCandidate(
            string browserName,
            string profileName,
            string extensionId,
            ExtensionManifestInfo manifest,
            string extensionFolder)
        {
            BrowserName = browserName;
            ProfileName = profileName;
            ExtensionId = extensionId;
            Name = manifest.Name;
            VersionText = string.IsNullOrWhiteSpace(manifest.Version) ? "-" : manifest.Version;
            Description = manifest.Description;
            ExtensionFolder = extensionFolder;
            Icon = LoadExtensionIcon(manifest.IconPath);
        }

        public string BrowserName { get; }

        public string ProfileName { get; }

        public string ExtensionId { get; }

        public string Name { get; }

        public string VersionText { get; }

        public string? Description { get; }

        public string ExtensionFolder { get; }

        public ImageSource? Icon { get; }

        public bool HasIcon => Icon is not null;

        public string Initial
        {
            get
            {
                var trimmed = Name.Trim();
                return trimmed.Length == 0
                    ? "?"
                    : trimmed[0].ToString().ToUpper(CultureInfo.CurrentCulture);
            }
        }
    }
}
