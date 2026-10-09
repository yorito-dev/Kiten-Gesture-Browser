namespace TabBrowser.Services;

internal static class XReadableScriptBuilder
{
    public static string Build(bool isEnabled)
    {
        var enabled = isEnabled ? "true" : "false";
        return $$"""
            (() => {
                const enabled = {{enabled}};
                const styleId = "tabbrowser-x-readable-style";
                const readableClass = "tabbrowser-x-readable";
                const hasArticlesClass = "tabbrowser-x-readable-has-articles";
                const hiddenAdClass = "tabbrowser-x-readable-hidden-ad";
                const wideContentClass = "tabbrowser-x-readable-wide-content";

                const removeReadableMode = () => {
                    document.documentElement.classList.remove(readableClass, hasArticlesClass);
                    document.getElementById(styleId)?.remove();
                    if (window.__tabBrowserXReadableDblClickHandler) {
                        document.removeEventListener("dblclick", window.__tabBrowserXReadableDblClickHandler, true);
                        window.__tabBrowserXReadableDblClickHandler = null;
                    }

                    if (window.__tabBrowserXReadableFullscreenChangeHandler) {
                        document.removeEventListener("fullscreenchange", window.__tabBrowserXReadableFullscreenChangeHandler, true);
                        window.__tabBrowserXReadableFullscreenChangeHandler = null;
                    }

                    window.__tabBrowserXReadableFullscreenScrollState = null;

                    if (window.__tabBrowserXReadableTimer) {
                        window.clearInterval(window.__tabBrowserXReadableTimer);
                        window.__tabBrowserXReadableTimer = 0;
                    }
                };

                const host = location.hostname.toLowerCase();
                const isTargetHost =
                    host === "x.com"
                    || host.endsWith(".x.com")
                    || host === "twitter.com"
                    || host.endsWith(".twitter.com");
                const path = location.pathname.toLowerCase();
                const isAuthFlow =
                    path.startsWith("/i/flow/")
                    || path.startsWith("/i/oauth2/")
                    || path.startsWith("/oauth/");

                if (!enabled || !isTargetHost || isAuthFlow) {
                    removeReadableMode();
                    return "removed";
                }

                let style = document.getElementById(styleId);
                if (!style) {
                    style = document.createElement("style");
                    style.id = styleId;
                    (document.head || document.documentElement).appendChild(style);
                }

                style.textContent = `
                    :root.${readableClass} {
                        color-scheme: dark;
                    }

                    :root.${readableClass}.${hasArticlesClass} body {
                        background: #0f1115 !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="sidebarColumn"] {
                        display: none !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] {
                        width: min(100%, clamp(720px, calc(100vw - 360px), 1120px)) !important;
                        max-width: min(100%, calc(100vw - 360px), 1120px) !important;
                        flex-basis: min(100%, clamp(720px, calc(100vw - 360px), 1120px)) !important;
                        box-sizing: border-box !important;
                        overflow-x: clip !important;
                        border-color: rgba(148, 163, 184, 0.24) !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"],
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] * {
                        scrollbar-width: none !important;
                        -ms-overflow-style: none !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"]::-webkit-scrollbar,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] *::-webkit-scrollbar {
                        width: 0 !important;
                        height: 0 !important;
                        display: none !important;
                        background: transparent !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"]::-webkit-scrollbar-thumb,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] *::-webkit-scrollbar-thumb,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"]::-webkit-scrollbar-track,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] *::-webkit-scrollbar-track {
                        background: transparent !important;
                        border: 0 !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] > div,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] section,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] [aria-label*="Timeline"],
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] [aria-label*="タイムライン"],
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] [data-testid="cellInnerDiv"],
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] [data-testid="cellInnerDiv"] > div,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] article[role="article"],
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] article[role="article"] > div {
                        width: 100% !important;
                        max-width: none !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] .${wideContentClass} {
                        width: 100% !important;
                        max-width: none !important;
                        min-width: 0 !important;
                        align-self: stretch !important;
                        box-sizing: border-box !important;
                        overflow-x: clip !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] article[role="article"] > div > div,
                    :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] article[role="article"] > div > div > div {
                        max-width: none !important;
                    }

                    @media (max-width: 1100px) {
                        :root.${readableClass}.${hasArticlesClass} [data-testid="primaryColumn"] {
                            width: auto !important;
                            max-width: none !important;
                            flex-basis: auto !important;
                        }
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"] {
                        background: rgba(17, 24, 39, 0.40) !important;
                        border-color: rgba(148, 163, 184, 0.22) !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"] + article[role="article"] {
                        margin-top: 6px !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"] [data-testid="tweetText"],
                    :root.${readableClass}.${hasArticlesClass} article[role="article"] div[lang] {
                        font-size: 17px !important;
                        line-height: 1.65 !important;
                        letter-spacing: 0 !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"] [data-testid="card.wrapper"] {
                        border-color: rgba(148, 163, 184, 0.24) !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"] video,
                    :root.${readableClass}.${hasArticlesClass} article[role="article"] img {
                        opacity: 1 !important;
                        visibility: visible !important;
                    }

                    :root.${readableClass}.${hasArticlesClass} article[role="article"].${hiddenAdClass} {
                        display: none !important;
                    }
                `;

                if (window.__tabBrowserXReadableDblClickHandler) {
                    document.removeEventListener("dblclick", window.__tabBrowserXReadableDblClickHandler, true);
                }

                if (window.__tabBrowserXReadableFullscreenChangeHandler) {
                    document.removeEventListener("fullscreenchange", window.__tabBrowserXReadableFullscreenChangeHandler, true);
                }

                const collectScrollContainers = (anchor) => {
                    const primaryColumn = anchor?.closest?.('[data-testid="primaryColumn"]')
                        ?? document.querySelector('[data-testid="primaryColumn"]')
                        ?? document.body;
                    const candidates = [
                        document.scrollingElement,
                        document.documentElement,
                        document.body,
                        primaryColumn,
                        ...Array.from(primaryColumn.querySelectorAll("*"))
                    ];
                    return candidates.filter((element, index, source) => {
                        return element
                            && source.indexOf(element) === index
                            && (element.scrollTop > 0
                                || element.scrollLeft > 0
                                || element.scrollHeight > element.clientHeight + 2
                                || element.scrollWidth > element.clientWidth + 2);
                    });
                };

                const captureFullscreenScrollState = (anchor) => {
                    const anchorElement =
                        anchor?.closest?.('article[role="article"]')
                        ?? anchor?.closest?.('[data-testid="cellInnerDiv"]')
                        ?? anchor
                        ?? null;
                    const anchorId = `tabbrowser-x-fullscreen-${Date.now()}-${Math.random().toString(36).slice(2)}`;
                    if (anchorElement instanceof Element) {
                        anchorElement.setAttribute("data-tabbrowser-x-fullscreen-anchor", anchorId);
                    }

                    window.__tabBrowserXReadableFullscreenScrollState = {
                        anchor: anchorElement,
                        anchorId,
                        anchorTop: anchorElement instanceof Element ? anchorElement.getBoundingClientRect().top : null,
                        windowX: window.scrollX,
                        windowY: window.scrollY,
                        containers: collectScrollContainers(anchorElement).map((element) => ({
                            element,
                            top: element.scrollTop,
                            left: element.scrollLeft
                        })),
                        restoring: false
                    };
                };

                const findScrollableAncestor = (anchor) => {
                    let node = anchor instanceof Element ? anchor.parentElement : null;
                    while (node && node !== document.body && node !== document.documentElement) {
                        if (node.scrollHeight > node.clientHeight + 2) {
                            return node;
                        }

                        node = node.parentElement;
                    }

                    return null;
                };

                const restoreFullscreenScrollState = () => {
                    const state = window.__tabBrowserXReadableFullscreenScrollState;
                    if (!state || state.restoring) {
                        return;
                    }

                    state.restoring = true;
                    const runRestore = () => {
                        for (const item of state.containers || []) {
                            if (item.element?.isConnected) {
                                item.element.scrollTop = item.top;
                                item.element.scrollLeft = item.left;
                            }
                        }

                        window.scrollTo({ left: state.windowX ?? 0, top: state.windowY ?? 0, behavior: "auto" });

                        const anchor =
                            state.anchor?.isConnected
                                ? state.anchor
                                : document.querySelector(`[data-tabbrowser-x-fullscreen-anchor="${state.anchorId}"]`);
                        if (anchor instanceof Element && Number.isFinite(state.anchorTop)) {
                            const delta = anchor.getBoundingClientRect().top - state.anchorTop;
                            if (Math.abs(delta) > 1) {
                                const scroller = findScrollableAncestor(anchor);
                                if (scroller) {
                                    scroller.scrollTop += delta;
                                } else {
                                    window.scrollTo({
                                        left: window.scrollX,
                                        top: window.scrollY + delta,
                                        behavior: "auto"
                                    });
                                }
                            }
                        }
                    };

                    window.requestAnimationFrame(runRestore);
                    window.setTimeout(runRestore, 80);
                    window.setTimeout(() => {
                        runRestore();
                        const anchor =
                            state.anchor?.isConnected
                                ? state.anchor
                                : document.querySelector(`[data-tabbrowser-x-fullscreen-anchor="${state.anchorId}"]`);
                        anchor?.removeAttribute?.("data-tabbrowser-x-fullscreen-anchor");
                        window.__tabBrowserXReadableFullscreenScrollState = null;
                    }, 260);
                };

                window.__tabBrowserXReadableFullscreenChangeHandler = () => {
                    if (!document.fullscreenElement) {
                        restoreFullscreenScrollState();
                    }
                };
                document.addEventListener("fullscreenchange", window.__tabBrowserXReadableFullscreenChangeHandler, true);

                window.__tabBrowserXReadableDblClickHandler = (event) => {
                    const target = event.target instanceof Element ? event.target : null;
                    const mediaHost = target?.closest('[data-testid="videoPlayer"], [data-testid="videoComponent"], [data-testid="videoPlayerComponent"]') ?? null;
                    const video =
                        target?.closest("video")
                        ?? mediaHost?.querySelector("video")
                        ?? target?.querySelector?.("video")
                        ?? null;
                    const fullscreenElement = document.fullscreenElement;
                    const exitFullscreen = document.exitFullscreen?.bind(document);

                    if (fullscreenElement
                        && exitFullscreen
                        && (video || (target && (target === fullscreenElement || fullscreenElement.contains(target))))) {
                        event.preventDefault();
                        event.stopPropagation();
                        exitFullscreen()
                            .then(restoreFullscreenScrollState)
                            .catch(() => {});
                        return;
                    }

                    if (!video) {
                        return;
                    }

                    const fullscreenTarget = mediaHost ?? video;
                    const requestFullscreen = fullscreenTarget.requestFullscreen?.bind(fullscreenTarget)
                        ?? video.requestFullscreen?.bind(video);
                    if (!requestFullscreen) {
                        return;
                    }

                    event.preventDefault();
                    event.stopPropagation();
                    captureFullscreenScrollState(fullscreenTarget);
                    requestFullscreen().catch(() => {
                        captureFullscreenScrollState(video);
                        video.requestFullscreen?.().catch(() => {
                            window.__tabBrowserXReadableFullscreenScrollState = null;
                        });
                    });
                };
                document.addEventListener("dblclick", window.__tabBrowserXReadableDblClickHandler, true);

                const hasPromotedLabel = (article) => {
                    const labels = ["プロモーション", "Promoted", "広告"];
                    const nodes = article.querySelectorAll("span, div, a");
                    for (const node of nodes) {
                        if (node.closest('[data-testid="tweetText"], div[lang]')) {
                            continue;
                        }

                        const text = (node.textContent || "").trim();
                        const ariaLabel = (node.getAttribute("aria-label") || "").trim();
                        if (labels.some((label) => text === label || ariaLabel === label || ariaLabel.includes(label))) {
                            return true;
                        }
                    }

                    return false;
                };

                const markWideContentAncestors = (article) => {
                    const primaryColumn = article.closest('[data-testid="primaryColumn"]');
                    let node = article;
                    while (node && node !== primaryColumn) {
                        node.classList?.add(wideContentClass);
                        node = node.parentElement;
                    }
                };

                const isVisibleElement = (element) => {
                    const rect = element.getBoundingClientRect();
                    return rect.width > 1 && rect.height > 1;
                };

                const translateLabels = [
                    "翻訳を表示",
                    "翻訳する",
                    "ポストを翻訳",
                    "ツイートを翻訳",
                    "Translate post",
                    "Translate Tweet",
                    "Show translation",
                    "See translation"
                ];
                const translateExcludeLabels = [
                    "原文を表示",
                    "元のポストを表示",
                    "元のツイートを表示",
                    "翻訳を非表示",
                    "Show original",
                    "Hide translation"
                ];

                const getElementLabel = (element) => {
                    return [
                        element.textContent,
                        element.getAttribute("aria-label"),
                        element.getAttribute("title")
                    ]
                        .filter(Boolean)
                        .join(" ")
                        .replace(/\s+/g, " ")
                        .trim();
                };

                const isTranslateControl = (element) => {
                    const label = getElementLabel(element);
                    if (!label) {
                        return false;
                    }

                    if (translateExcludeLabels.some((excludeLabel) => label.includes(excludeLabel))) {
                        return false;
                    }

                    return translateLabels.some((translateLabel) => label.includes(translateLabel));
                };

                const autoTranslateArticle = (article) => {
                    const controls = article.querySelectorAll('button, [role="button"], a[href]');
                    for (const control of controls) {
                        if (!(control instanceof HTMLElement)
                            || control.dataset.tabbrowserXTranslateClicked === "1"
                            || !isVisibleElement(control)
                            || !isTranslateControl(control)) {
                            continue;
                        }

                        control.dataset.tabbrowserXTranslateClicked = "1";
                        control.click();
                        return;
                    }
                };

                const refreshState = () => {
                    const articles = Array.from(document.querySelectorAll('article[role="article"]'));
                    const hasArticles = articles.length > 0;
                    document.documentElement.classList.toggle(hasArticlesClass, hasArticles);
                    for (const article of articles) {
                        const isHiddenAd = hasPromotedLabel(article);
                        article.classList.toggle(hiddenAdClass, isHiddenAd);
                        markWideContentAncestors(article);
                        if (!isHiddenAd) {
                            autoTranslateArticle(article);
                        }

                        article.classList.remove(
                            "tabbrowser-x-readable-thread-main",
                            "tabbrowser-x-readable-reply-post");
                    }
                };

                document.documentElement.classList.add(readableClass);
                refreshState();
                if (!window.__tabBrowserXReadableTimer) {
                    window.__tabBrowserXReadableTimer = window.setInterval(refreshState, 1200);
                }

                return "applied";
            })();
            """;
    }
}
