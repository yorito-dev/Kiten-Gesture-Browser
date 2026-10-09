using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabBrowser.Services;

internal static class WindowTopmostService
{
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionNoOwnerZOrder = 0x0200;

    public static void Apply(Window window, bool enabled)
    {
        window.Topmost = enabled;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            enabled ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove
                | SetWindowPositionNoSize
                | SetWindowPositionNoActivate
                | SetWindowPositionNoOwnerZOrder);
    }

    public static void BringToFrontWithoutActivation(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            HwndTop,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove
                | SetWindowPositionNoSize
                | SetWindowPositionNoActivate
                | SetWindowPositionNoOwnerZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
