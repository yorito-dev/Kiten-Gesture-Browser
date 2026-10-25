using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TabBrowser.Models.Workspaces;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;

namespace TabBrowser.Views;

public partial class WorkspaceCanvasView : UserControl, IDisposable
{
    private readonly ButtonContextMenuController _buttonContextMenuController = new();
    private const double CanvasOverscrollLimit = 240;
    private const double CanvasWidth = 4800;
    private const double CanvasHeight = 3200;
    private const double MiniMapScale = 0.0375;
    private const double MinimumZoom = 0.55;
    private const double MaximumZoom = 1.6;
    private const double ZoomStep = 0.15;
    private const uint GetAncestorRoot = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const double WorkspaceToolTipMaxWidth = 420;
    private const double WorkspaceToolTipMaxHeight = 520;
    private const double WorkspaceToolTipScreenMargin = 24;
    private const double WorkspaceToolTipTargetGap = 16;
    private const double WorkspaceToolTipWheelScrollStep = 48;
    private static readonly DependencyPropertyKey CardCacheRenderScalePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CardCacheRenderScale), typeof(double), typeof(WorkspaceCanvasView), new PropertyMetadata(1.0));
    public static readonly DependencyProperty CardCacheRenderScaleProperty = CardCacheRenderScalePropertyKey.DependencyProperty;

    public double CardCacheRenderScale => (double)GetValue(CardCacheRenderScaleProperty);

    private readonly Dictionary<ObservableCollection<WorkspaceItemViewModel>, ItemsControl> _workspaceVisualLayers = [];
    private static readonly string[] ExternalUrlDropFormats =
    [
        "UniformResourceLocatorW",
        "UniformResourceLocator",
        "URL",
        "text/x-moz-url",
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.StringFormat
    ];

    private Point _lastCanvasContextPoint;
    private Point _dragStartPoint;
    private Point _panStartPoint;
    private Point _canvasBlankClickStartPoint;
    private Point _rangeSelectionStartCanvasPoint;
    private Point _rangeSelectionStartViewportPoint;
    private Vector _miniMapDragGrabOffset;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private double _panOffsetX;
    private double _panOffsetY;
    private double _zoomFactor = 1.0;
    private WorkspaceOSViewModel? _subscribedViewModel;
    private ItemsControl _activeCanvasItemsControl = null!;
    private ContextMenu? _workspaceItemsContextMenu;
    private Func<ExternalBrowserKind, bool>? _isExternalBrowserAvailable;
    private Func<IReadOnlyList<string>, ExternalBrowserKind, Task>? _openExternalBrowserAsync;
    private ToolTip? _activeWorkspaceToolTip;
    private ScrollViewer? _activeWorkspaceToolTipScrollViewer;
    private WorkspaceItemViewModel? _draggingItem;
    private FrameworkElement? _draggingElement;
    private List<WorkspaceItemViewModel> _draggingItems = [];
    private Dictionary<Guid, Point> _dragItemStartPoints = [];
    private readonly HashSet<WorkspaceItemViewModel> _dragGuideItems = [];
    private HashSet<Guid> _rangeSelectionBaselineIds = [];
    private bool _isDragging;
    private bool _isPanning;
    private bool _isCanvasBlankClickPending;
    private bool _isRangeSelecting;
    private bool _isRangeSelectionDragging;
    private bool _isRangeSelectionModeVisualActive;
    private bool _isMiniMapDragging;
    private QuickLookSettings _quickLookSettings = QuickLookSettingsProfile.Normalize(null);
    private readonly WorkspaceQuickLookItemSource _quickLookItemSource;
    private bool _isDisposed;
    private Func<string, object?, Task>? _executeCommandAsync;

    public WorkspaceCanvasView()
    {
        InitializeComponent();
        _activeCanvasItemsControl = CanvasItemsControl;
        _workspaceItemsContextMenu = CanvasItemsControl.ContextMenu;
        QuickLookPreviewHost.ConfigureDockTarget(CanvasViewport);
        QuickLookPreviewHost.PaneLayoutChanged += (_, args) =>
            QuickLookPaneLayoutChanged?.Invoke(this, args);
        QuickLookPreviewHost.TextDisplayChanged += (_, args) =>
            QuickLookTextDisplayChanged?.Invoke(this, args);
        QuickLookPreviewHost.Configure(_quickLookSettings);
        _quickLookItemSource = new WorkspaceQuickLookItemSource(
            $"workspace-canvas:{Guid.NewGuid():N}",
            CreateQuickLookItems,
            CreateSelectedQuickLookItems,
            GetQuickLookCurrentIndex,
            SelectQuickLookIndex);
        Loaded += WorkspaceCanvasView_Loaded;
        DataContextChanged += WorkspaceCanvasView_DataContextChanged;
        Unloaded += WorkspaceCanvasView_Unloaded;
        IsKeyboardFocusWithinChanged += (_, _) => UpdateRangeSelectionModeVisual();
        CanvasViewport.SizeChanged += CanvasViewport_SizeChanged;
    }

    private WorkspaceOSViewModel? ViewModel => DataContext as WorkspaceOSViewModel;

    public Action<string>? OpenShellFolderInTab { get; set; }

    public Action<string>? OpenLocalVideoInMiniPlayer { get; set; }

    public Func<Guid, Guid, Window?, Task>? EditWorkspaceNoteAsync { get; set; }

    public void ConfigureCommandExecution(Func<string, object?, Task> executeCommandAsync)
    {
        _executeCommandAsync = executeCommandAsync;
    }

    public void ConfigureExternalBrowserOpening(
        Func<ExternalBrowserKind, bool> isExternalBrowserAvailable,
        Func<IReadOnlyList<string>, ExternalBrowserKind, Task> openExternalBrowserAsync)
    {
        _isExternalBrowserAvailable = isExternalBrowserAvailable;
        _openExternalBrowserAsync = openExternalBrowserAsync;
    }

    public void ConfigureQuickLook(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        QuickLookPreviewHost.Configure(_quickLookSettings);
    }

    public event EventHandler? QuickLookOpening;

    public event EventHandler<QuickLookPaneLayoutChangedEventArgs>? QuickLookPaneLayoutChanged;

    public event EventHandler<QuickLookTextDisplayChangedEventArgs>? QuickLookTextDisplayChanged;

    public void UpdateQuickLookPaneLayout(QuickLookSettings settings)
    {
        _quickLookSettings = QuickLookSettingsProfile.Normalize(settings);
        QuickLookPreviewHost.UpdatePaneLayout(_quickLookSettings);
    }

    public void CloseQuickLook()
    {
        QuickLookPreviewHost.Close();
    }

    public int SelectedItemCount =>
        ViewModel?.SelectedItems.Count(item => ViewModel.Items.Contains(item)) ?? 0;

    internal QuickLookHostDiagnosticSnapshot GetQuickLookDiagnosticSnapshot()
    {
        return QuickLookPreviewHost.GetDiagnosticSnapshot();
    }

    public void ArrangeSelectedCompact()
    {
        if (ViewModel is not { CanArrangeSelection: true } viewModel)
        {
            return;
        }

        viewModel.ArrangeItemsCommand.Execute(new WorkspaceArrangeRequest(
            WorkspaceArrangeMode.CurrentPosition,
            WorkspaceArrangeScope.Selection,
            GetVisibleCanvasArea()));
    }

    public void ArrangeCards(WorkspaceArrangeRequest request)
    {
        ViewModel?.ArrangeItemsCommand.Execute(request);
    }

    public void RestoreArrangement()
    {
        ViewModel?.RestoreLastArrangeCommand.Execute(null);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachWorkspaceToolTipInput();
        ResetRangeSelection();
        ResetCanvasPan();
        SubscribeToViewModel(null);
        foreach (var layer in _workspaceVisualLayers.Values)
        {
            DetachWorkspaceVisualLayerEvents(layer);
        }

        _workspaceVisualLayers.Clear();
        QuickLookPreviewHost.Dispose();
        Loaded -= WorkspaceCanvasView_Loaded;
        DataContextChanged -= WorkspaceCanvasView_DataContextChanged;
        Unloaded -= WorkspaceCanvasView_Unloaded;
        CanvasViewport.SizeChanged -= CanvasViewport_SizeChanged;
    }

    public bool TryHandleQuickLookKey(Key key)
    {
        if (key == Key.Escape && QuickLookPreviewHost.IsOpen)
        {
            QuickLookPreviewHost.Close();
            return true;
        }

        if (key != Key.Space
            || Keyboard.Modifiers != ModifierKeys.None
            || IsTextInputFocused()
            || !IsQuickLookEnabled())
        {
            return false;
        }

        _ = ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.WorkspaceQuickLookActionId,
            parameter: null,
            ShowQuickLookForSelectionAsync);
        return true;
    }

    private void CanvasItemsControl_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lastCanvasContextPoint = GetCanvasPoint(e);
    }

    private void CanvasViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        _isCanvasBlankClickPending = false;
        if (FindWorkspaceItem(source) is not null || !IsRangeSelectionSurface(source))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            _canvasBlankClickStartPoint = e.GetPosition(CanvasViewport);
            _isCanvasBlankClickPending = true;
            return;
        }

        ResetRangeSelection();
        _isRangeSelecting = true;
        SetRangeSelectionModeVisual(isActive: true, showCrossCursor: true);
        _rangeSelectionStartViewportPoint = e.GetPosition(CanvasViewport);
        _rangeSelectionStartCanvasPoint = ToCanvasPoint(_rangeSelectionStartViewportPoint);
        _rangeSelectionBaselineIds = ViewModel?.Items
            .Where(static item => item.IsSelected)
            .Select(static item => item.Id)
            .ToHashSet() ?? [];

        Focus();
        CanvasViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        e.Handled = true;
    }

    private void CanvasViewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        UpdateRangeSelectionModeVisual();
        if (_isCanvasBlankClickPending)
        {
            var blankClickPoint = e.GetPosition(CanvasViewport);
            if (e.LeftButton != MouseButtonState.Pressed
                || Math.Abs(blankClickPoint.X - _canvasBlankClickStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(blankClickPoint.Y - _canvasBlankClickStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _isCanvasBlankClickPending = false;
            }
        }

        if (!_isRangeSelecting)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetRangeSelection();
            return;
        }

        var currentPoint = e.GetPosition(CanvasViewport);
        if (!_isRangeSelectionDragging
            && Math.Abs(currentPoint.X - _rangeSelectionStartViewportPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _rangeSelectionStartViewportPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            e.Handled = true;
            return;
        }

        _isRangeSelectionDragging = true;
        UpdateRangeSelection(currentPoint);
        e.Handled = true;
    }

    private void CanvasViewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRangeSelecting)
        {
            ResetRangeSelection();
            e.Handled = true;
            return;
        }

        if (_isCanvasBlankClickPending)
        {
            _isCanvasBlankClickPending = false;
            if (ViewModel is { } viewModel)
            {
                viewModel.SelectedItem = null;
            }
        }
    }

    private void CanvasViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isCanvasBlankClickPending = false;
        if (_isRangeSelecting)
        {
            ResetRangeSelection(releaseCapture: false);
        }
    }

    private void CanvasViewport_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateRangeSelectionModeVisual();
    }

    private void CanvasViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        UpdateRangeSelectionModeVisual();
    }

    private void CanvasItemsControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedWorkspaceDrop(e)
            ? GetWorkspaceDropEffect(e)
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void CanvasItemsControl_Drop(object sender, DragEventArgs e)
    {
        if (!TrySaveDroppedWorkspaceData(e, e.GetPosition(this)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = GetWorkspaceDropEffect(e);
        e.Handled = true;
    }

    private void CanvasPanThumb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStartPoint = e.GetPosition(CanvasViewport);
        _panStartOffsetX = _panOffsetX;
        _panStartOffsetY = _panOffsetY;
        CanvasPanThumb.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void CanvasPanThumb_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetCanvasPan();
            return;
        }

        var currentPoint = e.GetPosition(CanvasViewport);
        _panOffsetX = _panStartOffsetX + (currentPoint.X - _panStartPoint.X);
        _panOffsetY = _panStartOffsetY + (currentPoint.Y - _panStartPoint.Y);
        ApplyCanvasPan();
        e.Handled = true;
    }

    private void CanvasPanThumb_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        ResetCanvasPan();
        e.Handled = true;
    }

    private void CanvasPanThumb_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            ResetCanvasPan(releaseCapture: false);
        }
    }

    private void WorkspaceCanvasView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        QuickLookPreviewHost.Close();
        SubscribeToViewModel(DataContext as WorkspaceOSViewModel);
        ResetCanvasPanOffset();
    }

    private void WorkspaceCanvasView_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(DataContext as WorkspaceOSViewModel);
        UpdateCardCacheRenderScale(VisualTreeHelper.GetDpi(this));
        ApplyCanvasPan();
    }

    private void SubscribeToViewModel(WorkspaceOSViewModel? viewModel)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribedViewModel.WorkspaceItemsPreloaded -= ViewModel_WorkspaceItemsPreloaded;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _subscribedViewModel.WorkspaceItemsPreloaded += ViewModel_WorkspaceItemsPreloaded;
            if (_subscribedViewModel.CurrentWorkspace is not null)
            {
                ActivateWorkspaceVisualLayer(_subscribedViewModel.Items);
            }
        }
    }

    private void WorkspaceCanvasView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachWorkspaceToolTipInput();
        ResetRangeSelection();
        SetRangeSelectionModeVisual(isActive: false, showCrossCursor: false);
        SetDragGuideState(isVisible: false);
        QuickLookPreviewHost.Close();
        SubscribeToViewModel(null);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceOSViewModel.Items)
            && sender is WorkspaceOSViewModel viewModel)
        {
            ActivateWorkspaceVisualLayer(viewModel.Items);
        }
        else if (e.PropertyName is nameof(WorkspaceOSViewModel.CurrentWorkspace)
            or nameof(WorkspaceOSViewModel.CurrentFolderId))
        {
            QuickLookPreviewHost.Close();
            ResetCanvasPanOffset();
            UpdateMiniMapViewport();
        }
        else if (e.PropertyName is nameof(WorkspaceOSViewModel.SelectedItem)
            or nameof(WorkspaceOSViewModel.SelectedItems))
        {
            RefreshOpenQuickLook();
        }
    }

    private void ViewModel_WorkspaceItemsPreloaded(
        ObservableCollection<WorkspaceItemViewModel> items)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () => PrepareWorkspaceVisualLayer(items),
            DispatcherPriority.Background);
    }

    private void PrepareWorkspaceVisualLayer(ObservableCollection<WorkspaceItemViewModel> items)
    {
        if (_isDisposed || _workspaceVisualLayers.ContainsKey(items))
        {
            return;
        }

        var layer = GetOrCreateWorkspaceVisualLayer(items);
        if (ReferenceEquals(layer, _activeCanvasItemsControl))
        {
            return;
        }

        layer.Opacity = 0;
        layer.IsHitTestVisible = false;
        layer.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (!_isDisposed
                    && !ReferenceEquals(layer, _activeCanvasItemsControl))
                {
                    layer.Visibility = Visibility.Collapsed;
                    layer.Opacity = 1;
                }
            },
            DispatcherPriority.ContextIdle);
    }

    private void ActivateWorkspaceVisualLayer(ObservableCollection<WorkspaceItemViewModel> items)
    {
        if (_isDisposed)
        {
            return;
        }

        var nextLayer = GetOrCreateWorkspaceVisualLayer(items);
        if (!ReferenceEquals(_activeCanvasItemsControl, nextLayer))
        {
            _activeCanvasItemsControl.ContextMenu = null;
            _activeCanvasItemsControl.IsHitTestVisible = false;
            _activeCanvasItemsControl.Visibility = Visibility.Collapsed;
            _activeCanvasItemsControl = nextLayer;
        }

        _activeCanvasItemsControl.ContextMenu = _workspaceItemsContextMenu;
        _activeCanvasItemsControl.Opacity = 1;
        _activeCanvasItemsControl.IsHitTestVisible = true;
        _activeCanvasItemsControl.Visibility = Visibility.Visible;
    }

    private ItemsControl GetOrCreateWorkspaceVisualLayer(
        ObservableCollection<WorkspaceItemViewModel> items)
    {
        if (_workspaceVisualLayers.TryGetValue(items, out var existingLayer))
        {
            return existingLayer;
        }

        if (CanvasItemsControl.ItemsSource is null
            && !_workspaceVisualLayers.ContainsValue(CanvasItemsControl))
        {
            CanvasItemsControl.ItemsSource = items;
            _workspaceVisualLayers[items] = CanvasItemsControl;
            return CanvasItemsControl;
        }

        var layer = new ItemsControl
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AllowDrop = true,
            Background = Brushes.Transparent,
            ItemsSource = items,
            ItemTemplate = CanvasItemsControl.ItemTemplate,
            ItemsPanel = CanvasItemsControl.ItemsPanel,
            ItemContainerStyle = CanvasItemsControl.ItemContainerStyle,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        AttachWorkspaceVisualLayerEvents(layer);
        CanvasItemsHost.Children.Add(layer);
        _workspaceVisualLayers[items] = layer;
        return layer;
    }

    private void AttachWorkspaceVisualLayerEvents(ItemsControl layer)
    {
        layer.DragOver += CanvasItemsControl_DragOver;
        layer.Drop += CanvasItemsControl_Drop;
        layer.PreviewMouseRightButtonDown += CanvasItemsControl_PreviewMouseRightButtonDown;
    }

    private void DetachWorkspaceVisualLayerEvents(ItemsControl layer)
    {
        if (ReferenceEquals(layer, CanvasItemsControl))
        {
            return;
        }

        layer.DragOver -= CanvasItemsControl_DragOver;
        layer.Drop -= CanvasItemsControl_Drop;
        layer.PreviewMouseRightButtonDown -= CanvasItemsControl_PreviewMouseRightButtonDown;
    }

    private void CanvasViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        // 軽量な寸法補正を現在のレイアウト内で完了させる。
        // Render優先度へ予約するとミニマップの寸法変更が別のレイアウトを起こす。
        UpdateCanvasPanForViewport();
        UpdateMiniMapViewport();
    }

    private async void AddNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var noteId = Guid.NewGuid();
        var point = new WorkspaceCanvasPoint(_lastCanvasContextPoint.X, _lastCanvasContextPoint.Y);
        var viewModel = ViewModel;
        var dialog = CreateWorkspaceNoteEditWindow("メモを追加", string.Empty, noteId);
        if (viewModel is not null)
        {
            dialog.SaveAndCloseAsync = async (content, fontSize) =>
            {
                if (!IsLoaded || !ReferenceEquals(ViewModel, viewModel))
                {
                    return false;
                }

                return await viewModel.TryAddNoteAsync(new WorkspaceNoteCreateRequest(
                    point,
                    content,
                    fontSize,
                    noteId));
            };
        }

        dialog.Owner = Window.GetWindow(this);

        await ModelessWindowService.ShowAsync(dialog);
        TrySaveWorkspaceNoteEditorWindowPlacement(dialog, dialog.IsAccepted ? noteId : null);
    }

    private void AddFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var point = new WorkspaceCanvasPoint(_lastCanvasContextPoint.X, _lastCanvasContextPoint.Y);
        ViewModel?.AddFolderCommand.Execute(point);
    }

    private async void ArrangeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !Enum.TryParse<WorkspaceArrangeScope>(parts[0], out var scope)
            || !Enum.TryParse<WorkspaceArrangeMode>(parts[1], out var mode))
        {
            return;
        }

        var request = new WorkspaceArrangeRequest(mode, scope, GetVisibleCanvasArea());
        await ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.WorkspaceApplyArrangementActionId,
            request,
            () =>
            {
                ArrangeCards(request);
                return Task.CompletedTask;
            });
    }

    private async void RestoreArrangeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.WorkspaceRestoreArrangementActionId,
            parameter: null,
            () =>
            {
                RestoreArrangement();
                return Task.CompletedTask;
            });
    }

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        if (HasSupportedWorkspaceDrop(e))
        {
            e.Effects = GetWorkspaceDropEffect(e);
            e.Handled = true;
        }
    }

    private void Item_Drop(object sender, DragEventArgs e)
    {
        if (!HasSupportedWorkspaceDrop(e))
        {
            return;
        }

        var targetFolderId = IsFolderDropTarget(sender)
            && (sender as FrameworkElement)?.DataContext is WorkspaceItemViewModel folder
                ? folder.Id
                : (Guid?)null;
        if (!TrySaveDroppedWorkspaceData(
                e,
                e.GetPosition(this),
                targetFolderId,
                useFolderDefaultPosition: targetFolderId is not null))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = GetWorkspaceDropEffect(e);
        e.Handled = true;
    }

    public bool TrySaveDroppedTabs(
        IReadOnlyList<CapturedTabSnapshot> tabs,
        Point pointInView,
        Guid? targetFolderId = null)
    {
        var pointInViewport = TranslatePoint(pointInView, CanvasViewport);
        if (tabs.Count == 0
            || ViewModel is not { } viewModel
            || !IsPointInside(pointInViewport, CanvasViewport))
        {
            return false;
        }

        var canvasPoint = ToCanvasPoint(pointInViewport);
        viewModel.SaveDroppedTabsCommand.Execute(new WorkspaceTabDropRequest(
            tabs,
            new WorkspaceCanvasPoint(canvasPoint.X, canvasPoint.Y),
            targetFolderId));
        return true;
    }

    private bool TrySaveDroppedWorkspaceData(
        DragEventArgs e,
        Point pointInView,
        Guid? targetFolderId = null,
        bool useFolderDefaultPosition = false)
    {
        var pointInViewport = TranslatePoint(pointInView, CanvasViewport);
        if (!IsPointInside(pointInViewport, CanvasViewport))
        {
            return false;
        }

        if (e.Data.GetData(WorkspaceCardDragPayload.DataFormat) is WorkspaceCardDragPayload cardPayload
            && ViewModel is { CurrentWorkspace: { } currentWorkspace } cardViewModel)
        {
            var canvasPoint = ToCanvasPoint(pointInViewport);
            var resolvedTargetFolderId = targetFolderId ?? cardViewModel.CurrentFolderId;
            var request = new WorkspaceCardDropRequest(
                cardPayload,
                currentWorkspace.Id,
                new WorkspaceCanvasPoint(canvasPoint.X, canvasPoint.Y),
                resolvedTargetFolderId,
                useFolderDefaultPosition);
            if (!cardViewModel.MoveDraggedItemsCommand.CanExecute(request))
            {
                return false;
            }

            cardViewModel.MoveDraggedItemsCommand.Execute(request);
            return true;
        }

        if (e.Data.GetData(TabShelfDragPayload.DataFormat) is TabShelfDragPayload payload)
        {
            if ((payload.Tabs.Count == 0 && payload.YouTubeMiniPlayers.Count == 0)
                || ViewModel is not { } viewModel)
            {
                return false;
            }

            var canvasPoint = ToCanvasPoint(pointInViewport);
            viewModel.SaveDroppedShelfItemsCommand.Execute(new WorkspaceShelfDropRequest(
                payload.Tabs,
                payload.YouTubeMiniPlayers,
                new WorkspaceCanvasPoint(canvasPoint.X, canvasPoint.Y),
                targetFolderId));
            return true;
        }

        if (TryGetDroppedPaths(e, out var paths)
            && ViewModel is { } shellViewModel)
        {
            var textFileMode = WorkspaceTextFileDropMode.PlaceAsFile;
            var textFileCount = paths.Count(path =>
                File.Exists(path) && WorkspaceShellItemService.IsTextDocumentPath(path));
            if (textFileCount > 0)
            {
                var dialog = new WorkspaceTextFileImportChoiceWindow(textFileCount)
                {
                    Owner = Window.GetWindow(this)
                };
                _ = dialog.ShowDialog();
                if (dialog.Choice == WorkspaceTextFileImportChoice.Cancel)
                {
                    return false;
                }

                textFileMode = dialog.Choice == WorkspaceTextFileImportChoice.ImportAsNote
                    ? WorkspaceTextFileDropMode.ImportAsNote
                    : WorkspaceTextFileDropMode.PlaceAsFile;
            }

            var canvasPoint = ToCanvasPoint(pointInViewport);
            shellViewModel.SaveDroppedShellItemsCommand.Execute(new WorkspaceShellDropRequest(
                paths,
                new WorkspaceCanvasPoint(canvasPoint.X, canvasPoint.Y),
                targetFolderId,
                textFileMode));
            return true;
        }

        if (TryGetExternalDropLink(e, out var url, out var title))
        {
            var snapshot = new CapturedTabSnapshot
            {
                TabId = Guid.NewGuid(),
                WindowId = Guid.Empty,
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Url = url,
                TabIndex = -1,
                IsActive = false,
                IsPinned = false
            };
            return TrySaveDroppedTabs([snapshot], pointInView, targetFolderId);
        }

        return false;
    }

    private static bool HasSupportedWorkspaceDrop(DragEventArgs e)
    {
        return e.Data.GetDataPresent(WorkspaceCardDragPayload.DataFormat)
            || e.Data.GetDataPresent(TabShelfDragPayload.DataFormat)
            || e.Data.GetDataPresent(DataFormats.FileDrop)
            || ExternalUrlDropFormats.Any(format => e.Data.GetDataPresent(format));
    }

    private static DragDropEffects GetWorkspaceDropEffect(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(WorkspaceCardDragPayload.DataFormat))
        {
            return DragDropEffects.Move;
        }

        return e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Link
            : DragDropEffects.Copy;
    }

    private static bool TryGetDroppedPaths(DragEventArgs e, out IReadOnlyList<string> paths)
    {
        paths = [];
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)
                || e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths)
            {
                return false;
            }

            paths = droppedPaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            return paths.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetExternalDropLink(DragEventArgs e, out string url, out string title)
    {
        foreach (var format in ExternalUrlDropFormats)
        {
            if (TryGetDropText(e, format, out var text)
                && TryExtractDroppedLink(text, out url, out title))
            {
                return true;
            }
        }

        url = string.Empty;
        title = string.Empty;
        return false;
    }

    private static bool TryGetDropText(DragEventArgs e, string format, out string text)
    {
        text = string.Empty;
        try
        {
            if (!e.Data.GetDataPresent(format))
            {
                return false;
            }

            var data = e.Data.GetData(format);
            switch (data)
            {
                case string value:
                    text = value;
                    return !string.IsNullOrWhiteSpace(text);
                case byte[] bytes:
                    text = DecodeDroppedUrlBytes(bytes, format);
                    return !string.IsNullOrWhiteSpace(text);
                case MemoryStream memoryStream:
                    text = DecodeDroppedUrlBytes(memoryStream.ToArray(), format);
                    return !string.IsNullOrWhiteSpace(text);
                case Stream stream:
                    text = ReadDroppedUrlStream(stream, format);
                    return !string.IsNullOrWhiteSpace(text);
                default:
                    return false;
            }
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    private static string ReadDroppedUrlStream(Stream stream, string format)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return DecodeDroppedUrlBytes(memoryStream.ToArray(), format);
    }

    private static string DecodeDroppedUrlBytes(byte[] bytes, string format)
    {
        var encoding = format.Equals("UniformResourceLocatorW", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.Unicode
            : System.Text.Encoding.UTF8;
        return encoding.GetString(bytes).TrimEnd('\0');
    }

    private static bool TryExtractDroppedLink(string text, out string url, out string title)
    {
        var previousText = string.Empty;
        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim().Trim('\0').Trim('"', '\''))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            var candidate = lines[index];
            if (TryNormalizeDroppedUrl(candidate, out url))
            {
                title = FindDroppedUrlTitle(lines, index, previousText);
                return true;
            }

            previousText = candidate;
        }

        url = string.Empty;
        title = string.Empty;
        return false;
    }

    private static string FindDroppedUrlTitle(IReadOnlyList<string> lines, int urlIndex, string fallbackTitle)
    {
        for (var index = urlIndex + 1; index < lines.Count; index++)
        {
            if (!TryNormalizeDroppedUrl(lines[index], out _))
            {
                return lines[index];
            }
        }

        return fallbackTitle;
    }

    private static bool TryNormalizeDroppedUrl(string value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri)
            && IsAllowedDroppedUrlScheme(absoluteUri.Scheme))
        {
            url = absoluteUri.ToString();
            return true;
        }

        if (LooksLikeDroppedHost(value))
        {
            url = "https://" + value;
            return true;
        }

        return false;
    }

    private static bool IsAllowedDroppedUrlScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDroppedHost(string value)
    {
        return !value.Any(char.IsWhiteSpace)
            && (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
                || value.Contains('.', StringComparison.Ordinal))
            && !value.Contains('\\', StringComparison.Ordinal)
            && !value.Contains('/', StringComparison.Ordinal);
    }

    private async void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceItemViewModel item)
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            if (!item.IsSelected)
            {
                viewModel.SelectedItem = item;
            }
        }

        Focus();

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            await OpenItemsFromDoubleClickAsync(item);
            return;
        }

        _draggingItem = item;
        _draggingElement = sender as FrameworkElement;
        _draggingItems = ViewModel is { } currentViewModel && item.IsSelected
            ? currentViewModel.SelectedItems.Where(currentViewModel.Items.Contains).ToList()
            : [item];
        if (_draggingItems.Count == 0)
        {
            _draggingItems = [item];
        }

        _dragItemStartPoints = _draggingItems.ToDictionary(
            static selectedItem => selectedItem.Id,
            static selectedItem => new Point(selectedItem.X, selectedItem.Y));
        _dragStartPoint = GetCanvasPoint(e);
        _isDragging = false;
        _draggingElement?.CaptureMouse();
        e.Handled = true;
    }

    private async Task OpenItemsFromDoubleClickAsync(WorkspaceItemViewModel item)
    {
        var folderWindow = Window.GetWindow(this) as WorkspaceFolderWindow;
        var selectedItems = GetSelectedItemsForAction(item);
        if (selectedItems.Count > 1)
        {
            var command = ViewModel?.OpenItemsCommand;
            if (command?.CanExecute(selectedItems) == true)
            {
                command.Execute(selectedItems);
                folderWindow?.Close();
            }

            return;
        }

        if (item.Type == WorkspaceItemType.Note)
        {
            var editTask = ShowEditNoteDialogAsync(item, folderWindow?.Owner ?? Window.GetWindow(this));
            folderWindow?.Close();
            await editTask;
            return;
        }

        var openCommand = ViewModel?.OpenItemCommand;
        if (openCommand?.CanExecute(item) == true)
        {
            openCommand.Execute(item);
            folderWindow?.Close();
        }
    }

    private void Item_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingItem is null
            || _draggingElement is null
            || _draggingItems.Count == 0
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = GetCanvasPoint(e);
        var deltaX = currentPoint.X - _dragStartPoint.X;
        var deltaY = currentPoint.Y - _dragStartPoint.Y;
        if (!_isDragging
            && Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!_isDragging)
        {
            _isDragging = true;
            SetDragGuideState(isVisible: true);
        }

        var isPointerInsideCanvas = IsPointInside(e.GetPosition(CanvasViewport), CanvasViewport);
        var isPointerOverDifferentWindow = IsCursorOverDifferentTopLevelWindow();
        if (WorkspaceCardDragPayload.ShouldStartWindowDrag(
                isPointerInsideCanvas,
                isPointerOverDifferentWindow))
        {
            BeginWorkspaceCardDragOutsideCanvas();
            e.Handled = true;
            return;
        }

        deltaX = Math.Max(deltaX, -_dragItemStartPoints.Values.Min(static point => point.X));
        deltaY = Math.Max(deltaY, -_dragItemStartPoints.Values.Min(static point => point.Y));
        foreach (var item in _draggingItems)
        {
            var startPoint = _dragItemStartPoints[item.Id];
            item.X = startPoint.X + deltaX;
            item.Y = startPoint.Y + deltaY;
        }

        e.Handled = true;
    }

    private void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingElement is not null && _draggingElement.IsMouseCaptured)
        {
            _draggingElement.ReleaseMouseCapture();
        }

        if (_draggingItem is not null && _isDragging)
        {
            if (TryMoveDraggedItemsIntoFolder(GetCanvasPoint(e)))
            {
                e.Handled = true;
            }
            else if (_draggingItems.Count > 1)
            {
                ViewModel?.MoveItemsCommand.Execute(_draggingItems.ToList());
            }
            else
            {
                ViewModel?.MoveItemCommand.Execute(_draggingItem);
            }

            e.Handled = true;
        }

        ClearItemDragState();
    }

    private bool TryMoveDraggedItemsIntoFolder(Point dropPoint)
    {
        if (ViewModel is not { CurrentWorkspace: { } currentWorkspace } viewModel
            || _draggingItems.Count == 0)
        {
            return false;
        }

        var draggedItemIds = _draggingItems.Select(static item => item.Id).ToHashSet();
        var folderHitPoint = _draggingItem is { } draggedItem
            ? new WorkspaceCanvasPoint(
                draggedItem.X + (draggedItem.Width / 2),
                draggedItem.Y + (draggedItem.Height / 2))
            : new WorkspaceCanvasPoint(dropPoint.X, dropPoint.Y);
        var targetFolderId = WorkspaceCardDragPayload.FindFolderDropTargetId(
            viewModel.Items.Select(static item => item.Item).ToList(),
            draggedItemIds,
            folderHitPoint);
        if (targetFolderId is null)
        {
            return false;
        }

        RestoreDraggedItemsToStartPoints();
        var payload = viewModel.CreateCardDragPayload(
            _draggingItems,
            new WorkspaceCanvasPoint(dropPoint.X, dropPoint.Y));
        if (payload is null)
        {
            return false;
        }

        var request = new WorkspaceCardDropRequest(
            payload,
            currentWorkspace.Id,
            new WorkspaceCanvasPoint(dropPoint.X, dropPoint.Y),
            targetFolderId,
            UseFolderDefaultPosition: true);
        if (!viewModel.MoveDraggedItemsCommand.CanExecute(request))
        {
            return false;
        }

        viewModel.MoveDraggedItemsCommand.Execute(request);
        return true;
    }

    private void BeginWorkspaceCardDragOutsideCanvas()
    {
        if (ViewModel is not { } viewModel
            || _draggingElement is null
            || _draggingItems.Count == 0)
        {
            return;
        }

        RestoreDraggedItemsToStartPoints();
        var payload = viewModel.CreateCardDragPayload(
            _draggingItems,
            new WorkspaceCanvasPoint(_dragStartPoint.X, _dragStartPoint.Y));
        if (payload is null)
        {
            ClearItemDragState();
            return;
        }

        var dragSource = _draggingElement;
        if (dragSource.IsMouseCaptured)
        {
            dragSource.ReleaseMouseCapture();
        }

        SetDragGuideState(isVisible: false);
        var data = new DataObject();
        data.SetData(WorkspaceCardDragPayload.DataFormat, payload);
        WorkspaceCardDragPreviewWindow? dragPreview = null;
        GiveFeedbackEventHandler? updatePreview = null;
        var previousOverrideCursor = Mouse.OverrideCursor;
        var result = DragDropEffects.None;
        try
        {
            dragPreview = CreateWorkspaceCardDragPreview(payload);
            if (dragPreview is not null)
            {
                updatePreview = (_, feedback) =>
                {
                    dragPreview.UpdatePosition();
                    feedback.UseDefaultCursors = false;
                    feedback.Handled = true;
                };
                dragSource.GiveFeedback += updatePreview;
                dragPreview.ShowWithoutActivation();
                Mouse.OverrideCursor = Cursors.None;
            }

            result = DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Move);
        }
        finally
        {
            if (updatePreview is not null)
            {
                dragSource.GiveFeedback -= updatePreview;
            }

            try
            {
                dragPreview?.Close();
            }
            finally
            {
                Mouse.OverrideCursor = previousOverrideCursor;
                ClearItemDragState(releaseCapture: false);
            }
        }

        if (result == DragDropEffects.None && IsCursorOutsideApplicationWindows())
        {
            ConfirmAndDeleteDraggedItems(payload);
        }
    }

    private void RestoreDraggedItemsToStartPoints()
    {
        foreach (var item in _draggingItems)
        {
            if (_dragItemStartPoints.TryGetValue(item.Id, out var startPoint))
            {
                item.X = startPoint.X;
                item.Y = startPoint.Y;
            }
        }
    }

    private WorkspaceCardDragPreviewWindow? CreateWorkspaceCardDragPreview(
        WorkspaceCardDragPayload payload)
    {
        var parts = new List<WorkspaceCardDragPreviewPart>();
        foreach (var root in payload.Roots)
        {
            var viewModel = _draggingItems.FirstOrDefault(item => item.Id == root.Item.Id);
            if (viewModel is null)
            {
                continue;
            }

            var container = _activeCanvasItemsControl.ItemContainerGenerator.ContainerFromItem(viewModel) as FrameworkElement;
            var width = container is { ActualWidth: > 0 }
                ? container.ActualWidth
                : viewModel.Width;
            var height = container is { ActualHeight: > 0 }
                ? container.ActualHeight
                : viewModel.Height;
            parts.Add(new WorkspaceCardDragPreviewPart(
                viewModel,
                root.OffsetX,
                root.OffsetY,
                width,
                height));
        }

        return parts.Count == 0
            ? null
            : new WorkspaceCardDragPreviewWindow(
                parts,
                CanvasItemsControl.ItemTemplate,
                payload.GrabOffsetX,
                payload.GrabOffsetY,
                _zoomFactor);
    }

    private bool IsCursorOverDifferentTopLevelWindow()
    {
        var sourceWindow = Window.GetWindow(this);
        if (sourceWindow is null || !GetCursorPos(out var cursorPoint))
        {
            return false;
        }

        var sourceHandle = new WindowInteropHelper(sourceWindow).Handle;
        var hitHandle = WindowFromPoint(cursorPoint);
        if (sourceHandle == IntPtr.Zero || hitHandle == IntPtr.Zero)
        {
            return false;
        }

        return GetRootWindow(sourceHandle) != GetRootWindow(hitHandle);
    }

    private static IntPtr GetRootWindow(IntPtr handle)
    {
        var root = GetAncestor(handle, GetAncestorRoot);
        return root == IntPtr.Zero ? handle : root;
    }

    private static bool IsCursorOutsideApplicationWindows()
    {
        if (!GetCursorPos(out var cursorPoint))
        {
            return false;
        }

        var inspectedWindow = false;
        foreach (var window in Application.Current.Windows
                     .OfType<Window>()
                     .Where(static window => window.IsVisible && window is not WorkspaceCardDragPreviewWindow))
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
            {
                continue;
            }

            inspectedWindow = true;
            if (cursorPoint.X >= windowRect.Left
                && cursorPoint.X < windowRect.Right
                && cursorPoint.Y >= windowRect.Top
                && cursorPoint.Y < windowRect.Bottom)
            {
                return false;
            }
        }

        return inspectedWindow;
    }

    private void ConfirmAndDeleteDraggedItems(WorkspaceCardDragPayload payload)
    {
        if (payload.Roots.Count == 0 || ViewModel is not { } viewModel)
        {
            return;
        }

        var message = payload.Roots.Count == 1
            ? $"「{payload.Roots[0].Item.Title}」を Workspace から削除しますか？\n\n実ファイルやリンク先は削除されません。"
            : $"ウィンドウ外へ移動した {payload.Roots.Count} 件のカードを Workspace から削除しますか？\n\n実ファイルやリンク先は削除されません。";
        var result = MessageBox.Show(
            Window.GetWindow(this),
            message,
            "Workspace 外へのドラッグ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            viewModel.DeleteDraggedItemsCommand.Execute(payload);
        }
    }

    private void ClearItemDragState(bool releaseCapture = true)
    {
        if (releaseCapture && _draggingElement is { IsMouseCaptured: true })
        {
            _draggingElement.ReleaseMouseCapture();
        }

        SetDragGuideState(isVisible: false);
        _draggingItem = null;
        _draggingElement = null;
        _draggingItems.Clear();
        _dragItemStartPoints.Clear();
        _isDragging = false;
    }

    private void SetDragGuideState(bool isVisible)
    {
        if (!isVisible)
        {
            foreach (var item in _dragGuideItems)
            {
                item.IsDragGuideVisible = false;
                item.IsBeingDragged = false;
            }

            _dragGuideItems.Clear();
            return;
        }

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var draggedItems = _draggingItems.ToHashSet();
        foreach (var item in viewModel.Items)
        {
            var isBeingDragged = draggedItems.Contains(item);
            if (!isBeingDragged && item.Type != WorkspaceItemType.Folder)
            {
                continue;
            }

            item.IsDragGuideVisible = true;
            item.IsBeingDragged = isBeingDragged;
            _dragGuideItems.Add(item);
        }
    }

    private void ResetCanvasPan(bool releaseCapture = true)
    {
        _isPanning = false;
        Mouse.OverrideCursor = null;
        if (releaseCapture && CanvasViewport.IsMouseCaptured)
        {
            CanvasViewport.ReleaseMouseCapture();
        }

        if (releaseCapture && CanvasPanThumb.IsMouseCaptured)
        {
            CanvasPanThumb.ReleaseMouseCapture();
        }
    }

    private void UpdateRangeSelection(Point currentViewportPoint)
    {
        var left = Math.Min(_rangeSelectionStartViewportPoint.X, currentViewportPoint.X);
        var top = Math.Min(_rangeSelectionStartViewportPoint.Y, currentViewportPoint.Y);
        RangeSelectionRectangle.Margin = new Thickness(left, top, 0, 0);
        RangeSelectionRectangle.Width = Math.Abs(currentViewportPoint.X - _rangeSelectionStartViewportPoint.X);
        RangeSelectionRectangle.Height = Math.Abs(currentViewportPoint.Y - _rangeSelectionStartViewportPoint.Y);
        RangeSelectionRectangle.Visibility = Visibility.Visible;

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var currentCanvasPoint = ToCanvasPoint(currentViewportPoint);
        var selectionArea = new WorkspaceCanvasRect(
            Math.Min(_rangeSelectionStartCanvasPoint.X, currentCanvasPoint.X),
            Math.Min(_rangeSelectionStartCanvasPoint.Y, currentCanvasPoint.Y),
            Math.Abs(currentCanvasPoint.X - _rangeSelectionStartCanvasPoint.X),
            Math.Abs(currentCanvasPoint.Y - _rangeSelectionStartCanvasPoint.Y));
        var selectedItems = viewModel.Items.Where(item =>
            _rangeSelectionBaselineIds.Contains(item.Id)
            || selectionArea.Intersects(item.X, item.Y, item.Width, item.Height));
        viewModel.SetSelectedItems(selectedItems);
    }

    private void ResetRangeSelection(bool releaseCapture = true)
    {
        _isRangeSelecting = false;
        _isRangeSelectionDragging = false;
        _rangeSelectionBaselineIds.Clear();
        RangeSelectionRectangle.Visibility = Visibility.Collapsed;
        RangeSelectionRectangle.Width = 0;
        RangeSelectionRectangle.Height = 0;

        if (releaseCapture && CanvasViewport.IsMouseCaptured)
        {
            CanvasViewport.ReleaseMouseCapture();
        }

        UpdateRangeSelectionModeVisual();
    }

    private void UpdateRangeSelectionModeVisual()
    {
        var isControlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var isPointerOverSelectionSurface = IsPointerOverRangeSelectionSurface();
        var isActive = _isRangeSelecting
            || isControlPressed && (IsKeyboardFocusWithin || isPointerOverSelectionSurface);
        var showCrossCursor = _isRangeSelecting
            || isControlPressed && isPointerOverSelectionSurface;
        SetRangeSelectionModeVisual(isActive, showCrossCursor);
    }

    private void SetRangeSelectionModeVisual(bool isActive, bool showCrossCursor)
    {
        if (_isRangeSelectionModeVisualActive != isActive)
        {
            _isRangeSelectionModeVisualActive = isActive;
            var visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            RangeSelectionModeFrame.Visibility = visibility;
            RangeSelectionModeIndicator.Visibility = visibility;
        }

        if (!_isPanning)
        {
            Mouse.OverrideCursor = showCrossCursor ? Cursors.Cross : null;
        }
    }

    private bool IsPointerOverRangeSelectionSurface()
    {
        if (!CanvasViewport.IsMouseOver)
        {
            return false;
        }

        var source = CanvasViewport.InputHitTest(Mouse.GetPosition(CanvasViewport)) as DependencyObject;
        return FindWorkspaceItem(source) is null
            && IsRangeSelectionSurface(source);
    }

    private bool IsRangeSelectionSurface(DependencyObject? source)
    {
        return IsDescendantOf(source, CanvasPanThumb)
            || IsDescendantOf(source, _activeCanvasItemsControl);
    }

    private static WorkspaceItemViewModel? FindWorkspaceItem(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is FrameworkElement { DataContext: WorkspaceItemViewModel item })
            {
                return item;
            }
        }

        return null;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        return current switch
        {
            Visual => VisualTreeHelper.GetParent(current),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => LogicalTreeHelper.GetParent(current)
        };
    }

    public void ResetPanOffset()
    {
        ResetCanvasPan();
        ResetCanvasPanOffset();
    }

    private void ResetCanvasPanOffset()
    {
        _panOffsetX = 0;
        _panOffsetY = 0;
        ApplyCanvasPan();
    }

    private void ApplyCanvasPan()
    {
        var scaledCanvasWidth = CanvasItemsControl.ActualWidth * _zoomFactor;
        var scaledCanvasHeight = CanvasItemsControl.ActualHeight * _zoomFactor;
        _panOffsetX = ClampPanOffset(_panOffsetX, CanvasViewport.ActualWidth, scaledCanvasWidth);
        _panOffsetY = ClampPanOffset(_panOffsetY, CanvasViewport.ActualHeight, scaledCanvasHeight);
        CanvasGridZoomTransform.ScaleX = _zoomFactor;
        CanvasGridZoomTransform.ScaleY = _zoomFactor;
        CanvasZoomTransform.ScaleX = _zoomFactor;
        CanvasZoomTransform.ScaleY = _zoomFactor;
        CanvasGridPanTransform.X = _panOffsetX;
        CanvasGridPanTransform.Y = _panOffsetY;
        CanvasPanTransform.X = _panOffsetX;
        CanvasPanTransform.Y = _panOffsetY;
        ResetZoomButton.Content = $"{Math.Round(_zoomFactor * 100)}%";
        UpdateMiniMapViewport();
    }

    private void UpdateCanvasPanForViewport()
    {
        var scaledCanvasWidth = CanvasItemsControl.ActualWidth * _zoomFactor;
        var scaledCanvasHeight = CanvasItemsControl.ActualHeight * _zoomFactor;
        var nextOffsetX = ClampPanOffset(
            _panOffsetX,
            CanvasViewport.ActualWidth,
            scaledCanvasWidth);
        var nextOffsetY = ClampPanOffset(
            _panOffsetY,
            CanvasViewport.ActualHeight,
            scaledCanvasHeight);
        if (nextOffsetX.Equals(_panOffsetX) && nextOffsetY.Equals(_panOffsetY))
        {
            return;
        }

        _panOffsetX = nextOffsetX;
        _panOffsetY = nextOffsetY;
        CanvasGridPanTransform.X = _panOffsetX;
        CanvasGridPanTransform.Y = _panOffsetY;
        CanvasPanTransform.X = _panOffsetX;
        CanvasPanTransform.Y = _panOffsetY;
    }

    private static double ClampPanOffset(double offset, double viewportSize, double canvasSize)
    {
        if (viewportSize <= 0 || canvasSize <= 0)
        {
            return offset;
        }

        var min = viewportSize - canvasSize - CanvasOverscrollLimit;
        var max = CanvasOverscrollLimit;
        // 全画面化や縮小表示で範囲が逆転した場合は、余白を両側へ均等に配分する。
        if (min > max)
        {
            return (viewportSize - canvasSize) / 2;
        }

        return Math.Clamp(offset, min, max);
    }

    private Point GetCanvasPoint(MouseEventArgs e)
    {
        return ToCanvasPoint(e.GetPosition(CanvasViewport));
    }

    private Point GetCanvasPoint(DragEventArgs e)
    {
        return ToCanvasPoint(e.GetPosition(CanvasViewport));
    }

    private Point ToCanvasPoint(Point viewportPoint)
    {
        return new Point(
            (viewportPoint.X - _panOffsetX) / _zoomFactor,
            (viewportPoint.Y - _panOffsetY) / _zoomFactor);
    }

    private WorkspaceCanvasRect GetVisibleCanvasArea()
    {
        var topLeft = ToCanvasPoint(new Point(0, 0));
        return new WorkspaceCanvasRect(
            topLeft.X,
            topLeft.Y,
            Math.Max(0, CanvasViewport.ActualWidth / _zoomFactor),
            Math.Max(0, CanvasViewport.ActualHeight / _zoomFactor));
    }

    public WorkspaceCanvasPoint GetDefaultCardDropPoint(WorkspaceCardDragPayload payload)
    {
        var visibleArea = GetVisibleCanvasArea();
        return new WorkspaceCanvasPoint(
            visibleArea.X + 32 + payload.GrabOffsetX,
            visibleArea.Y + 32 + payload.GrabOffsetY);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomFactor - ZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomFactor + ZoomStep);
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
    }

    private void SetZoom(double zoom)
    {
        var oldZoom = _zoomFactor;
        var center = ToCanvasPoint(new Point(CanvasViewport.ActualWidth / 2, CanvasViewport.ActualHeight / 2));
        _zoomFactor = Math.Clamp(Math.Round(zoom, 2), MinimumZoom, MaximumZoom);
        if (Math.Abs(oldZoom - _zoomFactor) < 0.01)
        {
            return;
        }

        CenterOnCanvasPoint(center);
        UpdateCardCacheRenderScale(VisualTreeHelper.GetDpi(this));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateCardCacheRenderScale(newDpi);
    }

    private void UpdateCardCacheRenderScale(DpiScale dpi)
    {
        // WPF 8のBitmapCacheはシステムDPIを基準にする。別モニターの高DPIと
        // Canvasの拡大にだけ解像度を補い、リサイズのたびにはキャッシュを作り直さない。
        var systemScale = Math.Max(1, GetDpiForSystem() / 96.0);
        var monitorScale = Math.Max(1, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY) / systemScale);
        SetValue(CardCacheRenderScalePropertyKey, Math.Max(1, _zoomFactor) * monitorScale);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private void ToggleMiniMapButton_Click(object sender, RoutedEventArgs e)
    {
        MiniMapPanel.Visibility = MiniMapPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ArrangeMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _buttonContextMenuController.Toggle(button);
        }
    }

    private void ButtonContextMenu_MouseDownOutsideCapturedElement(object sender, MouseButtonEventArgs e)
        => _buttonContextMenuController.OnOutsideCapturedPress(sender, e);

    private void MenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _buttonContextMenuController.OnButtonPreviewPress(sender, e);

    private void RestoreArrangeButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.RestoreLastArrangeCommand.Execute(null);
    }

    private void BringSelectedIntoViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedItem is not { } item)
        {
            ResetCanvasPanOffset();
            return;
        }

        var visibleArea = GetVisibleCanvasArea();
        item.X = visibleArea.X + 24;
        item.Y = visibleArea.Y + 24;
        ViewModel.MoveItemCommand.Execute(item);
        CenterOnItem(item);
    }

    private void JumpToRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var recent = viewModel.Items
            .OrderByDescending(static x => x.Item.CreatedAt)
            .FirstOrDefault();
        if (recent is null)
        {
            return;
        }

        viewModel.SelectedItem = recent;
        CenterOnItem(recent);
    }

    private void MiniMapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(MiniMapCanvas);
        var viewportBounds = GetMiniMapViewportBounds();
        _miniMapDragGrabOffset = viewportBounds.Contains(point)
            ? point - new Point(viewportBounds.X + viewportBounds.Width / 2, viewportBounds.Y + viewportBounds.Height / 2)
            : default;
        _isMiniMapDragging = true;
        MiniMapCanvas.Cursor = Cursors.SizeAll;
        MiniMapCanvas.CaptureMouse();

        if (!viewportBounds.Contains(point))
        {
            MoveMiniMapViewport(point);
        }

        e.Handled = true;
    }

    private void MiniMapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMiniMapDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetMiniMapDrag();
            return;
        }

        MoveMiniMapViewport(e.GetPosition(MiniMapCanvas));
        e.Handled = true;
    }

    private void MiniMapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMiniMapDragging)
        {
            return;
        }

        ResetMiniMapDrag();
        e.Handled = true;
    }

    private void MiniMapCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isMiniMapDragging)
        {
            ResetMiniMapDrag(releaseCapture: false);
        }
    }

    private void MoveMiniMapViewport(Point pointerPosition)
    {
        var viewportBounds = GetMiniMapViewportBounds();
        var halfWidth = viewportBounds.Width / 2;
        var halfHeight = viewportBounds.Height / 2;
        var centerX = Math.Clamp(
            pointerPosition.X - _miniMapDragGrabOffset.X,
            halfWidth,
            MiniMapCanvas.Width - halfWidth);
        var centerY = Math.Clamp(
            pointerPosition.Y - _miniMapDragGrabOffset.Y,
            halfHeight,
            MiniMapCanvas.Height - halfHeight);
        CenterOnCanvasPoint(new Point(centerX / MiniMapScale, centerY / MiniMapScale));
    }

    private Rect GetMiniMapViewportBounds()
    {
        var left = Canvas.GetLeft(MiniMapViewportRect);
        var top = Canvas.GetTop(MiniMapViewportRect);
        var width = double.IsFinite(MiniMapViewportRect.Width) ? MiniMapViewportRect.Width : 0;
        var height = double.IsFinite(MiniMapViewportRect.Height) ? MiniMapViewportRect.Height : 0;
        return new Rect(
            double.IsFinite(left) ? left : 0,
            double.IsFinite(top) ? top : 0,
            Math.Clamp(width, 0, MiniMapCanvas.Width),
            Math.Clamp(height, 0, MiniMapCanvas.Height));
    }

    private void ResetMiniMapDrag(bool releaseCapture = true)
    {
        _isMiniMapDragging = false;
        _miniMapDragGrabOffset = default;
        MiniMapCanvas.Cursor = Cursors.Hand;
        if (releaseCapture && MiniMapCanvas.IsMouseCaptured)
        {
            MiniMapCanvas.ReleaseMouseCapture();
        }
    }

    private void CenterOnItem(WorkspaceItemViewModel item)
    {
        CenterOnCanvasPoint(new Point(item.X + item.Width / 2, item.Y + item.Height / 2));
    }

    private void CenterOnCanvasPoint(Point canvasPoint)
    {
        _panOffsetX = CanvasViewport.ActualWidth / 2 - canvasPoint.X * _zoomFactor;
        _panOffsetY = CanvasViewport.ActualHeight / 2 - canvasPoint.Y * _zoomFactor;
        ApplyCanvasPan();
    }

    private void UpdateMiniMapViewport()
    {
        if (MiniMapViewportRect is null)
        {
            return;
        }

        var visibleArea = GetVisibleCanvasArea();
        var left = Math.Clamp(visibleArea.X * MiniMapScale, 0, MiniMapCanvas.Width);
        var top = Math.Clamp(visibleArea.Y * MiniMapScale, 0, MiniMapCanvas.Height);
        var width = Math.Clamp(visibleArea.Width * MiniMapScale, 4, MiniMapCanvas.Width);
        var height = Math.Clamp(visibleArea.Height * MiniMapScale, 4, MiniMapCanvas.Height);
        Canvas.SetLeft(MiniMapViewportRect, left);
        Canvas.SetTop(MiniMapViewportRect, top);
        MiniMapViewportRect.Width = Math.Min(width, MiniMapCanvas.Width - left);
        MiniMapViewportRect.Height = Math.Min(height, MiniMapCanvas.Height - top);
    }

    private static bool IsPointInside(Point point, FrameworkElement element)
    {
        return point.X >= 0
            && point.Y >= 0
            && point.X <= element.ActualWidth
            && point.Y <= element.ActualHeight;
    }

    private void Item_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkspaceItemViewModel item
            && ViewModel is { } viewModel)
        {
            if (!item.IsSelected || viewModel.SelectedItems.Count <= 1)
            {
                viewModel.SelectedItem = item;
            }
        }
    }

    private async void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { } item)
        {
            var selectedItems = GetSelectedItemsForAction(item);
            if (selectedItems.Count > 1)
            {
                ViewModel?.OpenItemsCommand.Execute(selectedItems);
            }
            else if (item.Type == WorkspaceItemType.Note)
            {
                await ShowEditNoteDialogAsync(item);
            }
            else
            {
                ViewModel?.OpenItemCommand.Execute(item);
            }
        }
    }

    private void WorkspaceItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var browserMenu = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(static item =>
                string.Equals(item.Tag as string, "ExternalBrowserMenu", StringComparison.Ordinal));
        if (browserMenu is null)
        {
            return;
        }

        var urls = menu.PlacementTarget is FrameworkElement { DataContext: WorkspaceItemViewModel item }
            ? GetExternalBrowserUrls(item)
            : [];
        browserMenu.Visibility = urls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (urls.Count == 0)
        {
            return;
        }

        foreach (var choice in browserMenu.Items.OfType<MenuItem>())
        {
            choice.IsEnabled = choice.Tag is string browserName
                && Enum.TryParse<ExternalBrowserKind>(browserName, out var browserKind)
                && _isExternalBrowserAvailable?.Invoke(browserKind) == true;
        }

        browserMenu.IsEnabled = browserMenu.Items
            .OfType<MenuItem>()
            .Any(static item => item.IsEnabled);
    }

    private async void OpenExternalBrowserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string browserName }
            && Enum.TryParse<ExternalBrowserKind>(browserName, out var browserKind)
            && GetMenuItemContext(sender) is { } item
            && GetExternalBrowserUrls(item) is { Count: > 0 } urls
            && _openExternalBrowserAsync is not null)
        {
            await _openExternalBrowserAsync(urls, browserKind);
        }
    }

    private static IReadOnlyList<string> GetExternalBrowserUrls(WorkspaceItemViewModel item)
    {
        IEnumerable<string> candidates = item.Item switch
        {
            SessionGroupItem session => session.Tabs
                .Where(static tab => !tab.IsFolderTab)
                .Select(static tab => tab.Url),
            YouTubeMiniPlayerSetItem playerSet => playerSet.Players
                .Select(static player => player.Url),
            _ => [item.Url]
        };
        return candidates
            .Where(ExternalBrowserService.IsSupportedWebUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void QuickLookMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!IsQuickLookEnabled() || GetMenuItemContext(sender) is not { } item)
        {
            return;
        }

        ViewModel?.SetSelectedItems([item], item);
        await ExecuteCommandOrFallbackAsync(
            ContextCommandProvider.WorkspaceQuickLookActionId,
            parameter: null,
            ShowQuickLookForSelectionAsync);
    }

    private Task ExecuteCommandOrFallbackAsync(
        string actionId,
        object? parameter,
        Func<Task> fallback)
    {
        return _executeCommandAsync is null
            ? fallback()
            : _executeCommandAsync(actionId, parameter);
    }

    private void Item_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (element.ToolTip is ToolTip toolTip)
        {
            ConfigureWorkspaceToolTipPlacement(toolTip, element);
        }

        if (element.DataContext is WorkspaceItemViewModel item
            && ViewModel is { } viewModel)
        {
            _ = viewModel.EnsureItemThumbnailAsync(item);
            _ = viewModel.EnsureShellItemIconAsync(item);
        }
    }

    private void Item_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip toolTip }
            && toolTip.IsOpen
            && toolTip.Content is ScrollViewer scrollViewer)
        {
            ScrollWorkspaceToolTip(scrollViewer, e);
        }
    }

    private void WorkspaceToolTip_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip { Content: ScrollViewer scrollViewer } toolTip)
        {
            return;
        }

        DetachWorkspaceToolTipInput();
        _activeWorkspaceToolTip = toolTip;
        _activeWorkspaceToolTipScrollViewer = scrollViewer;
        InputManager.Current.PreProcessInput += WorkspaceToolTipInput_PreProcessInput;
    }

    private void WorkspaceToolTip_Closed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _activeWorkspaceToolTip))
        {
            DetachWorkspaceToolTipInput();
        }
    }

    private void WorkspaceToolTipInput_PreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (_activeWorkspaceToolTip is not { IsOpen: true } toolTip
            || _activeWorkspaceToolTipScrollViewer is not { } scrollViewer
            || e.StagingItem.Input is not MouseWheelEventArgs mouseWheelEvent
            || mouseWheelEvent.Handled
            || !IsPointerOverWorkspaceToolTipOrTarget(toolTip))
        {
            return;
        }

        ScrollWorkspaceToolTip(scrollViewer, mouseWheelEvent);
    }

    private static bool IsPointerOverWorkspaceToolTipOrTarget(ToolTip toolTip)
    {
        if (toolTip.IsMouseOver || toolTip.PlacementTarget is UIElement { IsMouseOver: true })
        {
            return true;
        }

        var pointerPosition = Mouse.GetPosition(toolTip);
        return pointerPosition.X >= 0
            && pointerPosition.Y >= 0
            && pointerPosition.X <= toolTip.ActualWidth
            && pointerPosition.Y <= toolTip.ActualHeight;
    }

    private void DetachWorkspaceToolTipInput()
    {
        InputManager.Current.PreProcessInput -= WorkspaceToolTipInput_PreProcessInput;
        _activeWorkspaceToolTip = null;
        _activeWorkspaceToolTipScrollViewer = null;
    }

    private void WorkspaceToolTip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ToolTip { Content: ScrollViewer scrollViewer })
        {
            ScrollWorkspaceToolTip(scrollViewer, e);
        }
    }

    private static void ScrollWorkspaceToolTip(ScrollViewer scrollViewer, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var scrollAmount = e.Delta / 120d * WorkspaceToolTipWheelScrollStep;
        var targetOffset = Math.Clamp(
            scrollViewer.VerticalOffset - scrollAmount,
            0,
            scrollViewer.ScrollableHeight);
        if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) < 0.1)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        e.Handled = true;
    }

    private void ConfigureWorkspaceToolTipPlacement(ToolTip toolTip, FrameworkElement placementTarget)
    {
        var workAreaWidth = SystemParameters.WorkArea.Width;
        var workAreaHeight = SystemParameters.WorkArea.Height;
        var availableSideWidth = workAreaWidth - (WorkspaceToolTipScreenMargin * 2);
        var preferRight = true;

        if (placementTarget.IsVisible
            && placementTarget.ActualWidth > 0
            && placementTarget.ActualHeight > 0
            && PresentationSource.FromVisual(placementTarget) is not null)
        {
            var targetTopLeft = placementTarget.PointToScreen(new Point(0, 0));
            var targetBottomRight = placementTarget.PointToScreen(
                new Point(placementTarget.ActualWidth, placementTarget.ActualHeight));
            var monitorPoint = new NativePoint
            {
                X = (int)Math.Round((targetTopLeft.X + targetBottomRight.X) / 2),
                Y = (int)Math.Round((targetTopLeft.Y + targetBottomRight.Y) / 2)
            };
            var monitor = MonitorFromPoint(monitorPoint, MonitorDefaultToNearest);
            var monitorInfo = new NativeMonitorInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
            };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                var dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                workAreaWidth = (monitorInfo.Work.Right - monitorInfo.Work.Left) / dpiScaleX;
                workAreaHeight = (monitorInfo.Work.Bottom - monitorInfo.Work.Top) / dpiScaleY;
                var leftSpace = (targetTopLeft.X - monitorInfo.Work.Left) / dpiScaleX
                    - WorkspaceToolTipScreenMargin
                    - WorkspaceToolTipTargetGap;
                var rightSpace = (monitorInfo.Work.Right - targetBottomRight.X) / dpiScaleX
                    - WorkspaceToolTipScreenMargin
                    - WorkspaceToolTipTargetGap;
                preferRight = rightSpace >= leftSpace;
                availableSideWidth = Math.Max(leftSpace, rightSpace);
            }
        }

        var screenMargin = WorkspaceToolTipScreenMargin * 2;
        var availableWidth = Math.Max(
            1,
            Math.Min(workAreaWidth - screenMargin, availableSideWidth));
        var availableHeight = Math.Max(1, workAreaHeight - screenMargin);
        toolTip.MaxWidth = Math.Min(WorkspaceToolTipMaxWidth, availableWidth);
        toolTip.MaxHeight = Math.Min(WorkspaceToolTipMaxHeight, availableHeight);
        toolTip.PlacementTarget = placementTarget;
        toolTip.Placement = PlacementMode.Custom;
        toolTip.HorizontalOffset = 0;
        toolTip.VerticalOffset = 0;
        toolTip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            CreateWorkspaceToolTipPlacements(popupSize, targetSize, preferRight);
    }

    internal static CustomPopupPlacement[] CreateWorkspaceToolTipPlacements(
        Size popupSize,
        Size targetSize,
        bool preferRight)
    {
        var sideTop = (targetSize.Height - popupSize.Height) / 2;
        var horizontalLeft = (targetSize.Width - popupSize.Width) / 2;
        var right = new CustomPopupPlacement(
            new Point(targetSize.Width + WorkspaceToolTipTargetGap, sideTop),
            PopupPrimaryAxis.Horizontal);
        var left = new CustomPopupPlacement(
            new Point(-popupSize.Width - WorkspaceToolTipTargetGap, sideTop),
            PopupPrimaryAxis.Horizontal);
        var below = new CustomPopupPlacement(
            new Point(horizontalLeft, targetSize.Height + WorkspaceToolTipTargetGap),
            PopupPrimaryAxis.Vertical);
        var above = new CustomPopupPlacement(
            new Point(horizontalLeft, -popupSize.Height - WorkspaceToolTipTargetGap),
            PopupPrimaryAxis.Vertical);

        return preferRight
            ? [right, left, below, above]
            : [left, right, below, above];
    }

    private void OpenInNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { } item)
        {
            ViewModel?.OpenItemsInNewWindowCommand.Execute(GetSelectedItemsForAction(item));
        }
    }

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { } item)
        {
            ViewModel?.DuplicateItemCommand.Execute(item);
        }
    }

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { } item)
        {
            return;
        }

        var dialog = new WorkspaceTextInputWindow(
            "名前を変更",
            "新しい名前",
            item.Title)
        {
            Owner = Window.GetWindow(this)
        };

        await ModelessWindowService.ShowAsync(dialog);
        if (dialog.IsAccepted
            && IsLoaded
            && ViewModel is { } viewModel
            && viewModel.Items.Contains(item))
        {
            viewModel.RenameItemCommand.Execute(new WorkspaceRenameRequest(item, dialog.Value));
        }
    }

    private void ShowShellItemInFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { Item: ShellItem } item)
        {
            ViewModel?.ShowShellItemInFolderCommand.Execute(item);
        }
    }

    private void ConvertShellTextFileToNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { CanConvertShellTextFileToNote: true } item)
        {
            ViewModel?.ConvertShellTextFileToNoteCommand.Execute(item);
        }
    }

    private void OpenShellFolderInTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { Item: ShellItem { Kind: ShellItemKind.Directory } shellItem })
        {
            OpenShellFolderInTab?.Invoke(shellItem.TargetPath);
        }
    }

    private void OpenLocalVideoInMiniPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { Item: ShellItem { Kind: ShellItemKind.File } shellItem }
            && WorkspaceShellItemService.IsVideoPath(shellItem.TargetPath))
        {
            OpenLocalVideoInMiniPlayer?.Invoke(shellItem.TargetPath);
        }
    }

    private void CopyShellPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem shellItem })
        {
            return;
        }

        try
        {
            Clipboard.SetText(shellItem.TargetPath);
        }
        catch (ExternalException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "クリップボードを使用できませんでした。少し待ってから再試行してください。",
                "パスをコピー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RelinkShellItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem shellItem } item)
        {
            return;
        }

        var selectedPath = SelectShellTarget(shellItem);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ViewModel?.RelinkShellItemCommand.Execute(new WorkspaceShellPathRequest(item, selectedPath));
        }
    }

    private async void CreateShellDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem { Kind: ShellItemKind.Directory } shellItem } item)
        {
            return;
        }

        var dialog = new WorkspaceTextInputWindow("実フォルダーを作成", "フォルダー名", "新しいフォルダー")
        {
            Owner = Window.GetWindow(this)
        };
        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted
            || !IsLoaded
            || ViewModel is not { } viewModel
            || !viewModel.Items.Contains(item)
            || !ConfirmFileOperation($"次の場所に実フォルダーを作成します。\n\n{shellItem.TargetPath}\n\n名前: {dialog.Value}"))
        {
            return;
        }

        viewModel.CreateShellDirectoryCommand.Execute(new WorkspaceShellNameRequest(item, dialog.Value));
    }

    private async void RenameShellTargetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem shellItem } item)
        {
            return;
        }

        var currentName = WorkspaceShellItemService.GetDisplayName(shellItem.TargetPath);
        var dialog = new WorkspaceTextInputWindow("実ファイル名を変更", "拡張子を含む新しい名前", currentName)
        {
            Owner = Window.GetWindow(this)
        };
        await ModelessWindowService.ShowAsync(dialog);
        if (!dialog.IsAccepted
            || !IsLoaded
            || ViewModel is not { } viewModel
            || !viewModel.Items.Contains(item)
            || !ConfirmFileOperation($"実際のファイルまたはフォルダー名を変更します。\n\n{currentName}\n→ {dialog.Value}"))
        {
            return;
        }

        viewModel.RenameShellTargetCommand.Execute(new WorkspaceShellNameRequest(item, dialog.Value));
    }

    private void CopyShellItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExecuteShellTransfer(sender, isMove: false);
    }

    private void MoveShellItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExecuteShellTransfer(sender, isMove: true);
    }

    private void ExecuteShellTransfer(object sender, bool isMove)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem shellItem } item)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = isMove ? "移動先フォルダーを選択" : "コピー先フォルダーを選択",
            InitialDirectory = GetExistingParentDirectory(shellItem.TargetPath),
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var operation = isMove ? "移動" : "コピー";
        if (!ConfirmFileOperation($"実際のファイルまたはフォルダーを{operation}します。\n\n{shellItem.TargetPath}\n→ {dialog.FolderName}"))
        {
            return;
        }

        var request = new WorkspaceShellPathRequest(item, dialog.FolderName);
        if (isMove)
        {
            ViewModel?.MoveShellItemCommand.Execute(request);
        }
        else
        {
            ViewModel?.CopyShellItemCommand.Execute(request);
        }
    }

    private void RecycleShellItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { Item: ShellItem shellItem } item
            || !ConfirmFileOperation(
                $"実際のファイルまたはフォルダーをごみ箱へ移動します。\n\n{shellItem.TargetPath}\n\nWorkspace カードは参照切れとして残ります。"))
        {
            return;
        }

        ViewModel?.RecycleShellItemCommand.Execute(item);
    }

    private void UndoShellOperationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.UndoShellOperationCommand.Execute(null);
    }

    private void OpenRecycleBinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenRecycleBinCommand.Execute(null);
    }

    private string? SelectShellTarget(ShellItem shellItem)
    {
        if (shellItem.Kind == ShellItemKind.Directory)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "参照先フォルダーを選択",
                InitialDirectory = GetExistingParentDirectory(shellItem.TargetPath),
                Multiselect = false
            };
            return folderDialog.ShowDialog(Window.GetWindow(this)) == true
                ? folderDialog.FolderName
                : null;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = "参照先ファイルを選択",
            InitialDirectory = GetExistingParentDirectory(shellItem.TargetPath),
            CheckFileExists = true,
            Multiselect = false
        };
        return fileDialog.ShowDialog(Window.GetWindow(this)) == true
            ? fileDialog.FileName
            : null;
    }

    private static string GetExistingParentDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private bool ConfirmFileOperation(string message)
    {
        return MessageBox.Show(
            Window.GetWindow(this),
            message,
            "実ファイル操作の確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void SaveSelectedTabsToFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { } item)
        {
            ViewModel?.SaveSelectedTabsToFolderCommand.Execute(item);
        }
    }

    private void SaveAllTabsToFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is { } item)
        {
            ViewModel?.SaveAllTabsToFolderCommand.Execute(item);
        }
    }

    private async void EditNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { } item
            || item.Type != WorkspaceItemType.Note)
        {
            return;
        }

        await ShowEditNoteDialogAsync(item);
    }

    private async Task ShowEditNoteDialogAsync(WorkspaceItemViewModel item, Window? owner = null)
    {
        owner ??= Window.GetWindow(this);
        if (EditWorkspaceNoteAsync is not null)
        {
            await EditWorkspaceNoteAsync(item.Item.WorkspaceId, item.Id, owner);
            return;
        }

        var dialog = CreateWorkspaceNoteEditWindow(
            item.NoteEditorTitle,
            item.Note,
            item.Id,
            item.NoteFontSize);
        var viewModel = ViewModel;
        if (viewModel is not null && viewModel.Items.Contains(item))
        {
            dialog.SaveAndCloseAsync = async (content, fontSize) =>
            {
                if (!IsLoaded
                    || !ReferenceEquals(ViewModel, viewModel))
                {
                    return false;
                }

                return await viewModel.TryEditNoteAsync(new WorkspaceNoteEditRequest(
                    item,
                    content,
                    fontSize), dialog.ReportMissingTarget);
            };
            dialog.SaveAsNewAsync = (content, fontSize) => viewModel.TrySaveNoteAsNewAsync(
                new WorkspaceNoteEditRequest(item, content, fontSize));
        }

        dialog.Owner = owner;

        await ModelessWindowService.ShowAsync(dialog);
        TrySaveWorkspaceNoteEditorWindowPlacement(dialog, item.Id);
    }

    internal static WorkspaceNoteEditWindow CreateWorkspaceNoteEditWindow(
        string title,
        string initialContent,
        Guid? noteId,
        double initialFontSize = WorkspaceNoteFontSizeProfile.DefaultFontSize)
    {
        var settings = new SettingsService().Load();
        var placement = settings.WorkspaceNoteEditorWindowPlacement;
        if (noteId is { } id
            && id != Guid.Empty
            && settings.WorkspaceNoteEditorWindowPlacements.TryGetValue(id, out var itemPlacement))
        {
            placement = itemPlacement;
        }

        return new WorkspaceNoteEditWindow(
            title,
            initialContent,
            placement,
            initialFontSize);
    }

    internal static void SaveWorkspaceNoteEditorWindowPlacement(
        WorkspaceNoteEditWindow dialog,
        Guid? noteId)
    {
        if (WorkspaceNoteEditorWindowPlacementProfile.Normalize(dialog.CapturedWindowPlacement) is not { } captured)
        {
            return;
        }

        foreach (var window in Application.Current.Windows.OfType<MainWindow>())
        {
            window.UpdateWorkspaceNoteEditorWindowPlacement(noteId, captured);
        }

        var settings = new SettingsService().Load();
        var currentDefault = WorkspaceNoteEditorWindowPlacementProfile.Normalize(
            settings.WorkspaceNoteEditorWindowPlacement);
        var defaultMatches = PlacementsMatch(currentDefault, captured);
        var itemMatches = noteId is not { } id
            || id == Guid.Empty
            || settings.WorkspaceNoteEditorWindowPlacements.TryGetValue(id, out var currentItem)
                && PlacementsMatch(currentItem, captured);
        if (defaultMatches && itemMatches)
        {
            return;
        }

        settings.WorkspaceNoteEditorWindowPlacement = captured;
        if (noteId is { } itemId && itemId != Guid.Empty)
        {
            settings.WorkspaceNoteEditorWindowPlacements[itemId] = captured;
        }

        SettingsService.Save(settings);
    }

    private static void TrySaveWorkspaceNoteEditorWindowPlacement(
        WorkspaceNoteEditWindow dialog,
        Guid? noteId)
    {
        try
        {
            SaveWorkspaceNoteEditorWindowPlacement(dialog, noteId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("メモ編集Windowの位置を保存できません: " + ex.Message);
        }
    }

    private static bool PlacementsMatch(
        WorkspaceNoteEditorWindowPlacementSettings? left,
        WorkspaceNoteEditorWindowPlacementSettings right)
    {
        return left is not null
            && Math.Abs(left.Left - right.Left) < 0.5
            && Math.Abs(left.Top - right.Top) < 0.5
            && Math.Abs(left.Width - right.Width) < 0.5
            && Math.Abs(left.Height - right.Height) < 0.5;
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetMenuItemContext(sender) is not { } item)
        {
            return;
        }

        ConfirmAndDeleteItems(GetSelectedItemsForAction(item));
    }

    private void WorkspaceCanvasView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleQuickLookKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetRangeSelectionModeVisual(isActive: true, IsPointerOverRangeSelectionSurface());
        }
        else
        {
            UpdateRangeSelectionModeVisual();
        }

        if (ViewModel?.SelectedItem is not { } item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            var itemsToOpen = ViewModel.SelectedItems.Where(ViewModel.Items.Contains).ToList();
            if (itemsToOpen.Count > 1)
            {
                ViewModel.OpenItemsCommand.Execute(itemsToOpen);
            }
            else
            {
                ViewModel.OpenItemCommand.Execute(item);
            }

            e.Handled = true;
            return;
        }

        if (e.Key != Key.Delete)
        {
            return;
        }

        var selectedItems = ViewModel.SelectedItems.Where(ViewModel.Items.Contains).ToList();
        ConfirmAndDeleteItems(selectedItems.Count == 0 ? [item] : selectedItems);

        e.Handled = true;
    }

    private bool IsQuickLookEnabled()
    {
        return _quickLookSettings.IsEnabled && _quickLookSettings.EnableInWorkspace;
    }

    public async Task ShowQuickLookForSelectionAsync()
    {
        if (!IsQuickLookEnabled())
        {
            return;
        }

        var selectedItems = _quickLookItemSource.SelectedItems;
        var items = selectedItems.Count > 0
            ? selectedItems
            : _quickLookItemSource.Current is { } current ? [current] : [];
        if (items.Count == 0)
        {
            return;
        }

        QuickLookOpening?.Invoke(this, EventArgs.Empty);
        await QuickLookPreviewHost.ShowAsync(new QuickLookRequest(
            items,
            _quickLookItemSource.SourceId,
            _quickLookItemSource));
    }

    private void RefreshOpenQuickLook()
    {
        if (QuickLookPreviewHost.IsOpen)
        {
            _ = ShowQuickLookForSelectionAsync();
        }
    }

    private IReadOnlyList<QuickLookItem> CreateQuickLookItems()
    {
        return ViewModel?.Items.Select(CreateQuickLookItem).ToList() ?? [];
    }

    private IReadOnlyList<QuickLookItem> CreateSelectedQuickLookItems()
    {
        if (ViewModel is not { } viewModel)
        {
            return [];
        }

        return viewModel.SelectedItems
            .Where(viewModel.Items.Contains)
            .Select(CreateQuickLookItem)
            .ToList();
    }

    private int GetQuickLookCurrentIndex()
    {
        return ViewModel is { SelectedItem: { } selectedItem } viewModel
            ? viewModel.Items.IndexOf(selectedItem)
            : -1;
    }

    private void SelectQuickLookIndex(int index)
    {
        if (ViewModel is not { } viewModel || index < 0 || index >= viewModel.Items.Count)
        {
            return;
        }

        var item = viewModel.Items[index];
        viewModel.SetSelectedItems([item], item);
    }

    private QuickLookItem CreateQuickLookItem(WorkspaceItemViewModel item)
    {
        return QuickLookItemFactory.FromWorkspaceItem(
            item.Item,
            _quickLookItemSource.SourceId,
            item.Title,
            CreateQuickLookActions(item));
    }

    private IReadOnlyList<QuickLookAction> CreateQuickLookActions(WorkspaceItemViewModel item)
    {
        if (item.Type == WorkspaceItemType.Note)
        {
            return
            [
                new QuickLookAction(
                    "workspace.note.append",
                    "追記",
                    () => ShowEditNoteDialogAsync(item),
                    IsPrimary: true)
            ];
        }

        return
        [
            new QuickLookAction(
                "workspace.open",
                "開く",
                () =>
                {
                    ViewModel?.OpenItemCommand.Execute(item);
                    return Task.CompletedTask;
                },
                IsPrimary: true)
        ];
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase
            or PasswordBox
            or ComboBox { IsEditable: true };
    }

    private void WorkspaceCanvasView_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateRangeSelectionModeVisual));
    }

    private IReadOnlyList<WorkspaceItemViewModel> GetSelectedItemsForAction(WorkspaceItemViewModel item)
    {
        if (ViewModel is not { } viewModel || !item.IsSelected)
        {
            return [item];
        }

        var selectedItems = viewModel.SelectedItems.Where(viewModel.Items.Contains).ToList();
        return selectedItems.Count == 0 ? [item] : selectedItems;
    }

    private void ConfirmAndDeleteItems(IReadOnlyList<WorkspaceItemViewModel> items)
    {
        if (items.Count == 0 || ViewModel is not { } viewModel)
        {
            return;
        }

        var message = items.Count == 1
            ? $"「{items[0].Title}」を削除しますか？"
            : $"選択した {items.Count} 件のカードを削除しますか？";
        var result = MessageBox.Show(
            Window.GetWindow(this),
            message,
            items.Count == 1 ? "Workspace Item の削除" : "Workspace Cards の一括削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (items.Count == 1)
        {
            viewModel.DeleteItemCommand.Execute(items[0]);
        }
        else
        {
            viewModel.DeleteItemsCommand.Execute(items);
        }
    }

    private static WorkspaceItemViewModel? GetMenuItemContext(object sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        var current = menuItem.Parent;
        while (current is not null)
        {
            if (current is ContextMenu contextMenu)
            {
                return contextMenu.PlacementTarget is FrameworkElement target
                    ? target.DataContext as WorkspaceItemViewModel
                    : null;
            }

            current = current is MenuItem parentMenuItem
                ? parentMenuItem.Parent
                : null;
        }

        return null;
    }

    private static bool IsItemVisual(DependencyObject? source)
    {
        return FindVisualParent<ContentPresenter>(source) is not null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T parent)
            {
                return parent;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static bool IsFolderDropTarget(object sender)
    {
        return (sender as FrameworkElement)?.DataContext is WorkspaceItemViewModel
        {
            Type: WorkspaceItemType.Folder
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect Work;

        public uint Flags;
    }
}
