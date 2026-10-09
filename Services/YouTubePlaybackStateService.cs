using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubePlaybackStateService
{
    private const double PositionChangeThresholdSeconds = 1.5;
    private const double SpeedChangeThreshold = 0.01;

    public static double? ResolveReportedPlaybackSpeed(
        double? currentSpeed,
        double? reportedSpeed,
        bool allowDefaultSpeedChange)
    {
        var normalizedReportedSpeed = YouTubeControlScriptService.NormalizePlaybackSpeed(reportedSpeed);
        if (!normalizedReportedSpeed.HasValue)
        {
            return null;
        }

        var normalizedCurrentSpeed = YouTubeControlScriptService.NormalizePlaybackSpeed(currentSpeed);
        if (!allowDefaultSpeedChange
            && normalizedCurrentSpeed.HasValue
            && Math.Abs(normalizedCurrentSpeed.Value - 1) >= SpeedChangeThreshold
            && Math.Abs(normalizedReportedSpeed.Value - 1) < SpeedChangeThreshold)
        {
            return normalizedCurrentSpeed.Value;
        }

        return normalizedReportedSpeed.Value;
    }

    public static bool UpdatePlaybackState(
        BrowserTab tab,
        string videoId,
        double? positionSeconds,
        double? speed,
        bool? isPaused,
        bool? isLooping,
        bool? isTheaterMode,
        bool allowDefaultSpeedChange = true)
    {
        var changed = false;
        if (!string.Equals(tab.YouTubePlaybackVideoId, videoId, StringComparison.Ordinal))
        {
            tab.YouTubePlaybackVideoId = videoId;
            tab.YouTubePlaybackPositionSeconds = null;
            tab.YouTubePlaybackSpeed = null;
            tab.YouTubePlaybackIsPaused = null;
            tab.YouTubePlaybackIsLooping = null;
            tab.YouTubePlaybackIsTheaterMode = null;
            changed = true;
        }

        if (positionSeconds is { } position
            && (!tab.YouTubePlaybackPositionSeconds.HasValue
                || Math.Abs(tab.YouTubePlaybackPositionSeconds.Value - position) >= PositionChangeThresholdSeconds))
        {
            tab.YouTubePlaybackPositionSeconds = position;
            changed = true;
        }

        var resolvedSpeed = ResolveReportedPlaybackSpeed(
            tab.YouTubePlaybackSpeed,
            speed,
            allowDefaultSpeedChange);
        if (resolvedSpeed is { } playbackSpeed
            && (!tab.YouTubePlaybackSpeed.HasValue
                || Math.Abs(tab.YouTubePlaybackSpeed.Value - playbackSpeed) >= SpeedChangeThreshold))
        {
            tab.PreviousYouTubePlaybackSpeed = YouTubeControlScriptService.RememberNonDefaultPlaybackSpeed(
                playbackSpeed,
                tab.PreviousYouTubePlaybackSpeed);
            tab.YouTubePlaybackSpeed = playbackSpeed;
            changed = true;
        }

        if (isPaused.HasValue && tab.YouTubePlaybackIsPaused != isPaused.Value)
        {
            tab.YouTubePlaybackIsPaused = isPaused.Value;
            changed = true;
        }

        if (isLooping.HasValue && tab.YouTubePlaybackIsLooping != isLooping.Value)
        {
            tab.YouTubePlaybackIsLooping = isLooping.Value;
            changed = true;
        }

        if (isTheaterMode.HasValue && tab.YouTubePlaybackIsTheaterMode != isTheaterMode.Value)
        {
            tab.YouTubePlaybackIsTheaterMode = isTheaterMode.Value;
            changed = true;
        }

        return changed;
    }

    public static bool ApplyPlaybackStateMessage(BrowserTab tab, JsonElement root, string? fallbackUrl)
    {
        var url = GetJsonString(root, "url");
        if (!YouTubeUrlService.TryGetVideoId(url, out var videoId)
            && !YouTubeUrlService.TryGetVideoId(fallbackUrl, out videoId))
        {
            return false;
        }

        var position = YouTubeControlScriptService.NormalizePlaybackPosition(GetJsonDouble(root, "position"));
        var speed = YouTubeControlScriptService.NormalizePlaybackSpeed(GetJsonDouble(root, "speed"));
        var isPaused = GetJsonNullableBool(root, "paused");
        var isLooping = GetJsonNullableBool(root, "looping");
        var isTheaterMode = GetJsonNullableBool(root, "theaterMode");
        var reason = GetJsonString(root, "reason");
        var allowDefaultSpeedChange = string.Equals(reason, "ratechange", StringComparison.Ordinal)
            || string.Equals(reason, "restore", StringComparison.Ordinal);
        return UpdatePlaybackState(
            tab,
            videoId,
            position,
            speed,
            isPaused,
            isLooping,
            isTheaterMode,
            allowDefaultSpeedChange);
    }

    public static bool TryCreateCapturedStateUpdate(
        string? url,
        string? resultJson,
        out YouTubePlaybackStateUpdate update)
    {
        update = default;
        if (!YouTubeUrlService.TryGetVideoId(url, out var videoId)
            || string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        var hasPosition = TryReadScriptDouble(resultJson, "position", out var position)
            && double.IsFinite(position)
            && position >= 0;
        var hasSpeed = TryReadScriptDouble(resultJson, "speed", out var speed)
            && double.IsFinite(speed)
            && speed > 0;
        var hasPaused = TryReadScriptBoolean(resultJson, "paused", out var isPaused);
        var hasLooping = TryReadScriptBoolean(resultJson, "looping", out var isLooping);
        var hasTheaterMode = TryReadScriptBoolean(resultJson, "theaterMode", out var isTheaterMode);
        if (!hasPosition && !hasSpeed && !hasPaused && !hasLooping && !hasTheaterMode)
        {
            return false;
        }

        update = new YouTubePlaybackStateUpdate(
            videoId,
            hasPosition ? position : null,
            hasSpeed ? YouTubeControlScriptService.NormalizePlaybackSpeed(speed) : null,
            hasPaused ? isPaused : null,
            hasLooping ? isLooping : null,
            hasTheaterMode ? isTheaterMode : null);
        return true;
    }

    public static bool TryCreateRestoreRequest(
        BrowserTab tab,
        string? currentUrl,
        out YouTubePlaybackRestoreRequest request)
    {
        request = default;
        if (tab.YouTubePlaybackVideoId is not { Length: > 0 } savedVideoId
            || !YouTubeUrlService.TryGetVideoId(currentUrl, out var currentVideoId)
            || !string.Equals(savedVideoId, currentVideoId, StringComparison.Ordinal))
        {
            return false;
        }

        var positionSeconds = YouTubeControlScriptService.NormalizePlaybackPosition(tab.YouTubePlaybackPositionSeconds);
        var speed = YouTubeControlScriptService.NormalizePlaybackSpeed(tab.YouTubePlaybackSpeed);
        var isPaused = tab.YouTubePlaybackIsPaused;
        var isLooping = tab.YouTubePlaybackIsLooping;
        var isTheaterMode = tab.YouTubePlaybackIsTheaterMode;
        if (!positionSeconds.HasValue && !speed.HasValue && isPaused != true && !isLooping.HasValue && !isTheaterMode.HasValue)
        {
            return false;
        }

        request = new YouTubePlaybackRestoreRequest(currentVideoId, positionSeconds, speed, isPaused, isLooping, isTheaterMode);
        return true;
    }

    public static bool TryReadRestoreResult(string? resultJson, out YouTubePlaybackRestoreResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(resultJson)
            || !TryReadScriptBoolean(resultJson, "found", out var found)
            || !found
            || !TryReadScriptDouble(resultJson, "position", out var position)
            || !TryReadScriptDouble(resultJson, "speed", out var speed))
        {
            return false;
        }

        result = new YouTubePlaybackRestoreResult(
            position,
            YouTubeControlScriptService.NormalizePlaybackSpeed(speed),
            TryReadScriptBoolean(resultJson, "paused", out var paused) ? paused : null,
            TryReadScriptBoolean(resultJson, "looping", out var looping) ? looping : null,
            TryReadScriptBoolean(resultJson, "theaterMode", out var theaterMode) ? theaterMode : null);
        return true;
    }

    public static string BuildNavigationUrl(BrowserTab tab, string url, bool isBackgroundNavigation)
    {
        if (!YouTubeUrlService.TryGetVideoId(url, out var videoId))
        {
            return url;
        }

        var navigationUrl = string.Equals(
                videoId,
                tab.YouTubePlaybackVideoId,
                StringComparison.Ordinal)
            ? YouTubeUrlService.WithPlaybackPosition(url, tab.YouTubePlaybackPositionSeconds)
            : url;

        var shouldDisableAutoplay = isBackgroundNavigation
            || (tab.YouTubePlaybackIsPaused == true
                && !string.IsNullOrWhiteSpace(tab.YouTubePlaybackVideoId)
                && string.Equals(videoId, tab.YouTubePlaybackVideoId, StringComparison.Ordinal));
        return shouldDisableAutoplay
            ? YouTubeUrlService.WithAutoplayDisabled(navigationUrl)
            : navigationUrl;
    }

    public static void MarkBackgroundNavigationPaused(BrowserTab tab, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !YouTubeUrlService.TryGetVideoId(url, out var videoId))
        {
            return;
        }

        if (!string.Equals(tab.YouTubePlaybackVideoId, videoId, StringComparison.Ordinal))
        {
            tab.YouTubePlaybackVideoId = videoId;
            tab.YouTubePlaybackPositionSeconds = null;
            tab.YouTubePlaybackSpeed = null;
            tab.YouTubePlaybackIsLooping = null;
            tab.YouTubePlaybackIsTheaterMode = null;
        }

        tab.YouTubePlaybackIsPaused = true;
    }

    private static string? GetJsonString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetJsonDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static bool? GetJsonNullableBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryReadScriptDouble(string resultJson, string propertyName, out double value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadScriptBoolean(string resultJson, string propertyName, out bool value)
    {
        value = false;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }
        }
        catch
        {
        }

        value = false;
        return false;
    }
}

public readonly record struct YouTubePlaybackStateUpdate(
    string VideoId,
    double? PositionSeconds,
    double? Speed,
    bool? IsPaused,
    bool? IsLooping,
    bool? IsTheaterMode);

public readonly record struct YouTubePlaybackRestoreRequest(
    string VideoId,
    double? PositionSeconds,
    double? Speed,
    bool? IsPaused,
    bool? IsLooping,
    bool? IsTheaterMode);

public readonly record struct YouTubePlaybackRestoreResult(
    double PositionSeconds,
    double? Speed,
    bool? IsPaused,
    bool? IsLooping,
    bool? IsTheaterMode);
