using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRuntimeRegistryService
{
    public static void Register(
        ICollection<YouTubeMiniPlayerState> states,
        IDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime)
    {
        states.Add(state);
        runtimes[state.Id] = runtime;
    }

    public static bool TryRemove(
        ICollection<YouTubeMiniPlayerState> states,
        IDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Guid stateId,
        out YouTubeMiniPlayerRuntime runtime)
    {
        if (!runtimes.Remove(stateId, out runtime!))
        {
            return false;
        }

        states.Remove(runtime.State);
        return true;
    }

    public static List<YouTubeMiniPlayerRuntime> RemoveAll(
        ICollection<YouTubeMiniPlayerState> states,
        IDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        var removedRuntimes = runtimes.Values.ToList();
        runtimes.Clear();
        states.Clear();
        return removedRuntimes;
    }
}
