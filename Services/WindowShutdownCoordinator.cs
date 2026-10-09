namespace TabBrowser.Services;

internal enum WindowShutdownState
{
    NotStarted,
    Running,
    Completed
}

internal enum WindowShutdownPhase
{
    NotStarted,
    BeginWindowShutdown,
    DrainWindowWork,
    FlushPersistence,
    DisposeWindowResources,
    ReleaseWebEnvironment,
    CleanupPrivateData,
    Completed
}

internal sealed record WindowShutdownContext(
    bool IsClosingAllWindows,
    bool IsPrivateWindow,
    bool PersistSession,
    bool SkipSingleWindowSessionSave,
    bool RecordCurrentWindowInClosedHistory,
    bool ShouldFlushSession,
    bool ShouldSaveSettings,
    bool ShouldSaveFloatingMedia,
    bool RequestSessionSaveAfterFinalClose)
{
    public bool ShouldCancelPendingSessionSave =>
        !IsClosingAllWindows && !SkipSingleWindowSessionSave;

    public static bool ShouldCountAsRemainingPersistableWindow(
        bool isCurrentWindow,
        bool hasPendingShutdownContext,
        bool shutdownStarted)
    {
        return !isCurrentWindow
            && !hasPendingShutdownContext
            && !shutdownStarted;
    }

    public static WindowShutdownContext Create(
        bool isClosingAllWindows,
        bool isPrivateWindow,
        bool persistSession,
        bool skipSingleWindowSessionSave,
        bool recordCurrentWindowInClosedHistory,
        bool hasOtherPersistableWindows)
    {
        var participatesInSingleWindowSave =
            !isClosingAllWindows && !skipSingleWindowSessionSave;
        var shouldFlushSession = participatesInSingleWindowSave
            && !isPrivateWindow
            && (persistSession || !hasOtherPersistableWindows);

        return new WindowShutdownContext(
            isClosingAllWindows,
            isPrivateWindow,
            persistSession,
            skipSingleWindowSessionSave,
            recordCurrentWindowInClosedHistory,
            shouldFlushSession,
            participatesInSingleWindowSave && !isPrivateWindow && persistSession,
            !isClosingAllWindows,
            recordCurrentWindowInClosedHistory && !isClosingAllWindows && !isPrivateWindow);
    }
}

internal sealed record WindowShutdownOperations(
    Action BeginWindowShutdown,
    Func<Task> DrainWindowWorkAsync,
    Func<Task<WindowPersistenceResult>> FlushPersistenceAsync,
    Action DisposeWindowResources,
    Action ReleaseWebEnvironment,
    Func<Task> CleanupPrivateDataAsync);

internal sealed class WindowShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Action<WindowShutdownPhase, Exception>? _faultObserver;
    private TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowShutdownState _state;
    private WindowShutdownPhase _phase;
    private WindowPersistenceResult? _preparedPersistence;

    public WindowShutdownCoordinator(
        Action<WindowShutdownPhase, Exception>? faultObserver = null)
    {
        _faultObserver = faultObserver;
    }

    public WindowShutdownState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public WindowShutdownPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    public bool IsStarted => State is not WindowShutdownState.NotStarted;

    public bool IsCompleted => State is WindowShutdownState.Completed;

    public Task Completion => _completion.Task;

    public WindowPersistenceResult? PersistenceResult { get; private set; }

    public void PreparePersistence(WindowPersistenceResult result)
    {
        if (!result.Succeeded) throw new ArgumentException("保存未完了の結果では終了できません。", nameof(result));
        lock (_gate)
        {
            if (_state == WindowShutdownState.NotStarted) _preparedPersistence = result;
        }
    }

    public void ClearPreparedPersistence()
    {
        lock (_gate) _preparedPersistence = null;
    }

    public bool TryBegin(WindowShutdownOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        WindowPersistenceResult? preparedPersistence;

        lock (_gate)
        {
            if (_state is not WindowShutdownState.NotStarted)
            {
                return false;
            }

            _state = WindowShutdownState.Running;
            _phase = WindowShutdownPhase.FlushPersistence;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PersistenceResult = null;
            preparedPersistence = _preparedPersistence;
            _preparedPersistence = null;
        }

        _ = RunRemainingPhasesAsync(operations, preparedPersistence);
        return true;
    }

    private async Task RunRemainingPhasesAsync(WindowShutdownOperations operations, WindowPersistenceResult? preparedPersistence)
    {
        var completion = _completion;
        var persistenceSucceeded = false;
        try
        {
            // 最初のClosingを確実にe.Cancel=trueで返してから、二段階目の終了へ進む。
            await Task.Yield();
            try
            {
                PersistenceResult = preparedPersistence ?? await operations.FlushPersistenceAsync();
                if (!PersistenceResult.Succeeded) return;
                persistenceSucceeded = true;
            }
            catch (Exception)
            {
                PersistenceResult = new([new("終了前保存", WindowPersistenceStatus.Failed)]);
                // 内容やURLを含み得る保存例外を通常の終了診断へ渡さない。
                return;
            }
            RunPhase(
                WindowShutdownPhase.BeginWindowShutdown,
                operations.BeginWindowShutdown);
            await RunPhaseAsync(
                WindowShutdownPhase.DrainWindowWork,
                operations.DrainWindowWorkAsync);
            RunPhase(
                WindowShutdownPhase.DisposeWindowResources,
                operations.DisposeWindowResources);
            RunPhase(
                WindowShutdownPhase.ReleaseWebEnvironment,
                operations.ReleaseWebEnvironment);
            await RunPhaseAsync(
                WindowShutdownPhase.CleanupPrivateData,
                operations.CleanupPrivateDataAsync);
        }
        finally
        {
            lock (_gate)
            {
                _phase = persistenceSucceeded ? WindowShutdownPhase.Completed : WindowShutdownPhase.NotStarted;
                _state = persistenceSucceeded ? WindowShutdownState.Completed : WindowShutdownState.NotStarted;
            }

            completion.TrySetResult();
        }
    }

    private void RunPhase(WindowShutdownPhase phase, Action operation)
    {
        SetPhase(phase);
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            ObserveFault(phase, ex);
        }
    }

    private async Task RunPhaseAsync(WindowShutdownPhase phase, Func<Task> operation)
    {
        SetPhase(phase);
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ObserveFault(phase, ex);
        }
    }

    private void SetPhase(WindowShutdownPhase phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    private void ObserveFault(WindowShutdownPhase phase, Exception exception)
    {
        try
        {
            _faultObserver?.Invoke(phase, exception);
        }
        catch
        {
            // 診断callbackの失敗で終了処理を中断しない。
        }
    }
}
