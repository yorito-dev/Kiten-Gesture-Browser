namespace TabBrowser.Services;

public static class GestureCommandActionMap
{
    private static readonly IReadOnlyDictionary<string, string> ActionIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Back"] = BrowserCommandProvider.BackActionId,
            ["Forward"] = BrowserCommandProvider.ForwardActionId,
            ["NewTab"] = BrowserCommandProvider.NewTabActionId,
            ["DuplicateTab"] = BrowserCommandProvider.DuplicateTabActionId,
            ["DuplicateTabBackground"] = BrowserCommandProvider.DuplicateTabBackgroundActionId,
            ["CloseTab"] = BrowserCommandProvider.CloseTabActionId,
            ["CloseOtherTabs"] = BrowserCommandProvider.CloseOtherTabsActionId,
            ["CloseTabsToRight"] = BrowserCommandProvider.CloseTabsToRightActionId,
            ["CloseTabsToLeft"] = BrowserCommandProvider.CloseTabsToLeftActionId,
            ["Reload"] = BrowserCommandProvider.ReloadActionId,
            ["StopLoading"] = BrowserCommandProvider.StopLoadingActionId,
            ["NextTab"] = BrowserCommandProvider.NextTabActionId,
            ["PreviousTab"] = BrowserCommandProvider.PreviousTabActionId,
            ["RestoreClosedTab"] = BrowserCommandProvider.RestoreClosedTabActionId,
            ["ScrollTop"] = BrowserCommandProvider.ScrollTopActionId,
            ["ScrollBottom"] = BrowserCommandProvider.ScrollBottomActionId,
            ["ScrollPageUp"] = BrowserCommandProvider.ScrollPageUpActionId,
            ["ScrollPageDown"] = BrowserCommandProvider.ScrollPageDownActionId,
            ["SearchSelection"] = ContextCommandProvider.SearchSelectionActionId,
            ["CopyUrl"] = ClipboardCommandProvider.CopyUrlActionId,
            ["AddBookmark"] = BrowserCommandProvider.AddBookmarkActionId,
            ["ShowBookmarks"] = BrowserCommandProvider.ShowBookmarksActionId,
            ["ShowWorkspace"] = FeatureCommandProvider.OpenWorkspaceActionId,
            ["ToggleWorkspace"] = FeatureCommandProvider.ToggleWorkspaceActionId,
            ["ShowWorkspaceWindow"] = FeatureCommandProvider.OpenWorkspaceWindowActionId
        };

    public static bool TryGetActionId(string? gestureCommand, out string actionId)
    {
        return ActionIds.TryGetValue(gestureCommand ?? string.Empty, out actionId!);
    }
}
