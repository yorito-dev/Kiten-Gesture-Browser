using System.Windows;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal delegate Task<(double HorizontalOffset, double VerticalOffset)?> YouTubeMiniPlayerToolbarPlacementResolver(
    CoreWebView2 core,
    FrameworkElement host,
    FrameworkElement panel,
    bool respectMouseActivity,
    Point? hostOriginOverride);

internal static class YouTubeMiniPlayerToolbarPlacementRefreshService
{
    public static async Task UpdateAsync(
        YouTubeMiniPlayerRuntime runtime,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerToolbarPlacementResolver resolvePlacementAsync,
        Action<YouTubeMiniPlayerRuntime, double, double> setToolbarPlacement)
    {
        if (!runtimes.TryGetValue(runtime.State.Id, out var current)
            || !ReferenceEquals(current, runtime)
            || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        var placementGeneration = runtime.OverlayPlacementGeneration;
        var requestGeneration = ++runtime.ToolbarPlacementRequestGeneration;
        await ApplyCurrentAsync(
            () => resolvePlacementAsync(
                core,
                runtime.WebHost,
                runtime.Toolbar,
                respectMouseActivity: false,
                hostOriginOverride: new Point(0, 0)),
            () => runtimes.TryGetValue(runtime.State.Id, out var latest)
                && ReferenceEquals(latest, runtime)
                && runtime.ToolbarPopup.IsOpen
                && runtime.OverlayPlacementGeneration == placementGeneration
                && runtime.ToolbarPlacementRequestGeneration == requestGeneration,
            (horizontal, vertical) => setToolbarPlacement(runtime, horizontal, vertical));
    }

    internal static async Task ApplyCurrentAsync(
        Func<Task<(double HorizontalOffset, double VerticalOffset)?>> resolvePlacementAsync,
        Func<bool> isCurrent,
        Action<double, double> setToolbarPlacement)
    {
        var placement = await resolvePlacementAsync();
        if (placement is { } value && isCurrent())
        {
            setToolbarPlacement(value.HorizontalOffset, value.VerticalOffset);
        }
    }
}
