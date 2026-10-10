using System.IO;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class AppPaths
{
    public static string Root { get; } = GetRoot(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationChannelService.Current);

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    public static string SessionFile { get; } = Path.Combine(Root, "session.json");

    public static string WindowMemoryFile { get; } = Path.Combine(Root, "window-memory.json");

    public static string BookmarksFile { get; } = Path.Combine(Root, "bookmarks.json");

    public static string DownloadHistoryFile { get; } = Path.Combine(Root, "downloads", "history.json");

    public static string YouTubeMiniPlayersFile { get; } = Path.Combine(Root, "youtube-mini-players.json");

    public static string WorkspaceDatabaseFile { get; } = Path.Combine(Root, "workspace.db");

    public static string BrowsingHistoryDatabaseFile { get; } = Path.Combine(Root, "browsing-history.db");

    public static string RecentItemsFile { get; } = Path.Combine(Root, "recent-items.json");

    public static string PrivateBrowsingFolder { get; } = Path.Combine(Root, "private");

    public static string RuntimeDiagnosticsFolder { get; } = Path.Combine(Root, "diagnostics");

    public static string ActiveRunMarkerFile { get; } = Path.Combine(RuntimeDiagnosticsFolder, "active-run.marker");

    public static string RestoreInProgressMarkerFile { get; } = Path.Combine(Root, "restore.in-progress");

    public static string WebView2UserDataFolder { get; } = Path.Combine(Root, "WebView2");

    public static string QuickLookWebView2UserDataFolder { get; } = Path.Combine(Root, "QuickLookWebView2");

    public static string ContainerProfilesFile { get; } = Path.Combine(Root, "container-profiles.json");

    public static string ContainerProfilesFolder { get; } = Path.Combine(Root, "containers");

    public static string GetContainerProfileWebView2Folder(string containerProfileId)
    {
        var normalizedId = ContainerProfile.NormalizeId(containerProfileId);

        if (string.Equals(normalizedId, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return WebView2UserDataFolder;
        }

        return Path.Combine(ContainerProfilesFolder, normalizedId, "WebView2");
    }

    internal static string GetRoot(string localApplicationDataFolder, ApplicationChannel channel)
    {
        return Path.Combine(
            localApplicationDataFolder,
            ApplicationChannelService.GetDataDirectoryName(channel));
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WebView2UserDataFolder);
    }
}
