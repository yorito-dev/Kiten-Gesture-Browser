using System.Text.Json;

namespace TabBrowser.Services;

public enum AmazonPrimeVideoReadiness
{
    Unknown,
    Pending,
    Ready,
    SpinnerBlocked,
    EmeUnavailable,
    NavigationFailed,
    ProbeFailed
}

public readonly record struct AmazonPrimeVideoProbeResult(
    bool PrimeVideoPageFound,
    bool PlayButtonFound,
    bool SpinnerVisible,
    bool AriaDisabled,
    bool EmeAvailable)
{
    public AmazonPrimeVideoReadiness Readiness => !PrimeVideoPageFound
        ? AmazonPrimeVideoReadiness.Unknown
        : !EmeAvailable
        ? AmazonPrimeVideoReadiness.EmeUnavailable
        : PlayButtonFound && !SpinnerVisible && !AriaDisabled
            ? AmazonPrimeVideoReadiness.Ready
            : SpinnerVisible || AriaDisabled
                ? AmazonPrimeVideoReadiness.SpinnerBlocked
                : AmazonPrimeVideoReadiness.ProbeFailed;
}

public static class AmazonPrimeVideoService
{
    public const string CompatibilityActiveMessageType = "amazonPrimeVideoCompatibilityActive";

    public static string BuildWebViewBridgeSuppressionScript(string? webMessageToken = null)
    {
        var tokenJson = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        var messageTypeJson = JsonSerializer.Serialize(CompatibilityActiveMessageType);
        return $$"""
            (() => {
                const webMessageToken = {{tokenJson}};
                const compatibilityActiveMessageType = {{messageTypeJson}};
                const host = location.hostname.toLowerCase();
                if (host !== 'amazon.co.jp' && host !== 'www.amazon.co.jp') {
                    return;
                }

                const nativePostMessage = globalThis.chrome?.webview?.postMessage?.bind(globalThis.chrome.webview);
                const path = location.pathname;
                const directMatch = path.match(/\/gp\/video\/(?:detail|watch)\/([a-z0-9]{10})(?:\/|$)/i);
                const dpMatch = path.match(/\/dp\/([a-z0-9]{10})(?:\/|$)/i);
                const redirectMarkerKey = '__tabBrowserPrimeVideoRedirectAsin';
                const reloadMarkerKey = '__tabBrowserPrimeVideoReloadPath';
                const hasWebViewBridge = () => !!globalThis.chrome
                    && Object.prototype.hasOwnProperty.call(globalThis.chrome, 'webview');

                const suppressWebViewBridge = () => {
                    const started = Date.now();
                    let bridgeWasFound = false;
                    let absentChecks = 0;
                    const removeBridge = () => {
                        try {
                            if (hasWebViewBridge()) {
                                if (!bridgeWasFound) {
                                    nativePostMessage?.({
                                        type: compatibilityActiveMessageType,
                                        tabBrowserToken: webMessageToken
                                    });
                                }

                                bridgeWasFound = true;
                                absentChecks = 0;
                                delete globalThis.chrome.webview;
                            } else if (bridgeWasFound) {
                                absentChecks++;
                            }
                        } catch {
                            // WebView2がブリッジを追加するまで、短時間だけ再試行する。
                        }

                        if (absentChecks >= 2 || Date.now() - started >= 10000) {
                            clearInterval(timer);
                        }
                    };
                    const timer = setInterval(removeBridge, 0);
                    removeBridge();
                };

                if (directMatch) {
                    try {
                        sessionStorage.setItem(redirectMarkerKey, directMatch[1].toUpperCase());
                    } catch {
                        // sessionStorageが使えない場合も、現在の文書だけは互換表示にする。
                    }

                    suppressWebViewBridge();
                    return;
                }

                if (!dpMatch) {
                    return;
                }

                let redirectAsin = '';
                let reloadPath = '';
                try {
                    redirectAsin = sessionStorage.getItem(redirectMarkerKey) ?? '';
                    reloadPath = sessionStorage.getItem(reloadMarkerKey) ?? '';
                    sessionStorage.removeItem(redirectMarkerKey);
                    sessionStorage.removeItem(reloadMarkerKey);
                } catch {
                    // 読み出せない場合は、下のDOM判定へ進む。
                }

                if (redirectAsin.toUpperCase() === dpMatch[1].toUpperCase()
                    || reloadPath === path) {
                    suppressWebViewBridge();
                    return;
                }

                const primeVideoElementSelector = [
                    '[data-testid="dp-atf-play-button-spinner"]',
                    '[data-testid="dp-atf-play-button"]',
                    '[data-testid*="play-button"][aria-disabled]'
                ].join(',');
                const detectionStarted = Date.now();
                const detectPrimeVideoPage = () => {
                    if (!document.querySelector(primeVideoElementSelector)) {
                        if (Date.now() - detectionStarted >= 10000) {
                            clearInterval(detectionTimer);
                        }
                        return;
                    }

                    clearInterval(detectionTimer);
                    if (!hasWebViewBridge()) {
                        return;
                    }

                    try {
                        sessionStorage.setItem(reloadMarkerKey, path);
                    } catch {
                        suppressWebViewBridge();
                        return;
                    }

                    location.reload();
                };
                const detectionTimer = setInterval(detectPrimeVideoPage, 25);
                detectPrimeVideoPage();
            })()
            """;
    }

    public static string BuildWebViewBridgeSuppressionProbeScript()
    {
        return """
            (() => !globalThis.chrome
                || !Object.prototype.hasOwnProperty.call(globalThis.chrome, 'webview'))()
            """;
    }

    public static bool IsWebViewBridgeSuppressedProbeResult(string? value)
    {
        return string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public const string PreparationFailureTitle =
        "このタブでは映像を再生できません";

    public const string PreparationFailureMessage =
        "Amazon Prime Videoは埋め込みブラウザーとの互換性により、このタブ内で再生を準備できませんでした。";

    public const string EdgeContinuationMessage =
        "「Edgeコンパクト表示で視聴」を押すと、同じ作品をMicrosoft Edgeの小さな専用ウィンドウで開きます。現在のタブは残るため、視聴後はEdgeを閉じてTabBrowserでそのまま作業を続けられます。";

    public const string PreparationFailureExplanation =
        PreparationFailureMessage + "\n" + EdgeContinuationMessage;

    public static bool IsPlaybackPageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(uri.Host, "amazon.co.jp", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "www.amazon.co.jp", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith("/gp/video/detail/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/gp/video/watch/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPotentialPlaybackPageUrl(string? value)
    {
        if (IsPlaybackPageUrl(value))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(uri.Host, "amazon.co.jp", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "www.amazon.co.jp", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dpIndex = Array.FindIndex(
            segments,
            segment => string.Equals(segment, "dp", StringComparison.OrdinalIgnoreCase));
        return dpIndex >= 0
            && dpIndex + 1 < segments.Length
            && segments[dpIndex + 1].Length == 10
            && segments[dpIndex + 1].All(char.IsAsciiLetterOrDigit);
    }

    public static string BuildReadinessProbeScript()
    {
        return """
            (() => {
                const spinner = document.querySelector('[data-testid="dp-atf-play-button-spinner"]');
                const playButton = spinner?.closest('button,[role="button"],[aria-disabled]')
                    ?? document.querySelector('[data-testid="dp-atf-play-button"]')
                    ?? document.querySelector('[data-testid*="play-button"][aria-disabled]');
                const spinnerStyle = spinner ? window.getComputedStyle(spinner) : null;
                const spinnerVisible = !!spinner
                    && spinner.getClientRects().length > 0
                    && spinnerStyle?.display !== 'none'
                    && spinnerStyle?.visibility !== 'hidden';
                const ariaDisabled = playButton?.getAttribute('aria-disabled') === 'true'
                    || playButton?.hasAttribute('disabled') === true;
                return {
                    primeVideoPageFound: !!spinner || !!playButton,
                    playButtonFound: !!playButton,
                    spinnerVisible,
                    ariaDisabled,
                    emeAvailable: typeof navigator.requestMediaKeySystemAccess === 'function'
                };
            })()
            """;
    }

    public static bool TryParseProbeResult(
        string? value,
        out AmazonPrimeVideoProbeResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetBoolean(root, "primeVideoPageFound", out var primeVideoPageFound)
                || !TryGetBoolean(root, "playButtonFound", out var playButtonFound)
                || !TryGetBoolean(root, "spinnerVisible", out var spinnerVisible)
                || !TryGetBoolean(root, "ariaDisabled", out var ariaDisabled)
                || !TryGetBoolean(root, "emeAvailable", out var emeAvailable))
            {
                return false;
            }

            result = new AmazonPrimeVideoProbeResult(
                primeVideoPageFound,
                playButtonFound,
                spinnerVisible,
                ariaDisabled,
                emeAvailable);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetBoolean(
        JsonElement root,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var property)
            || (property.ValueKind != JsonValueKind.True
                && property.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}
