using System.Windows;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerArrangementPlanningFailureReason
{
    None,
    NoEligibleTargets,
    NoLayout
}

internal sealed record YouTubeMiniPlayerArrangementCandidate(
    Guid Id,
    bool IsMinimized,
    bool IsFullScreen,
    bool IsShorts,
    double Width,
    double Height,
    double? VideoAspectRatio,
    bool HasChat,
    Size? MinimumSize = null);

internal sealed record YouTubeMiniPlayerArrangementPlanningResult(
    IReadOnlyList<Guid> TargetIds,
    IReadOnlySet<Guid> FocusIds,
    IReadOnlyList<YouTubeMiniPlayerArrangementResult> Layout,
    YouTubeMiniPlayerArrangementPlanningFailureReason FailureReason);

internal static class YouTubeMiniPlayerArrangementPlanningService
{
    public static YouTubeMiniPlayerArrangementPlanningResult CreatePlan(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        IReadOnlyList<YouTubeMiniPlayerArrangementCandidate> candidates,
        IReadOnlyList<Guid> selectedIds,
        bool includeMinimized,
        YouTubeMiniPlayerLinearPlacement linearPlacement,
        Random? random = null)
    {
        var eligibleCandidates = candidates
            .Where(candidate =>
                !candidate.IsFullScreen
                && (includeMinimized || !candidate.IsMinimized))
            .ToList();
        var focusIds = new HashSet<Guid>();
        List<YouTubeMiniPlayerArrangementCandidate> targets;
        if (mode == YouTubeMiniPlayerArrangeMode.Focus)
        {
            targets = eligibleCandidates;
            var eligibleIds = targets.Select(static candidate => candidate.Id).ToHashSet();
            focusIds = selectedIds.Where(eligibleIds.Contains).ToHashSet();
            if (focusIds.Count == 0 && targets.Count > 0)
            {
                focusIds.Add(targets[0].Id);
            }
        }
        else if (selectedIds.Count > 0)
        {
            var candidatesById = eligibleCandidates.ToDictionary(static candidate => candidate.Id);
            targets = selectedIds
                .Where(candidatesById.ContainsKey)
                .Select(id => candidatesById[id])
                .ToList();
        }
        else
        {
            targets = eligibleCandidates;
        }

        if (targets.Count == 0)
        {
            return CreateFailure(YouTubeMiniPlayerArrangementPlanningFailureReason.NoEligibleTargets);
        }

        var layout = YouTubeMiniPlayerArrangementService.CreateLayout(
            mode,
            workArea,
            targets.Select(static candidate => new YouTubeMiniPlayerArrangementItem(
                candidate.Id,
                candidate.IsShorts,
                candidate.Width,
                candidate.Height,
                candidate.VideoAspectRatio,
                candidate.HasChat,
                candidate.MinimumSize)).ToList(),
            random,
            focusIds,
            linearPlacement);
        if (layout.Count == 0)
        {
            return new YouTubeMiniPlayerArrangementPlanningResult(
                targets.Select(static candidate => candidate.Id).ToArray(),
                focusIds,
                [],
                YouTubeMiniPlayerArrangementPlanningFailureReason.NoLayout);
        }

        return new YouTubeMiniPlayerArrangementPlanningResult(
            targets.Select(static candidate => candidate.Id).ToArray(),
            focusIds,
            layout,
            YouTubeMiniPlayerArrangementPlanningFailureReason.None);
    }

    private static YouTubeMiniPlayerArrangementPlanningResult CreateFailure(
        YouTubeMiniPlayerArrangementPlanningFailureReason reason)
    {
        return new YouTubeMiniPlayerArrangementPlanningResult([], new HashSet<Guid>(), [], reason);
    }
}
