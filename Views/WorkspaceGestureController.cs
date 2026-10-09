using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;

namespace TabBrowser.Views;

internal sealed record WorkspaceGestureFallbackCallbacks(
    Func<string, bool> CanHandleDirection,
    Func<Point, Point, bool> CanStart,
    Action<Point, Point> Start,
    Action<Point> Update,
    Action Complete,
    Action Cancel);

internal sealed class WorkspaceGestureController : IDisposable
{
    private const double DirectionDominanceRatio = 1.25;
    private const double ReverseDirectionThresholdMultiplier = 1.5;
    private const double GestureGuideMovementTolerance = 4;
    private static readonly TimeSpan GestureGuideDelay = TimeSpan.FromMilliseconds(450);

    private readonly FrameworkElement _target;
    private readonly Func<string?, string?, Task> _executeCommandAsync;
    private readonly Func<long?>? _tryBeginInteraction;
    private readonly Func<long?>? _tryBeginRightWheelInteraction;
    private readonly Action<long>? _completeInteraction;
    private readonly Func<bool>? _canStartTracking;
    private readonly Func<int, bool>? _executeRightWheelRoute;
    private readonly Func<string?>? _gestureGuideSupplement;
    private readonly WorkspaceGestureFallbackCallbacks? _fallbackCallbacks;
    private readonly bool _handleHandledMouseEvents;
    private readonly string _gestureScope;
    private IReadOnlyDictionary<string, GestureCommand> _commands = new Dictionary<string, GestureCommand>(StringComparer.Ordinal);
    private double _startThreshold;
    private double _directionThreshold;
    private bool _preserveContextMenuOnShortRightClick;
    private bool _tracking;
    private bool _active;
    private bool _suppressContextMenuOnce;
    private string _route = string.Empty;
    private Point? _startPoint;
    private Point? _lastDirectionPoint;
    private Popup? _popup;
    private TextBlock? _popupText;
    private Popup? _statusPopup;
    private GestureStatusHud? _statusHud;
    private DispatcherTimer? _cancelFeedbackTimer;
    private DispatcherTimer? _gestureGuideTimer;
    private bool _gestureGuideVisible;
    private bool _fallbackActive;
    private string? _presentedStatusRoute;
    private GestureStatusHudState? _presentedStatusState;
    private long? _interactionId;
    private long? _rightWheelInteractionId;
    private bool _inputHandlersAttached;
    private bool _isDisposed;

    public WorkspaceGestureController(
        FrameworkElement target,
        AppSettings settings,
        Func<string?, string?, Task> executeCommandAsync,
        Func<long?>? tryBeginInteraction = null,
        Action<long>? completeInteraction = null,
        Func<long?>? tryBeginRightWheelInteraction = null,
        Func<bool>? canStartTracking = null,
        bool handleHandledMouseEvents = false,
        string gestureScope = GestureCommandScope.Any,
        Func<int, bool>? executeRightWheelRoute = null,
        Func<string?>? gestureGuideSupplement = null,
        WorkspaceGestureFallbackCallbacks? fallbackCallbacks = null)
    {
        _target = target;
        _executeCommandAsync = executeCommandAsync;
        _tryBeginInteraction = tryBeginInteraction;
        _completeInteraction = completeInteraction;
        _tryBeginRightWheelInteraction = tryBeginRightWheelInteraction;
        _canStartTracking = canStartTracking;
        _executeRightWheelRoute = executeRightWheelRoute;
        _gestureGuideSupplement = gestureGuideSupplement;
        _fallbackCallbacks = fallbackCallbacks;
        _handleHandledMouseEvents = handleHandledMouseEvents;
        _gestureScope = GestureCommandScope.Normalize(gestureScope);
        UpdateSettings(settings);

        _target.Loaded += Target_Loaded;
        _target.Unloaded += Target_Unloaded;
        AttachInputHandlers();
    }

    public void UpdateSettings(AppSettings settings)
    {
        if (_isDisposed)
        {
            return;
        }

        _startThreshold = settings.GestureStartThreshold;
        _directionThreshold = settings.GestureDirectionThreshold;
        _preserveContextMenuOnShortRightClick = settings.PreserveContextMenuOnShortRightClick;
        _commands = GestureSettingsCatalog.ResolveMappingsForScope(
                settings.GestureMappings,
                _gestureScope)
            .ToDictionary(
                x => x.Route,
                x => new GestureCommand(x.Command, x.Label),
                StringComparer.Ordinal);
    }

    public void Cancel()
    {
        ResetGesture();
        CompleteRightWheelInteraction();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Cancel();
        DetachInputHandlers();
        _target.Loaded -= Target_Loaded;
        _target.Unloaded -= Target_Unloaded;
    }

    private void Target_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetGesture();
        CompleteRightWheelInteraction();
        StopCancellationFeedback();
        if (_canStartTracking is not null && !_canStartTracking())
        {
            return;
        }

        _tracking = true;
        _active = false;
        _route = string.Empty;
        _startPoint = e.GetPosition(_target);
        _lastDirectionPoint = _startPoint;
        StartGestureGuide();
    }

    private void Target_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_tracking
            || e.RightButton != MouseButtonState.Pressed
            || _startPoint is not { } startPoint
            || _lastDirectionPoint is not { } lastDirectionPoint)
        {
            return;
        }

        var point = e.GetPosition(_target);
        if (GetDistance(startPoint, point) > GestureGuideMovementTolerance)
        {
            StopGestureGuide();
        }

        if (_fallbackActive)
        {
            e.Handled = true;
            _fallbackCallbacks?.Update(point);
            return;
        }

        var initialDirection = GetDominantDirection(startPoint, point, _startThreshold);
        var hasConfiguredDirection = initialDirection is not null
            && HasConfiguredRouteStartingWith(initialDirection);
        var isFallbackDirection = initialDirection is not null
            && !hasConfiguredDirection
            && _fallbackCallbacks?.CanHandleDirection(initialDirection) == true;
        var shouldWaitForDirection = _fallbackCallbacks is not null && initialDirection is null;
        if (!_active
            && isFallbackDirection
            && _fallbackCallbacks?.CanStart(startPoint, point) == true)
        {
            if (!TryBeginInteraction())
            {
                ResetGesture();
                return;
            }

            _active = true;
            _fallbackActive = true;
            _target.CaptureMouse();
            _fallbackCallbacks.Start(startPoint, point);
            e.Handled = true;
            return;
        }

        if (!_active
            && !isFallbackDirection
            && !shouldWaitForDirection
            && GetDistance(startPoint, point) >= _startThreshold)
        {
            if (!TryBeginInteraction())
            {
                ResetGesture();
                return;
            }

            _active = true;
            _target.CaptureMouse();
        }

        if (!_active)
        {
            return;
        }

        e.Handled = true;
        AppendDirection(lastDirectionPoint, point);
        UpdatePopup(point);
        UpdateStatusHud();
    }

    private void Target_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteRightWheelInteraction();
        if (!_tracking)
        {
            return;
        }

        var completedRoute = _route;
        var wasActive = _active;
        var wasFallbackActive = _fallbackActive;
        ResetGesture(cancelFallback: false);

        if (wasFallbackActive)
        {
            _suppressContextMenuOnce = true;
            e.Handled = true;
            _fallbackCallbacks?.Complete();
            return;
        }

        if (!wasActive)
        {
            return;
        }

        _suppressContextMenuOnce = true;
        e.Handled = true;

        if (_commands.TryGetValue(completedRoute, out var completedCommand))
        {
            ExecuteCommand(
                completedCommand.Command,
                completedCommand.Command == "SearchSelection" ? GetSelectedText() : string.Empty);
        }
    }

    private void Target_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_tracking && e.RightButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.Delta == 0)
        {
            return;
        }

        ResetGesture();
        if (!TryBeginRightWheelInteraction())
        {
            return;
        }

        _suppressContextMenuOnce = true;
        e.Handled = true;

        var completedRoute = e.Delta < 0 ? "wheelDown" : "wheelUp";
        if (_executeRightWheelRoute?.Invoke(e.Delta) == true)
        {
            return;
        }

        if (_commands.TryGetValue(completedRoute, out var completedCommand))
        {
            ExecuteCommand(
                completedCommand.Command,
                completedCommand.Command == "SearchSelection" ? GetSelectedText() : string.Empty);
        }
    }

    private void Target_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_tracking || e.Key != Key.Escape)
        {
            return;
        }

        ResetGesture();
        _suppressContextMenuOnce = true;
        ShowCancellationFeedback(Mouse.GetPosition(_target));
        e.Handled = true;
    }

    private void Target_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_suppressContextMenuOnce || (!_preserveContextMenuOnShortRightClick && _tracking))
        {
            _suppressContextMenuOnce = false;
            e.Handled = true;
        }
    }

    private void Target_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_active)
        {
            ResetGesture(releaseCapture: false);
        }
    }

    private void Target_Loaded(object sender, RoutedEventArgs e)
    {
        AttachInputHandlers();
    }

    private void Target_Unloaded(object sender, RoutedEventArgs e)
    {
        Cancel();
        DetachInputHandlers();
    }

    private void AttachInputHandlers()
    {
        if (_isDisposed || _inputHandlersAttached)
        {
            return;
        }

        _target.AddHandler(
            UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(Target_PreviewMouseRightButtonDown),
            _handleHandledMouseEvents);
        _target.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(Target_PreviewMouseMove),
            _handleHandledMouseEvents);
        _target.AddHandler(
            UIElement.PreviewMouseRightButtonUpEvent,
            new MouseButtonEventHandler(Target_PreviewMouseRightButtonUp),
            _handleHandledMouseEvents);
        _target.AddHandler(
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(Target_PreviewMouseWheel),
            _handleHandledMouseEvents);
        _target.PreviewKeyDown += Target_PreviewKeyDown;
        _target.ContextMenuOpening += Target_ContextMenuOpening;
        _target.LostMouseCapture += Target_LostMouseCapture;
        _target.SizeChanged += Target_SizeChanged;
        _inputHandlersAttached = true;
    }

    private void DetachInputHandlers()
    {
        if (!_inputHandlersAttached)
        {
            return;
        }

        _target.RemoveHandler(
            UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(Target_PreviewMouseRightButtonDown));
        _target.RemoveHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(Target_PreviewMouseMove));
        _target.RemoveHandler(
            UIElement.PreviewMouseRightButtonUpEvent,
            new MouseButtonEventHandler(Target_PreviewMouseRightButtonUp));
        _target.RemoveHandler(
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(Target_PreviewMouseWheel));
        _target.PreviewKeyDown -= Target_PreviewKeyDown;
        _target.ContextMenuOpening -= Target_ContextMenuOpening;
        _target.LostMouseCapture -= Target_LostMouseCapture;
        _target.SizeChanged -= Target_SizeChanged;
        _inputHandlersAttached = false;
    }

    private void Target_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_statusPopup?.IsOpen == true)
        {
            PositionStatusPopup();
        }
    }

    private void AppendDirection(Point from, Point to)
    {
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);

        if (Math.Max(absoluteX, absoluteY) < _directionThreshold)
        {
            return;
        }

        var horizontal = absoluteX >= absoluteY;
        var primaryDistance = horizontal ? absoluteX : absoluteY;
        var secondaryDistance = horizontal ? absoluteY : absoluteX;
        if (primaryDistance < secondaryDistance * DirectionDominanceRatio)
        {
            return;
        }

        var direction = horizontal
            ? deltaX < 0 ? "←" : "→"
            : deltaY < 0 ? "↑" : "↓";
        var previousDirection = _route.Length > 0 ? _route[^1].ToString() : string.Empty;
        if (!string.IsNullOrEmpty(previousDirection)
            && IsOppositeDirection(previousDirection, direction)
            && primaryDistance < _directionThreshold * ReverseDirectionThresholdMultiplier)
        {
            return;
        }

        if (!previousDirection.Equals(direction, StringComparison.Ordinal))
        {
            _route += direction;
        }

        _lastDirectionPoint = to;
    }

    private static bool IsOppositeDirection(string first, string second)
    {
        return first is "←" && second is "→"
            || first is "→" && second is "←"
            || first is "↑" && second is "↓"
            || first is "↓" && second is "↑";
    }

    private void ExecuteCommand(string command, string selectedText)
    {
        _ = _executeCommandAsync(command, selectedText);
    }

    private void ResetGesture(bool releaseCapture = true, bool cancelFallback = true)
    {
        if (_fallbackActive && cancelFallback)
        {
            _fallbackCallbacks?.Cancel();
        }

        CompleteInteraction();
        StopGestureGuide();
        StopCancellationFeedback();
        _tracking = false;
        _active = false;
        _fallbackActive = false;
        _route = string.Empty;
        _startPoint = null;
        _lastDirectionPoint = null;
        HidePopup();
        HideStatusPopup();

        if (releaseCapture && _target.IsMouseCaptured)
        {
            _target.ReleaseMouseCapture();
        }
    }

    private bool TryBeginInteraction()
    {
        if (_tryBeginInteraction is null)
        {
            return true;
        }

        _interactionId = _tryBeginInteraction();
        return _interactionId is not null;
    }

    private void CompleteInteraction()
    {
        if (_interactionId is not { } interactionId)
        {
            return;
        }

        _interactionId = null;
        _completeInteraction?.Invoke(interactionId);
    }

    private bool TryBeginRightWheelInteraction()
    {
        if (_tryBeginRightWheelInteraction is null)
        {
            return true;
        }

        _rightWheelInteractionId = _tryBeginRightWheelInteraction();
        return _rightWheelInteractionId is not null;
    }

    private void CompleteRightWheelInteraction()
    {
        if (_rightWheelInteractionId is not { } interactionId)
        {
            return;
        }

        _rightWheelInteractionId = null;
        _completeInteraction?.Invoke(interactionId);
    }

    private void UpdatePopup(Point point)
    {
        EnsurePopup();
        if (_popup is null || _popupText is null)
        {
            return;
        }

        _popupText.Text = GetCurrentLabel();
        PositionPopup(point);
        _popup.IsOpen = true;
    }

    private void EnsurePopup()
    {
        if (_popup is not null)
        {
            return;
        }

        _popupText = new TextBlock
        {
            MinWidth = 90,
            MaxWidth = 380,
            Foreground = Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };

        var border = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromArgb(235, 17, 20, 26)),
            CornerRadius = new CornerRadius(5),
            Child = _popupText
        };

        _popup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = _target,
            StaysOpen = true,
            Child = border
        };
    }

    private void UpdateStatusHud()
    {
        var route = _route;
        GestureStatusHudState state;
        string primaryText;
        string nextDirections;
        if (string.IsNullOrWhiteSpace(route))
        {
            state = GestureStatusHudState.Recognizing;
            primaryText = "認識中";
            nextDirections = string.Empty;
        }
        else if (_commands.TryGetValue(route, out var command))
        {
            state = GestureStatusHudState.Registered;
            primaryText = command.Label;
            nextDirections = GetNextDirectionText(route);
        }
        else
        {
            var canContinue = _commands.Keys.Any(candidate =>
                !candidate.StartsWith("wheel", StringComparison.Ordinal)
                && candidate.Length > route.Length
                && candidate.StartsWith(route, StringComparison.Ordinal));
            state = canContinue
                ? GestureStatusHudState.Continuing
                : GestureStatusHudState.Unregistered;
            primaryText = canContinue ? "入力中" : "実行する機能なし";
            nextDirections = canContinue ? GetNextDirectionText(route) : string.Empty;
        }

        if (_presentedStatusRoute == route && _presentedStatusState == state)
        {
            return;
        }

        EnsureStatusPopup();
        if (_statusPopup is null || _statusHud is null)
        {
            return;
        }

        _presentedStatusRoute = route;
        _presentedStatusState = state;
        _statusHud.ShowPresentation(route, state, primaryText, nextDirections);
        PositionStatusPopup();
        _statusPopup.IsOpen = true;
    }

    private void EnsureStatusPopup()
    {
        if (_statusPopup is not null)
        {
            return;
        }

        _statusHud = new GestureStatusHud();
        _statusPopup = new Popup
        {
            AllowsTransparency = true,
            Focusable = false,
            IsHitTestVisible = false,
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = _target,
            StaysOpen = true,
            Child = _statusHud
        };
    }

    private void PositionStatusPopup()
    {
        if (_statusPopup is null || _statusHud is null)
        {
            return;
        }

        var width = Math.Clamp(_target.ActualWidth - 32, 180, 520);
        _statusHud.Width = width;
        _statusPopup.HorizontalOffset = Math.Max(8, (_target.ActualWidth - width) / 2);
        _statusPopup.VerticalOffset = 16;
    }

    private void HidePopup()
    {
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
    }

    private void HideStatusPopup()
    {
        if (_statusPopup is not null)
        {
            _statusPopup.IsOpen = false;
        }

        _presentedStatusRoute = null;
        _presentedStatusState = null;
    }

    private void ShowCancellationFeedback(Point point)
    {
        StopGestureGuide();
        EnsurePopup();
        if (_popup is null || _popupText is null)
        {
            return;
        }

        _popupText.Text = "キャンセル";
        PositionPopup(point);
        _popup.IsOpen = true;
        EnsureStatusPopup();
        if (_statusPopup is not null && _statusHud is not null)
        {
            _statusHud.ShowPresentation(
                string.Empty,
                GestureStatusHudState.Cancelled,
                "キャンセル");
            PositionStatusPopup();
            _statusPopup.IsOpen = true;
        }

        _cancelFeedbackTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _cancelFeedbackTimer.Tick -= CancelFeedbackTimer_Tick;
        _cancelFeedbackTimer.Tick += CancelFeedbackTimer_Tick;
        _cancelFeedbackTimer.Stop();
        _cancelFeedbackTimer.Start();
    }

    private void CancelFeedbackTimer_Tick(object? sender, EventArgs e)
    {
        StopCancellationFeedback();
    }

    private void StopCancellationFeedback()
    {
        _cancelFeedbackTimer?.Stop();
        HidePopup();
        HideStatusPopup();
    }

    private void StartGestureGuide()
    {
        StopGestureGuide();
        _gestureGuideTimer ??= new DispatcherTimer
        {
            Interval = GestureGuideDelay
        };
        _gestureGuideTimer.Tick -= GestureGuideTimer_Tick;
        _gestureGuideTimer.Tick += GestureGuideTimer_Tick;
        _gestureGuideTimer.Start();
    }

    private void GestureGuideTimer_Tick(object? sender, EventArgs e)
    {
        _gestureGuideTimer?.Stop();
        if (!_tracking || _active || _startPoint is not { } point)
        {
            return;
        }

        EnsurePopup();
        if (_popup is null || _popupText is null)
        {
            return;
        }

        _gestureGuideVisible = true;
        _popupText.Text = GetInitialGestureGuideText();
        PositionPopup(point);
        _popup.IsOpen = true;
    }

    private void StopGestureGuide()
    {
        _gestureGuideTimer?.Stop();
        if (_gestureGuideVisible)
        {
            _gestureGuideVisible = false;
            HidePopup();
        }
    }

    private void PositionPopup(Point point)
    {
        if (_popup is null)
        {
            return;
        }

        _popup.HorizontalOffset = point.X + 14;
        _popup.VerticalOffset = point.Y + 14;
    }

    private string GetCurrentLabel()
    {
        if (string.IsNullOrWhiteSpace(_route))
        {
            return "認識中";
        }

        if (_commands.TryGetValue(_route, out var command))
        {
            var nextDirections = GetNextDirectionText(_route);
            return $"{_route}  {command.Label}"
                + (string.IsNullOrEmpty(nextDirections) ? string.Empty : $"\n次: {nextDirections}");
        }

        var canContinue = _commands.Keys.Any(route =>
            !route.StartsWith("wheel", StringComparison.Ordinal)
            && route.Length > _route.Length
            && route.StartsWith(_route, StringComparison.Ordinal));
        if (!canContinue)
        {
            return $"{_route}  未登録";
        }

        var continuationDirections = GetNextDirectionText(_route);
        return $"{_route}  入力中"
            + (string.IsNullOrEmpty(continuationDirections) ? string.Empty : $"\n次: {continuationDirections}");
    }

    private string GetNextDirectionText(string currentRoute)
    {
        var results = new List<string>();
        foreach (var direction in new[] { "←", "→", "↑", "↓" })
        {
            var nextRoute = currentRoute + direction;
            var hasContinuation = _commands.Keys.Any(route =>
                !route.StartsWith("wheel", StringComparison.Ordinal)
                && route.Length >= nextRoute.Length
                && route.StartsWith(nextRoute, StringComparison.Ordinal));
            if (_commands.TryGetValue(nextRoute, out var exact))
            {
                results.Add($"{direction} {exact.Label}");
            }
            else if (hasContinuation)
            {
                results.Add($"{direction} 続き");
            }
        }

        return string.Join("  ", results);
    }

    private string GetInitialGestureGuideText()
    {
        var items = new List<string>();
        foreach (var direction in new[] { "←", "→", "↑", "↓" })
        {
            var hasContinuation = _commands.Keys.Any(route =>
                !route.StartsWith("wheel", StringComparison.Ordinal)
                && route.Length > direction.Length
                && route.StartsWith(direction, StringComparison.Ordinal));
            if (_commands.TryGetValue(direction, out var exact))
            {
                items.Add($"{direction} {exact.Label}");
            }
            else if (hasContinuation)
            {
                items.Add($"{direction} 続き");
            }
        }

        var directionGuide = items.Count == 0
            ? "ジェスチャー未登録"
            : string.Join("  ", items.Take(2))
                + (items.Count > 2 ? "\n" + string.Join("  ", items.Skip(2)) : string.Empty);
        var supplement = _gestureGuideSupplement?.Invoke();
        return string.IsNullOrWhiteSpace(supplement)
            ? directionGuide
            : $"{directionGuide}\n{supplement}";
    }

    private static string GetSelectedText()
    {
        return Keyboard.FocusedElement switch
        {
            TextBox textBox => textBox.SelectedText,
            RichTextBox richTextBox => richTextBox.Selection.Text,
            _ => string.Empty
        };
    }

    private static double GetDistance(Point first, Point second)
    {
        return Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    }

    private bool HasConfiguredRouteStartingWith(string direction)
    {
        return _commands.Keys.Any(route =>
            !route.StartsWith("wheel", StringComparison.Ordinal)
            && route.StartsWith(direction, StringComparison.Ordinal));
    }

    private static string? GetDominantDirection(Point startPoint, Point currentPoint, double threshold)
    {
        var deltaX = currentPoint.X - startPoint.X;
        var deltaY = currentPoint.Y - startPoint.Y;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);
        var primaryDistance = Math.Max(absoluteX, absoluteY);
        var secondaryDistance = Math.Min(absoluteX, absoluteY);
        if (primaryDistance < threshold
            || primaryDistance < secondaryDistance * DirectionDominanceRatio)
        {
            return null;
        }

        return absoluteX >= absoluteY
            ? deltaX < 0 ? "←" : "→"
            : deltaY < 0 ? "↑" : "↓";
    }

    private sealed record GestureCommand(string Command, string Label);
}
