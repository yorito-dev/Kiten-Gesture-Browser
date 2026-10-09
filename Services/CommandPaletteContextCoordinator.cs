namespace TabBrowser.Services;

internal sealed class CommandPaletteContextCoordinator
{
    private int _generation;
    private Guid? _explicitContextTabId;
    private string _selectedText = string.Empty;
    private string _linkUrl = string.Empty;
    private string _linkText = string.Empty;

    public int Begin(
        Guid? explicitContextTabId,
        string? linkUrl,
        string? linkText)
    {
        _selectedText = string.Empty;
        _linkUrl = linkUrl?.Trim() ?? string.Empty;
        _linkText = linkText?.Trim() ?? string.Empty;
        _explicitContextTabId = explicitContextTabId;
        return ++_generation;
    }

    public void Close()
    {
        _generation++;
        _selectedText = string.Empty;
        _linkUrl = string.Empty;
        _linkText = string.Empty;
        _explicitContextTabId = null;
    }

    public bool IsCurrent(int generation)
    {
        return generation == _generation;
    }

    public bool TrySetSelectedText(int generation, string selectedText)
    {
        if (!IsCurrent(generation))
        {
            return false;
        }

        _selectedText = selectedText;
        return true;
    }

    public CommandPaletteContextSnapshot CaptureSnapshot()
    {
        return new CommandPaletteContextSnapshot(
            _generation,
            _explicitContextTabId,
            _selectedText,
            _linkUrl,
            _linkText);
    }
}

internal readonly record struct CommandPaletteContextSnapshot(
    int Generation,
    Guid? ExplicitContextTabId,
    string SelectedText,
    string LinkUrl,
    string LinkText);
