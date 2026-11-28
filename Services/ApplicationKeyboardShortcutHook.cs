using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TabBrowser.Services;

internal interface IApplicationKeyboardShortcutWindow
{
    bool IsReadyForKeyboardShortcut { get; }
    bool IsCommandPaletteInputOwnerActive();
    void QueueCommandPaletteShortcut();
    bool TryQueueMiniPlayerResizeCancellation();
    void ReportKeyboardShortcutFailure(string failure);
}

internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

internal enum KeyboardHookRemovalResult { Removed, AlreadyGone, Failed }

internal interface IKeyboardShortcutHookNative
{
    IntPtr Install(LowLevelKeyboardProc callback);
    KeyboardHookRemovalResult Uninstall(IntPtr handle);
    IntPtr CallNext(IntPtr handle, int nCode, IntPtr wParam, IntPtr lParam);
    bool IsKeyDown(int virtualKey);
}

// UIスレッド上で1本だけ所有する。Windowのアクティブ化時だけ再登録を試みる。
internal sealed class ApplicationKeyboardShortcutHook : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmSystemKeyUp = 0x0105;
    private const int VirtualKeyF8 = 0x77;
    private const int VirtualKeyF9 = 0x78;
    private const int VirtualKeyK = 0x4B;
    private const int VirtualKeyEscape = 0x1B;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyMenu = 0x12;
    private const int InjectedFlag = 0x10;

    private readonly Func<IEnumerable<IApplicationKeyboardShortcutWindow>> _windows;
    private readonly IKeyboardShortcutHookNative _native;
    private readonly Func<int, bool, bool, bool, bool?, bool> _handleF8;
    private readonly Func<string?> _takeF8Failure;
    private readonly Action<uint>? _recordProbeDelay;
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookHandle;
    private bool _started;
    private bool _disposed;
    private bool _commandPaletteShortcutKeyDown;

    internal ApplicationKeyboardShortcutHook(
        Func<IEnumerable<IApplicationKeyboardShortcutWindow>> windows,
        IKeyboardShortcutHookNative? native = null,
        Func<int, bool, bool, bool, bool?, bool>? handleF8 = null,
        Func<string?>? takeF8Failure = null,
        Action<uint>? recordProbeDelay = null)
    {
        _windows = windows;
        _native = native ?? new Win32KeyboardShortcutHookNative();
        _handleF8 = handleF8 ?? ((key, down, modifiers, injected, wasDown)
            => YouTubeMiniPlayerTransparencyController.Shared.HandleKey(key, down, modifiers, injected, wasDown));
        _takeF8Failure = takeF8Failure ?? (() => YouTubeMiniPlayerTransparencyController.Shared.TakeFailure());
        _recordProbeDelay = recordProbeDelay;
        _callback = Callback;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        Install();
    }

    internal void Reinstall()
    {
        if (!_started || _disposed) return;
        if (!_native.IsKeyDown(VirtualKeyK)) _commandPaletteShortcutKeyDown = false;
        if (Uninstall()) Install();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
        _commandPaletteShortcutKeyDown = false;
        _started = false;
    }

    private void Install()
    {
        _hookHandle = _native.Install(_callback);
        if (_hookHandle == IntPtr.Zero)
        {
            Debug.WriteLine($"Keyboard shortcut hook: native error {Marshal.GetLastWin32Error()}");
        }
    }

    private bool Uninstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            if (_native.Uninstall(_hookHandle) == KeyboardHookRemovalResult.Failed)
            {
                Debug.WriteLine($"Keyboard shortcut hook: unhook error {Marshal.GetLastWin32Error()}");
                return false;
            }
            _hookHandle = IntPtr.Zero;
        }
        return true;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!_disposed && nCode >= 0 && lParam != IntPtr.Zero)
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmKeyUp or WmSystemKeyDown or WmSystemKeyUp)
                {
                    var data = Marshal.PtrToStructure<LowLevelKeyboardHookData>(lParam);
                    if (_recordProbeDelay is not null && message == WmKeyDown
                        && data.VirtualKeyCode == VirtualKeyF9 && (data.Flags & InjectedFlag) != 0)
                    {
                        // OSの入力時刻と、.NET 10のWindows起動後ミリ秒を同じ32ビット幅で比較する。
                        _recordProbeDelay(unchecked((uint)(Environment.TickCount - data.Time)));
                    }
                    if (HandleKey(message, data.VirtualKeyCode, data.Flags)) return new IntPtr(1);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"キーボードフック: {ex.GetType().Name}");
        }

        return _native.CallNext(_hookHandle, nCode, wParam, lParam);
    }

    private bool HandleKey(int message, int virtualKey, int flags)
    {
        var isDown = message is WmKeyDown or WmSystemKeyDown;
        if (virtualKey == VirtualKeyEscape && isDown)
        {
            _ = _windows().Any(static window => window.IsReadyForKeyboardShortcut && window.TryQueueMiniPlayerResizeCancellation());
            return false;
        }

        if (virtualKey == VirtualKeyF8)
        {
            var window = _windows().FirstOrDefault(static candidate => candidate.IsReadyForKeyboardShortcut);
            if (window is not null && _handleF8(virtualKey, isDown,
                    _native.IsKeyDown(VirtualKeyControl) || _native.IsKeyDown(VirtualKeyShift)
                        || _native.IsKeyDown(VirtualKeyMenu),
                    (flags & InjectedFlag) != 0, _native.IsKeyDown(VirtualKeyF8)))
            {
                if (_takeF8Failure() is { } failure)
                    window.ReportKeyboardShortcutFailure(failure);
                return true;
            }
        }

        if (!isDown)
        {
            if (virtualKey == VirtualKeyK) _commandPaletteShortcutKeyDown = false;
        }
        else if (virtualKey == VirtualKeyK
                 && !_commandPaletteShortcutKeyDown
                 && CommandPaletteKeyboardService.IsOpenShortcutVirtualKey(
                     virtualKey,
                     _native.IsKeyDown(VirtualKeyControl),
                     _native.IsKeyDown(VirtualKeyShift),
                     _native.IsKeyDown(VirtualKeyMenu)))
        {
            var window = _windows().FirstOrDefault(static candidate =>
                candidate.IsReadyForKeyboardShortcut && candidate.IsCommandPaletteInputOwnerActive());
            if (window is not null)
            {
                _commandPaletteShortcutKeyDown = true;
                window.QueueCommandPaletteShortcut();
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardHookData
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }
}

internal sealed class Win32KeyboardShortcutHookNative : IKeyboardShortcutHookNative
{
    private const int LowLevelKeyboardHook = 13;

    public IntPtr Install(LowLevelKeyboardProc callback)
    {
        var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        var moduleHandle = string.IsNullOrWhiteSpace(moduleName)
            ? IntPtr.Zero
            : GetModuleHandle(moduleName);
        return SetWindowsHookExKeyboard(LowLevelKeyboardHook, callback, moduleHandle, 0);
    }

    public KeyboardHookRemovalResult Uninstall(IntPtr handle)
    {
        if (UnhookWindowsHookEx(handle)) return KeyboardHookRemovalResult.Removed;
        return Marshal.GetLastWin32Error() == 1404 // ERROR_INVALID_HOOK_HANDLE: OSが既に外した。
            ? KeyboardHookRemovalResult.AlreadyGone
            : KeyboardHookRemovalResult.Failed;
    }

    public IntPtr CallNext(IntPtr handle, int nCode, IntPtr wParam, IntPtr lParam)
        => CallNextHookEx(handle, nCode, wParam, lParam);

    public bool IsKeyDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExKeyboard(
        int idHook, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr handle, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
