using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public enum WorkspaceArrangeMode
{
    CurrentPosition,
    Title,
    Type,
    Site,
    CreatedAt
}

public enum WorkspaceArrangeScope
{
    Selection,
    All
}

public sealed record WorkspaceArrangementCard(
    Guid ItemId,
    double X,
    double Y,
    double Width,
    double Height,
    string Title,
    WorkspaceItemType Type,
    string SiteOrLocation,
    DateTimeOffset CreatedAt);

public readonly record struct WorkspaceArrangementBounds(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Intersects(WorkspaceArrangementCard card)
    {
        return X < card.X + card.Width
            && Right > card.X
            && Y < card.Y + card.Height
            && Bottom > card.Y;
    }
}

public sealed record WorkspaceArrangementPlacement(Guid ItemId, double X, double Y);

public sealed record WorkspaceArrangementResult(
    bool Succeeded,
    IReadOnlyList<WorkspaceArrangementPlacement> Placements,
    string ErrorMessage)
{
    public static WorkspaceArrangementResult Success(IReadOnlyList<WorkspaceArrangementPlacement> placements)
    {
        return new WorkspaceArrangementResult(true, placements, string.Empty);
    }

    public static WorkspaceArrangementResult Failure(string errorMessage)
    {
        return new WorkspaceArrangementResult(false, [], errorMessage);
    }
}

public sealed class WorkspaceArrangementService
{
    public const string SelectionAreaUnavailableMessage = "選択範囲内に整列できる空き領域がありません";

    private const double GridSize = 8;
    private const double DefaultCellWidth = 88;
    private const double DefaultCellHeight = 100;
    private const double ArrangeMargin = 24;

    public WorkspaceArrangementResult ArrangeAll(
        IReadOnlyList<WorkspaceArrangementCard> cards,
        WorkspaceArrangementBounds visibleArea,
        WorkspaceArrangeMode mode)
    {
        if (cards.Count == 0)
        {
            return WorkspaceArrangementResult.Success([]);
        }

        var orderedCards = OrderCards(cards, mode);
        var cellWidth = AlignUp(Math.Max(DefaultCellWidth, orderedCards.Max(static card => card.Width)));
        var cellHeight = AlignUp(Math.Max(DefaultCellHeight, orderedCards.Max(static card => card.Height)));
        var hasVisibleArea = IsFinitePositive(visibleArea.Width) && IsFinitePositive(visibleArea.Height);
        var startX = AlignToGrid(hasVisibleArea ? visibleArea.X + ArrangeMargin : ArrangeMargin);
        var startY = AlignToGrid(hasVisibleArea ? visibleArea.Y + ArrangeMargin : ArrangeMargin);
        var usableWidth = hasVisibleArea
            ? Math.Max(cellWidth, visibleArea.Width - ArrangeMargin * 2)
            : cellWidth;
        var columnCount = Math.Max(1, (int)Math.Floor(usableWidth / cellWidth));
        var placements = orderedCards
            .Select((card, index) => new WorkspaceArrangementPlacement(
                card.ItemId,
                startX + index % columnCount * cellWidth,
                startY + index / columnCount * cellHeight))
            .ToList();
        return WorkspaceArrangementResult.Success(placements);
    }

    public WorkspaceArrangementResult ArrangeSelection(
        IReadOnlyList<WorkspaceArrangementCard> selectedCards,
        IReadOnlyList<WorkspaceArrangementCard> unselectedCards,
        WorkspaceArrangeMode mode)
    {
        if (selectedCards.Count == 0)
        {
            return WorkspaceArrangementResult.Success([]);
        }

        var bounds = GetOuterBounds(selectedCards);
        var obstacles = unselectedCards
            .Where(bounds.Intersects)
            .Select(static card => new OccupiedRectangle(card.X, card.Y, card.Width, card.Height))
            .ToList();
        var occupied = new List<OccupiedRectangle>(obstacles);
        var placements = new List<WorkspaceArrangementPlacement>(selectedCards.Count);
        foreach (var card in OrderCards(selectedCards, mode))
        {
            if (!TryFindPlacement(card, bounds, occupied, out var placement))
            {
                return WorkspaceArrangementResult.Failure(SelectionAreaUnavailableMessage);
            }

            placements.Add(new WorkspaceArrangementPlacement(card.ItemId, placement.X, placement.Y));
            occupied.Add(placement);
        }

        return WorkspaceArrangementResult.Success(placements);
    }

    private static bool TryFindPlacement(
        WorkspaceArrangementCard card,
        WorkspaceArrangementBounds bounds,
        IReadOnlyList<OccupiedRectangle> occupied,
        out OccupiedRectangle placement)
    {
        var startX = AlignUp(bounds.X);
        var startY = AlignUp(bounds.Y);
        var lastX = bounds.Right - card.Width;
        var lastY = bounds.Bottom - card.Height;
        for (var y = startY; y <= lastY + 0.01; y += GridSize)
        {
            for (var x = startX; x <= lastX + 0.01; x += GridSize)
            {
                var candidate = new OccupiedRectangle(x, y, card.Width, card.Height);
                if (!occupied.Any(candidate.Overlaps))
                {
                    placement = candidate;
                    return true;
                }
            }
        }

        placement = default;
        return false;
    }

    private static WorkspaceArrangementBounds GetOuterBounds(
        IReadOnlyList<WorkspaceArrangementCard> cards)
    {
        var left = cards.Min(static card => card.X);
        var top = cards.Min(static card => card.Y);
        var right = cards.Max(static card => card.X + card.Width);
        var bottom = cards.Max(static card => card.Y + card.Height);
        return new WorkspaceArrangementBounds(left, top, right - left, bottom - top);
    }

    private static List<WorkspaceArrangementCard> OrderCards(
        IReadOnlyList<WorkspaceArrangementCard> cards,
        WorkspaceArrangeMode mode)
    {
        return mode switch
        {
            WorkspaceArrangeMode.CurrentPosition => cards
                .OrderBy(static card => card.Y)
                .ThenBy(static card => card.X)
                .ThenBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            WorkspaceArrangeMode.Title => cards
                .OrderBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static card => card.Type)
                .ToList(),
            WorkspaceArrangeMode.Type => cards
                .OrderBy(static card => GetTypeOrder(card.Type))
                .ThenBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            WorkspaceArrangeMode.Site => cards
                .OrderBy(static card => card.SiteOrLocation, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            WorkspaceArrangeMode.CreatedAt => cards
                .OrderBy(static card => card.CreatedAt)
                .ThenBy(static card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => cards.ToList()
        };
    }

    private static int GetTypeOrder(WorkspaceItemType type)
    {
        return type switch
        {
            WorkspaceItemType.Folder => 0,
            WorkspaceItemType.SessionGroup => 1,
            WorkspaceItemType.ShellItem => 2,
            WorkspaceItemType.Bookmark => 3,
            WorkspaceItemType.Note => 4,
            WorkspaceItemType.YouTubeMiniPlayerSet => 5,
            _ => 9
        };
    }

    private static double AlignToGrid(double value)
    {
        return Math.Max(ArrangeMargin, Math.Round(value / GridSize) * GridSize);
    }

    private static double AlignUp(double value)
    {
        return Math.Ceiling(value / GridSize) * GridSize;
    }

    private static bool IsFinitePositive(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private readonly record struct OccupiedRectangle(double X, double Y, double Width, double Height)
    {
        public bool Overlaps(OccupiedRectangle other)
        {
            return X < other.X + other.Width
                && X + Width > other.X
                && Y < other.Y + other.Height
                && Y + Height > other.Y;
        }
    }
}
