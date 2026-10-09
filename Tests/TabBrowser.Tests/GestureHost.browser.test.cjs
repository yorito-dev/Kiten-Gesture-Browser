// Node VM上のDOM模擬試験。実WebView2／実サイトへ接続しない。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const source = readFileSync(resolve(__dirname, '../../Scripts/GestureHost.js'), 'utf8');

function createHost(bridgeInitiallyPresent = true) {
    const native = [], intercepted = [];
    const bridge = { postMessage(message) { native.push(message); } };
    const context = {
        document: { addEventListener() {}, removeEventListener() {}, baseURI: 'https://example.invalid/' },
        location: { hostname: 'example.invalid' },
        addEventListener() {}, removeEventListener() {},
        chrome: bridgeInitiallyPresent ? { webview: bridge } : {},
        console
    };
    context.window = context;
    vm.createContext(context);
    vm.runInContext('(() => { const __tabBrowserWebMessageToken = "fixture-token";'
        + 'const __tabBrowserGestureSettings = {}; const __tabBrowserGestureCommands = [];'
        + source + '})();', context);
    return { context, native, intercepted };
}

test('通常の送信は認証tokenをネイティブへ渡す', () => {
    const { context, native } = createHost();
    context.__tabBrowserPostYouTubePlaybackState({ videoId: 'fixture-id', position: 12 });
    assert.equal(native.length, 1);
    assert.equal(native[0].type, 'youtubePlaybackState');
    assert.equal(native[0].tabBrowserToken, 'fixture-token');
});

for (const replacement of ['method', 'bridge', 'chrome']) {
    test('注入後の' + replacement + '差替えへtokenを渡さない', () => {
        const { context, native, intercepted } = createHost();
        const spy = message => intercepted.push(message);
        if (replacement === 'method') context.chrome.webview.postMessage = spy;
        else if (replacement === 'bridge') context.chrome.webview = { postMessage: spy };
        else context.chrome = { webview: { postMessage: spy } };
        context.__tabBrowserPostYouTubePlaybackState({ position: 12 });
        assert.equal(intercepted.length, 0, 'ページが差し替えた送信先への漏出');
        assert.equal(native.length, 1);
    });
}

test('注入時にbridgeが無い場合は後から追加された送信先を使わない', () => {
    const { context, intercepted } = createHost(false);
    context.chrome.webview = { postMessage: message => intercepted.push(message) };
    context.__tabBrowserPostYouTubePlaybackState({ position: 12 });
    assert.equal(intercepted.length, 0);
});

test('設定の再注入でも元の送信先と既存の更新関数を維持する', () => {
    const { context, native, intercepted } = createHost();
    const update = context.__tabBrowserGestureHostUpdate;
    context.chrome.webview.postMessage = message => intercepted.push(message);
    vm.runInContext('(() => { const __tabBrowserWebMessageToken = "fixture-token";'
        + 'const __tabBrowserGestureSettings = { gestureStartThreshold: 20 };'
        + 'const __tabBrowserGestureCommands = [];'+ source + '})();', context);
    assert.equal(context.__tabBrowserGestureHostUpdate, update);
    context.__tabBrowserPostYouTubePlaybackState({ position: 20 });
    assert.equal(intercepted.length, 0);
    assert.equal(native.length, 1);
    assert.equal(native[0].tabBrowserToken, 'fixture-token');
});
