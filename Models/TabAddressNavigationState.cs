using TabBrowser.Services;

namespace TabBrowser.Models;

internal sealed class TabAddressNavigationState
{
    private AppRequest? _appRequest;
    private ulong? _navigationId;
    private string? _requestUrl;
    private bool _requestHasCertificateError;
    private bool _documentCommitted;

    internal string? PendingAddress { get; private set; }
    internal string? RequestUrl => _documentCommitted ? null : _requestUrl;
    internal bool HasPendingNavigation => _appRequest is not null || RequestUrl is not null;
    internal string? InternalDocumentUrl { get; private set; }
    internal string? CertificateErrorUrl { get; private set; }

    internal void ExpectAppRequest(string? requestUrl, string address, string? internalDocumentUrl = null)
        => _appRequest = new(requestUrl, address, internalDocumentUrl);

    internal void ClearAppRequest() => _appRequest = null;

    internal bool TryStart(ulong navigationId, string url, bool isRedirect)
    {
        if (isRedirect && _navigationId is { } currentId && currentId != navigationId)
        {
            return false;
        }

        if (isRedirect && _navigationId == navigationId)
        {
            if (!SitePrivacyService.IsCurrentPageCertificateRequest(_requestUrl, url))
            {
                _requestHasCertificateError = false;
            }
        }
        else
        {
            var appRequest = !isRedirect && _appRequest is { } request && request.Matches(url)
                ? request
                : null;
            _appRequest = null;
            _navigationId = navigationId;
            _documentCommitted = false;
            _requestHasCertificateError = false;
            PendingAddress = appRequest?.Address;
            InternalDocumentUrl = appRequest?.InternalDocumentUrl;
        }

        _requestUrl = url;
        return true;
    }

    internal bool TryCommit(ulong navigationId)
    {
        if (_navigationId != navigationId || _documentCommitted)
        {
            return false;
        }

        _documentCommitted = true;
        PendingAddress = null;
        CertificateErrorUrl = _requestHasCertificateError ? _requestUrl : null;
        return true;
    }

    internal void ObserveSameDocumentSource(string source)
    {
        if (_appRequest is { } request && request.Matches(source))
        {
            _appRequest = null;
        }
    }

    internal bool RecordCertificateError(string requestUrl)
    {
        if (!SitePrivacyService.IsCurrentPageCertificateRequest(_requestUrl, requestUrl))
        {
            return false;
        }

        _requestHasCertificateError = true;
        if (_documentCommitted)
        {
            CertificateErrorUrl = requestUrl;
            return true;
        }

        return false;
    }

    internal bool TryComplete(ulong navigationId, bool certificateFailure)
    {
        if (_navigationId != navigationId)
        {
            return false;
        }

        if (certificateFailure && !_documentCommitted)
        {
            // 完了通知が証明書エラー通知より先に届く場合も表示する。
            CertificateErrorUrl ??= _requestUrl;
        }

        ClearNavigation();
        return true;
    }

    internal void Reset()
    {
        ClearAppRequest();
        ClearNavigation();
        CertificateErrorUrl = null;
    }

    private void ClearNavigation()
    {
        _navigationId = null;
        _requestUrl = null;
        _requestHasCertificateError = false;
        _documentCommitted = false;
        PendingAddress = null;
        InternalDocumentUrl = null;
    }

    private sealed record AppRequest(string? RequestUrl, string Address, string? InternalDocumentUrl)
    {
        internal bool Matches(string source)
            => RequestUrl is null
                ? source.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)
                : Uri.TryCreate(RequestUrl, UriKind.Absolute, out var expected)
                    && Uri.TryCreate(source, UriKind.Absolute, out var actual)
                    && string.Equals(expected.AbsoluteUri, actual.AbsoluteUri, StringComparison.Ordinal);
    }
}
