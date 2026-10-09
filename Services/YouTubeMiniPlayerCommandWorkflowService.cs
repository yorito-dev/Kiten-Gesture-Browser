using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerCommandCallbacks(
    Action<Guid, bool> BringToFront,
    Action<YouTubeMiniPlayerState> ToggleMinimized,
    Action<YouTubeMiniPlayerState, bool> SetMinimized,
    Action<YouTubeMiniPlayerRuntime, bool> ApplyMinimizedState,
    Action<YouTubeMiniPlayerState> ActivateSourceTab,
    Action<Guid> Close,
    Action DisposeAll);

internal sealed record YouTubeMiniPlayerCommandWorkflowDependencies(
    Func<int> GetNextZIndex,
    Action<YouTubeMiniPlayerRuntime> UpdateToolbarPlacement,
    Action<YouTubeMiniPlayerRuntime> UpdateTitleBandPlacement,
    Action<YouTubeMiniPlayerState, UIElement> SetPosition,
    Func<YouTubeMiniPlayerState, Task> CaptureStateAsync,
    Func<YouTubeMiniPlayerSourceTabOpenRequest, BrowserTab> OpenReopenedTab,
    Action<BrowserTab> SelectTab,
    Func<Task> ActivateSelectedTabAsync,
    Action QueueSave,
    Action UpdateStateCaptureTimer,
    Action<string> SetStatus);

internal static class YouTubeMiniPlayerCommandWorkflowService
{
    public static YouTubeMiniPlayerCommandCallbacks Create(
        YouTubeMiniPlayerController controller,
        IEnumerable<BrowserTab> tabs,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        return new YouTubeMiniPlayerCommandCallbacks(
            (miniPlayerId, reopenPopup) => BringToFront(
                miniPlayerId,
                reopenPopup,
                controller.Runtimes,
                dependencies),
            state => ToggleMinimized(
                state,
                controller.Runtimes,
                dependencies),
            (state, isMinimized) => SetMinimized(
                state,
                isMinimized,
                controller.Runtimes,
                dependencies),
            (runtime, activate) => YouTubeMiniPlayerMinimizeService.ApplyState(
                runtime,
                dependencies.UpdateTitleBandPlacement,
                dependencies.SetPosition,
                activate),
            state => _ = YouTubeMiniPlayerSourceTabService.ActivateAsync(
                state,
                tabs,
                dependencies.CaptureStateAsync,
                dependencies.OpenReopenedTab,
                dependencies.SelectTab,
                dependencies.ActivateSelectedTabAsync,
                dependencies.QueueSave,
                dependencies.SetStatus),
            miniPlayerId => Close(miniPlayerId, controller, dependencies),
            () => DisposeAll(controller, dependencies));
    }

    private static void BringToFront(
        Guid miniPlayerId,
        bool reopenPopup,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        if (runtimes.TryGetValue(miniPlayerId, out var runtime))
        {
            YouTubeMiniPlayerPopupService.BringRuntimeToFront(
                runtime,
                dependencies.GetNextZIndex(),
                reopenPopup,
                dependencies.UpdateToolbarPlacement,
                dependencies.UpdateTitleBandPlacement);
        }
    }

    private static void ToggleMinimized(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        SetMinimized(
            state,
            !state.IsMinimized,
            runtimes,
            dependencies);
    }

    private static void SetMinimized(
        YouTubeMiniPlayerState state,
        bool isMinimized,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        if (state.IsMinimized == isMinimized)
        {
            return;
        }

        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        YouTubeMiniPlayerMinimizeService.SetMinimized(
            runtime,
            isMinimized,
            dependencies.UpdateTitleBandPlacement,
            dependencies.SetPosition);
        dependencies.QueueSave();
        dependencies.SetStatus(YouTubeMiniPlayerMinimizeService.CreateStatus(isMinimized));
    }

    private static void Close(
        Guid miniPlayerId,
        YouTubeMiniPlayerController controller,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        if (!controller.TryClose(miniPlayerId))
        {
            return;
        }

        dependencies.QueueSave();
        dependencies.UpdateStateCaptureTimer();
        dependencies.SetStatus("YouTubeミニプレイヤーを閉じました");
    }

    private static void DisposeAll(
        YouTubeMiniPlayerController controller,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        controller.DisposeAll();
        dependencies.UpdateStateCaptureTimer();
    }
}
