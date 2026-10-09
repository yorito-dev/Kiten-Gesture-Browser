namespace TabBrowser.Services;

internal sealed class AddressBarInteractionCoordinator
{
    private string _originalEditText = string.Empty;
    private string? _pasteNavigationText;
    private double _dragStartX;
    private double _dragStartY;
    private bool _isDragCandidate;
    private int _suggestionSuppressionDepth;

    public string OriginalEditText => _originalEditText;

    public string? PasteNavigationText => _pasteNavigationText;

    public bool IsDragCandidate => _isDragCandidate;

    public bool IsSuggestionSuppressed => _suggestionSuppressionDepth > 0;

    public void BeginEdit(string text)
    {
        _originalEditText = text;
    }

    public bool HasEditChanged(string currentText)
    {
        return !string.Equals(currentText, _originalEditText, StringComparison.Ordinal);
    }

    public void SetPasteNavigationText(string? text)
    {
        _pasteNavigationText = text;
    }

    public void ClearPasteNavigationText()
    {
        _pasteNavigationText = null;
    }

    public void BeginDragCandidate(double x, double y, bool isCandidate)
    {
        _dragStartX = x;
        _dragStartY = y;
        _isDragCandidate = isCandidate;
    }

    public void CancelDragCandidate()
    {
        _isDragCandidate = false;
    }

    public bool HasExceededDragThreshold(
        double currentX,
        double currentY,
        double minimumHorizontalDistance,
        double minimumVerticalDistance)
    {
        return _isDragCandidate
            && (Math.Abs(currentX - _dragStartX) >= minimumHorizontalDistance
                || Math.Abs(currentY - _dragStartY) >= minimumVerticalDistance);
    }

    public IDisposable EnterSuggestionSuppression()
    {
        _suggestionSuppressionDepth++;
        return new SuggestionSuppressionLease(this);
    }

    private void ExitSuggestionSuppression()
    {
        _suggestionSuppressionDepth--;
    }

    private sealed class SuggestionSuppressionLease(AddressBarInteractionCoordinator owner) : IDisposable
    {
        private AddressBarInteractionCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitSuggestionSuppression();
        }
    }
}
