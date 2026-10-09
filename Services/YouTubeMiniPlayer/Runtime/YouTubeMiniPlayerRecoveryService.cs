using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerRecoveryStage
{
    WaitingForConfirmation,
    CapturingState,
    ReloadingCurrentWebView,
    RecreatingWebView,
    OpeningSourceTab
}

internal enum YouTubeMiniPlayerRecoveryResult
{
    NotNeeded,
    RecoveredByReload,
    RecoveredByRecreate,
    SourceTabOpened,
    Failed,
    Cancelled,
    AlreadyRunning
}

internal sealed record YouTubeMiniPlayerRecoveryCallbacks(
    Func<CancellationToken, Task<bool>> ConfirmFailureAsync,
    Func<CancellationToken, Task<YouTubeMiniPlayerPlaybackStateSnapshot>> CaptureStateAsync,
    Func<YouTubeMiniPlayerPlaybackStateSnapshot, CancellationToken, Task<bool>> ReloadCurrentWebViewAsync,
    Func<YouTubeMiniPlayerPlaybackStateSnapshot, CancellationToken, Task<bool>> RecreateWebViewAsync,
    Func<YouTubeMiniPlayerPlaybackStateSnapshot, CancellationToken, Task<bool>> OpenSourceTabAsync,
    Action<YouTubeMiniPlayerRecoveryStage, string>? StageChanged = null);

internal readonly record struct YouTubeMiniPlayerPlaybackHealthProbe(
    bool HasVideo,
    bool HasVisibleVideo,
    double VideoNaturalWidth,
    double VideoNaturalHeight,
    double? PositionSeconds,
    double? DurationSeconds,
    int? ReadyState,
    int? NetworkState,
    bool IsPaused,
    bool IsEnded,
    double? BufferedAheadSeconds,
    double? PlaybackRate,
    double? Volume,
    bool IsMuted,
    int? MediaErrorCode);

internal sealed class YouTubeMiniPlayerRecoveryService : IDisposable
{
    private const double BufferedPlaybackRecoveryFloorSeconds = 1;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, RecoveryOperation> _operations = [];
    private readonly SemaphoreSlim _recoveryGate;
    private bool _isDisposed;

    public YouTubeMiniPlayerRecoveryService(SemaphoreSlim? recoveryGate = null)
    {
        _recoveryGate = recoveryGate ?? new SemaphoreSlim(1, 1);
    }

    public Task<YouTubeMiniPlayerRecoveryResult> RequestAsync(
        Guid miniPlayerId,
        string reason,
        TimeSpan confirmationDelay,
        bool skipCurrentWebViewReload,
        YouTubeMiniPlayerRecoveryCallbacks callbacks,
        bool allowSourceTabFallback = false,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return Task.FromResult(YouTubeMiniPlayerRecoveryResult.Cancelled);
            }

            if (_operations.ContainsKey(miniPlayerId))
            {
                return Task.FromResult(YouTubeMiniPlayerRecoveryResult.AlreadyRunning);
            }

            var operation = new RecoveryOperation(
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _operations.Add(miniPlayerId, operation);
            operation.Task = ExecuteAsync(
                miniPlayerId,
                operation,
                reason,
                confirmationDelay,
                skipCurrentWebViewReload,
                allowSourceTabFallback,
                callbacks);
            return operation.Task;
        }
    }

    public bool CancelPending(Guid miniPlayerId)
    {
        lock (_sync)
        {
            if (!_operations.TryGetValue(miniPlayerId, out var operation)
                || operation.Stage != YouTubeMiniPlayerRecoveryStage.WaitingForConfirmation)
            {
                return false;
            }

            _operations.Remove(miniPlayerId);
            operation.Cancellation.Cancel();
            return true;
        }
    }

    public void Cancel(Guid miniPlayerId)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(miniPlayerId, out var operation))
            {
                operation.Cancellation.Cancel();
            }
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            foreach (var operation in _operations.Values)
            {
                operation.Cancellation.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            foreach (var operation in _operations.Values)
            {
                operation.Cancellation.Cancel();
            }
        }
    }

    public static bool TryParseHealthProbe(
        string? resultJson,
        out YouTubeMiniPlayerPlaybackHealthProbe probe)
    {
        probe = default;
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            probe = new YouTubeMiniPlayerPlaybackHealthProbe(
                GetBoolean(root, "hasVideo"),
                GetBoolean(root, "hasVisibleVideo"),
                GetDouble(root, "videoNaturalWidth") ?? 0,
                GetDouble(root, "videoNaturalHeight") ?? 0,
                GetDouble(root, "position"),
                GetDouble(root, "duration"),
                GetInt32(root, "readyState"),
                GetInt32(root, "networkState"),
                GetBoolean(root, "paused"),
                GetBoolean(root, "ended"),
                GetDouble(root, "bufferedAhead"),
                GetDouble(root, "speed"),
                GetDouble(root, "volume"),
                GetBoolean(root, "muted"),
                GetInt32(root, "mediaError"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsDefiniteFailure(YouTubeMiniPlayerPlaybackHealthProbe probe)
    {
        return probe.MediaErrorCode is > 0
            || !probe.HasVideo
            || !probe.HasVisibleVideo
            || probe.VideoNaturalWidth <= 0
            || probe.VideoNaturalHeight <= 0
            || probe.NetworkState == 3;
    }

    public static bool IsPersistentPlaybackFailure(
        YouTubeMiniPlayerPlaybackHealthProbe first,
        YouTubeMiniPlayerPlaybackHealthProbe second)
    {
        if (second.IsPaused || second.IsEnded)
        {
            return false;
        }

        if (IsDefiniteFailure(first) && IsDefiniteFailure(second))
        {
            return true;
        }

        // バッファ済みの一時的なデコード／描画待ちは、再読込すると複数動画で負荷を増幅する。
        if (!IsDefiniteFailure(second)
            && second.BufferedAheadSeconds is >= BufferedPlaybackRecoveryFloorSeconds)
        {
            return false;
        }

        if (first.PositionSeconds is not { } firstPosition
            || second.PositionSeconds is not { } secondPosition
            || secondPosition - firstPosition >= 0.25)
        {
            return false;
        }

        return second.ReadyState is < 3
            || second.NetworkState == 3
            || second.BufferedAheadSeconds is <= 0.1;
    }

    public static bool IsRecoveredPlaybackHealthy(
        YouTubeMiniPlayerPlaybackHealthProbe first,
        YouTubeMiniPlayerPlaybackHealthProbe? second,
        bool requireProgress)
    {
        if (IsDefiniteFailure(first))
        {
            return false;
        }

        if (!requireProgress)
        {
            return true;
        }

        if (second is not { } current
            || IsDefiniteFailure(current)
            || current.IsPaused
            || current.IsEnded
            || first.PositionSeconds is not { } firstPosition
            || current.PositionSeconds is not { } currentPosition)
        {
            return false;
        }

        return currentPosition - firstPosition >= 0.25;
    }

    private async Task<YouTubeMiniPlayerRecoveryResult> ExecuteAsync(
        Guid miniPlayerId,
        RecoveryOperation operation,
        string reason,
        TimeSpan confirmationDelay,
        bool skipCurrentWebViewReload,
        bool allowSourceTabFallback,
        YouTubeMiniPlayerRecoveryCallbacks callbacks)
    {
        var recoveryGateEntered = false;
        try
        {
            await Task.Yield();
            SetStage(operation, YouTubeMiniPlayerRecoveryStage.WaitingForConfirmation, reason, callbacks);
            if (confirmationDelay > TimeSpan.Zero)
            {
                await Task.Delay(confirmationDelay, operation.Cancellation.Token);
            }

            await _recoveryGate.WaitAsync(operation.Cancellation.Token);
            recoveryGateEntered = true;

            if (!await callbacks.ConfirmFailureAsync(operation.Cancellation.Token))
            {
                return YouTubeMiniPlayerRecoveryResult.NotNeeded;
            }

            SetStage(operation, YouTubeMiniPlayerRecoveryStage.CapturingState, reason, callbacks);
            var snapshot = await callbacks.CaptureStateAsync(operation.Cancellation.Token);

            if (!skipCurrentWebViewReload)
            {
                SetStage(operation, YouTubeMiniPlayerRecoveryStage.ReloadingCurrentWebView, reason, callbacks);
                if (await TryRunStageAsync(
                        token => callbacks.ReloadCurrentWebViewAsync(snapshot, token),
                        operation.Cancellation.Token))
                {
                    return YouTubeMiniPlayerRecoveryResult.RecoveredByReload;
                }
            }

            SetStage(operation, YouTubeMiniPlayerRecoveryStage.RecreatingWebView, reason, callbacks);
            if (await TryRunStageAsync(
                    token => callbacks.RecreateWebViewAsync(snapshot, token),
                    operation.Cancellation.Token))
            {
                return YouTubeMiniPlayerRecoveryResult.RecoveredByRecreate;
            }

            if (!allowSourceTabFallback)
            {
                return YouTubeMiniPlayerRecoveryResult.Failed;
            }

            SetStage(operation, YouTubeMiniPlayerRecoveryStage.OpeningSourceTab, reason, callbacks);
            return await TryRunStageAsync(
                    token => callbacks.OpenSourceTabAsync(snapshot, token),
                    operation.Cancellation.Token)
                ? YouTubeMiniPlayerRecoveryResult.SourceTabOpened
                : YouTubeMiniPlayerRecoveryResult.Failed;
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            return YouTubeMiniPlayerRecoveryResult.Cancelled;
        }
        finally
        {
            if (recoveryGateEntered)
            {
                _recoveryGate.Release();
            }

            lock (_sync)
            {
                if (_operations.TryGetValue(miniPlayerId, out var current)
                    && ReferenceEquals(operation, current))
                {
                    _operations.Remove(miniPlayerId);
                }
            }

            operation.Cancellation.Dispose();
        }
    }

    private static async Task<bool> TryRunStageAsync(
        Func<CancellationToken, Task<bool>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            return await execute(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStage(
        RecoveryOperation operation,
        YouTubeMiniPlayerRecoveryStage stage,
        string reason,
        YouTubeMiniPlayerRecoveryCallbacks callbacks)
    {
        operation.Stage = stage;
        callbacks.StageChanged?.Invoke(stage, reason);
    }

    private static bool GetBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;
    }

    private static double? GetDouble(JsonElement root, string propertyName)
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

    private sealed class RecoveryOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public YouTubeMiniPlayerRecoveryStage Stage { get; set; } =
            YouTubeMiniPlayerRecoveryStage.WaitingForConfirmation;

        public Task<YouTubeMiniPlayerRecoveryResult> Task { get; set; } =
            System.Threading.Tasks.Task.FromResult(YouTubeMiniPlayerRecoveryResult.Cancelled);
    }
}
