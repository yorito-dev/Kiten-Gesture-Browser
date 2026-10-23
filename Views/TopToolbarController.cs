using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record TopToolbarElements(
    FrameworkElement TitleBar,
    RowDefinition ToolbarRow,
    FrameworkElement TopToolbar,
    FrameworkElement AddressBox,
    ScrollViewer TopToolbarScrollViewer,
    Button TopToolbarScrollLeftButton,
    Button TopToolbarScrollRightButton,
    Grid TopToolbarGrid,
    ColumnDefinition AddressToolbarColumn,
    FrameworkElement AddressArea,
    FrameworkElement AddressAreaOutline);

internal interface ITopToolbarHost
{
    bool IsAutoHideEnabled { get; }
    bool IsApplicationFullscreenActive { get; }
    bool HasOpenSurface { get; }
}

// ツールバーの開閉と横幅・スクロールを所有し、機能別Popupや設定保存はWindowへ残す。
internal sealed class TopToolbarController : IDisposable
{
    private Button? _dismissedMenuButton;
    private int _menuDismissPressTimestamp;

    internal void RecordMenuDismissPress(Button button, Point position, int timestamp)
    {
        if (!button.IsEnabled || !new Rect(button.RenderSize).Contains(position)) return;
        _dismissedMenuButton = button;
        _menuDismissPressTimestamp = timestamp;
    }

    internal bool ConsumeMenuDismissPress(Button button, int timestamp)
    {
        var consume = ReferenceEquals(button, _dismissedMenuButton)
            && timestamp == _menuDismissPressTimestamp;
        _dismissedMenuButton = null;
        return consume;
    }

    internal static bool CloseSurfaceOnButtonPress(FrameworkElement button, Point position, Action close)
    {
        if (!button.IsEnabled || !new Rect(button.RenderSize).Contains(position)) return false;

        // 外側クリックで既に閉じていても、元ボタンへの同じ押下による再表示を防ぐ。
        close();
        return true;
    }

    internal static void ToggleButtonContextMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = !menu.IsOpen;
    }

    private const double TopToolbarVisibleHeight = 40;
    private const double TopToolbarHiddenHeight = 4;
    private const double TopToolbarRevealDistance = 12;
    internal const double TopToolbarScrollStep = 180;
    private const double TopToolbarOverflowTolerance = 0.5;
    private const double AddressAreaDefaultWidth = 312;
    private const double AddressAreaMinimumWidth = 220;
    private const double AddressAreaMaximumWidth = 720;
    private const double AddressAreaRightSpacing = 8;

    private readonly TopToolbarElements _elements;
    private readonly ITopToolbarHost _host;
    private readonly OwnedDispatcherTimer _topToolbarAutoHideTimer;
    private bool _overflowUpdateQueued;
    private bool _disposed;

    internal TopToolbarController(TopToolbarElements elements, ITopToolbarHost host, OwnedDispatcherTimer autoHideTimer)
    {
        _elements = elements;
        _host = host;
        _topToolbarAutoHideTimer = autoHideTimer;
        _topToolbarAutoHideTimer.Tick += AutoHideTimer_Tick;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topToolbarAutoHideTimer.Stop();
        _topToolbarAutoHideTimer.Tick -= AutoHideTimer_Tick;
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _topToolbarAutoHideTimer.Stop();
        if (!_disposed) HideTopToolbarIfAllowed();
    }

    internal void HandlePointerMoved(Point position)
    {
        if (_disposed || !_host.IsAutoHideEnabled) return;
        if (position.Y <= _elements.TitleBar.ActualHeight + TopToolbarRevealDistance)
        {
            ShowTopToolbar();
            return;
        }

        QueueTopToolbarAutoHide();
    }

    internal bool HandleMouseWheel(int delta)
    {
        if (_disposed || _elements.TopToolbarScrollRightButton.Visibility != Visibility.Visible) return false;
        ScrollTopToolbarBy(-delta);
        return true;
    }

    internal void QueueOverflowUpdate(DispatcherPriority priority)
    {
        if (_disposed || _overflowUpdateQueued)
        {
            return;
        }

        _overflowUpdateQueued = true;
        _elements.TopToolbar.Dispatcher.BeginInvoke(priority, () =>
        {
            _overflowUpdateQueued = false;
            if (!_disposed) UpdateTopToolbarOverflowState();
        });
    }

    internal void ScrollTopToolbarBy(double offset)
    {
        var targetOffset = Math.Clamp(
            _elements.TopToolbarScrollViewer.HorizontalOffset + offset,
            0,
            _elements.TopToolbarScrollViewer.ScrollableWidth);
        _elements.TopToolbarScrollViewer.ScrollToHorizontalOffset(targetOffset);
    }

    internal void UpdateTopToolbarOverflowState()
    {
        if (_disposed) return;

        UpdateAdaptiveTopToolbarLayout();
        var viewportWidth = GetTopToolbarViewportWidth();
        var hasOverflow = _elements.TopToolbarScrollViewer.ExtentWidth
            > viewportWidth + TopToolbarOverflowTolerance;
        var scrollButtonVisibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        _elements.TopToolbarScrollLeftButton.Visibility = scrollButtonVisibility;
        _elements.TopToolbarScrollRightButton.Visibility = scrollButtonVisibility;

        if (!hasOverflow)
        {
            if (_elements.TopToolbarScrollViewer.HorizontalOffset > TopToolbarOverflowTolerance)
            {
                _elements.TopToolbarScrollViewer.ScrollToLeftEnd();
            }

            return;
        }

        _elements.TopToolbarScrollLeftButton.IsEnabled =
            _elements.TopToolbarScrollViewer.HorizontalOffset > TopToolbarOverflowTolerance;
        _elements.TopToolbarScrollRightButton.IsEnabled =
            _elements.TopToolbarScrollViewer.HorizontalOffset
            < _elements.TopToolbarScrollViewer.ScrollableWidth - TopToolbarOverflowTolerance;
    }

    internal void QueueTopToolbarLayoutAfterPageZoomVisibilityChange()
    {
        _elements.TopToolbar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_disposed) return;
            UpdateTopToolbarOverflowState();
            _elements.TopToolbar.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (_disposed) return;
                _elements.AddressAreaOutline.InvalidateMeasure();
                _elements.AddressAreaOutline.InvalidateArrange();
                _elements.AddressAreaOutline.InvalidateVisual();
            });
        });
    }

    private void UpdateAdaptiveTopToolbarLayout()
    {
        var viewportWidth = GetTopToolbarViewportWidth();
        if (viewportWidth <= 0)
        {
            return;
        }

        var horizontalMargin = _elements.TopToolbarGrid.Margin.Left + _elements.TopToolbarGrid.Margin.Right;
        var otherColumnsWidth = _elements.TopToolbarGrid.ColumnDefinitions
            .Where(column => !ReferenceEquals(column, _elements.AddressToolbarColumn))
            .Sum(static column => column.ActualWidth);
        var gridWidth = CalculateAdaptiveTopToolbarGridWidth(
            viewportWidth,
            otherColumnsWidth,
            horizontalMargin,
            _elements.AddressArea.Visibility == Visibility.Visible);
        if (!double.IsFinite(_elements.TopToolbarGrid.Width)
            || Math.Abs(_elements.TopToolbarGrid.Width - gridWidth) > TopToolbarOverflowTolerance)
        {
            _elements.TopToolbarGrid.Width = gridWidth;
        }

        if (_elements.AddressArea.Visibility != Visibility.Visible)
        {
            return;
        }

        var addressWidth = CalculateAdaptiveAddressAreaWidth(
            gridWidth + horizontalMargin,
            otherColumnsWidth,
            horizontalMargin);
        var outlineWidth = Math.Max(0, addressWidth - 1);
        if (!double.IsFinite(_elements.AddressAreaOutline.Width)
            || Math.Abs(_elements.AddressAreaOutline.Width - outlineWidth) > TopToolbarOverflowTolerance)
        {
            _elements.AddressAreaOutline.Width = outlineWidth;
        }

        if (Math.Abs(_elements.AddressArea.Width - addressWidth) > TopToolbarOverflowTolerance)
        {
            _elements.AddressArea.Width = addressWidth;
        }
    }

    private double GetTopToolbarViewportWidth()
    {
        var viewportWidth = _elements.TopToolbarScrollViewer.ViewportWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = _elements.TopToolbarScrollViewer.ActualWidth;
        }

        return double.IsFinite(viewportWidth) && viewportWidth > 0
            ? viewportWidth
            : 0;
    }

    internal static double CalculateAdaptiveAddressAreaWidth(
        double viewportWidth,
        double otherColumnsWidth,
        double horizontalMargin)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return AddressAreaDefaultWidth;
        }

        var normalizedOtherColumnsWidth = double.IsFinite(otherColumnsWidth)
            ? Math.Max(0, otherColumnsWidth)
            : 0;
        var normalizedHorizontalMargin = double.IsFinite(horizontalMargin)
            ? Math.Max(0, horizontalMargin)
            : 0;
        return Math.Clamp(
            viewportWidth - normalizedOtherColumnsWidth - normalizedHorizontalMargin - AddressAreaRightSpacing,
            AddressAreaMinimumWidth,
            AddressAreaMaximumWidth);
    }

    internal static double CalculateAdaptiveTopToolbarGridWidth(
        double viewportWidth,
        double otherColumnsWidth,
        double horizontalMargin,
        bool isAddressAreaVisible)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return AddressAreaDefaultWidth;
        }

        var normalizedOtherColumnsWidth = double.IsFinite(otherColumnsWidth)
            ? Math.Max(0, otherColumnsWidth)
            : 0;
        var normalizedHorizontalMargin = double.IsFinite(horizontalMargin)
            ? Math.Max(0, horizontalMargin)
            : 0;
        var availableWidth = Math.Max(0, viewportWidth - normalizedHorizontalMargin);
        var requiredWidth = normalizedOtherColumnsWidth
            + (isAddressAreaVisible ? AddressAreaMinimumWidth + AddressAreaRightSpacing : 0);
        return Math.Max(availableWidth, requiredWidth);
    }

    internal void ApplyTopToolbarAutoHideState()
    {
        if (_disposed) return;

        if (!_host.IsAutoHideEnabled)
        {
            _topToolbarAutoHideTimer.Stop();
            ShowTopToolbar();
            return;
        }

        if (ShouldKeepTopToolbarVisible())
        {
            ShowTopToolbar();
            return;
        }

        HideTopToolbar();
    }

    internal void ShowTopToolbar()
    {
        if (_disposed) return;

        _topToolbarAutoHideTimer.Stop();
        if (KeepTopToolbarCollapsedForYouTubeFullscreen())
        {
            return;
        }

        _elements.ToolbarRow.Height = new GridLength(TopToolbarVisibleHeight);
        _elements.TopToolbar.Opacity = 1;
        _elements.TopToolbar.IsHitTestVisible = true;
    }

    internal void QueueTopToolbarAutoHide()
    {
        if (_disposed) return;

        if (!_host.IsAutoHideEnabled || ShouldKeepTopToolbarVisible())
        {
            ShowTopToolbar();
            return;
        }

        _topToolbarAutoHideTimer.Stop();
        _topToolbarAutoHideTimer.Start();
    }

    private void HideTopToolbarIfAllowed()
    {
        if (!_host.IsAutoHideEnabled || ShouldKeepTopToolbarVisible())
        {
            ShowTopToolbar();
            return;
        }

        HideTopToolbar();
    }

    private void HideTopToolbar()
    {
        if (KeepTopToolbarCollapsedForYouTubeFullscreen())
        {
            return;
        }

        _elements.ToolbarRow.Height = new GridLength(TopToolbarHiddenHeight);
        _elements.TopToolbar.Opacity = 0.18;
        _elements.TopToolbar.IsHitTestVisible = false;
    }

    private bool KeepTopToolbarCollapsedForYouTubeFullscreen()
    {
        if (!_host.IsApplicationFullscreenActive)
        {
            return false;
        }

        _topToolbarAutoHideTimer.Stop();
        _elements.ToolbarRow.Height = new GridLength(0);
        return true;
    }

    private bool ShouldKeepTopToolbarVisible()
    {
        return _elements.AddressBox.IsKeyboardFocusWithin
            || _elements.TopToolbar.IsKeyboardFocusWithin
            || _elements.TopToolbar.IsMouseOver
            || _host.HasOpenSurface;
    }
}
