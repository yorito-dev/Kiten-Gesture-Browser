using System.Runtime.InteropServices;
using System.Windows;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerArrangementDisplay(string DeviceName, string DisplayName, Rect WorkArea);

// 画面一覧は操作時だけ取得する。選択はWindow内のUIが保持し、設定ファイルへ保存しない。
internal static class YouTubeMiniPlayerArrangementDisplayService
{
    public static readonly YouTubeMiniPlayerArrangementDisplay CurrentDisplay =
        new("", "現在のディスプレイ（ブラウザー）", Rect.Empty);

    public static IReadOnlyList<YouTubeMiniPlayerArrangementDisplay> GetDisplays()
    {
        var displays = new List<YouTubeMiniPlayerArrangementDisplay>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr context, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                var name = info.DeviceName.Replace(@"\\.\DISPLAY", "ディスプレイ ", StringComparison.OrdinalIgnoreCase);
                var primary = (info.Flags & 1) != 0 ? "・メイン" : "";
                var label = $"{name}（{info.Bounds.Right - info.Bounds.Left}×{info.Bounds.Bottom - info.Bounds.Top}{primary}）";
                displays.Add(new(info.DeviceName, label, new Rect(info.Work.Left, info.Work.Top,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top)));
            }
            return true;
        };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return displays.OrderBy(static display => display.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Rect ResolveWorkArea(IReadOnlyList<YouTubeMiniPlayerArrangementDisplay> displays,
        string? selectedDeviceName, Rect currentWorkArea, DpiScale hostDpi)
    {
        var selected = string.IsNullOrEmpty(selectedDeviceName) ? null : displays.FirstOrDefault(display =>
            string.Equals(display.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return currentWorkArea;

        // 既存の配置計画・Window座標と同じ、hostのDPIで換算した画面座標を渡す。
        var area = selected.WorkArea;
        return new(area.Left / hostDpi.DpiScaleX, area.Top / hostDpi.DpiScaleY,
            area.Width / hostDpi.DpiScaleX, area.Height / hostDpi.DpiScaleY);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr context, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr context, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Bounds;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
}
