using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TabBrowser.Models;

namespace TabBrowser.Services;

// 保存モデル・再生APIから独立した、プロセス内だけの一時表示状態。
internal sealed class YouTubeMiniPlayerTransparencyController
{
    internal static YouTubeMiniPlayerTransparencyController Shared { get; } = new();
    private readonly Dictionary<YouTubeMiniPlayerRuntime, IDisposable?> _targets = [];
    private readonly Func<bool> _buttonsDown;
    private readonly Func<YouTubeMiniPlayerRuntime, bool> _underCursor;
    private readonly Func<Window, IDisposable> _apply;
    private readonly Func<bool> _f8Down;
    private readonly Func<bool> _leftDown;
    private readonly Func<Point, IntPtr> _windowAtPoint;
    private readonly Func<Func<int, int, int, bool, bool>, IDisposable>? _mouseHookFactory;
    private IDisposable? _mouseHook;
    private bool _updatingMouseHook;
    private bool _keyDown;
    private bool _toggleOffOnRelease;
    private bool _grabUsed;
    private bool _ownsLeftPress;
    private bool _observedF8Down;
    private bool _observedLeftDown;
    private YouTubeMiniPlayerRuntime? _grabTarget;
    private NativePoint _grabPoint;
    private bool _pendingOff;
    private bool _releaseQueued;
    private bool _watchingSession;
    private Dispatcher? _dispatcher;
    private string? _failure;

    internal YouTubeMiniPlayerTransparencyController(Func<bool>? buttonsDown = null,
        Func<YouTubeMiniPlayerRuntime, bool>? underCursor = null, Func<Window, IDisposable>? apply = null,
        Func<bool>? f8Down = null, Func<bool>? leftDown = null, Func<Point, IntPtr>? windowAtPoint = null,
        Func<Func<int, int, int, bool, bool>, IDisposable>? mouseHookFactory = null)
    {
        _buttonsDown = buttonsDown ?? AnyMouseButtonDown;
        _underCursor = underCursor ?? IsUnderCursor;
        _apply = apply ?? (window => new NativeTransparency(window));
        _f8Down = f8Down ?? (() => (GetAsyncKeyState(0x77) & 0x8000) != 0);
        _leftDown = leftDown ?? (() => (GetAsyncKeyState(1) & 0x8000) != 0);
        _windowAtPoint = windowAtPoint ?? (point => WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
        _mouseHookFactory = mouseHookFactory;
    }

    internal bool IsEnabled { get; private set; }
    internal bool WantsMouseInput => _ownsLeftPress || IsEnabled && (_keyDown || _pendingOff);
    private bool IsMoveMode => IsEnabled && _keyDown && !_pendingOff;
    internal int TargetCount => _targets.Count;
    internal string? TakeFailure() { var message = _failure; _failure = null; return message; }

    internal IDisposable Register(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.State.SourceKind != MiniPlayerSourceKind.YouTube) return new Registration(() => { });
        if (_targets.ContainsKey(runtime)) throw new InvalidOperationException("透過対象は登録済みです");
        _dispatcher ??= runtime.TaskbarWindow.Dispatcher;
        _targets.Add(runtime, null);
        EventHandler changed = (_, _) => Refresh(runtime);
        DependencyPropertyChangedEventHandler visible = (_, _) => Refresh(runtime);
        EventHandler? closed = null;
        void Unregister()
        {
            if (!_targets.ContainsKey(runtime)) return;
            TryRestore(runtime);
            _targets.Remove(runtime);
            runtime.TaskbarWindow.SourceInitialized -= changed;
            runtime.TaskbarWindow.StateChanged -= changed;
            runtime.TaskbarWindow.IsVisibleChanged -= visible;
            runtime.TaskbarWindow.Closed -= closed;
            if (_targets.Count == 0)
            {
                Disable();
                _releaseQueued = false;
                if (_watchingSession) SystemEvents.SessionSwitch -= OnSessionSwitch;
                _watchingSession = false;
                _dispatcher = null;
            }
        }
        closed = (_, _) => Unregister();
        runtime.TaskbarWindow.SourceInitialized += changed;
        runtime.TaskbarWindow.StateChanged += changed;
        runtime.TaskbarWindow.IsVisibleChanged += visible;
        runtime.TaskbarWindow.Closed += closed;
        if (ReferenceEquals(this, Shared) && !_watchingSession)
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _watchingSession = true;
        }
        Refresh(runtime);
        return new Registration(Unregister);
    }

    internal bool HandleKey(int virtualKey, bool down, bool modifiers, bool injected = false, bool? wasDown = null)
    {
        try { return HandleKeyCore(virtualKey, down, modifiers, injected, wasDown); }
        finally { RefreshMouseHook(); }
    }

    private bool HandleKeyCore(int virtualKey, bool down, bool modifiers, bool injected, bool? wasDown)
    {
        if (virtualKey != 0x77 || injected) return false; // F8だけを扱う。
        if (!down)
        {
            var consumed = _keyDown;
            _keyDown = false;
            StopGrab();
            if (consumed && _toggleOffOnRelease && !_grabUsed)
            {
                if (_buttonsDown()) _pendingOff = true;
                else Disable();
            }
            _toggleOffOnRelease = false;
            RefreshMoveModes();
            return consumed;
        }
        if (_keyDown && (wasDown != false || !_observedF8Down || _grabUsed))
        {
            _observedF8Down |= _f8Down();
            return true;
        }
        // LL hook時点のOS状態は今回の押下をまだ含まない。解放通知を失った後の新しい押下は回復させる。
        _keyDown = false;
        StopGrab();
        _toggleOffOnRelease = false;
        _grabUsed = false;
        _observedF8Down = _f8Down();
        if (!IsEnabled && (modifiers || _buttonsDown()
            || _targets.Keys.Any(YouTubeMiniPlayerOverlayVisibilityService.IsDragging)
            || !_targets.Keys.Any(runtime => IsEligible(runtime) && _underCursor(runtime)))) return false;
        _keyDown = true;
        if (IsEnabled)
        {
            if (_buttonsDown()) _pendingOff = true;
            else _toggleOffOnRelease = true;
        }
        else
        {
            IsEnabled = true;
        }
        foreach (var runtime in _targets.Keys.ToArray()) Refresh(runtime);
        return true;
    }

    // F8中は入力を受ける移動面を使い、左入力の対象を固定してキャプチャする。
    // マウス移動はOSへ通し、カーソルの移動や他のhookを止めない。
    internal bool HandleMouse(int message, int x, int y, bool injected = false)
    {
        try { return HandleMouseCore(message, x, y, injected); }
        finally { RefreshMouseHook(); }
    }

    private bool HandleMouseCore(int message, int x, int y, bool injected)
    {
        if (injected) return false;
        if (message == 0x0201) // WM_LBUTTONDOWN
        {
            StopGrab();
            _ownsLeftPress = false; // 解放通知が欠けても、新しい押下へ古い所有権を持ち越さない。
            if (!IsMoveMode) return false;
            var point = new NativePoint { X = x, Y = y };
            _grabTarget = FindGrabTarget(point);
            if (_grabTarget is null) return false;
            _grabPoint = point;
            _observedF8Down |= _f8Down();
            _observedLeftDown = false;
            _grabUsed = _ownsLeftPress = true;
            if (_targets[_grabTarget] is NativeTransparency native) native.BeginCapture();
            return true;
        }
        if (message == 0x0202) // WM_LBUTTONUP
        {
            var consumed = _ownsLeftPress;
            StopGrab();
            _ownsLeftPress = false;
            return consumed;
        }
        if (message != 0x0200 || _grabTarget is not { } target) return false;
        var f8Down = _f8Down();
        var leftDown = _leftDown();
        // hookで消費した入力はOSの非同期状態へ反映されないことがある。
        // 押下を一度観測できた場合だけ、状態取得を解放通知欠落時の補助に使う。
        if (!_keyDown || _observedF8Down && !f8Down || _observedLeftDown && !leftDown
            || !IsEnabled || !IsEligible(target))
        {
            StopGrab();
            if (_observedLeftDown && !leftDown) _ownsLeftPress = false;
            return false;
        }
        _observedF8Down |= f8Down;
        _observedLeftDown |= leftDown;
        if (x == _grabPoint.X && y == _grabPoint.Y) return false;
        var handle = new WindowInteropHelper(target.TaskbarWindow).Handle;
        if (!GetWindowRect(handle, out var bounds)
            || !SetWindowPos(handle, IntPtr.Zero, bounds.Left + x - _grabPoint.X,
                bounds.Top + y - _grabPoint.Y, 0, 0, 0x15)) // NOSIZE | NOZORDER | NOACTIVATE
        {
            StopGrab();
            _failure = "プレイヤーを移動できませんでした。F8を離してからやり直してください。";
        }
        _grabPoint = new NativePoint { X = x, Y = y };
        return false;
    }

    private bool ReceiveMouse(int message, int x, int y, bool injected)
    {
        if (injected) return false;
        if (message is 0x0202 or 0x0205 or 0x0208 or 0x020C) QueueMouseReleased();
        return message is 0x0200 or 0x0201 or 0x0202 && HandleMouse(message, x, y);
    }

    private void RefreshMouseHook()
    {
        // 本体のActivated/Deactivatedに連動する右ホイール用hookには依存しない。
        if (_updatingMouseHook || _mouseHookFactory is null && !ReferenceEquals(this, Shared)) return;
        _updatingMouseHook = true;
        try
        {
            if (WantsMouseInput)
                _mouseHook ??= _mouseHookFactory is { } create ? create(ReceiveMouse) : new NativeMouseHook(ReceiveMouse);
            else
            {
                var lease = _mouseHook;
                _mouseHook = null;
                lease?.Dispose();
            }
        }
        catch (Win32Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MiniPlayer mouse hook: native error {ex.NativeErrorCode}");
            _failure = "移動用のマウス監視を開始できませんでした。通常表示へ戻します。";
            Disable();
        }
        finally { _updatingMouseHook = false; }
    }

    private YouTubeMiniPlayerRuntime? FindGrabTarget(NativePoint point)
    {
        // OSが実際に選んだ移動面だけを使い、背後のWindowからZ順を推測しない。
        var handle = GetAncestor(_windowAtPoint(new Point(point.X, point.Y)), 2);
        if (handle == IntPtr.Zero) return null;
        foreach (var (runtime, lease) in _targets)
        {
            if (runtime.IsInputTransparent && IsEligible(runtime)
                && (handle == new WindowInteropHelper(runtime.TaskbarWindow).Handle
                    || lease is NativeTransparency native && handle == native.MoveSurfaceHandle)) return runtime;
        }
        return null;
    }

    private void StopGrab()
    {
        var target = _grabTarget;
        _grabTarget = null;
        if (target is not null && _targets.TryGetValue(target, out var lease) && lease is NativeTransparency native)
            native.EndCapture();
    }

    private void RefreshMoveModes()
    {
        foreach (var runtime in _targets.Keys.ToArray()) Refresh(runtime);
    }

    internal void NotifyMouseReleased()
    {
        if (_pendingOff && !_buttonsDown()) Disable();
    }

    internal void QueueMouseReleased()
    {
        if (!_pendingOff || _releaseQueued || _targets.Keys.FirstOrDefault() is not { } target) return;
        _releaseQueued = true;
        // LL hookはボタン状態更新前に呼ばれる。解放をOSへ渡した後に復旧する。
        target.TaskbarWindow.Dispatcher.BeginInvoke(() =>
        {
            _releaseQueued = false;
            NotifyMouseReleased();
        }, DispatcherPriority.Background);
    }

    internal void Refresh(YouTubeMiniPlayerRuntime runtime)
    {
        if (!_targets.ContainsKey(runtime)) return;
        if (!IsEnabled || !IsEligible(runtime)) { TryRestore(runtime); return; }
        try
        {
            if (_targets[runtime] is NativeTransparency existing) { existing.SetMoveMode(IsMoveMode); return; }
            if (_targets[runtime] is not null) return;
            _targets[runtime] = _apply(runtime.TaskbarWindow);
            if (_targets[runtime] is NativeTransparency native)
            {
                native.CaptureLost = () => { if (ReferenceEquals(_grabTarget, runtime)) StopGrab(); };
                native.SetMoveMode(IsMoveMode);
            }
            runtime.IsInputTransparent = true;
            runtime.Overlay.ToolbarHideTimer?.Stop();
            runtime.Overlay.TitleBandHideTimer?.Stop();
            CloseMenus(runtime.Toolbar);
            CloseMenus(runtime.TitleBand);
            runtime.Overlay.IsToolbarMenuOpen = false;
            runtime.ToolbarPopup.IsOpen = false;
            runtime.TitleBandPopup.IsOpen = false;
            runtime.Toolbar.Visibility = Visibility.Collapsed;
            runtime.TitleBand.Visibility = Visibility.Collapsed;
            YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);
        }
        catch (Win32Exception ex)
        {
            // 部分的な切替を残さない。URLや再生状態を診断へ渡さない。
            System.Diagnostics.Debug.WriteLine($"MiniPlayer transparency: native error {ex.NativeErrorCode}");
            _failure = "透過表示に切り替えられませんでした。通常表示へ戻します。";
            Disable();
        }
    }

    private void Restore(YouTubeMiniPlayerRuntime runtime)
    {
        if (ReferenceEquals(_grabTarget, runtime)) StopGrab();
        if (!_targets.TryGetValue(runtime, out var lease) || lease is null) return;
        lease.Dispose();
        _targets[runtime] = null;
        runtime.IsInputTransparent = false;
        YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);
    }

    private void Disable()
    {
        StopGrab();
        IsEnabled = false;
        _pendingOff = false;
        foreach (var runtime in _targets.Keys.ToArray()) TryRestore(runtime);
        // OS側の復旧に失敗した対象があれば、F8による再試行の入口を残す。
        IsEnabled = _targets.Values.Any(lease => lease is not null);
        RefreshMouseHook();
    }

    private void TryRestore(YouTubeMiniPlayerRuntime runtime)
    {
        try { Restore(runtime); }
        catch (Win32Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MiniPlayer transparency restore: native error {ex.NativeErrorCode}");
            _failure = "通常表示への復旧に失敗しました。F8でもう一度解除してください。";
        }
    }

    internal void Reset() { Disable(); _keyDown = false; _toggleOffOnRelease = false; _releaseQueued = false; RefreshMouseHook(); }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is { HasShutdownStarted: false }) dispatcher.BeginInvoke(Reset);
    }

    private static bool IsEligible(YouTubeMiniPlayerRuntime runtime) =>
        runtime.State.SourceKind == MiniPlayerSourceKind.YouTube && !runtime.IsDisposed
        && !runtime.IsClosingTaskbarWindow && !runtime.IsFullScreen && !runtime.State.IsMinimized
        && runtime.TaskbarWindow.IsVisible && runtime.TaskbarWindow.WindowState == WindowState.Normal;

    private static void CloseMenus(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: { } menu }) menu.IsOpen = false;
        if (root is System.Windows.Controls.ComboBox combo) combo.IsDropDownOpen = false;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            CloseMenus(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    private static bool IsUnderCursor(YouTubeMiniPlayerRuntime runtime)
    {
        if (!GetCursorPos(out var point)) return false;
        var root = GetAncestor(WindowFromPoint(point), 2);
        bool Matches(IntPtr handle) => handle != IntPtr.Zero && root == handle;
        bool MatchesPopup(Popup popup) => popup.IsOpen && popup.Child is System.Windows.Media.Visual visual
            && PresentationSource.FromVisual(visual) is HwndSource source && Matches(source.Handle);
        return Matches(new WindowInteropHelper(runtime.TaskbarWindow).Handle)
            || MatchesPopup(runtime.TitleBandPopup) || MatchesPopup(runtime.ToolbarPopup) || MatchesPopup(runtime.Popup);
    }

    private static bool AnyMouseButtonDown() => new[] { 1, 2, 4, 5, 6 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private sealed class Registration(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    // 専用hookの寿命はF8操作と消費済みマウス解放まで。本体のフォーカスから独立する。
    internal sealed class NativeMouseHook : IDisposable
    {
        private readonly HookProc _procedure;
        private readonly Func<int, int, int, bool, bool> _receive;
        private IntPtr _handle;
        internal bool IsInstalled => _handle != IntPtr.Zero;

        internal NativeMouseHook(Func<int, int, int, bool, bool> receive)
        {
            _receive = receive;
            _procedure = Callback;
            _handle = SetWindowsHookEx(14, _procedure, GetModuleHandle(null), 0);
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "マウス監視を開始できません");
        }

        private IntPtr Callback(int code, IntPtr word, IntPtr parameter)
        {
            try
            {
                if (code >= 0 && IsInstalled)
                {
                    var message = word.ToInt32();
                    if (message is 0x0200 or 0x0201 or 0x0202 or 0x0205 or 0x0208 or 0x020C)
                    {
                        var data = Marshal.PtrToStructure<MouseData>(parameter);
                        if (_receive(message, data.Point.X, data.Point.Y, (data.Flags & 1) != 0)) return new IntPtr(1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ミニプレイヤーのマウスフック: {ex.GetType().Name}");
            }

            return CallNextHookEx(IntPtr.Zero, code, word, parameter);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData { public NativePoint Point; public int Data; public int Flags; public int Time; public IntPtr Extra; }
        private delegate IntPtr HookProc(int code, IntPtr word, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr word, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    }

    // WPF Opacityは使わず、WebView2の子HWNDを含む本体をWindows側で合成する。
    internal sealed class NativeTransparency : IDisposable
    {
        private const int Mask = 0x80000 | 0x20;
        private readonly IntPtr _handle;
        private readonly int _originalBits;
        private readonly uint _color;
        private readonly byte _alpha;
        private readonly uint _flags;
        private readonly HwndSource _source;
        private readonly Window _window;
        private Window? _moveSurface;
        private HwndSource? _moveSource;
        private bool _moveMode;
        private bool _captured;
        private bool _restored;
        internal Action? CaptureLost { get; set; }
        internal IntPtr MoveSurfaceHandle => _moveSource is { IsDisposed: false } ? _moveSource.Handle : IntPtr.Zero;
        private int ActiveBits => 0x80000 | (_moveMode ? 0 : 0x20);

        internal NativeTransparency(Window window)
        {
            _window = window;
            _handle = new WindowInteropHelper(window).Handle;
            if (_handle == IntPtr.Zero) throw new Win32Exception("プレイヤーの描画先がありません");
            var styles = GetWindowLong(_handle, -20);
            _originalBits = styles & Mask;
            if ((_originalBits & 0x80000) != 0 && !GetLayeredWindowAttributes(_handle, out _color, out _alpha, out _flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "既存の半透明属性を取得できません");
            _source = HwndSource.FromHwnd(_handle) ?? throw new Win32Exception("プレイヤーの描画元がありません");
            _source.AddHook(KeepNativeStyles);
            try
            {
                SetStyles(styles | Mask);
                if (!SetLayeredWindowAttributes(_handle, 0, 64, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "半透明属性を適用できません");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_restored) return;
            EndCapture();
            CaptureLost = null;
            _window.LocationChanged -= SyncMoveSurface;
            _window.SizeChanged -= SyncMoveSurface;
            if (_moveSource is { IsDisposed: false }) _moveSource.RemoveHook(MoveSurfaceHook);
            _moveSurface?.Close();
            _moveSurface = null;
            _moveSource = null;
            if (!_source.IsDisposed) _source.RemoveHook(KeepNativeStyles);
            if (IsWindow(_handle))
            {
                SetStyles((GetWindowLong(_handle, -20) & ~Mask) | _originalBits);
                if ((_originalBits & 0x80000) != 0 && !SetLayeredWindowAttributes(_handle, _color, _alpha, _flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _restored = true;
        }

        internal void SetMoveMode(bool enabled)
        {
            if (_restored || _moveMode == enabled) return;
            var previous = GetWindow(_handle, 3); // GW_HWNDPREV
            if (previous == MoveSurfaceHandle && previous != IntPtr.Zero) previous = GetWindow(previous, 3);
            _moveMode = enabled;
            SetStyles((GetWindowLong(_handle, -20) & ~Mask) | ActiveBits);
            if (!enabled)
            {
                EndCapture();
                _moveSurface?.Hide();
                return;
            }
            if (_moveSurface is null)
            {
                // ほぼ透明な入力面でWebView2の子HWNDを覆う。動画の合成自体は親のalpha64を維持。
                _moveSurface = new Window
                {
                    Owner = _window,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true,
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Focusable = false,
                    Cursor = Cursors.SizeAll,
                    Topmost = _window.Topmost,
                    Left = _window.Left,
                    Top = _window.Top,
                    Width = Math.Max(1, _window.ActualWidth),
                    Height = Math.Max(1, _window.ActualHeight)
                };
                var surfaceHandle = new WindowInteropHelper(_moveSurface).EnsureHandle();
                _moveSource = HwndSource.FromHwnd(surfaceHandle)!;
                _moveSource.AddHook(MoveSurfaceHook);
                _window.LocationChanged += SyncMoveSurface;
                _window.SizeChanged += SyncMoveSurface;
            }
            _moveSurface.Show();
            // 所有WindowのShowが親まで前面へ動かすため、表示前の位置へ両方を戻す。
            if (!SetWindowPos(_handle, previous, 0, 0, 0, 0, 0x213)
                || !SetWindowPos(MoveSurfaceHandle, previous, 0, 0, 0, 0, 0x213))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "移動面の重なり順を維持できません");
            SyncMoveSurface(null, EventArgs.Empty);
        }

        private void SyncMoveSurface(object? sender, EventArgs args)
        {
            if (!_moveMode || MoveSurfaceHandle == IntPtr.Zero) return;
            _moveSurface!.Topmost = _window.Topmost;
            if (!GetWindowRect(_handle, out var rect)
                || !SetWindowPos(MoveSurfaceHandle, IntPtr.Zero, rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, 0x214)) // NOACTIVATE | NOZORDER | NOOWNERZORDER
                throw new Win32Exception(Marshal.GetLastWin32Error(), "移動面を配置できません");
        }

        internal void BeginCapture()
        {
            if (!_moveMode || MoveSurfaceHandle == IntPtr.Zero) return;
            SetCapture(MoveSurfaceHandle);
            _captured = GetCapture() == MoveSurfaceHandle;
        }

        internal void EndCapture()
        {
            _captured = false;
            if (MoveSurfaceHandle != IntPtr.Zero && GetCapture() == MoveSurfaceHandle) ReleaseCapture();
        }

        private IntPtr MoveSurfaceHook(IntPtr handle, int message, IntPtr word, IntPtr parameter, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            if (message == 0x0084) { handled = true; return new IntPtr(1); } // HTCLIENT
            if (message == 0x0215 && _captured) { _captured = false; CaptureLost?.Invoke(); }
            return IntPtr.Zero;
        }

        private IntPtr KeepNativeStyles(IntPtr handle, int message, IntPtr word, IntPtr parameter, ref bool handled)
        {
            if (message == 0x007C && word.ToInt64() == -20) // WM_STYLECHANGING / GWL_EXSTYLE
            {
                // WPFは通常WindowのLAYEREDを取り除くため、この一時状態の2bitだけを所有する。
                // その他のstyle変更はそのまま通し、終了時には元の2bitへ復元する。
                Marshal.WriteInt32(parameter, 4, (Marshal.ReadInt32(parameter, 4) & ~Mask) | ActiveBits);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SetStyles(int styles)
        {
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLong(_handle, -20, styles) == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "拡張ウィンドウ属性を変更できません");
            if (!SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, 0x37))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ウィンドウ属性を再適用できません"); // サイズ・位置・順序・フォーカスを維持。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetLayeredWindowAttributes(IntPtr handle, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
