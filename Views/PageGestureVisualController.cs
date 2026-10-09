using System.Threading;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Views;

internal interface IScreenGestureOverlayWindow
{
    bool IsVisible { get; }
    double DistanceFromStart { get; }
    void Begin(Point screenPoint, bool useVirtualScreen);
    ScreenGesturePointerUpdate UpdatePointer(Point screenPoint);
    void ExpandToVirtualScreen();
    void ShowGuide(string text);
    void ShowPresentation(string labelText, string route, GestureStatusHudState state,
        string primaryText, string? nextDirections = null);
    void HidePresentationBeforeActivation();
    void End();
    void Close();
}

internal interface IPageGestureVisualHost
{
    bool TryGetStartScreenPoint(BrowserTab sourceTab, double clientX, double clientY, out Point startPoint);
    bool IsSourceAvailable(BrowserTab sourceTab);
    bool RequiresNativePageGestureVisualAtStart(BrowserTab sourceTab);
    double GestureStartThreshold { get; }
    double GetPageGestureDistanceFromStart(Point startScreenPoint, Point screenPoint);
    bool IsScreenPointOutsideMainWindow(Point screenPoint);
    IReadOnlyList<GestureMappingSettings> GetGestureMappings();
    void ResetNativeWebGestureTracking();
    void ResetQueuedPageGestureInput();
    void UseNativePageGestureVisual(BrowserTab sourceTab);
}

// page gestureの表示状態と遅延生成したOverlayを所有する。native入力の所有権と配送はhostへ接続する。
internal sealed class PageGestureVisualController
{
    private const double PageGestureGuideMovementTolerance = 4;
    private readonly IPageGestureVisualHost _host;
    private readonly Func<IScreenGestureOverlayWindow> _createOverlay;
    private volatile bool _isPageGestureVisualTracking;
    private bool _isPageGestureGuideVisible;
    private bool _isPageGestureVisualActive;
    private bool _isPageGestureUsingNativeVisual;
    private BrowserTab? _pageGestureVisualSourceTab;
    private IScreenGestureOverlayWindow? _pageGestureOverlayWindow;
    private readonly List<Point> _pageGestureScreenPoints = [];
    private Point _pageGestureStartScreenPoint;
    private string _pageGestureVisualRoute = string.Empty;
    private string? _pageGestureVisualStatus;
    private int _pageGestureVisualGeneration;

    public PageGestureVisualController(Window owner, IPageGestureVisualHost host)
        : this(host, () => new ScreenGestureOverlayWindow(owner))
    {
    }

    internal PageGestureVisualController(IPageGestureVisualHost host, Func<IScreenGestureOverlayWindow> createOverlay)
    {
        _host = host;
        _createOverlay = createOverlay;
    }

    public bool IsTracking => _isPageGestureVisualTracking;
    public int Generation => Volatile.Read(ref _pageGestureVisualGeneration);
    public BrowserTab? SourceTab => _pageGestureVisualSourceTab;
    public string Route => _pageGestureVisualRoute;
    public bool IsOverlayVisible => _pageGestureOverlayWindow?.IsVisible == true;

    public void MarkVisualActive() => _isPageGestureVisualActive = true;

    public void StopTrackingAfterDispatchFailure() => _isPageGestureVisualTracking = false;

    public void BeginPageGestureVisual(BrowserTab sourceTab, double clientX, double clientY)
    {
        EndPageGestureVisual(resetNativeWebGesture: false);
        if (!_host.TryGetStartScreenPoint(sourceTab, clientX, clientY, out var startPoint))
        {
            return;
        }

        _pageGestureVisualSourceTab = sourceTab;
        _pageGestureStartScreenPoint = startPoint;
        _pageGestureScreenPoints.Clear();
        _pageGestureScreenPoints.Add(startPoint);
        _pageGestureVisualRoute = string.Empty;
        _pageGestureVisualStatus = null;
        _isPageGestureGuideVisible = false;
        _isPageGestureVisualActive = false;
        _isPageGestureUsingNativeVisual = false;
        Interlocked.Increment(ref _pageGestureVisualGeneration);
        _isPageGestureVisualTracking = true;

        if (_host.RequiresNativePageGestureVisualAtStart(sourceTab))
        {
            ActivateNativePageGestureVisual(sourceTab, useVirtualScreen: false);
        }
    }

    public void UpdatePageGestureVisualPoint(Point devicePoint)
    {
        if (!_isPageGestureVisualTracking
            || _pageGestureVisualSourceTab is not { } sourceTab
            || !_host.IsSourceAvailable(sourceTab))
        {
            EndPageGestureVisual();
            return;
        }

        if (!double.IsFinite(devicePoint.X) || !double.IsFinite(devicePoint.Y))
        {
            return;
        }

        if (_pageGestureScreenPoints.Count == 0
            || (devicePoint - _pageGestureScreenPoints[^1]).Length >= 1.5)
        {
            _pageGestureScreenPoints.Add(devicePoint);
        }

        var distanceFromStart = _host.GetPageGestureDistanceFromStart(_pageGestureStartScreenPoint, devicePoint);
        if (!_isPageGestureVisualActive
            && distanceFromStart >= _host.GestureStartThreshold)
        {
            _isPageGestureVisualActive = true;
        }

        var isOutsideMainWindow = _host.IsScreenPointOutsideMainWindow(devicePoint);
        if (!_isPageGestureUsingNativeVisual && isOutsideMainWindow)
        {
            ActivateNativePageGestureVisual(sourceTab, useVirtualScreen: true);
        }
        else if (_isPageGestureUsingNativeVisual
            && isOutsideMainWindow
            && _pageGestureOverlayWindow is { } screenOverlay)
        {
            screenOverlay.ExpandToVirtualScreen();
        }

        if (_isPageGestureUsingNativeVisual && _pageGestureOverlayWindow is { } overlay)
        {
            var movement = overlay.UpdatePointer(devicePoint);
            distanceFromStart = movement.DistanceFromStart;
        }

        if (_isPageGestureGuideVisible
            && distanceFromStart > PageGestureGuideMovementTolerance)
        {
            HidePageGestureGuideUntilRecognition();
        }

        if (_isPageGestureUsingNativeVisual
            && _isPageGestureVisualActive
            && string.IsNullOrEmpty(_pageGestureVisualRoute)
            && _pageGestureVisualStatus is not "guide" and not "cancelled")
        {
            _pageGestureOverlayWindow?.ShowPresentation(
                "認識中", string.Empty, GestureStatusHudState.Recognizing, "認識中");
        }
    }

    public void ActivateNativePageGestureVisual(BrowserTab sourceTab, bool useVirtualScreen)
    {
        if (_isPageGestureUsingNativeVisual || _pageGestureScreenPoints.Count == 0)
        {
            return;
        }

        try
        {
            _pageGestureOverlayWindow ??= _createOverlay();
            _pageGestureOverlayWindow.Begin(_pageGestureScreenPoints[0], useVirtualScreen);
            foreach (var point in _pageGestureScreenPoints.Skip(1))
            {
                _pageGestureOverlayWindow.UpdatePointer(point);
            }
        }
        catch (InvalidOperationException)
        {
            _pageGestureOverlayWindow = null;
            return;
        }

        _isPageGestureUsingNativeVisual = true;
        UpdatePageGestureVisualState(
            sourceTab,
            _pageGestureVisualRoute,
            _pageGestureVisualStatus);
        _host.UseNativePageGestureVisual(sourceTab);
    }

    public void UpdatePageGestureVisualState(BrowserTab sourceTab, string? route, string? status)
    {
        if (!_isPageGestureVisualTracking
            || !ReferenceEquals(_pageGestureVisualSourceTab, sourceTab)
            || route is null
            || route.Length > 32
            || route.Any(static direction => direction is not ('←' or '→' or '↑' or '↓')))
        {
            return;
        }

        _pageGestureVisualRoute = route;
        _pageGestureVisualStatus = status;
        if (!_isPageGestureUsingNativeVisual || _pageGestureOverlayWindow is not { } overlay)
        {
            _isPageGestureGuideVisible = status == "guide";
            return;
        }

        var mappings = _host.GetGestureMappings();
        if (status == "guide")
        {
            if (overlay.DistanceFromStart > PageGestureGuideMovementTolerance)
            {
                HidePageGestureGuideUntilRecognition();
                return;
            }

            _isPageGestureGuideVisible = true;
            overlay.ShowGuide(GetInitialPageGestureGuideText(mappings));
            return;
        }

        if (status == "recognizing" && _isPageGestureGuideVisible)
        {
            HidePageGestureGuideUntilRecognition();
        }

        if (status == "cancelled")
        {
            _isPageGestureGuideVisible = false;
            _isPageGestureVisualActive = true;
            overlay.ShowPresentation(
                "キャンセル",
                string.Empty,
                GestureStatusHudState.Cancelled,
                "キャンセル");
            return;
        }

        if (string.IsNullOrEmpty(route))
        {
            if (_isPageGestureVisualActive)
            {
                overlay.ShowPresentation(
                    "認識中",
                    string.Empty,
                    GestureStatusHudState.Recognizing,
                    "認識中");
            }

            return;
        }

        _isPageGestureGuideVisible = false;
        _isPageGestureVisualActive = true;
        var mapping = mappings.FirstOrDefault(item => item.Route.Equals(route, StringComparison.Ordinal));
        if (mapping is not null)
        {
            var nextDirections = GetPageGestureNextDirectionsText(mappings, route);
            overlay.ShowPresentation(
                $"{route}  {mapping.Label}"
                    + (string.IsNullOrEmpty(nextDirections) ? string.Empty : $"\n次: {nextDirections}"),
                route,
                GestureStatusHudState.Registered,
                mapping.Label,
                nextDirections);
            return;
        }

        var canContinue = mappings.Any(item =>
            !item.Route.StartsWith("wheel", StringComparison.Ordinal)
            && item.Route.Length > route.Length
            && item.Route.StartsWith(route, StringComparison.Ordinal));
        if (canContinue)
        {
            var nextDirections = GetPageGestureNextDirectionsText(mappings, route);
            overlay.ShowPresentation(
                $"{route}  入力中"
                    + (string.IsNullOrEmpty(nextDirections) ? string.Empty : $"\n次: {nextDirections}"),
                route,
                GestureStatusHudState.Continuing,
                "入力中",
                nextDirections);
        }
        else
        {
            overlay.ShowPresentation(
                $"{route}  未登録",
                route,
                GestureStatusHudState.Unregistered,
                "実行する機能なし");
        }
    }

    private static string GetPageGestureNextDirectionsText(
        IReadOnlyList<GestureMappingSettings> mappings,
        string route)
    {
        var results = new List<string>();
        foreach (var direction in new[] { "←", "→", "↑", "↓" })
        {
            var nextRoute = route + direction;
            var exact = mappings.FirstOrDefault(item => item.Route.Equals(nextRoute, StringComparison.Ordinal));
            var hasContinuation = mappings.Any(item =>
                !item.Route.StartsWith("wheel", StringComparison.Ordinal)
                && item.Route.Length >= nextRoute.Length
                && item.Route.StartsWith(nextRoute, StringComparison.Ordinal));
            if (exact is not null)
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

    private static string GetInitialPageGestureGuideText(IReadOnlyList<GestureMappingSettings> mappings)
    {
        var items = new List<string>();
        foreach (var direction in new[] { "←", "→", "↑", "↓" })
        {
            var exact = mappings.FirstOrDefault(item => item.Route.Equals(direction, StringComparison.Ordinal));
            var hasContinuation = mappings.Any(item =>
                !item.Route.StartsWith("wheel", StringComparison.Ordinal)
                && item.Route.Length > direction.Length
                && item.Route.StartsWith(direction, StringComparison.Ordinal));
            if (exact is not null)
            {
                items.Add($"{direction} {exact.Label}");
            }
            else if (hasContinuation)
            {
                items.Add($"{direction} 続き");
            }
        }

        if (items.Count == 0)
        {
            return "ジェスチャー未登録";
        }

        return string.Join("  ", items.Take(2))
            + (items.Count > 2 ? "\n" + string.Join("  ", items.Skip(2)) : string.Empty);
    }

    private void HidePageGestureGuideUntilRecognition()
    {
        _isPageGestureGuideVisible = false;
        if (!_isPageGestureVisualActive)
        {
            _pageGestureOverlayWindow?.HidePresentationBeforeActivation();
        }
    }

    public void EndPageGestureVisual(bool resetNativeWebGesture = true)
    {
        if (resetNativeWebGesture)
        {
            _host.ResetNativeWebGestureTracking();
        }

        Interlocked.Increment(ref _pageGestureVisualGeneration);
        _isPageGestureVisualTracking = false;
        _isPageGestureGuideVisible = false;
        _isPageGestureVisualActive = false;
        _isPageGestureUsingNativeVisual = false;
        _pageGestureVisualSourceTab = null;
        _pageGestureScreenPoints.Clear();
        _pageGestureVisualRoute = string.Empty;
        _pageGestureVisualStatus = null;
        _host.ResetQueuedPageGestureInput();
        _pageGestureOverlayWindow?.End();
    }

    public void ClosePageGestureOverlayWindow()
    {
        var overlay = _pageGestureOverlayWindow;
        _pageGestureOverlayWindow = null;
        overlay?.Close();
    }
}
