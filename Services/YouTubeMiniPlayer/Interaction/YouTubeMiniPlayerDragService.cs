using System;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerDragService
{
    public static void ApplyDrag(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        double startLeft,
        double startTop,
        Point dragStartScreenPoint,
        Point currentScreenPoint,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clamp)
    {
        var position = YouTubeMiniPlayerInputService.CalculateDragPosition(
            startLeft,
            startTop,
            dragStartScreenPoint,
            currentScreenPoint);
        state.Left = position.X;
        state.Top = position.Y;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        clamp(state, root, false);
    }
}
