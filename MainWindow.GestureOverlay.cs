using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IPageGestureVisualHost, INativeWebGestureHost
{
    private const int PageGestureNativeReleaseFallbackMilliseconds = 250;

    private readonly PageGestureVisualController _pageGestureVisualController;
    private int _pageGesturePendingDeviceX;
    private int _pageGesturePendingDeviceY;
    private int _pageGestureUpdateQueued;
    private readonly NativeWebGestureController _nativeWebGestureController;

    bool INativeWebGestureHost.ContainsTab(BrowserTab tab) => _tabs.Contains(tab);

    bool INativeWebGestureHost.UsesNativeWebGestureInput(BrowserTab sourceTab) => UsesNativeWebGestureInput(sourceTab);

    bool INativeWebGestureHost.TryGetWindowPoint(Point devicePoint, out Point windowPoint)
    {
        try
        {
            windowPoint = PointFromScreen(devicePoint);
            return true;
        }
        catch (InvalidOperationException)
        {
            windowPoint = default;
            return false;
        }
    }

    double INativeWebGestureHost.GestureStartThreshold => _settings.GestureStartThreshold;
    double INativeWebGestureHost.GestureDirectionThreshold => _settings.GestureDirectionThreshold;

    IReadOnlyList<GestureMappingSettings> INativeWebGestureHost.GetWebGestureMappings()
        => GestureSettingsCatalog.ResolveMappingsForScope(_settings.GestureMappings, GestureCommandScope.Web);

    void INativeWebGestureHost.EndReplacedInputInteraction(InputInteractionSession? replaced, bool preserveNativeWebGesture)
        => EndReplacedInputInteraction(replaced, preserveNativeWebGesture);

    void INativeWebGestureHost.SuppressNextPageContextMenu() => SuppressNextPageContextMenu();

    void INativeWebGestureHost.UseNativePageGestureVisual(BrowserTab sourceTab)
    {
        _ = UseNativePageGestureVisualAsync(sourceTab);
    }

    void INativeWebGestureHost.CancelActivePageGesture(BrowserTab? sourceTab)
    {
        if (GetTabWebView(sourceTab)?.CoreWebView2 is { } core)
        {
            _ = CancelActiveGestureOverlayAsync(core);
        }
    }

    void INativeWebGestureHost.ExecuteGestureCommand(BrowserTab sourceTab, string command)
    {
        ActivateSplitPaneForTab(sourceTab);
        QueueGestureCommandExecution(command, string.Empty, sourceTab);
    }

    bool IPageGestureVisualHost.TryGetStartScreenPoint(
        BrowserTab sourceTab, double clientX, double clientY, out Point startPoint)
    {
        startPoint = default;
        if (GetTabWebView(sourceTab) is not { } webView
            || !double.IsFinite(clientX)
            || !double.IsFinite(clientY)
            || PresentationSource.FromVisual(webView) is null)
        {
            return false;
        }

        try
        {
            startPoint = webView.PointToScreen(new Point(clientX, clientY));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return double.IsFinite(startPoint.X) && double.IsFinite(startPoint.Y);
    }

    bool IPageGestureVisualHost.IsSourceAvailable(BrowserTab sourceTab)
        => _tabs.Contains(sourceTab)
            && GetTabWebView(sourceTab) is { } webView
            && PresentationSource.FromVisual(webView) is not null;

    bool IPageGestureVisualHost.RequiresNativePageGestureVisualAtStart(BrowserTab sourceTab)
        => RequiresNativePageGestureVisualAtStart(sourceTab);

    double IPageGestureVisualHost.GestureStartThreshold => _settings.GestureStartThreshold;

    double IPageGestureVisualHost.GetPageGestureDistanceFromStart(Point startScreenPoint, Point screenPoint)
        => GetPageGestureDistanceFromStart(startScreenPoint, screenPoint);

    bool IPageGestureVisualHost.IsScreenPointOutsideMainWindow(Point screenPoint)
        => IsScreenPointOutsideMainWindow(screenPoint);

    IReadOnlyList<GestureMappingSettings> IPageGestureVisualHost.GetGestureMappings()
    {
        var gestureScope = GestureCommandScope.FromSurface(
            CreateCommandInvocationContext(GetActiveBrowserCommandTarget()).Surface);
        return GestureSettingsCatalog.ResolveMappingsForScope(
            _settings.GestureMappings,
            gestureScope);
    }

    void IPageGestureVisualHost.ResetNativeWebGestureTracking() => ResetNativeWebGestureTracking();

    void IPageGestureVisualHost.ResetQueuedPageGestureInput()
        => Interlocked.Exchange(ref _pageGestureUpdateQueued, 0);

    void IPageGestureVisualHost.UseNativePageGestureVisual(BrowserTab sourceTab)
    {
        _ = UseNativePageGestureVisualAsync(sourceTab);
    }

    private long? TryBeginWorkspaceGesture(BrowserTab workspaceTab)
    {
        if (!workspaceTab.IsWorkspaceTab || !_tabs.Contains(workspaceTab))
        {
            return null;
        }

        return TryBeginWorkspaceGesture(workspaceTab.Id);
    }

    private long? TryBeginWorkspaceGesture(Guid inputScopeId)
    {
        var interaction = s_inputInteractionHub.TryBegin(
            InputInteractionKind.WorkspaceGesture,
            _workspaceWindowId,
            inputScopeId,
            DateTimeOffset.Now);
        if (!interaction.Accepted || interaction.Current is not { } current)
        {
            return null;
        }

        return current.Id;
    }

    private long? TryBeginWorkspaceRightWheel(Guid inputScopeId)
    {
        var interaction = s_inputInteractionHub.TryBegin(
            InputInteractionKind.RightWheelTabSwitch,
            _workspaceWindowId,
            inputScopeId,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        if (!interaction.Accepted || interaction.Current is not { } current)
        {
            return null;
        }

        return current.Id;
    }

    private void CompleteWorkspaceGesture(long interactionId)
    {
        s_inputInteractionHub.TryComplete(interactionId);
    }

    private void CancelWorkspaceInputScope(Guid inputScopeId)
    {
        s_inputInteractionHub.CancelForTab(_workspaceWindowId, inputScopeId);
    }

    private void EndReplacedInputInteraction(InputInteractionSession? replaced)
    {
        EndReplacedInputInteraction(replaced, preserveNativeWebGesture: false);
    }

    private void EndReplacedInputInteraction(
        InputInteractionSession? replaced,
        bool preserveNativeWebGesture)
    {
        if (replaced is { Kind: InputInteractionKind.MiniPlayerHoldSeek, TabId: { } holdSeekMiniPlayerId }
            && _youTubeMiniPlayerRuntimes.TryGetValue(holdSeekMiniPlayerId, out var holdSeekRuntime))
        {
            CancelYouTubeMiniPlayerRightInput(holdSeekRuntime, "holdSeek");
            return;
        }

        if (replaced is { Kind: InputInteractionKind.RightWheelTabSwitch, TabId: { } rightWheelScopeId })
        {
            if (_youTubeMiniPlayerRuntimes.TryGetValue(rightWheelScopeId, out var rightWheelRuntime))
            {
                CancelYouTubeMiniPlayerRightInput(rightWheelRuntime, "rightWheel");
                return;
            }

            CancelWorkspaceGestureScope(rightWheelScopeId);
            return;
        }

        if (replaced is not { TabId: { } tabId })
        {
            return;
        }

        if (replaced.Kind == InputInteractionKind.WorkspaceGesture)
        {
            CancelWorkspaceGestureScope(tabId);
            return;
        }

        if (replaced.Kind == InputInteractionKind.PageContextMenu
            && _pageContextMenuController.CloseForReplacedInteraction(replaced.Id))
        {
            return;
        }

        if (replaced.Kind != InputInteractionKind.PageGesture)
        {
            return;
        }

        EndPageGestureVisual(resetNativeWebGesture: !preserveNativeWebGesture);
        var sourceTab = _tabs.FirstOrDefault(tab => tab.Id == tabId);
        if (GetTabWebView(sourceTab)?.CoreWebView2 is { } core)
        {
            _ = CancelActiveGestureOverlayAsync(core);
        }
    }

    private void BeginPageGestureVisual(BrowserTab sourceTab, double clientX, double clientY)
        => _pageGestureVisualController.BeginPageGestureVisual(sourceTab, clientX, clientY);

    private void QueuePageGestureInputPoint(NativePoint devicePoint)
    {
        if (!_pageGestureVisualController.IsTracking && !_nativeWebGestureController.IsTracking)
        {
            return;
        }

        Volatile.Write(ref _pageGesturePendingDeviceX, devicePoint.X);
        Volatile.Write(ref _pageGesturePendingDeviceY, devicePoint.Y);
        var visualGeneration = _pageGestureVisualController.Generation;
        var nativeGeneration = _nativeWebGestureController.Generation;
        if (Interlocked.Exchange(ref _pageGestureUpdateQueued, 1) != 0)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _pageGestureUpdateQueued, 0);
                if (nativeGeneration != _nativeWebGestureController.Generation
                    || visualGeneration != _pageGestureVisualController.Generation
                        && !_nativeWebGestureController.IsTracking)
                {
                    return;
                }

                if (!_pageGestureVisualController.IsTracking && !_nativeWebGestureController.IsTracking)
                {
                    return;
                }

                var point = new Point(
                    Volatile.Read(ref _pageGesturePendingDeviceX),
                    Volatile.Read(ref _pageGesturePendingDeviceY));
                UpdateNativeWebGesturePoint(point);
            }), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            if (nativeGeneration == _nativeWebGestureController.Generation)
            {
                Interlocked.Exchange(ref _pageGestureUpdateQueued, 0);
            }
        }
    }

    private bool RequiresNativePageGestureVisualAtStart(BrowserTab sourceTab)
    {
        return _splitViewService.IsActive
            && SplitViewHost.Visibility == Visibility.Visible
            && _splitViewService.Contains(sourceTab);
    }

    private bool TryBeginNativeWebGestureCandidate(NativePoint screenPoint)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => TryBeginNativeWebGestureCandidate(screenPoint));
        }

        return _nativeWebGestureController.TryBeginNativeWebGestureCandidate(
            () => CaptureNativeWebGestureStart(screenPoint));
    }

    private NativeWebGestureStart? CaptureNativeWebGestureStart(NativePoint screenPoint)
    {
        if (!IsScreenPointOwnedByThisWindow(screenPoint))
        {
            return null;
        }

        var sourceTab = FindNativeWebGestureTab(screenPoint);
        if (sourceTab is null || GetTabWebView(sourceTab) is not { } webView)
        {
            return null;
        }

        Point clientPoint;
        Point windowPoint;
        try
        {
            var devicePoint = new Point(screenPoint.X, screenPoint.Y);
            clientPoint = webView.PointFromScreen(devicePoint);
            windowPoint = PointFromScreen(devicePoint);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new NativeWebGestureStart(sourceTab, clientPoint, windowPoint);
    }

    private BrowserTab? FindNativeWebGestureTab(NativePoint screenPoint)
    {
        var devicePoint = new Point(screenPoint.X, screenPoint.Y);
        foreach (var tab in _tabs.Reverse())
        {
            if (GetTabWebView(tab) is not { } webView
                || !webView.IsVisible
                || webView.Visibility != Visibility.Visible
                || webView.ActualWidth <= 0
                || webView.ActualHeight <= 0
                || PresentationSource.FromVisual(webView) is null)
            {
                continue;
            }

            try
            {
                var clientPoint = webView.PointFromScreen(devicePoint);
                if (clientPoint.X >= 0
                    && clientPoint.Y >= 0
                    && clientPoint.X <= webView.ActualWidth
                    && clientPoint.Y <= webView.ActualHeight)
                {
                    return tab;
                }
            }
            catch (InvalidOperationException)
            {
                // Window終了中や表示切替中のWebViewは対象外とする。
            }
        }

        return null;
    }

    private bool IsScreenPointOwnedByThisWindow(NativePoint screenPoint)
    {
        const uint getRoot = 2;
        var windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var hitHandle = WindowFromPoint(screenPoint);
        var hitRoot = hitHandle == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(hitHandle, getRoot);
        return windowHandle != IntPtr.Zero && hitRoot == windowHandle;
    }

    private void UpdateNativeWebGesturePoint(Point devicePoint)
        => _nativeWebGestureController.UpdateNativeWebGesturePoint(devicePoint);

    private void FinishNativeWebGesture(NativePoint screenPoint)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => FinishNativeWebGesture(screenPoint));
            return;
        }

        _nativeWebGestureController.FinishNativeWebGesture(new Point(screenPoint.X, screenPoint.Y));
    }

    private bool TryClaimNativeWebGestureForPage(BrowserTab sourceTab)
        => _nativeWebGestureController.TryClaimNativeWebGestureForPage(sourceTab);

    private bool ShouldIgnorePageGestureMessage(BrowserTab _)
        => _nativeWebGestureController.ShouldIgnorePageGestureMessage(_);

    private void MarkNativeWebGesturePageRoute(BrowserTab sourceTab, string? route)
        => _nativeWebGestureController.MarkNativeWebGesturePageRoute(sourceTab, route);

    private void ResetNativeWebGestureForTab(BrowserTab tab)
        => _nativeWebGestureController.ResetNativeWebGestureForTab(tab);

    private void CancelNativeWebGestureForRightWheel()
        => _nativeWebGestureController.CancelNativeWebGestureForRightWheel();

    private void ResetNativeWebGestureTracking()
        => _nativeWebGestureController.ResetNativeWebGestureTracking();

    private double GetPageGestureDistanceFromStart(Point startScreenPoint, Point screenPoint)
    {
        try
        {
            var startPoint = PointFromScreen(startScreenPoint);
            var currentPoint = PointFromScreen(screenPoint);
            return (currentPoint - startPoint).Length;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private bool IsScreenPointOutsideMainWindow(Point screenPoint)
    {
        try
        {
            var windowPoint = PointFromScreen(screenPoint);
            return windowPoint.X < 0
                || windowPoint.Y < 0
                || windowPoint.X > ActualWidth
                || windowPoint.Y > ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdatePageGestureVisualState(BrowserTab sourceTab, string? route, string? status)
        => _pageGestureVisualController.UpdatePageGestureVisualState(sourceTab, route, status);

    private void QueueEndPageGestureAfterNativeRelease()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var interaction = s_inputInteractionHub.GetCurrent(DateTimeOffset.Now);
                var sourceTab = interaction is
                {
                    Kind: InputInteractionKind.PageGesture,
                    WindowId: var windowId,
                    TabId: { } tabId
                } && windowId == _workspaceWindowId
                    ? _tabs.FirstOrDefault(tab => tab.Id == tabId)
                    : null;

                if (_pageGestureVisualController.IsTracking)
                {
                    sourceTab ??= _pageGestureVisualController.SourceTab;
                    EndPageGestureVisual();
                }

                if (sourceTab is not null
                    && interaction is { Kind: InputInteractionKind.PageGesture } pageGesture)
                {
                    _ = CancelPageGestureSourceAfterNativeReleaseAsync(sourceTab, pageGesture.Id);
                }
            }), DispatcherPriority.Input);
        }
        catch (InvalidOperationException)
        {
            _pageGestureVisualController.StopTrackingAfterDispatchFailure();
        }
    }

    private void QueueEndWorkspaceGestureAfterNativeRelease()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var interaction = s_inputInteractionHub.GetCurrent(DateTimeOffset.Now);
                if (interaction is not
                    {
                        Kind: InputInteractionKind.WorkspaceGesture,
                        WindowId: var windowId,
                        TabId: { } tabId
                    }
                    || windowId != _workspaceWindowId)
                {
                    return;
                }

                _ = CancelWorkspaceGestureAfterNativeReleaseAsync(tabId, interaction.Id);
            }), DispatcherPriority.Input);
        }
        catch (InvalidOperationException)
        {
            // Window終了中は既存の終了処理が所有権を破棄します。
        }
    }

    private static async Task UseNativePageGestureVisualAsync(BrowserTab sourceTab)
    {
        if (GetTabWebView(sourceTab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync("window.__tabBrowserGestureHostUseNativeVisual?.();");
        }
        catch
        {
            // ページ遷移中はWPF側表示だけを終了経路で閉じます。
        }
    }

    private async Task CancelPageGestureSourceAfterNativeReleaseAsync(
        BrowserTab sourceTab,
        long interactionId)
    {
        await Task.Delay(PageGestureNativeReleaseFallbackMilliseconds);
        if (!s_inputInteractionHub.IsCurrent(interactionId))
        {
            return;
        }

        s_inputInteractionHub.TryComplete(interactionId);
        if (GetTabWebView(sourceTab)?.CoreWebView2 is { } core)
        {
            await CancelActiveGestureOverlayAsync(core);
        }
    }

    private async Task CancelWorkspaceGestureAfterNativeReleaseAsync(
        Guid inputScopeId,
        long interactionId)
    {
        await Task.Delay(PageGestureNativeReleaseFallbackMilliseconds);
        if (!s_inputInteractionHub.IsCurrent(interactionId))
        {
            return;
        }

        CancelWorkspaceGestureScope(inputScopeId);
        s_inputInteractionHub.TryComplete(interactionId);
    }

    private void CancelWorkspaceGestureScope(Guid inputScopeId)
    {
        var nativeTab = _tabs.FirstOrDefault(tab => tab.Id == inputScopeId);
        if (GetTabHostedContent(nativeTab) is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.CancelGesture();
            return;
        }

        if (GetTabHostedContent(nativeTab) is Views.FolderTabView folderTabView)
        {
            folderTabView.CancelGesture();
            return;
        }

        if (nativeTab is not null
            && ReferenceEquals(ActiveTab, nativeTab)
            && CanStartBlankViewGesture())
        {
            _blankViewGestureController.Cancel();
            return;
        }

        if (_workspaceOSWindow is { } workspaceWindow
            && workspaceWindow.InputScopeId == inputScopeId)
        {
            workspaceWindow.CancelGesture();
            return;
        }

        var folderWindow = _workspaceFolderWindows.Values
            .FirstOrDefault(window => window.InputScopeId == inputScopeId);
        folderWindow?.CancelGesture();
    }

    private void EndPageGestureVisual(bool resetNativeWebGesture = true)
        => _pageGestureVisualController.EndPageGestureVisual(resetNativeWebGesture);

    private void ClosePageGestureOverlayWindow()
        => _pageGestureVisualController.ClosePageGestureOverlayWindow();
}
