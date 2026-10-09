using System;
using System.Collections.Generic;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRestoreService
{
    public static int RestorePlaceholders(
        IEnumerable<YouTubeMiniPlayerState> storedPlayers,
        int windowId,
        YouTubeMiniPlayerController controller,
        Func<YouTubeMiniPlayerState, YouTubeMiniPlayerRuntime> createRuntime,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clampToHost,
        Action<Guid> bringToFront,
        Action<YouTubeMiniPlayerRuntime, bool> applyMinimizedState)
    {
        _ = bringToFront;
        var restoredCount = 0;
        foreach (var state in YouTubeMiniPlayerPersistenceService.CreateRestoreTargets(
            storedPlayers,
            windowId,
            controller.States))
        {
            var runtime = createRuntime(state);
            controller.Register(state, runtime);
            clampToHost(state, runtime.Root, true);
            if (state.IsMinimized)
            {
                applyMinimizedState(runtime, false);
                restoredCount++;
                continue;
            }

            applyMinimizedState(runtime, false);
            restoredCount++;
        }

        return restoredCount;
    }
}
