using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser.Views;

// status text、表示期限、Popup配置、表示アニメーションをWindow単位で所有する。
// tab move undo の状態と期限は既存 TabMoveUndoController のまま保持する。
internal interface IStatusPresentationHost
{
    bool IsClosing { get; }
    bool ShowStatusText { get; }
    bool IsTabMoveUndoVisible { get; }
    void ReapplyAlwaysOnTopIfNeeded();
}

internal sealed record StatusPresentationElements(
    FrameworkElement RootGrid,
    RowDefinition BottomResizeRow,
    Popup StatusPopup,
    Border StatusBar,
    TextBlock StatusText,
    TranslateTransform StatusBarTranslateTransform);

internal sealed class StatusPresentationController : IDisposable
{
    private const int AnimationDurationMilliseconds = 140;
    private readonly StatusPresentationElements _elements;
    private readonly OwnedDispatcherTimer _clearTimer;
    private readonly IStatusPresentationHost _host;
    private int _animationVersion;
    private bool _disposed;

    internal StatusPresentationController(
        StatusPresentationElements elements,
        OwnedDispatcherTimer clearTimer,
        IStatusPresentationHost host)
    {
        _elements = elements;
        _clearTimer = clearTimer;
        _host = host;
        _clearTimer.Tick += ClearTimer_Tick;
    }

    internal string StatusText => _elements.StatusText.Text;

    internal void RefreshVisibility()
    {
        if (!_disposed)
        {
            UpdateStatusBarVisibility();
        }
    }

    internal void SetStatus(string message, bool autoClear = false)
    {
        if (_disposed)
        {
            return;
        }

        _elements.StatusText.Text = message;
        UpdateStatusBarVisibility();

        if (!autoClear || _host.IsClosing)
        {
            _clearTimer.Stop();
            return;
        }

        _clearTimer.Stop();
        _clearTimer.Start();
    }

    internal void ClearStatus()
    {
        if (_disposed)
        {
            return;
        }

        _elements.StatusText.Text = string.Empty;
        UpdateStatusBarVisibility();
    }

    internal void ClearStatusIfCurrent(string text)
    {
        if (_disposed || !string.Equals(StatusText, text, StringComparison.Ordinal))
        {
            return;
        }

        _clearTimer.Stop();
        ClearStatus();
    }

    internal void UpdatePopupPlacement()
    {
        if (_disposed || !_elements.StatusPopup.IsOpen)
        {
            return;
        }

        var bottomResizeHeight = _elements.BottomResizeRow.Height.IsAbsolute
            ? _elements.BottomResizeRow.Height.Value
            : _elements.BottomResizeRow.ActualHeight;
        var verticalOffset = Math.Max(
            0,
            _elements.RootGrid.ActualHeight
                - _elements.StatusBar.Height
                - bottomResizeHeight);
        _elements.StatusPopup.HorizontalOffset = 0;
        _elements.StatusPopup.VerticalOffset = verticalOffset;
        PopupWindowOrderService.ApplyBounds(
            _elements.StatusPopup,
            _elements.StatusBar,
            _elements.RootGrid,
            new Rect(
                0,
                verticalOffset,
                Math.Max(1, _elements.RootGrid.ActualWidth),
                _elements.StatusBar.Height),
            PopupWindowOrderPolicy.Normal);
        _host.ReapplyAlwaysOnTopIfNeeded();
    }

    internal void HandlePopupOpened()
    {
        if (_disposed)
        {
            return;
        }

        UpdatePopupPlacement();
        _elements.RootGrid.Dispatcher.BeginInvoke(
            UpdatePopupPlacement,
            DispatcherPriority.ContextIdle);
    }

    internal void EnsurePopupNotTopmost()
    {
        if (_disposed)
        {
            return;
        }

        PopupWindowOrderService.Apply(
            _elements.StatusPopup,
            _elements.StatusBar,
            PopupWindowOrderPolicy.Normal);
        _host.ReapplyAlwaysOnTopIfNeeded();
    }

    internal void ClosePopupImmediately()
    {
        if (_disposed)
        {
            return;
        }

        _animationVersion++;
        _elements.StatusBarTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _elements.StatusBarTranslateTransform.Y = _elements.StatusBar.Height;
        _elements.StatusBar.Visibility = Visibility.Collapsed;
        _elements.StatusPopup.IsOpen = false;
    }

    private void ClearTimer_Tick(object? sender, EventArgs e)
    {
        _clearTimer.Stop();
        ClearStatus();
    }

    private void UpdateStatusBarVisibility()
    {
        // Undo Button を含む即時表示判定は、hide完了時の文字だけの判定と意図的に異なる。
        var showStatusText = _host.ShowStatusText;
        var showStatusBar = _host.IsTabMoveUndoVisible
            || (showStatusText && !string.IsNullOrWhiteSpace(StatusText));
        if (showStatusBar)
        {
            ShowStatusBar();
        }
        else
        {
            HideStatusBar();
        }
    }

    private void ShowStatusBar()
    {
        if (_elements.StatusBar.Visibility == Visibility.Visible && _elements.StatusPopup.IsOpen)
        {
            return;
        }

        _animationVersion++;
        _elements.StatusBar.Visibility = Visibility.Visible;
        _elements.StatusPopup.IsOpen = true;
        UpdatePopupPlacement();
        AnimateStatusBarTo(0);
    }

    private void HideStatusBar()
    {
        if (_elements.StatusBar.Visibility != Visibility.Visible)
        {
            return;
        }

        var animationVersion = ++_animationVersion;
        AnimateStatusBarTo(_elements.StatusBar.Height, () =>
        {
            // 既存どおり Undo の可視性はここで再評価しない。
            if (animationVersion == _animationVersion && !ShouldShowStatusBar())
            {
                _elements.StatusBar.Visibility = Visibility.Collapsed;
                _elements.StatusPopup.IsOpen = false;
            }
        });
    }

    private bool ShouldShowStatusBar()
    {
        return _host.ShowStatusText && !string.IsNullOrWhiteSpace(StatusText);
    }

    private void AnimateStatusBarTo(double targetOffset, Action? completed = null)
    {
        var transform = _elements.StatusBarTranslateTransform;
        var currentOffset = transform.Y;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = targetOffset;

        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(AnimationDurationMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            },
            FillBehavior = FillBehavior.Stop
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clearTimer.Stop();
        _clearTimer.Tick -= ClearTimer_Tick;
        ClosePopupImmediately();
        _disposed = true;
    }
}
