using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;
using static TabBrowser.Tests.YouTubeMiniPlayerListViewControllerTests;

namespace TabBrowser.Tests;

[Collection("YouTubeMiniPlayerListWindow")]
public sealed class YouTubeMiniPlayerListPresentationTests
{
    [Fact]
    public void ActiveBadgeIsSeparateFromDropAndSelectionDecoration() => RunInSta(() =>
    {
        var row = LoadProductRow();
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { Title = "アクティブ表示の確認" });
        row.DataContext = item;
        var badge = Descendants(row).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerActivePlayerBadge");
        Layout(row, 740);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        item.UpdateActivePlayer(true);
        item.SetDropCue(true, false);
        Layout(row, 740);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal("アクティブ", Assert.IsType<TextBlock>(badge.Child).Text);
        Assert.Equal(Color.FromRgb(89, 98, 113), Assert.IsType<SolidColorBrush>(badge.BorderBrush).Color);
        item.UpdateActivePlayer(false);
        Layout(row, 740);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        Assert.True(item.IsDropBefore);
        return Task.CompletedTask;
    });

    [Fact]
    public void ActiveTitleIsOneLineAndReturnsAfterResultsWithoutChangingSelection() => RunInSta(async () =>
    {
        long now = 0;
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        using var window = new YouTubeMiniPlayerListWindow(() => now) { Width = 780, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, MaxHeight = 1000 };
        var bar = Descendants(content).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerListOperationStatusBar");
        var text = Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListOperationStatusText");
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.FitInitialHeight(1000);
        window.ShowActivePlayerTitle("長いタイトル" + new string('あ', 150));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(bar.IsVisible);
        Assert.Equal(window.InformationText, text.Text);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.InRange(text.ActualHeight, 1, 17);
        Assert.Contains(Descendants(bar).OfType<TextBlock>(), element => element.Text == "アクティブ");
        var tooltip = Assert.IsType<ToolTip>(text.ToolTip);
        tooltip.PlacementTarget = text;
        tooltip.Opacity = 0;
        tooltip.IsOpen = true;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(window.InformationText, Assert.IsType<TextBlock>(tooltip.Content).Text);
        tooltip.IsOpen = false;
        window.ShowOperationStatus("操作しました");
        window.ShowActivePlayerTitle("切り替え後の動画");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("操作しました", text.Text);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.Contains(Descendants(bar).OfType<TextBlock>(), element => element.Text == "操作結果");
        window.ToggleCollapsed();
        now = 5 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.False(bar.IsVisible);
        window.ToggleCollapsed();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("切り替え後の動画", text.Text);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        window.ShowActivePlayerTitle(string.Empty);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(bar.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRestoresFoldStateBeforeShowingAndKeepsSelectionAndPosition(bool isCollapsed) => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        var body = Descendants(content).OfType<FrameworkElement>().Single(element => element.Name == "YouTubeMiniPlayerListBody");
        var toolbar = Descendants(content).OfType<FrameworkElement>().Single(element => element.Name == "YouTubeMiniPlayerListToolbar");
        var list = Descendants(content).OfType<ListBox>().Single(element => element.Name == "YouTubeMiniPlayerList");
        var items = Enumerable.Range(0, 2).Select(index => new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Title = $"起動時の表示状態を確認 {index}",
            DurationSeconds = 300
        }, isRuntimeLoaded: false)).ToArray();
        list.ItemsSource = items;
        list.SelectedItems.Add(items[1]);
        using var window = new YouTubeMiniPlayerListWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, MaxHeight = 1000 };
        window.AttachContent(content, new ResourceDictionary());
        window.RestorePosition(new() { Left = 100, Top = 100 });
        window.RestoreCollapsed(isCollapsed);
        Assert.False(window.IsVisible);
        Assert.Equal(isCollapsed, window.IsCollapsed);
        Assert.Equal(isCollapsed ? Visibility.Collapsed : Visibility.Visible, body.Visibility);
        Assert.Equal(body.Visibility, toolbar.Visibility);
        if (isCollapsed) Assert.Equal(48, window.Height);
        window.ShowOrActivate();
        if (isCollapsed) Assert.InRange(window.Height, 40, 60);
        window.FitInitialHeight(1000);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(isCollapsed, window.IsCollapsed);
        if (isCollapsed) Assert.InRange(window.Height, 40, 60);
        else Assert.True(window.Height >= 180);
        var position = window.PointToScreen(new Point());
        Assert.InRange(Math.Abs(position.X - 100), 0, 1);
        Assert.InRange(Math.Abs(position.Y - 100), 0, 1);
        window.Close();
        window.ShowOrActivate();
        Assert.Equal(isCollapsed, window.IsCollapsed);
        window.ToggleCollapsed();
        Assert.Equal(!isCollapsed, window.IsCollapsed);
        Assert.Same(items, list.ItemsSource);
        Assert.Same(items[1], Assert.Single(list.SelectedItems.Cast<object>()));
        Assert.Equal(position, window.PointToScreen(new Point()));
    });

    [Theory]
    [InlineData(780)]
    [InlineData(900)]
    public void OperationResultIsVisibleOutsideTheScrollAreaAndSurvivesFolding(double width) => RunInSta(async () =>
    {
        long now = 0;
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        content.DataContext = new { OperationStatus = "本体側の別の表示値" };
        using var window = new YouTubeMiniPlayerListWindow(() => now) { Width = width, ShowActivated = false, ShowInTaskbar = false, Opacity = 0, MaxHeight = 1000 };
        var list = Descendants(content).OfType<ListBox>().Single(element => element.Name == "YouTubeMiniPlayerList");
        var bar = Descendants(content).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerListOperationStatusBar");
        var text = Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListOperationStatusText");
        var items = Enumerable.Range(0, 2).Select(index => new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Id = Guid.NewGuid(),
            Title = $"操作結果を確認する動画 {index}",
            DurationSeconds = 300,
            LastPositionSeconds = 60,
            IsPaused = false
        }, isRuntimeLoaded: true)).ToArray();
        list.ItemsSource = items;
        list.SelectedItems.Add(items[0]);
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListSummaryText").Text = "YTミニプレイヤー  2件｜再生2｜音声2";
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusSelectionText").Text = "選択 1 / 2件";
        Descendants(content).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusBar").Visibility = Visibility.Visible;
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListEmptyText").Visibility = Visibility.Collapsed;
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.FitInitialHeight(1000);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(bar.IsVisible);
        var oldHeight = window.Height;
        window.ShowOperationStatus("ライブ配信を最新位置へ移動：2件／スキップ・失敗：1件");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.FitToContentHeight();
        window.UpdateLayout();
        Assert.True(bar.IsVisible);
        Assert.Equal(window.OperationStatus, text.Text);
        Assert.Equal(Dock.Bottom, DockPanel.GetDock(bar));
        Assert.True(window.Height > oldHeight);
        Assert.True(list.TranslatePoint(new(0, list.ActualHeight), content).Y <= bar.TranslatePoint(new(), content).Y + 1);
        Assert.True(bar.TranslatePoint(new(0, bar.ActualHeight), content).Y <= content.ActualHeight);
        Assert.Equal(Color.FromRgb(19, 37, 42), Assert.IsType<SolidColorBrush>(bar.Background).Color);
        Assert.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(text));
        if (width == 780) SaveSnapshot(content, "operation-result");
        var undo = Descendants(bar).OfType<Button>().Single();
        Assert.False(undo.IsVisible);
        window.ShowOperationStatus("2件を整列しました", offerRestorePreviousArrangement: true);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Assert.True(undo.IsVisible);
        Assert.Equal("直前に戻す", undo.Content);
        Assert.True(text.TranslatePoint(new(text.ActualWidth, 0), bar).X <= undo.TranslatePoint(new(), bar).X);
        Assert.True(undo.TranslatePoint(new(undo.ActualWidth, undo.ActualHeight), bar).X <= bar.ActualWidth);
        var message = "対象を選択してください。" + new string('あ', 180);
        window.ShowOperationStatus(message);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Assert.False(undo.IsVisible);
        Assert.Equal(message, text.Text);
        var clip = Assert.IsType<Border>(text.Parent);
        Assert.InRange(clip.ActualHeight, 1, 32);
        Assert.True(clip.ClipToBounds);
        var tooltip = Assert.IsType<ToolTip>(text.ToolTip);
        tooltip.PlacementTarget = text;
        tooltip.Opacity = 0;
        tooltip.IsOpen = true;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(message, Assert.IsType<TextBlock>(tooltip.Content).Text);
        tooltip.IsOpen = false;
        window.ToggleCollapsed();
        Assert.True(window.IsCollapsed);
        Assert.False(bar.IsVisible);
        Assert.Equal(message, window.OperationStatus);
        Assert.InRange(window.Height, 40, 60);
        window.ToggleCollapsed();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(bar.IsVisible);
        Assert.Same(items, list.ItemsSource);
        Assert.Same(items[0], Assert.Single(list.SelectedItems.Cast<object>()));
        window.ToggleCollapsed();
        now = 5 * Stopwatch.Frequency;
        window.ClearExpiredOperationStatus();
        Assert.Empty(window.OperationStatus);
        Assert.True(window.IsCollapsed);
        Assert.InRange(window.Height, 40, 60);
        window.ToggleCollapsed();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(bar.IsVisible);
        Assert.InRange(Math.Abs(window.Height - oldHeight), 0, 1);
        Assert.Same(items[0], Assert.Single(list.SelectedItems.Cast<object>()));
    });

    [Theory]
    [InlineData(760)]
    [InlineData(900)]
    public void CaptionWithoutHoverPreviewButtonKeepsItsCommandsVisible(double width) => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerListHeader", out var document);
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerHoverPreviewButton");
        Layout(grid, width);
        var buttons = Descendants(grid).OfType<Button>().Where(button => button.Name is
            "YouTubeMiniPlayerListTopmostButton" or "YouTubeMiniPlayerListMinimizeButton" or "YouTubeMiniPlayerListCloseButton").ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, button =>
        {
            var left = button.TranslatePoint(new(), grid).X;
            Assert.InRange(left, 0, width);
            Assert.True(left + button.ActualWidth <= width);
            Assert.Equal(30, button.ActualHeight);
        });
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, null, true, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, null, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, true, false)]
    public void ChatMenuExplainsAvailabilityAndTracksOpenState(bool ready, bool? available, bool open, bool local, bool canToggle) => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerItemMorePopup", out _);
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Id = Guid.NewGuid(),
            Title = "チャットの対象動画",
            SourceKind = local ? MiniPlayerSourceKind.LocalFile : MiniPlayerSourceKind.YouTube
        });
        item.UpdateChatState(ready, available, open);
        grid.DataContext = item;
        Layout(grid, 280);
        var button = (Button)grid.FindName("YouTubeMiniPlayerItemChatButton");
        Assert.Equal(canToggle, button.IsEnabled);
        Assert.Equal(open ? "チャットを閉じる" : "チャットを開く", button.Content);
        Assert.False(string.IsNullOrWhiteSpace((string)button.ToolTip));
        Assert.True(ToolTipService.GetShowOnDisabled(button));
        if (ready && available == true && !open && !local) SaveSnapshot(grid, "chat-menu");
        item.UpdateChatState(true, null, true);
        Layout(grid, 280);
        Assert.Equal(!local, button.IsEnabled);
        Assert.Equal("チャットを閉じる", button.Content);
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductGlobalMenuUsesTwoColumnsWithDescriptionsCountsAndStateChanges() => RunInSta(() =>
    {
        var menu = LoadProductPart("YouTubeMiniPlayerGlobalActionsPopup", out _);
        var root = (Border)menu.FindName("YouTubeMiniPlayerGlobalActionsRoot");
        Assert.True(root.Focusable);
        Assert.Equal(System.Windows.Input.KeyboardNavigationMode.Cycle, System.Windows.Input.KeyboardNavigation.GetTabNavigation(root));
        root.DataContext = new YouTubeMiniPlayerGlobalActionsState(6, 4, 2, false, false, false);
        Layout(menu, 680);
        var play = (Button)menu.FindName("YouTubeMiniPlayerPlayPauseAllButton");
        var pause = (Button)menu.FindName("YouTubeMiniPlayerPauseAllButton");
        var live = (Button)menu.FindName("YouTubeMiniPlayerLiveEdgeAllButton");
        var mute = (Button)menu.FindName("YouTubeMiniPlayerMuteAllToggleButton");
        var minimize = (Button)menu.FindName("YouTubeMiniPlayerMinimizeRestoreAllButton");
        var close = (Button)menu.FindName("YouTubeMiniPlayerCloseAllButton");
        Assert.Equal(10, Descendants(menu).OfType<Button>().Count());
        Assert.All(Descendants(menu).OfType<Button>(), button =>
        {
            Assert.True(button.BorderThickness.Left > 0);
            Assert.True(((SolidColorBrush)button.BorderBrush).Color.A > 0);
        });
        Assert.Equal(play.TranslatePoint(new(), menu).X, live.TranslatePoint(new(), menu).X);
        Assert.True(mute.TranslatePoint(new(), menu).X > play.TranslatePoint(new(play.ActualWidth, 0), menu).X);
        Assert.True(pause.TranslatePoint(new(), menu).Y > play.TranslatePoint(new(), menu).Y);
        Assert.True(close.TranslatePoint(new(), menu).Y > live.TranslatePoint(new(), menu).Y);
        Assert.True(close.ActualWidth > play.ActualWidth * 1.8);
        var texts = Descendants(menu).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("対象：全 6件（チェックすると選択分だけ）", texts);
        Assert.Contains("一時操作（解除で元に戻す）", texts);
        Assert.Contains("読込済みの動画をまとめて再生", texts);
        Assert.Contains("配信中のライブを現在の配信位置へ", texts);
        Assert.Contains("4件", Descendants(play).OfType<TextBlock>().Select(text => text.Text));
        Assert.Contains("2件", Descendants(live).OfType<TextBlock>().Select(text => text.Text));
        Assert.Equal(Color.FromRgb(252, 165, 165), ((SolidColorBrush)close.Foreground).Color);
        SaveSnapshot(menu, "global-actions");
        root.DataContext = new YouTubeMiniPlayerGlobalActionsState(3, 1, 0, true, true, true);
        Layout(menu, 680);
        texts = Descendants(menu).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("すべてミュート解除", texts);
        Assert.Contains("すべて最小化解除", texts);
        Assert.Contains("ライブ位置へ移動中…", texts);
        root.DataContext = new YouTubeMiniPlayerGlobalActionsState(2, 1, 0, false, false, false, SelectedCount: 2);
        Layout(menu, 680);
        texts = Descendants(menu).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("対象：選択 2件（チェックした動画だけ）", texts);
        Assert.Contains("選択を再生", texts);
        Assert.Contains("選択を一時停止", texts);
        Assert.Contains("選択を閉じる", texts);
        SaveSnapshot(menu, "selected-actions");
        root.MaxWidth = 460;
        root.MaxHeight = 200;
        Layout(menu, 460);
        var scroll = Assert.Single(Descendants(root).OfType<ScrollViewer>());
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.True(root.ActualWidth <= 460 && root.ActualHeight <= 200);
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductRowShowsOnlyEnabledRepeatSettingsAndTracksPauseAndLoadState() => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Title = "基本フレーズ", LastPositionSeconds = 20, DurationSeconds = 60, IsPaused = true };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 740);
        var badge = (Border)row.FindName("YouTubeMiniPlayerRepeatStateBadge");
        var text = (TextBlock)badge.Child;
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        state.IsLooping = true;
        Refresh();
        Assert.Equal("ループ有効", text.Text);
        Assert.Equal(Visibility.Visible, badge.Visibility);
        SaveSnapshot(row, "repeat-loop");
        state.IsAbRepeatEnabled = true;
        state.AbRepeatStartSeconds = 12;
        state.AbRepeatEndSeconds = 34;
        state.IsPaused = false;
        Refresh();
        Assert.Equal("A-B再生中 0:12〜0:34", text.Text);
        Assert.True(text.DesiredSize.Width <= text.ActualWidth + 0.01);
        SaveSnapshot(row, "repeat-ab");
        state.IsPaused = true;
        Refresh();
        Assert.Equal("A-B有効 0:12〜0:34", text.Text);
        state.IsPaused = false;
        Refresh(false);
        Assert.Equal("A-B有効 0:12〜0:34", text.Text);
        state.SourceKind = MiniPlayerSourceKind.LocalFile;
        Refresh();
        Assert.Equal("A-B再生中 0:12〜0:34", text.Text);
        state.IsAbRepeatEnabled = false;
        Refresh();
        Assert.Equal("ループ有効", text.Text);
        state.IsLooping = false;
        Refresh();
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        state.IsAbRepeatEnabled = true;
        state.AbRepeatEndSeconds = double.NaN;
        Refresh();
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        return Task.CompletedTask;

        void Refresh(bool loaded = true)
        {
            item.UpdateRuntimeState(null, loaded && state.SourceKind == MiniPlayerSourceKind.LocalFile, false, loaded, null, false, false);
            Layout(row, 740);
        }
    });

    [Theory]
    [InlineData(740, false, MiniPlayerSourceKind.YouTube)]
    [InlineData(780, false, MiniPlayerSourceKind.YouTube)]
    [InlineData(740, true, MiniPlayerSourceKind.YouTube)]
    [InlineData(780, true, MiniPlayerSourceKind.YouTube)]
    [InlineData(740, false, MiniPlayerSourceKind.LocalFile)]
    [InlineData(780, false, MiniPlayerSourceKind.LocalFile)]
    public void ProductRowTitleSpansTheSpaceAboveActionsWithoutOverlap(double width, bool live, MiniPlayerSourceKind sourceKind) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState
        {
            Title = "初心者向けギター講座　コードチェンジと右手のリズムをゆっくり練習",
            SourceKind = sourceKind,
            IsLiveBroadcast = live,
            IsPaused = true,
            LastPositionSeconds = 20,
            DurationSeconds = 180,
            IsAbRepeatEnabled = true,
            AbRepeatStartSeconds = 12,
            AbRepeatEndSeconds = 34
        };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, width);
        var title = (TextBlock)row.FindName("YouTubeMiniPlayerListItemTitleText");
        var details = (StackPanel)row.FindName("YouTubeMiniPlayerListItemDetails");
        var actions = (StackPanel)row.FindName("YouTubeMiniPlayerListItemActions");
        Assert.Equal(2, Grid.GetColumnSpan(title));
        Assert.Equal(14, title.FontSize);
        Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
        Assert.True(title.ActualHeight < 28);
        var titleWidth = title.ActualWidth;
        Assert.True(titleWidth >= details.ActualWidth + actions.ActualWidth);
        Assert.Equal(title.TranslatePoint(new(), row).X, details.TranslatePoint(new(), row).X, 3);
        var titleRight = title.TranslatePoint(new(titleWidth, 0), row).X;
        var actionsRight = actions.TranslatePoint(new(actions.ActualWidth, 0), row).X;
        Assert.InRange(titleRight - actionsRight, -3, 1);
        AssertBelowTitle();
        SaveSnapshot(row, $"title-wide-{width}-{live}-{sourceKind}");
        foreach (var changedTitle in new[] { "短いタイトル", new string('長', 200) })
        {
            state.Title = changedTitle;
            item.UpdateRuntimeState(null, sourceKind == MiniPlayerSourceKind.LocalFile, false, true, null, false, false);
            Layout(row, width);
            Assert.Equal(changedTitle, title.Text);
            Assert.Equal(changedTitle, title.ToolTip);
            Assert.Equal(titleWidth, title.ActualWidth, 3);
            Assert.True(title.ActualHeight < 28);
            AssertBelowTitle();
        }
        return Task.CompletedTask;

        void AssertBelowTitle()
        {
            var bottom = title.TranslatePoint(new(0, title.ActualHeight), row).Y;
            Assert.True(details.TranslatePoint(new(), row).Y >= bottom);
            Assert.True(actions.TranslatePoint(new(), row).Y >= bottom);
            Assert.Equal(364, actions.ActualWidth, 3);
            Assert.True(actions.TranslatePoint(new(actions.ActualWidth, actions.ActualHeight), row).Y <= row.ActualHeight);
            Assert.True(actionsRight <= row.ActualWidth);
        }
    });

    [Fact]
    public void ProductRowShowsProgressAndRemainingMediaTimeAndTracksUpdates() => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Title = "進捗表示の確認", LastPositionSeconds = 754, DurationSeconds = 2900, IsPaused = true, PlaybackRate = 2 };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 740);
        var progress = Assert.Single(Descendants(row).OfType<ProgressBar>());
        Assert.True(IsShown(progress));
        Assert.Equal(754d / 2900, progress.Value, 6);
        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == "残り 35:46");
        Assert.False(progress.IsHitTestVisible);
        var seek = (Button)row.FindName("YouTubeMiniPlayerProgressSeekButton");
        Assert.True(seek.IsEnabled);
        Assert.True(seek.IsHitTestVisible);
        Assert.False(seek.Focusable);
        Assert.Equal(16, seek.ActualHeight);
        Assert.True(((SolidColorBrush)((ToolTip)seek.ToolTip).Background).Color.R < 80);
        var hit = VisualTreeHelper.HitTest(row, seek.TranslatePoint(new(seek.ActualWidth / 2, seek.ActualHeight / 2), row))!.VisualHit;
        while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
        Assert.Same(seek, hit);
        var track = (FrameworkElement)progress.Template.FindName("PART_Track", progress);
        var indicator = (FrameworkElement)progress.Template.FindName("PART_Indicator", progress);
        Assert.Equal(track.ActualWidth * progress.Value, indicator.ActualWidth, 4);
        SaveSnapshot(row, "progress");

        state.LastPositionSeconds = 1450;
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        Layout(row, 740);
        Assert.Equal(.5, progress.Value);
        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == "残り 24:10");
        // 速度2倍でも残り表示は動画時間。再生中の既存の位置補間にも追従する。
        state.IsPaused = false;
        state.PlaybackStateObservedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        Assert.InRange(item.PlaybackProgress, 1470d / 2900, 1472d / 2900);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(null, 60d, true, false)]
    [InlineData(double.NaN, 60d, true, false)]
    [InlineData(double.PositiveInfinity, 60d, true, false)]
    [InlineData(-1d, 60d, true, false)]
    [InlineData(10d, null, true, false)]
    [InlineData(10d, 0d, true, false)]
    [InlineData(10d, -1d, true, false)]
    [InlineData(10d, double.NaN, true, false)]
    [InlineData(10d, double.PositiveInfinity, true, false)]
    [InlineData(10d, 60d, false, false)]
    [InlineData(10d, 60d, true, true)]
    public void ProgressIsHiddenWithoutUsableLoadedVideoTime(double? position, double? duration, bool loaded, bool live) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { LastPositionSeconds = position, DurationSeconds = duration, IsLiveBroadcast = live };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: loaded);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 740);
        Assert.Equal(Visibility.Collapsed, item.PlaybackProgressVisibility);
        Assert.False(item.CanSeekPlaybackProgress);
        Assert.False(IsShown(Assert.Single(Descendants(row).OfType<ProgressBar>())));
        Assert.Equal(string.Empty, item.RemainingTimeText);
        Assert.Equal(0, item.PlaybackProgress);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0d, 0d, "残り 1:00")]
    [InlineData(60d, 1d, "残り 0:00")]
    [InlineData(90d, 1d, "残り 0:00")]
    public void ProgressHandlesStartEndAndStalePosition(double position, double fraction, string remaining) => RunInSta(() =>
    {
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { LastPositionSeconds = position, DurationSeconds = 60, IsPaused = true }, isRuntimeLoaded: true);
        Assert.Equal(fraction, item.PlaybackProgress);
        Assert.Equal(remaining, item.RemainingTimeText);
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductLiveLabelDistinguishesUnknownEdgeAndTracksBothKnownStates() => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Title = "ライブ状態の確認", IsLiveBroadcast = true };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 740);
        var label = Descendants(row).OfType<TextBlock>().Single(text => text.Text == "ライブ");
        var button = Descendants(row).OfType<Button>().Single(button => Equals(button.Content, "ライブへ"));
        Assert.True(IsShown(button));
        foreach (var atEdge in new[] { true, false })
        {
            YouTubeMiniPlayerStateService.ApplyScriptResult(state, atEdge ? "{\"isAtLiveEdge\":true}" : "{\"isAtLiveEdge\":false}");
            item.UpdateRuntimeState(null, false, false, true, null, false, false);
            Layout(row, 740);
            Assert.Equal(atEdge ? "ライブ端" : "ライブ端以外", label.Text);
            Assert.Equal(!atEdge, button.IsEnabled);
            Assert.Equal(atEdge ? Color.FromRgb(74, 222, 128) : Color.FromRgb(250, 204, 21), ((SolidColorBrush)label.Foreground).Color);
            SaveSnapshot(row, atEdge ? "live-edge" : "live-away");
        }
        item.UpdateRuntimeState(null, false, false, false, null, false, false);
        Layout(row, 740);
        Assert.False(IsShown(button));
        Assert.False(IsShown(label));
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductRepairStaysBesideDiagnosticAndOnlyUsesExistingFailureConditions() => RunInSta(() =>
    {
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { Title = "表示診断の確認" }, isRuntimeLoaded: true);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 740);
        var button = Descendants(row).OfType<Button>().Single(button => Equals(button.Content, "表示修復"));
        Assert.False(IsShown(button));
        item.UpdateRuntimeState(new(YouTubeMiniPlayerDisplayHealthState.Suspected, 1, DateTimeOffset.UtcNow), false, false, true, null, false, false);
        Layout(row, 740);
        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == "表示確認中" && IsShown(text));
        Assert.False(IsShown(button));
        item.UpdateRuntimeState(new(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended, 2, DateTimeOffset.UtcNow), false, false, true, null, false, false);
        Layout(row, 740);
        var label = Descendants(row).OfType<TextBlock>().Single(text => text.Text == "表示異常");
        Assert.True(IsShown(button));
        Assert.Same(((Border)label.Parent).Parent, button.Parent);
        var labelRight = label.TranslatePoint(new(label.ActualWidth, 0), row).X;
        Assert.InRange(button.TranslatePoint(new(), row).X - labelRight, 0, 15);
        SaveSnapshot(row, "repair");
        item.State.SourceKind = MiniPlayerSourceKind.LocalFile;
        item.UpdateRuntimeState(null, false, true, false, null, false, false);
        Layout(row, 740);
        Assert.True(IsShown(button));
        Assert.Equal("読込失敗", label.Text);
        return Task.CompletedTask;
    });

    private static Grid LoadProductRow()
    {
        LoadProductPart("YouTubeMiniPlayerSpeedPopup", out var document);
        var list = document.Descendants(P + "ListBox").Single(element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerList");
        return LoadElement(document, list.Element(P + "ListBox.ItemTemplate")!.Element(P + "DataTemplate")!.Elements().Single());
    }

    // 画面へ接続しない描画なので、祖先を含むVisibilityを確認する。
    private static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        }
        return true;
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(.5)]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ProductSpeedMenuHighlightsOnlyTheCurrentRateAndTracksUpdates(double rate) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Title = "速度確認用の動画", PlaybackRate = rate };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        var grid = LoadProductPart("YouTubeMiniPlayerSpeedPopup", out var document);
        grid.DataContext = item;
        Layout(grid, 300);
        var buttons = Descendants(grid).OfType<Button>().ToArray();
        Assert.Equal(8, buttons.Length);
        var selected = Assert.Single(buttons, button => button.BorderThickness.Left == 2);
        Assert.Equal(rate.ToString(CultureInfo.InvariantCulture), selected.Tag);
        Assert.Equal(Color.FromRgb(34, 211, 238), ((SolidColorBrush)selected.BorderBrush).Color);
        Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.Text == $"現在 {item.PlaybackRateText}");
        Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.Text == $"対象：{item.Title}");

        // 実際の行ボタンも、文化設定と既存の状態更新にかかわらず同じ現在値を表示する。
        var rowButton = document.Descendants(P + "Button").Single(element =>
            (string?)element.Attribute("Tag") == "YouTubeMiniPlayerSpeedPopupTrigger");
        var row = LoadElement(document, rowButton);
        row.DataContext = item;
        Layout(row, 60);
        Assert.Equal(item.PlaybackRateText, Assert.Single(Descendants(row).OfType<Button>()).Content);
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            state.PlaybackRate = rate == 1.25 ? 1.75 : 1.25;
            state.Title = "変更後の動画";
            item.UpdateRuntimeState(null, false, false, true, null, false, false);
            Layout(grid, 300);
            Layout(row, 60);
            selected = Assert.Single(buttons, button => button.BorderThickness.Left == 2);
            Assert.Equal(state.PlaybackRate.Value.ToString(CultureInfo.InvariantCulture), selected.Tag);
            Assert.Equal(item.PlaybackRateText, Assert.Single(Descendants(row).OfType<Button>()).Content);
            Assert.Contains(Descendants(grid).OfType<TextBlock>(), text => text.Text == "対象：変更後の動画");
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        if (rate == 1.25) SaveSnapshot(grid, "speed");
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownOrInvalidRateDisplaysNormalSpeed(double? rate) => RunInSta(() =>
    {
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { PlaybackRate = rate });
        Assert.Equal(1, item.PlaybackRate);
        Assert.Equal("1x", item.PlaybackRateText);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public void ClosedHistoryPopupShowsSavedPositionsAndFitsItsHeightLimit(int count) => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerClosedHistoryPopup", out var document);
        var declaration = document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerClosedHistoryPopup");
        Assert.Equal("YouTubeMiniPlayerClosedHistoryPopup_Closed", (string?)declaration.Attribute("Closed"));
        var list = (ItemsControl)grid.FindName("YouTubeMiniPlayerClosedHistoryList");
        var owner = CreateOwnerToken();
        list.ItemsSource = Enumerable.Range(0, count).Select(index => new YouTubeMiniPlayerListItem(owner, new()
        {
            Id = Guid.NewGuid(),
            Title = index == 0 ? "夕暮れの港と船の音" : $"閉じた動画の長いタイトルを確認するための動画 {index}",
            LastPositionSeconds = 754,
            DurationSeconds = 1800,
            IsPaused = false,
            PlaybackStateObservedAt = DateTimeOffset.UtcNow.AddHours(-1)
        })).ToArray();
        Layout(grid, 360);
        var root = Descendants(grid).OfType<Border>().First();
        Assert.Equal(360, root.ActualWidth);
        Assert.InRange(root.ActualHeight, 100, 420);
        var buttons = Descendants(list).OfType<Button>().ToArray();
        Assert.Equal(count, buttons.Length);
        foreach (var button in buttons)
        {
            Assert.True(button.ActualHeight >= 62);
            var texts = Descendants(button).OfType<TextBlock>().ToArray();
            Assert.Contains(texts, text => text.Text == "再生位置 12:34");
            var title = texts.Single(text => text.Text == ((YouTubeMiniPlayerListItem)button.DataContext).Title);
            Assert.InRange(title.ActualHeight, 1, 36);
            Assert.True(title.TranslatePoint(new(), root).X + title.ActualWidth <= root.ActualWidth);
        }
        var scroll = Descendants(grid).OfType<ScrollViewer>().Single();
        Assert.Equal(count == 20 ? Visibility.Visible : Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
        SaveSnapshot(grid, count == 3 ? "closed-history" : "closed-history-scroll");
        return Task.CompletedTask;
    });

    [Fact]
    public void RestoreAndAllMainActionsHaveEqualWidthsOnOneRow() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerListHeader", out _);
        Layout(grid, 760);
        var names = new[] { "YouTubeMiniPlayerArrangeMenuButton", "YouTubeMiniPlayerGlobalActionsButton" };
        var actions = Descendants(grid).OfType<Button>().Where(button => names.Contains(button.Name)
            || Equals(button.Content, "動画追加")).ToArray();
        Assert.Equal(3, actions.Length);
        Assert.All(actions, button => Assert.Equal(actions[0].ActualWidth, button.ActualWidth, 5));
        var height = grid.ActualHeight;
        var restore = (Button)grid.FindName("YouTubeMiniPlayerRestoreClosedButton");
        var history = (Button)grid.FindName("YouTubeMiniPlayerClosedHistoryButton");
        Assert.Equal(Visibility.Collapsed, history.Visibility);
        restore.Visibility = Visibility.Visible;
        restore.IsEnabled = true;
        Layout(grid, 760);
        Assert.Equal(height, grid.ActualHeight);
        Assert.Equal(30, restore.ActualHeight);
        Assert.True(history.IsEnabled);
        Assert.Equal(Visibility.Visible, history.Visibility);
        Assert.Equal(28, history.ActualWidth);
        Assert.Equal(restore.TranslatePoint(new(), grid).X + restore.ActualWidth,
            history.TranslatePoint(new(), grid).X + history.ActualWidth, 5);
        Assert.Equal("閉じた動画を選んで復元", System.Windows.Automation.AutomationProperties.GetName(history));
        restore.IsEnabled = false;
        Layout(grid, 760);
        Assert.False(history.IsEnabled);
        restore.IsEnabled = true;
        Layout(grid, 760);
        Assert.All(actions, button =>
        {
            Assert.Equal(restore.TranslatePoint(new(), grid).Y, button.TranslatePoint(new(), grid).Y);
            Assert.Equal(restore.ActualWidth, button.ActualWidth, 5);
            Assert.True(button.ActualWidth >= 95);
        });
        var caption = new FormattedText((string)restore.Content, CultureInfo.CurrentUICulture,
            restore.FlowDirection, new Typeface(restore.FontFamily, restore.FontStyle, restore.FontWeight, restore.FontStretch),
            restore.FontSize, restore.Foreground, VisualTreeHelper.GetDpi(restore).PixelsPerDip);
        Assert.True(caption.WidthIncludingTrailingWhitespace + restore.Padding.Left + restore.Padding.Right
            + restore.BorderThickness.Left + restore.BorderThickness.Right <= restore.ActualWidth);
        var ordered = actions.Append(restore).OrderBy(button => button.TranslatePoint(new(), grid).X).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(ordered[index - 1].TranslatePoint(new(), grid).X + ordered[index - 1].ActualWidth
                <= ordered[index].TranslatePoint(new(), grid).X);
        }
        Assert.True(ordered[^1].TranslatePoint(new(), grid).X + ordered[^1].ActualWidth <= grid.ActualWidth);
        var close = (Button)grid.FindName("YouTubeMiniPlayerListCloseButton");
        var dragHandle = (FrameworkElement)grid.FindName("YouTubeMiniPlayerListWindowDragArea");
        Assert.Equal("×", close.Content);
        Assert.Equal("一覧を閉じる", System.Windows.Automation.AutomationProperties.GetName(close));
        Assert.Equal(30, close.ActualWidth);
        Assert.Equal(30, close.ActualHeight);
        var closeOrigin = close.TranslatePoint(new(), grid);
        var dragOrigin = dragHandle.TranslatePoint(new(), grid);
        Assert.True(dragOrigin.X + dragHandle.ActualWidth <= closeOrigin.X);
        Assert.True(closeOrigin.X + close.ActualWidth <= grid.ActualWidth);
        Assert.True(closeOrigin.Y + close.ActualHeight <= ordered[0].TranslatePoint(new(), grid).Y);
        SaveSnapshot(grid, "toolbar");
        return Task.CompletedTask;
    });

    [Fact]
    public void ArrangementDestinationChoicesFitTheDarkMenuWithoutClipping() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerArrangePopup", out _);
        Layout(grid, 420);
        var combo = Descendants(grid).OfType<ComboBox>().Single(element => element.Name == "YouTubeMiniPlayerArrangeAreaComboBox");
        Assert.Equal(new[] { "ディスプレイ全体", "空きスペース", "指定した範囲" }, combo.Items.OfType<ComboBoxItem>().Select(item => item.Content));
        Assert.Equal(0, combo.SelectedIndex);
        Assert.True(((SolidColorBrush)combo.Background).Color.R < 80);
        var button = Descendants(grid).OfType<Button>().Single(element => element.Name == "YouTubeMiniPlayerArrangeRegionButton");
        Assert.False(button.IsEnabled);
        Assert.Equal("範囲を指定", button.Content);
        Assert.True(button.TranslatePoint(new(button.ActualWidth, 0), grid).X <= grid.ActualWidth);
        Assert.True(combo.ActualWidth > 200);
        return Task.CompletedTask;
    });

    [Fact]
    public void ArrangementFailureFitsBelowTheButtonsWithoutDisablingRetry() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerArrangePopup", out _);
        var panel = Descendants(grid).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerArrangeFailurePanel");
        var text = Assert.IsType<TextBlock>(panel.Child);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Layout(grid, 420);
        var heightWithoutMessage = grid.DesiredSize.Height;
        text.Text = "配置不可：横一列 3（下段）\n全件を配置できる空きスペースがありません。対象を減らすか範囲を指定してください";
        panel.Visibility = Visibility.Visible;
        Layout(grid, 420);

        var button = Descendants(grid).OfType<Button>().Single(element => Equals(element.Content, "ランダム配置＋再生位置"));
        Assert.True(button.IsEnabled);
        Assert.True(panel.TranslatePoint(new(0, 0), grid).Y >= button.TranslatePoint(new(0, button.ActualHeight), grid).Y);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.True(panel.ActualHeight > 38);
        Assert.True(panel.TranslatePoint(new(panel.ActualWidth, panel.ActualHeight), grid).Y <= grid.ActualHeight);
        Assert.InRange(grid.DesiredSize.Height - heightWithoutMessage, 38, 72);
        Assert.True(((SolidColorBrush)panel.Background).Color.R < 80);
        return Task.CompletedTask;
    });

    [Fact]
    public void SevenArrangementDiagramsLoadWithExistingCommandsAndDarkControls() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerArrangePopup", out var document);
        Layout(grid, 420);
        var images = Descendants(grid).OfType<Image>().ToArray();
        Assert.Equal(7, images.Length);
        Assert.All(images, image => Assert.IsType<DrawingImage>(image.Source));
        var popup = document.Descendants(P + "Popup").Single(element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerArrangePopup");
        var commands = popup.Descendants(P + "Button").Where(element => element.Descendants(P + "Image").Any()).ToArray();
        Assert.Equal(7, commands.Select(element => (string?)element.Attribute("Click")).Distinct().Count());
        Assert.Equal("YouTubeMiniPlayerLinearPlacementPopupTrigger", (string?)commands.Single(element =>
            (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerArrangeHorizontalButton").Attribute("Tag"));
        Assert.All(Descendants(grid).OfType<Button>(), button => Assert.True(((SolidColorBrush)button.Background).Color.R < 80));
        SaveSnapshot(grid, "arrangement");

        var root = Assert.IsType<Border>(Assert.Single(grid.Children));
        var preview = Assert.IsType<Border>(grid.FindName("YouTubeMiniPlayerArrangementPreviewBar"));
        var previous = Assert.IsType<Button>(grid.FindName("YouTubeMiniPlayerRestorePreviousArrangementButton"));
        var initial = Assert.IsType<Button>(grid.FindName("YouTubeMiniPlayerRestoreArrangementButton"));
        root.MaxHeight = 320;
        preview.Visibility = Visibility.Visible;
        previous.Visibility = Visibility.Visible;
        initial.Visibility = Visibility.Visible;
        Layout(grid, 420);

        var scroll = Assert.Single(Descendants(root).OfType<ScrollViewer>());
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.InRange(root.ActualHeight, 1, root.MaxHeight);
        var scrollBottom = scroll.TranslatePoint(new(0, scroll.ActualHeight), root).Y;
        var previewButtons = Descendants(preview).OfType<Button>().ToArray();
        Assert.Equal(4, previewButtons.Length);
        Assert.All(previewButtons, button =>
        {
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.ActualHeight > 0);
            Assert.InRange(button.TranslatePoint(new(), root).Y, scrollBottom, root.ActualHeight);
            Assert.InRange(button.TranslatePoint(new(0, button.ActualHeight), root).Y, scrollBottom, root.ActualHeight);
        });
        Assert.Equal("直前の配置に戻す", previous.Content);
        Assert.Equal("最初の整列前へ戻す", initial.Content);
        var previousRight = previous.TranslatePoint(new(previous.ActualWidth, 0), root);
        var initialLeft = initial.TranslatePoint(new(), root);
        Assert.Equal(previousRight.Y, initialLeft.Y);
        Assert.True(previousRight.X <= initialLeft.X);
        Assert.InRange(initialLeft.Y, preview.TranslatePoint(new(0, preview.ActualHeight), root).Y, root.ActualHeight);
        Assert.InRange(initial.TranslatePoint(new(0, initial.ActualHeight), root).Y, initialLeft.Y + 1, root.ActualHeight);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube, false, false, false)]
    [InlineData(MiniPlayerSourceKind.YouTube, true, false, true)]
    [InlineData(MiniPlayerSourceKind.LocalFile, true, false, false)]
    [InlineData(MiniPlayerSourceKind.LocalFile, false, true, false)]
    [InlineData(MiniPlayerSourceKind.LocalFile, true, true, true)]
    public void PlaybackControlsFollowTheSameLoadConditionAsThePrimaryButton(MiniPlayerSourceKind kind, bool runtimeLoaded, bool localLoaded, bool enabled) => RunInSta(() =>
    {
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { SourceKind = kind, IsLiveBroadcast = true });
        var notifications = new List<string?>();
        item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        item.UpdateRuntimeState(null, localLoaded, false, runtimeLoaded, null, false, false);
        Assert.Equal(enabled, item.CanControlPlayback);
        Assert.Equal(!enabled, item.PrimaryPlaybackButtonText == "読込");
        Assert.Contains(nameof(item.CanControlPlayback), notifications);
        LoadProductPart("YouTubeMiniPlayerSpeedPopup", out var document);
        var volume = document.Descendants(P + "Button").Single(element => (string?)element.Attribute("Tag") == "YouTubeMiniPlayerVolumePopupTrigger");
        var controls = LoadElement(document, volume.Parent!);
        controls.DataContext = item;
        Layout(controls, 364);
        var buttons = Descendants(controls).OfType<Button>().Where(button => Equals(button.Content, "-10秒") || Equals(button.Content, "+10秒") || Equals(button.Tag, "YouTubeMiniPlayerSpeedPopupTrigger")).ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.Equal(enabled, button.IsEnabled);
            Assert.True(ToolTipService.GetShowOnDisabled(button));
            if (!enabled) Assert.Equal("読込後に操作できます", button.ToolTip);
        });
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true, .8, "ミュート", "80%")]
    [InlineData(false, .8, "80%", "80%")]
    [InlineData(true, null, "ミュート", "100%")]
    [InlineData(false, null, "100%", "100%")]
    public void VolumeButtonDistinguishesMuteWithoutChangingStoredVolume(bool muted, double? volume, string buttonText, string volumeText) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Volume = volume, IsMuted = muted };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        Assert.Equal(buttonText, item.VolumeButtonText);
        Assert.Equal(volumeText, item.VolumeText);
        Assert.Equal(volume, state.Volume);
        var notifications = new List<string?>();
        item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        state.IsMuted = !muted;
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        Assert.Contains(nameof(item.VolumeButtonText), notifications);
        Assert.Equal(muted ? volumeText : "ミュート", item.VolumeButtonText);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(null, "100%")]
    [InlineData(.65, "65%")]
    [InlineData(0d, "0%")]
    [InlineData(1d, "100%")]
    [InlineData(-.1, "0%")]
    [InlineData(1.1, "100%")]
    [InlineData(double.NaN, "--%")]
    [InlineData(double.PositiveInfinity, "--%")]
    public void VolumeDisplayPreservesStoredSettingWhileMuted(double? volume, string expected) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { Volume = volume, IsMuted = true };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        Assert.Equal(expected, item.VolumeText);
        Assert.Contains("ミュート中", item.VolumeToolTip);
        Assert.Equal(volume, state.Volume);
        Assert.True(state.IsMuted);
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductPlaybackAndVolumeButtonsShowAndRefreshWithoutClipping() => RunInSta(() =>
    {
        LoadProductPart("YouTubeMiniPlayerSpeedPopup", out var document);
        var volume = document.Descendants(P + "Button").Single(element =>
            (string?)element.Attribute("Tag") == "YouTubeMiniPlayerVolumePopupTrigger");
        var controls = LoadElement(document, volume.Parent!);
        var state = new YouTubeMiniPlayerState { Volume = .65, IsMuted = false, IsPaused = true, PlaybackRate = 1.25 };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state, isRuntimeLoaded: true);
        controls.DataContext = item;
        Layout(controls, 364);
        var button = Descendants(controls).OfType<Button>().Single(button => Equals(button.Tag, "YouTubeMiniPlayerVolumePopupTrigger"));
        Assert.Equal("65%", button.Content);
        SaveSnapshot(controls, "volume");
        var playback = Descendants(controls).OfType<Button>().Single(button => Equals(button.Content, "再生"));
        var playBackground = ((SolidColorBrush)playback.Background).Color;
        var playBorder = ((SolidColorBrush)playback.BorderBrush).Color;
        AssertPlaybackFits("再生");
        SaveSnapshot(controls, "playback-play");
        state.IsPaused = false;
        state.Volume = .35;
        state.IsMuted = true;
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        Layout(controls, 364);
        Assert.Equal("ミュート", button.Content);
        var volumeLabel = Assert.Single(Descendants(button).OfType<TextBlock>());
        Assert.Equal("ミュート", volumeLabel.Text);
        Assert.True(volumeLabel.DesiredSize.Width <= volumeLabel.ActualWidth + 0.01);
        Assert.True(volumeLabel.TranslatePoint(new(volumeLabel.ActualWidth, 0), button).X <= button.ActualWidth - 2);
        Assert.Contains("ミュート中", Assert.IsType<string>(button.ToolTip));
        AssertPlaybackFits("一時停止");
        Assert.NotEqual(playBackground, ((SolidColorBrush)playback.Background).Color);
        Assert.NotEqual(playBorder, ((SolidColorBrush)playback.BorderBrush).Color);
        Assert.Contains("クリックで一時停止", Assert.IsType<string>(playback.ToolTip));
        SaveSnapshot(controls, "playback-pause");
        state.SourceKind = MiniPlayerSourceKind.LocalFile;
        item.UpdateRuntimeState(null, false, false, false, null, false, false);
        Layout(controls, 344);
        AssertPlaybackFits("読込");
        Assert.Contains("読み込んで再生", Assert.IsType<string>(playback.ToolTip));
        SaveSnapshot(controls, "playback-load");
        item.UpdateRuntimeState(null, true, false, true, null, false, false);
        Layout(controls, 344);
        AssertPlaybackFits("一時停止");
        state.IsPaused = null;
        item.UpdateRuntimeState(null, true, false, true, null, false, false);
        Layout(controls, 344);
        AssertPlaybackFits("再生");
        return Task.CompletedTask;

        void AssertPlaybackFits(string text)
        {
            Assert.Equal(text, playback.Content);
            Assert.Equal(66, playback.ActualWidth, 3);
            Assert.Equal(30, playback.ActualHeight, 3);
            Assert.Equal(13, playback.FontSize);
            Assert.Empty(Descendants(playback).OfType<System.Windows.Shapes.Path>());
            var label = Assert.Single(Descendants(playback).OfType<TextBlock>());
            Assert.Equal(text, label.Text);
            Assert.True(label.TranslatePoint(new(label.ActualWidth, 0), playback).X <= playback.ActualWidth - 2);
            Assert.True(label.DesiredSize.Width <= label.ActualWidth + 0.01);
        }
    });

    [Theory]
    [InlineData(MiniPlayerSourceKind.YouTube, true)]
    [InlineData(MiniPlayerSourceKind.YouTube, false)]
    [InlineData(MiniPlayerSourceKind.YouTube, null)]
    [InlineData(MiniPlayerSourceKind.LocalFile, true)]
    [InlineData(MiniPlayerSourceKind.LocalFile, false)]
    [InlineData(MiniPlayerSourceKind.LocalFile, null)]
    public void RestoredWaitingRowOffersLoadRegardlessOfSavedPauseState(MiniPlayerSourceKind sourceKind, bool? paused) => RunInSta(() =>
    {
        var state = new YouTubeMiniPlayerState { SourceKind = sourceKind, Title = "復元した動画", IsPaused = paused };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        var row = LoadProductRow();
        row.DataContext = item;
        Layout(row, 780);
        var playback = Descendants(row).OfType<Button>().Single(button =>
            button.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path.Path == nameof(YouTubeMiniPlayerListItem.PrimaryPlaybackButtonText));
        Assert.Equal("読込待ち", item.DisplayStateText);
        Assert.Equal("読込", playback.Content);
        Assert.True(playback.IsEnabled);
        Assert.Contains("読み込んで再生", Assert.IsType<string>(playback.ToolTip));
        item.UpdateRuntimeState(null, sourceKind == MiniPlayerSourceKind.LocalFile, false, true, null, false, false);
        Layout(row, 780);
        Assert.Equal(paused == false ? "一時停止" : "再生", playback.Content);
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductMoreMenuKeepsExistingCommandsAndAddsChat() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerItemMorePopup", out var document);
        var state = new YouTubeMiniPlayerState { Title = "個別操作の確認", IsLooping = true, IsTopmost = true };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        grid.DataContext = item;
        Layout(grid, 280);
        var texts = Descendants(grid).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("再生の設定", texts);
        Assert.Contains("ウィンドウ", texts);
        Assert.Contains("ほかのプレイヤーとの連動", texts);
        Assert.Contains(state.Title, texts);
        var buttons = Descendants(grid).OfType<Button>().ToArray();
        Assert.Equal(10, buttons.Length);
        Assert.Contains(buttons, button => Equals(button.Content, "ループ解除"));
        Assert.Contains(buttons, button => Equals(button.Content, "最前面解除"));
        var source = document.Descendants(P + "Popup").Single(element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerItemMorePopup");
        Assert.Equal(10, source.Descendants(P + "Button").Select(button => (string?)button.Attribute("Click")).Distinct().Count());
        SaveSnapshot(grid, "more");
        state.IsLooping = false;
        state.IsTopmost = false;
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        Layout(grid, 280);
        Assert.Contains(buttons, button => Equals(button.Content, "ループ"));
        Assert.Contains(buttons, button => Equals(button.Content, "最前面"));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(12d, 34d, 60d, true)]
    [InlineData(0d, 60d, 60d, true)]
    [InlineData(12d, 12.5d, 60d, true)]
    [InlineData(12d, 34d, null, false)]
    [InlineData(12d, 34d, 0d, false)]
    [InlineData(12d, 34d, double.NaN, false)]
    [InlineData(12d, 34d, double.PositiveInfinity, false)]
    [InlineData(12d, 10d, 60d, false)]
    [InlineData(12d, 12.1d, 60d, false)]
    [InlineData(12d, null, 60d, false)]
    [InlineData(null, null, 60d, false)]
    [InlineData(12d, 70d, 60d, false)]
    public void AbRepeatRangeUsesKnownDurationAndKeepsGeometryFinite(double? start, double? end, double? duration, bool visible) => RunInSta(() =>
    {
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        { AbRepeatStartSeconds = start, AbRepeatEndSeconds = end, DurationSeconds = duration, LastPositionSeconds = 20 });
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, item.AbRepeatTimelineVisibility);
        Assert.All(new[] { item.AbRepeatStartOffset, item.AbRepeatEndOffset, item.AbRepeatRangeWidth, item.AbRepeatPositionOffset },
            value => Assert.True(double.IsFinite(value) && value >= 0 && value <= 352));
        if (visible)
        {
            Assert.Equal(start!.Value / duration!.Value * 352, item.AbRepeatStartOffset, 6);
            Assert.Equal((end!.Value - start.Value) / duration.Value * 352, item.AbRepeatRangeWidth, 6);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void ProductAbRepeatMenuShowsRangeAndTracksExistingStateUpdates() => RunInSta(() =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerAbRepeatPopup", out _);
        var state = new YouTubeMiniPlayerState
        {
            Title = "基本フレーズ",
            AbRepeatStartSeconds = 12,
            AbRepeatEndSeconds = 34,
            DurationSeconds = 60,
            LastPositionSeconds = 20,
            IsPaused = true,
            IsAbRepeatEnabled = true
        };
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), state);
        grid.DataContext = item;
        ((Button)grid.FindName("YouTubeMiniPlayerAbRepeatToggleButton")).Content = "一時解除";
        Layout(grid, 380);
        var texts = Descendants(grid).OfType<TextBlock>().ToArray();
        Assert.Contains(texts, text => text.Text == "0:12");
        Assert.Contains(texts, text => text.Text == "0:34");
        Assert.Contains(texts, text => text.Text == "区間 0:22");
        Assert.Contains(texts, text => text.Text == "リピート中");
        Assert.False(Assert.Single(Descendants(grid).OfType<Viewbox>()).IsHitTestVisible);
        Assert.Equal(4, Descendants(grid).OfType<Button>().Count());
        SaveSnapshot(grid, "ab-repeat");
        state.AbRepeatEndSeconds = 40;
        state.Title = "更新された基本フレーズ";
        state.IsAbRepeatEnabled = false;
        item.UpdateRuntimeState(null, false, false, true, null, false, false);
        Layout(grid, 380);
        Assert.Contains(texts, text => text.Text == "0:40");
        Assert.Contains(texts, text => text.Text == "区間 0:28");
        Assert.Contains(texts, text => text.Text == "停止中");
        Assert.Contains(texts, text => text.Text == state.Title);
        return Task.CompletedTask;
    });

    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static void AssertTopmostAppearance(Button button, bool enabled)
    {
        Assert.Equal("最前面", button.Content);
        Assert.True(button.IsEnabled);
        button.ApplyTemplate();
        var border = Assert.IsType<Border>(button.Template.FindName("TopmostBorder", button));
        var color = Assert.IsType<SolidColorBrush>(button.Background).Color;
        Assert.Equal(color, Assert.IsType<SolidColorBrush>(border.Background).Color);
        Assert.Equal(Color.FromRgb(35, 38, 45), color);
        var frame = Assert.IsType<SolidColorBrush>(button.BorderBrush).Color;
        Assert.Equal(frame, Assert.IsType<SolidColorBrush>(border.BorderBrush).Color);
        var text = Assert.IsType<SolidColorBrush>(button.Foreground).Color;
        if (enabled)
        {
            Assert.True(frame.G >= 180 && frame.B >= 180 && frame.R < 100, "最前面が有効なときは水色の枠で表示します。");
            Assert.True(text.R >= 220 && text.G >= 220 && text.B >= 220);
        }
        else
        {
            Assert.Equal(Color.FromRgb(52, 56, 66), frame);
            Assert.InRange(text.R, (byte)120, (byte)190);
            Assert.InRange(Math.Max(text.R, Math.Max(text.G, text.B)) - Math.Min(text.R, Math.Min(text.G, text.B)), 0, 24);
        }
        Assert.StartsWith(enabled ? "常に最前面：有効" : "常に最前面：無効", Assert.IsType<string>(button.ToolTip));
        Assert.Empty(Descendants(button).OfType<System.Windows.Shapes.Path>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TopmostClickAfterRehostingKeepsCaptionInSyncWithTheListWindow(bool browserTopmost) => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        var button = Descendants(content).OfType<Button>().Single(control => control.Name == "YouTubeMiniPlayerListTopmostButton");
        var storage = new Decorator { Child = content, Visibility = Visibility.Collapsed };
        var browser = new Window { Content = storage, Topmost = browserTopmost, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        using var window = new YouTubeMiniPlayerListWindow { DataContext = browser, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        using var fixture = new Fixture();
        var popups = Enumerable.Range(0, 7).Select(_ => new Popup()).ToArray();
        using var controller = new YouTubeMiniPlayerListPopupController(window,
            new(content, content, window, popups[0], popups[1], popups[2], popups[3], popups[4], popups[5], popups[6]),
            fixture.Controller, fixture.Host);
        var owner = CreateOwnerToken();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_youTubeMiniPlayerListWindow", flags)!.SetValue(owner, window);
        typeof(MainWindow).GetField("_youTubeMiniPlayerListPopupController", flags)!.SetValue(owner, controller);
        button.Click += (RoutedEventHandler)typeof(MainWindow).GetMethod("YouTubeMiniPlayerListTopmostButton_Click", flags)!
            .CreateDelegate(typeof(RoutedEventHandler), owner);
        content.PreviewMouseLeftButtonDown += controller.YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown;
        try
        {
            browser.Show();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            storage.Child = null;
            window.AttachContent(content, new ResourceDictionary());
            window.ShowOrActivate();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.True(window.Topmost);
            Assert.True(YouTubeMiniPlayerListWindowTests.IsNativeTopmost(window));
            AssertTopmostAppearance(button, true);
            for (var index = 0; index < 2; index++)
            {
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 100 + index, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Assert.Equal(index == 1, window.Topmost);
                Assert.Equal(index == 1, YouTubeMiniPlayerListWindowTests.IsNativeTopmost(window));
                AssertTopmostAppearance(button, index == 1);
                Assert.Equal(browserTopmost, browser.Topmost);
                if (index == 0)
                {
                    window.Close();
                    window.ShowOrActivate();
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                    Assert.False(window.Topmost);
                    Assert.False(YouTubeMiniPlayerListWindowTests.IsNativeTopmost(window));
                    AssertTopmostAppearance(button, false);
                }
            }
            browser.Topmost = !browserTopmost;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.True(window.Topmost);
            AssertTopmostAppearance(button, true);
        }
        finally { browser.Close(); }
    });

    [Fact]
    public void CompleteProductListCanBeRehostedAndItsNamedBindingsRemainLive() => RunInSta(async () =>
    {
        var grid = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        using var window = new TabBrowser.Views.YouTubeMiniPlayerListWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            Width = 900,
            Height = 500
        };
        window.AttachContent(grid, new ResourceDictionary());
        window.ShowOrActivate();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        var restore = Descendants(grid).OfType<Button>().Single(button => button.Name == "YouTubeMiniPlayerRestoreClosedButton");
        var list = Descendants(grid).OfType<ListBox>().Single(control => control.Name == "YouTubeMiniPlayerList");
        Descendants(grid).OfType<TextBlock>().Single(text => text.Name == "YouTubeMiniPlayerListEmptyText").Visibility = Visibility.Collapsed;
        Descendants(grid).OfType<TextBlock>().Single(text => text.Name == "YouTubeMiniPlayerListSummaryText").Text = "YTミニプレイヤー  1件｜再生1｜音声1";
        list.ItemsSource = new[] { new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Title = "独立ウィンドウへ移行後もタイトルと既存操作を維持", DurationSeconds = 300,
            LastPositionSeconds = 60, IsPaused = false, Volume = .65
        }, isRuntimeLoaded: true) };
        restore.Visibility = Visibility.Visible;
        Layout(grid, 900);
        var actions = Descendants(grid).OfType<Button>().Where(button => button.Name is
            "YouTubeMiniPlayerArrangeMenuButton" or "YouTubeMiniPlayerGlobalActionsButton").ToArray();
        Assert.All(actions, button => Assert.Equal(restore.ActualWidth, button.ActualWidth, 5));
        restore.Visibility = Visibility.Collapsed;
        Layout(grid, 900);
        Assert.All(actions, button => Assert.True(button.ActualWidth > 250));
        Assert.Same(window, Window.GetWindow(list));
        Assert.Equal(2, YouTubeMiniPlayerListWindowTests.HitTestWindow(window, new Point(50, 20)));
        window.Width = 780;
        Layout(grid, 780);
        var topmost = Descendants(grid).OfType<Button>().Single(button => button.Name == "YouTubeMiniPlayerListTopmostButton");
        void AssertTopmostCaptionFits()
        {
            var caption = new FormattedText((string)topmost.Content, CultureInfo.CurrentUICulture,
                topmost.FlowDirection, new Typeface(topmost.FontFamily, topmost.FontStyle, topmost.FontWeight, topmost.FontStretch),
                topmost.FontSize, topmost.Foreground, VisualTreeHelper.GetDpi(topmost).PixelsPerDip);
            Assert.True(caption.WidthIncludingTrailingWhitespace + topmost.Padding.Left + topmost.Padding.Right
                + topmost.BorderThickness.Left + topmost.BorderThickness.Right <= topmost.ActualWidth);
        }
        AssertTopmostAppearance(topmost, true);
        SaveSnapshot(topmost, "topmost-enabled");
        AssertTopmostCaptionFits();
        Assert.Contains("無効にします", (string)topmost.ToolTip);
        var topmostWidth = topmost.ActualWidth;
        Assert.True(double.IsNaN(topmost.Width));
        Assert.InRange(topmostWidth, 45, 72);
        Assert.Equal(1, YouTubeMiniPlayerListWindowTests.HitTestWindow(window,
            topmost.TranslatePoint(new Point(topmost.ActualWidth / 2, topmost.ActualHeight / 2), window)));
        window.Topmost = false;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        Layout(grid, 780);
        AssertTopmostAppearance(topmost, false);
        SaveSnapshot(topmost, "topmost-disabled");
        AssertTopmostCaptionFits();
        Assert.Contains("有効にします", (string)topmost.ToolTip);
        Assert.Equal(topmostWidth, topmost.ActualWidth);
        window.Topmost = true;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        AssertTopmostAppearance(topmost, true);
        var close = Descendants(grid).OfType<Button>().Single(button => button.Name == "YouTubeMiniPlayerListCloseButton");
        Assert.Equal(1, YouTubeMiniPlayerListWindowTests.HitTestWindow(window,
            close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), window)));
        SaveSnapshot(grid, "window-list");
    });

    [Fact]
    public void SelectedCloseStaysSeparatedAndTheBandFitsAtMinimumWidth() => RunInSta(() =>
    {
        var band = LoadProductPart("YouTubeMiniPlayerAudioFocusHost", out _);
        var actions = Descendants(band).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusBar");
        actions.Visibility = Visibility.Visible;
        var close = Descendants(band).OfType<Button>().Single(element => element.Name == "YouTubeMiniPlayerCloseSelectedButton");
        var mute = Descendants(band).OfType<Button>().Single(element => element.Name == "YouTubeMiniPlayerMuteSelectedButton");
        var label = Descendants(actions).OfType<TextBlock>().Single(element => element.Text == "選択した動画へ");
        Assert.Contains($"最大{YouTubeMiniPlayerStore.ClosedHistoryLimit}件", Assert.IsType<string>(close.ToolTip));
        Assert.Contains("閉じた動画から戻せます", Assert.IsType<string>(close.ToolTip));
        foreach (var width in new[] { 720d, 740d })
        {
            Layout(band, width);
            Assert.True(close.TranslatePoint(new(), band).X - mute.TranslatePoint(new(mute.ActualWidth, 0), band).X >= 16);
            Assert.True(label.DesiredSize.Width - label.Margin.Left - label.Margin.Right <= label.ActualWidth + 0.01);
            Assert.True(label.TranslatePoint(new(), band).X >= 0);
            Assert.True(close.TranslatePoint(new(close.ActualWidth, 0), band).X <= band.ActualWidth + 1);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void SelectionControlsStayVisibleAndFitTheExistingBand() => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        using var window = new YouTubeMiniPlayerListWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var controls = Descendants(content).OfType<FrameworkElement>().Where(element => element.Name.Length > 0)
            .GroupBy(element => element.Name).ToDictionary(group => group.Key, group => group.First());
        var list = Assert.IsType<ListBox>(controls["YouTubeMiniPlayerList"]);
        var all = Assert.IsType<Button>(controls["YouTubeMiniPlayerSelectAllButton"]);
        var clear = Assert.IsType<Button>(controls["YouTubeMiniPlayerClearSelectionButton"]);
        var count = Assert.IsType<TextBlock>(controls["YouTubeMiniPlayerAudioFocusSelectionText"]);
        var band = controls["YouTubeMiniPlayerAudioFocusHost"];
        var actions = controls["YouTubeMiniPlayerAudioFocusBar"];
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        window.UpdateLayout();
        Assert.True(count.IsVisible);
        Assert.False(all.IsEnabled);
        Assert.False(clear.IsEnabled);
        foreach (var index in Enumerable.Range(0, 8))
            list.Items.Add(new YouTubeMiniPlayerListItem(CreateOwnerToken(), new() { Id = Guid.NewGuid(), Title = $"選択する動画 {index}" }));
        foreach (var selected in new[] { false, true })
        {
            if (selected) list.SelectAll();
            else list.UnselectAll();
            actions.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            count.Text = $"選択 {(selected ? 8 : 0)} / 8件";
            window.UpdateLayout();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            window.UpdateLayout();
            Assert.True(all.IsEnabled);
            Assert.Equal(selected, clear.IsEnabled);
            Assert.True(count.IsVisible);
            Assert.Equal(42, band.ActualHeight);
            if (selected)
            {
                Assert.True(count.TranslatePoint(new Point(count.ActualWidth, 0), band).X <= actions.TranslatePoint(new Point(), band).X);
                foreach (var button in Descendants(actions).OfType<Button>())
                    Assert.True(button.TranslatePoint(new Point(button.ActualWidth, 0), band).X <= band.ActualWidth + 1);
            }
        }
    });

    [Fact]
    public void ProductListFollowsItemCountAndScrollsOnlyAboveItsHeightLimit() => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        using var window = new YouTubeMiniPlayerListWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            Width = 900,
            MaxHeight = 700,
            Left = 100,
            Top = 100
        };
        var list = Descendants(content).OfType<ListBox>().Single(control => control.Name == "YouTubeMiniPlayerList");
        var empty = Descendants(content).OfType<TextBlock>().Single(text => text.Name == "YouTubeMiniPlayerListEmptyText");
        var items = new ObservableCollection<YouTubeMiniPlayerListItem>(Enumerable.Range(0, 4).Select(index =>
            new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
            {
                Id = Guid.NewGuid(),
                Title = $"高さ確認用の動画 {index}",
                DurationSeconds = 300,
                LastPositionSeconds = 60,
                Volume = .65
            }, isRuntimeLoaded: true)));
        list.ItemsSource = items;
        empty.Visibility = Visibility.Collapsed;
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        window.FitInitialHeight(700);
        var position = new Point(window.Left, window.Top);
        var previousHeight = window.Height;
        while (items.Count > 0)
        {
            items.RemoveAt(items.Count - 1);
            if (items.Count == 0)
            {
                list.Visibility = Visibility.Collapsed;
                empty.Visibility = Visibility.Visible;
            }
            window.FitToContentHeight();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (items.Count > 0) Assert.True(window.Height < previousHeight - 50);
            else Assert.InRange(window.Height, window.MinHeight, 225);
            Assert.Equal(900, window.Width);
            Assert.Equal(position, new Point(window.Left, window.Top));
            previousHeight = window.Height;
        }
        Assert.True(window.IsOpen);
        empty.Visibility = Visibility.Collapsed;
        list.Visibility = Visibility.Visible;
        for (var index = 0; index < 12; index++)
        {
            items.Add(new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
            {
                Id = Guid.NewGuid(),
                Title = $"追加した動画 {index}",
                DurationSeconds = 300,
                LastPositionSeconds = 60,
                Volume = .65
            }, isRuntimeLoaded: true));
            window.FitToContentHeight();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.InRange(window.Height, previousHeight, 700);
            if (index < 3)
            {
                var scroll = Descendants(list).OfType<ScrollViewer>().Single();
                Assert.True(scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                    $"追加={index + 1}, 高さ={window.Height}/{window.ActualHeight}, 内容={content.DesiredSize.Height}, 一覧={list.DesiredSize.Height}/{list.ActualHeight}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, scrollable={scroll.ScrollableHeight}, DPI={VisualTreeHelper.GetDpi(window).DpiScaleY}, 行={string.Join(",", Descendants(list).OfType<ListBoxItem>().Select(row => $"{row.DesiredSize.Height}/{row.ActualHeight}"))}, パネル={string.Join(",", Descendants(list).OfType<ItemsPresenter>().Select(panel => $"{panel.DesiredSize.Height}/{panel.ActualHeight}"))}");
            }
            previousHeight = window.Height;
            Assert.Equal(900, window.Width);
            Assert.Equal(position, new Point(window.Left, window.Top));
        }
        Assert.Equal(700, window.Height);
        Assert.Equal(Visibility.Visible, Descendants(list).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
        while (items.Count > 2) items.RemoveAt(items.Count - 1);
        window.FitToContentHeight();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(window.Height < 700);
        Assert.Equal(Visibility.Collapsed, Descendants(list).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentSizeChangesRefitWithoutAnUnnecessaryScrollbar(bool changeRow) => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out var document);
        var declaration = document.Descendants(P + "ListBox").Single(element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerList");
        Assert.Equal("YouTubeMiniPlayerListPopupRoot_SizeChanged", (string?)declaration.Attribute("SizeChanged"));
        Assert.Contains(declaration.Descendants(P + "EventSetter"), setter => (string?)setter.Attribute("Event") == "SizeChanged"
            && (string?)setter.Attribute("Handler") == "YouTubeMiniPlayerListPopupRoot_SizeChanged");
        using var window = new YouTubeMiniPlayerListWindow
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            Width = 780,
            MaxHeight = 700,
            Left = 100,
            Top = 100
        };
        using var fixture = new YouTubeMiniPlayerListViewControllerTests.Fixture();
        // 小さい作業領域でも、上限到達後の高さ調整とスクロールが収束することを確認する。
        var workArea = SystemParameters.WorkArea;
        fixture.Host.WorkArea = new Rect(workArea.X, workArea.Y,
            Math.Min(workArea.Width, 1024), Math.Min(workArea.Height, 700));
        var list = Descendants(content).OfType<ListBox>().Single(control => control.Name == "YouTubeMiniPlayerList");
        var focusHeader = Descendants(content).OfType<FrameworkElement>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusHost");
        focusHeader.Visibility = Visibility.Collapsed;
        var elements = new YouTubeMiniPlayerListPopupElements(content, content, window,
            new(), new(), new(), new(), new(), new(), new());
        using var controller = new YouTubeMiniPlayerListPopupController(window, elements, fixture.Controller, fixture.Host);
        controller.InitializeTracking();
        var style = new Style(typeof(ListBoxItem), list.ItemContainerStyle);
        style.Setters.Add(new EventSetter(FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler(controller.YouTubeMiniPlayerListPopupRoot_SizeChanged)));
        list.ItemContainerStyle = style;
        list.SizeChanged += controller.YouTubeMiniPlayerListPopupRoot_SizeChanged;
        Descendants(content).OfType<TextBlock>().Single(text => text.Name == "YouTubeMiniPlayerListEmptyText").Visibility = Visibility.Collapsed;
        var item = new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Id = Guid.NewGuid(),
            Title = "表示内容の高さが変わる動画",
            DurationSeconds = 300,
            LastPositionSeconds = 60,
            Volume = .65
        }, isRuntimeLoaded: true);
        list.ItemsSource = new[] { item };
        window.AttachContent(content, new ResourceDictionary());
        async Task WaitForIdle(string phase)
        {
            try
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"{phase}: Height={window.Height}, ActualHeight={window.ActualHeight}, MaxHeight={window.MaxHeight}, WorkArea={fixture.Host.WorkArea}", error);
            }
        }
        window.ShowOrActivate();
        await WaitForIdle("初回表示");
        window.FitInitialHeight(700);
        await WaitForIdle("初期の高さ調整");
        var previousHeight = window.Height;
        if (changeRow)
        {
            Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0)).MinHeight = 160;
        }
        else focusHeader.Visibility = Visibility.Visible;
        await WaitForIdle("内容の拡大");
        Assert.True(window.Height > previousHeight + 5,
            $"変更前={previousHeight}, 変更後={window.Height}");
        Assert.NotEqual(Visibility.Visible, Descendants(list).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
        Assert.Single(list.Items);
        if (changeRow) Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0)).MinHeight = 0;
        else focusHeader.Visibility = Visibility.Collapsed;
        await WaitForIdle("内容の縮小");
        Assert.InRange(window.Height, previousHeight - 1, previousHeight + 1);
        Assert.NotEqual(Visibility.Visible, Descendants(list).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
        list.ItemsSource = Enumerable.Range(0, 30).Select(index => new YouTubeMiniPlayerListItem(CreateOwnerToken(), new()
        {
            Id = Guid.NewGuid(),
            Title = $"スクロールを確認する動画 {index}",
            DurationSeconds = 300,
            LastPositionSeconds = 60,
            Volume = .65
        }, isRuntimeLoaded: true)).ToArray();
        window.FitToContentHeight();
        await WaitForIdle("表示上限への到達");
        var overflow = Descendants(list).OfType<ScrollViewer>().Single();
        Assert.Equal(Visibility.Visible, overflow.ComputedVerticalScrollBarVisibility);
        overflow.LineDown();
        await WaitForIdle("1行スクロール");
        Assert.True(overflow.VerticalOffset > 0, "内容高の調整後も、表示しきれない行へスクロールできること");
        overflow.ScrollToEnd();
        await WaitForIdle("末尾へスクロール");
        Assert.True(overflow.VerticalOffset > 0, "末尾の行を表示しても先頭へ戻されないこと");
    });

    [Fact]
    public void CaptionFoldingKeepsContentSelectionAndWindowState() => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out var document);
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute(X + "Name") == "YouTubeMiniPlayerListCollapseButton");
        using var window = new YouTubeMiniPlayerListWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, MaxHeight = 650, Left = 100, Top = 100 };
        var list = Descendants(content).OfType<ListBox>().Single(element => element.Name == "YouTubeMiniPlayerList");
        var body = Descendants(content).OfType<DockPanel>().Single(element => element.Name == "YouTubeMiniPlayerListBody");
        var toolbar = Descendants(content).OfType<Grid>().Single(element => element.Name == "YouTubeMiniPlayerListToolbar");
        var summary = Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListSummaryText");
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListEmptyText").Visibility = Visibility.Collapsed;
        Descendants(content).OfType<Border>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusBar").Visibility = Visibility.Visible;
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerAudioFocusSelectionText").Text = "選択 2 / 3件";
        summary.Text = "YTミニプレイヤー  3件｜再生2｜音声2";
        var owner = CreateOwnerToken();
        var items = Enumerable.Range(0, 3).Select(index => new YouTubeMiniPlayerListItem(owner, new()
        {
            Id = Guid.NewGuid(),
            Title = $"折りたたみを確認する動画 {index}",
            DurationSeconds = 300,
            LastPositionSeconds = 60,
            Volume = .65,
            IsPaused = index == 2
        }, isRuntimeLoaded: true)).ToArray();
        list.ItemsSource = items;
        list.SelectedItems.Add(items[0]);
        list.SelectedItems.Add(items[2]);
        window.AttachContent(content, new ResourceDictionary());
        window.CollapseRequested += (_, _) => window.ToggleCollapsed();
        window.ToggleCollapsed();
        Assert.False(window.IsCollapsed);
        window.ShowOrActivate();
        window.FitInitialHeight(650);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var position = new Point(window.Left, window.Top);
        var width = window.Width;
        var expandedHeight = window.Height;
        SaveSnapshot(content, "fold-expanded");
        YouTubeMiniPlayerListWindowTests.DoubleClickCaption(window);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(window.IsCollapsed);
        Assert.Equal("ドラッグして移動（他のディスプレイへ移動可能）。ダブルクリックで表示、非表示。右クリックメニューで位置を戻す", Assert.IsType<TextBlock>(((ToolTip)window.Resources["CaptionHelp"]).Content).Text);
        Assert.True(summary.IsVisible);
        Assert.False(body.IsVisible);
        Assert.False(toolbar.IsVisible);
        Assert.InRange(window.Height, 48, 60);
        Assert.True(window.Height < expandedHeight);
        Assert.Equal(position, new Point(window.Left, window.Top));
        Assert.Equal(width, window.Width);
        Assert.True(window.Topmost);
        Assert.Equal(new[] { items[0], items[2] }, list.SelectedItems.Cast<YouTubeMiniPlayerListItem>());
        Assert.False(items[0].State.IsPaused);
        Assert.True(items[2].State.IsPaused);
        SaveSnapshot(content, "fold-collapsed");
        window.Close();
        window.ToggleCollapsed();
        Assert.True(window.IsCollapsed);
        window.ShowOrActivate();
        window.WindowState = WindowState.Minimized;
        window.ToggleCollapsed();
        Assert.True(window.IsCollapsed);
        window.ShowOrActivate();
        Assert.True(window.IsCollapsed);
        Assert.False(body.IsVisible);
        YouTubeMiniPlayerListWindowTests.DoubleClickCaption(window);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(window.IsCollapsed);
        Assert.Equal("ドラッグして移動（他のディスプレイへ移動可能）。ダブルクリックで表示、非表示。右クリックメニューで位置を戻す", Assert.IsType<TextBlock>(((ToolTip)window.Resources["CaptionHelp"]).Content).Text);
        Assert.True(body.IsVisible);
        Assert.True(toolbar.IsVisible);
        Assert.Same(items, list.ItemsSource);
        Assert.Equal(new[] { items[0], items[2] }, list.SelectedItems.Cast<YouTubeMiniPlayerListItem>());
        Assert.InRange(window.Height, expandedHeight - 1, expandedHeight + 1);
        Assert.Equal(position, new Point(window.Left, window.Top));
        Assert.Equal(width, window.Width);
    });

    [Theory]
    [InlineData(2)]
    [InlineData(30)]
    public void FoldedListRefitsToCurrentRowCountWhenExpanded(int rowCount) => RunInSta(async () =>
    {
        var content = LoadProductPart("YouTubeMiniPlayerListPopupRoot", out _);
        using var window = new YouTubeMiniPlayerListWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, MaxHeight = 650, Left = 100, Top = 100 };
        using var fixture = new YouTubeMiniPlayerListViewControllerTests.Fixture();
        fixture.Host.WorkArea = new Rect(0, 0, 1024, 700);
        var list = Descendants(content).OfType<ListBox>().Single(element => element.Name == "YouTubeMiniPlayerList");
        var elements = new YouTubeMiniPlayerListPopupElements(content, content, window, new(), new(), new(), new(), new(), new(), new());
        using var controller = new YouTubeMiniPlayerListPopupController(window, elements, fixture.Controller, fixture.Host);
        controller.InitializeTracking();
        var owner = CreateOwnerToken();
        var items = new ObservableCollection<YouTubeMiniPlayerListItem>();
        list.ItemsSource = items;
        window.AttachContent(content, new ResourceDictionary());
        window.ShowOrActivate();
        // 初回の非同期更新が終わる前でも見出しだけにできる。
        window.ToggleCollapsed();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var foldedHeight = window.Height;
        for (var index = 0; index < rowCount; index++) items.Add(new(owner, new()
        {
            Id = Guid.NewGuid(),
            Title = $"追加した動画 {index}",
            DurationSeconds = 300,
            Volume = .65
        }, isRuntimeLoaded: true));
        Descendants(content).OfType<TextBlock>().Single(element => element.Name == "YouTubeMiniPlayerListEmptyText").Visibility = Visibility.Collapsed;
        window.FitToContentHeight();
        controller.ClampYouTubeMiniPlayerListPopupPosition();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(window.IsCollapsed);
        Assert.Equal(48, window.MinHeight);
        Assert.InRange(window.Height, foldedHeight - 1, foldedHeight + 1);
        window.ToggleCollapsed();
        controller.ClampYouTubeMiniPlayerListPopupPosition();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(window.IsCollapsed);
        Assert.Equal(180, window.MinHeight);
        Assert.Equal(rowCount, list.Items.Count);
        Assert.True(window.Height > foldedHeight);
        var scroll = Descendants(list).OfType<ScrollViewer>().Single();
        if (rowCount == 2) Assert.NotEqual(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        else
        {
            Assert.Equal(window.MaxHeight, window.Height);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            scroll.ScrollToEnd();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(scroll.VerticalOffset > 0);
        }
    });

    private static Grid LoadProductPart(string name, out XDocument document)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MainWindow.xaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        document = XDocument.Load(Path.Combine(directory.FullName, "MainWindow.xaml"));
        var element = document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);
        return LoadElement(document, element.Name == P + "Popup" ? element.Elements().Single() : element);
    }

    private static Grid LoadElement(XDocument document, XElement source)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "App.xaml"))) directory = directory.Parent;
        var app = XDocument.Load(Path.Combine(directory!.FullName, "App.xaml"));
        var resources = app.Root!.Element(P + "Application.Resources")!.Elements(P + "SolidColorBrush").Where(element => element.Attribute(X + "Key") is not null)
            .Concat(app.Descendants(P + "Style").Where(element => (string?)element.Attribute(X + "Key") is "DarkButtonStyle" or "DarkCheckBoxStyle"))
            .Concat(app.Root.Element(P + "Application.Resources")!.Elements(P + "Style").Where(element =>
                element.Attribute(X + "Key") is null && (string?)element.Attribute("TargetType") == "{x:Type CheckBox}"))
            .Concat(document.Root!.Element(P + "Window.Resources")!.Elements().Where(element =>
                ((string?)element.Attribute(X + "Key")) is string key && (key.StartsWith("YouTubeMiniPlayer", StringComparison.Ordinal)
                || key is "ToolbarButtonStyle" or "ReadabilityComboBoxStyle"))).Select(element => new XElement(element));
        var content = new XElement(source);
        var events = new[] { "Click", "SelectionChanged", "DragStarted", "DragDelta", "DragCompleted", "MouseDoubleClick", "MouseMove", "MouseLeftButtonUp", "PreviewMouseWheel", "PreviewMouseLeftButtonDown", "PreviewMouseRightButtonUp", "PreviewKeyDown", "Opened", "Closed", "DragOver", "Drop", "PreviewDragOver", "SizeChanged", "ValueChanged", "PreviewMouseMove", "MouseEnter" };
        foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray()) attribute.Remove();
        foreach (var setter in content.Descendants(P + "EventSetter").ToArray()) setter.Remove();
        var wrapper = new XElement(P + "Grid", new XAttribute("xmlns", P), new XAttribute(XNamespace.Xmlns + "x", X),
            new XAttribute("Background", "#11141A"), new XElement(P + "Grid.Resources", resources), content);
        return (Grid)XamlReader.Parse(wrapper.ToString());
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.InvalidateMeasure();
        element.Measure(new(width, 1000));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        element.Measure(new(width, 1000));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
        element.InvalidateVisual();
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Render);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveSnapshot(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("KITEN_MINIPLAYER_UI_SNAPSHOT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
