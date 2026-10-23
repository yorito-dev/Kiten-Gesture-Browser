// 実行: node --test Tests/TabBrowser.Tests/YouTubeMiniPlayerLayout.browser.test.cjs
// Node.js 22以降と既存のChromeを使用。専用の一時プロファイルで、実ユーザーのページは開かない。
const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { readFile, mkdtemp, rm } = require('node:fs/promises');
const { join, resolve, basename } = require('node:path');
const { tmpdir } = require('node:os');
const { once } = require('node:events');
const { waitForChromeEndpoint } = require('./ChromeTestLauncher.cjs');

let browser, socket, profile, sessionId;
let chromeEndpointSource, chromeStderrBytes = 0;
let nextId = 0;
const pending = new Map();
const call = (method, params = {}, targetSession = sessionId) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`${method}: timeout`)); }, 10000);
    pending.set(id, { resolve, reject, timeout });
    socket.send(JSON.stringify({ id, method, params, ...(targetSession ? { sessionId: targetSession } : {}) }));
});
const evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
    return result.result.value;
};
const frames = async (count = 8) => evaluate(`new Promise(resolve => {
    let remaining = ${count};
    const tick = () => --remaining <= 0 ? resolve() : requestAnimationFrame(tick);
    requestAnimationFrame(tick);
})`);
let script;
let startPositionScript;
before(async () => {
    const source = await readFile(resolve(__dirname, '../../Services/YouTubeControlScriptService.cs'), 'utf8');
    startPositionScript = source.slice(source.indexOf('public static string BuildMiniPlayerStartPositionReadScript('))
        .match(/return """\r?\n([\s\S]*?)\r?\n\s*"""\.Replace\(/)[1]
        .replaceAll('__VIDEO_ID__', JSON.stringify('abc123'))
        .replaceAll('__AD_PLAYING__', source.match(/const string AdvertisementPlayingExpression = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1]);
    const method = source.slice(source.indexOf('public static string BuildMiniPlayerWatchPageScript(\n') >= 0
        ? source.indexOf('public static string BuildMiniPlayerWatchPageScript(\n')
        : source.indexOf('public static string BuildMiniPlayerWatchPageScript(\r\n'));
    script = method.match(/return """\r?\n([\s\S]*?)\r?\n\s*"""\.Replace\(/)[1];
    const values = { MESSAGE_TOKEN: 'test-token', GESTURE_SETTINGS: { mappings: [] },
        CONTENT_ZOOM: 1, CONTENT_PAN_X: 0, CONTENT_PAN_Y: 0, AUDIO_BOOST_DECIBELS: 0, AUDIO_BOOST_GAIN: 1 };
    for (const [name, value] of Object.entries(values)) {
        script = script.replaceAll(`__TAB_BROWSER_YOUTUBE_MINI_PLAYER_${name}__`, JSON.stringify(value));
    }
    script = script.replaceAll('__TAB_BROWSER_YOUTUBE_MINI_PLAYER_AD_PLAYING__',
        source.match(/const string AdvertisementPlayingExpression = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1]);
    assert.ok(!script.includes('__TAB_BROWSER_YOUTUBE_MINI_PLAYER_'));
    profile = await mkdtemp(join(tmpdir(), 'tabbrowser-layout-test-'));
    browser = spawn(process.env.KITEN_BROWSER_TEST_CHROME
        || join(process.env.ProgramFiles, 'Google/Chrome/Application/chrome.exe'), [
        '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking',
        '--disable-extensions', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
    ], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
    browser.stderr.on('data', chunk => { chromeStderrBytes += chunk.length; });
    const { endpoint, source: endpointSource, stderrBytes, exitCode, signal } = await waitForChromeEndpoint(browser, profile);
    chromeEndpointSource = endpointSource;
    console.log(`Chrome接続先: source=${endpointSource} stderrBytes=${stderrBytes} exitCode=${exitCode ?? '未終了'} signal=${signal ?? 'なし'}`);
    socket = new WebSocket(endpoint);
    await new Promise((resolve, reject) => {
        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', reject, { once: true });
    });
    socket.addEventListener('message', event => {
        const response = JSON.parse(event.data);
        const request = pending.get(response.id);
        if (!request) return;
        pending.delete(response.id);
        clearTimeout(request.timeout);
        if (response.error) request.reject(new Error(JSON.stringify(response.error)));
        else request.resolve(response.result);
    });
    const { targetId } = await call('Target.createTarget', { url: 'about:blank' });
    ({ sessionId } = await call('Target.attachToTarget', { targetId, flatten: true }));
    await call('Emulation.setDeviceMetricsOverride', { width: 960, height: 540, deviceScaleFactor: 1, mobile: false });
}, { timeout: 30000 });

after(async () => {
    console.log(`Chrome起動診断: source=${chromeEndpointSource ?? '未取得'} stderrBytesTotal=${chromeStderrBytes} exitCode=${browser?.exitCode ?? '未終了'} signal=${browser?.signalCode ?? 'なし'}`);
    if (socket?.readyState === WebSocket.OPEN) {
        const exited = browser.exitCode === null ? once(browser, 'exit') : Promise.resolve();
        await call('Browser.close', {}, null).catch(() => {});
        await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]);
        socket.close();
    }
    if (browser?.exitCode === null) browser.kill();
    // このテストで生成した、絶対パスの専用一時フォルダーだけを後始末する。
    if (profile && resolve(profile).startsWith(resolve(tmpdir()) + require('node:path').sep)
        && basename(profile).startsWith('tabbrowser-layout-test-')) {
        await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
});

async function reset({ videoInitiallyMissing = false, width = 960, height = 540 } = {}) {
    await call('Page.navigate', { url: 'about:blank' });
    await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    await evaluate(`document.body.innerHTML = '<ytd-watch-flexy><ytd-player><div id="movie_player" class="html5-video-player"><div class="html5-video-container"><video class="html5-main-video"></video></div></div></ytd-player></ytd-watch-flexy>';
        window.__miniPlayerMessages = [];
        Object.defineProperty(window, 'chrome', { configurable: true, value: {
            webview: { postMessage: message => window.__miniPlayerMessages.push(message) }
        }});
        window.layoutCalls = 0;
        document.getElementById('movie_player').setSize = () => window.layoutCalls++;`);
    if (videoInitiallyMissing) await evaluate(`document.querySelector('video').remove()`);
    await evaluate(script);
    await frames();
}
async function bounds() {
    return evaluate(`(() => {
        const rect = document.querySelector('video').getBoundingClientRect();
        return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    })()`);
}
const fitted = { x: 0, y: 0, width: 960, height: 540 };

test('正常な拡大表示の再確認では寸法やstyleを書き直さず、各描画の倍率とパンを維持する', async () => {
    for (const zoom of [1, 1.5, 2, 4]) {
        await reset();
        await evaluate(`window.__tabBrowserYouTubeMiniPlayerSetContentTransform('test-token', ${zoom}, 0.4, -0.3, false)`);
        await frames();
        const expected = await bounds();
        const result = await evaluate(`new Promise(resolve => {
            const video = document.querySelector('video');
            const before = window.layoutCalls;
            let writes = 0;
            const observer = new MutationObserver(records => writes += records.length);
            observer.observe(document.getElementById('movie_player'), {
                attributes: true, attributeFilter: ['style'], subtree: true
            });
            const samples = [];
            const tick = () => {
                const rect = video.getBoundingClientRect();
                samples.push({ x: rect.x, y: rect.y, width: rect.width, height: rect.height });
                if (samples.length < 8) {
                    requestAnimationFrame(tick);
                } else {
                    observer.disconnect();
                    resolve({ calls: window.layoutCalls - before, writes, samples });
                }
            };
            window.__tabBrowserYouTubeMiniPlayerLayoutBridge.beginVerification(true);
            video.dispatchEvent(new Event('playing'));
            requestAnimationFrame(tick);
        })`);
        assert.equal(result.calls, 0, `zoom=${zoom}: 正常時のsetSize`);
        assert.equal(result.writes, 0, `zoom=${zoom}: 正常時のstyle変更`);
        for (const sample of result.samples) assert.deepEqual(sample, expected);
    }
});

test('拡大transformの書き戻しは次の描画前に直し、寸法補正を呼ばない', async () => {
    for (const zoom of [1.5, 2, 4]) {
        await reset();
        await evaluate(`window.__tabBrowserYouTubeMiniPlayerSetContentTransform('test-token', ${zoom}, -0.4, 0.3, false)`);
        await frames();
        const expected = await bounds();
        const before = await evaluate('window.layoutCalls');
        for (let attempt = 0; attempt < 3; attempt++) {
            const samples = await evaluate(`new Promise(resolve => {
                const video = document.querySelector('video');
                const samples = [];
                video.style.setProperty('transform', 'none', 'important');
                video.style.setProperty('transform-origin', '0px 0px', 'important');
                const tick = () => {
                    const rect = video.getBoundingClientRect();
                    samples.push({ x: rect.x, y: rect.y, width: rect.width, height: rect.height });
                    if (samples.length < 4) requestAnimationFrame(tick);
                    else resolve(samples);
                };
                requestAnimationFrame(tick);
            })`);
            for (const sample of samples) assert.deepEqual(sample, expected, `zoom=${zoom}, attempt=${attempt}`);
        }
        assert.equal(await evaluate('window.layoutCalls'), before);
    }
});

test('拡大中の2秒後と5秒後の再確認でも正常な表示を書き直さない', async () => {
    await reset();
    await evaluate(`window.__tabBrowserYouTubeMiniPlayerSetContentTransform('test-token', 2, 0.4, -0.3, false)`);
    await frames();
    const expected = await bounds();
    const before = await evaluate(`(() => {
        window.zoomStyleWrites = 0;
        window.zoomStyleObserver = new MutationObserver(records => window.zoomStyleWrites += records.length);
        window.zoomStyleObserver.observe(document.getElementById('movie_player'), {
            attributes: true, attributeFilter: ['style'], subtree: true
        });
        window.__tabBrowserYouTubeMiniPlayerLayoutBridge.beginVerification(true);
        return window.layoutCalls;
    })()`);
    await new Promise(resolve => setTimeout(resolve, 5200));
    await frames();
    const result = await evaluate(`(() => {
        window.zoomStyleObserver.disconnect();
        return { calls: window.layoutCalls, writes: window.zoomStyleWrites,
            timers: window.__tabBrowserYouTubeMiniPlayerLayoutBridge.delayedVerificationTimers.length };
    })()`);
    assert.deepEqual(result, { calls: before, writes: 0, timers: 0 });
    assert.deepEqual(await bounds(), expected);
});

test('公式設定メニューを開くと独自操作バーを退避し、メニュー上では再表示しない', async () => {
    await reset();
    await evaluate(`const settingsButton = document.createElement('button');
        settingsButton.className = 'ytp-settings-button';
        Object.assign(settingsButton.style, { position: 'fixed', left: '20px', top: '20px',
            width: '60px', height: '32px', zIndex: '2147483647' });
        document.body.append(settingsButton);
        const settingsMenu = document.createElement('div');
        settingsMenu.className = 'ytp-settings-menu ytp-popup';
        Object.assign(settingsMenu.style, { position: 'fixed', left: '20px', bottom: '0',
            width: '180px', height: '180px' });
        document.getElementById('movie_player').append(settingsMenu);`);

    await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 40, y: 36 });
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 40, y: 36, button: 'left', clickCount: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 40, y: 36, button: 'left', clickCount: 1 });
    assert.equal(await evaluate(`window.__miniPlayerMessages.some(message =>
        message.type === 'youtubeMiniPlayerHideToolbar' && message.token === 'test-token')`), true);

    await evaluate(`document.querySelector('.ytp-settings-menu').style.display = 'block';
        window.dispatchEvent(new PointerEvent('pointermove', { clientY: 530 }));`);
    assert.deepEqual(await evaluate(`(() => {
        const message = window.__miniPlayerMessages.at(-1);
        return { type: message?.type, hovered: message?.hovered };
    })()`), { type: 'youtubeMiniPlayerHover', hovered: false });

    await evaluate(`document.querySelector('.ytp-settings-menu').style.display = 'none';
        const submenu = document.createElement('div');
        submenu.className = 'ytp-panel-menu';
        Object.assign(submenu.style, { position: 'fixed', left: '20px', bottom: '0',
            width: '180px', height: '180px' });
        document.getElementById('movie_player').append(submenu);
        window.dispatchEvent(new PointerEvent('pointermove', { clientY: 530 }));`);
    assert.deepEqual(await evaluate(`(() => {
        const message = window.__miniPlayerMessages.at(-1);
        return { type: message?.type, hovered: message?.hovered };
    })()`), { type: 'youtubeMiniPlayerHover', hovered: false });

    await evaluate(`document.querySelector('.ytp-panel-menu').style.display = 'none';
        window.dispatchEvent(new PointerEvent('pointermove', { clientY: 530 }));`);
    assert.deepEqual(await evaluate(`(() => {
        const message = window.__miniPlayerMessages.at(-1);
        return { type: message?.type, hovered: message?.hovered };
    })()`), { type: 'youtubeMiniPlayerHover', hovered: true });
});

test('画質メニューの項目をクリックしても動画面のクリック抑止が奪わない', async () => {
    await reset();
    await evaluate(`window.__qualityClicks = 0;
        const panel = document.createElement('div');
        panel.className = 'ytp-panel-menu';
        Object.assign(panel.style, { position: 'fixed', left: '700px', top: '100px',
            width: '200px', height: '200px', zIndex: '2147483647' });
        const quality = document.createElement('div');
        quality.textContent = '1080p';
        Object.assign(quality.style, { height: '48px', padding: '8px' });
        quality.addEventListener('click', () => window.__qualityClicks++);
        panel.append(quality);
        document.getElementById('movie_player').append(panel);`);
    assert.equal(await evaluate('document.elementFromPoint(740, 124)?.textContent'), '1080p');

    await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 740, y: 124 });
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 740, y: 124, button: 'left', clickCount: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 751, y: 128, button: 'left', buttons: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 751, y: 128, button: 'left', clickCount: 1 });
    assert.equal(await evaluate('window.__qualityClicks'), 1);
    assert.equal(await evaluate(`window.__miniPlayerMessages.some(message =>
        message.type === 'youtubeMiniPlayerDrag')`), false);

    await evaluate(`document.querySelector('.ytp-panel-menu').style.display = 'none';
        window.__videoClicks = 0;
        document.querySelector('video').addEventListener('click', () => window.__videoClicks++);`);
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 400, y: 200, button: 'left', clickCount: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 400, y: 200, button: 'left', clickCount: 1 });
    assert.equal(await evaluate('window.__videoClicks'), 0);
});

test('空の全幅パネル領域が画質メニューへのクリックを覆わない', async () => {
    await reset();
    await evaluate(`window.__qualityClicks = 0;
        const menu = document.createElement('div');
        menu.className = 'ytp-settings-menu ytp-popup';
        Object.assign(menu.style, { position: 'fixed', left: '700px', top: '100px',
            width: '220px', height: '200px', zIndex: '2300' });
        const quality = document.createElement('div');
        quality.className = 'ytp-menuitem';
        quality.textContent = '480p';
        Object.assign(quality.style, { height: '48px', padding: '8px' });
        quality.addEventListener('click', () => window.__qualityClicks++);
        menu.append(quality);
        document.getElementById('movie_player').append(menu);
        const overlay = document.createElement('div');
        overlay.id = 'panels-full-bleed-container';
        Object.assign(overlay.style, { position: 'fixed', right: '0', top: '0',
            width: '280px', height: '250px', zIndex: '2' });
        document.querySelector('ytd-watch-flexy').append(overlay);`);
    assert.equal(await evaluate(`document.elementFromPoint(740, 124)?.textContent`), '480p');

    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 740, y: 124, button: 'left', clickCount: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 740, y: 124, button: 'left', clickCount: 1 });
    assert.equal(await evaluate('window.__qualityClicks'), 1);
});

test('初回に動画が未生成でも後から現れた実動画を補正し、別領域の動画を選ばない', async () => {
    await reset({ videoInitiallyMissing: true });
    assert.equal(await evaluate(`window.__tabBrowserYouTubeMiniPlayerLayoutBridge.layoutVerified`), false);
    await evaluate(`const unrelated = document.createElement('video');
        unrelated.id = 'unrelated-video'; document.body.prepend(unrelated);`);
    await frames();
    assert.equal(await evaluate(`window.__tabBrowserYouTubeMiniPlayerLayoutBridge.appliedVideo`), null);
    await evaluate(`const next = document.createElement('video'); next.id = 'initial-video';
        next.style.setProperty('width', '240px', 'important');
        document.querySelector('.html5-video-container').append(next);`);
    await frames();
    assert.deepEqual(await evaluate(`(() => {
        const bridge = window.__tabBrowserYouTubeMiniPlayerLayoutBridge;
        return { id: bridge.appliedVideo?.id, width: bridge.appliedVideo?.getBoundingClientRect().width,
            verified: bridge.layoutVerified, discovering: bridge.discoveryObserver !== null };
    })()`), { id: 'initial-video', width: 960, verified: true, discovering: false });
});

test('囲み要素の遅延縮小を補正し、静止後に補正を繰り返さない', async () => {
    await reset();
    await evaluate(`const container = document.querySelector('.html5-video-container');
        container.style.setProperty('transform-origin', 'top left', 'important');
        container.style.setProperty('transform', 'scale(0.25)', 'important');`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
    const count = await evaluate('window.layoutCalls');
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls'), count);
});

test('縦長動画で初回補正後に監視通知を逃した縮小も、起動後の限定再確認で直す', async () => {
    await reset({ width: 320, height: 560 });
    await evaluate(`const bridge = window.__tabBrowserYouTubeMiniPlayerLayoutBridge;
        bridge.contentTransformObserver?.disconnect();
        bridge.elementResizeObserver?.disconnect();
        document.querySelector('video').style.setProperty('width', '24px', 'important');`);
    await frames(12);
    assert.equal((await bounds()).width, 24);

    await new Promise(resolve => setTimeout(resolve, 2300));
    await frames();
    assert.deepEqual(await bounds(), { x: 0, y: 0, width: 320, height: 560 });

    const count = await evaluate('window.layoutCalls');
    await new Promise(resolve => setTimeout(resolve, 3100));
    await frames();
    assert.equal(await evaluate('window.layoutCalls'), count);
});

test('遷移開始とブリッジ破棄で遅延確認を取り消し、次の遷移で再予約する', async () => {
    await reset();
    assert.equal(await evaluate('window.__tabBrowserYouTubeMiniPlayerLayoutBridge.delayedVerificationTimers.length'), 2);
    await evaluate('document.dispatchEvent(new Event("yt-navigate-start"))');
    assert.equal(await evaluate('window.__tabBrowserYouTubeMiniPlayerLayoutBridge.delayedVerificationTimers.length'), 0);
    await evaluate('document.dispatchEvent(new Event("yt-navigate-finish"))');
    assert.equal(await evaluate('window.__tabBrowserYouTubeMiniPlayerLayoutBridge.delayedVerificationTimers.length'), 2);
    await evaluate('window.__tabBrowserYouTubeMiniPlayerLayoutBridge.dispose()');
    assert.equal(await evaluate('window.__tabBrowserYouTubeMiniPlayerLayoutBridge.delayedVerificationTimers.length'), 0);
});

test('寸法の変わらない囲み要素の移動も補正する', async () => {
    await reset();
    await evaluate(`document.querySelector('.html5-video-container').style.setProperty('transform', 'translate(100px, 50px)', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
});

test('動画だけを交換した後のstyle書き戻しも、mediaイベントなしで補正する', async () => {
    await reset();
    await evaluate(`document.querySelector('video').replaceWith(document.createElement('video'))`);
    await frames();
    await evaluate(`document.querySelector('video').style.setProperty('width', '240px', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
});

test('囲み要素を補正してもユーザーの200%拡大を維持する', async () => {
    await reset();
    await evaluate(`window.__tabBrowserYouTubeMiniPlayerSetContentTransform('test-token', 2, 0, 0, false);
        document.querySelector('.html5-video-container').style.setProperty('transform', 'scale(0.25)', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), { x: -480, y: -270, width: 1920, height: 1080 });
});

test('大きさが同じでも配置だけがずれた場合は実表示座標で補正する', async () => {
    await reset();
    await evaluate(`document.querySelector('.html5-video-container').style.setProperty('left', '100px', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
});

test('囲み要素ごとの交換後と縦長リサイズ後も補正する', async () => {
    await reset();
    await evaluate(`const oldContainer = document.querySelector('.html5-video-container');
        oldContainer.replaceWith(oldContainer.cloneNode(true));`);
    await frames();
    await evaluate(`document.querySelector('.html5-video-container').style.setProperty('transform', 'scale(0.25)', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
    await call('Emulation.setDeviceMetricsOverride', { width: 330, height: 586, deviceScaleFactor: 1, mobile: false });
    await frames();
    assert.deepEqual(await bounds(), { x: 0, y: 0, width: 330, height: 586 });
});

test('対話的リサイズ中は独自補正を休止し、終了後に最終寸法へ1回だけ合わせる', async () => {
    await reset();
    const before = await evaluate('window.layoutCalls');
    assert.deepEqual(await evaluate(`window.__tabBrowserSetYouTubeMiniPlayerInteractiveResize(
        'test-token', 1, true)`), { ok: true, active: true, generation: 1 });

    for (const [width, height] of [[900, 506], [840, 473], [780, 439]]) {
        await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
        await frames(3);
    }

    assert.equal(await evaluate('window.layoutCalls'), before);
    assert.deepEqual(await evaluate(`window.__tabBrowserSetYouTubeMiniPlayerInteractiveResize(
        'test-token', 2, false)`), { ok: true, active: false, generation: 2 });
    await frames();
    assert.equal(await evaluate('window.layoutCalls') - before, 1);
    assert.deepEqual(await bounds(), { x: 0, y: 0, width: 780, height: 439 });
    assert.deepEqual(await evaluate(`window.__tabBrowserSetYouTubeMiniPlayerInteractiveResize(
        'test-token', 1, true)`), { ok: false, reason: 'stale-generation' });
    assert.equal(await evaluate(
        'window.__tabBrowserYouTubeMiniPlayerLayoutBridge.interactiveResize'), false);
});

test('補正対象外の祖先にずれが残っても自己書き込みで再適用を続けない', async () => {
    await reset();
    await evaluate(`const flexy = document.querySelector('ytd-watch-flexy');
        flexy.style.display = 'block';
        flexy.style.transform = 'scale(0.5)';
        document.getElementById('movie_player').setSize = () => {
            window.layoutCalls++;
            document.querySelector('video').style.width = '240px';
        };
        window.dispatchEvent(new Event('resize'));`);
    await frames(20);
    const count = await evaluate('window.layoutCalls');
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls'), count);
});

test('旧動画が残る切り替えでも再生を開始した新動画を補正する', async () => {
    await reset();
    await evaluate(`const container = document.querySelector('.html5-video-container');
        const next = document.createElement('video');
        next.id = 'next-video';
        Object.defineProperties(next, {
            paused: { value: false }, ended: { value: false }, readyState: { value: 4 }
        });
        next.style.setProperty('width', '240px', 'important');
        container.append(next);
        next.dispatchEvent(new Event('playing'))`);
    await frames();
    assert.deepEqual(await evaluate(`(() => {
        const next = document.getElementById('next-video');
        return { width: next.getBoundingClientRect().width,
            selected: window.__tabBrowserYouTubeMiniPlayerLayoutBridge.appliedVideo === next };
    })()`), { width: 960, selected: true });
});

test('動画切り替え後に追加された中間階層の遅延縮小を補正する', async () => {
    await reset();
    await evaluate(`const video = document.querySelector('video');
        const outer = document.createElement('div');
        const inner = document.createElement('div');
        outer.id = 'transition-wrapper';
        outer.style.width = inner.style.width = '100%';
        outer.style.height = inner.style.height = '100%';
        video.replaceWith(outer); outer.append(inner); inner.append(video);
        document.dispatchEvent(new Event('yt-navigate-finish'))`);
    await frames();
    await evaluate(`const wrapper = document.getElementById('transition-wrapper');
        wrapper.style.setProperty('transform-origin', 'top left', 'important');
        wrapper.style.setProperty('transform', 'scale(0.25)', 'important')`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
    const count = await evaluate('window.layoutCalls');
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls'), count);
});

test('遷移完了イベント後にplayerの外側ごと交換されても監視を付け直す', async () => {
    await reset();
    await evaluate(`document.dispatchEvent(new Event('yt-navigate-finish'))`);
    await frames();
    await evaluate(`const oldHost = document.querySelector('ytd-player');
        const host = oldHost.cloneNode(true);
        host.querySelector('video').style.setProperty('width', '240px', 'important');
        oldHost.replaceWith(host);
        host.querySelector('#movie_player').setSize = () => window.layoutCalls++`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
    assert.equal(await evaluate(`window.__tabBrowserYouTubeMiniPlayerLayoutBridge.appliedPlayer === document.getElementById('movie_player')`), true);
});

test('切り替え途中のsetSizeが失敗してもCSS補正を行う', async () => {
    await reset();
    await evaluate(`document.getElementById('movie_player').setSize = () => { throw new Error('player not ready'); };
        document.querySelector('video').style.setProperty('width', '240px', 'important');
        document.dispatchEvent(new Event('yt-navigate-finish'))`);
    await frames();
    assert.deepEqual(await bounds(), fitted);
});

test('両方の動画が再生中でも最後に準備された動画を使い、切り替え開始時は参照を破棄する', async () => {
    await reset();
    await evaluate(`const old = document.querySelector('video');
        const next = old.cloneNode(true); next.id = 'next-video'; old.after(next);
        for (const video of [old, next]) {
            Object.defineProperties(video, { paused: { value: false }, ended: { value: false }, readyState: { value: 4 } });
        }
        next.dispatchEvent(new Event('playing'));`);
    await frames();
    assert.equal(await evaluate(`window.__tabBrowserYouTubeMiniPlayerLayoutBridge.appliedVideo.id`), 'next-video');
    assert.equal(await evaluate(`(() => {
        document.dispatchEvent(new Event('yt-navigate-start'));
        return window.__tabBrowserYouTubeMiniPlayerLayoutBridge.activeVideo === null;
    })()`), true);
});

test('一時停止した新動画の自然サイズ確定イベントでも対象を切り替える', async () => {
    await reset();
    await evaluate(`const old = document.querySelector('video');
        const next = old.cloneNode(true); next.id = 'next-video'; old.after(next);
        Object.defineProperty(next, 'readyState', { value: 2 });
        next.style.setProperty('width', '240px', 'important');
        next.dispatchEvent(new Event('resize'));`);
    await frames();
    assert.equal(await evaluate(`window.__tabBrowserYouTubeMiniPlayerLayoutBridge.appliedVideo.id`), 'next-video');
    assert.equal(await evaluate(`document.getElementById('next-video').getBoundingClientRect().width`), 960);
});

test('補正できないずれは未完了として扱い、確認の再補正は3回で止める', async () => {
    await reset();
    const before = await evaluate('window.layoutCalls');
    await evaluate(`const flexy = document.querySelector('ytd-watch-flexy');
        flexy.style.display = 'block'; flexy.style.transform = 'scale(0.5)';
        document.dispatchEvent(new Event('yt-navigate-finish'));`);
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls') - before, 3);
    assert.deepEqual(await evaluate(`(() => {
        const b = window.__tabBrowserYouTubeMiniPlayerLayoutBridge;
        return { verified: b.layoutVerified, remaining: b.verificationAttemptsRemaining, frame: b.verificationFrame };
    })()`), { verified: false, remaining: 0, frame: null });
    const count = await evaluate('window.layoutCalls');
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls'), count);
});

test('中間階層を補正しても拡大を維持し、解除後の予約やmediaイベントは補正しない', async () => {
    await reset();
    await evaluate(`const video = document.querySelector('video');
        const wrapper = document.createElement('div');
        video.replaceWith(wrapper); wrapper.append(video);
        window.__tabBrowserYouTubeMiniPlayerSetContentTransform('test-token', 2, 0, 0, false);
        document.dispatchEvent(new Event('yt-navigate-finish'));`);
    await frames();
    assert.deepEqual(await bounds(), { x: -480, y: -270, width: 1920, height: 1080 });
    const count = await evaluate('window.layoutCalls');
    await evaluate(`const bridge = window.__tabBrowserYouTubeMiniPlayerLayoutBridge;
        bridge.beginVerification(); bridge.dispose();
        document.querySelector('video').dispatchEvent(new Event('playing'));
        document.dispatchEvent(new Event('yt-navigate-finish'));`);
    await frames(20);
    assert.equal(await evaluate('window.layoutCalls'), count);
});

// 実Chromeの入力イベントで長押しを動かし、広告中のvideo書換えが0回であることを確認する。
for (const adClass of ['ad-showing', 'ad-interrupting']) {
    test('広告中は長押し早送り・巻き戻しと解除でvideoを書き換えない: ' + adClass, async () => {
        for (const delta of [60, -60]) {
            await reset();
            await evaluate(`(() => {
                const video = document.querySelector('video');
                window.adControlWrites = [];
                for (const [key, initial] of [['currentTime', 20], ['playbackRate', 1], ['paused', false]]) {
                    let value = initial;
                    Object.defineProperty(video, key, { configurable: true, get: () => value,
                        set: next => { window.adControlWrites.push(key); value = next; } });
                }
                video.pause = () => { window.adControlWrites.push('pause'); };
                video.play = () => { window.adControlWrites.push('play'); return Promise.resolve(); };
                document.getElementById('movie_player').classList.add(${JSON.stringify(adClass)});
            })()`);
            await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 300, y: 200, button: 'right', buttons: 2, clickCount: 1 });
            await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 300 + delta, y: 200, buttons: 2 });
            await evaluate('new Promise(resolve => setTimeout(resolve, 300))');
            assert.match(await evaluate("document.getElementById('tabbrowser-youtube-mini-player-hold-seek-indicator')?.textContent || ''"), /広告の再生中は操作できません/);
            await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 300 + delta, y: 200, button: 'right', buttons: 0, clickCount: 1 });
            assert.deepEqual(await evaluate('window.adControlWrites'), []);
        }
    });
}

for (const [name, setup, expected] of [
    ['再生中の現在位置', '', 123.75],
    ['一時停止中の現在位置', 'Object.defineProperty(video, "paused", { value: true });', 123.75],
    ['広告表示中', 'player.classList.add("ad-showing");', null],
    ['広告割込中', 'player.classList.add("ad-interrupting");', null],
    ['別動画への切替途中', 'player.getVideoData = () => ({ video_id: "other" });', null],
    ['動画要素なし', 'video.remove();', null],
    ['不正な位置', 'Object.defineProperty(video, "currentTime", { value: NaN });', null],
    ['動画情報なし', 'player.getVideoData = undefined;', null]
]) {
    test('ページから開く開始位置: ' + name, async () => {
        await reset();
        await evaluate(`(() => {
            const player = document.getElementById('movie_player');
            const video = player.querySelector('video');
            player.getVideoData = () => ({ video_id: 'abc123' });
            Object.defineProperty(video, 'currentTime', { configurable: true, value: 123.75 });
            ${setup}
        })()`);
        assert.equal(await evaluate(startPositionScript), expected);
    });
}
