using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TabBrowser.Views;
using Xunit;
using static TabBrowser.Tests.YouTubeMiniPlayerListViewControllerTests;

namespace TabBrowser.Tests;

[Collection("YouTubeMiniPlayerListWindow")]
public sealed class YouTubeMiniPlayerListPopupControllerTests
{
    [Theory]
    [InlineData(MouseButton.Left, true)]
    [InlineData(MouseButton.Right, true)]
    [InlineData(MouseButton.Middle, true)]
    [InlineData(MouseButton.Left, false)]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Middle, false)]
    public void IndividualMenuClosesOutsideEvenWithinTheList(MouseButton button, bool insideList) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        if (insideList) f.Hits.Add(f.Elements.YouTubeMiniPlayerListPopupRoot);
        f.Click(button);
        Assert.False(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void IndividualMenuKeepsClicksInsideItsOwnBounds(MouseButton button) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Hits.Add((FrameworkElement)f.Elements.YouTubeMiniPlayerItemMorePopup.Child);
        f.Click(button);
        Assert.True(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public void IndividualMenuTriggerClickIsLeftForTheExistingToggle() => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Hits.Add(f.Elements.YouTubeMiniPlayerItemMorePopup.PlacementTarget);
        f.LeftClick();
        Assert.True(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        f.Click(MouseButton.Right);
        Assert.False(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1400, 900, 360, 420)]
    [InlineData(240, 230, 224, 214)]
    public void ClosedHistoryFitsTheWorkAreaAndAlignsWithTheRestoreButton(
        double workWidth, double workHeight, double expectedWidth, double expectedHeight) => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        var trigger = new Button { Width = 180, Height = 30, HorizontalAlignment = HorizontalAlignment.Right };
        ((Border)f.Elements.YouTubeMiniPlayerListPopupRoot).Child = trigger;
        var popup = f.Elements.YouTubeMiniPlayerClosedHistoryPopup!;
        var root = new Border { Width = 360, Height = 100, MaxHeight = 420, Opacity = 0 };
        popup.Child = root;
        popup.PlacementTarget = trigger;
        popup.Placement = PlacementMode.Bottom;
        popup.VerticalOffset = 4;
        f.List.Host.WorkArea = new(0, 0, workWidth, workHeight);
        f.Owner.Left = 100;
        f.Owner.Top = 100;
        f.Elements.YouTubeMiniPlayerListWindow.ShowOrActivate();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        f.Controller.UpdateClosedHistoryPopupPlacement();
        popup.IsOpen = true;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        f.Controller.UpdateClosedHistoryPopupPlacement();
        Assert.Equal(expectedWidth, root.MaxWidth, 4);
        Assert.Equal(expectedHeight, root.MaxHeight, 4);
        Assert.Equal(trigger.PointToScreen(new(trigger.ActualWidth, 0)).X,
            root.PointToScreen(new(root.ActualWidth, 0)).X, 0);
        Assert.True(root.PointToScreen(new()).Y >= trigger.PointToScreen(new(0, trigger.ActualHeight)).Y);
    });

    [Fact]
    public void HistoryTriggerKeepsTheMenuForItsToggleAndOutsideClicksCloseIt() => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        var popup = f.Elements.YouTubeMiniPlayerClosedHistoryPopup!;
        f.Hits.Add(f.Elements.YouTubeMiniPlayerClosedHistoryButton!);
        f.LeftClick();
        Assert.True(popup.IsOpen);
        f.Hits.Clear();
        f.Hits.Add((FrameworkElement)popup.Child);
        f.LeftClick();
        Assert.True(popup.IsOpen);
        f.Hits.Clear();
        f.Hits.Add(f.Elements.YouTubeMiniPlayerListPopupRoot);
        f.LeftClick();
        Assert.False(popup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(2560, 800, PlacementMode.Left)]
    [InlineData(2560, 500, PlacementMode.Right)]
    [InlineData(2560, 40, PlacementMode.Right)]
    [InlineData(1920, 600, PlacementMode.Left)]
    [InlineData(1920, 500, PlacementMode.Right)]
    public void GlobalActionsSideChoiceUsesItsWiderPanel(double workWidth, double listLeft, PlacementMode expected)
    {
        var placement = YouTubeMiniPlayerListPopupController.CalculateYouTubeMiniPlayerArrangePopupPlacement(
            new(0, 0, workWidth, 1080), new(listLeft, 100, 780, 600), 680, 12, 8);
        Assert.Equal(expected, placement);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void ClickOnListOutsideSidePanelsClosesBothPanelsWithoutClosingList(MouseButton button) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        // 配置先の一覧を押しても、横のパネルを残す例外にはしない。
        f.Elements.YouTubeMiniPlayerArrangePopup.PlacementTarget = (UIElement)f.Elements.YouTubeMiniPlayerListPopupRoot;
        f.Elements.YouTubeMiniPlayerGlobalActionsPopup.PlacementTarget = (UIElement)f.Elements.YouTubeMiniPlayerListPopupRoot;
        f.Hits.Add((FrameworkElement)f.Elements.YouTubeMiniPlayerListPopupRoot);
        f.Click(button);
        Assert.False(f.Elements.YouTubeMiniPlayerArrangePopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerGlobalActionsPopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerLinearPlacementPopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SidePanelTriggerRemainsOpenUntilItsButtonHandlesTheToggle(bool arrange) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        var trigger = arrange ? f.Elements.YouTubeMiniPlayerArrangeMenuButton : f.Elements.YouTubeMiniPlayerGlobalActionsButton;
        f.Hits.Add(trigger!);
        f.Hits.Add((FrameworkElement)f.Elements.YouTubeMiniPlayerListPopupRoot);
        f.LeftClick();
        Assert.True((arrange ? f.Elements.YouTubeMiniPlayerArrangePopup : f.Elements.YouTubeMiniPlayerGlobalActionsPopup).IsOpen);
        Assert.False((arrange ? f.Elements.YouTubeMiniPlayerGlobalActionsPopup : f.Elements.YouTubeMiniPlayerArrangePopup).IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, MouseButton.Left)]
    [InlineData(true, MouseButton.Right)]
    public void DisabledOrRightClickedTriggerDoesNotKeepSidePanelOpen(bool enabled, MouseButton button) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        var trigger = f.Elements.YouTubeMiniPlayerGlobalActionsButton!;
        trigger.IsEnabled = enabled;
        f.Hits.Add(trigger);
        f.Hits.Add((FrameworkElement)f.Elements.YouTubeMiniPlayerListPopupRoot);
        f.Click(button);
        Assert.False(f.Elements.YouTubeMiniPlayerGlobalActionsPopup.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public void DeactivationClosesSidePanelsAndDisposedControllerIgnoresTheCallback() => RunInSta(() =>
    {
        using var f = new PopupFixture();
        void Deactivate() => typeof(YouTubeMiniPlayerListPopupController)
            .GetMethod("YouTubeMiniPlayerListOwnerWindow_Deactivated", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Controller, [f.Owner, EventArgs.Empty]);
        f.OpenAll();
        Deactivate();
        Assert.False(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerArrangePopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerGlobalActionsPopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerLinearPlacementPopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        f.Controller.Dispose();
        f.OpenAll();
        Deactivate();
        Assert.True(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerArrangePopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerGlobalActionsPopup.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(-1)]
    public void FocusWithinSidePanelStaysOpenAndFocusInOwnerContentClosesIt(int popupIndex) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        var focus = new TextBox();
        if (popupIndex >= 0) ((Border)f.Subpopups[popupIndex].Child).Child = focus;
        var args = new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 100, null, focus);
        typeof(YouTubeMiniPlayerListPopupController)
            .GetMethod("YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Controller, [f.Owner, args]);
        Assert.Equal(popupIndex is 4 or 6, f.Elements.YouTubeMiniPlayerArrangePopup.IsOpen);
        Assert.Equal(popupIndex == 5, f.Elements.YouTubeMiniPlayerGlobalActionsPopup.IsOpen);
        Assert.Equal(popupIndex is 4 or 6, f.Elements.YouTubeMiniPlayerLinearPlacementPopup.IsOpen);
        Assert.Equal(popupIndex == 7, f.Elements.YouTubeMiniPlayerClosedHistoryPopup!.IsOpen);
        Assert.Equal(popupIndex == 3, f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1080, 1, 1, 1064)]
    [InlineData(720, 1, 1, 704)]
    [InlineData(720, 1.5, 1, 1056)]
    [InlineData(1080, 1, 1.5, 709.333)]
    public void PopupHeightUsesCurrentWorkAreaAndBothDpiScales(
        double workAreaHeight, double ownerDpiScale, double popupDpiScale,
        double expectedMaxHeight)
    {
        var maxHeight = YouTubeMiniPlayerListPopupController.CalculateYouTubeMiniPlayerListPopupMaxHeight(
            workAreaHeight, ownerDpiScale, popupDpiScale, 8);

        Assert.Equal(expectedMaxHeight, maxHeight, 3);
    }

    [Fact]
    public void OpenWindowUpdatesItsLimitsWhenWorkAreaChanges() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.List.Host.WorkArea = new Rect(0, 0, 1920, 900);
        f.OpenAll();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        f.Controller.ClampYouTubeMiniPlayerListPopupPosition();
        Assert.Equal(884, f.Elements.YouTubeMiniPlayerListWindow.MaxHeight);

        f.List.Host.WorkArea = new Rect(1000, 0, 1920, 600);
        f.Controller.ClampYouTubeMiniPlayerListPopupPosition();
        Assert.Equal(584, f.Elements.YouTubeMiniPlayerListWindow.MaxHeight);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.Left >= 1008);
    });

    [Fact]
    public void DisplayDropdownOutsideArrangeBoundsDoesNotCloseTheListOrArrangePopup() => RunInSta(() =>
    {
        var selector = new ComboBox
        {
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}"><Border /></Popup>
                </ControlTemplate>
                """)
        };
        // ComboBoxはLoaded前の展開を抑止するため、既存テストと同じ非表示Hwndへ接続する。
        using var source = new HwndSource(new HwndSourceParameters("Kiten整列先選択の非表示テスト")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 400,
            Height = 60
        });
        source.RootVisual = selector;
        using var f = new PopupFixture(selector);
        selector.ItemsSource = new[] { "現在の画面", "ディスプレイ 2" };
        f.OpenAll();
        selector.ApplyTemplate();
        var dropdown = (Popup)selector.Template.FindName("PART_Popup", selector);
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            selector.IsDropDownOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
            Assert.True(dropdown.IsOpen, $"loaded={selector.IsLoaded}, visible={selector.IsVisible}, open={selector.IsDropDownOpen}, size={selector.ActualWidth}x{selector.ActualHeight}, child={dropdown.Child.IsVisible}");
            f.Hits.Add(dropdown.Child);
            f.LeftClick();
            Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
            Assert.True(f.Elements.YouTubeMiniPlayerArrangePopup.IsOpen);
        }
        finally { selector.IsDropDownOpen = false; }
        return Task.CompletedTask;
    });

    [Fact]
    public void CloseCancelsSubpopupsAndWaitsForPreviewBeforeClearingHighlights() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        var state = f.List.Add(0);
        await f.List.Refresh();
        f.List.Controller.Interaction.SetHoveredItem(state.Id);
        f.List.Controller.StartRefreshTimer();
        f.OpenAll();
        f.List.Host.Events.Clear();
        var stop = new TaskCompletionSource();
        f.List.Host.StopPreview = () => stop.Task;
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = false;
        var close = f.Controller.YouTubeMiniPlayerListPopup_Closed(f, EventArgs.Empty);
        Assert.False(close.IsCompleted);
        Assert.All(f.Subpopups, popup => Assert.False(popup.IsOpen));
        Assert.False(f.List.Timer.IsEnabled);
        Assert.Equal("cancel-arrangement:False", f.List.Host.Events[0]);
        Assert.True(f.List.Host.Events.IndexOf("cancel-hover") < f.List.Host.Events.IndexOf("stop-hover"));
        Assert.DoesNotContain("highlights", f.List.Host.Events);
        stop.SetResult();
        await close;
        Assert.Equal("highlights", f.List.Host.Events.Last());
        Assert.Null(f.List.Controller.Interaction.HoveredItemId);
    });

    [Fact]
    public void OpenRefreshesBeforeStartingTimerAndShutdownPreventsLateStart() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.List.Add(0);
        var capture = new TaskCompletionSource();
        f.List.Host.Capture = _ => capture.Task;
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        var open = f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        Assert.False(f.List.Timer.IsEnabled);
        Assert.Equal(1, f.List.Host.CaptureCount);
        f.List.Controller.StopRefreshTracking();
        capture.SetResult();
        await open;
        Assert.False(f.List.Timer.IsEnabled);
    });

    [Fact]
    public void OpenStartsExistingOneSecondTimerAfterRefresh() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        await f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        Assert.True(f.List.Timer.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(1), f.List.Timer.Interval);
    });

    [Theory]
    [InlineData("YouTubeMiniPlayerAbRepeatPopupTrigger", 0)]
    [InlineData("YouTubeMiniPlayerSpeedPopupTrigger", 1)]
    [InlineData("YouTubeMiniPlayerVolumePopupTrigger", 2)]
    [InlineData("YouTubeMiniPlayerLinearPlacementPopupTrigger", 6)]
    [InlineData("other", -1)]
    public void ContentClickPreservesOnlyItsMatchingAuxiliaryPopup(string tag, int keepIndex) => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.List.Host.TriggerTag = tag;
        f.List.Host.IsPreviewActive = true;
        f.Controller.YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown(f,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        foreach (var index in new[] { 0, 1, 2, 6 })
            Assert.Equal(index == keepIndex, f.Subpopups[index].IsOpen);
        Assert.True(f.Subpopups[3].IsOpen);
        Assert.True(f.Subpopups[4].IsOpen);
        Assert.True(f.Subpopups[5].IsOpen);
        Assert.Equal(new[] { "cancel-hover", "stop-hover" }, f.List.Host.Events);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(250)]
    [InlineData(650)]
    public void ReopeningFitsContentThatChangedWhileHiddenWithoutResettingPosition(double targetHeight) => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.Elements.YouTubeMiniPlayerListPopupRoot.Height = 500;
        f.Elements.YouTubeMiniPlayerListWindow.ShowOrActivate();
        await f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        var position = new Point(f.Owner.Left, f.Owner.Top);
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = false;
        await f.Controller.YouTubeMiniPlayerListPopup_Closed(f, EventArgs.Empty);
        f.Elements.YouTubeMiniPlayerListPopupRoot.Height = targetHeight;
        f.Elements.YouTubeMiniPlayerListWindow.ShowOrActivate();
        await f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        Assert.InRange(f.Owner.Height, targetHeight, targetHeight + 2);
        Assert.Equal(position, new Point(f.Owner.Left, f.Owner.Top));
    });

    [Fact]
    public void GrowingNearWorkAreaBottomKeepsTheWholeWindowWithinTheSameArea() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.Controller.InitializeTracking();
        f.Elements.YouTubeMiniPlayerListPopupRoot.Height = 200;
        f.Elements.YouTubeMiniPlayerListWindow.ShowOrActivate();
        await f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        f.Owner.Left = 100;
        f.Owner.Top = 850;
        f.Elements.YouTubeMiniPlayerListPopupRoot.Height = 650;
        f.Elements.YouTubeMiniPlayerListWindow.FitToContentHeight();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.InRange(f.Owner.Height, 650, 652);
        Assert.InRange(f.Owner.Top, 8, 423);
        Assert.True(f.Owner.Top + f.Owner.Height <= f.List.Host.WorkArea.Bottom - 7);
        Assert.InRange(f.Owner.Left, 99, 101);
    });

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ClickingWithinListOrAnyAuxiliaryPopupKeepsListOpen(int popupIndex) => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Hits.Add(popupIndex < 0 ? f.Elements.YouTubeMiniPlayerListPopupRoot : (FrameworkElement)f.Subpopups[popupIndex].Child);
        f.LeftClick();
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        if (popupIndex < 0)
        {
            Assert.All(f.Subpopups.Take(3), item => Assert.True(item.IsOpen));
            Assert.False(f.Subpopups[3].IsOpen);
            Assert.All(f.Subpopups.Skip(4), item => Assert.False(item.IsOpen));
        }
        if (popupIndex == 4) Assert.True(f.Subpopups[6].IsOpen);
        if (popupIndex == 6) Assert.True(f.Subpopups[4].IsOpen);
        Assert.True(f.List.Controller.Interaction.IsDuplicatePopupMouseDown(100));
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RoutedChildPopupClickKeepsItsTargetUntilTheItemRuns(int popupIndex) => RunInSta(() =>
    {
        using var f = new PopupFixture();
        var root = (Border)f.Elements.YouTubeMiniPlayerListPopupRoot;
        var panel = new StackPanel();
        root.Child = panel;
        var popup = f.Subpopups[popupIndex];
        popup.AllowsTransparency = true;
        ((Border)popup.Child).Opacity = 0;
        panel.Children.Add(popup);
        var button = new Button { Content = "項目を実行" };
        ((Border)popup.Child).Child = button;
        root.PreviewMouseLeftButtonDown += f.Controller.YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown;
        var previewCalls = 0;
        root.PreviewMouseLeftButtonDown += (_, _) => previewCalls++;
        var targetAvailable = true;
        popup.Closed += (_, _) => targetAvailable = false;
        var commands = 0;
        button.Click += (_, _) =>
        {
            Assert.True(popup.IsOpen);
            Assert.True(targetAvailable);
            commands++;
        };
        f.Elements.YouTubeMiniPlayerListWindow.ShowOrActivate();
        popup.IsOpen = true;
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 100, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        Assert.Equal(1, previewCalls);
        Assert.True(popup.IsOpen);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, commands);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrangementEditingOrEditableControlKeepsListOpen(bool editMode) => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.List.Host.IsArrangementEditMode = editMode;
        f.List.Host.OverEditableControl = !editMode;
        f.LeftClick();
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        await Task.CompletedTask;
    });

    [Fact]
    public void OutsideClickClosesChildPopupsAndKeepsIndependentListVisible() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Hits.Add(f.Subpopups[0].PlacementTarget);
        f.LeftClick();
        Assert.True(f.Subpopups[0].IsOpen);
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        f.Hits.Clear();
        f.LeftClick();
        Assert.All(f.Subpopups, popup => Assert.False(popup.IsOpen));
        await Task.CompletedTask;
    });

    [Fact]
    public void ClickInBrowserDoesNotCloseIndependentList() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Hits.Add(f.Elements.RootGrid);
        f.LeftClick();
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        await Task.CompletedTask;
    });

    [Fact]
    public void WindowInteractionClosesAuxiliaryPanelsWithoutClosingTheList() => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.Controller.CloseAuxiliaryPopups();
        Assert.All(f.Subpopups, popup => Assert.False(popup.IsOpen));
        Assert.True(f.Elements.YouTubeMiniPlayerListWindow.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public void QueuedClampCompletesClaimButDoesNotApplyAfterDispose() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        f.Controller.InitializeTracking();
        f.Controller.InitializeTracking();
        f.Controller.QueueYouTubeMiniPlayerListPopupPositionClamp();
        Assert.False(f.List.Controller.Interaction.TryQueuePopupPositionClamp());
        f.Controller.Dispose();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(f.List.Controller.Interaction.TryQueuePopupPositionClamp());
        f.List.Controller.Interaction.CompletePopupPositionClamp();
        f.Controller.QueueYouTubeMiniPlayerListPopupPositionClamp();
        Assert.True(f.List.Controller.Interaction.TryQueuePopupPositionClamp());
        f.List.Controller.Interaction.CompletePopupPositionClamp();
        f.Controller.InitializeTracking();
        Assert.False((bool)typeof(YouTubeMiniPlayerListPopupController)
            .GetField("_isTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Controller)!);
    });

    [Fact]
    public void IndependentWindowDeactivationKeepsEditingPreviewOnly() => RunInSta(() =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        f.List.Host.IsArrangementEditMode = true;
        typeof(YouTubeMiniPlayerListPopupController).GetMethod("YouTubeMiniPlayerListOwnerWindow_Deactivated",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Controller, [f.Owner, EventArgs.Empty]);
        Assert.DoesNotContain("cancel-arrangement:False", f.List.Host.Events);
        f.List.Host.IsArrangementEditMode = false;
        typeof(YouTubeMiniPlayerListPopupController).GetMethod("YouTubeMiniPlayerListOwnerWindow_Deactivated",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Controller, [f.Owner, EventArgs.Empty]);
        Assert.Contains("cancel-arrangement:False", f.List.Host.Events);
        return Task.CompletedTask;
    });

    [Fact]
    public void ClosingWhileOpeningDoesNotRestartTheRefreshTimer() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.List.Add(0);
        var capture = new TaskCompletionSource();
        f.List.Host.Capture = _ => capture.Task;
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        var open = f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        Assert.False(open.IsCompleted);
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = false;
        await f.Controller.YouTubeMiniPlayerListPopup_Closed(f, EventArgs.Empty);
        capture.SetResult();
        await open;
        Assert.False(f.List.Timer.IsEnabled);
    });

    [Fact]
    public void OldCloseCompletionDoesNotClearReopenedWindowHighlights() => RunInSta(async () =>
    {
        using var f = new PopupFixture();
        f.OpenAll();
        var stop = new TaskCompletionSource();
        f.List.Host.StopPreview = () => stop.Task;
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = false;
        var close = f.Controller.YouTubeMiniPlayerListPopup_Closed(f, EventArgs.Empty);
        f.Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
        await f.Controller.YouTubeMiniPlayerListPopup_Opened(f, EventArgs.Empty);
        f.List.Host.Events.Clear();
        stop.SetResult();
        await close;
        Assert.DoesNotContain("highlights", f.List.Host.Events);
        Assert.True(f.List.Timer.IsEnabled);
    });

    [Fact]
    public void WindowCompositionAndShutdownRemainConnectedToBothOwners()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var main = File.ReadAllText(Path.Combine(directory.FullName, "MainWindow.xaml.cs"));
        var partial = File.ReadAllText(Path.Combine(directory.FullName, "MainWindow.YouTubeMiniPlayer.cs"));
        var popup = File.ReadAllText(Path.Combine(directory.FullName, "Views", "YouTubeMiniPlayerListPopupController.cs"));
        var list = File.ReadAllText(Path.Combine(directory.FullName, "Views", "YouTubeMiniPlayerListViewController.cs"));
        Assert.Contains("new YouTubeMiniPlayerListViewController(", main);
        Assert.Contains("_youTubeMiniPlayerStateSyncCoordinator, _youTubeMiniPlayerListRefreshTimer, this", main);
        Assert.Contains("new YouTubeMiniPlayerListPopupController(", main);
        Assert.Contains("_youTubeMiniPlayerListPopupController.InitializeTracking()", partial);
        Assert.Contains("_youTubeMiniPlayerListPopupController.Dispose()", partial);
        Assert.Contains("_youTubeMiniPlayerListViewController.StopRefreshTracking()", partial);
        Assert.Contains("CollapseRequested += YouTubeMiniPlayerListWindow_CollapseRequested", main);
        Assert.Contains("CollapseRequested -= YouTubeMiniPlayerListWindow_CollapseRequested", partial);
        Assert.Contains("HoverPreviewToggleRequested += YouTubeMiniPlayerListWindow_HoverPreviewToggleRequested", main);
        Assert.Contains("HoverPreviewToggleRequested -= YouTubeMiniPlayerListWindow_HoverPreviewToggleRequested", partial);
        Assert.Contains("InputManager.Current.PreProcessInput += YouTubeMiniPlayerListPopup_PreProcessInput", popup);
        Assert.Contains("InputManager.Current.PreProcessInput -= YouTubeMiniPlayerListPopup_PreProcessInput", popup);
        Assert.Contains("_owner.Deactivated += YouTubeMiniPlayerListOwnerWindow_Deactivated", popup);
        Assert.Contains("_owner.Deactivated -= YouTubeMiniPlayerListOwnerWindow_Deactivated", popup);
        Assert.Contains("_owner.PreviewGotKeyboardFocus += YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus", popup);
        Assert.Contains("_owner.PreviewGotKeyboardFocus -= YouTubeMiniPlayerListOwnerWindow_PreviewGotKeyboardFocus", popup);
        Assert.Contains("_youTubeMiniPlayerListRefreshTimer.Tick -= RefreshTimer_Tick", list);
        Assert.Contains("_host.DoDragDrop(sourceContainer, data, DragDropEffects.Move)", list);
        Assert.Contains("=> DragDrop.DoDragDrop(source, data, effects)", partial);
        Assert.DoesNotContain("new WebView2(", list);
        Assert.DoesNotContain("new WebView2(", popup);
    }

    [Fact]
    public void FoldingClosesMenusAndCancelsPreviewBeforeItsRestoreCompletes() => RunInSta(async () =>
    {
        var body = new Border { Name = "YouTubeMiniPlayerListBody", Height = 300 };
        var toolbar = new Border { Name = "YouTubeMiniPlayerListToolbar", Height = 30 };
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 30 });
        content.Children.Add(toolbar);
        content.Children.Add(body);
        using var f = new PopupFixture(listContent: content);
        var window = f.Elements.YouTubeMiniPlayerListWindow;
        f.OpenAll();
        f.List.Controller.StartRefreshTimer();
        var stop = new TaskCompletionSource();
        f.List.Host.StopPreview = () => stop.Task;
        var fold = f.Controller.ToggleYouTubeMiniPlayerListCollapsedAsync();
        Assert.False(fold.IsCompleted);
        Assert.True(window.IsCollapsed);
        Assert.All(f.Subpopups, popup => Assert.False(popup.IsOpen));
        Assert.Contains("cancel-hover", f.List.Host.Events);
        Assert.Contains("stop-hover", f.List.Host.Events);
        Assert.True(f.List.Timer.IsEnabled);
        window.IsOpen = false;
        stop.SetResult();
        await fold;
        Assert.False(window.IsOpen);
        Assert.True(window.IsCollapsed);
        window.ShowOrActivate();
        await f.Controller.ToggleYouTubeMiniPlayerListCollapsedAsync();
        Assert.False(window.IsCollapsed);
        Assert.True(body.IsVisible);
    });

    private sealed class PopupFixture : IDisposable
    {
        internal readonly Fixture List = new();
        internal Window Owner => List.Elements.YouTubeMiniPlayerListWindow;
        internal readonly HashSet<UIElement> Hits = [];
        internal readonly YouTubeMiniPlayerListPopupElements Elements;
        internal readonly YouTubeMiniPlayerListPopupController Controller;
        internal Popup[] Subpopups =>
        [
            Elements.YouTubeMiniPlayerAbRepeatPopup, Elements.YouTubeMiniPlayerSpeedPopup,
            Elements.YouTubeMiniPlayerVolumePopup, Elements.YouTubeMiniPlayerItemMorePopup,
            Elements.YouTubeMiniPlayerArrangePopup, Elements.YouTubeMiniPlayerGlobalActionsPopup,
            Elements.YouTubeMiniPlayerLinearPlacementPopup, Elements.YouTubeMiniPlayerClosedHistoryPopup!
        ];

        internal PopupFixture(ComboBox? displaySelector = null, FrameworkElement? listContent = null)
        {
            Elements = new(new Grid(), listContent ?? new Border(), List.Elements.YouTubeMiniPlayerListWindow,
                new Popup(), new Popup(), new Popup(), new Popup(), new Popup(), new Popup(), new Popup(), displaySelector,
                new Button(), new Button(), List.Elements.YouTubeMiniPlayerClosedHistoryPopup, new Button());
            foreach (var popup in Subpopups)
            {
                popup.Child = new Border();
                popup.PlacementTarget = new Border();
            }
            Elements.YouTubeMiniPlayerListWindow.AttachContent(Elements.YouTubeMiniPlayerListPopupRoot, new ResourceDictionary());
            Controller = new YouTubeMiniPlayerListPopupController(Owner, Elements, List.Controller, List.Host,
                (element, _) => Hits.Contains(element));
        }
        internal void OpenAll()
        {
            Elements.YouTubeMiniPlayerListWindow.IsOpen = true;
            foreach (var popup in Subpopups) popup.IsOpen = true;
        }
        internal void LeftClick() => typeof(YouTubeMiniPlayerListPopupController)
            .GetMethod("ProcessLeftMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Controller, [100, new Point(50, 60)]);
        internal void Click(MouseButton button) => typeof(YouTubeMiniPlayerListPopupController)
            .GetMethod("ProcessMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Controller, [100, new Point(50, 60), button]);
        public void Dispose()
        {
            Controller.Dispose();
            foreach (var popup in Subpopups) popup.IsOpen = false;
            List.Dispose();

        }
    }
}
