using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Services;

namespace TabBrowser;

public partial class ExtensionManagerWindow : Window
{
    private readonly CoreWebView2Profile _profile;
    private readonly Action<string> _openExtensionPage;
    private readonly IReadOnlyList<ExtensionManifestInfo> _knownManifests;

    public ObservableCollection<BrowserExtensionItem> Extensions { get; } = [];

    public ExtensionManagerWindow(CoreWebView2Profile profile, Action<string> openExtensionPage)
    {
        InitializeComponent();

        _profile = profile;
        _openExtensionPage = openExtensionPage;
        _knownManifests = ExtensionManifestInfo.LoadKnownManifests();
        DataContext = this;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshExtensionsAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshExtensionsAsync();
    }

    private void ExtensionGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectedExtensionView();
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItem();
        if (item is null)
        {
            return;
        }

        var settingsUrl = item.GetSettingsUrl();
        if (string.IsNullOrWhiteSpace(settingsUrl))
        {
            SetStatus("設定ページを特定できませんでした");
            return;
        }

        _openExtensionPage(settingsUrl);
        Close();
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItem();
        if (item is null)
        {
            return;
        }

        try
        {
            await item.Extension.EnableAsync(!item.IsEnabled);
            item.IsEnabled = item.Extension.IsEnabled;
            UpdateSelectedExtensionView();
            SetStatus($"{item.Name} を {item.EnabledText} にしました");
        }
        catch (Exception ex)
        {
            SetStatus("有効状態を変更できません: " + ex.Message);
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItem();
        if (item is null)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"{item.Name} を削除しますか?",
            "拡張機能の削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await item.Extension.RemoveAsync();
            Extensions.Remove(item);
            UpdateSelectedExtensionView();
            SetStatus($"{item.Name} を削除しました");
        }
        catch (Exception ex)
        {
            SetStatus("削除できません: " + ex.Message);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RefreshExtensionsAsync()
    {
        try
        {
            Extensions.Clear();

            var extensions = await _profile.GetBrowserExtensionsAsync();
            foreach (var extension in extensions.OrderBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Extensions.Add(new BrowserExtensionItem(extension, FindManifestInfo(extension)));
            }

            SetStatus(Extensions.Count == 0
                ? "読み込み済み拡張機能はありません"
                : $"{Extensions.Count} 件の拡張機能を読み込みました");

            if (Extensions.Count > 0 && ExtensionGrid.SelectedItem is null)
            {
                ExtensionGrid.SelectedIndex = 0;
            }

            UpdateSelectedExtensionView();
        }
        catch (Exception ex)
        {
            SetStatus("拡張機能一覧を取得できません: " + ex.Message);
        }
    }

    private BrowserExtensionItem? GetSelectedItem()
    {
        if (ExtensionGrid.SelectedItem is BrowserExtensionItem item)
        {
            return item;
        }

        SetStatus("拡張機能を選択してください");
        return null;
    }

    private void UpdateSelectedExtensionView()
    {
        if (ExtensionGrid.SelectedItem is BrowserExtensionItem item)
        {
            SelectedNameText.Text = item.Name;
            SelectedIdText.Text = item.Id;
            SelectedStateText.Text = item.EnabledText;
            SelectedStateText.Foreground = item.IsEnabled
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.LightGray;
            OpenSettingsButton.IsEnabled = true;
            ToggleExtensionButton.IsEnabled = true;
            RemoveExtensionButton.IsEnabled = true;
            return;
        }

        SelectedNameText.Text = "未選択";
        SelectedIdText.Text = "一覧から拡張機能を選択してください";
        SelectedStateText.Text = "-";
        SelectedStateText.Foreground = System.Windows.Media.Brushes.LightGray;
        OpenSettingsButton.IsEnabled = false;
        ToggleExtensionButton.IsEnabled = false;
        RemoveExtensionButton.IsEnabled = false;
    }

    private ExtensionManifestInfo? FindManifestInfo(CoreWebView2BrowserExtension extension)
    {
        return ExtensionManifestInfo.FindByName(_knownManifests, extension.Name);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

}

public sealed class BrowserExtensionItem : INotifyPropertyChanged
{
    private readonly ExtensionManifestInfo? _manifestInfo;
    private bool _isEnabled;

    public BrowserExtensionItem(CoreWebView2BrowserExtension extension, ExtensionManifestInfo? manifestInfo)
    {
        Extension = extension;
        _manifestInfo = manifestInfo;
        Icon = LoadExtensionIcon(manifestInfo?.IconPath);
        _isEnabled = extension.IsEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CoreWebView2BrowserExtension Extension { get; }

    public string Id => Extension.Id;

    public string Name => Extension.Name;

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

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledText)));
        }
    }

    public string EnabledText => IsEnabled ? "ON" : "OFF";

    public string? GetPopupUrl()
    {
        return ExtensionManifestInfo.BuildExtensionUrl(Id, _manifestInfo?.DefaultPopupPage);
    }

    public string? GetSettingsUrl()
    {
        var page = _manifestInfo?.OptionsPage;
        if (string.IsNullOrWhiteSpace(page) && Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase))
        {
            page = "dashboard.html";
        }

        if (string.IsNullOrWhiteSpace(page))
        {
            page = "options.html";
        }

        return ExtensionManifestInfo.BuildExtensionUrl(Id, page);
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
}

public sealed class ExtensionManifestInfo
{
    private ExtensionManifestInfo(
        string name,
        string? description,
        string? version,
        string? optionsPage,
        string? defaultPopupPage,
        string? iconPath,
        string sourceDirectory)
    {
        Name = name;
        Description = description;
        Version = version;
        OptionsPage = optionsPage;
        DefaultPopupPage = defaultPopupPage;
        IconPath = iconPath;
        SourceDirectory = sourceDirectory;
    }

    public string Name { get; }

    public string? Description { get; }

    public string? Version { get; }

    public string? OptionsPage { get; }

    public string? DefaultPopupPage { get; }

    public string? IconPath { get; }

    public string SourceDirectory { get; }

    public static ExtensionManifestInfo? FindByName(
        IEnumerable<ExtensionManifestInfo> manifests,
        string extensionName)
    {
        var normalizedName = NormalizeName(extensionName);
        return manifests.FirstOrDefault(x => NormalizeName(x.Name) == normalizedName);
    }

    public static string? BuildExtensionUrl(string id, string? page)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(page))
        {
            return null;
        }

        return $"chrome-extension://{id}/{page.TrimStart('/')}";
    }

    public static IReadOnlyList<ExtensionManifestInfo> LoadKnownManifests()
    {
        var roots = GetCandidateExtensionRoots();
        var manifests = new List<ExtensionManifestInfo>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories))
            {
                var manifest = TryLoadManifest(manifestPath);
                if (manifest is not null)
                {
                    manifests.Add(manifest);
                }
            }
        }

        return manifests;
    }

    public static ExtensionManifestInfo? TryLoadManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var extensionRoot = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(extensionRoot))
            {
                return null;
            }

            var defaultLocale = GetString(root, "default_locale");
            var name = ResolveManifestText(GetString(root, "name"), extensionRoot, defaultLocale);
            var description = ResolveManifestText(GetString(root, "description"), extensionRoot, defaultLocale);

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var version = GetString(root, "version");
            var optionsPage = GetString(root, "options_page");
            if (string.IsNullOrWhiteSpace(optionsPage)
                && root.TryGetProperty("options_ui", out var optionsUi)
                && optionsUi.ValueKind == JsonValueKind.Object)
            {
                optionsPage = GetString(optionsUi, "page");
            }

            var defaultPopupPage = GetActionString(root, "default_popup");
            var iconPath = FindIconPath(root, extensionRoot);

            return new ExtensionManifestInfo(
                name,
                description,
                version,
                optionsPage,
                defaultPopupPage,
                iconPath,
                extensionRoot);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetCandidateExtensionRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, Path.Combine(Environment.CurrentDirectory, "extensions"));
        AddRoot(roots, Path.Combine(AppContext.BaseDirectory, "extensions"));
        AddRoot(roots, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "extensions")));
        AddRoot(roots, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "extensions")));
        AddRoot(roots, Path.Combine(AppPaths.WebView2UserDataFolder, "Default", "Extensions"));
        AddRoot(roots, Path.Combine(AppPaths.WebView2UserDataFolder, "EBWebView", "Default", "Extensions"));
        return roots;
    }

    private static void AddRoot(HashSet<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(path);
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

    private static string NormalizeName(string value)
    {
        return value.Trim().ToUpper(CultureInfo.InvariantCulture);
    }

    private static string? ResolveManifestText(string? value, string extensionRoot, string? defaultLocale)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        const string prefix = "__MSG_";
        const string suffix = "__";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var key = value[prefix.Length..^suffix.Length];
        foreach (var locale in GetLocaleCandidates(defaultLocale))
        {
            var resolved = TryResolveLocaleMessage(extensionRoot, locale, key);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetLocaleCandidates(string? defaultLocale)
    {
        if (!string.IsNullOrWhiteSpace(defaultLocale))
        {
            yield return defaultLocale;
        }

        yield return CultureInfo.CurrentUICulture.Name.Replace('-', '_');
        yield return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        yield return "en";
    }

    private static string? TryResolveLocaleMessage(string extensionRoot, string locale, string key)
    {
        try
        {
            var messagePath = Path.Combine(extensionRoot, "_locales", locale, "messages.json");
            if (!File.Exists(messagePath))
            {
                return null;
            }

            using var stream = File.OpenRead(messagePath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty(key, out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                return GetString(message, "message");
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? GetActionString(JsonElement root, string propertyName)
    {
        foreach (var actionName in new[] { "action", "browser_action" })
        {
            if (root.TryGetProperty(actionName, out var action)
                && action.ValueKind == JsonValueKind.Object
                && GetString(action, propertyName) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindIconPath(JsonElement root, string extensionRoot)
    {
        foreach (var actionName in new[] { "action", "browser_action" })
        {
            if (root.TryGetProperty(actionName, out var action)
                && action.ValueKind == JsonValueKind.Object
                && action.TryGetProperty("default_icon", out var defaultIcon)
                && ResolveIconPath(defaultIcon, extensionRoot) is { } actionIconPath)
            {
                return actionIconPath;
            }
        }

        return root.TryGetProperty("icons", out var icons)
            ? ResolveIconPath(icons, extensionRoot)
            : null;
    }

    private static string? ResolveIconPath(JsonElement iconElement, string extensionRoot)
    {
        if (iconElement.ValueKind == JsonValueKind.String)
        {
            return ResolveExtensionResourcePath(extensionRoot, iconElement.GetString());
        }

        if (iconElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var candidates = iconElement
            .EnumerateObject()
            .Select(property => new
            {
                Size = int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                    ? size
                    : 0,
                Value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .OrderByDescending(x => x.Size)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (ResolveExtensionResourcePath(extensionRoot, candidate.Value) is { } iconPath)
            {
                return iconPath;
            }
        }

        return null;
    }

    private static string? ResolveExtensionResourcePath(string extensionRoot, string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(extensionRoot);
        var relativePath = resourcePath.TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || !IsSupportedIconFile(fullPath))
        {
            return null;
        }

        return fullPath;
    }

    private static bool IsSupportedIconFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
