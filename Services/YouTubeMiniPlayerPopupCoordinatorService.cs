using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerPopupCoordinatorService
{
    public static void SetPosition(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<YouTubeMiniPlayerState, Point> getScreenPosition,
        Action<YouTubeMiniPlayerRuntime> queueNativePositionRefresh,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        YouTubeMiniPlayerPopupService.SetRuntimeScreenPosition(
            runtime,
            getScreenPosition(state),
            queueNativePositionRefresh,
            updateToolbarPlacement,
            updateTitleBandPlacement,
            refreshOverlayPosition);
    }

    public static void ForceNativePosition(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Func<YouTubeMiniPlayerState, Point> getScreenPosition)
    {
        if (runtimes.TryGetValue(state.Id, out var runtime))
        {
            ForceNativePosition(runtime, getScreenPosition);
        }
    }

    public static void ForceNativePosition(
        YouTubeMiniPlayerRuntime runtime,
        Func<YouTubeMiniPlayerState, Point> getScreenPosition)
    {
        YouTubeMiniPlayerPopupService.ForceRuntimeNativePosition(
            runtime,
            getScreenPosition(runtime.State));
    }

    public static void SyncOffsets(
        YouTubeMiniPlayerRuntime runtime,
        Func<YouTubeMiniPlayerState, Point> getScreenPosition,
        Action<YouTubeMiniPlayerRuntime> queueNativePositionRefresh,
        Action<YouTubeMiniPlayerRuntime> refreshHover)
    {
        YouTubeMiniPlayerPopupService.SyncRuntimeOffsets(
            runtime,
            getScreenPosition(runtime.State),
            queueNativePositionRefresh,
            refreshHover);
    }
}
