using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerNativePositionRefreshService
{
    public static void Queue(
        YouTubeMiniPlayerRuntime runtime,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Dispatcher dispatcher,
        Action<YouTubeMiniPlayerRuntime> forceNativePosition,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition)
    {
        _ = forceNativePosition;
        if (runtime.IsNativePositionRefreshQueued)
        {
            return;
        }

        runtime.IsNativePositionRefreshQueued = true;
        dispatcher.BeginInvoke(new Action(() =>
        {
            runtime.IsNativePositionRefreshQueued = false;
            if (!runtimes.TryGetValue(runtime.State.Id, out var current)
                || !ReferenceEquals(current, runtime)
                || !current.TaskbarWindow.IsVisible)
            {
                return;
            }

            refreshOverlayPosition(current);
        }), DispatcherPriority.Render);
    }
}
