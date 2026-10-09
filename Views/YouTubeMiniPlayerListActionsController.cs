using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record YouTubeMiniPlayerListActionsElements(
    Popup YouTubeMiniPlayerAbRepeatPopup,
    Popup YouTubeMiniPlayerSpeedPopup,
    Popup YouTubeMiniPlayerVolumePopup,
    Popup YouTubeMiniPlayerItemMorePopup,
    TextBlock YouTubeMiniPlayerVolumeTitleText,
    Slider YouTubeMiniPlayerVolumeSlider,
    TextBlock YouTubeMiniPlayerVolumeValueText,
    UniformGrid YouTubeMiniPlayerAudioBoostPanel,
    TextBlock YouTubeMiniPlayerAudioBoostValueText,
    TextBlock YouTubeMiniPlayerAudioBoostHintText,
    TextBlock YouTubeMiniPlayerAbRepeatTitleText,
    TextBlock YouTubeMiniPlayerAbRepeatRangeText,
    Button YouTubeMiniPlayerAbRepeatSetStartButton,
    Button YouTubeMiniPlayerAbRepeatSetEndButton,
    Button YouTubeMiniPlayerAbRepeatToggleButton,
    Button YouTubeMiniPlayerAbRepeatClearButton,
    Button YouTubeMiniPlayerAudioBoostOffButton,
    Button YouTubeMiniPlayerAudioBoost3Button,
    Button YouTubeMiniPlayerAudioBoost6Button,
    Button YouTubeMiniPlayerAudioBoost9Button);

internal interface IYouTubeMiniPlayerListActionsHost
{
    bool CanControlVolume(YouTubeMiniPlayerListItem item);
    Task<bool> SetVolumeAsync(YouTubeMiniPlayerListItem item, double volume);
    Task<bool> SetAudioBoostAsync(YouTubeMiniPlayerListItem item, int decibels);
    void QueueSave(YouTubeMiniPlayerListItem item);
    void SetStatus(YouTubeMiniPlayerListItem item, string message);
    Task RefreshListAsync();
    Task ExecuteCommandAsync(string actionId, YouTubeMiniPlayerListItem item, object? parameter = null);
}

internal sealed class YouTubeMiniPlayerListActionsController
{
    private const int YouTubeMiniPlayerListVolumeApplyDelayMilliseconds = 200;
    private readonly YouTubeMiniPlayerVolumeInteractionCoordinator _youTubeMiniPlayerVolumeInteractionCoordinator = new();
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerListVolumeTimer;
    private readonly YouTubeMiniPlayerListActionsElements _elements;
    private readonly IYouTubeMiniPlayerListActionsHost _host;
    private YouTubeMiniPlayerListItem? _youTubeMiniPlayerListAbRepeatTarget;
    private YouTubeMiniPlayerListItem? _youTubeMiniPlayerListSpeedTarget;
    private YouTubeMiniPlayerListItem? _youTubeMiniPlayerListVolumeTarget;
    private YouTubeMiniPlayerListItem? _youTubeMiniPlayerListMoreTarget;
    private Task _youTubeMiniPlayerListVolumeApplyTask = Task.CompletedTask;
    private bool _isStopped;

    public YouTubeMiniPlayerListActionsController(
        YouTubeMiniPlayerListActionsElements elements, OwnedDispatcherTimer volumeTimer,
        IYouTubeMiniPlayerListActionsHost host)
    {
        _elements = elements;
        _youTubeMiniPlayerListVolumeTimer = volumeTimer;
        _host = host;
    }

    private Popup YouTubeMiniPlayerAbRepeatPopup => _elements.YouTubeMiniPlayerAbRepeatPopup;
    private Popup YouTubeMiniPlayerSpeedPopup => _elements.YouTubeMiniPlayerSpeedPopup;
    private Popup YouTubeMiniPlayerVolumePopup => _elements.YouTubeMiniPlayerVolumePopup;
    private Popup YouTubeMiniPlayerItemMorePopup => _elements.YouTubeMiniPlayerItemMorePopup;
    private TextBlock YouTubeMiniPlayerVolumeTitleText => _elements.YouTubeMiniPlayerVolumeTitleText;
    private Slider YouTubeMiniPlayerVolumeSlider => _elements.YouTubeMiniPlayerVolumeSlider;
    private TextBlock YouTubeMiniPlayerVolumeValueText => _elements.YouTubeMiniPlayerVolumeValueText;
    private UniformGrid YouTubeMiniPlayerAudioBoostPanel => _elements.YouTubeMiniPlayerAudioBoostPanel;
    private TextBlock YouTubeMiniPlayerAudioBoostValueText => _elements.YouTubeMiniPlayerAudioBoostValueText;
    private TextBlock YouTubeMiniPlayerAudioBoostHintText => _elements.YouTubeMiniPlayerAudioBoostHintText;
    private TextBlock YouTubeMiniPlayerAbRepeatTitleText => _elements.YouTubeMiniPlayerAbRepeatTitleText;
    private TextBlock YouTubeMiniPlayerAbRepeatRangeText => _elements.YouTubeMiniPlayerAbRepeatRangeText;
    private Button YouTubeMiniPlayerAbRepeatSetStartButton => _elements.YouTubeMiniPlayerAbRepeatSetStartButton;
    private Button YouTubeMiniPlayerAbRepeatSetEndButton => _elements.YouTubeMiniPlayerAbRepeatSetEndButton;
    private Button YouTubeMiniPlayerAbRepeatToggleButton => _elements.YouTubeMiniPlayerAbRepeatToggleButton;
    private Button YouTubeMiniPlayerAbRepeatClearButton => _elements.YouTubeMiniPlayerAbRepeatClearButton;
    private Button YouTubeMiniPlayerAudioBoostOffButton => _elements.YouTubeMiniPlayerAudioBoostOffButton;
    private Button YouTubeMiniPlayerAudioBoost3Button => _elements.YouTubeMiniPlayerAudioBoost3Button;
    private Button YouTubeMiniPlayerAudioBoost6Button => _elements.YouTubeMiniPlayerAudioBoost6Button;
    private Button YouTubeMiniPlayerAudioBoost9Button => _elements.YouTubeMiniPlayerAudioBoost9Button;

    public void InitializeVolumeTimer()
    {
        _youTubeMiniPlayerListVolumeTimer.Interval = TimeSpan.FromMilliseconds(YouTubeMiniPlayerListVolumeApplyDelayMilliseconds);
        _youTubeMiniPlayerListVolumeTimer.Tick += VolumeTimer_Tick;
    }

    private async void VolumeTimer_Tick(object? sender, EventArgs e)
    {
        _youTubeMiniPlayerListVolumeTimer.Stop();
        _youTubeMiniPlayerListVolumeApplyTask = ApplyPendingYouTubeMiniPlayerListVolumeAsync();
        await _youTubeMiniPlayerListVolumeApplyTask;
    }

    public void StopVolumeTracking()
    {
        _isStopped = true;
        _youTubeMiniPlayerListVolumeTimer.Stop();
        _youTubeMiniPlayerListVolumeTimer.Tick -= VolumeTimer_Tick;
    }

    public async Task YouTubeMiniPlayerItemMuteButton_PreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ToggleYouTubeMiniPlayerVolumePopupAsync(button, item);
    }

    public void YouTubeMiniPlayerItemMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        if (TryCloseYouTubeMiniPlayerListPopup(
                YouTubeMiniPlayerItemMorePopup,
                _youTubeMiniPlayerListMoreTarget,
                item))
        {
            return;
        }

        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        YouTubeMiniPlayerVolumePopup.IsOpen = false;
        _youTubeMiniPlayerListMoreTarget = item;
        YouTubeMiniPlayerItemMorePopup.DataContext = item;
        YouTubeMiniPlayerItemMorePopup.PlacementTarget = button;
        YouTubeMiniPlayerItemMorePopup.IsOpen = true;
    }

    public void YouTubeMiniPlayerItemMorePopup_Closed(object sender, EventArgs e)
    {
        _youTubeMiniPlayerListMoreTarget = null;
        YouTubeMiniPlayerItemMorePopup.DataContext = null;
        YouTubeMiniPlayerItemMorePopup.PlacementTarget = null;
    }

    public async Task YouTubeMiniPlayerItemVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ToggleYouTubeMiniPlayerVolumePopupAsync(button, item);
    }

    private async Task ToggleYouTubeMiniPlayerVolumePopupAsync(
        Button button,
        YouTubeMiniPlayerListItem item)
    {
        if (TryCloseYouTubeMiniPlayerListPopup(
                YouTubeMiniPlayerVolumePopup,
                _youTubeMiniPlayerListVolumeTarget,
                item))
        {
            return;
        }

        if (!_host.CanControlVolume(item))
        {
            _host.SetStatus(item, "ミニプレイヤーをまだ操作できません");
            return;
        }

        await FlushPendingYouTubeMiniPlayerListVolumeAsync();

        _youTubeMiniPlayerListVolumeTarget = item;
        using (_youTubeMiniPlayerVolumeInteractionCoordinator.EnterProgrammaticVolumeUpdate())
        {
            YouTubeMiniPlayerVolumeTitleText.Text = item.Title;
            YouTubeMiniPlayerVolumeSlider.Value = Math.Clamp((item.State.Volume ?? 1) * 100, 0, 100);
            YouTubeMiniPlayerVolumeValueText.Text = $"{YouTubeMiniPlayerVolumeSlider.Value:0}%";
        }

        UpdateYouTubeMiniPlayerAudioBoostControls(item);
        YouTubeMiniPlayerVolumePopup.PlacementTarget = button;
        YouTubeMiniPlayerVolumePopup.IsOpen = true;
    }

    public async Task YouTubeMiniPlayerAudioBoostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_youTubeMiniPlayerListVolumeTarget is not { } item
            || sender is not Button button
            || !int.TryParse(button.Tag?.ToString(), out var audioBoostDecibels)
            || !_youTubeMiniPlayerVolumeInteractionCoordinator.TryEnterAudioBoost(
                out var audioBoostLease))
        {
            return;
        }

        e.Handled = true;
        UpdateYouTubeMiniPlayerAudioBoostControls(item);
        try
        {
            var changed = await _host.SetAudioBoostAsync(item, audioBoostDecibels);
            if (!changed)
            {
                return;
            }

            _host.QueueSave(item);
            _host.SetStatus(
                item,
                item.State.AudioBoostDecibels == 0
                    ? "音量ブーストをOFFにしました"
                    : $"音量ブーストを+{item.State.AudioBoostDecibels} dBにしました");
            await _host.RefreshListAsync();
        }
        finally
        {
            audioBoostLease!.Dispose();
            if (ReferenceEquals(_youTubeMiniPlayerListVolumeTarget, item))
            {
                UpdateYouTubeMiniPlayerAudioBoostControls(item);
            }
        }
    }

    private void UpdateYouTubeMiniPlayerAudioBoostControls(YouTubeMiniPlayerListItem item)
    {
        var isYouTube = item.State.SourceKind == MiniPlayerSourceKind.YouTube;
        var audioBoostDecibels = isYouTube
            ? YouTubeMiniPlayerAudioBoostProfile.Normalize(item.State.AudioBoostDecibels)
            : 0;
        YouTubeMiniPlayerAudioBoostPanel.IsEnabled = isYouTube
            && !_youTubeMiniPlayerVolumeInteractionCoordinator.IsAudioBoostRunning;
        YouTubeMiniPlayerAudioBoostValueText.Text = audioBoostDecibels == 0
            ? "OFF"
            : $"+{audioBoostDecibels} dB";
        YouTubeMiniPlayerAudioBoostHintText.Text = isYouTube
            ? "+6 dB推奨。+9 dBは音割れに注意"
            : "ローカル動画では利用できません";

        foreach (var boostButton in new[]
                 {
                     YouTubeMiniPlayerAudioBoostOffButton,
                     YouTubeMiniPlayerAudioBoost3Button,
                     YouTubeMiniPlayerAudioBoost6Button,
                     YouTubeMiniPlayerAudioBoost9Button
                 })
        {
            var isSelected = int.TryParse(boostButton.Tag?.ToString(), out var value)
                && value == audioBoostDecibels;
            if (isSelected)
            {
                boostButton.Background = new SolidColorBrush(Color.FromRgb(120, 53, 15));
                boostButton.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                boostButton.Foreground = new SolidColorBrush(Color.FromRgb(255, 251, 235));
            }
            else
            {
                boostButton.ClearValue(Control.BackgroundProperty);
                boostButton.ClearValue(Control.BorderBrushProperty);
                boostButton.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    public void YouTubeMiniPlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (YouTubeMiniPlayerVolumeValueText is not null)
        {
            YouTubeMiniPlayerVolumeValueText.Text = $"{e.NewValue:0}%";
        }

        if (_isStopped
            || _youTubeMiniPlayerVolumeInteractionCoordinator.IsProgrammaticVolumeUpdate
            || _youTubeMiniPlayerListVolumeTarget is null)
        {
            return;
        }

        _youTubeMiniPlayerVolumeInteractionCoordinator.SetPendingVolumePercent(e.NewValue);
        _youTubeMiniPlayerListVolumeTimer.Stop();
        _youTubeMiniPlayerListVolumeTimer.Start();
    }

    public async Task YouTubeMiniPlayerVolumePopup_Closed(object sender, EventArgs e)
    {
        await FlushPendingYouTubeMiniPlayerListVolumeAsync();

        _youTubeMiniPlayerListVolumeTarget = null;
        YouTubeMiniPlayerVolumePopup.PlacementTarget = null;
    }

    private async Task ApplyPendingYouTubeMiniPlayerListVolumeAsync()
    {
        if (_youTubeMiniPlayerListVolumeTarget is not { } item
            || !_youTubeMiniPlayerVolumeInteractionCoordinator.TryTakePendingVolume(
                out var normalizedVolume))
        {
            return;
        }

        var changed = await _host.SetVolumeAsync(item, normalizedVolume);
        if (changed)
        {
            _host.QueueSave(item);
            await _host.RefreshListAsync();
        }
    }

    private async Task FlushPendingYouTubeMiniPlayerListVolumeAsync()
    {
        _youTubeMiniPlayerListVolumeTimer.Stop();
        await _youTubeMiniPlayerListVolumeApplyTask;
        if (_youTubeMiniPlayerVolumeInteractionCoordinator.HasPendingVolume)
        {
            _youTubeMiniPlayerListVolumeApplyTask = ApplyPendingYouTubeMiniPlayerListVolumeAsync();
            await _youTubeMiniPlayerListVolumeApplyTask;
        }
    }

    public async Task YouTubeMiniPlayerItemSeekBackButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteYouTubeMiniPlayerListSeekAsync(sender, e, -10);
    }

    public async Task YouTubeMiniPlayerItemSeekForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteYouTubeMiniPlayerListSeekAsync(sender, e, 10);
    }

    public async Task YouTubeMiniPlayerItemLiveEdgeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        if (!item.CanSeekToLiveEdge)
        {
            return;
        }

        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.SeekToLiveEdgeActionId,
            item);
    }

    private async Task ExecuteYouTubeMiniPlayerListSeekAsync(object sender, RoutedEventArgs e, double deltaSeconds)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.SeekRelativeActionId,
            item,
            deltaSeconds);
    }

    public async Task YouTubeMiniPlayerItemLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.ToggleLoopActionId,
            item);
    }

    public void YouTubeMiniPlayerItemAbRepeatMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        if (TryCloseYouTubeMiniPlayerListPopup(
                YouTubeMiniPlayerAbRepeatPopup,
                _youTubeMiniPlayerListAbRepeatTarget,
                item))
        {
            return;
        }

        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        _youTubeMiniPlayerListAbRepeatTarget = item;
        UpdateYouTubeMiniPlayerAbRepeatPopup(item);
        YouTubeMiniPlayerAbRepeatPopup.PlacementTarget = button;
        YouTubeMiniPlayerAbRepeatPopup.IsOpen = true;
    }

    public async Task YouTubeMiniPlayerAbRepeatActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || _youTubeMiniPlayerListAbRepeatTarget is not { } item)
        {
            return;
        }

        var action = button.Tag?.ToString() switch
        {
            "SetStart" => YouTubeMiniPlayerAbRepeatAction.SetStart,
            "SetEnd" => YouTubeMiniPlayerAbRepeatAction.SetEnd,
            "Toggle" => YouTubeMiniPlayerAbRepeatAction.Toggle,
            "Clear" => YouTubeMiniPlayerAbRepeatAction.Clear,
            _ => (YouTubeMiniPlayerAbRepeatAction?)null
        };
        if (action is null)
        {
            return;
        }

        e.Handled = true;
        YouTubeMiniPlayerAbRepeatPopup.IsOpen = false;
        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.ExecuteAbRepeatActionId,
            item,
            action.Value);
    }

    public void YouTubeMiniPlayerAbRepeatPopup_Closed(object sender, EventArgs e)
    {
        _youTubeMiniPlayerListAbRepeatTarget = null;
        YouTubeMiniPlayerAbRepeatPopup.PlacementTarget = null;
    }

    private void UpdateYouTubeMiniPlayerAbRepeatPopup(YouTubeMiniPlayerListItem item)
    {
        YouTubeMiniPlayerAbRepeatTitleText.Text = item.Title;
        YouTubeMiniPlayerAbRepeatRangeText.Text = item.AbRepeatRangeText;
        YouTubeMiniPlayerAbRepeatSetStartButton.IsEnabled = item.CanSetAbRepeatPoint;
        YouTubeMiniPlayerAbRepeatSetEndButton.IsEnabled = item.CanSetAbRepeatPoint
            && item.State.AbRepeatStartSeconds.HasValue;
        YouTubeMiniPlayerAbRepeatToggleButton.IsEnabled =
            YouTubeMiniPlayerAbRepeatState.HasValidRange(item.State);
        YouTubeMiniPlayerAbRepeatToggleButton.Content = item.State.IsAbRepeatEnabled
            ? "一時解除"
            : "開始";
        YouTubeMiniPlayerAbRepeatClearButton.IsEnabled = item.State.AbRepeatStartSeconds.HasValue
            || item.State.AbRepeatEndSeconds.HasValue;
    }

    public void YouTubeMiniPlayerItemSpeedMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        if (TryCloseYouTubeMiniPlayerListPopup(
                YouTubeMiniPlayerSpeedPopup,
                _youTubeMiniPlayerListSpeedTarget,
                item))
        {
            return;
        }

        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        _youTubeMiniPlayerListSpeedTarget = item;
        YouTubeMiniPlayerSpeedPopup.PlacementTarget = button;
        YouTubeMiniPlayerSpeedPopup.IsOpen = true;
    }

    public async Task YouTubeMiniPlayerItemSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || _youTubeMiniPlayerListSpeedTarget is not { } item)
        {
            return;
        }

        var speed = button.Tag?.ToString() switch
        {
            "0.25" => 0.25,
            "0.5" => 0.5,
            "0.75" => 0.75,
            "1" => 1.0,
            "1.25" => 1.25,
            "1.5" => 1.5,
            "1.75" => 1.75,
            "2" => 2.0,
            _ => 0
        };
        if (speed <= 0)
        {
            return;
        }

        e.Handled = true;
        YouTubeMiniPlayerSpeedPopup.IsOpen = false;
        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.SetSpeedActionId,
            item,
            speed);
    }

    public void YouTubeMiniPlayerSpeedPopup_Closed(object sender, EventArgs e)
    {
        _youTubeMiniPlayerListSpeedTarget = null;
        YouTubeMiniPlayerSpeedPopup.PlacementTarget = null;
    }

    public async Task YouTubeMiniPlayerItemCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        if (_youTubeMiniPlayerListMoreTarget?.State.Id == item.State.Id)
        {
            YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        }

        await _host.ExecuteCommandAsync(
            MiniPlayerCommandProvider.CloseActionId,
            item);
    }

    private static bool TryCloseYouTubeMiniPlayerListPopup(
        Popup popup,
        YouTubeMiniPlayerListItem? popupTarget,
        YouTubeMiniPlayerListItem clickedItem)
    {
        if (!popup.IsOpen || popupTarget?.State.Id != clickedItem.State.Id)
        {
            return false;
        }

        popup.IsOpen = false;
        return true;
    }
}
