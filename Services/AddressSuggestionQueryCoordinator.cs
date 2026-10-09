using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record AddressSuggestionCandidate(
    AddressSuggestionSource Source,
    Guid SourceId,
    BrowsingHistoryEntry? HistoryEntry = null);

internal sealed record AddressSuggestionQueryResult(
    string Query,
    long Generation,
    IReadOnlyList<AddressSuggestionCandidate> Candidates);

/// <summary>
/// Window単位の候補検索の寿命を管理する。画面の収集・反映は呼出元のコンテキストで行い、
/// WPF実体は保持しない。遅延と履歴検索の後も同じコンテキストへ戻す。
/// </summary>
internal sealed class AddressSuggestionQueryCoordinator : IDisposable
{
    internal const int HistoryQueryLimit = 16;
    internal const int HistoryMinimumQueryLength = 2;
    internal static readonly TimeSpan HistoryDelay = TimeSpan.FromMilliseconds(150);

    private readonly object _gate = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<BrowsingHistoryQuery, CancellationToken, Task<IReadOnlyList<BrowsingHistoryEntry>>> _searchHistoryAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _currentCancellation;
    private long _generation;
    private bool _disposed;

    public AddressSuggestionQueryCoordinator(
        CancellationToken lifetimeToken,
        Func<BrowsingHistoryQuery, CancellationToken, Task<IReadOnlyList<BrowsingHistoryEntry>>> searchHistoryAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _lifetimeToken = lifetimeToken;
        _searchHistoryAsync = searchHistoryAsync;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task UpdateAsync(
        string query,
        Func<IReadOnlyList<AddressSuggestionCandidate>> captureMemoryCandidates,
        bool isPrivateWindow,
        bool isHistoryEnabled,
        Action<AddressSuggestionQueryResult> publish)
    {
        CancellationTokenSource cancellation;
        CancellationTokenSource? previousCancellation;
        long generation;
        lock (_gate)
        {
            if (_disposed || _lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            previousCancellation = _currentCancellation;
            _currentCancellation = cancellation;
            generation = ++_generation;
        }

        var token = cancellation.Token;
        try
        {
            CancelRequest(previousCancellation);
            if (!IsCurrent(generation) || token.IsCancellationRequested)
            {
                return;
            }

            var candidates = captureMemoryCandidates().ToList();
            PublishCandidates(query, candidates, generation, publish);
            if (!IsCurrent(generation)
                || token.IsCancellationRequested
                || isPrivateWindow
                || !isHistoryEnabled
                || query.Length < HistoryMinimumQueryLength)
            {
                return;
            }

            try
            {
                await _delayAsync(HistoryDelay, token);
                if (!IsCurrent(generation) || token.IsCancellationRequested)
                {
                    return;
                }

                var history = await _searchHistoryAsync(
                    new BrowsingHistoryQuery(SearchText: query, Limit: HistoryQueryLimit), token);
                if (!IsCurrent(generation) || token.IsCancellationRequested)
                {
                    return;
                }

                candidates.AddRange(history.Select(CreateHistoryCandidate));
                PublishCandidates(query, candidates, generation, publish);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch
            {
                // 履歴検索に失敗しても、先に表示したタブとブックマーク候補は維持する。
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_currentCancellation, cancellation))
                {
                    _currentCancellation = null;
                }
            }

            // Cancel/Dispose側で先に破棄せず、この要求のawaitが完了してから解放する。
            cancellation.Dispose();
        }
    }

    public bool IsCurrent(AddressSuggestionQueryResult result) => IsCurrent(result.Generation);

    private bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return !_disposed && !_lifetimeToken.IsCancellationRequested && generation == _generation;
        }
    }

    public void Cancel() => Invalidate(dispose: false);

    public void OnPopupClosed(bool hadCandidates)
    {
        if (hadCandidates)
        {
            Cancel();
        }
    }

    public void Dispose() => Invalidate(dispose: true);

    private void Invalidate(bool dispose)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _disposed |= dispose;
            _generation++;
            cancellation = _currentCancellation;
            _currentCancellation = null;
        }

        CancelRequest(cancellation);
    }

    private static void CancelRequest(CancellationTokenSource? cancellation)
    {
        try
        {
            // キャンセル通知による再入を許すため、外部コールバックはlockの外で呼ぶ。
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 古い要求が既に完了してCTSを解放していても、世代の無効化は完了している。
        }
    }

    private void PublishCandidates(
        string query,
        IReadOnlyList<AddressSuggestionCandidate> candidates,
        long generation,
        Action<AddressSuggestionQueryResult> publish)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        var lookup = AddressSuggestionService.CreateDistinctStableKeyLookup(
            candidates, static candidate => candidate.Source.StableKey);
        var results = AddressSuggestionService.Search(
            query, lookup.Values.Select(static candidate => candidate.Source));
        var selectedCandidates = results.Select(result => lookup[result.Source.StableKey]).ToArray();
        if (IsCurrent(generation))
        {
            publish(new AddressSuggestionQueryResult(query, generation, selectedCandidates));
        }
    }

    private static AddressSuggestionCandidate CreateHistoryCandidate(BrowsingHistoryEntry entry) => new(
        new AddressSuggestionSource(
            AddressSuggestionKind.BrowsingHistory,
            $"history:{entry.Id}",
            entry.Url,
            string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title,
            entry.Host,
            entry.Host,
            entry.ContainerProfileId,
            LastUsedAt: entry.VisitedAt),
        Guid.Empty,
        entry);
}
