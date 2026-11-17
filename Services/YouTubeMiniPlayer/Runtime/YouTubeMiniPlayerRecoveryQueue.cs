using System.Runtime.CompilerServices;

namespace TabBrowser.Services;

// 同じ共有枠へ到達した要求をFIFOにする。取消された待機枠も、先行枠を追い越させない。
internal sealed class YouTubeMiniPlayerRecoveryQueue(SemaphoreSlim gate)
{
    private static readonly ConditionalWeakTable<SemaphoreSlim, YouTubeMiniPlayerRecoveryQueue> Queues = new();
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public static YouTubeMiniPlayerRecoveryQueue For(SemaphoreSlim gate) => Queues.GetValue(gate, static key => new(key));

    public Task<IDisposable> EnterAsync(CancellationToken token)
    {
        lock (_sync)
        {
            var previous = _tail;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tail = completed.Task;
            return EnterCoreAsync(previous, completed, token);
        }
    }

    private async Task<IDisposable> EnterCoreAsync(Task previous, TaskCompletionSource completed, CancellationToken token)
    {
        try
        {
            await previous.WaitAsync(token);
            await gate.WaitAsync(token);
            return new Lease(gate, completed);
        }
        catch
        {
            // 待機取消を先に完了させても、後続の実行開始は先行処理の終了を待つ。
            _ = previous.ContinueWith(_ => completed.TrySetResult(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate, TaskCompletionSource completed) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            gate.Release();
            completed.TrySetResult();
        }
    }
}
