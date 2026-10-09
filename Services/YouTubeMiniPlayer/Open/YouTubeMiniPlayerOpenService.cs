using System;
using System.Collections.Generic;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerOpenService
{
    public static YouTubeMiniPlayerRuntime CreateAndRegister(
        BrowserTab sourceTab,
        int windowId,
        string url,
        string? title,
        bool isShorts,
        Rect bounds,
        YouTubeMiniPlayerController controller,
        Func<YouTubeMiniPlayerState, YouTubeMiniPlayerRuntime> createRuntime,
        Action<YouTubeMiniPlayerState, UIElement> setPosition,
        Action<Guid> bringToFront)
    {
        var state = YouTubeMiniPlayerStateFactory.Create(
            sourceTab,
            windowId,
            url,
            title,
            isShorts,
            bounds,
            DateTimeOffset.UtcNow);

        var runtime = createRuntime(state);
        controller.Register(state, runtime);
        setPosition(state, runtime.Root);
        bringToFront(state.Id);
        return runtime;
    }
}
