using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record YouTubeMiniPlayerListPopupElements(
    FrameworkElement RootGrid,
    FrameworkElement YouTubeMiniPlayerListPopupRoot,
    Popup YouTubeMiniPlayerListPopup,
    Popup YouTubeMiniPlayerAbRepeatPopup,
    Popup YouTubeMiniPlayerSpeedPopup,
    Popup YouTubeMiniPlayerVolumePopup,
    Popup YouTubeMiniPlayerItemMorePopup,
    Popup YouTubeMiniPlayerArrangePopup,
    Popup YouTubeMiniPlayerGlobalActionsPopup,
    Popup YouTubeMiniPlayerLinearPlacementPopup);

internal interface IYouTubeMiniPlayerListPopupHost
{
    bool IsPreviewActive { get; }
    bool IsArrangementEditMode { get; }
    bool IsOverArrangementEditableControl(Point point);
    Rect GetWorkAreaForDevicePoint(Point devicePoint);
    string? GetTriggerTag(DependencyObject? source);
    bool TryGetCursorDevicePoint(out Point point);
    void CancelArrangementPreview(bool showStatus);
    void CancelPendingHoverPreview();
    Task StopHoverPreviewAsync();
}

internal sealed class YouTubeMiniPlayerListPopupController : IDisposable
{
    private const double YouTubeMiniPlayerListPopupDefaultHorizontalOffset = -696;
    private const double YouTubeMiniPlayerListPopupDefaultVerticalOffset = 4;
    private const double YouTubeMiniPlayerListPopupEdgeMargin = 8;
    private const double YouTubeMiniPlayerArrangePopupWidth = 420;
    private const double YouTubeMiniPlayerArrangePopupGap = 12;
    private readonly Window _owner;
    private readonly YouTubeMiniPlayerListPopupElements _elements;
    private readonly YouTubeMiniPlayerListViewController _list;
    private readonly IYouTubeMiniPlayerListPopupHost _host;
    private readonly Func<FrameworkElement, Point, bool> _hitTest;
    private readonly YouTubeMiniPlayerListInteractionCoordinator _youTubeMiniPlayerListInteractionCoordinator;
    private bool _isDisposed;
    private bool _isTracking;

    public YouTubeMiniPlayerListPopupController(
        Window owner, YouTubeMiniPlayerListPopupElements elements,
        YouTubeMiniPlayerListViewController list, IYouTubeMiniPlayerListPopupHost host,
        Func<FrameworkElement, Point, bool>? hitTest = null)
    {
        _owner = owner;
        _elements = elements;
        _list = list;
        _host = host;
        _hitTest = hitTest ?? IsCursorWithinYouTubeMiniPlayerListElement;
        _youTubeMiniPlayerListInteractionCoordinator = list.Interaction;
    }

    private Dispatcher Dispatcher => _owner.Dispatcher;
    private FrameworkElement RootGrid => _elements.RootGrid;
    private FrameworkElement YouTubeMiniPlayerListPopupRoot => _elements.YouTubeMiniPlayerListPopupRoot;
    private Popup YouTubeMiniPlayerListPopup => _elements.YouTubeMiniPlayerListPopup;
    private Popup YouTubeMiniPlayerAbRepeatPopup => _elements.YouTubeMiniPlayerAbRepeatPopup;
    private Popup YouTubeMiniPlayerSpeedPopup => _elements.YouTubeMiniPlayerSpeedPopup;
    private Popup YouTubeMiniPlayerVolumePopup => _elements.YouTubeMiniPlayerVolumePopup;
    private Popup YouTubeMiniPlayerItemMorePopup => _elements.YouTubeMiniPlayerItemMorePopup;
    private Popup YouTubeMiniPlayerArrangePopup => _elements.YouTubeMiniPlayerArrangePopup;
    private Popup YouTubeMiniPlayerGlobalActionsPopup => _elements.YouTubeMiniPlayerGlobalActionsPopup;
    private Popup YouTubeMiniPlayerLinearPlacementPopup => _elements.YouTubeMiniPlayerLinearPlacementPopup;

    public void InitializeTracking()
    {
        if (_isDisposed || _isTracking) return;
        _isTracking = true;
        InputManager.Current.PreProcessInput += YouTubeMiniPlayerListPopup_PreProcessInput;
        _owner.LocationChanged += YouTubeMiniPlayerListOwnerWindow_LocationChanged;
        RootGrid.SizeChanged += YouTubeMiniPlayerListOwnerLayout_SizeChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_isTracking)
        {
            InputManager.Current.PreProcessInput -= YouTubeMiniPlayerListPopup_PreProcessInput;
            _owner.LocationChanged -= YouTubeMiniPlayerListOwnerWindow_LocationChanged;
            RootGrid.SizeChanged -= YouTubeMiniPlayerListOwnerLayout_SizeChanged;
            _isTracking = false;
        }
    }

    public async Task YouTubeMiniPlayerListPopup_Opened(object sender, EventArgs e)
    {
        QueueYouTubeMiniPlayerListPopupPositionClamp();
        _list.BeginYouTubeMiniPlayerThumbnailLoads();
        await _list.RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: true);
        _list.StartRefreshTimer();
    }

    public async Task YouTubeMiniPlayerListPopup_Closed(object sender, EventArgs e)
    {
        _host.CancelArrangementPreview(showStatus: false);
        _list.ResetYouTubeMiniPlayerListDragState();
        _list.ClearYouTubeMiniPlayerListDropCue();
        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        YouTubeMiniPlayerVolumePopup.IsOpen = false;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        _list.StopRefreshTimer();
        _list.CancelYouTubeMiniPlayerThumbnailLoads();
        _host.CancelPendingHoverPreview();
        await _host.StopHoverPreviewAsync();
        _list.ClearYouTubeMiniPlayerListWindowHighlights();
    }

    public void YouTubeMiniPlayerListPopupDragHandle_DragStarted(
        object sender,
        DragStartedEventArgs e)
    {
        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        YouTubeMiniPlayerVolumePopup.IsOpen = false;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        e.Handled = true;
    }

    public void YouTubeMiniPlayerListPopupDragHandle_DragDelta(
        object sender,
        DragDeltaEventArgs e)
    {
        YouTubeMiniPlayerListPopup.HorizontalOffset += e.HorizontalChange;
        YouTubeMiniPlayerListPopup.VerticalOffset += e.VerticalChange;
        ClampYouTubeMiniPlayerListPopupPosition(GetCurrentCursorDevicePoint());
        e.Handled = true;
    }

    public void YouTubeMiniPlayerListPopupDragHandle_DragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        ClampYouTubeMiniPlayerListPopupPosition(GetCurrentCursorDevicePoint());
        e.Handled = true;
    }

    private Point? GetCurrentCursorDevicePoint()
        => _host.TryGetCursorDevicePoint(out var point) ? point : null;

    public void YouTubeMiniPlayerListPopupDragHandle_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        YouTubeMiniPlayerListPopup.HorizontalOffset =
            YouTubeMiniPlayerListPopupDefaultHorizontalOffset;
        YouTubeMiniPlayerListPopup.VerticalOffset =
            YouTubeMiniPlayerListPopupDefaultVerticalOffset;
        QueueYouTubeMiniPlayerListPopupPositionClamp();
        e.Handled = true;
    }

    public void YouTubeMiniPlayerListPopupRoot_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        QueueYouTubeMiniPlayerListPopupPositionClamp();
    }

    private void YouTubeMiniPlayerListOwnerWindow_LocationChanged(
        object? sender,
        EventArgs e)
    {
        QueueYouTubeMiniPlayerListPopupPositionClamp();
    }

    private void YouTubeMiniPlayerListOwnerLayout_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        QueueYouTubeMiniPlayerListPopupPositionClamp();
    }

    public void QueueYouTubeMiniPlayerListPopupPositionClamp()
    {
        if (_isDisposed || !YouTubeMiniPlayerListPopup.IsOpen)
        {
            return;
        }

        if (!_youTubeMiniPlayerListInteractionCoordinator.TryQueuePopupPositionClamp())
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                _youTubeMiniPlayerListInteractionCoordinator.CompletePopupPositionClamp();
                if (!_isDisposed) ClampYouTubeMiniPlayerListPopupPosition();
            },
            DispatcherPriority.ContextIdle);
    }

    public void ClampYouTubeMiniPlayerListPopupPosition(Point? preferredDevicePoint = null)
    {
        if (_isDisposed || !YouTubeMiniPlayerListPopup.IsOpen
            || YouTubeMiniPlayerListPopup.Child is not FrameworkElement popupRoot
            || popupRoot.ActualWidth <= 0
            || popupRoot.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            var ownerDpi = VisualTreeHelper.GetDpi(_owner);
            var popupDpi = VisualTreeHelper.GetDpi(popupRoot);
            var deviceOrigin = popupRoot.PointToScreen(new Point(0, 0));
            var popupDeviceBounds = new Rect(
                deviceOrigin.X,
                deviceOrigin.Y,
                popupRoot.ActualWidth * popupDpi.DpiScaleX,
                popupRoot.ActualHeight * popupDpi.DpiScaleY);
            var popupBounds = new Rect(
                deviceOrigin.X / ownerDpi.DpiScaleX,
                deviceOrigin.Y / ownerDpi.DpiScaleY,
                popupRoot.ActualWidth * popupDpi.DpiScaleX / ownerDpi.DpiScaleX,
                popupRoot.ActualHeight * popupDpi.DpiScaleY / ownerDpi.DpiScaleY);
            var adjustment = CalculateYouTubeMiniPlayerListPopupClampAdjustment(
                _host.GetWorkAreaForDevicePoint(
                    CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
                        popupDeviceBounds,
                        preferredDevicePoint)),
                popupBounds,
                YouTubeMiniPlayerListPopupEdgeMargin);
            if (Math.Abs(adjustment.X) >= 0.1)
            {
                YouTubeMiniPlayerListPopup.HorizontalOffset += adjustment.X;
            }

            if (Math.Abs(adjustment.Y) >= 0.1)
            {
                YouTubeMiniPlayerListPopup.VerticalOffset += adjustment.Y;
            }

            if (YouTubeMiniPlayerArrangePopup.IsOpen)
            {
                UpdateYouTubeMiniPlayerArrangePopupPlacement();
            }
        }
        catch (InvalidOperationException)
        {
            // Popup の画面座標が確定する前は Opened 後の再試行へ任せる。
        }
    }

    internal static Vector CalculateYouTubeMiniPlayerListPopupClampAdjustment(
        Rect workArea,
        Rect popupBounds,
        double edgeMargin)
    {
        static double ClampOrigin(
            double origin,
            double length,
            double areaOrigin,
            double areaLength,
            double margin)
        {
            var minimum = areaOrigin + margin;
            var maximum = areaOrigin + areaLength - margin - length;
            return maximum >= minimum
                ? Math.Clamp(origin, minimum, maximum)
                : areaOrigin + ((areaLength - length) / 2);
        }

        var clampedLeft = ClampOrigin(
            popupBounds.Left,
            popupBounds.Width,
            workArea.Left,
            workArea.Width,
            edgeMargin);
        var clampedTop = ClampOrigin(
            popupBounds.Top,
            popupBounds.Height,
            workArea.Top,
            workArea.Height,
            edgeMargin);
        return new Vector(
            clampedLeft - popupBounds.Left,
            clampedTop - popupBounds.Top);
    }

    internal static Point CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
        Rect popupDeviceBounds,
        Point? preferredDevicePoint)
        => preferredDevicePoint ?? new Point(
            popupDeviceBounds.Left + (popupDeviceBounds.Width / 2),
            popupDeviceBounds.Top + (popupDeviceBounds.Height / 2));

    public void UpdateYouTubeMiniPlayerArrangePopupPlacement()
    {
        if (YouTubeMiniPlayerListPopupRoot.ActualWidth <= 0
            || YouTubeMiniPlayerListPopupRoot.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            var ownerDpi = VisualTreeHelper.GetDpi(_owner);
            var popupDpi = VisualTreeHelper.GetDpi(YouTubeMiniPlayerListPopupRoot);
            var deviceOrigin = YouTubeMiniPlayerListPopupRoot.PointToScreen(new Point(0, 0));
            var listPopupDeviceBounds = new Rect(
                deviceOrigin.X,
                deviceOrigin.Y,
                YouTubeMiniPlayerListPopupRoot.ActualWidth * popupDpi.DpiScaleX,
                YouTubeMiniPlayerListPopupRoot.ActualHeight * popupDpi.DpiScaleY);
            var listPopupBounds = new Rect(
                deviceOrigin.X / ownerDpi.DpiScaleX,
                deviceOrigin.Y / ownerDpi.DpiScaleY,
                YouTubeMiniPlayerListPopupRoot.ActualWidth * popupDpi.DpiScaleX / ownerDpi.DpiScaleX,
                YouTubeMiniPlayerListPopupRoot.ActualHeight * popupDpi.DpiScaleY / ownerDpi.DpiScaleY);
            var placement = CalculateYouTubeMiniPlayerArrangePopupPlacement(
                _host.GetWorkAreaForDevicePoint(
                    CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
                        listPopupDeviceBounds,
                        preferredDevicePoint: null)),
                listPopupBounds,
                YouTubeMiniPlayerArrangePopupWidth,
                YouTubeMiniPlayerArrangePopupGap,
                YouTubeMiniPlayerListPopupEdgeMargin);

            YouTubeMiniPlayerArrangePopup.Placement = placement;
            YouTubeMiniPlayerArrangePopup.HorizontalOffset = placement == PlacementMode.Left
                ? -YouTubeMiniPlayerArrangePopupGap
                : YouTubeMiniPlayerArrangePopupGap;
            YouTubeMiniPlayerArrangePopup.VerticalOffset = 0;
        }
        catch (InvalidOperationException)
        {
            // Popupの画面座標が未確定の場合は、XAMLの左側配置を維持する。
        }
    }

    internal static PlacementMode CalculateYouTubeMiniPlayerArrangePopupPlacement(
        Rect workArea,
        Rect listPopupBounds,
        double arrangePopupWidth,
        double gap,
        double edgeMargin)
    {
        var leftSpace = listPopupBounds.Left - (workArea.Left + edgeMargin);
        var rightSpace = (workArea.Right - edgeMargin) - listPopupBounds.Right;
        var requiredSpace = arrangePopupWidth + gap;
        if (leftSpace >= requiredSpace)
        {
            return PlacementMode.Left;
        }

        if (rightSpace >= requiredSpace)
        {
            return PlacementMode.Right;
        }

        return leftSpace >= rightSpace
            ? PlacementMode.Left
            : PlacementMode.Right;
    }

    public void YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _host.CancelPendingHoverPreview();
        if (_host.IsPreviewActive)
        {
            _ = _host.StopHoverPreviewAsync();
        }

        var triggerTag = _host.GetTriggerTag(e.OriginalSource as DependencyObject);
        if (triggerTag == "YouTubeMiniPlayerAbRepeatPopupTrigger")
        {
            YouTubeMiniPlayerSpeedPopup.IsOpen = false;
            YouTubeMiniPlayerVolumePopup.IsOpen = false;
            YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
            return;
        }

        if (triggerTag == "YouTubeMiniPlayerSpeedPopupTrigger")
        {
            YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
            YouTubeMiniPlayerVolumePopup.IsOpen = false;
            YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
            return;
        }

        if (triggerTag == "YouTubeMiniPlayerVolumePopupTrigger")
        {
            YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
            YouTubeMiniPlayerSpeedPopup.IsOpen = false;
            YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
            return;
        }

        if (triggerTag == "YouTubeMiniPlayerLinearPlacementPopupTrigger")
        {
            YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
            YouTubeMiniPlayerSpeedPopup.IsOpen = false;
            YouTubeMiniPlayerVolumePopup.IsOpen = false;
            return;
        }

        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        YouTubeMiniPlayerVolumePopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
    }

    private void YouTubeMiniPlayerListPopup_PreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (_isDisposed || e.StagingItem.Input is not MouseEventArgs mouseInput)
        {
            return;
        }

        _list.StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft();
        if (mouseInput is not MouseButtonEventArgs mouseArgs
            || mouseArgs.ChangedButton != MouseButton.Left
            || mouseArgs.ButtonState != MouseButtonState.Pressed
            || _youTubeMiniPlayerListInteractionCoordinator.IsDuplicatePopupMouseDown(mouseArgs.Timestamp)
            || !_host.TryGetCursorDevicePoint(out var cursorPoint))
        {
            return;
        }

        ProcessLeftMouseDown(mouseArgs.Timestamp, cursorPoint);
    }

    private void ProcessLeftMouseDown(int timestamp, Point cursorPoint)
    {
        _youTubeMiniPlayerListInteractionCoordinator.RecordPopupMouseDown(timestamp);
        var isWithinAbRepeatPopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerAbRepeatPopup,
            cursorPoint);
        var isWithinSpeedPopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerSpeedPopup,
            cursorPoint);
        var isWithinVolumePopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerVolumePopup,
            cursorPoint);
        var isWithinItemMorePopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerItemMorePopup,
            cursorPoint);
        var isWithinArrangePopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerArrangePopup,
            cursorPoint);
        var isWithinGlobalActionsPopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerGlobalActionsPopup,
            cursorPoint);
        var isWithinLinearPlacementPopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerLinearPlacementPopup,
            cursorPoint);
        var isWithinListPopup = IsCursorWithinYouTubeMiniPlayerListPopup(
            YouTubeMiniPlayerListPopup,
            cursorPoint);
        var isWithinArrangementPreview =
            _host.IsOverArrangementEditableControl(cursorPoint);
        var isArrangementPreviewEditMode =
            _host.IsArrangementEditMode;

        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerAbRepeatPopup,
            cursorPoint,
            isWithinAbRepeatPopup || isWithinListPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerSpeedPopup,
            cursorPoint,
            isWithinSpeedPopup || isWithinListPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerVolumePopup,
            cursorPoint,
            isWithinVolumePopup || isWithinListPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerItemMorePopup,
            cursorPoint,
            isWithinItemMorePopup || isWithinListPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerArrangePopup,
            cursorPoint,
            isWithinArrangePopup || isWithinListPopup || isWithinLinearPlacementPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerGlobalActionsPopup,
            cursorPoint,
            isWithinGlobalActionsPopup || isWithinListPopup);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
            YouTubeMiniPlayerLinearPlacementPopup,
            cursorPoint,
            isWithinLinearPlacementPopup || isWithinListPopup || isWithinArrangePopup);

        if (YouTubeMiniPlayerListPopup.IsOpen
            && !isWithinListPopup
            && !IsCursorWithinYouTubeMiniPlayerListPopupPlacementTarget(YouTubeMiniPlayerListPopup, cursorPoint)
            && !isWithinAbRepeatPopup
            && !isWithinSpeedPopup
            && !isWithinVolumePopup
            && !isWithinItemMorePopup
            && !isWithinArrangePopup
            && !isWithinGlobalActionsPopup
            && !isWithinLinearPlacementPopup
            && !isWithinArrangementPreview
            && !isArrangementPreviewEditMode)
        {
            YouTubeMiniPlayerListPopup.IsOpen = false;
        }
    }

    private void CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
        Popup popup,
        Point cursorPoint,
        bool isWithinPopup)
    {
        if (popup.IsOpen
            && !isWithinPopup
            && !IsCursorWithinYouTubeMiniPlayerListPopupPlacementTarget(popup, cursorPoint))
        {
            popup.IsOpen = false;
        }
    }

    private bool IsCursorWithinYouTubeMiniPlayerListPopup(Popup popup, Point cursorPoint)
    {
        return popup.IsOpen
            && popup.Child is FrameworkElement child
            && _hitTest(child, cursorPoint);
    }

    private bool IsCursorWithinYouTubeMiniPlayerListPopupPlacementTarget(
        Popup popup,
        Point cursorPoint)
    {
        return popup.PlacementTarget is FrameworkElement placementTarget
            && _hitTest(placementTarget, cursorPoint);
    }

    private static bool IsCursorWithinYouTubeMiniPlayerListElement(
        FrameworkElement element,
        Point cursorPoint)
    {
        if (!element.IsVisible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var point = element.PointFromScreen(cursorPoint);
            return point.X >= 0
                && point.Y >= 0
                && point.X <= element.ActualWidth
                && point.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
