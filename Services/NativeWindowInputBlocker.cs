using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabBrowser.Services;

internal sealed class NativeWindowInputBlocker : IDisposable
{
    private readonly List<IntPtr> _blockedWindowHandles;
    private int _disposed;

    private NativeWindowInputBlocker(List<IntPtr> blockedWindowHandles)
    {
        _blockedWindowHandles = blockedWindowHandles;
    }

    public static NativeWindowInputBlocker Block(IEnumerable<Window> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var blockedWindowHandles = new List<IntPtr>();
        foreach (var window in windows.Distinct())
        {
            if (!window.IsEnabled)
            {
                continue;
            }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !IsWindowEnabled(handle))
            {
                continue;
            }

            _ = EnableWindow(handle, false);
            if (!IsWindowEnabled(handle))
            {
                blockedWindowHandles.Add(handle);
            }
        }

        return new NativeWindowInputBlocker(blockedWindowHandles);
    }

    internal static bool IsInputEnabled(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && IsWindowEnabled(handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var handle in _blockedWindowHandles)
        {
            if (IsWindow(handle))
            {
                _ = EnableWindow(handle, true);
            }
        }

        _blockedWindowHandles.Clear();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr windowHandle, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);
}
