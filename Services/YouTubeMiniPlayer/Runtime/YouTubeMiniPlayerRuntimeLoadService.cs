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
    Action UpdateStateCaptureTimer,
    Func<bool>? IsClosing = null);

internal static class YouTubeMiniPlayerRuntimeLoadService
{
    private static readonly YouTubeMiniPlayerLoadCoordinator SharedLoads = new();

    public static void Cancel(YouTubeMiniPlayerRuntime runtime) => SharedLoads.Cancel(runtime);

    public static Task LoadAsync(
        YouTubeMiniPlayerState state,
        Uri uri,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeLoadCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (!runtimes.TryGetValue(state.Id, out var runtime) || runtime.IsDisposed)
        {
            return Task.CompletedTask;
        }

        return SharedLoads.RunAsync(runtime, uri.AbsoluteUri,
            () => !cancellationToken.IsCancellationRequested && !runtime.IsDisposed && callbacks.IsClosing?.Invoke() != true
                && runtimes.TryGetValue(state.Id, out var current) && ReferenceEquals(current, runtime),
            async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                await LoadCoreAsync(state, uri, runtimes, callbacks, runtime, linked.Token);
            });
    }

    private static async Task LoadCoreAsync(
        YouTubeMiniPlayerState state, Uri uri,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        YouTubeMiniPlayerRuntimeLoadCallbacks callbacks, YouTubeMiniPlayerRuntime runtime,
        CancellationToken cancellationToken)
    {
        var webView = EnsureWebView(runtime, callbacks.CreateWebView);
        bool IsCurrent() => !cancellationToken.IsCancellationRequested
            && callbacks.IsClosing?.Invoke() != true && IsCurrentRuntime(state, runtimes, runtime, webView);
        await EnsureWebViewLayoutAsync(runtime, webView);
        if (!IsCurrent())
        {
            return;
        }

        var environment = await callbacks.EnsureEnvironmentAsync(state.ContainerProfileId);
        if (!IsCurrent())
        {
            return;
        }

        var core = await EnsureCoreAsync(webView, environment, cancellationToken);
        if (!IsCurrent())
        {
            return;
        }

        await EnsureWebViewLayoutAsync(runtime, webView);
        if (!IsCurrent())
        {
            return;
        }

        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId is { } previous && previous != args.NavigationId) navigation.TrySetResult();
            navigationId ??= args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId == args.NavigationId) navigation.TrySetResult();
        }
        webView.NavigationStarting += Starting;
        webView.NavigationCompleted += Completed;
        try
        {
            AttachHandlersAndNavigate(runtime, core, uri,
                callbacks.AttachDocumentTitleHandler, callbacks.AttachWebMessageHandler);
            callbacks.UpdateStateCaptureTimer();
            // 再生数は制限せず、重い初期化と最初のページ読み込みだけを共有枠で待つ。
            await WaitForNavigationAsync(navigation.Task, TimeSpan.FromSeconds(20), cancellationToken);
        }
        finally
        {
            webView.NavigationStarting -= Starting;
            webView.NavigationCompleted -= Completed;
        }
    }

    internal static async Task WaitForNavigationAsync(Task navigation, TimeSpan timeout, CancellationToken token)
    {
        try { await navigation.WaitAsync(timeout, token); }
        catch (TimeoutException) { /* 遅いページ1件で後続を塞がない。ページ自身の読み込みは継続する。 */ }
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
        CoreWebView2Environment environment,
        CancellationToken cancellationToken = default)
    {
        await webView.EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();
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

// 全Windowの初期化をまとめる。同じRuntimeの同じ要求は共有し、閉じる／置換で待機を取り消す。
internal sealed class YouTubeMiniPlayerLoadCoordinator(int capacity = 2, TimeSpan? initializationTimeout = null)
{
    private readonly SemaphoreSlim _slots = new(capacity, capacity);
    private readonly TimeSpan _initializationTimeout = initializationTimeout ?? TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<object, LoadOperation> _requests = new(ReferenceEqualityComparer.Instance);

    public Task RunAsync(object owner, string source, Func<bool> isCurrent, Func<CancellationToken, Task> initialize)
    {
        LoadOperation? previous;
        LoadOperation operation;
        lock (_gate)
        {
            _requests.TryGetValue(owner, out previous);
            if (previous is not null && !previous.Completion.Task.IsCompleted && previous.Source == source) return previous.Completion.Task;
            operation = new LoadOperation(source);
            _requests[owner] = operation;
        }
        previous?.Cancel();
        _ = ExecuteAsync(owner, operation, isCurrent, initialize);
        return operation.Completion.Task;
    }

    public void Cancel(object owner)
    {
        LoadOperation? operation;
        lock (_gate) _requests.Remove(owner, out operation);
        operation?.Cancel();
    }

    private async Task ExecuteAsync(object owner, LoadOperation operation, Func<bool> isCurrent, Func<CancellationToken, Task> initialize)
    {
        var entered = false;
        try
        {
            await _slots.WaitAsync(operation.Token);
            entered = true;
            operation.Token.ThrowIfCancellationRequested();
            if (isCurrent())
            {
                var work = initialize(operation.Token);
                try { await work.WaitAsync(_initializationTimeout, operation.Token); }
                catch
                {
                    operation.Cancel();
                    _ = ObserveLateCompletionAsync(work);
                    throw;
                }
            }
            operation.Completion.TrySetResult();
        }
        catch (TimeoutException ex)
        {
            operation.Completion.TrySetException(ex);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            operation.Completion.TrySetResult();
        }
        catch (Exception) when (operation.Token.IsCancellationRequested)
        {
            // 取消不能なCore初期化が終了・置換後に失敗しても、新しい要求へ失敗を返さない。
            operation.Completion.TrySetResult();
        }
        catch (Exception ex) { operation.Completion.TrySetException(ex); }
        finally
        {
            if (entered) _slots.Release();
            lock (_gate)
            {
                if (_requests.TryGetValue(owner, out var current) && ReferenceEquals(current, operation))
                    _requests.Remove(owner);
            }
            operation.Cancellation.Dispose();
        }
    }

    private static async Task ObserveLateCompletionAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* 取消不能なWebView2初期化の遅延失敗を観測し、次の要求へ伝播させない。 */ }
    }

    private sealed class LoadOperation(string source)
    {
        public string Source { get; } = source;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token => Cancellation.Token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
