using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerRuntimeWiringCallbacks(
    Action<YouTubeMiniPlayerState> ForcePopupNativePosition,
    Action<Guid, bool> BringToFront,
    Action<FrameworkElement, YouTubeMiniPlayerState> AttachDrag,
    Action<YouTubeMiniPlayerState> ToggleMinimized,
    Action<YouTubeMiniPlayerState, bool> SetMinimized,
    Action<YouTubeMiniPlayerState> ActivateSourceTab,
    Action<Guid> Close,
    Action<YouTubeMiniPlayerState, bool> SetTitleBandVisibility,
    Action<YouTubeMiniPlayerRuntime> UpdateTitleBandPlacement,
    Action<YouTubeMiniPlayerRuntime> QueueOverlayPositionRefresh,
    Func<Point> GetHostOrigin,
    Action<Button, double?> SetSpeedResetContent,
    Action<Button, bool?> SetLoopContent,
    Func<YouTubeMiniPlayerState, double, Button, Button, Task> ChangePlaybackSpeedAsync,
    Func<YouTubeMiniPlayerState, double, Button, Button, Task> SetPlaybackSpeedAsync,
    Func<YouTubeMiniPlayerState, Button, Button, Task> ToggleLoopAsync,
    Func<YouTubeMiniPlayerState, Button, Button, Task> RefreshControlDisplayAsync,
    Func<YouTubeMiniPlayerState, YouTubeMiniPlayerAbRepeatAction, Task<bool>> ExecuteAbRepeatAsync,
    Action<YouTubeMiniPlayerState, bool> SetToolbarVisibility,
    Action<YouTubeMiniPlayerRuntime> UpdateToolbarPlacement,
    Func<Point, bool> IsTitleBandHotZone,
    Func<Point, double, bool> IsToolbarHotZone,
    Action<string> SetStatus,
    Action QueueSave,
    double SpeedStep);

internal static class YouTubeMiniPlayerRuntimeWiringService
{
    private const int WmMouseActivate = 0x0021;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmDpiChanged = 0x02E0;
    private const int MaActivate = 1;

    public static void Attach(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntimeParts runtimeParts,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        var runtime = runtimeParts.Runtime;
        var chrome = runtimeParts.Chrome;
        var root = runtime.Root;
        _ = callbacks.ForcePopupNativePosition;
        root.MouseLeftButtonDown += (_, _) => callbacks.BringToFront(state.Id, false);

        AttachTitleBandHandlers(state, runtimeParts, runtimes, callbacks);
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            runtime.ToolbarPopup.IsOpen = false;
            if (runtime.LocalVideoView is { } localVideoView)
            {
                callbacks.AttachDrag(localVideoView.DragSurface, state);
            }
        }
        else
        {
            AttachToolbarHandlers(state, runtimeParts, runtimes, callbacks);
        }

        AttachChromeHandlers(state, chrome, callbacks);
        AttachMouseActivationHandler(runtime, callbacks.QueueOverlayPositionRefresh);
        AttachTaskbarWindowHandlers(state, runtime, callbacks);
    }

    private static void AttachMouseActivationHandler(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerRuntime> queueOverlayPositionRefresh)
    {
        var window = runtime.TaskbarWindow;
        HwndSource? source = null;
        HwndSourceHook hook = (
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr parameter,
            ref bool handled) =>
        {
            if (message == WmDpiChanged)
            {
                runtime.OverlayPlacementGeneration++;
                queueOverlayPositionRefresh(runtime);
                return IntPtr.Zero;
            }

            if (message != WmMouseActivate
                || !ShouldPreserveMouseDown(GetMouseMessage(parameter)))
            {
                return IntPtr.Zero;
            }

            handled = true;
            return new IntPtr(MaActivate);
        };

        void AttachHook()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            source = HwndSource.FromHwnd(handle);
            source?.AddHook(hook);
        }

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            AttachHook();
        }

        window.SourceInitialized += OnSourceInitialized;
        window.Closed += (_, _) => source?.RemoveHook(hook);
    }

    private static int GetMouseMessage(IntPtr parameter)
    {
        return (int)((parameter.ToInt64() >> 16) & 0xffff);
    }

    private static bool ShouldPreserveMouseDown(int message)
    {
        return message is WmLeftButtonDown or WmRightButtonDown;
    }

    private static void AttachTaskbarWindowHandlers(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        runtime.TaskbarWindow.Activated += (_, _) =>
        {
            if (runtime.IsTaskbarActivationInProgress)
            {
                return;
            }

            if (state.IsMinimized)
            {
                callbacks.SetMinimized(state, false);
            }

            callbacks.BringToFront(state.Id, true);
        };

        runtime.TaskbarWindow.LocationChanged += (_, _) =>
        {
            runtime.OverlayPlacementGeneration++;
            SyncStateFromWindow(state, runtime, callbacks);
            callbacks.QueueOverlayPositionRefresh(runtime);
        };

        runtime.TaskbarWindow.SizeChanged += (_, _) =>
        {
            runtime.OverlayPlacementGeneration++;
            SyncStateFromWindow(state, runtime, callbacks);
            callbacks.QueueOverlayPositionRefresh(runtime);
        };

        runtime.TaskbarWindow.StateChanged += (_, _) =>
        {
            if (runtime.IsFullScreen
                && runtime.TaskbarWindow.WindowState == WindowState.Normal)
            {
                runtime.Root.Width = double.NaN;
                runtime.Root.Height = double.NaN;
                runtime.TaskbarWindow.WindowState = WindowState.Maximized;
                return;
            }

            var isMinimized = runtime.TaskbarWindow.WindowState == WindowState.Minimized;
            if (state.IsMinimized != isMinimized)
            {
                callbacks.SetMinimized(state, isMinimized);
            }
        };

        runtime.TaskbarWindow.Closing += (_, e) =>
        {
            if (runtime.IsClosingTaskbarWindow)
            {
                return;
            }

            e.Cancel = true;
            if (runtime.IsTaskbarCloseRequested)
            {
                return;
            }

            runtime.IsTaskbarCloseRequested = true;
            runtime.TaskbarWindow.Dispatcher.BeginInvoke((Action)(() =>
            {
                callbacks.Close(state.Id);
                if (!runtime.IsClosingTaskbarWindow)
                {
                    runtime.IsTaskbarCloseRequested = false;
                }
            }));
        };
    }

    private static void SyncStateFromWindow(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        if (!runtime.TaskbarWindow.IsVisible
            || runtime.TaskbarWindow.WindowState == WindowState.Minimized
            || runtime.IsFullScreen
            || runtime.IsResizing)
        {
            return;
        }

        var hostOrigin = callbacks.GetHostOrigin();
        var changed = false;
        var left = runtime.TaskbarWindow.Left - hostOrigin.X;
        var top = runtime.TaskbarWindow.Top - hostOrigin.Y;
        if (double.IsFinite(left) && Math.Abs(state.Left - left) >= 0.5)
        {
            state.Left = left;
            changed = true;
        }

        if (double.IsFinite(top) && Math.Abs(state.Top - top) >= 0.5)
        {
            state.Top = top;
            changed = true;
        }

        var width = runtime.TaskbarWindow.Width;
        var height = runtime.TaskbarWindow.Height;
        if (double.IsFinite(width) && width >= YouTubeMiniPlayerLayoutService.MinimumWidth && Math.Abs(state.Width - width) >= 0.5)
        {
            state.Width = width;
            runtime.Root.Width = width;
            changed = true;
        }

        if (double.IsFinite(height) && height >= YouTubeMiniPlayerLayoutService.MinimumHeight && Math.Abs(state.Height - height) >= 0.5)
        {
            state.Height = height;
            runtime.Root.Height = height;
            changed = true;
        }

        if (changed)
        {
            state.UpdatedAt = DateTimeOffset.UtcNow;
            if (runtime.TitleBandPopup.IsOpen)
            {
                callbacks.UpdateTitleBandPlacement(runtime);
            }

            if (runtime.ToolbarPopup.IsOpen)
            {
                callbacks.UpdateToolbarPlacement(runtime);
            }

            if (!runtime.IsWebDragging && !runtime.IsSurfaceDragging && !runtime.IsResizing)
            {
                callbacks.QueueSave();
            }
        }
    }

    private static void AttachTitleBandHandlers(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntimeParts runtimeParts,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        var runtime = runtimeParts.Runtime;
        var titleBandParts = runtimeParts.TitleBandParts;
        var titleBand = titleBandParts.Root;
        YouTubeMiniPlayerChromeFactory.UpdateTopmostButton(
            titleBandParts.TopmostButton,
            state.IsTopmost);
        titleBandParts.TopmostButton.Click += (_, _) => ToggleTopmost(
            state,
            runtime,
            callbacks);
        titleBandParts.MinimizeButton.Click += (_, _) => callbacks.ToggleMinimized(state);
        titleBandParts.SourceButton.Click += (_, _) => callbacks.ActivateSourceTab(state);
        titleBandParts.CloseButton.Click += (_, _) => callbacks.Close(state.Id);
        callbacks.AttachDrag(titleBand, state);
        titleBand.MouseEnter += (_, _) => callbacks.SetTitleBandVisibility(state, true);
        titleBand.MouseMove += (_, _) => callbacks.SetTitleBandVisibility(state, true);
        titleBand.MouseLeave += (_, _) => callbacks.SetTitleBandVisibility(state, false);
        runtime.TitleBandPopup.Opened += (_, _) =>
        {
            if (runtimes.TryGetValue(state.Id, out var current))
            {
                callbacks.UpdateTitleBandPlacement(current);
            }
        };
    }

    private static void ToggleTopmost(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        SetTopmost(
            state,
            runtime,
            !state.IsTopmost,
            callbacks.UpdateTitleBandPlacement,
            callbacks.UpdateToolbarPlacement,
            callbacks.QueueSave,
            callbacks.SetStatus);
    }

    internal static void SetTopmost(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        bool isTopmost,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action queueSave,
        Action<string> setStatus)
    {
        if (state.IsTopmost == isTopmost)
        {
            return;
        }

        state.IsTopmost = isTopmost;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        runtime.TaskbarWindow.Topmost = state.IsTopmost;
        YouTubeMiniPlayerChromeFactory.UpdateTopmostButton(runtime.TitleBandTopmostButton, state.IsTopmost);
        if (runtime.TitleBandPopup.IsOpen)
        {
            updateTitleBandPlacement(runtime);
        }

        if (runtime.ToolbarPopup.IsOpen)
        {
            updateToolbarPlacement(runtime);
        }

        queueSave();
        setStatus(state.IsTopmost
            ? "YouTubeミニプレイヤーを常に最前面にしました"
            : "YouTubeミニプレイヤーの常に最前面を解除しました");
    }

    private static void AttachToolbarHandlers(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntimeParts runtimeParts,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        var runtime = runtimeParts.Runtime;
        var toolbarParts = runtimeParts.ToolbarParts;
        var toolbar = toolbarParts.Root;
        YouTubeMiniPlayerChromeFactory.UpdateContentZoomButton(
            toolbarParts.ZoomResetButton,
            state.ContentZoomFactor);
        callbacks.SetSpeedResetContent(toolbarParts.SpeedResetButton, state.PlaybackRate);
        callbacks.SetLoopContent(toolbarParts.LoopButton, state.IsLooping);
        YouTubeMiniPlayerChromeFactory.UpdateAbRepeatControls(toolbarParts, state);
        toolbarParts.ZoomResetButton.Click += async (_, _) =>
        {
            var changed = YouTubeMiniPlayerContentTransformState.ToggleOriginalSize(state);
            YouTubeMiniPlayerChromeFactory.UpdateContentZoomButton(
                toolbarParts.ZoomResetButton,
                state.ContentZoomFactor);
            await YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
                state,
                runtimes,
                YouTubeControlScriptService.BuildSetMiniPlayerContentTransformScript(
                    runtime.WebMessageToken,
                    state.ContentZoomFactor,
                    state.ContentPanX,
                    state.ContentPanY),
                showStatus: false,
                callbacks.SetStatus);
            if (changed)
            {
                state.UpdatedAt = DateTimeOffset.UtcNow;
                callbacks.QueueSave();
            }
        };
        toolbarParts.SpeedDownButton.Click += async (_, _) => await callbacks.ChangePlaybackSpeedAsync(state, -callbacks.SpeedStep, toolbarParts.SpeedResetButton, toolbarParts.LoopButton);
        toolbarParts.SpeedResetButton.Click += async (_, _) =>
        {
            var targetSpeed = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(
                state.PlaybackRate,
                state.PreviousPlaybackRate,
                out var previousSpeed);
            state.PreviousPlaybackRate = previousSpeed;
            await callbacks.SetPlaybackSpeedAsync(state, targetSpeed, toolbarParts.SpeedResetButton, toolbarParts.LoopButton);
        };
        toolbarParts.SpeedUpButton.Click += async (_, _) => await callbacks.ChangePlaybackSpeedAsync(state, callbacks.SpeedStep, toolbarParts.SpeedResetButton, toolbarParts.LoopButton);
        toolbarParts.LoopButton.Click += async (_, _) =>
        {
            await callbacks.ToggleLoopAsync(state, toolbarParts.SpeedResetButton, toolbarParts.LoopButton);
            YouTubeMiniPlayerChromeFactory.UpdateAbRepeatControls(toolbarParts, state);
        };
        toolbarParts.AbRepeatButton.Click += (_, e) =>
        {
            e.Handled = true;
            if (toolbarParts.AbRepeatButton.ContextMenu is { } menu)
            {
                menu.IsOpen = !menu.IsOpen;
            }
        };
        toolbarParts.AbRepeatButton.ContextMenu!.Opened += (_, _) =>
        {
            runtime.IsToolbarMenuOpen = true;
            YouTubeMiniPlayerChromeFactory.UpdateAbRepeatControls(toolbarParts, state);
            callbacks.SetToolbarVisibility(state, true);
        };
        toolbarParts.AbRepeatButton.ContextMenu.Closed += (_, _) =>
        {
            runtime.IsToolbarMenuOpen = false;
            callbacks.SetToolbarVisibility(state, false);
        };
        toolbarParts.AbRepeatSetStartMenuItem.Click += async (_, _) =>
            await ExecuteAbRepeatActionAsync(YouTubeMiniPlayerAbRepeatAction.SetStart);
        toolbarParts.AbRepeatSetEndMenuItem.Click += async (_, _) =>
            await ExecuteAbRepeatActionAsync(YouTubeMiniPlayerAbRepeatAction.SetEnd);
        toolbarParts.AbRepeatToggleMenuItem.Click += async (_, _) =>
            await ExecuteAbRepeatActionAsync(YouTubeMiniPlayerAbRepeatAction.Toggle);
        toolbarParts.AbRepeatClearMenuItem.Click += async (_, _) =>
            await ExecuteAbRepeatActionAsync(YouTubeMiniPlayerAbRepeatAction.Clear);
        toolbar.MouseEnter += (_, _) =>
        {
            YouTubeMiniPlayerChromeFactory.UpdateContentZoomButton(
                toolbarParts.ZoomResetButton,
                state.ContentZoomFactor);
            YouTubeMiniPlayerChromeFactory.UpdateAbRepeatControls(toolbarParts, state);
            _ = callbacks.RefreshControlDisplayAsync(state, toolbarParts.SpeedResetButton, toolbarParts.LoopButton);
        };
        callbacks.AttachDrag(toolbar, state);
        toolbar.MouseEnter += (_, _) => callbacks.SetToolbarVisibility(state, true);
        toolbar.MouseMove += (_, _) => callbacks.SetToolbarVisibility(state, true);
        toolbar.MouseLeave += (_, _) => callbacks.SetToolbarVisibility(state, false);
        runtime.ToolbarPopup.Opened += (_, _) =>
        {
            if (runtimes.TryGetValue(state.Id, out var current))
            {
                callbacks.UpdateToolbarPlacement(current);
            }
        };

        async Task ExecuteAbRepeatActionAsync(YouTubeMiniPlayerAbRepeatAction action)
        {
            await callbacks.ExecuteAbRepeatAsync(state, action);
            callbacks.SetLoopContent(toolbarParts.LoopButton, state.IsLooping);
            YouTubeMiniPlayerChromeFactory.UpdateAbRepeatControls(toolbarParts, state);
        }
    }

    private static void AttachChromeHandlers(
        YouTubeMiniPlayerState state,
        Grid chrome,
        YouTubeMiniPlayerRuntimeWiringCallbacks callbacks)
    {
        callbacks.AttachDrag(chrome, state);
        chrome.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(chrome);
            callbacks.SetToolbarVisibility(
                state,
                callbacks.IsToolbarHotZone(point, chrome.ActualHeight));
            callbacks.SetTitleBandVisibility(
                state,
                callbacks.IsTitleBandHotZone(point));
        };
        chrome.MouseLeave += (_, _) =>
        {
            callbacks.SetToolbarVisibility(state, false);
            callbacks.SetTitleBandVisibility(state, false);
        };
    }
}
