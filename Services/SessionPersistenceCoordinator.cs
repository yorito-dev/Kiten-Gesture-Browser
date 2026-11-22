using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class SessionPersistenceCoordinator
{
    internal static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(700);
    internal static readonly TimeSpan DefaultMaximumDeferThreshold = TimeSpan.FromMilliseconds(5000);
    internal static readonly TimeSpan ImmediateDelay = TimeSpan.FromMilliseconds(1);

    private readonly object _gate = new();
    private readonly object _scheduleApplyGate = new();
    private readonly SemaphoreSlim _asyncFlushGate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Action _cancelSchedule;
    private readonly Func<Task<SessionPersistenceWorkItem?>> _captureLatestWorkItemAsync;
    private readonly Action<Exception> _scheduledSaveFailure;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _maximumDeferThreshold;

    private DateTimeOffset? _firstQueuedAt;
    private bool _saveRunning;
    private bool _queuedDuringRun;
    private bool _flushRunning;
    private long _requestVersion;
    private long _lastPersistedRequestVersion;
    private long _scheduleGeneration;
    private long _flushEpoch;
    private Task _activeSaveTask = Task.CompletedTask;

    public SessionPersistenceCoordinator(
        Func<DateTimeOffset> now,
        Action<TimeSpan, Action> schedule,
        Action cancelSchedule,
        Func<Task<SessionPersistenceWorkItem?>> captureLatestWorkItemAsync,
        Action<Exception> scheduledSaveFailure,
        TimeSpan? debounceDelay = null,
        TimeSpan? maximumDeferThreshold = null)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _cancelSchedule = cancelSchedule ?? throw new ArgumentNullException(nameof(cancelSchedule));
        _captureLatestWorkItemAsync = captureLatestWorkItemAsync
            ?? throw new ArgumentNullException(nameof(captureLatestWorkItemAsync));
        _scheduledSaveFailure = scheduledSaveFailure
            ?? throw new ArgumentNullException(nameof(scheduledSaveFailure));
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        _maximumDeferThreshold = maximumDeferThreshold ?? DefaultMaximumDeferThreshold;
    }

    public void RequestSave()
    {
        var now = _now();
        ScheduledCallback? scheduledCallback = null;
        lock (_gate)
        {
            _requestVersion++;
            _firstQueuedAt ??= now;

            if (_flushRunning || _saveRunning)
            {
                _queuedDuringRun = true;
                return;
            }

            scheduledCallback = CreateScheduledCallbackLocked(now);
        }

        ApplySchedule(scheduledCallback.Value);
    }

    public void CancelPending()
    {
        long cancelGeneration;
        lock (_gate)
        {
            cancelGeneration = ++_scheduleGeneration;
            _firstQueuedAt = null;
            _queuedDuringRun = false;
        }

        CancelAppliedSchedule(cancelGeneration);
    }

    public Task FlushAsync() => FlushAsync(CancellationToken.None);

    public Task<bool> FlushPendingAsync() => FlushPendingAsync(CancellationToken.None);

    public Task<bool> FlushPendingAsync(CancellationToken cancellationToken)
        => FlushCoreAsync(cancellationToken, skipCaptureWhenAlreadyPersisted: true);

    public async Task FlushAsync(CancellationToken cancellationToken)
        => await FlushCoreAsync(cancellationToken, skipCaptureWhenAlreadyPersisted: false).ConfigureAwait(false);

    private async Task<bool> FlushCoreAsync(
        CancellationToken cancellationToken,
        bool skipCaptureWhenAlreadyPersisted)
    {
        await _asyncFlushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = BeginAsyncFlush();
            CancelAppliedSchedule(start.ScheduleGeneration);
            return await RunAsyncFlushAsync(
                    start.Epoch,
                    start.ActiveSaveTask,
                    cancellationToken,
                    skipCaptureWhenAlreadyPersisted)
                .ConfigureAwait(false);
        }
        finally
        {
            _asyncFlushGate.Release();
        }
    }

    internal Task GetActiveSaveTaskForTesting()
    {
        lock (_gate)
        {
            return _activeSaveTask;
        }
    }

    internal bool IsFlushRunningForTesting()
    {
        lock (_gate)
        {
            return _flushRunning;
        }
    }

    internal long GetRequestVersionForTesting()
    {
        lock (_gate)
        {
            return _requestVersion;
        }
    }

    internal bool IsLatestRequestPersisted(bool requireSavedSnapshot = false)
    {
        lock (_gate)
        {
            return !_saveRunning
                && !_flushRunning
                && _lastPersistedRequestVersion == _requestVersion
                && (!requireSavedSnapshot || _lastPersistedRequestVersion > 0);
        }
    }

    private AsyncFlushStart BeginAsyncFlush()
    {
        lock (_gate)
        {
            var epoch = ++_flushEpoch;
            _flushRunning = true;
            var scheduleGeneration = ++_scheduleGeneration;
            _firstQueuedAt = null;
            _queuedDuringRun = false;
            return new AsyncFlushStart(epoch, scheduleGeneration, _activeSaveTask);
        }
    }

    private async Task<bool> RunAsyncFlushAsync(
        long epoch,
        Task activeSaveTask,
        CancellationToken cancellationToken,
        bool skipCaptureWhenAlreadyPersisted)
    {
        long capturedRequestVersion = -1;
        try
        {
            await activeSaveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (skipCaptureWhenAlreadyPersisted && TryCompletePersistedFlush(epoch))
            {
                return false;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetRequestVersionForFinalSave(epoch, out capturedRequestVersion))
                {
                    return false;
                }

                var workItem = await _captureLatestWorkItemAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (workItem is not null)
                {
                    var completion = TryRegisterAsyncFinalWrite(epoch);
                    if (completion is null)
                    {
                        return false;
                    }

                    try
                    {
                        await workItem.SaveAsync().ConfigureAwait(false);
                        MarkRequestVersionPersisted(capturedRequestVersion);
                    }
                    finally
                    {
                        CompleteActiveSave(completion);
                    }
                }
                else if (!IsCurrentFlush(epoch))
                {
                    return false;
                }

                var completionResult = TryCompleteFlush(epoch, capturedRequestVersion);
                if (completionResult is FlushCompletion.Completed or FlushCompletion.Superseded)
                {
                    return true;
                }
            }
        }
        catch
        {
            var callback = EndFailedFlush(epoch, capturedRequestVersion);
            if (callback is { } scheduledCallback)
            {
                ApplySchedule(scheduledCallback);
            }

            throw;
        }
    }

    private bool TryGetRequestVersionForFinalSave(long epoch, out long requestVersion)
    {
        lock (_gate)
        {
            if (epoch != _flushEpoch)
            {
                requestVersion = -1;
                return false;
            }

            requestVersion = GetRequestVersionForFinalSaveLocked();
            return true;
        }
    }

    private long GetRequestVersionForFinalSaveLocked()
    {
        _scheduleGeneration++;
        _firstQueuedAt = null;
        _queuedDuringRun = false;
        return _requestVersion;
    }

    private TaskCompletionSource? TryRegisterAsyncFinalWrite(long epoch)
    {
        lock (_gate)
        {
            if (epoch != _flushEpoch)
            {
                return null;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeSaveTask = completion.Task;
            return completion;
        }
    }

    private void CompleteActiveSave(TaskCompletionSource completion)
    {
        completion.TrySetResult();
        lock (_gate)
        {
            if (ReferenceEquals(_activeSaveTask, completion.Task))
            {
                _activeSaveTask = Task.CompletedTask;
            }
        }
    }

    private bool IsCurrentFlush(long epoch)
    {
        lock (_gate)
        {
            return epoch == _flushEpoch;
        }
    }

    private FlushCompletion TryCompleteFlush(long epoch, long capturedRequestVersion)
    {
        lock (_gate)
        {
            if (epoch != _flushEpoch)
            {
                return FlushCompletion.Superseded;
            }

            if (_requestVersion != capturedRequestVersion)
            {
                return FlushCompletion.Retry;
            }

            _flushRunning = false;
            _firstQueuedAt = null;
            _queuedDuringRun = false;
            return FlushCompletion.Completed;
        }
    }

    private ScheduledCallback? EndFailedFlush(long epoch, long capturedRequestVersion)
    {
        lock (_gate)
        {
            if (epoch != _flushEpoch)
            {
                return null;
            }

            _flushRunning = false;
            if (capturedRequestVersion >= 0 && _requestVersion != capturedRequestVersion)
            {
                _firstQueuedAt ??= _now();
                return CreateScheduledCallbackLocked(_now());
            }

            _firstQueuedAt = null;
            _queuedDuringRun = false;
            return null;
        }
    }

    private ScheduledCallback CreateScheduledCallbackLocked(DateTimeOffset now)
    {
        var firstQueuedAt = _firstQueuedAt ?? now;
        var queuedDuration = now - firstQueuedAt;
        var delay = queuedDuration >= _maximumDeferThreshold
            ? ImmediateDelay
            : _debounceDelay;
        var generation = ++_scheduleGeneration;
        return new ScheduledCallback(delay, generation);
    }

    private void ApplySchedule(ScheduledCallback callback)
    {
        lock (_scheduleApplyGate)
        {
            if (!IsCurrentSchedule(callback.Generation))
            {
                return;
            }

            _cancelSchedule();
            if (!IsCurrentSchedule(callback.Generation))
            {
                return;
            }

            _schedule(callback.Delay, () => OnScheduledSave(callback.Generation));
        }
    }

    private void CancelAppliedSchedule(long generation)
    {
        lock (_scheduleApplyGate)
        {
            lock (_gate)
            {
                if (generation != _scheduleGeneration)
                {
                    return;
                }
            }

            _cancelSchedule();
        }
    }

    private bool IsCurrentSchedule(long generation)
    {
        lock (_gate)
        {
            return !_flushRunning && generation == _scheduleGeneration;
        }
    }

    private void OnScheduledSave(long generation)
    {
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_flushRunning
                || _saveRunning
                || generation != _scheduleGeneration)
            {
                return;
            }

            _saveRunning = true;
            _queuedDuringRun = false;
            _firstQueuedAt = null;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeSaveTask = completion.Task;
        }

        _ = ExecuteScheduledSaveAsync(completion);
    }

    private async Task ExecuteScheduledSaveAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        long capturedRequestVersion;
        lock (_gate)
        {
            capturedRequestVersion = _requestVersion;
        }

        try
        {
            var workItem = await _captureLatestWorkItemAsync().ConfigureAwait(false);
            if (workItem is not null)
            {
                await workItem.SaveAsync().ConfigureAwait(false);
                MarkRequestVersionPersisted(capturedRequestVersion);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        ScheduledCallback? scheduledCallback = null;
        lock (_gate)
        {
            _saveRunning = false;
            if (!_flushRunning && _queuedDuringRun)
            {
                _queuedDuringRun = false;
                scheduledCallback = CreateScheduledCallbackLocked(_now());
            }
        }

        if (failure is not null)
        {
            try
            {
                _scheduledSaveFailure(failure);
            }
            catch
            {
                // 診断処理の失敗で保存調停を停止しない。
            }
        }

        CompleteActiveSave(completion);

        if (scheduledCallback is { } callback)
        {
            ApplySchedule(callback);
        }
    }

    private bool TryCompletePersistedFlush(long epoch)
    {
        lock (_gate)
        {
            if (epoch != _flushEpoch
                || _saveRunning
                || _lastPersistedRequestVersion != _requestVersion)
            {
                return false;
            }

            _flushRunning = false;
            _firstQueuedAt = null;
            _queuedDuringRun = false;
            return true;
        }
    }

    private void MarkRequestVersionPersisted(long requestVersion)
    {
        lock (_gate)
        {
            _lastPersistedRequestVersion = Math.Max(_lastPersistedRequestVersion, requestVersion);
        }
    }

    private enum FlushCompletion
    {
        Retry,
        Completed,
        Superseded
    }

    private readonly record struct AsyncFlushStart(
        long Epoch,
        long ScheduleGeneration,
        Task ActiveSaveTask);

    private readonly record struct ScheduledCallback(TimeSpan Delay, long Generation);
}
