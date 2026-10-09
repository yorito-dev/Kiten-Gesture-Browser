using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

// 障害通知時の実Coreを固定し、待機後は同じCoreの所属を確認して操作する。
internal sealed record WebViewRenderRecoveryTarget(
    Func<bool> IsCurrent,
    Action Reload,
    Func<uint?> GetBrowserProcessId);

internal sealed record WebViewMediaRecoveryTarget(
    Func<bool> IsCurrent,
    Func<Task<string>> CapturePlaybackHealthAsync,
    Action Reload,
    Func<uint?> GetBrowserProcessId);

internal interface IWebViewRecoveryHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    bool IsClosing { get; }
    bool HasDispatcherShutdownStarted { get; }
    bool HasWebView(BrowserTab tab);
    bool IsWebViewVisible(BrowserTab tab);
    bool IsFirstActiveWindow();
    bool TryRunSupervised(string operation, Func<CancellationToken, Task> action);
    Task InvokeInBackgroundAsync(Func<Task> action);
    void DisposeDefaultProfileAuxiliaryWebViews();
    Task RecoverFloatingMediaProcessGroupAsync(string profileId, uint browserProcessId);
    void ReleaseTabRuntimeResources(BrowserTab tab);
    Task EnsureTabLoadedAsync(BrowserTab tab, string? url);
    void MarkWebViewLoadFailure(BrowserTab tab, string message);
    void ShowBlankView();
    void ShowWebViewRecoveryPlaceholder(BrowserTab tab, string title, string detail);
    void SetStatus(string message, bool autoClear = false);
    void AppendSessionDiagnostic(string operation, Exception exception, string detail);
    void QueueWorkspaceOpenTabsRefresh();
}

// 復旧の予約、再検証、再作成と結果処理を一つの所有者へまとめる。
// Runtime資源・Dispatcher・Window寿命はhost、世代と試行回数は既存Lifecycleが所有する。
internal sealed class WebViewRecoveryController
{
    private static readonly TimeSpan GpuMediaRecoveryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GpuMediaProbeInterval = TimeSpan.FromSeconds(1);
    private readonly IWebViewRecoveryHost _host;
    private readonly WebViewLifecycleService _webViewLifecycleService;
    private readonly WebViewProcessRecoveryCoordinator _processRecoveryCoordinator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly HashSet<Guid> _pendingGpuMediaRecoveries = [];

    internal WebViewRecoveryController(
        IWebViewRecoveryHost host,
        WebViewLifecycleService lifecycle,
        WebViewProcessRecoveryCoordinator processRecoveryCoordinator,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _webViewLifecycleService = lifecycle;
        _processRecoveryCoordinator = processRecoveryCoordinator;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public void QueueWebViewProcessAutoRecovery(BrowserTab tab)
    {
        if (!_host.Tabs.Contains(tab)
            || !_webViewLifecycleService.TryCreateAutoRecoveryPlan(
                tab,
                _host.IsClosing,
                prioritize: ReferenceEquals(tab, _host.ActiveTab),
                out var recoveryPlan))
        {
            return;
        }

        if (!_host.TryRunSupervised(
            "webview-process-auto-recovery",
            cancellationToken => RecoverWebViewProcessFailureAfterDelayAsync(
                tab,
                recoveryPlan,
                cancellationToken)))
        {
            _webViewLifecycleService.CompleteAutoRecovery(tab);
        }
    }

    public void QueueWebViewRenderProcessReload(
        BrowserTab tab,
        WebViewRenderRecoveryTarget runtime,
        string failure,
        CoreWebView2ProcessFailedKind failureKind)
    {
        if (!_webViewLifecycleService.TryCreateRenderReloadPlan(tab, failure, out var reloadPlan))
        {
            if (_host.ActiveTab == tab)
            {
                _host.ShowWebViewRecoveryPlaceholder(tab, "WebView2 描画プロセス異常", failure);
            }

            _processRecoveryCoordinator.RecordRecoveryResult(
                tab.ContainerProfileId,
                runtime.GetBrowserProcessId(),
                failureKind,
                WebViewProcessFailureAction.ReloadAffectedRuntime,
                "escalated-to-recreate");
            QueueWebViewProcessAutoRecovery(tab);
            return;
        }

        if (_host.ActiveTab == tab)
        {
            _host.SetStatus("WebView2 の描画を再読み込みしています", autoClear: true);
        }

        _host.TryRunSupervised(
            "webview-render-process-reload",
            cancellationToken => ReloadWebViewAfterRenderProcessFailureAsync(
                tab,
                runtime,
                reloadPlan,
                failure,
                failureKind,
                cancellationToken));
    }

    public void QueueWebViewGpuMediaRecovery(
        BrowserTab tab,
        WebViewMediaRecoveryTarget runtime,
        string failure,
        CoreWebView2ProcessFailedKind failureKind)
    {
        if (!_host.Tabs.Contains(tab) || !_pendingGpuMediaRecoveries.Add(tab.Id))
        {
            return;
        }

        if (!_host.TryRunSupervised(
            "webview-gpu-media-recovery",
            cancellationToken => RecoverMediaPlaybackAfterGpuFailureAsync(
                tab,
                runtime,
                failure,
                failureKind,
                cancellationToken)))
        {
            _pendingGpuMediaRecoveries.Remove(tab.Id);
        }
    }

    private async Task RecoverMediaPlaybackAfterGpuFailureAsync(
        BrowserTab tab,
        WebViewMediaRecoveryTarget runtime,
        string failure,
        CoreWebView2ProcessFailedKind failureKind,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delay(GpuMediaRecoveryDelay, cancellationToken);
            if (!CanContinueGpuMediaRecovery(tab, runtime))
            {
                return;
            }

            var first = await CapturePlaybackHealthAsync(runtime, cancellationToken);
            await _delay(GpuMediaProbeInterval, cancellationToken);
            if (!CanContinueGpuMediaRecovery(tab, runtime))
            {
                return;
            }

            var second = await CapturePlaybackHealthAsync(runtime, cancellationToken);
            if (second is { IsPaused: true } or { IsEnded: true })
            {
                RecordGpuMediaRecoveryResult(tab, runtime, failureKind, "media-not-playing");
                return;
            }

            if (first is not null
                && second is not null
                && IsPlaybackProgressing(first.Value, second.Value))
            {
                RecordGpuMediaRecoveryResult(tab, runtime, failureKind, "media-auto-recovered");
                return;
            }

            if ((first is null || !IsActivePlayback(first.Value))
                && second is { HasVideo: true, IsPaused: false, IsEnded: false })
            {
                await _delay(GpuMediaProbeInterval, cancellationToken);
                if (!CanContinueGpuMediaRecovery(tab, runtime))
                {
                    return;
                }

                var third = await CapturePlaybackHealthAsync(runtime, cancellationToken);
                if (third is { IsPaused: true } or { IsEnded: true })
                {
                    RecordGpuMediaRecoveryResult(tab, runtime, failureKind, "media-not-playing");
                    return;
                }

                if (third is not null && IsPlaybackProgressing(second.Value, third.Value))
                {
                    RecordGpuMediaRecoveryResult(tab, runtime, failureKind, "media-auto-recovered");
                    return;
                }
            }

            if (!CanContinueGpuMediaRecovery(tab, runtime))
            {
                return;
            }

            RecordGpuMediaRecoveryResult(tab, runtime, failureKind, "media-reload-required");
            QueueWebViewRenderProcessReload(
                tab,
                new WebViewRenderRecoveryTarget(
                    runtime.IsCurrent,
                    runtime.Reload,
                    runtime.GetBrowserProcessId),
                failure,
                failureKind);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _host.AppendSessionDiagnostic(
                "webview-gpu-media-recovery-failed",
                ex,
                $"tab={tab.Id};kind={failureKind}");
            if (CanContinueGpuMediaRecovery(tab, runtime))
            {
                QueueWebViewRenderProcessReload(
                    tab,
                    new WebViewRenderRecoveryTarget(
                        runtime.IsCurrent,
                        runtime.Reload,
                        runtime.GetBrowserProcessId),
                    failure,
                    failureKind);
            }
        }
        finally
        {
            _pendingGpuMediaRecoveries.Remove(tab.Id);
        }
    }

    private bool CanContinueGpuMediaRecovery(BrowserTab tab, WebViewMediaRecoveryTarget runtime)
    {
        return !_host.IsClosing
            && _host.Tabs.Contains(tab)
            && runtime.IsCurrent();
    }

    private static async Task<YouTubeMiniPlayerPlaybackHealthProbe?> CapturePlaybackHealthAsync(
        WebViewMediaRecoveryTarget runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await runtime.CapturePlaybackHealthAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return YouTubeMiniPlayerRecoveryService.TryParseHealthProbe(result, out var probe)
                ? probe
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool IsPlaybackProgressing(
        YouTubeMiniPlayerPlaybackHealthProbe first,
        YouTubeMiniPlayerPlaybackHealthProbe second)
    {
        return first.HasVideo
            && second.HasVideo
            && !second.IsPaused
            && !second.IsEnded
            && first.PositionSeconds is { } firstPosition
            && second.PositionSeconds is { } secondPosition
            && secondPosition - firstPosition >= 0.25;
    }

    private static bool IsActivePlayback(YouTubeMiniPlayerPlaybackHealthProbe probe)
    {
        return probe.HasVideo && !probe.IsPaused && !probe.IsEnded;
    }

    private void RecordGpuMediaRecoveryResult(
        BrowserTab tab,
        WebViewMediaRecoveryTarget runtime,
        CoreWebView2ProcessFailedKind failureKind,
        string result)
    {
        _processRecoveryCoordinator.RecordRecoveryResult(
            tab.ContainerProfileId,
            runtime.GetBrowserProcessId(),
            failureKind,
            WebViewProcessFailureAction.RevalidateMediaPlayback,
            result);
    }

    private async Task ReloadWebViewAfterRenderProcessFailureAsync(
        BrowserTab tab,
        WebViewRenderRecoveryTarget runtime,
        WebViewRecoveryPlan reloadPlan,
        string failure,
        CoreWebView2ProcessFailedKind failureKind,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delay(reloadPlan.Delay, cancellationToken);
            if (_host.IsClosing
                || !_host.Tabs.Contains(tab)
                || !_webViewLifecycleService.IsPendingOperationCurrent(
                    tab,
                    reloadPlan.PendingOperationGeneration)
                || !runtime.IsCurrent())
            {
                return;
            }

            runtime.Reload();
            _processRecoveryCoordinator.RecordRecoveryResult(
                tab.ContainerProfileId,
                runtime.GetBrowserProcessId(),
                failureKind,
                WebViewProcessFailureAction.ReloadAffectedRuntime,
                "reload-requested");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _host.AppendSessionDiagnostic("webview-render-reload-failed", ex, $"tab={tab.Id};kind={failureKind}");
            _host.MarkWebViewLoadFailure(tab, failure);
            _processRecoveryCoordinator.RecordRecoveryResult(
                tab.ContainerProfileId,
                browserProcessId: null,
                failureKind,
                WebViewProcessFailureAction.ReloadAffectedRuntime,
                "reload-failed");
            QueueWebViewProcessAutoRecovery(tab);
        }
    }

    public void QueueWebViewProcessGroupRecovery(WebViewBrowserProcessExit notification)
    {
        if (_host.IsClosing || _host.HasDispatcherShutdownStarted)
        {
            return;
        }

        _host.TryRunSupervised(
            "webview-process-group-recovery",
            cancellationToken => _host.InvokeInBackgroundAsync(
                () => RecoverWebViewProcessGroupAsync(notification, cancellationToken)));
    }

    private async Task RecoverWebViewProcessGroupAsync(
        WebViewBrowserProcessExit notification,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_host.IsClosing)
            {
                return;
            }

            var profileId = ContainerProfile.NormalizeId(notification.ContainerProfileId);
            var affectedTabs = _host.Tabs
                .Where(tab => !tab.IsNativeContentTab
                    && _host.HasWebView(tab)
                    && string.Equals(
                        ContainerProfile.NormalizeId(tab.ContainerProfileId),
                        profileId,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(tab => ReferenceEquals(tab, _host.ActiveTab))
                .ThenByDescending(tab => _host.IsWebViewVisible(tab))
                .ToList();

            if (string.Equals(profileId, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                _host.DisposeDefaultProfileAuxiliaryWebViews();
            }

            foreach (var tab in affectedTabs)
            {
                _webViewLifecycleService.PrepareForProcessGroupRecovery(
                    tab,
                    "WebView2 ブラウザープロセスが終了しました");
                QueueWebViewProcessAutoRecovery(tab);
            }

            await _host.RecoverFloatingMediaProcessGroupAsync(profileId, notification.BrowserProcessId);
            _processRecoveryCoordinator.RecordRecoveryResult(
                profileId,
                notification.BrowserProcessId,
                CoreWebView2ProcessFailedKind.BrowserProcessExited,
                WebViewProcessFailureAction.RecreateProcessGroup,
                $"window-queued-tabs={affectedTabs.Count}");

            if (_host.IsFirstActiveWindow())
            {
                _host.SetStatus($"WebView2 を復旧しています（タブ {affectedTabs.Count} 件）", autoClear: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _host.AppendSessionDiagnostic(
                "webview-process-group-recover-failed",
                ex,
                $"scope={(string.Equals(ContainerProfile.NormalizeId(notification.ContainerProfileId), ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase) ? "default" : "container")};process={notification.BrowserProcessId}");
            _processRecoveryCoordinator.RecordRecoveryResult(
                notification.ContainerProfileId,
                notification.BrowserProcessId,
                CoreWebView2ProcessFailedKind.BrowserProcessExited,
                WebViewProcessFailureAction.RecreateProcessGroup,
                "window-recovery-failed");
        }
    }

    private async Task RecoverWebViewProcessFailureAfterDelayAsync(
        BrowserTab tab,
        WebViewRecoveryPlan recoveryPlan,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delay(recoveryPlan.Delay, cancellationToken);
            if (_host.IsClosing
                || !_host.Tabs.Contains(tab)
                || !_webViewLifecycleService.IsPendingOperationCurrent(
                    tab,
                    recoveryPlan.PendingOperationGeneration)
                || !_webViewLifecycleService.HasLoadFailure(tab))
            {
                return;
            }

            await RecoverWebViewProcessFailureAsync(tab, isAutomatic: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _webViewLifecycleService.CompleteAutoRecovery(tab);
        }
    }

    public async Task RecoverWebViewProcessFailureAsync(BrowserTab tab, bool isAutomatic = false)
    {
        if (!_host.Tabs.Contains(tab))
        {
            return;
        }

        try
        {
            _webViewLifecycleService.PrepareForRuntimeRecovery(
                tab,
                resetRecoveryAttempts: !isAutomatic);
            _host.ReleaseTabRuntimeResources(tab);

            if (_host.ActiveTab == tab)
            {
                _host.ShowBlankView();
                _host.SetStatus(isAutomatic ? "WebView2 を自動再作成しています" : "WebView2 を再作成しています");
            }

            if (string.IsNullOrWhiteSpace(tab.Url))
            {
                if (_host.ActiveTab == tab)
                {
                    _host.SetStatus("再読み込みする URL がありません", autoClear: true);
                }

                return;
            }

            await _host.EnsureTabLoadedAsync(tab, tab.Url);
            if (_webViewLifecycleService.HasLoadFailure(tab))
            {
                if (_host.ActiveTab == tab)
                {
                    _host.SetStatus("WebView2 の再作成に失敗しました", autoClear: true);
                }

                return;
            }

            if (_host.ActiveTab == tab)
            {
                _host.SetStatus(isAutomatic ? "WebView2 を自動復旧しました" : "再読み込みしました", autoClear: true);
            }

            _webViewLifecycleService.ResetRecoveryAttempts(tab);
            _host.QueueWorkspaceOpenTabsRefresh();
        }
        catch (Exception ex)
        {
            _host.AppendSessionDiagnostic("webview-recover-failed", ex, $"tab={tab.Id};automatic={isAutomatic}");
            var message = "WebView2 の再作成に失敗しました。\n" + ex.Message;
            _host.MarkWebViewLoadFailure(tab, message);
            if (_host.ActiveTab == tab)
            {
                _host.SetStatus("WebView2 の再作成に失敗しました", autoClear: true);
                _host.ShowWebViewRecoveryPlaceholder(tab, "WebView2 の再作成に失敗しました", message);
            }
        }
    }

}
