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

    public YouTubeMiniPlayerLinearPlacement HorizontalPlacement => _horizontalPlacement;

    public YouTubeMiniPlayerLinearPlacement VerticalPlacement => _verticalPlacement;

    public YouTubeMiniPlayerArrangeMode? LinearPlacementPopupMode => _linearPlacementPopupMode;

    public bool HasInitialArrangement => _initialArrangement.Count > 0;

    public void OpenLinearPlacementPopup(YouTubeMiniPlayerArrangeMode mode)
    {
        _linearPlacementPopupMode = mode;
    }

    public void CloseLinearPlacementPopup()
    {
        _linearPlacementPopupMode = null;
    }

    public bool TrySetLinearPlacement(
        YouTubeMiniPlayerArrangeMode mode,
        YouTubeMiniPlayerLinearPlacement placement)
    {
        if (mode == YouTubeMiniPlayerArrangeMode.Horizontal)
        {
            _horizontalPlacement = placement;
            return true;
        }

        if (mode == YouTubeMiniPlayerArrangeMode.Vertical)
        {
            _verticalPlacement = placement;
            return true;
        }

        return false;
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
    YouTubeLiveChatDockSide DockSide);
