using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record YouTubeMiniPlayerListPopupElements(
    FrameworkElement RootGrid,
    FrameworkElement YouTubeMiniPlayerListPopupRoot,
    YouTubeMiniPlayerListWindow YouTubeMiniPlayerListWindow,
    Popup YouTubeMiniPlayerAbRepeatPopup,
    Popup YouTubeMiniPlayerSpeedPopup,
    Popup YouTubeMiniPlayerVolumePopup,
    Popup YouTubeMiniPlayerItemMorePopup,
    Popup YouTubeMiniPlayerArrangePopup,
    Popup YouTubeMiniPlayerGlobalActionsPopup,
    Popup YouTubeMiniPlayerLinearPlacementPopup,
    ComboBox? YouTubeMiniPlayerArrangeDisplayComboBox = null,
    FrameworkElement? YouTubeMiniPlayerArrangeMenuButton = null,
    FrameworkElement? YouTubeMiniPlayerGlobalActionsButton = null,
    Popup? YouTubeMiniPlayerClosedHistoryPopup = null,
    FrameworkElement? YouTubeMiniPlayerClosedHistoryButton = null);

internal interface IYouTubeMiniPlayerListPopupHost
{
    bool IsPreviewActive { get; }
    bool IsArrangementEditMode { get; }
    Rect GetWorkAreaForDevicePoint(Point devicePoint);
    string? GetTriggerTag(DependencyObject? source);
    bool TryGetCursorDevicePoint(out Point point);
    void CancelArrangementPreview(bool showStatus);
    void CancelPendingHoverPreview();
    Task StopHoverPreviewAsync();
}

internal sealed class YouTubeMiniPlayerListPopupController : IDisposable
{
    private const double YouTubeMiniPlayerListPopupEdgeMargin = 8;
    private const double YouTubeMiniPlayerArrangePopupWidth = 420;
    private const double YouTubeMiniPlayerGlobalActionsPopupWidth = 680;
    private const double YouTubeMiniPlayerArrangePopupGap = 12;
    private readonly Window _owner;
    private readonly YouTubeMiniPlayerListPopupElements _elements;
    private readonly YouTubeMiniPlayerListViewController _list;
    private readonly IYouTubeMiniPlayerListPopupHost _host;
    private readonly Func<FrameworkElement, Point, bool> _hitTest;
    private readonly YouTubeMiniPlayerListInteractionCoordinator _youTubeMiniPlayerListInteractionCoordinator;
    private bool _isDisposed;
    private bool _isTracking;
    private int _presentationGeneration;
    private bool _contentHeightFitRequested;

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
    private YouTubeMiniPlayerListWindow ListWindow => _elements.YouTubeMiniPlayerListWindow;
    private Popup YouTubeMiniPlayerAbRepeatPopup => _elements.YouTubeMiniPlayerAbRepeatPopup;
    private Popup YouTubeMiniPlayerSpeedPopup => _elements.YouTubeMiniPlayerSpeedPopup;
    private Popup YouTubeMiniPlayerVolumePopup => _elements.YouTubeMiniPlayerVolumePopup;
    private Popup YouTubeMiniPlayerItemMorePopup => _elements.YouTubeMiniPlayerItemMorePopup;
    private Popup YouTubeMiniPlayerArrangePopup => _elements.YouTubeMiniPlayerArrangePopup;
    private Popup YouTubeMiniPlayerGlobalActionsPopup => _elements.YouTubeMiniPlayerGlobalActionsPopup;
    private Popup YouTubeMiniPlayerLinearPlacementPopup => _elements.YouTubeMiniPlayerLinearPlacementPopup;
    private Popup? YouTubeMiniPlayerClosedHistoryPopup => _elements.YouTubeMiniPlayerClosedHistoryPopup;

    public void InitializeTracking()
    {
        if (_isDisposed || _isTracking) return;
        _isTracking = true;
        InputManager.Current.PreProcessInput += YouTubeMiniPlayerListPopup_PreProcessInput;
        _owner.LocationChanged += YouTubeMiniPlayerListOwnerWindow_LocationChanged;
        _owner.Deactivated += YouTubeMiniPlayerListOwnerWindow_Deactivated;
        _owner.PreviewGotKeyboardFocus += YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus;
        RootGrid.SizeChanged += YouTubeMiniPlayerListOwnerLayout_SizeChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _presentationGeneration++;
        CloseAuxiliaryPopups();
        if (_isTracking)
        {
            InputManager.Current.PreProcessInput -= YouTubeMiniPlayerListPopup_PreProcessInput;
            _owner.LocationChanged -= YouTubeMiniPlayerListOwnerWindow_LocationChanged;
            _owner.Deactivated -= YouTubeMiniPlayerListOwnerWindow_Deactivated;
            _owner.PreviewGotKeyboardFocus -= YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus;
            RootGrid.SizeChanged -= YouTubeMiniPlayerListOwnerLayout_SizeChanged;
            _isTracking = false;
        }
    }

    public async Task YouTubeMiniPlayerListPopup_Opened(object sender, EventArgs e)
    {
        if (_isDisposed) return;
        var generation = ++_presentationGeneration;
        QueueYouTubeMiniPlayerListPopupPositionClamp();
        _list.BeginYouTubeMiniPlayerThumbnailLoads();
        await _list.RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: true);
        if (_isDisposed || generation != _presentationGeneration || !ListWindow.IsOpen) return;
        ClampYouTubeMiniPlayerListPopupPosition();
        ListWindow.FitInitialHeight(ListWindow.MaxHeight);
        ListWindow.FitToContentHeight();
        ClampYouTubeMiniPlayerListPopupPosition();
        _list.StartRefreshTimer();
    }

    private void YouTubeMiniPlayerListOwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        if (YouTubeMiniPlayerClosedHistoryPopup is { } history) history.IsOpen = false;
        if (!_host.IsArrangementEditMode) _host.CancelArrangementPreview(showStatus: false);
    }

    private void YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isDisposed) return;
        if (!IsFocusWithinPopup(YouTubeMiniPlayerItemMorePopup, e.NewFocus)
            && !IsFocusWithinElement(YouTubeMiniPlayerItemMorePopup.PlacementTarget, e.NewFocus))
            YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        // WebView2等へ入力先が移る場合も、MouseDownの到達だけに依存しない。
        var withinArrange = IsFocusWithinPopup(YouTubeMiniPlayerArrangePopup, e.NewFocus)
            || IsFocusWithinPopup(GetArrangeDisplayPopup(), e.NewFocus)
            || IsFocusWithinPopup(YouTubeMiniPlayerLinearPlacementPopup, e.NewFocus)
            || IsFocusWithinElement(_elements.YouTubeMiniPlayerArrangeMenuButton, e.NewFocus);
        if (!withinArrange)
        {
            YouTubeMiniPlayerArrangePopup.IsOpen = false;
            YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        }
        if (!IsFocusWithinPopup(YouTubeMiniPlayerGlobalActionsPopup, e.NewFocus)
            && !IsFocusWithinElement(_elements.YouTubeMiniPlayerGlobalActionsButton, e.NewFocus))
        {
            YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        }
        if (!IsFocusWithinPopup(YouTubeMiniPlayerClosedHistoryPopup, e.NewFocus)
            && !IsFocusWithinElement(_elements.YouTubeMiniPlayerClosedHistoryButton, e.NewFocus)
            && YouTubeMiniPlayerClosedHistoryPopup is { } history) history.IsOpen = false;
    }

    private static bool IsFocusWithinPopup(Popup? popup, IInputElement? focus)
        => popup?.IsOpen == true && IsFocusWithinElement(popup.Child, focus);

    private static bool IsFocusWithinElement(DependencyObject? root, IInputElement? focus)
    {
        for (var current = focus as DependencyObject; current is not null;)
        {
            if (ReferenceEquals(current, root)) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    public async Task YouTubeMiniPlayerListPopup_Closed(object sender, EventArgs e)
    {
        if (_isDisposed) return;
        var generation = ++_presentationGeneration;
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
        if (YouTubeMiniPlayerClosedHistoryPopup is { } history) history.IsOpen = false;
        _list.StopRefreshTimer();
        _list.CancelYouTubeMiniPlayerThumbnailLoads();
        _host.CancelPendingHoverPreview();
        await _host.StopHoverPreviewAsync();
        if (_isDisposed || generation != _presentationGeneration || ListWindow.IsOpen) return;
        _list.ClearYouTubeMiniPlayerListWindowHighlights();
    }

    public async Task ToggleYouTubeMiniPlayerListCollapsedAsync()
    {
        if (_isDisposed || !ListWindow.IsOpen || ListWindow.IsManipulating) return;
        CloseAuxiliaryPopups();
        _host.CancelArrangementPreview(showStatus: false);
        _list.ResetYouTubeMiniPlayerListDragState();
        _list.ClearYouTubeMiniPlayerListDropCue();
        _host.CancelPendingHoverPreview();
        ListWindow.ToggleCollapsed();
        QueueYouTubeMiniPlayerListPopupPositionClamp();
        // 表示の切替を先に行い、復元完了後に古い要求で再度開閉しない。
        await _host.StopHoverPreviewAsync();
    }

    public void CloseAuxiliaryPopups()
    {
        ListWindow.CloseCaptionContextMenu();
        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        YouTubeMiniPlayerVolumePopup.IsOpen = false;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        if (YouTubeMiniPlayerClosedHistoryPopup is { } history) history.IsOpen = false;
    }

    public void YouTubeMiniPlayerListPopupRoot_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (sender is ListBox or ListBoxItem) _contentHeightFitRequested = true;
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
        if (_isDisposed || !ListWindow.IsOpen || ListWindow.IsManipulating)
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
                var fitContentHeight = _contentHeightFitRequested;
                _contentHeightFitRequested = false;
                if (!_isDisposed)
                {
                    ClampYouTubeMiniPlayerListPopupPosition();
                    if (fitContentHeight)
                    {
                        ListWindow.FitToContentHeight();
                        ClampYouTubeMiniPlayerListPopupPosition();
                    }
                }
            },
            DispatcherPriority.ContextIdle);
    }

    public void ClampYouTubeMiniPlayerListPopupPosition(Point? preferredDevicePoint = null)
    {
        if (_isDisposed || !ListWindow.IsOpen || ListWindow.IsManipulating || ListWindow.WindowState != WindowState.Normal) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(ListWindow);
            var origin = ListWindow.PointToScreen(new Point());
            var probe = new Point(origin.X + ListWindow.ActualWidth * dpi.DpiScaleX / 2,
                origin.Y + ListWindow.ActualHeight * dpi.DpiScaleY / 2);
            var workArea = _host.GetWorkAreaForDevicePoint(preferredDevicePoint ?? probe);
            var availableWidth = Math.Max(1, workArea.Width - 16);
            var availableHeight = Math.Max(1, workArea.Height - 16);
            ListWindow.MinWidth = Math.Min(780, availableWidth);
            ListWindow.MinHeight = Math.Min(ListWindow.MinimumContentHeight, availableHeight);
            ListWindow.MaxHeight = Math.Max(ListWindow.MinHeight, availableHeight);
            ListWindow.MaxWidth = Math.Max(ListWindow.MinWidth, availableWidth);
            if (ListWindow.Width > ListWindow.MaxWidth) ListWindow.Width = ListWindow.MaxWidth;
            if (ListWindow.Height > ListWindow.MaxHeight) ListWindow.Height = ListWindow.MaxHeight;
            var bounds = new Rect(origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY,
                ListWindow.ActualWidth, ListWindow.ActualHeight);
            var adjustment = CalculateYouTubeMiniPlayerListPopupClampAdjustment(workArea, bounds, 8);
            if (Math.Abs(adjustment.X) >= 0.1) ListWindow.Left += adjustment.X;
            if (Math.Abs(adjustment.Y) >= 0.1) ListWindow.Top += adjustment.Y;
            if (YouTubeMiniPlayerArrangePopup.IsOpen) UpdateYouTubeMiniPlayerArrangePopupPlacement();
            if (YouTubeMiniPlayerGlobalActionsPopup.IsOpen)
            {
                UpdateYouTubeMiniPlayerGlobalActionsPopupSize();
                UpdateYouTubeMiniPlayerGlobalActionsPopupPlacement();
            }
        }
        catch (InvalidOperationException)
        {
            // HWNDと画面座標が確定してから再計算する。
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

    internal static double CalculateYouTubeMiniPlayerListPopupMaxHeight(
        double workAreaHeight,
        double ownerDpiScale,
        double popupDpiScale,
        double edgeMargin)
        => workAreaHeight <= 0 || ownerDpiScale <= 0 || popupDpiScale <= 0
            ? 680
            : Math.Max(0, (workAreaHeight - (2 * edgeMargin))
                * ownerDpiScale / popupDpiScale);

    internal static Point CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
        Rect popupDeviceBounds,
        Point? preferredDevicePoint)
        => preferredDevicePoint ?? new Point(
            popupDeviceBounds.Left + (popupDeviceBounds.Width / 2),
            popupDeviceBounds.Top + (popupDeviceBounds.Height / 2));

    public void UpdateYouTubeMiniPlayerGlobalActionsPopupSize()
    {
        if (YouTubeMiniPlayerGlobalActionsPopup.Child is not FrameworkElement root) return;
        try
        {
            var ownerDpi = VisualTreeHelper.GetDpi(_owner);
            var popupDpi = VisualTreeHelper.GetDpi(root);
            var origin = root.PointToScreen(new Point());
            var workArea = _host.GetWorkAreaForDevicePoint(origin);
            root.MaxWidth = CalculateYouTubeMiniPlayerListPopupMaxHeight(
                workArea.Width, ownerDpi.DpiScaleX, popupDpi.DpiScaleX, YouTubeMiniPlayerListPopupEdgeMargin);
            root.MaxHeight = CalculateYouTubeMiniPlayerListPopupMaxHeight(
                workArea.Height, ownerDpi.DpiScaleY, popupDpi.DpiScaleY, YouTubeMiniPlayerListPopupEdgeMargin);
        }
        catch (InvalidOperationException)
        {
            // 画面座標が確定するまでは、XAMLの幅・高さ上限を使う。
        }
    }

    public void UpdateYouTubeMiniPlayerArrangePopupPlacement()
        => UpdateYouTubeMiniPlayerSidePopupPlacement(YouTubeMiniPlayerArrangePopup, YouTubeMiniPlayerArrangePopupWidth);

    public void UpdateYouTubeMiniPlayerGlobalActionsPopupPlacement()
        => UpdateYouTubeMiniPlayerSidePopupPlacement(YouTubeMiniPlayerGlobalActionsPopup, YouTubeMiniPlayerGlobalActionsPopupWidth);

    public void UpdateClosedHistoryPopupPlacement()
    {
        if (YouTubeMiniPlayerClosedHistoryPopup is not { PlacementTarget: FrameworkElement target, Child: FrameworkElement root } popup) return;
        try
        {
            var ownerDpi = VisualTreeHelper.GetDpi(_owner);
            var popupDpi = VisualTreeHelper.GetDpi(root);
            var workArea = _host.GetWorkAreaForDevicePoint(target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2)));
            root.MaxWidth = Math.Min(360, CalculateYouTubeMiniPlayerListPopupMaxHeight(
                workArea.Width, ownerDpi.DpiScaleX, popupDpi.DpiScaleX, YouTubeMiniPlayerListPopupEdgeMargin));
            root.MaxHeight = Math.Min(420, CalculateYouTubeMiniPlayerListPopupMaxHeight(
                workArea.Height, ownerDpi.DpiScaleY, popupDpi.DpiScaleY, YouTubeMiniPlayerListPopupEdgeMargin));
            popup.HorizontalOffset = target.ActualWidth - Math.Min(root.Width, root.MaxWidth);
        }
        catch (InvalidOperationException)
        {
            // HWND未確定時はXAMLの上限を使い、開いた後に表示先画面へ合わせる。
        }
    }

    private void UpdateYouTubeMiniPlayerSidePopupPlacement(Popup popup, double popupWidth)
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
                popupWidth,
                YouTubeMiniPlayerArrangePopupGap,
                YouTubeMiniPlayerListPopupEdgeMargin);

            popup.Placement = placement;
            popup.HorizontalOffset = placement == PlacementMode.Left
                ? -YouTubeMiniPlayerArrangePopupGap
                : YouTubeMiniPlayerArrangePopupGap;
            popup.VerticalOffset = 0;
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
        // 子Popupから親一覧へ届く入力では、項目の実行前に操作対象を解除しない。
        var source = e.OriginalSource as IInputElement;
        if (IsFocusWithinPopup(YouTubeMiniPlayerAbRepeatPopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerSpeedPopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerVolumePopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerItemMorePopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerArrangePopup, source)
            || IsFocusWithinPopup(GetArrangeDisplayPopup(), source)
            || IsFocusWithinPopup(YouTubeMiniPlayerGlobalActionsPopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerClosedHistoryPopup, source)
            || IsFocusWithinPopup(YouTubeMiniPlayerLinearPlacementPopup, source))
        {
            return;
        }

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
        if (_isDisposed || !ListWindow.IsOpen || e.StagingItem.Input is not MouseEventArgs mouseInput)
        {
            return;
        }

        _list.StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft();
        if (mouseInput is not MouseButtonEventArgs mouseArgs
            || mouseArgs.ButtonState != MouseButtonState.Pressed
            || _youTubeMiniPlayerListInteractionCoordinator.IsDuplicatePopupMouseDown(mouseArgs.Timestamp)
            || !_host.TryGetCursorDevicePoint(out var cursorPoint))
        {
            return;
        }

        ProcessMouseDown(mouseArgs.Timestamp, cursorPoint, mouseArgs.ChangedButton);
    }

    private void ProcessMouseDown(int timestamp, Point cursorPoint, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            ProcessLeftMouseDown(timestamp, cursorPoint);
        }
        else
        {
            _youTubeMiniPlayerListInteractionCoordinator.RecordPopupMouseDown(timestamp);
            CloseSidePopupsIfPointerIsOutside(cursorPoint, protectTriggers: false);
        }
    }

    private bool IsWithinArrangePopupOrDropdown(Point cursorPoint)
    {
        var isWithin = IsCursorWithinYouTubeMiniPlayerListPopup(YouTubeMiniPlayerArrangePopup, cursorPoint);
        if (GetArrangeDisplayPopup() is Popup displayPopup)
        {
            isWithin |= IsCursorWithinYouTubeMiniPlayerListPopup(displayPopup, cursorPoint);
        }
        return isWithin;
    }

    private Popup? GetArrangeDisplayPopup()
        => _elements.YouTubeMiniPlayerArrangeDisplayComboBox?.Template?.FindName("PART_Popup",
            _elements.YouTubeMiniPlayerArrangeDisplayComboBox) as Popup;

    private void CloseSidePopupsIfPointerIsOutside(Point cursorPoint, bool protectTriggers = true)
    {
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(YouTubeMiniPlayerItemMorePopup, cursorPoint,
            IsCursorWithinYouTubeMiniPlayerListPopup(YouTubeMiniPlayerItemMorePopup, cursorPoint)
                || (protectTriggers && IsWithinTrigger(YouTubeMiniPlayerItemMorePopup.PlacementTarget as FrameworkElement, cursorPoint)),
            protectPlacementTarget: false);
        var isWithinArrange = IsWithinArrangePopupOrDropdown(cursorPoint);
        var isWithinLinear = IsCursorWithinYouTubeMiniPlayerListPopup(YouTubeMiniPlayerLinearPlacementPopup, cursorPoint);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(YouTubeMiniPlayerArrangePopup, cursorPoint,
            isWithinArrange || isWithinLinear
                || (protectTriggers && IsWithinTrigger(_elements.YouTubeMiniPlayerArrangeMenuButton, cursorPoint)),
            protectPlacementTarget: false);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(YouTubeMiniPlayerGlobalActionsPopup, cursorPoint,
            IsCursorWithinYouTubeMiniPlayerListPopup(YouTubeMiniPlayerGlobalActionsPopup, cursorPoint)
                || (protectTriggers && IsWithinTrigger(_elements.YouTubeMiniPlayerGlobalActionsButton, cursorPoint)),
            protectPlacementTarget: false);
        CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(YouTubeMiniPlayerLinearPlacementPopup, cursorPoint,
            isWithinLinear || isWithinArrange);
        if (YouTubeMiniPlayerClosedHistoryPopup is { } history)
        {
            CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(history, cursorPoint,
                IsCursorWithinYouTubeMiniPlayerListPopup(history, cursorPoint)
                    || (protectTriggers && IsWithinTrigger(_elements.YouTubeMiniPlayerClosedHistoryButton, cursorPoint)),
                protectPlacementTarget: false);
        }
    }

    private bool IsWithinTrigger(FrameworkElement? trigger, Point cursorPoint)
        => trigger?.IsEnabled == true && _hitTest(trigger, cursorPoint);

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
        var isWithinListPopup = ListWindow.IsOpen && _hitTest(YouTubeMiniPlayerListPopupRoot, cursorPoint);

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
        CloseSidePopupsIfPointerIsOutside(cursorPoint);

    }

    private void CloseYouTubeMiniPlayerListPopupIfPointerIsOutside(
        Popup popup,
        Point cursorPoint,
        bool isWithinPopup,
        bool protectPlacementTarget = true)
    {
        if (popup.IsOpen
            && !isWithinPopup
            && (!protectPlacementTarget || !IsCursorWithinYouTubeMiniPlayerListPopupPlacementTarget(popup, cursorPoint)))
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
