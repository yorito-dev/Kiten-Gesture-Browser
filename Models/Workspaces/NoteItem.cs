namespace TabBrowser.Models.Workspaces;

public static class WorkspaceNoteFontSizeProfile
{
    public const double DefaultFontSize = 14;

    public const double MinimumFontSize = 10;

    public const double MaximumFontSize = 24;

    public const double Step = 2;

    public static double Normalize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), MinimumFontSize, MaximumFontSize)
            : DefaultFontSize;
    }
}

public sealed class NoteItem : WorkspaceItem
{
    private double _fontSize = WorkspaceNoteFontSizeProfile.DefaultFontSize;

    public override WorkspaceItemType Type => WorkspaceItemType.Note;

    public double FontSize
    {
        get => _fontSize;
        set => _fontSize = WorkspaceNoteFontSizeProfile.Normalize(value);
    }

    public string Content
    {
        get => Note;
        set => Note = value;
    }
}
