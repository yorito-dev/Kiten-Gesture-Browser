using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.ViewModels;

namespace TabBrowser.Views;

public partial class TabShelfView : UserControl
{
    private const int HoverPreviewOwnerCheckIntervalMilliseconds = 40;

    private readonly DispatcherTimer _hoverPreviewOwnerCheckTimer;
    private Point _dragStartPoint;
    private TabShelfTabViewModel? _dragSourceTab;
    private Grid? _hoverPreviewRow;
    private Popup? _hoverPreviewPopup;
    private bool _isHoverBoundaryTracking;

    public TabShelfView()
    {
        InitializeComponent();

        _hoverPreviewOwnerCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HoverPreviewOwnerCheckIntervalMilliseconds),
        };
        _hoverPreviewOwnerCheckTimer.Tick += (_, _) =>
        {
            if (_hoverPreviewPopup?.IsOpen == true
                && IsPointerInside(_hoverPreviewRow))
            {
                return;
            }

            CloseHoverPreview();
        };

        Unloaded += (_, _) => CloseHoverPreview();
    }

    public event EventHandler? QuickLookRequested;

    public event EventHandler? QuickLookSelectionChanged;

    public event EventHandler<TabShelfTabTransferRequestedEventArgs>? TabTransferRequested;

    public IReadOnlyList<TabShelfTabViewModel> GetQuickLookItems()
    {
        return OpenTabsList.Items.Cast<TabShelfTabViewModel>().ToList();
    }

    public TabShelfTabViewModel? GetQuickLookSelectedItem()
    {
        return OpenTabsList.SelectedItem as TabShelfTabViewModel;
    }

    public int GetQuickLookSelectedIndex()
    {
        return OpenTabsList.SelectedIndex;
    }

    public void SelectQuickLookIndex(int index)
    {
        if (index < 0 || index >= OpenTabsList.Items.Count)
        {
            return;
        }

        OpenTabsList.SelectedIndex = index;
        OpenTabsList.ScrollIntoView(OpenTabsList.SelectedItem);
    }

    private async void TabShelfRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Grid row)
        {
            return;
        }

        var popup = FindVisualChild<Popup>(row);
        if (popup is null)
        {
            return;
        }

        _hoverPreviewOwnerCheckTimer.Stop();
        if (_hoverPreviewPopup is not null && !ReferenceEquals(_hoverPreviewPopup, popup))
        {
            _hoverPreviewPopup.IsOpen = false;
        }

        _hoverPreviewRow = row;
        _hoverPreviewPopup = popup;
        popup.IsOpen = true;
        _hoverPreviewOwnerCheckTimer.Start();
        StartHoverBoundaryTracking();

        if (row.DataContext is TabShelfTabViewModel item)
        {
            await item.EnsureYouTubeMiniPlayerThumbnailAsync();
        }
    }

    private void TabShelfRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(sender, _hoverPreviewRow))
        {
            return;
        }

        CloseHoverPreview();
    }

    private void TabShelfView_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseHoverPreview();
    }

    private void StartHoverBoundaryTracking()
    {
        if (_isHoverBoundaryTracking)
        {
            return;
        }

        InputManager.Current.PreProcessInput += TabShelfHoverBoundary_PreProcessInput;
        _isHoverBoundaryTracking = true;
    }

    private void StopHoverBoundaryTracking()
    {
        if (!_isHoverBoundaryTracking)
        {
            return;
        }

        InputManager.Current.PreProcessInput -= TabShelfHoverBoundary_PreProcessInput;
        _isHoverBoundaryTracking = false;
    }

    private void TabShelfHoverBoundary_PreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        if (_hoverPreviewPopup?.IsOpen != true)
        {
            StopHoverBoundaryTracking();
            return;
        }

        if (e.StagingItem.Input is not MouseEventArgs)
        {
            return;
        }

        if (!IsPointerInside(_hoverPreviewRow))
        {
            CloseHoverPreview();
        }
    }

    private void OpenTabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseHoverPreview();

        if (FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject) is not null
            || FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            _dragSourceTab = null;
            return;
        }

        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        _dragSourceTab = item?.DataContext as TabShelfTabViewModel;
        _dragStartPoint = e.GetPosition(OpenTabsList);
    }

    private void OpenTabsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
        }
    }

    private void OpenTabsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && OpenTabsList.SelectedItem is TabShelfTabViewModel)
        {
            QuickLookRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OpenTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QuickLookSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PreviewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (OpenTabsList.SelectedItem is TabShelfTabViewModel)
        {
            QuickLookRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OpenTabsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourceTab is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(OpenTabsList);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (DataContext is not TabShelfViewModel viewModel)
        {
            _dragSourceTab = null;
            return;
        }

        var dragSource = _dragSourceTab;
        _dragSourceTab = null;
        var payload = await viewModel.CreateDragPayloadAsync(dragSource);
        if (payload.Tabs.Count == 0 && payload.YouTubeMiniPlayers.Count == 0)
        {
            return;
        }

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(TabShelfDragPayload.DataFormat, payload);
        DragDrop.DoDragDrop(OpenTabsList, data, DragDropEffects.Copy | DragDropEffects.Move);
        e.Handled = true;
    }

    private void OpenTabsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryCreateWindowTransferRequest(e, targetGroupWindowId: null, out _)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OpenTabsList_Drop(object sender, DragEventArgs e)
    {
        if (!TryCreateWindowTransferRequest(e, targetGroupWindowId: null, out var request))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var args = new TabShelfTabTransferRequestedEventArgs(request);
        TabTransferRequested?.Invoke(this, args);
        e.Effects = args.Accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OpenTabsGroupHeader_DragOver(object sender, DragEventArgs e)
    {
        var targetWindowId = GetGroupWindowId(sender);
        e.Effects = targetWindowId is not null
            && TryCreateWindowTransferRequest(e, targetWindowId, out _)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void OpenTabsGroupHeader_Drop(object sender, DragEventArgs e)
    {
        var targetWindowId = GetGroupWindowId(sender);
        if (targetWindowId is null
            || !TryCreateWindowTransferRequest(e, targetWindowId, out var request))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var args = new TabShelfTabTransferRequestedEventArgs(request);
        TabTransferRequested?.Invoke(this, args);
        e.Effects = args.Accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool TryCreateWindowTransferRequest(
        DragEventArgs e,
        Guid? targetGroupWindowId,
        out TabShelfWindowTransferRequest request)
    {
        request = default!;
        if (!e.Data.GetDataPresent(TabShelfDragPayload.DataFormat)
            || e.Data.GetData(TabShelfDragPayload.DataFormat) is not TabShelfDragPayload payload
            || payload.PrimaryTabId is not { } primaryTabId
            || payload.Tabs.All(tab => tab.TabId != primaryTabId))
        {
            return false;
        }

        var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetTab = targetItem?.DataContext as TabShelfTabViewModel;
        var insertAfterTarget = targetItem is not null
            && e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2;
        request = new TabShelfWindowTransferRequest(
            payload.Tabs,
            primaryTabId,
            targetTab?.WindowId ?? targetGroupWindowId,
            targetTab?.TabId,
            insertAfterTarget);
        return true;
    }

    private static Guid? GetGroupWindowId(object sender)
    {
        if (sender is not FrameworkElement { DataContext: CollectionViewGroup group })
        {
            return null;
        }

        return group.Items
            .OfType<TabShelfTabViewModel>()
            .FirstOrDefault(item => item.WindowId != Guid.Empty)?.WindowId;
    }

    private void CloseHoverPreview()
    {
        _hoverPreviewOwnerCheckTimer.Stop();
        StopHoverBoundaryTracking();
        if (_hoverPreviewPopup is not null)
        {
            _hoverPreviewPopup.IsOpen = false;
        }

        _hoverPreviewPopup = null;
        _hoverPreviewRow = null;
    }

    private static bool IsPointerInside(FrameworkElement? element)
    {
        if (element is null
            || !element.IsLoaded
            || !element.IsVisible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var position = Mouse.GetPosition(element);
            return position.X >= 0
                && position.Y >= 0
                && position.X <= element.ActualWidth
                && position.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

public sealed class TabShelfTabTransferRequestedEventArgs(TabShelfWindowTransferRequest request) : EventArgs
{
    public TabShelfWindowTransferRequest Request { get; } = request;

    public bool Accepted { get; set; }
}
