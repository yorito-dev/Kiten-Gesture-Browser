namespace TabBrowser.Services;

internal readonly record struct YouTubeMiniPlayerDragCandidateSnapshot(
    Guid? SourceId,
    bool SourceWasSelected,
    double StartX,
    double StartY);

internal readonly record struct YouTubeMiniPlayerDropCueSnapshot(
    Guid? TargetId,
    bool InsertAfter);

internal sealed class YouTubeMiniPlayerListDragCoordinator
{
    private Guid? _candidateSourceId;
    private bool _candidateSourceWasSelected;
    private double _candidateStartX;
    private double _candidateStartY;
    private bool _dragActive;
    private Guid? _dropCueTargetId;
    private bool _dropCueInsertAfter;

    public bool IsDragActive => _dragActive;

    public void BeginCandidate(
        Guid sourceId,
        bool sourceWasSelected,
        double startX,
        double startY)
    {
        _candidateSourceId = sourceId;
        _candidateSourceWasSelected = sourceWasSelected;
        _candidateStartX = startX;
        _candidateStartY = startY;
    }

    public YouTubeMiniPlayerDragCandidateSnapshot CaptureCandidate()
    {
        return new YouTubeMiniPlayerDragCandidateSnapshot(
            _candidateSourceId,
            _candidateSourceWasSelected,
            _candidateStartX,
            _candidateStartY);
    }

    public bool HasExceededDragThreshold(
        double currentX,
        double currentY,
        double minimumHorizontalDistance,
        double minimumVerticalDistance)
    {
        return _candidateSourceId is not null
            && (Math.Abs(currentX - _candidateStartX) >= minimumHorizontalDistance
                || Math.Abs(currentY - _candidateStartY) >= minimumVerticalDistance);
    }

    public void ResetCandidate()
    {
        _candidateSourceId = null;
        _candidateSourceWasSelected = false;
        _candidateStartX = 0;
        _candidateStartY = 0;
    }

    public void BeginDrag()
    {
        _dragActive = true;
    }

    public void EndDrag()
    {
        _dragActive = false;
    }

    public bool TryReplaceDropCue(
        Guid? targetId,
        bool insertAfter,
        out YouTubeMiniPlayerDropCueSnapshot previous)
    {
        previous = new YouTubeMiniPlayerDropCueSnapshot(
            _dropCueTargetId,
            _dropCueInsertAfter);
        if (_dropCueTargetId == targetId && _dropCueInsertAfter == insertAfter)
        {
            return false;
        }

        _dropCueTargetId = targetId;
        _dropCueInsertAfter = insertAfter;
        return true;
    }

    public YouTubeMiniPlayerDropCueSnapshot ClearDropCue()
    {
        var previous = new YouTubeMiniPlayerDropCueSnapshot(
            _dropCueTargetId,
            _dropCueInsertAfter);
        _dropCueTargetId = null;
        _dropCueInsertAfter = false;
        return previous;
    }

    public bool TryCreateReorderPlan(
        IReadOnlyList<Guid> currentOrder,
        Guid sourceId,
        Guid? targetId,
        bool insertAfter,
        out IReadOnlyList<Guid> reorderedIds)
    {
        ArgumentNullException.ThrowIfNull(currentOrder);
        reorderedIds = Array.Empty<Guid>();

        var sourceIndex = -1;
        for (var index = 0; index < currentOrder.Count; index++)
        {
            if (currentOrder[index] == sourceId)
            {
                sourceIndex = index;
                break;
            }
        }

        if (sourceIndex < 0 || targetId == sourceId)
        {
            return false;
        }

        var plannedOrder = currentOrder.ToList();
        plannedOrder.RemoveAt(sourceIndex);
        var insertionIndex = plannedOrder.Count;
        if (targetId is { } target)
        {
            var targetIndex = plannedOrder.IndexOf(target);
            if (targetIndex < 0)
            {
                return false;
            }

            insertionIndex = targetIndex + (insertAfter ? 1 : 0);
        }

        plannedOrder.Insert(insertionIndex, sourceId);
        if (currentOrder.SequenceEqual(plannedOrder))
        {
            return false;
        }

        reorderedIds = plannedOrder;
        return true;
    }
}
