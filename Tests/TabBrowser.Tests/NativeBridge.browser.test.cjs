// C#から生成されるJSのNode VM模擬試験。実メール・WebView2・外部通信は使わない。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync, existsSync, readdirSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
// 公開ソースは既存の来歴ファイルで識別し、除外機能はskipせず不存在を検査する。
const sourceRoot = resolve(__dirname, '../..');
const provenancePath = resolve(sourceRoot, 'SOURCE-PROVENANCE.json');
const isPublicSource = existsSync(provenancePath)
    && JSON.parse(readFileSync(provenancePath, 'utf8').replace(/^\uFEFF/, '')).kind === 'public-source-snapshot';
const excludedMethods = new Set(['BuildWebViewBridgeSuppressionScript', 'BuildCompatibilityRetryScript']);
function isExcluded(method) { return isPublicSource && excludedMethods.has(method); }
function assertMethodExcluded(file, method) { assert.equal(readFileSync(resolve(sourceRoot, file), 'utf8').includes('public static string ' + method + '('), false, '除外対象の実装が残っている: ' + method); }
function assertHookExcluded(method) { assert.equal(readFileSync(resolve(sourceRoot, 'MainWindow.xaml.cs'), 'utf8').includes(method + '('), false, '除外対象の呼出しが残っている: ' + method); }
function productServiceText(directory) {
    return readdirSync(directory, { withFileTypes: true }).map(entry => {
        const path = resolve(directory, entry.name);
        return entry.isDirectory() ? productServiceText(path) : entry.name.endsWith('.cs') ? readFileSync(path, 'utf8') : '';
    }).join('\n');
}
function script(file, method, messageType, values = {}) {
    const text = readFileSync(resolve(__dirname, '../../' + file), 'utf8');
    const methodText = text.slice(text.indexOf('public static string ' + method + '('));
    const raw = methodText.match(/return (?:\$\$)?"""\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
    const adExpression = readFileSync(resolve(sourceRoot, 'Services/YouTubeControlScriptService.cs'), 'utf8')
        .match(/const string AdvertisementPlayingExpression = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
    return raw.replaceAll('{{adPlayingExpression}}', adExpression).replaceAll('{{serializedToken}}', '"fixture-token"')
        .replaceAll('{{enabledJson}}', JSON.stringify(values.enabled ?? true))
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
    test(method + (isExcluded(method) ? ': 公開ソースに除外対象の実装がない' : ': 公開markerへtokenを保持しない'), () => {
        if (isExcluded(method)) { assertMethodExcluded(file, method); return; }
        const h = host(hostname);
        vm.runInContext(script(file, method, messageType), h.context);
        assert.equal(h.context[key].token, undefined, 'ページからtokenを直接読める');
    });
    test(method + (isExcluded(method) ? ': 公開ソースに除外対象の登録がない' : ': 通常イベントの送信は維持し、後からのhookには漏らさない'), () => {
        if (isExcluded(method)) { assertHookExcluded(method); return; }
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
    test(method + (isExcluded(method) ? ': 公開ソースにbridge削除の実装がない' : ': 遅れて現れたページ定義bridgeへtokenを渡さない'), () => {
        if (isExcluded(method)) { assertMethodExcluded(file, method); return; }
        const h = host(hostname, path, false);
        vm.runInContext(script(file, method, type), h.context);
        h.context.chrome.webview = { postMessage: message => h.intercepted.push(message) };
        for (const tick of h.timers) tick();
        assert.equal(h.intercepted.length, 0);
    });
    test(method + (isExcluded(method) ? ': 公開ソースに互換処理の呼出しがない' : ': 元のbridgeへの通知と隠蔽を維持する'), () => {
        if (isExcluded(method)) { assertHookExcluded(method); return; }
        const h = host(hostname, path);
        vm.runInContext(script(file, method, type), h.context);
        assert.equal(h.native.length, 1);
        assert.equal(h.native[0].type, type);
        assert.equal(h.context.chrome.webview, undefined);
    });
}
test(isPublicSource ? '公開ソースにWebView bridgeの削除スクリプトがない' : 'Amazon対象外のサイトへ互換処理を適用しない', () => {
    if (isPublicSource) {
        assert.equal(productServiceText(resolve(sourceRoot, 'Services')).includes('delete globalThis.chrome.webview'), false);
        return;
    }
    const h = host('example.invalid', '/gp/video/watch/B012345678');
    vm.runInContext(script('Services/AmazonPrimeVideoService.cs',
        'BuildWebViewBridgeSuppressionScript', 'amazonPrimeVideoCompatibilityActive'), h.context);
    assert.equal(h.native.length, 0); assert.equal(h.timers.length, 0);
    assert.ok(h.context.chrome.webview);
});

// 失敗通知とPrimeの準備確認は、WebView bridgeを削除せずに実行できる。
for (const hostname of ['example.invalid', 'www.amazon.co.jp']) {
    test('映像エラーを通知してbridgeを維持する: ' + hostname, () => {
        const h = host(hostname);
        const bridge = h.context.chrome.webview;
        vm.runInContext(script('Services/StreamingPlaybackCompatibilityService.cs', 'BuildFailureBridgeScript', 'streamingPlaybackFailure'), h.context);
        const video = new h.Video();
        h.handlers.get('error')({ target: video });
        h.handlers.get('error')({ target: video });
        assert.equal(h.native.length, 1);
        assert.equal(h.native[0].type, 'streamingPlaybackFailure');
        assert.equal(h.native[0].tabBrowserToken, 'fixture-token');
        assert.equal(h.context.chrome.webview, bridge);
        assert.equal(h.timers.length, 0);
    });
}
test('Prime準備確認はbridge削除を要求せず、待機表示とDRM APIを読み取る', () => {
    const h = host('www.amazon.co.jp');
    const bridge = h.context.chrome.webview;
    const button = { getAttribute: () => 'true', hasAttribute: () => false };
    const spinner = { closest: () => button, getClientRects: () => [{}] };
    h.context.document.querySelector = selector => selector.includes('spinner') ? spinner : button;
    h.context.navigator = { requestMediaKeySystemAccess() {} };
    const result = vm.runInContext(script('Services/AmazonPrimeVideoService.cs', 'BuildReadinessProbeScript', ''), h.context);
    assert.equal(result.primeVideoPageFound, true);
    assert.equal(result.spinnerVisible, true);
    assert.equal(result.ariaDisabled, true);
    assert.equal(result.emeAvailable, true);
    assert.equal(h.context.chrome.webview, bridge);
    assert.equal(h.native.length, 0);
    assert.equal(h.timers.length, 0);
});

// 保存状態の復元は、実際のC#のJS本文を使って音量・ミュートの順序を確認する。
function miniPlayerScript(method, values) {
    const source = readFileSync(resolve(__dirname, '../../Services/YouTubeControlScriptService.cs'), 'utf8');
    const start = source.indexOf('static string ' + method + '(');
    assert.ok(start >= 0, method);
    const raw = source.slice(start).match(/return (?:\$\$)?"""\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
    values = { AdvertisementPlayingExpression: source.match(/const string AdvertisementPlayingExpression = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1], ...values };
    return raw.replace(/\{\{(\w+)\}\}/g, (_, key) => {
        assert.ok(Object.hasOwn(values, key), method + ': ' + key);
        return values[key];
    });
}
function muteRestoreHost({ muted = true, savedMuted = true, userOverride = false, playbackUnmutes = false,
    fullPlayback = false } = {}) {
    const handlers = new Map(), transitions = [], adClasses = new Set(), adObservers = new Set();
    let currentMuted = muted;
    const video = {
        readyState: 2, paused: false, volume: 0.4, playbackRate: 1, loop: false, duration: 100, currentTime: 0,
        setAttribute() {}, removeAttribute() {}, addEventListener() {}, removeEventListener() {},
        get muted() { return currentMuted; },
        set muted(value) { currentMuted = value; transitions.push(value); }
    };
    const player = { classList: { contains: name => adClasses.has(name) }, mute() { video.muted = true; }, unMute() { video.muted = false; },
        setVolume(value) { video.volume = value / 100; }, getVideoData() { return { video_id: 'fixture-video' }; },
        seekTo(value) { video.currentTime = value; if (playbackUnmutes) video.muted = false; } };
    const context = {
        document: {
            getElementById() { return player; },
            querySelector(selector) { return selector === 'video' ? (context.videoAvailable ? video : null) : player; },
            addEventListener(name, handler) { handlers.set(name, handler); },
            removeEventListener(name) { handlers.delete(name); }
        },
        location: { protocol: 'https:', hostname: 'www.youtube.com', href: 'https://www.youtube.com/watch?v=fixture-video' },
        MutationObserver: class { constructor(callback) { this.callback = callback; }
            observe() { adObservers.add(this); } disconnect() { adObservers.delete(this); } },
        URL, HTMLMediaElement: { HAVE_METADATA: 1 }, videoAvailable: true,
        __tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard: { audio: userOverride },
        playbackRestore() { if (playbackUnmutes) video.muted = false; return { ok: true, found: true }; }
    };
    context.window = context;
    vm.createContext(context);
    const volumeScript = miniPlayerScript('BuildVolumeScript', {
        volumeText: '0.4', markUserOverrideText: 'false', respectUserOverrideText: 'true',
        unmuteOnVolumeChangeText: 'false'
    });
    const muteScript = miniPlayerScript('BuildMuteScript', {
        targetText: String(savedMuted), markUserOverrideText: 'false', respectUserOverrideText: 'true'
    });
    const playbackScript = fullPlayback ? miniPlayerScript('BuildPlaybackStateRestoreScript', {
        positionText: '30', speedText: '1.5', pauseText: 'false', loopingText: 'false',
        theaterModeText: 'null', respectUserOverrideText: 'true'
    }) : 'window.playbackRestore()';
    const initialLivePlaybackScript = fullPlayback ? miniPlayerScript('BuildInitialLivePlaybackScript', {
        seekToLiveEdgeScript: miniPlayerScript('BuildSeekToLiveEdgeScript', {})
    }) : '({ ok: true, skipped: true })';
    const abRepeatScript = fullPlayback ? miniPlayerScript('BuildAbRepeatScript', {
        startJson: '10', endJson: '20', enabledJson: 'true', wholeLoopingJson: 'false', videoIdJson: '"fixture-video"'
    }) : '({ ok: true, skipped: true })';
    const restoreScript = miniPlayerScript('BuildMiniPlayerSavedStateReadyBridgeScript', {
        videoIdJson: '"fixture-video"', requestKeyJson: '"fixture-request"', volumeScript, muteScript,
        playbackScript, initialLivePlaybackScript, abRepeatScript
    });
    return { context, video, player, handlers, transitions, volumeScript, restoreScript, adClasses, adObservers };
}
test('保存音量を復元してもミュートを一瞬解除しない', async () => {
    const h = muteRestoreHost();
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.video.muted, true);
    assert.ok(!h.transitions.includes(false), '音量復元でミュートが解除された');
});
test('再生位置やライブ再開の処理が音声を有効にしても最後に保存ミュートへ戻す', async () => {
    const h = muteRestoreHost({ playbackUnmutes: true });
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.video.muted, true);
    assert.equal(h.context.__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge.applied, true);
});
test('復元中に利用者が解除したミュートを保存値で上書きしない', async () => {
    const h = muteRestoreHost({ muted: false, userOverride: true });
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.video.muted, false);
    assert.deepEqual(h.transitions, []);
});
test('通常の音量変更では従来どおりミュートを解除する', () => {
    const h = muteRestoreHost();
    const manualScript = miniPlayerScript('BuildVolumeScript', {
        volumeText: '0.6', markUserOverrideText: 'true', respectUserOverrideText: 'false',
        unmuteOnVolumeChangeText: 'true'
    });
    const result = vm.runInContext(manualScript, h.context);
    assert.equal(result.muted, false);
    assert.equal(h.video.volume, 0.6);
});
test('全復元スクリプトの合成を実行でき、再生位置・速度・ABリピートとミュートを復元する', async () => {
    const h = muteRestoreHost({ fullPlayback: true, playbackUnmutes: true });
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.context.__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge.applied, true);
    assert.equal(h.video.currentTime, 30);
    assert.equal(h.video.playbackRate, 1.5);
    assert.equal(h.context.__tabBrowserYouTubeMiniPlayerAbRepeat.enabled, true);
    assert.equal(h.video.muted, true);
});
test('復元の途中で利用者がミュートを解除した場合も最後の再適用で上書きしない', async () => {
    const h = muteRestoreHost();
    h.context.playbackRestore = () => {
        h.context.__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard.audio = true;
        h.video.muted = false;
        return { ok: true, found: true };
    };
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.video.muted, false);
});
test('最後のミュート適用時に動画が消えたら完了扱いにせず準備イベントから復元を再試行する', async () => {
    const h = muteRestoreHost();
    h.context.playbackRestore = () => {
        h.context.videoAvailable = false;
        return { ok: true, found: true };
    };
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    const bridge = h.context.__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge;
    assert.equal(bridge.applied, false);
    assert.ok(h.handlers.has('canplay'));
    h.context.videoAvailable = true;
    h.context.playbackRestore = () => ({ ok: true, found: true });
    await h.handlers.get('canplay')();
    assert.equal(bridge.applied, true);
    assert.equal(h.video.muted, true);
    assert.equal(h.handlers.size, 0);
});
test('保存状態が音声ありならミュートへ変更しない', async () => {
    const h = muteRestoreHost({ muted: false, savedMuted: false });
    vm.runInContext(h.restoreScript, h.context);
    await new Promise(setImmediate);
    assert.equal(h.video.muted, false);
    assert.equal(h.context.__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge.applied, true);
});

// ライブ継続枠の通信・時計・ページは架空のものだけを使う。
// 広告状態のclassだけを変えた場合も、本編IDを確認して一度だけ復元する。
for (const adClass of ['ad-showing', 'ad-interrupting']) {
    for (const normalTab of [false, true]) {
        test(`広告中は保存状態を適用せず、本編で一度だけ復元する: ${adClass}/${normalTab ? 'tab' : 'mini'}`, async () => {
            const h = muteRestoreHost({ fullPlayback: true });
            h.adClasses.add(adClass);
            let seeks = 0;
            h.player.seekTo = value => { seeks++; h.video.currentTime = value; };
            const restore = miniPlayerScript('BuildPlaybackStateRestoreScript', {
                positionText: '30', speedText: '1.5', pauseText: 'false', loopingText: 'true',
                theaterModeText: 'null', respectUserOverrideText: 'true'
            });
            const code = normalTab ? miniPlayerScript('BuildPlaybackStateRestoreReadyBridgeScript', {
                webMessageTokenJson: '"test-token"', videoIdJson: '"fixture-video"', requestKeyJson: '"normal"',
                theaterModeText: 'null', restoreScript: restore
            }) : h.restoreScript;
            h.context.chrome = { webview: { postMessage() {} } };
            vm.runInContext(code, h.context);
            await new Promise(setImmediate);
            assert.equal(seeks, 0); assert.equal(h.video.currentTime, 0);
            assert.equal(h.video.playbackRate, 1); assert.equal(h.video.loop, false);
            // 広告classが消えても、playerが広告の動画IDの間は待つ。
            h.adClasses.clear(); h.player.getVideoData = () => ({ video_id: 'advertisement' });
            for (const observer of [...h.adObservers]) observer.callback();
            await new Promise(setImmediate); assert.equal(seeks, 0);
            h.player.getVideoData = () => ({ video_id: 'fixture-video' });
            h.handlers.get('playing')?.();
            await new Promise(setImmediate);
            assert.equal(seeks, 1); assert.equal(h.video.currentTime, 30); assert.equal(h.video.playbackRate, 1.5);
            h.handlers.get('canplay')?.(); h.handlers.get('playing')?.();
            await new Promise(setImmediate); assert.equal(seeks, 1);
        });
    }
    test('広告中の速度・各シークは何も変更せず理由を返す: ' + adClass, () => {
        const h = muteRestoreHost(); h.adClasses.add(adClass);
        for (const [method, values] of [
            ['BuildSpeedScript', { deltaText: '0.25', targetText: 'null' }],
            ['BuildSeekRelativeScript', { deltaText: '10' }],
            ['BuildSeekToRatioScript', { ratioText: '0.5' }],
            ['BuildSeekToLiveEdgeScript', {}],
            ['BuildPlaybackStateRestoreScript', { positionText: '30', speedText: '1.5', pauseText: 'true',
                loopingText: 'true', theaterModeText: 'true', respectUserOverrideText: 'false' }]
        ]) {
            const valuesWithBounds = { 'MinimumSpeed.ToString(CultureInfo.InvariantCulture)': '0.25',
                'MaximumSpeed.ToString(CultureInfo.InvariantCulture)': '2', ...values };
            // C#の数値定数を展開し、それ以外は既存の抽出器で実スクリプトを使う。
            let code = miniPlayerScript(method, valuesWithBounds)
                .replaceAll('{{MinimumSpeed.ToString(CultureInfo.InvariantCulture)}}', '0.25')
                .replaceAll('{{MaximumSpeed.ToString(CultureInfo.InvariantCulture)}}', '2');
            const result = vm.runInContext(code, h.context);
            assert.equal(result.ok, false, method); assert.equal(result.reason, 'advertisement', method);
            assert.equal(h.video.currentTime, 0, method); assert.equal(h.video.playbackRate, 1, method);
            assert.equal(h.video.loop, false, method); assert.equal(h.video.paused, false, method);
        }
    });
    test('広告中も再生・一時停止・音量・ミュートを使える: ' + adClass, async () => {
        const h = muteRestoreHost({ muted: false }); h.adClasses.add(adClass);
        h.video.pause = () => { h.video.paused = true; };
        h.video.play = () => { h.video.paused = false; return Promise.resolve(); };
        assert.equal((await vm.runInContext(miniPlayerScript('BuildPlayPauseScript', {}), h.context)).ok, true);
        assert.equal(h.video.paused, true);
        assert.equal((await vm.runInContext(miniPlayerScript('BuildPlayPauseScript', {}), h.context)).ok, true);
        assert.equal(h.video.paused, false);
        vm.runInContext(h.volumeScript, h.context); assert.equal(h.video.volume, 0.4);
        vm.runInContext(miniPlayerScript('BuildMuteScript', { targetText: 'true', markUserOverrideText: 'true',
            respectUserOverrideText: 'false' }), h.context); assert.equal(h.video.muted, true);
    });
}

function continuationHost() {
    const events = new Map(), timers = new Map(), posts = [], requests = [], adClasses = new Set();
    const channelId = 'UC' + 'a'.repeat(22);
    const response = (id = 'oldLive1234') => ({
        videoDetails: { videoId: id, channelId, isLiveContent: true },
        microformat: { playerMicroformatRenderer: { liveBroadcastDetails: {
            isLiveNow: true, startTimestamp: '2026-01-01T00:00:00Z' } } },
        playabilityStatus: { status: 'OK' }
    });
    const video = { ended: false, loop: false, muted: true, volume: 0.3 };
    let playerState = 1, timerId = 0, onState;
    const player = { querySelector: () => video, classList: { contains: name => adClasses.has(name) },
        getPlayerResponse: () => h.currentResponse, getPlayerState: () => playerState,
        addEventListener(name, handler) { if (name === 'onStateChange') onState = handler; },
        removeEventListener() { onState = null; } };
    const add = (name, handler) => {
        if (!events.has(name)) events.set(name, new Set());
        events.get(name).add(handler);
    };
    const remove = (name, handler) => events.get(name)?.delete(handler);
    const context = {
        location: { protocol: 'https:', hostname: 'www.youtube.com', href: 'https://www.youtube.com/watch?v=oldLive1234' },
        document: { getElementById: () => player, querySelector: () => player,
            addEventListener: add, removeEventListener: remove },
        addEventListener: add, removeEventListener: remove, URL, Date,
        setTimeout(callback, ms) { const id = ++timerId; timers.set(id, { callback, ms }); return id; },
        clearTimeout(id) { timers.delete(id); },
        chrome: { webview: { postMessage: value => posts.push(value) } },
        fetch(...args) { requests.push(args); throw new Error('継続bridgeはfetchを呼んではならない'); }
    };
    const h = { context, events, timers, posts, requests, adClasses, video, player, response,
        currentResponse: response(),
        fire(name, target = video) { for (const handler of [...events.get(name) || []]) handler({ target }); },
        end() { video.ended = true; playerState = 0; h.fire('ended'); },
        playerEnd() { playerState = 0; onState(0); },
        replay() { video.ended = false; playerState = 1; h.fire('play'); },
        async tick(ms) {
            const entry = [...timers].find(([, timer]) => timer.ms === ms);
            assert.ok(entry, 'timer: ' + ms);
            timers.delete(entry[0]); await entry[1].callback(); await new Promise(setImmediate);
        },
        install(enabled = true) { return vm.runInContext(script('Services/YouTubeMiniPlayerWebViewService.cs',
            'BuildLiveContinuationBridgeScript', '', { enabled }), context); }
    };
    context.window = context; context.top = context; vm.createContext(context);
    return h;
}

test('ライブ終了2秒後にチャンネルIDだけを一度通知し、fetchもHTML解析もしない', async () => {
    const h = continuationHost(); h.install();
    assert.equal(h.timers.size, 0); h.end(); h.fire('ended'); h.install();
    assert.equal(h.posts.length, 0); assert.equal(h.timers.size, 1);
    await h.tick(2000);
    assert.equal(h.posts.length, 1); assert.equal(h.posts[0].type, 'youtubeMiniPlayerLiveContinuation');
    assert.equal(h.posts[0].channelId, h.currentResponse.videoDetails.channelId);
    assert.equal(h.posts[0].sourceVideoId, 'oldLive1234'); assert.equal(h.posts[0].token, 'fixture-token');
    assert.equal(Object.hasOwn(h.posts[0], 'nextVideoId'), false); assert.equal(Object.hasOwn(h.posts[0], 'url'), false);
    h.fire('ended'); h.fire('canplay'); h.install();
    assert.equal(h.posts.length, 1); assert.equal(h.requests.length, 0); assert.equal(h.timers.size, 0);
    assert.equal(h.video.muted, true); assert.equal(h.video.volume, 0.3);
    const code = script('Services/YouTubeMiniPlayerWebViewService.cs', 'BuildLiveContinuationBridgeScript', '');
    assert.doesNotMatch(code, /\bfetch\s*\(|readResponse|JSON\.parse|10000|AbortController/);
});

for (const mode of ['通常動画', 'アーカイブ', '予約枠', '別動画の古いメタデータ', '未来の開始時刻', '不正チャンネル', 'チャンネルなし']) {
    test('ライブ終了通知を開始しない: ' + mode, () => {
        const h = continuationHost();
        if (mode === '通常動画') h.currentResponse = { videoDetails: { videoId: 'oldLive1234', channelId: h.currentResponse.videoDetails.channelId } };
        if (mode === 'アーカイブ') h.currentResponse.microformat.playerMicroformatRenderer.liveBroadcastDetails.isLiveNow = false;
        if (mode === '予約枠') h.currentResponse.videoDetails.isUpcoming = true;
        if (mode === '別動画の古いメタデータ') h.currentResponse.videoDetails.videoId = 'another1234';
        if (mode === '未来の開始時刻') h.currentResponse.microformat.playerMicroformatRenderer.liveBroadcastDetails.startTimestamp = '2999-01-01T00:00:00Z';
        if (mode === '不正チャンネル') h.currentResponse.videoDetails.channelId = 'UCbad/../../';
        if (mode === 'チャンネルなし') delete h.currentResponse.videoDetails.channelId;
        h.install(); h.end();
        assert.equal(h.requests.length, 0); assert.equal(h.posts.length, 0); assert.equal(h.timers.size, 0);
    });
}

test('YouTube自身が2秒以内に次へ移動した場合は通知を取り消す', () => {
    const h = continuationHost(); h.install(); h.end();
    h.context.location.href = 'https://www.youtube.com/watch?v=newLive1234'; h.fire('yt-navigate-start');
    h.currentResponse = h.response('newLive1234');
    h.replay(); // 新しい動画の再生状態を準備し、古い動画のended/state=0を引き継がない。
    h.fire('yt-navigate-finish');
    assert.equal(h.posts.length, 0); assert.equal(h.requests.length, 0); assert.equal(h.timers.size, 0);
});

test('YouTubeプレイヤーの終了通知からも2秒待って通知する', async () => {
    const h = continuationHost(); h.install(); h.playerEnd();
    assert.equal(h.posts.length, 0); await h.tick(2000);
    assert.equal(h.posts.length, 1); assert.equal(h.requests.length, 0);
});

for (const reason of ['手動遷移', 'ページ破棄', '再生再開', 'シーク', 'OFF']) {
    test('通知待ちの' + reason + 'で予約を取り消す', () => {
        const h = continuationHost(); h.install(); h.end();
        if (reason === '手動遷移') h.fire('yt-navigate-start');
        if (reason === 'ページ破棄') h.fire('pagehide');
        if (reason === '再生再開') h.replay();
        if (reason === 'シーク') { h.replay(); h.fire('seeking'); }
        if (reason === 'OFF') h.install(false);
        assert.equal(h.posts.length, 0); assert.equal(h.requests.length, 0); assert.equal(h.timers.size, 0);
        if (reason === 'ページ破棄') {
            assert.equal(h.context.__tabBrowserMiniPlayerLiveContinuation, undefined);
            assert.equal([...h.events.values()].reduce((n, handlers) => n + handlers.size, 0), 0);
        }
    });
}

for (const mode of ['ad-showing', 'ad-interrupting', 'loop', 'ab-repeat', 'old-element']) {
    test('広告中・リピート中・古い動画の終了は通知しない: ' + mode, () => {
        const h = continuationHost(); h.install();
        if (mode.startsWith('ad-')) h.adClasses.add(mode);
        if (mode === 'loop') h.video.loop = true;
        if (mode === 'ab-repeat') h.context.__tabBrowserYouTubeMiniPlayerAbRepeat = { enabled: true };
        if (mode === 'old-element') { h.video.ended = true; h.fire('ended', {}); }
        else h.end();
        assert.equal(h.posts.length, 0); assert.equal(h.timers.size, 0); assert.equal(h.requests.length, 0);
    });
}
for (const mode of ['ad-showing', 'ad-interrupting', 'loop', 'ab-repeat']) {
    test('2秒の待機中に操作条件が変わった場合は通知しない: ' + mode, async () => {
        const h = continuationHost(); h.install(); h.end();
        if (mode.startsWith('ad-')) h.adClasses.add(mode);
        if (mode === 'loop') h.video.loop = true;
        if (mode === 'ab-repeat') h.context.__tabBrowserYouTubeMiniPlayerAbRepeat = { enabled: true };
        await h.tick(2000);
        assert.equal(h.posts.length, 0); assert.equal(h.requests.length, 0); assert.equal(h.timers.size, 0);
    });
}
for (const mode of ['external', 'http', 'iframe']) {
    test('外部サイト・HTTP・子フレームへ継続bridgeを設置しない: ' + mode, () => {
        const h = continuationHost();
        if (mode === 'external') h.context.location.hostname = 'youtube.com.example.invalid';
        if (mode === 'http') h.context.location.protocol = 'http:';
        if (mode === 'iframe') h.context.top = {};
        assert.equal(h.install().ok, false); assert.equal(h.events.size, 0); assert.equal(h.requests.length, 0);
    });
}
test('自動移動OFFでは終了通知も予約もしない', () => {
    const h = continuationHost(); h.install(false); h.end(); h.fire('canplay');
    assert.equal(h.requests.length, 0); assert.equal(h.posts.length, 0); assert.equal(h.timers.size, 0);
});
test('通知前のOFF→ONは再予約でき、通知後のOFF→ONでも同じ動画を通知し直さない', async () => {
    const h = continuationHost(); h.install(); h.end();
    const listeners = [...h.events.values()].reduce((n, handlers) => n + handlers.size, 0);
    h.install(false); assert.equal(h.timers.size, 0);
    h.install(true); assert.equal(h.timers.size, 1); await h.tick(2000);
    assert.equal(h.posts.length, 1); h.install(false); h.install(true); h.end();
    assert.equal(h.posts.length, 1); assert.equal(h.timers.size, 0); assert.equal(h.requests.length, 0);
    assert.equal([...h.events.values()].reduce((n, handlers) => n + handlers.size, 0), listeners);
});
