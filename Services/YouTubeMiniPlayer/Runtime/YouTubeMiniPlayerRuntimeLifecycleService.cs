using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerRuntimeLifecycleService
{
    public static CoreWebView2? TryGetCoreWebView2(YouTubeMiniPlayerRuntime? runtime)
    {
        if (runtime is null || runtime.IsDisposed)
        {
            return null;
        }

        try
        {
            return runtime.WebView?.CoreWebView2;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static async Task<bool> TrySuspendForInactivityAsync(
        YouTubeMiniPlayerRuntime runtime, PerformanceDiagnosticLogController? diagnostics)
    {
        if (TryGetCoreWebView2(runtime) is not { } core)
        {
            return false;
        }

        try
        {
            if (core.IsSuspended)
            {
                runtime.IsSuspendedForInactivity = true;
                return false;
            }

            var suspended = await WebViewSuspendDiagnostics.TrySuspendAsync(
                core, runtime.WebView, WebViewSuspendRoute.YouTubeMiniPlayer, diagnostics);
            runtime.IsSuspendedForInactivity = suspended;
            return suspended;
        }
        catch
        {
            return false;
        }
    }

    public static bool StopLoadForInactivity(YouTubeMiniPlayerRuntime runtime)
    {
        if (TryGetCoreWebView2(runtime) is not { } core)
        {
            return false;
        }

        try
        {
            core.Stop();
            runtime.IsNavigationLoading = false;
            runtime.IsLoadStoppedForInactivity = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static YouTubeMiniPlayerInactivityResumeResult ResumeFromInactivity(
        YouTubeMiniPlayerRuntime runtime, bool deferReload = false)
    {
        runtime.InactiveSuspendGeneration++;
        if (TryGetCoreWebView2(runtime) is not { } core)
        {
            runtime.IsSuspendedForInactivity = false;
            return default;
        }

        var resumed = false;
        var reloading = false;
        var wasLoadStopped = runtime.IsLoadStoppedForInactivity;
        try
        {
            if (core.IsSuspended)
            {
                core.Resume();
                resumed = true;
            }

            runtime.IsSuspendedForInactivity = false;
            if (runtime.IsLoadStoppedForInactivity)
            {
                if (!deferReload)
                {
                    runtime.IsLoadStoppedForInactivity = false;
                    YouTubeMiniPlayerWebViewService.RunAppNavigation(
                        core, CoreWebView2NavigationKind.Reload, core.Source, core.Reload);
                }
                reloading = true;
            }
        }
        catch
        {
            runtime.IsLoadStoppedForInactivity = wasLoadStopped;
        }

        return new YouTubeMiniPlayerInactivityResumeResult(resumed, reloading);
    }

    public static void AttachWebMessageHandler(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerState, CoreWebView2WebMessageReceivedEventArgs> handleWebMessage)
    {
        if (runtime.IsDisposed
            || runtime.Registrations.WebMessageReceived is not null
            || TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        EventHandler<CoreWebView2WebMessageReceivedEventArgs> handler = (_, args) =>
            handleWebMessage(runtime.State, args);
        core.WebMessageReceived += handler;
        runtime.Registrations.WebMessageReceived = handler;
    }

    public static void AttachDocumentTitleHandler(
        YouTubeMiniPlayerRuntime runtime,
        Action<YouTubeMiniPlayerState> updateTitle)
    {
        if (runtime.IsDisposed
            || runtime.Registrations.DocumentTitleChanged is not null
            || TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        EventHandler<object> handler = (_, _) => updateTitle(runtime.State);
        core.DocumentTitleChanged += handler;
        runtime.Registrations.DocumentTitleChanged = handler;
        updateTitle(runtime.State);
    }

    public static void AttachFullScreenHandler(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsDisposed
            || runtime.Registrations.ContainsFullScreenElementChanged is not null
            || TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        EventHandler<object> handler = (_, _) =>
            ApplyFullScreenState(runtime, core.ContainsFullScreenElement);
        core.ContainsFullScreenElementChanged += handler;
        runtime.Registrations.ContainsFullScreenElementChanged = handler;
    }

    public static void ToggleFullScreen(YouTubeMiniPlayerRuntime runtime)
    {
        ApplyFullScreenState(runtime, !runtime.IsFullScreen);
    }

    private static void DetachWebMessageHandler(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.Registrations.WebMessageReceived is null || TryGetCoreWebView2(runtime) is not { } core)
        {
            runtime.Registrations.WebMessageReceived = null;
            return;
        }

        core.WebMessageReceived -= runtime.Registrations.WebMessageReceived;
        runtime.Registrations.WebMessageReceived = null;
    }

    private static void DetachDocumentTitleHandler(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.Registrations.DocumentTitleChanged is null || TryGetCoreWebView2(runtime) is not { } core)
        {
            runtime.Registrations.DocumentTitleChanged = null;
            return;
        }

        core.DocumentTitleChanged -= runtime.Registrations.DocumentTitleChanged;
        runtime.Registrations.DocumentTitleChanged = null;
    }

    private static void DetachFullScreenHandler(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.Registrations.ContainsFullScreenElementChanged is null
            || TryGetCoreWebView2(runtime) is not { } core)
        {
            runtime.Registrations.ContainsFullScreenElementChanged = null;
            return;
        }

        core.ContainsFullScreenElementChanged -= runtime.Registrations.ContainsFullScreenElementChanged;
        runtime.Registrations.ContainsFullScreenElementChanged = null;
    }

    private static void ApplyFullScreenState(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
    {
        if (runtime.IsFullScreen == isFullScreen)
        {
            return;
        }

        if (isFullScreen)
        {
            EnterFullScreen(runtime);
            return;
        }

        ExitFullScreen(runtime);
    }

    private static void EnterFullScreen(YouTubeMiniPlayerRuntime runtime)
    {
        var window = runtime.TaskbarWindow;
        runtime.FullScreenRestoreBounds = new System.Windows.Rect(
            window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        runtime.IsFullScreen = true;
        StopManipulation(runtime);
        ApplyFullScreenChrome(runtime, true);
        _ = ApplyFullScreenPageStyleAsync(runtime, true);
        HideOverlays(runtime);
        runtime.Root.Width = double.NaN;
        runtime.Root.Height = double.NaN;
        window.WindowState = System.Windows.WindowState.Maximized;
        NotifyFullScreenStateChanged(runtime, true);
    }

    private static void ExitFullScreen(YouTubeMiniPlayerRuntime runtime)
    {
        var window = runtime.TaskbarWindow;
        var restoreBounds = runtime.FullScreenRestoreBounds;
        runtime.IsFullScreen = false;
        window.WindowState = System.Windows.WindowState.Normal;
        if (IsValidRestoreBounds(restoreBounds))
        {
            window.Left = restoreBounds.Left;
            window.Top = restoreBounds.Top;
            window.Width = restoreBounds.Width;
            window.Height = restoreBounds.Height;
        }

        ApplyFullScreenChrome(runtime, false);
        YouTubeMiniPlayerLayoutService.ApplyMinimumSize(runtime);
        runtime.Root.Width = runtime.State.Width;
        runtime.Root.Height = runtime.State.Height;
        runtime.FullScreenRestoreBounds = System.Windows.Rect.Empty;
        _ = ApplyFullScreenPageStyleAsync(runtime, false);
        NotifyFullScreenStateChanged(runtime, false);
    }

    private static void NotifyFullScreenStateChanged(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
    {
        try
        {
            runtime.FullScreenStateChanged?.Invoke(isFullScreen);
        }
        catch
        {
        }
    }

    private static void StopManipulation(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.IsWebDragging = false;
        runtime.IsSurfaceDragging = false;
        runtime.IsResizing = false;
        runtime.WebDragResizeEdges = MiniPlayerResizeEdges.None;
        runtime.WebDragFollowTimer?.Stop();
    }

    private static void ApplyFullScreenChrome(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
    {
        if (isFullScreen)
        {
            runtime.ListHighlightBorder.Visibility = Visibility.Collapsed;
            runtime.Root.MinWidth = runtime.TaskbarWindow.MinWidth = YouTubeMiniPlayerLayoutService.MinimumWidth;
            runtime.Root.MinHeight = runtime.TaskbarWindow.MinHeight = YouTubeMiniPlayerLayoutService.MinimumHeight;
        }
        YouTubeMiniPlayerChromeFactory.RefreshListWindowHighlight(runtime);

        if (runtime.Root is Border rootBorder)
        {
            rootBorder.BorderThickness = isFullScreen
                ? new Thickness(0)
                : new Thickness(YouTubeMiniPlayerLayoutService.FrameBorderThickness);
            rootBorder.CornerRadius = isFullScreen
                ? new CornerRadius(0)
                : new CornerRadius(8);
        }

        if (runtime.WebHost.Parent is not Grid layout)
        {
            return;
        }

        layout.Margin = isFullScreen
            ? new Thickness(0)
            : new Thickness(YouTubeMiniPlayerLayoutService.ResizeBorderThickness);
        if (layout.Parent is not Panel chrome)
        {
            return;
        }

        foreach (var resizeThumb in chrome.Children.OfType<Thumb>())
        {
            resizeThumb.IsHitTestVisible = !isFullScreen;
            resizeThumb.Visibility = isFullScreen
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private static async Task ApplyFullScreenPageStyleAsync(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
    {
        if (TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        var script = isFullScreen
            ? """document.documentElement?.setAttribute("data-tabbrowser-mini-player-fullscreen", "true");"""
            : """document.documentElement?.removeAttribute("data-tabbrowser-mini-player-fullscreen");""";
        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch
        {
        }
    }

    private static bool IsValidRestoreBounds(System.Windows.Rect bounds)
    {
        return !bounds.IsEmpty
            && double.IsFinite(bounds.Left)
            && double.IsFinite(bounds.Top)
            && double.IsFinite(bounds.Width)
            && double.IsFinite(bounds.Height)
            && bounds.Width > 0
            && bounds.Height > 0;
    }

    private static void HideOverlays(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.Overlay.TitleBandHideTimer?.Stop();
        runtime.Overlay.ToolbarHideTimer?.Stop();
        runtime.TitleBandPopup.IsOpen = false;
        runtime.ToolbarPopup.IsOpen = false;
        runtime.TitleBand.Visibility = System.Windows.Visibility.Collapsed;
        runtime.Toolbar.Visibility = System.Windows.Visibility.Collapsed;
    }

    public static void ResetWebView(YouTubeMiniPlayerRuntime runtime)
    {
        YouTubeMiniPlayerRuntimeLoadService.Cancel(runtime);
        runtime.LiveChat.ResetAvailabilityRequests();
        YouTubeMiniPlayerRuntimeContentFactory.ClearLiveContinuation(runtime);
        runtime.InactiveSuspendGeneration++;
        runtime.IsNavigationLoading = false;
        runtime.IsSuspendedForInactivity = false;
        runtime.IsLoadStoppedForInactivity = false;
        runtime.Restoration.CancelRecoveryNavigation();
        runtime.DisplayHealth.Reset();
        runtime.SizeBasedQualityUpdateTimer?.Stop();
        runtime.SizeBasedQualityAppliedSignature = null;
        YouTubeMiniPlayerChromeFactory.UpdateNavigationHistoryButtons(
            runtime,
            canGoBack: false,
            canGoForward: false);
        ReleaseWebView(runtime);
    }

    private static void ReleasePopupSurfaces(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.Popup.IsOpen = false;
        runtime.Popup.Child = null;
        runtime.TitleBandPopup.IsOpen = false;
        runtime.TitleBandPopup.Child = null;
        runtime.ToolbarPopup.IsOpen = false;
        runtime.ToolbarPopup.Child = null;
        runtime.TitleBand.Visibility = System.Windows.Visibility.Collapsed;
        runtime.Toolbar.Visibility = System.Windows.Visibility.Collapsed;
    }

    private static void ReleaseWebView(YouTubeMiniPlayerRuntime runtime)
    {
        DetachWebMessageHandler(runtime);
        DetachDocumentTitleHandler(runtime);
        DetachFullScreenHandler(runtime);
        var webView = runtime.WebView;
        runtime.WebView = null;
        runtime.WebHost.Children.Clear();
        webView?.Dispose();
    }

    public static void DisposeRuntime(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsDisposed)
        {
            return;
        }

        runtime.Registrations.Transparency?.Dispose();
        runtime.Registrations.Transparency = null;
        runtime.IsDisposed = true;
        ReleasePopupSurfaces(runtime);
        runtime.Overlay.TitleBandHideTimer?.Stop();
        runtime.Overlay.ToolbarHideTimer?.Stop();
        runtime.WebDragFollowTimer?.Stop();
        runtime.SizeBasedQualityUpdateTimer?.Stop();
        runtime.LocalVideoView?.Dispose();
        runtime.FullScreenStateChanged = null;
        runtime.InteractiveResizeStateChanged = null;
        ResetWebView(runtime);
        runtime.TaskbarWindow.Content = null;
        runtime.IsTaskbarCloseRequested = false;
        runtime.IsClosingTaskbarWindow = true;
        if (runtime.TaskbarWindow.IsVisible)
        {
            runtime.TaskbarWindow.Close();
        }
    }
}

internal readonly record struct YouTubeMiniPlayerInactivityResumeResult(
    bool Resumed,
    bool Reloading);
