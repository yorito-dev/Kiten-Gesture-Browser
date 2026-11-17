using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TabBrowser.Services;

namespace TabBrowser.Views;

// 選択文字列はcoordinatorだけが所有し、閉じたメニューへ残さない。
internal sealed class SuperDragSearchMenu : IDisposable
{
    private readonly SuperDragCoordinator _coordinator;
    private readonly Guid _selectionId;
    private Func<string, SuperDragNavigation?>? _take;
    private Action<SuperDragNavigation>? _execute;
    private bool _disposed;
    private readonly InputInteractionHub? _inputHub;
    internal long? InteractionId { get; }
    internal ContextMenu Menu { get; } = new()
    {
        Placement = PlacementMode.MousePoint,
        StaysOpen = false,
        Background = new SolidColorBrush(Color.FromRgb(16, 19, 24)),
        Foreground = Brushes.WhiteSmoke
    };

    internal SuperDragSearchMenu(SuperDragCoordinator coordinator, SuperDragSearchSelection selection,
        Func<string, SuperDragNavigation?> take, Action<SuperDragNavigation> execute,
        InputInteractionHub? inputHub = null, long? interactionId = null)
    {
        _coordinator = coordinator; _selectionId = selection.Id; _take = take; _execute = execute;
        _inputHub = inputHub; InteractionId = interactionId;
        foreach (var choice in selection.Choices)
        {
            var action = choice.Action;
            var item = new MenuItem
            {
                Header = choice.Label,
                StaysOpenOnClick = true,
                Background = Menu.Background,
                Foreground = Menu.Foreground
            };
            item.Click += (_, _) =>
            {
                if (_disposed) return;
                var open = _execute;
                SuperDragNavigation? navigation;
                try { navigation = _take?.Invoke(action); }
                finally { Dispose(); }
                if (navigation is not null) open?.Invoke(navigation);
            };
            Menu.Items.Add(item);
        }
        coordinator.SearchSelectionInvalidated += Dispose;
        Menu.Closed += Closed;
    }

    internal void Show(FrameworkElement owner)
    {
        if (_disposed) return;
        Menu.PlacementTarget = owner;
        try { Menu.IsOpen = true; }
        catch { Dispose(); throw; }
    }

    private void Closed(object sender, RoutedEventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.SearchSelectionInvalidated -= Dispose;
        _coordinator.CloseSearchSelection(_selectionId);
        if (InteractionId is { } id) _inputHub?.TryComplete(id);
        Menu.Closed -= Closed;
        Menu.IsOpen = false;
        Menu.Items.Clear();
        Menu.PlacementTarget = null;
        _take = null; _execute = null;
    }
}
