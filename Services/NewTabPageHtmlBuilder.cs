namespace TabBrowser.Services;

internal static class NewTabPageHtmlBuilder
{
    // 呼出元でJSON化した値を受け取り、表示本文だけを生成する。
    public static string Build(string bookmarkDataJson, string webMessageTokenJson)
    {
        return $$"""
            <!doctype html>
            <html lang="ja">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>新しいタブ</title>
                <style>
                    :root {
                        color-scheme: dark;
                        font-family: "Yu Gothic UI", "Meiryo", sans-serif;
                        background: #0f1115;
                        color: #f3f4f6;
                    }
                    * {
                        box-sizing: border-box;
                    }
                    body {
                        margin: 0;
                        min-height: 100vh;
                        background: #0f1115;
                    }
                    main {
                        max-width: 1040px;
                        margin: 0 auto;
                        padding: 44px 28px;
                    }
                    h1 {
                        margin: 0 0 18px;
                        font-size: 24px;
                        font-weight: 650;
                        letter-spacing: 0;
                    }
                    form {
                        display: grid;
                        grid-template-columns: 1fr auto;
                        gap: 8px;
                        margin-bottom: 28px;
                    }
                    input {
                        width: 100%;
                        height: 42px;
                        border: 1px solid #343842;
                        background: #181b22;
                        color: #f3f4f6;
                        padding: 0 14px;
                        font-size: 15px;
                        outline: none;
                    }
                    input:focus {
                        border-color: #8ab4f8;
                    }
                    button {
                        border: 1px solid #343842;
                        background: #23262d;
                        color: #f3f4f6;
                        cursor: pointer;
                    }
                    form button {
                        min-width: 82px;
                        height: 42px;
                        font-size: 14px;
                    }
                    .section-title {
                        display: flex;
                        align-items: center;
                        justify-content: space-between;
                        gap: 12px;
                        margin: 0 0 10px;
                        color: #d1d5db;
                        font-size: 13px;
                        font-weight: 650;
                    }
                    .section-count {
                        color: #9ca3af;
                        font-size: 12px;
                        font-weight: 500;
                    }
                    .bookmarks {
                        display: grid;
                        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
                        gap: 8px;
                    }
                    .bookmark {
                        min-width: 0;
                        height: 74px;
                        padding: 8px 10px;
                        text-align: left;
                        border-color: #303541;
                        background: #151a22;
                    }
                    .bookmark:hover,
                    .bookmark.is-selected {
                        border-color: #8ab4f8;
                        background: #1d2530;
                    }
                    .bookmark-menu {
                        position: fixed;
                        z-index: 20;
                        display: none;
                        min-width: 132px;
                        padding: 5px;
                        border: 1px solid #3a404d;
                        background: #151820;
                        box-shadow: 0 10px 22px rgba(0, 0, 0, 0.38);
                    }
                    .bookmark-menu.is-open {
                        display: block;
                    }
                    .bookmark-menu button {
                        width: 100%;
                        height: 30px;
                        padding: 0 10px;
                        text-align: left;
                        border-color: transparent;
                        background: transparent;
                        color: #fca5a5;
                        font-size: 13px;
                    }
                    .bookmark-menu button:hover {
                        background: #2a1b22;
                    }
                    .title {
                        overflow: hidden;
                        white-space: nowrap;
                        text-overflow: ellipsis;
                        font-size: 13px;
                        font-weight: 650;
                    }
                    .folder {
                        overflow: hidden;
                        white-space: nowrap;
                        text-overflow: ellipsis;
                        margin-top: 4px;
                        color: #c4b5fd;
                        font-size: 11px;
                    }
                    .url {
                        overflow: hidden;
                        white-space: nowrap;
                        text-overflow: ellipsis;
                        margin-top: 5px;
                        color: #9ca3af;
                        font-size: 11px;
                    }
                    .empty {
                        padding: 18px 0;
                        color: #9ca3af;
                        font-size: 13px;
                    }
                </style>
            </head>
            <body>
                <main>
                    <h1>新しいタブ</h1>
                    <form id="searchForm">
                        <input id="searchInput" autocomplete="off" autofocus placeholder="URL または検索語句">
                        <button type="submit">検索</button>
                    </form>
                    <div class="section-title">
                        <span>ブックマーク</span>
                        <span id="bookmarkCount" class="section-count"></span>
                    </div>
                    <div id="bookmarkList" class="bookmarks"></div>
                </main>
                <div id="bookmarkContextMenu" class="bookmark-menu">
                    <button id="deleteBookmarkButton" type="button">削除</button>
                </div>
                <script>
                    const webview = window.chrome && window.chrome.webview;
                    const webMessageToken = {{webMessageTokenJson}};
                    let bookmarks = {{bookmarkDataJson}};
                    const bookmarkLimit = 48;
                    const searchInput = document.getElementById("searchInput");
                    const bookmarkList = document.getElementById("bookmarkList");
                    const bookmarkCount = document.getElementById("bookmarkCount");
                    const bookmarkContextMenu = document.getElementById("bookmarkContextMenu");
                    const deleteBookmarkButton = document.getElementById("deleteBookmarkButton");
                    let visibleBookmarks = [];
                    let selectedBookmarkIndex = -1;
                    let contextBookmarkId = "";
                    let previewTimer = 0;

                    function postWebMessage(message) {
                        if (!webview || !message || typeof message !== "object") {
                            return;
                        }

                        webview.postMessage({ ...message, tabBrowserToken: webMessageToken });
                    }

                    function escapeHtml(value) {
                        return String(value || "")
                            .replaceAll("&", "&amp;")
                            .replaceAll("<", "&lt;")
                            .replaceAll(">", "&gt;")
                            .replaceAll('"', "&quot;")
                            .replaceAll("'", "&#39;");
                    }

                    function scoreBookmark(bookmark, query) {
                        const title = String(bookmark.title || "").toLowerCase();
                        const url = String(bookmark.url || "").toLowerCase();
                        const host = String(bookmark.host || "").toLowerCase();
                        const folderPath = String(bookmark.folderPath || "").toLowerCase();
                        let score = 0;

                        if (title.startsWith(query)) {
                            score += 120;
                        } else if (title.includes(query)) {
                            score += 80;
                        }

                        if (host.startsWith(query)) {
                            score += 100;
                        } else if (host.includes(query)) {
                            score += 65;
                        }

                        if (url.startsWith(query)) {
                            score += 90;
                        } else if (url.includes(query)) {
                            score += 55;
                        }

                        if (folderPath.startsWith(query)) {
                            score += 70;
                        } else if (folderPath.includes(query)) {
                            score += 45;
                        }

                        return score;
                    }

                    function renderBookmark(bookmark, index) {
                        const folder = bookmark.folderPath
                            ? `<div class="folder">${escapeHtml(bookmark.folderPath)}</div>`
                            : "";
                        return `
                            <button class="bookmark${index === selectedBookmarkIndex ? " is-selected" : ""}" type="button" data-bookmark-id="${escapeHtml(bookmark.id)}">
                                <div class="title">${escapeHtml(bookmark.title)}</div>
                                ${folder}
                                <div class="url">${escapeHtml(bookmark.url)}</div>
                            </button>`;
                    }

                    function renderBookmarks({ resetSelection = true } = {}) {
                        hideBookmarkPreview({ clearFrame: true });
                        const query = searchInput.value.trim().toLowerCase();
                        const matches = query
                            ? bookmarks
                                .map(bookmark => ({ ...bookmark, score: scoreBookmark(bookmark, query) }))
                                .filter(bookmark => bookmark.score > 0)
                                .sort((a, b) => b.score - a.score || b.rank - a.rank)
                            : bookmarks.slice();

                        visibleBookmarks = matches.slice(0, bookmarkLimit);
                        if (resetSelection || selectedBookmarkIndex >= visibleBookmarks.length) {
                            selectedBookmarkIndex = -1;
                        }

                        bookmarkList.innerHTML = visibleBookmarks.length
                            ? visibleBookmarks.map(renderBookmark).join("")
                            : `<div class="empty">${bookmarks.length === 0 ? "ブックマークはまだありません" : "一致するブックマークはありません"}</div>`;

                        bookmarkCount.textContent = query
                            ? `${visibleBookmarks.length} / ${matches.length} 件表示`
                            : `${visibleBookmarks.length} / ${bookmarks.length} 件表示`;
                    }

                    function selectBookmark(delta) {
                        if (visibleBookmarks.length === 0) {
                            return false;
                        }

                        if (selectedBookmarkIndex < 0) {
                            selectedBookmarkIndex = delta > 0 ? 0 : visibleBookmarks.length - 1;
                        } else {
                            selectedBookmarkIndex = Math.max(
                                0,
                                Math.min(visibleBookmarks.length - 1, selectedBookmarkIndex + delta));
                        }

                        renderBookmarks({ resetSelection: false });
                        bookmarkList.querySelector(".bookmark.is-selected")?.scrollIntoView({ block: "nearest" });
                        return true;
                    }

                    function openSelectedBookmark() {
                        if (!webview || selectedBookmarkIndex < 0 || selectedBookmarkIndex >= visibleBookmarks.length) {
                            return false;
                        }

                        postWebMessage({
                            type: "openBookmark",
                            id: visibleBookmarks[selectedBookmarkIndex].id
                        });
                        return true;
                    }

                    function hideBookmarkMenu() {
                        contextBookmarkId = "";
                        bookmarkContextMenu.classList.remove("is-open");
                    }

                    function showBookmarkMenu(bookmarkId, x, y) {
                        contextBookmarkId = bookmarkId || "";
                        bookmarkContextMenu.classList.add("is-open");
                        const width = bookmarkContextMenu.offsetWidth || 132;
                        const height = bookmarkContextMenu.offsetHeight || 40;
                        bookmarkContextMenu.style.left = Math.max(6, Math.min(x, window.innerWidth - width - 6)) + "px";
                        bookmarkContextMenu.style.top = Math.max(6, Math.min(y, window.innerHeight - height - 6)) + "px";
                    }

                    function hideBookmarkPreview({ clearFrame = false } = {}) {
                        window.clearTimeout(previewTimer);
                        previewTimer = 0;
                        if (webview) {
                            postWebMessage({
                                type: "hideBookmarkPreview",
                                clearNavigation: clearFrame
                            });
                        }
                    }

                    function showBookmarkPreview(bookmark, anchor) {
                        if (!bookmark || !bookmark.url || contextBookmarkId) {
                            return;
                        }

                        const rect = anchor.getBoundingClientRect();
                        if (webview) {
                            postWebMessage({
                                type: "showBookmarkPreview",
                                id: bookmark.id,
                                title: bookmark.title || bookmark.url,
                                url: bookmark.url,
                                left: rect.left,
                                top: rect.top,
                                right: rect.right,
                                bottom: rect.bottom
                            });
                        }
                    }

                    function queueBookmarkPreview(bookmarkId, anchor) {
                        hideBookmarkPreview();
                        const bookmark = bookmarks.find(item => item.id === bookmarkId);
                        if (!bookmark) {
                            return;
                        }

                        previewTimer = window.setTimeout(() => showBookmarkPreview(bookmark, anchor), 450);
                    }

                    document.getElementById("searchForm").addEventListener("submit", event => {
                        event.preventDefault();
                        if (openSelectedBookmark()) {
                            return;
                        }

                        const query = searchInput.value.trim();
                        if (query && webview) {
                            postWebMessage({ type: "newTabSearch", query });
                        }
                    });
                    searchInput.addEventListener("input", () => renderBookmarks());
                    searchInput.addEventListener("keydown", event => {
                        if (event.key === "ArrowDown") {
                            if (selectBookmark(1)) {
                                event.preventDefault();
                            }
                        } else if (event.key === "ArrowUp") {
                            if (selectBookmark(-1)) {
                                event.preventDefault();
                            }
                        } else if (event.key === "Escape" && selectedBookmarkIndex >= 0) {
                            selectedBookmarkIndex = -1;
                            renderBookmarks({ resetSelection: false });
                            event.preventDefault();
                        }
                    });
                    document.addEventListener("contextmenu", event => {
                        const button = event.target.closest("[data-bookmark-id]");
                        if (!button) {
                            hideBookmarkMenu();
                            return;
                        }

                        event.preventDefault();
                        event.stopPropagation();
                        hideBookmarkPreview({ clearFrame: true });
                        showBookmarkMenu(button.getAttribute("data-bookmark-id"), event.clientX, event.clientY);
                    }, true);
                    deleteBookmarkButton.addEventListener("click", event => {
                        event.preventDefault();
                        event.stopPropagation();
                        if (webview && contextBookmarkId) {
                            postWebMessage({
                                type: "deleteBookmark",
                                id: contextBookmarkId
                            });
                            bookmarks = bookmarks.filter(bookmark => bookmark.id !== contextBookmarkId);
                            renderBookmarks();
                        }

                        hideBookmarkMenu();
                    });
                    document.addEventListener("click", event => {
                        if (event.target.closest("#bookmarkContextMenu")) {
                            return;
                        }

                        hideBookmarkMenu();
                        hideBookmarkPreview({ clearFrame: true });
                        const button = event.target.closest("[data-bookmark-id]");
                        if (!button || !webview) {
                            return;
                        }

                        postWebMessage({
                            type: "openBookmark",
                            id: button.getAttribute("data-bookmark-id")
                        });
                    });
                    document.addEventListener("keydown", event => {
                        if (event.key === "Escape") {
                            hideBookmarkMenu();
                            hideBookmarkPreview({ clearFrame: true });
                        }
                    });
                    bookmarkList.addEventListener("mouseover", event => {
                        const button = event.target.closest("[data-bookmark-id]");
                        if (!button || !bookmarkList.contains(button)) {
                            return;
                        }

                        queueBookmarkPreview(button.getAttribute("data-bookmark-id"), button);
                    });
                    bookmarkList.addEventListener("mouseout", event => {
                        const button = event.target.closest("[data-bookmark-id]");
                        if (!button || button.contains(event.relatedTarget)) {
                            return;
                        }

                        hideBookmarkPreview();
                    });
                    window.addEventListener("scroll", () => {
                        hideBookmarkMenu();
                        hideBookmarkPreview({ clearFrame: true });
                    }, true);
                    window.addEventListener("resize", () => {
                        hideBookmarkMenu();
                        hideBookmarkPreview({ clearFrame: true });
                    });
                    renderBookmarks();
                </script>
            </body>
            </html>
            """;
    }
}
