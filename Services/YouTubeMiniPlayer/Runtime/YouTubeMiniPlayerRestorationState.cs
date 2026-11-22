using Microsoft.Web.WebView2.Wpf;

namespace TabBrowser.Services;

internal sealed class YouTubeMiniPlayerRestorationState
{
    public bool IsInitialLivePlaybackResolved { get; set; }

    private readonly object _recoveryNavigationSync = new();
    private long _recoveryNavigationGeneration;
    private TaskCompletionSource<bool>? _recoveryNavigationCompletion;
    private WebView2? _expectedRecoveryWebView;

    public YouTubeMiniPlayerRecoveryNavigationWait BeginRecoveryNavigation(WebView2? expectedWebView)
    {
        lock (_recoveryNavigationSync)
        {
            _recoveryNavigationGeneration++;
            _recoveryNavigationCompletion?.TrySetResult(false);
            _expectedRecoveryWebView = expectedWebView;
            _recoveryNavigationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new YouTubeMiniPlayerRecoveryNavigationWait(
                _recoveryNavigationGeneration,
                _recoveryNavigationCompletion.Task);
        }
    }

    public void CompleteRecoveryNavigation(in WebView2? currentWebView, WebView2 webView, bool isHealthy)
    {
        lock (_recoveryNavigationSync)
        {
            if (_recoveryNavigationCompletion is null
                || (_expectedRecoveryWebView is not null
                    && !ReferenceEquals(_expectedRecoveryWebView, webView))
                || !ReferenceEquals(currentWebView, webView))
            {
                return;
            }

            _recoveryNavigationCompletion.TrySetResult(isHealthy);
            _recoveryNavigationCompletion = null;
            _expectedRecoveryWebView = null;
        }
    }

    public void CancelRecoveryNavigation(YouTubeMiniPlayerRecoveryNavigationWait? wait = null)
    {
        lock (_recoveryNavigationSync)
        {
            if (wait is { } expected && expected.Generation != _recoveryNavigationGeneration)
            {
                return;
            }

            _recoveryNavigationCompletion?.TrySetResult(false);
            _recoveryNavigationCompletion = null;
            _expectedRecoveryWebView = null;
        }
    }
}

internal readonly record struct YouTubeMiniPlayerRecoveryNavigationWait(
    long Generation,
    Task<bool> Completion);
