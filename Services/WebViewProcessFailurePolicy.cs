using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

public enum WebViewProcessFailureAction
{
    ObserveOnly,
    RevalidateMediaPlayback,
    ReloadAffectedRuntime,
    RecreateProcessGroup
}

public static class WebViewProcessFailurePolicy
{
    public static WebViewProcessFailureAction GetAction(CoreWebView2ProcessFailedKind kind)
    {
        return kind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewProcessFailureAction.RecreateProcessGroup,
            CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited
                => WebViewProcessFailureAction.ReloadAffectedRuntime,
            CoreWebView2ProcessFailedKind.GpuProcessExited
                => WebViewProcessFailureAction.RevalidateMediaPlayback,
            _ => WebViewProcessFailureAction.ObserveOnly
        };
    }

    public static bool ShouldRevalidateMediaPlayback(
        CoreWebView2ProcessFailedKind kind,
        string? url)
    {
        return GetAction(kind) == WebViewProcessFailureAction.RevalidateMediaPlayback
            && !string.IsNullOrWhiteSpace(url)
            && BrowserUrlRulesService.IsBackgroundMediaPlaybackUrl(url);
    }
}
