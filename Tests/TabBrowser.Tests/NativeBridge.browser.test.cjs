// C#から生成されるJSのNode VM模擬試験。実メール・WebView2・外部通信は使わない。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
function script(file, method, messageType) {
    const text = readFileSync(resolve(__dirname, '../../' + file), 'utf8');
    const methodText = text.slice(text.indexOf('public static string ' + method + '('));
    const raw = methodText.match(/return \$\$"""\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
    return raw.replaceAll('{{serializedToken}}', '"fixture-token"')
        .replaceAll('{{tokenJson}}', '"fixture-token"')
        .replaceAll('{{messageTypeJson}}', JSON.stringify(messageType))
        .replaceAll('{{serializedMessageType}}', JSON.stringify(messageType))
        .replaceAll('{{serializedOrigin}}', '"https://example.invalid"')
        .replaceAll('{{MessageType}}', messageType)
        .replaceAll('{{(enablePeriodicSamples ? "true" : "false")}}', 'false')
        .replaceAll('{{(enableStateUpdates ? "true" : "false")}}', 'false')
        .replaceAll('{{periodicSampleSetup}}', '');
}
function host(hostname, path = '/', hasBridge = true) {
    const native = [], intercepted = [], handlers = new Map(), timers = [];
    class Video {
        error = { code: 3 }; currentTime = 1; duration = 100; buffered = { length: 0 };
        readyState = 0; networkState = 1; videoWidth = 640; videoHeight = 360;
        getBoundingClientRect() { return { width: 640, height: 360, left: 0, top: 0, right: 640, bottom: 360 }; }
    }
    const context = {
        location: { hostname, pathname: path, origin: 'https://' + hostname },
        document: {
            querySelector() { return null; }, visibilityState: 'visible',
            addEventListener(name, handler) { handlers.set(name, handler); },
            removeEventListener(name) { handlers.delete(name); }
        },
        HTMLVideoElement: Video, HTMLSourceElement: class {}, HTMLMediaElement: { HAVE_CURRENT_DATA: 2 },
        sessionStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
        chrome: hasBridge ? { webview: { postMessage(message) { native.push(message); } } } : {},
        innerWidth: 1024, innerHeight: 768,
        getComputedStyle() { return { display: 'block', visibility: 'visible' }; },
        setInterval(callback) { timers.push(callback); return timers.length; }, clearInterval() {},
        clearTimeout() {}, console
    };
    context.window = context; context.top = context; vm.createContext(context);
    return { context, native, intercepted, handlers, timers, Video };
}
for (const [file, method, key, messageType, hostname, event] of [
    ['Services/StreamingPlaybackCompatibilityService.cs', 'BuildFailureBridgeScript',
        '__tabBrowserStreamingPlaybackFailure', 'streamingPlaybackFailure', 'example.invalid', 'error'],
    ['Services/Diagnostics/YouTubePlaybackDiagnosticService.cs', 'BuildBridgeScript',
        '__tabBrowserYouTubePlaybackDiagnostic', 'youtubePlaybackDiagnostic', 'youtube.com', 'waiting']
]) {
    test(method + ': 公開markerへtokenを保持しない', () => {
        const h = host(hostname);
        vm.runInContext(script(file, method, messageType), h.context);
        assert.equal(h.context[key].token, undefined, 'ページからtokenを直接読める');
    });
    test(method + ': 通常イベントの送信は維持し、後からのhookには漏らさない', () => {
        const h = host(hostname);
        vm.runInContext(script(file, method, messageType), h.context);
        h.context.chrome.webview.postMessage = message => h.intercepted.push(message);
        h.handlers.get(event)({ type: event, target: new h.Video() });
        assert.equal(h.intercepted.length, 0);
        assert.equal(h.native.length, 1);
        assert.equal(h.native[0].type, messageType);
    });
}
for (const [file, method, type, hostname, path] of [
    ['Services/AmazonPrimeVideoService.cs', 'BuildWebViewBridgeSuppressionScript',
        'amazonPrimeVideoCompatibilityActive', 'amazon.co.jp', '/gp/video/watch/B012345678'],
    ['Services/StreamingPlaybackCompatibilityService.cs', 'BuildCompatibilityRetryScript',
        'streamingPlaybackCompatibilityActive', 'example.invalid', '/']
]) {
    test(method + ': 遅れて現れたページ定義bridgeへtokenを渡さない', () => {
        const h = host(hostname, path, false);
        vm.runInContext(script(file, method, type), h.context);
        h.context.chrome.webview = { postMessage: message => h.intercepted.push(message) };
        for (const tick of h.timers) tick();
        assert.equal(h.intercepted.length, 0);
    });
    test(method + ': 元のbridgeへの通知と隠蔽を維持する', () => {
        const h = host(hostname, path);
        vm.runInContext(script(file, method, type), h.context);
        assert.equal(h.native.length, 1);
        assert.equal(h.native[0].type, type);
        assert.equal(h.context.chrome.webview, undefined);
    });
}
test('Amazon対象外のサイトへ互換処理を適用しない', () => {
    const h = host('example.invalid', '/gp/video/watch/B012345678');
    vm.runInContext(script('Services/AmazonPrimeVideoService.cs',
        'BuildWebViewBridgeSuppressionScript', 'amazonPrimeVideoCompatibilityActive'), h.context);
    assert.equal(h.native.length, 0); assert.equal(h.timers.length, 0);
    assert.ok(h.context.chrome.webview);
});
