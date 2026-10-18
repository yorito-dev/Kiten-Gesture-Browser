using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerStateRestoreService
{
    public static async Task<long?> ApplyWatchPageModeAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<bool> isClosing,
        Action<string>? setStatus = null,
        Action<YouTubeMiniPlayerState, double, double>? applyVideoDimensions = null,
        IEnumerable<GestureMappingSettings>? gestureMappings = null,
        double gestureStartThreshold = 14,
        double gestureDirectionThreshold = 22,
        bool enableLiveContinuation = true)
    {
        if (!TryGetLoadedRuntime(state, runtimes, out var runtime))
        {
            return null;
        }

        runtime.IsLiveContinuationEnabled = enableLiveContinuation;
        var generation = runtime.DisplayHealth.BeginApply();
        await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubeMiniPlayerWebViewService.BuildLiveContinuationBridgeScript(runtime.WebMessageToken, enableLiveContinuation),
            showStatus: false,
            _ => { });
        await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubePlaybackDiagnosticService.BuildBridgeScript(
                runtime.WebMessageToken,
                enableStateUpdates: true),
            showStatus: false,
            _ => { });

        if (isClosing()
            || !TryGetLoadedRuntime(state, runtimes, out var currentRuntime)
            || !ReferenceEquals(runtime, currentRuntime)
            || !runtime.DisplayHealth.TryBeginAttempt(generation, maxAttempts: 1))
        {
            return null;
        }

        var result = await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMiniPlayerWatchPageScript(
                runtime.WebMessageToken,
                gestureMappings,
                gestureStartThreshold,
                gestureDirectionThreshold,
                state.ContentZoomFactor,
                state.ContentPanX,
                state.ContentPanY,
                state.AudioBoostDecibels),
            showStatus: false,
            _ => { });
        var isReady = IsMiniPlayerWatchPageReady(result);
        if (!runtime.DisplayHealth.ReportAttemptResult(generation, isReady, isFinalAttempt: true))
        {
            return null;
        }

        if (TryReadVideoDimensions(result, out var videoWidth, out var videoHeight))
        {
            applyVideoDimensions?.Invoke(state, videoWidth, videoHeight);
        }
        else if (IsShortsPage(result))
        {
            applyVideoDimensions?.Invoke(state, 9, 16);
        }

        if (!isReady)
        {
            setStatus?.Invoke("YouTubeミニプレイヤーの表示補正を開始できないため自動復旧を開始します");
        }

        return runtime.DisplayHealth.IsCurrentGeneration(generation)
            ? generation
            : null;
    }

    public static async Task ApplySavedStateAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<bool> isClosing,
        long expectedDisplayGeneration)
    {
        if (!TryGetLoadedRuntime(state, runtimes, out var runtime)
            || !runtime.DisplayHealth.IsCurrentGeneration(expectedDisplayGeneration))
        {
            return;
        }

        if (!YouTubeUrlService.TryGetVideoId(state.Url, out var videoId)
            || !IsRestoreAttemptCurrent(
                state,
                runtimes,
                runtime,
                expectedDisplayGeneration,
                isClosing))
        {
            return;
        }

        var result = await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMiniPlayerSavedStateReadyBridgeScript(
                videoId,
                state.LastPositionSeconds,
                state.PlaybackRate,
                state.IsPaused,
                state.IsLooping,
                state.IsMuted,
                state.Volume,
                state.AbRepeatStartSeconds,
                state.AbRepeatEndSeconds,
                state.IsAbRepeatEnabled),
            showStatus: false,
            _ => { });
        runtime.IsInitialLivePlaybackResolved = !string.IsNullOrWhiteSpace(result);
    }

    public static async Task<bool> RequestLayoutVerificationAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        var result = await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMiniPlayerLayoutVerificationScript(),
            showStatus: false,
            _ => { });
        return YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result);
    }

    private static bool IsRestoreAttemptCurrent(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntime expectedRuntime,
        long expectedDisplayGeneration,
        Func<bool> isClosing)
    {
        return !isClosing()
            && TryGetLoadedRuntime(state, runtimes, out var currentRuntime)
            && ReferenceEquals(expectedRuntime, currentRuntime)
            && currentRuntime.DisplayHealth.IsCurrentGeneration(expectedDisplayGeneration);
    }

    private static bool TryGetLoadedRuntime(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        out YouTubeMiniPlayerRuntime runtime)
    {
        return runtimes.TryGetValue(state.Id, out runtime!)
            && YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null;
    }

    private static bool TryReadScriptBoolean(string resultJson, string propertyName, out bool value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
            {
                value = property.GetBoolean();
                return true;
            }
        }
        catch
        {
        }

        value = false;
        return false;
    }

    private static bool IsMiniPlayerWatchPageReady(string? resultJson)
    {
        return !string.IsNullOrWhiteSpace(resultJson)
            && ((TryReadScriptBoolean(resultJson, "bridgeInstalled", out var installed) && installed)
                || (TryReadScriptBoolean(resultJson, "ok", out var ok) && ok));
    }

    internal static bool TryReadVideoDimensions(
        string? resultJson,
        out double videoWidth,
        out double videoHeight)
    {
        videoWidth = 0;
        videoHeight = 0;
        return !string.IsNullOrWhiteSpace(resultJson)
            && TryReadScriptDouble(resultJson, "videoNaturalWidth", out videoWidth)
            && TryReadScriptDouble(resultJson, "videoNaturalHeight", out videoHeight)
            && double.IsFinite(videoWidth)
            && double.IsFinite(videoHeight)
            && videoWidth > 0
            && videoHeight > 0;
    }

    private static bool IsShortsPage(string? resultJson)
    {
        return !string.IsNullOrWhiteSpace(resultJson)
            && TryReadScriptBoolean(resultJson, "isShortsPage", out var isShortsPage)
            && isShortsPage;
    }

    private static bool TryReadScriptDouble(string resultJson, string propertyName, out double value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value))
            {
                return true;
            }
        }
        catch
        {
        }

        value = 0;
        return false;
    }
}
