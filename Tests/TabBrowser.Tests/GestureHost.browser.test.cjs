// Node VM上のDOM模擬試験。実WebView2／実サイトへ接続しない。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');
const source = readFileSync(resolve(__dirname, '../../Scripts/GestureHost.js'), 'utf8');

// 製品のイベント登録から実行し、テスト用の認識処理へ置き換えない。
function createSuperDragHost(config = {}, linkText = '架空 [リンク]', labels = {}) {
    const handlers = new Map(), messages = [], hints = [];
    class Element {
        constructor(kind = 'body', protectedTarget = false) { this.kind = kind; this.protectedTarget = protectedTarget; }
        closest(selector) {
            if (selector.includes('input') && this.protectedTarget) return this;
            if (selector === 'a[href]' && this.kind === 'link') return { href: 'https://target.invalid/a', textContent: linkText };
            if (selector === 'img' && this.kind === 'image') return { currentSrc: 'https://target.invalid/image.png' };
            if (selector.includes(',a[href]') && this.kind === 'link') return this;
            return null;
        }
    }
    const surface = name => ({ addEventListener(type, handler) {
        const key = name + ':' + type; handlers.set(key, [...(handlers.get(key) || []), handler]);
    }, removeEventListener() {} });
    const document = { ...surface('document'), hidden: false, baseURI: 'https://example.invalid/page',
        documentElement: { appendChild(hint) { hints.push(hint); } },
        createElement() { return { style: {}, remove() { this.removed = true; } }; } };
    const settings = { superDrag: { IsEnabled: true, ExcludedHosts: [],
        Text: { Up: 'SearchForeground', Right: 'SearchBackground', Down: 'Copy', Left: 'Translate' },
        Link: { Up: 'Foreground', Right: 'Background', Down: 'Copy', Left: 'NewWindow' },
        Image: { Up: 'Foreground', Right: 'Background', Down: 'Copy', Left: 'NewWindow' }, ...config }, superDragLabels: labels };
    const context = { ...surface('window'), document, Element, innerWidth: 900, innerHeight: 700,
        location: { hostname: 'example.invalid', protocol: 'https:', href: 'https://example.invalid/page' },
        chrome: { webview: { postMessage: message => messages.push(message) } }, console,
        getSelection: () => ({ isCollapsed: false, containsNode: () => true, toString: () => '架空の検索語' }) };
    context.window = context; vm.createContext(context);
    const product = process.env.KITEN_SUPER_DRAG_BASELINE
        ? readFileSync(process.env.KITEN_SUPER_DRAG_BASELINE, 'utf8') : source;
    vm.runInContext('(() => {const __tabBrowserWebMessageToken="fixture-token";'
        + 'const __tabBrowserGestureSettings=' + JSON.stringify(settings) + ';const __tabBrowserGestureCommands=[];'
        + product + '})();', context);
    const emit = (type, extra = {}, targetSurface = 'window') => {
        const event = { isTrusted: true, defaultPrevented: false, target: new Element(), screenX: 100, screenY: 100,
            clientX: 100, clientY: 100, dataTransfer: { types: ['text/plain'] },
            preventDefault() { this.defaultPrevented = true; }, ...extra };
        for (const handler of handlers.get(targetSurface + ':' + type) || []) handler(event);
        return event;
    };
    return { emit, messages, hints, context, Element, settings,
        start(kind = 'link', extra = {}) { return emit('dragstart', { target: new Element(kind), ...extra }); },
        drops: () => messages.filter(message => message.type === 'superDrag' && message.phase === 'drop') };
}

const lRoutes = [
    ['upRight', 0, -70, 70, 0], ['upLeft', 0, -70, -70, 0],
    ['rightUp', 70, 0, 0, -70], ['rightDown', 70, 0, 0, 70],
    ['downRight', 0, 70, 70, 0], ['downLeft', 0, 70, -70, 0],
    ['leftUp', -70, 0, 0, -70], ['leftDown', -70, 0, 0, 70]
];
for (const kind of ['text', 'link', 'image']) for (const [route, x1, y1, x2, y2] of lRoutes) {
    test(`L字: ${kind}/${route}は登録した経路を一度だけ実行する`, () => {
        const h = createSuperDragHost({ [kind[0].toUpperCase() + kind.slice(1)]: { [route]: 'Copy' } });
        h.start(kind); h.emit('dragover', { screenX: 100 + x1, screenY: 100 + y1 });
        h.emit('dragover', { screenX: 100 + x1 + x2, screenY: 100 + y1 + y2 });
        h.emit('drop', { screenX: 100 + x1 + x2, screenY: 100 + y1 + y2 }); h.emit('dragend');
        assert.equal(h.drops().length, 1); assert.equal(h.drops()[0].direction, route);
        assert.equal(h.messages.filter(message => message.phase === 'cancel').length, 0, '成功後に検索先メニューを取り消さない');
    });
}
for (const scenario of ['unregistered', 'reverse', 'third', 'jitter', 'diagonal', 'label', 'markdown']) {
    test('L字: 誤実行防止と経路表示 ' + scenario, () => {
        const h = createSuperDragHost({ Link: { Right: 'Background', RightDown: scenario === 'unregistered' ? 'None' : scenario === 'markdown' ? 'CopyMarkdown' : 'Copy' } },
            '架空リンク', { link: { Copy: 'URLをコピー' } });
        h.start('link'); h.emit('dragover', { screenX: 170 });
        if (scenario === 'diagonal') { h.emit('dragend'); h.start(); h.emit('dragover', { screenX: 170, screenY: 170 }); }
        else if (scenario === 'reverse') h.emit('dragover', { screenX: 100 });
        else if (scenario === 'jitter') h.emit('dragover', { screenX: 172, screenY: 120 });
        else h.emit('dragover', { screenX: 172, screenY: 170 });
        if (scenario === 'third') h.emit('dragover', { screenX: 240, screenY: 170 });
        if (scenario === 'label') assert.match(h.hints.at(-1).textContent, /→↓ URLをコピー/);
        if (scenario === 'unregistered') assert.match(h.hints.at(-1).textContent, /→↓ 未登録/);
        const end = scenario === 'reverse' ? { screenX: 100 } : scenario === 'jitter' ? { screenX: 172, screenY: 120 }
            : scenario === 'third' ? { screenX: 240, screenY: 170 } : { screenX: 172, screenY: 170 };
        h.emit('drop', end);
        if (['unregistered', 'reverse', 'third', 'diagonal'].includes(scenario)) assert.equal(h.drops().length, 0);
        else {
            assert.equal(h.drops().length, 1); assert.equal(h.drops()[0].direction, scenario === 'jitter' ? 'right' : 'rightDown');
            if (scenario === 'markdown') assert.equal(h.drops()[0].linkText, '架空リンク');
        }
    });
}

for (const [kind, action] of [
    ['text', 'SearchYouTubeForeground'], ['text', 'SearchXForeground'],
    ['text', 'SearchChatGPT'], ['text', 'SearchChatGPTForeground'],
    ['text', 'SearchGoogle'], ['text', 'SearchGoogleForeground'],
    ['text', 'SearchBing'], ['text', 'SearchBingForeground'],
    ['text', 'SearchWikipedia'], ['text', 'SearchWikipediaForeground'],
    ['text', 'OpenUrlForeground'], ['text', 'OpenUrlBackground'],
    ['link', 'SplitRight'], ['link', 'SplitBottom'], ['link', 'CopyMarkdown']]) {
    test(`スーパードラッグ: 追加割り当て ${kind}/${action}も同じ製品drop経路を通る`, () => {
        const h = createSuperDragHost({ [kind === 'text' ? 'Text' : 'Link']: { Up: action } });
        h.start(kind);
        h.emit('dragover', { screenY: 40 }); h.emit('drop', { screenY: 40 }); h.emit('dragend');
        assert.equal(h.drops().length, 1); assert.equal(h.drops()[0].direction, 'up');
        assert.equal(h.drops()[0].kind, kind);
        if (action === 'CopyMarkdown') assert.equal(h.drops()[0].linkText, '架空 [リンク]');
        else assert.equal(h.drops()[0].linkText, undefined);
    });
}
test('スーパードラッグ: Markdownのリンク名は開始時に固定し160文字に制限する', () => {
    const h = createSuperDragHost({ Link: { Up: 'CopyMarkdown', Right: 'Copy' } }, 'a'.repeat(300));
    h.start('link'); h.emit('drop', { screenY: 40 });
    assert.equal(h.drops()[0].linkText, 'a'.repeat(160));
    h.start('link'); h.emit('drop', { screenX: 160 });
    assert.equal(h.drops()[1].linkText, undefined);
});

for (const kind of ['text', 'link', 'image']) for (const [direction, dx, dy] of [
    ['up', 0, -60], ['right', 60, 0], ['down', 0, 60], ['left', -60, 0]]) {
    test(`スーパードラッグ: ${kind}/${direction}は実際の開始・移動・drop経路で一度だけ送る`, () => {
        const h = createSuperDragHost(); h.start(kind);
        const over = h.emit('dragover', { screenX: 100 + dx, screenY: 100 + dy });
        assert.equal(over.defaultPrevented, true); assert.equal(h.hints.length, 1);
        h.emit('drop', { screenX: 100 + dx, screenY: 100 + dy });
        h.emit('drop', { screenX: 100 + dx, screenY: 100 + dy }); h.emit('dragend');
        assert.equal(h.drops().length, 1); assert.equal(h.drops()[0].kind, kind);
        assert.equal(h.drops()[0].direction, direction); assert.equal(h.drops()[0].tabBrowserToken, 'fixture-token');
        assert.equal(h.hints[0].removed, true);
    });
}
for (const scenario of ['disabled', 'excluded', 'editable-source', 'editable-drop', 'shadow-source', 'shadow-drop', 'site-drop',
    'short', 'diagonal', 'modifier', 'external-file', 'escape', 'blur', 'pagehide', 'dragend', 'hidden', 'untrusted', 'none']) {
    test('スーパードラッグ: 通常操作と取消を維持 ' + scenario, () => {
        const h = createSuperDragHost({ IsEnabled: scenario !== 'disabled',
            ExcludedHosts: scenario === 'excluded' ? ['example.invalid'] : [],
            ...(scenario === 'none' ? { Link: { Right: 'None' } } : {}) });
        h.start('link', { target: new h.Element('link', scenario === 'editable-source'),
            ...(scenario === 'shadow-source' ? { composedPath: () => [new h.Element('input', true)] } : {}) });
        if (scenario === 'escape') h.emit('keydown', { key: 'Escape' });
        if (['blur', 'pagehide', 'dragend'].includes(scenario)) h.emit(scenario);
        if (scenario === 'hidden') { h.context.document.hidden = true; h.emit('visibilitychange', {}, 'document'); }
        const event = { screenX: scenario === 'short' ? 110 : 160, screenY: scenario === 'diagonal' ? 160 : 100,
            target: new h.Element('body', scenario === 'editable-drop'), defaultPrevented: scenario === 'site-drop',
            ...(scenario === 'shadow-drop' ? { composedPath: () => [new h.Element('input', true)] } : {}),
            ctrlKey: scenario === 'modifier', isTrusted: scenario !== 'untrusted',
            dataTransfer: { types: scenario === 'external-file' ? ['Files'] : ['text/uri-list'] } };
        h.emit('dragover', event); const dropped = h.emit('drop', event);
        assert.equal(h.drops().length, 0);
        assert.equal(dropped.defaultPrevented, scenario === 'site-drop');
    });
}

test('スーパードラッグ: 外へ出るだけでは通常の外部ドロップを取り消さず、戻れば同じドラッグを扱う', () => {
    const h = createSuperDragHost(); assert.equal(h.start().defaultPrevented, false);
    h.emit('dragover', { screenX: 160 }); h.emit('dragleave', { relatedTarget: null });
    assert.equal(h.hints[0].removed, true); assert.equal(h.drops().length, 0);
    h.emit('drop', { screenX: 160 }); assert.equal(h.drops().length, 1);
});

test('スーパードラッグ: 認証済み設定更新で途中の要求を取消す', () => {
    const h = createSuperDragHost(); h.start(); h.context.__tabBrowserGestureHostUpdate({}, [], 'fixture-token');
    h.emit('drop', { screenX: 160 }); assert.equal(h.drops().length, 0);
});

test('スーパードラッグ: 実Chromeのネイティブドラッグ経路とページ側の優先', { timeout: 60000 }, async t => {
    const { spawn } = require('node:child_process');
    const { mkdtemp, rm } = require('node:fs/promises');
    const { join } = require('node:path');
    const { tmpdir } = require('node:os');
    const { once } = require('node:events');
    const { createServer } = require('node:http');
    const { waitForChromeEndpoint } = require('./ChromeTestLauncher.cjs');
    const server = createServer((request, response) => {
        if (request.url === '/pixel') {
            response.setHeader('Content-Type', 'image/svg+xml');
            response.end('<svg xmlns="http://www.w3.org/2000/svg" width="80" height="40"><rect width="80" height="40" fill="blue"/></svg>');
            return;
        }
        response.setHeader('Content-Type', 'text/html; charset=utf-8');
        response.end('<!doctype html><body style="margin:0;height:800px">'
            + '<a id="link" href="https://target.invalid/" style="position:absolute;left:30px;top:30px;width:100px;height:40px">架空リンク<img id="linkedImage" src="/pixel" style="position:absolute;left:70px;top:0;width:25px;height:25px"></a>'
            + '<span id="text" style="position:absolute;left:30px;top:130px">選択した架空テキスト</span>'
            + '<img id="image" alt="画像" src="/pixel" style="position:absolute;left:30px;top:230px;width:80px;height:40px">'
            + '<input id="input" style="position:absolute;left:400px;top:20px;width:200px;height:60px">'
            + '<div id="shadowInput" style="position:absolute;left:400px;top:230px;width:200px;height:60px"></div>'
            + '<script>const input=document.createElement("input");input.style.cssText="width:200px;height:60px";document.getElementById("shadowInput").attachShadow({mode:"open"}).appendChild(input);</script>'
            + '<div id="site" style="position:absolute;left:400px;top:120px;width:200px;height:80px" '
            + 'ondragover="event.preventDefault()" ondrop="event.preventDefault();window.siteDropped=true">サイト領域</div>');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const profile = await mkdtemp(join(tmpdir(), 'kiten-superdrag-browser-'));
    const browser = spawn(process.env.KITEN_BROWSER_TEST_CHROME || join(process.env.ProgramFiles,
        'Google/Chrome/Application/chrome.exe'), ['--headless=new', '--no-first-run', '--no-default-browser-check',
        '--disable-background-networking', '--disable-extensions', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'],
        { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
    let socket, sessionId, nextId = 0, intercepted;
    const pending = new Map();
    const call = (method, params = {}, session = sessionId) => new Promise((resolve, reject) => {
        const id = ++nextId, timeout = setTimeout(() => { pending.delete(id); reject(new Error(method + ': timeout')); }, 10000);
        pending.set(id, { resolve, reject, timeout }); socket.send(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }));
    });
    const evaluate = async expression => {
        const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
        assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result.value;
    };
    try {
        const { endpoint } = await waitForChromeEndpoint(browser, profile);
        socket = new WebSocket(endpoint); await once(socket, 'open');
        socket.addEventListener('message', event => {
            const data = JSON.parse(event.data);
            if (data.method === 'Input.dragIntercepted') intercepted?.(data.params.data);
            const request = pending.get(data.id); if (!request) return;
            pending.delete(data.id); clearTimeout(request.timeout);
            if (data.error) request.reject(new Error(JSON.stringify(data.error))); else request.resolve(data.result);
        });
        const { targetId } = await call('Target.createTarget', { url: 'about:blank' });
        ({ sessionId } = await call('Target.attachToTarget', { targetId, flatten: true }));
        await call('Page.enable'); await call('Input.setInterceptDrags', { enabled: true });
        const config = createSuperDragHost().settings;
        for (const kind of ['Text', 'Link', 'Image']) config.superDrag[kind].RightDown = 'Copy';
        await call('Page.addScriptToEvaluateOnNewDocument', { source: 'window.dragMessages=[];window.dragEvents=[];for(const type of ["dragstart","dragover","drop","dragend","blur"])addEventListener(type,e=>dragEvents.push({type,x:e.screenX,y:e.screenY,clientX:e.clientX,clientY:e.clientY,target:e.target?.tagName,prevented:e.defaultPrevented}),true);window.chrome={webview:{postMessage:m=>dragMessages.push(m)}};'
            // YouTubeの実サイトへ接続せず、URLだけを架空値にした実DOMで右クリックの伝播を検証する。
            + '(()=>{const location=window.fixtureLocation=window.location.search.includes("youtube=1")?{hostname:"www.youtube.com",protocol:"https:",href:"https://www.youtube.com/watch?v=fixture"}:window.location;'
            + 'const performance=window.fixtureClock={value:0,now(){return this.value}};'
            + 'const __tabBrowserWebMessageToken="fixture-token";const __tabBrowserGestureSettings=' + JSON.stringify(config)
            + ';const __tabBrowserGestureCommands=location===window.location?[]:null;' + source + '})();' });
        await call('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/` });
        await evaluate('new Promise(resolve=>document.readyState==="complete"?resolve():addEventListener("load",resolve,{once:true}))');
        async function drag(kind, targetX, targetY, waypoints = []) {
            await evaluate('dragMessages=[];dragEvents=[];window.siteDropped=false;window.getSelection().removeAllRanges()');
            if (kind === 'text') await evaluate('(()=>{const range=document.createRange();range.selectNodeContents(document.getElementById("text"));getSelection().addRange(range)})()');
            const point = await evaluate(`(()=>{const r=document.getElementById(${JSON.stringify(kind)}).getBoundingClientRect();return {x:r.left+8,y:r.top+12}})()`);
            let timeout;
            const dataTask = new Promise((resolve, reject) => {
                timeout = setTimeout(() => reject(new Error('ネイティブdragstart未到達: ' + kind)), 3000); intercepted = resolve;
            });
            try {
                await call('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
                await call('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', buttons: 1, clickCount: 1 });
                await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x + 12, y: point.y + 1, button: 'left', buttons: 1 });
                const data = await dataTask;
                await call('Input.dispatchDragEvent', { type: 'dragEnter', x: targetX, y: targetY, data });
                for (const point of [...waypoints, { x: targetX, y: targetY }])
                    await call('Input.dispatchDragEvent', { type: 'dragOver', ...point, data });
                await call('Input.dispatchDragEvent', { type: 'drop', x: targetX, y: targetY, data });
                await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: targetX, y: targetY, button: 'left', buttons: 0, clickCount: 1 });
                return evaluate('({messages:dragMessages,events:dragEvents,siteDropped:window.siteDropped})');
            } finally { clearTimeout(timeout); intercepted = null; }
        }
        for (const kind of ['link', 'text', 'image']) await t.test(kind + 'の通常領域dropが実行要求になる', async () => {
            const result = await drag(kind, 300, { link: 42, text: 142, image: 242 }[kind]);
            const drops = result.messages.filter(message => message.phase === 'drop');
            assert.equal(drops.length, 1, JSON.stringify(result)); assert.equal(drops[0].kind, kind);
        });
        await t.test('リンク付き画像はリンクとして扱う', async () => {
            const result = await drag('linkedImage', 300, 42);
            const drops = result.messages.filter(message => message.phase === 'drop');
            assert.equal(drops.length, 1, JSON.stringify(result)); assert.equal(drops[0].kind, 'link');
        });
        for (const kind of ['link', 'text', 'image']) await t.test('L字のネイティブdrop ' + kind, async () => {
            const result = await drag(kind, 300, 340, [{ x: 300, y: { link: 42, text: 142, image: 242 }[kind] }]);
            const drops = result.messages.filter(message => message.phase === 'drop');
            assert.equal(drops.length, 1, JSON.stringify(result)); assert.equal(drops[0].direction, 'rightDown');
        });
        await t.test('未登録L字のネイティブdropは実行しない', async () => {
            const result = await drag('text', 300, 60, [{ x: 300, y: 142 }]);
            assert.equal(result.messages.filter(message => message.phase === 'drop').length, 0, JSON.stringify(result));
        });
        await t.test('入力欄へのdropはネイティブのまま', async () => {
            const result = await drag('link', 460, 50); assert.equal(result.messages.filter(message => message.phase === 'drop').length, 0);
        });
        await t.test('Shadow DOM内の入力欄へのdropを奪わない', async () => {
            const result = await drag('link', 460, 250);
            assert.equal(result.messages.filter(message => message.phase === 'drop').length, 0);
            assert.match(await evaluate('document.getElementById("shadowInput").shadowRoot.querySelector("input").value'), /target\.invalid/);
        });
        await t.test('サイトのdrop処理が優先される', async () => {
            const result = await drag('link', 460, 150); assert.equal(result.siteDropped, true);
            assert.equal(result.messages.filter(message => message.phase === 'drop').length, 0);
        });
        for (const scenario of ['second', 'native-down-race', 'native-finish-first', 'native-finish-last', 'drag', 'wheel', 'escape', 'left', 'hidden',
            'navigation', 'replacement', 'outside', 'link', 'controls', 'editable', 'other-site', 'no-open-menu', 'long-press',
            'third-video', 'third-menu', 'fourth-menu', 'third-hidden-menu', 'third-invisible-menu', 'third-transparent-menu',
            'third-dismissed-menu', 'third-selection', 'third-drag', 'third-wheel', 'third-long-press',
            'portal-same-point', 'portal-inside', 'portal-fourth', 'portal-hidden', 'portal-dismissed', 'portal-navigation',
            'portal-replacement', 'portal-other-player', 'portal-drag', 'portal-wheel']) {
            await t.test('YouTube公式メニュー: ' + scenario, async () => {
                await call('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/?youtube=${scenario === 'other-site' ? 0 : 1}` });
                await evaluate('new Promise(resolve=>document.readyState==="complete"?resolve():addEventListener("load",resolve,{once:true}))');
                await evaluate(`document.body.innerHTML='<div class="html5-video-player" style="position:absolute;left:20px;top:20px;width:500px;height:300px"><video style="width:100%;height:100%"></video><a href="https://example.invalid/" style="position:absolute;left:10px;top:10px">リンク</a><button style="position:absolute;left:300px;top:10px">操作</button><input style="position:absolute;left:300px;top:60px"></div>';
                    window.siteContexts=[];document.addEventListener('contextmenu',e=>{
                        siteContexts.push({trusted:e.isTrusted,defaultPrevented:e.defaultPrevented});
                        if(!document.querySelector('.ytp-contextmenu')) e.preventDefault();
                    });dragMessages=[];`);
                const portal = scenario.startsWith('portal-');
                const repeat = portal || scenario.startsWith('third-') || scenario === 'fourth-menu';
                if (repeat) await evaluate(`document.addEventListener('contextmenu',()=>{
                    if(document.querySelector('.ytp-contextmenu')) return;
                    const menu=document.createElement('div');menu.className='ytp-contextmenu';
                    menu.style.cssText='position:absolute;left:${portal ? 151 : 100}px;top:${portal ? 151 : 100}px;width:180px;height:100px;background:#222';
                    menu.innerHTML='<button style="width:100%;height:100%">架空の公式メニュー</button>';
                    ${portal ? 'document.body' : "document.querySelector('.html5-video-player')"}.appendChild(menu);
                })`);
                const point = scenario === 'link' ? { x: 35, y: 35 } : scenario === 'controls' ? { x: 335, y: 35 }
                    : scenario === 'editable' ? { x: 335, y: 85 } : { x: 150, y: 150 };
                const press = p => call('Input.dispatchMouseEvent', { type: 'mousePressed', ...p, button: 'right', buttons: 2, clickCount: 1 });
                const release = p => call('Input.dispatchMouseEvent', { type: 'mouseReleased', ...p, button: 'right', buttons: 0, clickCount: 1 });
                await press(point); await release(point);
                const menus = await evaluate('dragMessages.filter(m=>m.type==="pageContextMenu")');
                assert.equal(menus.length, 1);
                assert.equal(menus[0].isYouTubeVideo, !['link', 'controls', 'editable', 'other-site'].includes(scenario));
                if (scenario === 'left') {
                    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: 600, y: 350, button: 'left', buttons: 1, clickCount: 1 });
                    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 600, y: 350, button: 'left', buttons: 0, clickCount: 1 });
                }
                if (scenario === 'hidden') await evaluate('Object.defineProperty(document,"hidden",{value:true,configurable:true});document.dispatchEvent(new Event("visibilitychange"));Object.defineProperty(document,"hidden",{value:false,configurable:true})');
                if (scenario === 'navigation') await evaluate('fixtureLocation.href+="&next=1"');
                if (scenario === 'replacement') await evaluate('document.querySelector("video").replaceWith(document.querySelector("video").cloneNode())');
                const secondPoint = scenario === 'outside' ? { x: 600, y: 350 } : point;
                if (scenario === 'native-down-race') await press(secondPoint);
                // WPF側のメニュー表示中に行う、認証付きの既存引き継ぎAPIを通す。
                await evaluate(`window.__tabBrowserGestureHostStartAt("fixture-token",${secondPoint.x},${secondPoint.y},${scenario !== 'no-open-menu' && menus[0].isYouTubeVideo})`);
                if (scenario !== 'native-down-race') await press(secondPoint);
                if (scenario === 'drag') {
                    await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 240, y: 150, buttons: 2 });
                }
                if (scenario === 'wheel') await call('Input.dispatchMouseEvent', { type: 'mouseWheel', ...secondPoint, buttons: 2, deltaX: 0, deltaY: 100 });
                if (scenario === 'escape') {
                    await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
                    await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
                }
                if (scenario === 'long-press') await evaluate('fixtureClock.value=1000');
                if (scenario === 'native-finish-first') await evaluate(`window.__tabBrowserGestureHostFinishAt("fixture-token",${secondPoint.x},${secondPoint.y})`);
                await release(scenario === 'drag' ? { x: 240, y: 150 } : secondPoint);
                if (scenario === 'native-finish-last') await evaluate(`window.__tabBrowserGestureHostFinishAt("fixture-token",${secondPoint.x},${secondPoint.y})`);
                const result = await evaluate('({contexts:siteContexts,messages:dragMessages})');
                const allowed = repeat || ['second', 'native-down-race', 'native-finish-first', 'native-finish-last'].includes(scenario);
                assert.equal(result.contexts.length, allowed ? 1 : 0, JSON.stringify(result));
                if (allowed) {
                    assert.equal(result.contexts[0].trusted, true);
                    assert.equal(result.contexts[0].defaultPrevented, false, '2回目は通常どおりYouTubeのハンドラーへ渡す');
                    assert.equal(result.messages.filter(m => m.type === 'pageContextMenu').length, 1, 'Kitenメニューを重ねない');
                }
                if (scenario === 'drag') assert.equal(result.messages.filter(m => m.type === 'gesture' && m.command === 'Forward').length, 1);
                if (scenario === 'wheel') assert.equal(result.messages.filter(m => m.type === 'gesture' && m.route === 'wheelDown').length, 1);
                if (repeat) {
                    if (['third-hidden-menu', 'portal-hidden'].includes(scenario)) await evaluate(`document.querySelector('.ytp-contextmenu').style.display='none'`);
                    if (scenario === 'third-invisible-menu') await evaluate(`document.querySelector('.ytp-contextmenu').style.visibility='hidden'`);
                    if (scenario === 'third-transparent-menu') await evaluate(`document.querySelector('.ytp-contextmenu').style.opacity='0'`);
                    if (scenario === 'third-selection') await evaluate(`(()=>{const range=document.createRange();range.selectNodeContents(document.querySelector('a'));getSelection().addRange(range)})()`);
                    if (['third-dismissed-menu', 'portal-dismissed'].includes(scenario)) {
                        await call('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
                        await call('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
                        await evaluate(`document.querySelector('.ytp-contextmenu').remove()`);
                    }
                    if (scenario === 'portal-navigation') await evaluate('fixtureLocation.href+="&next=1"');
                    if (scenario === 'portal-replacement') await evaluate('document.querySelector("video").replaceWith(document.querySelector("video").cloneNode())');
                    if (scenario === 'portal-other-player') await evaluate(`(()=>{const other=document.createElement('div');other.className='html5-video-player';other.style.cssText='position:absolute;left:600px;top:20px';other.innerHTML='<video></video>';document.body.appendChild(other);other.appendChild(document.querySelector('.ytp-contextmenu'))})()`);
                    const thirdPoint = ['third-menu', 'fourth-menu', 'portal-same-point', 'portal-fourth'].includes(scenario) ? point
                        : portal && !['portal-hidden', 'portal-dismissed', 'portal-navigation', 'portal-replacement', 'portal-other-player'].includes(scenario) ? { x: 170, y: 170 } : { x: 380, y: 180 };
                    await press(thirdPoint);
                    if (['third-drag', 'portal-drag'].includes(scenario)) await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 480, y: 180, buttons: 2 });
                    if (['third-wheel', 'portal-wheel'].includes(scenario)) await call('Input.dispatchMouseEvent', { type: 'mouseWheel', ...thirdPoint, buttons: 2, deltaX: 0, deltaY: 100 });
                    if (scenario === 'third-long-press') await evaluate('fixtureClock.value=1000');
                    await release(['third-drag', 'portal-drag'].includes(scenario) ? { x: 480, y: 180 } : thirdPoint);
                    if (['fourth-menu', 'portal-fourth'].includes(scenario)) { await press(thirdPoint); await release(thirdPoint); }
                    const repeated = await evaluate('({contexts:siteContexts,messages:dragMessages})');
                    const kitenAgain = ['third-hidden-menu', 'third-invisible-menu', 'third-transparent-menu', 'third-dismissed-menu', 'third-selection', 'third-long-press',
                        'portal-hidden', 'portal-dismissed', 'portal-navigation', 'portal-replacement', 'portal-other-player'].includes(scenario);
                    const gesture = ['third-drag', 'third-wheel', 'portal-drag', 'portal-wheel'].includes(scenario);
                    assert.equal(repeated.messages.filter(m => m.type === 'pageContextMenu').length, kitenAgain ? 2 : 1, '3回目以降にKitenメニューを重ねない');
                    assert.equal(repeated.contexts.length, kitenAgain || gesture ? 1 : ['fourth-menu', 'portal-fourth'].includes(scenario) ? 3 : 2);
                    assert.ok(repeated.contexts.every(context => context.trusted));
                    if (!kitenAgain && !gesture) assert.ok(repeated.contexts.slice(1).every(context => context.defaultPrevented), 'ブラウザー標準メニューへのフォールバックも抑止する');
                    if (['third-drag', 'portal-drag'].includes(scenario)) assert.equal(repeated.messages.filter(m => m.type === 'gesture' && m.command === 'Forward').length, 1);
                    if (['third-wheel', 'portal-wheel'].includes(scenario)) assert.equal(repeated.messages.filter(m => m.type === 'gesture' && m.route === 'wheelDown').length, 1);
                }
            });
        }
    } finally {
        if (socket?.readyState === WebSocket.OPEN) {
            const exited = browser.exitCode === null ? once(browser, 'exit') : Promise.resolve();
            await call('Browser.close', {}, null).catch(() => {});
            await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]); socket.close();
        }
        if (browser.exitCode === null) browser.kill();
        for (const request of pending.values()) clearTimeout(request.timeout);
        server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
        if (resolve(profile).startsWith(resolve(tmpdir()) + require('node:path').sep)
            && require('node:path').basename(profile).startsWith('kiten-superdrag-browser-'))
            await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
});

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
