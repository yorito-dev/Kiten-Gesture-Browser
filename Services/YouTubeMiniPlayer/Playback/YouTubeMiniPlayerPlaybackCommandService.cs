using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerAbRepeatAction
{
    SetStart,
    SetEnd,
    Toggle,
    Clear
}

internal static class YouTubeMiniPlayerPlaybackCommandService
{
    public static Func<YouTubeMiniPlayerState, double, Button, Button, Task> CreateChangeSpeedHandler(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        return (state, delta, speedResetButton, loopButton) => ChangeSpeedAsync(
            state,
            delta,
            speedResetButton,
            loopButton,
            runtimes,
            setStatus,
            setSpeedResetContent,
            setLoopContent,
            queueSave);
    }

    public static Func<YouTubeMiniPlayerState, double, Button, Button, Task> CreateSetSpeedHandler(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        return (state, speed, speedResetButton, loopButton) => SetSpeedAsync(
            state,
            speed,
            speedResetButton,
            loopButton,
            runtimes,
            setStatus,
            setSpeedResetContent,
            setLoopContent,
            queueSave);
    }

    public static Func<YouTubeMiniPlayerState, Button, Button, Task> CreateToggleLoopHandler(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        return (state, speedResetButton, loopButton) => ToggleLoopAsync(
            state,
            speedResetButton,
            loopButton,
            runtimes,
            setStatus,
            setSpeedResetContent,
            setLoopContent,
            queueSave);
    }

    public static Func<YouTubeMiniPlayerState, Button, Button, Task> CreateRefreshDisplayHandler(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent)
    {
        return (state, speedResetButton, loopButton) => RefreshDisplayAsync(
            state,
            speedResetButton,
            loopButton,
            runtimes,
            setStatus,
            setSpeedResetContent,
            setLoopContent);
    }

    public static Func<YouTubeMiniPlayerState, YouTubeMiniPlayerAbRepeatAction, Task<bool>> CreateAbRepeatHandler(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action queueSave)
    {
        return async (state, action) =>
        {
            var changed = await ExecuteAbRepeatActionAsync(state, runtimes, action, setStatus);
            if (changed)
            {
                queueSave();
            }

            return changed;
        };
    }

    public static async Task ChangeSpeedAsync(
        YouTubeMiniPlayerState state,
        double delta,
        Button speedResetButton,
        Button loopButton,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSpeedScript(delta, targetSpeed: null),
            showStatus: true,
            setStatus);
        ApplyResult(state, result, speedResetButton, loopButton, setSpeedResetContent, setLoopContent);
        queueSave();
    }

    public static async Task SetSpeedAsync(
        YouTubeMiniPlayerState state,
        double speed,
        Button speedResetButton,
        Button loopButton,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSpeedScript(delta: null, speed),
            showStatus: true,
            setStatus);
        ApplyResult(state, result, speedResetButton, loopButton, setSpeedResetContent, setLoopContent);
        queueSave();
    }

    public static async Task ToggleLoopAsync(
        YouTubeMiniPlayerState state,
        Button speedResetButton,
        Button loopButton,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent,
        Action queueSave)
    {
        var wasAbRepeatEnabled = state.IsAbRepeatEnabled;
        if (wasAbRepeatEnabled)
        {
            state.IsAbRepeatEnabled = false;
            state.IsLooping = true;
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            wasAbRepeatEnabled
                ? BuildAbRepeatScript(state)
                : YouTubeControlScriptService.BuildLoopScript(targetLooping: null),
            showStatus: true,
            setStatus);
        if (wasAbRepeatEnabled
            && !YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            state.IsAbRepeatEnabled = true;
            state.IsLooping = false;
        }

        ApplyResult(state, result, speedResetButton, loopButton, setSpeedResetContent, setLoopContent);
        queueSave();
    }

    public static async Task SetMutedAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        bool isMuted)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            localController.SetMuted(isMuted);
            return;
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            state.IsMuted = isMuted;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        state.IsMuted = isMuted;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMuteScript(
                isMuted,
                markMiniPlayerUserOverride: true),
            showStatus: false,
            _ => { });
        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
    }

    public static async Task<bool> TogglePlayPauseAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            var changed = localController.TogglePlayPause();
            if (!changed)
            {
                setStatus("ローカル動画はまだ読み込まれていません");
            }

            return changed;
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildPlayPauseScript(),
            showStatus: true,
            setStatus);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        return true;
    }

    public static async Task<bool> ToggleMutedAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return localController.ToggleMuted();
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMuteScript(
                !state.IsMuted,
                markMiniPlayerUserOverride: true),
            showStatus: true,
            setStatus);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        return true;
    }

    public static Task<bool> PauseAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return Task.FromResult(localController.Pause());
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildPauseScript(),
            showStatus: true,
            setStatus);
    }

    public static async Task<bool> SetPausedAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        bool isPaused,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return localController.SetPaused(isPaused);
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            return false;
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSetPausedScript(isPaused),
            showStatus: true,
            setStatus);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        return state.IsPaused == isPaused;
    }

    public static async Task<bool> SetMutedTransientAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        bool isMuted)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return localController.SetMutedTransient(isMuted);
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            return false;
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildMuteScript(
                isMuted,
                markMiniPlayerUserOverride: false),
            showStatus: false,
            _ => { });
        return YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result);
    }

    public static async Task<bool> SetPausedTransientAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        bool isPaused)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return localController.SetPausedTransient(isPaused);
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            return false;
        }

        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSetPausedTransientScript(isPaused),
            showStatus: false,
            _ => { });
        return YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result);
    }

    public static Task<bool> SetVolumeAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        double volume,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return Task.FromResult(localController.SetVolume(volume));
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildVolumeScript(
                volume,
                markMiniPlayerUserOverride: true),
            showStatus: true,
            setStatus);
    }

    public static async Task<bool> SetAudioBoostAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        int audioBoostDecibels,
        Action<string> setStatus)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            setStatus("音量ブーストはYouTube動画でのみ利用できます");
            return false;
        }

        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            setStatus("ミニプレイヤーをまだ操作できません");
            return false;
        }

        var normalizedDecibels = YouTubeMiniPlayerAudioBoostProfile.Normalize(audioBoostDecibels);
        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSetMiniPlayerAudioBoostScript(
                runtime.WebMessageToken,
                normalizedDecibels),
            showStatus: true,
            setStatus,
            treatAsUserGesture: true);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        state.AudioBoostDecibels = normalizedDecibels;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public static Task<bool> SeekRelativeAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        double deltaSeconds,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            var changed = localController.SeekRelative(deltaSeconds);
            if (!changed)
            {
                setStatus("ローカル動画はまだ読み込まれていません");
            }

            return Task.FromResult(changed);
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSeekRelativeScript(deltaSeconds),
            showStatus: true,
            setStatus);
    }

    public static Task<bool> SeekToRatioAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        double positionRatio)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return Task.FromResult(localController.SeekToRatio(positionRatio));
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSeekToRatioScript(positionRatio),
            showStatus: false,
            _ => { });
    }

    public static Task<bool> SeekToLiveEdgeAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            return Task.FromResult(false);
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSeekToLiveEdgeScript(),
            showStatus: true,
            setStatus);
    }

    public static Task<bool> ToggleLoopAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return Task.FromResult(localController.ToggleLoop());
        }

        if (state.IsAbRepeatEnabled)
        {
            return DisableAbRepeatAndEnableWholeLoopAsync(state, runtimes, setStatus);
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildLoopScript(targetLooping: null),
            showStatus: true,
            setStatus);
    }

    public static Task<bool> SetPlaybackRateAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        double playbackRate,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return Task.FromResult(localController.SetPlaybackRate(playbackRate));
        }

        return ExecuteListCommandAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildSpeedScript(delta: null, playbackRate),
            showStatus: true,
            setStatus);
    }

    public static async Task<bool> ExecuteAbRepeatActionAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerAbRepeatAction action,
        Action<string> setStatus)
    {
        if (TryGetLocalController(state, runtimes, out var localController))
        {
            return ExecuteLocalAbRepeatAction(state, localController, action, setStatus);
        }

        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            if (action == YouTubeMiniPlayerAbRepeatAction.Clear)
            {
                var hadState = state.AbRepeatStartSeconds.HasValue
                    || state.AbRepeatEndSeconds.HasValue
                    || state.IsAbRepeatEnabled;
                YouTubeMiniPlayerAbRepeatState.Clear(state);
                if (hadState)
                {
                    state.UpdatedAt = DateTimeOffset.UtcNow;
                }

                return hadState;
            }

            setStatus("ローカル動画を読み込んでからA-B範囲を設定してください");
            return false;
        }

        var previous = (
            state.AbRepeatStartSeconds,
            state.AbRepeatEndSeconds,
            state.IsAbRepeatEnabled,
            state.IsLooping);
        if (action is YouTubeMiniPlayerAbRepeatAction.SetStart or YouTubeMiniPlayerAbRepeatAction.SetEnd)
        {
            var playbackResult = await ExecuteAsync(
                state,
                runtimes,
                YouTubeControlScriptService.BuildPlaybackStateReadScript(),
                showStatus: true,
                setStatus);
            if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(playbackResult))
            {
                return false;
            }

            YouTubeMiniPlayerStateService.ApplyScriptResult(state, playbackResult);
        }

        switch (action)
        {
            case YouTubeMiniPlayerAbRepeatAction.SetStart:
                if (state.LastPositionSeconds is not { } start || !double.IsFinite(start))
                {
                    setStatus("現在の再生位置を取得できませんでした");
                    return false;
                }

                state.AbRepeatStartSeconds = Math.Max(0, start);
                if (state.AbRepeatEndSeconds is not { } end
                    || end - state.AbRepeatStartSeconds.Value < YouTubeMiniPlayerAbRepeatState.MinimumDurationSeconds)
                {
                    state.AbRepeatEndSeconds = null;
                    state.IsAbRepeatEnabled = false;
                }
                break;

            case YouTubeMiniPlayerAbRepeatAction.SetEnd:
                if (state.AbRepeatStartSeconds is not { } rangeStart)
                {
                    setStatus("先にA点を設定してください");
                    return false;
                }

                if (state.LastPositionSeconds is not { } rangeEnd
                    || !double.IsFinite(rangeEnd)
                    || rangeEnd - rangeStart < YouTubeMiniPlayerAbRepeatState.MinimumDurationSeconds)
                {
                    setStatus("B点はA点から0.5秒以上後に設定してください");
                    return false;
                }

                state.AbRepeatEndSeconds = rangeEnd;
                state.IsAbRepeatEnabled = true;
                state.IsLooping = false;
                break;

            case YouTubeMiniPlayerAbRepeatAction.Toggle:
                if (!YouTubeMiniPlayerAbRepeatState.HasValidRange(state))
                {
                    setStatus("A点とB点を設定してください");
                    return false;
                }

                state.IsAbRepeatEnabled = !state.IsAbRepeatEnabled;
                if (state.IsAbRepeatEnabled)
                {
                    state.IsLooping = false;
                }
                break;

            case YouTubeMiniPlayerAbRepeatAction.Clear:
                if (state.AbRepeatStartSeconds is null
                    && state.AbRepeatEndSeconds is null
                    && !state.IsAbRepeatEnabled)
                {
                    setStatus("A-B範囲は設定されていません");
                    return false;
                }

                YouTubeMiniPlayerAbRepeatState.Clear(state);
                break;
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        var applied = await ApplyYouTubeAbRepeatStateAsync(state, runtimes, setStatus);
        if (!applied)
        {
            (state.AbRepeatStartSeconds,
                state.AbRepeatEndSeconds,
                state.IsAbRepeatEnabled,
                state.IsLooping) = previous;
            return false;
        }

        setStatus(action switch
        {
            YouTubeMiniPlayerAbRepeatAction.SetStart => "A点を設定しました",
            YouTubeMiniPlayerAbRepeatAction.SetEnd => "B点を設定してA-B再生を開始しました",
            YouTubeMiniPlayerAbRepeatAction.Toggle when state.IsAbRepeatEnabled => "A-B再生を開始しました",
            YouTubeMiniPlayerAbRepeatAction.Toggle => "A-B再生を一時解除しました",
            _ => "A-B範囲を消去しました"
        });
        return true;
    }

    public static Task<bool> RepairAsync(YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<YouTubeMiniPlayerState, Uri, Task> loadRuntimeAsync,
        Func<YouTubeMiniPlayerState, Task<bool>> repairYouTubeAsync,
        Action<string> setStatus)
        => state.SourceKind == MiniPlayerSourceKind.LocalFile
            ? ReloadAsync(state, runtimes, loadRuntimeAsync, setStatus)
            : repairYouTubeAsync(state);

    public static async Task<bool> ReloadAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<YouTubeMiniPlayerState, Uri, Task> loadRuntimeAsync,
        Action<string> setStatus)
    {
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            var changed = TryGetLocalController(state, runtimes, out var localController)
                && runtimes.TryGetValue(state.Id, out var localRuntime) && !localRuntime.IsDisposed
                && localController.Reload();
            if (!changed)
            {
                setStatus("ローカル動画を再読み込みできませんでした");
            }

            return changed;
        }

        if (!runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.IsDisposed
            || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is null)
        {
            setStatus("ミニプレイヤーをまだ操作できません");
            return false;
        }

        if (!YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(state.Url, out var uri))
        {
            setStatus("ミニプレイヤーのURLを再読み込みできません");
            return false;
        }

        try
        {
            await YouTubeMiniPlayerStateCaptureService.CaptureAsync(state, runtimes);
            if (!runtimes.TryGetValue(state.Id, out var currentRuntime)
                || !ReferenceEquals(runtime, currentRuntime)
                || runtime.IsDisposed)
            {
                return false;
            }

            YouTubeMiniPlayerRuntimeLifecycleService.ResetWebView(runtime);
            await loadRuntimeAsync(state, uri);
            if (!runtimes.TryGetValue(state.Id, out currentRuntime)
                || !ReferenceEquals(runtime, currentRuntime)
                || runtime.IsDisposed
                || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is null)
            {
                setStatus("ミニプレイヤーの描画を再初期化できませんでした");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            setStatus("ミニプレイヤーの描画復旧に失敗しました: " + ex.Message);
            return false;
        }
    }

    public static async Task RefreshDisplayAsync(
        YouTubeMiniPlayerState state,
        Button speedResetButton,
        Button loopButton,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent)
    {
        var result = await ExecuteAsync(
            state,
            runtimes,
            YouTubeControlScriptService.BuildPlaybackStateReadScript(),
            showStatus: false,
            setStatus);
        ApplyResult(state, result, speedResetButton, loopButton, setSpeedResetContent, setLoopContent);
    }

    private static async Task<string?> ExecuteAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        string script,
        bool showStatus,
        Action<string> setStatus,
        bool treatAsUserGesture = false)
    {
        if (runtimes.TryGetValue(state.Id, out var runtime))
        {
            var inactivityResume = YouTubeMiniPlayerRuntimeLifecycleService.ResumeFromInactivity(runtime);
            if (inactivityResume.Reloading)
            {
                if (showStatus)
                {
                    setStatus("休止していたミニプレイヤーを再読み込みしています");
                }

                return null;
            }
        }

        return await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state,
            runtimes,
            script,
            showStatus,
            setStatus,
            treatAsUserGesture);
    }

    private static async Task<bool> ExecuteListCommandAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        string script,
        bool showStatus,
        Action<string> setStatus)
    {
        var result = await ExecuteAsync(state, runtimes, script, showStatus, setStatus);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        return true;
    }

    private static bool ExecuteLocalAbRepeatAction(
        YouTubeMiniPlayerState state,
        LocalVideoPlaybackController controller,
        YouTubeMiniPlayerAbRepeatAction action,
        Action<string> setStatus)
    {
        var changed = action switch
        {
            YouTubeMiniPlayerAbRepeatAction.SetStart => controller.SetAbRepeatStart(),
            YouTubeMiniPlayerAbRepeatAction.SetEnd => controller.SetAbRepeatEnd(),
            YouTubeMiniPlayerAbRepeatAction.Toggle => controller.ToggleAbRepeat(),
            YouTubeMiniPlayerAbRepeatAction.Clear => controller.ClearAbRepeat(),
            _ => false
        };
        if (!changed)
        {
            setStatus(action switch
            {
                YouTubeMiniPlayerAbRepeatAction.SetEnd when state.AbRepeatStartSeconds is null => "先にA点を設定してください",
                YouTubeMiniPlayerAbRepeatAction.SetEnd => "B点はA点から0.5秒以上後に設定してください",
                YouTubeMiniPlayerAbRepeatAction.Toggle => "A点とB点を設定してください",
                YouTubeMiniPlayerAbRepeatAction.Clear => "A-B範囲は設定されていません",
                _ => "ローカル動画を読み込んでからA-B範囲を設定してください"
            });
            return false;
        }

        setStatus(action switch
        {
            YouTubeMiniPlayerAbRepeatAction.SetStart => "A点を設定しました",
            YouTubeMiniPlayerAbRepeatAction.SetEnd => "B点を設定してA-B再生を開始しました",
            YouTubeMiniPlayerAbRepeatAction.Toggle when state.IsAbRepeatEnabled => "A-B再生を開始しました",
            YouTubeMiniPlayerAbRepeatAction.Toggle => "A-B再生を一時解除しました",
            _ => "A-B範囲を消去しました"
        });
        return true;
    }

    private static async Task<bool> DisableAbRepeatAndEnableWholeLoopAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        state.IsAbRepeatEnabled = false;
        state.IsLooping = true;
        var applied = await ApplyYouTubeAbRepeatStateAsync(state, runtimes, setStatus);
        if (!applied)
        {
            state.IsAbRepeatEnabled = true;
            state.IsLooping = false;
            return false;
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private static async Task<bool> ApplyYouTubeAbRepeatStateAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<string> setStatus)
    {
        var result = await ExecuteAsync(
            state,
            runtimes,
            BuildAbRepeatScript(state),
            showStatus: true,
            setStatus);
        if (!YouTubeMiniPlayerScriptExecutionService.IsSuccessfulResult(result))
        {
            return false;
        }

        YouTubeMiniPlayerStateService.ApplyScriptResult(state, result);
        return true;
    }

    private static string BuildAbRepeatScript(YouTubeMiniPlayerState state)
    {
        var expectedVideoId = YouTubeUrlService.TryGetVideoId(state.Url, out var videoId)
            ? videoId
            : null;
        return YouTubeControlScriptService.BuildAbRepeatScript(
            state.AbRepeatStartSeconds,
            state.AbRepeatEndSeconds,
            state.IsAbRepeatEnabled,
            state.IsLooping == true,
            expectedVideoId);
    }

    private static void ApplyResult(
        YouTubeMiniPlayerState state,
        string? resultJson,
        Button? speedResetButton,
        Button? loopButton,
        Action<Button, double?> setSpeedResetContent,
        Action<Button, bool?> setLoopContent)
    {
        var result = YouTubeMiniPlayerStateService.ApplyScriptResult(state, resultJson);
        if (result.SpeedChanged
            && result.Speed.HasValue
            && speedResetButton is not null)
        {
            setSpeedResetContent(speedResetButton, result.Speed.Value);
        }

        if (result.LoopingChanged
            && result.IsLooping.HasValue
            && loopButton is not null)
        {
            setLoopContent(loopButton, result.IsLooping.Value);
        }
    }

    private static bool TryGetLocalController(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        out LocalVideoPlaybackController controller)
    {
        controller = null!;
        if (state.SourceKind != MiniPlayerSourceKind.LocalFile
            || !runtimes.TryGetValue(state.Id, out var runtime)
            || runtime.LocalVideoView?.Controller is not { } localController)
        {
            return false;
        }

        controller = localController;
        return true;
    }
}
