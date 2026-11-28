using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerRuntimeWiringTests
{
    [Fact]
    public void CreateKeepsTitleBandAndToolbarVisibilityAndPlacementSeparate()
    {
        Action<YouTubeMiniPlayerState, bool> titleBand = (_, _) => { };
        Action<YouTubeMiniPlayerState, bool> toolbar = (_, _) => { };
        Action<YouTubeMiniPlayerRuntime> titleBandPlacement = _ => { };
        Action<YouTubeMiniPlayerRuntime> toolbarPlacement = _ => { };
        var callbacks = CreateWiring(
            new Dictionary<Guid, YouTubeMiniPlayerRuntime>(),
            visibility: new YouTubeMiniPlayerOverlayVisibilityCallbacks(
                SetToolbarVisibility: toolbar,
                KeepToolbarVisible: _ => { },
                SetTitleBandVisibility: titleBand),
            placement: new YouTubeMiniPlayerOverlayPlacementCallbacks(
                UpdateTitleBandPlacement: titleBandPlacement,
                UpdateToolbarPlacement: toolbarPlacement,
                UpdateToolbarPlacementAsync: _ => Task.CompletedTask,
                RefreshOverlayPopupPosition: _ => { }));

        Assert.Same(titleBand, callbacks.SetTitleBandVisibility);
        Assert.Same(toolbar, callbacks.SetToolbarVisibility);
        Assert.Same(titleBandPlacement, callbacks.UpdateTitleBandPlacement);
        Assert.Same(toolbarPlacement, callbacks.UpdateToolbarPlacement);
    }

    internal static YouTubeMiniPlayerRuntimeWiringCallbacks CreateWiring(
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action<Guid>? close = null,
        Func<Point>? getHostOrigin = null,
        Action? queueSave = null,
        YouTubeMiniPlayerOverlayVisibilityCallbacks? visibility = null,
        YouTubeMiniPlayerOverlayPlacementCallbacks? placement = null)
    {
        return YouTubeMiniPlayerRuntimeWiringCallbackFactory.Create(
            runtimes,
            new YouTubeMiniPlayerPositionCallbacks(
                ClampToHost: (_, _, _) => { },
                SetPosition: (_, _) => { },
                ForceRuntimeNativePosition: _ => { },
                SyncPopupOffsets: _ => { },
                QueueNativePositionRefresh: _ => { },
                GetHostOrigin: getHostOrigin ?? (() => new Point()),
                GetPopupScreenPosition: _ => new Point()),
            new YouTubeMiniPlayerCommandCallbacks(
                BringToFront: (_, _) => { },
                ToggleMinimized: _ => { },
                SetMinimized: (_, _) => { },
                ApplyMinimizedState: (_, _) => { },
                ActivateSourceTab: _ => { },
                Close: close ?? (_ => { }),
                DisposeAll: () => { }),
            visibility ?? new YouTubeMiniPlayerOverlayVisibilityCallbacks(
                SetToolbarVisibility: (_, _) => { },
                KeepToolbarVisible: _ => { },
                SetTitleBandVisibility: (_, _) => { }),
            placement ?? new YouTubeMiniPlayerOverlayPlacementCallbacks(
                UpdateTitleBandPlacement: _ => { },
                UpdateToolbarPlacement: _ => { },
                UpdateToolbarPlacementAsync: _ => Task.CompletedTask,
                RefreshOverlayPopupPosition: _ => { }),
            new YouTubeMiniPlayerRuntimePlaybackControls
            {
                SetSpeedResetContent = (_, _) => { },
                SetLoopContent = (_, _) => { },
                SpeedStep = 0.25
            },
            attachDrag: (_, _) => { },
            setStatus: _ => { },
            queueSave: queueSave ?? (() => { }));
    }
}
