using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

internal interface IYouTubePlaybackDiagnosticHost
{
    bool IsPrivateWindow { get; }

    int WindowIndex { get; }

    void HandlePlaybackActivityChanged(
        string sourceKind,
        Guid targetId,
        YouTubePlaybackDiagnosticMessage message);

    void QueuePerformanceDiagnosticLogLine(IReadOnlyList<string> fields);

    string NormalizeDiagnosticToken(string value);
}

public sealed class YouTubePlaybackDiagnosticController
{
    private readonly IYouTubePlaybackDiagnosticHost _host;
    private readonly YouTubePlaybackDiagnosticTracker _tracker;
    private readonly Func<DateTimeOffset> _utcNow;

    internal YouTubePlaybackDiagnosticController(
        IYouTubePlaybackDiagnosticHost host,
        YouTubePlaybackDiagnosticTracker? tracker = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _host = host;
        _tracker = tracker ?? new YouTubePlaybackDiagnosticTracker();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void Handle(
        string sourceKind,
        Guid targetId,
        string? url,
        YouTubePlaybackDiagnosticMessage message)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !YouTubeUrlService.IsYouTubeUrl(url))
        {
            return;
        }

        var trackerId = CreateTargetId(sourceKind, targetId);
        var observedAt = _utcNow();
        var observation = _tracker.Observe(trackerId, message, observedAt);
        _host.HandlePlaybackActivityChanged(sourceKind, targetId, message);
        if (_host.IsPrivateWindow || !observation.ShouldLog)
        {
            return;
        }

        _tracker.AddDiagnosticEntry(new RuntimeYouTubePlaybackDiagnosticEntry(
            observedAt,
            _host.WindowIndex,
            sourceKind,
            trackerId,
            message.EventName,
            observation.Operation,
            message.CurrentTimeSeconds,
            message.ReadyState,
            message.NetworkState,
            message.IsPaused,
            message.BufferedAheadSeconds,
            message.TotalVideoFrames,
            message.DroppedVideoFrames,
            message.CorruptedVideoFrames,
            observation.StallDurationMilliseconds));

        var fields = new List<string>
        {
            "runner=youtube-playback",
            $"operation={observation.Operation}",
            $"window={_host.WindowIndex}",
            $"source={sourceKind}",
            $"target={targetId}",
            $"event={message.EventName}",
            $"position={FormatMetric(message.CurrentTimeSeconds)}",
            $"duration={FormatMetric(message.DurationSeconds)}",
            $"readyState={FormatMetric(message.ReadyState)}",
            $"networkState={FormatMetric(message.NetworkState)}",
            $"paused={FormatMetric(message.IsPaused)}",
            $"ended={FormatMetric(message.IsEnded)}",
            $"bufferedAhead={FormatMetric(message.BufferedAheadSeconds)}",
            $"playbackRate={FormatMetric(message.PlaybackRate)}",
            $"visibility={_host.NormalizeDiagnosticToken(message.VisibilityState)}",
            $"mediaError={FormatMetric(message.MediaErrorCode)}",
            $"totalFrames={FormatMetric(message.TotalVideoFrames)}",
            $"droppedFrames={FormatMetric(message.DroppedVideoFrames)}",
            $"corruptedFrames={FormatMetric(message.CorruptedVideoFrames)}",
            $"droppedRate={FormatDroppedFrameRate(message.TotalVideoFrames, message.DroppedVideoFrames)}"
        };
        if (observation.StallDurationMilliseconds is { } stallDuration)
        {
            fields.Add($"stallMs={stallDuration}");
        }

        if (message.EventName == "pause")
        {
            fields.Add("pauseSource=user-or-page");
        }

        if (YouTubeUrlService.TryGetVideoId(url, out var videoId))
        {
            fields.Add($"video={_host.NormalizeDiagnosticToken(videoId)}");
        }

        _host.QueuePerformanceDiagnosticLogLine(fields);
    }

    public void HandleProcessFailure(
        string sourceKind,
        Guid targetId,
        string? url,
        CoreWebView2ProcessFailedKind processFailedKind)
    {
        if (_host.IsPrivateWindow
            || string.IsNullOrWhiteSpace(url)
            || !YouTubeUrlService.IsYouTubeUrl(url))
        {
            return;
        }

        var trackerId = CreateTargetId(sourceKind, targetId);
        var observedAt = _utcNow();
        var stallDuration = _tracker.ObserveProcessFailure(trackerId, observedAt);
        _tracker.AddDiagnosticEntry(new RuntimeYouTubePlaybackDiagnosticEntry(
            observedAt,
            _host.WindowIndex,
            sourceKind,
            trackerId,
            "process-failed",
            "process-failed",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            stallDuration));
        var fields = new List<string>
        {
            "runner=youtube-playback",
            "operation=process-failed",
            $"window={_host.WindowIndex}",
            $"source={sourceKind}",
            $"target={targetId}",
            $"kind={processFailedKind}",
            $"action={WebViewProcessFailurePolicy.GetAction(processFailedKind)}"
        };
        if (stallDuration is { } activeStallDuration)
        {
            fields.Add($"activeStallMs={activeStallDuration}");
        }

        if (YouTubeUrlService.TryGetVideoId(url, out var videoId))
        {
            fields.Add($"video={_host.NormalizeDiagnosticToken(videoId)}");
        }

        _host.QueuePerformanceDiagnosticLogLine(fields);
    }

    public void Reset(string sourceKind, Guid targetId)
        => _tracker.Reset(CreateTargetId(sourceKind, targetId));

    public bool TryGetActivity(
        string sourceKind,
        Guid targetId,
        out YouTubePlaybackActivitySnapshot? activity)
    {
        if (_tracker.TryGetActivity(CreateTargetId(sourceKind, targetId), out var snapshot))
        {
            activity = snapshot;
            return true;
        }

        activity = null;
        return false;
    }

    public IReadOnlyList<RuntimeMiniPlayerFrameSample> CaptureMiniPlayerFrameSamples()
        => _tracker.CaptureMiniPlayerFrameSamples(_host.WindowIndex);

    public static IReadOnlyList<RuntimeYouTubePlaybackDiagnosticEntry> CaptureDiagnosticHistory(
        IEnumerable<YouTubePlaybackDiagnosticController> controllers)
        => YouTubePlaybackDiagnosticTracker.CaptureDiagnosticHistory(
            controllers.Select(static controller => controller._tracker));

    private static string CreateTargetId(string sourceKind, Guid targetId)
        => $"{sourceKind}:{targetId:N}";

    private static string FormatMetric<T>(T? value)
        where T : struct
    {
        return value switch
        {
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => value?.ToString() ?? "unknown"
        };
    }

    private static string FormatDroppedFrameRate(long? totalFrames, long? droppedFrames)
    {
        return totalFrames is > 0 && droppedFrames.HasValue
            ? Math.Clamp(droppedFrames.Value * 100d / totalFrames.Value, 0, 100)
                .ToString("0.###", CultureInfo.InvariantCulture)
            : "unknown";
    }
}
