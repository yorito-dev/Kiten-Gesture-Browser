using TabBrowser.Models;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services;

internal interface IYouTubeMiniPlayerPersistenceOwner
{
    bool IsPrivateWindow { get; }
    Guid SessionWindowId { get; }
    int WindowIndex { get; }
    IReadOnlyList<YouTubeMiniPlayerState> States { get; }
    Task<bool> CaptureLoadedAsync();
    int RestoreWindowMemoryPlaceholders(Guid savedSessionWindowId, IEnumerable<YouTubeMiniPlayerState> players, HashSet<Guid> unavailableIds);
}

internal interface IYouTubeMiniPlayerPersistenceHost : IYouTubeMiniPlayerPersistenceOwner
{
    bool IsClosingWindow { get; }
    bool IsInitialized { get; }
    bool RestorePlaceholdersEnabled { get; }
    IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> GetOwners();
    Func<IEnumerable<YouTubeMiniPlayerState>, int> CreatePlaceholderRestorer();
    IEnumerable<YouTubeMiniPlayerState> LoadStoredPlayers();
    void SaveSnapshot(IReadOnlyList<YouTubeMiniPlayerState> players);
    bool RemoveOpenPlayersFromClosedHistory();
    void QueueSessionSave();
    void RefreshClosedHistory();
    void UpdateStateCaptureTimer();
    bool TryCloseRuntime(Guid id);
    void UpdateAudioIndicators();
    Task<bool> CaptureLoadedStatesAsync();
    Task<bool> CaptureFallbackStatesAsync(DateTimeOffset observedAt, TimeSpan eventStateFreshness);
    Task RefreshListAsync();
    void SetStatus(string message);
    void AppendDiagnostic(string category, Exception exception, string detail);
}

// 保存・補助取得の予約、共有capture状態、Window記憶／Workspaceの状態変換と復元手順をまとめる。
// Store、Runtime生成と実状態取得は既存serviceへ接続し、保存形式と列挙時点を維持する。
internal sealed class YouTubeMiniPlayerPersistenceController
{
    private const int YouTubeMiniPlayerSaveDebounceMilliseconds = 30000;
    private const int YouTubeMiniPlayerStateCaptureIntervalMilliseconds = 120000;
    private const int YouTubeMiniPlayerEventStateFreshnessSeconds = 30;
    private readonly IYouTubeMiniPlayerPersistenceHost _host;
    private readonly YouTubeMiniPlayerStateSyncCoordinator _youTubeMiniPlayerStateSyncCoordinator;
    private readonly YouTubeMiniPlayerAudioSessionCoordinator _audioSession;
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerSaveTimer;
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerStateCaptureTimer;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _saveRequestVersion;
    private long _persistedSaveRequestVersion;

    public YouTubeMiniPlayerPersistenceController(
        IYouTubeMiniPlayerPersistenceHost host,
        YouTubeMiniPlayerStateSyncCoordinator stateSync,
        YouTubeMiniPlayerAudioSessionCoordinator audioSession,
        OwnedDispatcherTimer saveTimer,
        OwnedDispatcherTimer captureTimer,
        Func<DateTimeOffset>? utcNow = null)
    {
        _host = host;
        _youTubeMiniPlayerStateSyncCoordinator = stateSync;
        _audioSession = audioSession;
        _youTubeMiniPlayerSaveTimer = saveTimer;
        _youTubeMiniPlayerStateCaptureTimer = captureTimer;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void InitializeTimers()
    {
        _youTubeMiniPlayerSaveTimer.Interval = TimeSpan.FromMilliseconds(YouTubeMiniPlayerSaveDebounceMilliseconds);
        _youTubeMiniPlayerSaveTimer.Tick += SaveTimer_Tick;
        _youTubeMiniPlayerStateCaptureTimer.Interval = TimeSpan.FromMilliseconds(YouTubeMiniPlayerStateCaptureIntervalMilliseconds);
        _youTubeMiniPlayerStateCaptureTimer.Tick += StateCaptureTimer_Tick;
    }

    public void StopTracking()
    {
        _youTubeMiniPlayerSaveTimer.Stop();
        _youTubeMiniPlayerSaveTimer.Tick -= SaveTimer_Tick;
        _youTubeMiniPlayerStateCaptureTimer.Stop();
        _youTubeMiniPlayerStateCaptureTimer.Tick -= StateCaptureTimer_Tick;
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _youTubeMiniPlayerSaveTimer.Stop();
        SaveYouTubeMiniPlayersNow();
    }

    private async void StateCaptureTimer_Tick(object? sender, EventArgs e)
        => await CaptureFallbackAndQueueSaveAsync();

    public async Task CaptureFallbackAndQueueSaveAsync()
    {
        var captureChanged = await CaptureYouTubeMiniPlayerStatesForFallbackAsync();
        if (captureChanged || _youTubeMiniPlayerStateSyncCoordinator.HasPendingEventStateSave)
        {
            _youTubeMiniPlayerStateSyncCoordinator.ClearPendingEventStateSave();
            QueueYouTubeMiniPlayerSave();
        }
    }

    public void RestoreYouTubeMiniPlayerPlaceholders()
    {
        if (!_host.RestorePlaceholdersEnabled)
        {
            return;
        }

        var restore = _host.CreatePlaceholderRestorer();
        restore(_host.LoadStoredPlayers());
        if (_host.RemoveOpenPlayersFromClosedHistory())
        {
            _host.QueueSessionSave();
            _host.RefreshClosedHistory();
        }

        _host.UpdateStateCaptureTimer();
    }

    private static async Task CaptureYouTubeMiniPlayerStatesForWindowMemoryAsync(
        IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> windows)
    {
        foreach (var window in windows.Where(static window => !window.IsPrivateWindow))
        {
            await window.CaptureLoadedAsync();
        }
    }

    public static async Task AttachFloatingMediaWindowMemorySnapshotAsync(
        SessionData windowMemory,
        IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> windows,
        YouTubeMiniPlayerAudioSessionCoordinator audioSession)
    {
        await CaptureYouTubeMiniPlayerStatesForWindowMemoryAsync(windows);
        windowMemory.WindowMemoryYouTubeMiniPlayers = CreateWindowMemoryYouTubeMiniPlayerSnapshot(windows, audioSession);
    }

    private static List<YouTubeMiniPlayerState> CreateWindowMemoryYouTubeMiniPlayerSnapshot(
        IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> windows,
        YouTubeMiniPlayerAudioSessionCoordinator audioSession)
    {
        return YouTubeMiniPlayerPersistenceService.CreateWindowMemorySaveSnapshot(
            windows
                .Where(static window => !window.IsPrivateWindow)
                .Select(window => (
                    window.SessionWindowId,
                    Players: window.States
                        .Where(static player => player.SourceKind == MiniPlayerSourceKind.YouTube)
                        .Select(state => CreatePersistentState(state, audioSession)))));
    }

    public int RestoreWindowMemoryYouTubeMiniPlayerPlaceholders(
        Guid savedSessionWindowId,
        IEnumerable<YouTubeMiniPlayerState> storedPlayers,
        HashSet<Guid> unavailableIds)
    {
        var restoreTargets = YouTubeMiniPlayerPersistenceService.CreateWindowMemoryRestoreTargets(
            storedPlayers, savedSessionWindowId, _host.SessionWindowId, _host.WindowIndex, unavailableIds);
        if (restoreTargets.Count == 0)
        {
            return 0;
        }

        var restore = _host.CreatePlaceholderRestorer();
        var restoredCount = 0;
        foreach (var state in restoreTargets)
        {
            try
            {
                var count = restore([state]);
                if (count == 0)
                {
                    continue;
                }

                restoredCount += count;
                unavailableIds.Add(state.Id);
            }
            catch (Exception ex)
            {
                _host.TryCloseRuntime(state.Id);
                _host.AppendDiagnostic(
                    "window-memory-youtube-mini-player-restore-failed", ex,
                    $"savedWindow={savedSessionWindowId:N};targetWindow={_host.WindowIndex};miniPlayer={state.Id:N}");
            }
        }

        if (restoredCount > 0)
        {
            _host.UpdateStateCaptureTimer();
            QueueYouTubeMiniPlayerSave();
            _ = _host.RefreshListAsync();
        }

        return restoredCount;
    }

    public static void RestoreFloatingMediaWindowMemoryPlaceholders<TOwner>(
        SessionData windowMemory,
        IReadOnlyDictionary<Guid, TOwner> restoredWindowsBySavedId,
        Func<IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner>> getOwners)
        where TOwner : IYouTubeMiniPlayerPersistenceOwner
    {
        var rememberedPlayers = windowMemory.WindowMemoryYouTubeMiniPlayers ?? [];
        if (rememberedPlayers.Count == 0 || restoredWindowsBySavedId.Count == 0)
        {
            return;
        }

        var unavailableIds = getOwners()
            .SelectMany(static window => window.States)
            .Select(static player => player.Id)
            .ToHashSet();
        foreach (var (savedSessionWindowId, restoredWindow) in restoredWindowsBySavedId)
        {
            restoredWindow.RestoreWindowMemoryPlaceholders(savedSessionWindowId, rememberedPlayers, unavailableIds);
        }
    }

    public void QueueYouTubeMiniPlayerSave()
    {
        _host.UpdateAudioIndicators();
        if (_host.IsPrivateWindow)
        {
            return;
        }

        Interlocked.Increment(ref _saveRequestVersion);
        YouTubeMiniPlayerTimerService.QueueSave(
            _youTubeMiniPlayerSaveTimer, _host.IsInitialized, _host.IsClosingWindow);
    }

    public void SaveYouTubeMiniPlayersNow()
    {
        var saveVersion = Volatile.Read(ref _saveRequestVersion);
        _host.SaveSnapshot(CaptureSaveSnapshot());
        MarkSavePersisted(saveVersion);
    }

    internal Action CapturePendingSaveOperation()
    {
        var saveVersion = Volatile.Read(ref _saveRequestVersion);
        var snapshotAt = _utcNow();
        var persistableWindows = GetPersistableWindows();
        if (Volatile.Read(ref _persistedSaveRequestVersion) >= saveVersion
            && !_youTubeMiniPlayerStateSyncCoordinator.HasPendingEventStateSave
            && !HasAdvancingPlaybackPosition(persistableWindows, snapshotAt))
        {
            return static () => { };
        }

        var snapshot = CaptureSaveSnapshot(persistableWindows, snapshotAt);
        return () =>
        {
            _host.SaveSnapshot(snapshot);
            MarkSavePersisted(saveVersion);
        };
    }

    private void MarkSavePersisted(long saveVersion)
    {
        while (true)
        {
            var persisted = Volatile.Read(ref _persistedSaveRequestVersion);
            if (persisted >= saveVersion
                || Interlocked.CompareExchange(ref _persistedSaveRequestVersion, saveVersion, persisted) == persisted)
            {
                return;
            }
        }
    }

    internal IReadOnlyList<YouTubeMiniPlayerState> CaptureSaveSnapshot()
    {
        return CaptureSaveSnapshot(GetPersistableWindows(), _utcNow());
    }

    private List<IYouTubeMiniPlayerPersistenceOwner> GetPersistableWindows()
    {
        return _host.GetOwners()
            .Where(static window => !window.IsPrivateWindow)
            .ToList();
    }

    private static bool HasAdvancingPlaybackPosition(
        IEnumerable<IYouTubeMiniPlayerPersistenceOwner> windows,
        DateTimeOffset snapshotAt)
    {
        return windows
            .SelectMany(static window => window.States)
            .Any(state =>
            {
                var current = YouTubeControlScriptService.NormalizePlaybackPosition(state.LastPositionSeconds);
                var projected = YouTubeMiniPlayerStateService.ResolvePersistedPlaybackPosition(state, snapshotAt);
                return current.HasValue
                    && projected.HasValue
                    && Math.Abs(projected.Value - current.Value) > 0.001;
            });
    }

    private IReadOnlyList<YouTubeMiniPlayerState> CaptureSaveSnapshot(
        IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> persistableWindows,
        DateTimeOffset snapshotAt)
    {
        return YouTubeMiniPlayerPersistenceService.CreateWindowSetSaveSnapshot(
            persistableWindows.Select(window => window.States.Select(
                state => CreatePersistentState(state, _audioSession, snapshotAt))));
    }

    public static YouTubeMiniPlayerState CreatePersistentState(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerAudioSessionCoordinator audioSession,
        DateTimeOffset? snapshotAt = null)
    {
        var persistent = audioSession.CreatePersistentState(
            new YouTubeMiniPlayerAudioFocusState(state.Id, state.IsMuted, state.IsPaused));
        var clone = YouTubeMiniPlayerNavigationService.CloneState(state);
        clone.IsMuted = persistent.IsMuted;
        clone.IsPaused = persistent.IsPaused;
        clone.LastPositionSeconds = YouTubeMiniPlayerStateService.ResolvePersistedPlaybackPosition(
            state,
            snapshotAt ?? DateTimeOffset.UtcNow);
        return clone;
    }

    public async Task<bool> CaptureYouTubeMiniPlayerStatesForLoadedAsync()
    {
        if (_host.IsClosingWindow || _youTubeMiniPlayerStateSyncCoordinator.IsCaptureRunning || _audioSession.IsPreviewActive)
        {
            return false;
        }

        if (!_youTubeMiniPlayerStateSyncCoordinator.TryEnterCapture(out var captureLease))
        {
            return false;
        }

        try
        {
            var changed = await _host.CaptureLoadedStatesAsync();
            _youTubeMiniPlayerStateSyncCoordinator.MarkCaptureSucceeded();
            return changed;
        }
        catch (Exception ex)
        {
            _youTubeMiniPlayerStateCaptureTimer.Stop();
            if (_youTubeMiniPlayerStateSyncCoordinator.TryMarkCaptureFailureReported())
            {
                _host.AppendDiagnostic("youtube-mini-player-state-periodic-capture-failed", ex, $"window={_host.WindowIndex}");
            }

            return false;
        }
        finally
        {
            captureLease!.Dispose();
        }
    }

    public async Task<bool> CaptureYouTubeMiniPlayerStatesForFallbackAsync()
    {
        if (_host.IsClosingWindow || _youTubeMiniPlayerStateSyncCoordinator.IsCaptureRunning || _audioSession.IsPreviewActive)
        {
            return false;
        }

        if (!_youTubeMiniPlayerStateSyncCoordinator.TryEnterCapture(out var captureLease))
        {
            return false;
        }

        try
        {
            var changed = await _host.CaptureFallbackStatesAsync(_utcNow(), TimeSpan.FromSeconds(YouTubeMiniPlayerEventStateFreshnessSeconds));
            _youTubeMiniPlayerStateSyncCoordinator.MarkCaptureSucceeded();
            return changed;
        }
        catch (Exception ex)
        {
            if (_youTubeMiniPlayerStateSyncCoordinator.TryMarkCaptureFailureReported())
            {
                _host.SetStatus("YouTubeミニプレイヤー状態の補助取得に失敗しました: " + ex.Message);
            }

            return false;
        }
        finally
        {
            captureLease!.Dispose();
        }
    }

    public IReadOnlyList<YouTubeMiniPlayerWorkspaceState> CaptureWorkspaceMediaOverlayShelfItems()
    {
        return _host.GetOwners()
            .SelectMany(static window => window.States)
            .Where(static state => state.SourceKind == MiniPlayerSourceKind.YouTube)
            .DistinctBy(static state => state.Id)
            .OrderBy(static state => state.CreatedAt)
            .Select(state => CreatePersistentState(state, _audioSession))
            .Select(YouTubeMiniPlayerWorkspaceState.FromPlayerState)
            .ToList();
    }

    public async Task<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>> CaptureWorkspaceMediaOverlaysAsync()
    {
        foreach (var window in _host.GetOwners())
        {
            await window.CaptureLoadedAsync();
        }

        return CaptureWorkspaceMediaOverlayShelfItems();
    }

    public Task OpenWorkspaceMediaSetAsync(YouTubeMiniPlayerSetItem item)
    {
        var createdAt = _utcNow();
        var states = item.Players
            .Where(static player => YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(player.Url, out _))
            .Select((player, index) => player.CreatePlayerState(_host.WindowIndex, createdAt.AddTicks(index)))
            .ToList();
        if (states.Count == 0)
        {
            _host.SetStatus("復元できるYouTubeミニプレイヤーがありません");
            return Task.CompletedTask;
        }

        var restore = _host.CreatePlaceholderRestorer();
        var restoredCount = restore(states);
        _host.UpdateStateCaptureTimer();
        QueueYouTubeMiniPlayerSave();
        _ = _host.RefreshListAsync();
        _host.SetStatus($"WorkspaceからYouTubeミニプレイヤーを復元しました（{restoredCount}件）");
        return Task.CompletedTask;
    }
}
