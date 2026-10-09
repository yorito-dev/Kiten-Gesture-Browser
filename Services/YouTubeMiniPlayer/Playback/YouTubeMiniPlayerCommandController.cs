using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IYouTubeMiniPlayerCommandOwner
{
    IReadOnlyList<YouTubeMiniPlayerState> States { get; }
    bool TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime);
    bool IsRuntimeLoaded(YouTubeMiniPlayerRuntime runtime);
    bool LoadLocalMedia(YouTubeMiniPlayerRuntime runtime);
    Task LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri);
    Task<bool> TogglePlayPauseAsync(YouTubeMiniPlayerState state);
    Task<bool> ToggleMutedAsync(YouTubeMiniPlayerState state);
    Task<bool> SeekRelativeAsync(YouTubeMiniPlayerState state, double deltaSeconds);
    Task<bool> SeekToLiveEdgeAsync(YouTubeMiniPlayerState state);
    Task<bool> ToggleLoopAsync(YouTubeMiniPlayerState state);
    Task<bool> SetPlaybackRateAsync(YouTubeMiniPlayerState state, double speed);
    Task<bool> ExecuteAbRepeatActionAsync(YouTubeMiniPlayerState state, YouTubeMiniPlayerAbRepeatAction action);
    Task<bool> SetPausedAsync(YouTubeMiniPlayerState state, bool isPaused);
    Task SetMutedAsync(YouTubeMiniPlayerState state, bool isMuted);
    Task ExecuteAudioFocusAsync(YouTubeMiniPlayerAudioFocusMode mode, IReadOnlyCollection<Guid> ids, string message);
    Task<bool> RepairAsync(YouTubeMiniPlayerState state);
    void ActivateSourceTab(YouTubeMiniPlayerState state);
    void ToggleMinimized(YouTubeMiniPlayerState state);
    Action<YouTubeMiniPlayerState, bool> CreateSetMinimizedAction();
    void SetTopmost(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, bool isTopmost);
    void Close(Guid id);
    void DisposeAll();
    void QueueSave();
    void SetStatus(string message);
}

internal interface IYouTubeMiniPlayerCommandHost : IYouTubeMiniPlayerCommandOwner
{
    IReadOnlyList<IYouTubeMiniPlayerCommandOwner> GetOwners();
    IEnumerable<GestureMappingSettings> GestureMappings { get; }
    Task StopHoverPreviewAsync();
    Task RefreshListAsync();
    Task ExecuteRegisteredCommandAsync(string actionId, CommandInvocationContext context, object? parameter, bool showFailure);
}

// 個別／一覧／一括コマンドの対象解決と実行順、成功時の反映を所有する。
// 実Window操作と再生primitiveはownerへ接続し、Window横断の列挙時点を維持する。
internal sealed class YouTubeMiniPlayerCommandController(
    IYouTubeMiniPlayerCommandHost host, Func<DateTimeOffset>? utcNow = null)
{
    private readonly IYouTubeMiniPlayerCommandHost _host = host;
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    private bool TryResolveMiniPlayerCommandTarget(
        CommandActionContext context,
        out IYouTubeMiniPlayerCommandOwner owner,
        out YouTubeMiniPlayerState state)
    {
        if (context.ActiveTabId is { } targetId)
        {
            foreach (var window in _host.GetOwners())
            {
                var candidate = window.States.FirstOrDefault(
                    item => item.Id == targetId);
                if (candidate is not null
                    && window.TryGetRuntime(targetId, out _))
                {
                    owner = window;
                    state = candidate;
                    return true;
                }
            }
        }

        owner = null!;
        state = null!;
        return false;
    }

    public async Task ExecuteMiniPlayerPlayPauseCommandAsync(
        CommandActionContext context)
    {
        if (!TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            return;
        }

        var changed = await owner.TogglePlayPauseAsync(state);
        if (changed)
        {
            owner.QueueSave();
        }

        await _host.RefreshListAsync();
    }

    public async Task ExecuteMiniPlayerToggleMuteCommandAsync(
        CommandActionContext context)
    {
        if (!TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            return;
        }

        var changed = await owner.ToggleMutedAsync(state);
        if (changed)
        {
            owner.QueueSave();
        }

        await _host.RefreshListAsync();
    }

    public async Task ExecuteMiniPlayerSoloAudioCommandAsync(
        CommandActionContext context)
    {
        if (TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            await owner.ExecuteAudioFocusAsync(
                YouTubeMiniPlayerAudioFocusMode.SoloAudio,
                [state.Id],
                "このミニプレイヤーだけ音声を出しています");
        }
    }

    public async Task ExecuteMiniPlayerSoloPlaybackCommandAsync(
        CommandActionContext context)
    {
        if (TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            await owner.ExecuteAudioFocusAsync(
                YouTubeMiniPlayerAudioFocusMode.SoloPlayback,
                [state.Id],
                "このミニプレイヤーだけ再生しています");
        }
    }

    public Task ExecuteMiniPlayerSeekRelativeCommandAsync(
        CommandActionContext context)
    {
        var deltaSeconds = context.Parameter is double value ? value : 0;
        return ExecuteMiniPlayerPlaybackCommandAsync(
            context,
            (owner, state) =>
                owner.SeekRelativeAsync(state, deltaSeconds),
            deltaSeconds < 0
                ? $"{Math.Abs(deltaSeconds):0.#}秒戻しました"
                : $"{deltaSeconds:0.#}秒進めました");
    }

    public Task ExecuteMiniPlayerSeekToLiveEdgeCommandAsync(
        CommandActionContext context)
    {
        return ExecuteMiniPlayerPlaybackCommandAsync(
            context,
            (owner, state) =>
                owner.SeekToLiveEdgeAsync(state),
            "現在のライブ配信位置へ移動しました",
            queueSave: false);
    }

    public Task ExecuteMiniPlayerToggleLoopCommandAsync(
        CommandActionContext context)
    {
        if (!TryResolveMiniPlayerCommandTarget(context, out _, out var state))
        {
            return Task.CompletedTask;
        }

        var enablingLoop = state.IsLooping != true;
        return ExecuteMiniPlayerPlaybackCommandAsync(
            context,
            (owner, targetState) =>
                owner.ToggleLoopAsync(targetState),
            enablingLoop ? "ループ再生を有効にしました" : "ループ再生を解除しました");
    }

    public Task ExecuteMiniPlayerSetSpeedCommandAsync(
        CommandActionContext context)
    {
        var speed = context.Parameter is double value ? value : 0;
        return speed <= 0
            ? Task.CompletedTask
            : ExecuteMiniPlayerPlaybackCommandAsync(
                context,
                (owner, state) =>
                    owner.SetPlaybackRateAsync(state, speed),
                $"再生速度を{speed:0.##}xに変更しました");
    }

    public Task ExecuteMiniPlayerAbRepeatCommandAsync(
        CommandActionContext context)
    {
        return context.Parameter is YouTubeMiniPlayerAbRepeatAction action
            ? ExecuteMiniPlayerPlaybackCommandAsync(
                context,
                (owner, state) =>
                    owner.ExecuteAbRepeatActionAsync(state, action),
                "A-Bリピートを更新しました")
            : Task.CompletedTask;
    }

    public Task ExecuteMiniPlayerReloadCommandAsync(
        CommandActionContext context)
    {
        return ExecuteMiniPlayerPlaybackCommandAsync(
            context,
            (owner, state) =>
                owner.RepairAsync(state),
            "ミニプレイヤーの描画を復旧しました",
            queueSave: false);
    }

    public Task ExecuteMiniPlayerActivateSourceTabCommandAsync(
        CommandActionContext context)
    {
        if (TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            owner.ActivateSourceTab(state);
        }

        return Task.CompletedTask;
    }

    public async Task ExecuteMiniPlayerToggleMinimizedCommandAsync(
        CommandActionContext context)
    {
        if (TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            owner.ToggleMinimized(state);
            await _host.RefreshListAsync();
        }
    }

    public async Task ExecuteMiniPlayerToggleTopmostCommandAsync(
        CommandActionContext context)
    {
        if (!TryResolveMiniPlayerCommandTarget(context, out var owner, out var state)
            || !owner.TryGetRuntime(state.Id, out var runtime))
        {
            return;
        }

        owner.SetTopmost(state, runtime, !state.IsTopmost);
        await _host.RefreshListAsync();
    }

    public async Task ExecuteMiniPlayerCloseCommandAsync(
        CommandActionContext context)
    {
        if (TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            owner.Close(state.Id);
            await _host.RefreshListAsync();
        }
    }

    private async Task ExecuteMiniPlayerPlaybackCommandAsync(
        CommandActionContext context,
        Func<IYouTubeMiniPlayerCommandOwner, YouTubeMiniPlayerState, Task<bool>> execute,
        string successMessage,
        bool queueSave = true)
    {
        if (!TryResolveMiniPlayerCommandTarget(context, out var owner, out var state))
        {
            return;
        }

        var changed = await execute(owner, state);
        if (!changed)
        {
            return;
        }

        if (queueSave)
        {
            owner.QueueSave();
        }

        owner.SetStatus(successMessage);
        await _host.RefreshListAsync();
    }

    public static CommandInvocationContext CreateMiniPlayerCommandContext(Guid runtimeId)
    {
        return new CommandInvocationContext(
            CommandSurface.MiniPlayer,
            HasMiniPlayers: true,
            TargetId: runtimeId);
    }

    public async Task ExecuteMiniPlayerCommandAsync(
        string actionId,
        Guid miniPlayerId,
        object? parameter = null)
    {
        await _host.StopHoverPreviewAsync();
        await _host.ExecuteRegisteredCommandAsync(
            actionId,
            CreateMiniPlayerCommandContext(miniPlayerId),
            parameter,
            showFailure: true);
    }

    public async Task ExecuteYouTubeMiniPlayerListPlayPauseAsync(
        IYouTubeMiniPlayerCommandOwner owner,
        YouTubeMiniPlayerState state)
    {
        await _host.StopHoverPreviewAsync();
        if (!owner.TryGetRuntime(state.Id, out var runtime)
            || runtime.IsDisposed)
        {
            return;
        }

        var isLoaded = owner.IsRuntimeLoaded(runtime);
        if (isLoaded)
        {
            await ExecuteMiniPlayerCommandAsync(
                MiniPlayerCommandProvider.PlayPauseActionId,
                state.Id);
            return;
        }

        var previousPaused = state.IsPaused;
        state.IsPaused = false;
        state.UpdatedAt = _utcNow();
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            if (!owner.LoadLocalMedia(runtime))
            {
                state.IsPaused = previousPaused;
                owner.SetStatus("ローカル動画を読み込めませんでした");
                return;
            }
        }
        else
        {
            if (!YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(state.Url, out var uri))
            {
                state.IsPaused = previousPaused;
                owner.SetStatus("ミニプレイヤーのURLを読み込めません");
                return;
            }

            try
            {
                await owner.LoadRuntimeAsync(state, uri);
            }
            catch (Exception ex)
            {
                state.IsPaused = previousPaused;
                owner.SetStatus(
                    "YouTubeミニプレイヤーを読み込めませんでした: " + ex.Message);
                return;
            }
        }

        owner.QueueSave();
        owner.SetStatus("ミニプレイヤーを読み込んで再生します");
        await _host.RefreshListAsync();
    }

    public bool ShouldPlayAllLoadedYouTubeMiniPlayers()
    {
        var loadedTargets = GetLoadedYouTubeMiniPlayerTargets();
        return loadedTargets.Count > 0
            && loadedTargets.All(static target => target.State.IsPaused == true);
    }

    private IReadOnlyList<(IYouTubeMiniPlayerCommandOwner Owner, YouTubeMiniPlayerState State)>
        GetLoadedYouTubeMiniPlayerTargets()
    {
        return _host.GetOwners()
            .SelectMany(window => window.States
                .Where(state => window.TryGetRuntime(state.Id, out var runtime)
                    && window.IsRuntimeLoaded(runtime))
                .Select(state => (Owner: window, State: state)))
            .ToList();
    }

    public async Task PlayAllLoadedYouTubeMiniPlayersAsync()
    {
        var loadedTargets = GetLoadedYouTubeMiniPlayerTargets();
        if (loadedTargets.Count == 0)
        {
            _host.SetStatus("再生できる読み込み済みミニプレイヤーがありません");
            return;
        }

        var results = await Task.WhenAll(loadedTargets.Select(async target =>
        {
            var changed = await target.Owner.SetPausedAsync(target.State, isPaused: false);
            if (changed)
            {
                target.State.IsPaused = false;
            }

            return (target.Owner, Changed: changed);
        }));
        var successCount = results.Count(static result => result.Changed);
        foreach (var owner in results
                     .Where(static result => result.Changed)
                     .Select(static result => result.Owner)
                     .Distinct())
        {
            owner.QueueSave();
        }

        _host.SetStatus(
            successCount > 0
                ? $"読み込み済みミニプレイヤーをすべて再生しました（{successCount}件）"
                : "再生できる読み込み済みミニプレイヤーがありません");
        await _host.RefreshListAsync();
    }

    public async Task PauseAllYouTubeMiniPlayersAsync()
    {
        var successCount = 0;
        foreach (var window in _host.GetOwners())
        {
            var loadedStates = window.States
                .Where(state => window.TryGetRuntime(state.Id, out var runtime)
                    && window.IsRuntimeLoaded(runtime))
                .ToList();
            if (loadedStates.Count == 0)
            {
                continue;
            }

            var results = await Task.WhenAll(loadedStates.Select(state =>
                window.SetPausedAsync(state, isPaused: true)));
            for (var index = 0; index < results.Length; index++)
            {
                if (results[index])
                {
                    loadedStates[index].IsPaused = true;
                }
            }

            var changedCount = results.Count(static changed => changed);
            if (changedCount > 0)
            {
                successCount += changedCount;
                window.QueueSave();
            }
        }

        _host.SetStatus(
            successCount > 0
                ? $"YouTubeミニプレイヤーをすべて一時停止しました（{successCount}件）"
                : "一時停止できる読み込み済みYouTubeミニプレイヤーがありません");
        await _host.RefreshListAsync();
    }

    public async Task SetAllYouTubeMiniPlayersMutedAsync(bool isMuted)
    {
        var windows = _host.GetOwners()
            .Where(static window => window.States.Count > 0)
            .ToList();
        var targetCount = windows.Sum(static window => window.States.Count);
        if (targetCount == 0)
        {
            _host.SetStatus("ミュートを変更できるYouTubeミニプレイヤーがありません");
            return;
        }

        foreach (var window in windows)
        {
            await Task.WhenAll(window.States
                .ToList()
                .Select(state => window.SetMutedAsync(state, isMuted)));
            window.QueueSave();
        }

        _host.SetStatus(
            isMuted
                ? $"YouTubeミニプレイヤーをすべてミュートしました（{targetCount}件）"
                : $"YouTubeミニプレイヤーのミュートをすべて解除しました（{targetCount}件）");
        await _host.RefreshListAsync();
    }

    public void SetAllYouTubeMiniPlayersMinimized(bool isMinimized)
    {
        foreach (var owner in _host.GetOwners())
        {
            var setMinimized = owner.CreateSetMinimizedAction();
            foreach (var state in owner.States.ToList())
            {
                setMinimized(state, isMinimized);
            }
        }

        _ = _host.RefreshListAsync();
    }

    public void CloseAllYouTubeMiniPlayers()
    {
        foreach (var owner in _host.GetOwners())
        {
            owner.DisposeAll();
            owner.QueueSave();
        }

        _ = _host.RefreshListAsync();
    }

    public async Task ExecuteYouTubeMiniPlayerGestureAsync(
        YouTubeMiniPlayerRuntime runtime,
        string route)
    {
        if (!_host.TryGetRuntime(runtime.State.Id, out var currentRuntime)
            || !ReferenceEquals(runtime, currentRuntime)
            || !GestureSettingsCatalog.IsSupportedRoute(route)
            || route.StartsWith("wheel", StringComparison.Ordinal))
        {
            return;
        }

        var mapping = GestureSettingsCatalog.ResolveMappingsForScope(
                _host.GestureMappings,
                GestureCommandScope.MiniPlayer)
            .FirstOrDefault(candidate => candidate.Route.Equals(route, StringComparison.Ordinal));
        if (mapping is null)
        {
            return;
        }

        await ExecuteYouTubeMiniPlayerGestureCommandAsync(runtime.State.Id, mapping.Command);
    }

    public async Task ExecuteYouTubeMiniPlayerGestureCommandAsync(
        Guid miniPlayerId,
        string? command)
    {
        if (string.IsNullOrWhiteSpace(command)
            || !_host.TryGetRuntime(miniPlayerId, out _))
        {
            return;
        }

        var actionId = GestureCommandActionMap.TryGetActionId(command, out var mappedActionId)
            ? mappedActionId
            : command;
        await _host.StopHoverPreviewAsync();
        await _host.ExecuteRegisteredCommandAsync(
            actionId,
            CreateMiniPlayerCommandContext(miniPlayerId),
            parameter: null,
            showFailure: true);
    }

}
