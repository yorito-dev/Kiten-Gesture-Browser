using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class SitePrivacyInteractionCoordinator
{
    private readonly Dictionary<string, string> _privatePopupPolicies = new(StringComparer.Ordinal);
    private BlockedPopupRequest? _pendingBlockedPopup;
    private int _siteInformationGeneration;
    private Guid? _siteInformationTabId;
    private SiteAddressInformation? _siteInformation;

    public string? GetPrivatePopupPolicy(string? sourceUrl)
    {
        return TryNormalizeOrigin(sourceUrl, out var origin)
            ? _privatePopupPolicies.GetValueOrDefault(origin)
            : null;
    }

    public bool SetPrivatePopupPolicy(string? sourceUrl, string policy)
    {
        if (!TryNormalizeOrigin(sourceUrl, out var origin))
        {
            return false;
        }

        _privatePopupPolicies[origin] = PopupPermissionPolicy.Normalize(policy);
        return true;
    }

    public bool RemovePrivatePopupPolicy(string? sourceUrl)
    {
        return TryNormalizeOrigin(sourceUrl, out var origin)
            && _privatePopupPolicies.Remove(origin);
    }

    public IReadOnlyList<PrivatePopupPolicyEntry> CapturePrivatePopupPolicies()
    {
        return _privatePopupPolicies
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => new PrivatePopupPolicyEntry(pair.Key, pair.Value))
            .ToList();
    }

    public void SetPendingBlockedPopup(Guid tabId, string sourceUrl, string popupUrl)
    {
        _pendingBlockedPopup = new BlockedPopupRequest(tabId, sourceUrl, popupUrl);
    }

    public BlockedPopupRequest? TakePendingBlockedPopup()
    {
        var pending = _pendingBlockedPopup;
        _pendingBlockedPopup = null;
        return pending;
    }

    public int BeginSiteInformation(Guid tabId, SiteAddressInformation information)
    {
        _siteInformationTabId = tabId;
        _siteInformation = information;
        return ++_siteInformationGeneration;
    }

    public bool IsCurrentSiteInformationGeneration(int generation)
    {
        return generation == _siteInformationGeneration;
    }

    public bool IsCurrentSiteInformation(int generation, Guid tabId, string url)
    {
        return IsCurrentSiteInformationGeneration(generation)
            && IsCurrentSiteInformationContext(tabId, url);
    }

    public bool IsCurrentSiteInformationContext(Guid tabId, string url)
    {
        return _siteInformationTabId == tabId
            && _siteInformation is { } information
            && string.Equals(information.Url, url, StringComparison.OrdinalIgnoreCase);
    }

    public SiteInformationContextSnapshot CaptureSiteInformationContext()
    {
        return new SiteInformationContextSnapshot(
            _siteInformationGeneration,
            _siteInformationTabId,
            _siteInformation);
    }

    public void CloseSiteInformation()
    {
        _siteInformationGeneration++;
        _siteInformationTabId = null;
        _siteInformation = null;
    }

    private static bool TryNormalizeOrigin(string? sourceUrl, out string origin)
    {
        return PageZoomPreferenceSettingsProfile.TryNormalizeOrigin(sourceUrl, out origin);
    }
}

internal readonly record struct PrivatePopupPolicyEntry(string Origin, string Policy);

internal sealed record BlockedPopupRequest(Guid TabId, string SourceUrl, string PopupUrl);

internal readonly record struct SiteInformationContextSnapshot(
    int Generation,
    Guid? TabId,
    SiteAddressInformation? Information);
