namespace TabBrowser.Services;

internal enum YouTubeContinuationCompletion { Ready, Expired, Redirected }

// 継続先の判定と、現在ドキュメントの初期化を別の寿命として扱う。
internal sealed class YouTubeMiniPlayerDocumentInitialization
{
    private ulong _navigationId;
    private long _generation;
    private bool _claimed;

    public void Begin(ulong navigationId)
    {
        _navigationId = navigationId;
        _generation++;
        _claimed = false;
    }

    public bool IsCurrent(ulong navigationId) => navigationId == _navigationId;

    public async Task CompleteAsync(ulong navigationId, Func<bool> documentAvailable,
        Func<Task<YouTubeContinuationCompletion>> resolveContinuation, Func<Func<bool>, Task> initialize)
    {
        var generation = _generation;
        bool IsCurrent() => generation == _generation && navigationId == _navigationId && documentAvailable();
        if (_claimed || !IsCurrent()) return;
        _claimed = true;
        var resolution = await resolveContinuation();
        if (!IsCurrent() || resolution == YouTubeContinuationCompletion.Redirected) return;
        await initialize(IsCurrent);
    }
}
