using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class BrowsingHistoryService : IAsyncDisposable
{
    private const int FlushDelayMilliseconds = 2000;
    private const int FlushBatchThreshold = 32;
    private const int RecentVisitCapacity = 1024;
    private static readonly TimeSpan DuplicateVisitWindow = TimeSpan.FromSeconds(30);

    private readonly object _syncRoot = new();
    private readonly BrowsingHistoryRepository _repository;
    private readonly Dictionary<(Guid SourceId, BrowsingHistorySourceKind SourceKind, string Url, string ContainerId), BrowsingHistoryVisit> _pending = [];
    private readonly Dictionary<(Guid SourceId, BrowsingHistorySourceKind SourceKind, string Url, string ContainerId), DateTimeOffset> _recentVisits = [];
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _flushTimer;
    private BrowsingHistorySettings _settings = new();
    private bool _disposed;

    public BrowsingHistoryService()
        : this(new BrowsingHistoryRepository())
    {
    }

    public BrowsingHistoryService(BrowsingHistoryRepository repository)
    {
        _repository = repository;
        _flushTimer = new Timer(
            static state => ((BrowsingHistoryService)state!).OnFlushTimer(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event EventHandler? HistoryChanged;

    public void Configure(BrowsingHistorySettings? settings)
    {
        lock (_syncRoot)
        {
            _settings = BrowsingHistorySettingsProfile.Normalize(settings);
        }
    }

    public void EnqueueVisit(BrowsingHistoryVisit visit, bool isPrivate = false)
    {
        if (!TryNormalizeVisit(visit, out var normalized))
        {
            return;
        }

        var flushImmediately = false;
        lock (_syncRoot)
        {
            if (_disposed
                || !BrowsingHistorySettingsProfile.ShouldRecord(
                    _settings,
                    normalized.Url,
                    normalized.ContainerProfileId,
                    isPrivate))
            {
                return;
            }

            var key = (
                normalized.SourceId,
                normalized.SourceKind,
                normalized.Url,
                normalized.ContainerProfileId);
            if (_recentVisits.TryGetValue(key, out var lastVisitedAt)
                && normalized.VisitedAt - lastVisitedAt < DuplicateVisitWindow)
            {
                return;
            }

            _recentVisits[key] = normalized.VisitedAt;
            _pending[key] = normalized;
            TrimRecentVisits();
            flushImmediately = _pending.Count >= FlushBatchThreshold;
            if (!flushImmediately)
            {
                _flushTimer.Change(FlushDelayMilliseconds, Timeout.Infinite);
            }
        }

        if (flushImmediately)
        {
            _ = FlushSafeAsync();
        }
    }

    public async Task<IReadOnlyList<BrowsingHistoryEntry>> SearchAsync(
        BrowsingHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await _repository.SearchAsync(query, cancellationToken);
    }

    public async Task<BrowsingHistoryDeleteSummary> GetAllSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await _repository.GetAllSummaryAsync(cancellationToken);
    }

    public async Task<BrowsingHistoryDeleteSummary> GetHostSummaryAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await _repository.GetHostSummaryAsync(host, cancellationToken);
    }

    public async Task<BrowsingHistoryDeleteSummary> GetRangeSummaryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await _repository.GetRangeSummaryAsync(from, to, cancellationToken);
    }

    public Task<int> DeleteIdsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync(() => _repository.DeleteIdsAsync(ids, cancellationToken));
    }

    public Task<int> DeleteHostAsync(string host, CancellationToken cancellationToken = default)
    {
        return DeleteAsync(() => _repository.DeleteHostAsync(host, cancellationToken));
    }

    public Task<int> DeleteRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync(() => _repository.DeleteRangeAsync(from, to, cancellationToken));
    }

    public Task<int> DeleteContainerAsync(
        string containerProfileId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync(() =>
            _repository.DeleteContainerAsync(containerProfileId, from, to, cancellationToken));
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        return DeleteAsync(() => _repository.DeleteAllAsync(cancellationToken));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<BrowsingHistoryVisit> visits;
            lock (_syncRoot)
            {
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                visits = _pending.Values
                    .OrderBy(static visit => visit.VisitedAt)
                    .ToList();
                _pending.Clear();
            }

            if (visits.Count == 0)
            {
                return;
            }

            try
            {
                await Task.Run(
                        () => _repository.AddVisitsAsync(visits, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                lock (_syncRoot)
                {
                    foreach (var visit in visits)
                    {
                        _pending[(
                            visit.SourceId,
                            visit.SourceKind,
                            visit.Url,
                            visit.ContainerProfileId)] = visit;
                    }

                    if (!_disposed)
                    {
                        _flushTimer.Change(FlushDelayMilliseconds, Timeout.Infinite);
                    }
                }

                throw;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // 終了時の履歴保存失敗でアプリ終了を妨げません。
        }

        _flushTimer.Dispose();
        _flushLock.Dispose();
    }

    private async Task<int> DeleteAsync(Func<Task<int>> delete)
    {
        await FlushAsync();
        var deleted = await delete();
        if (deleted > 0)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        return deleted;
    }

    private void OnFlushTimer()
    {
        _ = FlushSafeAsync();
    }

    private async Task FlushSafeAsync()
    {
        try
        {
            await FlushAsync();
        }
        catch
        {
            // 記録失敗をWeb閲覧処理へ伝播させません。次回の一括保存で再試行します。
        }
    }

    private void TrimRecentVisits()
    {
        if (_recentVisits.Count <= RecentVisitCapacity)
        {
            return;
        }

        foreach (var key in _recentVisits
                     .OrderBy(static pair => pair.Value)
                     .Take(_recentVisits.Count - RecentVisitCapacity)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            _recentVisits.Remove(key);
        }
    }

    private static bool TryNormalizeVisit(
        BrowsingHistoryVisit visit,
        out BrowsingHistoryVisit normalized)
    {
        normalized = visit;
        if (!Uri.TryCreate(visit.Url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = visit with
        {
            Url = uri.AbsoluteUri,
            Title = string.IsNullOrWhiteSpace(visit.Title) ? uri.Host : visit.Title.Trim(),
            ContainerProfileId = ContainerProfile.NormalizeId(visit.ContainerProfileId),
            SourceKind = Enum.IsDefined(visit.SourceKind)
                ? visit.SourceKind
                : BrowsingHistorySourceKind.WebTab,
            VisitedAt = visit.VisitedAt == default ? DateTimeOffset.UtcNow : visit.VisitedAt.ToUniversalTime()
        };
        return true;
    }
}
