// Chromeが実際に公開した接続先を、標準エラー出力または専用プロファイルから取得する。
const { readFile } = require('node:fs/promises');
const { join } = require('node:path');

function waitForChromeEndpoint(browser, profile, { timeoutMs = 15000, pollIntervalMs = 100, readPortFile = readFile } = {}) {
    return new Promise((resolve, reject) => {
        let settled = false, pollTimer;
        let stderr = '';
        let portFileState = '未作成';
        let exitCode = browser.exitCode, signal = browser.signalCode;
        const describe = () => `pid=${browser.pid ?? 'なし'} exitCode=${exitCode ?? '未終了'} signal=${signal ?? 'なし'} DevToolsActivePort=${portFileState} stderr=${stderr}`;
        const finish = (error, endpoint, source) => {
            if (settled) return;
            settled = true;
            clearTimeout(timeout);
            clearTimeout(pollTimer);
            browser.removeListener('error', onError);
            browser.removeListener('exit', onExit);
            browser.stderr?.removeListener('data', onData);
            browser.stderr?.resume();
            if (error) reject(error);
            else resolve({ endpoint, source, stderrBytes: Buffer.byteLength(stderr), exitCode, signal });
        };
        const onError = error => finish(new Error(`Chrome起動失敗: ${error.message}; ${describe()}`));
        // 終了状態を記録し、実際の接続先取得は同じ期限内で判定する。
        const onExit = (code, exitSignal) => { exitCode = code; signal = exitSignal; };
        const onData = chunk => {
            stderr = (stderr + chunk).slice(-16384);
            const match = stderr.match(/DevTools listening on (ws:\/\/[^\s]+)\r?\n/);
            if (match) finish(null, match[1], 'stderr');
        };
        const timeout = setTimeout(() => finish(new Error(`Chrome起動待ち: ${describe()}`)), timeoutMs);
        browser.once('error', onError);
        browser.once('exit', onExit);
        browser.stderr?.on('data', onData);
        const probePortFile = async () => {
            if (settled) return;
            try {
                const content = await readPortFile(join(profile, 'DevToolsActivePort'), 'utf8');
                if (settled) return;
                const [portText, browserPath] = content.trim().split(/\r?\n/);
                const port = Number(portText);
                if (/^\d+$/.test(portText) && port >= 1 && port <= 65535
                    && /^\/devtools\/browser\/[A-Za-z0-9-]+$/.test(browserPath ?? '')) {
                    portFileState = '取得済み';
                    finish(null, `ws://127.0.0.1:${port}${browserPath}`, 'DevToolsActivePort');
                    return;
                }
                // Chromeの書込み途中では、同じ期限内で完成を待つ。
                portFileState = '不完全または不正';
            } catch (error) {
                if (settled) return;
                if (error.code !== 'ENOENT' && error.code !== 'EBUSY') {
                    finish(new Error(`Chrome接続先file読取失敗: ${error.code}; ${describe()}`));
                    return;
                }
                // WindowsではChromeの書込み中に共有違反となる。成功扱いにせず同じ期限内で完成を待つ。
                portFileState = error.code === 'EBUSY' ? '使用中（EBUSY）' : '未作成';
            }
            if (!settled) pollTimer = setTimeout(probePortFile, pollIntervalMs);
        };
        void probePortFile();
    });
}

module.exports = { waitForChromeEndpoint };
