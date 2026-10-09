using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace TabBrowser.Services;

internal enum PopupWindowOrderPolicy
{
    Normal,
    Topmost
}

internal static class PopupWindowOrderService
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const int GetWindowExtendedStyle = -20;
    private const int WindowExtendedStyleTopmost = 0x00000008;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint GetWindowPrevious = 3;

    public static bool Apply(Popup popup, Visual visual, PopupWindowOrderPolicy policy)
    {
        if (!popup.IsOpen
            || PresentationSource.FromVisual(visual) is not HwndSource source
            || source.Handle == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowPos(
            source.Handle,
            policy == PopupWindowOrderPolicy.Topmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove
                | SetWindowPositionNoSize
                | SetWindowPositionNoActivate);
    }

    public static bool ApplyRelativeToOwner(
        Popup popup,
        Visual popupVisual,
        Visual ownerVisual)
    {
        if (!TryGetRelativeHandles(
                popup,
                popupVisual,
                ownerVisual,
                out var popupHandle,
                out var ownerHandle))
        {
            return false;
        }

        return ApplyRelativeToOwner(
            popupHandle,
            ownerHandle,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove
                | SetWindowPositionNoSize
                | SetWindowPositionNoActivate);
    }

    public static bool ApplyRelativeBounds(
        Popup popup,
        Visual popupVisual,
        Visual ownerVisual,
        Visual placementTarget,
        Rect bounds)
    {
        if (!TryGetRelativeHandles(
                popup,
                popupVisual,
                ownerVisual,
                out var popupHandle,
                out var ownerHandle)
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return false;
        }

        try
        {
            var screenOrigin = placementTarget.PointToScreen(new Point(bounds.X, bounds.Y));
            var dpi = VisualTreeHelper.GetDpi(placementTarget);
            return ApplyRelativeToOwner(
                popupHandle,
                ownerHandle,
                (int)Math.Round(screenOrigin.X),
                (int)Math.Round(screenOrigin.Y),
                Math.Max(1, (int)Math.Round(bounds.Width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Round(bounds.Height * dpi.DpiScaleY)),
                SetWindowPositionNoActivate);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetRelativeHandles(
        Popup popup,
        Visual popupVisual,
        Visual ownerVisual,
        out IntPtr popupHandle,
        out IntPtr ownerHandle)
    {
        popupHandle = IntPtr.Zero;
        ownerHandle = IntPtr.Zero;
        if (!popup.IsOpen
            || PresentationSource.FromVisual(popupVisual) is not HwndSource popupSource
            || popupSource.Handle == IntPtr.Zero
            || PresentationSource.FromVisual(ownerVisual) is not HwndSource ownerSource
            || ownerSource.Handle == IntPtr.Zero
            || popupSource.Handle == ownerSource.Handle)
        {
            return false;
        }

        popupHandle = popupSource.Handle;
        ownerHandle = ownerSource.Handle;
        return true;
    }

    private static bool ApplyRelativeToOwner(
        IntPtr popupHandle,
        IntPtr ownerHandle,
        int x,
        int y,
        int width,
        int height,
        uint flags)
    {
        var ownerIsTopmost = IsTopmost(ownerHandle);
        if (ownerIsTopmost != IsTopmost(popupHandle)
            && !SetWindowPos(
                popupHandle,
                ownerIsTopmost ? HwndTopmost : HwndNotTopmost,
                0,
                0,
                0,
                0,
                SetWindowPositionNoMove
                    | SetWindowPositionNoSize
                    | SetWindowPositionNoActivate))
        {
            return false;
        }

        var insertAfter = GetWindow(ownerHandle, GetWindowPrevious);
        if (insertAfter == popupHandle)
        {
            if ((flags & (SetWindowPositionNoMove | SetWindowPositionNoSize))
                == (SetWindowPositionNoMove | SetWindowPositionNoSize))
            {
                return true;
            }

            insertAfter = IntPtr.Zero;
            flags |= SetWindowPositionNoZOrder;
        }

        return SetWindowPos(
            popupHandle,
            insertAfter,
            x,
            y,
            width,
            height,
            flags);
    }

    private static bool IsTopmost(IntPtr handle)
    {
        return (GetWindowLong(handle, GetWindowExtendedStyle) & WindowExtendedStyleTopmost) != 0;
    }

    public static bool ApplyBounds(
        Popup popup,
        Visual popupVisual,
        Visual placementTarget,
        Rect bounds,
        PopupWindowOrderPolicy policy)
    {
        if (!popup.IsOpen
            || PresentationSource.FromVisual(popupVisual) is not HwndSource source
            || source.Handle == IntPtr.Zero
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return false;
        }

        try
        {
            var screenOrigin = placementTarget.PointToScreen(new Point(bounds.X, bounds.Y));
            var dpi = VisualTreeHelper.GetDpi(placementTarget);
            return SetWindowPos(
                source.Handle,
                policy == PopupWindowOrderPolicy.Topmost ? HwndTopmost : HwndNotTopmost,
                (int)Math.Round(screenOrigin.X),
                (int)Math.Round(screenOrigin.Y),
                Math.Max(1, (int)Math.Round(bounds.Width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Round(bounds.Height * dpi.DpiScaleY)),
                SetWindowPositionNoActivate);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
