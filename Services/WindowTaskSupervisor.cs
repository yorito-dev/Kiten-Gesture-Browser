namespace TabBrowser.Services;

internal sealed record WindowTaskDrainResult(bool TimedOut, int PendingTaskCount);

internal sealed class WindowTaskSupervisor : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string, Exception>? _faultObserver;
    private readonly Dictionary<long, TrackedOperation> _operations = [];
    private long _nextOperationId;
    private bool _shutdownStarted;
    private bool _disposed;

    public WindowTaskSupervisor(Action<string, Exception>? faultObserver = null)
    {
        _lifetimeToken = _lifetimeCancellation.Token;
        _faultObserver = faultObserver;
    }

    public CancellationToken LifetimeToken => _lifetimeToken;

    internal bool IsShutdownStarted
    {
        get
        {
            lock (_gate)
            {
                return _shutdownStarted;
            }
        }
    }

    internal int ActiveTaskCount
    {
        get
        {
            lock (_gate)
            {
                return _operations.Count;
            }
        }
    }

    public bool TryRun(string operation, Func<CancellationToken, Task> operationFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(operationFactory);

        if (!TryReserve(operation, out var trackedOperation))
        {
            return false;
        }

        Task task;
        try
        {
            task = operationFactory(_lifetimeToken)
                ?? Task.FromException(new InvalidOperationException("Window task factory returned null."));
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        _ = ObserveAsync(trackedOperation!, task);
        return true;
    }

    public bool TryTrack(string operation, Task task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(task);

        if (!TryReserve(operation, out var trackedOperation))
        {
            ObserveRejectedTask(operation, task);
            return false;
        }

        _ = ObserveAsync(trackedOperation!, task);
        return true;
    }

    public bool BeginShutdown()
    {
        lock (_gate)
        {
            if (_shutdownStarted)
            {
                return false;
            }

            _shutdownStarted = true;
        }

        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposeとの競合時もshutdown開始済みという終端状態を維持する。
        }

        return true;
    }

    public async Task<WindowTaskDrainResult> DrainAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task[] completions;
        lock (_gate)
        {
            completions = _operations.Values
                .Select(static operation => operation.Completion.Task)
                .ToArray();
        }

        if (completions.Length == 0)
        {
            return new WindowTaskDrainResult(false, 0);
        }

        var allCompleted = Task.WhenAll(completions);
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await allCompleted.ConfigureAwait(false);
        }
        else
        {
            var timeoutTask = Task.Delay(timeout);
            if (!ReferenceEquals(await Task.WhenAny(allCompleted, timeoutTask).ConfigureAwait(false), allCompleted))
            {
                return new WindowTaskDrainResult(true, GetActiveTaskCount());
            }

            await allCompleted.ConfigureAwait(false);
        }

        return new WindowTaskDrainResult(false, GetActiveTaskCount());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        BeginShutdown();
        _lifetimeCancellation.Dispose();
    }

    private bool TryReserve(string operation, out TrackedOperation? trackedOperation)
    {
        lock (_gate)
        {
            if (_shutdownStarted || _disposed)
            {
                trackedOperation = null;
                return false;
            }

            var operationId = ++_nextOperationId;
            trackedOperation = new TrackedOperation(operationId, operation);
            _operations.Add(operationId, trackedOperation);
            return true;
        }
    }

    private async Task ObserveAsync(TrackedOperation trackedOperation, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ObserveFault(trackedOperation.Operation, ex);
        }
        finally
        {
            lock (_gate)
            {
                _operations.Remove(trackedOperation.Id);
            }

            trackedOperation.Completion.TrySetResult(null);
        }
    }

    private void ObserveRejectedTask(string operation, Task task)
    {
        _ = ObserveRejectedTaskAsync(operation, task);
    }

    private async Task ObserveRejectedTaskAsync(string operation, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ObserveFault(operation, ex);
        }
    }

    private void ObserveFault(string operation, Exception exception)
    {
        try
        {
            _faultObserver?.Invoke(operation, exception);
        }
        catch
        {
            // 診断callbackの失敗でWindow task監督を止めない。
        }
    }

    private int GetActiveTaskCount()
    {
        lock (_gate)
        {
            return _operations.Count;
        }
    }

    private sealed record TrackedOperation(
        long Id,
        string Operation,
        TaskCompletionSource<object?> Completion)
    {
        public TrackedOperation(long id, string operation)
            : this(
                id,
                operation,
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously))
        {
        }
    }
}
