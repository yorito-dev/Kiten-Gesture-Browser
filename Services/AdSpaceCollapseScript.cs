using System.Text.Json;

namespace TabBrowser.Services;

internal static class AdSpaceCollapseScript
{
    internal static string Build(string documentUrl, string owner, bool enabled, bool dispose = false)
    {
        var urlJson = JsonSerializer.Serialize(documentUrl);
        var ownerJson = JsonSerializer.Serialize(owner);
        var enabledJson = enabled ? "true" : "false";
        var disposeJson = dispose ? "true" : "false";
        return $$"""
        (() => {
            if (window.top !== window || location.href !== {{urlJson}}) return;
            const owner = {{ownerJson}}, enabled = {{enabledJson}}, release = {{disposeJson}};
            const key = '__tabBrowserAdSpaceCollapse';
            const previous = window[key];
            if (release) {
                if (previous?.owner === owner) previous.dispose();
                return;
            }
            if (!enabled) {
                if (previous?.owner === owner) previous.dispose();
                return;
            }
            const host = location.hostname.toLowerCase().replace(/\.$/, '');
            if (!/^https?:$/.test(location.protocol) || ['youtube.com', 'youtube-nocookie.com', 'youtu.be']
                .some(domain => host === domain || host.endsWith('.' + domain))) return;
            if (!/^[a-z0-9-]{1,64}$/i.test(owner) || !document.documentElement
                || !CSS.supports('selector(:has(*))')) return;
            if (previous?.owner === owner) { previous.refresh(); return; }
            previous?.dispose();

            // サイト名では限定せず、広告専用の名前・属性を持つ空の要素だけを調べる。
            const selector = [
                '[data-ad-slot]', '[data-ad-unit]', '[data-ad-unit-id]', '[data-google-query-id]',
                '[id^="gpt-div-"]', '[id^="div-gpt-ad"]', '[id^="google_ads_iframe_"]',
                '.adsbygoogle', '.ad', '.ads', '.advertisement', '.advertising',
                '.ad-slot', '.ad-container', '.ad-wrapper', '.ad-placeholder', '.ad-unit',
                '.ad-banner', '.ad-area', '.ad-box', '.adbox', '.adunit',
                '#ad', '#ads', '#advertisement', '#ad-container', '#ad-wrapper', '#ad-slot', '#ad-banner'
            ].join(',');
            const protectedSelector = [
                'iframe', 'frame', 'video', 'audio', 'img', 'picture', 'svg', 'canvas', 'object', 'embed',
                'a[href]', 'button', 'input', 'select', 'textarea', 'form', 'main', 'article', 'nav', 'header', 'footer',
                'details', 'summary', 'progress', 'meter', 'label[for]', '[onclick]', '[tabindex]:not([tabindex="-1"])',
                '[contenteditable]:not([contenteditable="false"])', '[role="main"]', '[role="article"]',
                '[role="dialog"]', '[role="navigation"]', '[role="button"]', '[role="textbox"]',
                '[role="link"]', '[role="checkbox"]', '[role="radio"]', '[role="slider"]',
                '[role="switch"]', '[role="combobox"]', '[role="menuitem"]'
            ].join(',');
            const containerTags = new Set(['DIV', 'ASIDE', 'SECTION', 'INS']);
            const ignoredTags = new Set(['SCRIPT', 'STYLE', 'TEMPLATE', 'NOSCRIPT']);
            const labels = new Set(['ad', 'ads', 'advertisement', 'advertisements', 'advertising',
                'sponsored', 'sponsored links', '広告', '広告枠', 'スポンサーリンク', 'スポンサー', 'pr']);
            const isLabel = text => text.length <= 128 && (!text.trim() || labels.has(text.trim().toLowerCase()));
            const attribute = 'data-tabbrowser-ad-space-' + owner;
            const marked = new Set(), known = new Set(), dirty = new Set(), roots = new Set();
            let timer = 0, disposed = false, prune = false;
            const style = document.createElement('style');
            // フレーム等が後から入った瞬間もCSS側で保護し、次のJS通知を待って隠さない。
            style.textContent = `[${attribute}=""]:not(${protectedSelector}):not(:has(${protectedSelector})) { display: none !important; }`;
            (document.head || document.documentElement).append(style);

            function hasVisualContent(element) {
                const computed = getComputedStyle(element);
                if (computed.backgroundImage !== 'none' || computed.maskImage !== 'none') return true;
                for (const pseudo of ['::before', '::after']) {
                    const value = getComputedStyle(element, pseudo);
                    if (value.backgroundImage !== 'none' || value.maskImage !== 'none') return true;
                    const content = value.content;
                    if (content && content !== 'none' && content !== 'normal'
                        && !isLabel(content.replace(/^["']|["']$/g, ''))) return true;
                }
                return false;
            }
            function canCollapse(element) {
                if (!containerTags.has(element.tagName) || !element.matches(selector)
                    || element.matches(protectedSelector) || element.querySelector(protectedSelector)
                    || element.shadowRoot) return false;
                const walker = document.createTreeWalker(element, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
                    acceptNode(node) {
                        return node.nodeType === Node.ELEMENT_NODE && ignoredTags.has(node.tagName)
                            ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
                    }
                });
                if (hasVisualContent(element)) return false;
                let node, visited = 0;
                while ((node = walker.nextNode())) {
                    if (++visited > 160) return false;
                    if (node.nodeType === Node.TEXT_NODE) {
                        if (!isLabel(node.nodeValue || '')) return false;
                    } else if (node.shadowRoot || node.localName.includes('-') || hasVisualContent(node)) return false;
                }
                return true;
            }
            function restore(element) {
                if (marked.delete(element) && element.getAttribute(attribute) === '') element.removeAttribute(attribute);
            }
            function consider(element) {
                if (known.has(element) || known.size < 2048) { known.add(element); dirty.add(element); }
            }
            function collect(root) {
                if (!root.isConnected) return;
                if (root.matches(selector)) consider(root);
                for (const element of root.querySelectorAll(selector)) {
                    consider(element);
                    if (known.size >= 2048) break;
                }
            }
            function queueAncestors(node) {
                let element = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
                for (let depth = 0; element && depth < 16; depth++, element = element.parentElement) {
                    if (known.has(element) || element.matches(selector)) consider(element);
                }
            }
            function schedule() {
                if (!disposed && !timer && (roots.size || dirty.size || prune)) timer = setTimeout(flush, 120);
            }
            function flush() {
                timer = 0;
                if (disposed) return;
                // 消えた要素を保持し続けず、非広告部分の変更でページ全体を再検索しない。
                if (prune) {
                    prune = false;
                    for (const element of known) if (!element.isConnected) {
                        restore(element); known.delete(element); dirty.delete(element);
                    }
                }
                for (const root of roots) collect(root);
                roots.clear();
                let processed = 0;
                for (const element of dirty) {
                    dirty.delete(element);
                    if (element.isConnected && canCollapse(element)) {
                        if (!element.hasAttribute(attribute)) { element.setAttribute(attribute, ''); marked.add(element); }
                    } else restore(element);
                    if (++processed >= 200) break;
                }
                schedule();
            }
            const observer = new MutationObserver(records => {
                if (disposed) return;
                for (const record of records) {
                    queueAncestors(record.target);
                    if (record.type === 'childList') {
                        if (record.removedNodes.length) prune = true;
                        for (const node of record.addedNodes) {
                            if (node.nodeType === Node.ELEMENT_NODE && !ignoredTags.has(node.tagName) && roots.size < 64) roots.add(node);
                        }
                    }
                }
                schedule();
            });
            function refresh() {
                if (disposed) return;
                for (const element of known) dirty.add(element);
                schedule();
            }
            function loaded(event) { if (event.target?.tagName === 'LINK') refresh(); }
            function dispose() {
                if (disposed) return;
                disposed = true;
                clearTimeout(timer); timer = 0;
                observer.disconnect();
                window.removeEventListener('resize', refresh);
                window.removeEventListener('pagehide', dispose);
                document.removeEventListener('load', loaded, true);
                for (const element of marked) restore(element);
                known.clear(); dirty.clear(); roots.clear();
                style.remove();
                if (window[key] === api) delete window[key];
            }
            const api = { owner, refresh, dispose };
            window[key] = api;
            observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true,
                attributes: true, attributeFilter: ['id', 'class', 'style', 'src', 'role', 'contenteditable', 'tabindex', 'onclick',
                    'data-ad-slot', 'data-ad-unit', 'data-ad-unit-id', 'data-google-query-id'] });
            window.addEventListener('resize', refresh);
            window.addEventListener('pagehide', dispose);
            document.addEventListener('load', loaded, true);
            roots.add(document.documentElement);
            flush();
        })();
        """;
    }
}
