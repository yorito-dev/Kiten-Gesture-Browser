using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;
using static TabBrowser.Views.YouTubeControlPanelPresentation;

namespace TabBrowser.Views;

internal sealed record YouTubeControlPanelElements(
    Popup YouTubeControlPopup,
    FrameworkElement YouTubeControlPanel,
    Button YouTubeSpeedDownButton,
    Button YouTubeSpeedResetButton,
    Button YouTubeSpeedUpButton,
    Button YouTubeLoopButton,
    Popup SplitLeftYouTubeControlPopup,
    FrameworkElement SplitLeftYouTubeControlPanel,
    Button SplitLeftYouTubeSpeedDownButton,
    Button SplitLeftYouTubeSpeedResetButton,
    Button SplitLeftYouTubeSpeedUpButton,
    Button SplitLeftYouTubeLoopButton,
    Popup SplitRightYouTubeControlPopup,
    FrameworkElement SplitRightYouTubeControlPanel,
    Button SplitRightYouTubeSpeedDownButton,
    Button SplitRightYouTubeSpeedResetButton,
    Button SplitRightYouTubeSpeedUpButton,
    Button SplitRightYouTubeLoopButton,
    FrameworkElement WebHost,
    FrameworkElement SplitLeftPaneHost,
    FrameworkElement SplitRightPaneHost);

internal interface IYouTubeControlPanelHost
{
    bool IsActive { get; }
    bool IsClosing { get; }
    BrowserTab? ActiveTab { get; }
    bool IsSplitViewVisible();
    bool TryGetActiveYouTubeTab(out BrowserTab tab);
    bool TryGetSplitPaneYouTubeTab(string pane, out BrowserTab tab);
    BrowserTab? GetTabForPane(string pane);
    bool ContainsSplitTab(BrowserTab tab);
    bool IsYouTubeVideoTab(BrowserTab? tab);
    bool IsYouTubeSpeedDisplayTarget(BrowserTab tab);
    Task<(double HorizontalOffset, double VerticalOffset)?> GetPlacementAsync(
        BrowserTab tab, FrameworkElement host, FrameworkElement panel);
    Task RefreshControlDisplayAsync(BrowserTab tab);
    Point GetElementOriginInWebHost(FrameworkElement element);
    void AppendSplitYouTubeControlDiagnostic(
        string operation, string reason, string pane, BrowserTab? tab,
        Popup popup, FrameworkElement host, string result);
    string GetYouTubeControlIneligibleReason(BrowserTab? tab);
}

// 通常／分割の表示と更新予約を所有する。WebView操作・入力検証はWindow側へ委譲する。
internal sealed class YouTubeControlPanelController : IDisposable
{
    internal const int YouTubeControlIdleHideDelayMilliseconds = 3500;
    private static readonly int[] SplitYouTubeControlPanelRefreshDelaysMilliseconds = [120, 400, 1200, 2600];

    private readonly YouTubeControlPanelElements _elements;
    private readonly IYouTubeControlPanelHost _host;
    private readonly YouTubeControlWorkCoordinator _youTubeControlWorkCoordinator;
    private readonly WindowTaskSupervisor _windowTaskSupervisor;
    private readonly OwnedDispatcherTimer _youtubeControlPlacementTimer;
    private readonly Dispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private bool _disposed;

    internal YouTubeControlPanelController(
        YouTubeControlPanelElements elements,
        IYouTubeControlPanelHost host,
        YouTubeControlWorkCoordinator workCoordinator,
        WindowTaskSupervisor windowTaskSupervisor,
        OwnedDispatcherTimer placementTimer,
        Func<DateTimeOffset>? now = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        _elements = elements;
        _host = host;
        _youTubeControlWorkCoordinator = workCoordinator;
        _windowTaskSupervisor = windowTaskSupervisor;
        _youtubeControlPlacementTimer = placementTimer;
        _dispatcher = elements.WebHost.Dispatcher;
        _now = now ?? (() => DateTimeOffset.Now);
        _delayAsync = delayAsync ?? Task.Delay;
        _youtubeControlPlacementTimer.Tick += PlacementTimer_Tick;
        AttachYouTubeControlPanelInteractionHandlers(_elements.YouTubeControlPanel);
        AttachYouTubeControlPanelInteractionHandlers(_elements.SplitLeftYouTubeControlPanel);
        AttachYouTubeControlPanelInteractionHandlers(_elements.SplitRightYouTubeControlPanel);
    }

    private bool IsClosing => _disposed || _host.IsClosing;

    private void PlacementTimer_Tick(object? sender, EventArgs e)
    {
        _youtubeControlPlacementTimer.Stop();
        if (_disposed)
        {
            return;
        }

        UpdateYouTubeControlPopupPlacement();
        if (_elements.YouTubeControlPopup.IsOpen
            || _elements.SplitLeftYouTubeControlPopup.IsOpen
            || _elements.SplitRightYouTubeControlPopup.IsOpen)
        {
            _youtubeControlPlacementTimer.Start();
        }
    }

    private void Panel_MouseActivity(object sender, MouseEventArgs e)
    {
        if (!_disposed)
        {
            NoteYouTubeControlPanelInteraction();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _youtubeControlPlacementTimer.Stop();
        _youtubeControlPlacementTimer.Tick -= PlacementTimer_Tick;
        DetachYouTubeControlPanelInteractionHandlers(_elements.YouTubeControlPanel);
        DetachYouTubeControlPanelInteractionHandlers(_elements.SplitLeftYouTubeControlPanel);
        DetachYouTubeControlPanelInteractionHandlers(_elements.SplitRightYouTubeControlPanel);
    }

    private void DetachYouTubeControlPanelInteractionHandlers(UIElement panel)
    {
        panel.MouseEnter -= Panel_MouseActivity;
        panel.MouseMove -= Panel_MouseActivity;
        panel.PreviewMouseDown -= Panel_MouseActivity;
        panel.PreviewMouseWheel -= Panel_MouseActivity;
    }

    internal void UpdateYouTubeSpeedDisplay(double? speed)
    {
        UpdateYouTubeSpeedDisplay(null, speed);
    }

    internal void UpdateYouTubeSpeedDisplay(BrowserTab? tab, double? speed)
    {
        if (tab is null || _host.ActiveTab?.Id == tab.Id)
        {
            SetYouTubeSpeedResetContent(_elements.YouTubeSpeedResetButton, speed);
        }

        var leftTab = _host.GetTabForPane(SplitViewPane.Left);
        if (tab is null || leftTab?.Id == tab.Id)
        {
            SetYouTubeSpeedResetContent(_elements.SplitLeftYouTubeSpeedResetButton, speed);
        }

        var rightTab = _host.GetTabForPane(SplitViewPane.Right);
        if (tab is null || rightTab?.Id == tab.Id)
        {
            SetYouTubeSpeedResetContent(_elements.SplitRightYouTubeSpeedResetButton, speed);
        }
    }

    internal void UpdateYouTubeLoopDisplay(bool? isLooping)
    {
        UpdateYouTubeLoopDisplay(null, isLooping);
    }

    internal void UpdateYouTubeLoopDisplay(BrowserTab? tab, bool? isLooping)
    {
        if (tab is null || _host.ActiveTab?.Id == tab.Id)
        {
            SetYouTubeLoopContent(_elements.YouTubeLoopButton, isLooping);
        }

        var leftTab = _host.GetTabForPane(SplitViewPane.Left);
        if (tab is null || leftTab?.Id == tab.Id)
        {
            SetYouTubeLoopContent(_elements.SplitLeftYouTubeLoopButton, isLooping);
        }

        var rightTab = _host.GetTabForPane(SplitViewPane.Right);
        if (tab is null || rightTab?.Id == tab.Id)
        {
            SetYouTubeLoopContent(_elements.SplitRightYouTubeLoopButton, isLooping);
        }
    }

    private async Task RefreshYouTubeControlDisplayFallbackAsync(BrowserTab tab)
    {
        if (!_host.IsYouTubeSpeedDisplayTarget(tab)
            || (tab.YouTubePlaybackSpeed.HasValue && tab.YouTubePlaybackIsLooping.HasValue)
            || !_youTubeControlWorkCoordinator.TryBeginDisplayFallback(tab.Id))
        {
            return;
        }

        await _host.RefreshControlDisplayAsync(tab);
    }

    internal void UpdateYouTubeControlPanelState(bool canControlYouTube)
    {
        if (!_host.IsActive)
        {
            SuspendYouTubeControlPopupsForInactiveWindow();
            return;
        }

        if (_host.IsSplitViewVisible())
        {
            _elements.YouTubeControlPopup.IsOpen = false;
            _elements.YouTubeSpeedDownButton.IsEnabled = false;
            _elements.YouTubeSpeedResetButton.IsEnabled = false;
            _elements.YouTubeSpeedUpButton.IsEnabled = false;
            _elements.YouTubeLoopButton.IsEnabled = false;

            if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
            {
                _youtubeControlPlacementTimer.Stop();
                HideSplitYouTubeControlPopups();
                return;
            }

            if (HasSplitYouTubeControlPanelRefreshTarget())
            {
                QueueSplitYouTubeControlPanelRefresh();
            }
            else
            {
                _youtubeControlPlacementTimer.Stop();
                HideSplitYouTubeControlPopups();
            }

            return;
        }

        HideSplitYouTubeControlPopups();
        _elements.YouTubeSpeedDownButton.IsEnabled = canControlYouTube;
        _elements.YouTubeSpeedResetButton.IsEnabled = canControlYouTube;
        _elements.YouTubeSpeedUpButton.IsEnabled = canControlYouTube;
        _elements.YouTubeLoopButton.IsEnabled = canControlYouTube;

        if (!canControlYouTube)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            UpdateYouTubeSpeedDisplay(null);
            UpdateYouTubeLoopDisplay(null);
            return;
        }

        if (_host.TryGetActiveYouTubeTab(out var activeYouTubeTab))
        {
            UpdateYouTubeSpeedDisplay(activeYouTubeTab, activeYouTubeTab.YouTubePlaybackSpeed);
            UpdateYouTubeLoopDisplay(activeYouTubeTab, activeYouTubeTab.YouTubePlaybackIsLooping);
        }

        if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            return;
        }

        var wasOpen = _elements.YouTubeControlPopup.IsOpen;
        QueueYouTubeControlPopupPlacementUpdate(openAfterPlacement: true);

        if (!wasOpen)
        {
            _dispatcher.BeginInvoke(new Action(UpdateYouTubeControlPopupPlacement), DispatcherPriority.Loaded);
        }
    }

    internal void UpdateYouTubeControlPopupPlacement()
    {
        if (!_host.IsActive)
        {
            SuspendYouTubeControlPopupsForInactiveWindow();
            return;
        }

        if (IsClosing)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            HideSplitYouTubeControlPopups();
            return;
        }

        if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            HideSplitYouTubeControlPopups();
            return;
        }

        if (_host.IsSplitViewVisible())
        {
            _elements.YouTubeControlPopup.IsOpen = false;
            if (!UpdateSplitYouTubeControlPanels("timer"))
            {
                _youtubeControlPlacementTimer.Stop();
            }

            return;
        }

        HideSplitYouTubeControlPopups();
        QueueYouTubeControlPopupPlacementUpdate(openAfterPlacement: _host.TryGetActiveYouTubeTab(out _));
    }

    internal void QueueYouTubeControlPlacementAfterWindowChange()
    {
        if (_youTubeControlWorkCoordinator.IsWindowPlacementQueued
            || (!_elements.YouTubeControlPopup.IsOpen
            && !_elements.SplitLeftYouTubeControlPopup.IsOpen
            && !_elements.SplitRightYouTubeControlPopup.IsOpen))
        {
            return;
        }

        _youTubeControlWorkCoordinator.MarkWindowPlacementQueued();
        _dispatcher.BeginInvoke(
            new Action(() =>
            {
                _youTubeControlWorkCoordinator.CompleteWindowPlacementQueue();
                if (!_disposed)
                {
                    UpdateYouTubeControlPopupPlacement();
                }
            }),
            DispatcherPriority.Background);
    }

    internal void RepositionOpenYouTubeControlPopupsAfterWindowChange()
    {
        RepositionOpenYouTubeControlPopup(
            _elements.YouTubeControlPopup,
            _elements.YouTubeControlPanel);
        RepositionOpenYouTubeControlPopup(
            _elements.SplitLeftYouTubeControlPopup,
            _elements.SplitLeftYouTubeControlPanel);
        RepositionOpenYouTubeControlPopup(
            _elements.SplitRightYouTubeControlPopup,
            _elements.SplitRightYouTubeControlPanel);
    }

    private void RepositionOpenYouTubeControlPopup(Popup popup, FrameworkElement panel)
    {
        if (!popup.IsOpen)
        {
            return;
        }

        var panelSize = GetYouTubeControlPanelSize(panel);
        PopupWindowOrderService.ApplyRelativeBounds(
            popup,
            panel,
            _elements.WebHost,
            _elements.WebHost,
            new Rect(
                popup.HorizontalOffset,
                popup.VerticalOffset,
                panelSize.Width,
                panelSize.Height));
    }

    private void QueueYouTubeControlPopupPlacementUpdate(
        bool openAfterPlacement,
        bool allowInactiveWindow = false)
    {
        if (IsClosing)
        {
            return;
        }

        _windowTaskSupervisor.TryRun(
            "youtube-control-popup-placement",
            cancellationToken => UpdateYouTubeControlPopupPlacementAsync(
                openAfterPlacement,
                allowInactiveWindow,
                cancellationToken));
    }

    private async Task UpdateYouTubeControlPopupPlacementAsync(
        bool openAfterPlacement,
        bool allowInactiveWindow,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!_host.IsActive && !allowInactiveWindow)
        {
            SuspendYouTubeControlPopupsForInactiveWindow();
            return;
        }

        if (IsClosing)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            HideSplitYouTubeControlPopups();
            return;
        }

        if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            HideSplitYouTubeControlPopups();
            return;
        }

        if (_host.IsSplitViewVisible())
        {
            _elements.YouTubeControlPopup.IsOpen = false;
            UpdateSplitYouTubeControlPanels("placement-async", allowInactiveWindow);
            return;
        }

        if (_youTubeControlWorkCoordinator.IsPopupPlacementRunning)
        {
            return;
        }

        if (!_host.TryGetActiveYouTubeTab(out var tab))
        {
            _youtubeControlPlacementTimer.Stop();
            _elements.YouTubeControlPopup.IsOpen = false;
            return;
        }

        IDisposable? popupPlacementLease = null;
        try
        {
            popupPlacementLease = _youTubeControlWorkCoordinator.EnterPopupPlacement();
            var placement = await _host.GetPlacementAsync(tab, _elements.WebHost, _elements.YouTubeControlPanel);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsClosing
                || (!_host.IsActive && !allowInactiveWindow)
                || _youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity
                || _host.ActiveTab?.Id != tab.Id
                || !_host.TryGetActiveYouTubeTab(out _))
            {
                _elements.YouTubeControlPopup.IsOpen = false;
                return;
            }

            if (placement is not { } value)
            {
                if (_elements.YouTubeControlPopup.IsOpen && HasRecentYouTubeControlPanelInteraction())
                {
                    return;
                }

                _elements.YouTubeControlPopup.IsOpen = false;
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetYouTubeControlPopupPlacement(
                _elements.YouTubeControlPopup, _elements.YouTubeControlPanel, _elements.WebHost,
                value.HorizontalOffset, value.VerticalOffset,
                _youTubeControlWorkCoordinator.IsApplicationFullscreenActive);
            if (openAfterPlacement || _elements.YouTubeControlPopup.IsOpen)
            {
                await RefreshYouTubeControlDisplayFallbackAsync(tab);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsClosing
                || (!_host.IsActive && !allowInactiveWindow)
                || _youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity
                || _host.ActiveTab?.Id != tab.Id
                || !_host.TryGetActiveYouTubeTab(out _))
            {
                _elements.YouTubeControlPopup.IsOpen = false;
                return;
            }

            if (openAfterPlacement)
            {
                _elements.YouTubeControlPopup.IsOpen = true;
            }

            if (_elements.YouTubeControlPopup.IsOpen)
            {
                ApplyOwnerRelativeWindowOrder(
                    _elements.YouTubeControlPopup,
                    _elements.YouTubeControlPanel);
            }
        }
        finally
        {
            popupPlacementLease?.Dispose();
        }
    }

    internal bool UpdateSplitYouTubeControlPanels(
        string reason,
        bool allowInactiveWindow = false)
    {
        if (!_host.IsActive && !allowInactiveWindow)
        {
            SuspendYouTubeControlPopupsForInactiveWindow();
            return false;
        }

        if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
        {
            HideSplitYouTubeControlPopups();
            return false;
        }

        if (!_host.IsSplitViewVisible())
        {
            HideSplitYouTubeControlPopups();
            return false;
        }

        var hasLeftPanel = UpdateSplitYouTubeControlPanelForPane(
            reason,
            SplitViewPane.Left,
            _elements.SplitLeftYouTubeControlPopup,
            _elements.SplitLeftYouTubeControlPanel,
            _elements.SplitLeftPaneHost,
            _elements.SplitLeftYouTubeSpeedDownButton,
            _elements.SplitLeftYouTubeSpeedResetButton,
            _elements.SplitLeftYouTubeSpeedUpButton,
            _elements.SplitLeftYouTubeLoopButton,
            allowInactiveWindow);
        var hasRightPanel = UpdateSplitYouTubeControlPanelForPane(
            reason,
            SplitViewPane.Right,
            _elements.SplitRightYouTubeControlPopup,
            _elements.SplitRightYouTubeControlPanel,
            _elements.SplitRightPaneHost,
            _elements.SplitRightYouTubeSpeedDownButton,
            _elements.SplitRightYouTubeSpeedResetButton,
            _elements.SplitRightYouTubeSpeedUpButton,
            _elements.SplitRightYouTubeLoopButton,
            allowInactiveWindow);

        return hasLeftPanel || hasRightPanel;
    }

    private bool UpdateSplitYouTubeControlPanelForPane(
        string reason,
        string pane,
        Popup popup,
        FrameworkElement panel,
        FrameworkElement host,
        Button speedDownButton,
        Button speedResetButton,
        Button speedUpButton,
        Button loopButton,
        bool allowInactiveWindow)
    {
        if (!_host.TryGetSplitPaneYouTubeTab(pane, out var tab))
        {
            var paneTab = _host.IsSplitViewVisible()
                ? _host.GetTabForPane(pane)
                : null;
            var wasOpen = popup.IsOpen;
            popup.IsOpen = false;
            speedDownButton.IsEnabled = false;
            speedResetButton.IsEnabled = false;
            speedUpButton.IsEnabled = false;
            loopButton.IsEnabled = false;
            SetYouTubeSpeedResetContent(speedResetButton, null);
            SetYouTubeLoopContent(loopButton, null);
            if (wasOpen)
            {
                _host.AppendSplitYouTubeControlDiagnostic(
                    "split-youtube-panel-hide",
                    reason,
                    pane,
                    paneTab,
                    popup,
                    host,
                    _host.GetYouTubeControlIneligibleReason(paneTab));
            }
            return false;
        }

        QueueSplitYouTubeControlPanelForPaneUpdate(
            reason,
            pane,
            tab,
            popup,
            panel,
            host,
            speedDownButton,
            speedResetButton,
            speedUpButton,
            loopButton,
            allowInactiveWindow);
        return true;
    }

    private void QueueSplitYouTubeControlPanelForPaneUpdate(
        string reason,
        string pane,
        BrowserTab tab,
        Popup popup,
        FrameworkElement panel,
        FrameworkElement host,
        Button speedDownButton,
        Button speedResetButton,
        Button speedUpButton,
        Button loopButton,
        bool allowInactiveWindow)
    {
        _windowTaskSupervisor.TryRun(
            "split-youtube-control-pane-update",
            cancellationToken => UpdateSplitYouTubeControlPanelForPaneAsync(
                reason,
                pane,
                tab,
                popup,
                panel,
                host,
                speedDownButton,
                speedResetButton,
                speedUpButton,
                loopButton,
                allowInactiveWindow,
                cancellationToken));
    }

    private async Task UpdateSplitYouTubeControlPanelForPaneAsync(
        string reason,
        string pane,
        BrowserTab tab,
        Popup popup,
        FrameworkElement panel,
        FrameworkElement host,
        Button speedDownButton,
        Button speedResetButton,
        Button speedUpButton,
        Button loopButton,
        bool allowInactiveWindow,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var wasOpen = popup.IsOpen;
            var placement = await _host.GetPlacementAsync(tab, host, panel);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsClosing
                || (!_host.IsActive && !allowInactiveWindow)
                || _youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity
                || !_host.IsSplitViewVisible()
                || _host.GetTabForPane(pane)?.Id != tab.Id
                || !_host.IsYouTubeVideoTab(tab))
            {
                popup.IsOpen = false;
                return;
            }

            if (placement is not { } value)
            {
                if (popup.IsOpen && HasRecentYouTubeControlPanelInteraction())
                {
                    return;
                }

                var wasVisible = popup.IsOpen;
                popup.IsOpen = false;
                speedDownButton.IsEnabled = false;
                speedResetButton.IsEnabled = false;
                speedUpButton.IsEnabled = false;
                loopButton.IsEnabled = false;
                if (wasVisible)
                {
                    _host.AppendSplitYouTubeControlDiagnostic(
                        "split-youtube-panel-hide",
                        reason,
                        pane,
                        tab,
                        popup,
                        host,
                        "player-inactive");
                }
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            speedDownButton.IsEnabled = true;
            speedResetButton.IsEnabled = true;
            speedUpButton.IsEnabled = true;
            loopButton.IsEnabled = true;
            SetYouTubeSpeedResetContent(speedResetButton, tab.YouTubePlaybackSpeed);
            SetYouTubeLoopContent(loopButton, tab.YouTubePlaybackIsLooping);
            SetSplitYouTubeControlPopupPlacement(
                popup, panel, host, value.HorizontalOffset, value.VerticalOffset,
                _elements.WebHost, _host.GetElementOriginInWebHost(host));
            await RefreshYouTubeControlDisplayFallbackAsync(tab);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (IsClosing
                || (!_host.IsActive && !allowInactiveWindow)
                || _youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity
                || !_host.IsSplitViewVisible()
                || _host.GetTabForPane(pane)?.Id != tab.Id
                || !_host.IsYouTubeVideoTab(tab))
            {
                popup.IsOpen = false;
                return;
            }

            popup.IsOpen = true;
            ApplyOwnerRelativeWindowOrder(popup, panel);
            if (!wasOpen)
            {
                _host.AppendSplitYouTubeControlDiagnostic(
                    "split-youtube-panel-show",
                    reason,
                    pane,
                    tab,
                    popup,
                    host,
                    "ok");
            }
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                popup.IsOpen = false;
            }
        }
    }

    internal void HideSplitYouTubeControlPopups()
    {
        _elements.SplitLeftYouTubeControlPopup.IsOpen = false;
        _elements.SplitRightYouTubeControlPopup.IsOpen = false;
        _elements.SplitLeftYouTubeSpeedDownButton.IsEnabled = false;
        _elements.SplitLeftYouTubeSpeedResetButton.IsEnabled = false;
        _elements.SplitLeftYouTubeSpeedUpButton.IsEnabled = false;
        _elements.SplitLeftYouTubeLoopButton.IsEnabled = false;
        _elements.SplitRightYouTubeSpeedDownButton.IsEnabled = false;
        _elements.SplitRightYouTubeSpeedResetButton.IsEnabled = false;
        _elements.SplitRightYouTubeSpeedUpButton.IsEnabled = false;
        _elements.SplitRightYouTubeLoopButton.IsEnabled = false;
        SetYouTubeSpeedResetContent(_elements.SplitLeftYouTubeSpeedResetButton, null);
        SetYouTubeSpeedResetContent(_elements.SplitRightYouTubeSpeedResetButton, null);
        SetYouTubeLoopContent(_elements.SplitLeftYouTubeLoopButton, null);
        SetYouTubeLoopContent(_elements.SplitRightYouTubeLoopButton, null);
    }

    internal void SuspendYouTubeControlPopupsForInactiveWindow()
    {
        _youtubeControlPlacementTimer.Stop();
        _elements.YouTubeControlPopup.IsOpen = false;
        _elements.SplitLeftYouTubeControlPopup.IsOpen = false;
        _elements.SplitRightYouTubeControlPopup.IsOpen = false;
    }

    private void ApplyOwnerRelativeWindowOrder(Popup popup, FrameworkElement panel)
    {
        PopupWindowOrderService.ApplyRelativeToOwner(
            popup,
            panel,
            _elements.WebHost);
    }

    private void AttachYouTubeControlPanelInteractionHandlers(UIElement panel)
    {
        panel.MouseEnter += Panel_MouseActivity;
        panel.MouseMove += Panel_MouseActivity;
        panel.PreviewMouseDown += Panel_MouseActivity;
        panel.PreviewMouseWheel += Panel_MouseActivity;
    }

    private void NoteYouTubeControlPanelInteraction()
    {
        _youTubeControlWorkCoordinator.RecordPanelInteraction(_now());
        RestartYouTubeControlIdleCheck();
    }

    internal void HandleYouTubeControlActivity(BrowserTab sourceTab, string? reason)
    {
        if (IsClosing || !_host.IsYouTubeVideoTab(sourceTab))
        {
            return;
        }

        if (_host.IsSplitViewVisible())
        {
            if (!_host.ContainsSplitTab(sourceTab))
            {
                return;
            }
        }
        else if (!ReferenceEquals(sourceTab, _host.ActiveTab))
        {
            return;
        }

        if (IsYouTubeControlScrollActivity(reason))
        {
            SuppressYouTubeControlPopupsUntilPointerActivity();
            return;
        }

        var resumedAfterSuppression = false;
        if (_youTubeControlWorkCoordinator.IsSuppressedUntilPointerActivity)
        {
            if (!CanResumeYouTubeControlAfterSuppression(reason))
            {
                return;
            }

            resumedAfterSuppression = _youTubeControlWorkCoordinator.ResumeFromPointerSuppression();
        }

        if (resumedAfterSuppression || !IsYouTubeControlIdleOnlyActivity(reason))
        {
            if (_host.IsSplitViewVisible())
            {
                UpdateSplitYouTubeControlPanels(
                    reason ?? "activity",
                    allowInactiveWindow: true);
            }
            else
            {
                QueueYouTubeControlPopupPlacementUpdate(
                    openAfterPlacement: true,
                    allowInactiveWindow: true);
            }
        }

        RestartYouTubeControlIdleCheck();
    }

    internal void ResetYouTubeControlSuppressionAfterNavigation(BrowserTab sourceTab)
    {
        if (_host.IsYouTubeVideoTab(sourceTab)
            && (ReferenceEquals(sourceTab, _host.ActiveTab)
                || (_host.IsSplitViewVisible() && _host.ContainsSplitTab(sourceTab))))
        {
            _youTubeControlWorkCoordinator.ClearPointerSuppression();
        }
    }

    private static bool IsYouTubeControlIdleOnlyActivity(string? reason)
    {
        return reason is "pointer-keepalive" or "pointer-leave" or "pointer-leave-player";
    }

    private static bool IsYouTubeControlScrollActivity(string? reason)
    {
        return reason == "page-scroll";
    }

    private static bool CanResumeYouTubeControlAfterSuppression(string? reason)
    {
        return reason is "pointer-enter" or "pointer-resume" or "pointer-keepalive";
    }

    private void SuppressYouTubeControlPopupsUntilPointerActivity()
    {
        _youTubeControlWorkCoordinator.SuppressUntilPointerActivity();
        _youtubeControlPlacementTimer.Stop();
        _elements.YouTubeControlPopup.IsOpen = false;
        HideSplitYouTubeControlPopups();
    }

    private void RestartYouTubeControlIdleCheck()
    {
        _youtubeControlPlacementTimer.Stop();
        _youtubeControlPlacementTimer.Start();
    }

    internal bool HasRecentYouTubeControlPanelInteraction()
    {
        return _youTubeControlWorkCoordinator.HasRecentPanelInteraction(
            _now(),
            TimeSpan.FromMilliseconds(YouTubeControlIdleHideDelayMilliseconds));
    }

    internal void QueueSplitYouTubeControlPanelRefresh()
    {
        var generation = _youTubeControlWorkCoordinator.BeginSplitPanelRefresh();
        if (!HasSplitYouTubeControlPanelRefreshTarget())
        {
            return;
        }

        QueueSplitYouTubeControlPanelRefresh(generation, DispatcherPriority.Render, "youtube-refresh-render");
        QueueSplitYouTubeControlPanelRefresh(generation, DispatcherPriority.Loaded, "youtube-refresh-loaded");
        QueueSplitYouTubeControlPanelRefresh(generation, DispatcherPriority.ContextIdle, "youtube-refresh-idle");
        _windowTaskSupervisor.TryRun(
            "split-youtube-control-panel-refresh-delays",
            cancellationToken => RefreshSplitYouTubeControlPanelsAfterDelaysAsync(generation, cancellationToken));
    }

    private void QueueSplitYouTubeControlPanelRefresh(
        int generation,
        DispatcherPriority priority,
        string reason)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsCurrentSplitYouTubeControlPanelRefresh(generation))
            {
                UpdateSplitYouTubeControlPanels(reason);
            }
        }), priority);
    }

    private bool HasSplitYouTubeControlPanelRefreshTarget()
    {
        return _host.IsSplitViewVisible()
            && (_elements.SplitLeftYouTubeControlPopup.IsOpen
                || _elements.SplitRightYouTubeControlPopup.IsOpen
                || _host.TryGetSplitPaneYouTubeTab(SplitViewPane.Left, out _)
                || _host.TryGetSplitPaneYouTubeTab(SplitViewPane.Right, out _));
    }

    private bool IsCurrentSplitYouTubeControlPanelRefresh(int generation)
    {
        return _youTubeControlWorkCoordinator.IsCurrentSplitPanelRefresh(generation)
            && !IsClosing
            && HasSplitYouTubeControlPanelRefreshTarget();
    }

    private async Task RefreshSplitYouTubeControlPanelsAfterDelaysAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in SplitYouTubeControlPanelRefreshDelaysMilliseconds)
            {
                await _delayAsync(delay, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!IsCurrentSplitYouTubeControlPanelRefresh(generation))
                {
                    return;
                }

                UpdateSplitYouTubeControlPanels($"youtube-refresh-delay-{delay}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

}
