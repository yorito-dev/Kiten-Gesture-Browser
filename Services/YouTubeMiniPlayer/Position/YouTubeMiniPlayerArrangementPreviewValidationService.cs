using System;
using System.Collections.Generic;
using System.Linq;

namespace TabBrowser.Services;

internal readonly record struct YouTubeMiniPlayerArrangementPreviewBounds(
    double Left,
    double Top,
    double Width,
    double Height);

internal sealed record YouTubeMiniPlayerArrangementPreviewValidationItem(
    Guid StateId,
    object? RuntimeIdentity,
    bool IsFullScreen,
    int PlayerWindowState,
    YouTubeMiniPlayerArrangementPreviewBounds PlayerBounds,
    object? ChatWindowIdentity,
    int? ChatWindowState,
    YouTubeMiniPlayerArrangementPreviewBounds? ChatWindowBounds);

internal sealed record YouTubeMiniPlayerArrangementPreviewValidationSnapshot(
    YouTubeMiniPlayerArrangementPreviewBounds WorkArea,
    IReadOnlyList<YouTubeMiniPlayerArrangementPreviewValidationItem> Items);

internal static class YouTubeMiniPlayerArrangementPreviewValidationService
{
    private const double BoundsTolerance = 1.0;

    public static bool IsCurrent(
        YouTubeMiniPlayerArrangementPreviewValidationSnapshot baseline,
        YouTubeMiniPlayerArrangementPreviewValidationSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        if (!AreBoundsClose(baseline.WorkArea, current.WorkArea))
        {
            return false;
        }

        foreach (var expected in baseline.Items)
        {
            var actual = current.Items.FirstOrDefault(item => item.StateId == expected.StateId);
            if (actual is null
                || !ReferenceEquals(actual.RuntimeIdentity, expected.RuntimeIdentity)
                || actual.IsFullScreen != expected.IsFullScreen
                || actual.PlayerWindowState != expected.PlayerWindowState
                || !AreBoundsClose(actual.PlayerBounds, expected.PlayerBounds)
                || !ReferenceEquals(actual.ChatWindowIdentity, expected.ChatWindowIdentity))
            {
                return false;
            }

            if (expected.ChatWindowIdentity is not null
                && (actual.ChatWindowState != expected.ChatWindowState
                    || expected.ChatWindowBounds is not { } expectedChatBounds
                    || actual.ChatWindowBounds is not { } actualChatBounds
                    || !AreBoundsClose(actualChatBounds, expectedChatBounds)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreBoundsClose(
        YouTubeMiniPlayerArrangementPreviewBounds left,
        YouTubeMiniPlayerArrangementPreviewBounds right)
    {
        return Math.Abs(left.Left - right.Left) <= BoundsTolerance
            && Math.Abs(left.Top - right.Top) <= BoundsTolerance
            && Math.Abs(left.Width - right.Width) <= BoundsTolerance
            && Math.Abs(left.Height - right.Height) <= BoundsTolerance;
    }
}
