using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerRuntimeLoadCallbacks(
    Func<YouTubeMiniPlayerRuntime, WebView2> CreateWebView,
    Func<string?, Task<CoreWebView2Environment>> EnsureEnvironmentAsync,
    Action<YouTubeMiniPlayerRuntime> AttachDocumentTitleHandler,
    Action<YouTubeMiniPlayerRuntime> AttachWebMessageHandler,
    Action UpdateStateCaptureTimer);

internal static class YouTubeMiniPlayerRuntimeLoadService
{
    public static async Task LoadAsync(
        YouTubeMiniPlayerState state,
        Uri uri,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeLoadCallbacks callbacks)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime) || runtime.IsDisposed)
        {
            return;
        }

        var webView = EnsureWebView(runtime, callbacks.CreateWebView);
        await EnsureWebViewLayoutAsync(runtime, webView);
        if (!IsCurrentRuntime(state, runtimes, runtime, webView))
        {
            return;
        }

        var environment = await callbacks.EnsureEnvironmentAsync(state.ContainerProfileId);
        if (!IsCurrentRuntime(state, runtimes, runtime, webView))
        {
            return;
        }

        var core = await EnsureCoreAsync(webView, environment);
        if (!IsCurrentRuntime(state, runtimes, runtime, webView))
        {
            return;
        }

        await EnsureWebViewLayoutAsync(runtime, webView);
        if (!IsCurrentRuntime(state, runtimes, runtime, webView))
        {
            return;
        }

        AttachHandlersAndNavigate(
            runtime,
            core,
            uri,
            callbacks.AttachDocumentTitleHandler,
            callbacks.AttachWebMessageHandler);
        callbacks.UpdateStateCaptureTimer();
    }

    private static bool IsCurrentRuntime(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntime runtime,
        WebView2 webView)
    {
        return !runtime.IsDisposed
            && ReferenceEquals(runtime.WebView, webView)
            && runtimes.TryGetValue(state.Id, out var currentRuntime)
            && ReferenceEquals(runtime, currentRuntime);
    }

    public static async Task<CoreWebView2> EnsureCoreAsync(
        WebView2 webView,
        CoreWebView2Environment environment)
    {
        await webView.EnsureCoreWebView2Async(environment);
        WebViewLifecycleService.ApplyDefaultCoreSettings(webView.CoreWebView2);
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        return webView.CoreWebView2;
    }

    public static void AttachHandlersAndNavigate(
        YouTubeMiniPlayerRuntime runtime,
        CoreWebView2 core,
        Uri uri,
        Action<YouTubeMiniPlayerRuntime> attachDocumentTitleHandler,
        Action<YouTubeMiniPlayerRuntime> attachWebMessageHandler)
    {
        attachDocumentTitleHandler(runtime);
        attachWebMessageHandler(runtime);
        YouTubeMiniPlayerWebViewService.NavigateWithYouTubeReferer(core, uri);
    }

    public static WebView2 EnsureWebView(
        YouTubeMiniPlayerRuntime runtime,
        Func<YouTubeMiniPlayerRuntime, WebView2> createWebView)
    {
        if (runtime.WebView is { } existingWebView)
        {
            return existingWebView;
        }

        runtime.WebHost.Children.Clear();
        var webView = createWebView(runtime);
        runtime.WebView = webView;
        runtime.WebHost.Children.Add(webView);
        return webView;
    }

    private static Task EnsureWebViewLayoutAsync(
        YouTubeMiniPlayerRuntime runtime,
        WebView2 webView)
    {
        return webView.Dispatcher.InvokeAsync(
            () =>
            {
                runtime.Root.UpdateLayout();
                runtime.WebHost.UpdateLayout();
                webView.UpdateLayout();
            },
            DispatcherPriority.Render).Task;
    }
}
