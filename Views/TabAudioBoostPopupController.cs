using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record TabAudioBoostPopupElements(
    Popup TabAudioBoostPopup,
    FrameworkElement TabAudioBoostPanel,
    TextBlock TabAudioBoostTitleText,
    TextBlock TabAudioBoostValueText,
    Button TabAudioBoostOffButton,
    Button TabAudioBoost3Button,
    Button TabAudioBoost6Button,
    Button TabAudioBoost9Button);

internal interface ITabAudioBoostPopupHost
{
    Task<bool> SetBoostAsync(BrowserTab tab, int audioBoostDecibels);
    void QueueSessionSave();
    void SetStatus(string message);
}

// Popupの対象、適用中状態、表示と後始末を所有する。実WebViewと保存は既存のWindow入口へ委譲する。
internal sealed class TabAudioBoostPopupController(
    TabAudioBoostPopupElements elements,
    ITabAudioBoostPopupHost host)
{
    private readonly TabAudioBoostPopupElements _elements = elements;
    private readonly ITabAudioBoostPopupHost _host = host;
    private BrowserTab? _tabAudioBoostPopupTab;
    private bool _isApplyingTabAudioBoost;

    public void Show(BrowserTab tab, Button button)
    {
        _tabAudioBoostPopupTab = tab;
        _elements.TabAudioBoostTitleText.Text = tab.DisplayTitle;
        UpdateTabAudioBoostControls(tab);
        _elements.TabAudioBoostPopup.PlacementTarget = button;
        _elements.TabAudioBoostPopup.IsOpen = true;
    }

    public async Task HandleButtonClickAsync(object sender, RoutedEventArgs e)
    {
        if (_isApplyingTabAudioBoost
            || _tabAudioBoostPopupTab is not { } tab
            || sender is not Button button
            || !int.TryParse(button.Tag?.ToString(), out var audioBoostDecibels))
        {
            return;
        }

        e.Handled = true;
        _isApplyingTabAudioBoost = true;
        UpdateTabAudioBoostControls(tab);
        try
        {
            if (!await _host.SetBoostAsync(tab, audioBoostDecibels))
            {
                return;
            }

            _host.QueueSessionSave();
            _host.SetStatus(
                tab.AudioBoostDecibels == 0
                    ? "このタブの音量ブーストをOFFにしました"
                    : $"このタブの音量ブーストを+{tab.AudioBoostDecibels} dBにしました");
        }
        finally
        {
            _isApplyingTabAudioBoost = false;
            if (ReferenceEquals(_tabAudioBoostPopupTab, tab))
            {
                UpdateTabAudioBoostControls(tab);
            }
        }
    }

    private void UpdateTabAudioBoostControls(BrowserTab tab)
    {
        var audioBoostDecibels = AudioBoostProfile.Normalize(tab.AudioBoostDecibels);
        _elements.TabAudioBoostPanel.IsEnabled = !_isApplyingTabAudioBoost;
        _elements.TabAudioBoostValueText.Text = audioBoostDecibels == 0
            ? "OFF"
            : $"+{audioBoostDecibels} dB";

        foreach (var boostButton in new[]
                 {
                     _elements.TabAudioBoostOffButton,
                     _elements.TabAudioBoost3Button,
                     _elements.TabAudioBoost6Button,
                     _elements.TabAudioBoost9Button
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

    public void OnClosed()
    {
        _tabAudioBoostPopupTab = null;
        _elements.TabAudioBoostPopup.PlacementTarget = null;
    }
}
