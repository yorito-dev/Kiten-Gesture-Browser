using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed class YouTubeMiniPlayerArrangementRegionOutlineWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int ToolWindowStyle = 0x00000080;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly Rect _physicalBounds;

    public YouTubeMiniPlayerArrangementRegionOutlineWindow(Rect physicalBounds, DpiScale hostDpi)
    {
        if (!YouTubeMiniPlayerArrangementAreaService.IsValid(physicalBounds)) throw new ArgumentOutOfRangeException(nameof(physicalBounds));
        _physicalBounds = physicalBounds;
        var bounds = YouTubeMiniPlayerArrangementAreaService.ToHostCoordinates(physicalBounds, hostDpi);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(103, 232, 249)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection([4, 2]),
            IsHitTestVisible = false
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var flags = YouTubeMiniPlayerPopupService.GetInputTransparentStyle(ToolWindowStyle);
        var style = GetWindowLong(handle, ExtendedWindowStyleIndex);
        _ = SetWindowLong(handle, ExtendedWindowStyleIndex, style | flags);
        if ((GetWindowLong(handle, ExtendedWindowStyleIndex) & flags) != flags)
        {
            throw new InvalidOperationException("範囲枠を入力透過にできませんでした");
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var bounds = _physicalBounds;
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, TopmostWindow, (int)Math.Round(bounds.Left),
            (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height),
            SetWindowPosNoActivate | SetWindowPosShowWindow))
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Content = null;
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
