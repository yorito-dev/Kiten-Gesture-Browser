// 本番が生成したJavaScriptを実行する、実サイトへ接続しないプレイヤーfixture。
const vm = require('node:vm');
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => input += chunk);
process.stdin.on('end', async () => {
    try {
        const request = JSON.parse(input);
        let activeId = request.playerId;
        const video = { readyState: 4, currentTime: 7, duration: 1000, playbackRate: 1,
            muted: false, volume: 1, loop: false, paused: true, ended: false,
            seekable: { length: 1, start: () => 0, end: () => 1000 },
            play() { this.paused = false; return Promise.resolve(); },
            pause() { this.paused = true; }, setAttribute() {}, removeAttribute() {},
            addEventListener() {}, removeEventListener() {} };
        const seeks = [];
        const player = { classList: { contains: () => false }, querySelector: () => null,
            getVideoData: () => ({ video_id: activeId, isLive: true }),
            getPlayerResponse: () => ({ videoDetails: { videoId: request.responseId || activeId,
                channelId: request.channelId, isLive: true }, playabilityStatus: { status: 'OK' } }),
            getPlaybackRate: () => video.playbackRate, setPlaybackRate: value => video.playbackRate = value,
            setVolume(value) { video.volume = value / 100; if (request.switchAfterVolume) activeId = 'abcdefghijk'; },
            mute: () => video.muted = true,
            unMute: () => video.muted = false, setLoop: value => video.loop = value,
            seekTo(value) { seeks.push(value); video.currentTime = value; },
            playVideo: () => video.play(), pauseVideo: () => video.pause() };
        const document = { getElementById: id => id === 'movie_player' ? player : null,
            querySelector: selector => selector === 'video' ? video
                : selector === '.html5-video-player' ? player : null,
            querySelectorAll: selector => selector === 'video' ? [video] : [],
            addEventListener() {}, removeEventListener() {} };
        const context = { document, location: { href: request.source }, URL,
            HTMLMediaElement: { HAVE_METADATA: 1 }, HTMLElement: class {},
            MutationObserver: class { observe() {} disconnect() {} },
            setTimeout, clearTimeout, requestAnimationFrame: callback => queueMicrotask(callback) };
        context.window = context;
        const result = vm.runInNewContext(request.script, context);
        await new Promise(resolve => setImmediate(resolve));
        const bridge = context.__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge;
        process.stdout.write(JSON.stringify({ result, applied: bridge?.applied === true,
            muted: video.muted, volume: video.volume, speed: video.playbackRate,
            position: video.currentTime, seeks,
            abRepeat: context.__tabBrowserYouTubeMiniPlayerAbRepeat || null }));
    } catch (error) { console.error(error); process.exitCode = 1; }
});
