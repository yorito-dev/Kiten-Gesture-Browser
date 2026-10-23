using System.Threading;
using System.Diagnostics;

namespace TabBrowser.Services;

internal sealed class ApplicationWindowCloseCoordinator
{
    private int _permanentCloseAllRequested;
    private int _temporaryScopeCount;
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly Dictionary<string, Task> _pendingWrites = [];
    private bool _discardingNotes;

    // メモ破棄は明示操作からだけ行う。終了前保存の成功と対象の再確認まで本文を残す。
    public async Task<WindowPersistenceResult> CloseWithoutSavingNotesAsync(
        Func<bool> canDiscard,
        Func<Task<WindowPersistenceResult>> saveBeforeClose,
        Action discardNotes,
        Action<WindowPersistenceResult> closeWindows)
    {
        if (_discardingNotes || !canDiscard())
            return new([new("メモの保存中または編集Windowの変更", WindowPersistenceStatus.Pending)]);
        _discardingNotes = true;
        try
        {
            var result = await saveBeforeClose();
            if (!result.Succeeded) return result;
            if (!canDiscard())
                return new([new("メモの保存中または編集Windowの変更", WindowPersistenceStatus.Pending)]);
            discardNotes();
            closeWindows(result);
            return result;
        }
        finally
        {
            _discardingNotes = false;
        }
    }

    // 1回の保存待機は最大15秒。期限超過した書込は追跡し、同じ対象キーへ重ねて開始しない。
    public async Task<WindowPersistenceResult> SaveBeforeCloseAsync(
        IReadOnlyList<WindowPersistenceTarget> targets,
        Func<Task, TimeSpan, Task>? waitAsync = null)
    {
        if (!_persistenceGate.Wait(0))
        {
            return new([new("別の終了前保存", WindowPersistenceStatus.Pending)]);
        }
        var results = new List<WindowPersistenceTargetResult>();
        var elapsed = Stopwatch.StartNew();
        waitAsync ??= static (task, timeout) => task.WaitAsync(timeout);
        try
        {
            var started = new List<(WindowPersistenceTarget Target, Task? Write, bool Prior, WindowPersistenceStatus? Failure)>();
            foreach (var target in targets.DistinctBy(static target => target.Key))
            {
                try
                {
                    if (_pendingWrites.TryGetValue(target.Key, out var prior) && !prior.IsCompleted)
                    {
                        started.Add((target, prior, true, null));
                        continue;
                    }
                    var write = target.SaveAsync();
                    _pendingWrites[target.Key] = write;
                    _ = write.ContinueWith(static task => { _ = task.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    started.Add((target, write, false, null));
                }
                catch (Exception)
                {
                    started.Add((target, null, false, WindowPersistenceStatus.Failed));
                }
            }

            // 保存先は独立している。起動を直列待機させず、終了全体の期限だけを共有する。
            foreach (var (target, write, prior, failure) in started)
            {
                if (failure is { } status)
                {
                    results.Add(new(target.Name, status));
                    continue;
                }

                var timeout = TimeSpan.FromSeconds(15) - elapsed.Elapsed;
                if (timeout <= TimeSpan.Zero && !write!.IsCompleted)
                {
                    results.Add(new(target.Name, WindowPersistenceStatus.Pending));
                    continue;
                }
                try
                {
                    if (write!.IsCompleted)
                        await write;
                    else
                        await waitAsync(write, timeout);
                    // 古いsnapshotの完了だけで、新しい終了要求の成功とは扱わない。
                    results.Add(new(target.Name, prior ? WindowPersistenceStatus.Pending : WindowPersistenceStatus.Saved));
                }
                catch (TimeoutException)
                {
                    results.Add(new(target.Name, WindowPersistenceStatus.Pending));
                }
                catch (Exception)
                {
                    results.Add(new(target.Name, WindowPersistenceStatus.Failed));
                }
            }
            return new(results);
        }
        finally
        {
            foreach (var key in _pendingWrites.Where(static entry => entry.Value.IsCompleted).Select(static entry => entry.Key).ToList())
                _pendingWrites.Remove(key);
            _persistenceGate.Release();
        }
    }

    public bool IsClosingAllWindows =>
        Volatile.Read(ref _temporaryScopeCount) > 0
        || Volatile.Read(ref _permanentCloseAllRequested) != 0;

    public IDisposable EnterTemporaryCloseAllScope()
    {
        Interlocked.Increment(ref _temporaryScopeCount);
        return new TemporaryScope(this);
    }

    public void BeginPermanentCloseAll()
    {
        Interlocked.Exchange(ref _permanentCloseAllRequested, 1);
    }

    private void ExitTemporaryCloseAllScope()
    {
        Interlocked.Decrement(ref _temporaryScopeCount);
    }

    private sealed class TemporaryScope(ApplicationWindowCloseCoordinator owner) : IDisposable
    {
        private ApplicationWindowCloseCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitTemporaryCloseAllScope();
        }
    }
}

internal enum WindowPersistenceStatus { Saved, Failed, Pending }
internal sealed record WindowPersistenceTarget(string Key, string Name, Func<Task> SaveAsync);
internal sealed record WindowPersistenceTargetResult(string Name, WindowPersistenceStatus Status);
internal sealed record WindowPersistenceResult(IReadOnlyList<WindowPersistenceTargetResult> Targets)
{
    public static WindowPersistenceResult Success { get; } = new([]);
    public bool Succeeded => Targets.All(static target => target.Status == WindowPersistenceStatus.Saved);
    public string FailureMessage => "保存が完了していないため終了を中止しました（"
        + string.Join("、", Targets.Where(static target => target.Status != WindowPersistenceStatus.Saved)
            .Select(static target => target.Name + (target.Status == WindowPersistenceStatus.Pending ? ": 処理待ち" : ": 失敗")))
        + "）。内容は開いたままです。保存先を確認し、処理待ちの場合はしばらく待ってから、もう一度終了してください。";
}
