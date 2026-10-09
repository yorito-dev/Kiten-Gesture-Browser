namespace TabBrowser.Models;

public sealed class SplitViewModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WindowId { get; set; }

    public Guid LeftTabId { get; set; }

    public Guid RightTabId { get; set; }

    public string ActivePane { get; set; } = SplitViewPane.Left;

    public string Orientation { get; set; } = SplitViewOrientation.LeftRight;

    public double Ratio { get; set; } = SplitViewModelProfile.DefaultRatio;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public static class SplitViewPane
{
    public const string Left = "left";
    public const string Right = "right";
}

public static class SplitViewOrientation
{
    public const string LeftRight = "leftRight";
    public const string TopBottom = "topBottom";
}

public static class SplitViewModelProfile
{
    public const double DefaultRatio = 0.5;
    public const double MinimumRatio = 0.2;
    public const double MaximumRatio = 0.8;

    public static SplitViewModel? Normalize(SplitViewModel? model)
    {
        if (model is null
            || model.Id == Guid.Empty
            || model.WindowId == Guid.Empty
            || model.LeftTabId == Guid.Empty
            || model.RightTabId == Guid.Empty
            || model.LeftTabId == model.RightTabId)
        {
            return null;
        }

        return new SplitViewModel
        {
            Id = model.Id,
            WindowId = model.WindowId,
            LeftTabId = model.LeftTabId,
            RightTabId = model.RightTabId,
            ActivePane = string.Equals(model.ActivePane, SplitViewPane.Right, StringComparison.OrdinalIgnoreCase)
                ? SplitViewPane.Right
                : SplitViewPane.Left,
            Orientation = NormalizeOrientation(model.Orientation),
            Ratio = NormalizeRatio(model.Ratio),
            CreatedAt = model.CreatedAt == default ? DateTimeOffset.Now : model.CreatedAt,
            UpdatedAt = model.UpdatedAt == default ? DateTimeOffset.Now : model.UpdatedAt
        };
    }

    public static string NormalizeOrientation(string? orientation)
    {
        return string.Equals(orientation, SplitViewOrientation.TopBottom, StringComparison.OrdinalIgnoreCase)
            ? SplitViewOrientation.TopBottom
            : SplitViewOrientation.LeftRight;
    }

    public static double NormalizeRatio(double? ratio)
    {
        return ratio is { } value && double.IsFinite(value)
            ? Math.Clamp(value, MinimumRatio, MaximumRatio)
            : DefaultRatio;
    }
}
