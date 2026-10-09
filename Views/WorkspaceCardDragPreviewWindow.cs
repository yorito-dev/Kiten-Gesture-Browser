using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TabBrowser.ViewModels;

namespace TabBrowser.Views;

internal sealed record WorkspaceCardDragPreviewPart(
    WorkspaceItemViewModel Item,
    double X,
    double Y,
    double Width,
    double Height);

internal sealed class WorkspaceCardDragPreviewWindow : Window
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int TransparentWindowStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateWindowStyle = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly double _grabOffsetX;
    private readonly double _grabOffsetY;
    private HwndSource? _windowSource;

    public WorkspaceCardDragPreviewWindow(
        IReadOnlyList<WorkspaceCardDragPreviewPart> parts,
        DataTemplate itemTemplate,
        double grabOffsetX,
        double grabOffsetY,
        double sourceScale)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(itemTemplate);
        if (parts.Count == 0)
        {
            throw new ArgumentException("ドラッグプレビューには1件以上のカードが必要です。", nameof(parts));
        }

        var normalizedScale = sourceScale > 0 ? sourceScale : 1.0;
        _grabOffsetX = grabOffsetX * normalizedScale;
        _grabOffsetY = grabOffsetY * normalizedScale;

        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.Manual;

        var canvas = new Canvas
        {
            Width = parts.Max(part => (part.X + part.Width) * normalizedScale),
            Height = parts.Max(part => (part.Y + part.Height) * normalizedScale),
            IsHitTestVisible = false
        };
        foreach (var part in parts)
        {
            var preview = new ContentPresenter
            {
                Content = part.Item,
                ContentTemplate = itemTemplate,
                Width = part.Width,
                Height = part.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Focusable = false,
                RenderTransform = new ScaleTransform(normalizedScale, normalizedScale),
                RenderTransformOrigin = new Point(0, 0)
            };
            Canvas.SetLeft(preview, part.X * normalizedScale);
            Canvas.SetTop(preview, part.Y * normalizedScale);
            canvas.Children.Add(preview);
        }

        Width = Math.Max(1, canvas.Width);
        Height = Math.Max(1, canvas.Height);
        Content = canvas;
    }

    public void ShowWithoutActivation()
    {
        UpdatePosition();
        Show();
        UpdatePosition();
    }

    public void UpdatePosition()
    {
        if (!GetCursorPos(out var cursorPoint))
        {
            return;
        }

        var dpiScale = GetDpiScale(cursorPoint);
        var left = cursorPoint.X - (int)Math.Round(_grabOffsetX * dpiScale);
        var top = cursorPoint.Y - (int)Math.Round(_grabOffsetY * dpiScale);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            Left = left / dpiScale;
            Top = top / dpiScale;
            return;
        }

        _ = SetWindowPos(
            handle,
            TopmostWindow,
            left,
            top,
            0,
            0,
            SetWindowPosNoSize | SetWindowPosNoActivate | SetWindowPosShowWindow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLong(handle, ExtendedWindowStyleIndex);
        _ = SetWindowLong(
            handle,
            ExtendedWindowStyleIndex,
            styles | TransparentWindowStyle | ToolWindowStyle | NoActivateWindowStyle);
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
        Content = null;
        base.OnClosed(e);
    }

    private static IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private static double GetDpiScale(NativePoint point)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newValue);

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
    private struct NativePoint
    {
        public int X;

        public int Y;
    }
}
