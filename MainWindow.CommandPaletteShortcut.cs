using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow : IApplicationKeyboardShortcutWindow
{
    private bool _commandPaletteShortcutRequiresTopmost;

    bool IApplicationKeyboardShortcutWindow.IsReadyForKeyboardShortcut => !_isClosingWindow && IsLoaded;

    bool IApplicationKeyboardShortcutWindow.IsCommandPaletteInputOwnerActive()
        => IsCommandPaletteInputOwnerActive();

    void IApplicationKeyboardShortcutWindow.QueueCommandPaletteShortcut()
        => Dispatcher.BeginInvoke(ShowCommandPaletteFromShortcut, DispatcherPriority.Send);

    void IApplicationKeyboardShortcutWindow.ReportKeyboardShortcutFailure(string failure)
        => SetStatus(failure, autoClear: true);

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

}
