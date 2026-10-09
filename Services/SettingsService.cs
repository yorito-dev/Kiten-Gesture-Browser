using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsFile;
    private readonly string _settingsBackupFile;

    public SettingsService()
        : this(AppPaths.SettingsFile)
    {
    }

    public SettingsService(string settingsFile)
    {
        _settingsFile = settingsFile;
        _settingsBackupFile = AtomicFileService.CreateBackupFilePath(settingsFile);
    }

    public async Task<AppSettings> LoadAsync()
    {
        EnsureStorage();
        var primaryExists = File.Exists(_settingsFile);
        if (primaryExists)
        {
            try
            {
                if (await LoadFromFileAsync(_settingsFile) is { } primarySettings)
                {
                    return primarySettings;
                }
            }
            catch
            {
                // backupを試した後に、既存の部分回復へ進む。
            }
        }

        var backupSettings = await TryLoadBackupAsync();
        if (backupSettings is not null)
        {
            await TryRepairPrimaryFromBackupAsync(backupSettings, primaryExists);
            return backupSettings;
        }

        if (!primaryExists)
        {
            var defaults = new AppSettings();
            await SaveToFileAsync(_settingsFile, defaults, _settingsBackupFile);
            return defaults;
        }

        return LoadRecoveredSettings(_settingsFile);
    }

    public AppSettings Load()
    {
        EnsureStorage();
        var primaryExists = File.Exists(_settingsFile);
        if (primaryExists)
        {
            try
            {
                if (LoadFromFile(_settingsFile) is { } primarySettings)
                {
                    return primarySettings;
                }
            }
            catch
            {
                // backupを試した後に、既存の部分回復へ進む。
            }
        }

        var backupSettings = TryLoadBackup();
        if (backupSettings is not null)
        {
            TryRepairPrimaryFromBackup(backupSettings, primaryExists);
            return backupSettings;
        }

        if (!primaryExists)
        {
            var defaults = new AppSettings();
            SaveToFile(_settingsFile, defaults, _settingsBackupFile);
            return defaults;
        }

        return LoadRecoveredSettings(_settingsFile);
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        AppPaths.Ensure();
        await SaveToFileAsync(
            AppPaths.SettingsFile,
            settings,
            AtomicFileService.CreateBackupFilePath(AppPaths.SettingsFile));
    }

    public static void Save(AppSettings settings)
    {
        AppPaths.Ensure();
        SaveToFile(AppPaths.SettingsFile, settings, AtomicFileService.CreateBackupFilePath(AppPaths.SettingsFile));
    }

    internal static Action CaptureSaveOperation(AppSettings settings, string? filePath = null)
    {
        var snapshot = JsonSerializer.Serialize(settings, JsonOptions);
        filePath ??= AppPaths.SettingsFile;
        return () =>
        {
            if (HasSameFileContent(filePath, snapshot))
            {
                return;
            }

            AtomicFileService.Write(
                filePath,
                AtomicFileService.CreateBackupFilePath(filePath),
                stream =>
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(snapshot);
                });
        };
    }

    private static bool HasSameFileContent(string filePath, string content)
    {
        try
        {
            return File.Exists(filePath)
                && string.Equals(File.ReadAllText(filePath), content, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task SaveToFileAsync(string filePath, AppSettings settings)
    {
        await SaveToFileAsync(filePath, settings, AtomicFileService.CreateBackupFilePath(filePath));
    }

    public static void SaveToFile(string filePath, AppSettings settings)
    {
        SaveToFile(filePath, settings, AtomicFileService.CreateBackupFilePath(filePath));
    }

    private async Task<AppSettings?> TryLoadBackupAsync()
    {
        try
        {
            return File.Exists(_settingsBackupFile)
                ? await LoadFromFileAsync(_settingsBackupFile)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private AppSettings? TryLoadBackup()
    {
        try
        {
            return File.Exists(_settingsBackupFile)
                ? LoadFromFile(_settingsBackupFile)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryRepairPrimaryFromBackupAsync(AppSettings backupSettings, bool primaryExists)
    {
        try
        {
            if (primaryExists)
            {
                BackupBrokenSettingsFile(_settingsFile);
            }

            await SaveToFileAsync(_settingsFile, backupSettings, backupFilePath: null);
            AppendRecoveryDiagnostic(_settingsFile, "backup-loaded-primary-repaired");
        }
        catch
        {
            // 読み込めたbackupを返し、backupと既存本体を変更しない。修復保存は次回再試行できる。
            AppendRecoveryDiagnostic(_settingsFile, "backup-loaded-primary-repair-failed");
        }
    }

    private void TryRepairPrimaryFromBackup(AppSettings backupSettings, bool primaryExists)
    {
        try
        {
            if (primaryExists)
            {
                BackupBrokenSettingsFile(_settingsFile);
            }

            SaveToFile(_settingsFile, backupSettings, backupFilePath: null);
            AppendRecoveryDiagnostic(_settingsFile, "backup-loaded-primary-repaired");
        }
        catch
        {
            // 読み込めたbackupを返し、backupと既存本体を変更しない。修復保存は次回再試行できる。
            AppendRecoveryDiagnostic(_settingsFile, "backup-loaded-primary-repair-failed");
        }
    }

    private static async Task<AppSettings?> LoadFromFileAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        return NormalizeLoadedSettings(await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions));
    }

    private static AppSettings? LoadFromFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return NormalizeLoadedSettings(JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions));
    }

    private static AppSettings? NormalizeLoadedSettings(AppSettings? settings)
    {
        if (settings is not null)
        {
            settings.SearchAliases = SearchAliasSettingsProfile.Normalize(settings.SearchAliases);
            settings.FolderTabViewMode = FolderTabViewMode.Normalize(settings.FolderTabViewMode);
            settings.FolderTabSortMode = FolderTabSortMode.Normalize(settings.FolderTabSortMode);
            settings.FolderTabSortDirection = FolderTabSortDirection.Normalize(settings.FolderTabSortDirection);
            settings.FolderTabSortPreferences = FolderTabSortPreferenceSettingsProfile.Normalize(
                settings.FolderTabSortPreferences);
            settings.FolderTabPinnedPlaces = FolderTabPlacesService.NormalizePinnedPlaces(
                settings.FolderTabPinnedPlaces);
            settings.ClipboardHistory = ClipboardHistorySettingsProfile.Normalize(settings.ClipboardHistory);
            settings.BrowsingHistory = BrowsingHistorySettingsProfile.Normalize(settings.BrowsingHistory);
            settings.SitePrivacy = SitePrivacySettingsProfile.Normalize(settings.SitePrivacy);
            settings.QuickLook = QuickLookSettingsProfile.Normalize(settings.QuickLook);
            var miniPlayerQualityThresholds = YouTubeMiniPlayerQualitySettingsProfile.Normalize(
                settings.YouTubeMiniPlayerQualityLowWidthThreshold,
                settings.YouTubeMiniPlayerQualityHighWidthThreshold);
            settings.YouTubeMiniPlayerQualityLowWidthThreshold =
                miniPlayerQualityThresholds.LowWidthThreshold;
            settings.YouTubeMiniPlayerQualityHighWidthThreshold =
                miniPlayerQualityThresholds.HighWidthThreshold;
            settings.WorkspaceNoteEditorWindowPlacement = WorkspaceNoteEditorWindowPlacementProfile.Normalize(
                settings.WorkspaceNoteEditorWindowPlacement);
            settings.WorkspaceNoteEditorWindowPlacements = WorkspaceNoteEditorWindowPlacementProfile.Normalize(
                settings.WorkspaceNoteEditorWindowPlacements);
            settings.WorkspaceFolderWindowPlacements = WorkspaceFolderWindowPlacementProfile.Normalize(
                settings.WorkspaceFolderWindowPlacements);
            settings.PageZoomPreferences = PageZoomPreferenceSettingsProfile.Normalize(
                settings.PageZoomPreferences);
        }

        return settings;
    }

    private static async Task SaveToFileAsync(string filePath, AppSettings settings, string? backupFilePath)
    {
        await AtomicFileService.WriteAsync(
            filePath,
            backupFilePath,
            async stream => await JsonSerializer.SerializeAsync(stream, settings, JsonOptions));
    }

    private static void SaveToFile(string filePath, AppSettings settings, string? backupFilePath)
    {
        AtomicFileService.Write(
            filePath,
            backupFilePath,
            stream => JsonSerializer.Serialize(stream, settings, JsonOptions));
    }

    private static AppSettings LoadRecoveredSettings(string settingsFile)
    {
        var settings = new AppSettings();
        try
        {
            var text = File.Exists(settingsFile)
                ? File.ReadAllText(settingsFile)
                : string.Empty;

            settings.PreservedCompatibilitySettings = RecoverCompatibilitySettings(text, PublicSettingsCompatibility.RootKeys);
            settings.ToolbarVisibility.PreservedCompatibilitySettings = RecoverCompatibilitySettings(
                ExtractObjectSection(text, "toolbarVisibility"), PublicSettingsCompatibility.ToolbarKeys);

            if (TryExtractBooleanSetting(text, "enableFocusedViewingCpuSaver", out var enableFocusedViewingCpuSaver))
            {
                settings.EnableFocusedViewingCpuSaver = enableFocusedViewingCpuSaver;
            }

            if (TryExtractBooleanSetting(text, "enableInactiveTabMemorySaver", out var enableInactiveTabMemorySaver))
            {
                settings.EnableInactiveTabMemorySaver = enableInactiveTabMemorySaver;
            }

            if (TryExtractBooleanSetting(text, "enableBrowserExtensions", out var enableBrowserExtensions))
            {
                settings.EnableBrowserExtensions = enableBrowserExtensions;
            }

            if (TryExtractBooleanSetting(text, "enableXReadableMode", out var enableXReadableMode))
            {
                settings.EnableXReadableMode = enableXReadableMode;
            }

            if (TryExtractBooleanSetting(text, "alwaysOnTop", out var alwaysOnTop))
            {
                settings.AlwaysOnTop = alwaysOnTop;
            }

            if (TryExtractBooleanSetting(text, "preserveContextMenuOnShortRightClick", out var preserveContextMenu))
            {
                settings.PreserveContextMenuOnShortRightClick = preserveContextMenu;
            }

            if (TryExtractDoubleSetting(text, "gestureStartThreshold", out var gestureStartThreshold))
            {
                settings.GestureStartThreshold = gestureStartThreshold;
            }

            if (TryExtractDoubleSetting(text, "gestureDirectionThreshold", out var gestureDirectionThreshold))
            {
                settings.GestureDirectionThreshold = gestureDirectionThreshold;
            }

            if (TryExtractDoubleSetting(text, "focusedViewingCpuSaverDelaySeconds", out var cpuSaverDelay))
            {
                settings.FocusedViewingCpuSaverDelaySeconds = cpuSaverDelay;
            }

            var searchEngine = ExtractStringSetting(text, "searchEngine");
            if (!string.IsNullOrWhiteSpace(searchEngine))
            {
                settings.SearchEngine = searchEngine;
            }

            var searchUrlTemplate = ExtractStringSetting(text, "searchUrlTemplate");
            if (!string.IsNullOrWhiteSpace(searchUrlTemplate))
            {
                settings.SearchUrlTemplate = searchUrlTemplate;
            }

            if (TryExtractDownloadSettings(text, out var downloadSettings))
            {
                settings.Downloads = downloadSettings;
            }

            if (TryExtractTabLayoutSettings(text, out var tabLayoutSettings))
            {
                settings.TabLayout = tabLayoutSettings;
            }

            if (TryExtractGuidSetting(text, "lastWorkspaceId", out var lastWorkspaceId))
            {
                settings.LastWorkspaceId = lastWorkspaceId;
            }

            if (TryExtractWindowPlacement(text, out var windowPlacement))
            {
                settings.WindowPlacement = windowPlacement;
            }

            if (TryExtractBookmarkManagerWindowSize(text, out var bookmarkManagerWindowSize))
            {
                settings.BookmarkManagerWindowSize = bookmarkManagerWindowSize;
            }

            if (TryExtractWorkspaceNoteEditorWindowPlacement(text, out var workspaceNoteEditorWindowPlacement))
            {
                settings.WorkspaceNoteEditorWindowPlacement = workspaceNoteEditorWindowPlacement;
            }

            BackupBrokenSettingsFile(settingsFile);
            SaveToFile(settingsFile, settings, backupFilePath: null);
            AppendRecoveryDiagnostic(settingsFile, "partial-settings-recovered");
        }
        catch
        {
            // 破損設定から復旧できない場合は既定設定で起動する。
        }

        return settings;
    }

    private static Dictionary<string, JsonElement>? RecoverCompatibilitySettings(string text, string[] keys)
    {
        var recovered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            // 旧設定のscalarだけを救済し、不完全なJSON値や一時検証情報を保存しない。
            var scalar = """("(?:\\.|[^"\\])*"|true|false|null)(?=\s*[,}])""";
            var match = System.Text.RegularExpressions.Regex.Match(
                text, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*" + scalar);
            if (!match.Success)
            {
                continue;
            }

            try
            {
                using var value = JsonDocument.Parse(match.Groups[1].Value);
                recovered[key] = value.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 救済できない値は原本のbroken backupに残す。
            }
        }

        return recovered.Count == 0 ? null : recovered;
    }

    private static bool TryExtractBooleanSetting(string text, string propertyName, out bool value)
    {
        value = false;
        var marker = "\"" + propertyName + "\"";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var colonIndex = text.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0)
        {
            return false;
        }

        var valueText = text[(colonIndex + 1)..].TrimStart();
        if (valueText.StartsWith("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (valueText.StartsWith("false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool TryExtractDoubleSetting(string text, string propertyName, out double value)
    {
        value = 0;
        var rawValue = ExtractRawSettingValue(text, propertyName);
        return !string.IsNullOrWhiteSpace(rawValue)
            && double.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExtractGuidSetting(string text, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        var rawValue = ExtractStringSetting(text, propertyName);
        return Guid.TryParse(rawValue, out value);
    }

    private static bool TryExtractIntSetting(string text, string propertyName, out int value)
    {
        value = 0;
        var rawValue = ExtractRawSettingValue(text, propertyName);
        return !string.IsNullOrWhiteSpace(rawValue)
            && int.TryParse(rawValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExtractDownloadSettings(string text, out DownloadSettings settings)
    {
        settings = new DownloadSettings();
        var section = ExtractObjectSection(text, "downloads");
        if (string.IsNullOrWhiteSpace(section))
        {
            return false;
        }

        var defaultSaveDirectory = ExtractStringSetting(section, "defaultSaveDirectory");
        if (!string.IsNullOrWhiteSpace(defaultSaveDirectory))
        {
            settings.DefaultSaveDirectory = defaultSaveDirectory;
        }

        if (TryExtractBooleanSetting(section, "askSaveLocation", out var askSaveLocation))
        {
            settings.AskSaveLocation = askSaveLocation;
        }

        if (TryExtractBooleanSetting(section, "openShelfOnDownloadStart", out var openShelfOnDownloadStart))
        {
            settings.OpenShelfOnDownloadStart = openShelfOnDownloadStart;
        }

        if (TryExtractBooleanSetting(section, "openShelfOnDownloadCompleted", out var openShelfOnDownloadCompleted))
        {
            settings.OpenShelfOnDownloadCompleted = openShelfOnDownloadCompleted;
        }

        if (TryExtractIntSetting(section, "historyRetentionLimit", out var historyRetentionLimit))
        {
            settings.HistoryRetentionLimit = historyRetentionLimit;
        }

        settings = DownloadSettingsProfile.Normalize(settings);
        return true;
    }

    private static bool TryExtractTabLayoutSettings(string text, out TabLayoutSettings settings)
    {
        settings = new TabLayoutSettings();
        var section = ExtractObjectSection(text, "tabLayout");
        if (string.IsNullOrWhiteSpace(section))
        {
            return false;
        }

        var mode = ExtractStringSetting(section, "mode");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            settings.Mode = mode;
        }

        if (TryExtractDoubleSetting(section, "verticalSidebarWidth", out var verticalSidebarWidth))
        {
            settings.VerticalSidebarWidth = verticalSidebarWidth;
        }

        if (TryExtractBooleanSetting(section, "verticalSidebarCollapsed", out var verticalSidebarCollapsed))
        {
            settings.VerticalSidebarCollapsed = verticalSidebarCollapsed;
        }

        if (TryExtractBooleanSetting(section, "showTabSearch", out var showTabSearch))
        {
            settings.ShowTabSearch = showTabSearch;
        }

        if (TryExtractBooleanSetting(section, "showPinnedTabsSection", out var showPinnedTabsSection))
        {
            settings.ShowPinnedTabsSection = showPinnedTabsSection;
        }

        settings = TabLayoutSettingsProfile.Normalize(settings);
        return true;
    }

    private static bool TryExtractWindowPlacement(string text, out WindowPlacementSettings placement)
    {
        placement = new WindowPlacementSettings();
        var section = ExtractObjectSection(text, "windowPlacement");
        if (string.IsNullOrWhiteSpace(section))
        {
            return false;
        }

        if (TryExtractDoubleSetting(section, "left", out var left))
        {
            placement.Left = left;
        }

        if (TryExtractDoubleSetting(section, "top", out var top))
        {
            placement.Top = top;
        }

        if (TryExtractDoubleSetting(section, "width", out var width))
        {
            placement.Width = width;
        }

        if (TryExtractDoubleSetting(section, "height", out var height))
        {
            placement.Height = height;
        }

        var state = ExtractStringSetting(section, "state");
        if (!string.IsNullOrWhiteSpace(state))
        {
            placement.State = state;
        }

        return placement.Width > 0 && placement.Height > 0;
    }

    private static bool TryExtractBookmarkManagerWindowSize(
        string text,
        out BookmarkManagerWindowSizeSettings windowSize)
    {
        windowSize = new BookmarkManagerWindowSizeSettings();
        var section = ExtractObjectSection(text, "bookmarkManagerWindowSize");
        if (string.IsNullOrWhiteSpace(section))
        {
            return false;
        }

        if (TryExtractDoubleSetting(section, "width", out var width))
        {
            windowSize.Width = width;
        }

        if (TryExtractDoubleSetting(section, "height", out var height))
        {
            windowSize.Height = height;
        }

        return windowSize.Width > 0 && windowSize.Height > 0;
    }

    private static bool TryExtractWorkspaceNoteEditorWindowPlacement(
        string text,
        out WorkspaceNoteEditorWindowPlacementSettings placement)
    {
        placement = new WorkspaceNoteEditorWindowPlacementSettings();
        var section = ExtractObjectSection(text, "workspaceNoteEditorWindowPlacement");
        if (string.IsNullOrWhiteSpace(section))
        {
            return false;
        }

        if (TryExtractDoubleSetting(section, "left", out var left))
        {
            placement.Left = left;
        }

        if (TryExtractDoubleSetting(section, "top", out var top))
        {
            placement.Top = top;
        }

        if (TryExtractDoubleSetting(section, "width", out var width))
        {
            placement.Width = width;
        }

        if (TryExtractDoubleSetting(section, "height", out var height))
        {
            placement.Height = height;
        }

        placement = WorkspaceNoteEditorWindowPlacementProfile.Normalize(placement) ?? new();
        return placement.Width > 0 && placement.Height > 0;
    }

    private static string ExtractStringSetting(string text, string propertyName)
    {
        var marker = "\"" + propertyName + "\"";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var colonIndex = text.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0)
        {
            return string.Empty;
        }

        var quoteIndex = text.IndexOf('"', colonIndex + 1);
        if (quoteIndex < 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        var isEscaped = false;
        for (var i = quoteIndex + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (isEscaped)
            {
                builder.Append(c switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });
                isEscaped = false;
                continue;
            }

            if (c == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (c == '"')
            {
                return builder.ToString();
            }

            builder.Append(c);
        }

        return string.Empty;
    }

    private static string ExtractRawSettingValue(string text, string propertyName)
    {
        var marker = "\"" + propertyName + "\"";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var colonIndex = text.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0)
        {
            return string.Empty;
        }

        var start = colonIndex + 1;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = start;
        while (end < text.Length && text[end] is not ',' and not '}' and not ']')
        {
            end++;
        }

        return text[start..end].Trim();
    }

    private static string ExtractObjectSection(string text, string propertyName)
    {
        var marker = "\"" + propertyName + "\"";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var colonIndex = text.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0)
        {
            return string.Empty;
        }

        var objectStart = text.IndexOf('{', colonIndex + 1);
        if (objectStart < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        var isEscaped = false;
        for (var i = objectStart; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return text[objectStart..(i + 1)];
            }
        }

        return string.Empty;
    }

    private void EnsureStorage()
    {
        if (_settingsFile.Equals(AppPaths.SettingsFile, StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.Ensure();
            return;
        }

        AtomicFileService.EnsureDirectory(_settingsFile);
    }

    private static void BackupBrokenSettingsFile(string settingsFile)
    {
        if (!File.Exists(settingsFile))
        {
            return;
        }

        var directory = Path.GetDirectoryName(settingsFile) ?? AppPaths.Root;
        var backupPath = Path.Combine(
            directory,
            $"settings.json.broken-{DateTimeOffset.Now:yyyyMMddHHmmss}.bak");
        if (File.Exists(backupPath))
        {
            backupPath = Path.Combine(
                directory,
                $"settings.json.broken-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak");
        }

        File.Copy(settingsFile, backupPath, overwrite: false);
    }

    private static void AppendRecoveryDiagnostic(string settingsFile, string outcome)
    {
        try
        {
            var root = Path.GetDirectoryName(settingsFile) ?? AppPaths.Root;

            var directory = Path.Combine(root, "diagnostics");
            var logPath = Path.Combine(directory, "settings-recovery.log");

            Directory.CreateDirectory(directory);
            var line = string.Join(
                " | ",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                "runner=settings",
                "operation=settings-recovered",
                "exit=0",
                "timeout=False",
                "stderr=" + outcome);
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // 設定復旧ログの失敗で起動を止めない。
        }
    }

}
