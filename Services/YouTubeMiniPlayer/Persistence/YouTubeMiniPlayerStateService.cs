using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public readonly record struct YouTubeMiniPlayerStateApplyResult(
    bool SpeedChanged,
    double? Speed,
    bool LoopingChanged,
    bool? IsLooping);

public readonly record struct YouTubeMiniPlayerPlaybackStateSnapshot(
    bool IsMuted,
    double? Volume,
    double? PlaybackRate,
    double? LastPositionSeconds,
    double? DurationSeconds,
    bool? IsPaused,
    bool? IsLooping);

public static class YouTubeMiniPlayerStateService
{
    private const double MaximumPlaybackPositionProjectionAgeSeconds = 15;

    public static YouTubeMiniPlayerPlaybackStateSnapshot CreatePlaybackStateSnapshot(YouTubeMiniPlayerState state)
    {
        return new YouTubeMiniPlayerPlaybackStateSnapshot(
            state.IsMuted,
            state.Volume,
            state.PlaybackRate,
            state.LastPositionSeconds,
            state.DurationSeconds,
            state.IsPaused,
            state.IsLooping);
    }

    public static bool HasPlaybackStateChanged(
        YouTubeMiniPlayerPlaybackStateSnapshot before,
        YouTubeMiniPlayerPlaybackStateSnapshot after)
    {
        return !before.Equals(after);
    }

    public static double? ResolvePersistedPlaybackPosition(
        YouTubeMiniPlayerState state,
        DateTimeOffset snapshotAt)
    {
        var position = YouTubeControlScriptService.NormalizePlaybackPosition(state.LastPositionSeconds);
        if (position is not { } currentPosition
            || state.SourceKind != MiniPlayerSourceKind.YouTube
            || state.IsPaused != false
            || state.IsLiveBroadcast
            || state.IsAbRepeatEnabled
            || state.PlaybackStateObservedAt is not { } observedAt)
        {
            return position;
        }

        var elapsedSeconds = (snapshotAt - observedAt).TotalSeconds;
        if (!double.IsFinite(elapsedSeconds)
            || elapsedSeconds <= 0
            || elapsedSeconds > MaximumPlaybackPositionProjectionAgeSeconds)
        {
            return position;
        }

        var playbackRate = YouTubeControlScriptService.NormalizePlaybackSpeed(state.PlaybackRate)
            ?? YouTubeControlScriptService.DefaultSpeed;
        var projectedPosition = currentPosition + (elapsedSeconds * playbackRate);
        if (!double.IsFinite(projectedPosition))
        {
            return position;
        }

        if (state.DurationSeconds is { } duration
            && double.IsFinite(duration)
            && duration > 0)
        {
            projectedPosition = state.IsLooping == true
                ? projectedPosition % duration
                : Math.Min(projectedPosition, duration);
        }

        return YouTubeControlScriptService.NormalizePlaybackPosition(projectedPosition);
    }

    public static void ApplyPlaybackStateSnapshot(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerPlaybackStateSnapshot snapshot)
    {
        state.IsMuted = snapshot.IsMuted;
        state.Volume = snapshot.Volume;
        state.PlaybackRate = snapshot.PlaybackRate;
        state.LastPositionSeconds = snapshot.LastPositionSeconds;
        state.DurationSeconds = snapshot.DurationSeconds;
        state.IsPaused = snapshot.IsPaused;
        state.IsLooping = snapshot.IsLooping;
        state.PlaybackStateObservedAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static bool ApplyPlaybackDiagnosticMessage(
        YouTubeMiniPlayerState state,
        YouTubePlaybackDiagnosticMessage message,
        DateTimeOffset observedAt)
    {
        if (!string.Equals(message.EventName, "state", StringComparison.Ordinal))
        {
            return false;
        }

        var before = CreatePlaybackStateSnapshot(state);
        state.LastPositionSeconds = message.CurrentTimeSeconds ?? state.LastPositionSeconds;
        state.DurationSeconds = message.DurationSeconds is > 0
            ? message.DurationSeconds
            : state.DurationSeconds;
        state.IsPaused = message.IsPaused ?? state.IsPaused;
        if (message.PlaybackRate is { } playbackRate)
        {
            state.PreviousPlaybackRate = YouTubeControlScriptService.RememberNonDefaultPlaybackSpeed(
                playbackRate,
                state.PreviousPlaybackRate);
            state.PlaybackRate = playbackRate;
        }
        state.IsMuted = message.IsMuted ?? state.IsMuted;
        state.Volume = message.Volume is { } volume
            ? Math.Clamp(volume, 0, 1)
            : state.Volume;
        state.IsLooping = message.IsLooping ?? state.IsLooping;
        state.PlaybackStateObservedAt = observedAt;
        state.UpdatedAt = observedAt;
        return HasPlaybackStateChanged(before, CreatePlaybackStateSnapshot(state));
    }

    public static YouTubeMiniPlayerStateApplyResult ApplyScriptResult(
        YouTubeMiniPlayerState state,
        string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return default;
        }

        var observedPlaybackState = false;
        var speedChanged = false;
        double? speedValue = null;
        var loopingChanged = false;
        bool? loopingValue = null;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                state.UpdatedAt = DateTimeOffset.UtcNow;
                return default;
            }

            var root = document.RootElement;
            if (TryReadBoolean(root, "paused", out var isPaused))
            {
                state.IsPaused = isPaused;
                observedPlaybackState = true;
            }

            if (TryReadBoolean(root, "muted", out var isMuted))
            {
                state.IsMuted = isMuted;
                observedPlaybackState = true;
            }

            if (TryReadDouble(root, "volume", out var volume))
            {
                state.Volume = Math.Max(0, Math.Min(1, volume));
                observedPlaybackState = true;
            }

            if (TryReadDouble(root, "speed", out var speed))
            {
                state.PreviousPlaybackRate = YouTubeControlScriptService.RememberNonDefaultPlaybackSpeed(
                    speed,
                    state.PreviousPlaybackRate);
                state.PlaybackRate = speed;
                speedChanged = true;
                speedValue = speed;
                observedPlaybackState = true;
            }

            if (TryReadBoolean(root, "looping", out var isLooping))
            {
                state.IsLooping = isLooping;
                loopingChanged = true;
                loopingValue = isLooping;
                observedPlaybackState = true;
            }

            if (TryReadDouble(root, "position", out var position))
            {
                state.LastPositionSeconds = position;
                observedPlaybackState = true;
            }

            if (TryReadDouble(root, "duration", out var duration))
            {
                state.DurationSeconds = duration > 0 ? duration : null;
                observedPlaybackState = true;
            }

            var hasLiveState = TryReadBoolean(root, "isLive", out var isLive);
            if (hasLiveState)
            {
                state.IsLiveBroadcast = isLive;
                if (!isLive)
                {
                    state.IsAtLiveEdge = false;
                    state.LiveViewerCountText = null;
                }
            }

            if (TryReadNullableString(root, "liveViewerCountText", out var liveViewerCountText))
            {
                state.LiveViewerCountText = state.IsLiveBroadcast
                    ? NormalizeLiveViewerCountText(liveViewerCountText)
                    : null;
            }

            if (TryReadBoolean(root, "isAtLiveEdge", out var isAtLiveEdge))
            {
                state.IsAtLiveEdge = isAtLiveEdge;
            }
        }
        catch
        {
        }

        if (observedPlaybackState)
        {
            state.PlaybackStateObservedAt = DateTimeOffset.UtcNow;
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        return new YouTubeMiniPlayerStateApplyResult(
            speedChanged,
            speedValue,
            loopingChanged,
            loopingValue);
    }

    public static string? NormalizeLiveViewerCountText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            " ",
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= 64
            ? normalized
            : normalized[..64];
    }

    private static bool TryReadDouble(JsonElement root, string propertyName, out double value)
    {
        if (root.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool value)
    {
        if (root.TryGetProperty(propertyName, out var property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadNullableString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }
}
