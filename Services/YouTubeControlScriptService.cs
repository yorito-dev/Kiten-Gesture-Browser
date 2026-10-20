using System.Globalization;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeControlScriptService
{
    public const double DefaultSpeed = 1.0;
    public const double MinimumSpeed = 0.25;
    public const double MaximumSpeed = 2.0;
    private const double SpeedComparisonTolerance = 0.01;
    internal const string AdvertisementBlockedMessage = "広告の再生中は操作できません";
    internal const string AdvertisementPlayingExpression = """
        (() => {
            const adPlayer = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
            return Boolean(adPlayer?.classList?.contains("ad-showing")
                || adPlayer?.classList?.contains("ad-interrupting"));
        })()
        """;

    internal static bool IsAdvertisementBlockedResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() == "advertisement";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static double? NormalizePlaybackPosition(double? positionSeconds)
    {
        return positionSeconds is { } position
            && double.IsFinite(position)
            && position >= 0
            ? Math.Max(0, position)
            : null;
    }

    public static double? NormalizePlaybackSpeed(double? speed)
    {
        return speed is { } playbackSpeed
            && double.IsFinite(playbackSpeed)
            && playbackSpeed > 0
            ? Math.Max(MinimumSpeed, Math.Min(MaximumSpeed, playbackSpeed))
            : null;
    }

    public static double? RememberNonDefaultPlaybackSpeed(double? speed, double? previousSpeed)
    {
        var normalizedSpeed = NormalizePlaybackSpeed(speed);
        if (normalizedSpeed.HasValue && !IsDefaultPlaybackSpeed(normalizedSpeed.Value))
        {
            return normalizedSpeed.Value;
        }

        var normalizedPreviousSpeed = NormalizePlaybackSpeed(previousSpeed);
        return normalizedPreviousSpeed.HasValue && !IsDefaultPlaybackSpeed(normalizedPreviousSpeed.Value)
            ? normalizedPreviousSpeed.Value
            : null;
    }

    public static double ResolvePlaybackSpeedToggleTarget(
        double? currentSpeed,
        double? previousSpeed,
        out double? updatedPreviousSpeed)
    {
        var normalizedCurrentSpeed = NormalizePlaybackSpeed(currentSpeed) ?? DefaultSpeed;
        updatedPreviousSpeed = RememberNonDefaultPlaybackSpeed(normalizedCurrentSpeed, previousSpeed);
        return IsDefaultPlaybackSpeed(normalizedCurrentSpeed)
            ? updatedPreviousSpeed ?? DefaultSpeed
            : DefaultSpeed;
    }

    private static bool IsDefaultPlaybackSpeed(double speed)
    {
        return Math.Abs(speed - DefaultSpeed) < SpeedComparisonTolerance;
    }

    public static string BuildBestQualityScript()
    {
        return """
            (() => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const order = ["highres", "hd2160", "hd1440", "hd1080", "hd720", "large", "medium", "small", "tiny", "auto"];
                const available = player && typeof player.getAvailableQualityLevels === "function"
                    ? player.getAvailableQualityLevels()
                    : [];
                let quality = "";
                for (const candidate of order) {
                    if (available.includes(candidate)) {
                        quality = candidate;
                        break;
                    }
                }

                if (!quality && available.length > 0) {
                    quality = available[0];
                }

                if (player && quality) {
                    if (typeof player.setPlaybackQualityRange === "function") {
                        player.setPlaybackQualityRange(quality, quality);
                    }

                    if (typeof player.setPlaybackQuality === "function") {
                        player.setPlaybackQuality(quality);
                    }
                }

                return {
                    ok: Boolean(quality),
                    quality,
                    available: available.join(",")
                };
            })();
            """;
    }

    public static string BuildBestQualityReadyBridgeScript()
    {
        return """
            (() => {
                const bridgeKey = "__tabBrowserYouTubeBestQualityReadyBridge";
                const bridgeVersion = 1;
                const existing = window[bridgeKey];
                if (existing?.version === bridgeVersion) {
                    existing.tryApply?.();
                    return { ok: true, installed: false };
                }

                const order = ["highres", "hd2160", "hd1440", "hd1080", "hd720", "large", "medium", "small", "tiny", "auto"];
                const state = {
                    version: bridgeVersion,
                    appliedVideoIdentity: "",
                    tryApply: null
                };
                const getPlayer = () => document.getElementById("movie_player")
                    || document.querySelector(".html5-video-player");
                const getVideoIdentity = player => {
                    const videoData = typeof player?.getVideoData === "function"
                        ? player.getVideoData()
                        : null;
                    return String(videoData?.video_id || videoData?.videoId || location.href || "");
                };
                state.tryApply = () => {
                    const video = document.querySelector("video");
                    const player = getPlayer();
                    if (!video || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !player) {
                        return false;
                    }

                    const videoIdentity = getVideoIdentity(player);
                    if (!videoIdentity || state.appliedVideoIdentity === videoIdentity) {
                        return state.appliedVideoIdentity === videoIdentity;
                    }

                    const available = typeof player.getAvailableQualityLevels === "function"
                        ? player.getAvailableQualityLevels()
                        : [];
                    let quality = "";
                    for (const candidate of order) {
                        if (available.includes(candidate)) {
                            quality = candidate;
                            break;
                        }
                    }

                    if (!quality && available.length > 0) {
                        quality = available[0];
                    }

                    if (!quality) {
                        return false;
                    }

                    if (typeof player.setPlaybackQualityRange === "function") {
                        player.setPlaybackQualityRange(quality, quality);
                    }

                    if (typeof player.setPlaybackQuality === "function") {
                        player.setPlaybackQuality(quality);
                    }

                    state.appliedVideoIdentity = videoIdentity;
                    return true;
                };

                const tryApply = () => state.tryApply();
                document.addEventListener("loadedmetadata", tryApply, true);
                document.addEventListener("canplay", tryApply, true);
                document.addEventListener("playing", tryApply, true);
                document.addEventListener("yt-navigate-finish", tryApply, true);
                window[bridgeKey] = state;
                state.tryApply();
                return { ok: true, installed: true };
            })();
            """;
    }

    public static string ResolveMiniPlayerQualityCap(
        double displayWidth,
        bool isFullScreen,
        int lowWidthThreshold = YouTubeMiniPlayerQualitySettingsProfile.DefaultLowWidthThreshold,
        int highWidthThreshold = YouTubeMiniPlayerQualitySettingsProfile.DefaultHighWidthThreshold)
    {
        if (isFullScreen)
        {
            return "highres";
        }

        var normalizedThresholds = YouTubeMiniPlayerQualitySettingsProfile.Normalize(
            lowWidthThreshold,
            highWidthThreshold);
        if (!double.IsFinite(displayWidth)
            || displayWidth < normalizedThresholds.LowWidthThreshold)
        {
            return "hd720";
        }

        return displayWidth < normalizedThresholds.HighWidthThreshold
            ? "hd1080"
            : "hd1440";
    }

    public static string BuildMiniPlayerSizeBasedQualityScript(
        bool isEnabled,
        double displayWidth,
        bool isFullScreen,
        int lowWidthThreshold = YouTubeMiniPlayerQualitySettingsProfile.DefaultLowWidthThreshold,
        int highWidthThreshold = YouTubeMiniPlayerQualitySettingsProfile.DefaultHighWidthThreshold)
    {
        var qualityCap = ResolveMiniPlayerQualityCap(
            displayWidth,
            isFullScreen,
            lowWidthThreshold,
            highWidthThreshold);
        var serializedQualityCap = JsonSerializer.Serialize(qualityCap);
        var serializedEnabled = JsonSerializer.Serialize(isEnabled);
        return $$"""
            (() => {
                const bridgeKey = "__tabBrowserYouTubeMiniPlayerSizeBasedQuality";
                const bridgeVersion = 1;
                const enabled = {{serializedEnabled}};
                const requestedQualityCap = {{serializedQualityCap}};
                const existing = window[bridgeKey];
                if (!enabled) {
                    if (existing?.appliedByBridge && !existing.manualVideoIdentity) {
                        existing.releaseRange?.();
                    }
                    existing?.dispose?.();
                    delete window[bridgeKey];
                    return { ok: true, enabled: false };
                }

                if (existing?.version === bridgeVersion) {
                    existing.qualityCap = requestedQualityCap;
                    existing.tryApply?.();
                    return {
                        ok: true,
                        enabled: true,
                        installed: false,
                        qualityCap: requestedQualityCap,
                        manual: Boolean(existing.manualVideoIdentity)
                    };
                }

                existing?.dispose?.();
                const qualityOrder = [
                    "highres", "hd4320", "hd2880", "hd2160", "hd1440",
                    "hd1080", "hd720", "large", "medium", "small", "tiny", "auto"
                ];
                const state = {
                    version: bridgeVersion,
                    qualityCap: requestedQualityCap,
                    appliedKey: "",
                    appliedByBridge: false,
                    manualVideoIdentity: "",
                    lastVideoIdentity: "",
                    tryApply: null,
                    releaseRange: null,
                    dispose: null
                };
                const getPlayer = () => document.getElementById("movie_player")
                    || document.querySelector(".html5-video-player");
                const getVideoIdentity = player => {
                    const videoData = typeof player?.getVideoData === "function"
                        ? player.getVideoData()
                        : null;
                    return String(videoData?.video_id || videoData?.videoId || location.href || "");
                };
                const selectQuality = available => {
                    const capIndex = state.qualityCap === "highres"
                        ? 0
                        : Math.max(0, qualityOrder.indexOf(state.qualityCap));
                    for (let index = capIndex; index < qualityOrder.length; index++) {
                        if (available.includes(qualityOrder[index])) {
                            return qualityOrder[index];
                        }
                    }
                    return "";
                };
                state.releaseRange = () => {
                    const player = getPlayer();
                    if (typeof player?.setPlaybackQualityRange === "function") {
                        player.setPlaybackQualityRange("auto", "auto");
                    }
                    state.appliedByBridge = false;
                    state.appliedKey = "";
                };
                state.tryApply = () => {
                    const video = document.querySelector("video");
                    const player = getPlayer();
                    if (!video || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !player) {
                        return false;
                    }

                    const videoIdentity = getVideoIdentity(player);
                    if (!videoIdentity) {
                        return false;
                    }
                    if (state.lastVideoIdentity !== videoIdentity) {
                        state.lastVideoIdentity = videoIdentity;
                        state.manualVideoIdentity = "";
                        state.appliedKey = "";
                        state.appliedByBridge = false;
                    }
                    if (state.manualVideoIdentity === videoIdentity) {
                        return true;
                    }

                    const appliedKey = `${videoIdentity}|${state.qualityCap}`;
                    if (state.appliedKey === appliedKey) {
                        return true;
                    }
                    const available = typeof player.getAvailableQualityLevels === "function"
                        ? player.getAvailableQualityLevels()
                        : [];
                    const quality = selectQuality(available);
                    if (!quality) {
                        return false;
                    }
                    if (typeof player.setPlaybackQualityRange === "function") {
                        player.setPlaybackQualityRange(quality, quality);
                    }
                    if (typeof player.setPlaybackQuality === "function") {
                        player.setPlaybackQuality(quality);
                    }
                    state.appliedKey = appliedKey;
                    state.appliedByBridge = true;
                    return true;
                };

                const onMediaReady = () => state.tryApply();
                const onManualQualityInput = event => {
                    if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") {
                        return;
                    }
                    const menuItem = event.target?.closest?.(".ytp-menuitem");
                    if (!menuItem?.closest?.(".ytp-settings-menu")) {
                        return;
                    }
                    const itemText = String(menuItem.textContent || "").replace(/\s+/g, " ").trim();
                    if (!/(?:\d{3,4}p|自動|Auto|高画質|データセーバー|Higher picture quality|Data saver)/i.test(itemText)) {
                        return;
                    }
                    const player = getPlayer();
                    const videoIdentity = getVideoIdentity(player);
                    if (!videoIdentity) {
                        return;
                    }
                    state.manualVideoIdentity = videoIdentity;
                    state.lastVideoIdentity = videoIdentity;
                    state.releaseRange();
                };
                state.dispose = () => {
                    document.removeEventListener("loadedmetadata", onMediaReady, true);
                    document.removeEventListener("canplay", onMediaReady, true);
                    document.removeEventListener("playing", onMediaReady, true);
                    document.removeEventListener("yt-navigate-finish", onMediaReady, true);
                    document.removeEventListener("pointerdown", onManualQualityInput, true);
                    document.removeEventListener("keydown", onManualQualityInput, true);
                };
                document.addEventListener("loadedmetadata", onMediaReady, true);
                document.addEventListener("canplay", onMediaReady, true);
                document.addEventListener("playing", onMediaReady, true);
                document.addEventListener("yt-navigate-finish", onMediaReady, true);
                document.addEventListener("pointerdown", onManualQualityInput, true);
                document.addEventListener("keydown", onManualQualityInput, true);
                window[bridgeKey] = state;
                state.tryApply();
                return {
                    ok: true,
                    enabled: true,
                    installed: true,
                    qualityCap: requestedQualityCap,
                    manual: false
                };
            })();
            """;
    }

    public static string BuildPlayerRectScript(string? webMessageToken = null)
    {
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        return $$"""
            (() => {
                const stateKey = "__tabBrowserYouTubeControlState";
                const webMessageToken = {{serializedWebMessageToken}};
                const state = window[stateKey] || (window[stateKey] = {
                    mouseX: Number.NaN,
                    mouseY: Number.NaN,
                    mouseActivityAt: 0,
                    lastActivityPostedAt: Number.NEGATIVE_INFINITY,
                    lastScrollActivityPostedAt: Number.NEGATIVE_INFINITY,
                    mouseOverPlayer: false,
                    scrollSuppressed: false,
                    pointerFrameRequestId: 0,
                    installed: false,
                    installedVersion: 0
                });
                if (typeof state.mouseActivityAt !== "number") {
                    state.mouseActivityAt = 0;
                }
                if (typeof state.lastActivityPostedAt !== "number") {
                    state.lastActivityPostedAt = Number.NEGATIVE_INFINITY;
                }
                if (typeof state.lastScrollActivityPostedAt !== "number") {
                    state.lastScrollActivityPostedAt = Number.NEGATIVE_INFINITY;
                }
                const postActivity = (reason, force = false) => {
                    if (!webMessageToken) {
                        return;
                    }

                    const now = Date.now();
                    if (!force && now - state.lastActivityPostedAt < 2500) {
                        return;
                    }

                    state.lastActivityPostedAt = now;
                    window.chrome?.webview?.postMessage({
                        type: "youtubeControlActivity",
                        tabBrowserToken: webMessageToken,
                        reason
                    });
                };
                if (state.installedVersion !== 7) {
                    state.dispose?.();
                    state.pointerFrameRequestId = 0;
                    let pendingMouseX = Number.NaN;
                    let pendingMouseY = Number.NaN;
                    let pendingPointerTarget = null;
                    let shouldRecoverPointerTargetOnMove = true;
                    let pointerListenersInstalled = false;
                    const isVideoPage = () => {
                        const host = String(location.hostname || "").toLowerCase();
                        const isYouTubeHost = host === "youtube.com"
                            || host.endsWith(".youtube.com")
                            || host === "youtube-nocookie.com"
                            || host.endsWith(".youtube-nocookie.com");
                        if (!isYouTubeHost) {
                            return false;
                        }

                        const path = String(location.pathname || "").replace(/\/+$/, "");
                        if (path === "/watch") {
                            return Boolean(new URLSearchParams(location.search).get("v"));
                        }

                        return path.startsWith("/shorts/")
                            || path.startsWith("/embed/")
                            || path.startsWith("/live/");
                    };
                    const isPlayerTarget = target => isVideoPage()
                        && target instanceof Element
                        && Boolean(target.closest(".html5-video-player, #movie_player, ytd-player, #player-container"));
                    const processPointerMove = () => {
                        state.pointerFrameRequestId = 0;
                        state.mouseX = pendingMouseX;
                        state.mouseY = pendingMouseY;
                        state.mouseActivityAt = Date.now();
                        if (!state.mouseOverPlayer && shouldRecoverPointerTargetOnMove) {
                            shouldRecoverPointerTargetOnMove = false;
                            if (isPlayerTarget(pendingPointerTarget)) {
                                const resumesAfterScroll = state.scrollSuppressed;
                                state.mouseOverPlayer = true;
                                state.scrollSuppressed = false;
                                postActivity(
                                    resumesAfterScroll ? "pointer-resume" : "pointer-enter",
                                    true);
                                return;
                            }
                        }

                        if (state.mouseOverPlayer) {
                            const resumesAfterScroll = state.scrollSuppressed;
                            state.scrollSuppressed = false;
                            postActivity(
                                resumesAfterScroll ? "pointer-resume" : "pointer-keepalive",
                                resumesAfterScroll);
                        }
                    };
                    const onPointerMove = event => {
                        pendingMouseX = event.clientX;
                        pendingMouseY = event.clientY;
                        pendingPointerTarget = event.target;
                        if (!state.pointerFrameRequestId) {
                            state.pointerFrameRequestId = window.requestAnimationFrame(processPointerMove);
                        }
                    };
                    const onPointerOver = event => {
                        if (!state.mouseOverPlayer && isPlayerTarget(event.target)) {
                            state.mouseX = event.clientX;
                            state.mouseY = event.clientY;
                            state.mouseActivityAt = Date.now();
                            state.mouseOverPlayer = true;
                            state.scrollSuppressed = false;
                            postActivity("pointer-enter", true);
                        }
                    };
                    const onPointerOut = event => {
                        if (state.mouseOverPlayer
                            && isPlayerTarget(event.target)
                            && !isPlayerTarget(event.relatedTarget)) {
                            state.mouseOverPlayer = false;
                            postActivity("pointer-leave-player", true);
                        }
                    };
                    const onDocumentMouseLeave = () => {
                        state.mouseX = Number.NaN;
                        state.mouseY = Number.NaN;
                        state.mouseOverPlayer = false;
                        postActivity("pointer-leave", true);
                    };
                    const postLayoutActivity = () => {
                        if (state.mouseOverPlayer) {
                            postActivity("layout", true);
                        }
                    };
                    const onScrollActivity = () => {
                        const now = Date.now();
                        if (now - state.lastScrollActivityPostedAt < 400) {
                            return;
                        }

                        state.lastScrollActivityPostedAt = now;
                        state.scrollSuppressed = true;
                        postActivity("page-scroll", true);
                    };
                    const attachPointerListeners = () => {
                        if (pointerListenersInstalled) {
                            return;
                        }

                        document.addEventListener("pointermove", onPointerMove, true);
                        document.addEventListener("pointerover", onPointerOver, true);
                        document.addEventListener("pointerout", onPointerOut, true);
                        document.addEventListener("mouseleave", onDocumentMouseLeave, true);
                        document.addEventListener("wheel", onScrollActivity, { capture: true, passive: true });
                        document.addEventListener("scroll", onScrollActivity, true);
                        pointerListenersInstalled = true;
                    };
                    const detachPointerListeners = () => {
                        if (!pointerListenersInstalled) {
                            return;
                        }

                        if (state.pointerFrameRequestId) {
                            window.cancelAnimationFrame(state.pointerFrameRequestId);
                            state.pointerFrameRequestId = 0;
                        }
                        document.removeEventListener("pointermove", onPointerMove, true);
                        document.removeEventListener("pointerover", onPointerOver, true);
                        document.removeEventListener("pointerout", onPointerOut, true);
                        document.removeEventListener("mouseleave", onDocumentMouseLeave, true);
                        document.removeEventListener("wheel", onScrollActivity, true);
                        document.removeEventListener("scroll", onScrollActivity, true);
                        pointerListenersInstalled = false;
                        state.mouseX = Number.NaN;
                        state.mouseY = Number.NaN;
                        pendingPointerTarget = null;
                        state.mouseOverPlayer = false;
                        state.scrollSuppressed = false;
                    };
                    const syncPointerListeners = () => {
                        if (isVideoPage()) {
                            attachPointerListeners();
                        } else {
                            detachPointerListeners();
                        }
                    };
                    const onNavigateFinish = () => {
                        shouldRecoverPointerTargetOnMove = true;
                        syncPointerListeners();
                        postLayoutActivity();
                    };
                    window.addEventListener("resize", postLayoutActivity, true);
                    document.addEventListener("fullscreenchange", postLayoutActivity, true);
                    document.addEventListener("yt-navigate-finish", onNavigateFinish, true);
                    syncPointerListeners();
                    state.dispose = () => {
                        detachPointerListeners();
                        window.removeEventListener("resize", postLayoutActivity, true);
                        document.removeEventListener("fullscreenchange", postLayoutActivity, true);
                        document.removeEventListener("yt-navigate-finish", onNavigateFinish, true);
                    };
                    state.installed = true;
                    state.installedVersion = 7;
                }

                const candidates = [
                    document.querySelector(".html5-video-player"),
                    document.getElementById("movie_player"),
                    document.querySelector("ytd-player #player-container"),
                    document.querySelector("#player-container")
                ];
                const player = candidates.find(candidate => candidate && typeof candidate.getBoundingClientRect === "function");
                const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
                const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                if (!player || viewportWidth <= 0 || viewportHeight <= 0) {
                    return {
                        ok: false
                    };
                }

                const rect = player.getBoundingClientRect();
                const visibleWidth = Math.max(0, Math.min(rect.right, viewportWidth) - Math.max(rect.left, 0));
                const visibleHeight = Math.max(0, Math.min(rect.bottom, viewportHeight) - Math.max(rect.top, 0));
                const isMouseOverPlayer =
                    Number.isFinite(state.mouseX)
                    && Number.isFinite(state.mouseY)
                    && state.mouseX >= rect.left
                    && state.mouseX <= rect.right
                    && state.mouseY >= rect.top
                    && state.mouseY <= rect.bottom;
                const lastMouseActivityAgeMilliseconds = state.mouseActivityAt > 0
                    ? Math.max(0, Date.now() - state.mouseActivityAt)
                    : 2147483647;
                return {
                    ok: rect.width > 0 && rect.height > 0 && visibleWidth > 0 && visibleHeight > 0,
                    isMouseOverPlayer,
                    lastMouseActivityAgeMilliseconds,
                    left: rect.left,
                    top: rect.top,
                    width: rect.width,
                    height: rect.height,
                    visibleWidth,
                    visibleHeight,
                    viewportWidth,
                    viewportHeight
                };
            })();
            """;
    }

    public static string BuildPlaybackStateReadScript()
    {
        return """
            (() => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const getActiveVideo = () => player?.querySelector("video.html5-main-video")
                    || player?.querySelector("video.video-stream")
                    || player?.querySelector("video")
                    || document.querySelector("video.html5-main-video")
                    || document.querySelector("video.video-stream");
                const video = getActiveVideo();
                if (!video) {
                    return null;
                }

                const playerSpeed = player && typeof player.getPlaybackRate === "function"
                    ? Number(player.getPlaybackRate())
                    : Number.NaN;
                const videoSpeed = Number(video.playbackRate);
                const speed = Number.isFinite(playerSpeed) && playerSpeed > 0
                    ? playerSpeed
                    : Number.isFinite(videoSpeed) && videoSpeed > 0
                        ? videoSpeed
                        : null;

                const isTheaterMode = () => Boolean(document.querySelector("ytd-watch-flexy[theater]"));
                const videoData = player && typeof player.getVideoData === "function"
                    ? player.getVideoData()
                    : null;
                const playerResponse = (player && typeof player.getPlayerResponse === "function"
                    ? player.getPlayerResponse()
                    : null) || window.ytInitialPlayerResponse || null;
                const details = playerResponse?.videoDetails || null;
                const broadcast = playerResponse?.microformat
                    ?.playerMicroformatRenderer
                    ?.liveBroadcastDetails || null;
                const startTime = broadcast?.startTimestamp
                    ? Date.parse(broadcast.startTimestamp)
                    : Number.NaN;
                const hasStarted = Number.isFinite(startTime)
                    && startTime <= Date.now() + 60000;
                const hasEnded = Boolean(broadcast?.endTimestamp);
                const explicitlyLive = videoData?.isLive === true || details?.isLive === true;
                const isLive = !hasEnded && (
                    (explicitlyLive && (!broadcast || hasStarted))
                    || (details?.isLiveContent === true && hasStarted));
                const readText = value => {
                    if (typeof value === "string") {
                        return value;
                    }
                    if (typeof value?.simpleText === "string") {
                        return value.simpleText;
                    }
                    if (Array.isArray(value?.runs)) {
                        return value.runs.map(run => String(run?.text || "")).join("");
                    }
                    return "";
                };
                const primaryInfo = (window.ytInitialData?.contents
                    ?.twoColumnWatchNextResults?.results?.results?.contents || [])
                    .find(item => item?.videoPrimaryInfoRenderer)
                    ?.videoPrimaryInfoRenderer || null;
                const viewCountRenderer = document.querySelector("ytd-video-view-count-renderer")?.data
                    || primaryInfo?.viewCount?.videoViewCountRenderer
                    || null;
                const liveViewerTextSource = readText(viewCountRenderer?.viewCount)
                    || document.querySelector("ytd-video-view-count-renderer #view-count")?.textContent
                    || "";
                const liveViewerCountText = isLive
                    ? String(liveViewerTextSource).replace(/\s+/g, " ").trim().slice(0, 64) || null
                    : null;
                const position = Number(video.currentTime) || 0;
                const liveBadge = player?.querySelector(".ytp-live-badge")
                    || document.querySelector(".ytp-live-badge");
                const isAtLiveEdge = isLive
                    && Boolean(liveBadge)
                    && liveBadge.classList.contains("ytp-live-badge-is-livehead");
                const duration = Number.isFinite(video.duration) ? video.duration : 0;

                return {
                    ok: true,
                    position,
                    speed,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    looping: Boolean(video.loop),
                    theaterMode: isTheaterMode(),
                    duration,
                    isLive,
                    isAtLiveEdge,
                    liveViewerCountText
                };
            })();
            """;
    }

    public static string BuildPlaybackHealthProbeScript()
    {
        return """
            (() => {
                const video = document.querySelector("video");
                if (!video) {
                    return {
                        ok: false,
                        hasVideo: false,
                        hasVisibleVideo: false,
                        videoNaturalWidth: 0,
                        videoNaturalHeight: 0
                    };
                }

                const rect = video.getBoundingClientRect?.();
                const position = Number(video.currentTime);
                const duration = Number(video.duration);
                let bufferedAhead = 0;
                try {
                    if (Number.isFinite(position)) {
                        for (let index = 0; index < video.buffered.length; index++) {
                            const start = video.buffered.start(index);
                            const end = video.buffered.end(index);
                            if (position >= start && position <= end) {
                                bufferedAhead = Math.max(0, end - position);
                                break;
                            }
                        }
                    }
                } catch {
                }

                return {
                    ok: true,
                    hasVideo: true,
                    hasVisibleVideo: Boolean(rect && rect.width > 0 && rect.height > 0),
                    videoNaturalWidth: Number(video.videoWidth) || 0,
                    videoNaturalHeight: Number(video.videoHeight) || 0,
                    position: Number.isFinite(position) ? position : null,
                    duration: Number.isFinite(duration) ? duration : null,
                    readyState: Number.isInteger(video.readyState) ? video.readyState : null,
                    networkState: Number.isInteger(video.networkState) ? video.networkState : null,
                    paused: Boolean(video.paused),
                    ended: Boolean(video.ended),
                    bufferedAhead,
                    speed: Number(video.playbackRate) || 1,
                    volume: Number(video.volume) || 0,
                    muted: Boolean(video.muted),
                    mediaError: Number.isInteger(video.error?.code) ? video.error.code : null
                };
            })();
            """;
    }

    public static string BuildMiniPlayerWatchPageScript()
    {
        return BuildMiniPlayerWatchPageScript(string.Empty);
    }

    public static string BuildMiniPlayerLayoutVerificationScript()
    {
        return """
            (() => {
                const bridge = window.__tabBrowserYouTubeMiniPlayerLayoutBridge;
                if (!bridge || typeof bridge.beginVerification !== "function") {
                    return { ok: false, requested: false };
                }

                bridge.beginVerification(true);
                return { ok: true, requested: true };
            })();
            """;
    }

    public static string BuildMiniPlayerWatchPageScript(
        string webMessageToken,
        IEnumerable<GestureMappingSettings>? gestureMappings = null,
        double gestureStartThreshold = 14,
        double gestureDirectionThreshold = 22,
        double contentZoomFactor = 1,
        double contentPanX = 0,
        double contentPanY = 0,
        int audioBoostDecibels = 0)
    {
        var normalizedAudioBoostDecibels = YouTubeMiniPlayerAudioBoostProfile.Normalize(audioBoostDecibels);
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedAudioBoostDecibels = JsonSerializer.Serialize(normalizedAudioBoostDecibels);
        var serializedAudioBoostGain = JsonSerializer.Serialize(
            YouTubeMiniPlayerAudioBoostProfile.GetLinearGain(normalizedAudioBoostDecibels));
        var serializedContentZoomFactor = JsonSerializer.Serialize(
            double.IsFinite(contentZoomFactor) ? Math.Clamp(contentZoomFactor, 1, 4) : 1);
        var serializedContentPanX = JsonSerializer.Serialize(
            double.IsFinite(contentPanX) ? Math.Clamp(contentPanX, -1, 1) : 0);
        var serializedContentPanY = JsonSerializer.Serialize(
            double.IsFinite(contentPanY) ? Math.Clamp(contentPanY, -1, 1) : 0);
        var serializedGestureSettings = SerializeMiniPlayerGestureSettings(
            gestureMappings,
            gestureStartThreshold,
            gestureDirectionThreshold);
        return """
            (() => {
                const styleId = "tabbrowser-youtube-mini-player-style";
                let style = document.getElementById(styleId);
                if (!style) {
                    style = document.createElement("style");
                    style.id = styleId;
                    document.documentElement.appendChild(style);
                }

                style.textContent = `
                    html,
                    body {
                        margin: 0 !important;
                        padding: 0 !important;
                        width: 100% !important;
                        height: 100% !important;
                        overflow: hidden !important;
                        background: #000 !important;
                    }

                    #masthead-container,
                    ytd-mini-guide-renderer,
                    tp-yt-app-drawer,
                    #secondary,
                    #below,
                    #comments,
                    ytd-comments,
                    ytd-watch-metadata,
                    ytd-merch-shelf-renderer,
                    ytd-engagement-panel-section-list-renderer,
                    ytd-playlist-panel-renderer,
                    ytd-live-chat-frame,
                    #chat {
                        display: none !important;
                    }

                    /* 空の全幅パネルがプレーヤーの設定メニューを覆っても、入力を奪わない。 */
                    #panels-full-bleed-container {
                        pointer-events: none !important;
                    }

                    ytd-app,
                    #content,
                    #page-manager,
                    ytd-watch-flexy,
                    ytd-watch-flexy[flexy],
                    ytd-watch-flexy #columns,
                    ytd-watch-flexy #primary,
                    ytd-watch-flexy #primary-inner {
                        width: 100% !important;
                        height: 100% !important;
                        min-width: 0 !important;
                        max-width: none !important;
                        margin: 0 !important;
                        padding: 0 !important;
                        background: transparent !important;
                        overflow: hidden !important;
                    }

                    .html5-video-container {
                        display: block !important;
                        position: absolute !important;
                        inset: 0 !important;
                        width: 100% !important;
                        height: 100% !important;
                        min-width: 0 !important;
                        min-height: 0 !important;
                        max-width: none !important;
                        max-height: none !important;
                        margin: 0 !important;
                        padding: 0 !important;
                        overflow: hidden !important;
                        background: #000 !important;
                    }

                    video,
                    .html5-main-video,
                    .video-stream {
                        display: block !important;
                        position: absolute !important;
                        inset: 0 !important;
                        left: 0 !important;
                        top: 0 !important;
                        width: 100% !important;
                        height: 100% !important;
                        min-width: 0 !important;
                        min-height: 0 !important;
                        max-width: none !important;
                        max-height: none !important;
                        margin: 0 !important;
                        padding: 0 !important;
                        object-fit: contain !important;
                        background: #000 !important;
                        opacity: 1 !important;
                        visibility: visible !important;
                        transform: none !important;
                        z-index: 1 !important;
                    }

                    html[data-tabbrowser-mini-player-content-cursor="grab"],
                    html[data-tabbrowser-mini-player-content-cursor="grab"] body,
                    html[data-tabbrowser-mini-player-content-cursor="grab"] .html5-video-player,
                    html[data-tabbrowser-mini-player-content-cursor="grab"] video {
                        cursor: grab !important;
                    }

                    html[data-tabbrowser-mini-player-content-cursor="grabbing"],
                    html[data-tabbrowser-mini-player-content-cursor="grabbing"] body,
                    html[data-tabbrowser-mini-player-content-cursor="grabbing"] .html5-video-player,
                    html[data-tabbrowser-mini-player-content-cursor="grabbing"] video {
                        cursor: grabbing !important;
                    }

                    html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-chrome-top,
                    html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-gradient-top,
                    html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-title,
                    html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-show-cards-title {
                        display: none !important;
                        opacity: 0 !important;
                        visibility: hidden !important;
                        pointer-events: none !important;
                    }
                `;

                const getPlayer = () => document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const getVideo = () => {
                    const player = getPlayer();
                    const videos = [...(player || document).querySelectorAll("video")];
                    const activeVideo = window.__tabBrowserYouTubeMiniPlayerLayoutBridge?.activeVideo;
                    const playingVideos = videos.filter(video => !video.paused && !video.ended
                        && video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA);
                    // 切り替え途中に旧動画が残っても、準備・再生イベントを受けた現動画を優先する。
                    return playingVideos.includes(activeVideo) ? activeVideo
                        : playingVideos[0]
                            || (videos.includes(activeVideo) && !activeVideo.ended ? activeVideo : null)
                            || videos.find(video => !video.ended && video.readyState >= HTMLMediaElement.HAVE_METADATA)
                            || videos.find(video => video.matches(".html5-main-video, .video-stream"))
                            || videos[0] || null;
                };
                const getVideoLayoutParents = (player, video) => {
                    const parents = [];
                    if (player instanceof HTMLElement && player.contains(video)) {
                        for (let parent = video?.parentElement; parent && parent !== player; parent = parent.parentElement) {
                            parents.push(parent);
                        }
                    }
                    return parents;
                };
                const setImportant = (element, name, value) => {
                    if (element.style.getPropertyValue(name) !== value
                        || element.style.getPropertyPriority(name) !== "important") {
                        element.style.setProperty(name, value, "important");
                    }
                };
                const fullScreenMessageToken = __TAB_BROWSER_YOUTUBE_MINI_PLAYER_MESSAGE_TOKEN__;
                const contentTransformKey = "__tabBrowserYouTubeMiniPlayerContentTransform";
                const contentTransformHudId = "tabbrowser-youtube-mini-player-content-transform-hud";
                const contentPanEdgeFeedbackId = "tabbrowser-youtube-mini-player-content-pan-edge-feedback";
                const normalizeContentZoom = value => Math.max(1, Math.min(4, Math.round((Number(value) || 1) * 10) / 10));
                const normalizeContentPan = value => Math.max(-1, Math.min(1, Number(value) || 0));
                const contentTransformState = window[contentTransformKey] || {
                    zoomFactor: 1,
                    panX: 0,
                    panY: 0
                };
                window[contentTransformKey] = contentTransformState;
                const isContentAtOriginalSize = () => normalizeContentZoom(contentTransformState.zoomFactor) === 1;
                const getContentTransformCss = () => {
                    const zoomFactor = normalizeContentZoom(contentTransformState.zoomFactor);
                    const panX = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panX) : 0;
                    const panY = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panY) : 0;
                    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1;
                    const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 1;
                    const translateX = panX * viewportWidth * (zoomFactor - 1) / 2;
                    const translateY = panY * viewportHeight * (zoomFactor - 1) / 2;
                    return `translate(${translateX}px, ${translateY}px) scale(${zoomFactor})`;
                };
                const applyContentTransform = video => {
                    if (!video) {
                        return;
                    }

                    const zoomFactor = normalizeContentZoom(contentTransformState.zoomFactor);
                    const panX = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panX) : 0;
                    const panY = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panY) : 0;
                    contentTransformState.zoomFactor = zoomFactor;
                    contentTransformState.panX = panX;
                    contentTransformState.panY = panY;
                    setImportant(video, "transform-origin", "center center");
                    setImportant(video, "transform", getContentTransformCss());
                };
                const hasContentTransformDrift = video => video instanceof HTMLVideoElement
                    && (video.style.getPropertyValue("transform") !== getContentTransformCss()
                        || video.style.getPropertyPriority("transform") !== "important"
                        || video.style.getPropertyValue("transform-origin") !== "center center"
                        || video.style.getPropertyPriority("transform-origin") !== "important");
                const hasImportantVideoStyleDrift = (video, name, value) => video instanceof HTMLVideoElement
                    && (video.style.getPropertyValue(name) !== value
                        || video.style.getPropertyPriority(name) !== "important");
                const hasImportantVideoZeroStyleDrift = (video, name) => {
                    if (!(video instanceof HTMLVideoElement)) {
                        return false;
                    }

                    const value = video.style.getPropertyValue(name);
                    return (value !== "0" && value !== "0px")
                        || video.style.getPropertyPriority(name) !== "important";
                };
                const hasVideoStyleDrift = video => hasContentTransformDrift(video)
                    || hasImportantVideoStyleDrift(video, "display", "block")
                    || hasImportantVideoStyleDrift(video, "position", "absolute")
                    || hasImportantVideoZeroStyleDrift(video, "left")
                    || hasImportantVideoZeroStyleDrift(video, "top")
                    || hasImportantVideoStyleDrift(video, "width", "100%")
                    || hasImportantVideoStyleDrift(video, "height", "100%")
                    || hasImportantVideoStyleDrift(video, "object-fit", "contain")
                    || hasImportantVideoStyleDrift(video, "opacity", "1")
                    || hasImportantVideoStyleDrift(video, "visibility", "visible");
                const ensureContentTransformHud = () => {
                    let hud = document.getElementById(contentTransformHudId);
                    if (hud) {
                        return hud;
                    }

                    hud = document.createElement("div");
                    hud.id = contentTransformHudId;
                    Object.assign(hud.style, {
                        position: "fixed",
                        top: "52px",
                        right: "12px",
                        zIndex: "2147483647",
                        display: "flex",
                        alignItems: "center",
                        gap: "8px",
                        padding: "6px 8px",
                        color: "#f3f4f6",
                        background: "rgba(18, 22, 30, 0.9)",
                        border: "1px solid rgba(133, 150, 173, 0.7)",
                        borderRadius: "4px",
                        boxSizing: "border-box",
                        font: "600 12px 'Segoe UI', sans-serif",
                        lineHeight: "1",
                        pointerEvents: "none",
                        opacity: "0",
                        transition: "opacity 120ms ease-out"
                    });
                    const label = document.createElement("span");
                    label.dataset.tabbrowserContentTransformRole = "label";
                    label.style.minWidth = "38px";
                    label.style.textAlign = "right";
                    const map = document.createElement("span");
                    Object.assign(map.style, {
                        position: "relative",
                        display: "block",
                        width: "72px",
                        height: "40px",
                        overflow: "hidden",
                        background: "#07090d",
                        border: "1px solid #4b5563",
                        boxSizing: "border-box"
                    });
                    const viewport = document.createElement("span");
                    viewport.dataset.tabbrowserContentTransformRole = "viewport";
                    Object.assign(viewport.style, {
                        position: "absolute",
                        display: "block",
                        border: "1px solid #8ab4f8",
                        background: "rgba(138, 180, 248, 0.18)",
                        boxSizing: "border-box"
                    });
                    map.appendChild(viewport);
                    hud.append(label, map);
                    document.documentElement.appendChild(hud);
                    return hud;
                };
                const showContentTransformHud = (keepVisible = false) => {
                    const hud = ensureContentTransformHud();
                    const label = hud.querySelector('[data-tabbrowser-content-transform-role="label"]');
                    const viewport = hud.querySelector('[data-tabbrowser-content-transform-role="viewport"]');
                    const zoomFactor = normalizeContentZoom(contentTransformState.zoomFactor);
                    const panX = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panX) : 0;
                    const panY = zoomFactor > 1 ? normalizeContentPan(contentTransformState.panY) : 0;
                    if (label) {
                        label.textContent = `${Math.round(zoomFactor * 100)}%`;
                    }
                    if (viewport instanceof HTMLElement) {
                        const width = 72 / zoomFactor;
                        const height = 40 / zoomFactor;
                        viewport.style.width = `${width}px`;
                        viewport.style.height = `${height}px`;
                        viewport.style.left = `${(72 - width) * (1 - panX) / 2}px`;
                        viewport.style.top = `${(40 - height) * (1 - panY) / 2}px`;
                    }
                    hud.style.opacity = "1";
                    if (contentTransformState.hudHideTimer) {
                        clearTimeout(contentTransformState.hudHideTimer);
                        contentTransformState.hudHideTimer = null;
                    }
                    if (!keepVisible) {
                        contentTransformState.hudHideTimer = setTimeout(() => {
                            hud.style.opacity = "0";
                            contentTransformState.hudHideTimer = null;
                        }, 900);
                    }
                };
                const showContentPanEdgeFeedback = () => {
                    let feedback = document.getElementById(contentPanEdgeFeedbackId);
                    if (!feedback) {
                        feedback = document.createElement("div");
                        feedback.id = contentPanEdgeFeedbackId;
                        Object.assign(feedback.style, {
                            position: "fixed",
                            inset: "1px",
                            zIndex: "2147483646",
                            border: "2px solid #8ab4f8",
                            borderRadius: "2px",
                            boxSizing: "border-box",
                            pointerEvents: "none",
                            opacity: "0",
                            transition: "opacity 80ms ease-out"
                        });
                        document.documentElement.appendChild(feedback);
                    }

                    feedback.style.opacity = "1";
                    if (contentTransformState.edgeFeedbackTimer) {
                        clearTimeout(contentTransformState.edgeFeedbackTimer);
                    }
                    contentTransformState.edgeFeedbackTimer = setTimeout(() => {
                        feedback.style.opacity = "0";
                        contentTransformState.edgeFeedbackTimer = null;
                    }, 160);
                };
                const setContentTransform = (token, zoomFactor, panX, panY, showHud = false) => {
                    if (token !== fullScreenMessageToken) {
                        return false;
                    }

                    const previousZoomFactor = normalizeContentZoom(contentTransformState.zoomFactor);
                    contentTransformState.zoomFactor = normalizeContentZoom(zoomFactor);
                    contentTransformState.panX = contentTransformState.zoomFactor > 1 ? normalizeContentPan(panX) : 0;
                    contentTransformState.panY = contentTransformState.zoomFactor > 1 ? normalizeContentPan(panY) : 0;
                    applyContentTransform(getVideo());
                    if (contentTransformState.zoomFactor <= 1) {
                        document.documentElement.removeAttribute("data-tabbrowser-mini-player-content-cursor");
                    }
                    if (previousZoomFactor > 1 && isContentAtOriginalSize()) {
                        window.__tabBrowserYouTubeMiniPlayerLayoutBridge?.scheduleApply?.();
                    }
                    if (showHud) {
                        showContentTransformHud();
                    }
                    return true;
                };
                Object.defineProperty(window, "__tabBrowserYouTubeMiniPlayerSetContentTransform", {
                    configurable: true,
                    writable: false,
                    value: setContentTransform
                });
                setContentTransform(
                    fullScreenMessageToken,
                    __TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_ZOOM__,
                    __TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_PAN_X__,
                    __TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_PAN_Y__);
                const savedStateRestoreGuardKey = "__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard";
                const savedStateRestoreGuardVersion = 1;
                const existingSavedStateRestoreGuard = window[savedStateRestoreGuardKey];
                if (existingSavedStateRestoreGuard?.version !== savedStateRestoreGuardVersion) {
                    existingSavedStateRestoreGuard?.dispose?.();
                    const savedStateRestoreGuard = {
                        version: savedStateRestoreGuardVersion,
                        playback: Boolean(existingSavedStateRestoreGuard?.playback),
                        audio: Boolean(existingSavedStateRestoreGuard?.audio),
                        dispose: null
                    };
                    const isEditableTarget = target => target instanceof HTMLElement
                        && (target.isContentEditable || target.closest("input, textarea, select, [contenteditable='true']"));
                    const isAudioControlTarget = target => target?.closest?.([
                        ".ytp-mute-button",
                        ".ytp-volume-area",
                        ".ytp-volume-panel",
                        ".ytp-volume-slider",
                        ".ytp-volume-slider-handle"
                    ].join(","));
                    const isPlaybackButtonTarget = target => target?.closest?.([
                        ".ytp-play-button",
                        ".ytp-large-play-button",
                        ".ytp-cued-thumbnail-overlay"
                    ].join(","));
                    const markPointerInteraction = event => {
                        if (!event.isTrusted || event.button !== 0) {
                            return;
                        }

                        if (isAudioControlTarget(event.target)) {
                            savedStateRestoreGuard.audio = true;
                        } else if (isPlaybackButtonTarget(event.target)) {
                            savedStateRestoreGuard.playback = true;
                        }
                    };
                    const markVideoClick = event => {
                        if (!event.isTrusted || isAudioControlTarget(event.target)) {
                            return;
                        }

                        const target = event.target;
                        if (isPlaybackButtonTarget(target)
                            || target?.matches?.("video, .html5-video-player, .html5-video-container")) {
                            savedStateRestoreGuard.playback = true;
                        }
                    };
                    const markKeyboardInteraction = event => {
                        if (!event.isTrusted || isEditableTarget(event.target) || event.ctrlKey || event.altKey || event.metaKey) {
                            return;
                        }

                        if (event.code === "Space" || event.code === "KeyK" || event.key === "MediaPlayPause") {
                            savedStateRestoreGuard.playback = true;
                        } else if (event.code === "KeyM") {
                            savedStateRestoreGuard.audio = true;
                        }
                    };
                    document.addEventListener("pointerdown", markPointerInteraction, true);
                    document.addEventListener("click", markVideoClick, true);
                    document.addEventListener("keydown", markKeyboardInteraction, true);
                    savedStateRestoreGuard.dispose = () => {
                        document.removeEventListener("pointerdown", markPointerInteraction, true);
                        document.removeEventListener("click", markVideoClick, true);
                        document.removeEventListener("keydown", markKeyboardInteraction, true);
                    };
                    window[savedStateRestoreGuardKey] = savedStateRestoreGuard;
                }

                const audioBoostBridgeKey = "__tabBrowserYouTubeMiniPlayerAudioBoost";
                const audioBoostBridgeVersion = 1;
                const requestedAudioBoostDecibels = __TAB_BROWSER_YOUTUBE_MINI_PLAYER_AUDIO_BOOST_DECIBELS__;
                const requestedAudioBoostGain = __TAB_BROWSER_YOUTUBE_MINI_PLAYER_AUDIO_BOOST_GAIN__;
                const normalizeAudioBoostDecibels = value => {
                    const normalized = Math.round(Number(value) || 0);
                    return [3, 6, 9].includes(normalized) ? normalized : 0;
                };
                const normalizeAudioBoostGain = (decibels, value) => {
                    const expected = Math.pow(10, decibels / 20);
                    const requested = Number(value);
                    return Number.isFinite(requested) && requested >= 1 && requested <= 3
                        ? requested
                        : expected;
                };
                let audioBoostState = window[audioBoostBridgeKey];
                if (!audioBoostState
                    || audioBoostState.version !== audioBoostBridgeVersion
                    || typeof audioBoostState.setBoost !== "function"
                    || typeof audioBoostState.applyToVideo !== "function") {
                    const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
                    audioBoostState = {
                        version: audioBoostBridgeVersion,
                        context: null,
                        entries: new WeakMap(),
                        pendingEntries: new WeakMap(),
                        currentVideo: null,
                        currentEntry: null,
                        targetDecibels: 0,
                        targetGain: 1,
                        requestGeneration: 0,
                        applyToVideo: null,
                        setBoost: null
                    };
                    const updateEntry = (entry, decibels, gain) => {
                        if (!entry) {
                            return;
                        }

                        const now = entry.context.currentTime;
                        entry.directGain.gain.cancelScheduledValues(now);
                        entry.boostGain.gain.cancelScheduledValues(now);
                        entry.directGain.gain.setValueAtTime(decibels > 0 ? 0 : 1, now);
                        entry.boostGain.gain.setValueAtTime(decibels > 0 ? gain : 0, now);
                    };
                    const ensureEntry = async video => {
                        const current = audioBoostState.entries.get(video);
                        if (current) {
                            return current;
                        }

                        const pending = audioBoostState.pendingEntries.get(video);
                        if (pending) {
                            return await pending;
                        }

                        const creation = (async () => {
                            if (!AudioContextCtor) {
                                return null;
                            }

                            let context = audioBoostState.context;
                            if (!context || context.state === "closed") {
                                context = new AudioContextCtor({ latencyHint: "interactive" });
                                audioBoostState.context = context;
                            }

                            if (context.state === "suspended") {
                                try {
                                    await context.resume();
                                } catch {
                                }
                            }

                            if (context.state !== "running") {
                                return null;
                            }

                            const directGain = context.createGain();
                            const boostGain = context.createGain();
                            const compressor = context.createDynamicsCompressor();
                            compressor.threshold.value = -3;
                            compressor.knee.value = 12;
                            compressor.ratio.value = 6;
                            compressor.attack.value = 0.003;
                            compressor.release.value = 0.25;
                            const source = context.createMediaElementSource(video);
                            try {
                                source.connect(directGain);
                                directGain.connect(context.destination);
                                source.connect(boostGain);
                                boostGain.connect(compressor);
                                compressor.connect(context.destination);
                            } catch {
                                try {
                                    source.disconnect();
                                    directGain.disconnect();
                                    boostGain.disconnect();
                                    compressor.disconnect();
                                    source.connect(context.destination);
                                } catch {
                                }
                                return null;
                            }

                            const entry = { context, source, directGain, boostGain, compressor };
                            updateEntry(entry, 0, 1);
                            audioBoostState.entries.set(video, entry);
                            return entry;
                        })();
                        audioBoostState.pendingEntries.set(video, creation);
                        try {
                            return await creation;
                        } catch {
                            return null;
                        } finally {
                            audioBoostState.pendingEntries.delete(video);
                        }
                    };
                    audioBoostState.applyToVideo = async video => {
                        try {
                            if (audioBoostState.currentEntry
                                && audioBoostState.currentVideo !== video) {
                                updateEntry(audioBoostState.currentEntry, 0, 1);
                                audioBoostState.currentEntry = null;
                            }

                            audioBoostState.currentVideo = video || null;
                            if (!(video instanceof HTMLMediaElement)) {
                                return {
                                    ok: true,
                                    pending: audioBoostState.targetDecibels > 0,
                                    boostDecibels: audioBoostState.targetDecibels
                                };
                            }

                            let entry = audioBoostState.entries.get(video) || null;
                            if (!entry && audioBoostState.targetDecibels === 0) {
                                return { ok: true, bypassed: true, boostDecibels: 0 };
                            }

                            entry = entry || await ensureEntry(video);
                            if (!entry) {
                                return {
                                    ok: false,
                                    reason: "audio-boost-unavailable",
                                    boostDecibels: audioBoostState.targetDecibels
                                };
                            }

                            updateEntry(
                                entry,
                                audioBoostState.targetDecibels,
                                audioBoostState.targetGain);
                            audioBoostState.currentEntry = entry;
                            return {
                                ok: true,
                                applied: audioBoostState.targetDecibels > 0,
                                boostDecibels: audioBoostState.targetDecibels
                            };
                        } catch (error) {
                            return {
                                ok: false,
                                reason: "audio-boost-failed",
                                error: String(error && error.message || error),
                                boostDecibels: audioBoostState.targetDecibels
                            };
                        }
                    };
                    audioBoostState.setBoost = async (decibels, gain, retainOnFailure = false) => {
                        const normalizedDecibels = normalizeAudioBoostDecibels(decibels);
                        const normalizedGain = normalizeAudioBoostGain(normalizedDecibels, gain);
                        const previousDecibels = audioBoostState.targetDecibels;
                        const previousGain = audioBoostState.targetGain;
                        const generation = ++audioBoostState.requestGeneration;
                        audioBoostState.targetDecibels = normalizedDecibels;
                        audioBoostState.targetGain = normalizedGain;
                        const result = await audioBoostState.applyToVideo(getVideo());
                        if (generation !== audioBoostState.requestGeneration) {
                            return {
                                ok: true,
                                superseded: true,
                                boostDecibels: audioBoostState.targetDecibels
                            };
                        }

                        if (!result.ok && !retainOnFailure) {
                            audioBoostState.targetDecibels = previousDecibels;
                            audioBoostState.targetGain = previousGain;
                            await audioBoostState.applyToVideo(getVideo());
                        }

                        return result;
                    };
                    window[audioBoostBridgeKey] = audioBoostState;
                }

                Object.defineProperty(window, "__tabBrowserSetYouTubeMiniPlayerAudioBoost", {
                    configurable: true,
                    writable: false,
                    value: (token, decibels, gain) => token === fullScreenMessageToken
                        ? audioBoostState.setBoost(decibels, gain, false)
                        : Promise.resolve({ ok: false, reason: "invalid-token" })
                });
                Promise.resolve(audioBoostState.setBoost(
                    requestedAudioBoostDecibels,
                    requestedAudioBoostGain,
                    true)).catch(() => { });

                const applyVideoLayout = () => {
                    const player = getPlayer();
                    if (player && typeof player.setSize === "function") {
                        try {
                            player.setSize(window.innerWidth || 1, window.innerHeight || 1);
                        } catch {
                            // 切り替え中のYouTube内部APIが未準備でも、下のCSS補正は継続する。
                        }
                    }

                    const video = getVideo();
                    if (!video) {
                        return;
                    }

                    const playerHost = player instanceof HTMLElement
                        ? player.closest("ytd-player")
                        : null;
                    if (playerHost instanceof HTMLElement) {
                        setImportant(playerHost, "position", "fixed");
                        setImportant(playerHost, "inset", "0");
                        setImportant(playerHost, "width", "100vw");
                        setImportant(playerHost, "height", "100vh");
                        setImportant(playerHost, "margin", "0");
                        setImportant(playerHost, "padding", "0");
                        setImportant(playerHost, "transform", "none");
                        setImportant(playerHost, "z-index", "1");
                    }

                    if (player instanceof HTMLElement) {
                        setImportant(player, "position", "absolute");
                        setImportant(player, "inset", "0");
                        setImportant(player, "width", "100%");
                        setImportant(player, "height", "100%");
                        setImportant(player, "margin", "0");
                        setImportant(player, "padding", "0");
                        setImportant(player, "transform", "none");
                    }

                    // 動画と実プレイヤーの間だけを整える。兄弟の操作UIや外側のページには広げない。
                    for (const videoContainer of getVideoLayoutParents(player, video)) {
                        setImportant(videoContainer, "display", "block");
                        setImportant(videoContainer, "position", "absolute");
                        setImportant(videoContainer, "inset", "0");
                        setImportant(videoContainer, "width", "100%");
                        setImportant(videoContainer, "height", "100%");
                        setImportant(videoContainer, "overflow", "hidden");
                        setImportant(videoContainer, "transform", "none");
                    }

                    setImportant(video, "display", "block");
                    setImportant(video, "position", "absolute");
                    setImportant(video, "inset", "0");
                    setImportant(video, "left", "0");
                    setImportant(video, "top", "0");
                    setImportant(video, "width", "100%");
                    setImportant(video, "height", "100%");
                    setImportant(video, "object-fit", "contain");
                    setImportant(video, "opacity", "1");
                    setImportant(video, "visibility", "visible");
                    applyContentTransform(video);
                    setImportant(video, "z-index", "1");
                    Promise.resolve(audioBoostState.applyToVideo(video)).catch(() => { });
                };

                const layoutSizeTolerance = 1.5;
                const hasElementSizeDrift = (element, expectedWidth, expectedHeight) => {
                    if (!(element instanceof HTMLElement)) {
                        return false;
                    }

                    const rect = element.getBoundingClientRect();
                    return Math.abs(rect.width - expectedWidth) > layoutSizeTolerance
                        || Math.abs(rect.height - expectedHeight) > layoutSizeTolerance
                        || Math.abs(rect.left) > layoutSizeTolerance
                        || Math.abs(rect.top) > layoutSizeTolerance;
                };
                const hasVideoLayoutDrift = (player, video, expectedWidth, expectedHeight) => {
                    if (!isContentAtOriginalSize()
                        || !(player instanceof HTMLElement)
                        || !(video instanceof HTMLVideoElement)) {
                        return false;
                    }

                    const playerHost = player.closest("ytd-player");
                    const videoContainer = video.closest(".html5-video-container");
                    return hasElementSizeDrift(playerHost, expectedWidth, expectedHeight)
                        || hasElementSizeDrift(player, expectedWidth, expectedHeight)
                        || hasElementSizeDrift(videoContainer, expectedWidth, expectedHeight)
                        || getVideoLayoutParents(player, video).some(element =>
                            hasElementSizeDrift(element, expectedWidth, expectedHeight))
                        || hasElementSizeDrift(video, expectedWidth, expectedHeight);
                };

                const layoutBridgeKey = "__tabBrowserYouTubeMiniPlayerLayoutBridge";
                const layoutBridgeVersion = 20;
                const existingLayoutBridge = window[layoutBridgeKey];
                if (existingLayoutBridge?.version === layoutBridgeVersion) {
                    existingLayoutBridge.beginVerification?.(true);
                } else {
                    existingLayoutBridge?.dispose?.();
                    const layoutState = {
                        version: layoutBridgeVersion,
                        scheduled: false,
                        applying: false,
                        forceApply: false,
                        disposed: false,
                        activeVideo: null,
                        applyFrame: null,
                        verificationFrame: null,
                        verificationAttemptsRemaining: 0,
                        delayedVerificationTimers: [],
                        layoutVerified: false,
                        interactiveResize: false,
                        interactiveResizeGeneration: 0,
                        playerParentObserver: null,
                        flexyObserver: null,
                        discoveryObserver: null,
                        observedPlayer: null,
                        observedPlayerParent: null,
                        observedFlexy: null,
                        elementResizeObserver: null,
                        observedResizeElements: [],
                        contentTransformObserver: null,
                        observedContentTransformVideo: null,
                        observedStyleElements: [],
                        contentTransformRepairScheduled: false,
                        appliedPlayer: null,
                        appliedVideo: null,
                        appliedWidth: 0,
                        appliedHeight: 0,
                        scheduleApply: null,
                        beginVerification: null,
                        setInteractiveResize: null,
                        attachObservers: null,
                        attachResizeObserver: null,
                        dispose: null
                    };

                    const runApply = () => {
                        layoutState.applyFrame = null;
                        layoutState.scheduled = false;
                        if (layoutState.disposed
                            || layoutState.applying
                            || layoutState.interactiveResize) {
                            return;
                        }

                        const forceApply = layoutState.forceApply;
                        layoutState.forceApply = false;
                        const player = getPlayer();
                        const video = getVideo();
                        const width = window.innerWidth || 1;
                        const height = window.innerHeight || 1;
                        if (!forceApply
                            && layoutState.appliedPlayer === player
                            && layoutState.appliedVideo === video
                            && layoutState.appliedWidth === width
                            && layoutState.appliedHeight === height
                            && !hasLayoutStyleDrift()) {
                            layoutState.layoutVerified = Boolean(player && video) && !hasLayoutStyleDrift();
                            return;
                        }

                        layoutState.applying = true;
                        try {
                            applyVideoLayout();
                            layoutState.appliedPlayer = getPlayer();
                            layoutState.appliedVideo = getVideo();
                            layoutState.appliedWidth = width;
                            layoutState.appliedHeight = height;
                        } finally {
                            // 補正中のstyle書き込みを次の補正理由にしない。
                            layoutState.contentTransformObserver?.takeRecords?.();
                            layoutState.applying = false;
                            // setSize内で同期交換された動画も、新しい対象へ監視を付け直す。
                            layoutState.attachObservers?.();
                        }
                        // 注入成功と表示完了を区別し、次の描画で実寸を確認する。再補正は最大3回まで。
                        if (layoutState.verificationFrame === null) {
                            layoutState.verificationFrame = window.requestAnimationFrame(() => {
                                layoutState.verificationFrame = null;
                                if (layoutState.disposed) {
                                    return;
                                }
                                layoutState.attachObservers?.();
                                layoutState.layoutVerified = Boolean(getPlayer() && getVideo()) && !hasLayoutStyleDrift();
                                if (!layoutState.layoutVerified && --layoutState.verificationAttemptsRemaining > 0) {
                                    layoutState.scheduleApply(true);
                                } else {
                                    layoutState.verificationAttemptsRemaining = 0;
                                }
                            });
                        }
                    };
                    layoutState.scheduleApply = (force = false) => {
                        if (layoutState.disposed || layoutState.interactiveResize) {
                            return;
                        }
                        layoutState.layoutVerified = false;
                        layoutState.forceApply = layoutState.forceApply || Boolean(force);
                        if (layoutState.scheduled) {
                            return;
                        }

                        layoutState.scheduled = true;
                        layoutState.applyFrame = window.requestAnimationFrame(runApply);
                    };
                    const scheduleApply = () => layoutState.scheduleApply(false);
                    const hasLayoutStyleDrift = () => {
                        const player = getPlayer();
                        const video = getVideo();
                        // 内容拡大とは別に、プレイヤー側の縮小・移動は常に解除する。
                        const containers = [player?.closest("ytd-player"), player,
                            ...getVideoLayoutParents(player, video)];
                        return containers.some(element => element instanceof HTMLElement
                                && (element.style.getPropertyValue("transform") !== "none"
                                    || element.style.getPropertyPriority("transform") !== "important"))
                            || hasVideoStyleDrift(video)
                            || hasVideoLayoutDrift(player, video, window.innerWidth || 1, window.innerHeight || 1);
                    };
                    const scheduleVideoStyleRepair = () => {
                        if (layoutState.interactiveResize
                            || layoutState.applying) {
                            return;
                        }

                        // 拡大だけの書き戻しは監視通知内で直し、100%のまま次の描画へ渡さない。
                        const video = getVideo();
                        if (!isContentAtOriginalSize() && hasContentTransformDrift(video)) {
                            applyContentTransform(video);
                        }
                        if (layoutState.contentTransformRepairScheduled || !hasLayoutStyleDrift()) {
                            return;
                        }

                        layoutState.contentTransformRepairScheduled = true;
                        window.requestAnimationFrame(() => {
                            layoutState.contentTransformRepairScheduled = false;
                            if (hasLayoutStyleDrift()) {
                                layoutState.scheduleApply(true);
                            }
                        });
                    };
                    const attachAndSchedule = () => {
                        if (layoutState.interactiveResize) {
                            return;
                        }

                        layoutState.attachObservers?.();
                        layoutState.scheduleApply(true);
                    };
                    const clearDelayedVerifications = () => {
                        for (const timer of layoutState.delayedVerificationTimers) {
                            window.clearTimeout(timer);
                        }
                        layoutState.delayedVerificationTimers = [];
                    };
                    const scheduleDelayedVerifications = () => {
                        clearDelayedVerifications();
                        for (const delay of [2000, 5000]) {
                            const timer = window.setTimeout(() => {
                                layoutState.delayedVerificationTimers = layoutState.delayedVerificationTimers
                                    .filter(current => current !== timer);
                                if (layoutState.disposed || layoutState.interactiveResize) {
                                    return;
                                }
                                layoutState.attachObservers?.();
                                if (!layoutState.layoutVerified || hasLayoutStyleDrift()) {
                                    layoutState.verificationAttemptsRemaining = 3;
                                    layoutState.scheduleApply(false);
                                }
                            }, delay);
                            layoutState.delayedVerificationTimers.push(timer);
                        }
                    };
                    layoutState.beginVerification = (includeDelayed = false) => {
                        layoutState.verificationAttemptsRemaining = 3;
                        layoutState.attachObservers?.();
                        // 再確認の要求だけでは正常なプレイヤーの寸法を作り直さない。
                        layoutState.scheduleApply(false);
                        if (includeDelayed) {
                            scheduleDelayedVerifications();
                        }
                    };
                    layoutState.setInteractiveResize = (generation, active) => {
                        if (!Number.isSafeInteger(generation) || generation < 0) {
                            return { ok: false, reason: "invalid-generation" };
                        }

                        if (generation < layoutState.interactiveResizeGeneration) {
                            return { ok: false, reason: "stale-generation" };
                        }

                        layoutState.interactiveResizeGeneration = generation;
                        const nextActive = Boolean(active);
                        if (layoutState.interactiveResize === nextActive) {
                            return { ok: true, active: nextActive, generation };
                        }

                        layoutState.interactiveResize = nextActive;
                        if (nextActive) {
                            clearDelayedVerifications();
                            window.cancelAnimationFrame(layoutState.applyFrame);
                            window.cancelAnimationFrame(layoutState.verificationFrame);
                            layoutState.applyFrame = null;
                            layoutState.verificationFrame = null;
                            layoutState.scheduled = false;
                            layoutState.forceApply = false;
                            layoutState.verificationAttemptsRemaining = 0;
                            layoutState.layoutVerified = false;
                        } else {
                            layoutState.beginVerification();
                        }

                        return { ok: true, active: nextActive, generation };
                    };
                    const handleVideoReady = event => {
                        const video = event.target;
                        if (video instanceof HTMLVideoElement && getPlayer()?.contains(video)) {
                            const isNewVideo = layoutState.activeVideo !== video;
                            layoutState.activeVideo = video;
                            layoutState.beginVerification(isNewVideo);
                        }
                    };
                    const handleNavigateStart = () => {
                        clearDelayedVerifications();
                        layoutState.activeVideo = null;
                        layoutState.appliedVideo = null;
                        layoutState.layoutVerified = false;
                        layoutState.verificationAttemptsRemaining = 0;
                        window.cancelAnimationFrame(layoutState.verificationFrame);
                        layoutState.verificationFrame = null;
                    };
                    const handleNavigateFinish = () => layoutState.beginVerification(true);
                    layoutState.attachResizeObserver = (player, video) => {
                        const playerHost = player instanceof HTMLElement
                            ? player.closest("ytd-player")
                            : null;
                        const videoContainer = video instanceof HTMLVideoElement
                            ? video.closest(".html5-video-container")
                            : null;
                        const resizeElements = [playerHost, player, videoContainer, video]
                            .filter((element, index, elements) => element instanceof HTMLElement
                                && elements.indexOf(element) === index);
                        const hasSameElements = resizeElements.length === layoutState.observedResizeElements.length
                            && resizeElements.every((element, index) => element === layoutState.observedResizeElements[index]);
                        if (hasSameElements) {
                            return;
                        }

                        layoutState.elementResizeObserver?.disconnect?.();
                        layoutState.elementResizeObserver = null;
                        layoutState.observedResizeElements = resizeElements;
                        if (typeof ResizeObserver !== "function" || resizeElements.length === 0) {
                            return;
                        }

                        layoutState.elementResizeObserver = new ResizeObserver(scheduleApply);
                        for (const element of resizeElements) {
                            layoutState.elementResizeObserver.observe(element);
                        }
                    };
                    const attachContentTransformObserver = video => {
                        const styleElements = [...layoutState.observedResizeElements,
                            ...getVideoLayoutParents(getPlayer(), video), video?.parentElement]
                            .filter((element, index, elements) => element instanceof HTMLElement
                                && elements.indexOf(element) === index);
                        if (layoutState.observedContentTransformVideo === video
                            && styleElements.length === layoutState.observedStyleElements.length
                            && styleElements.every((element, index) => element === layoutState.observedStyleElements[index])) {
                            return;
                        }

                        layoutState.contentTransformObserver?.disconnect?.();
                        layoutState.contentTransformObserver = null;
                        layoutState.observedContentTransformVideo = video;
                        layoutState.observedStyleElements = styleElements;
                        if (styleElements.length > 0) {
                            layoutState.contentTransformObserver = new MutationObserver(() => {
                                const currentVideo = getVideo();
                                if (getPlayer() !== layoutState.observedPlayer
                                    || currentVideo !== layoutState.observedContentTransformVideo
                                    || (currentVideo?.parentElement
                                        && !layoutState.observedStyleElements.includes(currentVideo.parentElement))
                                    || getVideoLayoutParents(getPlayer(), currentVideo)
                                        .some(element => !layoutState.observedStyleElements.includes(element))) {
                                    attachAndSchedule();
                                    return;
                                }

                                scheduleVideoStyleRepair();
                            });
                            // 動画とその配置要素だけ。子孫全体は監視しない。
                            for (const element of styleElements) {
                                layoutState.contentTransformObserver.observe(element, {
                                    attributes: true,
                                    attributeFilter: ["style", "class"],
                                    childList: true
                                });
                            }
                        }
                    };
                    layoutState.attachObservers = () => {
                        const player = getPlayer();
                        const video = getVideo();
                        const playerParent = player?.parentElement || null;
                        if (layoutState.observedPlayer !== player
                            || layoutState.observedPlayerParent !== playerParent) {
                            layoutState.playerParentObserver?.disconnect?.();
                            layoutState.playerParentObserver = null;
                            layoutState.observedPlayer = player;
                            layoutState.observedPlayerParent = playerParent;
                            if (playerParent instanceof HTMLElement) {
                                layoutState.playerParentObserver = new MutationObserver(() => {
                                    if (getPlayer() !== layoutState.observedPlayer) {
                                        attachAndSchedule();
                                    }
                                });
                                layoutState.playerParentObserver.observe(playerParent, { childList: true });
                            }
                        }

                        const flexy = document.querySelector("ytd-watch-flexy");
                        if (layoutState.observedFlexy !== flexy) {
                            layoutState.flexyObserver?.disconnect?.();
                            layoutState.flexyObserver = null;
                            layoutState.observedFlexy = flexy;
                            if (flexy instanceof HTMLElement) {
                                layoutState.flexyObserver = new MutationObserver(attachAndSchedule);
                                layoutState.flexyObserver.observe(flexy, {
                                    attributes: true,
                                    attributeFilter: ["class", "theater", "fullscreen"]
                                });
                            }
                        }

                        layoutState.attachResizeObserver(player, video);
                        attachContentTransformObserver(video);

                        if (player instanceof HTMLElement && video instanceof HTMLVideoElement) {
                            layoutState.discoveryObserver?.disconnect?.();
                            layoutState.discoveryObserver = null;
                            return;
                        }

                        if (!layoutState.discoveryObserver) {
                            const discoveryRoot = document.body || document.documentElement;
                            if (discoveryRoot) {
                                layoutState.discoveryObserver = new MutationObserver(attachAndSchedule);
                                layoutState.discoveryObserver.observe(discoveryRoot, {
                                    childList: true,
                                    subtree: true
                                });
                            }
                        }
                    };
                    layoutState.attachObservers();
                    window.addEventListener("resize", scheduleApply);
                    document.addEventListener("yt-navigate-start", handleNavigateStart, true);
                    document.addEventListener("yt-navigate-finish", handleNavigateFinish, true);
                    const videoReadyEvents = ["loadedmetadata", "loadeddata", "canplay", "playing", "resize"];
                    for (const eventName of videoReadyEvents) {
                        document.addEventListener(eventName, handleVideoReady, true);
                    }
                    layoutState.dispose = () => {
                        layoutState.disposed = true;
                        clearDelayedVerifications();
                        window.cancelAnimationFrame(layoutState.applyFrame);
                        window.cancelAnimationFrame(layoutState.verificationFrame);
                        layoutState.playerParentObserver?.disconnect?.();
                        layoutState.flexyObserver?.disconnect?.();
                        layoutState.discoveryObserver?.disconnect?.();
                        layoutState.elementResizeObserver?.disconnect?.();
                        layoutState.contentTransformObserver?.disconnect?.();

                        window.removeEventListener("resize", scheduleApply);
                        document.removeEventListener("yt-navigate-start", handleNavigateStart, true);
                        document.removeEventListener("yt-navigate-finish", handleNavigateFinish, true);
                        for (const eventName of videoReadyEvents) {
                            document.removeEventListener(eventName, handleVideoReady, true);
                        }
                    };
                    window[layoutBridgeKey] = layoutState;
                    layoutState.beginVerification(true);
                }

                Object.defineProperty(window, "__tabBrowserSetYouTubeMiniPlayerInteractiveResize", {
                    configurable: true,
                    writable: false,
                    value: (token, generation, active) => {
                        if (token !== fullScreenMessageToken) {
                            return { ok: false, reason: "invalid-token" };
                        }

                        const bridge = window[layoutBridgeKey];
                        if (!bridge || typeof bridge.setInteractiveResize !== "function") {
                            return { ok: false, reason: "layout-bridge-unavailable" };
                        }

                        return bridge.setInteractiveResize(generation, active);
                    }
                });

                const isYouTubeNativeMenuOpen = () =>
                    [...document.querySelectorAll(".ytp-popup, .ytp-panel-menu")].some(menu => {
                        if (menu.getClientRects().length === 0) {
                            return false;
                        }

                        const style = window.getComputedStyle(menu);
                        return style.display !== "none"
                            && style.visibility !== "hidden"
                            && Number(style.opacity) > 0;
                    });
                const hoverBridgeKey = "__tabBrowserYouTubeMiniPlayerHoverBridge";
                const hoverBridgeVersion = 7;
                const existingHoverBridge = window[hoverBridgeKey];
                if (existingHoverBridge?.version !== hoverBridgeVersion) {
                    existingHoverBridge?.dispose?.();
                    let lastHoverSentAt = 0;
                    let lastHovered = null;
                    let lastTitleHovered = null;
                    const isTitleBandHotZone = (event) => {
                        const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                        const hotZoneHeight = Math.min(96, Math.max(56, viewportHeight * 0.18));
                        return event.clientY >= 0 && event.clientY <= hotZoneHeight;
                    };
                    const isToolbarHotZone = (event) => {
                        const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
                        const hotZoneHeight = Math.min(96, Math.max(56, viewportHeight * 0.18));
                        return event.clientY >= viewportHeight - hotZoneHeight && event.clientY <= viewportHeight;
                    };
                    const hideToolbarForSettings = event => {
                        if (event.isTrusted === false
                            || (event.type === "pointerdown" && event.button !== 0)
                            || (event.type === "keydown"
                                && (event.repeat || (event.key !== "Enter" && event.key !== " ")))
                            || !event.target?.closest?.(".ytp-settings-button")) {
                            return;
                        }

                        window.chrome?.webview?.postMessage?.({
                            type: "youtubeMiniPlayerHideToolbar",
                            token: fullScreenMessageToken
                        });
                    };
                    const sendHover = (hovered, titleHovered, force = false) => {
                        if (!window.chrome?.webview?.postMessage) {
                            return;
                        }

                        const allowToolbar = Boolean(hovered) && !isYouTubeNativeMenuOpen();

                        const now = Date.now();
                        if (!force && allowToolbar === lastHovered && titleHovered === lastTitleHovered && now - lastHoverSentAt < 250) {
                            return;
                        }

                        lastHovered = allowToolbar;
                        lastTitleHovered = titleHovered;
                        lastHoverSentAt = now;
                        window.chrome.webview.postMessage({
                            type: "youtubeMiniPlayerHover",
                            hovered: allowToolbar,
                            titleHovered: Boolean(titleHovered)
                        });
                    };

                    const handleHoverMove = event =>
                        sendHover(isToolbarHotZone(event), isTitleBandHotZone(event));
                    const handleMouseEnter = event =>
                        sendHover(isToolbarHotZone(event), isTitleBandHotZone(event), true);
                    const handleMouseLeave = () => sendHover(false, false, true);
                    const handleWindowBlur = () => sendHover(false, false, true);
                    document.addEventListener("pointerdown", hideToolbarForSettings, true);
                    document.addEventListener("keydown", hideToolbarForSettings, true);
                    window.addEventListener("pointermove", handleHoverMove, true);
                    document.addEventListener("mouseenter", handleMouseEnter, true);
                    document.addEventListener("mouseleave", handleMouseLeave, true);
                    window.addEventListener("blur", handleWindowBlur);
                    window[hoverBridgeKey] = {
                        version: hoverBridgeVersion,
                        dispose: () => {
                            window.removeEventListener("pointermove", handleHoverMove, true);
                            document.removeEventListener("mouseenter", handleMouseEnter, true);
                            document.removeEventListener("mouseleave", handleMouseLeave, true);
                            document.removeEventListener("pointerdown", hideToolbarForSettings, true);
                            document.removeEventListener("keydown", hideToolbarForSettings, true);
                            window.removeEventListener("blur", handleWindowBlur);
                        }
                    };
                }

                const nativeDragGuardKey = "__tabBrowserYouTubeMiniPlayerNativeDragGuard";
                if (!window[nativeDragGuardKey]) {
                    document.addEventListener("dragstart", event => {
                        event.preventDefault();
                    }, true);
                    window[nativeDragGuardKey] = true;
                }

                const gestureSettingsKey = "__tabBrowserYouTubeMiniPlayerGestureSettings";
                const normalizeMiniPlayerGestureSettings = settings => {
                    const mappings = Array.isArray(settings?.mappings)
                        ? settings.mappings
                            .filter(mapping => typeof mapping?.route === "string"
                                && mapping.route.length > 0
                                && mapping.route.length <= 3
                                && [...mapping.route].every(direction => "←→↑↓".includes(direction)))
                            .map(mapping => ({
                                route: mapping.route,
                                label: typeof mapping.label === "string" && mapping.label.trim()
                                    ? mapping.label.trim()
                                    : "割り当て済み"
                            }))
                        : [];
                    return {
                        mappings,
                        startThreshold: Math.max(4, Math.min(100, Number(settings?.startThreshold) || 14)),
                        directionThreshold: Math.max(4, Math.min(100, Number(settings?.directionThreshold) || 22))
                    };
                };
                window[gestureSettingsKey] = normalizeMiniPlayerGestureSettings(
                    __TAB_BROWSER_YOUTUBE_MINI_PLAYER_GESTURE_SETTINGS__);
                Object.defineProperty(window, "__tabBrowserYouTubeMiniPlayerUpdateGestureSettings", {
                    configurable: true,
                    writable: false,
                    value: (token, settings) => {
                        if (token !== fullScreenMessageToken) {
                            return false;
                        }

                        window[gestureSettingsKey] = normalizeMiniPlayerGestureSettings(settings);
                        return true;
                    }
                });

                const dragBridgeKey = "__tabBrowserYouTubeMiniPlayerDragBridge";
                if (!window[dragBridgeKey]) {
                    const state = {
                        dragging: false,
                        moved: false,
                        pointerId: null,
                        startX: 0,
                        startY: 0,
                        resizeMode: false
                    };
                    const sendDrag = (phase, event) => {
                        if (!window.chrome?.webview?.postMessage) {
                            return;
                        }

                        window.chrome.webview.postMessage({
                            type: "youtubeMiniPlayerDrag",
                            phase,
                            resize: state.resizeMode
                        });
                    };
                    const isYouTubeInteractiveTarget = target => Boolean(target?.closest?.([
                            "a[href]",
                            "button",
                            "input",
                            "select",
                            "textarea",
                            "[role='button']",
                            "[role='menuitem']",
                            "[role='slider']",
                            "[contenteditable='true']",
                            ".ytp-chrome-bottom",
                            ".ytp-chrome-controls",
                            ".ytp-progress-bar-container",
                            ".ytp-progress-bar",
                            ".ytp-volume-area",
                            ".ytp-volume-panel",
                            ".ytp-volume-slider",
                            ".ytp-settings-button",
                            ".ytp-subtitles-button",
                            ".ytp-miniplayer-button",
                            ".ytp-size-button",
                            ".ytp-fullscreen-button",
                            ".ytp-button",
                            ".ytp-popup",
                            ".ytp-panel-menu",
                            ".ytp-menuitem"
                        ].join(",")));
                    const isYouTubeControlsTarget = (event) => {
                        if (isYouTubeNativeMenuOpen() || isYouTubeInteractiveTarget(event.target)) {
                            return true;
                        }

                        const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                        const bounds = player?.getBoundingClientRect?.();
                        if (!bounds) {
                            return false;
                        }

                        const controlsHeight = Math.min(96, Math.max(56, bounds.height * 0.18));
                        return event.clientY >= bounds.bottom - controlsHeight;
                    };
                    const isYouTubeEndScreenTarget = target => Boolean(target?.closest?.([
                            ".html5-endscreen",
                            ".ytp-endscreen-content",
                            ".ytp-ce-element",
                            ".ytp-videowall-still",
                            ".ytp-autonav-endscreen-upnext-container"
                        ].join(",")));
                    const isYouTubeRightInputBlockedTarget = event =>
                        !isYouTubeEndScreenTarget(event.target) && isYouTubeControlsTarget(event);
                    const setContentPanCursor = value => {
                        if (value) {
                            document.documentElement.setAttribute(
                                "data-tabbrowser-mini-player-content-cursor",
                                value);
                        } else {
                            document.documentElement.removeAttribute(
                                "data-tabbrowser-mini-player-content-cursor");
                        }
                    };
                    const updateContentPanCursor = event => {
                        setContentPanCursor(
                            contentTransformState.zoomFactor > 1 && !isYouTubeControlsTarget(event)
                                ? "grab"
                                : "");
                    };
                    const nativePostMessage = window.chrome?.webview?.postMessage?.bind(window.chrome.webview);
                    let pressedMiniPlayerSideButton = null;
                    const isMiniPlayerSideButton = button => button === 3 || button === 4;
                    document.addEventListener("pointerdown", event => {
                        if (!event.isTrusted || !isMiniPlayerSideButton(event.button)) {
                            return;
                        }

                        pressedMiniPlayerSideButton = event.button;
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    document.addEventListener("pointerup", event => {
                        if (!event.isTrusted || !isMiniPlayerSideButton(event.button)) {
                            return;
                        }

                        const shouldExecute = pressedMiniPlayerSideButton === event.button;
                        pressedMiniPlayerSideButton = null;
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        if (shouldExecute) {
                            nativePostMessage?.({
                                type: "youtubeMiniPlayerSideButton",
                                button: event.button === 3 ? "xButton1" : "xButton2",
                                token: fullScreenMessageToken
                            });
                        }
                    }, true);
                    document.addEventListener("pointercancel", () => {
                        pressedMiniPlayerSideButton = null;
                    }, true);
                    document.addEventListener("auxclick", event => {
                        if (!event.isTrusted || !isMiniPlayerSideButton(event.button)) {
                            return;
                        }

                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    window.addEventListener("blur", () => {
                        pressedMiniPlayerSideButton = null;
                    });
                    const dragGestureThreshold = 6;
                    const suppressVideoSurfaceClick = event => {
                        if (!event.isTrusted || event.button !== 0
                            || isYouTubeNativeMenuOpen() || isYouTubeInteractiveTarget(event.target)) {
                            return;
                        }

                        event.preventDefault();
                        event.stopImmediatePropagation();
                    };
                    const sendHoldSeekPhase = phase => nativePostMessage?.({
                        type: "youtubeMiniPlayerHoldSeek",
                        phase,
                        token: fullScreenMessageToken
                    });
                    let miniPlayerRightWheelActive = false;
                    const sendRightWheel = (phase, delta = 0, anchorX = 0, anchorY = 0) => nativePostMessage?.({
                        type: "youtubeMiniPlayerRightWheel",
                        phase,
                        delta,
                        anchorX,
                        anchorY,
                        token: fullScreenMessageToken
                    });
                    const sendContentTransform = () => nativePostMessage?.({
                        type: "youtubeMiniPlayerContentTransform",
                        zoomFactor: contentTransformState.zoomFactor,
                        panX: contentTransformState.panX,
                        panY: contentTransformState.panY,
                        token: fullScreenMessageToken
                    });
                    const contentPanState = {
                        active: false,
                        moved: false,
                        suppressAuxClick: false,
                        pointerId: null,
                        startX: 0,
                        startY: 0,
                        startPanX: 0,
                        startPanY: 0
                    };
                    const contentPanThreshold = 6;
                    const finishContentPan = (event, persist = true) => {
                        if (!contentPanState.active
                            || (event?.pointerId !== undefined
                                && contentPanState.pointerId !== event.pointerId)) {
                            return false;
                        }

                        const pointerId = contentPanState.pointerId;
                        const moved = contentPanState.moved;
                        contentPanState.active = false;
                        contentPanState.moved = false;
                        contentPanState.pointerId = null;
                        if (pointerId !== null) {
                            try {
                                document.documentElement.releasePointerCapture(pointerId);
                            } catch {
                            }
                        }
                        if (moved && persist) {
                            sendContentTransform();
                        }
                        if (moved) {
                            showContentTransformHud();
                        }
                        if (event) {
                            updateContentPanCursor(event);
                        } else {
                            setContentPanCursor("");
                        }
                        if (event) {
                            event.preventDefault();
                            event.stopImmediatePropagation();
                        }
                        return true;
                    };
                    const cancelContentPan = event => {
                        if (!contentPanState.active) {
                            return false;
                        }

                        if (contentPanState.moved) {
                            contentTransformState.panX = contentPanState.startPanX;
                            contentTransformState.panY = contentPanState.startPanY;
                            applyContentTransform(getVideo());
                        }
                        return finishContentPan(event, false);
                    };
                    document.addEventListener("pointerdown", event => {
                        if (!event.isTrusted
                            || event.button !== 1
                            || event.isPrimary === false
                            || contentTransformState.zoomFactor <= 1
                            || isYouTubeControlsTarget(event)) {
                            return;
                        }

                        contentPanState.active = true;
                        contentPanState.moved = false;
                        contentPanState.suppressAuxClick = true;
                        contentPanState.pointerId = event.pointerId;
                        contentPanState.startX = event.screenX;
                        contentPanState.startY = event.screenY;
                        contentPanState.startPanX = contentTransformState.panX;
                        contentPanState.startPanY = contentTransformState.panY;
                        try {
                            document.documentElement.setPointerCapture(event.pointerId);
                        } catch {
                        }
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    document.addEventListener("pointermove", event => {
                        if (!contentPanState.active || contentPanState.pointerId !== event.pointerId) {
                            updateContentPanCursor(event);
                            return;
                        }

                        const deltaX = event.screenX - contentPanState.startX;
                        const deltaY = event.screenY - contentPanState.startY;
                        if (!contentPanState.moved
                            && Math.hypot(deltaX, deltaY) < contentPanThreshold) {
                            event.preventDefault();
                            event.stopImmediatePropagation();
                            return;
                        }

                        const zoomFactor = contentTransformState.zoomFactor;
                        setContentPanCursor("grabbing");
                        const maxTranslationX = Math.max(1, (window.innerWidth || 1) * (zoomFactor - 1) / 2);
                        const maxTranslationY = Math.max(1, (window.innerHeight || 1) * (zoomFactor - 1) / 2);
                        const rawPanX = contentPanState.startPanX + (deltaX / maxTranslationX);
                        const rawPanY = contentPanState.startPanY + (deltaY / maxTranslationY);
                        contentTransformState.panX = normalizeContentPan(rawPanX);
                        contentTransformState.panY = normalizeContentPan(rawPanY);
                        if (rawPanX < -1 || rawPanX > 1 || rawPanY < -1 || rawPanY > 1) {
                            showContentPanEdgeFeedback();
                        }
                        contentPanState.moved = true;
                        applyContentTransform(getVideo());
                        showContentTransformHud(true);
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    document.addEventListener("pointerup", event => finishContentPan(event), true);
                    document.addEventListener("pointercancel", event => finishContentPan(event), true);
                    document.addEventListener("auxclick", event => {
                        if (event.button !== 1 || !contentPanState.suppressAuxClick) {
                            return;
                        }

                        contentPanState.suppressAuxClick = false;
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    document.addEventListener("keydown", event => {
                        if (event.key === "Escape" && contentPanState.active) {
                            cancelContentPan(event);
                        }
                    }, true);
                    const sendCloseGesture = () => nativePostMessage?.({
                        type: "youtubeMiniPlayerCloseGesture",
                        token: fullScreenMessageToken
                    });
                    const sendGesturePhase = (phase, route = "") => nativePostMessage?.({
                        type: "youtubeMiniPlayerGesture",
                        phase,
                        route,
                        token: fullScreenMessageToken
                    });
                    window.addEventListener("click", suppressVideoSurfaceClick, true);
                    window.addEventListener("dblclick", (event) => {
                        if (!event.isTrusted || isYouTubeControlsTarget(event) || !nativePostMessage) {
                            return;
                        }

                        event.preventDefault();
                        event.stopPropagation();
                        nativePostMessage({
                            type: "youtubeMiniPlayerToggleFullScreen",
                            token: fullScreenMessageToken
                        });
                    }, true);
                    const finishDrag = (event) => {
                        if (!state.dragging || state.pointerId !== event.pointerId) {
                            return;
                        }

                        if (state.moved) {
                            event.preventDefault();
                            event.stopPropagation();
                            sendDrag("end", event);
                        }
                        state.dragging = false;
                        state.moved = false;
                        state.pointerId = null;
                        state.resizeMode = false;
                        try {
                            document.documentElement.releasePointerCapture(event.pointerId);
                        } catch {
                        }
                    };

                    document.addEventListener("pointerdown", (event) => {
                        if (event.button !== 0 || event.isPrimary === false) {
                            return;
                        }

                        if (isYouTubeControlsTarget(event)) {
                            return;
                        }

                        state.dragging = true;
                        state.moved = false;
                        state.pointerId = event.pointerId;
                        state.startX = event.screenX;
                        state.startY = event.screenY;
                        state.resizeMode = event.ctrlKey;
                    }, true);
                    document.addEventListener("pointermove", (event) => {
                        if (!state.dragging || state.pointerId !== event.pointerId) {
                            return;
                        }

                        const deltaX = Math.abs(event.screenX - state.startX);
                        const deltaY = Math.abs(event.screenY - state.startY);
                        if (!state.moved && deltaX < dragGestureThreshold && deltaY < dragGestureThreshold) {
                            return;
                        }

                        if (!state.moved) {
                            state.moved = true;
                            try {
                                document.documentElement.setPointerCapture(event.pointerId);
                            } catch {
                            }
                            sendDrag("start", event);
                        }
                        event.preventDefault();
                        event.stopPropagation();
                        sendDrag("move", event);
                    }, true);
                    document.addEventListener("pointerup", finishDrag, true);
                    document.addEventListener("pointercancel", finishDrag, true);
                    window.addEventListener("blur", () => {
                        if (!state.dragging || !state.moved || !window.chrome?.webview?.postMessage) {
                            return;
                        }

                        window.chrome.webview.postMessage({
                            type: "youtubeMiniPlayerDrag",
                            phase: "end"
                        });
                        state.dragging = false;
                        state.moved = false;
                        state.pointerId = null;
                        state.resizeMode = false;
                    });

                    const holdSeekState = {
                        tracking: false,
                        active: false,
                        direction: 0,
                        pointerId: null,
                        startX: 0,
                        startY: 0,
                        directionAnchorX: 0,
                        directionAnchorY: 0,
                        video: null,
                        wasPaused: true,
                        previousPlaybackRate: 1,
                        holdUpdateTimer: null,
                        gestureActive: false,
                        gestureRoute: "",
                        gestureAnchorX: 0,
                        gestureAnchorY: 0,
                        closeGestureArmed: false,
                        closeGestureCancelled: false,
                        suppressContextMenu: false,
                        suppressContextMenuTimer: null,
                        indicatorHideTimer: null,
                        gestureGuideTimer: null,
                        gestureGuideVisible: false
                    };
                    const holdSeekGestureThreshold = 24;
                    const miniPlayerGestureGuideDelayMilliseconds = 450;
                    const miniPlayerGestureGuideMovementTolerance = 4;
                    const rewindStepSeconds = 0.5;
                    const rewindIntervalMilliseconds = 250;
                    const isAdvertisementPlaying = () => __TAB_BROWSER_YOUTUBE_MINI_PLAYER_AD_PLAYING__;
                    const getHoldSeekIndicator = () => {
                        let indicator = document.getElementById("tabbrowser-youtube-mini-player-hold-seek-indicator");
                        if (indicator) {
                            return indicator;
                        }

                        indicator = document.createElement("div");
                        indicator.id = "tabbrowser-youtube-mini-player-hold-seek-indicator";
                        Object.assign(indicator.style, {
                            position: "fixed",
                            left: "50%",
                            top: "50%",
                            transform: "translate(-50%, -50%)",
                            zIndex: "2147483647",
                            display: "none",
                            padding: "10px 16px",
                            borderRadius: "6px",
                            background: "rgba(0, 0, 0, 0.72)",
                            color: "#fff",
                            font: "600 20px/1.3 sans-serif",
                            fontVariantNumeric: "tabular-nums",
                            textAlign: "center",
                            whiteSpace: "pre",
                            minWidth: "160px",
                            maxWidth: "calc(100vw - 24px)",
                            boxSizing: "border-box",
                            pointerEvents: "none",
                            opacity: "0",
                            transition: "opacity 100ms linear"
                        });
                        document.documentElement.appendChild(indicator);
                        return indicator;
                    };
                    const formatPlaybackTime = value => {
                        if (!Number.isFinite(value) || value < 0) {
                            return "0:00";
                        }

                        const totalSeconds = Math.floor(value);
                        const seconds = totalSeconds % 60;
                        const totalMinutes = Math.floor(totalSeconds / 60);
                        const minutes = totalMinutes % 60;
                        const hours = Math.floor(totalMinutes / 60);
                        const paddedSeconds = String(seconds).padStart(2, "0");
                        if (hours > 0) {
                            return `${hours}:${String(minutes).padStart(2, "0")}:${paddedSeconds}`;
                        }

                        return `${totalMinutes}:${paddedSeconds}`;
                    };
                    const getHoldSeekPlaybackTimeText = video => {
                        const currentTimeText = formatPlaybackTime(video.currentTime);
                        if (!Number.isFinite(video.duration) || video.duration <= 0) {
                            return currentTimeText;
                        }

                        return `${currentTimeText} / ${formatPlaybackTime(video.duration)}`;
                    };
                    const showHoldSeekIndicator = text => {
                        const indicator = getHoldSeekIndicator();
                        if (holdSeekState.indicatorHideTimer) {
                            clearTimeout(holdSeekState.indicatorHideTimer);
                            holdSeekState.indicatorHideTimer = null;
                        }

                        indicator.textContent = text;
                        indicator.style.display = "block";
                        window.requestAnimationFrame(() => {
                            indicator.style.opacity = "1";
                        });
                    };
                    const hideHoldSeekIndicator = () => {
                        const indicator = document.getElementById("tabbrowser-youtube-mini-player-hold-seek-indicator");
                        if (!indicator) {
                            return;
                        }

                        indicator.style.opacity = "0";
                        holdSeekState.indicatorHideTimer = setTimeout(() => {
                            indicator.style.display = "none";
                            holdSeekState.indicatorHideTimer = null;
                        }, 120);
                    };
                    const getMiniPlayerGestureSettings = () => window[gestureSettingsKey]
                        || { mappings: [], startThreshold: 14, directionThreshold: 22 };
                    const getMiniPlayerGestureMapping = route => getMiniPlayerGestureSettings()
                        .mappings
                        .find(mapping => mapping.route === route);
                    const hasMiniPlayerGesturePrefix = route => getMiniPlayerGestureSettings()
                        .mappings
                        .some(mapping => mapping.route.startsWith(route));
                    const getMiniPlayerGestureGuideText = () => {
                        const percentage = `${Math.round(normalizeContentZoom(contentTransformState.zoomFactor) * 100)}%`;
                        const panAvailability = contentTransformState.zoomFactor > 1 ? "" : "（拡大時）";
                        return `← / →  一時シーク   ↓  閉じる\n`
                            + `右＋ホイール  ズーム (${percentage})\n`
                            + `ホイールボタン＋ドラッグ  表示位置${panAvailability}`;
                    };
                    const clearMiniPlayerGestureGuide = () => {
                        if (holdSeekState.gestureGuideTimer) {
                            clearTimeout(holdSeekState.gestureGuideTimer);
                            holdSeekState.gestureGuideTimer = null;
                        }
                        if (holdSeekState.gestureGuideVisible) {
                            holdSeekState.gestureGuideVisible = false;
                            hideHoldSeekIndicator();
                        }
                    };
                    const scheduleMiniPlayerGestureGuide = () => {
                        clearMiniPlayerGestureGuide();
                        holdSeekState.gestureGuideTimer = setTimeout(() => {
                            holdSeekState.gestureGuideTimer = null;
                            if (!holdSeekState.tracking
                                || holdSeekState.active
                                || holdSeekState.gestureActive
                                || holdSeekState.closeGestureArmed) {
                                return;
                            }

                            holdSeekState.gestureGuideVisible = true;
                            showHoldSeekIndicator(getMiniPlayerGestureGuideText());
                        }, miniPlayerGestureGuideDelayMilliseconds);
                    };
                    const getDominantGestureDirection = (deltaX, deltaY, threshold) => {
                        const absoluteX = Math.abs(deltaX);
                        const absoluteY = Math.abs(deltaY);
                        if (Math.max(absoluteX, absoluteY) < threshold) {
                            return "";
                        }

                        if (absoluteX >= absoluteY * 1.25) {
                            return deltaX < 0 ? "←" : "→";
                        }
                        if (absoluteY >= absoluteX * 1.25) {
                            return deltaY < 0 ? "↑" : "↓";
                        }
                        return "";
                    };
                    const isOppositeGestureDirection = (first, second) => (
                        first === "←" && second === "→"
                        || first === "→" && second === "←"
                        || first === "↑" && second === "↓"
                        || first === "↓" && second === "↑"
                    );
                    const updateMiniPlayerGestureIndicator = () => {
                        const route = holdSeekState.gestureRoute;
                        const exact = getMiniPlayerGestureMapping(route);
                        const canContinue = hasMiniPlayerGesturePrefix(route)
                            && getMiniPlayerGestureSettings().mappings.some(mapping => mapping.route.length > route.length
                                && mapping.route.startsWith(route));
                        const label = exact?.label || (canContinue ? "入力中" : "実行する機能なし");
                        showHoldSeekIndicator(`${route}\n${label}`);
                    };
                    const activateMiniPlayerGesture = (direction, event) => {
                        if (!direction || !hasMiniPlayerGesturePrefix(direction)) {
                            return false;
                        }

                        clearMiniPlayerGestureGuide();
                        holdSeekState.gestureActive = true;
                        holdSeekState.gestureRoute = direction;
                        holdSeekState.gestureAnchorX = event.screenX;
                        holdSeekState.gestureAnchorY = event.screenY;
                        sendGesturePhase("start");
                        updateMiniPlayerGestureIndicator();
                        event.preventDefault();
                        event.stopImmediatePropagation();
                        return true;
                    };
                    const appendMiniPlayerGestureDirection = event => {
                        const settings = getMiniPlayerGestureSettings();
                        const deltaX = event.screenX - holdSeekState.gestureAnchorX;
                        const deltaY = event.screenY - holdSeekState.gestureAnchorY;
                        const direction = getDominantGestureDirection(
                            deltaX,
                            deltaY,
                            settings.directionThreshold);
                        if (!direction) {
                            return;
                        }

                        const previousDirection = holdSeekState.gestureRoute.slice(-1);
                        const primaryDistance = direction === "←" || direction === "→"
                            ? Math.abs(deltaX)
                            : Math.abs(deltaY);
                        if (previousDirection
                            && isOppositeGestureDirection(previousDirection, direction)
                            && primaryDistance < settings.directionThreshold * 1.5) {
                            return;
                        }

                        holdSeekState.gestureAnchorX = event.screenX;
                        holdSeekState.gestureAnchorY = event.screenY;
                        if (previousDirection === direction || holdSeekState.gestureRoute.length >= 3) {
                            return;
                        }

                        holdSeekState.gestureRoute += direction;
                        updateMiniPlayerGestureIndicator();
                    };
                    const suppressMiniPlayerContextMenu = () => {
                        holdSeekState.suppressContextMenu = true;
                        if (holdSeekState.suppressContextMenuTimer) {
                            clearTimeout(holdSeekState.suppressContextMenuTimer);
                        }

                        holdSeekState.suppressContextMenuTimer = setTimeout(() => {
                            holdSeekState.suppressContextMenu = false;
                            holdSeekState.suppressContextMenuTimer = null;
                        }, 600);
                    };
                    const finishMiniPlayerGesture = (event, allowComplete = true) => {
                        if (!holdSeekState.gestureActive) {
                            return false;
                        }

                        const route = holdSeekState.gestureRoute;
                        const pointerId = holdSeekState.pointerId;
                        holdSeekState.tracking = false;
                        holdSeekState.gestureActive = false;
                        holdSeekState.gestureRoute = "";
                        holdSeekState.pointerId = null;
                        if (pointerId !== null) {
                            try {
                                document.documentElement.releasePointerCapture(pointerId);
                            } catch {
                            }
                        }
                        hideHoldSeekIndicator();
                        if (allowComplete && event?.type === "pointerup") {
                            sendGesturePhase("complete", route);
                        } else {
                            sendGesturePhase("cancel");
                        }
                        suppressMiniPlayerContextMenu();
                        event?.preventDefault?.();
                        event?.stopImmediatePropagation?.();
                        return true;
                    };
                    const updateHoldSeekIndicator = (applyRewindStep = true) => {
                        const video = holdSeekState.video;
                        if (!video) {
                            return;
                        }

                        if (isAdvertisementPlaying()) {
                            showHoldSeekIndicator("広告の再生中は操作できません");
                            return { ok: false, skipped: true, reason: "advertisement" };
                        }

                        if (holdSeekState.direction < 0 && applyRewindStep) {
                            video.currentTime = Math.max(0, video.currentTime - rewindStepSeconds);
                        }

                        const modeText = holdSeekState.direction > 0
                            ? `${video.playbackRate}x`
                            : "巻き戻し";
                        showHoldSeekIndicator(`${modeText}\n${getHoldSeekPlaybackTimeText(video)}`);
                    };
                    const restoreHoldSeekPlayback = () => {
                        if (holdSeekState.holdUpdateTimer) {
                            clearInterval(holdSeekState.holdUpdateTimer);
                            holdSeekState.holdUpdateTimer = null;
                        }

                        const video = holdSeekState.video;
                        if (video && !isAdvertisementPlaying()) {
                            video.playbackRate = holdSeekState.previousPlaybackRate;
                            if (holdSeekState.wasPaused) {
                                video.pause();
                            } else if (video.paused) {
                                Promise.resolve(video.play()).catch(() => { });
                            }
                        }

                        holdSeekState.video = null;
                        hideHoldSeekIndicator();
                    };
                    const applyHoldSeekDirection = direction => {
                        if (isAdvertisementPlaying()) {
                            showHoldSeekIndicator("広告の再生中は操作できません");
                            return { ok: false, skipped: true, reason: "advertisement" };
                        }

                        const video = holdSeekState.video;
                        if (!video) {
                            return;
                        }

                        holdSeekState.direction = direction;
                        if (direction > 0) {
                            const fastPlaybackRate = Math.max(2, holdSeekState.previousPlaybackRate);
                            video.playbackRate = fastPlaybackRate;
                            if (video.paused) {
                                Promise.resolve(video.play()).catch(() => { });
                            }
                        } else {
                            video.playbackRate = holdSeekState.previousPlaybackRate;
                            video.pause();
                        }

                        updateHoldSeekIndicator(false);
                    };
                    const activateHoldSeek = (direction, event) => {
                        if (isAdvertisementPlaying()) {
                            showHoldSeekIndicator("広告の再生中は操作できません");
                            return { ok: false, skipped: true, reason: "advertisement" };
                        }

                        const video = getVideo();
                        if (!video) {
                            return;
                        }

                        clearMiniPlayerGestureGuide();
                        holdSeekState.active = true;
                        holdSeekState.video = video;
                        holdSeekState.wasPaused = video.paused;
                        holdSeekState.previousPlaybackRate = Number.isFinite(video.playbackRate)
                            ? video.playbackRate
                            : 1;
                        holdSeekState.directionAnchorX = event.screenX;
                        holdSeekState.directionAnchorY = event.screenY;
                        applyHoldSeekDirection(direction);
                        holdSeekState.holdUpdateTimer = setInterval(
                            updateHoldSeekIndicator,
                            rewindIntervalMilliseconds);
                        sendHoldSeekPhase("start");

                        event.preventDefault();
                        event.stopImmediatePropagation();
                    };
                    const switchHoldSeekDirection = (direction, event) => {
                        if (!holdSeekState.active || holdSeekState.direction === direction) {
                            return;
                        }

                        holdSeekState.directionAnchorX = event.screenX;
                        holdSeekState.directionAnchorY = event.screenY;
                        applyHoldSeekDirection(direction);
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    };
                    const finishHoldSeek = event => {
                        clearMiniPlayerGestureGuide();
                        if (!holdSeekState.tracking
                            && !holdSeekState.active
                            && !holdSeekState.closeGestureArmed) {
                            return;
                        }

                        if (event
                            && holdSeekState.pointerId !== null
                            && "pointerId" in event
                            && event.pointerId !== holdSeekState.pointerId) {
                            return;
                        }

                        const wasActive = holdSeekState.active;
                        const wasCloseGestureCancelled = holdSeekState.closeGestureCancelled;
                        const shouldClose = holdSeekState.closeGestureArmed
                            && event?.type === "pointerup";
                        const pointerId = holdSeekState.pointerId;
                        holdSeekState.tracking = false;
                        holdSeekState.active = false;
                        holdSeekState.closeGestureArmed = false;
                        holdSeekState.closeGestureCancelled = false;
                        holdSeekState.direction = 0;
                        holdSeekState.pointerId = null;
                        restoreHoldSeekPlayback();
                        if (pointerId !== null) {
                            try {
                                document.documentElement.releasePointerCapture(pointerId);
                            } catch {
                            }
                        }

                        if (!wasActive && !shouldClose && !wasCloseGestureCancelled) {
                            return;
                        }

                        if (wasActive) {
                            sendHoldSeekPhase("end");
                        }
                        if (shouldClose) {
                            sendCloseGesture();
                        }

                        suppressMiniPlayerContextMenu();
                        event?.preventDefault?.();
                        event?.stopImmediatePropagation?.();
                    };
                    document.addEventListener("pointerdown", event => {
                        if (!event.isTrusted
                            || event.button !== 2
                            || event.isPrimary === false
                            || isYouTubeRightInputBlockedTarget(event)) {
                            return;
                        }

                        holdSeekState.tracking = true;
                        holdSeekState.active = false;
                        holdSeekState.closeGestureArmed = false;
                        holdSeekState.closeGestureCancelled = false;
                        holdSeekState.direction = 0;
                        holdSeekState.gestureActive = false;
                        holdSeekState.gestureRoute = "";
                        holdSeekState.pointerId = event.pointerId;
                        holdSeekState.startX = event.screenX;
                        holdSeekState.startY = event.screenY;
                        try {
                            document.documentElement.setPointerCapture(event.pointerId);
                        } catch {
                        }
                        scheduleMiniPlayerGestureGuide();
                    }, true);
                    document.addEventListener("pointermove", event => {
                        if (!holdSeekState.tracking || holdSeekState.pointerId !== event.pointerId) {
                            return;
                        }

                        if (Math.hypot(
                            event.screenX - holdSeekState.startX,
                            event.screenY - holdSeekState.startY) > miniPlayerGestureGuideMovementTolerance) {
                            clearMiniPlayerGestureGuide();
                        }

                        if (holdSeekState.closeGestureCancelled) {
                            event.preventDefault();
                            event.stopImmediatePropagation();
                            return;
                        }

                        if (holdSeekState.gestureActive) {
                            appendMiniPlayerGestureDirection(event);
                            event.preventDefault();
                            event.stopImmediatePropagation();
                            return;
                        }

                        if (!holdSeekState.active && !holdSeekState.closeGestureArmed) {
                            const deltaX = event.screenX - holdSeekState.startX;
                            const deltaY = event.screenY - holdSeekState.startY;
                            const configuredDirection = getDominantGestureDirection(
                                deltaX,
                                deltaY,
                                getMiniPlayerGestureSettings().startThreshold);
                            if (activateMiniPlayerGesture(configuredDirection, event)) {
                                return;
                            }
                            const isCloseGesture = deltaY >= holdSeekGestureThreshold
                                && Math.abs(deltaY) >= Math.abs(deltaX) * 1.5;
                            if (isCloseGesture) {
                                clearMiniPlayerGestureGuide();
                                holdSeekState.closeGestureArmed = true;
                                holdSeekState.directionAnchorX = event.screenX;
                                holdSeekState.directionAnchorY = event.screenY;
                                showHoldSeekIndicator("\u9589\u3058\u308b");
                                event.preventDefault();
                                event.stopImmediatePropagation();
                                return;
                            }

                            if (Math.abs(deltaX) < holdSeekGestureThreshold
                                || Math.abs(deltaX) < Math.abs(deltaY) * 1.5) {
                                return;
                            }

                            activateHoldSeek(deltaX > 0 ? 1 : -1, event);
                            return;
                        }

                        if (holdSeekState.closeGestureArmed) {
                            const closeDirectionDeltaX = event.screenX - holdSeekState.directionAnchorX;
                            const closeDirectionDeltaY = event.screenY - holdSeekState.directionAnchorY;
                            const closeDirectionDistance = Math.max(
                                Math.abs(closeDirectionDeltaX),
                                Math.abs(closeDirectionDeltaY));
                            if (closeDirectionDistance >= holdSeekGestureThreshold) {
                                const continuesCloseDirection = closeDirectionDeltaY >= holdSeekGestureThreshold
                                    && Math.abs(closeDirectionDeltaY) >= Math.abs(closeDirectionDeltaX) * 1.5;
                                if (continuesCloseDirection) {
                                    holdSeekState.directionAnchorX = event.screenX;
                                    holdSeekState.directionAnchorY = event.screenY;
                                } else {
                                    holdSeekState.closeGestureArmed = false;
                                    holdSeekState.closeGestureCancelled = true;
                                    hideHoldSeekIndicator();
                                }
                            }

                            event.preventDefault();
                            event.stopImmediatePropagation();
                            return;
                        }

                        const directionDeltaX = event.screenX - holdSeekState.directionAnchorX;
                        const directionDeltaY = event.screenY - holdSeekState.directionAnchorY;
                        const hasHorizontalDirectionChange = Math.abs(directionDeltaX) >= holdSeekGestureThreshold
                            && Math.abs(directionDeltaX) >= Math.abs(directionDeltaY) * 1.5;
                        if (hasHorizontalDirectionChange
                            && ((holdSeekState.direction > 0 && directionDeltaX < 0)
                                || (holdSeekState.direction < 0 && directionDeltaX > 0))) {
                            switchHoldSeekDirection(directionDeltaX > 0 ? 1 : -1, event);
                            return;
                        }

                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    const finishMiniPlayerRightWheel = () => {
                        if (!miniPlayerRightWheelActive) {
                            return;
                        }

                        miniPlayerRightWheelActive = false;
                        sendRightWheel("end");
                    };
                    const finishRightButtonInput = event => {
                        finishMiniPlayerGesture(event);
                        finishHoldSeek(event);
                        finishMiniPlayerRightWheel();
                    };
                    document.addEventListener("pointerup", finishRightButtonInput, true);
                    document.addEventListener("pointercancel", finishRightButtonInput, true);
                    document.addEventListener("contextmenu", event => {
                        holdSeekState.suppressContextMenu = false;
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, true);
                    document.addEventListener("wheel", event => {
                        if ((event.buttons & 2) !== 2 || isYouTubeRightInputBlockedTarget(event)) {
                            return;
                        }

                        clearMiniPlayerGestureGuide();
                        if (holdSeekState.gestureActive) {
                            finishMiniPlayerGesture(null, false);
                        }
                        if (holdSeekState.tracking || holdSeekState.active) {
                            finishHoldSeek();
                        }

                        miniPlayerRightWheelActive = true;
                        const wheelDelta = event.deltaMode === 1
                            ? event.deltaY * 40
                            : event.deltaMode === 2
                                ? event.deltaY * 100
                                : event.deltaY;
                        const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1;
                        const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 1;
                        const anchorX = normalizeContentPan((event.clientX / viewportWidth * 2) - 1);
                        const anchorY = normalizeContentPan((event.clientY / viewportHeight * 2) - 1);
                        sendRightWheel("wheel", wheelDelta, anchorX, anchorY);
                        event.preventDefault();
                        event.stopImmediatePropagation();
                    }, { capture: true, passive: false });
                    Object.defineProperty(window, "__tabBrowserYouTubeMiniPlayerCancelRightInput", {
                        configurable: true,
                        writable: false,
                        value: (token, inputKind = "all") => {
                            if (token !== fullScreenMessageToken) {
                                return false;
                            }

                            if (inputKind === "all" || inputKind === "gesture") {
                                finishMiniPlayerGesture(null, false);
                            }
                            if (inputKind === "all" || inputKind === "holdSeek") {
                                finishHoldSeek();
                            }
                            if (inputKind === "all" || inputKind === "rightWheel") {
                                finishMiniPlayerRightWheel();
                            }
                            return true;
                        }
                    });
                    window.addEventListener("blur", () => {
                        setContentPanCursor("");
                        finishContentPan(null);
                        finishMiniPlayerGesture(null, false);
                        finishHoldSeek();
                        finishMiniPlayerRightWheel();
                    });
                    window.addEventListener("pagehide", () => {
                        setContentPanCursor("");
                        finishContentPan(null);
                        finishMiniPlayerGesture(null, false);
                        finishHoldSeek();
                        finishMiniPlayerRightWheel();
                    });
                    window[dragBridgeKey] = true;
                }

                window.dispatchEvent(new Event("resize"));
                const player = getPlayer();
                const video = getVideo();
                const videoRect = video?.getBoundingClientRect?.();
                const canonicalUrl = document.querySelector('link[rel="canonical"]')?.href || "";
                let isShortsPage = location.pathname.startsWith("/shorts/");
                if (!isShortsPage && canonicalUrl) {
                    try {
                        isShortsPage = new URL(canonicalUrl, location.href).pathname.startsWith("/shorts/");
                    } catch {
                    }
                }

                return {
                    ok: Boolean(video && videoRect && videoRect.width > 0 && videoRect.height > 0),
                    bridgeInstalled: true,
                    hasPlayer: Boolean(player),
                    hasVideo: Boolean(video),
                    videoWidth: Number(videoRect?.width) || 0,
                    videoHeight: Number(videoRect?.height) || 0,
                    videoNaturalWidth: Number(video?.videoWidth) || 0,
                    videoNaturalHeight: Number(video?.videoHeight) || 0,
                    isShortsPage,
                    viewportWidth: window.innerWidth || document.documentElement.clientWidth || 0,
                    viewportHeight: window.innerHeight || document.documentElement.clientHeight || 0
                };
            })();
            """.Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_MESSAGE_TOKEN__",
                    serializedWebMessageToken,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_GESTURE_SETTINGS__",
                    serializedGestureSettings,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_ZOOM__",
                    serializedContentZoomFactor,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_PAN_X__",
                    serializedContentPanX,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_PAN_Y__",
                    serializedContentPanY,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_AUDIO_BOOST_DECIBELS__",
                    serializedAudioBoostDecibels,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_AD_PLAYING__",
                    AdvertisementPlayingExpression,
                    StringComparison.Ordinal)
                .Replace(
                    "__TAB_BROWSER_YOUTUBE_MINI_PLAYER_AUDIO_BOOST_GAIN__",
                    serializedAudioBoostGain,
                    StringComparison.Ordinal);
    }

    public static string BuildSetMiniPlayerContentTransformScript(
        string webMessageToken,
        double contentZoomFactor,
        double contentPanX,
        double contentPanY)
    {
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedContentZoomFactor = JsonSerializer.Serialize(
            double.IsFinite(contentZoomFactor) ? Math.Clamp(contentZoomFactor, 1, 4) : 1);
        var serializedContentPanX = JsonSerializer.Serialize(
            double.IsFinite(contentPanX) ? Math.Clamp(contentPanX, -1, 1) : 0);
        var serializedContentPanY = JsonSerializer.Serialize(
            double.IsFinite(contentPanY) ? Math.Clamp(contentPanY, -1, 1) : 0);
        return $"window.__tabBrowserYouTubeMiniPlayerSetContentTransform?.({serializedWebMessageToken}, {serializedContentZoomFactor}, {serializedContentPanX}, {serializedContentPanY}, true);";
    }

    public static string BuildSetMiniPlayerInteractiveResizeScript(
        string webMessageToken,
        int generation,
        bool isActive)
    {
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedGeneration = JsonSerializer.Serialize(generation);
        var serializedIsActive = JsonSerializer.Serialize(isActive);
        return $$"""
            (() => {
                const setInteractiveResize = window.__tabBrowserSetYouTubeMiniPlayerInteractiveResize;
                if (typeof setInteractiveResize !== "function") {
                    return { ok: false, reason: "interactive-resize-bridge-unavailable" };
                }

                return setInteractiveResize(
                    {{serializedWebMessageToken}},
                    {{serializedGeneration}},
                    {{serializedIsActive}});
            })();
            """;
    }

    public static string BuildSetMiniPlayerAudioBoostScript(
        string webMessageToken,
        int audioBoostDecibels)
    {
        var normalizedDecibels = YouTubeMiniPlayerAudioBoostProfile.Normalize(audioBoostDecibels);
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedDecibels = JsonSerializer.Serialize(normalizedDecibels);
        var serializedGain = JsonSerializer.Serialize(
            YouTubeMiniPlayerAudioBoostProfile.GetLinearGain(normalizedDecibels));
        return $$"""
            (async () => {
                const setAudioBoost = window.__tabBrowserSetYouTubeMiniPlayerAudioBoost;
                if (typeof setAudioBoost !== "function") {
                    return { ok: false, reason: "audio-boost-bridge-unavailable" };
                }

                return await setAudioBoost(
                    {{serializedWebMessageToken}},
                    {{serializedDecibels}},
                    {{serializedGain}});
            })();
            """;
    }

    public static string BuildUpdateMiniPlayerGestureSettingsScript(
        string webMessageToken,
        IEnumerable<GestureMappingSettings>? gestureMappings,
        double gestureStartThreshold,
        double gestureDirectionThreshold)
    {
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedGestureSettings = SerializeMiniPlayerGestureSettings(
            gestureMappings,
            gestureStartThreshold,
            gestureDirectionThreshold);
        return $"window.__tabBrowserYouTubeMiniPlayerUpdateGestureSettings?.({serializedWebMessageToken}, {serializedGestureSettings});";
    }

    public static string BuildCancelMiniPlayerRightInputScript(
        string webMessageToken,
        string inputKind = "all")
    {
        var serializedWebMessageToken = JsonSerializer.Serialize(webMessageToken);
        var serializedInputKind = JsonSerializer.Serialize(inputKind);
        return $"window.__tabBrowserYouTubeMiniPlayerCancelRightInput?.({serializedWebMessageToken}, {serializedInputKind});";
    }

    private static string SerializeMiniPlayerGestureSettings(
        IEnumerable<GestureMappingSettings>? gestureMappings,
        double gestureStartThreshold,
        double gestureDirectionThreshold)
    {
        var mappings = GestureSettingsCatalog.ResolveMappingsForScope(
                gestureMappings,
                GestureCommandScope.MiniPlayer)
            .Where(static mapping => !mapping.Route.StartsWith("wheel", StringComparison.Ordinal))
            .Select(static mapping => new
            {
                route = mapping.Route,
                label = mapping.Label
            })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            mappings,
            startThreshold = NormalizeGestureThreshold(gestureStartThreshold, 14),
            directionThreshold = NormalizeGestureThreshold(gestureDirectionThreshold, 22)
        });
    }

    private static double NormalizeGestureThreshold(double value, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 4, 100) : fallback;
    }

    public static string BuildPlaybackStateBridgeScript(
        string? webMessageToken,
        bool isActive = true)
    {
        var webMessageTokenJson = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        var activeText = isActive ? "true" : "false";
        return $$"""
            (() => {
                const host = String(window.location.hostname || "").toLowerCase();
                if (window.location.protocol !== "https:"
                    || (host !== "youtu.be" && host !== "youtube.com" && !host.endsWith(".youtube.com"))) {
                    return { ok: false, reason: "untrusted-origin" };
                }

                const key = "__tabBrowserYouTubePlaybackStateBridge";
                const bridgeVersion = 8;
                const webMessageToken = {{webMessageTokenJson}};
                const initialActive = {{activeText}};
                if (window[key]?.version === bridgeVersion
                    && typeof window[key].attach === "function"
                    && typeof window[key].setActive === "function") {
                    window[key].attach();
                    window[key].setActive(initialActive);
                    return { ok: true, alreadyInstalled: true };
                }

                const state = {
                    active: initialActive,
                    video: null,
                    theaterElement: null,
                    listeners: [],
                    videoDiscoveryObserver: null,
                    theaterObserver: null,
                    lastSentAt: 0,
                    minPositionUpdateIntervalMilliseconds: 1500
                };

                const getPlayer = () => document.getElementById("movie_player")
                    || document.querySelector(".html5-video-player");
                const getActiveVideo = () => {
                    const player = getPlayer();
                    return player?.querySelector("video.html5-main-video")
                        || player?.querySelector("video.video-stream")
                        || player?.querySelector("video")
                        || document.querySelector("video.html5-main-video")
                        || document.querySelector("video.video-stream");
                };
                const getSpeed = (video) => {
                    const player = getPlayer();
                    const playerSpeed = player && typeof player.getPlaybackRate === "function"
                        ? Number(player.getPlaybackRate())
                        : Number.NaN;
                    if (Number.isFinite(playerSpeed) && playerSpeed > 0) {
                        return playerSpeed;
                    }

                    const videoSpeed = Number(video?.playbackRate);
                    if (Number.isFinite(videoSpeed) && videoSpeed > 0) {
                        return videoSpeed;
                    }

                    return null;
                };

                const isTheaterMode = () => Boolean(document.querySelector("ytd-watch-flexy[theater]"));

                const postPlaybackState = (message) => {
                    const nativePost = window.chrome?.webview?.postMessage;
                    if (typeof nativePost === "function" && webMessageToken) {
                        nativePost.call(window.chrome.webview, {
                            ...message,
                            type: "youtubePlaybackState",
                            tabBrowserToken: webMessageToken
                        });
                        return true;
                    }

                    const hostPost = window.__tabBrowserPostYouTubePlaybackState;
                    if (typeof hostPost === "function") {
                        hostPost(message);
                        return true;
                    }

                    return false;
                };

                const send = (force = false, reason = "update") => {
                    const now = Date.now();
                    if (!force && now - state.lastSentAt < state.minPositionUpdateIntervalMilliseconds) {
                        return;
                    }

                    const video = getActiveVideo();
                    if (!video) {
                        return;
                    }

                    if (!postPlaybackState({
                        url: location.href,
                        position: Number(video.currentTime) || 0,
                        speed: getSpeed(video),
                        paused: Boolean(video.paused),
                        looping: Boolean(video.loop),
                        theaterMode: isTheaterMode(),
                        reason
                    })) {
                        return;
                    }

                    state.lastSentAt = now;
                };

                const clearListeners = () => {
                    for (const item of state.listeners) {
                        item.video.removeEventListener(item.name, item.handler, true);
                    }

                    state.listeners = [];
                };

                const addListener = (video, name, handler) => {
                    video.addEventListener(name, handler, true);
                    state.listeners.push({ video, name, handler });
                };

                const stopVideoDiscovery = () => {
                    if (state.videoDiscoveryObserver) {
                        state.videoDiscoveryObserver.disconnect();
                        state.videoDiscoveryObserver = null;
                    }
                };

                const attach = () => {
                    const video = getActiveVideo();
                    if (!video || video === state.video) {
                        return Boolean(video);
                    }

                    clearListeners();
                    stopVideoDiscovery();
                    state.video = video;
                    addListener(video, "pause", () => send(true, "pause"));
                    addListener(video, "play", () => send(true, "play"));
                    addListener(video, "ended", () => send(true, "ended"));
                    addListener(video, "ratechange", () => send(true, "ratechange"));
                    addListener(video, "seeked", () => send(true, "seeked"));
                    addListener(video, "loadedmetadata", () => send(true, "loadedmetadata"));
                    addListener(video, "canplay", () => send(true, "canplay"));
                    addListener(video, "timeupdate", () => {
                        if (state.active) {
                            send(false, "timeupdate");
                        }
                    });
                    send(true, "attach");
                    return true;
                };

                const attachTheaterObserver = () => {
                    const theaterElement = document.querySelector("ytd-watch-flexy");
                    if (!theaterElement || theaterElement === state.theaterElement) {
                        return;
                    }

                    if (state.theaterObserver) {
                        state.theaterObserver.disconnect();
                    }

                    state.theaterElement = theaterElement;
                    state.theaterObserver = new MutationObserver(() => send(true, "theater"));
                    state.theaterObserver.observe(theaterElement, { attributes: true, attributeFilter: ["theater"] });
                };

                const startVideoDiscovery = () => {
                    if (state.videoDiscoveryObserver || !document.documentElement) {
                        return;
                    }

                    state.videoDiscoveryObserver = new MutationObserver((records) => {
                        for (const record of records) {
                            for (const node of record.addedNodes) {
                                if (node instanceof HTMLVideoElement) {
                                    attach();
                                    return;
                                }

                                if (node instanceof Element) {
                                    const video = node.querySelector("video");
                                    if (video) {
                                        attach();
                                        return;
                                    }
                                }
                            }
                        }
                    });
                    state.videoDiscoveryObserver.observe(document.documentElement, { childList: true, subtree: true });
                };

                const setActive = (value) => {
                    const nextActive = Boolean(value);
                    if (state.active && !nextActive) {
                        send(true, "deactivate");
                    }

                    state.active = nextActive;
                    if (nextActive) {
                        attach();
                        send(true, "activate");
                    }

                    return state.active;
                };

                const onMediaReady = (event) => {
                    if (event.target === getActiveVideo()) {
                        attach();
                    }
                };
                const onNavigateFinish = () => {
                    state.video = null;
                    clearListeners();
                    if (!attach()) {
                        startVideoDiscovery();
                    }

                    attachTheaterObserver();
                };

                document.addEventListener("loadedmetadata", onMediaReady, true);
                document.addEventListener("canplay", onMediaReady, true);
                document.addEventListener("yt-navigate-finish", onNavigateFinish, true);
                document.addEventListener("visibilitychange", () => send(true, "visibilitychange"), true);
                window.addEventListener("pagehide", () => send(true, "pagehide"), true);
                window.addEventListener("beforeunload", () => send(true, "beforeunload"), true);
                window[key] = { version: bridgeVersion, attach, setActive, send };
                if (!attach()) {
                    startVideoDiscovery();
                }

                attachTheaterObserver();
                return { ok: true, installed: true };
            })();
            """;
    }

    public static string BuildSetPlaybackStateBridgeActiveScript(bool isActive)
    {
        var activeText = isActive ? "true" : "false";
        return $"window.__tabBrowserYouTubePlaybackStateBridge?.setActive?.({activeText});";
    }

    public static string BuildPausedResumeGuardScript(string videoId)
    {
        var videoIdJson = JsonSerializer.Serialize(videoId);

        return $$"""
            (() => {
                const key = "__tabBrowserYouTubePausedResumeGuard";
                const expectedVideoId = {{videoIdJson}};
                const readVideoId = () => {
                    try {
                        const url = new URL(location.href);
                        const host = url.hostname.toLowerCase();
                        const isYouTube =
                            host === "youtu.be"
                            || host === "youtube.com"
                            || host.endsWith(".youtube.com")
                            || host === "youtube-nocookie.com"
                            || host.endsWith(".youtube-nocookie.com");
                        if (!isYouTube) {
                            return "";
                        }

                        if (host === "youtu.be") {
                            return decodeURIComponent(url.pathname.split("/").filter(Boolean)[0] || "");
                        }

                        const segments = url.pathname.split("/").filter(Boolean);
                        if (segments.length >= 2 && ["shorts", "embed", "live"].includes(segments[0].toLowerCase())) {
                            return decodeURIComponent(segments[1] || "");
                        }

                        if (url.pathname.replace(/\/+$/, "").toLowerCase() === "/watch") {
                            return url.searchParams.get("v") || "";
                        }
                    } catch {
                    }

                    return "";
                };

                const isExpectedVideo = () => readVideoId() === expectedVideoId;
                const existing = window[key];
                if (existing && existing.expectedVideoId === expectedVideoId) {
                    existing.enabled = true;
                    return { ok: true, alreadyInstalled: true };
                }

                const originalPlay = HTMLMediaElement.prototype.play;
                const originalSetAttribute = Element.prototype.setAttribute;
                const state = {
                    enabled: true,
                    expectedVideoId,
                    originalPlay,
                    originalSetAttribute,
                    observer: null,
                    intervalId: 0,
                    intervalTicksRemaining: 150
                };

                const disableForUserGesture = event => {
                    if (event?.isTrusted && isExpectedVideo()) {
                        state.enabled = false;
                        if (state.intervalId) {
                            clearInterval(state.intervalId);
                            state.intervalId = 0;
                        }
                    }
                };

                document.addEventListener("pointerdown", disableForUserGesture, true);
                document.addEventListener("keydown", disableForUserGesture, true);

                const shouldStopVideo = () => state.enabled && isExpectedVideo();
                const stopVideo = (video) => {
                    if (!video || !shouldStopVideo()) {
                        return;
                    }

                    try {
                        const player = document.getElementById("movie_player")
                            || document.querySelector(".html5-video-player");
                        if (player && typeof player.pauseVideo === "function") {
                            player.pauseVideo();
                        }
                    } catch {
                    }

                    try {
                        video.autoplay = false;
                        video.removeAttribute("autoplay");
                    } catch {
                    }

                    try {
                        if (!video.paused) {
                            video.pause();
                        }
                    } catch {
                    }
                };

                const stopVideos = () => {
                    if (!shouldStopVideo()) {
                        return;
                    }

                    for (const video of document.querySelectorAll("video")) {
                        stopVideo(video);
                    }
                };

                Element.prototype.setAttribute = function(name, value) {
                    if (state.enabled
                        && this instanceof HTMLMediaElement
                        && String(name).toLowerCase() === "autoplay"
                        && isExpectedVideo()) {
                        return;
                    }

                    return originalSetAttribute.call(this, name, value);
                };

                HTMLMediaElement.prototype.play = function(...args) {
                    if (shouldStopVideo()) {
                        stopVideo(this);
                        return Promise.reject(new DOMException(
                            "Background playback is paused until user interaction.",
                            "NotAllowedError"));
                    }

                    return originalPlay.apply(this, args);
                };

                const stopUnexpectedPlay = (event) => {
                    stopVideo(event.target);
                };

                const installObserver = () => {
                    stopVideos();
                    if (!document.documentElement) {
                        return;
                    }

                    state.observer = new MutationObserver(() => stopVideos());
                    state.observer.observe(document.documentElement, {
                        childList: true,
                        subtree: true,
                        attributes: true,
                        attributeFilter: ["autoplay"]
                    });
                };

                document.addEventListener("play", stopUnexpectedPlay, true);
                document.addEventListener("playing", stopUnexpectedPlay, true);
                document.addEventListener("loadedmetadata", stopUnexpectedPlay, true);
                document.addEventListener("canplay", stopUnexpectedPlay, true);
                document.addEventListener("timeupdate", stopUnexpectedPlay, true);
                if (document.documentElement) {
                    installObserver();
                } else {
                    document.addEventListener("DOMContentLoaded", installObserver, { once: true, capture: true });
                }

                state.intervalId = setInterval(() => {
                    state.intervalTicksRemaining -= 1;
                    if (!state.enabled || !isExpectedVideo() || state.intervalTicksRemaining <= 0) {
                        clearInterval(state.intervalId);
                        state.intervalId = 0;
                        return;
                    }

                    stopVideos();
                }, 100);
                window[key] = state;
                return { ok: true, installed: true };
            })();
            """;
    }

    public static string BuildPlaybackStateRestoreScript(
        double? positionSeconds,
        double? speed,
        bool? isPaused,
        bool? isLooping,
        bool? isTheaterMode,
        bool respectMiniPlayerUserOverride = false)
    {
        var positionText = ToJavaScriptNullableNumber(NormalizePlaybackPosition(positionSeconds));
        var speedText = ToJavaScriptNullableNumber(NormalizePlaybackSpeed(speed));
        var pauseText = isPaused == true ? "true" : "false";
        var loopingText = isLooping.HasValue ? (isLooping.Value ? "true" : "false") : "null";
        var theaterModeText = isTheaterMode.HasValue ? (isTheaterMode.Value ? "true" : "false") : "null";
        var respectUserOverrideText = respectMiniPlayerUserOverride ? "true" : "false";

        return $$"""
            (() => {
                const targetPosition = {{positionText}};
                const targetSpeed = {{speedText}};
                const pauseAfterRestore = {{pauseText}};
                const targetLooping = {{loopingText}};
                const targetTheaterMode = {{theaterModeText}};
                const respectMiniPlayerUserOverride = {{respectUserOverrideText}};
                if ({{AdvertisementPlayingExpression}}) {
                    return { ok: false, found: true, skipped: true, reason: "advertisement" };
                }
                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard;
                const userOverrodePlayback = respectMiniPlayerUserOverride
                    && Boolean(savedStateRestoreGuard?.playback);
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const setTheaterMode = (enabled) => {
                    const current = Boolean(document.querySelector("ytd-watch-flexy[theater]"));
                    if (current === enabled) {
                        return;
                    }

                    const sizeButton = document.querySelector(".ytp-size-button");
                    if (sizeButton instanceof HTMLElement) {
                        sizeButton.click();
                    }
                };

                if (targetTheaterMode !== null) {
                    setTheaterMode(targetTheaterMode);
                }

                if (targetLooping !== null) {
                    video.loop = targetLooping;
                    if (targetLooping) {
                        video.setAttribute("loop", "");
                    } else {
                        video.removeAttribute("loop");
                    }

                    if (player && typeof player.setLoop === "function") {
                        player.setLoop(targetLooping);
                    }
                }

                if (targetSpeed !== null) {
                    if (player && typeof player.setPlaybackRate === "function") {
                        player.setPlaybackRate(targetSpeed);
                    }

                    video.playbackRate = targetSpeed;
                }

                if (targetPosition !== null && targetPosition > 0 && !userOverrodePlayback) {
                    const duration = Number(video.duration);
                    const maxPosition = Number.isFinite(duration) && duration > 2
                        ? Math.max(0, duration - 1)
                        : targetPosition;
                    const position = Math.max(0, Math.min(targetPosition, maxPosition));
                    if (player && typeof player.seekTo === "function") {
                        player.seekTo(position, true);
                    }

                    video.currentTime = position;
                }

                if (pauseAfterRestore && !userOverrodePlayback) {
                    if (player && typeof player.pauseVideo === "function") {
                        player.pauseVideo();
                    }

                    if (typeof video.pause === "function") {
                        video.pause();
                    }
                }

                let currentSpeed = targetSpeed;
                if (player && typeof player.getPlaybackRate === "function") {
                    currentSpeed = Number(player.getPlaybackRate()) || currentSpeed || 1;
                } else {
                    currentSpeed = Number(video.playbackRate) || currentSpeed || 1;
                }

                return {
                    ok: true,
                    found: true,
                    position: Number(video.currentTime) || 0,
                    speed: currentSpeed,
                    paused: Boolean(video.paused),
                    looping: Boolean(video.loop),
                    theaterMode: Boolean(document.querySelector("ytd-watch-flexy[theater]")),
                    skippedUserOverride: userOverrodePlayback
                };
            })();
            """;
    }

    public static string BuildPlaybackStateRestoreReadyBridgeScript(
        string? webMessageToken,
        string videoId,
        double? positionSeconds,
        double? speed,
        bool? isPaused,
        bool? isLooping,
        bool? isTheaterMode)
    {
        var webMessageTokenJson = JsonSerializer.Serialize(webMessageToken ?? string.Empty);
        var videoIdJson = JsonSerializer.Serialize(videoId);
        var requestKeyJson = JsonSerializer.Serialize(JsonSerializer.Serialize(new
        {
            videoId,
            positionSeconds = NormalizePlaybackPosition(positionSeconds),
            speed = NormalizePlaybackSpeed(speed),
            isPaused,
            isLooping,
            isTheaterMode
        }));
        var theaterModeText = isTheaterMode.HasValue ? (isTheaterMode.Value ? "true" : "false") : "null";
        var restoreScript = BuildPlaybackStateRestoreScript(
            positionSeconds,
            speed,
            isPaused,
            isLooping,
            isTheaterMode);

        return $$"""
            (() => {
                const host = String(window.location.hostname || "").toLowerCase();
                if (window.location.protocol !== "https:"
                    || (host !== "youtu.be" && host !== "youtube.com" && !host.endsWith(".youtube.com"))) {
                    return { ok: false, reason: "untrusted-origin" };
                }

                const key = "__tabBrowserYouTubePlaybackStateRestoreReadyBridge";
                const webMessageToken = {{webMessageTokenJson}};
                const expectedVideoId = {{videoIdJson}};
                const requestKey = {{requestKeyJson}};
                const targetTheaterMode = {{theaterModeText}};
                const existing = window[key];
                if (existing?.requestKey === requestKey) {
                    existing.attempt?.();
                    return { ok: true, alreadyInstalled: true, applied: Boolean(existing.applied) };
                }

                existing?.dispose?.();
                const state = {
                    requestKey,
                    applied: false,
                    applying: false,
                    adPlayer: null,
                    adObserver: null,
                    theaterObserver: null,
                    attempt: null,
                    dispose: null
                };
                const readLocationVideoId = () => {
                    try {
                        const url = new URL(location.href);
                        const host = url.hostname.toLowerCase();
                        if (host === "youtu.be") {
                            return decodeURIComponent(url.pathname.split("/").filter(Boolean)[0] || "");
                        }

                        const segments = url.pathname.split("/").filter(Boolean);
                        if (segments.length >= 2 && ["shorts", "embed", "live"].includes(segments[0].toLowerCase())) {
                            return decodeURIComponent(segments[1] || "");
                        }

                        return url.searchParams.get("v") || "";
                    } catch {
                        return "";
                    }
                };
                const readPlayerVideoId = () => {
                    const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                    try {
                        return String(player?.getVideoData?.()?.video_id || "");
                    } catch {
                        return "";
                    }
                };
                const isExpectedVideoReady = () => {
                    const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                    if (state.adPlayer !== player) {
                        state.adObserver?.disconnect();
                        state.adPlayer = player;
                        state.adObserver = player ? new MutationObserver(() => state.attempt()) : null;
                        state.adObserver?.observe(player, { attributes: true, attributeFilter: ["class"] });
                    }
                    if ({{AdvertisementPlayingExpression}}) {
                        return false;
                    }
                    if (readLocationVideoId() !== expectedVideoId) {
                        return false;
                    }

                    const playerVideoId = readPlayerVideoId();
                    if (playerVideoId !== expectedVideoId) {
                        return false;
                    }

                    const video = document.querySelector("video");
                    return Boolean(video && video.readyState >= HTMLMediaElement.HAVE_METADATA);
                };
                const applyRestore = () => {{restoreScript}};
                const postPlaybackState = (message) => {
                    const nativePost = window.chrome?.webview?.postMessage;
                    if (typeof nativePost === "function" && webMessageToken) {
                        nativePost.call(window.chrome.webview, {
                            ...message,
                            type: "youtubePlaybackState",
                            tabBrowserToken: webMessageToken
                        });
                        return true;
                    }

                    const hostPost = window.__tabBrowserPostYouTubePlaybackState;
                    if (typeof hostPost === "function") {
                        hostPost(message);
                        return true;
                    }

                    return false;
                };
                const removeListeners = () => {
                    state.adObserver?.disconnect();
                    state.adObserver = null;
                    document.removeEventListener("loadedmetadata", state.attempt, true);
                    document.removeEventListener("canplay", state.attempt, true);
                    document.removeEventListener("playing", state.attempt, true);
                    document.removeEventListener("durationchange", state.attempt, true);
                    document.removeEventListener("yt-navigate-finish", state.attempt, true);
                    state.theaterObserver?.disconnect();
                    state.theaterObserver = null;
                };
                state.dispose = removeListeners;
                state.attempt = () => {
                    if (state.applied || state.applying || !isExpectedVideoReady()) {
                        return state.applied;
                    }

                    state.applying = true;
                    try {
                        const result = applyRestore();
                        if (!result?.ok || !result.found) {
                            return false;
                        }

                        if (targetTheaterMode !== null
                            && Boolean(result.theaterMode) !== targetTheaterMode) {
                            return false;
                        }

                        if (!postPlaybackState({
                            url: location.href,
                            position: Number(result.position) || 0,
                            speed: Number(result.speed) || 1,
                            paused: Boolean(result.paused),
                            looping: Boolean(result.looping),
                            theaterMode: Boolean(result.theaterMode),
                            reason: "restore"
                        })) {
                            return false;
                        }

                        state.applied = true;
                        removeListeners();
                        return true;
                    } finally {
                        state.applying = false;
                    }
                };

                document.addEventListener("loadedmetadata", state.attempt, true);
                document.addEventListener("canplay", state.attempt, true);
                document.addEventListener("playing", state.attempt, true);
                document.addEventListener("durationchange", state.attempt, true);
                document.addEventListener("yt-navigate-finish", state.attempt, true);
                if (targetTheaterMode !== null) {
                    const theaterElement = document.querySelector("ytd-watch-flexy");
                    if (theaterElement) {
                        state.theaterObserver = new MutationObserver(() => state.attempt());
                        state.theaterObserver.observe(theaterElement, {
                            attributes: true,
                            attributeFilter: ["theater"]
                        });
                    }
                }

                window[key] = state;
                const applied = state.attempt();
                return { ok: true, installed: true, applied };
            })();
            """;
    }

    public static string BuildMiniPlayerSavedStateReadyBridgeScript(
        string videoId,
        double? positionSeconds,
        double? speed,
        bool? isPaused,
        bool? isLooping,
        bool isMuted,
        double? volume,
        double? abRepeatStartSeconds,
        double? abRepeatEndSeconds,
        bool isAbRepeatEnabled)
    {
        var videoIdJson = JsonSerializer.Serialize(videoId);
        var requestKeyJson = JsonSerializer.Serialize(JsonSerializer.Serialize(new
        {
            videoId,
            positionSeconds = NormalizePlaybackPosition(positionSeconds),
            speed = NormalizePlaybackSpeed(speed),
            isPaused,
            isLooping,
            isMuted,
            volume,
            abRepeatStartSeconds,
            abRepeatEndSeconds,
            isAbRepeatEnabled
        }));
        var volumeScript = volume.HasValue
            ? BuildVolumeScript(volume.Value, respectMiniPlayerUserOverride: true, unmuteOnVolumeChange: false)
            : "({ ok: true, skipped: true })";
        var muteScript = BuildMuteScript(
            isMuted,
            respectMiniPlayerUserOverride: true);
        var playbackScript = BuildPlaybackStateRestoreScript(
            positionSeconds,
            speed,
            isPaused,
            isLooping,
            isTheaterMode: null,
            respectMiniPlayerUserOverride: true);
        var initialLivePlaybackScript = BuildInitialLivePlaybackScript();
        var abRepeatScript = isAbRepeatEnabled
            && abRepeatStartSeconds is { } abStart
            && abRepeatEndSeconds is { } abEnd
            && abStart >= 0
            && abEnd > abStart
                ? BuildAbRepeatScript(
                    abRepeatStartSeconds,
                    abRepeatEndSeconds,
                    isEnabled: true,
                    isWholeLooping: false,
                    expectedVideoId: videoId)
                : "({ ok: true, skipped: true })";

        return $$"""
            (() => {
                const key = "__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge";
                const expectedVideoId = {{videoIdJson}};
                const requestKey = {{requestKeyJson}};
                const existing = window[key];
                if (existing?.requestKey === requestKey) {
                    existing.attempt?.();
                    return { ok: true, alreadyInstalled: true };
                }

                existing?.dispose?.();
                const state = {
                    requestKey,
                    applied: false,
                    applying: false,
                    adPlayer: null,
                    adObserver: null,
                    attempt: null,
                    dispose: null
                };
                const readLocationVideoId = () => {
                    try {
                        const url = new URL(location.href);
                        const host = url.hostname.toLowerCase();
                        if (host === "youtu.be") {
                            return decodeURIComponent(url.pathname.split("/").filter(Boolean)[0] || "");
                        }

                        const segments = url.pathname.split("/").filter(Boolean);
                        if (segments.length >= 2 && ["shorts", "embed", "live"].includes(segments[0].toLowerCase())) {
                            return decodeURIComponent(segments[1] || "");
                        }

                        return url.searchParams.get("v") || "";
                    } catch {
                        return "";
                    }
                };
                const readPlayerVideoId = () => {
                    const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                    try {
                        return String(player?.getVideoData?.()?.video_id || "");
                    } catch {
                        return "";
                    }
                };
                const isExpectedVideoReady = () => {
                    const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                    if (state.adPlayer !== player) {
                        state.adObserver?.disconnect();
                        state.adPlayer = player;
                        state.adObserver = player ? new MutationObserver(() => state.attempt()) : null;
                        state.adObserver?.observe(player, { attributes: true, attributeFilter: ["class"] });
                    }
                    if ({{AdvertisementPlayingExpression}}) {
                        return false;
                    }
                    if (readLocationVideoId() !== expectedVideoId) {
                        return false;
                    }

                    const playerVideoId = readPlayerVideoId();
                    if (playerVideoId !== expectedVideoId) {
                        return false;
                    }

                    const video = document.querySelector("video");
                    return Boolean(video && video.readyState >= HTMLMediaElement.HAVE_METADATA);
                };
                const removeListeners = () => {
                    state.adObserver?.disconnect();
                    state.adObserver = null;
                    document.removeEventListener("loadedmetadata", state.attempt, true);
                    document.removeEventListener("canplay", state.attempt, true);
                    document.removeEventListener("durationchange", state.attempt, true);
                    document.removeEventListener("playing", state.attempt, true);
                    document.removeEventListener("yt-navigate-finish", state.attempt, true);
                };
                state.dispose = removeListeners;
                state.attempt = async () => {
                    if (state.applied || state.applying || !isExpectedVideoReady()) {
                        return state.applied;
                    }

                    state.applying = true;
                    try {
                        const muteResult = await {{muteScript}};
                        if (!muteResult?.ok || !muteResult.found) {
                            return false;
                        }

                        await {{volumeScript}};
                        const playbackResult = await {{playbackScript}};
                        if (!playbackResult?.ok || !playbackResult.found) {
                            return false;
                        }

                        if (!isExpectedVideoReady()) return false;
                        await {{initialLivePlaybackScript}};
                        if (!isExpectedVideoReady()) return false;
                        await {{abRepeatScript}};
                        const finalMuteResult = await {{muteScript}};
                        if (!finalMuteResult?.ok || !finalMuteResult.found) {
                            return false;
                        }

                        state.applied = true;
                        removeListeners();
                        return true;
                    } finally {
                        state.applying = false;
                    }
                };

                document.addEventListener("loadedmetadata", state.attempt, true);
                document.addEventListener("canplay", state.attempt, true);
                document.addEventListener("durationchange", state.attempt, true);
                document.addEventListener("playing", state.attempt, true);
                document.addEventListener("yt-navigate-finish", state.attempt, true);
                window[key] = state;
                state.attempt();
                return { ok: true, installed: true };
            })();
            """;
    }

    public static string BuildSpeedScript(double? delta, double? targetSpeed)
    {
        var deltaText = delta.HasValue ? delta.Value.ToString(CultureInfo.InvariantCulture) : "null";
        var targetText = targetSpeed.HasValue ? targetSpeed.Value.ToString(CultureInfo.InvariantCulture) : "null";

        return $$"""
            (() => {
                const minimum = {{MinimumSpeed.ToString(CultureInfo.InvariantCulture)}};
                const maximum = {{MaximumSpeed.ToString(CultureInfo.InvariantCulture)}};
                const delta = {{deltaText}};
                const targetSpeed = {{targetText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if ({{AdvertisementPlayingExpression}}) {
                    return { ok: false, found: true, skipped: true, reason: "advertisement" };
                }

                let current = 1;
                if (player && typeof player.getPlaybackRate === "function") {
                    current = Number(player.getPlaybackRate()) || 1;
                } else if (video) {
                    current = Number(video.playbackRate) || 1;
                }

                let speed = targetSpeed === null
                    ? current + delta
                    : targetSpeed;
                speed = Math.max(minimum, Math.min(maximum, Math.round(speed * 4) / 4));

                if (player && typeof player.setPlaybackRate === "function") {
                    player.setPlaybackRate(speed);
                }

                if (video) {
                    video.playbackRate = speed;
                }

                return {
                    ok: true,
                    speed
                };
            })();
            """;
    }

    public static string BuildSpeedReadScript()
    {
        return """
            (() => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                let speed = 1;
                if (player && typeof player.getPlaybackRate === "function") {
                    speed = Number(player.getPlaybackRate()) || 1;
                } else if (video) {
                    speed = Number(video.playbackRate) || 1;
                }

                return {
                    ok: true,
                    speed
                };
            })();
            """;
    }

    public static string BuildPlayPauseScript()
    {
        return """
            (async () => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard
                    || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                savedStateRestoreGuard.playback = true;

                if (video.paused) {
                    try {
                        if (player && typeof player.playVideo === "function") {
                            player.playVideo();
                        } else {
                            await video.play();
                        }
                    } catch (error) {
                        return { ok: false, found: true, reason: String(error && error.message || error) };
                    }
                } else {
                    if (player && typeof player.pauseVideo === "function") {
                        player.pauseVideo();
                    } else {
                        video.pause();
                    }
                }

                return {
                    ok: true,
                    found: true,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildPauseScript()
    {
        return """
            (() => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard
                    || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                savedStateRestoreGuard.playback = true;

                if (player && typeof player.pauseVideo === "function") {
                    player.pauseVideo();
                } else {
                    video.pause();
                }

                return {
                    ok: true,
                    found: true,
                    position: Number(video.currentTime) || 0,
                    duration: Number(video.duration) || 0,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildSetPausedScript(bool isPaused)
    {
        return BuildSetPausedScript(isPaused, managePausedResumeGuard: false);
    }

    public static string BuildSetPausedTransientScript(bool isPaused)
    {
        return BuildSetPausedScript(isPaused, managePausedResumeGuard: true);
    }

    private static string BuildSetPausedScript(bool isPaused, bool managePausedResumeGuard)
    {
        var pausedText = isPaused ? "true" : "false";
        var managePausedResumeGuardText = managePausedResumeGuard ? "true" : "false";
        return $$"""
            (() => {
                const shouldPause = {{pausedText}};
                const managePausedResumeGuard = {{managePausedResumeGuardText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard
                    || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                savedStateRestoreGuard.playback = true;

                const pausedResumeGuard = window.__tabBrowserYouTubePausedResumeGuard;
                const transientGuardStateKey = "__tabBrowserTransientOriginalEnabled";
                if (managePausedResumeGuard && pausedResumeGuard) {
                    if (shouldPause) {
                        if (Object.prototype.hasOwnProperty.call(pausedResumeGuard, transientGuardStateKey)) {
                            pausedResumeGuard.enabled = Boolean(pausedResumeGuard[transientGuardStateKey]);
                            delete pausedResumeGuard[transientGuardStateKey];
                        }
                    } else {
                        if (!Object.prototype.hasOwnProperty.call(pausedResumeGuard, transientGuardStateKey)) {
                            pausedResumeGuard[transientGuardStateKey] = Boolean(pausedResumeGuard.enabled);
                        }

                        pausedResumeGuard.enabled = false;
                    }
                }

                try {
                    if (shouldPause) {
                        if (player && typeof player.pauseVideo === "function") {
                            player.pauseVideo();
                        } else {
                            video.pause();
                        }
                    } else if (player && typeof player.playVideo === "function") {
                        player.playVideo();
                    } else {
                        void video.play().catch(() => {});
                    }
                } catch (error) {
                    return { ok: false, found: true, reason: String(error && error.message || error) };
                }

                return {
                    ok: true,
                    found: true,
                    position: Number(video.currentTime) || 0,
                    duration: Number(video.duration) || 0,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildSeekRelativeScript(double deltaSeconds)
    {
        var deltaText = Math.Max(-600, Math.Min(600, deltaSeconds)).ToString(CultureInfo.InvariantCulture);

        return $$"""
            (() => {
                const deltaSeconds = {{deltaText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                if ({{AdvertisementPlayingExpression}}) {
                    return { ok: false, found: true, skipped: true, reason: "advertisement" };
                }

                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                const maximum = duration > 0 ? duration : Number.MAX_SAFE_INTEGER;
                const position = Math.max(0, Math.min(maximum, (Number(video.currentTime) || 0) + deltaSeconds));
                if (player && typeof player.seekTo === "function") {
                    player.seekTo(position, true);
                } else {
                    video.currentTime = position;
                }

                return {
                    ok: true,
                    found: true,
                    position,
                    duration,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildSeekToRatioScript(double positionRatio)
    {
        var ratioText = Math.Clamp(positionRatio, 0, 1).ToString(CultureInfo.InvariantCulture);

        return $$"""
            (() => {
                const positionRatio = {{ratioText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                if ({{AdvertisementPlayingExpression}}) {
                    return { ok: false, found: true, skipped: true, reason: "advertisement" };
                }

                const duration = Number(video.duration);
                if (!Number.isFinite(duration) || duration <= 0) {
                    return { ok: false, found: true, reason: "duration-unavailable" };
                }

                const maximum = Math.max(0, duration - 0.25);
                const position = maximum * positionRatio;
                if (player && typeof player.seekTo === "function") {
                    player.seekTo(position, true);
                } else {
                    video.currentTime = position;
                }

                return {
                    ok: true,
                    found: true,
                    position,
                    duration,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildSeekToLiveEdgeScript()
    {
        return $$"""
            (() => {
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                if ({{AdvertisementPlayingExpression}}) {
                    return { ok: false, found: true, skipped: true, reason: "advertisement" };
                }

                const videoData = player && typeof player.getVideoData === "function"
                    ? player.getVideoData()
                    : null;
                const playerResponse = (player && typeof player.getPlayerResponse === "function"
                    ? player.getPlayerResponse()
                    : null) || window.ytInitialPlayerResponse || null;
                const details = playerResponse?.videoDetails || null;
                const broadcast = playerResponse?.microformat
                    ?.playerMicroformatRenderer
                    ?.liveBroadcastDetails || null;
                const startTime = broadcast?.startTimestamp
                    ? Date.parse(broadcast.startTimestamp)
                    : Number.NaN;
                const hasStarted = Number.isFinite(startTime)
                    && startTime <= Date.now() + 60000;
                const hasEnded = Boolean(broadcast?.endTimestamp);
                const explicitlyLive = videoData?.isLive === true || details?.isLive === true;
                const isLive = !hasEnded && (
                    (explicitlyLive && (!broadcast || hasStarted))
                    || (details?.isLiveContent === true && hasStarted));
                if (!isLive) {
                    return { ok: false, found: true, reason: "not-live", isLive: false };
                }

                const seekable = video.seekable;
                if (!seekable || seekable.length <= 0) {
                    return { ok: false, found: true, reason: "live-edge-unavailable", isLive: true };
                }

                const liveEdge = Number(seekable.end(seekable.length - 1));
                if (!Number.isFinite(liveEdge) || liveEdge < 0) {
                    return { ok: false, found: true, reason: "live-edge-unavailable", isLive: true };
                }

                const position = Math.max(0, liveEdge - 0.25);
                if (player && typeof player.seekTo === "function") {
                    player.seekTo(position, true);
                } else {
                    video.currentTime = position;
                }

                const liveBadge = player?.querySelector(".ytp-live-badge")
                    || document.querySelector(".ytp-live-badge");
                if (liveBadge
                    && !liveBadge.classList.contains("ytp-live-badge-is-livehead")
                    && typeof liveBadge.click === "function") {
                    liveBadge.click();
                }

                return {
                    ok: true,
                    found: true,
                    position,
                    duration: Number.isFinite(video.duration) ? video.duration : 0,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop),
                    isLive: true
                };
            })();
            """;
    }

    public static string BuildInitialLivePlaybackScript()
    {
        var seekToLiveEdgeScript = BuildSeekToLiveEdgeScript();
        return $$"""
            (async () => {
                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard;
                if (savedStateRestoreGuard?.playback) {
                    return { ok: true, found: true, skippedUserOverride: true };
                }

                const seekResult = {{seekToLiveEdgeScript}};
                if (!seekResult || seekResult.ok !== true) {
                    return seekResult;
                }

                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false, isLive: true };
                }

                try {
                    if (player && typeof player.playVideo === "function") {
                        player.playVideo();
                    }

                    if (typeof video.play === "function") {
                        await video.play();
                    }
                } catch (error) {
                    return {
                        ...seekResult,
                        ok: false,
                        reason: "playback-blocked",
                        error: String(error && error.message || error),
                        initialLivePlaybackApplied: false
                    };
                }

                return {
                    ...seekResult,
                    ok: true,
                    position: Number(video.currentTime) || seekResult.position || 0,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop),
                    isLive: true,
                    initialLivePlaybackApplied: true
                };
            })();
            """;
    }

    public static string BuildAbRepeatScript(
        double? startSeconds,
        double? endSeconds,
        bool isEnabled,
        bool isWholeLooping,
        string? expectedVideoId)
    {
        var normalizedStart = NormalizePlaybackPosition(startSeconds);
        var normalizedEnd = NormalizePlaybackPosition(endSeconds);
        var startJson = JsonSerializer.Serialize(normalizedStart);
        var endJson = JsonSerializer.Serialize(normalizedEnd);
        var enabledJson = JsonSerializer.Serialize(isEnabled);
        var wholeLoopingJson = JsonSerializer.Serialize(isWholeLooping);
        var videoIdJson = JsonSerializer.Serialize(expectedVideoId);
        return $$"""
            (() => {
                const bridgeKey = "__tabBrowserYouTubeMiniPlayerAbRepeat";
                const requestedStart = {{startJson}};
                const requestedEnd = {{endJson}};
                const requestedEnabled = {{enabledJson}};
                const wholeLooping = {{wholeLoopingJson}};
                const expectedVideoId = {{videoIdJson}};
                const video = document.querySelector("video");
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const existing = window[bridgeKey];
                const disposeExisting = () => {
                    existing?.dispose?.();
                    if (window[bridgeKey] === existing) {
                        delete window[bridgeKey];
                    }
                };
                if (!video) {
                    disposeExisting();
                    return { ok: false, error: "video-not-found" };
                }

                const readCurrentVideoId = () => {
                    try {
                        const url = new URL(window.location.href);
                        if (url.pathname.startsWith("/shorts/")) {
                            return url.pathname.split("/").filter(Boolean)[1] || "";
                        }

                        return url.searchParams.get("v") || "";
                    } catch {
                        return "";
                    }
                };
                const currentVideoId = readCurrentVideoId();
                if (expectedVideoId && currentVideoId && expectedVideoId !== currentVideoId) {
                    disposeExisting();
                    return { ok: false, error: "video-changed" };
                }

                const duration = Number(video.duration);
                const hasValidRange = Number.isFinite(requestedStart)
                    && Number.isFinite(requestedEnd)
                    && requestedStart >= 0
                    && requestedEnd - requestedStart >= 0.5
                    && Number.isFinite(duration)
                    && duration > 0
                    && requestedEnd <= duration + 0.5;
                if (!requestedEnabled || !hasValidRange) {
                    disposeExisting();
                    video.loop = Boolean(wholeLooping);
                    if (video.loop) {
                        video.setAttribute("loop", "");
                    } else {
                        video.removeAttribute("loop");
                    }

                    return {
                        ok: !requestedEnabled,
                        error: requestedEnabled ? "invalid-range" : null,
                        position: Number(video.currentTime) || 0,
                        duration: Number.isFinite(duration) ? duration : 0,
                        paused: Boolean(video.paused),
                        muted: Boolean(video.muted),
                        volume: Number(video.volume) || 0,
                        speed: Number(video.playbackRate) || 1,
                        looping: Boolean(video.loop),
                        abEnabled: false
                    };
                }

                let bridge = existing;
                if (bridge?.video !== video) {
                    disposeExisting();
                    bridge = null;
                }

                if (!bridge) {
                    bridge = {
                        video,
                        start: requestedStart,
                        end: requestedEnd,
                        enabled: true,
                        seeking: false,
                        onBoundary: null,
                        dispose: null
                    };
                    bridge.onBoundary = () => {
                        if (!bridge.enabled
                            || bridge.seeking
                            || {{AdvertisementPlayingExpression}}) {
                            return;
                        }

                        const currentTime = Number(video.currentTime);
                        if (!Number.isFinite(currentTime) || currentTime + 0.02 < bridge.end) {
                            return;
                        }

                        bridge.seeking = true;
                        try {
                            video.currentTime = bridge.start;
                        } finally {
                            window.setTimeout(() => {
                                bridge.seeking = false;
                            }, 0);
                        }
                    };
                    bridge.dispose = () => {
                        video.removeEventListener("timeupdate", bridge.onBoundary);
                        video.removeEventListener("ended", bridge.onBoundary);
                        bridge.enabled = false;
                    };
                    video.addEventListener("timeupdate", bridge.onBoundary);
                    video.addEventListener("ended", bridge.onBoundary);
                    window[bridgeKey] = bridge;
                }

                bridge.start = requestedStart;
                bridge.end = requestedEnd;
                bridge.enabled = true;
                video.loop = false;
                video.removeAttribute("loop");
                return {
                    ok: true,
                    position: Number(video.currentTime) || 0,
                    duration,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: false,
                    abEnabled: true
                };
            })();
            """;
    }

    public static string BuildMuteScript(
        bool? targetMuted,
        bool markMiniPlayerUserOverride = false,
        bool respectMiniPlayerUserOverride = false)
    {
        var targetText = targetMuted.HasValue
            ? (targetMuted.Value ? "true" : "false")
            : "null";
        var markUserOverrideText = markMiniPlayerUserOverride ? "true" : "false";
        var respectUserOverrideText = respectMiniPlayerUserOverride ? "true" : "false";

        return $$"""
            (() => {
                const targetMuted = {{targetText}};
                const markMiniPlayerUserOverride = {{markUserOverrideText}};
                const respectMiniPlayerUserOverride = {{respectUserOverrideText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard;
                if (respectMiniPlayerUserOverride && savedStateRestoreGuard?.audio) {
                    return {
                        ok: true,
                        found: true,
                        skippedUserOverride: true,
                        paused: Boolean(video.paused),
                        muted: Boolean(video.muted),
                        volume: Number(video.volume) || 0,
                        speed: Number(video.playbackRate) || 1,
                        looping: Boolean(video.loop)
                    };
                }

                if (markMiniPlayerUserOverride) {
                    const guard = savedStateRestoreGuard
                        || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                    guard.audio = true;
                }

                const muted = targetMuted === null
                    ? !Boolean(video.muted)
                    : Boolean(targetMuted);
                video.muted = muted;
                if (player && typeof player.mute === "function" && typeof player.unMute === "function") {
                    if (muted) {
                        player.mute();
                    } else {
                        player.unMute();
                    }
                }

                return {
                    ok: true,
                    found: true,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildVolumeScript(
        double volume,
        bool markMiniPlayerUserOverride = false,
        bool respectMiniPlayerUserOverride = false,
        bool unmuteOnVolumeChange = true)
    {
        var volumeText = Math.Max(0, Math.Min(1, volume)).ToString(CultureInfo.InvariantCulture);
        var markUserOverrideText = markMiniPlayerUserOverride ? "true" : "false";
        var respectUserOverrideText = respectMiniPlayerUserOverride ? "true" : "false";
        var unmuteOnVolumeChangeText = unmuteOnVolumeChange ? "true" : "false";

        return $$"""
            (() => {
                const volume = {{volumeText}};
                const markMiniPlayerUserOverride = {{markUserOverrideText}};
                const respectMiniPlayerUserOverride = {{respectUserOverrideText}};
                const unmuteOnVolumeChange = {{unmuteOnVolumeChangeText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard;
                if (respectMiniPlayerUserOverride && savedStateRestoreGuard?.audio) {
                    return {
                        ok: true,
                        found: true,
                        skippedUserOverride: true,
                        paused: Boolean(video.paused),
                        muted: Boolean(video.muted),
                        volume: Number(video.volume) || 0,
                        speed: Number(video.playbackRate) || 1,
                        looping: Boolean(video.loop)
                    };
                }

                if (markMiniPlayerUserOverride) {
                    const guard = savedStateRestoreGuard
                        || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                    guard.audio = true;
                }

                video.volume = volume;
                if (player && typeof player.setVolume === "function") {
                    player.setVolume(Math.round(volume * 100));
                }

                if (volume > 0 && unmuteOnVolumeChange) {
                    video.muted = false;
                    if (player && typeof player.unMute === "function") {
                        player.unMute();
                    }
                }

                return {
                    ok: true,
                    found: true,
                    paused: Boolean(video.paused),
                    muted: Boolean(video.muted),
                    volume: Number(video.volume) || 0,
                    speed: Number(video.playbackRate) || 1,
                    looping: Boolean(video.loop)
                };
            })();
            """;
    }

    public static string BuildLoopScript(bool? targetLooping)
    {
        var targetText = targetLooping.HasValue
            ? (targetLooping.Value ? "true" : "false")
            : "null";

        return $$"""
            (() => {
                const targetLooping = {{targetText}};
                const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const video = document.querySelector("video");
                if (!video) {
                    return { ok: false, found: false };
                }

                const playerState = player && typeof player.getPlayerState === "function"
                    ? Number(player.getPlayerState())
                    : null;
                const isEnded = Boolean(video.ended) || playerState === 0;
                const looping = targetLooping === null
                    ? !Boolean(video.loop)
                    : Boolean(targetLooping);
                video.loop = looping;
                if (looping) {
                    video.setAttribute("loop", "");
                } else {
                    video.removeAttribute("loop");
                }

                if (player && typeof player.setLoop === "function") {
                    player.setLoop(looping);
                }

                let restarted = false;
                if (looping && isEnded) {
                    const savedStateRestoreGuard = window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard
                        || (window.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard = {});
                    savedStateRestoreGuard.playback = true;
                    const pausedResumeGuard = window.__tabBrowserYouTubePausedResumeGuard;
                    if (pausedResumeGuard) {
                        pausedResumeGuard.enabled = false;
                    }

                    try {
                        if (player && typeof player.seekTo === "function") {
                            player.seekTo(0, true);
                        } else {
                            video.currentTime = 0;
                        }

                        if (player && typeof player.playVideo === "function") {
                            player.playVideo();
                        } else {
                            Promise.resolve(video.play()).catch(() => { });
                        }

                        restarted = true;
                    } catch {
                    }
                }

                return {
                    ok: true,
                    found: true,
                    position: Number(video.currentTime) || 0,
                    paused: Boolean(video.paused),
                    looping: Boolean(video.loop),
                    restarted
                };
            })();
            """;
    }

    public static string BuildFullscreenScript()
    {
        return """
            (async () => {
                try {
                    const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                    const video = document.querySelector("video");
                    const fullscreenButton = document.querySelector(".ytp-fullscreen-button");
                    if (fullscreenButton) {
                        fullscreenButton.click();
                        return { ok: true, method: "button" };
                    }

                    const target = video || player || document.documentElement;
                    if (target && target.requestFullscreen) {
                        await target.requestFullscreen();
                        return { ok: true, method: "requestFullscreen" };
                    }

                    return { ok: false, reason: "fullscreen-unavailable" };
                } catch (error) {
                    return { ok: false, reason: String(error && error.message || error) };
                }
            })();
            """;
    }

    private static string ToJavaScriptNullableNumber(double? value)
    {
        return value is { } number && double.IsFinite(number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "null";
    }
}
