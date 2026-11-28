(() => {
    const video = document.querySelector("video");
    if (!video) {
        return {
            ok: false,
            hasVideo: false,
            hasVisibleVideo: false,
            videoNaturalWidth: 0,
            videoNaturalHeight: 0
        };
    }

    const rect = video.getBoundingClientRect?.();
    const position = Number(video.currentTime);
    const duration = Number(video.duration);
    let bufferedAhead = 0;
    try {
        if (Number.isFinite(position)) {
            for (let index = 0; index < video.buffered.length; index++) {
                const start = video.buffered.start(index);
                const end = video.buffered.end(index);
                if (position >= start && position <= end) {
                    bufferedAhead = Math.max(0, end - position);
                    break;
                }
            }
        }
    } catch {
    }

    return {
        ok: true,
        hasVideo: true,
        hasVisibleVideo: Boolean(rect && rect.width > 0 && rect.height > 0),
        videoNaturalWidth: Number(video.videoWidth) || 0,
        videoNaturalHeight: Number(video.videoHeight) || 0,
        position: Number.isFinite(position) ? position : null,
        duration: Number.isFinite(duration) ? duration : null,
        readyState: Number.isInteger(video.readyState) ? video.readyState : null,
        networkState: Number.isInteger(video.networkState) ? video.networkState : null,
        paused: Boolean(video.paused),
        ended: Boolean(video.ended),
        bufferedAhead,
        speed: Number(video.playbackRate) || 1,
        volume: Number(video.volume) || 0,
        muted: Boolean(video.muted),
        mediaError: Number.isInteger(video.error?.code) ? video.error.code : null
    };
})();
