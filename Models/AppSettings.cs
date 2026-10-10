using System.IO;

using System.Text.Json;

using System.Text.Json.Serialization;

namespace TabBrowser.Models;

public sealed class AppSettings

    : IJsonOnDeserialized, IJsonOnSerializing

{
    public int Version { get; set; } = 1;

    public string SearchEngine { get; set; } = "Google";

    public string SearchUrlTemplate { get; set; } = "https://www.google.com/search?q={0}";

    public List<SearchAliasSettings> SearchAliases { get; set; } = SearchAliasSettingsProfile.CreateDefaults();

    public double GestureStartThreshold { get; set; } = 14;

    public double GestureDirectionThreshold { get; set; } = 22;

    public bool PreserveContextMenuOnShortRightClick { get; set; } = true;

    public bool EnableLinkHoverPreview { get; set; } = true;

    public List<GestureMappingSettings> GestureMappings { get; set; } = GestureSettingsCatalog.CreateDefaultMappings();

    public bool EnableFocusedViewingCpuSaver { get; set; } = true;

    public double FocusedViewingCpuSaverDelaySeconds { get; set; } = 60;

    public bool EnableInactiveTabMemorySaver { get; set; } = true;

    public ClipboardHistorySettings ClipboardHistory { get; set; } = new();

    public BrowsingHistorySettings BrowsingHistory { get; set; } = new();

    public SitePrivacySettings SitePrivacy { get; set; } = new();

    public bool EnableBrowserExtensions { get; set; } = true;

    public bool EnableXReadableMode { get; set; } = true;

    public ChatGptReadabilitySettings ChatGptReadability { get; set; } = new();

    public FiveChLiveReadabilitySettings FiveChLiveReadability { get; set; } = new();

    public bool AlwaysOnTop { get; set; }

    // 既存の内部版の値を往復保存するだけで、公開版の機能や通信には使用しない。
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? PreservedCompatibilitySettings { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
        => PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(PreservedCompatibilitySettings);

    void IJsonOnSerializing.OnSerializing()
        => PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(PreservedCompatibilitySettings);

    public DownloadSettings Downloads { get; set; } = new();

    public TabLayoutSettings TabLayout { get; set; } = new();

    public string FolderTabViewMode { get; set; } = global::TabBrowser.Models.FolderTabViewMode.Details;

    public string FolderTabSortMode { get; set; } = global::TabBrowser.Models.FolderTabSortMode.Name;

    public string FolderTabSortDirection { get; set; } = global::TabBrowser.Models.FolderTabSortDirection.Ascending;

    public List<FolderTabSortPreferenceSettings> FolderTabSortPreferences { get; set; } = [];

    public List<string> FolderTabPinnedPlaces { get; set; } = [];

    public bool ShowHiddenFolderTabItems { get; set; }

    public QuickLookSettings QuickLook { get; set; } = new();

    public bool EnableYouTubeMiniPlayerHoverAudioPreview { get; set; }

    public bool EnableYouTubeMiniPlayerSizeBasedQuality { get; set; }

    public int YouTubeMiniPlayerQualityLowWidthThreshold { get; set; } =
        YouTubeMiniPlayerQualitySettingsProfile.DefaultLowWidthThreshold;

    public int YouTubeMiniPlayerQualityHighWidthThreshold { get; set; } =
        YouTubeMiniPlayerQualitySettingsProfile.DefaultHighWidthThreshold;

    public WindowPlacementSettings? WindowPlacement { get; set; }

    public BookmarkManagerWindowSizeSettings? BookmarkManagerWindowSize { get; set; }

    public WorkspaceNoteEditorWindowPlacementSettings? WorkspaceNoteEditorWindowPlacement { get; set; }

    public Dictionary<Guid, WorkspaceNoteEditorWindowPlacementSettings> WorkspaceNoteEditorWindowPlacements { get; set; } = [];

    public Dictionary<Guid, WorkspaceFolderWindowPlacementSettings> WorkspaceFolderWindowPlacements { get; set; } = [];

    public Guid? LastWorkspaceId { get; set; }

    public ToolbarVisibilitySettings ToolbarVisibility { get; set; } = new();

    public List<CommandPaletteUsageSettings> CommandPaletteUsage { get; set; } = [];

    public List<PageZoomPreferenceSettings> PageZoomPreferences { get; set; } = [];
}

public static class YouTubeMiniPlayerQualitySettingsProfile
{
    public const int MinimumWidthThreshold = 320;
    public const int MaximumWidthThreshold = 2560;
    public const int MinimumThresholdGap = 200;
    public const int SliderStep = 20;
    public const int DefaultLowWidthThreshold = 640;
    public const int DefaultHighWidthThreshold = 1100;

    public static (int LowWidthThreshold, int HighWidthThreshold) Normalize(
        int lowWidthThreshold,
        int highWidthThreshold)
    {
        var low = Math.Clamp(
            lowWidthThreshold,
            MinimumWidthThreshold,
            MaximumWidthThreshold - MinimumThresholdGap);
        var high = Math.Clamp(
            highWidthThreshold,
            MinimumWidthThreshold + MinimumThresholdGap,
            MaximumWidthThreshold);
        if (high - low < MinimumThresholdGap)
        {
            high = low + MinimumThresholdGap;
        }

        return (low, high);
    }
}

public sealed class SearchAliasSettings
{
    public string Alias { get; set; } = string.Empty;

    public string SearchUrlTemplate { get; set; } = string.Empty;
}

public static class SearchAliasSettingsProfile
{
    public const int MaximumAliasLength = 16;

    public static List<SearchAliasSettings> CreateDefaults()
    {
        return
        [
            new()
            {
                Alias = "g",
                SearchUrlTemplate = "https://www.google.com/search?q={0}"
            },
            new()
            {
                Alias = "yt",
                SearchUrlTemplate = "https://www.youtube.com/results?search_query={0}"
            },
            new()
            {
                Alias = "x",
                SearchUrlTemplate = "https://x.com/search?q={0}&src=typed_query"
            },
            new()
            {
                Alias = "w",
                SearchUrlTemplate = "https://ja.wikipedia.org/w/index.php?search={0}"
            }
        ];
    }

    public static List<SearchAliasSettings> Normalize(IEnumerable<SearchAliasSettings>? aliases)
    {
        aliases ??= CreateDefaults();
        var normalized = new List<SearchAliasSettings>();
        var knownAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            var name = alias?.Alias?.Trim() ?? string.Empty;
            var template = alias?.SearchUrlTemplate?.Trim() ?? string.Empty;
            if (!IsValidAlias(name)
                || !IsValidSearchUrlTemplate(template)
                || !knownAliases.Add(name))
            {
                continue;
            }

            normalized.Add(new SearchAliasSettings
            {
                Alias = name.ToLowerInvariant(),
                SearchUrlTemplate = template
            });
        }

        return normalized;
    }

    public static bool IsValidAlias(string? value)
    {
        var alias = value?.Trim() ?? string.Empty;
        return alias.Length is > 0 and <= MaximumAliasLength
            && alias.All(static character =>
                character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_'
                or '-');
    }

    public static bool IsValidSearchUrlTemplate(string? value)
    {
        var template = value?.Trim() ?? string.Empty;
        if (!template.Contains("{0}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var formatted = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                template,
                "test");
            return Uri.TryCreate(formatted, UriKind.Absolute, out var uri)
                && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class PageZoomPreferenceSettings
{
    public string Origin { get; set; } = string.Empty;

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public double ZoomFactor { get; set; } = PageZoomPreferenceSettingsProfile.DefaultZoomFactor;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class PageZoomPreferenceSettingsProfile
{
    public const double MinimumZoomFactor = 0.25;
    public const double DefaultZoomFactor = 1.0;
    public const double MaximumZoomFactor = 5.0;
    public const int MaximumEntryCount = 500;

    public static List<PageZoomPreferenceSettings> Normalize(
        IEnumerable<PageZoomPreferenceSettings>? entries)
    {
        return (entries ?? [])
            .Select(NormalizeEntry)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .GroupBy(
                static entry => (entry.Origin, entry.ContainerProfileId),
                static entry => entry)
            .Select(static group => group
                .OrderByDescending(static entry => entry.UpdatedAt)
                .First())
            .OrderByDescending(static entry => entry.UpdatedAt)
            .Take(MaximumEntryCount)
            .ToList();
    }

    public static double GetZoomFactor(
        IEnumerable<PageZoomPreferenceSettings>? entries,
        string? url,
        string? containerProfileId)
    {
        if (!TryNormalizeOrigin(url, out var origin))
        {
            return DefaultZoomFactor;
        }

        var normalizedContainerProfileId = ContainerProfile.NormalizeId(containerProfileId);
        return Normalize(entries).FirstOrDefault(entry =>
                entry.Origin.Equals(origin, StringComparison.Ordinal)
                && entry.ContainerProfileId.Equals(normalizedContainerProfileId, StringComparison.Ordinal))
            ?.ZoomFactor
            ?? DefaultZoomFactor;
    }

    public static List<PageZoomPreferenceSettings> Record(
        IEnumerable<PageZoomPreferenceSettings>? entries,
        string? url,
        string? containerProfileId,
        double zoomFactor,
        DateTimeOffset updatedAt)
    {
        var normalized = Normalize(entries);
        if (!TryNormalizeOrigin(url, out var origin))
        {
            return normalized;
        }

        var normalizedContainerProfileId = ContainerProfile.NormalizeId(containerProfileId);
        normalized.RemoveAll(entry =>
            entry.Origin.Equals(origin, StringComparison.Ordinal)
            && entry.ContainerProfileId.Equals(normalizedContainerProfileId, StringComparison.Ordinal));
        normalized.Add(new PageZoomPreferenceSettings
        {
            Origin = origin,
            ContainerProfileId = normalizedContainerProfileId,
            ZoomFactor = NormalizeZoomFactor(zoomFactor),
            UpdatedAt = updatedAt
        });
        return Normalize(normalized);
    }

    public static double NormalizeZoomFactor(double zoomFactor)
    {
        return double.IsFinite(zoomFactor)
            ? Math.Clamp(zoomFactor, MinimumZoomFactor, MaximumZoomFactor)
            : DefaultZoomFactor;
    }

    public static bool TryNormalizeOrigin(string? url, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(
            uri.Scheme.ToLowerInvariant(),
            uri.IdnHost.ToLowerInvariant())
        {
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        origin = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/').ToLowerInvariant();
        return origin.Length > 0;
    }

    private static PageZoomPreferenceSettings? NormalizeEntry(PageZoomPreferenceSettings entry)
    {
        if (!TryNormalizeOrigin(entry.Origin, out var origin))
        {
            return null;
        }

        return new PageZoomPreferenceSettings
        {
            Origin = origin,
            ContainerProfileId = ContainerProfile.NormalizeId(entry.ContainerProfileId),
            ZoomFactor = NormalizeZoomFactor(entry.ZoomFactor),
            UpdatedAt = entry.UpdatedAt == default ? DateTimeOffset.UnixEpoch : entry.UpdatedAt
        };
    }
}

public static class FolderTabViewMode
{
    public const string Details = "details";
    public const string Icons = "icons";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Icons, StringComparison.OrdinalIgnoreCase)
            ? Icons
            : Details;
    }
}

public sealed class ClipboardHistorySettings
{
    public bool IsEnabled { get; set; }

    public int MaximumItemCount { get; set; } = ClipboardHistorySettingsProfile.DefaultMaximumItemCount;

    public bool CaptureUrls { get; set; } = true;

    public bool CaptureFiles { get; set; } = true;
}

public static class ClipboardHistorySettingsProfile
{
    public const int MinimumMaximumItemCount = 10;
    public const int DefaultMaximumItemCount = 50;
    public const int MaximumMaximumItemCount = 200;

    public static ClipboardHistorySettings Normalize(ClipboardHistorySettings? settings)
    {
        settings ??= new ClipboardHistorySettings();
        return new ClipboardHistorySettings
        {
            IsEnabled = settings.IsEnabled,
            MaximumItemCount = Math.Clamp(
                settings.MaximumItemCount,
                MinimumMaximumItemCount,
                MaximumMaximumItemCount),
            CaptureUrls = settings.CaptureUrls,
            CaptureFiles = settings.CaptureFiles
        };
    }
}

public sealed class BrowsingHistorySettings
{
    public bool IsEnabled { get; set; }

    public List<string> ExcludedHosts { get; set; } = [];

    public List<string> ExcludedContainerProfileIds { get; set; } = [];
}

public static class BrowsingHistorySettingsProfile
{
    public const int MaximumExcludedHostCount = 200;
    public const int MaximumExcludedContainerCount = 64;

    public static BrowsingHistorySettings Normalize(BrowsingHistorySettings? settings)
    {
        settings ??= new BrowsingHistorySettings();
        return new BrowsingHistorySettings
        {
            IsEnabled = settings.IsEnabled,
            ExcludedHosts = (settings.ExcludedHosts ?? [])
                .Select(NormalizeHost)
                .Where(static host => host.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumExcludedHostCount)
                .ToList(),
            ExcludedContainerProfileIds = (settings.ExcludedContainerProfileIds ?? [])
                .Select(NormalizeContainerProfileId)
                .Where(static id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumExcludedContainerCount)
                .ToList()
        };
    }

    public static bool ShouldRecord(
        BrowsingHistorySettings? settings,
        string? url,
        string? containerProfileId,
        bool isPrivate)
    {
        var normalized = Normalize(settings);
        if (!normalized.IsEnabled || isPrivate || !TryGetHttpUri(url, out var uri))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        var containerId = ContainerProfile.NormalizeId(containerProfileId);
        return !normalized.ExcludedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            && !normalized.ExcludedContainerProfileIds.Contains(containerId, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (TryGetHttpUri(trimmed, out var uri))
        {
            return uri.IdnHost.ToLowerInvariant();
        }

        if (Uri.CheckHostName(trimmed) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return trimmed.Trim('[', ']').ToLowerInvariant();
        }

        return string.Empty;
    }

    private static string NormalizeContainerProfileId(string? value)
    {
        if (string.Equals(value?.Trim(), ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return ContainerProfile.DefaultId;
        }

        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id.ToString("N")
            : string.Empty;
    }

    private static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            && parsed is not null
            && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}

public static class FolderTabSortMode
{
    public const string Name = "name";
    public const string Type = "type";
    public const string LastWriteTime = "lastWriteTime";
    public const string Size = "size";

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Type => Type,
            "lastwritetime" => LastWriteTime,
            Size => Size,
            _ => Name
        };
    }
}

public static class FolderTabSortDirection
{
    public const string Ascending = "ascending";
    public const string Descending = "descending";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Descending, StringComparison.OrdinalIgnoreCase)
            ? Descending
            : Ascending;
    }
}

public sealed class FolderTabSortPreferenceSettings
{
    public string FolderPath { get; set; } = string.Empty;

    public string SortMode { get; set; } = FolderTabSortMode.Name;

    public string SortDirection { get; set; } = FolderTabSortDirection.Ascending;

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class FolderTabSortPreferenceSettingsProfile
{
    public const int MaximumEntryCount = 128;

    public static bool Update(
        AppSettings settings,
        string folderPath,
        string sortMode,
        string sortDirection,
        Func<DateTimeOffset>? getUpdatedAt = null)
    {
        var normalizedMode = FolderTabSortMode.Normalize(sortMode);
        var normalizedDirection = FolderTabSortDirection.Normalize(sortDirection);
        var existingPreference = Find(settings.FolderTabSortPreferences, folderPath);
        var globalChanged = !string.Equals(settings.FolderTabSortMode, normalizedMode, StringComparison.Ordinal)
            || !string.Equals(settings.FolderTabSortDirection, normalizedDirection, StringComparison.Ordinal);
        var preferenceChanged = existingPreference is null
            || !string.Equals(existingPreference.SortMode, normalizedMode, StringComparison.Ordinal)
            || !string.Equals(existingPreference.SortDirection, normalizedDirection, StringComparison.Ordinal);
        if (!globalChanged && !preferenceChanged)
        {
            return false;
        }

        settings.FolderTabSortMode = normalizedMode;
        settings.FolderTabSortDirection = normalizedDirection;
        if (preferenceChanged)
        {
            settings.FolderTabSortPreferences = Record(
                settings.FolderTabSortPreferences,
                folderPath,
                normalizedMode,
                normalizedDirection,
                getUpdatedAt is null ? DateTimeOffset.UtcNow : getUpdatedAt());
        }

        return true;
    }

    public static List<FolderTabSortPreferenceSettings> Normalize(
        IEnumerable<FolderTabSortPreferenceSettings>? entries)
    {
        return (entries ?? [])
            .OfType<FolderTabSortPreferenceSettings>()
            .Select(NormalizeEntry)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .GroupBy(static entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(entry => entry.UpdatedAt).First())
            .OrderByDescending(static entry => entry.UpdatedAt)
            .Take(MaximumEntryCount)
            .ToList();
    }

    public static FolderTabSortPreferenceSettings? Find(
        IEnumerable<FolderTabSortPreferenceSettings>? entries,
        string? folderPath)
    {
        if (!TryNormalizeFolderPath(folderPath, out var normalizedPath))
        {
            return null;
        }

        return Normalize(entries).FirstOrDefault(
            entry => string.Equals(entry.FolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    public static List<FolderTabSortPreferenceSettings> Record(
        IEnumerable<FolderTabSortPreferenceSettings>? entries,
        string? folderPath,
        string? sortMode,
        string? sortDirection,
        DateTimeOffset updatedAt)
    {
        var normalized = Normalize(entries);
        if (!TryNormalizeFolderPath(folderPath, out var normalizedPath))
        {
            return normalized;
        }

        normalized.RemoveAll(
            entry => string.Equals(entry.FolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        normalized.Add(new FolderTabSortPreferenceSettings
        {
            FolderPath = normalizedPath,
            SortMode = FolderTabSortMode.Normalize(sortMode),
            SortDirection = FolderTabSortDirection.Normalize(sortDirection),
            UpdatedAt = updatedAt
        });
        return Normalize(normalized);
    }

    private static FolderTabSortPreferenceSettings? NormalizeEntry(
        FolderTabSortPreferenceSettings entry)
    {
        if (!TryNormalizeFolderPath(entry.FolderPath, out var normalizedPath))
        {
            return null;
        }

        return new FolderTabSortPreferenceSettings
        {
            FolderPath = normalizedPath,
            SortMode = FolderTabSortMode.Normalize(entry.SortMode),
            SortDirection = FolderTabSortDirection.Normalize(entry.SortDirection),
            UpdatedAt = entry.UpdatedAt
        };
    }

    private static bool TryNormalizeFolderPath(string? folderPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        var candidate = folderPath?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public static class QuickLookPanePlacement
{
    public const string Right = "right";
    public const string Bottom = "bottom";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Bottom, StringComparison.OrdinalIgnoreCase)
            ? Bottom
            : Right;
    }
}

public sealed class QuickLookSettings
{
    public bool IsEnabled { get; set; } = true;

    public bool EnableInFolderTabs { get; set; } = true;

    public bool EnableInWorkspace { get; set; } = true;

    public bool EnableInUnifiedSearch { get; set; } = true;

    public bool EnableInWorkspaceInbox { get; set; } = true;

    public bool UseRightSidePane { get; set; }

    public string PanePlacement { get; set; } = QuickLookPanePlacement.Right;

    public double RightPaneWidth { get; set; } = QuickLookSettingsProfile.DefaultRightPaneWidth;

    public double BottomPaneHeight { get; set; } = QuickLookSettingsProfile.DefaultBottomPaneHeight;

    public double FloatingOffsetX { get; set; }

    public double FloatingOffsetY { get; set; }

    public double FloatingWidth { get; set; } = QuickLookSettingsProfile.DefaultFloatingWidth;

    public double FloatingHeight { get; set; } = QuickLookSettingsProfile.DefaultFloatingHeight;

    public long TextMaxBytes { get; set; } = QuickLookSettingsProfile.DefaultTextMaxBytes;

    public int TextFontSize { get; set; } = QuickLookSettingsProfile.DefaultTextFontSize;

    public bool TextWrap { get; set; } = true;

    public int ImageDecodePixelWidth { get; set; } = QuickLookSettingsProfile.DefaultImageDecodePixelWidth;

    public long ImageCacheLimitBytes { get; set; } = QuickLookSettingsProfile.DefaultImageCacheLimitBytes;

    public bool AutoPlayMedia { get; set; }
}

public static class QuickLookSettingsProfile
{
    public static bool HasPaneLayoutChanged(QuickLookSettings current, QuickLookSettings normalized)
    {
        return !(current.UseRightSidePane == normalized.UseRightSidePane
            && string.Equals(current.PanePlacement, normalized.PanePlacement, StringComparison.Ordinal)
            && Math.Abs(current.RightPaneWidth - normalized.RightPaneWidth) < 0.5
            && Math.Abs(current.BottomPaneHeight - normalized.BottomPaneHeight) < 0.5
            && Math.Abs(current.FloatingOffsetX - normalized.FloatingOffsetX) < 0.5
            && Math.Abs(current.FloatingOffsetY - normalized.FloatingOffsetY) < 0.5
            && Math.Abs(current.FloatingWidth - normalized.FloatingWidth) < 0.5
            && Math.Abs(current.FloatingHeight - normalized.FloatingHeight) < 0.5);
    }

    public static bool HasTextDisplayChanged(QuickLookSettings current, QuickLookSettings normalized)
    {
        return current.TextFontSize != normalized.TextFontSize || current.TextWrap != normalized.TextWrap;
    }

    public const long MinimumTextMaxBytes = 64 * 1024;
    public const long DefaultTextMaxBytes = 1024 * 1024;
    public const long MaximumTextMaxBytes = 8 * 1024 * 1024;
    public const int MinimumTextFontSize = 11;
    public const int DefaultTextFontSize = 13;
    public const int MaximumTextFontSize = 20;
    public const int MinimumImageDecodePixelWidth = 512;
    public const int DefaultImageDecodePixelWidth = 1600;
    public const int MaximumImageDecodePixelWidth = 4096;
    public const long MinimumImageCacheLimitBytes = 16 * 1024 * 1024;
    public const long DefaultImageCacheLimitBytes = 96 * 1024 * 1024;
    public const long MaximumImageCacheLimitBytes = 512 * 1024 * 1024;
    public const double MinimumRightPaneWidth = 260;
    public const double DefaultRightPaneWidth = 420;
    public const double MaximumRightPaneWidth = 960;
    public const double MinimumBottomPaneHeight = 180;
    public const double DefaultBottomPaneHeight = 360;
    public const double MaximumBottomPaneHeight = 720;
    public const double MinimumFloatingWidth = 320;
    public const double DefaultFloatingWidth = 760;
    public const double MaximumFloatingWidth = 1600;
    public const double MinimumFloatingHeight = 240;
    public const double DefaultFloatingHeight = 580;
    public const double MaximumFloatingHeight = 1200;
    public const double MaximumFloatingOffset = 10000;

    public static QuickLookSettings Normalize(QuickLookSettings? settings)
    {
        settings ??= new QuickLookSettings();
        return new QuickLookSettings
        {
            IsEnabled = settings.IsEnabled,
            EnableInFolderTabs = settings.EnableInFolderTabs,
            EnableInWorkspace = settings.EnableInWorkspace,
            EnableInUnifiedSearch = settings.EnableInUnifiedSearch,
            EnableInWorkspaceInbox = settings.EnableInWorkspaceInbox,
            UseRightSidePane = settings.UseRightSidePane,
            PanePlacement = QuickLookPanePlacement.Normalize(settings.PanePlacement),
            RightPaneWidth = NormalizeFiniteRange(
                settings.RightPaneWidth,
                MinimumRightPaneWidth,
                DefaultRightPaneWidth,
                MaximumRightPaneWidth),
            BottomPaneHeight = NormalizeFiniteRange(
                settings.BottomPaneHeight,
                MinimumBottomPaneHeight,
                DefaultBottomPaneHeight,
                MaximumBottomPaneHeight),
            FloatingOffsetX = NormalizeFiniteRange(
                settings.FloatingOffsetX,
                -MaximumFloatingOffset,
                0,
                MaximumFloatingOffset),
            FloatingOffsetY = NormalizeFiniteRange(
                settings.FloatingOffsetY,
                -MaximumFloatingOffset,
                0,
                MaximumFloatingOffset),
            FloatingWidth = NormalizeFiniteRange(
                settings.FloatingWidth,
                MinimumFloatingWidth,
                DefaultFloatingWidth,
                MaximumFloatingWidth),
            FloatingHeight = NormalizeFiniteRange(
                settings.FloatingHeight,
                MinimumFloatingHeight,
                DefaultFloatingHeight,
                MaximumFloatingHeight),
            TextMaxBytes = Math.Clamp(settings.TextMaxBytes, MinimumTextMaxBytes, MaximumTextMaxBytes),
            TextFontSize = Math.Clamp(
                settings.TextFontSize,
                MinimumTextFontSize,
                MaximumTextFontSize),
            TextWrap = settings.TextWrap,
            ImageDecodePixelWidth = Math.Clamp(
                settings.ImageDecodePixelWidth,
                MinimumImageDecodePixelWidth,
                MaximumImageDecodePixelWidth),
            ImageCacheLimitBytes = Math.Clamp(
                settings.ImageCacheLimitBytes,
                MinimumImageCacheLimitBytes,
                MaximumImageCacheLimitBytes),
            AutoPlayMedia = settings.AutoPlayMedia
        };
    }

    private static double NormalizeFiniteRange(
        double value,
        double minimum,
        double fallback,
        double maximum)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }
}

public sealed class CommandPaletteUsageSettings
{
    public string ItemId { get; set; } = string.Empty;

    public int UseCount { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }
}

public static class CommandPaletteUsageSettingsProfile
{
    public const int MaximumEntryCount = 100;

    public static List<CommandPaletteUsageSettings> Normalize(
        IEnumerable<CommandPaletteUsageSettings>? entries)
    {
        return (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId.Trim(), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(entry => entry.LastUsedAt).First())
            .OrderByDescending(entry => entry.LastUsedAt)
            .Take(MaximumEntryCount)
            .Select(entry => new CommandPaletteUsageSettings
            {
                ItemId = entry.ItemId.Trim(),
                UseCount = Math.Max(1, entry.UseCount),
                LastUsedAt = entry.LastUsedAt
            })
            .ToList();
    }

    public static List<CommandPaletteUsageSettings> Record(
        IEnumerable<CommandPaletteUsageSettings>? entries,
        string itemId,
        DateTimeOffset usedAt)
    {
        var normalized = Normalize(entries);
        var entry = normalized.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (entry is null)
        {
            normalized.Add(new CommandPaletteUsageSettings
            {
                ItemId = itemId,
                UseCount = 1,
                LastUsedAt = usedAt
            });
        }
        else
        {
            entry.UseCount = entry.UseCount == int.MaxValue ? int.MaxValue : entry.UseCount + 1;
            entry.LastUsedAt = usedAt;
        }

        return Normalize(normalized);
    }
}

public sealed class DownloadSettings
{
    public string DefaultSaveDirectory { get; set; } = string.Empty;

    public bool AskSaveLocation { get; set; } = true;

    public bool OpenShelfOnDownloadStart { get; set; } = true;

    public bool OpenShelfOnDownloadCompleted { get; set; }

    public int HistoryRetentionLimit { get; set; }
}

public sealed class TabLayoutSettings
{
    public string Mode { get; set; } = TabLayoutSettingsProfile.HorizontalMode;

    public double VerticalSidebarWidth { get; set; } = TabLayoutSettingsProfile.DefaultVerticalSidebarWidth;

    public bool VerticalSidebarCollapsed { get; set; }

    public bool ShowTabSearch { get; set; } = true;

    public bool ShowPinnedTabsSection { get; set; } = true;
}

public static class TabLayoutSettingsProfile
{
    public const string HorizontalMode = "horizontal";
    public const string VerticalMode = "vertical";
    public const double DefaultVerticalSidebarWidth = 260;
    public const double MinimumVerticalSidebarWidth = 180;
    public const double MaximumVerticalSidebarWidth = 520;

    public static TabLayoutSettings Normalize(TabLayoutSettings? settings)
    {
        settings ??= new TabLayoutSettings();
        var mode = string.Equals(settings.Mode, VerticalMode, StringComparison.OrdinalIgnoreCase)
            ? VerticalMode
            : HorizontalMode;

        return new TabLayoutSettings
        {
            Mode = mode,
            VerticalSidebarWidth = Math.Clamp(
                settings.VerticalSidebarWidth,
                MinimumVerticalSidebarWidth,
                MaximumVerticalSidebarWidth),
            VerticalSidebarCollapsed = settings.VerticalSidebarCollapsed,
            ShowTabSearch = settings.ShowTabSearch,
            ShowPinnedTabsSection = settings.ShowPinnedTabsSection
        };
    }
}

public static class DownloadSettingsProfile
{
    public const int MaximumHistoryRetentionLimit = 10000;

    public static DownloadSettings Normalize(DownloadSettings? settings)
    {
        settings ??= new DownloadSettings();
        return new DownloadSettings
        {
            DefaultSaveDirectory = (settings.DefaultSaveDirectory ?? string.Empty).Trim().Trim('"'),
            AskSaveLocation = settings.AskSaveLocation,
            OpenShelfOnDownloadStart = settings.OpenShelfOnDownloadStart,
            OpenShelfOnDownloadCompleted = settings.OpenShelfOnDownloadCompleted,
            HistoryRetentionLimit = Math.Clamp(settings.HistoryRetentionLimit, 0, MaximumHistoryRetentionLimit)
        };
    }
}

public sealed class ChatGptReadabilitySettings
{
    public bool IsEnabled { get; set; }

    public int FontScalePercent { get; set; } = 100;

    public double LineHeight { get; set; } = 1.6;

    public string Theme { get; set; } = "standard";

    public string ContentWidth { get; set; } = "standard";

    public int CodeFontScalePercent { get; set; } = 100;

    public bool WrapCodeBlocks { get; set; }

    public bool LargeInput { get; set; }

    public bool HighlightKeyboardFocus { get; set; }
}

public sealed class FiveChLiveReadabilitySettings
{
    public bool IsEnabled { get; set; } = true;

    public bool HighlightNewPosts { get; set; } = true;

    public bool AutoRefresh { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 5;

    public bool AutoOpenNextThread { get; set; } = true;

    public bool AutoScroll { get; set; } = true;

    public bool PreviewReplyAnchors { get; set; } = true;

    public bool HighlightPosterIds { get; set; } = true;

    public int FontSize { get; set; } = 16;

    public int PostSpacing { get; set; } = 8;
}

public sealed class ToolbarVisibilitySettings

    : IJsonOnDeserialized, IJsonOnSerializing

{
    public bool AutoHideToolbar { get; set; }

    public bool ShowBackButton { get; set; } = true;

    public bool ShowForwardButton { get; set; } = true;

    public bool ShowReloadButton { get; set; } = true;

    public bool ShowRestoreButton { get; set; } = true;

    public bool ShowClosedTabHistoryButton { get; set; } = true;

    public bool ShowAddressBox { get; set; } = true;

    public bool ShowGoButton { get; set; } = true;

    public bool ShowActionsButton { get; set; } = true;

    public bool ShowAddBookmarkButton { get; set; }

    public bool ShowBookmarkListButton { get; set; }

    public bool ShowWorkspaceOSButton { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? PreservedCompatibilitySettings { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
        => PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(PreservedCompatibilitySettings, toolbar: true);

    void IJsonOnSerializing.OnSerializing()
        => PreservedCompatibilitySettings = PublicSettingsCompatibility.Clone(PreservedCompatibilitySettings, toolbar: true);

    public bool ShowToolsButton { get; set; } = true;

    public bool ShowAddExtensionButton { get; set; }

    public bool ShowImportExtensionButton { get; set; }

    public bool ShowManageExtensionsButton { get; set; }

    public bool ShowGestureSettingsButton { get; set; }

    public bool ShowSettingsButton { get; set; }

    public bool ShowExtensionIcons { get; set; } = true;

    public bool ShowStatusText { get; set; } = true;
}

internal static class PublicSettingsCompatibility
{
    internal static readonly string[] RootKeys =
    [
        "enableOnePasswordIntegration", "onePasswordCliPath", "ytDlpPath",
        "enableYouTubePoTokenProvider", "youTubePoTokenPluginDirectory",
        "youTubePoTokenProviderBaseUrl", "useMwebClient"
    ];

    internal static readonly string[] ToolbarKeys = ["showOnePasswordCandidateButton"];

    internal static Dictionary<string, JsonElement>? Clone(
        Dictionary<string, JsonElement>? values, bool toolbar = false)
    {
        if (values is null)
        {
            return null;
        }

        var allowed = toolbar ? ToolbarKeys : RootKeys;
        var retained = values
            .Where(pair => allowed.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        return retained.Count == 0 ? null : retained;
    }
}

public sealed class WindowPlacementSettings
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public string State { get; set; } = "Normal";
}

public sealed class BookmarkManagerWindowSizeSettings
{
    public double Width { get; set; }

    public double Height { get; set; }
}

public sealed class WorkspaceNoteEditorWindowPlacementSettings
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

public static class WorkspaceNoteEditorWindowPlacementProfile
{
    public const double DefaultWidth = 680;
    public const double DefaultHeight = 520;
    public const double MinimumWidth = 440;
    public const double MinimumHeight = 320;

    public static WorkspaceNoteEditorWindowPlacementSettings? Normalize(
        WorkspaceNoteEditorWindowPlacementSettings? placement)
    {
        if (placement is null
            || !double.IsFinite(placement.Left)
            || !double.IsFinite(placement.Top)
            || !IsValidWindowLength(placement.Width)
            || !IsValidWindowLength(placement.Height))
        {
            return null;
        }

        return new WorkspaceNoteEditorWindowPlacementSettings
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = Math.Max(MinimumWidth, placement.Width),
            Height = Math.Max(MinimumHeight, placement.Height)
        };
    }

    public static Dictionary<Guid, WorkspaceNoteEditorWindowPlacementSettings> Normalize(
        IReadOnlyDictionary<Guid, WorkspaceNoteEditorWindowPlacementSettings>? placements)
    {
        var normalized = new Dictionary<Guid, WorkspaceNoteEditorWindowPlacementSettings>();
        if (placements is null)
        {
            return normalized;
        }

        foreach (var pair in placements)
        {
            if (pair.Key != Guid.Empty && Normalize(pair.Value) is { } placement)
            {
                normalized[pair.Key] = placement;
            }
        }

        return normalized;
    }

    private static bool IsValidWindowLength(double value)
    {
        return double.IsFinite(value) && value >= 1 && value < 20000;
    }
}

public sealed class WorkspaceFolderWindowPlacementSettings
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

public static class WorkspaceFolderWindowPlacementProfile
{
    public static bool Update(
        AppSettings settings,
        Guid folderId,
        WorkspaceFolderWindowPlacementSettings placement)
    {
        if (Normalize(placement) is not { } normalized)
        {
            return false;
        }

        settings.WorkspaceFolderWindowPlacements ??= [];
        if (settings.WorkspaceFolderWindowPlacements.TryGetValue(folderId, out var current)
            && Math.Abs(current.Left - normalized.Left) < 0.5
            && Math.Abs(current.Top - normalized.Top) < 0.5
            && Math.Abs(current.Width - normalized.Width) < 0.5
            && Math.Abs(current.Height - normalized.Height) < 0.5)
        {
            return false;
        }

        settings.WorkspaceFolderWindowPlacements[folderId] = normalized;
        return true;
    }

    public static WorkspaceFolderWindowPlacementSettings? Normalize(
        WorkspaceFolderWindowPlacementSettings? placement)
    {
        if (placement is null
            || !double.IsFinite(placement.Left)
            || !double.IsFinite(placement.Top)
            || !double.IsFinite(placement.Width)
            || !double.IsFinite(placement.Height)
            || placement.Width <= 0
            || placement.Height <= 0)
        {
            return null;
        }

        return new WorkspaceFolderWindowPlacementSettings
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height
        };
    }

    public static Dictionary<Guid, WorkspaceFolderWindowPlacementSettings> Normalize(
        IReadOnlyDictionary<Guid, WorkspaceFolderWindowPlacementSettings>? placements)
    {
        var normalized = new Dictionary<Guid, WorkspaceFolderWindowPlacementSettings>();
        if (placements is null)
        {
            return normalized;
        }

        foreach (var pair in placements)
        {
            if (pair.Key != Guid.Empty && Normalize(pair.Value) is { } placement)
            {
                normalized[pair.Key] = placement;
            }
        }

        return normalized;
    }
}

public sealed class GestureMappingSettings
{
    public string Route { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Scope { get; set; } = GestureCommandScope.Any;
}

public sealed class GestureCommandOption(
    string command,
    string label,
    string category = "一般",
    IEnumerable<string>? scopes = null,
    IEnumerable<string>? keywords = null)
{
    public string Command { get; } = command;

    public string Label { get; } = label;

    public string Category { get; } = category;

    public IReadOnlyList<string> Scopes { get; } = (scopes ?? [GestureCommandScope.Any])
        .Select(GestureCommandScope.Normalize)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<string> Keywords { get; } = (keywords ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .ToList();

    public bool SupportsScope(string? scope)
    {
        var normalizedScope = GestureCommandScope.Normalize(scope);
        return Scopes.Contains(GestureCommandScope.Any, StringComparer.Ordinal)
            || Scopes.Contains(normalizedScope, StringComparer.Ordinal);
    }
}

public sealed class GestureScopeOption(string value, string label)
{
    public string Value { get; } = value;

    public string Label { get; } = label;
}

public static class GestureCommandScope
{
    public const string Any = "any";
    public const string Global = "global";
    public const string Web = "web";
    public const string FolderTab = "folderTab";
    public const string Workspace = "workspace";
    public const string MiniPlayer = "miniPlayer";

    public static IReadOnlyList<GestureScopeOption> Options { get; } =
    [
        new(Any, "既存画面共通"),
        new(Global, "共通／空画面"),
        new(Web, "Webページ"),
        new(FolderTab, "フォルダータブ"),
        new(Workspace, "Workspace"),
        new(MiniPlayer, "動画ミニプレイヤー")
    ];

    public static string Normalize(string? scope)
    {
        return scope switch
        {
            Global => Global,
            Web => Web,
            FolderTab => FolderTab,
            Workspace => Workspace,
            MiniPlayer => MiniPlayer,
            _ => Any
        };
    }

    public static string GetLabel(string? scope)
    {
        var normalized = Normalize(scope);
        return Options.First(option => option.Value == normalized).Label;
    }

    public static string FromSurface(CommandSurface surface)
    {
        return surface switch
        {
            CommandSurface.Web => Web,
            CommandSurface.FolderTab => FolderTab,
            CommandSurface.Workspace => Workspace,
            CommandSurface.MiniPlayer => MiniPlayer,
            _ => Global
        };
    }
}

public static class GestureSettingsCatalog
{
    public const int MaximumDirectionCount = 3;

    private static readonly char[] Directions = ['←', '→', '↑', '↓'];

    public static IReadOnlyList<string> CommonRoutes { get; } = CreateCommonRoutes();

    public static IReadOnlyList<GestureCommandOption> CommandOptions { get; } =
    [
        new("Back", "戻る"),
        new("Forward", "進む"),
        new("NewTab", "新しいタブ"),
        new("DuplicateTab", "現在タブを複製"),
        new("DuplicateTabBackground", "現在タブを裏で複製"),
        new("CloseTab", "タブを閉じる"),
        new("CloseOtherTabs", "他のタブを閉じる"),
        new("CloseTabsToRight", "右側のタブを閉じる"),
        new("CloseTabsToLeft", "左側のタブを閉じる"),
        new("Reload", "更新"),
        new("StopLoading", "読み込み停止"),
        new("NextTab", "次のタブ"),
        new("PreviousTab", "前のタブ"),
        new("RestoreClosedTab", "閉じたタブを復元"),
        new("ScrollTop", "ページ先頭へ"),
        new("ScrollBottom", "ページ末尾へ"),
        new("ScrollPageUp", "1ページ上へ"),
        new("ScrollPageDown", "1ページ下へ"),
        new("SearchSelection", "選択テキストを検索"),
        new("CopyUrl", "URL をコピー"),
        new("AddBookmark", "現在ページをブックマーク"),
        new("ShowBookmarks", "ブックマーク一覧を開く"),
        new("ShowWorkspace", "Workspace OS を開く"),
        new("ToggleWorkspace", "Workspace OS を開く／元のタブへ戻る"),
        new("ShowWorkspaceWindow", "Workspace OS を別ウィンドウで開く")
    ];

    public static List<GestureMappingSettings> CreateDefaultMappings()
    {
        return
        [
            CreateMapping("←", "Back"),
            CreateMapping("→", "Forward"),
            CreateMapping("↑", "Reload"),
            CreateMapping("↓", "CloseTab"),
            CreateMapping("wheelDown", "NextTab"),
            CreateMapping("wheelUp", "PreviousTab"),
            CreateMapping("←↑", "RestoreClosedTab"),
            CreateMapping("→↑", "ScrollTop"),
            CreateMapping("→↓", "ScrollBottom"),
            CreateMapping("↓→", "NewTab"),
            CreateMapping("↑↓", "DuplicateTab"),
            CreateMapping("↓←", "CopyUrl"),
            CreateMapping("↑→", "SearchSelection"),
            CreateMapping("←→", "ShowWorkspace"),
            CreateMapping("→←", "ShowBookmarks")
        ];
    }

    public static List<GestureMappingSettings> NormalizeMappings(IEnumerable<GestureMappingSettings>? mappings)
    {
        if (mappings is null)
        {
            return CreateDefaultMappings();
        }

        var normalized = new List<GestureMappingSettings>();
        var seenRoutes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            var route = (mapping.Route ?? string.Empty).Trim();
            var command = (mapping.Command ?? string.Empty).Trim();
            var scope = GestureCommandScope.Normalize(mapping.Scope);
            var routeKey = scope + "\0" + route;
            if (string.IsNullOrWhiteSpace(route)
                || !IsSupportedRoute(route)
                || scope == GestureCommandScope.MiniPlayer
                    && route.StartsWith("wheel", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(command)
                || !IsSupportedCommandId(command)
                || !seenRoutes.Add(routeKey))
            {
                continue;
            }

            var label = IsKnownCommand(command)
                ? GetLabel(command)
                : string.IsNullOrWhiteSpace(mapping.Label)
                    ? command
                    : mapping.Label.Trim();
            normalized.Add(CreateMapping(route, command, scope, label));
        }

        return normalized;
    }

    public static List<GestureMappingSettings> ResolveMappingsForScope(
        IEnumerable<GestureMappingSettings>? mappings,
        string? scope)
    {
        var normalizedScope = GestureCommandScope.Normalize(scope);
        var normalizedMappings = NormalizeMappings(mappings);
        if (normalizedScope == GestureCommandScope.MiniPlayer)
        {
            // 既存の共通割り当てで左右シークや下方向の閉じるを上書きしないため、
            // ミニプレイヤーでは専用スコープだけを適用します。
            return normalizedMappings
                .Where(mapping => mapping.Scope.Equals(
                    GestureCommandScope.MiniPlayer,
                    StringComparison.Ordinal))
                .ToList();
        }

        return normalizedMappings
            .GroupBy(static mapping => mapping.Route, StringComparer.Ordinal)
            .Select(group =>
                group.FirstOrDefault(mapping =>
                    mapping.Scope.Equals(normalizedScope, StringComparison.Ordinal))
                ?? group.FirstOrDefault(mapping =>
                    mapping.Scope.Equals(GestureCommandScope.Any, StringComparison.Ordinal)))
            .Where(static mapping => mapping is not null)
            .Select(static mapping => mapping!)
            .ToList();
    }

    public static bool IsKnownCommand(string command)
    {
        return CommandOptions.Any(x => x.Command.Equals(command, StringComparison.Ordinal));
    }

    public static bool IsSupportedCommandId(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 128)
        {
            return false;
        }

        return command.All(static value =>
            char.IsAsciiLetterOrDigit(value)
            || value is '.' or '_' or '-');
    }

    public static bool IsSupportedRoute(string? route)
    {
        if (route is "wheelUp" or "wheelDown")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(route)
            || route.Length > MaximumDirectionCount
            || route.Any(static direction =>
                direction is not ('←' or '→' or '↑' or '↓')))
        {
            return false;
        }

        for (var index = 1; index < route.Length; index++)
        {
            if (route[index] == route[index - 1])
            {
                return false;
            }
        }

        return true;
    }

    public static string GetLabel(string command)
    {
        return CommandOptions.FirstOrDefault(x => x.Command.Equals(command, StringComparison.Ordinal))?.Label
            ?? command;
    }

    private static GestureMappingSettings CreateMapping(
        string route,
        string command,
        string scope = GestureCommandScope.Any,
        string? label = null)
    {
        return new GestureMappingSettings
        {
            Route = route,
            Command = command,
            Label = string.IsNullOrWhiteSpace(label) ? GetLabel(command) : label,
            Scope = GestureCommandScope.Normalize(scope)
        };
    }

    private static IReadOnlyList<string> CreateCommonRoutes()
    {
        var routes = new List<string>();
        for (var length = 1; length <= MaximumDirectionCount; length++)
        {
            AddRoutes(string.Empty, length);
        }

        routes.Add("wheelUp");
        routes.Add("wheelDown");
        return routes;

        void AddRoutes(string prefix, int remaining)
        {
            if (remaining == 0)
            {
                routes.Add(prefix);
                return;
            }

            foreach (var direction in Directions)
            {
                if (prefix.Length > 0 && prefix[^1] == direction)
                {
                    continue;
                }

                AddRoutes(prefix + direction, remaining - 1);
            }
        }
    }
}
