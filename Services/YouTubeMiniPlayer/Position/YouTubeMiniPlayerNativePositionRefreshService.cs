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
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition)
    {
        if (runtime.Overlay.IsNativePositionRefreshQueued)
        {
            return;
        }

        runtime.Overlay.IsNativePositionRefreshQueued = true;
        dispatcher.BeginInvoke(new Action(() =>
        {
            runtime.Overlay.IsNativePositionRefreshQueued = false;
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
