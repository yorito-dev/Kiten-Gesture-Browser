// 接続先通知が欠ける場合と本当の起動失敗を、Chrome本体に依存せず区別する回帰。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { mkdtemp, writeFile, readFile, mkdir, rm } = require('node:fs/promises');
const { join, resolve, basename, sep } = require('node:path');
const { tmpdir } = require('node:os');
const { waitForChromeEndpoint } = require('./ChromeTestLauncher.cjs');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

async function fixture(t) {
    const profile = await mkdtemp(join(tmpdir(), 'tabbrowser-launcher-test-'));
    t.after(async () => {
        assert.ok(resolve(profile).startsWith(resolve(tmpdir()) + sep));
        assert.ok(basename(profile).startsWith('tabbrowser-launcher-test-'));
        await rm(profile, { recursive: true, force: true });
    });
    const browser = new EventEmitter();
    browser.pid = 42;
    browser.exitCode = null;
    browser.signalCode = null;
    browser.stderr = new EventEmitter();
    browser.stderr.resume = () => {};
    return { profile, browser, portFile: join(profile, 'DevToolsActivePort') };
}
function assertDetached(browser) {
    assert.equal(browser.listenerCount('error'), 0);
    assert.equal(browser.listenerCount('exit'), 0);
    assert.equal(browser.stderr.listenerCount('data'), 0);
}

test('分割されたstderr通知から接続先を取得し監視を解除する', async t => {
    const { browser, profile } = await fixture(t);
    const result = waitForChromeEndpoint(browser, profile);
    browser.stderr.emit('data', 'DevTools listening on ws://127.0.0.1:9222/devtools/brow');
    browser.stderr.emit('data', 'ser/abc-123\n');
    assert.equal((await result).endpoint, 'ws://127.0.0.1:9222/devtools/browser/abc-123');
    assert.equal((await result).source, 'stderr');
    assertDetached(browser);
});

test('stderrが空でもChromeの接続先fileから取得する', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await writeFile(portFile, '9223\r\n/devtools/browser/abc-456\r\n');
    const result = await waitForChromeEndpoint(browser, profile);
    assert.equal(result.endpoint, 'ws://127.0.0.1:9223/devtools/browser/abc-456');
    assert.equal(result.source, 'DevToolsActivePort');
    assert.equal(result.stderrBytes, 0);
    assertDetached(browser);
});

test('接続先fileの書込み途中を成功にせず完成を待つ', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await writeFile(portFile, '9224\n');
    const pending = waitForChromeEndpoint(browser, profile, { timeoutMs: 1000, pollIntervalMs: 5 });
    await new Promise(resolve => setTimeout(resolve, 15));
    await writeFile(portFile, '9224\n/devtools/browser/complete\n');
    assert.equal((await pending).endpoint, 'ws://127.0.0.1:9224/devtools/browser/complete');
    assertDetached(browser);
});

test('接続先fileの一時的なEBUSY後も実ファイルの正しい内容を確認する', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await writeFile(portFile, '9225\r\n/devtools/browser/after-busy\r\n');
    let reads = 0;
    const result = await waitForChromeEndpoint(browser, profile, {
        timeoutMs: 1000, pollIntervalMs: 5,
        readPortFile: async (path, encoding) => {
            assert.equal(path, portFile);
            assert.equal(encoding, 'utf8');
            if (++reads === 1) throw Object.assign(new Error('file busy'), { code: 'EBUSY' });
            return readFile(path, encoding);
        }
    });
    assert.equal(reads, 2);
    assert.equal(result.endpoint, 'ws://127.0.0.1:9225/devtools/browser/after-busy');
    assert.equal(result.source, 'DevToolsActivePort');
    assertDetached(browser);
});

test('接続先fileのEBUSYが続く場合も同じ期限で失敗し監視を解除する', async t => {
    const { browser, profile } = await fixture(t);
    let reads = 0;
    await assert.rejects(waitForChromeEndpoint(browser, profile, {
        timeoutMs: 60, pollIntervalMs: 5,
        readPortFile: async () => {
            reads++;
            throw Object.assign(new Error('file busy'), { code: 'EBUSY' });
        }
    }), /Chrome起動待ち:.*使用中（EBUSY）/);
    const finishedReads = reads;
    assert.ok(finishedReads >= 1);
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(reads, finishedReads);
    assertDetached(browser);
});

test('EBUSYが解消しても不正な接続先を成功にしない', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await writeFile(portFile, '70000\nws://outside.example/devtools/browser/invalid\n');
    let reads = 0;
    await assert.rejects(waitForChromeEndpoint(browser, profile, {
        timeoutMs: 60, pollIntervalMs: 5,
        readPortFile: async (path, encoding) => {
            if (++reads === 1) throw Object.assign(new Error('file busy'), { code: 'EBUSY' });
            return readFile(path, encoding);
        }
    }), /Chrome起動待ち:.*不完全または不正/);
    assertDetached(browser);
});

test('EBUSY以外の読取エラーは待機や成功にせず失敗する', async t => {
    const { browser, profile } = await fixture(t);
    let reads = 0;
    await assert.rejects(waitForChromeEndpoint(browser, profile, {
        readPortFile: async () => {
            reads++;
            throw Object.assign(new Error('access denied'), { code: 'EACCES' });
        }
    }), /Chrome接続先file読取失敗: EACCES/);
    assert.equal(reads, 1);
    assertDetached(browser);
});

test('不正なportや外部URLを接続先として受け入れない', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await writeFile(portFile, '70000\nws://outside.example/devtools/browser/invalid\n');
    await assert.rejects(waitForChromeEndpoint(browser, profile, { timeoutMs: 60, pollIntervalMs: 5 }), /Chrome起動待ち:.*不完全または不正/);
    assertDetached(browser);
});

test('接続先がないまま終了した場合は終了codeとsignalを含めて失敗する', async t => {
    const { browser, profile } = await fixture(t);
    const pending = waitForChromeEndpoint(browser, profile, { timeoutMs: 60, pollIntervalMs: 5 });
    browser.emit('exit', 17, 'SIGTERM');
    await assert.rejects(pending, /exitCode=17 signal=SIGTERM DevToolsActivePort=未作成/);
    assertDetached(browser);
});

test('spawnのerrorを起動失敗として伝え監視を解除する', async t => {
    const { browser, profile } = await fixture(t);
    const pending = waitForChromeEndpoint(browser, profile);
    browser.emit('error', new Error('ENOENT: chrome-not-found'));
    await assert.rejects(pending, /Chrome起動失敗: ENOENT: chrome-not-found/);
    assertDetached(browser);
});

test('通常logだけで接続先がない場合は成功にせず期限で失敗する', async t => {
    const { browser, profile } = await fixture(t);
    const pending = waitForChromeEndpoint(browser, profile, { timeoutMs: 60, pollIntervalMs: 5 });
    browser.stderr.emit('data', 'browser startup failed');
    await assert.rejects(pending, /Chrome起動待ち:.*stderr=browser startup failed/);
    assertDetached(browser);
});

test('接続先fileの読取errorを無視して成功にしない', async t => {
    const { browser, profile, portFile } = await fixture(t);
    await mkdir(portFile);
    await assert.rejects(waitForChromeEndpoint(browser, profile), /Chrome接続先file読取失敗:/);
    assertDetached(browser);
});

test('stderrを受信しない実Chromeでもfile経由で実CDP接続できる', async () => {
    const profile = await mkdtemp(join(tmpdir(), 'tabbrowser-launcher-test-'));
    const browser = spawn(process.env.KITEN_BROWSER_TEST_CHROME
        || join(process.env.ProgramFiles, 'Google/Chrome/Application/chrome.exe'), [
        '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-background-networking',
        '--disable-extensions', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
    ], { windowsHide: true, stdio: 'ignore' });
    let socket;
    try {
        const endpoint = await waitForChromeEndpoint(browser, profile);
        assert.equal(endpoint.source, 'DevToolsActivePort');
        assert.equal(endpoint.stderrBytes, 0);
        socket = new WebSocket(endpoint.endpoint);
        await once(socket, 'open', { signal: AbortSignal.timeout(10000) });
        const version = await new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Browser.getVersion待ち')), 10000);
            socket.addEventListener('message', event => {
                const message = JSON.parse(event.data);
                clearTimeout(timeout);
                if (message.id !== 1 || message.error) reject(new Error(JSON.stringify(message)));
                else resolve(message.result);
            }, { once: true });
            socket.send(JSON.stringify({ id: 1, method: 'Browser.getVersion' }));
        });
        assert.match(version.product, /Chrome\//);
        console.log(`stderrなし実接続: source=${endpoint.source} stderrBytes=${endpoint.stderrBytes} CDP=Browser.getVersion product=${version.product}`);
    } finally {
        if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 2, method: 'Browser.close' }));
        if (browser.exitCode === null) {
            await Promise.race([once(browser, 'exit'), new Promise(resolve => setTimeout(resolve, 3000))]);
        }
        socket?.close();
        if (browser.exitCode === null) browser.kill();
        assert.ok(resolve(profile).startsWith(resolve(tmpdir()) + sep));
        assert.ok(basename(profile).startsWith('tabbrowser-launcher-test-'));
        await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    }
});
