using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TabBrowser.Services;

public static class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

        var borderColor = ToColorRef(42, 47, 58);
        var captionColor = ToColorRef(16, 17, 20);
        var textColor = ToColorRef(243, 246, 250);
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
