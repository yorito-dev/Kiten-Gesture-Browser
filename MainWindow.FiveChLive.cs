using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow
{
    private readonly FiveChLiveReadabilityService _fiveChLiveReadabilityService = new();

    private void QueueFiveChLiveReadabilityAttempt(BrowserTab tab)
    {
        _ = ApplyFiveChLiveReadabilityAsync(tab);
    }

    private void ApplyFiveChLiveReadabilityToLoadedTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            _ = ApplyFiveChLiveReadabilityAsync(tab);
        }
    }

    private async Task ApplyFiveChLiveReadabilityAsync(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)
            || tab.IsWorkspaceTab
            || GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !_fiveChLiveReadabilityService.IsFiveChThreadUrl(GetCurrentPageUrl(tab)))
        {
            return;
        }

        try
        {
            _settings.FiveChLiveReadability = FiveChLiveReadabilityProfile.Normalize(_settings.FiveChLiveReadability);
            await core.ExecuteScriptAsync(
                _fiveChLiveReadabilityService.BuildApplyScript(
                    _settings.FiveChLiveReadability,
                    IsFiveChLiveReadabilityVisible(tab)));
        }
        catch
        {
            // 遷移中やHTML構造が異なる場合は通常表示を維持し、次の完了通知で再試行する。
        }
    }

    private bool IsFiveChLiveReadabilityVisible(BrowserTab tab)
    {
        return ActiveTab == tab || IsVisibleOrPreparingSplitPaneNavigation(tab);
    }

    private void QueueFiveChLiveReadabilityActivation(BrowserTab tab, bool isActive)
    {
        _ = SetFiveChLiveReadabilityActivationAsync(tab, isActive);
    }

    private async Task SetFiveChLiveReadabilityActivationAsync(BrowserTab tab, bool isActive)
    {
        if (!_tabs.Contains(tab)
            || tab.IsWorkspaceTab
            || GetTabWebView(tab)?.CoreWebView2 is not { } core
            || !_fiveChLiveReadabilityService.IsFiveChThreadUrl(GetCurrentPageUrl(tab)))
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(_fiveChLiveReadabilityService.BuildSetActiveScript(isActive));
        }
        catch
        {
            // タブ遷移中は次の適用または表示切替で状態を同期する。
        }
    }
}
