using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabBrowser.Services;

public static class WindowPlacementVisibilityService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionNoOwnerZOrder = 0x0200;

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => EnsureVisibleOnNearestWorkArea(window);

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            window.Loaded -= loadedHandler;
            EnsureVisibleOnNearestWorkArea(window);
        };
        window.Loaded += loadedHandler;
    }

    public static void EnsureVisibleOnNearestWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var nativeBounds))
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var bounds = nativeBounds.ToRect();
        var constrained = ConstrainToWorkArea(bounds, monitorInfo.Work.ToRect());
        if (AreClose(bounds, constrained))
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(constrained.Left),
            (int)Math.Round(constrained.Top),
            Math.Max(1, (int)Math.Round(constrained.Width)),
            Math.Max(1, (int)Math.Round(constrained.Height)),
            SetWindowPositionNoZOrder
                | SetWindowPositionNoActivate
                | SetWindowPositionNoOwnerZOrder);
    }

    internal static Rect ConstrainToWorkArea(Rect bounds, Rect workArea)
    {
        if (!IsValid(bounds) || !IsValid(workArea))
        {
            return bounds;
        }

        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        var left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new Rect(left, top, width, height);
    }

    private static bool IsValid(Rect value)
    {
        return !value.IsEmpty
            && double.IsFinite(value.Left)
            && double.IsFinite(value.Top)
            && double.IsFinite(value.Width)
            && double.IsFinite(value.Height)
            && value.Width > 0
            && value.Height > 0;
    }

    private static bool AreClose(Rect left, Rect right)
    {
        return Math.Abs(left.Left - right.Left) < 0.5
            && Math.Abs(left.Top - right.Top) < 0.5
            && Math.Abs(left.Width - right.Width) < 0.5
            && Math.Abs(left.Height - right.Height) < 0.5;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;

        public readonly Rect ToRect()
        {
            return new Rect(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect Work;

        public uint Flags;
    }
}
