using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow
{
    private const int LowLevelKeyboardHook = 13;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _commandPaletteKeyboardHookProc;
    private IntPtr _commandPaletteKeyboardHookHandle;
    private bool _isCommandPaletteShortcutKeyDown;
    private bool _commandPaletteShortcutRequiresTopmost;

    private void InstallCommandPaletteKeyboardHook()
    {
        if (_commandPaletteKeyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        var moduleHandle = string.IsNullOrWhiteSpace(moduleName)
            ? IntPtr.Zero
            : GetModuleHandle(moduleName);
        _commandPaletteKeyboardHookHandle = SetWindowsHookExKeyboard(
            LowLevelKeyboardHook,
            _commandPaletteKeyboardHookProc,
            moduleHandle,
            0);
    }

    private void UninstallCommandPaletteKeyboardHook()
    {
        if (_commandPaletteKeyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_commandPaletteKeyboardHookHandle);
        _commandPaletteKeyboardHookHandle = IntPtr.Zero;
        _isCommandPaletteShortcutKeyDown = false;
    }

    private IntPtr CommandPaletteKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var hookData = Marshal.PtrToStructure<LowLevelKeyboardHookData>(lParam);
            if (message is WmKeyUp or WmSystemKeyUp)
            {
                if (hookData.VirtualKeyCode == 0x4B)
                {
                    _isCommandPaletteShortcutKeyDown = false;
                }
            }
            else if (message is WmKeyDown or WmSystemKeyDown
                     && !_isCommandPaletteShortcutKeyDown
                     && CommandPaletteKeyboardService.IsOpenShortcutVirtualKey(
                         hookData.VirtualKeyCode,
                         IsVirtualKeyDown(VirtualKeyControl),
                         IsVirtualKeyDown(VirtualKeyShift),
                         IsVirtualKeyDown(VirtualKeyMenu))
                     && IsCommandPaletteInputOwnerActive())
            {
                _isCommandPaletteShortcutKeyDown = true;
                Dispatcher.BeginInvoke(
                    ShowCommandPaletteFromShortcut,
                    DispatcherPriority.Send);
            }
        }

        return CallNextHookEx(_commandPaletteKeyboardHookHandle, nCode, wParam, lParam);
    }

    private void ShowCommandPaletteFromShortcut()
    {
        if (_isClosingWindow || !IsLoaded)
        {
            return;
        }

        _commandPaletteShortcutRequiresTopmost = !IsActive;
        ActivateCommandPaletteOwnerWindow();
        if (!IsCommandPaletteOpen)
        {
            OpenCommandPalette();
            return;
        }

        ApplyCommandPaletteShortcutWindowOrder();
        QueueCommandPaletteSearchFocus();
    }

    private void ActivateCommandPaletteOwnerWindow()
    {
        if (IsActive)
        {
            return;
        }

        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
    }

    private void ApplyCommandPaletteShortcutWindowOrder()
    {
        if (!_commandPaletteShortcutRequiresTopmost || !IsCommandPaletteOpen)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => PopupWindowOrderService.Apply(
                CommandPaletteOverlay,
                CommandPalettePanel,
                PopupWindowOrderPolicy.Topmost),
            DispatcherPriority.Loaded);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExKeyboard(
        int idHook,
        LowLevelKeyboardProc hookProc,
        IntPtr moduleHandle,
        uint threadId);

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

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
