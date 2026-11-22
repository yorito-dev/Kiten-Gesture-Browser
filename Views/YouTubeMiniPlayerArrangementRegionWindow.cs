using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TabBrowser.Services;

namespace TabBrowser.Views;

// 一回の範囲選択だけを所有する。座標は物理pixelで保持し、描画時にだけDIPへ変換する。
internal sealed class YouTubeMiniPlayerArrangementRegionWindow : Window
{
    private static readonly TimeSpan SizeLabelUpdateInterval = TimeSpan.FromMilliseconds(100);

    private readonly Rect _physicalWorkArea;
    private readonly Canvas _canvas;
    private readonly DragOutline _outline;
    private readonly Border _instructions;
    private readonly Rect _instructionsArea;
    private readonly TextBlock _label;
    private Point? _start;
    private Rect? _drawnBounds;
    private Size? _shownSize;
    private TimeSpan _lastSizeLabelUpdate;
    private bool _dragFramePending;
    private bool _dragRenderingAttached;
    private bool _finishing;
    private bool _presented;

    public Rect? SelectedPhysicalBounds { get; private set; }
    public bool ReturnToMenu { get; private set; }

    public YouTubeMiniPlayerArrangementRegionWindow(Rect physicalWorkArea, DpiScale hostDpi, Rect? instructionsArea = null)
    {
        if (!YouTubeMiniPlayerArrangementAreaService.IsValid(physicalWorkArea)) throw new ArgumentOutOfRangeException(nameof(physicalWorkArea));
        _physicalWorkArea = physicalWorkArea;
        _instructionsArea = instructionsArea ?? physicalWorkArea;
        var initial = YouTubeMiniPlayerArrangementAreaService.ToHostCoordinates(physicalWorkArea, hostDpi);
        Left = initial.Left;
        Top = initial.Top;
        Width = initial.Width;
        Height = initial.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(50, 11, 14, 20));
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _outline = new DragOutline { IsHitTestVisible = false, ClipToBounds = true };
        _label = new TextBlock
        {
            Text = "ドラッグして配置範囲を囲む（ディスプレイをまたげます）  ／  Esc・右クリックで中止",
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(12, 8, 12, 8)
        };
        _instructions = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 64, 77)),
            BorderThickness = new Thickness(1),
            Child = _label,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_instructions, 16);
        Canvas.SetTop(_instructions, 16);
        _canvas.Children.Add(_instructions);
        Content = new Grid { Children = { _outline, _canvas } };
        IsVisibleChanged += (_, _) => { if (_presented && !IsVisible && !_finishing) Cancel(); };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var bounds = _physicalWorkArea;
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), (int)Math.Round(bounds.Left),
            (int)Math.Round(bounds.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0040))
        {
            Cancel();
            return;
        }
        var instructionsOrigin = _canvas.PointFromScreen(_instructionsArea.TopLeft);
        Canvas.SetLeft(_instructions, instructionsOrigin.X + 16);
        Canvas.SetTop(_instructions, instructionsOrigin.Y + 16);
        _presented = true;
        Focus();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_finishing) return;
        BeginDrag(GetPhysicalButtonPosition(e));
        if (!CaptureMouse()) { Cancel(); return; }
        QueueDragFrame();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_start.HasValue && !_finishing) QueueDragFrame();
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_start.HasValue || _finishing) return;
        var end = GetPhysicalButtonPosition(e);
        e.Handled = true;
        CompleteDrag(end);
    }

    private Point GetPhysicalButtonPosition(MouseButtonEventArgs e)
    {
        var point = _canvas.PointToScreen(e.GetPosition(_canvas));
        return new(Math.Round(point.X), Math.Round(point.Y));
    }

    private void BeginDrag(Point start)
    {
        StopDragRendering();
        _start = start;
        _drawnBounds = null;
        _shownSize = null;
    }

    private void CompleteDrag(Point end)
    {
        if (_start is not { } start || _finishing) return;
        var area = YouTubeMiniPlayerArrangementAreaService.SelectRegion(start, end, _physicalWorkArea);
        EndDrag();
        if (area.Width < 16 || area.Height < 16)
        {
            _outline.Draw(null);
            _label.Text = "範囲が小さすぎます。ドラッグし直してください  ／  Escで中止";
            return;
        }
        SelectedPhysicalBounds = area;
        Finish(returnToMenu: true);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Finish(returnToMenu: true); }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Finish(returnToMenu: true);
        base.OnPreviewMouseRightButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_start.HasValue && !_finishing) Cancel();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_finishing) Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _finishing = true;
        EndDrag();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopDragRendering();
        Content = null;
        base.OnClosed(e);
    }

    public void Cancel() => Finish(returnToMenu: false);

    private void Finish(bool returnToMenu)
    {
        if (_finishing) return;
        _finishing = true;
        ReturnToMenu = returnToMenu;
        EndDrag();
        if (returnToMenu && IsActive && Owner is YouTubeMiniPlayerListWindow { IsOpen: true, IsCollapsed: false } listWindow)
        {
            listWindow.Activate();
        }
        Close();
    }

    private void EndDrag()
    {
        _start = null;
        StopDragRendering();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void QueueDragFrame()
    {
        _dragFramePending = true;
        if (_dragRenderingAttached) return;
        _dragRenderingAttached = true;
        CompositionTarget.Rendering += OnDragRendering;
    }

    private void StopDragRendering()
    {
        _dragFramePending = false;
        if (!_dragRenderingAttached) return;
        _dragRenderingAttached = false;
        CompositionTarget.Rendering -= OnDragRendering;
    }

    private void OnDragRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
            RenderDragFrame(frame.RenderingTime, ReadPhysicalCursorPosition);
    }

    private static Point? ReadPhysicalCursorPosition()
        => GetCursorPos(out var point) ? new Point(point.X, point.Y) : null;

    private void RenderDragFrame(TimeSpan renderingTime, Func<Point?> readCursor)
    {
        if (!_dragRenderingAttached || _finishing || _start is not { } start) return;
        if (_dragFramePending)
        {
            _dragFramePending = false;
            if (readCursor() is { } current)
            {
                var area = YouTubeMiniPlayerArrangementAreaService.SelectRegion(start, current, _physicalWorkArea);
                if (_drawnBounds != area) DrawDragFrame(area);
            }
        }

        var size = _drawnBounds?.Size;
        if (size is { } actual && _shownSize != actual
            && (_shownSize is null || renderingTime - _lastSizeLabelUpdate >= SizeLabelUpdateInterval))
        {
            _label.Text = $"配置範囲：{actual.Width:0} × {actual.Height:0} px  ／  Escで中止";
            _shownSize = actual;
            _lastSizeLabelUpdate = renderingTime;
        }
        if (!_dragFramePending && _shownSize == size) StopDragRendering();
    }

    private void DrawDragFrame(Rect area)
    {
        var topLeft = _canvas.PointFromScreen(area.TopLeft);
        var bottomRight = _canvas.PointFromScreen(area.BottomRight);
        _outline.Draw(new Rect(topLeft, bottomRight));
        _drawnBounds = area;
    }

    private sealed class DragOutline : FrameworkElement
    {
        private const double StrokeWidth = 2;
        private static readonly Pen Stroke = CreateStroke();
        private readonly DrawingVisual _visual = new();

        public DragOutline() => AddVisualChild(_visual);

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
            => index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));

        public void Draw(Rect? bounds)
        {
            using var context = _visual.RenderOpen();
            if (bounds is not { } area || area.Width == 0 || area.Height == 0) return;
            var insetX = Math.Min(StrokeWidth / 2, area.Width / 2);
            var insetY = Math.Min(StrokeWidth / 2, area.Height / 2);
            context.DrawRectangle(null, Stroke, new Rect(area.Left + insetX, area.Top + insetY,
                area.Width - 2 * insetX, area.Height - 2 * insetY));
        }

        private static Pen CreateStroke()
        {
            var stroke = new Pen(new SolidColorBrush(Color.FromRgb(103, 232, 249)), StrokeWidth);
            stroke.Freeze();
            return stroke;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
