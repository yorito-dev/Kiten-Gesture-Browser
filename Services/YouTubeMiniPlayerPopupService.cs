using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerPopupService
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    public static void SetRelativePosition(
        Popup popup,
        UIElement placementTarget,
        Visual popupVisual,
        Point targetOffset)
    {
        popup.PlacementTarget = placementTarget;
        popup.Placement = PlacementMode.Relative;
        popup.HorizontalOffset = targetOffset.X;
        popup.VerticalOffset = targetOffset.Y;
        ForceNativePosition(popup, placementTarget, popupVisual, targetOffset);
    }

    public static void ForceNativePosition(
        Popup popup,
        UIElement placementTarget,
        Visual popupVisual,
        Point targetOffset)
    {
        if (!popup.IsOpen
            || PresentationSource.FromVisual(popupVisual) is not HwndSource source
            || source.Handle == IntPtr.Zero
            || PresentationSource.FromVisual(placementTarget) is not HwndSource ownerSource
            || ownerSource.CompositionTarget is null)
        {
            return;
        }

        // 絶対画面座標は、移動前の倍率を保持し得るPopupではなく本体WindowのDPIで求める。
        var devicePosition = GetDeviceScreenPosition(
            placementTarget.PointToScreen(new Point(0, 0)),
            targetOffset,
            ownerSource.CompositionTarget.TransformToDevice);
        SetWindowPos(
            source.Handle,
            IntPtr.Zero,
            (int)Math.Round(devicePosition.X),
            (int)Math.Round(devicePosition.Y),
            0,
            0,
            SetWindowPositionNoSize | SetWindowPositionNoZOrder | SetWindowPositionNoActivate);
    }

    internal static Point GetDeviceScreenPosition(
        Point ownerDeviceOrigin,
        Point logicalOffset,
        Matrix ownerToDevice)
    {
        var deviceOffset = ownerToDevice.Transform(new Vector(logicalOffset.X, logicalOffset.Y));
        return ownerDeviceOrigin + deviceOffset;
    }

    public static void BringToFront(Popup popup, Visual visual)
    {
        if (!popup.IsOpen
            || PresentationSource.FromVisual(visual) is not HwndSource source
            || source.Handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            source.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove | SetWindowPositionNoSize | SetWindowPositionNoActivate);
    }

    public static void SetTopmost(Popup popup, Visual visual, bool isTopmost)
    {
        if (!popup.IsOpen
            || PresentationSource.FromVisual(visual) is not HwndSource source
            || source.Handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            source.Handle,
            isTopmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove | SetWindowPositionNoSize | SetWindowPositionNoActivate);
    }

    internal static void SetRuntimeScreenPosition(
        YouTubeMiniPlayerRuntime runtime,
        Point screenPosition,
        Action<YouTubeMiniPlayerRuntime> queueNativePositionRefresh,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement,
        Action<YouTubeMiniPlayerRuntime> refreshOverlayPosition)
    {
        if (runtime.IsFullScreen)
        {
            refreshOverlayPosition(runtime);
            return;
        }

        YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
        SyncTaskbarWindowPosition(runtime, screenPosition);
        queueNativePositionRefresh(runtime);
        if (runtime.ToolbarPopup.IsOpen)
        {
            updateToolbarPlacement(runtime);
        }

        if (runtime.TitleBandPopup.IsOpen)
        {
            updateTitleBandPlacement(runtime);
        }

        refreshOverlayPosition(runtime);
    }

    internal static void ForceRuntimeNativePosition(YouTubeMiniPlayerRuntime runtime, Point screenPosition)
    {
        if (runtime.IsFullScreen)
        {
            return;
        }

        runtime.TaskbarWindow.Left = screenPosition.X;
        runtime.TaskbarWindow.Top = screenPosition.Y;
    }

    internal static void SyncRuntimeOffsets(
        YouTubeMiniPlayerRuntime runtime,
        Point screenPosition,
        Action<YouTubeMiniPlayerRuntime> queueNativePositionRefresh,
        Action<YouTubeMiniPlayerRuntime> refreshHover)
    {
        _ = screenPosition;
        if (runtime.IsFullScreen)
        {
            refreshHover(runtime);
            return;
        }

        queueNativePositionRefresh(runtime);
        refreshHover(runtime);
    }

    internal static void BringRuntimeToFront(
        YouTubeMiniPlayerRuntime runtime,
        int zIndex,
        bool reopenPopup,
        Action<YouTubeMiniPlayerRuntime> updateToolbarPlacement,
        Action<YouTubeMiniPlayerRuntime> updateTitleBandPlacement)
    {
        if (runtime.State.IsMinimized)
        {
            return;
        }

        var wasToolbarOpen = reopenPopup && runtime.ToolbarPopup.IsOpen;
        var wasTitleBandOpen = reopenPopup && runtime.TitleBandPopup.IsOpen;
        if (wasTitleBandOpen)
        {
            runtime.TitleBandPopup.IsOpen = false;
        }

        if (wasToolbarOpen)
        {
            runtime.ToolbarPopup.IsOpen = false;
        }

        Panel.SetZIndex(runtime.Root, zIndex);
        if (!runtime.TaskbarWindow.IsVisible)
        {
            runtime.TaskbarWindow.Show();
        }

        if (runtime.TaskbarWindow.WindowState == WindowState.Minimized)
        {
            runtime.TaskbarWindow.WindowState = WindowState.Normal;
        }

        ActivateRuntimeWindow(runtime);

        if (wasToolbarOpen)
        {
            updateToolbarPlacement(runtime);
            runtime.ToolbarPopup.IsOpen = true;
        }

        if (wasTitleBandOpen)
        {
            updateTitleBandPlacement(runtime);
            runtime.TitleBandPopup.IsOpen = true;
        }

        if (!reopenPopup && runtime.ToolbarPopup.IsOpen)
        {
            updateToolbarPlacement(runtime);
        }

        if (!reopenPopup && runtime.TitleBandPopup.IsOpen)
        {
            updateTitleBandPlacement(runtime);
        }
    }

    internal static void ActivateRuntimeWindow(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.TaskbarWindow.IsActive)
        {
            return;
        }

        runtime.IsTaskbarActivationInProgress = true;
        try
        {
            runtime.TaskbarWindow.Activate();
        }
        finally
        {
            runtime.TaskbarWindow.Dispatcher.BeginInvoke((Action)(() =>
            {
                runtime.IsTaskbarActivationInProgress = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static void SyncTaskbarWindowPosition(YouTubeMiniPlayerRuntime runtime, Point screenPosition)
    {
        if (runtime.IsFullScreen)
        {
            return;
        }

        var width = Math.Max(YouTubeMiniPlayerLayoutService.MinimumWidth, runtime.State.Width);
        var height = Math.Max(YouTubeMiniPlayerLayoutService.MinimumHeight, runtime.State.Height);
        runtime.TaskbarWindow.Width = width;
        runtime.TaskbarWindow.Height = height;
        runtime.TaskbarWindow.Left = screenPosition.X;
        runtime.TaskbarWindow.Top = screenPosition.Y;
        runtime.Root.Width = width;
        runtime.Root.Height = height;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
