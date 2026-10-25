using System;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerResizeFrameScheduler
{
    private readonly Action<Action> _queueRender;
    private readonly Action<Rect> _applyBounds;
    private Rect _pendingBounds;
    private bool _hasPendingBounds;
    private bool _isRenderQueued;
    private int _generation;

    public YouTubeMiniPlayerResizeFrameScheduler(
        Action<Action> queueRender,
        Action<Rect> applyBounds)
    {
        _queueRender = queueRender;
        _applyBounds = applyBounds;
    }

    public void Queue(Rect bounds)
    {
        _pendingBounds = bounds;
        _hasPendingBounds = true;
        if (_isRenderQueued)
        {
            return;
        }

        _isRenderQueued = true;
        var generation = _generation;
        _queueRender(() => ApplyQueued(generation));
    }

    public void Flush()
    {
        _generation++;
        _isRenderQueued = false;
        ApplyPending();
    }

    public void Cancel()
    {
        _generation++;
        _isRenderQueued = false;
        _hasPendingBounds = false;
    }

    private void ApplyQueued(int generation)
    {
        if (generation != _generation)
        {
            return;
        }

        _isRenderQueued = false;
        ApplyPending();
    }

    private void ApplyPending()
    {
        if (!_hasPendingBounds)
        {
            return;
        }

        var bounds = _pendingBounds;
        _hasPendingBounds = false;
        _applyBounds(bounds);
    }
}

internal static class YouTubeMiniPlayerResizeService
{
    public static void ApplyResize(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp)
    {
        var bounds = CalculateResizeBounds(
            state,
            startLeft,
            startTop,
            startWidth,
            startHeight,
            totalHorizontalChange,
            totalVerticalChange,
            resizeLeft,
            resizeTop,
            resizeRight,
            resizeBottom);
        ApplyBounds(state, root, bounds, clamp);
    }

    public static Rect CalculateResizeBounds(
        YouTubeMiniPlayerState state,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange,
        bool resizeLeft,
        bool resizeTop,
        bool resizeRight,
        bool resizeBottom,
        Size? minimumSize = null)
    {
        return YouTubeMiniPlayerInputService.CalculateResizeBounds(
            YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(state),
            startLeft,
            startTop,
            startWidth,
            startHeight,
            totalHorizontalChange,
            totalVerticalChange,
            resizeLeft,
            resizeTop,
            resizeRight,
            resizeBottom,
            minimumSize ?? YouTubeMiniPlayerLayoutService.GetMinimumSize(state));
    }

    public static void ApplyCtrlDragResize(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        double startLeft,
        double startTop,
        double startWidth,
        double startHeight,
        double totalHorizontalChange,
        double totalVerticalChange,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp,
        Size? minimumSize = null)
    {
        var bounds = YouTubeMiniPlayerInputService.CalculateCtrlDragResizeBounds(
            YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(state),
            startLeft,
            startTop,
            startWidth,
            startHeight,
            totalHorizontalChange,
            totalVerticalChange,
            minimumSize ?? YouTubeMiniPlayerLayoutService.GetMinimumSize(state));
        ApplyBounds(state, root, bounds, clamp);
    }

    public static void ApplyBounds(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        Rect bounds,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp)
    {
        state.Left = bounds.Left;
        state.Top = bounds.Top;
        state.Width = bounds.Width;
        state.Height = bounds.Height;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        clamp(state, root, false);
    }
}
