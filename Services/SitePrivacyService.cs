using System.IO;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class SitePrivacyService
{
    public static bool TryCreateAddressInformation(
        string? url,
        out SiteAddressInformation information)
    {
        information = default!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(uri.AbsoluteUri, out var origin))
            {
                return false;
            }

            information = new SiteAddressInformation(
                uri.AbsoluteUri,
                origin,
                uri.Host,
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? "HTTPSで接続"
                    : "HTTPで接続（暗号化されていません）",
                SupportsSitePermissions: true);
            return true;
        }

        if (uri.IsFile)
        {
            information = new SiteAddressInformation(
                uri.AbsoluteUri,
                "ローカルファイル",
                Path.GetFileName(uri.LocalPath),
                "ローカルファイル",
                SupportsSitePermissions: false);
            return true;
        }

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            information = new SiteAddressInformation(
                uri.AbsoluteUri,
                uri.AbsoluteUri,
                "ブラウザー内部ページ",
                "内部ページ",
                SupportsSitePermissions: false);
            return true;
        }

        return false;
    }

    public static IReadOnlyList<SitePermissionEntry> GetEntriesForOrigin(
        IEnumerable<SitePermissionEntry> entries,
        string? origin)
    {
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(origin, out var normalizedOrigin))
        {
            return [];
        }

        return entries
            .Where(entry => PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(entry.Origin, out var entryOrigin)
                && entryOrigin.Equals(normalizedOrigin, StringComparison.Ordinal))
            .OrderBy(static entry => entry.PermissionName, StringComparer.CurrentCulture)
            .ToList();
    }

    public static bool IsManagedPermission(CoreWebView2PermissionKind kind)
    {
        return kind is CoreWebView2PermissionKind.Camera
            or CoreWebView2PermissionKind.Microphone
            or CoreWebView2PermissionKind.Notifications
            or CoreWebView2PermissionKind.Geolocation;
    }

    public static string GetPermissionName(CoreWebView2PermissionKind kind)
    {
        return kind switch
        {
            CoreWebView2PermissionKind.Camera => "カメラ",
            CoreWebView2PermissionKind.Microphone => "マイク",
            CoreWebView2PermissionKind.Notifications => "通知",
            CoreWebView2PermissionKind.Geolocation => "位置情報",
            _ => kind.ToString()
        };
    }

    public static string GetPermissionStateName(CoreWebView2PermissionState state)
    {
        return state switch
        {
            CoreWebView2PermissionState.Allow => "許可",
            CoreWebView2PermissionState.Deny => "拒否",
            _ => "既定"
        };
    }

    public static async Task<IReadOnlyList<SitePermissionEntry>> GetPermissionEntriesAsync(
        CoreWebView2Profile profile)
    {
        var settings = await profile.GetNonDefaultPermissionSettingsAsync();
        return settings
            .Where(static setting => IsManagedPermission(setting.PermissionKind))
            .Select(static setting => new SitePermissionEntry(
                setting.PermissionOrigin,
                setting.PermissionKind,
                setting.PermissionState,
                GetPermissionName(setting.PermissionKind),
                GetPermissionStateName(setting.PermissionState)))
            .OrderBy(static entry => entry.Origin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.PermissionName, StringComparer.CurrentCulture)
            .ToList();
    }

    public static Task SetPermissionStateAsync(
        CoreWebView2Profile profile,
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state)
    {
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(origin, out var normalizedOrigin))
        {
            throw new ArgumentException("有効なHTTP/HTTPSオリジンではありません。", nameof(origin));
        }

        return profile.SetPermissionStateAsync(kind, normalizedOrigin, state);
    }

    public static CoreWebView2BrowsingDataKinds GetBrowsingDataKinds(SiteDataCategory categories)
    {
        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (categories.HasFlag(SiteDataCategory.Cache))
        {
            kinds |= CoreWebView2BrowsingDataKinds.DiskCache
                | CoreWebView2BrowsingDataKinds.CacheStorage;
        }

        if (categories.HasFlag(SiteDataCategory.CookiesAndSiteData))
        {
            kinds |= CoreWebView2BrowsingDataKinds.Cookies
                | CoreWebView2BrowsingDataKinds.AllDomStorage
                | CoreWebView2BrowsingDataKinds.IndexedDb
                | CoreWebView2BrowsingDataKinds.FileSystems
                | CoreWebView2BrowsingDataKinds.ServiceWorkers;
        }

        if (categories.HasFlag(SiteDataCategory.BrowsingHistory))
        {
            kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
        }

        if (categories.HasFlag(SiteDataCategory.DownloadHistory))
        {
            kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
        }

        return kinds;
    }

    public static Task ClearProfileDataAsync(
        CoreWebView2Profile profile,
        SiteDataCategory categories,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var kinds = GetBrowsingDataKinds(categories);
        if (kinds == 0)
        {
            return Task.CompletedTask;
        }

        if (from is not null && to is not null)
        {
            return profile.ClearBrowsingDataAsync(
                kinds,
                from.Value.UtcDateTime,
                to.Value.UtcDateTime);
        }

        return profile.ClearBrowsingDataAsync(kinds);
    }
}
