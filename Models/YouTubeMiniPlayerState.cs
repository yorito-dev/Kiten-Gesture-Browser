using System.Text.Json.Serialization;

namespace TabBrowser.Models;

public enum MiniPlayerSourceKind
{
    YouTube,
    LocalFile
}

public enum YouTubeLiveChatDockSide
{
    Right,
    Left
}

public sealed class YouTubeMiniPlayerState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int WindowId { get; set; }

    public Guid? OwnerSessionWindowId { get; set; }

    public Guid SourceTabId { get; set; }

    public MiniPlayerSourceKind SourceKind { get; set; }

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string LocalFilePath { get; set; } = string.Empty;

    public bool IsShorts { get; set; }

    [JsonIgnore]
    public bool IsLiveBroadcast { get; set; }

    [JsonIgnore]
    public bool IsAtLiveEdge { get; set; }

    [JsonIgnore]
    public string? LiveViewerCountText { get; set; }

    public double? VideoAspectRatio { get; set; }

    public double ContentZoomFactor { get; set; } = 1;

    [JsonIgnore]
    public double? PreviousContentZoomFactor { get; set; }

    public double ContentPanX { get; set; }

    public double ContentPanY { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = 360;

    public double Height { get; set; } = 240;

    public bool IsMuted { get; set; }

    public double? Volume { get; set; }

    public int AudioBoostDecibels { get; set; }

    public double? PlaybackRate { get; set; }

    [JsonIgnore]
    public double? PreviousPlaybackRate { get; set; }

    public double? LastPositionSeconds { get; set; }

    [JsonIgnore]
    public DateTimeOffset? PlaybackStateObservedAt { get; set; }

    public double? DurationSeconds { get; set; }

    public bool? IsPaused { get; set; }

    public bool? IsLooping { get; set; }

    public double? AbRepeatStartSeconds { get; set; }

    public double? AbRepeatEndSeconds { get; set; }

    public bool IsAbRepeatEnabled { get; set; }

    public bool IsMinimized { get; set; }

    public bool IsTopmost { get; set; } = true;

    public double? LiveChatWindowLeft { get; set; }

    public double? LiveChatWindowTop { get; set; }

    public double? LiveChatWindowWidth { get; set; }

    public double? LiveChatWindowHeight { get; set; }

    public bool? IsLiveChatWindowTopmost { get; set; }

    public bool IsLiveChatWindowDocked { get; set; } = true;

    public YouTubeLiveChatDockSide LiveChatWindowDockSide { get; set; }

    public int? ListOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class YouTubeMiniPlayerAudioBoostProfile
{
    public static int Normalize(int decibels)
    {
        return AudioBoostProfile.Normalize(decibels);
    }

    public static double GetLinearGain(int decibels)
    {
        return AudioBoostProfile.GetLinearGain(decibels);
    }
}

public static class AudioBoostProfile
{
    public static int Normalize(int decibels)
    {
        return decibels is 3 or 6 or 9 ? decibels : 0;
    }

    public static double GetLinearGain(int decibels)
    {
        return Math.Pow(10, Normalize(decibels) / 20d);
    }
}

public static class YouTubeMiniPlayerContentTransformState
{
    public const double MinimumZoomFactor = 1;
    public const double MaximumZoomFactor = 4;
    public const double ZoomStep = 0.1;

    public static bool Normalize(YouTubeMiniPlayerState state)
    {
        var zoomFactor = NormalizeZoomFactor(state.ContentZoomFactor);
        var panX = zoomFactor > MinimumZoomFactor ? NormalizePan(state.ContentPanX) : 0;
        var panY = zoomFactor > MinimumZoomFactor ? NormalizePan(state.ContentPanY) : 0;
        return SetNormalized(state, zoomFactor, panX, panY);
    }

    public static bool ChangeZoom(YouTubeMiniPlayerState state, int stepCount)
    {
        return ChangeZoomAtAnchor(state, stepCount, anchorX: 0, anchorY: 0);
    }

    public static bool ChangeZoomAtAnchor(
        YouTubeMiniPlayerState state,
        int stepCount,
        double anchorX,
        double anchorY)
    {
        Normalize(state);
        if (stepCount == 0)
        {
            return false;
        }

        var previousZoomFactor = state.ContentZoomFactor;
        var zoomFactor = NormalizeZoomFactor(previousZoomFactor + (stepCount * ZoomStep));
        if (zoomFactor == previousZoomFactor)
        {
            return false;
        }

        var panX = CalculateAnchoredPan(
            previousZoomFactor,
            zoomFactor,
            state.ContentPanX,
            anchorX);
        var panY = CalculateAnchoredPan(
            previousZoomFactor,
            zoomFactor,
            state.ContentPanY,
            anchorY);
        return SetNormalized(state, zoomFactor, panX, panY);
    }

    public static bool Set(
        YouTubeMiniPlayerState state,
        double zoomFactor,
        double panX,
        double panY)
    {
        zoomFactor = NormalizeZoomFactor(zoomFactor);
        panX = zoomFactor > MinimumZoomFactor ? NormalizePan(panX) : 0;
        panY = zoomFactor > MinimumZoomFactor ? NormalizePan(panY) : 0;
        return SetNormalized(state, zoomFactor, panX, panY);
    }

    public static bool MovePan(
        YouTubeMiniPlayerState state,
        double startPanX,
        double startPanY,
        double deltaX,
        double deltaY,
        double viewportWidth,
        double viewportHeight)
    {
        return MovePan(
            state,
            startPanX,
            startPanY,
            deltaX,
            deltaY,
            viewportWidth,
            viewportHeight,
            out _);
    }

    public static bool MovePan(
        YouTubeMiniPlayerState state,
        double startPanX,
        double startPanY,
        double deltaX,
        double deltaY,
        double viewportWidth,
        double viewportHeight,
        out bool reachedBoundary)
    {
        reachedBoundary = false;
        Normalize(state);
        if (state.ContentZoomFactor <= MinimumZoomFactor
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return false;
        }

        var maxTranslationX = viewportWidth * (state.ContentZoomFactor - 1) / 2;
        var maxTranslationY = viewportHeight * (state.ContentZoomFactor - 1) / 2;
        var panX = startPanX + (deltaX / maxTranslationX);
        var panY = startPanY + (deltaY / maxTranslationY);
        reachedBoundary = panX is < -1 or > 1 || panY is < -1 or > 1;
        return Set(
            state,
            state.ContentZoomFactor,
            panX,
            panY);
    }

    public static bool Reset(YouTubeMiniPlayerState state)
    {
        return SetNormalized(state, MinimumZoomFactor, 0, 0);
    }

    public static bool ToggleOriginalSize(YouTubeMiniPlayerState state)
    {
        Normalize(state);
        var targetZoomFactor = state.ContentZoomFactor > MinimumZoomFactor
            ? MinimumZoomFactor
            : NormalizePreviousZoomFactor(state.PreviousContentZoomFactor) ?? MinimumZoomFactor;
        return SetNormalized(state, targetZoomFactor, 0, 0);
    }

    public static string FormatPercentage(double zoomFactor)
    {
        return $"{Math.Round(NormalizeZoomFactor(zoomFactor) * 100):0}%";
    }

    public static string CreateCropGeometry(
        uint videoWidth,
        uint videoHeight,
        double zoomFactor,
        double panX,
        double panY)
    {
        zoomFactor = NormalizeZoomFactor(zoomFactor);
        if (zoomFactor <= MinimumZoomFactor || videoWidth == 0 || videoHeight == 0)
        {
            return string.Empty;
        }

        panX = NormalizePan(panX);
        panY = NormalizePan(panY);
        var cropWidth = Math.Clamp((uint)Math.Round(videoWidth / zoomFactor), 1, videoWidth);
        var cropHeight = Math.Clamp((uint)Math.Round(videoHeight / zoomFactor), 1, videoHeight);
        var maximumLeft = videoWidth - cropWidth;
        var maximumTop = videoHeight - cropHeight;
        var left = (uint)Math.Round(((1 - panX) / 2) * maximumLeft);
        var top = (uint)Math.Round(((1 - panY) / 2) * maximumTop);
        return $"{cropWidth}x{cropHeight}+{left}+{top}";
    }

    private static double NormalizeZoomFactor(double zoomFactor)
    {
        return double.IsFinite(zoomFactor)
            ? Math.Clamp(Math.Round(zoomFactor * 10) / 10, MinimumZoomFactor, MaximumZoomFactor)
            : MinimumZoomFactor;
    }

    private static double? NormalizePreviousZoomFactor(double? zoomFactor)
    {
        if (zoomFactor is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        var normalized = NormalizeZoomFactor(value);
        return normalized > MinimumZoomFactor ? normalized : null;
    }

    private static double NormalizePan(double pan)
    {
        return double.IsFinite(pan) ? Math.Clamp(pan, -1, 1) : 0;
    }

    private static double CalculateAnchoredPan(
        double previousZoomFactor,
        double zoomFactor,
        double previousPan,
        double anchor)
    {
        if (zoomFactor <= MinimumZoomFactor)
        {
            return 0;
        }

        anchor = NormalizePan(anchor);
        previousPan = previousZoomFactor > MinimumZoomFactor
            ? NormalizePan(previousPan)
            : 0;
        var previousTranslation = previousPan * (previousZoomFactor - 1);
        var zoomRatio = zoomFactor / previousZoomFactor;
        var anchoredTranslation = (anchor * (1 - zoomRatio))
            + (previousTranslation * zoomRatio);
        return NormalizePan(anchoredTranslation / (zoomFactor - 1));
    }

    private static bool SetNormalized(
        YouTubeMiniPlayerState state,
        double zoomFactor,
        double panX,
        double panY)
    {
        state.PreviousContentZoomFactor = zoomFactor > MinimumZoomFactor
            ? zoomFactor
            : NormalizePreviousZoomFactor(state.PreviousContentZoomFactor);
        if (state.ContentZoomFactor == zoomFactor
            && state.ContentPanX == panX
            && state.ContentPanY == panY)
        {
            return false;
        }

        state.ContentZoomFactor = zoomFactor;
        state.ContentPanX = panX;
        state.ContentPanY = panY;
        return true;
    }
}

public static class YouTubeLiveChatWindowState
{
    public const double DefaultWidth = 420;
    public const double DefaultHeight = 640;
    public const double MinimumWidth = 320;
    public const double MinimumHeight = 240;

    public static void Normalize(YouTubeMiniPlayerState state)
    {
        var hasSavedPlacement = state.LiveChatWindowLeft.HasValue
            || state.LiveChatWindowTop.HasValue
            || state.LiveChatWindowWidth.HasValue
            || state.LiveChatWindowHeight.HasValue;
        if (!IsFinite(state.LiveChatWindowLeft) || !IsFinite(state.LiveChatWindowTop))
        {
            state.LiveChatWindowLeft = null;
            state.LiveChatWindowTop = null;
        }

        state.LiveChatWindowWidth = NormalizeSize(state.LiveChatWindowWidth, MinimumWidth);
        state.LiveChatWindowHeight = NormalizeSize(state.LiveChatWindowHeight, MinimumHeight);
        if (!Enum.IsDefined(state.LiveChatWindowDockSide))
        {
            state.LiveChatWindowDockSide = YouTubeLiveChatDockSide.Right;
        }

        if (!hasSavedPlacement)
        {
            state.IsLiveChatWindowDocked = true;
        }
    }

    private static bool IsFinite(double? value)
    {
        return value is { } number && double.IsFinite(number);
    }

    private static double? NormalizeSize(double? value, double minimum)
    {
        return value is { } number && double.IsFinite(number) && number > 0
            ? Math.Max(minimum, number)
            : null;
    }
}

public static class YouTubeMiniPlayerAbRepeatState
{
    public const double MinimumDurationSeconds = 0.5;

    public static bool HasValidRange(YouTubeMiniPlayerState state)
    {
        return state.AbRepeatStartSeconds is { } start
            && state.AbRepeatEndSeconds is { } end
            && double.IsFinite(start)
            && double.IsFinite(end)
            && start >= 0
            && end - start >= MinimumDurationSeconds;
    }

    public static void Normalize(YouTubeMiniPlayerState state)
    {
        state.AbRepeatStartSeconds = NormalizePosition(state.AbRepeatStartSeconds);
        state.AbRepeatEndSeconds = NormalizePosition(state.AbRepeatEndSeconds);
        if (state.AbRepeatStartSeconds is not { } start
            || state.AbRepeatEndSeconds is not { } end
            || end - start < MinimumDurationSeconds)
        {
            state.AbRepeatEndSeconds = null;
            state.IsAbRepeatEnabled = false;
        }

        if (state.IsAbRepeatEnabled)
        {
            state.IsLooping = false;
        }
    }

    public static void Clear(YouTubeMiniPlayerState state)
    {
        state.AbRepeatStartSeconds = null;
        state.AbRepeatEndSeconds = null;
        state.IsAbRepeatEnabled = false;
    }

    private static double? NormalizePosition(double? positionSeconds)
    {
        return positionSeconds is { } position
            && double.IsFinite(position)
            && position >= 0
                ? position
                : null;
    }
}
