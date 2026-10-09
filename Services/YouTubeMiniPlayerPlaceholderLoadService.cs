using System.Windows;
using System.Windows.Input;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerPlaceholderLoadService
{
    internal const double CloseGestureThreshold = 24;

    public static void AttachLoadHandler(
        YouTubeMiniPlayerPlaceholderParts placeholder,
        YouTubeMiniPlayerState state,
        Func<YouTubeMiniPlayerState, Uri, Task> loadRuntimeAsync,
        Action<string> setStatus)
    {
        placeholder.LoadButton.Click += async (_, _) =>
        {
            if (!Uri.TryCreate(YouTubeMiniPlayerNavigationService.CreateNavigationUrl(state), UriKind.Absolute, out var uri))
            {
                setStatus("ミニプレイヤーのURLを読み込めません");
                return;
            }

            try
            {
                await loadRuntimeAsync(state, uri);
                setStatus("YouTubeミニプレイヤーを読み込みました");
            }
            catch (Exception ex)
            {
                setStatus("YouTubeミニプレイヤーを読み込めませんでした: " + ex.Message);
            }
        };
    }

    public static void AttachCloseGestureHandler(
        YouTubeMiniPlayerPlaceholderParts placeholder,
        Func<long?> tryBeginInteraction,
        Action<long> completeInteraction,
        Action close)
    {
        var root = placeholder.Root;
        var tracking = false;
        var closeGestureArmed = false;
        var closeGestureCancelled = false;
        var startPoint = default(Point);
        var directionAnchor = default(Point);
        long? interactionId = null;

        void Reset(bool releaseMouseCapture = true)
        {
            var completedInteractionId = interactionId;
            interactionId = null;
            tracking = false;
            closeGestureArmed = false;
            closeGestureCancelled = false;
            placeholder.CloseGestureIndicator.Visibility = Visibility.Collapsed;

            if (completedInteractionId is { } id)
            {
                completeInteraction(id);
            }

            if (releaseMouseCapture && root.IsMouseCaptured)
            {
                root.ReleaseMouseCapture();
            }
        }

        root.PreviewMouseRightButtonDown += (_, e) =>
        {
            Reset();
            if (placeholder.LoadButton.IsMouseOver)
            {
                return;
            }

            tracking = true;
            startPoint = e.GetPosition(root);
        };
        root.PreviewMouseMove += (_, e) =>
        {
            if (!tracking)
            {
                return;
            }

            if (e.RightButton != MouseButtonState.Pressed)
            {
                Reset();
                return;
            }

            if (closeGestureCancelled)
            {
                e.Handled = true;
                return;
            }

            var currentPoint = e.GetPosition(root);
            if (!closeGestureArmed)
            {
                if (!IsCloseGestureMovement(startPoint, currentPoint))
                {
                    return;
                }

                interactionId = tryBeginInteraction();
                if (interactionId is null)
                {
                    Reset();
                    return;
                }

                closeGestureArmed = true;
                directionAnchor = currentPoint;
                placeholder.CloseGestureIndicator.Visibility = Visibility.Visible;
                root.CaptureMouse();
                e.Handled = true;
                return;
            }

            var directionDistance = Math.Max(
                Math.Abs(currentPoint.X - directionAnchor.X),
                Math.Abs(currentPoint.Y - directionAnchor.Y));
            if (directionDistance >= CloseGestureThreshold)
            {
                if (IsCloseGestureMovement(directionAnchor, currentPoint))
                {
                    directionAnchor = currentPoint;
                }
                else
                {
                    closeGestureArmed = false;
                    closeGestureCancelled = true;
                    placeholder.CloseGestureIndicator.Visibility = Visibility.Collapsed;
                }
            }

            e.Handled = true;
        };
        root.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!tracking)
            {
                return;
            }

            var shouldClose = closeGestureArmed && interactionId is not null;
            var shouldHandle = shouldClose || closeGestureCancelled;
            Reset();
            if (shouldHandle)
            {
                e.Handled = true;
            }

            if (shouldClose)
            {
                close();
            }
        };
        root.LostMouseCapture += (_, _) => Reset(releaseMouseCapture: false);
        root.Unloaded += (_, _) => Reset();
    }

    internal static bool IsCloseGestureMovement(Point startPoint, Point currentPoint)
    {
        var deltaX = currentPoint.X - startPoint.X;
        var deltaY = currentPoint.Y - startPoint.Y;
        return deltaY >= CloseGestureThreshold
            && Math.Abs(deltaY) >= Math.Abs(deltaX) * 1.5;
    }
}
