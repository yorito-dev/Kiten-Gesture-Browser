using System.Text.Json;

namespace TabBrowser.Services;

public readonly record struct StreamingPlaybackFailureMessage(int MediaErrorCode);

public static class StreamingPlaybackCompatibilityService
{
    public const string MessageType = "streamingPlaybackFailure";

    public static bool IsEligiblePageUrl(string? value)
    {
        if (!TryGetWebUri(value, out var uri)
            || uri.IsLoopback
            || YouTubeUrlService.IsYouTubeUrl(value!)

            )
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
