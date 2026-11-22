using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerArrangementSessionCoordinator
{
    private readonly List<YouTubeMiniPlayerArrangementSnapshot> _initialArrangement = [];
    private YouTubeMiniPlayerLinearPlacement _horizontalPlacement =
        YouTubeMiniPlayerLinearPlacement.Second;
    private YouTubeMiniPlayerLinearPlacement _verticalPlacement =
        YouTubeMiniPlayerLinearPlacement.Second;
    private YouTubeMiniPlayerArrangeMode? _linearPlacementPopupMode;
    private YouTubeMiniPlayerArrangeMode? _confirmedLinearPlacementMode;

    public YouTubeMiniPlayerLinearPlacement HorizontalPlacement => _horizontalPlacement;

    public YouTubeMiniPlayerLinearPlacement VerticalPlacement => _verticalPlacement;

    public YouTubeMiniPlayerArrangeMode? LinearPlacementPopupMode => _linearPlacementPopupMode;

    public bool HasInitialArrangement => _initialArrangement.Count > 0;
    public IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot> PreviousArrangement { get; private set; } = [];

    public void OpenLinearPlacementPopup(YouTubeMiniPlayerArrangeMode mode, bool hasPreview = false)
    {
        _linearPlacementPopupMode = mode;
        if (!hasPreview || _confirmedLinearPlacementMode != mode)
        {
            _confirmedLinearPlacementMode = null;
        }
    }

    public void CloseLinearPlacementPopup()
    {
        _linearPlacementPopupMode = null;
    }

    public bool TrySetLinearPlacement(
        YouTubeMiniPlayerArrangeMode mode,
        YouTubeMiniPlayerLinearPlacement placement, bool confirm = false)
    {
        if (!confirm && _confirmedLinearPlacementMode == mode)
        {
            return false;
        }

        switch (mode)
        {
            case YouTubeMiniPlayerArrangeMode.Horizontal:
                _horizontalPlacement = placement;
                break;
            case YouTubeMiniPlayerArrangeMode.Vertical:
                _verticalPlacement = placement;
                break;
            default:
                return false;
        }

        if (confirm) _confirmedLinearPlacementMode = mode;
        return true;
    }

    public YouTubeMiniPlayerLinearPlacement GetLinearPlacement(YouTubeMiniPlayerArrangeMode mode)
    {
        return mode switch
        {
            YouTubeMiniPlayerArrangeMode.Horizontal => _horizontalPlacement,
            YouTubeMiniPlayerArrangeMode.Vertical => _verticalPlacement,
            _ => YouTubeMiniPlayerLinearPlacement.Second
        };
    }

    public void RecordInitialArrangement(
        IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot> allItems,
        IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot> targets)
    {
        var snapshots = _initialArrangement.Count == 0 ? allItems : targets;
        foreach (var snapshot in snapshots)
        {
            if (_initialArrangement.Any(existing =>
                    existing.OwnerWindowId == snapshot.OwnerWindowId
                    && existing.StateId == snapshot.StateId))
            {
                continue;
            }

            _initialArrangement.Add(snapshot);
        }
    }

    public IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot> SnapshotInitialArrangement()
    {
        return _initialArrangement.ToArray();
    }

    public void ClearInitialArrangement()
    {
        _initialArrangement.Clear();
    }

    public void RecordPreviousArrangement(IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot> targets)
        => PreviousArrangement = targets;

    public void ClearPreviousArrangement()
        => PreviousArrangement = [];
}

internal readonly record struct YouTubeMiniPlayerArrangementSnapshot(
    Guid OwnerWindowId,
    Guid StateId,
    double Left,
    double Top,
    double Width,
    double Height,
    YouTubeLiveChatArrangementSnapshot? Chat);

internal readonly record struct YouTubeLiveChatArrangementSnapshot(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsDocked,
    YouTubeLiveChatDockSide DockSide)
{
    public bool RemoveSpacing { get; init; }
}
