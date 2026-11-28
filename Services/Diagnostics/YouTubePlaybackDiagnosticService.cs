using System.Globalization;
using System.Text.Json;

namespace TabBrowser.Services;

public sealed record YouTubePlaybackDiagnosticMessage(
    string? Token,
    string EventName,
    double? CurrentTimeSeconds,
    double? DurationSeconds,
    int? ReadyState,
    int? NetworkState,
    bool? IsPaused,
    bool? IsEnded,
    double? BufferedAheadSeconds,
    double? PlaybackRate,
    bool? IsMuted,
    double? Volume,
    bool? IsLooping,
    string? Reason,
    string VisibilityState,
    int? MediaErrorCode,
    long? TotalVideoFrames,
    long? DroppedVideoFrames,
    long? CorruptedVideoFrames,
    double? VideoNaturalWidth = null,
    double? VideoNaturalHeight = null,
    string? PlayerSurface = null,
    YouTubeMiniPlayerLayoutCounts? LayoutCounts = null,
    YouTubeMiniPlayerPausedLayoutProbe? PausedLayoutProbe = null);

public sealed record YouTubeMiniPlayerLayoutCounts(
    int Replacements, int Applied, int PlayerMissing, int VideoMissing, int StyleDrift, int ApplyErrors);

public sealed record YouTubeMiniPlayerLayoutDimensions(string Natural, string Video, string Parent, string Viewport);

public sealed record YouTubeMiniPlayerPausedLayoutProbe(
    string Reason, string Result, string PreviousNatural, YouTubeMiniPlayerLayoutDimensions Before,
    YouTubeMiniPlayerLayoutDimensions After, bool Replaced, bool Paused, bool Applied);

public sealed record YouTubePlaybackDiagnosticObservation(
    bool ShouldLog,
    string Operation,
    long? StallDurationMilliseconds = null);

public sealed record YouTubePlaybackFrameSample(
    string TargetId,
    DateTimeOffset ObservedAt,
    long? TotalVideoFrames,
    long? DroppedVideoFrames,
    long? CorruptedVideoFrames);

public enum YouTubePlaybackActivityState
{
    Unknown,
    Playing,
    Paused,
    Ended,
    Waiting,
    Failed
}

public sealed record YouTubePlaybackActivitySnapshot(
    YouTubePlaybackActivityState State,
    DateTimeOffset ObservedAt,
    DateTimeOffset? LastProgressAt,
    double? PositionSeconds);

public sealed class YouTubePlaybackDiagnosticTracker
{
    public const int DiagnosticHistoryCapacity = 40;
    private readonly Queue<RuntimeYouTubePlaybackDiagnosticEntry> _diagnosticHistory = new();
    private readonly Dictionary<string, DateTimeOffset> _stallStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _sampleStallStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeriodicSample> _lastPeriodicSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, YouTubePlaybackActivitySnapshot> _activitySnapshots = new(StringComparer.Ordinal);

    // 記録対象の許可は呼出元で判定する。観測状態のReset／Clearでは履歴を破棄しない。
    public void AddDiagnosticEntry(RuntimeYouTubePlaybackDiagnosticEntry entry)
    {
        _diagnosticHistory.Enqueue(entry);
        while (_diagnosticHistory.Count > DiagnosticHistoryCapacity)
        {
            _diagnosticHistory.Dequeue();
        }
    }

    public static IReadOnlyList<RuntimeYouTubePlaybackDiagnosticEntry> CaptureDiagnosticHistory(
        IEnumerable<YouTubePlaybackDiagnosticTracker> trackers)
    {
        return trackers
            .SelectMany(static tracker => tracker._diagnosticHistory)
            .OrderByDescending(static entry => entry.ObservedAt)
            .Take(DiagnosticHistoryCapacity)
            .OrderBy(static entry => entry.ObservedAt)
            .ToList();
    }

    public IReadOnlyList<RuntimeMiniPlayerFrameSample> CaptureMiniPlayerFrameSamples(int windowIndex)
    {
        return GetFrameSamples()
            .Where(static sample => sample.TargetId.StartsWith("mini-player:", StringComparison.Ordinal))
            .Select(sample => new RuntimeMiniPlayerFrameSample(
                sample.ObservedAt,
                windowIndex,
                sample.TargetId,
                sample.TotalVideoFrames,
                sample.DroppedVideoFrames,
                sample.CorruptedVideoFrames))
            .ToList();
    }

    public YouTubePlaybackDiagnosticObservation Observe(
        string targetId,
        YouTubePlaybackDiagnosticMessage message,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return new(false, "ignored");
        UpdateActivity(targetId, message, observedAt);
        if (message.EventName == "state" && message.TotalVideoFrames.HasValue)
        {
            if (message.Reason == "timeupdate") return ObservePeriodicSample(targetId, message, observedAt);
            _lastPeriodicSamples[targetId] = CreatePeriodicSample(message, observedAt);
        }
        if (string.Equals(message.EventName, "sample", StringComparison.Ordinal))
        {
            return ObservePeriodicSample(targetId, message, observedAt);
        }

        var observation = Observe(targetId, message.EventName, observedAt);
        switch (message.EventName)
        {
            case "playing":
            case "canplay":
                _sampleStallStartedAt.Remove(targetId);
                _lastPeriodicSamples[targetId] = CreatePeriodicSample(message, observedAt);
                break;

            case "pause":
            case "error":
            case "emptied":
                ResetPeriodicTracking(targetId);
                break;
        }

        return observation;
    }

    public YouTubePlaybackDiagnosticObservation Observe(
        string targetId,
        string eventName,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return new YouTubePlaybackDiagnosticObservation(false, "ignored");
        }

        switch (eventName)
        {
            case "waiting":
            case "stalled":
                _sampleStallStartedAt.Remove(targetId);
                if (_stallStartedAt.ContainsKey(targetId))
                {
                    return new YouTubePlaybackDiagnosticObservation(false, "stall-active");
                }

                _stallStartedAt[targetId] = observedAt;
                return new YouTubePlaybackDiagnosticObservation(true, "stall-start");

            case "playing":
            case "canplay":
                _sampleStallStartedAt.Remove(targetId);
                if (_stallStartedAt.Remove(targetId, out var startedAt))
                {
                    return new YouTubePlaybackDiagnosticObservation(
                        true,
                        "stall-recovered",
                        GetElapsedMilliseconds(startedAt, observedAt));
                }

                return new YouTubePlaybackDiagnosticObservation(
                    eventName == "playing",
                    eventName);

            case "emptied":
                _stallStartedAt.Remove(targetId);
                ResetPeriodicTracking(targetId);
                return new YouTubePlaybackDiagnosticObservation(true, eventName);

            case "pause":
            case "error":
                _stallStartedAt.Remove(targetId);
                ResetPeriodicTracking(targetId);
                return new YouTubePlaybackDiagnosticObservation(true, eventName);

            default:
                return new YouTubePlaybackDiagnosticObservation(false, "ignored");
        }
    }

    public long? ObserveProcessFailure(string targetId, DateTimeOffset observedAt)
    {
        _activitySnapshots[targetId] = new YouTubePlaybackActivitySnapshot(
            YouTubePlaybackActivityState.Failed,
            observedAt,
            null,
            null);
        DateTimeOffset? startedAt = null;
        if (_stallStartedAt.Remove(targetId, out var eventStallStartedAt))
        {
            startedAt = eventStallStartedAt;
        }

        if (_sampleStallStartedAt.Remove(targetId, out var sampleStallStartedAt)
            && (!startedAt.HasValue || sampleStallStartedAt < startedAt.Value))
        {
            startedAt = sampleStallStartedAt;
        }

        _lastPeriodicSamples.Remove(targetId);
        return startedAt.HasValue
            ? GetElapsedMilliseconds(startedAt.Value, observedAt)
            : null;
    }

    public void Reset(string targetId)
    {
        _stallStartedAt.Remove(targetId);
        _activitySnapshots.Remove(targetId);
        ResetPeriodicTracking(targetId);
    }

    public void Clear()
    {
        _stallStartedAt.Clear();
        _sampleStallStartedAt.Clear();
        _lastPeriodicSamples.Clear();
        _activitySnapshots.Clear();
    }

    public bool TryGetActivity(
        string targetId,
        out YouTubePlaybackActivitySnapshot snapshot)
    {
        return _activitySnapshots.TryGetValue(targetId, out snapshot!);
    }

    public IReadOnlyList<YouTubePlaybackFrameSample> GetFrameSamples()
    {
        return _lastPeriodicSamples
            .Select(static pair => new YouTubePlaybackFrameSample(
                pair.Key,
                pair.Value.ObservedAt,
                pair.Value.TotalVideoFrames,
                pair.Value.DroppedVideoFrames,
                pair.Value.CorruptedVideoFrames))
            .ToList();
    }

    private YouTubePlaybackDiagnosticObservation ObservePeriodicSample(
        string targetId,
        YouTubePlaybackDiagnosticMessage message,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return new YouTubePlaybackDiagnosticObservation(false, "ignored");
        }

        var current = CreatePeriodicSample(message, observedAt);
        if (message.IsPaused != false
            || message.IsEnded == true
            || !string.Equals(message.VisibilityState, "visible", StringComparison.OrdinalIgnoreCase)
            || message.CurrentTimeSeconds is null)
        {
            _sampleStallStartedAt.Remove(targetId);
            _lastPeriodicSamples[targetId] = current;
            return new YouTubePlaybackDiagnosticObservation(false, "sample-inactive");
        }

        if (_stallStartedAt.ContainsKey(targetId))
        {
            _sampleStallStartedAt.Remove(targetId);
            _lastPeriodicSamples[targetId] = current;
            return new YouTubePlaybackDiagnosticObservation(false, "event-stall-active");
        }

        if (!_lastPeriodicSamples.TryGetValue(targetId, out var previous))
        {
            _lastPeriodicSamples[targetId] = current;
            return new YouTubePlaybackDiagnosticObservation(false, "sample-baseline");
        }

        _lastPeriodicSamples[targetId] = current;
        if (HasPlaybackReset(previous, current))
        {
            _sampleStallStartedAt.Remove(targetId);
            return new YouTubePlaybackDiagnosticObservation(false, "sample-reset");
        }

        var positionAdvanced = current.PositionSeconds is { } currentPosition
            && previous.PositionSeconds is { } previousPosition
            && currentPosition - previousPosition >= 0.25;
        var framesAdvanced = current.TotalVideoFrames.HasValue
            && previous.TotalVideoFrames.HasValue
            && current.TotalVideoFrames.Value > previous.TotalVideoFrames.Value;
        if (positionAdvanced || framesAdvanced)
        {
            if (_sampleStallStartedAt.Remove(targetId, out var startedAt))
            {
                return new YouTubePlaybackDiagnosticObservation(
                    true,
                    "sample-stall-recovered",
                    GetElapsedMilliseconds(startedAt, observedAt));
            }

            return new YouTubePlaybackDiagnosticObservation(false, "sample-progressing");
        }

        if (_sampleStallStartedAt.ContainsKey(targetId))
        {
            return new YouTubePlaybackDiagnosticObservation(false, "sample-stall-active");
        }

        _sampleStallStartedAt[targetId] = previous.ObservedAt;
        return new YouTubePlaybackDiagnosticObservation(
            true,
            "sample-stall-start",
            GetElapsedMilliseconds(previous.ObservedAt, observedAt));
    }

    private void ResetPeriodicTracking(string targetId)
    {
        _sampleStallStartedAt.Remove(targetId);
        _lastPeriodicSamples.Remove(targetId);
    }

    private void UpdateActivity(
        string targetId,
        YouTubePlaybackDiagnosticMessage message,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        _activitySnapshots.TryGetValue(targetId, out var previous);
        var lastProgressAt = previous?.LastProgressAt;
        if (message.CurrentTimeSeconds is { } currentPosition
            && previous?.PositionSeconds is { } previousPosition
            && currentPosition >= previousPosition + 0.1)
        {
            lastProgressAt = observedAt;
        }
        else if (string.Equals(message.EventName, "playing", StringComparison.Ordinal))
        {
            lastProgressAt = observedAt;
        }

        var state = ResolveActivityState(message, previous?.State ?? YouTubePlaybackActivityState.Unknown);
        _activitySnapshots[targetId] = new YouTubePlaybackActivitySnapshot(
            state,
            observedAt,
            lastProgressAt,
            message.CurrentTimeSeconds ?? previous?.PositionSeconds);
    }

    private static YouTubePlaybackActivityState ResolveActivityState(
        YouTubePlaybackDiagnosticMessage message,
        YouTubePlaybackActivityState previousState)
    {
        if (message.IsEnded == true
            || string.Equals(message.Reason, "ended", StringComparison.Ordinal))
        {
            return YouTubePlaybackActivityState.Ended;
        }

        if (string.Equals(message.EventName, "error", StringComparison.Ordinal))
        {
            return YouTubePlaybackActivityState.Failed;
        }

        if (message.EventName is "waiting" or "stalled" or "emptied")
        {
            return YouTubePlaybackActivityState.Waiting;
        }

        if (message.IsPaused == true
            || string.Equals(message.EventName, "pause", StringComparison.Ordinal))
        {
            return YouTubePlaybackActivityState.Paused;
        }

        if (message.IsPaused == false
            || string.Equals(message.EventName, "playing", StringComparison.Ordinal))
        {
            return YouTubePlaybackActivityState.Playing;
        }

        return previousState;
    }

    private static PeriodicSample CreatePeriodicSample(
        YouTubePlaybackDiagnosticMessage message,
        DateTimeOffset observedAt)
    {
        return new PeriodicSample(
            observedAt,
            message.CurrentTimeSeconds,
            message.TotalVideoFrames,
            message.DroppedVideoFrames,
            message.CorruptedVideoFrames);
    }

    private static bool HasPlaybackReset(PeriodicSample previous, PeriodicSample current)
    {
        return (current.PositionSeconds is { } currentPosition
                && previous.PositionSeconds is { } previousPosition
                && currentPosition < previousPosition - 1)
            || (current.TotalVideoFrames.HasValue
                && previous.TotalVideoFrames.HasValue
                && current.TotalVideoFrames.Value < previous.TotalVideoFrames.Value);
    }

    private static long GetElapsedMilliseconds(DateTimeOffset startedAt, DateTimeOffset observedAt)
    {
        return (long)Math.Round(Math.Max(0, (observedAt - startedAt).TotalMilliseconds));
    }

    private readonly record struct PeriodicSample(
        DateTimeOffset ObservedAt,
        double? PositionSeconds,
        long? TotalVideoFrames,
        long? DroppedVideoFrames,
        long? CorruptedVideoFrames);
}

public static class YouTubePlaybackDiagnosticService
{
    // 観測できた状況だけを分類する。buffered-waitをGPU故障などの原因確定には使わない。
    public static string ClassifyWait(YouTubePlaybackDiagnosticMessage message)
    {
        if (message.MediaErrorCode is > 0) return "media-error";
        if (message.IsPaused == true || message.IsEnded == true) return "inactive";
        if (message.BufferedAheadSeconds is >= 1) return "buffered-wait";
        if (message.BufferedAheadSeconds is <= 0.1) return "buffer-empty";
        return "unknown";
    }

    public const string MessageType = "youtubePlaybackDiagnostic";

    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "waiting",
        "stalled",
        "playing",
        "canplay",
        "loadedmetadata",
        "pause",
        "error",
        "emptied",
        "sample",
        "state",
        "surface-probe",
        "layout-counts",
        "paused-layout-probe"
    };

    public static string BuildBridgeScript(
        string webMessageToken,
        bool enablePeriodicSamples = false,
        bool enableStateUpdates = false)
    {
        var serializedToken = JsonSerializer.Serialize(webMessageToken);
        var periodicSampleSetup = enablePeriodicSamples
            ? """
                const sampleCurrentVideo = () => {
                    const video = document.querySelector("video");
                    if (video instanceof HTMLVideoElement) {
                        postDiagnostic("sample", video);
                    }
                };
                sampleTimer = window.setInterval(sampleCurrentVideo, 30000);
                window.setTimeout(sampleCurrentVideo, 0);
                """
            : string.Empty;
        return $$"""
            (() => {
                const host = String(window.location.hostname || "").toLowerCase();
                if (host !== "youtu.be" && host !== "youtube.com" && !host.endsWith(".youtube.com")) {
                    return;
                }

                const bridgeKey = "__tabBrowserYouTubePlaybackDiagnostic";
                const bridgeVersion = 8;
                const periodicSamplesEnabled = {{(enablePeriodicSamples ? "true" : "false")}};
                const stateUpdatesEnabled = {{(enableStateUpdates ? "true" : "false")}};
                const existing = window[bridgeKey];
                if (existing?.version === bridgeVersion
                    && existing?.periodicSamplesEnabled === periodicSamplesEnabled
                    && existing?.stateUpdatesEnabled === stateUpdatesEnabled) {
                    return;
                }

                existing?.dispose?.();
                const token = {{serializedToken}};
                const nativePostMessage = window.chrome?.webview?.postMessage?.bind(window.chrome.webview);
                const eventNames = ["waiting", "stalled", "playing", "canplay", "loadedmetadata", "pause", "error", "emptied"];
                const readFinite = value => {
                    const number = Number(value);
                    return Number.isFinite(number) ? number : null;
                };
                const readFrameCount = value => {
                    const number = Number(value);
                    return Number.isFinite(number) && number >= 0 ? Math.trunc(number) : null;
                };
                const readFrameQuality = video => {
                    let quality = null;
                    try {
                        quality = video.getVideoPlaybackQuality?.() ?? null;
                    } catch {
                    }

                    return {
                        totalVideoFrames: readFrameCount(
                            quality?.totalVideoFrames ?? video.webkitDecodedFrameCount),
                        droppedVideoFrames: readFrameCount(
                            quality?.droppedVideoFrames ?? video.webkitDroppedFrameCount),
                        corruptedVideoFrames: readFrameCount(quality?.corruptedVideoFrames)
                    };
                };
                const readBufferedAhead = video => {
                    try {
                        const currentTime = Number(video.currentTime);
                        if (!Number.isFinite(currentTime)) {
                            return null;
                        }

                        for (let index = 0; index < video.buffered.length; index++) {
                            const start = video.buffered.start(index);
                            const end = video.buffered.end(index);
                            if (currentTime >= start && currentTime <= end) {
                                return Math.max(0, end - currentTime);
                            }
                        }
                    } catch {
                        return null;
                    }

                    return 0;
                };
                const postDiagnostic = (eventName, video) => {
                    if (!(video instanceof HTMLVideoElement) || !nativePostMessage) {
                        return;
                    }

                    const frameQuality = readFrameQuality(video);

                    nativePostMessage({
                        type: "{{MessageType}}",
                        token,
                        event: eventName,
                        currentTime: readFinite(video.currentTime),
                        duration: readFinite(video.duration),
                        readyState: Number.isInteger(video.readyState) ? video.readyState : null,
                        networkState: Number.isInteger(video.networkState) ? video.networkState : null,
                        paused: Boolean(video.paused),
                        ended: Boolean(video.ended),
                        bufferedAhead: readBufferedAhead(video),
                        playbackRate: readFinite(video.playbackRate),
                        muted: Boolean(video.muted),
                        volume: readFinite(video.volume),
                        looping: Boolean(video.loop),
                        visibility: String(document.visibilityState || "unknown"),
                        errorCode: Number.isInteger(video.error?.code) ? video.error.code : null,
                        totalVideoFrames: frameQuality.totalVideoFrames,
                        droppedVideoFrames: frameQuality.droppedVideoFrames,
                        corruptedVideoFrames: frameQuality.corruptedVideoFrames,
                        videoNaturalWidth: readFinite(video.videoWidth),
                        videoNaturalHeight: readFinite(video.videoHeight)
                    });
                };
                let lastProbeKey = "";
                let lastProbeAt = -Infinity;
                const postPlayerSurfaceProbe = () => {
                    const path = String(window.location.pathname || "");
                    const surface = path === "/watch" ? "watch"
                        : path.startsWith("/shorts/") ? "shorts"
                        : path.startsWith("/live/") ? "live"
                        : path.startsWith("/embed/") ? "embed" : null;
                    if (!surface || !nativePostMessage) {
                        return;
                    }

                    const video = document.querySelector("video");
                    const player = document.querySelector("#movie_player");
                    const reason = !(video instanceof HTMLVideoElement) ? "video-missing"
                        : typeof player?.getPlayerResponse !== "function" ? "player-api-missing" : "available";
                    const now = Date.now();
                    const key = surface + "|" + reason;
                    if (key === lastProbeKey || now - lastProbeAt < 30000) {
                        return;
                    }

                    lastProbeKey = key;
                    lastProbeAt = now;
                    nativePostMessage({ type: "{{MessageType}}", token, event: "surface-probe", reason, surface });
                };
                const handleEvent = event => {
                    postDiagnostic(event.type, event.target);
                    if (event.type === "loadedmetadata" || event.type === "canplay") {
                        postPlayerSurfaceProbe();
                    }
                };
                let lastStateSentAt = 0;
                const postPlaybackState = (video, reason, force = false) => {
                    if (!(video instanceof HTMLVideoElement) || !nativePostMessage) {
                        return;
                    }

                    const now = Date.now();
                    if (!force && now - lastStateSentAt < 10000) {
                        return;
                    }

                    lastStateSentAt = now;
                    const frameQuality = readFrameQuality(video);
                    nativePostMessage({
                        type: "{{MessageType}}",
                        token,
                        event: "state",
                        reason,
                        readyState: Number.isInteger(video.readyState) ? video.readyState : null,
                        networkState: Number.isInteger(video.networkState) ? video.networkState : null,
                        bufferedAhead: readBufferedAhead(video),
                        errorCode: Number.isInteger(video.error?.code) ? video.error.code : null,
                        totalVideoFrames: frameQuality.totalVideoFrames,
                        droppedVideoFrames: frameQuality.droppedVideoFrames,
                        corruptedVideoFrames: frameQuality.corruptedVideoFrames,
                        currentTime: readFinite(video.currentTime),
                        duration: readFinite(video.duration),
                        paused: Boolean(video.paused),
                        ended: Boolean(video.ended),
                        playbackRate: readFinite(video.playbackRate),
                        muted: Boolean(video.muted),
                        volume: readFinite(video.volume),
                        looping: Boolean(video.loop),
                        visibility: String(document.visibilityState || "unknown"),
                        videoNaturalWidth: readFinite(video.videoWidth),
                        videoNaturalHeight: readFinite(video.videoHeight)
                    });
                };
                const stateEventNames = [
                    "play",
                    "pause",
                    "ratechange",
                    "volumechange",
                    "seeked",
                    "durationchange",
                    "ended",
                    "resize"
                ];
                const handleStateEvent = event => postPlaybackState(event.target, event.type, true);
                const handleTimeUpdate = event => postPlaybackState(event.target, "timeupdate");

                for (const eventName of eventNames) {
                    document.addEventListener(eventName, handleEvent, true);
                }
                if (stateUpdatesEnabled) {
                    for (const eventName of stateEventNames) {
                        document.addEventListener(eventName, handleStateEvent, true);
                    }
                    document.addEventListener("timeupdate", handleTimeUpdate, true);
                }

                document.addEventListener("yt-navigate-finish", postPlayerSurfaceProbe, true);
                postPlayerSurfaceProbe();

                let sampleTimer = null;
                {{periodicSampleSetup}}

                const currentVideo = document.querySelector("video");
                if (currentVideo instanceof HTMLVideoElement
                    && currentVideo.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
                    postDiagnostic("canplay", currentVideo);
                    if (stateUpdatesEnabled) {
                        postPlaybackState(currentVideo, "bridge-ready", true);
                    }
                }

                window[bridgeKey] = {
                    version: bridgeVersion,
                    periodicSamplesEnabled,
                    stateUpdatesEnabled,
                    dispose: () => {
                        if (sampleTimer !== null) {
                            window.clearInterval(sampleTimer);
                        }
                        document.removeEventListener("yt-navigate-finish", postPlayerSurfaceProbe, true);
                        for (const eventName of eventNames) {
                            document.removeEventListener(eventName, handleEvent, true);
                        }
                        if (stateUpdatesEnabled) {
                            for (const eventName of stateEventNames) {
                                document.removeEventListener(eventName, handleStateEvent, true);
                            }
                            document.removeEventListener("timeupdate", handleTimeUpdate, true);
                        }
                    }
                };
            })();
            """;
    }

    public static bool TryParse(string json, out YouTubePlaybackDiagnosticMessage message)
    {
        message = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParse(document.RootElement, out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParse(JsonElement root, out YouTubePlaybackDiagnosticMessage message)
    {
        message = null!;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetString(root, "type", out var type)
            || !string.Equals(type, MessageType, StringComparison.Ordinal)
            || !TryGetString(root, "event", out var eventName)
            || !SupportedEvents.Contains(eventName))
        {
            return false;
        }

        var reason = GetOptionalString(root, "reason");
        var surface = GetOptionalString(root, "surface");
        if (eventName == "surface-probe" && (surface is not ("watch" or "shorts" or "live" or "embed")
            || reason is not ("video-missing" or "player-api-missing" or "available")))
        {
            return false;
        }

        YouTubeMiniPlayerLayoutCounts? layoutCounts = null;
        if (eventName == "layout-counts")
        {
            var replacements = GetInt32(root, "replacements");
            var applied = GetInt32(root, "applied");
            var playerMissing = GetInt32(root, "playerMissing");
            var videoMissing = GetInt32(root, "videoMissing");
            var styleDrift = GetInt32(root, "styleDrift");
            var applyErrors = GetInt32(root, "applyErrors");
            if (replacements is not >= 0 || applied is not >= 0
                || playerMissing is not >= 0 || videoMissing is not >= 0
                || styleDrift is not >= 0 || applyErrors is not >= 0)
            {
                return false;
            }

            layoutCounts = new(replacements.Value, applied.Value,
                playerMissing.Value, videoMissing.Value, styleDrift.Value, applyErrors.Value);
        }

        YouTubeMiniPlayerPausedLayoutProbe? pausedLayoutProbe = null;
        if (eventName == "paused-layout-probe")
        {
            var result = GetOptionalString(root, "result");
            var previousNatural = GetLayoutSize(root, "previousNatural");
            var before = ReadLayoutDimensions(root, "before");
            var after = ReadLayoutDimensions(root, "after");
            var replaced = GetBoolean(root, "replaced");
            var paused = GetBoolean(root, "paused");
            var applied = GetBoolean(root, "applied");
            if (reason is not ("host-verification" or "media-change")
                || result is not ("verified" or "drift" or "missing" or "apply-error")
                || previousNatural is null || before is null || after is null
                || replaced is null || paused is null || applied is null)
            {
                return false;
            }

            pausedLayoutProbe = new(reason, result, previousNatural, before, after, replaced.Value, paused.Value, applied.Value);
        }

        message = new YouTubePlaybackDiagnosticMessage(
            GetOptionalString(root, "token"),
            eventName,
            GetFiniteDouble(root, "currentTime"),
            GetFiniteDouble(root, "duration"),
            GetInt32(root, "readyState"),
            GetInt32(root, "networkState"),
            GetBoolean(root, "paused"),
            GetBoolean(root, "ended"),
            GetFiniteDouble(root, "bufferedAhead"),
            GetFiniteDouble(root, "playbackRate"),
            GetBoolean(root, "muted"),
            GetFiniteDouble(root, "volume"),
            GetBoolean(root, "looping"),
            reason,
            GetOptionalString(root, "visibility") ?? "unknown",
            GetInt32(root, "errorCode"),
            GetInt64(root, "totalVideoFrames"),
            GetInt64(root, "droppedVideoFrames"),
            GetInt64(root, "corruptedVideoFrames"),
            GetFiniteDouble(root, "videoNaturalWidth"),
            GetFiniteDouble(root, "videoNaturalHeight"),
            eventName == "surface-probe" ? surface : null,
            layoutCounts,
            pausedLayoutProbe);
        return true;
    }

    private static YouTubeMiniPlayerLayoutDimensions? ReadLayoutDimensions(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var natural = GetLayoutSize(value, "natural");
        var video = GetLayoutSize(value, "video");
        var parent = GetLayoutSize(value, "parent");
        var viewport = GetLayoutSize(value, "viewport");
        return natural is not null && video is not null && parent is not null && viewport is not null
            ? new(natural, video, parent, viewport)
            : null;
    }

    private static string? GetLayoutSize(JsonElement root, string name)
    {
        const int maximumDimension = 32768;
        var size = GetOptionalString(root, name);
        if (size is null || size.Length > 11)
        {
            return null;
        }

        var separator = size.IndexOf('x', StringComparison.Ordinal);
        return separator > 0
            && int.TryParse(size.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(size.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            && width is >= 0 and <= maximumDimension && height is >= 0 and <= maximumDimension
            ? FormattableString.Invariant($"{width}x{height}")
            : null;
    }

    public static bool IsTrustedYouTubeSource(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && YouTubeUrlService.IsYouTubeUrl(source);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double? GetFiniteDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
            && double.IsFinite(value)
            ? value
            : null;
    }

    private static int? GetInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? GetInt64(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
            && value >= 0
            ? value
            : null;
    }

    private static bool? GetBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
