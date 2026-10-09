using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Models;

public sealed class SitePrivacySettings
{
    public List<PopupPermissionRule> PopupRules { get; set; } = [];
}

public sealed class PopupPermissionRule
{
    public string Origin { get; set; } = string.Empty;

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string Policy { get; set; } = PopupPermissionPolicy.Block;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class PopupPermissionPolicy
{
    public const string Allow = "allow";
    public const string Block = "block";
    public const int MaximumRuleCount = 500;

    public static string Normalize(string? value)
    {
        return string.Equals(value, Allow, StringComparison.OrdinalIgnoreCase)
            ? Allow
            : Block;
    }
}

public static class SitePrivacySettingsProfile
{
    public static SitePrivacySettings Normalize(SitePrivacySettings? settings)
    {
        settings ??= new SitePrivacySettings();
        return new SitePrivacySettings
        {
            PopupRules = (settings.PopupRules ?? [])
                .Select(NormalizeRule)
                .Where(static rule => rule is not null)
                .Select(static rule => rule!)
                .GroupBy(
                    static rule => (rule.Origin, rule.ContainerProfileId),
                    static rule => rule)
                .Select(static group => group
                    .OrderByDescending(static rule => rule.UpdatedAt)
                    .First())
                .OrderByDescending(static rule => rule.UpdatedAt)
                .Take(PopupPermissionPolicy.MaximumRuleCount)
                .ToList()
        };
    }

    public static string? GetPopupPolicy(
        SitePrivacySettings? settings,
        string? url,
        string? containerProfileId)
    {
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(url, out var origin))
        {
            return null;
        }

        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        return Normalize(settings).PopupRules.FirstOrDefault(rule =>
                rule.Origin.Equals(origin, StringComparison.Ordinal)
                && rule.ContainerProfileId.Equals(normalizedContainerId, StringComparison.Ordinal))
            ?.Policy;
    }

    public static SitePrivacySettings RecordPopupPolicy(
        SitePrivacySettings? settings,
        string? url,
        string? containerProfileId,
        string policy,
        DateTimeOffset updatedAt)
    {
        var normalized = Normalize(settings);
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(url, out var origin))
        {
            return normalized;
        }

        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        normalized.PopupRules.RemoveAll(rule =>
            rule.Origin.Equals(origin, StringComparison.Ordinal)
            && rule.ContainerProfileId.Equals(normalizedContainerId, StringComparison.Ordinal));
        normalized.PopupRules.Add(new PopupPermissionRule
        {
            Origin = origin,
            ContainerProfileId = normalizedContainerId,
            Policy = PopupPermissionPolicy.Normalize(policy),
            UpdatedAt = updatedAt == default ? DateTimeOffset.UtcNow : updatedAt
        });
        return Normalize(normalized);
    }

    public static SitePrivacySettings RemovePopupPolicy(
        SitePrivacySettings? settings,
        string? url,
        string? containerProfileId)
    {
        var normalized = Normalize(settings);
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(url, out var origin))
        {
            return normalized;
        }

        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        normalized.PopupRules.RemoveAll(rule =>
            rule.Origin.Equals(origin, StringComparison.Ordinal)
            && rule.ContainerProfileId.Equals(normalizedContainerId, StringComparison.Ordinal));
        return normalized;
    }

    private static PopupPermissionRule? NormalizeRule(PopupPermissionRule rule)
    {
        if (!PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(rule.Origin, out var origin))
        {
            return null;
        }

        return new PopupPermissionRule
        {
            Origin = origin,
            ContainerProfileId = ContainerProfile.NormalizeId(rule.ContainerProfileId),
            Policy = PopupPermissionPolicy.Normalize(rule.Policy),
            UpdatedAt = rule.UpdatedAt == default ? DateTimeOffset.UnixEpoch : rule.UpdatedAt
        };
    }
}

public enum SitePermissionDecision
{
    Dismiss,
    AllowOnce,
    AllowAlways,
    Deny
}

public sealed record SitePermissionRequestInfo(
    Guid TabId,
    string SiteName,
    string Origin,
    string ContainerProfileId,
    string ContainerName,
    string PermissionName,
    bool IsScreenCapture,
    bool IsPrivate);

public sealed record SitePermissionEntry(
    string Origin,
    CoreWebView2PermissionKind PermissionKind,
    CoreWebView2PermissionState PermissionState,
    string PermissionName,
    string StateName);

public sealed record PopupPermissionEntry(
    string Origin,
    string Policy,
    string PolicyName);

public sealed record SiteAddressInformation(
    string Url,
    string Origin,
    string Host,
    string ConnectionName,
    bool SupportsSitePermissions);

[Flags]
public enum SiteDataCategory
{
    None = 0,
    Cache = 1,
    CookiesAndSiteData = 2,
    BrowsingHistory = 4,
    DownloadHistory = 8
}

public sealed record SiteDataClearRequest(
    string ContainerProfileId,
    SiteDataCategory Categories,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record SiteDataClearResult(
    int ApplicationBrowsingHistoryDeleted,
    int ApplicationDownloadHistoryDeleted);

public sealed record PrivateBrowsingContext(
    Guid Id,
    string UserDataFolder);

public enum PrivateBrowsingDeleteStatus
{
    Deleted,
    NotFound,
    Failed
}

public sealed record PrivateBrowsingDeleteResult(
    PrivateBrowsingDeleteStatus Status,
    int AttemptCount,
    string? FailureReason = null)
{
    public bool IsSuccess => Status is not PrivateBrowsingDeleteStatus.Failed;
}
