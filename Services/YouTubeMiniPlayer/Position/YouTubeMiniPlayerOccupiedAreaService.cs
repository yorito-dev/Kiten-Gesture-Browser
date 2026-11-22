using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TabBrowser.Services;

// 整列要求と適用直前だけ取得する。ウィンドウ名・内容は取得しない。
internal static class YouTubeMiniPlayerOccupiedAreaService
{
    public static bool TryCapture(IReadOnlySet<IntPtr> excludedHandles, DpiScale hostDpi, out IReadOnlyList<Rect> areas)
    {
        var result = new List<Rect>();
        var complete = true;
        EnumWindowProc callback = (handle, _) =>
        {
            if (excludedHandles.Contains(handle) || !IsWindowVisible(handle) || IsIconic(handle)) return true;
            var className = new StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(handle, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(handle, out bounds))
            {
                // 列挙中に閉じたWindowは無視する。存続中なら部分取得で配置しない。
                if (IsWindow(handle)) complete = false;
                return true;
            }
            if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                result.Add(YouTubeMiniPlayerArrangementAreaService.ToHostCoordinates(
                    new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), hostDpi));
            return true;
        };
        var enumerated = EnumWindows(callback, IntPtr.Zero);
        areas = result;
        return enumerated && complete;
    }

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr context);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr context);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out int value, int size);
}
