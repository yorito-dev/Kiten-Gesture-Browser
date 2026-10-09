using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TabBrowser.Views;

internal readonly record struct ScreenGesturePointerUpdate(double DistanceFromStart);

internal sealed class ScreenGestureOverlayWindow : Window, IScreenGestureOverlayWindow
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int TransparentWindowStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateWindowStyle = 0x08000000;
    private const int VirtualScreenLeftMetric = 76;
    private const int VirtualScreenTopMetric = 77;
    private const int VirtualScreenWidthMetric = 78;
    private const int VirtualScreenHeightMetric = 79;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosShowWindow = 0x0040;
    private const double PointMinimumDistance = 1.5;
    private const double LabelGap = 14;
    private const double ScreenMargin = 8;
    private const double LabelMaximumWidth = 380;
    private const double LabelFallbackHeight = 80;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly Canvas _canvas;
    private readonly Polyline _trail;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly GestureStatusHud _statusHud;
    private readonly List<Point> _screenPoints = [];
    private HwndSource? _windowSource;
    private Point _startScreenPoint;
    private Point _currentScreenPoint;
    private Point _lastRenderedPoint;
    private bool _isTracking;
    private bool _usesVirtualScreen;
    private int _presentationGeneration;

    public ScreenGestureOverlayWindow(Window owner)
    {
        Owner = owner;
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
        Width = 1;
        Height = 1;

        _canvas = new Canvas
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        _trail = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(102, 227, 255)),
            StrokeThickness = 4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Visibility = Visibility.Collapsed
        };
        _labelText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Text = "認識中",
            TextWrapping = TextWrapping.Wrap
        };
        _label = new Border
        {
            MaxWidth = LabelMaximumWidth,
            Padding = new Thickness(10, 7, 10, 7),
            Background = new SolidColorBrush(Color.FromArgb(235, 17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _labelText,
            Visibility = Visibility.Collapsed
        };
        _statusHud = new GestureStatusHud
        {
            Width = 460,
            MaxWidth = 520,
            Visibility = Visibility.Collapsed
        };

        _canvas.Children.Add(_trail);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(_statusHud);
        Content = _canvas;
    }

    public double DistanceFromStart
    {
        get
        {
            if (!_isTracking || _windowSource is null)
            {
                return 0;
            }

            var startPoint = PointFromScreen(_startScreenPoint);
            var currentPoint = PointFromScreen(_currentScreenPoint);
            return (currentPoint - startPoint).Length;
        }
    }

    public void Begin(Point screenPoint, bool useVirtualScreen)
    {
        var generation = ++_presentationGeneration;
        Opacity = 0;
        ResetPresentation();
        _startScreenPoint = screenPoint;
        _currentScreenPoint = screenPoint;
        _screenPoints.Add(screenPoint);
        _isTracking = true;
        _usesVirtualScreen = useVirtualScreen;

        if (!IsVisible)
        {
            Show();
        }

        if (useVirtualScreen)
        {
            UpdateVirtualScreenBounds();
        }
        else
        {
            UpdateOwnerBounds();
        }

        RebuildTrail();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation == _presentationGeneration && _isTracking && IsVisible)
            {
                Opacity = 1;
            }
        }), DispatcherPriority.Render);
    }

    public ScreenGesturePointerUpdate UpdatePointer(Point screenPoint)
    {
        if (!_isTracking || _windowSource is null)
        {
            return default;
        }

        _currentScreenPoint = screenPoint;
        var point = PointFromScreen(screenPoint);
        if ((point - _lastRenderedPoint).Length >= PointMinimumDistance)
        {
            _screenPoints.Add(screenPoint);
            _trail.Points.Add(point);
            _lastRenderedPoint = point;
        }

        if (_label.Visibility == Visibility.Visible)
        {
            PositionLabel(point);
        }

        if (_statusHud.Visibility == Visibility.Visible)
        {
            PositionStatusHud(screenPoint);
        }

        return new ScreenGesturePointerUpdate(DistanceFromStart);
    }

    public void ExpandToVirtualScreen()
    {
        if (!_isTracking || _usesVirtualScreen)
        {
            return;
        }

        _usesVirtualScreen = true;
        UpdateVirtualScreenBounds();
        RebuildTrail();
        if (_label.Visibility == Visibility.Visible)
        {
            PositionLabel(PointFromScreen(_currentScreenPoint));
        }

        if (_statusHud.Visibility == Visibility.Visible)
        {
            PositionStatusHud(_currentScreenPoint);
        }
    }

    public void ShowGuide(string text)
    {
        if (!_isTracking)
        {
            return;
        }

        _trail.Visibility = Visibility.Collapsed;
        _labelText.Text = text;
        _label.Visibility = Visibility.Visible;
        _statusHud.Visibility = Visibility.Collapsed;
        PositionLabel(PointFromScreen(_currentScreenPoint));
    }

    public void ShowPresentation(
        string labelText,
        string route,
        GestureStatusHudState state,
        string primaryText,
        string? nextDirections = null)
    {
        if (!_isTracking)
        {
            return;
        }

        _trail.Visibility = Visibility.Visible;
        _labelText.Text = labelText;
        _label.Visibility = Visibility.Visible;
        _statusHud.ShowPresentation(route, state, primaryText, nextDirections);
        _statusHud.Visibility = Visibility.Visible;
        PositionLabel(PointFromScreen(_currentScreenPoint));
        PositionStatusHud(_currentScreenPoint);
    }

    public void HidePresentationBeforeActivation()
    {
        _trail.Visibility = Visibility.Collapsed;
        _label.Visibility = Visibility.Collapsed;
        _statusHud.Visibility = Visibility.Collapsed;
    }

    public void End()
    {
        _presentationGeneration++;
        _isTracking = false;
        _usesVirtualScreen = false;
        Opacity = 0;
        ResetPresentation();
        ParkClearedSurface();
    }

    private void ResetPresentation()
    {
        _screenPoints.Clear();
        _trail.Points.Clear();
        _trail.Visibility = Visibility.Collapsed;
        _labelText.Text = "認識中";
        _label.Visibility = Visibility.Collapsed;
        _statusHud.Visibility = Visibility.Collapsed;
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

    private void UpdateOwnerBounds()
    {
        var ownerHandle = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        if (ownerHandle == IntPtr.Zero || !GetWindowRect(ownerHandle, out var ownerBounds))
        {
            _usesVirtualScreen = true;
            UpdateVirtualScreenBounds();
            return;
        }

        UpdateBounds(ownerBounds);
    }

    private void ParkClearedSurface()
    {
        if (!IsVisible)
        {
            return;
        }

        var left = GetSystemMetrics(VirtualScreenLeftMetric);
        var top = GetSystemMetrics(VirtualScreenTopMetric);
        UpdateBounds(new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + 1,
            Bottom = top + 1
        });
    }

    private void UpdateVirtualScreenBounds()
    {
        var left = GetSystemMetrics(VirtualScreenLeftMetric);
        var top = GetSystemMetrics(VirtualScreenTopMetric);
        var width = Math.Max(1, GetSystemMetrics(VirtualScreenWidthMetric));
        var height = Math.Max(1, GetSystemMetrics(VirtualScreenHeightMetric));
        UpdateBounds(new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        });
    }

    private void UpdateBounds(NativeRect bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            TopmostWindow,
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            SetWindowPosNoActivate | SetWindowPosShowWindow);
    }

    private void RebuildTrail()
    {
        _trail.Points.Clear();
        var hasRenderedPoint = false;
        foreach (var screenPoint in _screenPoints)
        {
            var point = PointFromScreen(screenPoint);
            if (hasRenderedPoint && (point - _lastRenderedPoint).Length < PointMinimumDistance)
            {
                continue;
            }

            _trail.Points.Add(point);
            _lastRenderedPoint = point;
            hasRenderedPoint = true;
        }
    }

    private void PositionLabel(Point pointerPoint)
    {
        var workArea = GetPlacementArea(_currentScreenPoint);
        _label.Measure(new Size(LabelMaximumWidth, double.PositiveInfinity));
        var labelWidth = Math.Min(
            LabelMaximumWidth,
            Math.Max(_label.DesiredSize.Width, _label.ActualWidth));
        var labelHeight = Math.Max(
            LabelFallbackHeight,
            Math.Max(_label.DesiredSize.Height, _label.ActualHeight));
        var minimumLeft = workArea.Left + ScreenMargin;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - labelWidth - ScreenMargin);
        var minimumTop = workArea.Top + ScreenMargin;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - labelHeight - ScreenMargin);

        var left = pointerPoint.X + LabelGap;
        if (left > maximumLeft)
        {
            left = pointerPoint.X - labelWidth - LabelGap;
        }

        var top = pointerPoint.Y + LabelGap;
        if (top > maximumTop)
        {
            top = pointerPoint.Y - labelHeight - LabelGap;
        }

        Canvas.SetLeft(_label, Math.Clamp(left, minimumLeft, maximumLeft));
        Canvas.SetTop(_label, Math.Clamp(top, minimumTop, maximumTop));
    }

    private void PositionStatusHud(Point screenPoint)
    {
        var workArea = GetPlacementArea(screenPoint);
        var width = Math.Clamp(workArea.Width - 32, 180, 520);
        _statusHud.Width = width;
        Canvas.SetLeft(_statusHud, workArea.Left + Math.Max(8, (workArea.Width - width) / 2));
        Canvas.SetTop(_statusHud, workArea.Top + 16);
    }

    private Rect GetPlacementArea(Point screenPoint)
    {
        return _usesVirtualScreen
            ? GetMonitorWorkArea(screenPoint)
            : new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
    }

    private Rect GetMonitorWorkArea(Point screenPoint)
    {
        var nativePoint = new NativePoint
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y)
        };
        var monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        }

        var topLeft = PointFromScreen(
            new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
        var bottomRight = PointFromScreen(
            new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
        return new Rect(topLeft, bottomRight);
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect WorkArea;

        public uint Flags;
    }
}
