using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TabBrowser.Views;

// 接続した表示用ボタンだけを対象に、同じ押下によるメニューの再表示を防ぐ。
internal sealed class ButtonContextMenuController
{
    private Button? _dismissedButton;
    private int _dismissTimestamp;

    internal void RecordDismissPress(Button button, Point position, int timestamp)
    {
        if (!button.IsEnabled || !new Rect(button.RenderSize).Contains(position)) return;
        _dismissedButton = button;
        _dismissTimestamp = timestamp;
    }

    internal void OnOutsideCapturedPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ContextMenu { PlacementTarget: Button button })
            RecordDismissPress(button, e.GetPosition(button), e.Timestamp);
    }

    internal void OnButtonPreviewPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button) return;
        var consume = ReferenceEquals(button, _dismissedButton) && e.Timestamp == _dismissTimestamp;
        _dismissedButton = null;
        if (consume) e.Handled = true;
    }

    internal bool CloseIfOpen(Button button)
    {
        if (button.ContextMenu is not { IsOpen: true } menu) return false;
        menu.IsOpen = false;
        return true;
    }

    internal void Toggle(Button button, PlacementMode? placement = null)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        if (placement.HasValue) menu.Placement = placement.Value;
        menu.IsOpen = !menu.IsOpen;
    }
}
