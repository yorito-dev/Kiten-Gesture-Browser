using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerClosedHistoryStore
{
    private readonly object _historyLock = new();
    private readonly List<YouTubeMiniPlayerState> _history = [];

    public void Remember(
        YouTubeMiniPlayerState state,
        Guid ownerSessionWindowId,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);

        var snapshot = YouTubeMiniPlayerNavigationService.CloneState(state);
        snapshot.OwnerSessionWindowId = ownerSessionWindowId;
        snapshot.UpdatedAt = updatedAt;

        lock (_historyLock)
        {
            _history.RemoveAll(current => current.Id == snapshot.Id);
            _history.Add(snapshot);
            if (_history.Count > YouTubeMiniPlayerStore.ClosedHistoryLimit)
            {
                _history.RemoveRange(
                    0,
                    _history.Count - YouTubeMiniPlayerStore.ClosedHistoryLimit);
            }
        }
    }

    public void Replace(IEnumerable<YouTubeMiniPlayerState>? states)
    {
        var normalized = YouTubeMiniPlayerStore.NormalizeClosedHistory(states);
        lock (_historyLock)
        {
            _history.Clear();
            _history.AddRange(normalized);
        }
    }

    public bool HasAny
    {
        get
        {
            lock (_historyLock)
            {
                return _history.Count > 0;
            }
        }
    }

    public bool TryTake(Guid? miniPlayerId, out YouTubeMiniPlayerState? state)
    {
        lock (_historyLock)
        {
            if (_history.Count == 0)
            {
                state = null;
                return false;
            }

            var index = miniPlayerId is { } id
                ? _history.FindLastIndex(current => current.Id == id)
                : _history.Count - 1;
            if (index < 0)
            {
                state = null;
                return false;
            }

            state = _history[index];
            _history.RemoveAt(index);
            return true;
        }
    }

    public bool RemoveOpenPlayers(IEnumerable<YouTubeMiniPlayerState> openPlayers)
    {
        ArgumentNullException.ThrowIfNull(openPlayers);

        lock (_historyLock)
        {
            var remaining = YouTubeMiniPlayerPersistenceService.ExcludeOpenPlayersFromClosedHistory(
                _history,
                openPlayers);
            if (remaining.Count == _history.Count)
            {
                return false;
            }

            _history.Clear();
            _history.AddRange(remaining);
            return true;
        }
    }

    public List<YouTubeMiniPlayerState> CreatePersistenceSnapshot()
    {
        lock (_historyLock)
        {
            return YouTubeMiniPlayerStore.NormalizeClosedHistory(
                _history.Select(YouTubeMiniPlayerNavigationService.CloneState));
        }
    }

    public IReadOnlyList<YouTubeMiniPlayerState> CreatePresentationSnapshot()
    {
        lock (_historyLock)
        {
            return _history
                .Select(YouTubeMiniPlayerNavigationService.CloneState)
                .ToList();
        }
    }
}
