using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerWebViewService
{
    private const int ProcessRecoveryDelayMilliseconds = 500;

    public static WebView2 CreateWebView(
        YouTubeMiniPlayerState state,
        Action<YouTubeMiniPlayerState, WebView2> onCoreInitialized,
        Action<YouTubeMiniPlayerState, WebView2> onNavigationStarting,
        Action<YouTubeMiniPlayerState, WebView2> onSourceChanged,
        Action<YouTubeMiniPlayerState, WebView2> onHistoryChanged,
        Func<YouTubeMiniPlayerState, WebView2, CoreWebView2NavigationCompletedEventArgs, Task> onNavigationCompletedAsync,
        Action<YouTubeMiniPlayerState, WebView2, CoreWebView2ProcessFailedEventArgs> onProcessFailed)
    {
        var webView = new WebView2();
        var processRecoveryAttempted = false;
        webView.CoreWebView2InitializationCompleted += (_, _) =>
        {
            onCoreInitialized(state, webView);
            if (webView.CoreWebView2 is { } core)
            {
                core.SourceChanged += (_, _) => onSourceChanged(state, webView);
                core.HistoryChanged += (_, _) => onHistoryChanged(state, webView);
                onHistoryChanged(state, webView);
                core.ProcessFailed += (_, args) =>
                {
                    onProcessFailed(state, webView, args);
                    if (processRecoveryAttempted || !ShouldReloadAfterProcessFailure(args.ProcessFailedKind))
                    {
                        return;
                    }

                    processRecoveryAttempted = true;
                    _ = ReloadAfterProcessFailureAsync(webView, core);
                };
            }
        };
        webView.NavigationStarting += (_, _) => onNavigationStarting(state, webView);
        webView.NavigationCompleted += async (_, args) =>
        {
            if (args.IsSuccess)
            {
                processRecoveryAttempted = false;
            }

            await onNavigationCompletedAsync(state, webView, args);
        };
        return webView;
    }

    private static bool ShouldReloadAfterProcessFailure(CoreWebView2ProcessFailedKind kind)
    {
        return WebViewProcessFailurePolicy.GetAction(kind)
            == WebViewProcessFailureAction.ReloadAffectedRuntime;
    }

    private static async Task ReloadAfterProcessFailureAsync(WebView2 webView, CoreWebView2 core)
    {
        try
        {
            await Task.Delay(ProcessRecoveryDelayMilliseconds);
            await webView.Dispatcher.InvokeAsync(() =>
            {
                if (!webView.Dispatcher.HasShutdownStarted
                    && ReferenceEquals(webView.CoreWebView2, core))
                {
                    core.Reload();
                }
            });
        }
        catch
        {
        }
    }

    public static void NavigateWithYouTubeReferer(CoreWebView2 core, Uri uri)
    {
        var request = core.Environment.CreateWebResourceRequest(
            uri.AbsoluteUri,
            "GET",
            postData: null,
            "Referer: https://www.youtube.com/\r\n");
        core.NavigateWithWebResourceRequest(request);
    }

    public static bool TryCreateLiveChatUri(string sourceUrl, out Uri uri)
    {
        uri = null!;
        if (!YouTubeUrlService.TryGetVideoId(sourceUrl, out var videoId))
        {
            return false;
        }

        uri = new Uri(
            "https://www.youtube.com/live_chat?v="
            + Uri.EscapeDataString(videoId)
            + "&is_popout=1"
            + "&dark_theme=1");
        return true;
    }

    public static bool TryCreateLiveChatReplayUri(
        string sourceUrl,
        string? replayContinuation,
        out Uri uri)
    {
        uri = null!;
        if (!YouTubeUrlService.TryGetVideoId(sourceUrl, out var videoId)
            || string.IsNullOrWhiteSpace(replayContinuation)
            || replayContinuation.Length > 8192)
        {
            return false;
        }

        uri = new Uri(
            "https://www.youtube.com/live_chat_replay?continuation="
            + Uri.EscapeDataString(replayContinuation)
            + "&v="
            + Uri.EscapeDataString(videoId)
            + "&is_popout=1"
            + "&dark_theme=1");
        return true;
    }

    public static string BuildLiveChatDarkThemeScript()
    {
        return """
            (() => {
                const styleId = "tabbrowser-live-chat-dark-theme";
                const applyTheme = () => {
                    const root = document.documentElement;
                    if (!root) {
                        return;
                    }

                    root.style.colorScheme = "dark";
                    if (document.getElementById(styleId)) {
                        return;
                    }

                    const style = document.createElement("style");
                    style.id = styleId;
                    style.textContent = `
                        :root {
                            --yt-live-chat-background-color: #0f0f0f !important;
                            --yt-live-chat-primary-text-color: #f1f1f1 !important;
                            --yt-live-chat-secondary-text-color: #aaaaaa !important;
                            --yt-live-chat-tertiary-text-color: #717171 !important;
                            --yt-live-chat-header-background-color: #111318 !important;
                            --yt-live-chat-action-panel-background-color: #111318 !important;
                            --yt-live-chat-secondary-background-color: #1c212b !important;
                            --yt-live-chat-tertiary-background-color: #242b36 !important;
                            --yt-live-chat-message-highlight-background-color: #1f334b !important;
                        }
                        html,
                        body,
                        yt-live-chat-app,
                        yt-live-chat-renderer,
                        yt-live-chat-item-list-renderer,
                        #contents,
                        #items {
                            background-color: #0f0f0f !important;
                            color: #f1f1f1 !important;
                        }
                    `;
                    (document.head || root).appendChild(style);
                };

                applyTheme();
                if (document.readyState === "loading") {
                    document.addEventListener("DOMContentLoaded", applyTheme, { once: true });
                }
            })();
            """;
    }

    public static string BuildLiveChatAutoFollowScript()
    {
        return """
            (() => {
                const stateKey = "__tabBrowserLiveChatAutoFollow";
                const stateVersion = 4;
                const existingState = window[stateKey];
                if (existingState?.version === stateVersion) {
                    existingState.refresh();
                    return;
                }

                existingState?.dispose?.();
                const state = {
                    version: stateVersion,
                    started: false,
                    initialFollowActive: true,
                    initialFollowTimer: 0,
                    scheduledFrame: 0,
                    scroller: null,
                    items: null,
                    discoveryObserver: null,
                    listObserver: null,
                    listResizeObserver: null,
                    replacementObserver: null,
                    refresh: null,
                    dispose: null
                };
                window[stateKey] = state;

                const findTargets = () => {
                    const scroller = document.querySelector(
                        "yt-live-chat-item-list-renderer #item-scroller")
                        || document.querySelector("#item-scroller");
                    if (!(scroller instanceof HTMLElement)) {
                        return null;
                    }

                    const items = scroller.querySelector(":scope > #items")
                        || scroller.querySelector("#items")
                        || document.querySelector("yt-live-chat-item-list-renderer #items");
                    return items instanceof HTMLElement
                        ? { scroller, items }
                        : null;
                };

                const handleUserIntent = (event) => {
                    if (event.type === "keydown"
                        && !["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End"]
                            .includes(event.key)) {
                        return;
                    }

                    stopInitialFollow();
                };

                const detachUserIntentListeners = () => {
                    state.scroller?.removeEventListener("wheel", handleUserIntent, true);
                    state.scroller?.removeEventListener("touchstart", handleUserIntent, true);
                    state.scroller?.removeEventListener("pointerdown", handleUserIntent, true);
                    document.removeEventListener("keydown", handleUserIntent, true);
                };

                const disconnectBoundTargets = () => {
                    detachUserIntentListeners();
                    state.listObserver?.disconnect();
                    state.listResizeObserver?.disconnect();
                    state.replacementObserver?.disconnect();
                    state.listObserver = null;
                    state.listResizeObserver = null;
                    state.replacementObserver = null;
                    state.scroller = null;
                    state.items = null;
                };

                const stopInitialFollow = () => {
                    state.initialFollowActive = false;
                    if (state.initialFollowTimer) {
                        window.clearTimeout(state.initialFollowTimer);
                        state.initialFollowTimer = 0;
                    }

                    state.discoveryObserver?.disconnect();
                    state.discoveryObserver = null;
                    disconnectBoundTargets();
                    if (state.scheduledFrame) {
                        window.cancelAnimationFrame(state.scheduledFrame);
                        state.scheduledFrame = 0;
                    }
                };

                const follow = () => {
                    if (!state.initialFollowActive || state.scheduledFrame) {
                        return;
                    }

                    state.scheduledFrame = window.requestAnimationFrame(() => {
                        state.scheduledFrame = 0;
                        if (!state.scroller?.isConnected || !state.items?.isConnected) {
                            state.refresh();
                            return;
                        }

                        state.scroller.scrollTop = state.scroller.scrollHeight;
                    });
                };

                const bindCommentList = () => {
                    if (!state.initialFollowActive) {
                        return false;
                    }

                    const targets = findTargets();
                    if (!targets) {
                        disconnectBoundTargets();
                        return false;
                    }

                    if (state.scroller === targets.scroller
                        && state.items === targets.items
                        && state.scroller.isConnected
                        && state.items.isConnected) {
                        return true;
                    }

                    disconnectBoundTargets();
                    state.scroller = targets.scroller;
                    state.items = targets.items;
                    state.scroller.addEventListener(
                        "wheel",
                        handleUserIntent,
                        { capture: true, passive: true });
                    state.scroller.addEventListener(
                        "touchstart",
                        handleUserIntent,
                        { capture: true, passive: true });
                    state.scroller.addEventListener(
                        "pointerdown",
                        handleUserIntent,
                        { capture: true, passive: true });
                    document.addEventListener("keydown", handleUserIntent, true);
                    state.listObserver = new MutationObserver((mutations) => {
                        if (mutations.some((mutation) =>
                            mutation.addedNodes.length > 0)) {
                            follow();
                        }
                    });
                    state.listObserver.observe(
                        state.items,
                        { childList: true, subtree: true });
                    state.listResizeObserver = new ResizeObserver(follow);
                    state.listResizeObserver.observe(state.items);

                    state.replacementObserver = new MutationObserver(() => {
                        const currentTargets = findTargets();
                        if (!currentTargets
                            || currentTargets.scroller !== state.scroller
                            || currentTargets.items !== state.items) {
                            state.refresh();
                        }
                    });
                    state.replacementObserver.observe(state.scroller, { childList: true });
                    if (state.scroller.parentElement) {
                        state.replacementObserver.observe(
                            state.scroller.parentElement,
                            { childList: true });
                    }

                    state.discoveryObserver?.disconnect();
                    state.discoveryObserver = null;
                    if (!state.initialFollowTimer) {
                        state.initialFollowTimer = window.setTimeout(stopInitialFollow, 2000);
                    }

                    follow();
                    return true;
                };

                const startDiscovery = () => {
                    if (state.discoveryObserver) {
                        return;
                    }

                    const root = document.body || document.documentElement;
                    if (!root) {
                        return;
                    }

                    state.discoveryObserver = new MutationObserver(() => {
                        if (bindCommentList()) {
                            state.discoveryObserver?.disconnect();
                            state.discoveryObserver = null;
                        }
                    });
                    state.discoveryObserver.observe(root, { childList: true, subtree: true });
                };

                const refresh = () => {
                    if (!state.initialFollowActive) {
                        return;
                    }

                    if (bindCommentList()) {
                        follow();
                    } else {
                        startDiscovery();
                    }
                };
                state.refresh = refresh;

                const start = () => {
                    if (state.started) {
                        refresh();
                        return;
                    }

                    state.started = true;
                    refresh();
                };

                const handleVisible = () => {
                    if (!document.hidden) {
                        refresh();
                    }
                };
                document.addEventListener("yt-navigate-finish", refresh, true);
                document.addEventListener("visibilitychange", handleVisible, true);
                window.addEventListener("pageshow", refresh);
                state.dispose = () => {
                    stopInitialFollow();
                    document.removeEventListener("DOMContentLoaded", start);
                    document.removeEventListener("yt-navigate-finish", refresh, true);
                    document.removeEventListener("visibilitychange", handleVisible, true);
                    window.removeEventListener("pageshow", refresh);
                };

                if (document.readyState === "loading") {
                    document.addEventListener("DOMContentLoaded", start, { once: true });
                } else {
                    start();
                }
            })();
            """;
    }

    public static string BuildLiveContinuationBridgeScript(string token, bool enabled = true)
    {
        var serializedToken = JsonSerializer.Serialize(token);
        var enabledJson = JsonSerializer.Serialize(enabled);
        return $$"""
            (() => {
                if (location.protocol !== "https:"
                    || !["www.youtube.com", "youtube.com", "m.youtube.com"].includes(location.hostname)
                    || window.top !== window) {
                    return { ok: false };
                }
                const key = "__tabBrowserMiniPlayerLiveContinuation";
                const enabled = {{enabledJson}};
                if (window[key]) {
                    window[key].setEnabled(enabled);
                    return { ok: true };
                }
                const token = {{serializedToken}};
                const state = { enabled, source: null, generation: 0, timer: null, request: null, player: null, disposed: false };
                const getPlayer = () => document.getElementById("movie_player") || document.querySelector(".html5-video-player");
                const getVideo = () => getPlayer()?.querySelector("video");
                const videoId = () => new URL(location.href).searchParams.get("v") || "";
                const isAd = () => getPlayer()?.classList.contains("ad-showing") || getPlayer()?.classList.contains("ad-interrupting");
                const isLive = response => {
                    const details = response?.videoDetails;
                    const broadcast = response?.microformat?.playerMicroformatRenderer?.liveBroadcastDetails;
                    return details?.isUpcoming !== true && !broadcast?.endTimestamp
                        && (broadcast?.isLiveNow === true || (broadcast?.isLiveNow !== false && details?.isLive === true))
                        && (!broadcast?.startTimestamp || Date.parse(broadcast.startTimestamp) <= Date.now());
                };
                const cancel = () => {
                    state.generation++;
                    clearTimeout(state.timer);
                    state.timer = null;
                    state.request?.abort();
                    state.request = null;
                };
                const isEnded = () => Boolean(getVideo()?.ended) || getPlayer()?.getPlayerState?.() === 0;
                const isCurrent = (source, generation) => state.enabled && !state.disposed
                    && state.generation === generation && state.source === source
                    && videoId() === source.videoId && isEnded() && !isAd() && !getVideo()?.loop;
                // HTML内の公開プレイヤー情報だけをJSONとして読む。取得したscriptは実行しない。
                const readResponse = html => {
                    const match = /\bytInitialPlayerResponse\s*=\s*\{/.exec(html);
                    if (!match) return null;
                    const start = match.index + match[0].length - 1;
                    let depth = 0, quoted = false, escaped = false;
                    for (let i = start; i < html.length; i++) {
                        const ch = html[i];
                        if (quoted) {
                            if (escaped) escaped = false;
                            else if (ch === "\\") escaped = true;
                            else if (ch === '"') quoted = false;
                        } else if (ch === '"') quoted = true;
                        else if (ch === "{") depth++;
                        else if (ch === "}" && --depth === 0) return JSON.parse(html.slice(start, i + 1));
                    }
                    return null;
                };
                const check = async (source, generation, attempt) => {
                    state.timer = null;
                    if (!isCurrent(source, generation)) return;
                    const request = new AbortController();
                    state.request = request;
                    const timeout = setTimeout(() => request.abort(), 5000);
                    try {
                        const result = await fetch("https://www.youtube.com/channel/" + source.channelId + "/live", {
                            credentials: "omit", cache: "no-store", signal: request.signal
                        });
                        const responseUrl = new URL(result.url);
                        if (!result.ok || responseUrl.protocol !== "https:" || responseUrl.hostname !== "www.youtube.com") return;
                        const html = await result.text();
                        const response = html.length <= 4 * 1024 * 1024 ? readResponse(html) : null;
                        const details = response?.videoDetails;
                        const nextVideoId = String(details?.videoId || "");
                        if (isCurrent(source, generation) && isLive(response)
                            && response?.playabilityStatus?.status === "OK"
                            && details?.channelId === source.channelId && nextVideoId !== source.videoId
                            && /^[A-Za-z0-9_-]{11}$/.test(nextVideoId)) {
                            const post = window.chrome?.webview?.postMessage;
                            if (typeof post === "function") {
                                post.call(window.chrome.webview, {
                                    type: "youtubeMiniPlayerLiveContinuation", token,
                                    sourceVideoId: source.videoId, nextVideoId
                                });
                                source.completed = true;
                            }
                        }
                    } catch {
                        // 公開情報が確認できない場合は現在の動画に留まる。
                    } finally {
                        clearTimeout(timeout);
                        if (state.request === request) state.request = null;
                        if (!source.completed && attempt === 0 && isCurrent(source, generation)) {
                            state.timer = setTimeout(() => check(source, generation, 1), 10000);
                        }
                    }
                };
                const begin = () => {
                    const source = state.source;
                    if (!source || source.started || !isCurrent(source, state.generation)) return;
                    source.started = true;
                    const generation = state.generation;
                    // YouTube自身の遷移を先に待ち、終了したライブだけ最大2回確認する。
                    state.timer = setTimeout(() => check(source, generation, 0), 2000);
                };
                const onPlayerState = event => {
                    refresh();
                    if (Number(event?.data ?? event) === 0) begin();
                };
                const refresh = () => {
                    if (state.disposed || !state.enabled) return;
                    const player = getPlayer();
                    if (player !== state.player) {
                        state.player?.removeEventListener?.("onStateChange", onPlayerState);
                        state.player = player;
                        player?.addEventListener?.("onStateChange", onPlayerState);
                    }
                    const id = videoId();
                    if (state.source && state.source.videoId !== id) {
                        cancel();
                        state.source = null;
                    }
                    const response = player?.getPlayerResponse?.() || window.ytInitialPlayerResponse;
                    const details = response?.videoDetails;
                    if (!state.source && details?.videoId === id && isLive(response)
                        && /^UC[A-Za-z0-9_-]{22}$/.test(String(details?.channelId || "")) && !isAd()) {
                        state.source = { videoId: id, channelId: details.channelId, started: false, completed: false };
                    }
                    if (isEnded()) begin();
                };
                const onEnded = event => { if (event.target === getVideo()) { refresh(); begin(); } };
                const onReplay = event => {
                    if (event.target === getVideo() && !isEnded()) {
                        cancel();
                        state.source = null;
                    }
                    refresh();
                };
                const onNavigate = () => { cancel(); state.source = null; };
                const readyEvents = ["loadedmetadata", "canplay", "playing", "yt-player-updated", "yt-navigate-finish"];
                for (const name of readyEvents) document.addEventListener(name, refresh, true);
                document.addEventListener("ended", onEnded, true);
                document.addEventListener("play", onReplay, true);
                document.addEventListener("seeking", onReplay, true);
                document.addEventListener("yt-navigate-start", onNavigate, true);
                const dispose = () => {
                    state.disposed = true;
                    cancel();
                    state.player?.removeEventListener?.("onStateChange", onPlayerState);
                    for (const name of readyEvents) document.removeEventListener(name, refresh, true);
                    document.removeEventListener("ended", onEnded, true);
                    document.removeEventListener("play", onReplay, true);
                    document.removeEventListener("seeking", onReplay, true);
                    document.removeEventListener("yt-navigate-start", onNavigate, true);
                    window.removeEventListener("pagehide", dispose);
                    delete window[key];
                };
                const setEnabled = value => {
                    if (state.enabled !== value) {
                        state.enabled = value;
                        cancel();
                        state.source = null;
                    }
                    if (state.enabled) refresh();
                };
                window[key] = { refresh, dispose, setEnabled };
                window.addEventListener("pagehide", dispose, { once: true });
                refresh();
                return { ok: true };
            })();
            """;
    }

    public static string BuildLiveStatusScript(string expectedVideoId)
    {
        var serializedVideoId = JsonSerializer.Serialize(expectedVideoId);
        return $$"""
            (() => {
                const expectedVideoId = {{serializedVideoId}};
                const player = document.getElementById("movie_player")
                    || document.querySelector(".html5-video-player");
                const response = (player && typeof player.getPlayerResponse === "function"
                    ? player.getPlayerResponse()
                    : null) || window.ytInitialPlayerResponse || null;
                const details = response?.videoDetails || null;
                const actualVideoId = String(details?.videoId || "");
                if (!details || actualVideoId !== expectedVideoId) {
                    return { ok: false, isLive: false };
                }

                const playerData = player && typeof player.getVideoData === "function"
                    ? player.getVideoData()
                    : null;
                const broadcast = response?.microformat
                    ?.playerMicroformatRenderer
                    ?.liveBroadcastDetails || null;
                const startTime = broadcast?.startTimestamp
                    ? Date.parse(broadcast.startTimestamp)
                    : Number.NaN;
                const hasStarted = Number.isFinite(startTime)
                    && startTime <= Date.now() + 60000;
                const hasEnded = Boolean(broadcast?.endTimestamp);
                const explicitlyLive = playerData?.isLive === true || details.isLive === true;
                const isLive = !hasEnded && (
                    (explicitlyLive && (!broadcast || hasStarted))
                    || (details.isLiveContent === true && hasStarted));

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
                const initialData = window.ytInitialData || null;
                const primaryInfo = (initialData?.contents
                    ?.twoColumnWatchNextResults?.results?.results?.contents || [])
                    .find(item => item?.videoPrimaryInfoRenderer)
                    ?.videoPrimaryInfoRenderer || null;
                const viewCountRenderer = document.querySelector("ytd-video-view-count-renderer")?.data
                    || primaryInfo?.viewCount?.videoViewCountRenderer
                    || null;
                const liveViewerTextSource = readText(viewCountRenderer?.viewCount)
                    || document.querySelector("ytd-video-view-count-renderer #view-count")?.textContent
                    || "";
                const viewerCountText = isLive
                    ? String(liveViewerTextSource).replace(/\s+/g, " ").trim().slice(0, 64) || null
                    : null;

                let replayContinuation = null;
                if (!isLive && details.isLiveContent === true) {
                    const chatFrameData = document.querySelector("ytd-live-chat-frame")?.data || null;
                    const conversationBar = initialData?.contents
                        ?.twoColumnWatchNextResults
                        ?.conversationBar || null;
                    const liveChatRenderer = chatFrameData?.liveChatRenderer
                        || (chatFrameData?.isReplay === true ? chatFrameData : null)
                        || conversationBar?.liveChatRenderer
                        || null;
                    if (liveChatRenderer?.isReplay === true) {
                        const continuations = Array.isArray(liveChatRenderer.continuations)
                            ? liveChatRenderer.continuations
                            : [];
                        for (const entry of continuations) {
                            const continuation = entry?.reloadContinuationData?.continuation
                                || entry?.timedContinuationData?.continuation
                                || entry?.invalidationContinuationData?.continuation
                                || "";
                            if (typeof continuation === "string" && continuation.length > 0) {
                                replayContinuation = continuation;
                                break;
                            }
                        }
                    }
                }

                return { ok: true, isLive, replayContinuation, viewerCountText };
            })();
            """;
    }

    public static bool TryReadLiveChatStatusResult(
        string? resultJson,
        out bool isLive,
        out string? replayContinuation)
    {
        isLive = false;
        replayContinuation = null;
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("isLive", out var live)
                || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            isLive = live.GetBoolean();
            if (root.TryGetProperty("replayContinuation", out var replay))
            {
                if (replay.ValueKind == JsonValueKind.String)
                {
                    replayContinuation = replay.GetString();
                    if (string.IsNullOrWhiteSpace(replayContinuation)
                        || replayContinuation.Length > 8192)
                    {
                        replayContinuation = null;
                    }
                }
                else if (replay.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadLiveViewerCountResult(
        string? resultJson,
        out string? viewerCountText)
    {
        viewerCountText = null;
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("isLive", out var live)
                || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            if (!live.GetBoolean())
            {
                return true;
            }

            if (!root.TryGetProperty("viewerCountText", out var viewerCount)
                || viewerCount.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (viewerCount.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            viewerCountText = YouTubeMiniPlayerStateService.NormalizeLiveViewerCountText(
                viewerCount.GetString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string BuildLiveChatReplaySyncScript(double positionSeconds, bool isPaused)
    {
        var normalizedPosition = double.IsFinite(positionSeconds)
            ? Math.Max(0, positionSeconds)
            : 0;
        var serializedPosition = JsonSerializer.Serialize(normalizedPosition);
        var playerState = isPaused ? 2 : 1;
        return $$"""
            (() => {
                const targetOrigin = window.location.origin;
                window.postMessage(
                    { "yt-player-video-progress": {{serializedPosition}} },
                    targetOrigin);
                window.postMessage(
                    { "yt-player-state-change": {{playerState}} },
                    targetOrigin);
                return true;
            })();
            """;
    }
}
