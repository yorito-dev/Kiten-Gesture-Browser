using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal enum YouTubeMiniPlayerArrangementPreviewRole
{
    Standard,
    Focus,
    Secondary,
    Chat
}

internal enum YouTubeMiniPlayerArrangementResizeCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal sealed record YouTubeMiniPlayerArrangementPreviewItem(
    Guid? PlayerId,
    Rect Bounds,
    string Label,
    YouTubeMiniPlayerArrangementPreviewRole Role,
    double VideoAspectRatio = 0);

internal sealed class YouTubeMiniPlayerArrangementPreviewWindow : Window, IYouTubeMiniPlayerArrangementPreview
{
    private const int ExtendedWindowStyleIndex = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int TransparentWindowStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateWindowStyle = 0x08000000;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly Rect _workArea;
    private readonly Canvas _canvas;
    private readonly Dictionary<Guid, PreviewFrameState> _playerFrames = [];
    private HwndSource? _windowSource;
    private PreviewFrameState? _activeFrame;
    private YouTubeMiniPlayerArrangementResizeCorner? _activeResizeCorner;
    private Point _interactionStartPoint;
    private Rect _interactionStartBounds;
    private bool _isEditMode;

    public YouTubeMiniPlayerArrangementPreviewWindow(
        YouTubeMiniPlayerArrangementPlan plan,
        IReadOnlyList<YouTubeMiniPlayerArrangementPreviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var workArea = plan.WorkArea;
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        ArgumentNullException.ThrowIfNull(items);

        Plan = plan;
        _workArea = workArea;
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;
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
        SizeToContent = SizeToContent.Manual;

        _canvas = new Canvas
        {
            Width = workArea.Width,
            Height = workArea.Height,
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };

        foreach (var item in items)
        {
            AddPreviewFrame(item);
        }

        PreviewMouseMove += PreviewWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp += PreviewWindow_PreviewMouseLeftButtonUp;
        LostMouseCapture += PreviewWindow_LostMouseCapture;
        Content = _canvas;
    }

    public bool HasEditablePlayerFrames => _playerFrames.Count > 0;

    public bool IsEditMode => _isEditMode;

    public YouTubeMiniPlayerArrangementPlan Plan { get; }

    public void ShowWithoutActivation()
    {
        Show();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = SetWindowPos(
                handle,
                TopmostWindow,
                0,
                0,
                0,
                0,
                SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate | SetWindowPosShowWindow);
        }
    }

    public void SetEditMode(bool enabled)
    {
        _isEditMode = enabled && HasEditablePlayerFrames;
        EndInteraction();
        IsHitTestVisible = _isEditMode;
        _canvas.IsHitTestVisible = _isEditMode;
        foreach (var frame in _playerFrames.Values)
        {
            frame.Border.IsHitTestVisible = _isEditMode;
            frame.Border.Cursor = Cursors.Arrow;
            foreach (var handle in frame.InteractionHandles)
            {
                handle.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        ApplyExtendedWindowStyles();
    }

    public void ResetPlayerBounds()
    {
        foreach (var frame in _playerFrames.Values)
        {
            SetFrameBounds(frame, frame.OriginalBounds);
        }
    }

    public IReadOnlyDictionary<Guid, Rect> GetPlayerBounds()
    {
        return _playerFrames.ToDictionary(
            static pair => pair.Key,
            pair => new Rect(
                _workArea.Left + pair.Value.Bounds.Left,
                _workArea.Top + pair.Value.Bounds.Top,
                pair.Value.Bounds.Width,
                pair.Value.Bounds.Height));
    }

    public bool IsPointOverEditableControl(Point screenPoint)
    {
        if (!_isEditMode)
        {
            return false;
        }

        try
        {
            var localPoint = PointFromScreen(screenPoint);
            return _playerFrames.Values.Any(frame =>
                frame.InteractionHandles.Any(handle => IsPointWithinElement(handle, localPoint)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
        ApplyExtendedWindowStyles();
    }

    protected override void OnClosed(EventArgs e)
    {
        EndInteraction();
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
        Content = null;
        base.OnClosed(e);
    }

    private void AddPreviewFrame(YouTubeMiniPlayerArrangementPreviewItem item)
    {
        var localBounds = new Rect(
            item.Bounds.Left - _workArea.Left,
            item.Bounds.Top - _workArea.Top,
            Math.Max(1, item.Bounds.Width),
            Math.Max(1, item.Bounds.Height));
        var border = new Border
        {
            Width = localBounds.Width,
            Height = localBounds.Height,
            BorderThickness = new Thickness(2),
            BorderBrush = CreateBorderBrush(item.Role),
            Background = CreateBackgroundBrush(item.Role),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false
        };

        var labelBorder = new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(6, 3, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(230, 17, 20, 26)),
            BorderBrush = CreateBorderBrush(item.Role),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = item.Label,
                MaxWidth = Math.Max(48, localBounds.Width - 28),
                Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false
            }
        };

        var content = new Grid { IsHitTestVisible = true };
        content.Children.Add(labelBorder);
        border.Child = content;
        Canvas.SetLeft(border, localBounds.Left);
        Canvas.SetTop(border, localBounds.Top);
        _canvas.Children.Add(border);

        if (item.PlayerId is not { } playerId)
        {
            return;
        }

        var aspectRatio = double.IsFinite(item.VideoAspectRatio) && item.VideoAspectRatio > 0
            ? item.VideoAspectRatio
            : YouTubeMiniPlayerLayoutService.VideoAspectRatio;
        var frame = new PreviewFrameState(playerId, border, localBounds, aspectRatio);
        _playerFrames[playerId] = frame;
        AddMoveHandle(content, frame);
        AddResizeHandle(content, frame, YouTubeMiniPlayerArrangementResizeCorner.TopLeft);
        AddResizeHandle(content, frame, YouTubeMiniPlayerArrangementResizeCorner.TopRight);
        AddResizeHandle(content, frame, YouTubeMiniPlayerArrangementResizeCorner.BottomLeft);
        AddResizeHandle(content, frame, YouTubeMiniPlayerArrangementResizeCorner.BottomRight);
    }

    private void AddMoveHandle(Grid content, PreviewFrameState frame)
    {
        var handle = new Border
        {
            Width = 32,
            Height = 24,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.SizeAll,
            ToolTip = new ToolTip
            {
                Content = "移動",
                Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
                Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 6, 3)
            },
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "✥",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 15,
                IsHitTestVisible = false
            }
        };
        handle.PreviewMouseLeftButtonDown += (_, e) => BeginInteraction(frame, null, e);
        Panel.SetZIndex(handle, 10);
        frame.InteractionHandles.Add(handle);
        content.Children.Add(handle);
    }

    private void AddResizeHandle(
        Grid content,
        PreviewFrameState frame,
        YouTubeMiniPlayerArrangementResizeCorner corner)
    {
        var isLeft = corner is YouTubeMiniPlayerArrangementResizeCorner.TopLeft
            or YouTubeMiniPlayerArrangementResizeCorner.BottomLeft;
        var isTop = corner is YouTubeMiniPlayerArrangementResizeCorner.TopLeft
            or YouTubeMiniPlayerArrangementResizeCorner.TopRight;
        var handle = new Border
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = isLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(2),
            Cursor = corner is YouTubeMiniPlayerArrangementResizeCorner.TopLeft
                or YouTubeMiniPlayerArrangementResizeCorner.BottomRight
                    ? Cursors.SizeNWSE
                    : Cursors.SizeNESW,
            Visibility = Visibility.Collapsed
        };
        handle.PreviewMouseLeftButtonDown += (_, e) => BeginInteraction(frame, corner, e);
        Panel.SetZIndex(handle, 10);
        frame.InteractionHandles.Add(handle);
        content.Children.Add(handle);
    }

    private void BeginInteraction(
        PreviewFrameState frame,
        YouTubeMiniPlayerArrangementResizeCorner? corner,
        MouseButtonEventArgs e)
    {
        if (!_isEditMode || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _activeFrame = frame;
        _activeResizeCorner = corner;
        _interactionStartPoint = e.GetPosition(_canvas);
        _interactionStartBounds = frame.Bounds;
        _ = Mouse.Capture(frame.Border, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void PreviewWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeFrame is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(_canvas);
        var horizontalChange = currentPoint.X - _interactionStartPoint.X;
        var verticalChange = currentPoint.Y - _interactionStartPoint.Y;
        Rect bounds;
        if (_activeResizeCorner is { } corner)
        {
            var resizeLeft = corner is YouTubeMiniPlayerArrangementResizeCorner.TopLeft
                or YouTubeMiniPlayerArrangementResizeCorner.BottomLeft;
            var resizeTop = corner is YouTubeMiniPlayerArrangementResizeCorner.TopLeft
                or YouTubeMiniPlayerArrangementResizeCorner.TopRight;
            bounds = YouTubeMiniPlayerInputService.CalculateConstrainedCornerResizeBounds(
                _interactionStartBounds,
                horizontalChange,
                verticalChange,
                _activeFrame.VideoAspectRatio,
                resizeLeft,
                resizeTop,
                new Rect(0, 0, _canvas.Width, _canvas.Height));
        }
        else
        {
            bounds = YouTubeMiniPlayerInputService.CalculateConstrainedMoveBounds(
                _interactionStartBounds,
                horizontalChange,
                verticalChange,
                new Rect(0, 0, _canvas.Width, _canvas.Height));
        }

        SetFrameBounds(_activeFrame, bounds);
        e.Handled = true;
    }

    private void PreviewWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeFrame is null)
        {
            return;
        }

        EndInteraction();
        e.Handled = true;
    }

    private void PreviewWindow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _activeFrame = null;
        _activeResizeCorner = null;
    }

    private void EndInteraction()
    {
        _activeFrame = null;
        _activeResizeCorner = null;
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }
    }

    private static void SetFrameBounds(PreviewFrameState frame, Rect bounds)
    {
        frame.Bounds = bounds;
        frame.Border.Width = bounds.Width;
        frame.Border.Height = bounds.Height;
        Canvas.SetLeft(frame.Border, bounds.Left);
        Canvas.SetTop(frame.Border, bounds.Top);
        if (frame.Border.Child is Grid { Children.Count: > 0 } content
            && content.Children[0] is Border { Child: TextBlock label })
        {
            label.MaxWidth = Math.Max(48, bounds.Width - 28);
        }
    }

    private bool IsPointWithinElement(FrameworkElement element, Point canvasPoint)
    {
        if (element.Visibility != Visibility.Visible)
        {
            return false;
        }

        try
        {
            var topLeft = element.TranslatePoint(new Point(0, 0), _canvas);
            var width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
            var height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
            return double.IsFinite(width)
                && double.IsFinite(height)
                && width > 0
                && height > 0
                && new Rect(topLeft, new Size(width, height)).Contains(canvasPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ApplyExtendedWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLong(handle, ExtendedWindowStyleIndex)
            | ToolWindowStyle
            | NoActivateWindowStyle;
        styles = _isEditMode
            ? styles & ~TransparentWindowStyle
            : styles | TransparentWindowStyle;
        _ = SetWindowLong(handle, ExtendedWindowStyleIndex, styles);
        _ = SetWindowPos(
            handle,
            TopmostWindow,
            0,
            0,
            0,
            0,
            SetWindowPosNoMove
            | SetWindowPosNoSize
            | SetWindowPosNoActivate
            | SetWindowPosFrameChanged
            | SetWindowPosShowWindow);
    }

    private static Brush CreateBorderBrush(YouTubeMiniPlayerArrangementPreviewRole role)
    {
        return role switch
        {
            YouTubeMiniPlayerArrangementPreviewRole.Focus =>
                new SolidColorBrush(Color.FromRgb(34, 211, 238)),
            YouTubeMiniPlayerArrangementPreviewRole.Secondary =>
                new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            YouTubeMiniPlayerArrangementPreviewRole.Chat =>
                new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            _ => new SolidColorBrush(Color.FromRgb(96, 165, 250))
        };
    }

    private static Brush CreateBackgroundBrush(YouTubeMiniPlayerArrangementPreviewRole role)
    {
        return role switch
        {
            YouTubeMiniPlayerArrangementPreviewRole.Focus =>
                new SolidColorBrush(Color.FromArgb(42, 8, 145, 178)),
            YouTubeMiniPlayerArrangementPreviewRole.Secondary =>
                new SolidColorBrush(Color.FromArgb(34, 55, 65, 81)),
            YouTubeMiniPlayerArrangementPreviewRole.Chat =>
                new SolidColorBrush(Color.FromArgb(38, 180, 83, 9)),
            _ => new SolidColorBrush(Color.FromArgb(38, 30, 64, 175))
        };
    }

    private IntPtr WindowProcedure(
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
        if (!_isEditMode)
        {
            return new IntPtr(HtTransparent);
        }

        var packedPoint = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xffff)),
            unchecked((short)((packedPoint >> 16) & 0xffff)));
        return IsPointOverEditableControl(screenPoint)
            ? new IntPtr(HtClient)
            : new IntPtr(HtTransparent);
    }

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

    private sealed class PreviewFrameState(
        Guid playerId,
        Border border,
        Rect originalBounds,
        double videoAspectRatio)
    {
        public Guid PlayerId { get; } = playerId;

        public Border Border { get; } = border;

        public Rect OriginalBounds { get; } = originalBounds;

        public Rect Bounds { get; set; } = originalBounds;

        public double VideoAspectRatio { get; } = videoAspectRatio;

        public List<FrameworkElement> InteractionHandles { get; } = [];
    }
}
