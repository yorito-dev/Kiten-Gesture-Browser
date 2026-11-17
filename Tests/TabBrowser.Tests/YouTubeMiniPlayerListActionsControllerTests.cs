using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;
using static TabBrowser.Tests.YouTubeMiniPlayerListViewControllerTests;

namespace TabBrowser.Tests;

[Collection("YouTubeMiniPlayerListWindow")]
public sealed class YouTubeMiniPlayerListActionsControllerTests
{
    [Fact]
    public void LateIndividualMenuClosedNotificationKeepsTheNewTarget() => RunInSta(() =>
    {
        using var f = new Fixture();
        var first = f.Item();
        var next = f.Item();
        f.Controller.YouTubeMiniPlayerItemMoreButton_Click(new Button { DataContext = first }, new RoutedEventArgs(Button.ClickEvent));
        f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        var button = new Button { DataContext = next };
        f.Controller.YouTubeMiniPlayerItemMoreButton_Click(button, new RoutedEventArgs(Button.ClickEvent));
        f.Controller.YouTubeMiniPlayerItemMorePopup_Closed(null!, EventArgs.Empty);
        Assert.True(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        Assert.Same(next, f.Elements.YouTubeMiniPlayerItemMorePopup.DataContext);
        Assert.Same(button, f.Elements.YouTubeMiniPlayerItemMorePopup.PlacementTarget);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChatActionKeepsTheDisplayedIntentWhenAnotherWindowChangesItsState(bool initiallyOpen) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        item.UpdateChatState(true, true, initiallyOpen);
        f.Controller.YouTubeMiniPlayerItemMoreButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        f.Host.RefreshChat = target => target.UpdateChatState(true, true, !initiallyOpen);
        f.Host.Chat = (target, isOpen) =>
        {
            Assert.Same(item, target);
            Assert.False(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
            f.Controller.YouTubeMiniPlayerItemMorePopup_Closed(null!, EventArgs.Empty);
            Assert.Equal(!initiallyOpen, isOpen);
            return Task.CompletedTask;
        };
        await f.Controller.YouTubeMiniPlayerItemChatButton_Click(new Button(), new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(f.Host.Chats);
        Assert.Contains("refresh", f.Host.Order);
    });

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)]
    [InlineData(false, false, true)]
    public void ChatActionUsesAvailabilityButCanCloseAnExistingWindow(bool ready, bool? available, bool open) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        f.Host.RefreshChat = target => target.UpdateChatState(ready, available, open);
        f.Controller.YouTubeMiniPlayerItemMoreButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        await f.Controller.YouTubeMiniPlayerItemChatButton_Click(new Button(), new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(open ? 1 : 0, f.Host.Chats.Count);
        if (open) Assert.False(f.Host.Chats[0].IsOpen);
    });

    private static YouTubeMiniPlayerListItem SeekItem(bool loaded = true, bool live = false, double? duration = 600)
        => new(CreateOwnerToken(), new YouTubeMiniPlayerState
        {
            Id = Guid.NewGuid(),
            LastPositionSeconds = 80,
            DurationSeconds = duration,
            IsLiveBroadcast = live,
            IsPaused = true
        }, isRuntimeLoaded: loaded);

    [Theory]
    [InlineData(0, 200, 0)]
    [InlineData(50, 200, .25)]
    [InlineData(200, 200, 1)]
    [InlineData(-10, 200, 0)]
    [InlineData(210, 200, 1)]
    public void ProgressSeekUsesBarWidthAndClampsToTheVideo(double x, double width, double expected) => RunInSta(() =>
    {
        var item = SeekItem();
        Assert.True(YouTubeMiniPlayerListActionsController.TryGetProgressRatio(item, x, width, out var ratio));
        Assert.Equal(expected, ratio);
        Assert.Equal("移動先 約2:30", item.GetPlaybackSeekToolTip(.25));
        return Task.CompletedTask;
    });

    [Fact]
    public void ProgressSeekRejectsUnavailableVideoAndInvalidGeometry() => RunInSta(async () =>
    {
        using var f = new Fixture();
        foreach (var item in new[] { SeekItem(false), SeekItem(live: true), SeekItem(duration: null), SeekItem(duration: double.NaN), SeekItem(duration: 0) })
        {
            Assert.False(YouTubeMiniPlayerListActionsController.TryGetProgressRatio(item, 50, 200, out _));
            await f.Controller.SeekProgressAsync(item, .25);
        }
        var valid = SeekItem();
        foreach (var (x, width) in new[] { (double.NaN, 200d), (double.PositiveInfinity, 200d), (10d, 0d), (10d, -1d), (10d, double.NaN) })
            Assert.False(YouTubeMiniPlayerListActionsController.TryGetProgressRatio(valid, x, width, out _));
        await f.Controller.SeekProgressAsync(valid, double.NaN);
        Assert.Empty(f.Host.Seeks);
        Assert.Empty(f.Host.Order);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProgressSeekOnlySavesAndRefreshesOnSuccess(bool success) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = SeekItem();
        f.Host.Seek = (_, _) => Task.FromResult(success);
        await f.Controller.SeekProgressAsync(item, .25);
        Assert.Same(item, Assert.Single(f.Host.Seeks).Item);
        Assert.Equal(.25, f.Host.Seeks[0].Ratio);
        Assert.Equal(success ? new[] { "seek", "save", "refresh" } : new[] { "seek", "status" }, f.Host.Order);
    });

    [Fact]
    public void ProgressSeekBlocksOnlyThePendingTargetAndAllowsRetry() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var firstItem = SeekItem();
        var secondItem = SeekItem();
        var hold = new TaskCompletionSource<bool>();
        f.Host.Seek = (item, _) => ReferenceEquals(item, firstItem) ? hold.Task : Task.FromResult(true);
        var first = f.Controller.SeekProgressAsync(firstItem, .25);
        await f.Controller.SeekProgressAsync(firstItem, .5);
        Assert.Single(f.Host.Seeks);
        await f.Controller.SeekProgressAsync(secondItem, .75);
        Assert.Equal(2, f.Host.Seeks.Count);
        hold.SetResult(true);
        await first;
        await f.Controller.SeekProgressAsync(firstItem, .8);
        Assert.Equal(3, f.Host.Seeks.Count);
        Assert.Equal(.8, f.Host.Seeks[2].Ratio);
    });

    [Fact]
    public void ProgressSeekSuppressesCompletionAfterControllerStops() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var hold = new TaskCompletionSource<bool>();
        f.Host.Seek = (_, _) => hold.Task;
        var item = SeekItem();
        var pending = f.Controller.SeekProgressAsync(item, .25);
        f.Controller.StopVolumeTracking();
        hold.SetResult(true);
        await pending;
        await f.Controller.SeekProgressAsync(item, .5);
        Assert.Single(f.Host.Seeks);
        Assert.Equal(new[] { "seek" }, f.Host.Order);
    });

    [Fact]
    public void ProgressSeekExceptionReleasesThePendingTarget() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = SeekItem();
        f.Host.Seek = (_, _) => Task.FromException<bool>(new InvalidOperationException("seek"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Controller.SeekProgressAsync(item, .25));
        f.Host.Seek = (_, _) => Task.FromResult(true);
        await f.Controller.SeekProgressAsync(item, .5);
        Assert.Equal(2, f.Host.Seeks.Count);
        Assert.Equal("refresh", f.Host.Order.Last());
    });

    [Fact]
    public void VolumeWheelIgnoresRowButtonsAndStoppedInput() => RunInSta(async () =>
    {
        using var f = new Fixture();
        await f.Open(f.Item(volume: .5));
        foreach (var sender in new object[] { new Button(), f.Elements.YouTubeMiniPlayerVolumeSlider })
        {
            if (sender is Slider) f.Controller.StopVolumeTracking();
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            f.Controller.YouTubeMiniPlayerVolumeSlider_PreviewMouseWheel(sender, args);
            Assert.False(args.Handled);
            Assert.Equal(50, f.Elements.YouTubeMiniPlayerVolumeSlider.Value);
        }
        Assert.Empty(f.Host.Volumes);
    });

    [Theory]
    [InlineData(.5, 120, .55)]
    [InlineData(.5, -120, .45)]
    [InlineData(.98, 120, 1)]
    [InlineData(.02, -120, 0)]
    [InlineData(.5, 240, .6)]
    public void VolumeWheelUsesFivePercentStepsAndTheExistingDebounce(double initial, int delta, double expected) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item(volume: initial);
        await f.Open(item);
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        f.Controller.YouTubeMiniPlayerVolumeSlider_PreviewMouseWheel(f.Elements.YouTubeMiniPlayerVolumeSlider, args);
        Assert.True(args.Handled);
        Assert.Equal(expected * 100, f.Elements.YouTubeMiniPlayerVolumeSlider.Value, 5);
        Assert.Empty(f.Host.Volumes);
        Assert.True(f.Timer.IsEnabled);
        await f.Apply();
        var applied = Assert.Single(f.Host.Volumes);
        Assert.Same(item, applied.Item);
        Assert.Equal(expected, applied.Value, 5);
    });

    [Fact]
    public void VolumeOpenUsesStateAndSuppressesProgrammaticApply() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item(volume: .42);
        await f.Open(item);
        Assert.Equal(42, f.Elements.YouTubeMiniPlayerVolumeSlider.Value);
        Assert.Equal("42%", f.Elements.YouTubeMiniPlayerVolumeValueText.Text);
        Assert.Equal(item.Title, f.Elements.YouTubeMiniPlayerVolumeTitleText.Text);
        Assert.Same(item, f.Elements.YouTubeMiniPlayerVolumePopup.DataContext);
        Assert.True(f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen);
        Assert.False(f.Timer.IsEnabled);
        Assert.Empty(f.Host.Volumes);
        Assert.Equal(TimeSpan.FromMilliseconds(200), f.Timer.Interval);
    });

    [Fact]
    public void SameIdClosesBeforeRuntimeEligibilityCheck() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        await f.Open(item);
        f.Host.CanControl = false;
        await f.Open(new YouTubeMiniPlayerListItem(CreateOwnerToken(), new YouTubeMiniPlayerState { Id = item.State.Id }));
        Assert.False(f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen);
        Assert.Equal(1, f.Host.EligibilityChecks);
        Assert.Empty(f.Host.Statuses);
    });

    [Fact]
    public void UnavailableRuntimeDoesNotReplaceCurrentTarget() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var a = f.Item();
        await f.Open(a);
        f.Host.CanControl = false;
        await f.Open(f.Item());
        f.Change(60);
        await f.Apply();
        Assert.Same(a, Assert.Single(f.Host.Volumes).Item);
        Assert.Contains("ミニプレイヤーをまだ操作できません", f.Host.Statuses);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VolumeUsesLatestPendingValueAndOnlySavesWhenChanged(bool changed) => RunInSta(async () =>
    {
        using var f = new Fixture();
        f.Host.Volume = (_, _) => Task.FromResult(changed);
        await f.Open(f.Item());
        f.Change(20);
        f.Change(63);
        Assert.True(f.Timer.IsEnabled);
        await f.Apply();
        Assert.Equal(.63, Assert.Single(f.Host.Volumes).Value);
        Assert.Equal(changed ? new[] { "volume", "save", "refresh" } : new[] { "volume" }, f.Host.Order);
        await f.Apply();
        Assert.Single(f.Host.Volumes);
    });

    [Fact]
    public void SwitchingTargetWaitsForActiveVolumeAndFlushesOldPendingValue() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var a = f.Item();
        var b = f.Item(volume: .81);
        await f.Open(a);
        var pending = new TaskCompletionSource<bool>();
        f.Host.Volume = (_, _) => f.Host.Volumes.Count == 1 ? pending.Task : Task.FromResult(true);
        f.Change(22);
        f.Tick();
        f.Change(47);
        var switched = f.Open(b);
        Assert.False(switched.IsCompleted);
        Assert.Equal(a.Title, f.Elements.YouTubeMiniPlayerVolumeTitleText.Text);
        pending.SetResult(true);
        await switched;
        Assert.Equal(2, f.Host.Volumes.Count);
        Assert.All(f.Host.Volumes, v => Assert.Same(a, v.Item));
        Assert.Equal(.47, f.Host.Volumes[1].Value);
        Assert.Equal(b.Title, f.Elements.YouTubeMiniPlayerVolumeTitleText.Text);
        Assert.Equal(81, f.Elements.YouTubeMiniPlayerVolumeSlider.Value);
        Assert.False(f.Timer.IsEnabled);
    });

    [Fact]
    public void CloseAwaitsPendingVolumeBeforeReleasingTargetAndPlacement() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        await f.Open(item);
        var pending = new TaskCompletionSource<bool>();
        f.Host.Volume = (_, _) => pending.Task;
        f.Change(39);
        var closing = f.Controller.YouTubeMiniPlayerVolumePopup_Closed(f.Elements.YouTubeMiniPlayerVolumePopup, EventArgs.Empty);
        Assert.False(closing.IsCompleted);
        Assert.NotNull(f.Elements.YouTubeMiniPlayerVolumePopup.PlacementTarget);
        pending.SetResult(true);
        await closing;
        Assert.Null(f.Elements.YouTubeMiniPlayerVolumePopup.PlacementTarget);
        Assert.Null(f.Elements.YouTubeMiniPlayerVolumePopup.DataContext);
        f.Change(80);
        await f.Apply();
        Assert.Single(f.Host.Volumes);
        Assert.False(f.Timer.IsEnabled);
    });

    [Fact]
    public void StopPreventsSliderFromRestartingTimerButCloseStillFlushes() => RunInSta(async () =>
    {
        using var f = new Fixture();
        await f.Open(f.Item());
        f.Change(40);
        f.Controller.StopVolumeTracking();
        f.Change(70);
        Assert.False(f.Timer.IsEnabled);
        await f.Controller.YouTubeMiniPlayerVolumePopup_Closed(new object(), EventArgs.Empty);
        Assert.Equal(.4, Assert.Single(f.Host.Volumes).Value);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoostSingleFlightReleasesLeaseAndPreservesChangedGate(bool changed) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        await f.Open(item);
        var pending = new TaskCompletionSource<bool>();
        f.Host.Boost = (_, _) => pending.Task;
        var firstArgs = new RoutedEventArgs(Button.ClickEvent);
        var first = f.Controller.YouTubeMiniPlayerAudioBoostButton_Click(new Button { Tag = "6" }, firstArgs);
        Assert.True(firstArgs.Handled);
        Assert.False(f.Elements.YouTubeMiniPlayerAudioBoostPanel.IsEnabled);
        var secondArgs = new RoutedEventArgs(Button.ClickEvent);
        await f.Controller.YouTubeMiniPlayerAudioBoostButton_Click(new Button { Tag = "9" }, secondArgs);
        Assert.False(secondArgs.Handled);
        Assert.Single(f.Host.Boosts);
        item.State.AudioBoostDecibels = 6;
        pending.SetResult(changed);
        await first;
        Assert.True(f.Elements.YouTubeMiniPlayerAudioBoostPanel.IsEnabled);
        Assert.Equal(changed ? new[] { "boost", "save", "status", "refresh" } : new[] { "boost" }, f.Host.Order);
        Assert.Equal("+6 dB", f.Elements.YouTubeMiniPlayerAudioBoostValueText.Text);
    });

    [Fact]
    public void BoostExceptionReleasesLeaseAndDoesNotOverwriteAnotherTargetsControls() => RunInSta(async () =>
    {
        using var f = new Fixture();
        await f.Open(f.Item());
        var pending = new TaskCompletionSource<bool>();
        f.Host.Boost = (_, _) => pending.Task;
        var boost = f.Controller.YouTubeMiniPlayerAudioBoostButton_Click(new Button { Tag = "6" }, new RoutedEventArgs(Button.ClickEvent));
        var other = f.Item();
        other.State.AudioBoostDecibels = 3;
        await f.Open(other);
        pending.SetException(new InvalidOperationException("boost"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boost);
        Assert.Equal("+3 dB", f.Elements.YouTubeMiniPlayerAudioBoostValueText.Text);
        f.Host.Boost = (_, _) => Task.FromResult(false);
        await f.Controller.YouTubeMiniPlayerAudioBoostButton_Click(new Button { Tag = "0" }, new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, f.Host.Boosts.Count);
    });

    [Fact]
    public void LocalBoostControlsRemainUnavailable() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        item.State.SourceKind = MiniPlayerSourceKind.LocalFile;
        await f.Open(item);
        Assert.False(f.Elements.YouTubeMiniPlayerAudioBoostPanel.IsEnabled);
        Assert.Equal("OFF", f.Elements.YouTubeMiniPlayerAudioBoostValueText.Text);
        Assert.Equal("ローカル動画では利用できません", f.Elements.YouTubeMiniPlayerAudioBoostHintText.Text);
    });

    [Theory]
    [InlineData("0.25", .25)]
    [InlineData("1.75", 1.75)]
    [InlineData("2", 2)]
    [InlineData("bad", 0)]
    public void SpeedUsesCapturedTargetAndClosesBeforeCommand(string tag, double expected) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        f.Controller.YouTubeMiniPlayerItemSpeedMenuButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(item, f.Elements.YouTubeMiniPlayerSpeedPopup.DataContext);
        f.Host.OnCommand = () => Assert.False(f.Elements.YouTubeMiniPlayerSpeedPopup.IsOpen);
        var args = new RoutedEventArgs(Button.ClickEvent);
        await f.Controller.YouTubeMiniPlayerItemSpeedButton_Click(new Button { Tag = tag }, args);
        Assert.Equal(expected > 0, args.Handled);
        if (expected > 0)
        {
            var command = Assert.Single(f.Host.Commands);
            Assert.Equal(MiniPlayerCommandProvider.SetSpeedActionId, command.Action);
            Assert.Same(item, command.Item);
            Assert.Equal(expected, command.Parameter);
        }
        else Assert.Empty(f.Host.Commands);
        f.Controller.YouTubeMiniPlayerSpeedPopup_Closed(new object(), EventArgs.Empty);
        Assert.Null(f.Elements.YouTubeMiniPlayerSpeedPopup.PlacementTarget);
        Assert.Null(f.Elements.YouTubeMiniPlayerSpeedPopup.DataContext);
    });

    [Theory]
    [InlineData("SetStart", (int)YouTubeMiniPlayerAbRepeatAction.SetStart)]
    [InlineData("SetEnd", (int)YouTubeMiniPlayerAbRepeatAction.SetEnd)]
    [InlineData("Toggle", (int)YouTubeMiniPlayerAbRepeatAction.Toggle)]
    [InlineData("Clear", (int)YouTubeMiniPlayerAbRepeatAction.Clear)]
    public void AbRepeatDisplaysStateAndUsesCapturedAction(string tag, int expected) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        item.State.AbRepeatStartSeconds = 10;
        item.State.AbRepeatEndSeconds = 20;
        item.State.IsAbRepeatEnabled = true;
        f.Controller.YouTubeMiniPlayerItemAbRepeatMenuButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(item, f.Elements.YouTubeMiniPlayerAbRepeatPopup.DataContext);
        Assert.NotNull(System.Windows.Data.BindingOperations.GetBinding(f.Elements.YouTubeMiniPlayerAbRepeatRangeText, TextBlock.TextProperty));
        Assert.True(f.Elements.YouTubeMiniPlayerAbRepeatSetEndButton.IsEnabled);
        Assert.True(f.Elements.YouTubeMiniPlayerAbRepeatToggleButton.IsEnabled);
        Assert.Equal("一時解除", f.Elements.YouTubeMiniPlayerAbRepeatToggleButton.Content);
        f.Host.OnCommand = () => Assert.False(f.Elements.YouTubeMiniPlayerAbRepeatPopup.IsOpen);
        await f.Controller.YouTubeMiniPlayerAbRepeatActionButton_Click(new Button { Tag = tag }, new RoutedEventArgs(Button.ClickEvent));
        var command = Assert.Single(f.Host.Commands);
        Assert.Equal(MiniPlayerCommandProvider.ExecuteAbRepeatActionId, command.Action);
        Assert.Same(item, command.Item);
        Assert.Equal((YouTubeMiniPlayerAbRepeatAction)expected, command.Parameter);
        f.Controller.YouTubeMiniPlayerAbRepeatPopup_Closed(new object(), EventArgs.Empty);
        Assert.Null(f.Elements.YouTubeMiniPlayerAbRepeatPopup.PlacementTarget);
        Assert.Null(f.Elements.YouTubeMiniPlayerAbRepeatPopup.DataContext);
        Assert.Null(System.Windows.Data.BindingOperations.GetBinding(f.Elements.YouTubeMiniPlayerAbRepeatRangeText, TextBlock.TextProperty));
    });

    [Fact]
    public void AbRepeatTitleTracksTargetUpdatesAndReleasesBindingOnClose() => RunInSta(() =>
    {
        using var f = new Fixture();
        var item = f.Item();
        f.Controller.YouTubeMiniPlayerItemAbRepeatMenuButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        item.State.Title = "更新された動画名";
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        Assert.Equal(item.Title, f.Elements.YouTubeMiniPlayerAbRepeatTitleText.Text);
        f.Controller.YouTubeMiniPlayerAbRepeatPopup_Closed(new object(), EventArgs.Empty);
        Assert.Null(System.Windows.Data.BindingOperations.GetBinding(f.Elements.YouTubeMiniPlayerAbRepeatTitleText, TextBlock.TextProperty));
        return Task.CompletedTask;
    });

    [Fact]
    public void MorePopupClosesOtherPopupsAndCloseCommandUsesItsTarget() => RunInSta(async () =>
    {
        using var f = new Fixture();
        var item = f.Item();
        f.Elements.YouTubeMiniPlayerAbRepeatPopup.IsOpen = true;
        f.Elements.YouTubeMiniPlayerSpeedPopup.IsOpen = true;
        f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen = true;
        f.Controller.YouTubeMiniPlayerItemMoreButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        Assert.False(f.Elements.YouTubeMiniPlayerAbRepeatPopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerSpeedPopup.IsOpen);
        Assert.False(f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen);
        Assert.Same(item, f.Elements.YouTubeMiniPlayerItemMorePopup.DataContext);
        f.Host.OnCommand = () => Assert.False(f.Elements.YouTubeMiniPlayerItemMorePopup.IsOpen);
        await f.Controller.YouTubeMiniPlayerItemCloseButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MiniPlayerCommandProvider.CloseActionId, Assert.Single(f.Host.Commands).Action);
        f.Controller.YouTubeMiniPlayerItemMorePopup_Closed(new object(), EventArgs.Empty);
        Assert.Null(f.Elements.YouTubeMiniPlayerItemMorePopup.DataContext);
        Assert.Null(f.Elements.YouTubeMiniPlayerItemMorePopup.PlacementTarget);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveEdgeHonorsItemEligibility(bool live) => RunInSta(async () =>
    {
        using var f = new Fixture();
        var state = new YouTubeMiniPlayerState { Id = Guid.NewGuid(), IsLiveBroadcast = live, IsAtLiveEdge = false };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        var args = new RoutedEventArgs(Button.ClickEvent);
        await f.Controller.YouTubeMiniPlayerItemLiveEdgeButton_Click(new Button { DataContext = item }, args);
        Assert.True(args.Handled);
        Assert.Equal(live ? 1 : 0, f.Host.Commands.Count);
    });

    [Fact]
    public void RoutedSpeedMenuClickAppliesTheValueToItsOriginalTarget() => RunInSta(async () =>
    {
        using var list = new YouTubeMiniPlayerListViewControllerTests.Fixture();
        using var f = new Fixture();
        var button = new Button { Tag = "1.5", Content = "1.5x" };
        using var input = AttachMenuInput(list, f, f.Elements.YouTubeMiniPlayerSpeedPopup, button);
        f.Elements.YouTubeMiniPlayerSpeedPopup.Closed += (sender, args) =>
            f.Controller.YouTubeMiniPlayerSpeedPopup_Closed(sender!, args);
        var item = f.Item();
        f.Controller.YouTubeMiniPlayerItemSpeedMenuButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        Task? command = null;
        button.Click += (_, args) => command = f.Controller.YouTubeMiniPlayerItemSpeedButton_Click(button, args);
        RaiseMenuMouseDown(button);
        Assert.True(f.Elements.YouTubeMiniPlayerSpeedPopup.IsOpen);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(command);
        await command;
        var applied = Assert.Single(f.Host.Commands);
        Assert.Equal(MiniPlayerCommandProvider.SetSpeedActionId, applied.Action);
        Assert.Same(item, applied.Item);
        Assert.Equal(1.5, applied.Parameter);
        Assert.False(f.Elements.YouTubeMiniPlayerSpeedPopup.IsOpen);
    });

    [Fact]
    public void RoutedVolumeSliderInputAppliesTheValueWithoutLosingItsTarget() => RunInSta(async () =>
    {
        using var list = new YouTubeMiniPlayerListViewControllerTests.Fixture();
        using var f = new Fixture();
        var slider = f.Elements.YouTubeMiniPlayerVolumeSlider;
        using var input = AttachMenuInput(list, f, f.Elements.YouTubeMiniPlayerVolumePopup, slider);
        f.Elements.YouTubeMiniPlayerVolumePopup.Closed += async (sender, args) =>
            await f.Controller.YouTubeMiniPlayerVolumePopup_Closed(sender!, args);
        var item = f.Item();
        await f.Open(item);
        RaiseMenuMouseDown(slider);
        Assert.True(f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen);
        slider.Value = 65;
        await f.Apply();
        var applied = Assert.Single(f.Host.Volumes);
        Assert.Same(item, applied.Item);
        Assert.Equal(.65, applied.Value);
        Assert.True(f.Elements.YouTubeMiniPlayerVolumePopup.IsOpen);
    });

    private static void RaiseMenuMouseDown(UIElement element)
        => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 100, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });

    private static YouTubeMiniPlayerListPopupController AttachMenuInput(
        YouTubeMiniPlayerListViewControllerTests.Fixture list, Fixture actions, Popup popup, UIElement content)
    {
        var root = new Border();
        var panel = new StackPanel();
        root.Child = panel;
        panel.Children.Add(popup);
        popup.AllowsTransparency = true;
        content.Opacity = 0;
        popup.Child = content;
        var window = list.Elements.YouTubeMiniPlayerListWindow;
        var elements = new YouTubeMiniPlayerListPopupElements(root, root, window,
            actions.Elements.YouTubeMiniPlayerAbRepeatPopup, actions.Elements.YouTubeMiniPlayerSpeedPopup,
            actions.Elements.YouTubeMiniPlayerVolumePopup, actions.Elements.YouTubeMiniPlayerItemMorePopup,
            new Popup(), new Popup(), new Popup());
        var controller = new YouTubeMiniPlayerListPopupController(window, elements, list.Controller, list.Host);
        root.PreviewMouseLeftButtonDown += controller.YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown;
        window.AttachContent(root, new ResourceDictionary());
        window.ShowOrActivate();
        return controller;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Host Host = new();
        internal readonly OwnedDispatcherTimer Timer = new(DispatcherTimerOwner.FloatingMediaHost, DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
        internal readonly YouTubeMiniPlayerListActionsElements Elements = new(
            new Popup(),
            new Popup(),
            new Popup(),
            new Popup(),
            new TextBlock(),
            new Slider { Maximum = 100 },
            new TextBlock(),
            new UniformGrid(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new Button(),
            new Button(),
            new Button(),
            new Button { Tag = "0" },
            new Button { Tag = "3" },
            new Button { Tag = "6" },
            new Button { Tag = "9" });
        internal readonly YouTubeMiniPlayerListActionsController Controller;
        internal Fixture()
        {
            Controller = new(Elements, Timer, Host);
            Controller.InitializeVolumeTimer();
            Elements.YouTubeMiniPlayerVolumeSlider.ValueChanged += Controller.YouTubeMiniPlayerVolumeSlider_ValueChanged;
        }
        internal YouTubeMiniPlayerListItem Item(double volume = .5)
            => new(CreateOwnerToken(), new YouTubeMiniPlayerState { Id = Guid.NewGuid(), Title = Guid.NewGuid().ToString(), Volume = volume });
        internal Task Open(YouTubeMiniPlayerListItem item)
            => Controller.YouTubeMiniPlayerItemVolumeButton_Click(new Button { DataContext = item }, new RoutedEventArgs(Button.ClickEvent));
        internal void Change(double value)
            => Controller.YouTubeMiniPlayerVolumeSlider_ValueChanged(Elements.YouTubeMiniPlayerVolumeSlider, new RoutedPropertyChangedEventArgs<double>(0, value));
        internal Task Apply() => (Task)Invoke("ApplyPendingYouTubeMiniPlayerListVolumeAsync")!;
        internal void Tick() => Invoke("VolumeTimer_Tick", null, EventArgs.Empty);
        private object? Invoke(string name, params object?[] args)
            => typeof(YouTubeMiniPlayerListActionsController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Controller, args);
        public void Dispose()
        {
            Controller.StopVolumeTracking();
            Elements.YouTubeMiniPlayerVolumeSlider.ValueChanged -= Controller.YouTubeMiniPlayerVolumeSlider_ValueChanged;
            Elements.YouTubeMiniPlayerVolumePopup.IsOpen = false;
            Elements.YouTubeMiniPlayerSpeedPopup.IsOpen = false;
            Elements.YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
            Elements.YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        }
    }

    private sealed class Host : IYouTubeMiniPlayerListActionsHost
    {
        internal bool CanControl = true;
        internal int EligibilityChecks;
        internal readonly List<(YouTubeMiniPlayerListItem Item, double Value)> Volumes = [];
        internal readonly List<(YouTubeMiniPlayerListItem Item, int Value)> Boosts = [];
        internal readonly List<(YouTubeMiniPlayerListItem Item, double Ratio)> Seeks = [];
        internal readonly List<(string Action, YouTubeMiniPlayerListItem Item, object? Parameter)> Commands = [];
        internal readonly List<string> Order = [];
        internal readonly List<string> Statuses = [];
        internal Func<YouTubeMiniPlayerListItem, double, Task<bool>> Volume = (_, _) => Task.FromResult(true);
        internal Func<YouTubeMiniPlayerListItem, int, Task<bool>> Boost = (_, _) => Task.FromResult(true);
        internal Func<YouTubeMiniPlayerListItem, double, Task<bool>> Seek = (_, _) => Task.FromResult(true);
        internal Action? OnCommand;
        internal readonly List<(YouTubeMiniPlayerListItem Item, bool IsOpen)> Chats = [];
        internal Action<YouTubeMiniPlayerListItem>? RefreshChat;
        internal Func<YouTubeMiniPlayerListItem, bool, Task> Chat = (_, _) => Task.CompletedTask;
        public void RefreshChatState(YouTubeMiniPlayerListItem item) => RefreshChat?.Invoke(item);
        public Task SetChatWindowOpenAsync(YouTubeMiniPlayerListItem item, bool isOpen)
        { Chats.Add((item, isOpen)); return Chat(item, isOpen); }
        public bool CanControlVolume(YouTubeMiniPlayerListItem item) { EligibilityChecks++; return CanControl; }
        public Task<bool> SetVolumeAsync(YouTubeMiniPlayerListItem item, double volume)
        { Volumes.Add((item, volume)); Order.Add("volume"); return Volume(item, volume); }
        public Task<bool> SetAudioBoostAsync(YouTubeMiniPlayerListItem item, int decibels)
        { Boosts.Add((item, decibels)); Order.Add("boost"); return Boost(item, decibels); }
        public Task<bool> SeekToRatioAsync(YouTubeMiniPlayerListItem item, double ratio)
        { Seeks.Add((item, ratio)); Order.Add("seek"); return Seek(item, ratio); }
        public void QueueSave(YouTubeMiniPlayerListItem item) => Order.Add("save");
        public void SetStatus(YouTubeMiniPlayerListItem item, string message) { Order.Add("status"); Statuses.Add(message); }
        public Task RefreshListAsync() { Order.Add("refresh"); return Task.CompletedTask; }
        public Task ExecuteCommandAsync(string actionId, YouTubeMiniPlayerListItem item, object? parameter = null)
        { Commands.Add((actionId, item, parameter)); OnCommand?.Invoke(); return Task.CompletedTask; }
    }
}
