using System;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerTitleService
{
    public static void AttachDocumentTitleHandler(
        YouTubeMiniPlayerRuntime runtime,
        Action queueSave)
    {
        YouTubeMiniPlayerRuntimeLifecycleService.AttachDocumentTitleHandler(
            runtime,
            _ => ApplyDocumentTitle(runtime, queueSave));
    }

    public static void AttachDocumentTitleHandler(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action queueSave)
    {
        if (runtimes.TryGetValue(state.Id, out var runtime))
        {
            AttachDocumentTitleHandler(runtime, queueSave);
        }
    }

    public static void ApplyDocumentTitle(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action queueSave)
    {
        if (runtimes.TryGetValue(state.Id, out var runtime))
        {
            ApplyDocumentTitle(runtime, queueSave);
        }
    }

    public static void ApplyDocumentTitle(
        YouTubeMiniPlayerRuntime runtime,
        Action queueSave)
    {
        if (TryApplyDocumentTitle(runtime))
        {
            queueSave();
        }
    }

    public static bool TryApplyDocumentTitle(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.LiveContinuationNavigation is not null
            || runtime.WebView?.CoreWebView2 is not { } core)
        {
            return false;
        }

        var state = runtime.State;
        var title = YouTubeMiniPlayerNavigationService.NormalizeTitle(core.DocumentTitle, state.Url);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var shouldSave = false;
        if (!string.Equals(state.Title, title, StringComparison.Ordinal))
        {
            state.Title = title;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            shouldSave = true;
        }

        runtime.TitleBandTitleText.Text = title;
        runtime.TaskbarWindow.Title = title;
        runtime.TaskbarTitleText.Text = title;
        return shouldSave;
    }
}
