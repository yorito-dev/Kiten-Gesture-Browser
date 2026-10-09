using System.Text.Json;

namespace TabBrowser.Services;

public readonly record struct StreamingPlaybackFailureMessage(int MediaErrorCode);

public static class StreamingPlaybackCompatibilityService
{
    public const string MessageType = "streamingPlaybackFailure";
    public const string CompatibilityActiveMessageType = "streamingPlaybackCompatibilityActive";

    public const string RetryStartingMessage =
        "映像再生エラーを検出。互換再試行中: この再読み込みだけページとのTabBrowser連携を一時停止します（User-Agent・Cookie・DRM設定は変更しません）";

    public const string RetryCompletedMessage =
        "ページとのTabBrowser連携を一時停止した互換モードで再読み込みしました";

    public const string RetryUnavailableMessage =
        "映像の互換モード再試行を開始できませんでした";

    public static bool IsEligiblePageUrl(string? value)
    {
        if (!TryGetWebUri(value, out var uri)
            || uri.IsLoopback
            || YouTubeUrlService.IsYouTubeUrl(value!)
            || AmazonPrimeVideoService.IsPotentialPlaybackPageUrl(value))
        {
            return false;
        }

        return true;
    }

    public static bool IsTrustedTopLevelSource(string? pageUrl, string? messageSource)
    {
        return TryGetWebUri(pageUrl, out var pageUri)
            && TryGetWebUri(messageSource, out var sourceUri)
            && string.Equals(pageUri.Scheme, sourceUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pageUri.Host, sourceUri.Host, StringComparison.OrdinalIgnoreCase)
            && pageUri.Port == sourceUri.Port;
    }

    public static bool TryGetCompatibilityRetryTarget(
        string? pageUrl,
        out string origin,
        out string retryKey)
    {
        origin = string.Empty;
        retryKey = string.Empty;
        if (!IsEligiblePageUrl(pageUrl)
            || !TryGetWebUri(pageUrl, out var uri))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        retryKey = uri.GetLeftPart(UriPartial.Query);
        return true;
    }

    public static string BuildFailureBridgeScript(string webMessageToken)
    {
        var serializedToken = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        return $$"""
            (() => {
                if (window.top !== window) {
                    return;
                }

                const bridgeKey = "__tabBrowserStreamingPlaybackFailure";
                const bridgeVersion = 2;
                const token = {{serializedToken}};
                const nativePostMessage = window.chrome?.webview?.postMessage?.bind(window.chrome.webview);
                const existing = window[bridgeKey];
                if (existing?.version === bridgeVersion) {
                    return;
                }

                existing?.dispose?.();
                const reportedVideos = new WeakSet();
                const resolveVideo = target => {
                    if (target instanceof HTMLVideoElement) {
                        return target;
                    }

                    return target instanceof HTMLSourceElement
                        ? target.parentElement?.closest("video") ?? null
                        : null;
                };
                const isVisiblePrimaryVideo = video => {
                    const rect = video.getBoundingClientRect();
                    const style = window.getComputedStyle(video);
                    return rect.width >= 320
                        && rect.height >= 180
                        && rect.right > 0
                        && rect.bottom > 0
                        && rect.left < window.innerWidth
                        && rect.top < window.innerHeight
                        && style.display !== "none"
                        && style.visibility !== "hidden";
                };
                const handleMediaError = event => {
                    const video = resolveVideo(event.target);
                    const errorCode = Number(video?.error?.code);
                    if (!video
                        || reportedVideos.has(video)
                        || ![2, 3, 4].includes(errorCode)
                        || !isVisiblePrimaryVideo(video)
                        || !nativePostMessage) {
                        return;
                    }

                    reportedVideos.add(video);
                    nativePostMessage({
                        type: "{{MessageType}}",
                        tabBrowserToken: token,
                        errorCode
                    });
                };

                document.addEventListener("error", handleMediaError, true);
                Object.defineProperty(window, bridgeKey, {
                    value: {
                        version: bridgeVersion,
                        dispose: () => document.removeEventListener("error", handleMediaError, true)
                    },
                    configurable: true
                });
            })();
            """;
    }

    public static string BuildCompatibilityRetryScript(
        string webMessageToken,
        string expectedOrigin)
    {
        var serializedToken = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        var serializedOrigin = JsonSerializer.Serialize(expectedOrigin ?? string.Empty);
        var serializedMessageType = JsonSerializer.Serialize(CompatibilityActiveMessageType);
        return $$"""
            (() => {
                if (window.top !== window || location.origin !== {{serializedOrigin}}) {
                    return;
                }

                const token = {{serializedToken}};
                const messageType = {{serializedMessageType}};
                const nativePostMessage = globalThis.chrome?.webview?.postMessage?.bind(globalThis.chrome.webview);
                const hasWebViewBridge = () => !!globalThis.chrome
                    && Object.prototype.hasOwnProperty.call(globalThis.chrome, 'webview');
                const started = Date.now();
                let bridgeWasFound = false;
                let absentChecks = 0;
                const removeBridge = () => {
                    try {
                        if (hasWebViewBridge()) {
                            if (!bridgeWasFound) {
                                nativePostMessage?.({
                                    type: messageType,
                                    tabBrowserToken: token
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
            })()
            """;
    }

    public static bool TryParse(
        JsonElement root,
        out StreamingPlaybackFailureMessage message)
    {
        message = default;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), MessageType, StringComparison.Ordinal)
            || !root.TryGetProperty("errorCode", out var errorCodeProperty)
            || errorCodeProperty.ValueKind != JsonValueKind.Number
            || !errorCodeProperty.TryGetInt32(out var errorCode)
            || errorCode is < 2 or > 4)
        {
            return false;
        }

        message = new StreamingPlaybackFailureMessage(errorCode);
        return true;
    }

    private static bool TryGetWebUri(string? value, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            && (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
