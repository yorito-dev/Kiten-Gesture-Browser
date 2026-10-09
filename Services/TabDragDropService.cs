namespace TabBrowser.Services;

public sealed class TabDragDropService
{
    public bool CanStartTabDrag(bool isWorkspaceTab)
    {
        return !isWorkspaceTab;
    }

    public bool CanTransferShelfTab(
        bool isWorkspaceTab,
        bool isSameWindow,
        bool hasMatchingPrivacyMode)
    {
        return !isWorkspaceTab && !isSameWindow && hasMatchingPrivacyMode;
    }

    public bool ShouldAutoDetachTab(
        bool isWorkspaceTab,
        bool isOutsideTabSpace,
        bool isOverOtherTabBrowserWindow,
        bool isWorkspaceDropActive,
        DateTimeOffset? candidateSince,
        DateTimeOffset now,
        int delayMilliseconds)
    {
        return !isWorkspaceTab
            && isOutsideTabSpace
            && !isOverOtherTabBrowserWindow
            && !isWorkspaceDropActive
            && candidateSince is { } since
            && now - since >= TimeSpan.FromMilliseconds(Math.Max(0, delayMilliseconds));
    }

    public bool IsPointOutsideBounds(
        double pointX,
        double pointY,
        double width,
        double height,
        double tolerance)
    {
        return pointX < -tolerance
            || pointY < -tolerance
            || pointX > width + tolerance
            || pointY > height + tolerance;
    }

    public bool ShouldShowDetachPreview(
        bool isWorkspaceTab,
        bool isVerticalTabList,
        double pointX,
        double pointY,
        double width,
        double height,
        double detachDistance,
        bool isOverOtherTabBrowserWindow,
        bool isWorkspaceDropActive)
    {
        if (isWorkspaceTab || isOverOtherTabBrowserWindow || isWorkspaceDropActive)
        {
            return false;
        }

        var distance = Math.Max(0, detachDistance);
        return isVerticalTabList
            ? pointX <= -distance || pointX >= width + distance
            : pointY <= -distance || pointY >= height + distance;
    }

    public bool ShouldConfirmReorderFromRootDrop(
        double pointX,
        double pointY,
        double width,
        double height,
        double tabListTolerance,
        double reorderDropTolerance,
        DateTimeOffset? reorderCandidateAt,
        DateTimeOffset now,
        int reorderDropGraceMilliseconds)
    {
        if (!IsPointOutsideBounds(pointX, pointY, width, height, tabListTolerance))
        {
            return true;
        }

        if (reorderCandidateAt is null)
        {
            return false;
        }

        if (now - reorderCandidateAt.Value > TimeSpan.FromMilliseconds(reorderDropGraceMilliseconds))
        {
            return false;
        }

        return !IsPointOutsideBounds(pointX, pointY, width, height, reorderDropTolerance);
    }

    public bool ShouldShiftTabForExternalDropPreview(int tabIndex, int insertionIndex)
    {
        return tabIndex >= 0
            && insertionIndex >= 0
            && tabIndex >= insertionIndex;
    }

    public bool ShouldSuppressWorkspaceReorderPreview(
        bool isWorkspaceHover,
        bool isSameWindowDrag)
    {
        return isWorkspaceHover && isSameWindowDrag;
    }

    public double GetExternalDropPreviewDistance(double itemExtent, int draggedTabCount)
    {
        return Math.Max(0, itemExtent) * Math.Max(1, draggedTabCount);
    }

    public ExternalTabDropTarget? ResolveExternalDrop(string? explicitUrl, string? plainText)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return new ExternalTabDropTarget(
                ExternalTabDropKind.Url,
                explicitUrl.Trim());
        }

        var normalizedText = AddressService.NormalizePasteInput(plainText);
        return normalizedText is null
            ? null
            : new ExternalTabDropTarget(ExternalTabDropKind.Search, normalizedText);
    }
}

public enum ExternalTabDropKind
{
    Url,
    Search
}

public sealed record ExternalTabDropTarget(ExternalTabDropKind Kind, string Value);
