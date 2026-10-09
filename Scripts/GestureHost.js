(function () {
    // 文書生成時の送信先だけを保持し、ページによる後からの差替えへtokenを渡さない。
    const __tabBrowserNativePostMessage = getNativePostMessage();
    const objectAssign = Object.assign.bind(Object);
    const MapConstructor = Map;
    const defaultSettings = {
        gestureStartThreshold: 14,
        gestureDirectionThreshold: 22,
        preserveContextMenuOnShortRightClick: true
    };
    const directionDominanceRatio = 1.25;
    const reverseDirectionThresholdMultiplier = 1.5;
    const gestureGuideDelayMilliseconds = 450;
    const gestureGuideMovementTolerance = 4;
    const gestureVisualPointMinimumDistance = 2;

    const defaultCommands = [
        ["←", { command: "Back", label: "戻る" }],
        ["→", { command: "Forward", label: "進む" }],
        ["↓", { command: "CloseTab", label: "タブを閉じる" }],
        ["↑", { command: "Reload", label: "更新" }],
        ["←→", { command: "NextTab", label: "次のタブ" }],
        ["→←", { command: "PreviousTab", label: "前のタブ" }],
        ["←↑", { command: "RestoreClosedTab", label: "閉じたタブを復元" }],
        ["→↑", { command: "ScrollTop", label: "ページ先頭へ" }],
        ["→↓", { command: "ScrollBottom", label: "ページ末尾へ" }],
        ["↑→", { command: "SearchSelection", label: "選択テキストを検索" }],
        ["wheelDown", { command: "NextTab", label: "次のタブ" }],
        ["wheelUp", { command: "PreviousTab", label: "前のタブ" }]
    ];

    function normalizeSettings(value) {
        return objectAssign({}, defaultSettings, value || {});
    }

    function getMappingValue(item, camelName, pascalName) {
        if (!item) {
            return "";
        }

        return item[camelName] || item[pascalName] || "";
    }

    function buildCommandMap(items) {
        const source = Array.isArray(items) ? items : defaultCommands;
        const map = new MapConstructor();
        for (const item of source) {
            if (Array.isArray(item)) {
                map.set(item[0], item[1]);
                continue;
            }

            const route = String(getMappingValue(item, "route", "Route")).trim();
            const command = String(getMappingValue(item, "command", "Command")).trim();
            const label = String(getMappingValue(item, "label", "Label") || command).trim();
            if (route && command) {
                map.set(route, { command, label });
            }
        }

        return map;
    }

    if (window.__tabBrowserGestureHostInstalled) {
        if (typeof window.__tabBrowserGestureHostUpdate === "function") {
            window.__tabBrowserGestureHostUpdate(
                __tabBrowserGestureSettings,
                __tabBrowserGestureCommands,
                __tabBrowserWebMessageToken);
        }

        return;
    }

    const incomingSettings = normalizeSettings(__tabBrowserGestureSettings);
    const incomingCommands = buildCommandMap(__tabBrowserGestureCommands);

    Object.defineProperty(window, "__tabBrowserGestureHostInstalled", {
        value: true,
        writable: false,
        configurable: false
    });

    let settings = incomingSettings;
    let commands = incomingCommands;

    Object.defineProperty(window, "__tabBrowserGestureHostUpdate", {
        value: function (nextSettings, nextCommands, token) {
            if (token !== __tabBrowserWebMessageToken) {
                return;
            }

            settings = normalizeSettings(nextSettings);
            commands = nextCommands instanceof MapConstructor ? nextCommands : buildCommandMap(nextCommands);
        },
        writable: false,
        configurable: false
    });

    let tracking = false;
    let active = false;
    let consumedContextMenu = false;
    let route = "";
    let startPoint = null;
    let lastDirectionPoint = null;
    let latestGesturePoint = null;
    let points = [];
    let overlay = null;
    let canvas = null;
    let label = null;
    let statusHud = null;
    let statusRoute = null;
    let statusBadge = null;
    let statusCommand = null;
    let statusNext = null;
    let wheelListenersInstalled = false;
    let keyListenersInstalled = false;
    let lastVisualStateRoute = null;
    let lastRenderedPresentationRoute = null;
    let lastDrawnPointIndex = 0;
    let visualFrameRequest = null;
    let pointerLabelWidth = 280;
    let pointerLabelHeight = 44;
    let cancellationVisualTimer = null;
    let gestureGuideTimer = null;
    let gestureGuideVisible = false;
    let useNativeVisualOverlay = false;
    let cancelled = false;
    let gestureStartLinkUrl = "";
    let gestureStartLinkText = "";
    let lastHandledMouseMoveEvent = null;

    function getNativePostMessage() {
        const webViewBridge = window.chrome && window.chrome.webview;
        return webViewBridge && typeof webViewBridge.postMessage === "function"
            ? webViewBridge.postMessage.bind(webViewBridge)
            : null;
    }

    function postMessage(message) {
        if (__tabBrowserNativePostMessage) {
            __tabBrowserNativePostMessage({
                ...message,
                tabBrowserToken: __tabBrowserWebMessageToken
            });
        }
    }

    Object.defineProperty(window, "__tabBrowserPostYouTubePlaybackState", {
        value: function (message) {
            if (!message || typeof message !== "object") {
                return;
            }

            postMessage({ ...message, type: "youtubePlaybackState" });
        },
        writable: false,
        configurable: false
    });

    function getAnchor(event) {
        const target = event.target;
        if (!target || !target.closest) {
            return null;
        }

        return target.closest("a[href]");
    }

    function getAnchorAtPoint(x, y) {
        const target = document.elementFromPoint(x, y);
        if (!target || !target.closest) {
            return null;
        }

        return target.closest("a[href]");
    }

    function normalizeHref(anchor) {
        try {
            return new URL(anchor.getAttribute("href"), document.baseURI).href;
        } catch {
            return anchor.href || "";
        }
    }

    let hoveredLinkAnchor = null;
    let hoveredLinkUrl = "";
    let hoveredLinkPoint = { x: 0, y: 0 };
    let lastLinkHoverMessageAt = 0;
    let lastLinkHoverMouseMoveHandledAt = 0;
    let linkHoverMouseMoveInstalled = false;
    let youtubeLinkHoverFrameRequest = 0;
    let youtubeLinkHoverStopTimer = 0;
    let youtubeLinkHoverHasPendingPoint = false;
    let youtubeLinkHoverPendingX = 0;
    let youtubeLinkHoverPendingY = 0;
    let youtubeLinkHoverLastMovementPoint = null;
    const linkHoverMouseMoveMinimumIntervalMilliseconds = 50;
    const youtubeLinkHoverStopDelayMilliseconds = 200;
    const youtubeLinkHoverMovementTolerancePixels = 3;

    function isPreviewableHref(url) {
        return /^https?:\/\//i.test(url || "");
    }

    function isYouTubePage() {
        const host = String(location.hostname || "").toLowerCase();
        return host === "youtube.com" || host.endsWith(".youtube.com");
    }

    function trimLinkPreviewText(value) {
        return String(value || "").replace(/\s+/g, " ").trim().slice(0, 160);
    }

    function getLinkPreviewTitle(anchor) {
        return trimLinkPreviewText(
            anchor.getAttribute("title")
            || anchor.getAttribute("aria-label")
            || anchor.textContent
            || "");
    }

    function isFiveChReplyAnchor(anchor) {
        if (!/(^|\.)5ch\.(?:net|io)$/i.test(location.hostname || "")) {
            return false;
        }

        const text = trimLinkPreviewText(anchor.textContent);
        return anchor.classList.contains("reply_link")
            || /^(?:>>|＞＞)\s*\d+/.test(text);
    }

    function isEditableContextTarget(event) {
        const path = typeof event.composedPath === "function"
            ? event.composedPath()
            : [event.target];
        for (const candidate of path) {
            if (!(candidate instanceof HTMLElement)) {
                continue;
            }

            const editable = candidate.closest("input, textarea, [contenteditable]");
            if (!editable || editable.matches(":disabled, [readonly]")) {
                continue;
            }

            if (editable instanceof HTMLInputElement) {
                return ![
                    "button",
                    "checkbox",
                    "color",
                    "file",
                    "hidden",
                    "image",
                    "radio",
                    "range",
                    "reset",
                    "submit"
                ].includes((editable.type || "text").toLowerCase());
            }

            return editable instanceof HTMLTextAreaElement || editable.isContentEditable;
        }

        return false;
    }

    function showPageContextMenu(event) {
        const anchor = getAnchor(event);
        const linkUrl = anchor ? normalizeHref(anchor) : "";
        postMessage({
            type: "pageContextMenu",
            pageUrl: location.href || document.URL || "",
            linkUrl,
            linkText: anchor ? getLinkPreviewTitle(anchor) : "",
            selectedText: getSelectedText(),
            isEditable: isEditableContextTarget(event),
            x: event.clientX,
            y: event.clientY
        });
    }

    function cancelYouTubeLinkHoverStopDetection() {
        if (youtubeLinkHoverFrameRequest) {
            window.cancelAnimationFrame(youtubeLinkHoverFrameRequest);
            youtubeLinkHoverFrameRequest = 0;
        }

        if (youtubeLinkHoverStopTimer) {
            window.clearTimeout(youtubeLinkHoverStopTimer);
            youtubeLinkHoverStopTimer = 0;
        }

        youtubeLinkHoverHasPendingPoint = false;
        youtubeLinkHoverLastMovementPoint = null;
    }

    function clearLinkHoverPreview() {
        cancelYouTubeLinkHoverStopDetection();
        if (!hoveredLinkAnchor) {
            return;
        }

        hoveredLinkAnchor = null;
        hoveredLinkUrl = "";
        postMessage({ type: "linkHoverEnd" });
    }

    function showLinkHover(anchor, x, y) {
        if (document.fullscreenElement) {
            clearLinkHoverPreview();
            return;
        }

        if (tracking || active) {
            clearLinkHoverPreview();
            return;
        }

        if (!anchor) {
            clearLinkHoverPreview();
            return;
        }

        const url = normalizeHref(anchor);
        if (isFiveChReplyAnchor(anchor) || !isPreviewableHref(url)) {
            clearLinkHoverPreview();
            return;
        }

        const now = Date.now();
        const movedEnough = Math.abs(x - hoveredLinkPoint.x) + Math.abs(y - hoveredLinkPoint.y) >= 10;
        if (hoveredLinkAnchor === anchor
            && hoveredLinkUrl === url
            && !movedEnough
            && now - lastLinkHoverMessageAt < 250) {
            return;
        }

        hoveredLinkAnchor = anchor;
        hoveredLinkUrl = url;
        hoveredLinkPoint = { x, y };
        lastLinkHoverMessageAt = now;
        postMessage({
            type: "linkHover",
            url,
            title: getLinkPreviewTitle(anchor),
            x,
            y
        });
    }

    function handleLinkHover(event) {
        if (!event.isTrusted || isYouTubePage()) {
            return;
        }

        showLinkHover(getAnchor(event), event.clientX, event.clientY);
    }

    function handleLinkHoverMouseMove(event) {
        const now = performance.now();
        if (now - lastLinkHoverMouseMoveHandledAt < linkHoverMouseMoveMinimumIntervalMilliseconds) {
            return;
        }

        lastLinkHoverMouseMoveHandledAt = now;
        handleLinkHover(event);
    }

    function showYouTubeLinkHoverAfterStop() {
        youtubeLinkHoverStopTimer = 0;
        const point = youtubeLinkHoverLastMovementPoint;
        if (!point || !isYouTubePage()) {
            return;
        }

        showLinkHover(getAnchorAtPoint(point.x, point.y), point.x, point.y);
    }

    function scheduleYouTubeLinkHoverAfterAnimationFrame() {
        youtubeLinkHoverFrameRequest = 0;
        if (!youtubeLinkHoverHasPendingPoint) {
            return;
        }

        const point = {
            x: youtubeLinkHoverPendingX,
            y: youtubeLinkHoverPendingY
        };
        youtubeLinkHoverHasPendingPoint = false;

        if (document.fullscreenElement || tracking || active) {
            clearLinkHoverPreview();
            return;
        }

        const previousPoint = youtubeLinkHoverLastMovementPoint;
        const movedEnough = !previousPoint
            || Math.abs(point.x - previousPoint.x) + Math.abs(point.y - previousPoint.y) >= youtubeLinkHoverMovementTolerancePixels;
        if (!movedEnough) {
            return;
        }

        clearLinkHoverPreview();
        youtubeLinkHoverLastMovementPoint = point;
        youtubeLinkHoverStopTimer = window.setTimeout(
            showYouTubeLinkHoverAfterStop,
            youtubeLinkHoverStopDelayMilliseconds);
    }

    function handleYouTubeLinkHoverMouseMove(event) {
        if (!isYouTubePage()) {
            return;
        }

        youtubeLinkHoverPendingX = event.clientX;
        youtubeLinkHoverPendingY = event.clientY;
        youtubeLinkHoverHasPendingPoint = true;
        if (!youtubeLinkHoverFrameRequest) {
            youtubeLinkHoverFrameRequest = window.requestAnimationFrame(
                scheduleYouTubeLinkHoverAfterAnimationFrame);
        }
    }

    function syncLinkHoverMouseMoveListener() {
        if (document.fullscreenElement) {
            clearLinkHoverPreview();
        }

        const shouldInstall = !document.fullscreenElement && !isYouTubePage();
        if (shouldInstall === linkHoverMouseMoveInstalled) {
            return;
        }

        if (shouldInstall) {
            document.addEventListener("mousemove", handleLinkHoverMouseMove, true);
        } else {
            document.removeEventListener("mousemove", handleLinkHoverMouseMove, true);
            clearLinkHoverPreview();
        }

        linkHoverMouseMoveInstalled = shouldInstall;
    }

    function openBackgroundTab(event) {
        if (!event.isTrusted) {
            return false;
        }

        const anchor = getAnchor(event);
        if (!anchor) {
            return false;
        }

        const url = normalizeHref(anchor);
        if (!url) {
            return false;
        }

        event.preventDefault();
        event.stopPropagation();
        postMessage({ type: "openBackgroundTab", url });
        return true;
    }

    function ensureOverlay() {
        if (overlay || useNativeVisualOverlay) {
            return;
        }

        overlay = document.createElement("div");
        overlay.style.position = "fixed";
        overlay.style.inset = "0";
        overlay.style.zIndex = "2147483647";
        overlay.style.pointerEvents = "none";
        overlay.style.contain = "strict";

        canvas = document.createElement("canvas");
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;
        canvas.style.width = "100vw";
        canvas.style.height = "100vh";

        label = document.createElement("div");
        label.style.position = "fixed";
        label.style.minWidth = "90px";
        label.style.maxWidth = "380px";
        label.style.padding = "7px 10px";
        label.style.border = "1px solid rgba(255, 255, 255, 0.22)";
        label.style.borderRadius = "6px";
        label.style.background = "rgba(17, 20, 26, 0.92)";
        label.style.color = "#f3f4f6";
        label.style.font = "13px/1.4 system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";
        label.style.boxShadow = "0 8px 20px rgba(0, 0, 0, 0.28)";
        label.style.whiteSpace = "pre-line";

        statusHud = document.createElement("div");
        statusHud.style.position = "fixed";
        statusHud.style.top = "16px";
        statusHud.style.left = "50%";
        statusHud.style.transform = "translateX(-50%)";
        statusHud.style.display = "none";
        statusHud.style.pointerEvents = "none";
        statusHud.style.boxSizing = "border-box";
        statusHud.style.width = "min(520px, calc(100vw - 32px))";
        statusHud.style.padding = "9px 12px 10px 15px";
        statusHud.style.border = "1px solid #526173";
        statusHud.style.borderLeft = "4px solid #66e3ff";
        statusHud.style.borderRadius = "6px";
        statusHud.style.background = "rgba(17, 20, 26, 0.95)";
        statusHud.style.color = "#f8fafc";
        statusHud.style.fontFamily = "system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif";
        statusHud.style.boxShadow = "0 10px 24px rgba(0, 0, 0, 0.34)";

        const statusHeader = document.createElement("div");
        statusHeader.style.display = "flex";
        statusHeader.style.alignItems = "center";
        statusHeader.style.gap = "10px";

        statusRoute = document.createElement("div");
        statusRoute.style.flex = "1";
        statusRoute.style.minWidth = "0";
        statusRoute.style.overflow = "hidden";
        statusRoute.style.color = "#a5f3fc";
        statusRoute.style.fontSize = "15px";
        statusRoute.style.fontWeight = "600";
        statusRoute.style.textOverflow = "ellipsis";
        statusRoute.style.whiteSpace = "nowrap";

        statusBadge = document.createElement("div");
        statusBadge.style.padding = "2px 6px";
        statusBadge.style.border = "1px solid #66e3ff";
        statusBadge.style.borderRadius = "3px";
        statusBadge.style.background = "#1b2530";
        statusBadge.style.color = "#a5f3fc";
        statusBadge.style.fontSize = "11px";
        statusBadge.style.fontWeight = "600";
        statusBadge.style.whiteSpace = "nowrap";

        statusCommand = document.createElement("div");
        statusCommand.style.marginTop = "4px";
        statusCommand.style.overflow = "hidden";
        statusCommand.style.color = "#f8fafc";
        statusCommand.style.fontSize = "18px";
        statusCommand.style.fontWeight = "600";
        statusCommand.style.lineHeight = "1.35";
        statusCommand.style.whiteSpace = "normal";

        statusNext = document.createElement("div");
        statusNext.style.display = "none";
        statusNext.style.marginTop = "5px";
        statusNext.style.color = "#cbd5e1";
        statusNext.style.fontSize = "12px";
        statusNext.style.lineHeight = "1.4";
        statusNext.style.whiteSpace = "normal";

        statusHeader.append(statusRoute, statusBadge);
        statusHud.append(statusHeader, statusCommand, statusNext);
        overlay.append(canvas, label, statusHud);
        document.documentElement.appendChild(overlay);
        lastRenderedPresentationRoute = null;
        lastDrawnPointIndex = 0;
        pointerLabelWidth = 280;
        pointerLabelHeight = 44;
    }

    function removeOverlay() {
        cancelScheduledVisualFrame();
        if (overlay) {
            overlay.remove();
        }

        overlay = null;
        canvas = null;
        label = null;
        statusHud = null;
        statusRoute = null;
        statusBadge = null;
        statusCommand = null;
        statusNext = null;
        lastRenderedPresentationRoute = null;
        lastDrawnPointIndex = 0;
        pointerLabelWidth = 280;
        pointerLabelHeight = 44;
    }

    function resetGestureState(consumeContextMenu) {
        const wasTracking = tracking;
        if (cancellationVisualTimer !== null) {
            clearTimeout(cancellationVisualTimer);
            cancellationVisualTimer = null;
        }
        clearGestureGuide();

        tracking = false;
        active = false;
        cancelled = false;
        useNativeVisualOverlay = false;
        route = "";
        startPoint = null;
        lastDirectionPoint = null;
        latestGesturePoint = null;
        points = [];
        lastVisualStateRoute = null;
        gestureStartLinkUrl = "";
        gestureStartLinkText = "";
        if (consumeContextMenu) {
            consumedContextMenu = true;
        }

        removeOverlay();
        removeWheelListeners();
        removeKeyListeners();
        if (wasTracking) {
            postMessage({ type: "gestureVisualEnd" });
        }
    }

    window.__tabBrowserGestureHostCancel = function () {
        resetGestureState(tracking);
    };

    window.__tabBrowserGestureHostUseNativeVisual = function () {
        useNativeVisualOverlay = true;
        removeOverlay();
    };

    function ensureOverlaySize() {
        if (!canvas) {
            return false;
        }

        const width = window.innerWidth;
        const height = window.innerHeight;
        if (canvas.width === width && canvas.height === height) {
            return false;
        }

        canvas.width = width;
        canvas.height = height;
        return true;
    }

    function getGesturePresentation(currentRoute) {
        if (!currentRoute) {
            return {
                status: "recognizing",
                text: "認識中",
                primaryText: "認識中",
                nextDirections: ""
            };
        }

        const item = commands.get(currentRoute);
        if (item) {
            const nextDirections = getNextDirectionText(currentRoute);
            return {
                status: "registered",
                text: currentRoute + "  " + item.label + (nextDirections ? "\n次: " + nextDirections : ""),
                primaryText: item.label,
                nextDirections
            };
        }

        for (const configuredRoute of commands.keys()) {
            if (!configuredRoute.startsWith("wheel")
                && configuredRoute.length > currentRoute.length
                && configuredRoute.startsWith(currentRoute)) {
                const nextDirections = getNextDirectionText(currentRoute);
                return {
                    status: "continuing",
                    text: currentRoute + "  入力中" + (nextDirections ? "\n次: " + nextDirections : ""),
                    primaryText: "入力中",
                    nextDirections
                };
            }
        }

        return {
            status: "unregistered",
            text: currentRoute + "  未登録",
            primaryText: "実行する機能なし",
            nextDirections: ""
        };
    }

    function getNextDirectionText(currentRoute) {
        const results = [];
        for (const direction of ["←", "→", "↑", "↓"]) {
            const nextRoute = currentRoute + direction;
            const exact = commands.get(nextRoute);
            let hasContinuation = false;
            for (const configuredRoute of commands.keys()) {
                if (!configuredRoute.startsWith("wheel")
                    && configuredRoute.length >= nextRoute.length
                    && configuredRoute.startsWith(nextRoute)) {
                    hasContinuation = true;
                    break;
                }
            }

            if (exact) {
                results.push(direction + " " + exact.label);
            } else if (hasContinuation) {
                results.push(direction + " 続き");
            }
        }

        return results.join("  ");
    }

    function getInitialGestureGuideText() {
        const items = [];
        for (const direction of ["←", "→", "↑", "↓"]) {
            const exact = commands.get(direction);
            const hasContinuation = Array.from(commands.keys()).some(configuredRoute =>
                !configuredRoute.startsWith("wheel")
                && configuredRoute.length > direction.length
                && configuredRoute.startsWith(direction));
            if (exact) {
                items.push(direction + " " + exact.label);
            } else if (hasContinuation) {
                items.push(direction + " 続き");
            }
        }

        if (items.length === 0) {
            return "ジェスチャー未登録";
        }

        return items.slice(0, 2).join("  ")
            + (items.length > 2 ? "\n" + items.slice(2).join("  ") : "");
    }

    function postGestureVisualState() {
        if (lastVisualStateRoute === route) {
            return;
        }

        lastVisualStateRoute = route;
        const presentation = getGesturePresentation(route);
        postMessage({
            type: "gestureVisualState",
            route,
            status: presentation.status
        });
    }

    function showCancellationFeedback() {
        if (!tracking || cancelled) {
            return;
        }

        cancelled = true;
        active = false;
        route = "";
        consumedContextMenu = true;
        removeWheelListeners();
        removeKeyListeners();
        clearGestureGuide();
        cancelScheduledVisualFrame();
        ensureOverlay();

        const point = latestGesturePoint || startPoint || { x: 24, y: 24 };
        if (label) {
            label.textContent = "キャンセル";
            refreshPointerLabelMetrics();
            positionOverlayLabel(point);
        }
        updateStatusHud({
            status: "cancelled",
            primaryText: "キャンセル",
            nextDirections: ""
        });

        postMessage({ type: "gestureVisualState", route: "", status: "cancelled" });
        cancellationVisualTimer = setTimeout(function () {
            cancellationVisualTimer = null;
            resetGestureState(true);
        }, 220);
    }

    function updateStatusHud(presentation) {
        if (!statusHud || !statusRoute || !statusBadge || !statusCommand || !statusNext) {
            return;
        }

        const state = presentation.status || "recognizing";
        const accent = state === "registered"
            ? "#34d399"
            : state === "continuing"
                ? "#fbbf24"
                : state === "unregistered" || state === "cancelled"
                    ? "#f87171"
                    : "#66e3ff";
        const statusText = state === "registered"
            ? "登録済み"
            : state === "continuing"
                ? "入力中"
                : state === "unregistered"
                    ? "未登録"
                    : state === "cancelled"
                        ? "キャンセル"
                        : "認識中";

        statusHud.style.display = "block";
        statusHud.style.borderLeftColor = accent;
        statusRoute.style.color = accent;
        statusRoute.textContent = "方向  " + (route || "…");
        statusBadge.style.borderColor = accent;
        statusBadge.style.color = accent;
        statusBadge.textContent = statusText;
        statusCommand.textContent = presentation.primaryText || statusText;
        statusNext.textContent = presentation.nextDirections
            ? "次  " + presentation.nextDirections
            : "";
        statusNext.style.display = presentation.nextDirections ? "block" : "none";
    }

    function scheduleGestureGuide() {
        clearGestureGuide();
        gestureGuideTimer = setTimeout(function () {
            gestureGuideTimer = null;
            if (!tracking || active || cancelled || !startPoint) {
                return;
            }

            gestureGuideVisible = true;
            ensureOverlay();
            if (label) {
                label.textContent = getInitialGestureGuideText();
                refreshPointerLabelMetrics();
                positionOverlayLabel(startPoint);
            }

            postMessage({ type: "gestureVisualState", route: "", status: "guide" });
        }, gestureGuideDelayMilliseconds);
    }

    function clearGestureGuide(notifyRecognizing) {
        if (gestureGuideTimer !== null) {
            clearTimeout(gestureGuideTimer);
            gestureGuideTimer = null;
        }

        if (!gestureGuideVisible) {
            return;
        }

        gestureGuideVisible = false;
        removeOverlay();
        if (notifyRecognizing) {
            postMessage({ type: "gestureVisualState", route: "", status: "recognizing" });
        }
    }

    function positionOverlayLabel(point) {
        if (!label) {
            return;
        }

        label.style.left = Math.min(point.x + 14, Math.max(8, window.innerWidth - pointerLabelWidth - 8)) + "px";
        label.style.top = Math.min(point.y + 14, Math.max(8, window.innerHeight - pointerLabelHeight - 8)) + "px";
    }

    function refreshPointerLabelMetrics() {
        if (!label) {
            return;
        }

        pointerLabelWidth = Math.min(label.offsetWidth || 280, 380);
        pointerLabelHeight = label.offsetHeight || 44;
    }

    function cancelScheduledVisualFrame() {
        if (visualFrameRequest === null) {
            return;
        }

        window.cancelAnimationFrame(visualFrameRequest);
        visualFrameRequest = null;
    }

    function scheduleDraw() {
        if (!canvas || !label || visualFrameRequest !== null) {
            return;
        }

        visualFrameRequest = window.requestAnimationFrame(function () {
            visualFrameRequest = null;
            drawIncrementalFrame();
        });
    }

    function drawIncrementalFrame() {
        if (!canvas || !label || cancelled || useNativeVisualOverlay) {
            return;
        }

        if (ensureOverlaySize()) {
            lastDrawnPointIndex = 0;
        }

        const context = canvas.getContext("2d");
        if (points.length > lastDrawnPointIndex + 1) {
            context.lineWidth = 4;
            context.lineCap = "round";
            context.lineJoin = "round";
            context.strokeStyle = "#66E3FF";
            context.shadowColor = "rgba(102, 227, 255, 0.7)";
            context.shadowBlur = 8;
            context.beginPath();
            context.moveTo(
                points[lastDrawnPointIndex].x,
                points[lastDrawnPointIndex].y);

            for (let index = lastDrawnPointIndex + 1; index < points.length; index++) {
                const point = points[index];
                context.lineTo(point.x, point.y);
            }

            context.stroke();
            lastDrawnPointIndex = points.length - 1;
        }

        if (lastRenderedPresentationRoute !== route) {
            const presentation = getGesturePresentation(route);
            label.textContent = presentation.text;
            updateStatusHud(presentation);
            refreshPointerLabelMetrics();
            lastRenderedPresentationRoute = route;
        }

        const lastPoint = latestGesturePoint || startPoint || { x: 24, y: 24 };
        positionOverlayLabel(lastPoint);
    }

    function appendDirection(from, to) {
        const dx = to.x - from.x;
        const dy = to.y - from.y;
        const absoluteX = Math.abs(dx);
        const absoluteY = Math.abs(dy);

        if (Math.max(absoluteX, absoluteY) < settings.gestureDirectionThreshold) {
            return;
        }

        const horizontal = absoluteX >= absoluteY;
        const primaryDistance = horizontal ? absoluteX : absoluteY;
        const secondaryDistance = horizontal ? absoluteY : absoluteX;
        if (primaryDistance < secondaryDistance * directionDominanceRatio) {
            return;
        }

        const direction = horizontal
            ? (dx < 0 ? "←" : "→")
            : (dy < 0 ? "↑" : "↓");
        const previousDirection = route[route.length - 1];
        if (previousDirection
            && isOppositeDirection(previousDirection, direction)
            && primaryDistance < settings.gestureDirectionThreshold * reverseDirectionThresholdMultiplier) {
            return;
        }

        if (previousDirection !== direction) {
            route += direction;
            postGestureVisualState();
        }

        lastDirectionPoint = to;
    }

    function isOppositeDirection(first, second) {
        return (first === "←" && second === "→")
            || (first === "→" && second === "←")
            || (first === "↑" && second === "↓")
            || (first === "↓" && second === "↑");
    }

    function distance(a, b) {
        return Math.hypot(a.x - b.x, a.y - b.y);
    }

    function appendVisualPoint(point) {
        latestGesturePoint = point;
        const lastPoint = points[points.length - 1];
        if (lastPoint && distance(lastPoint, point) < gestureVisualPointMinimumDistance) {
            return false;
        }

        points.push(point);
        return true;
    }

    function getSelectedText() {
        const activeElement = document.activeElement;
        if (activeElement && typeof activeElement.selectionStart === "number" && typeof activeElement.selectionEnd === "number") {
            return activeElement.value.substring(activeElement.selectionStart, activeElement.selectionEnd);
        }

        const selection = window.getSelection();
        return selection ? selection.toString() : "";
    }

    document.addEventListener("auxclick", function (event) {
        if (event.button === 1) {
            openBackgroundTab(event);
        }
    }, true);

    document.addEventListener("click", function (event) {
        if (event.button === 0 && event.ctrlKey) {
            openBackgroundTab(event);
        }
    }, true);

    document.addEventListener("mouseover", handleLinkHover, true);
    syncLinkHoverMouseMoveListener();
    document.addEventListener("fullscreenchange", syncLinkHoverMouseMoveListener, true);
    document.addEventListener("mouseout", function (event) {
        const relatedTarget = event.relatedTarget;
        if (!relatedTarget || !(relatedTarget instanceof Node)) {
            clearLinkHoverPreview();
            return;
        }

        if (hoveredLinkAnchor && !hoveredLinkAnchor.contains(relatedTarget)) {
            clearLinkHoverPreview();
        }
    }, true);
    window.addEventListener("scroll", clearLinkHoverPreview, true);

    function startGestureAt(x, y) {
        if (tracking) {
            return false;
        }

        clearLinkHoverPreview();
        tracking = true;
        active = false;
        cancelled = false;
        route = "";
        startPoint = { x, y };
        lastDirectionPoint = startPoint;
        latestGesturePoint = startPoint;
        points = [startPoint];
        const startAnchor = getAnchorAtPoint(x, y);
        gestureStartLinkUrl = startAnchor ? normalizeHref(startAnchor) : "";
        gestureStartLinkText = startAnchor ? getLinkPreviewTitle(startAnchor) : "";
        lastVisualStateRoute = null;
        ensureWheelListeners();
        ensureKeyListeners();
        postMessage({
            type: "gestureVisualStart",
            x,
            y
        });
        scheduleGestureGuide();
        return true;
    }

    function handleMouseDown(event) {
        if (!event.isTrusted || event.button !== 2 || tracking) {
            return;
        }

        startGestureAt(event.clientX, event.clientY);
    }

    window.addEventListener("mousedown", handleMouseDown, true);
    document.addEventListener("mousedown", handleMouseDown, true);

    function updateGesturePoint(point, showOverlay) {
        if (distance(startPoint, point) > gestureGuideMovementTolerance) {
            clearGestureGuide(true);
        }

        appendVisualPoint(point);

        if (!active && distance(startPoint, point) >= settings.gestureStartThreshold) {
            active = true;
            if (showOverlay) {
                ensureOverlay();
            }
        }

        if (!active) {
            return false;
        }

        appendDirection(lastDirectionPoint, point);
        if (showOverlay) {
            scheduleDraw();
        }

        return true;
    }

    function handleMouseMove(event) {
        if (event === lastHandledMouseMoveEvent) {
            return;
        }

        lastHandledMouseMoveEvent = event;
        if (!event.isTrusted) {
            return;
        }

        handleYouTubeLinkHoverMouseMove(event);
        if (!tracking || !startPoint || !lastDirectionPoint) {
            return;
        }

        if (cancelled) {
            event.preventDefault();
            event.stopPropagation();
            return;
        }

        const point = { x: event.clientX, y: event.clientY };
        if (!updateGesturePoint(point, true)) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
    }

    window.addEventListener("mousemove", handleMouseMove, true);
    document.addEventListener("mousemove", handleMouseMove, true);

    function ensureWheelListeners() {
        if (wheelListenersInstalled) {
            return;
        }

        window.addEventListener("wheel", handleWheel, { capture: true, passive: false });
        document.addEventListener("wheel", handleWheel, { capture: true, passive: false });
        wheelListenersInstalled = true;
    }

    function removeWheelListeners() {
        if (!wheelListenersInstalled) {
            return;
        }

        window.removeEventListener("wheel", handleWheel, true);
        document.removeEventListener("wheel", handleWheel, true);
        wheelListenersInstalled = false;
    }

    function ensureKeyListeners() {
        if (keyListenersInstalled) {
            return;
        }

        window.addEventListener("keydown", handleKeyDown, true);
        document.addEventListener("keydown", handleKeyDown, true);
        keyListenersInstalled = true;
    }

    function removeKeyListeners() {
        if (!keyListenersInstalled) {
            return;
        }

        window.removeEventListener("keydown", handleKeyDown, true);
        document.removeEventListener("keydown", handleKeyDown, true);
        keyListenersInstalled = false;
    }

    function handleKeyDown(event) {
        if (!event.isTrusted || !tracking || event.key !== "Escape") {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        showCancellationFeedback();
    }

    function handleWheel(event) {
        if (!event.isTrusted || (!tracking && (event.buttons & 2) !== 2)) {
            return;
        }

        if (event.deltaY === 0) {
            return;
        }

        const completedLinkUrl = gestureStartLinkUrl;
        const completedLinkText = gestureStartLinkText;
        resetGestureState(true);
        event.preventDefault();
        event.stopPropagation();

        const completedRoute = event.deltaY > 0 ? "wheelDown" : "wheelUp";
        const completedCommand = commands.get(completedRoute);

        if (!completedCommand) {
            return;
        }

        postMessage({
            type: "gesture",
            route: completedRoute,
            command: completedCommand.command,
            selectedText: getSelectedText(),
            linkUrl: completedLinkUrl,
            linkText: completedLinkText
        });
    }

    function completeGestureAfterFinalPoint() {
        if (cancelled) {
            consumedContextMenu = true;
            return true;
        }

        const completedRoute = route;
        const completedCommand = commands.get(completedRoute);
        const wasActive = active;
        const completedLinkUrl = gestureStartLinkUrl;
        const completedLinkText = gestureStartLinkText;

        resetGestureState(false);

        if (!wasActive) {
            return false;
        }

        consumedContextMenu = true;

        if (completedCommand) {
            postMessage({
                type: "gesture",
                route: completedRoute,
                command: completedCommand.command,
                selectedText: getSelectedText(),
                linkUrl: completedLinkUrl,
                linkText: completedLinkText
            });
        }

        return true;
    }

    function handleMouseUp(event) {
        if (!event.isTrusted || event.button !== 2 || !tracking) {
            return;
        }

        if (startPoint && lastDirectionPoint) {
            updateGesturePoint({ x: event.clientX, y: event.clientY }, false);
        }

        if (completeGestureAfterFinalPoint()) {
            event.preventDefault();
            event.stopPropagation();
        }
    }

    window.addEventListener("mouseup", handleMouseUp, true);
    document.addEventListener("mouseup", handleMouseUp, true);

    Object.defineProperty(window, "__tabBrowserGestureHostStartAt", {
        value: function (token, x, y) {
            if (token !== __tabBrowserWebMessageToken
                || !Number.isFinite(x)
                || !Number.isFinite(y)) {
                return false;
            }

            return startGestureAt(x, y);
        },
        writable: false,
        configurable: false
    });

    Object.defineProperty(window, "__tabBrowserGestureHostFinishAt", {
        value: function (token, x, y) {
            if (token !== __tabBrowserWebMessageToken
                || !Number.isFinite(x)
                || !Number.isFinite(y)
                || !tracking) {
                return false;
            }

            if (startPoint && lastDirectionPoint) {
                updateGesturePoint({ x, y }, false);
            }

            return completeGestureAfterFinalPoint();
        },
        writable: false,
        configurable: false
    });

    function handleContextMenu(event) {
        if (!event.isTrusted) {
            return;
        }

        if (consumedContextMenu || cancelled || active || (!settings.preserveContextMenuOnShortRightClick && tracking)) {
            consumedContextMenu = false;
            event.preventDefault();
            event.stopPropagation();
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        showPageContextMenu(event);
    }

    window.addEventListener("contextmenu", handleContextMenu, true);
    document.addEventListener("contextmenu", handleContextMenu, true);

    window.addEventListener("blur", function () {
        consumedContextMenu = false;
        clearLinkHoverPreview();
        resetGestureState(false);
    });
})();
