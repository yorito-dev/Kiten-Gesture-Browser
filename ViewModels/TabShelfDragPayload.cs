using TabBrowser.Services.Workspaces;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class TabShelfDragPayload
{
    public const string DataFormat = "TabBrowser.TabShelfDrag.v1";

    public TabShelfDragPayload(
        IReadOnlyList<CapturedTabSnapshot> tabs,
        IReadOnlyList<YouTubeMiniPlayerWorkspaceState> youTubeMiniPlayers,
        Guid? primaryTabId = null)
    {
        Tabs = tabs;
        YouTubeMiniPlayers = youTubeMiniPlayers;
        PrimaryTabId = primaryTabId;
    }

    public IReadOnlyList<CapturedTabSnapshot> Tabs { get; }

    public IReadOnlyList<YouTubeMiniPlayerWorkspaceState> YouTubeMiniPlayers { get; }

    public Guid? PrimaryTabId { get; }
}

public sealed record TabShelfWindowTransferRequest(
    IReadOnlyList<CapturedTabSnapshot> Tabs,
    Guid PrimaryTabId,
    Guid? TargetWindowId,
    Guid? TargetTabId,
    bool InsertAfterTarget);

public sealed record WorkspaceTabDropRequest(
    IReadOnlyList<CapturedTabSnapshot> Tabs,
    WorkspaceCanvasPoint Point,
    Guid? TargetFolderId = null);

public sealed record WorkspaceShelfDropRequest(
    IReadOnlyList<CapturedTabSnapshot> Tabs,
    IReadOnlyList<YouTubeMiniPlayerWorkspaceState> YouTubeMiniPlayers,
    WorkspaceCanvasPoint Point,
    Guid? TargetFolderId = null);

public sealed record WorkspaceCardDragRoot(
    WorkspaceItem Item,
    double OffsetX,
    double OffsetY);

public sealed class WorkspaceCardDragPayload
{
    public const string DataFormat = "TabBrowser.WorkspaceCardDrag.v1";

    private readonly TaskCompletionSource<bool> _dropCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private WorkspaceCardDragPayload(
        Guid sourceWorkspaceId,
        IReadOnlyList<WorkspaceCardDragRoot> roots,
        IReadOnlyList<WorkspaceItem> items,
        double grabOffsetX,
        double grabOffsetY)
    {
        SourceWorkspaceId = sourceWorkspaceId;
        Roots = roots;
        Items = items;
        GrabOffsetX = grabOffsetX;
        GrabOffsetY = grabOffsetY;
    }

    public Guid SourceWorkspaceId { get; }

    public IReadOnlyList<WorkspaceCardDragRoot> Roots { get; }

    public IReadOnlyList<WorkspaceItem> Items { get; }

    public double GrabOffsetX { get; }

    public double GrabOffsetY { get; }

    internal Task<bool> DropCompletion => _dropCompletion.Task;

    internal void CompleteDrop(bool moved)
    {
        _dropCompletion.TrySetResult(moved);
    }

    public static WorkspaceCardDragPayload? Create(
        Guid sourceWorkspaceId,
        IReadOnlyList<WorkspaceItem> allItems,
        IReadOnlyList<WorkspaceItem> requestedRoots,
        WorkspaceCanvasPoint grabPoint)
    {
        var roots = requestedRoots
            .Where(item => item.WorkspaceId == sourceWorkspaceId)
            .DistinctBy(static item => item.Id)
            .ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        var minimumX = roots.Min(static item => item.X);
        var minimumY = roots.Min(static item => item.Y);
        var includedIds = roots.Select(static item => item.Id).ToHashSet();
        var addedDescendant = true;
        while (addedDescendant)
        {
            addedDescendant = false;
            foreach (var item in allItems.Where(item =>
                         item.WorkspaceId == sourceWorkspaceId
                         && item.ParentFolderId is { } parentFolderId
                         && includedIds.Contains(parentFolderId)))
            {
                addedDescendant |= includedIds.Add(item.Id);
            }
        }

        var items = allItems
            .Where(item => includedIds.Contains(item.Id))
            .DistinctBy(static item => item.Id)
            .ToList();
        foreach (var root in roots.Where(root => items.All(item => item.Id != root.Id)))
        {
            items.Add(root);
        }

        return new WorkspaceCardDragPayload(
            sourceWorkspaceId,
            roots.Select(item => new WorkspaceCardDragRoot(
                item,
                item.X - minimumX,
                item.Y - minimumY)).ToList(),
            items,
            grabPoint.X - minimumX,
            grabPoint.Y - minimumY);
    }

    public static Guid? FindFolderDropTargetId(
        IReadOnlyList<WorkspaceItem> visibleItems,
        IReadOnlySet<Guid> draggedItemIds,
        WorkspaceCanvasPoint dropPoint)
    {
        for (var index = visibleItems.Count - 1; index >= 0; index--)
        {
            var item = visibleItems[index];
            if (item is not FolderItem || draggedItemIds.Contains(item.Id))
            {
                continue;
            }

            var width = Math.Clamp(item.Width, 76, 88);
            var height = Math.Clamp(item.Height, 92, 100);
            if (dropPoint.X >= item.X
                && dropPoint.X <= item.X + width
                && dropPoint.Y >= item.Y
                && dropPoint.Y <= item.Y + height)
            {
                return item.Id;
            }
        }

        return null;
    }

    public static bool ShouldStartWindowDrag(
        bool isPointerInsideCanvas,
        bool isPointerOverDifferentWindow)
    {
        return !isPointerInsideCanvas || isPointerOverDifferentWindow;
    }
}

public sealed record WorkspaceCardDropRequest(
    WorkspaceCardDragPayload Payload,
    Guid TargetWorkspaceId,
    WorkspaceCanvasPoint Point,
    Guid? TargetFolderId = null,
    bool UseFolderDefaultPosition = false)
{
    public WorkspaceCanvasPoint ResolveAnchor(WorkspaceCanvasPoint? folderDefaultPoint = null)
    {
        if (UseFolderDefaultPosition
            && TargetFolderId is not null
            && folderDefaultPoint is not null)
        {
            return folderDefaultPoint;
        }

        return new WorkspaceCanvasPoint(
            Math.Max(0, Point.X - Payload.GrabOffsetX),
            Math.Max(0, Point.Y - Payload.GrabOffsetY));
    }
}

public static class WorkspaceCardPlacement
{
    public const double CanvasWidth = 4800;
    public const double CanvasHeight = 3200;

    private const double AvoidanceStepX = 32;
    private const double AvoidanceStepY = 40;
    private const int MaxAvoidanceRadius = 45;

    public static IReadOnlyList<WorkspaceItemMove> CreateMoves(
        WorkspaceCardDragPayload payload,
        Guid targetWorkspaceId,
        Guid? targetFolderId,
        WorkspaceCanvasPoint preferredAnchor,
        IReadOnlyList<WorkspaceItem> targetItems)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (targetWorkspaceId == Guid.Empty)
        {
            throw new ArgumentException("Target workspace is required.", nameof(targetWorkspaceId));
        }

        var draggedIds = payload.Items.Select(static item => item.Id).ToHashSet();
        var targetPeers = targetItems
            .Where(item => item.ParentFolderId == targetFolderId && !draggedIds.Contains(item.Id))
            .ToList();
        var groupWidth = payload.Roots.Max(static root => root.OffsetX + GetCardWidth(root.Item));
        var groupHeight = payload.Roots.Max(static root => root.OffsetY + GetCardHeight(root.Item));
        var anchor = FindAvailableAnchor(
            preferredAnchor,
            payload.Roots,
            targetPeers,
            groupWidth,
            groupHeight);
        var rootPositions = payload.Roots.ToDictionary(
            static root => root.Item.Id,
            root => new WorkspaceCanvasPoint(
                Math.Round(anchor.X + root.OffsetX, 1),
                Math.Round(anchor.Y + root.OffsetY, 1)));

        return payload.Items
            .DistinctBy(static item => item.Id)
            .Select(item =>
            {
                var isRoot = rootPositions.TryGetValue(item.Id, out var position);
                return new WorkspaceItemMove(
                    item.Id,
                    item.WorkspaceId,
                    item.ParentFolderId,
                    targetWorkspaceId,
                    isRoot ? targetFolderId : item.ParentFolderId,
                    isRoot ? position!.X : item.X,
                    isRoot ? position!.Y : item.Y);
            })
            .ToList();
    }

    private static WorkspaceCanvasPoint FindAvailableAnchor(
        WorkspaceCanvasPoint preferredAnchor,
        IReadOnlyList<WorkspaceCardDragRoot> roots,
        IReadOnlyList<WorkspaceItem> targetPeers,
        double groupWidth,
        double groupHeight)
    {
        var start = ClampAnchor(preferredAnchor, groupWidth, groupHeight);
        if (IsAvailable(start, roots, targetPeers))
        {
            return start;
        }

        for (var radius = 1; radius <= MaxAvoidanceRadius; radius++)
        {
            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                if (TryCandidate(offsetX, -radius, out var candidate)
                    || TryCandidate(offsetX, radius, out candidate))
                {
                    return candidate;
                }
            }

            for (var offsetY = -radius + 1; offsetY < radius; offsetY++)
            {
                if (TryCandidate(-radius, offsetY, out var candidate)
                    || TryCandidate(radius, offsetY, out candidate))
                {
                    return candidate;
                }
            }
        }

        for (var y = 0.0; y <= Math.Max(0, CanvasHeight - groupHeight); y += AvoidanceStepY)
        {
            for (var x = 0.0; x <= Math.Max(0, CanvasWidth - groupWidth); x += AvoidanceStepX)
            {
                var candidate = new WorkspaceCanvasPoint(x, y);
                if (IsAvailable(candidate, roots, targetPeers))
                {
                    return candidate;
                }
            }
        }

        return start;

        bool TryCandidate(int offsetX, int offsetY, out WorkspaceCanvasPoint candidate)
        {
            candidate = ClampAnchor(
                new WorkspaceCanvasPoint(
                    start.X + (offsetX * AvoidanceStepX),
                    start.Y + (offsetY * AvoidanceStepY)),
                groupWidth,
                groupHeight);
            return IsAvailable(candidate, roots, targetPeers);
        }
    }

    private static WorkspaceCanvasPoint ClampAnchor(
        WorkspaceCanvasPoint point,
        double groupWidth,
        double groupHeight)
    {
        return new WorkspaceCanvasPoint(
            Math.Round(Math.Clamp(point.X, 0, Math.Max(0, CanvasWidth - groupWidth)), 1),
            Math.Round(Math.Clamp(point.Y, 0, Math.Max(0, CanvasHeight - groupHeight)), 1));
    }

    private static bool IsAvailable(
        WorkspaceCanvasPoint anchor,
        IReadOnlyList<WorkspaceCardDragRoot> roots,
        IReadOnlyList<WorkspaceItem> targetPeers)
    {
        return roots.All(root => targetPeers.All(item => !RectanglesOverlap(
            anchor.X + root.OffsetX,
            anchor.Y + root.OffsetY,
            GetCardWidth(root.Item),
            GetCardHeight(root.Item),
            item.X,
            item.Y,
            GetCardWidth(item),
            GetCardHeight(item))));
    }

    private static double GetCardWidth(WorkspaceItem item)
    {
        return Math.Clamp(item.Width, 76, 88);
    }

    private static double GetCardHeight(WorkspaceItem item)
    {
        return Math.Clamp(item.Height, 92, 100);
    }

    private static bool RectanglesOverlap(
        double x1,
        double y1,
        double width1,
        double height1,
        double x2,
        double y2,
        double width2,
        double height2)
    {
        return x1 < x2 + width2
            && x1 + width1 > x2
            && y1 < y2 + height2
            && y1 + height1 > y2;
    }
}
