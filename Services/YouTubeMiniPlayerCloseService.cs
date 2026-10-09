using System;
using System.Collections.Generic;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerCloseService
{
    public static bool TryClose(
        ICollection<YouTubeMiniPlayerState> states,
        IDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Guid miniPlayerId)
    {
        if (!YouTubeMiniPlayerRuntimeRegistryService.TryRemove(
            states,
            runtimes,
            miniPlayerId,
            out var runtime))
        {
            return false;
        }

        YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
        return true;
    }

    public static int DisposeAll(
        ICollection<YouTubeMiniPlayerState> states,
        IDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        var removedRuntimes = YouTubeMiniPlayerRuntimeRegistryService.RemoveAll(states, runtimes);
        foreach (var runtime in removedRuntimes)
        {
            YouTubeMiniPlayerRuntimeLifecycleService.DisposeRuntime(runtime);
        }

        return removedRuntimes.Count;
    }
}
