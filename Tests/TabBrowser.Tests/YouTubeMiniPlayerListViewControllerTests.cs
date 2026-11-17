using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

[Collection("YouTubeMiniPlayerListWindow")]
public sealed class YouTubeMiniPlayerListViewControllerTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("dragging")]
    [InlineData("refresh-running")]
    [InlineData("preview")]
    public void ExistingRefreshExpiresResultsEvenWhenRowRefreshIsDeferred(string guard) => RunInSta(async () =>
    {
        long now = 0;
        using var f = new Fixture(() => now);
        f.Host.ActivePlayerTitle = "最後にアクティブになった動画";
        f.Elements.YouTubeMiniPlayerListWindow.ShowOperationStatus("一時停止しました");
        IDisposable? lease = null;
        if (guard == "dragging") f.Drag.BeginDrag();
        if (guard == "refresh-running") f.StateSync.TryEnterListRefresh(out lease);
        if (guard == "preview") f.Host.IsPreviewActive = true;
        now = 5 * Stopwatch.Frequency;
        await f.Refresh();
        Assert.Empty(f.Elements.YouTubeMiniPlayerListWindow.OperationStatus);
        Assert.Equal(f.Host.ActivePlayerTitle, f.Elements.YouTubeMiniPlayerListWindow.InformationText);
        Assert.False(f.Elements.YouTubeMiniPlayerListWindow.IsVisible);
        lease?.Dispose();
        f.Drag.EndDrag();
    });

    [Fact]
    public void ActiveMarkerAndInformationNavigationPreserveCheckedRows() => RunInSta(async () =>
    {
        using var f = new Fixture();
        for (var index = 0; index < 3; index++) f.Add(index);
        f.States[2].Title = "最後に操作した動画";
        f.Host.ActivePlayer = f.States[2];
        await f.Refresh();
        Assert.Equal(f.States[2].Id, Assert.Single(f.Controller.Items, item => item.IsActivePlayer).State.Id);
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[0]);
        Assert.True(f.Controller.ScrollToActivePlayer());
        Assert.Same(f.Controller.Items[0], Assert.Single(f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<object>()));
        f.Elements.YouTubeMiniPlayerListWindow.ShowOperationStatus("操作結果");
        Assert.False(f.Controller.ScrollToActivePlayer());
        f.Host.ActivePlayer = f.States[1];
        f.Host.IsPreviewActive = true;
        await f.Refresh();
        Assert.Equal(f.States[1].Id, Assert.Single(f.Controller.Items, item => item.IsActivePlayer).State.Id);
        f.Host.ActivePlayer = null;
        await f.Refresh();
        Assert.DoesNotContain(f.Controller.Items, item => item.IsActivePlayer);
        f.Elements.YouTubeMiniPlayerListWindow.ShowOperationStatus(string.Empty);
        Assert.False(f.Controller.ScrollToActivePlayer());
    });

    [Fact]
    public void OperationTargetsUseSelectionOrAllAndSelectionOnlyNeverFallsBack() => RunInSta(async () =>
    {
        using var f = new Fixture();
        for (var index = 0; index < 3; index++) f.Add(index);
        await f.Refresh();
        f.Elements.YouTubeMiniPlayerList.SelectionChanged += f.Controller.YouTubeMiniPlayerList_SelectionChanged;
        Assert.Equal(f.Controller.Items, f.Controller.GetOperationTargets());
        Assert.Empty(f.Controller.GetOperationTargets(selectionOnly: true));
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[0]);
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[2]);
        Assert.Equal(new[] { f.Controller.Items[0], f.Controller.Items[2] }, f.Controller.GetOperationTargets());
        var state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Equal(2, state.TotalCount);
        Assert.Equal(2, state.SelectedCount);
        Assert.Contains("選択 2件", state.ScopeText);
        f.Elements.YouTubeMiniPlayerList.UnselectAll();
        state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Contains("全 3件", state.ScopeText);
        Assert.Empty(f.Controller.GetOperationTargets(selectionOnly: true));
    });

    [Fact]
    public void ClosedHistoryRestoresTheChosenIdAfterReleasingThePopupItems() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = true;
        var first = new YouTubeMiniPlayerState { Id = Guid.NewGuid(), Title = "同じタイトル", LastPositionSeconds = 60 };
        var second = new YouTubeMiniPlayerState { Id = Guid.NewGuid(), Title = "同じタイトル", LastPositionSeconds = 120 };
        f.Host.ClosedStates.AddRange([first, second]);
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        var list = f.Elements.YouTubeMiniPlayerClosedHistoryList!;
        Assert.Equal(new[] { second.Id, first.Id }, list.Items.Cast<YouTubeMiniPlayerListItem>().Select(item => item.State.Id));
        f.Host.RestoreClosed = id =>
        {
            Assert.Equal(first.Id, id);
            Assert.False(f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen);
            Assert.Null(list.ItemsSource);
            return Task.CompletedTask;
        };
        await f.Controller.RestoreClosedHistoryItemAsync(first.Id);
        Assert.Equal(new[] { first.Id }, f.Host.RestoredClosedIds);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnavailableOrBusyHistoryDoesNotReadOrLoadItems(bool available, bool busy) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = available;
        f.Host.IsRestoringClosedPlayer = busy;
        f.Host.ClosedStates.Add(new() { Id = Guid.NewGuid(), Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        await f.Controller.RestoreClosedHistoryItemAsync(f.Host.ClosedStates[0].Id);
        Assert.False(f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen);
        Assert.Equal(0, f.Host.ClosedHistoryReads);
        Assert.Equal(0, f.ThumbnailCalls);
        Assert.Empty(f.Host.RestoredClosedIds);
    });

    [Fact]
    public void StaleHistoryChoiceNeverRestoresAnotherVideoAndBusyRestoreIsNotRepeated() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = true;
        var removed = new YouTubeMiniPlayerState { Id = Guid.NewGuid() };
        var remaining = new YouTubeMiniPlayerState { Id = Guid.NewGuid() };
        f.Host.ClosedStates.AddRange([removed, remaining]);
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        f.Host.ClosedStates.Remove(removed);
        await f.Controller.RestoreClosedHistoryItemAsync(removed.Id);
        Assert.Empty(f.Host.RestoredClosedIds);
        var completion = new TaskCompletionSource();
        f.Host.RestoreClosed = _ => { f.Host.IsRestoringClosedPlayer = true; return completion.Task; };
        f.Controller.OpenClosedHistory();
        var restore = f.Controller.RestoreClosedHistoryItemAsync(remaining.Id);
        await f.Controller.RestoreClosedHistoryItemAsync(remaining.Id);
        Assert.Equal(new[] { remaining.Id }, f.Host.RestoredClosedIds);
        Assert.False(restore.IsCompleted);
        completion.SetResult();
        await restore;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryCloseOrStopCancelsThumbnailsAndIgnoresLateResults(bool stop) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = true;
        f.Host.ClosedStates.Add(new() { Id = Guid.NewGuid(), Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        var completion = new TaskCompletionSource<ImageSource?>();
        f.Load = (_, _) => completion.Task;
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        var item = Assert.Single(f.Elements.YouTubeMiniPlayerClosedHistoryList!.Items.Cast<YouTubeMiniPlayerListItem>());
        var token = f.LastThumbnailToken;
        Assert.False(token.IsCancellationRequested);
        if (stop) f.Controller.StopRefreshTracking();
        else f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen = false;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(token.IsCancellationRequested);
        Assert.Null(f.Elements.YouTubeMiniPlayerClosedHistoryList.ItemsSource);
        completion.SetResult(new DrawingImage());
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Null(item.Thumbnail);
        Assert.False(f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen);
    });

    [Fact]
    public void HistoryRefreshPreservesUnchangedItemsAndTracksOtherWindowsChanges() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = true;
        f.Host.ClosedStates.Add(new() { Id = Guid.NewGuid(), Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        var source = f.Elements.YouTubeMiniPlayerClosedHistoryList!.ItemsSource;
        f.Controller.UpdateYouTubeMiniPlayerRestoreClosedButtonState();
        Assert.Same(source, f.Elements.YouTubeMiniPlayerClosedHistoryList.ItemsSource);
        Assert.Equal(1, f.ThumbnailCalls);
        var latest = new YouTubeMiniPlayerState { Id = Guid.NewGuid(), Title = "別のウィンドウで閉じた動画" };
        f.Host.ClosedStates.Add(latest);
        f.Controller.UpdateYouTubeMiniPlayerRestoreClosedButtonState();
        Assert.Equal(latest.Id, f.Elements.YouTubeMiniPlayerClosedHistoryList.Items.Cast<YouTubeMiniPlayerListItem>().First().State.Id);
        f.Host.HasRestorablePlayer = false;
        f.Controller.UpdateYouTubeMiniPlayerRestoreClosedButtonState();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen);
        Assert.Null(f.Elements.YouTubeMiniPlayerClosedHistoryList.ItemsSource);
    });

    [Fact]
    public void LateHistoryClosedNotificationDoesNotClearTheReopenedMenu() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = true;
        f.Host.ClosedStates.Add(new() { Id = Guid.NewGuid(), Title = "閉じた動画" });
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.OpenClosedHistory();
        f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen = false;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        f.Controller.OpenClosedHistory();
        var source = f.Elements.YouTubeMiniPlayerClosedHistoryList!.ItemsSource;
        f.Controller.ClosedHistoryPopupClosed();
        Assert.NotNull(source);
        Assert.Same(source, f.Elements.YouTubeMiniPlayerClosedHistoryList.ItemsSource);
        Assert.True(f.Elements.YouTubeMiniPlayerClosedHistoryPopup.IsOpen);
    });

    [Fact]
    public void GlobalLiveActionStaysAvailableWhenLastSnapshotSaysLiveEdge() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var live = f.Add(0);
        live.IsLiveBroadcast = true;
        live.IsAtLiveEdge = true;
        live.IsLiveEdgeStateKnown = true;
        f.Host.LoadedIds.Add(live.Id);
        await f.Refresh();

        var state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Equal(1, state.LiveCount);
        Assert.True(f.Elements.YouTubeMiniPlayerLiveEdgeAllButton.IsEnabled);
    });

    [Fact]
    public void GlobalMenuCountsAndStatesFollowSelectionAndCurrentRuntime() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var live = f.Add(0);
        live.IsLiveBroadcast = true;
        var regular = f.Add(1);
        var unloaded = f.Add(2);
        unloaded.IsLiveBroadcast = true;
        f.Host.LoadedIds.UnionWith([live.Id, regular.Id]);
        await f.Refresh();
        f.Elements.YouTubeMiniPlayerList.SelectedItem = f.Controller.Items.Single(item => item.State == regular);
        await f.Refresh();
        var state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Equal((1, 1, 0), (state.TotalCount, state.LoadedCount, state.LiveCount));
        Assert.False(f.Elements.YouTubeMiniPlayerLiveEdgeAllButton.IsEnabled);
        foreach (var item in f.States) { item.IsMuted = true; item.IsMinimized = true; }
        live.IsAtLiveEdge = true;
        await f.Refresh();
        state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Equal("選択をミュート解除", state.MuteActionText);
        Assert.Equal("選択を最小化解除", state.MinimizeActionText);
        Assert.False(f.Elements.YouTubeMiniPlayerLiveEdgeAllButton.IsEnabled);
        f.Host.IsLiveSeekInProgress = true;
        await f.Refresh();
        state = Assert.IsType<YouTubeMiniPlayerGlobalActionsState>(f.Elements.YouTubeMiniPlayerGlobalActionsRoot.DataContext);
        Assert.Contains("移動中", state.LiveActionText);
        Assert.False(f.Elements.YouTubeMiniPlayerPlayPauseAllButton.IsEnabled);
        Assert.False(f.Elements.YouTubeMiniPlayerPauseAllButton.IsEnabled);
        Assert.False(f.Elements.YouTubeMiniPlayerMuteAllToggleButton.IsEnabled);
        Assert.False(f.Elements.YouTubeMiniPlayerMinimizeRestoreAllButton.IsEnabled);
        Assert.False(f.Elements.YouTubeMiniPlayerCloseAllButton.IsEnabled);
    });

    [Fact]
    public void RefreshPreservesIdentitySelectionAndUsesMove() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var a = f.Add(1);
        var b = f.Add(2);
        await f.Refresh();
        var first = f.Controller.Items[0];
        var second = f.Controller.Items[1];
        var source = f.Elements.YouTubeMiniPlayerList.ItemsSource;
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(first);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)source).CollectionChanged += (_, e) => changes.Add(e.Action);
        b.ListOrder = 0;
        await f.Refresh();
        Assert.Same(source, f.Elements.YouTubeMiniPlayerList.ItemsSource);
        Assert.Same(second, f.Controller.Items[0]);
        Assert.Same(first, f.Controller.Items[1]);
        Assert.True(f.Elements.YouTubeMiniPlayerList.SelectedItems.Contains(first));
        Assert.Contains(NotifyCollectionChangedAction.Move, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(1, f.Host.ShelfRefreshes);
        Assert.Same(a, first.State);
    });

    [Fact]
    public void AddingAndRemovingPlayersFitsTheWindowWithoutReplacingSelectionOrMovingIt() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0); f.Add(1); f.Add(2);
        var list = f.Elements.YouTubeMiniPlayerList;
        var item = new FrameworkElementFactory(typeof(Border));
        item.SetValue(FrameworkElement.HeightProperty, 100d);
        list.ItemTemplate = new DataTemplate { VisualTree = item };
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
        VirtualizingPanel.SetIsVirtualizing(list, false);
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 80 });
        content.Children.Add(list);
        var window = f.Elements.YouTubeMiniPlayerListWindow;
        window.Left = 100;
        window.Top = 100;
        window.AttachContent(content, new ResourceDictionary());
        await f.Refresh();
        window.ShowOrActivate();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        window.FitInitialHeight(900);
        var previousHeight = window.Height;
        var selected = f.Controller.Items[0];
        list.SelectedItems.Add(selected);
        var source = list.ItemsSource;
        var position = new Point(window.Left, window.Top);
        f.States.RemoveAt(2);
        await f.Refresh();
        Assert.True(window.Height < previousHeight - 70,
            $"縮小前={previousHeight}, 縮小後={window.Height}, 内容高={content.DesiredSize.Height}, 行数={f.Controller.Items.Count}");
        var smallerHeight = window.Height;
        f.Add(3);
        await f.Refresh();
        Assert.True(window.Height > smallerHeight + 70);
        Assert.Same(source, list.ItemsSource);
        Assert.Same(selected, f.Controller.Items[0]);
        Assert.Contains(selected, list.SelectedItems.Cast<YouTubeMiniPlayerListItem>());
        Assert.Equal(position, new Point(window.Left, window.Top));
        f.States.Clear();
        await f.Refresh();
        Assert.Equal(window.MinHeight, window.Height);
        Assert.True(window.IsOpen);
        f.Add(0);
        await f.Refresh();
        Assert.True(window.Height > window.MinHeight);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacesItemWhenStateOrOwnerIdentityChanges(bool replaceState) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var state = f.Add(0);
        await f.Refresh();
        var old = Assert.Single(f.Controller.Items);
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(old);
        var owner = replaceState ? f.Owner : CreateOwnerToken();
        var replacement = replaceState ? new YouTubeMiniPlayerState { Id = state.Id, Url = state.Url } : state;
        f.Host.Sources = [new(owner, [replacement])];
        await f.Refresh();
        var current = Assert.Single(f.Controller.Items);
        Assert.NotSame(old, current);
        Assert.Same(owner, current.Owner);
        Assert.Same(replacement, current.State);
        Assert.True(f.Elements.YouTubeMiniPlayerList.SelectedItems.Contains(current));
    });

    [Fact]
    public void EmptyListAndToolbarReflectActualLoadedItems() => RunInSta(async () =>
    {
        using var f = new Fixture();
        await f.Refresh();
        Assert.Equal(Visibility.Visible, f.Elements.YouTubeMiniPlayerListEmptyText.Visibility);
        Assert.Equal(Visibility.Collapsed, f.Elements.YouTubeMiniPlayerList.Visibility);
        Assert.False(f.Elements.YouTubeMiniPlayerGlobalActionsButton.IsEnabled);
        var a = f.Add(0);
        a.IsPaused = false;
        a.IsMuted = false;
        var b = f.Add(1);
        b.IsPaused = false;
        f.Host.LoadedIds.Add(a.Id);
        f.Host.HasInitialArrangement = true;
        await f.Refresh();
        Assert.Contains("2件｜再生1｜音声1", f.Elements.YouTubeMiniPlayerListSummaryText.Text);
        Assert.True(f.Elements.YouTubeMiniPlayerPlayPauseAllButton.IsEnabled);
        Assert.Equal(Visibility.Visible, f.Elements.YouTubeMiniPlayerRestoreArrangementButton.Visibility);
        Assert.Equal(Visibility.Collapsed, f.Elements.YouTubeMiniPlayerListEmptyText.Visibility);
        f.States.Clear();
        await f.Refresh();
        Assert.Empty(f.Controller.Items);
        Assert.False(f.Elements.YouTubeMiniPlayerMuteAllToggleButton.IsEnabled);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RestoreClosedButtonPreservesAvailabilityAndBusyGuard(bool available, bool busy) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.HasRestorablePlayer = available;
        f.Host.IsRestoringClosedPlayer = busy;
        f.Controller.UpdateYouTubeMiniPlayerRestoreClosedButtonState();
        Assert.Equal(available ? Visibility.Visible : Visibility.Collapsed, f.Elements.YouTubeMiniPlayerRestoreClosedButton.Visibility);
        Assert.Equal(available && !busy, f.Elements.YouTubeMiniPlayerRestoreClosedButton.IsEnabled);
        await Task.CompletedTask;
    });

    [Fact]
    public void ShelfSignatureIgnoresLocalVideoAndOrderButDetectsYouTubeSetChanges() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var local = f.Add(0);
        local.SourceKind = MiniPlayerSourceKind.LocalFile;
        await f.Refresh();
        Assert.Equal(0, f.Host.ShelfRefreshes);
        var youtube = f.Add(1);
        await f.Refresh();
        Assert.Equal(1, f.Host.ShelfRefreshes);
        youtube.ListOrder = -1;
        await f.Refresh();
        Assert.Equal(1, f.Host.ShelfRefreshes);
        f.States.Remove(youtube);
        await f.Refresh();
        Assert.Equal(2, f.Host.ShelfRefreshes);
    });

    [Theory]
    [InlineData("closing")]
    [InlineData("stopped")]
    [InlineData("dragging")]
    [InlineData("refresh-running")]
    [InlineData("preview")]
    public void RefreshGuardsAvoidCapturingOrRebuilding(string guard) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        IDisposable? lease = null;
        switch (guard)
        {
            case "closing": f.Host.IsClosingWindow = true; break;
            case "stopped": f.Controller.StopRefreshTracking(); break;
            case "dragging": f.Drag.BeginDrag(); break;
            case "refresh-running": f.StateSync.TryEnterListRefresh(out lease); break;
            case "preview": f.Host.IsPreviewActive = true; break;
        }
        await f.Controller.RefreshYouTubeMiniPlayerListAsync(true);
        Assert.Empty(f.Controller.Items);
        Assert.Equal(0, f.Host.OwnerReads);
        Assert.Equal(guard == "preview" ? 1 : 0, f.Host.IndicatorUpdates);
        lease?.Dispose();
        f.Drag.EndDrag();
    });

    [Fact]
    public void CaptureIsSingleFlightAndReloadsOwnerSnapshotAfterAwait() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        var hold = new TaskCompletionSource();
        f.Host.Capture = _ => hold.Task;
        var first = f.Controller.RefreshYouTubeMiniPlayerListAsync(true);
        Assert.True(f.StateSync.IsListRefreshRunning);
        await f.Controller.RefreshYouTubeMiniPlayerListAsync(true);
        Assert.Equal(1, f.Host.CaptureCount);
        f.Add(1);
        hold.SetResult();
        await first;
        Assert.Equal(2, f.Host.OwnerReads);
        Assert.Equal(2, f.Controller.Items.Count);
        Assert.False(f.StateSync.IsListRefreshRunning);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureExitOrFailureAlwaysReleasesRefreshLease(bool fail) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        var hold = new TaskCompletionSource();
        f.Host.Capture = _ => hold.Task;
        var task = f.Controller.RefreshYouTubeMiniPlayerListAsync(true);
        if (fail)
        {
            hold.SetException(new InvalidOperationException("capture"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        else
        {
            f.Controller.StopRefreshTracking();
            hold.SetResult();
            await task;
        }
        Assert.False(f.StateSync.IsListRefreshRunning);
        Assert.Empty(f.Controller.Items);
    });

    [Fact]
    public void SelectionChangeDuringRefreshDoesNotCancelArrangement() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Add(0);
        await f.Refresh();
        f.Host.HasArrangementPreview = true;
        f.Elements.YouTubeMiniPlayerList.SelectionChanged += f.Controller.YouTubeMiniPlayerList_SelectionChanged;
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[0]);
        Assert.Contains("cancel-arrangement:True", f.Host.Events);
        f.Host.Events.Clear();
        f.States.Remove(item);
        await f.Refresh();
        Assert.DoesNotContain("cancel-arrangement:True", f.Host.Events);
    });

    [Fact]
    public void ReorderUpdatesCollectionAndSharedStateBeforeSaveAndNotification() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var a = f.Add(0);
        var b = f.Add(1);
        var c = f.Add(2);
        await f.Refresh();
        f.Host.Events.Clear();
        Assert.True(f.Controller.MoveYouTubeMiniPlayerListItem(a.Id, c.Id, true));
        Assert.Equal(new[] { b.Id, c.Id, a.Id }, f.Controller.Items.Select(x => x.State.Id));
        Assert.Equal(new int?[] { 0, 1, 2 }, f.Controller.Items.Select(x => x.State.ListOrder));
        Assert.Equal(new[] { "save", "other-lists", "status" }, f.Host.Events);
        f.Host.Events.Clear();
        Assert.False(f.Controller.MoveYouTubeMiniPlayerListItem(a.Id, a.Id, true));
        Assert.False(f.Controller.MoveYouTubeMiniPlayerListItem(Guid.NewGuid(), null, true));
        Assert.Empty(f.Host.Events);
    });

    [Fact]
    public void DropCueReplacementAndClearRemoveOldVisualFlags() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0); f.Add(1);
        await f.Refresh();
        var a = f.Controller.Items[0];
        var b = f.Controller.Items[1];
        f.Controller.SetYouTubeMiniPlayerListDropCue(a, false);
        Assert.True(a.IsDropBefore);
        f.Controller.SetYouTubeMiniPlayerListDropCue(b, true);
        Assert.False(a.IsDropBefore);
        Assert.True(b.IsDropAfter);
        f.Controller.ClearYouTubeMiniPlayerListDropCue();
        Assert.False(b.IsDropAfter);
    });

    [Fact]
    public void DragExceptionStillClearsCandidateActiveStateAndCue() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var state = f.Add(0);
        await f.Refresh();
        f.LayoutList();
        f.Drag.BeginCandidate(state.Id, false, 0, 0);
        f.Controller.SetYouTubeMiniPlayerListDropCue(f.Controller.Items[0], true);
        f.Host.OnDrag = (_, data, effects) =>
        {
            Assert.True(f.Drag.IsDragActive);
            Assert.Equal(state.Id.ToString("D"), data.GetData("TabBrowser.YouTubeMiniPlayerListItem"));
            Assert.Equal(DragDropEffects.Move, effects);
            throw new InvalidOperationException("drag");
        };
        var args = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent };
        var error = Assert.Throws<TargetInvocationException>(() => f.Invoke("StartYouTubeMiniPlayerListDrag", state.Id, args));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.True(args.Handled);
        Assert.False(f.Drag.IsDragActive);
        Assert.Null(f.Drag.CaptureCandidate().SourceId);
        Assert.False(f.Controller.Items[0].IsDropAfter);
    });

    [Fact]
    public void RowClicksAddAndRemoveNonAdjacentItemsOnRelease() => RunInSta(async () =>
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++) f.Add(i);
        await f.Refresh();
        f.LayoutList();
        var expectedSelections = new[] { new[] { 0 }, new[] { 0, 2 }, new[] { 2 } };
        var step = 0;
        foreach (var index in new[] { 0, 2, 0 })
        {
            var container = (ListBoxItem)f.Elements.YouTubeMiniPlayerList.ItemContainerGenerator.ContainerFromIndex(index);
            var before = f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<object>().ToArray();
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = container };
            f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(container, down);
            Assert.True(down.Handled);
            Assert.Equal(before, f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<object>());
            f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(container,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = container });
            Assert.Equal(expectedSelections[step++].Select(value => f.Controller.Items[value]),
                f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<YouTubeMiniPlayerListItem>());
        }
        Assert.Equal(f.Controller.Items[2], Assert.Single(f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<object>()));
    });

    [Fact]
    public void SelectAllIncludesEveryRowAndNewRowsAreNotAutomaticallySelected() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0).IsMinimized = true;
        f.Add(1);
        f.Add(2);
        await f.Refresh();
        f.Elements.YouTubeMiniPlayerList.SelectionChanged += f.Controller.YouTubeMiniPlayerList_SelectionChanged;
        f.Host.Events.Clear();
        f.Controller.YouTubeMiniPlayerSelectAllButton_Click(new Button(), new RoutedEventArgs());
        Assert.Equal(3, f.Elements.YouTubeMiniPlayerList.SelectedItems.Count);
        f.Add(3);
        await f.Refresh();
        Assert.Equal(3, f.Elements.YouTubeMiniPlayerList.SelectedItems.Count);
        Assert.DoesNotContain(f.Controller.Items[3], f.Elements.YouTubeMiniPlayerList.SelectedItems.Cast<object>());
        f.Controller.YouTubeMiniPlayerClearSelectionButton_Click(new Button(), new RoutedEventArgs());
        Assert.Empty(f.Elements.YouTubeMiniPlayerList.SelectedItems);
        Assert.DoesNotContain("save", f.Host.Events);
        Assert.DoesNotContain("restore-front", f.Host.Events);
    });

    [Theory]
    [InlineData("outside")]
    [InlineData("lost")]
    [InlineData("hide")]
    [InlineData("stop")]
    public void InterruptedRowPressDoesNotToggleOrKeepMouseCapture(string action) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        await f.Refresh();
        var window = f.Elements.YouTubeMiniPlayerListWindow;
        window.AttachContent(f.Elements.YouTubeMiniPlayerList, new ResourceDictionary());
        window.ShowOrActivate();
        window.UpdateLayout();
        var container = (ListBoxItem)f.Elements.YouTubeMiniPlayerList.ItemContainerGenerator.ContainerFromIndex(0);
        container.LostMouseCapture += f.Controller.YouTubeMiniPlayerListItem_LostMouseCapture;
        f.Host.CursorPoint = container.PointToScreen(new Point(5, 5));
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(container,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = container });
        Assert.Same(container, Mouse.Captured);
        switch (action)
        {
            case "lost": Mouse.Capture(null); break;
            case "hide": window.Hide(); break;
            case "stop": f.Controller.StopRefreshTracking(); break;
            default: f.Host.CursorPoint = container.PointToScreen(new Point(-10, -10)); break;
        }
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(container,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = container });
        Assert.Empty(f.Elements.YouTubeMiniPlayerList.SelectedItems);
        Assert.NotSame(container, Mouse.Captured);
        Assert.Null(f.Drag.CaptureCandidate().SourceId);
    });

    [Fact]
    public void RowDragAndDoubleClickKeepOtherSelections() => RunInSta(async () =>
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++) f.Add(i);
        await f.Refresh();
        f.LayoutList();
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[0]);
        f.Elements.YouTubeMiniPlayerList.SelectedItems.Add(f.Controller.Items[2]);
        var container = (ListBoxItem)f.Elements.YouTubeMiniPlayerList.ItemContainerGenerator.ContainerFromIndex(0);
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(container,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = container });
        f.Host.OnDrag = (_, _, _) => Assert.Equal(2, f.Elements.YouTubeMiniPlayerList.SelectedItems.Count);
        f.Invoke("StartYouTubeMiniPlayerListDrag", f.Controller.Items[0].State.Id,
            new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(container,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = container });
        Assert.Equal(2, f.Elements.YouTubeMiniPlayerList.SelectedItems.Count);
        container.IsSelected = false;
        f.Controller.YouTubeMiniPlayerListItem_MouseDoubleClick(container,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = container });
        Assert.True(container.IsSelected);
        Assert.Equal(2, f.Elements.YouTubeMiniPlayerList.SelectedItems.Count);
        Assert.Single(f.Host.Events, value => value == "restore-front");
    });

    [Fact]
    public void SelectedItemClickDeselectsButModifierOrButtonDoesNot() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var state = f.Add(0);
        await f.Refresh();
        var item = new ListBoxItem { DataContext = f.Controller.Items[0], IsSelected = true };
        f.Drag.BeginCandidate(state.Id, true, 0, 0);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = item };
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(item, click);
        Assert.False(item.IsSelected);
        Assert.True(click.Handled);
        item.IsSelected = true;
        f.Host.Modifiers = ModifierKeys.Control;
        f.Drag.BeginCandidate(state.Id, true, 0, 0);
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(item, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        Assert.True(item.IsSelected);
        f.Host.Modifiers = ModifierKeys.None;
        f.Drag.BeginCandidate(state.Id, true, 0, 0);
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(item,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = new Button() });
        Assert.True(item.IsSelected);
        var seekContent = new Border();
        var seekButton = new Button { Content = seekContent };
        seekButton.Measure(new Size(200, 16));
        seekButton.Arrange(new Rect(0, 0, 200, 16));
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(item,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = seekContent });
        Assert.Null(f.Drag.CaptureCandidate().SourceId);
        f.Drag.BeginCandidate(state.Id, true, 0, 0);
        f.Controller.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(item,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = seekContent });
        Assert.True(item.IsSelected);
    });

    [Fact]
    public void HoverLeaveClearsHighlightAndRequestsExistingPreviewStop() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var state = f.Add(0);
        await f.Refresh();
        var item = new ListBoxItem { DataContext = f.Controller.Items[0] };
        f.Controller.YouTubeMiniPlayerListItem_MouseEnter(item, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        Assert.Equal(state.Id, f.Controller.Interaction.HoveredItemId);
        Assert.Contains("schedule-hover:" + state.Id, f.Host.Events);
        f.Host.PreviewTargetId = state.Id;
        f.Host.Events.Clear();
        f.Controller.YouTubeMiniPlayerListItem_MouseLeave(item, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        Assert.Null(f.Controller.Interaction.HoveredItemId);
        Assert.Equal(new[] { "cancel-hover", "stop-hover", "highlights" }, f.Host.Events);
    });

    [Fact]
    public void ThumbnailBatchRetriesOnlyOnExplicitNewBatch() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        await f.Refresh();
        f.Controller.BeginYouTubeMiniPlayerThumbnailLoads();
        Assert.Equal(1, f.ThumbnailCalls);
        Assert.False(f.Controller.Items[0].HasThumbnail);
        await f.Refresh();
        Assert.Equal(1, f.ThumbnailCalls);
        f.Controller.BeginYouTubeMiniPlayerThumbnailLoads();
        Assert.Equal(2, f.ThumbnailCalls);
    });

    [Fact]
    public void LateThumbnailFromCancelledBatchCannotOverwriteNewImage() => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Add(0);
        await f.Refresh();
        var old = new TaskCompletionSource<ImageSource?>();
        var current = new TaskCompletionSource<ImageSource?>();
        f.Load = (_, _) => f.ThumbnailCalls == 1 ? old.Task : current.Task;
        f.Controller.BeginYouTubeMiniPlayerThumbnailLoads();
        var oldToken = f.LastThumbnailToken;
        f.Controller.BeginYouTubeMiniPlayerThumbnailLoads();
        Assert.True(oldToken.IsCancellationRequested);
        var image = new DrawingImage();
        current.SetResult(image);
        old.SetResult(new DrawingImage());
        Assert.Same(image, f.Controller.Items[0].Thumbnail);
    });

    [Fact]
    public void ThumbnailConcurrencyRemainsThreeAndClosingInvalidatesEveryItem() => RunInSta(async () =>
    {
        using var f = new Fixture();
        for (var i = 0; i < 5; i++) f.Add(i);
        await f.Refresh();
        var completions = new List<TaskCompletionSource<ImageSource?>>();
        f.Load = (_, _) =>
        {
            var completion = new TaskCompletionSource<ImageSource?>();
            completions.Add(completion);
            return completion.Task;
        };
        f.Controller.BeginYouTubeMiniPlayerThumbnailLoads();
        Assert.Equal(3, f.ThumbnailCalls);
        f.Controller.CancelYouTubeMiniPlayerThumbnailLoads();
        Assert.True(f.LastThumbnailToken.IsCancellationRequested);
        foreach (var completion in completions) completion.SetResult(new DrawingImage());
        Assert.All(f.Controller.Items, item => Assert.False(item.HasThumbnail));
    });

    [Fact]
    public void RefreshTimerCannotRestartAfterTermination() => RunInSta(async () =>
    {
        using var f = new Fixture();
        Assert.Equal(TimeSpan.FromSeconds(1), f.Timer.Interval);
        f.Controller.StartRefreshTimer();
        Assert.True(f.Timer.IsEnabled);
        f.Controller.StopRefreshTracking();
        f.Controller.StartRefreshTimer();
        Assert.False(f.Timer.IsEnabled);
        await f.Refresh();
        Assert.Equal(0, f.Host.OwnerReads);
    });

    internal static MainWindow CreateOwnerToken()
        => (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));

    internal static void RunInSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "一覧のSTAテストが期限内に完了しませんでした。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly MainWindow Owner = CreateOwnerToken();
        internal readonly List<YouTubeMiniPlayerState> States = [];
        internal readonly Host Host = new();
        internal readonly YouTubeMiniPlayerStateSyncCoordinator StateSync = new();
        internal readonly OwnedDispatcherTimer Timer = new(DispatcherTimerOwner.FloatingMediaHost, DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
        internal readonly YouTubeMiniPlayerListElements Elements;
        internal readonly YouTubeMiniPlayerListViewController Controller;
        internal Func<YouTubeMiniPlayerState, CancellationToken, Task<ImageSource?>> Load = (_, _) => Task.FromResult<ImageSource?>(null);
        internal int ThumbnailCalls;
        internal CancellationToken LastThumbnailToken;
        internal YouTubeMiniPlayerListDragCoordinator Drag =>
            (YouTubeMiniPlayerListDragCoordinator)typeof(YouTubeMiniPlayerListViewController).GetField("_youTubeMiniPlayerListDragCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Controller)!;

        internal Fixture(Func<long>? getTimestamp = null)
        {
            var window = getTimestamp is null ? new YouTubeMiniPlayerListWindow() : new YouTubeMiniPlayerListWindow(getTimestamp);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Opacity = 0;
            Elements = new(window, new ListBox { SelectionMode = SelectionMode.Extended }, new TextBlock(), new TextBlock(),
                new Button(), new Button(), new Button(), new Button(), new Button(), new Button(), new Button(),
                new Button(), new Button(), new Button(), new Border(), new Popup { Child = new Border() }, new ItemsControl());
            Host.Sources = [new(Owner, States)];
            Controller = new YouTubeMiniPlayerListViewController(Elements, StateSync, Timer, Host,
                (state, token) => { ThumbnailCalls++; LastThumbnailToken = token; return Load(state, token); });
            Controller.InitializeItems();
            Controller.InitializeRefreshTimer();
            Elements.YouTubeMiniPlayerClosedHistoryPopup!.Closed += (_, _) => Controller.ClosedHistoryPopupClosed();
        }

        internal YouTubeMiniPlayerState Add(int order)
        {
            var state = new YouTubeMiniPlayerState { Id = Guid.NewGuid(), ListOrder = order, Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" };
            States.Add(state);
            return state;
        }
        internal Task Refresh() => Controller.RefreshYouTubeMiniPlayerListAsync(false);
        internal void Invoke(string name, params object?[] args)
            => typeof(YouTubeMiniPlayerListViewController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Controller, args);
        internal void LayoutList()
        {
            var list = Elements.YouTubeMiniPlayerList;
            list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) };
            list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
            list.Width = 500;
            list.Height = 300;
            VirtualizingPanel.SetIsVirtualizing(list, false);
            list.Measure(new Size(500, 300));
            list.Arrange(new Rect(0, 0, 500, 300));
            list.UpdateLayout();
        }
        public void Dispose()
        {
            Controller.StopRefreshTracking();
            Controller.CancelYouTubeMiniPlayerThumbnailLoads();
            Elements.YouTubeMiniPlayerListWindow.Dispose();
        }
    }

    internal sealed class Host : IYouTubeMiniPlayerListHost, IYouTubeMiniPlayerListPopupHost
    {
        internal IReadOnlyList<YouTubeMiniPlayerListOwnerSource> Sources = [];
        internal readonly List<string> Events = [];
        internal readonly HashSet<Guid> LoadedIds = [];
        internal readonly List<YouTubeMiniPlayerState> ClosedStates = [];
        internal readonly List<Guid> RestoredClosedIds = [];
        internal int ClosedHistoryReads;
        internal Func<Guid, Task> RestoreClosed = _ => Task.CompletedTask;
        internal Func<MainWindow, Task> Capture = _ => Task.CompletedTask;
        internal Action<DependencyObject, IDataObject, DragDropEffects>? OnDrag;
        internal int OwnerReads, CaptureCount, ShelfRefreshes, IndicatorUpdates;
        internal YouTubeMiniPlayerState? ActivePlayer;
        internal string ActivePlayerTitle
        {
            get => ActivePlayer?.Title ?? string.Empty;
            set => ActivePlayer = new() { Title = value };
        }
        public YouTubeMiniPlayerState? GetActivePlayer() => ActivePlayer;
        public bool IsClosingWindow { get; set; }
        public bool IsPreviewActive { get; set; }
        public Guid? PreviewTargetId { get; set; }
        public bool HasArrangementPreview { get; set; }
        public bool HasInitialArrangement { get; set; }
        public bool HasRestorablePlayer { get; set; }
        public bool IsRestoringClosedPlayer { get; set; }
        public bool IsLiveSeekInProgress { get; set; }
        public ModifierKeys Modifiers { get; set; }
        public bool IsArrangementEditMode { get; set; }
        internal bool OverEditableControl;
        internal string? TriggerTag;
        internal Rect WorkArea = new(0, 0, 1920, 1080);
        internal Func<Task> StopPreview = () => Task.CompletedTask;
        internal Point? CursorPoint;
        public IReadOnlyList<YouTubeMiniPlayerListOwnerSource> GetOwnerSources() { OwnerReads++; return Sources; }
        public Task CaptureStatesAsync(MainWindow owner) { CaptureCount++; return Capture(owner); }
        public YouTubeMiniPlayerListRuntimeSnapshot CaptureRuntimeSnapshot(YouTubeMiniPlayerListItem item)
            => new(null, false, false, IsItemLoaded(item), null, false, false);
        public bool IsItemLoaded(YouTubeMiniPlayerListItem item) => LoadedIds.Contains(item.State.Id);
        public bool ShouldPlayAllLoadedPlayers() => false;
        public void RefreshLoadedWorkspaceOpenTabs() => ShelfRefreshes++;
        public void UpdateAudioFocusBar() => Events.Add("focus-bar");
        public void UpdateWindowHighlights() => Events.Add("highlights");
        public void UpdateAudioIndicators() { IndicatorUpdates++; Events.Add("indicators"); }
        public void CancelArrangementPreview(bool showStatus) => Events.Add("cancel-arrangement:" + showStatus);
        public void QueueSave() => Events.Add("save");
        public void RefreshOtherOpenLists() => Events.Add("other-lists");
        public void SetStatus(string message) => Events.Add("status");
        public void RestoreAndBringToFront(YouTubeMiniPlayerListItem item) => Events.Add("restore-front");
        public void CancelPendingHoverPreview() => Events.Add("cancel-hover");
        public void ScheduleHoverPreview(Guid id) => Events.Add("schedule-hover:" + id);
        public Task StopHoverPreviewAsync() { Events.Add("stop-hover"); return StopPreview(); }
        public bool TryGetCursorDevicePoint(out Point point) { point = CursorPoint ?? default; return CursorPoint.HasValue; }
        public void DoDragDrop(DependencyObject source, IDataObject data, DragDropEffects effects) => OnDrag?.Invoke(source, data, effects);
        public IReadOnlyList<YouTubeMiniPlayerListItem> GetClosedHistoryItems()
        {
            ClosedHistoryReads++;
            return ClosedStates.AsEnumerable().Reverse().Select(state => new YouTubeMiniPlayerListItem(Sources[0].Owner,
                YouTubeMiniPlayerNavigationService.CloneState(state))).ToArray();
        }
        public Task RestoreClosedHistoryAsync(Guid id) { RestoredClosedIds.Add(id); return RestoreClosed(id); }
        public bool IsOverArrangementEditableControl(Point point) => OverEditableControl;
        public Rect GetWorkAreaForDevicePoint(Point devicePoint) => WorkArea;
        public string? GetTriggerTag(DependencyObject? source) => TriggerTag;
    }
}
