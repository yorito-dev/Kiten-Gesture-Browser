using System.Text.Json;
using System.Text.RegularExpressions;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class FiveChLiveReadabilityService
{
    public const string StyleElementId = "tabbrowser-fivech-live-style";
    public const string StateKey = "__tabBrowserFiveChLiveReadability";

    public bool IsFiveChThreadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IsFiveChHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4
            && segments[0].Equals("test", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("read.cgi", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(segments[2])
            && long.TryParse(segments[3], out _);
    }

    public bool IsReplyAnchorLink(string? currentUrl, string? targetUrl, string? linkText)
    {
        if (!IsFiveChThreadUrl(currentUrl)
            || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
        {
            return false;
        }

        if (Regex.IsMatch(linkText?.Trim() ?? string.Empty, @"^(?:>>|＞＞)\s*\d+"))
        {
            return true;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || !currentUri.Host.Equals(targetUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currentSegments = currentUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var targetSegments = targetUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (currentSegments.Length < 4
            || targetSegments.Length < 4
            || !currentSegments.Take(4).SequenceEqual(targetSegments.Take(4), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return targetSegments.Length >= 5 && long.TryParse(targetSegments[4], out _)
            || long.TryParse(targetUri.Fragment.TrimStart('#'), out _);
    }

    public string BuildApplyScript(FiveChLiveReadabilitySettings? settings, bool isActive = true)
    {
        var normalized = FiveChLiveReadabilityProfile.Normalize(settings);
        var settingsJson = JsonSerializer.Serialize(new
        {
            enabled = normalized.IsEnabled,
            active = isActive,
            highlightNewPosts = normalized.HighlightNewPosts,
            autoRefresh = normalized.AutoRefresh,
            refreshIntervalSeconds = normalized.RefreshIntervalSeconds,
            autoOpenNextThread = normalized.AutoOpenNextThread,
            autoScroll = normalized.AutoScroll,
            previewReplyAnchors = normalized.PreviewReplyAnchors,
            highlightPosterIds = normalized.HighlightPosterIds,
            fontSize = normalized.FontSize,
            postSpacing = normalized.PostSpacing
        });

        return ApplyScriptTemplate.Replace("__TAB_BROWSER_SETTINGS__", settingsJson, StringComparison.Ordinal);
    }

    public string BuildSetActiveScript(bool isActive)
    {
        var stateKeyJson = JsonSerializer.Serialize(StateKey);
        var isActiveJson = JsonSerializer.Serialize(isActive);
        return $"window[{stateKeyJson}]?.setActive?.({isActiveJson});";
    }

    private static bool IsFiveChHost(string host)
    {
        return host.Equals("5ch.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".5ch.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("5ch.io", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".5ch.io", StringComparison.OrdinalIgnoreCase);
    }

    private const string ApplyScriptTemplate = """
        (() => {
          const stateKey = "__tabBrowserFiveChLiveReadability";
          const styleId = "tabbrowser-fivech-live-style";
          const bodyClass = "tabbrowser-fivech-live";
          const settings = __TAB_BROWSER_SETTINGS__;
          const previous = window[stateKey];
          if (previous && typeof previous.dispose === "function") {
            previous.dispose();
          }

          const cleanupDom = () => {
            document.getElementById(styleId)?.remove();
            document.body?.classList.remove(bodyClass);
            document.querySelectorAll(".post").forEach((post) => {
              post.classList.remove("tb5ch-read", "tb5ch-unread", "tb5ch-fresh", "tb5ch-filtered-out", "tb5ch-target-flash");
              post.style.removeProperty("--tb5ch-id-color");
              post.removeAttribute("data-tb5ch-enhanced");
            });
            document.querySelectorAll(".uid[data-tb5ch-id]").forEach((uid) => {
              uid.style.removeProperty("--tb5ch-id-color");
              uid.removeAttribute("data-tb5ch-id");
              uid.removeAttribute("title");
            });
            document.querySelectorAll(".tb5ch-image-link, .tb5ch-external-link").forEach((link) => {
              link.classList.remove("tb5ch-image-link", "tb5ch-external-link");
            });
            document.getElementById("tabbrowser-fivech-new-posts")?.remove();
            document.getElementById("tabbrowser-fivech-refresh-status")?.remove();
            document.getElementById("tabbrowser-fivech-reply-preview")?.remove();
            document.getElementById("tabbrowser-fivech-id-filter")?.remove();
            document.querySelectorAll(".post_hover").forEach((preview) => preview.remove());
          };

          const boardText = document.querySelector("#boardname")?.textContent || "";
          const board = location.pathname.split("/").filter(Boolean)[2] || "";
          const liveBoards = new Set([
            "dancesite", "dome", "endless", "festival", "himawari", "jasmine", "kokkai",
            "liveanb", "livebase", "livecx", "liveetv", "livefoot", "livegalileo", "livejupiter",
            "livemarket1", "livemarket2", "livenhk", "livesaturn", "livetbs", "livetx", "liventv",
            "liveuranus", "livevenus", "local", "oonna", "radio", "sports", "weekly"
          ]);
          const isLiveThread = boardText.includes("実況") || liveBoards.has(board.toLowerCase());
          if (!settings.enabled || !isLiveThread || !document.querySelector(".post")) {
            cleanupDom();
            window[stateKey] = { dispose: cleanupDom };
            return;
          }

          cleanupDom();
          const style = document.createElement("style");
          style.id = styleId;
          style.textContent = `
            body.${bodyClass} {
              margin: 0 10px 48px !important;
              background: #171b20 !important;
              color: #e5e7eb !important;
            }
            body.${bodyClass} #hideme,
            body.${bodyClass} #hidemetoo,
            body.${bodyClass} #fixedDivLeft,
            body.${bodyClass} #fixedDivRight,
            body.${bodyClass} .vm-placement,
            body.${bodyClass} ins.adsbygoogle,
            body.${bodyClass} [id^="div-gpt-ad"],
            body.${bodyClass} .post ~ .navmenu,
            body.${bodyClass} .post ~ .newposts,
            body.${bodyClass} .post ~ .formbox,
            body.${bodyClass} footer {
              display: none !important;
            }
            body.${bodyClass} .post_hover {
              display: none !important;
            }
            body.${bodyClass} #maincontent,
            body.${bodyClass} #threadcontent {
              width: 100% !important;
              max-width: 1120px !important;
              margin-left: auto !important;
              margin-right: auto !important;
              box-sizing: border-box !important;
            }
            body.${bodyClass} #threadtitle {
              margin: 12px 0 !important;
              color: #f9fafb !important;
              font-size: 21px !important;
              line-height: 1.35 !important;
            }
            body.${bodyClass} .post {
              display: block !important;
              box-sizing: border-box !important;
              width: 100% !important;
              margin: 0 0 ${settings.postSpacing}px !important;
              padding: 7px 4px 9px !important;
              overflow: visible !important;
              border: 0 !important;
              border-bottom: 1px solid #323943 !important;
              border-radius: 0 !important;
              background: transparent !important;
              color: #e5e7eb !important;
            }
            body.${bodyClass} .post.tb5ch-read {
              opacity: 1;
            }
            body.${bodyClass} .post.tb5ch-unread {
              border-bottom-color: #38bdf8 !important;
              background: #15232d !important;
            }
            body.${bodyClass} .post.tb5ch-fresh {
              animation: tb5ch-new-post-highlight 8s ease-out both;
            }
            body.${bodyClass} .post.tb5ch-target-flash {
              outline: 2px solid #facc15 !important;
              outline-offset: -2px !important;
            }
            body.${bodyClass} .post.tb5ch-filtered-out {
              display: none !important;
            }
            @keyframes tb5ch-new-post-highlight {
              0%, 30% { background: #183446; }
              100% { background: transparent; }
            }
            body.${bodyClass} .post-header {
              display: flex !important;
              flex-wrap: wrap !important;
              align-items: center !important;
              gap: 3px 8px !important;
              margin: 0 0 5px !important;
              color: #8fa5b8 !important;
              font-size: 12px !important;
              line-height: 1.35 !important;
            }
            body.${bodyClass} .post-header > div:first-child,
            body.${bodyClass} .post-header > span {
              display: contents !important;
            }
            body.${bodyClass} .post-header form,
            body.${bodyClass} .post-header [style*="float:right"],
            body.${bodyClass} .post-header [style*="float: right"] {
              display: none !important;
            }
            body.${bodyClass} .postid {
              display: inline-block !important;
              min-width: auto !important;
              margin: 0 !important;
              padding: 2px 6px !important;
              border: 1px solid #3b4653 !important;
              border-radius: 4px !important;
              color: #dbeafe !important;
              background: #111820 !important;
              font-size: 12px !important;
              font-weight: 700 !important;
            }
            body.${bodyClass} .postid::before {
              content: none !important;
            }
            body.${bodyClass} .postusername {
              margin: 0 !important;
              color: #8fb6d1 !important;
              font-size: 12px !important;
            }
            body.${bodyClass} .postusername b {
              font-weight: 400 !important;
            }
            body.${bodyClass} .date {
              margin: 0 !important;
              color: #8da3b7 !important;
              font-size: 11px !important;
              font-variant-numeric: tabular-nums !important;
            }
            body.${bodyClass} .uid {
              display: inline-block !important;
              padding: 2px 6px !important;
              border: 1px solid #3b4653 !important;
              border-radius: 4px !important;
              color: var(--tb5ch-id-color, #cbd5e1) !important;
              background: #111820 !important;
              font-size: 12px !important;
              font-weight: 700 !important;
              cursor: pointer !important;
              user-select: text !important;
            }
            body.${bodyClass} .post-content,
            body.${bodyClass} .message {
              max-width: 100% !important;
              color: #f3f4f6 !important;
              font-size: ${settings.fontSize}px !important;
              line-height: 1.55 !important;
              overflow-wrap: anywhere !important;
              word-break: break-word !important;
              white-space: normal !important;
            }
            body.${bodyClass} .post-content a,
            body.${bodyClass} .message a {
              overflow-wrap: anywhere !important;
              word-break: break-all !important;
            }
            body.${bodyClass} a.reply_link {
              display: inline !important;
              margin: 0 !important;
              padding: 0 !important;
              border: 0 !important;
              border-radius: 0 !important;
              color: #38a9ff !important;
              background: transparent !important;
              font-weight: 500 !important;
              text-decoration: underline !important;
              text-decoration-thickness: 1px !important;
              text-underline-offset: 2px !important;
            }
            body.${bodyClass} a.reply_link:hover {
              color: #7cc7ff !important;
              background: transparent !important;
            }
            body.${bodyClass} .tb5ch-image-link {
              color: #f9a8d4 !important;
              text-decoration: underline !important;
              text-decoration-style: dotted !important;
            }
            body.${bodyClass} .tb5ch-external-link:not(.tb5ch-image-link) {
              color: #67e8f9 !important;
              text-decoration: underline !important;
              text-decoration-style: dotted !important;
            }
            body.${bodyClass} .post-content img,
            body.${bodyClass} .message img {
              max-width: min(100%, 720px) !important;
              height: auto !important;
            }
            #tabbrowser-fivech-new-posts {
              position: fixed;
              right: 22px;
              bottom: 24px;
              z-index: 2147483646;
              min-width: 112px;
              min-height: 38px;
              padding: 8px 14px;
              border: 1px solid #38bdf8;
              border-radius: 4px;
              color: #f8fafc;
              background: #075985;
              box-shadow: 0 8px 24px rgba(0, 0, 0, 0.42);
              font: 700 14px/1.2 system-ui, sans-serif;
              cursor: pointer;
            }
            #tabbrowser-fivech-refresh-status {
              position: fixed;
              right: 22px;
              bottom: 70px;
              z-index: 2147483645;
              padding: 4px 8px;
              border: 1px solid #475569;
              border-radius: 3px;
              color: #cbd5e1;
              background: rgba(15, 23, 42, 0.94);
              font: 600 11px/1.25 system-ui, sans-serif;
              pointer-events: none;
            }
            #tabbrowser-fivech-refresh-status[data-state="error"] {
              border-color: #f87171;
              color: #fecaca;
              background: rgba(69, 10, 10, 0.96);
            }
            #tabbrowser-fivech-refresh-status[data-state="done"] {
              border-color: #64748b;
              color: #94a3b8;
            }
            #tabbrowser-fivech-reply-preview {
              position: fixed;
              z-index: 2147483647;
              width: min(430px, calc(100vw - 24px));
              max-height: min(360px, calc(100vh - 24px));
              padding: 10px 12px;
              overflow: auto;
              border: 1px solid #60a5fa;
              border-radius: 4px;
              color: #f3f4f6;
              background: #111827;
              box-shadow: 0 12px 36px rgba(0, 0, 0, 0.58);
              font: ${settings.fontSize}px/1.6 system-ui, sans-serif;
              overflow-wrap: anywhere;
              pointer-events: auto;
            }
            #tabbrowser-fivech-reply-preview .post {
              margin: 0 !important;
            }
            #tabbrowser-fivech-reply-preview .tb5ch-reply-preview-level + .tb5ch-reply-preview-level {
              margin-top: 10px;
              padding-top: 10px;
              border-top: 1px solid #475569;
            }
            #tabbrowser-fivech-id-filter {
              position: sticky;
              top: 4px;
              z-index: 2147483645;
              display: flex;
              align-items: center;
              justify-content: space-between;
              gap: 12px;
              margin: 4px 0 8px;
              padding: 7px 10px;
              border: 1px solid #22d3ee;
              border-radius: 4px;
              color: #ecfeff;
              background: #164e63;
              font: 700 13px/1.3 system-ui, sans-serif;
            }
            #tabbrowser-fivech-id-filter button {
              min-height: 28px;
              padding: 3px 10px;
              border: 1px solid #a5f3fc;
              border-radius: 3px;
              color: #ecfeff;
              background: #155e75;
              cursor: pointer;
            }
          `;
          (document.head || document.documentElement).appendChild(style);
          document.body.classList.add(bodyClass);

          const timers = new Set();
          const knownPostIds = new Set();
          let pendingCount = 0;
          let activeId = "";
          let scrollFrame = 0;
          let refreshTimer = 0;
          let refreshAbortController = null;
          let refreshInFlight = false;
          let refreshFailureCount = 0;
          let refreshActive = settings.active;
          let threadFinished = Boolean(document.querySelector(".stopdone"));
          let nextThreadNavigationStarted = false;
          let follow = settings.autoScroll && isNearBottom();
          let replyPreviewCloseTimer = 0;
          let disposed = false;

          const newPostsButton = document.createElement("button");
          newPostsButton.id = "tabbrowser-fivech-new-posts";
          newPostsButton.type = "button";
          newPostsButton.hidden = true;
          document.body.appendChild(newPostsButton);

          const refreshStatus = document.createElement("div");
          refreshStatus.id = "tabbrowser-fivech-refresh-status";
          refreshStatus.hidden = !settings.autoRefresh;
          document.body.appendChild(refreshStatus);

          const replyPreview = document.createElement("div");
          replyPreview.id = "tabbrowser-fivech-reply-preview";
          replyPreview.hidden = true;
          document.body.appendChild(replyPreview);

          const idFilter = document.createElement("div");
          idFilter.id = "tabbrowser-fivech-id-filter";
          idFilter.hidden = true;
          const idFilterLabel = document.createElement("span");
          const idFilterClear = document.createElement("button");
          idFilterClear.type = "button";
          idFilterClear.textContent = "絞り込み解除";
          idFilter.append(idFilterLabel, idFilterClear);
          (document.querySelector("#threadcontent") || document.body).prepend(idFilter);

          function isNearBottom() {
            const root = document.scrollingElement || document.documentElement;
            return root.scrollHeight - root.scrollTop - root.clientHeight <= 180;
          }

          const getPostId = (post) => (post.dataset.id || post.id || "").trim();
          const getThreadPosts = () => [...document.querySelectorAll(".post")].filter((post) =>
            !post.closest("#tabbrowser-fivech-reply-preview, .post_hover")
            && !post.parentElement?.closest(".post"));
          const getPosterId = (post) => {
            const value = post.dataset.userid || post.querySelector(".uid")?.textContent || "";
            return value.trim();
          };

          const colorForId = (value) => {
            let hash = 0;
            for (let index = 0; index < value.length; index += 1) {
              hash = ((hash << 5) - hash + value.charCodeAt(index)) | 0;
            }
            return `hsl(${Math.abs(hash) % 360} 72% 72%)`;
          };

          const classifyLinks = (post) => {
            post.querySelectorAll(".post-content a, .message a").forEach((link) => {
              let parsed;
              try {
                parsed = new URL(link.href, location.href);
              } catch {
                return;
              }
              if (/\.(?:avif|bmp|gif|jpe?g|png|webp)(?:$|[?#])/i.test(parsed.href)) {
                link.classList.add("tb5ch-image-link");
              } else if (parsed.host && parsed.host !== location.host && !link.classList.contains("reply_link")) {
                link.classList.add("tb5ch-external-link");
              }
            });
          };

          const markRead = (post) => {
            post.classList.remove("tb5ch-unread");
            post.classList.add("tb5ch-read");
          };

          const readObserver = new IntersectionObserver((entries) => {
            entries.forEach((entry) => {
              if (entry.isIntersecting && entry.intersectionRatio >= 0.55) {
                markRead(entry.target);
                readObserver.unobserve(entry.target);
              }
            });
          }, { threshold: [0.55] });

          const enhancePost = (post, isNew) => {
            if (!(post instanceof HTMLElement) || post.dataset.tb5chEnhanced === "true") {
              return;
            }
            post.dataset.tb5chEnhanced = "true";
            classifyLinks(post);
            const posterId = getPosterId(post);
            if (settings.highlightPosterIds && posterId) {
              const color = colorForId(posterId);
              post.style.setProperty("--tb5ch-id-color", color);
              const uid = post.querySelector(".uid");
              if (uid) {
                uid.style.setProperty("--tb5ch-id-color", color);
                uid.dataset.tb5chId = posterId;
                uid.title = "このIDのレスだけを表示";
              }
            }
            if (isNew && settings.highlightNewPosts) {
              post.classList.add("tb5ch-unread", "tb5ch-fresh");
              readObserver.observe(post);
              const timer = window.setTimeout(() => {
                post.classList.remove("tb5ch-fresh");
                timers.delete(timer);
              }, 8000);
              timers.add(timer);
            } else {
              markRead(post);
            }
          };

          const updateNewPostsButton = () => {
            newPostsButton.hidden = pendingCount <= 0;
            newPostsButton.textContent = pendingCount > 0 ? `新着${pendingCount}件` : "";
          };

          const clearPending = () => {
            pendingCount = 0;
            updateNewPostsButton();
          };

          const scrollToLatest = () => {
            const root = document.scrollingElement || document.documentElement;
            window.scrollTo({ top: root.scrollHeight, behavior: "auto" });
            follow = settings.autoScroll;
            clearPending();
          };

          const applyIdFilter = (posterId) => {
            activeId = posterId;
            getThreadPosts().forEach((post) => {
              post.classList.toggle("tb5ch-filtered-out", Boolean(activeId) && getPosterId(post) !== activeId);
            });
            idFilter.hidden = !activeId;
            idFilterLabel.textContent = activeId ? `${activeId} のレスのみ表示` : "";
          };

          const findReplyTarget = (anchor) => {
            const match = (anchor.textContent || "").match(/(?:>>|＞＞)\s*(\d+)/);
            if (!match) {
              return null;
            }
            return getThreadPosts().find((post) => post.id === match[1] || post.dataset.id === match[1]) || null;
          };

          const cancelReplyPreviewClose = () => {
            if (!replyPreviewCloseTimer) {
              return;
            }
            clearTimeout(replyPreviewCloseTimer);
            timers.delete(replyPreviewCloseTimer);
            replyPreviewCloseTimer = 0;
          };

          const closeReplyPreview = () => {
            cancelReplyPreviewClose();
            replyPreview.hidden = true;
            replyPreview.replaceChildren();
          };

          const scheduleReplyPreviewClose = () => {
            cancelReplyPreviewClose();
            const timer = window.setTimeout(() => {
              timers.delete(timer);
              replyPreviewCloseTimer = 0;
              closeReplyPreview();
            }, 300);
            replyPreviewCloseTimer = timer;
            timers.add(timer);
          };

          const createReplyPreviewLevel = (target) => {
            const level = document.createElement("div");
            level.className = "tb5ch-reply-preview-level";
            level.dataset.postId = getPostId(target);
            const clone = target.cloneNode(true);
            clone.removeAttribute("id");
            clone.querySelectorAll("form, button, script").forEach((element) => element.remove());
            level.appendChild(clone);
            return level;
          };

          const showReplyPreview = (anchor) => {
            if (!settings.previewReplyAnchors) {
              return;
            }
            const target = findReplyTarget(anchor);
            if (!target) {
              closeReplyPreview();
              return;
            }
            cancelReplyPreviewClose();
            replyPreview.replaceChildren(createReplyPreviewLevel(target));
            replyPreview.scrollTop = 0;
            replyPreview.hidden = false;
            const anchorRect = anchor.getBoundingClientRect();
            const previewRect = replyPreview.getBoundingClientRect();
            const margin = 12;
            let left = Math.min(anchorRect.left, window.innerWidth - previewRect.width - margin);
            left = Math.max(margin, left);
            let top = anchorRect.bottom + 8;
            if (top + previewRect.height > window.innerHeight - margin) {
              top = anchorRect.top - previewRect.height - 8;
            }
            top = Math.max(margin, Math.min(top, window.innerHeight - previewRect.height - margin));
            replyPreview.style.left = `${left}px`;
            replyPreview.style.top = `${top}px`;
          };

          const appendReplyPreviewLevel = (anchor) => {
            const sourceLevel = anchor.closest(".tb5ch-reply-preview-level");
            const target = findReplyTarget(anchor);
            if (!sourceLevel || !target) {
              return false;
            }
            const levels = [...replyPreview.querySelectorAll(":scope > .tb5ch-reply-preview-level")];
            const sourceIndex = levels.indexOf(sourceLevel);
            if (sourceIndex < 0) {
              return false;
            }
            levels.slice(sourceIndex + 1).forEach((level) => level.remove());
            const targetId = getPostId(target);
            if (levels.slice(0, sourceIndex + 1).some((level) => level.dataset.postId === targetId)) {
              return true;
            }
            replyPreview.appendChild(createReplyPreviewLevel(target));
            requestAnimationFrame(() => {
              if (!replyPreview.hidden) {
                replyPreview.scrollTop = replyPreview.scrollHeight;
              }
            });
            return true;
          };

          const onScroll = (event) => {
            if (event.target instanceof Node && replyPreview.contains(event.target)) {
              return;
            }
            if (scrollFrame) {
              return;
            }
            scrollFrame = requestAnimationFrame(() => {
              scrollFrame = 0;
              closeReplyPreview();
              if (settings.autoScroll) {
                follow = isNearBottom();
                if (follow) {
                  clearPending();
                }
              }
            });
          };

          const onMouseOver = (event) => {
            if (!(event.target instanceof Element)) {
              return;
            }
            const anchor = event.target.closest("a.reply_link");
            const relatedTarget = event.relatedTarget;
            if (anchor && replyPreview.contains(anchor)) {
              cancelReplyPreviewClose();
              if (!(relatedTarget instanceof Node) || !anchor.contains(relatedTarget)) {
                appendReplyPreviewLevel(anchor);
              }
              return;
            }
            if (anchor && (!(relatedTarget instanceof Node) || !anchor.contains(relatedTarget))) {
              showReplyPreview(anchor);
            }
          };

          const onMouseOut = (event) => {
            if (!(event.target instanceof Element)) {
              return;
            }
            const anchor = event.target.closest("a.reply_link");
            const relatedTarget = event.relatedTarget;
            if (anchor && replyPreview.contains(anchor)) {
              return;
            }
            if (anchor && (!(relatedTarget instanceof Node) || !anchor.contains(relatedTarget))) {
              scheduleReplyPreviewClose();
            }
          };

          const onReplyPreviewMouseEnter = () => cancelReplyPreviewClose();
          const onReplyPreviewMouseLeave = () => scheduleReplyPreviewClose();
          const onReplyPreviewClick = (event) => {
            if (!(event.target instanceof Element)) {
              return;
            }
            const anchor = event.target.closest("a.reply_link");
            if (anchor && appendReplyPreviewLevel(anchor)) {
              event.preventDefault();
              event.stopPropagation();
            }
          };

          const onClick = (event) => {
            if (!(event.target instanceof Element)) {
              return;
            }
            const uid = event.target.closest(".uid[data-tb5ch-id]");
            if (uid && settings.highlightPosterIds) {
              event.preventDefault();
              const selectedId = uid.dataset.tb5chId || "";
              applyIdFilter(activeId === selectedId ? "" : selectedId);
              return;
            }
            const anchor = event.target.closest("a.reply_link");
            const target = anchor ? findReplyTarget(anchor) : null;
            if (target) {
              target.classList.add("tb5ch-target-flash");
              const timer = window.setTimeout(() => {
                target.classList.remove("tb5ch-target-flash");
                timers.delete(timer);
              }, 1600);
              timers.add(timer);
            }
          };

          const initialPosts = getThreadPosts();
          initialPosts.forEach((post) => {
            const postId = getPostId(post);
            if (postId) {
              knownPostIds.add(postId);
            }
            enhancePost(post, false);
          });

          const mutationObserver = new MutationObserver((mutations) => {
            const candidates = new Set();
            mutations.forEach((mutation) => {
              mutation.addedNodes.forEach((node) => {
                if (!(node instanceof Element)) {
                  return;
                }
                if (node.matches(".post")) {
                  candidates.add(node);
                }
                node.querySelectorAll?.(".post").forEach((post) => candidates.add(post));
              });
            });
            let newCount = 0;
            candidates.forEach((post) => {
              const postId = getPostId(post);
              const isNew = !postId || !knownPostIds.has(postId);
              if (postId) {
                knownPostIds.add(postId);
              }
              enhancePost(post, isNew);
              if (isNew) {
                newCount += 1;
              }
            });
            if (newCount <= 0) {
              return;
            }
            if (activeId) {
              applyIdFilter(activeId);
            }
            if (settings.autoScroll && follow) {
              requestAnimationFrame(scrollToLatest);
            } else {
              pendingCount += newCount;
              updateNewPostsButton();
            }
          });
          mutationObserver.observe(document.querySelector("#threadcontent") || document.body, {
            childList: true,
            subtree: true
          });

          const updateRefreshStatus = (text, state = "active") => {
            refreshStatus.hidden = !settings.autoRefresh;
            refreshStatus.textContent = text;
            refreshStatus.dataset.state = state;
          };

          const clearRefreshTimer = () => {
            if (refreshTimer) {
              clearTimeout(refreshTimer);
              refreshTimer = 0;
            }
          };

          const scheduleRefresh = (delaySeconds) => {
            clearRefreshTimer();
            if (disposed
                || !settings.autoRefresh
                || !refreshActive
                || nextThreadNavigationStarted
                || (threadFinished && !settings.autoOpenNextThread)
                || document.visibilityState !== "visible") {
              return;
            }
            refreshTimer = window.setTimeout(refreshPosts, Math.max(0, delaySeconds) * 1000);
          };

          const decodeThreadResponse = async (response) => {
            const bytes = await response.arrayBuffer();
            const contentType = response.headers.get("content-type") || "";
            const charsetMatch = contentType.match(/charset\s*=\s*([^;\s]+)/i);
            const charset = (charsetMatch?.[1] || "shift_jis").replace(/["']/g, "");
            try {
              return new TextDecoder(charset).decode(bytes);
            } catch {
              return new TextDecoder("shift_jis").decode(bytes);
            }
          };

          const isFiveChHost = (host) => /(^|\.)5ch\.(?:net|io)$/i.test(host || "");

          const parseThreadReference = (rawValue, baseUrl, expectedBoard, currentThreadId) => {
            if (!rawValue) {
              return null;
            }
            let raw = rawValue.trim();
            try {
              raw = decodeURIComponent(raw);
            } catch {
              // URL全体でない文字列はそのまま解析する。
            }

            const embeddedMatch = raw.match(
              /(?:https?:)?\/\/(?:[a-z0-9-]+\.)*5ch\.(?:net|io)\/test\/read\.cgi\/([^/?#]+)\/(\d+)/i);
            let candidateBoard = embeddedMatch?.[1] || "";
            let candidateThreadId = embeddedMatch?.[2] || "";
            if (!candidateThreadId) {
              try {
                const parsed = new URL(raw, baseUrl);
                if (!isFiveChHost(parsed.hostname)) {
                  return null;
                }
                const segments = parsed.pathname.split("/").filter(Boolean);
                if (segments[0]?.toLowerCase() !== "test"
                    || segments[1]?.toLowerCase() !== "read.cgi") {
                  return null;
                }
                candidateBoard = segments[2] || "";
                candidateThreadId = segments[3] || "";
              } catch {
                return null;
              }
            }

            if (candidateBoard.toLowerCase() !== expectedBoard.toLowerCase()
                || !/^\d+$/.test(candidateThreadId)) {
              return null;
            }
            const numericThreadId = Number(candidateThreadId);
            if (!Number.isSafeInteger(numericThreadId)
                || numericThreadId <= currentThreadId
                || numericThreadId - currentThreadId > 7 * 24 * 60 * 60) {
              return null;
            }
            return {
              id: numericThreadId,
              url: new URL(
                `/test/read.cgi/${encodeURIComponent(expectedBoard)}/${candidateThreadId}/l50`,
                location.origin).href
            };
          };

          const findExplicitNextThread = (sourceDocument, threadBoard, currentThreadId) => {
            const candidates = [];
            const baseUrl = sourceDocument.querySelector("base")?.href || document.baseURI || location.href;
            sourceDocument.querySelectorAll(".post-content a, .message a").forEach((anchor) => {
              const values = [anchor.getAttribute("href") || "", anchor.textContent || ""];
              values.forEach((value) => {
                const candidate = parseThreadReference(value, baseUrl, threadBoard, currentThreadId);
                if (candidate) {
                  candidates.push(candidate);
                }
              });
            });
            return candidates.sort((left, right) => left.id - right.id)[0] || null;
          };

          const stripThreadListDecorations = (title) => (title || "")
            .normalize("NFKC")
            .toLowerCase()
            .replace(/^\s*\d+\s*:\s*/, "")
            .replace(/\s*[（(]\d+[）)]\s*$/, "")
            .trim();

          const normalizeThreadTitle = (title) => stripThreadListDecorations(title)
            .replace(/(?:★|☆|part)\s*\d+\s*$/gi, "")
            .replace(/[^\p{L}\p{N}]+/gu, "");

          const getThreadTitlePrefix = (title) => {
            const stripped = stripThreadListDecorations(title);
            const markerIndex = stripped.search(/(?:★|☆|part\s*\d+|\d{5,})/i);
            return normalizeThreadTitle(markerIndex >= 0 ? stripped.slice(0, markerIndex) : stripped);
          };

          const calculateTitleSimilarity = (currentTitle, candidateTitle) => {
            const currentNormalized = normalizeThreadTitle(currentTitle);
            const candidateNormalized = normalizeThreadTitle(candidateTitle);
            if (!currentNormalized || !candidateNormalized) {
              return 0;
            }
            if (currentNormalized === candidateNormalized) {
              return 1;
            }
            const currentPrefix = getThreadTitlePrefix(currentTitle);
            const candidatePrefix = getThreadTitlePrefix(candidateTitle);
            if (currentPrefix.length >= 6 && currentPrefix === candidatePrefix) {
              return 1;
            }
            if (currentNormalized.length < 2 || candidateNormalized.length < 2) {
              return 0;
            }
            const currentPairs = new Set();
            const candidatePairs = new Set();
            for (let index = 0; index < currentNormalized.length - 1; index += 1) {
              currentPairs.add(currentNormalized.slice(index, index + 2));
            }
            for (let index = 0; index < candidateNormalized.length - 1; index += 1) {
              candidatePairs.add(candidateNormalized.slice(index, index + 2));
            }
            let intersectionCount = 0;
            currentPairs.forEach((pair) => {
              if (candidatePairs.has(pair)) {
                intersectionCount += 1;
              }
            });
            return (2 * intersectionCount) / (currentPairs.size + candidatePairs.size);
          };

          const verifyExplicitNextThread = async (candidate, currentTitle) => {
            if (!candidate) {
              return null;
            }
            try {
              const response = await fetch(candidate.url, {
                method: "GET",
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "text/html" },
                signal: refreshAbortController?.signal
              });
              if (!response.ok) {
                return null;
              }
              const html = await decodeThreadResponse(response);
              const candidateDocument = new DOMParser().parseFromString(html, "text/html");
              const candidateTitle = candidateDocument.querySelector("#threadtitle")?.textContent || "";
              return calculateTitleSimilarity(currentTitle, candidateTitle) >= 0.72
                ? candidate
                : null;
            } catch (error) {
              if (error?.name === "AbortError") {
                throw error;
              }
              return null;
            }
          };

          const findNextThreadFromBoard = async (threadBoard, currentThreadId, currentTitle) => {
            const boardUrl = new URL(`/${encodeURIComponent(threadBoard)}/subback.html`, location.origin);
            const response = await fetch(boardUrl.href, {
              method: "GET",
              cache: "no-store",
              credentials: "same-origin",
              headers: { Accept: "text/html" },
              signal: refreshAbortController?.signal
            });
            if (!response.ok) {
              throw new Error(`HTTP ${response.status}`);
            }
            const html = await decodeThreadResponse(response);
            const boardDocument = new DOMParser().parseFromString(html, "text/html");
            const baseUrl = new URL(`/test/read.cgi/${encodeURIComponent(threadBoard)}/`, location.origin).href;
            const candidates = [];
            boardDocument.querySelectorAll("a[href]").forEach((anchor) => {
              const candidate = parseThreadReference(
                anchor.getAttribute("href") || "",
                baseUrl,
                threadBoard,
                currentThreadId);
              if (!candidate) {
                return;
              }
              const score = calculateTitleSimilarity(currentTitle, anchor.textContent || "");
              if (score >= 0.72) {
                candidates.push({ ...candidate, score });
              }
            });
            return candidates
              .sort((left, right) => right.score - left.score || left.id - right.id)[0]
              || null;
          };

          const tryOpenNextThread = async (sourceDocument, threadBoard, currentThreadId) => {
            const currentTitle = document.querySelector("#threadtitle")?.textContent || document.title || "";
            const explicitCandidate = findExplicitNextThread(sourceDocument, threadBoard, currentThreadId);
            const nextThread = await verifyExplicitNextThread(explicitCandidate, currentTitle)
              || await findNextThreadFromBoard(threadBoard, currentThreadId, currentTitle);
            if (!nextThread || disposed || nextThreadNavigationStarted) {
              return false;
            }
            nextThreadNavigationStarted = true;
            clearRefreshTimer();
            updateRefreshStatus("次スレへ移動", "active");
            const timer = window.setTimeout(() => {
              timers.delete(timer);
              if (!disposed) {
                location.assign(nextThread.url);
              }
            }, 600);
            timers.add(timer);
            return true;
          };

          const appendFetchedPosts = (remoteDocument) => {
            const currentPosts = getThreadPosts();
            const lastPost = currentPosts.at(-1);
            const container = lastPost?.parentElement;
            if (!lastPost || !container) {
              return 0;
            }

            const existingIds = new Set(currentPosts.map(getPostId).filter(Boolean));
            const remotePosts = [...remoteDocument.querySelectorAll(".post")];
            const newRemotePosts = remotePosts.filter((post) => {
              const postId = getPostId(post);
              if (!postId || existingIds.has(postId)) {
                return false;
              }
              existingIds.add(postId);
              return true;
            });
            if (newRemotePosts.length === 0) {
              return 0;
            }

            const insertionPoint = lastPost.nextSibling;
            newRemotePosts.forEach((remotePost) => {
              const importedPost = document.importNode(remotePost, true);
              importedPost.querySelectorAll("script").forEach((script) => script.remove());
              container.insertBefore(importedPost, insertionPoint);
            });
            return newRemotePosts.length;
          };

          async function refreshPosts() {
            refreshTimer = 0;
            if (disposed
                || !settings.autoRefresh
                || !refreshActive
                || nextThreadNavigationStarted
                || (threadFinished && !settings.autoOpenNextThread)
                || refreshInFlight
                || document.visibilityState !== "visible") {
              return;
            }

            const pathSegments = location.pathname.split("/").filter(Boolean);
            const threadBoard = pathSegments[2] || "";
            const threadId = pathSegments[3] || "";
            if (!threadBoard || !/^\d+$/.test(threadId)) {
              updateRefreshStatus("自動更新停止", "error");
              return;
            }

            refreshInFlight = true;
            refreshAbortController = new AbortController();
            try {
              const refreshUrl = new URL(
                `/test/read.cgi/${encodeURIComponent(threadBoard)}/${encodeURIComponent(threadId)}/l50`,
                location.origin);
              const response = await fetch(refreshUrl.href, {
                method: "GET",
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "text/html" },
                signal: refreshAbortController.signal
              });
              if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
              }

              const html = await decodeThreadResponse(response);
              const remoteDocument = new DOMParser().parseFromString(html, "text/html");
              if (!remoteDocument.querySelector(".post")) {
                throw new Error("レス要素なし");
              }

              const addedCount = appendFetchedPosts(remoteDocument);
              threadFinished = Boolean(remoteDocument.querySelector(".stopdone"));
              refreshFailureCount = 0;
              const updatedAt = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
              refreshStatus.title = `最終更新 ${updatedAt}`;
              const openedNextThread = threadFinished
                && settings.autoOpenNextThread
                && await tryOpenNextThread(remoteDocument, threadBoard, Number(threadId));
              if (!openedNextThread) {
                updateRefreshStatus(
                  threadFinished
                    ? settings.autoOpenNextThread
                      ? "次スレ待機中"
                      : "スレッド終了"
                    : addedCount > 0
                      ? `${addedCount}件取得`
                      : `自動更新 ${settings.refreshIntervalSeconds}秒`,
                  threadFinished ? "done" : "active");
              }
            } catch (error) {
              if (error?.name !== "AbortError") {
                refreshFailureCount += 1;
                updateRefreshStatus("更新失敗・再試行", "error");
              }
            } finally {
              refreshInFlight = false;
              refreshAbortController = null;
              if (!disposed
                  && settings.autoRefresh
                  && refreshActive
                  && !nextThreadNavigationStarted
                  && (!threadFinished || settings.autoOpenNextThread)
                  && document.visibilityState === "visible") {
                const retryMultiplier = refreshFailureCount > 0
                  ? Math.min(2 ** refreshFailureCount, 12)
                  : 1;
                const baseDelaySeconds = threadFinished
                  ? Math.max(settings.refreshIntervalSeconds, 10)
                  : settings.refreshIntervalSeconds;
                scheduleRefresh(Math.min(baseDelaySeconds * retryMultiplier, 60));
              }
            }
          }

          const onVisibilityChange = () => {
            if (document.visibilityState === "visible" && refreshActive) {
              scheduleRefresh(0);
              return;
            }
            clearRefreshTimer();
            refreshAbortController?.abort();
          };

          newPostsButton.addEventListener("click", scrollToLatest);
          idFilterClear.addEventListener("click", () => applyIdFilter(""));
          replyPreview.addEventListener("mouseenter", onReplyPreviewMouseEnter);
          replyPreview.addEventListener("mouseleave", onReplyPreviewMouseLeave);
          replyPreview.addEventListener("click", onReplyPreviewClick);
          window.addEventListener("scroll", onScroll, { passive: true });
          document.addEventListener("visibilitychange", onVisibilityChange);
          document.addEventListener("mouseover", onMouseOver, true);
          document.addEventListener("mouseout", onMouseOut, true);
          document.addEventListener("click", onClick, false);

          if (settings.autoRefresh) {
            if (threadFinished) {
              if (settings.autoOpenNextThread) {
                updateRefreshStatus("次スレ確認中", "done");
                if (refreshActive) {
                  scheduleRefresh(0);
                }
              } else {
                updateRefreshStatus("スレッド終了", "done");
              }
            } else {
              updateRefreshStatus(`自動更新 ${settings.refreshIntervalSeconds}秒`);
              if (refreshActive) {
                scheduleRefresh(settings.refreshIntervalSeconds);
              }
            }
          }

          window[stateKey] = {
            setActive(value) {
              refreshActive = Boolean(value);
              if (refreshActive && document.visibilityState === "visible") {
                scheduleRefresh(0);
              } else {
                clearRefreshTimer();
                refreshAbortController?.abort();
              }
            },
            dispose() {
              if (disposed) {
                return;
              }
              disposed = true;
              clearRefreshTimer();
              refreshAbortController?.abort();
              mutationObserver.disconnect();
              readObserver.disconnect();
              timers.forEach((timer) => clearTimeout(timer));
              timers.clear();
              if (scrollFrame) {
                cancelAnimationFrame(scrollFrame);
              }
              window.removeEventListener("scroll", onScroll);
              replyPreview.removeEventListener("mouseenter", onReplyPreviewMouseEnter);
              replyPreview.removeEventListener("mouseleave", onReplyPreviewMouseLeave);
              replyPreview.removeEventListener("click", onReplyPreviewClick);
              document.removeEventListener("visibilitychange", onVisibilityChange);
              document.removeEventListener("mouseover", onMouseOver, true);
              document.removeEventListener("mouseout", onMouseOut, true);
              document.removeEventListener("click", onClick, false);
              cleanupDom();
            }
          };
        })();
        """;
}

public static class FiveChLiveReadabilityProfile
{
    public const int MinimumFontSize = 13;
    public const int MaximumFontSize = 22;
    public const int DefaultFontSize = 16;
    public const int MinimumRefreshIntervalSeconds = 3;
    public const int MaximumRefreshIntervalSeconds = 60;
    public const int DefaultRefreshIntervalSeconds = 5;
    public const int MinimumPostSpacing = 2;
    public const int MaximumPostSpacing = 20;
    public const int DefaultPostSpacing = 8;

    public static FiveChLiveReadabilitySettings Normalize(FiveChLiveReadabilitySettings? settings)
    {
        settings ??= new FiveChLiveReadabilitySettings();
        return new FiveChLiveReadabilitySettings
        {
            IsEnabled = settings.IsEnabled,
            HighlightNewPosts = settings.HighlightNewPosts,
            AutoRefresh = settings.AutoRefresh,
            RefreshIntervalSeconds = Math.Clamp(
                settings.RefreshIntervalSeconds,
                MinimumRefreshIntervalSeconds,
                MaximumRefreshIntervalSeconds),
            AutoOpenNextThread = settings.AutoOpenNextThread,
            AutoScroll = settings.AutoScroll,
            PreviewReplyAnchors = settings.PreviewReplyAnchors,
            HighlightPosterIds = settings.HighlightPosterIds,
            FontSize = Math.Clamp(settings.FontSize, MinimumFontSize, MaximumFontSize),
            PostSpacing = Math.Clamp(settings.PostSpacing, MinimumPostSpacing, MaximumPostSpacing)
        };
    }
}
