// 製品のJSをそのまま専用Chromeで実行する。ページ・画像・フレームは架空データのみ。
const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { readFile, mkdtemp, rm } = require('node:fs/promises');
const { join, resolve, basename, sep } = require('node:path');
const { tmpdir } = require('node:os');
const { once } = require('node:events');
const { waitForChromeEndpoint } = require('./ChromeTestLauncher.cjs');
let browser, socket, profile, sessionId, raw, pageHtml, sequence = 0, nextId = 0;
const pending = new Map();
const call = (method, params = {}, targetSession = sessionId) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(method + ': timeout')); }, 10000);
    pending.set(id, { resolve, reject, timer });
    socket.send(JSON.stringify({ id, method, params, ...(targetSession ? { sessionId: targetSession } : {}) }));
});
const evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
    return result.result.value;
};
async function until(expression) {
    const limit = Date.now() + 8000;
    while (Date.now() < limit) {
        try { if (await evaluate(expression)) return; } catch (error) { if (Date.now() >= limit) throw error; }
        await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.fail('表示条件が成立しません: ' + expression);
}
function product(url, { owner = 'fixture-owner', enabled = true, dispose = false } = {}) {
    return raw.replaceAll('{{urlJson}}', JSON.stringify(url)).replaceAll('{{ownerJson}}', JSON.stringify(owner))
        .replaceAll('{{enabledJson}}', JSON.stringify(enabled)).replaceAll('{{disposeJson}}', JSON.stringify(dispose));
}
async function reset(html, host = 'news.example', options = {}) {
    pageHtml = '<!doctype html><style>.ad-slot,.ad{height:180px;margin:24px}p{min-height:24px}</style>' + html;
    const url = `https://${host}/fixture?case=${++sequence}`;
    await call('Page.navigate', { url });
    await until(`location.href === ${JSON.stringify(url)} && document.readyState === 'complete'`);
    await evaluate(product(url, options));
    return url;
}
const hidden = id => `document.getElementById(${JSON.stringify(id)}).getBoundingClientRect().height === 0`;

before(async () => {
    const source = await readFile(resolve(__dirname, '../../Services/AdSpaceCollapseScript.cs'), 'utf8');
    raw = source.match(/return \$\$"""\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
    profile = await mkdtemp(join(tmpdir(), 'tabbrowser-ad-space-test-'));
    browser = spawn(process.env.KITEN_BROWSER_TEST_CHROME || join(process.env.ProgramFiles, 'Google/Chrome/Application/chrome.exe'),
        ['--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking',
            '--disable-extensions', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'],
        { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
    const { endpoint } = await waitForChromeEndpoint(browser, profile);
    socket = new WebSocket(endpoint);
    await new Promise((resolve, reject) => {
        socket.addEventListener('open', resolve, { once: true });
        socket.addEventListener('error', reject, { once: true });
    });
    socket.addEventListener('message', event => {
        const response = JSON.parse(event.data);
        if (response.method === 'Fetch.requestPaused') {
            const html = new URL(response.params.request.url).pathname === '/child'
                ? '<style>.ad-slot{height:180px}</style><div id="child-ad" class="ad-slot"></div>' : pageHtml;
            void call('Fetch.fulfillRequest', { requestId: response.params.requestId, responseCode: 200,
                responseHeaders: [{ name: 'Content-Type', value: 'text/html; charset=utf-8' }],
                body: Buffer.from(html).toString('base64') }, response.sessionId).catch(() => {});
            return;
        }
        const request = pending.get(response.id);
        if (!request) return;
        pending.delete(response.id); clearTimeout(request.timer);
        if (response.error) request.reject(new Error(JSON.stringify(response.error)));
        else request.resolve(response.result);
    });
    const { targetId } = await call('Target.createTarget', { url: 'about:blank' });
    ({ sessionId } = await call('Target.attachToTarget', { targetId, flatten: true }));
    await call('Page.enable');
    await call('Fetch.enable', { patterns: [{ urlPattern: '*' }] });
    await call('Emulation.setDeviceMetricsOverride', { width: 960, height: 720, deviceScaleFactor: 1, mobile: false });
}, { timeout: 30000 });
after(async () => {
    if (socket?.readyState === WebSocket.OPEN) {
        const exited = browser.exitCode === null ? once(browser, 'exit') : Promise.resolve();
        await call('Browser.close', {}, null).catch(() => {});
        await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]);
        socket.close();
    }
    if (browser?.exitCode === null) browser.kill();
    if (profile && resolve(profile).startsWith(resolve(tmpdir()) + sep) && basename(profile).startsWith('tabbrowser-ad-space-test-'))
        await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
});

for (const host of ['news.example', 'shop.example', 'pc.watch.impress.co.jp', 'xn--wgv71a119e.example']) {
    test(`サイト名で限定せず広告枠の実寸法を縮める: ${host}`, async () => {
        await reset('<div id="empty" class="ad-slot"></div><p id="article">本文</p>', host);
        assert.equal(await evaluate(hidden('empty')), true);
        assert.ok(await evaluate('article.getBoundingClientRect().height') > 0);
    });
}
test('共通の広告class・属性・GPT IDを判定し、親の広告枠の余白も除く', async () => {
    await reset('<aside id="outer" class="ad"><div id="gpt-div-fixture"><script>/* 広告用コード */</script></div></aside>'
        + '<ins id="ins" class="adsbygoogle" style="height:80px;display:inline-block"></ins>'
        + '<section id="slot" data-ad-slot="fixture" style="height:80px"></section>');
    for (const id of ['outer', 'gpt-div-fixture', 'ins', 'slot']) assert.equal(await evaluate(hidden(id)), true);
});
test('広告以外の空白と、本文・リンクを含む広告風の要素は残す', async () => {
    await reset('<div id="space" style="height:80px"></div><div id="text" class="ad">本文の記事</div>'
        + '<div id="link" class="ad"><a href="#">記事へ</a></div>');
    for (const id of ['space', 'text', 'link']) assert.equal(await evaluate(hidden(id)), false);
});
for (const child of ['img', 'video', 'audio', 'iframe', 'canvas', 'svg', 'input', 'button', 'form', 'article', 'main', 'nav']) {
    test(`中のコンテンツ・操作部を巻き込まない: ${child}`, async () => {
        const body = child === 'iframe' ? '<iframe srcdoc="動画の代替"></iframe>' : `<${child}></${child}>`;
        await reset(`<div id="protected" class="ad">${body}</div>`);
        assert.equal(await evaluate(hidden('protected')), false);
    });
}
test('広告という短いラベルだけは詰め、別の文は残す', async () => {
    await reset('<div id="label" class="ad">広告</div><div id="body" class="ad">広告に関する記事です</div>');
    assert.equal(await evaluate(hidden('label')), true);
    assert.equal(await evaluate(hidden('body')), false);
});
test('背景画像とCSS生成コンテンツを持つ枠は空と扱わない', async () => {
    await reset('<style>#pseudo:before{content:"本文です"}</style><div id="pseudo" class="ad"></div>'
        + '<div id="background" class="ad" style="background-image:linear-gradient(red,blue)"></div>');
    for (const id of ['pseudo', 'background']) assert.equal(await evaluate(hidden(id)), false);
});
test('後から本文が入った枠を表示へ戻す', async () => {
    await reset('<div id="empty" class="ad"></div>');
    await evaluate('empty.textContent="後から読み込んだ本文"');
    await until('!(' + hidden('empty') + ')');
});
test('YouTube等のフレームが入った瞬間から親枠を表示へ戻す', async () => {
    await reset('<div id="empty" class="ad"></div>');
    assert.equal(await evaluate(`empty.innerHTML='<iframe srcdoc="YouTube埋め込みの代替"></iframe>'; ${hidden('empty')}`), false);
});
test('動的に追加された広告枠とclass変更を追跡する', async () => {
    await reset('<div id="changing" style="height:180px"></div>');
    await evaluate('changing.className="ad";document.body.insertAdjacentHTML("beforeend",\'<div id="later" class="ad-slot"></div>\')');
    await until(hidden('changing') + ' && ' + hidden('later'));
    await evaluate('changing.className="ordinary"');
    await until('!(' + hidden('changing') + ')');
});
test('無効化で元の表示を戻し、サイト自身のstyle変更も保つ', async () => {
    const url = await reset('<div id="empty" class="ad" style="display:flex;height:180px"></div>');
    await evaluate('empty.style.display="grid"');
    await evaluate(product(url, { enabled: false }));
    assert.equal(await evaluate(hidden('empty')), false);
    assert.equal(await evaluate('getComputedStyle(empty).display'), 'grid');
    assert.equal(await evaluate('window.__tabBrowserAdSpaceCollapse'), undefined);
});
test('最初から無効なら表示やDOMを変更しない', async () => {
    await reset('<div id="empty" class="ad"></div>', 'news.example', { enabled: false });
    assert.equal(await evaluate(hidden('empty')), false);
    assert.equal(await evaluate('document.querySelectorAll("style").length'), 1);
});
test('同じ所有者の更新でobserverとstyleを二重に作らない', async () => {
    const url = await reset('<div id="empty" class="ad"></div>');
    await evaluate('window.savedAdSpace=window.__tabBrowserAdSpaceCollapse');
    await evaluate(product(url)); await evaluate(product(url));
    assert.equal(await evaluate('savedAdSpace === window.__tabBrowserAdSpaceCollapse'), true);
    assert.equal(await evaluate('document.querySelectorAll("style").length'), 2);
});
test('所有者の移動後に古い解放が来ても新しい表示制御を消さない', async () => {
    const url = await reset('<div id="empty" class="ad"></div>');
    await evaluate(product(url, { owner: 'new-owner' }));
    await evaluate(product(url, { dispose: true }));
    assert.equal(await evaluate(hidden('empty')), true);
    assert.equal(await evaluate('window.__tabBrowserAdSpaceCollapse.owner'), 'new-owner');
    await evaluate(product(url, { owner: 'new-owner', dispose: true }));
    assert.equal(await evaluate(hidden('empty')), false);
});
test('別のURLへ移動した後に古い要求を適用しない', async () => {
    await reset('<div id="empty" class="ad"></div>', 'news.example', { enabled: false });
    await evaluate(product('https://news.example/previous'));
    assert.equal(await evaluate(hidden('empty')), false);
});
for (const host of ['youtube.com', 'www.youtube.com', 'music.youtube.com', 'www.youtube-nocookie.com', 'youtu.be']) {
    test(`YouTubeのページには適用しない: ${host}`, async () => {
        await reset('<div id="empty" class="ad"></div>', host);
        assert.equal(await evaluate(hidden('empty')), false);
        assert.equal(await evaluate('window.__tabBrowserAdSpaceCollapse'), undefined);
    });
}
test('YouTubeに似た別ホストには通常の広告判定を適用する', async () => {
    await reset('<div id="empty" class="ad"></div>', 'notyoutube.com');
    assert.equal(await evaluate(hidden('empty')), true);
});
test('トップ以外のドキュメントでは実行しない', async () => {
    await reset('<iframe id="child" src="/child"></iframe>');
    await until('child.contentDocument?.readyState === "complete"');
    await evaluate('child.contentWindow.eval(' + JSON.stringify(product('https://news.example/child')) + ')');
    assert.ok(await evaluate('child.contentDocument.getElementById("child-ad").getBoundingClientRect().height') > 0);
});
test('変更の集中をまとめ、静止後にタイマーを回し続けない', async () => {
    await reset('<div id="empty" class="ad"></div>');
    await evaluate(`window.adSpaceTimers=0;window.adSpacePending=0;const original=setTimeout;
        window.setTimeout=(callback,delay,...args)=>{
            if(delay!==120)return original(callback,delay,...args);
            adSpaceTimers++;adSpacePending++;return original(()=>{adSpacePending--;callback(...args)},delay);
        };
        for(let i=0;i<80;i++)empty.append(document.createTextNode(' '));`);
    await until('adSpacePending === 0 && adSpaceTimers > 0');
    assert.ok(await evaluate('adSpaceTimers') <= 2);
});
test('大量の広告枠も処理を分割して折り畳む', async () => {
    await reset(Array.from({ length: 450 }, (_, i) => `<div id="ad-${i}" class="ad"></div>`).join(''));
    await until(hidden('ad-449'));
    assert.equal(await evaluate(hidden('ad-0')), true);
});
test('ページ離脱で解放し、復帰時は新しい初期化で再開できる', async () => {
    const url = await reset('<div id="empty" class="ad"></div>');
    await evaluate('dispatchEvent(new PageTransitionEvent("pagehide",{persisted:true}))');
    assert.equal(await evaluate(hidden('empty')), false);
    await evaluate(product(url));
    assert.equal(await evaluate(hidden('empty')), true);
});
test('Shadow DOMを持つ広告風の領域は保護する', async () => {
    const url = await reset('<div id="shadow" class="ad"></div>', 'news.example', { enabled: false });
    await evaluate('shadow.attachShadow({mode:"open"}).innerHTML="本文"');
    await evaluate(product(url));
    assert.equal(await evaluate(hidden('shadow')), false);
});
test('ARIA操作部やキーボードで操作できる空の領域も保護する', async () => {
    await reset('<div id="link-role" class="ad" role="link"></div><div id="focusable" class="ad" tabindex="0"></div>'
        + '<div id="details-slot" class="ad"><details><summary></summary></details></div>');
    for (const id of ['link-role', 'focusable', 'details-slot']) assert.equal(await evaluate(hidden(id)), false);
});
test('属性変更で操作部になった瞬間も表示を戻す', async () => {
    await reset('<div id="empty" class="ad"></div>');
    assert.equal(await evaluate(`empty.tabIndex=0; ${hidden('empty')}`), false);
});
