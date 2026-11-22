using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace TabBrowser.Services;

// WebView単位で非表示を所有する。移管先から表示を戻した場合も進行中の休止へ伝わる。
internal sealed class BackgroundWebViewSuspension
{
    private static readonly ConditionalWeakTable<WebView2, BackgroundWebViewSuspension> s_views = new();
    private Visibility? _originalVisibility;
    private bool _resumeRequested;

    public Task? Pending { get; private set; }

    public static BackgroundWebViewSuspension For(WebView2 view) => s_views.GetOrCreateValue(view);

    public static BackgroundWebViewSuspension? Find(WebView2 view)
        => s_views.TryGetValue(view, out var suspension) ? suspension : null;

    public async Task<bool> TrySuspendAsync(
        UIElement view,
        Func<bool> canSuspend,
        Func<Task<bool>> suspendAsync,
        Func<bool> shouldRestoreAfterSuspend,
        Action resume)
    {
        if (Pending is not null || !canSuspend())
        {
            return false;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending = completion.Task;
        _resumeRequested = false;
        var suspended = false;
        try
        {
            _originalVisibility ??= view.Visibility;
            // WPF版WebView2がIsVisibleをControllerに同期する。Hiddenはレイアウト領域を保持する。
            view.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            if (view.IsVisible || _resumeRequested || !canSuspend())
            {
                return false;
            }

            suspended = await suspendAsync();
            if (suspended && (_resumeRequested || shouldRestoreAfterSuspend()))
            {
                RestoreVisibility(view);
                resume();
                return false;
            }

            return suspended;
        }
        finally
        {
            try
            {
                if (!suspended)
                {
                    RestoreVisibility(view);
                }
            }
            finally
            {
                Pending = null;
                completion.SetResult();
            }
        }
    }

    public bool RestoreForUse(UIElement view)
    {
        if (Pending is not null)
        {
            _resumeRequested = true;
        }

        return RestoreVisibility(view);
    }

    private bool RestoreVisibility(UIElement view)
    {
        if (_originalVisibility is not { } original)
        {
            return false;
        }

        _originalVisibility = null;
        view.SetCurrentValue(UIElement.VisibilityProperty, original);
        return true;
    }
}
