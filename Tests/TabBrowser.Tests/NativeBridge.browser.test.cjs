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
    return raw.replaceAll('{{serializedToken}}', '"fixture-token"')
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
    return raw.replace(/\{\{(\w+)\}\}/g, (_, key) => {
        assert.ok(Object.hasOwn(values, key), method + ': ' + key);
        return values[key];
    });
}
function muteRestoreHost({ muted = true, savedMuted = true, userOverride = false, playbackUnmutes = false,
    fullPlayback = false } = {}) {
    const handlers = new Map(), transitions = [];
    let currentMuted = muted;
    const video = {
        readyState: 2, paused: false, volume: 0.4, playbackRate: 1, loop: false, duration: 100, currentTime: 0,
        setAttribute() {}, removeAttribute() {}, addEventListener() {}, removeEventListener() {},
        get muted() { return currentMuted; },
        set muted(value) { currentMuted = value; transitions.push(value); }
    };
    const player = { mute() { video.muted = true; }, unMute() { video.muted = false; },
        setVolume(value) { video.volume = value / 100; }, getVideoData() { return { video_id: 'fixture-video' }; },
        seekTo(value) { video.currentTime = value; if (playbackUnmutes) video.muted = false; } };
    const context = {
        document: {
            getElementById() { return player; },
            querySelector(selector) { return selector === 'video' ? (context.videoAvailable ? video : null) : player; },
            addEventListener(name, handler) { handlers.set(name, handler); },
            removeEventListener(name) { handlers.delete(name); }
        },
        location: { href: 'https://www.youtube.com/watch?v=fixture-video' },
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
    return { context, video, handlers, transitions, volumeScript, restoreScript };
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
function continuationHost() {
    const events = new Map(), timers = new Map(), posts = [], requests = [];
    const channelId = 'UC' + 'a'.repeat(22);
    const response = (id = 'oldLive1234') => ({
        videoDetails: { videoId: id, channelId, isLiveContent: true },
        microformat: { playerMicroformatRenderer: { liveBroadcastDetails: {
            isLiveNow: true, startTimestamp: '2026-01-01T00:00:00Z' } } },
        playabilityStatus: { status: 'OK' }
    });
    const video = { ended: false, loop: false, muted: true, volume: 0.3 };
    let playerState = 1, timerId = 0, onState;
    const player = { querySelector: () => video, classList: { contains: () => false },
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
        addEventListener: add, removeEventListener: remove, URL, AbortController, Date,
        setTimeout(callback, ms) { const id = ++timerId; timers.set(id, { callback, ms }); return id; },
        clearTimeout(id) { timers.delete(id); },
        chrome: { webview: { postMessage: value => posts.push(value) } },
        async fetch(url, options) {
            requests.push({ url, options });
            if (h.fetchOverride) return h.fetchOverride(options);
            return { ok: true, url: 'https://www.youtube.com/watch?v=newLive1234',
                text: async () => 'var ytInitialPlayerResponse = ' + JSON.stringify(h.nextResponse) + '; otherScript();' };
        }
    };
    const h = { context, events, timers, posts, requests, video, player, response,
        currentResponse: response(), nextResponse: response('newLive1234'),
        fire(name, target = video) { for (const handler of [...events.get(name) || []]) handler({ target }); },
        end() { video.ended = true; playerState = 0; h.fire('ended'); },
        playerEnd() { playerState = 0; onState(0); },
        replay() { video.ended = false; playerState = 1; h.fire('play'); },
        async tick(ms) {
            const entry = [...timers].find(([, timer]) => timer.ms === ms);
            assert.ok(entry, 'timer: ' + ms);
            timers.delete(entry[0]);
            await entry[1].callback();
            await new Promise(setImmediate);
        },
        install(enabled = true) { return vm.runInContext(script('Services/YouTubeMiniPlayerWebViewService.cs',
            'BuildLiveContinuationBridgeScript', '', { enabled }), context); }
    };
    context.window = context; context.top = context; vm.createContext(context);
    return h;
}

test('ライブ終了時だけ同じ配信者の別枠を通知し、音声設定を変更しない', async () => {
    const h = continuationHost();
    h.nextResponse.videoDetails.title = 'JSON内の { 括弧 } と "引用"';
    h.install();
    assert.equal(h.requests.length, 0);
    assert.equal(h.timers.size, 0);
    h.end();
    assert.equal(h.requests.length, 0);
    await h.tick(2000);
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].url, 'https://www.youtube.com/channel/' + h.currentResponse.videoDetails.channelId + '/live');
    assert.equal(h.requests[0].options.credentials, 'omit');
    assert.equal(h.requests[0].options.cache, 'no-store');
    assert.equal(h.posts.length, 1);
    assert.equal(h.posts[0].type, 'youtubeMiniPlayerLiveContinuation');
    assert.equal(h.posts[0].token, 'fixture-token');
    assert.equal(h.posts[0].sourceVideoId, 'oldLive1234');
    assert.equal(h.posts[0].nextVideoId, 'newLive1234');
    assert.equal(h.video.muted, true);
    assert.equal(h.video.volume, 0.3);
    assert.equal(h.timers.size, 0);
});

for (const mode of ['通常動画', 'アーカイブ', '予約枠', '別動画の古いメタデータ']) {
    test('継続確認を開始しない: ' + mode, () => {
        const h = continuationHost();
        if (mode === '通常動画') h.currentResponse = { videoDetails: { videoId: 'oldLive1234', channelId: h.currentResponse.videoDetails.channelId } };
        if (mode === 'アーカイブ') h.currentResponse.microformat.playerMicroformatRenderer.liveBroadcastDetails.isLiveNow = false;
        if (mode === '予約枠') h.currentResponse.videoDetails.isUpcoming = true;
        if (mode === '別動画の古いメタデータ') h.currentResponse.videoDetails.videoId = 'another1234';
        h.install(); h.end();
        assert.equal(h.requests.length, 0);
        assert.equal(h.timers.size, 0);
    });
}
for (const mode of ['終了済み', '予約枠', '別配信者', '同じ動画', '不正ID', '再生不可', '未来の開始時刻']) {
    test('移動先から除外し、最大2回で確認を終える: ' + mode, async () => {
        const h = continuationHost();
        const candidate = h.nextResponse;
        if (mode === '終了済み') candidate.microformat.playerMicroformatRenderer.liveBroadcastDetails.endTimestamp = '2026-01-02T00:00:00Z';
        if (mode === '予約枠') candidate.videoDetails.isUpcoming = true;
        if (mode === '別配信者') candidate.videoDetails.channelId = 'UC' + 'b'.repeat(22);
        if (mode === '同じ動画') candidate.videoDetails.videoId = 'oldLive1234';
        if (mode === '不正ID') candidate.videoDetails.videoId = 'id&evil=1';
        if (mode === '再生不可') candidate.playabilityStatus.status = 'LOGIN_REQUIRED';
        if (mode === '未来の開始時刻') candidate.microformat.playerMicroformatRenderer.liveBroadcastDetails.startTimestamp = '2999-01-01T00:00:00Z';
        h.install(); h.end(); h.fire('ended'); h.install();
        assert.equal(h.timers.size, 1);
        await h.tick(2000); await h.tick(10000);
        assert.equal(h.requests.length, 2);
        assert.equal(h.posts.length, 0);
        assert.equal(h.timers.size, 0);
    });
}

test('継続枠の開始が遅れた場合は2回目に確認できる', async () => {
    const h = continuationHost();
    h.nextResponse = h.response();
    h.install(); h.end(); await h.tick(2000);
    h.nextResponse = h.response('newLive1234');
    await h.tick(10000);
    assert.equal(h.requests.length, 2);
    assert.equal(h.posts.length, 1);
});

test('YouTubeプレイヤーの終了通知でも確認を開始する', async () => {
    const h = continuationHost();
    h.install(); h.playerEnd(); await h.tick(2000);
    assert.equal(h.posts.length, 1);
});

for (const reason of ['手動遷移', 'ページ破棄']) {
    test('確認中の' + reason + 'で通信を中止し、遅れた結果を破棄する', async () => {
        const h = continuationHost();
        let complete;
        h.fetchOverride = () => new Promise(resolve => { complete = resolve; });
        h.install(); h.end();
        const checking = h.tick(2000);
        h.fire(reason === '手動遷移' ? 'yt-navigate-start' : 'pagehide');
        assert.equal(h.requests[0].options.signal.aborted, true);
        complete({ ok: true, url: 'https://www.youtube.com/watch?v=newLive1234',
            text: async () => 'var ytInitialPlayerResponse=' + JSON.stringify(h.nextResponse) + ';' });
        await checking;
        assert.equal(h.posts.length, 0);
        assert.equal(h.timers.size, 0);
        if (reason === 'ページ破棄') {
            assert.equal(h.context.__tabBrowserMiniPlayerLiveContinuation, undefined);
            assert.equal([...h.events.values()].reduce((n, handlers) => n + handlers.size, 0), 0);
        }
    });
}

test('配信終了後に再生を再開した場合は継続確認を取り消す', () => {
    const h = continuationHost(); h.install(); h.end();
    h.currentResponse.microformat.playerMicroformatRenderer.liveBroadcastDetails.isLiveNow = false;
    h.replay();
    assert.equal(h.timers.size, 0);
    h.end();
    assert.equal(h.timers.size, 0);
});

test('ループ中・広告中・古い動画要素の終了では確認しない', () => {
    for (const mode of ['loop', 'ad', 'old-element']) {
        const h = continuationHost(); h.install();
        if (mode === 'loop') h.video.loop = true;
        if (mode === 'ad') h.player.classList.contains = () => true;
        if (mode === 'old-element') { h.video.ended = true; h.fire('ended', {}); }
        else h.end();
        assert.equal(h.timers.size, 0, mode);
    }
});

test('外部サイト・HTTP・子フレームには継続bridgeを設置しない', () => {
    for (const mode of ['external', 'http', 'iframe']) {
        const h = continuationHost();
        if (mode === 'external') h.context.location.hostname = 'youtube.com.example.invalid';
        if (mode === 'http') h.context.location.protocol = 'http:';
        if (mode === 'iframe') h.context.top = {};
        assert.equal(h.install().ok, false);
        assert.equal(h.events.size, 0);
    }
});

test('通信失敗・壊れた情報・外部リダイレクトでも確認は2回で終わる', async () => {
    for (const mode of ['network', 'broken', 'redirect', 'not-live']) {
        const h = continuationHost();
        if (mode === 'network') h.fetchOverride = async () => { throw new Error('offline fixture'); };
        if (mode === 'broken') h.fetchOverride = async () => ({ ok: true,
            url: 'https://www.youtube.com/channel/test/live', text: async () => 'var ytInitialPlayerResponse = {broken};' });
        if (mode === 'redirect') h.fetchOverride = async () => ({ ok: true,
            url: 'https://example.invalid/', text: async () => { throw new Error('外部ページを読まない'); } });
        if (mode === 'not-live') h.nextResponse.microformat.playerMicroformatRenderer.liveBroadcastDetails.isLiveNow = false;
        h.install(); h.end(); await h.tick(2000); await h.tick(10000);
        assert.equal(h.posts.length, 0, mode);
        assert.equal(h.requests.length, 2, mode);
        assert.equal(h.timers.size, 0, mode);
    }
});

test('通信の5秒タイムアウトで中止し、再確認を残したままページ破棄で解放できる', async () => {
    const h = continuationHost();
    h.fetchOverride = options => new Promise((_, reject) => {
        options.signal.addEventListener('abort', () => reject(new Error('aborted fixture')), { once: true });
    });
    h.install(); h.end(); const checking = h.tick(2000);
    await h.tick(5000); await checking;
    assert.equal(h.requests[0].options.signal.aborted, true);
    assert.equal(h.posts.length, 0);
    assert.equal(h.timers.size, 1);
    h.fire('pagehide');
    assert.equal(h.timers.size, 0);
});

test('自動移動OFFではライブが終わっても通信を開始しない', () => {
    const h = continuationHost(); h.install(false); h.end(); h.fire('canplay');
    assert.equal(h.requests.length, 0);
    assert.equal(h.posts.length, 0);
    assert.equal(h.timers.size, 0);
});

test('確認待ちにOFFへ変更すると予約を取り消し、その後ONで再開できる', async () => {
    const h = continuationHost(); h.install(); h.end();
    assert.equal(h.timers.size, 1);
    const listenersBefore = [...h.events.values()].reduce((n, handlers) => n + handlers.size, 0);
    h.install(false);
    assert.equal(h.timers.size, 0);
    h.install(true); h.install(true);
    assert.equal(h.timers.size, 1);
    assert.equal([...h.events.values()].reduce((n, handlers) => n + handlers.size, 0), listenersBefore);
    await h.tick(2000);
    assert.equal(h.posts.length, 1);
});

test('通信中にOFFへ変更すると中止し、ONへ戻しても古い結果を使わない', async () => {
    const h = continuationHost();
    let complete;
    h.fetchOverride = () => new Promise(resolve => { complete = resolve; });
    h.install(); h.end(); const checking = h.tick(2000);
    h.install(false);
    assert.equal(h.requests[0].options.signal.aborted, true);
    h.install(true);
    complete({ ok: true, url: 'https://www.youtube.com/watch?v=newLive1234',
        text: async () => 'var ytInitialPlayerResponse=' + JSON.stringify(h.nextResponse) + ';' });
    await checking;
    assert.equal(h.posts.length, 0);
    assert.equal(h.timers.size, 1);
    h.fetchOverride = null;
    await h.tick(2000);
    assert.equal(h.posts.length, 1);
});
