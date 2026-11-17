using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal partial class YouTubeMiniPlayerListWindow : Window, IDisposable
{
    private static readonly DependencyPropertyKey IsCollapsedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCollapsed), typeof(bool), typeof(YouTubeMiniPlayerListWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCollapsedProperty = IsCollapsedPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey OperationStatusPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(OperationStatus), typeof(string), typeof(YouTubeMiniPlayerListWindow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty OperationStatusProperty = OperationStatusPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey InformationTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(InformationText), typeof(string), typeof(YouTubeMiniPlayerListWindow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty InformationTextProperty = InformationTextPropertyKey.DependencyProperty;
    private static readonly TimeSpan OperationStatusDisplayDuration = TimeSpan.FromSeconds(5);
    private readonly Func<long> _getTimestamp;
    private long? _operationStatusShownAt;
    private string _activePlayerTitle = string.Empty;

    private HwndSource? _source;
    private bool _disposed;
    private bool _presentationActive;
    private bool _initialSizePending = true;
    private bool _initialPlacementPending = true;
    private bool _contentHeightUpdatePending;
    private Point? _lastInitialDevicePosition;
    private Point? _initialRestoredDevicePosition;
    private Point? _lastNormalDevicePosition;
    private readonly ToolTip _captionHelp;
    private readonly ContextMenu _captionMenu;
    private readonly MenuItem _hoverPreviewMenuItem;
    private bool _captionHoverTracking;
    private FrameworkElement? _collapsibleBody;
    private FrameworkElement? _toolbar;

    public YouTubeMiniPlayerListWindow() : this(Stopwatch.GetTimestamp)
    {
    }

    internal YouTubeMiniPlayerListWindow(Func<long> getTimestamp)
    {
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        InitializeComponent();
        _captionHelp = (ToolTip)Resources["CaptionHelp"];
        _captionMenu = (ContextMenu)Resources["CaptionContextMenu"];
        _hoverPreviewMenuItem = (MenuItem)Resources["CaptionHoverPreviewMenuItem"];
        _captionHelp.PlacementTarget = this;
        Title += ApplicationChannelService.WindowTitleSuffix;
        IsVisibleChanged += (_, _) => NotifyPresentationChanged();
        // 移動中はメモリだけ更新する。保存は既存の移動完了通知で予約する。
        LocationChanged += (_, _) => CapturePosition();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                return;
            }
            NotifyPresentationChanged();
        };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessage);
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _disposed = true;
            HideCaptionHelp();
            CloseCaptionContextMenu();
            _captionMenu.PlacementTarget = null;
            CollapseRequested = null;
            HoverPreviewToggleRequested = null;
            _operationStatusShownAt = null;
            SetValue(OperationStatusPropertyKey, string.Empty);
            _activePlayerTitle = string.Empty;
            SetValue(InformationTextPropertyKey, string.Empty);
            if (_source?.IsDisposed == false) _source.RemoveHook(WindowMessage);
            _source = null;
            NotifyPresentationChanged();
            ListContentHost.Content = null;
            _collapsibleBody = null;
            _toolbar = null;
        };
    }

    internal event EventHandler? PresentationChanged;
    internal event EventHandler? InteractionStarted;
    internal event EventHandler? InteractionCompleted;
    internal event EventHandler? CollapseRequested;
    internal event EventHandler? HoverPreviewToggleRequested;
    internal bool IsManipulating { get; private set; }
    public bool IsCollapsed => (bool)GetValue(IsCollapsedProperty);
    public string OperationStatus => (string)GetValue(OperationStatusProperty);
    public string InformationText => (string)GetValue(InformationTextProperty);
    internal double MinimumContentHeight => IsCollapsed ? 48 : 180;
    internal FrameworkElement? InitialPlacementTarget { get; init; }

    internal bool IsOpen
    {
        get => !_disposed && IsVisible && WindowState != WindowState.Minimized;
        set
        {
            if (value) ShowOrActivate();
            else if (!_disposed) Hide();
        }
    }

    internal void AttachContent(FrameworkElement content, ResourceDictionary resources)
    {
        if (ListContentHost.Content is not null) throw new InvalidOperationException("一覧の表示内容は設定済みです。");
        // UI名と既存イベントは維持し、表示先だけをMainWindowから切り離す。
        var scope = new NameScope();
        NameScope.SetNameScope(content, scope);
        RegisterNames(content, scope);
        Resources.MergedDictionaries.Add(resources);
        ListContentHost.Content = content;
        // 移し替え前のMainWindowではなく、一覧自身の設定を見出しへ表示する。
        if (scope.FindName("YouTubeMiniPlayerListTopmostButton") is FrameworkElement topmostButton)
            topmostButton.DataContext = this;
        if (scope.FindName("YouTubeMiniPlayerListOperationStatusBar") is FrameworkElement statusBar)
            statusBar.DataContext = this;
        _collapsibleBody = scope.FindName("YouTubeMiniPlayerListBody") as FrameworkElement;
        _toolbar = scope.FindName("YouTubeMiniPlayerListToolbar") as FrameworkElement;
    }

    private static void RegisterNames(DependencyObject element, INameScope scope)
    {
        if (element is FrameworkElement { Name.Length: > 0 } named) scope.RegisterName(named.Name, named);
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) RegisterNames(child, scope);
    }

    internal void ShowOrActivate()
    {
        if (_disposed) return;
        ClearExpiredOperationStatus();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible)
        {
            PlaceInitiallyAtTarget();
            Show();
            CapturePosition();
        }
        // 非アクティブ表示の指定時は、表示元からフォーカスを移さない。
        if (ShowActivated) Activate();
    }

    internal void ToggleCollapsed()
    {
        if (_disposed || !IsOpen || IsManipulating || _collapsibleBody is null || _toolbar is null) return;
        HideCaptionHelp();
        CloseCaptionContextMenu();
        ApplyCollapsed(!IsCollapsed);
    }

    internal void RestoreCollapsed(bool isCollapsed)
        => ApplyCollapsed(isCollapsed);

    private void ApplyCollapsed(bool isCollapsed)
    {
        if (_disposed || _collapsibleBody is null || _toolbar is null) return;
        SetValue(IsCollapsedPropertyKey, isCollapsed);
        _collapsibleBody.Visibility = _toolbar.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        MinHeight = Math.Min(MinimumContentHeight, MaxHeight);
        // 起動復元では表示先の画面と位置が確定してから初回の高さを測る。
        if (!IsOpen)
        {
            if (_initialSizePending && isCollapsed) Height = MinimumContentHeight;
            return;
        }
        // 初回更新中の操作でも、折りたたみ後の見出し高をその場で確定する。
        if (_initialSizePending) FitInitialHeight(MaxHeight);
        else FitToContentHeight();
    }

    internal void FitInitialHeight(double maximumHeight)
    {
        if (!_initialSizePending || WindowState != WindowState.Normal) return;
        _initialSizePending = false;
        Height = MeasureContentHeight(maximumHeight);
        _contentHeightUpdatePending = false;
        // 初回の内容高が確定した後、仮の高さによる画面内補正を既定位置へ戻す。
        PlaceInitiallyAtTarget();
        _initialPlacementPending = false;
        _initialRestoredDevicePosition = null;
    }

    internal void ShowOperationStatus(string message)
    {
        if (_disposed) return;
        // 同じ文言でも新しい通知なら期限を延長する。空の結果には期限を持たせない。
        _operationStatusShownAt = string.IsNullOrEmpty(message) ? null : _getTimestamp();
        if (string.Equals(OperationStatus, message, StringComparison.Ordinal)) return;
        SetValue(OperationStatusPropertyKey, message);
        // 同じ表示文字列でも、操作結果とタイトルでは折り返し方が変わる。
        UpdateInformationText(forceFit: true);
    }

    internal void ShowActivePlayerTitle(string title)
    {
        if (_disposed || string.Equals(_activePlayerTitle, title, StringComparison.Ordinal)) return;
        _activePlayerTitle = title;
        UpdateInformationText();
    }

    private void UpdateInformationText(bool forceFit = false)
    {
        var text = string.IsNullOrEmpty(OperationStatus) ? _activePlayerTitle : OperationStatus;
        var changed = !string.Equals(InformationText, text, StringComparison.Ordinal);
        if (changed) SetValue(InformationTextPropertyKey, text);
        if (changed || forceFit) FitToContentHeight();
    }

    internal void ClearExpiredOperationStatus()
    {
        if (_disposed || _operationStatusShownAt is not { } shownAt
            || Stopwatch.GetElapsedTime(shownAt, _getTimestamp()) < OperationStatusDisplayDuration) return;
        ShowOperationStatus(string.Empty);
    }

    internal static void PublishOperationStatus(string message, YouTubeMiniPlayerListWindow source,
        IEnumerable<YouTubeMiniPlayerListWindow> lists)
    {
        if (source._disposed) return;
        // 通知元には非表示中も結果を残し、別ownerの結果は開いている一覧にも届ける。
        foreach (var list in lists.Prepend(source).Distinct())
            if (ReferenceEquals(list, source) || list.IsOpen) list.ShowOperationStatus(message);
    }

    internal void FitToContentHeight()
    {
        _contentHeightUpdatePending = true;
        if (_disposed || !IsOpen || IsManipulating || _initialSizePending) return;
        var height = MeasureContentHeight(MaxHeight);
        _contentHeightUpdatePending = false;
        if (Math.Abs(height - Height) > 0.5) Height = height;
    }

    private double MeasureContentHeight(double maximumHeight)
    {
        if (!double.IsFinite(maximumHeight) || maximumHeight <= 0)
            maximumHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16);
        if (ListContentHost.Content is FrameworkElement view)
        {
            // 行の増減で保留されたレイアウトを確定してから、自然な内容高を測る。
            view.UpdateLayout();
            // 表示可能な上限内で測り、スクロール領域の無制限測定と実表示の往復を防ぐ。
            view.Measure(new Size(Math.Max(MinWidth, Width), maximumHeight));
            // 行高の丸めで最終行が僅かに欠け、不要なスクロールバーが出るのを防ぐ。
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            var height = (Math.Ceiling(view.DesiredSize.Height * scale) + 1) / scale;
            return Math.Clamp(height, Math.Min(MinHeight, maximumHeight), maximumHeight);
        }
        return Height;
    }

    internal static Point CalculateInitialDevicePosition(Point targetBottomLeft, DpiScale dpi)
        => new(targetBottomLeft.X - 696 * dpi.DpiScaleX, targetBottomLeft.Y + 4 * dpi.DpiScaleY);

    private void PlaceInitiallyAtTarget()
    {
        if (_initialRestoredDevicePosition is { } restored)
        {
            RememberInitialPosition();
            PlaceAtDevicePosition(restored);
        }
        else if (_initialPlacementPending) PlaceAtInitialPosition();
    }

    internal void RestorePosition(YouTubeMiniPlayerListPositionState position)
    {
        static bool IsCoordinate(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;
        if (_disposed || !IsCoordinate(position.Left) || !IsCoordinate(position.Top)) return;
        _initialRestoredDevicePosition = _lastNormalDevicePosition = new Point(position.Left, position.Top);
        _initialPlacementPending = false;
    }

    internal YouTubeMiniPlayerListPositionState? CapturePosition()
    {
        if (_initialRestoredDevicePosition is { } restored)
            return new() { Left = restored.X, Top = restored.Y };
        if (!_disposed && IsVisible && WindowState == WindowState.Normal
            && new WindowInteropHelper(this).Handle != IntPtr.Zero)
            _lastNormalDevicePosition = PointToScreen(new Point());
        // 最小化中の退避座標を保存せず、最後の通常表示位置を保持する。
        return _lastNormalDevicePosition is { } point ? new() { Left = point.X, Top = point.Y } : null;
    }

    private void PlaceAtInitialPosition()
    {
        RememberInitialPosition();
        if (_lastInitialDevicePosition is { } point) PlaceAtDevicePosition(point);
    }

    private void RememberInitialPosition()
    {
        if (InitialPlacementTarget is { IsVisible: true } target
            && Window.GetWindow(target) is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            try
            {
                _lastInitialDevicePosition = CalculateInitialDevicePosition(
                    target.PointToScreen(new Point(0, target.ActualHeight)), VisualTreeHelper.GetDpi(target));
            }
            catch (InvalidOperationException)
            {
                // 起点が未接続・非表示の場合は、最後に取得できた有効な位置を使う。
            }
        }
    }

    private void PlaceAtDevicePosition(Point point)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // 物理座標へ一度に移し、移動先のDPIはWPFへ反映させる。
        _ = SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0, 0,
            0x0001 | 0x0004 | 0x0010 | 0x0200); // NoSize / NoZOrder / NoActivate / NoOwnerZOrder
    }

    private void ResetToInitialPosition()
    {
        if (_disposed || !IsOpen) return;
        CloseCaptionContextMenu();
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        _initialRestoredDevicePosition = null;
        _initialPlacementPending = _initialSizePending;
        PlaceAtInitialPosition();
        WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(this);
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal void CloseCaptionContextMenu() => _captionMenu.IsOpen = false;

    internal void UpdateHoverPreviewMenu(bool enabled, bool canToggle)
    {
        _hoverPreviewMenuItem.Header = enabled ? "一時視聴：オン" : "一時視聴：オフ";
        _hoverPreviewMenuItem.ToolTip = enabled
            ? "マウスを重ねた動画だけを一時視聴します。クリックでオフにして元の再生・ミュート状態へ戻します"
            : "一時視聴はオフです。クリックでオンにすると、マウスを重ねた動画だけを一時視聴します";
        System.Windows.Automation.AutomationProperties.SetName(_hoverPreviewMenuItem, enabled ? "一時視聴をオフにする" : "一時視聴をオンにする");
        _hoverPreviewMenuItem.IsEnabled = canToggle && !_disposed;
    }

    private void CaptionHoverPreview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_disposed || !IsOpen || !_hoverPreviewMenuItem.IsEnabled) return;
        CloseCaptionContextMenu();
        HoverPreviewToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CaptionResetPosition_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ResetToInitialPosition();
    }

    private void ShowCaptionContextMenu(IntPtr coordinates)
    {
        if (_disposed || !IsOpen || IsManipulating) return;
        HideCaptionHelp();
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        var packed = coordinates.ToInt64();
        var point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
        _captionMenu.PlacementTarget = this;
        _captionMenu.Placement = PlacementMode.RelativePoint;
        _captionMenu.HorizontalOffset = point.X;
        _captionMenu.VerticalOffset = point.Y;
        _captionMenu.IsOpen = true;
    }

    private void NotifyPresentationChanged()
    {
        var active = IsOpen;
        if (!active)
        {
            HideCaptionHelp();
            CloseCaptionContextMenu();
        }
        if (_presentationActive == active) return;
        _presentationActive = active;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed) return;
        // 通常の×とAlt+F4は再表示可能な非表示。所有元の完全終了だけ破棄する。
        e.Cancel = true;
        Hide();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        HideCaptionHelp();
        CloseCaptionContextMenu();
        base.OnDeactivated(e);
    }

    private void TrackCaptionHover()
    {
        if (_captionHoverTracking || _source?.IsDisposed != false || _captionMenu.IsOpen) return;
        var tracking = new CaptionMouseTracking
        {
            Size = (uint)Marshal.SizeOf<CaptionMouseTracking>(),
            Flags = 0x0013, // TME_HOVER / TME_LEAVE / TME_NONCLIENT
            Window = _source.Handle,
            HoverTime = uint.MaxValue // システム標準のホバー待機時間
        };
        _captionHoverTracking = TrackMouseEvent(ref tracking);
    }

    private void ShowCaptionHelp(IntPtr coordinates)
    {
        if (!_captionHoverTracking || !IsOpen || IsManipulating || _captionMenu.IsOpen) return;
        // 非クライアント領域の物理座標を、この一覧のDPIに合わせた相対位置へ戻す。
        var packed = coordinates.ToInt64();
        var point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
        _captionHelp.HorizontalOffset = point.X + 12;
        _captionHelp.VerticalOffset = point.Y + 20;
        _captionHelp.IsOpen = true;
    }

    private void HideCaptionHelp()
    {
        if (_captionHelp.IsOpen) _captionHelp.IsOpen = false;
        if (!_captionHoverTracking) return;
        if (_source?.IsDisposed == false)
        {
            var tracking = new CaptionMouseTracking
            {
                Size = (uint)Marshal.SizeOf<CaptionMouseTracking>(),
                Flags = 0x80000013, // TME_CANCEL / TME_HOVER / TME_LEAVE / TME_NONCLIENT
                Window = _source.Handle
            };
            _ = TrackMouseEvent(ref tracking);
        }
        _captionHoverTracking = false;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x00a5 && wParam.ToInt64() == 2) // WM_NCRBUTTONUP / HTCAPTION
        {
            // WindowChrome標準のシステムメニューより先に、一覧専用のメニューを表示する。
            handled = true;
            ShowCaptionContextMenu(lParam);
            return IntPtr.Zero;
        }
        if (message == 0x0020 && wParam == hwnd && (lParam.ToInt64() & 0xffff) == 2 && IsOpen) // WM_SETCURSOR / HTCAPTION
        {
            // タイトルバーの移動可能な部分だけを変更し、ボタン等は通常のカーソルに任せる。
            handled = Mouse.SetCursor(Cursors.SizeAll);
            if (handled) return new IntPtr(1);
        }
        if (message == 0x00a0) // WM_NCMOUSEMOVE
        {
            if (wParam.ToInt64() == 2 && IsOpen && !IsManipulating) TrackCaptionHover(); // HTCAPTION
            else HideCaptionHelp();
        }
        else if (message == 0x02a0 && wParam.ToInt64() == 2) // WM_NCMOUSEHOVER / HTCAPTION
        {
            ShowCaptionHelp(lParam);
        }
        else if (message is 0x02a2 or 0x0200 or 0x00a1 or 0x00a3 or 0x00a4 or 0x00a7
            or 0x0201 or 0x0204 or 0x0207 or 0x0100 or 0x0104) // 離脱・マウス操作・キー入力
        {
            HideCaptionHelp();
        }
        if (message == 0x00a3 && wParam.ToInt64() == 2) // WM_NCLBUTTONDBLCLK / HTCAPTION
        {
            handled = true;
            if (!_disposed && IsOpen && !IsManipulating) CollapseRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (message == 0x0112 && (wParam.ToInt64() & 0xfff0) is 0xf000 or 0xf030) // SC_SIZE / SC_MAXIMIZE
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message == 0x0231) // WM_ENTERSIZEMOVE
        {
            HideCaptionHelp();
            CloseCaptionContextMenu();
            IsManipulating = true;
            _initialSizePending = false;
            _initialPlacementPending = false;
            _initialRestoredDevicePosition = null;
            InteractionStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (message == 0x0232) // WM_EXITSIZEMOVE
        {
            IsManipulating = false;
            if (_contentHeightUpdatePending) FitToContentHeight();
            WindowPlacementVisibilityService.EnsureVisibleOnNearestWorkArea(this);
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionMouseTracking
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref CaptionMouseTracking tracking);
}
