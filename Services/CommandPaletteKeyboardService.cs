using System.Windows.Input;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class CommandPaletteKeyboardService
{
    private const int VirtualKeyK = 0x4B;

    public static int GetSelectionIndex(Key key, int currentIndex, int itemCount, int pageSize)
    {
        if (itemCount <= 0)
        {
            return -1;
        }

        var index = Math.Clamp(currentIndex < 0 ? 0 : currentIndex, 0, itemCount - 1);
        var normalizedPageSize = Math.Max(1, pageSize);
        return key switch
        {
            Key.Home => 0,
            Key.End => itemCount - 1,
            Key.Up => Math.Max(0, index - 1),
            Key.Down => Math.Min(itemCount - 1, currentIndex < 0 ? 0 : index + 1),
            Key.PageUp => Math.Max(0, index - normalizedPageSize),
            Key.PageDown => Math.Min(itemCount - 1, index + normalizedPageSize),
            _ => index
        };
    }

    public static bool IsOpenShortcut(Key key, Key systemKey, ModifierKeys modifiers)
    {
        return GetCommandKey(key, systemKey) == Key.K
            && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
    }

    public static bool IsOpenShortcutVirtualKey(
        int virtualKey,
        bool isControlDown,
        bool isShiftDown,
        bool isAltDown)
    {
        return virtualKey == VirtualKeyK
            && isControlDown
            && isShiftDown
            && !isAltDown;
    }

    public static Key GetCommandKey(Key key, Key systemKey)
    {
        return key switch
        {
            Key.System => systemKey,
            // IME の確定や変換中キーをパレット操作として扱わない。
            Key.ImeProcessed => Key.None,
            Key.DeadCharProcessed => Key.None,
            _ => key
        };
    }

    public static bool IsReservedNewTabEnter(Key key, Key systemKey, ModifierKeys modifiers)
    {
        return GetCommandKey(key, systemKey) == Key.Enter
            && (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    public static bool IsReservedSplitEnter(Key key, Key systemKey, ModifierKeys modifiers)
    {
        return GetCommandKey(key, systemKey) == Key.Enter
            && (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
    }

    public static CommandPaletteExecutionMode GetExecutionMode(
        Key key,
        Key systemKey,
        ModifierKeys modifiers)
    {
        if (IsReservedNewTabEnter(key, systemKey, modifiers))
        {
            return CommandPaletteExecutionMode.NewTab;
        }

        return IsReservedSplitEnter(key, systemKey, modifiers)
            ? CommandPaletteExecutionMode.Split
            : CommandPaletteExecutionMode.Default;
    }

    public static bool ShouldLetImeHandleKey(Key key, Key systemKey, bool isImeCompositionActive)
    {
        if (!isImeCompositionActive)
        {
            return false;
        }

        return GetCommandKey(key, systemKey) is Key.Enter or Key.Escape or Key.Up or Key.Down;
    }
}
