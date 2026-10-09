using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerPersistenceService
{
    public static List<YouTubeMiniPlayerState> CreateRestoreTargets(
        IEnumerable<YouTubeMiniPlayerState> storedPlayers,
        int windowId,
        IEnumerable<YouTubeMiniPlayerState> existingPlayers)
    {
        var unavailableIds = existingPlayers.Select(static player => player.Id).ToHashSet();
        return storedPlayers
            .Where(player => player.WindowId == windowId && unavailableIds.Add(player.Id))
            .ToList();
    }

    public static List<YouTubeMiniPlayerState> ExcludeOpenPlayersFromClosedHistory(
        IEnumerable<YouTubeMiniPlayerState> closedPlayers,
        IEnumerable<YouTubeMiniPlayerState> openPlayers)
    {
        var openIds = openPlayers.Select(static player => player.Id).ToHashSet();
        return closedPlayers
            .Where(player => !openIds.Contains(player.Id))
            .ToList();
    }

    public static List<YouTubeMiniPlayerState> CreateSaveSnapshot(IEnumerable<YouTubeMiniPlayerState> players)
    {
        return players
            .Select(YouTubeMiniPlayerNavigationService.CloneState)
            .ToList();
    }

    public static List<YouTubeMiniPlayerState> CreateWindowSetSaveSnapshot(
        IEnumerable<IEnumerable<YouTubeMiniPlayerState>> windowPlayers)
    {
        var snapshot = new List<YouTubeMiniPlayerState>();
        var restoredWindowId = 0;
        foreach (var players in windowPlayers)
        {
            restoredWindowId++;
            foreach (var player in players)
            {
                var clone = YouTubeMiniPlayerNavigationService.CloneState(player);
                clone.WindowId = restoredWindowId;
                snapshot.Add(clone);
            }
        }

        return snapshot;
    }

    public static List<YouTubeMiniPlayerState> CreateWindowMemorySaveSnapshot(
        IEnumerable<(Guid SessionWindowId, IEnumerable<YouTubeMiniPlayerState> Players)> windowPlayers)
    {
        var snapshot = new List<YouTubeMiniPlayerState>();
        foreach (var (sessionWindowId, players) in windowPlayers)
        {
            if (sessionWindowId == Guid.Empty)
            {
                continue;
            }

            foreach (var player in players.Where(static player =>
                         player.SourceKind == MiniPlayerSourceKind.YouTube))
            {
                var clone = YouTubeMiniPlayerNavigationService.CloneState(player);
                clone.OwnerSessionWindowId = sessionWindowId;
                snapshot.Add(clone);
            }
        }

        return snapshot;
    }

    public static List<YouTubeMiniPlayerState> CreateWindowMemoryRestoreTargets(
        IEnumerable<YouTubeMiniPlayerState> storedPlayers,
        Guid savedSessionWindowId,
        Guid restoredSessionWindowId,
        int restoredWindowId,
        IEnumerable<Guid> unavailableIds)
    {
        if (savedSessionWindowId == Guid.Empty || restoredSessionWindowId == Guid.Empty)
        {
            return [];
        }

        var usedIds = unavailableIds.ToHashSet();
        var targets = new List<YouTubeMiniPlayerState>();
        foreach (var player in storedPlayers.Where(player =>
                     player.SourceKind == MiniPlayerSourceKind.YouTube
                     && player.OwnerSessionWindowId == savedSessionWindowId
                     && YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(player.Url, out _)))
        {
            var clone = YouTubeMiniPlayerNavigationService.CloneState(player);
            do
            {
                clone.Id = Guid.NewGuid();
            }
            while (!usedIds.Add(clone.Id));

            clone.WindowId = restoredWindowId;
            clone.OwnerSessionWindowId = restoredSessionWindowId;
            targets.Add(clone);
        }

        return targets;
    }

    public static void SaveSnapshot(
        YouTubeMiniPlayerStore store,
        IEnumerable<YouTubeMiniPlayerState> players)
    {
        store.Save(CreateSaveSnapshot(players));
    }
}
