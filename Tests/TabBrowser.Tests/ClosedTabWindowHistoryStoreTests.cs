using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class ClosedTabWindowHistoryStoreTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RestoreReservationKeepsReferenceAndOrderUntilSingleCommit()
    {
        var store = new ClosedTabWindowHistoryStore();
        var first = CreateClosedTab("https://example.test/first", ReferenceTime);
        var second = CreateClosedTab("https://example.test/second", ReferenceTime);
        store.ReplaceClosedTabs([first, second], ReferenceTime);
        Assert.True(store.TryBeginTabRestore(second));
        Assert.False(store.TryBeginTabRestore(second));
        Assert.False(store.RemoveClosedTab(second));
        Assert.False(store.TryBeginTabRestore(CreateClosedTab(second.Url!, ReferenceTime)));
        Assert.Equal([first, second], store.ClosedTabs);
        store.EndTabRestore(second);
        Assert.Equal([first, second], store.ClosedTabs);
        Assert.True(store.TryBeginTabRestore(second));
        Assert.True(store.CommitTabRestore(second));
        Assert.False(store.CommitTabRestore(second));
        store.EndTabRestore(second);
        Assert.Same(first, Assert.Single(store.ClosedTabs));
        Assert.False(store.TryBeginTabRestore(second));
    }

    [Fact]
    public void LinkedWindowIsKeptDuringTabReservationAndUpdatedOnlyOnCommit()
    {
        var store = new ClosedTabWindowHistoryStore();
        var window = CreateClosedWindow(ReferenceTime, CreateSessionTab("https://example.test/tab"));
        var tab = CreateChildClosedTab(window, window.WindowState.Tabs[0], 0);
        store.AddClosedWindowAndTabs(window, [tab], ReferenceTime);
        Assert.True(store.TryBeginTabRestore(tab));
        Assert.True(store.HasRestoringTabInWindow(window.Id));
        Assert.Empty(store.RemoveClosedWindow(window));
        Assert.Same(window, Assert.Single(store.ClosedWindows));
        store.ReplaceClosedTabs([], ReferenceTime);
        Assert.Same(tab, Assert.Single(store.ClosedTabs));
        Assert.True(store.CommitTabRestore(tab));
        Assert.Empty(store.ClosedWindows);
        store.EndTabRestore(tab);
    }

    [Fact]
    public void ReplaceClosedTabsUsesExistingPolicyAndPreservesRetainedReferences()
    {
        var store = new ClosedTabWindowHistoryStore();
        var recent = CreateClosedTab("https://example.com/recent", ReferenceTime.AddDays(-6));
        var expired = CreateClosedTab("https://example.com/expired", ReferenceTime.AddDays(-8));
        var missingClosedAt = CreateClosedTab("https://example.com/missing", closedAt: null);

        store.ReplaceClosedTabs([recent, expired, missingClosedAt], ReferenceTime);

        Assert.Collection(
            store.ClosedTabs,
            tab => Assert.Same(recent, tab),
            tab => Assert.Same(missingClosedAt, tab));
        Assert.Equal(ReferenceTime, missingClosedAt.ClosedAt);
    }

    [Fact]
    public void ReplaceClosedWindowsUsesExistingPolicyAndPreservesOrder()
    {
        var store = new ClosedTabWindowHistoryStore();
        var first = CreateClosedWindow(ReferenceTime, CreateSessionTab("https://example.com/first"));
        var second = CreateClosedWindow(ReferenceTime, CreateSessionTab("https://example.com/second"));
        var expired = CreateClosedWindow(
            ReferenceTime.AddDays(-8),
            CreateSessionTab("https://example.com/expired"));
        var linkedTab = CreateChildClosedTab(first, first.WindowState.Tabs[0], tabIndex: 0);
        var orphanedTab = CreateClosedTab("https://example.com/orphaned", ReferenceTime);
        orphanedTab.ClosedWindowId = expired.Id;
        orphanedTab.SourceTabId = expired.WindowState.Tabs[0].Id;
        orphanedTab.ClosedWindowTabIndex = 0;
        store.ReplaceClosedTabs([linkedTab, orphanedTab], ReferenceTime);

        store.ReplaceClosedWindows([first, second, expired], ReferenceTime);

        Assert.Collection(
            store.ClosedWindows,
            window => Assert.Same(first, window),
            window => Assert.Same(second, window));
        Assert.Equal(first.Id, linkedTab.ClosedWindowId);
        Assert.Null(orphanedTab.ClosedWindowId);
        Assert.Null(orphanedTab.ClosedWindowTabIndex);
    }

    [Fact]
    public void MergeProtectedHistoryKeepsCurrentOrderAndAddsOnlyNonduplicates()
    {
        var store = new ClosedTabWindowHistoryStore();
        var currentTab = CreateClosedTab("https://example.com/current", ReferenceTime);
        currentTab.SourceTabId = Guid.NewGuid();
        var duplicateTab = CreateClosedTab("https://example.com/current", ReferenceTime);
        duplicateTab.SourceTabId = currentTab.SourceTabId;
        var addedTab = CreateClosedTab("https://example.com/added", ReferenceTime.AddMinutes(-1));
        var currentWindow = CreateClosedWindow(
            ReferenceTime,
            CreateSessionTab("https://example.com/current-window"));
        var duplicateWindow = CreateClosedWindow(
            ReferenceTime,
            CreateSessionTab("https://example.com/duplicate-window"));
        duplicateWindow.Id = currentWindow.Id;
        var addedWindow = CreateClosedWindow(
            ReferenceTime.AddMinutes(-1),
            CreateSessionTab("https://example.com/added-window"));
        store.ReplaceClosedTabs([currentTab], ReferenceTime);
        store.ReplaceClosedWindows([currentWindow], ReferenceTime);

        store.MergeProtectedHistory(
            new SessionData
            {
                ClosedTabs = [duplicateTab, addedTab],
                ClosedWindows = [duplicateWindow, addedWindow]
            },
            ReferenceTime);

        Assert.Collection(
            store.ClosedTabs,
            tab => Assert.Same(currentTab, tab),
            tab => Assert.Same(addedTab, tab));
        Assert.Collection(
            store.ClosedWindows,
            window => Assert.Same(currentWindow, window),
            window => Assert.Same(addedWindow, window));
    }

    [Fact]
    public void AddClosedWindowAndTabsPreservesParentChildRelationshipsAndOrder()
    {
        var store = new ClosedTabWindowHistoryStore();
        var firstTabState = CreateSessionTab("https://example.com/first");
        var secondTabState = CreateSessionTab("https://example.com/second");
        var closedWindow = CreateClosedWindow(ReferenceTime, firstTabState, secondTabState);
        var firstChild = CreateChildClosedTab(closedWindow, firstTabState, tabIndex: 0);
        var secondChild = CreateChildClosedTab(closedWindow, secondTabState, tabIndex: 1);

        store.AddClosedWindowAndTabs(closedWindow, [firstChild, secondChild], ReferenceTime);

        Assert.Same(closedWindow, Assert.Single(store.ClosedWindows));
        Assert.Collection(
            store.ClosedTabs,
            tab => Assert.Same(firstChild, tab),
            tab => Assert.Same(secondChild, tab));
        Assert.All(store.ClosedTabs, tab => Assert.Equal(closedWindow.Id, tab.ClosedWindowId));
    }

    [Fact]
    public void UpdateClosedWindowAfterTabRestoreRepairsActiveTabAndIncompleteSplit()
    {
        var store = new ClosedTabWindowHistoryStore();
        var firstTabState = CreateSessionTab("https://example.com/first", splitViewId: "pair");
        var secondTabState = CreateSessionTab("https://example.com/second", splitViewId: "pair");
        var closedWindow = CreateClosedWindow(ReferenceTime, firstTabState, secondTabState);
        closedWindow.WindowState.LastActiveTabId = firstTabState.Id;
        var firstChild = CreateChildClosedTab(closedWindow, firstTabState, tabIndex: 0);
        var secondChild = CreateChildClosedTab(closedWindow, secondTabState, tabIndex: 1);
        store.AddClosedWindowAndTabs(closedWindow, [firstChild, secondChild], ReferenceTime);
        store.RemoveClosedTab(firstChild);

        var changed = store.UpdateClosedWindowAfterTabRestore(firstChild);

        Assert.True(changed);
        var remainingTab = Assert.Single(closedWindow.WindowState.Tabs);
        Assert.Same(secondTabState, remainingTab);
        Assert.Equal(secondTabState.Id, closedWindow.WindowState.LastActiveTabId);
        Assert.Null(remainingTab.SplitViewId);
        Assert.Null(remainingTab.SplitViewOrientation);
        Assert.Null(remainingTab.SplitViewRatio);
    }

    [Fact]
    public void UpdateClosedWindowAfterLastTabRestoreRemovesParentWindow()
    {
        var store = new ClosedTabWindowHistoryStore();
        var tabState = CreateSessionTab("https://example.com/only");
        var closedWindow = CreateClosedWindow(ReferenceTime, tabState);
        var child = CreateChildClosedTab(closedWindow, tabState, tabIndex: 0);
        store.AddClosedWindowAndTabs(closedWindow, [child], ReferenceTime);
        store.RemoveClosedTab(child);

        var changed = store.UpdateClosedWindowAfterTabRestore(child);

        Assert.True(changed);
        Assert.Empty(store.ClosedWindows);
    }

    [Fact]
    public void RemoveClosedWindowReturnsLinkedChildrenAndKeepsIndependentTab()
    {
        var store = new ClosedTabWindowHistoryStore();
        var firstTabState = CreateSessionTab("https://example.com/first");
        var secondTabState = CreateSessionTab("https://example.com/second");
        var closedWindow = CreateClosedWindow(ReferenceTime, firstTabState, secondTabState);
        var firstChild = CreateChildClosedTab(closedWindow, firstTabState, tabIndex: 0);
        var secondChild = CreateChildClosedTab(closedWindow, secondTabState, tabIndex: 1);
        var independent = CreateClosedTab("https://example.com/independent", ReferenceTime);
        store.ReplaceClosedTabs([independent], ReferenceTime);
        store.AddClosedWindowAndTabs(closedWindow, [firstChild, secondChild], ReferenceTime);

        var removedTabs = store.RemoveClosedWindow(closedWindow);

        Assert.Empty(store.ClosedWindows);
        Assert.Same(independent, Assert.Single(store.ClosedTabs));
        Assert.Collection(
            removedTabs,
            tab => Assert.Same(firstChild, tab),
            tab => Assert.Same(secondChild, tab));
    }

    [Fact]
    public void NormalizeReportsExactRemovedReferencesAndSnapshotsReuseRetainedObjects()
    {
        var store = new ClosedTabWindowHistoryStore();
        var retained = CreateClosedTab("https://example.com/retained", ReferenceTime);
        var removed = CreateClosedTab("https://example.com/removed", ReferenceTime);
        var closedWindow = CreateClosedWindow(
            ReferenceTime,
            CreateSessionTab("https://example.com/window"));
        store.ReplaceClosedTabs([retained, removed], ReferenceTime);
        store.ReplaceClosedWindows([closedWindow], ReferenceTime);
        removed.ClosedAt = ReferenceTime.AddDays(-8);

        var removedTabs = store.NormalizeClosedTabs(ReferenceTime);
        var tabSnapshot = store.CreateClosedTabSnapshot(ReferenceTime);
        var windowSnapshot = store.CreateClosedWindowSnapshot(ReferenceTime);

        Assert.Same(removed, Assert.Single(removedTabs));
        Assert.Same(retained, Assert.Single(store.ClosedTabs));
        Assert.Same(retained, Assert.Single(tabSnapshot));
        Assert.NotSame(store.ClosedTabs, tabSnapshot);
        Assert.Same(closedWindow, Assert.Single(windowSnapshot));
        Assert.NotSame(store.ClosedWindows, windowSnapshot);
    }

    private static ClosedTabState CreateClosedTab(string url, DateTimeOffset? closedAt)
    {
        return new ClosedTabState
        {
            Url = url,
            ClosedAt = closedAt
        };
    }

    private static ClosedTabState CreateChildClosedTab(
        ClosedWindowState closedWindow,
        SessionTabState tabState,
        int tabIndex)
    {
        return new ClosedTabState
        {
            Url = tabState.Url,
            ClosedAt = closedWindow.ClosedAt,
            ClosedWindowId = closedWindow.Id,
            SourceWindowId = closedWindow.WindowState.WindowId,
            SourceTabId = tabState.Id,
            ClosedWindowTabIndex = tabIndex
        };
    }

    private static ClosedWindowState CreateClosedWindow(
        DateTimeOffset closedAt,
        params SessionTabState[] tabs)
    {
        return new ClosedWindowState
        {
            ClosedAt = closedAt,
            WindowState = new SessionWindowState
            {
                Tabs = tabs.ToList()
            }
        };
    }

    private static SessionTabState CreateSessionTab(string url, string? splitViewId = null)
    {
        return new SessionTabState
        {
            Url = url,
            SplitViewId = splitViewId,
            SplitViewOrientation = splitViewId is null ? null : "Vertical",
            SplitViewRatio = splitViewId is null ? null : 0.5
        };
    }
}
