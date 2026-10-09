using System.Threading;
using System.Windows;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal readonly record struct NativeWebGestureStart(BrowserTab SourceTab, Point ClientPoint, Point WindowPoint);

internal interface INativeWebGestureHost
{
    bool ContainsTab(BrowserTab tab);
    bool UsesNativeWebGestureInput(BrowserTab sourceTab);
    bool TryGetWindowPoint(Point devicePoint, out Point windowPoint);
    double GestureStartThreshold { get; }
    double GestureDirectionThreshold { get; }
    IReadOnlyList<GestureMappingSettings> GetWebGestureMappings();
    void EndReplacedInputInteraction(InputInteractionSession? replaced, bool preserveNativeWebGesture);
    void SuppressNextPageContextMenu();
    void UseNativePageGestureVisual(BrowserTab sourceTab);
    void CancelActivePageGesture(BrowserTab? sourceTab);
    void ExecuteGestureCommand(BrowserTab sourceTab, string command);
}

// native入力の状態遷移を所有する。実Window座標・互換性判定・Core操作はhostへ接続する。
internal sealed class NativeWebGestureController
{
    private enum NativeWebGestureOwner
    {
        None,
        NativeCandidate,
        Page,
        Native
    }

    private readonly INativeWebGestureHost _host;
    private readonly InputInteractionHub _inputHub;
    private readonly Guid _windowId;
    private readonly PageGestureVisualController _visual;
    private readonly Func<DateTimeOffset> _now;
    private readonly NativeGestureRouteTracker _nativeWebGestureTracker = new();
    private BrowserTab? _nativeWebGestureSourceTab;
    private long? _nativeWebGestureInteractionId;
    private NativeWebGestureOwner _nativeWebGestureOwner;
    private Point _nativeWebGestureStartClientPoint;
    private int _nativeWebGesturePageMoveCount;
    private bool _nativeWebGesturePageRouteEstablished;
    private long _nativeWebGestureGeneration;

    public NativeWebGestureController(INativeWebGestureHost host, InputInteractionHub inputHub, Guid windowId,
        PageGestureVisualController visual, Func<DateTimeOffset>? now = null)
    {
        _host = host;
        _inputHub = inputHub;
        _windowId = windowId;
        _visual = visual;
        _now = now ?? (static () => DateTimeOffset.Now);
    }

    public bool IsTracking => _nativeWebGestureTracker.IsTracking;
    public long Generation => Volatile.Read(ref _nativeWebGestureGeneration);

    public bool TryBeginNativeWebGestureCandidate(Func<NativeWebGestureStart?> resolveStart)
    {
        _visual.EndPageGestureVisual();
        if (resolveStart() is not { } start)
        {
            return false;
        }

        var sourceTab = start.SourceTab;
        var clientPoint = start.ClientPoint;
        var windowPoint = start.WindowPoint;
        _nativeWebGestureSourceTab = sourceTab;
        _nativeWebGestureStartClientPoint = clientPoint;
        _nativeWebGestureOwner = NativeWebGestureOwner.NativeCandidate;
        _nativeWebGesturePageMoveCount = 0;
        _nativeWebGesturePageRouteEstablished = false;
        Interlocked.Increment(ref _nativeWebGestureGeneration);
        _nativeWebGestureTracker.Begin(
            windowPoint,
            _host.GestureStartThreshold,
            _host.GestureDirectionThreshold);

        if (!_host.UsesNativeWebGestureInput(sourceTab))
        {
            return false;
        }

        return TryPromoteNativeWebGesture(sourceTab);
    }

    public void UpdateNativeWebGesturePoint(Point devicePoint)
    {
        if (_nativeWebGestureSourceTab is not { } sourceTab
            || !_host.ContainsTab(sourceTab)
            || !_nativeWebGestureTracker.IsTracking)
        {
            if (_visual.IsTracking)
            {
                _visual.UpdatePageGestureVisualPoint(devicePoint);
            }

            return;
        }

        if (!_host.TryGetWindowPoint(devicePoint, out var windowPoint))
        {
            return;
        }

        _nativeWebGestureTracker.Update(windowPoint);
        if (_nativeWebGestureOwner == NativeWebGestureOwner.NativeCandidate
            && _nativeWebGestureTracker.IsActive)
        {
            TryPromoteNativeWebGesture(sourceTab);
        }
        else if (_nativeWebGestureOwner == NativeWebGestureOwner.Page)
        {
            _nativeWebGesturePageMoveCount++;
            var pageTakeoverDistance = Math.Max(
                _host.GestureStartThreshold * 1.5,
                _host.GestureStartThreshold + _host.GestureDirectionThreshold);
            if (!_nativeWebGesturePageRouteEstablished
                && _nativeWebGesturePageMoveCount >= 2
                && _nativeWebGestureTracker.DistanceFromStart >= pageTakeoverDistance)
            {
                TryPromoteNativeWebGesture(sourceTab);
            }
        }

        if (_nativeWebGestureOwner is NativeWebGestureOwner.Page or NativeWebGestureOwner.Native
            && _visual.IsTracking)
        {
            _visual.UpdatePageGestureVisualPoint(devicePoint);
        }

        if (_nativeWebGestureOwner == NativeWebGestureOwner.Native
            && _nativeWebGestureTracker.IsActive
            && !_visual.Route.Equals(_nativeWebGestureTracker.Route, StringComparison.Ordinal))
        {
            _visual.MarkVisualActive();
            _visual.UpdatePageGestureVisualState(sourceTab, _nativeWebGestureTracker.Route, "recognizing");
        }
    }

    public bool TryPromoteNativeWebGesture(BrowserTab sourceTab)
    {
        if (!ReferenceEquals(_nativeWebGestureSourceTab, sourceTab)
            || !_nativeWebGestureTracker.IsTracking
            || _nativeWebGestureOwner == NativeWebGestureOwner.Native)
        {
            return _nativeWebGestureOwner == NativeWebGestureOwner.Native;
        }

        var interaction = _inputHub.TryBegin(
            InputInteractionKind.PageGesture,
            _windowId,
            sourceTab.Id,
            _now());
        if (!interaction.Accepted || interaction.Current is not { } currentInteraction)
        {
            ResetNativeWebGestureTracking();
            return false;
        }

        _host.EndReplacedInputInteraction(interaction.Replaced, preserveNativeWebGesture: true);
        _nativeWebGestureInteractionId = currentInteraction.Id;
        _nativeWebGestureOwner = NativeWebGestureOwner.Native;
        if (!_visual.IsTracking
            || !ReferenceEquals(_visual.SourceTab, sourceTab))
        {
            _visual.BeginPageGestureVisual(
                sourceTab,
                _nativeWebGestureStartClientPoint.X,
                _nativeWebGestureStartClientPoint.Y);
        }

        if (!_visual.IsTracking)
        {
            ResetNativeWebGestureTracking();
            return false;
        }

        _host.SuppressNextPageContextMenu();
        _visual.ActivateNativePageGestureVisual(sourceTab, useVirtualScreen: false);
        _host.UseNativePageGestureVisual(sourceTab);
        if (_nativeWebGestureTracker.IsActive)
        {
            _visual.MarkVisualActive();
            _visual.UpdatePageGestureVisualState(sourceTab, _nativeWebGestureTracker.Route, "recognizing");
        }

        return true;
    }

    public void FinishNativeWebGesture(Point devicePoint)
    {
        if (_nativeWebGestureOwner == NativeWebGestureOwner.NativeCandidate)
        {
            ResetNativeWebGestureTracking();
            return;
        }

        if (_nativeWebGestureOwner != NativeWebGestureOwner.Native
            || _nativeWebGestureSourceTab is not { } sourceTab
            || !_nativeWebGestureTracker.IsTracking)
        {
            return;
        }

        UpdateNativeWebGesturePoint(devicePoint);
        var interactionId = _nativeWebGestureInteractionId;
        var completion = _nativeWebGestureTracker.Complete();
        _nativeWebGestureInteractionId = null;
        if (interactionId is { } activeInteractionId)
        {
            _inputHub.TryComplete(activeInteractionId);
        }

        _visual.EndPageGestureVisual(resetNativeWebGesture: false);
        _host.CancelActivePageGesture(sourceTab);

        if (!completion.WasActive || !_host.ContainsTab(sourceTab))
        {
            return;
        }

        var mapping = _host.GetWebGestureMappings()
            .FirstOrDefault(item => item.Route.Equals(completion.Route, StringComparison.Ordinal));
        if (mapping is null)
        {
            return;
        }

        _host.ExecuteGestureCommand(sourceTab, mapping.Command);
    }

    public bool TryClaimNativeWebGestureForPage(BrowserTab sourceTab)
    {
        if (_nativeWebGestureOwner == NativeWebGestureOwner.Native)
        {
            return false;
        }

        if (_nativeWebGestureOwner == NativeWebGestureOwner.NativeCandidate)
        {
            if (!ReferenceEquals(_nativeWebGestureSourceTab, sourceTab))
            {
                return false;
            }

            _nativeWebGestureOwner = NativeWebGestureOwner.Page;
            _nativeWebGesturePageMoveCount = 0;
            _nativeWebGesturePageRouteEstablished = false;
        }

        return true;
    }

    public bool ShouldIgnorePageGestureMessage(BrowserTab _)
    {
        return _nativeWebGestureOwner == NativeWebGestureOwner.Native;
    }

    public void MarkNativeWebGesturePageRoute(BrowserTab sourceTab, string? route)
    {
        if (_nativeWebGestureOwner == NativeWebGestureOwner.Page
            && ReferenceEquals(_nativeWebGestureSourceTab, sourceTab)
            && !string.IsNullOrEmpty(route))
        {
            _nativeWebGesturePageRouteEstablished = true;
        }
    }

    public void ResetNativeWebGestureForTab(BrowserTab tab)
    {
        if (ReferenceEquals(_nativeWebGestureSourceTab, tab)
            || ReferenceEquals(_visual.SourceTab, tab))
        {
            _visual.EndPageGestureVisual();
        }
    }

    public void CancelNativeWebGestureForRightWheel()
    {
        if (_nativeWebGestureOwner == NativeWebGestureOwner.None
            && !_nativeWebGestureTracker.IsTracking)
        {
            return;
        }

        var sourceTab = _nativeWebGestureSourceTab;
        _visual.EndPageGestureVisual();
        _host.CancelActivePageGesture(sourceTab);
    }

    public void ResetNativeWebGestureTracking()
    {
        if (_nativeWebGestureInteractionId is { } interactionId)
        {
            _inputHub.TryComplete(interactionId);
        }

        _nativeWebGestureInteractionId = null;
        _nativeWebGestureSourceTab = null;
        _nativeWebGestureOwner = NativeWebGestureOwner.None;
        _nativeWebGesturePageMoveCount = 0;
        _nativeWebGesturePageRouteEstablished = false;
        _nativeWebGestureTracker.Reset();
        Interlocked.Increment(ref _nativeWebGestureGeneration);
    }
}
