using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerArrangementPlanningFailureReason
{
    None,
    NoEligibleTargets,
    NoLayout,
    AreaUnavailable,
    RegionNotSelected
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
    Size? MinimumSize = null)
{
    public bool HasDockedChat { get; init; }
    public YouTubeLiveChatDockSide ChatDockSide { get; init; } = YouTubeLiveChatDockSide.Right;
}

internal sealed record YouTubeMiniPlayerArrangementPlanningResult(
    IReadOnlyList<Guid> TargetIds,
    IReadOnlySet<Guid> FocusIds,
    IReadOnlyList<YouTubeMiniPlayerArrangementResult> Layout,
    YouTubeMiniPlayerArrangementPlanningFailureReason FailureReason)
{
    public Rect? PlacementArea { get; init; }
}

internal static class YouTubeMiniPlayerArrangementPlanningService
{
    public static YouTubeMiniPlayerArrangementPlanningResult CreatePlan(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        IReadOnlyList<YouTubeMiniPlayerArrangementCandidate> candidates,
        IReadOnlyList<Guid> selectedIds,
        bool includeMinimized,
        YouTubeMiniPlayerLinearPlacement linearPlacement,
        Random? random = null,
        YouTubeMiniPlayerArrangementAreaRequest? areaRequest = null,
        bool removeSpacing = false)
    {
        var targets = GetTargets(candidates, selectedIds, includeMinimized);
        var focusIds = new HashSet<Guid>();
        if (mode == YouTubeMiniPlayerArrangeMode.Focus)
        {
            focusIds = selectedIds.Where(id => targets.Any(candidate => candidate.Id == id)).ToHashSet();
            if (focusIds.Count == 0 && targets.Count > 0) focusIds.Add(targets[0].Id);
        }

        if (targets.Count == 0)
        {
            return CreateFailure(YouTubeMiniPlayerArrangementPlanningFailureReason.NoEligibleTargets);
        }

        var items = targets.Select(static candidate => new YouTubeMiniPlayerArrangementItem(
                candidate.Id,
                candidate.IsShorts,
                candidate.Width,
                candidate.Height,
                candidate.VideoAspectRatio,
                candidate.HasChat,
                candidate.MinimumSize)).ToList();
        if (areaRequest is { IsAvailable: false }) return CreateFailure(YouTubeMiniPlayerArrangementPlanningFailureReason.AreaUnavailable);
        if (areaRequest is { Mode: YouTubeMiniPlayerArrangementAreaMode.SelectedRegion, SelectedRegion: null })
            return CreateFailure(YouTubeMiniPlayerArrangementPlanningFailureReason.RegionNotSelected);

        IReadOnlyList<Rect> areas = areaRequest?.Mode switch
        {
            YouTubeMiniPlayerArrangementAreaMode.FreeSpace => YouTubeMiniPlayerArrangementAreaService.FindEmptyAreas(workArea, areaRequest.Obstacles),
            YouTubeMiniPlayerArrangementAreaMode.SelectedRegion => areaRequest.SelectedRegion is { } region
                && YouTubeMiniPlayerArrangementAreaService.IsValid(region) && workArea.Contains(region)
                ? [region, .. (areaRequest.OutsideWorkAreas.Count == 0 ? []
                    : YouTubeMiniPlayerArrangementAreaService.FindEmptyAreas(region, areaRequest.OutsideWorkAreas))] : [],
            _ => [workArea]
        };
        IReadOnlyList<YouTubeMiniPlayerArrangementResult> layout = [];
        Rect? placementArea = null;
        var bestScore = -1.0;
        var validateFit = removeSpacing || areaRequest is { Mode: not YouTubeMiniPlayerArrangementAreaMode.Display };
        var minimums = removeSpacing ? targets.ToDictionary(candidate => candidate.Id,
            YouTubeMiniPlayerArrangementAreaService.GetRequiredMinimum) : null;
        foreach (var area in areas)
        {
            var candidateLayout = validateFit
                ? YouTubeMiniPlayerArrangementAreaService.CreateLayout(mode, area, targets, random, focusIds, linearPlacement, removeSpacing)
                : YouTubeMiniPlayerArrangementService.CreateLayout(mode, area, items, random, focusIds, linearPlacement);
            if (candidateLayout.Count == 0) continue;
            if (validateFit)
            {
                if (candidateLayout.Count != targets.Count || candidateLayout.Select(result => result.Id).Distinct().Count() != targets.Count) continue;
                if (mode == YouTubeMiniPlayerArrangeMode.Chat && items.Any(item => item.HasChat
                    && !candidateLayout.Any(result => result.Id == item.Id && result.ChatBounds.HasValue))) continue;
                if (!YouTubeMiniPlayerArrangementAreaService.Fits(candidateLayout, area,
                    areaRequest?.PlacementExclusions ?? [],
                    mode == YouTubeMiniPlayerArrangeMode.Random, minimums)) continue;
            }
            var score = removeSpacing && mode == YouTubeMiniPlayerArrangeMode.Grid
                ? candidateLayout.Sum(result => result.Bounds.Width * result.Bounds.Height)
                : candidateLayout.Min(result => result.Bounds.Width * result.Bounds.Height);
            if (score <= bestScore) continue;
            bestScore = score;
            layout = candidateLayout;
            placementArea = area;
            if (areaRequest is { Mode: YouTubeMiniPlayerArrangementAreaMode.SelectedRegion } && area == areaRequest.SelectedRegion) break;
        }
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
            YouTubeMiniPlayerArrangementPlanningFailureReason.None)
        { PlacementArea = placementArea };
    }

    public static List<YouTubeMiniPlayerArrangementCandidate> GetTargets(
        IReadOnlyList<YouTubeMiniPlayerArrangementCandidate> candidates, IReadOnlyList<Guid> selectedIds, bool includeMinimized)
    {
        var eligibleCandidates = candidates.Where(candidate => !candidate.IsFullScreen && (includeMinimized || !candidate.IsMinimized)).ToList();
        List<YouTubeMiniPlayerArrangementCandidate> targets;
        if (selectedIds.Count > 0)
        {
            var candidatesById = eligibleCandidates.ToDictionary(candidate => candidate.Id);
            targets = selectedIds.Where(candidatesById.ContainsKey).Select(id => candidatesById[id]).ToList();
        }
        else
        {
            targets = eligibleCandidates;
        }
        return targets;
    }

    private static YouTubeMiniPlayerArrangementPlanningResult CreateFailure(
        YouTubeMiniPlayerArrangementPlanningFailureReason reason)
    {
        return new YouTubeMiniPlayerArrangementPlanningResult([], new HashSet<Guid>(), [], reason);
    }
}
