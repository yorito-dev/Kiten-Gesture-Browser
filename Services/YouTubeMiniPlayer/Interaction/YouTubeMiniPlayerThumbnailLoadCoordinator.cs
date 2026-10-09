namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerThumbnailLoadCoordinator
{
    private readonly SemaphoreSlim _gate = new(3, 3);
    private CancellationTokenSource? _batchCancellation;

    public CancellationToken BeginBatch()
    {
        CancelBatch();
        _batchCancellation = new CancellationTokenSource();
        return _batchCancellation.Token;
    }

    public bool TryGetCurrentBatchToken(out CancellationToken cancellationToken)
    {
        if (_batchCancellation is null)
        {
            cancellationToken = default;
            return false;
        }

        cancellationToken = _batchCancellation.Token;
        return true;
    }

    public void CancelBatch()
    {
        var cancellation = _batchCancellation;
        _batchCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new GateLease(_gate);
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
