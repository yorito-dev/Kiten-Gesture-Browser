// 実行: node --test Tests/TabBrowser.Tests/YouTubeMiniPlayerQuality.browser.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { runInNewContext } = require('node:vm');

const source = readFileSync(resolve(__dirname, '../../Services/YouTubeControlScriptService.cs'), 'utf8');
const method = source.slice(source.indexOf('public static string BuildMiniPlayerSizeBasedQualityScript('));
const template = method.match(/return \$\$"""\r?\n([\s\S]*?)\r?\n\s*""";/)?.[1];
assert.ok(template, '製品の画質スクリプトを取得できる');

function fixture(enabled, cap) {
    const calls = [];
    const listeners = new Map();
    let videoId = 'video-a';
    let available = ['hd1080', 'hd2160', 'hd720'];
    const video = { readyState: 3 };
    const player = {
        getVideoData: () => ({ video_id: videoId }),
        getAvailableQualityLevels: () => available,
        setPlaybackQualityRange: (...args) => calls.push(['range', ...args]),
        setPlaybackQuality: value => calls.push(['quality', value])
    };
    const document = {
        getElementById: () => player,
        querySelector: selector => selector === 'video' ? video : player,
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    const context = { window: {}, document, location: { href: 'https://www.youtube.com/watch?v=video-a' },
        HTMLMediaElement: { HAVE_CURRENT_DATA: 2 } };
    const script = template.replaceAll('{{serializedEnabled}}', JSON.stringify(enabled))
        .replaceAll('{{serializedQualityCap}}', JSON.stringify(cap));
    const apply = () => runInNewContext(script, context);
    return { calls, listeners, apply, setVideoId: value => { videoId = value; },
        setAvailable: value => { available = value; },
        setReadyState: value => { video.readyState = value; } };
}

test('再生開始後に高い画質候補が現れたら上げ、低下方向へは再指定しない', () => {
    const f = fixture(false, 'highres');
    f.setAvailable(['hd720']);
    f.apply();
    assert.deepEqual(f.calls, [['range', 'hd720', 'hd720'], ['quality', 'hd720']]);
    f.setAvailable(['hd720', 'hd2160']);
    f.listeners.get('playing')();
    assert.deepEqual(f.calls.slice(2), [['range', 'hd2160', 'hd2160'], ['quality', 'hd2160']]);
    f.setAvailable(['hd720']);
    f.listeners.get('canplay')();
    assert.equal(f.calls.length, 4);
});

test('動画の準備前は画質を指定せず、再生開始時に試す', () => {
    const f = fixture(false, 'highres');
    f.setReadyState(0);
    f.apply();
    assert.deepEqual(f.calls, []);
    f.setReadyState(3);
    f.listeners.get('playing')();
    assert.deepEqual(f.calls, [['range', 'hd2160', 'hd2160'], ['quality', 'hd2160']]);
});

test('表示サイズ連動がオフなら最高画質を一度試し、次の動画にも適用する', () => {
    const f = fixture(false, 'highres');
    f.apply();
    assert.deepEqual(f.calls, [['range', 'hd2160', 'hd2160'], ['quality', 'hd2160']]);
    f.apply();
    assert.equal(f.calls.length, 2);
    f.setVideoId('video-b');
    f.listeners.get('yt-navigate-finish')();
    assert.deepEqual(f.calls.slice(2), [['range', 'hd2160', 'hd2160'], ['quality', 'hd2160']]);
});

test('手動で画質を選んだ動画は自動指定し直さない', () => {
    const f = fixture(false, 'highres');
    f.apply();
    f.listeners.get('pointerdown')({ type: 'pointerdown', target: { closest: selector =>
        selector === '.ytp-menuitem' ? { textContent: '720p', closest: () => ({}) } : null } });
    assert.deepEqual(f.calls.slice(2), [['range', 'auto', 'auto']]);
    f.listeners.get('canplay')();
    assert.equal(f.calls.length, 3);
    f.setVideoId('video-b');
    f.listeners.get('yt-navigate-finish')();
    assert.deepEqual(f.calls.slice(3), [['range', 'hd2160', 'hd2160'], ['quality', 'hd2160']]);
});

test('表示サイズ連動がオンなら指定上限を維持する', () => {
    const f = fixture(true, 'hd720');
    f.apply();
    assert.deepEqual(f.calls, [['range', 'hd720', 'hd720'], ['quality', 'hd720']]);
});
