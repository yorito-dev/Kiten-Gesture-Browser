using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal enum TabRuntimeEventSubscriptionKind
{
    WebViewControl,
    CoreWebView
}

internal enum TabRuntimeState
{
    Detached,
    Attached,
    Transferring,
    Disposed
}

internal enum TabRuntimeTransitionStatus
{
    Applied,
    Unchanged,
    Rejected
}

internal readonly record struct TabRuntimeTransferToken(
    Guid RuntimeId,
    Guid SourceWindowId,
    Guid TargetWindowId,
    long Generation);

internal readonly record struct TabRuntimeSnapshot(
    Guid RuntimeId,
    TabRuntimeState State,
    Guid? OwnerWindowId,
    Guid? TransferSourceWindowId,
    Guid? TransferTargetWindowId,
    long TransferGeneration);

internal readonly record struct TabRuntimeResourceSnapshot(
    bool HasWebView,
    bool HasWebMessageToken,
    bool HasWebViewControlSubscription,
    bool HasCoreWebViewSubscription,
    Guid? WebViewControlOwnerWindowId,
    Guid? CoreWebViewOwnerWindowId,
    bool HasHostedContent);

internal readonly record struct TabRuntimeTransitionResult(
    TabRuntimeTransitionStatus Status,
    TabRuntimeSnapshot Snapshot,
    string? FailureReason = null,
    TabRuntimeTransferToken? TransferToken = null)
{
    public bool Succeeded => Status != TabRuntimeTransitionStatus.Rejected;

    public bool Applied => Status == TabRuntimeTransitionStatus.Applied;
}

internal readonly record struct TabRuntimeTransferCommit(
    TabRuntime Runtime,
    TabRuntimeTransferToken Token);

internal readonly record struct TabRuntimeBatchTransitionResult(
    bool Succeeded,
    int? FailedTransferIndex = null,
    TabRuntimeTransitionResult? Failure = null);

internal sealed class TabRuntime
{
    private readonly object _gate = new();
    private readonly TabRuntimeTransientState _transientState;
    private TabRuntimeState _state = TabRuntimeState.Detached;
    private Guid? _ownerWindowId;
    private Guid? _transferSourceWindowId;
    private Guid? _transferTargetWindowId;
    private long _transferGeneration;
    private long _eventSubscriptionGeneration;
    private bool _resourceReleaseInProgress;
    private int _resourceReleaseThreadId;
    private WebView2? _webView;
    private string? _webMessageToken;
    private EventSubscriptionEntry? _webViewControlSubscription;
    private EventSubscriptionEntry? _coreWebViewSubscription;
    private UIElement? _hostedContent;

    public TabRuntime()
    {
        _transientState = new TabRuntimeTransientState();
    }

    public Guid RuntimeId { get; } = Guid.NewGuid();

    internal ITabRuntimePresentation Presentation => _transientState;

    public WebView2? WebView
    {
        get
        {
            lock (_gate)
            {
                return _webView;
            }
        }
    }

    public string? WebMessageToken
    {
        get
        {
            lock (_gate)
            {
                return _webMessageToken;
            }
        }
    }

    public UIElement? HostedContent
    {
        get
        {
            lock (_gate)
            {
                return _hostedContent;
            }
        }
    }

    public TabRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    public TabRuntimeResourceSnapshot GetResourceSnapshot()
    {
        lock (_gate)
        {
            return new TabRuntimeResourceSnapshot(
                _webView is not null,
                !string.IsNullOrEmpty(_webMessageToken),
                _webViewControlSubscription is not null,
                _coreWebViewSubscription is not null,
                _webViewControlSubscription?.OwnerWindowId,
                _coreWebViewSubscription?.OwnerWindowId,
                _hostedContent is not null);
        }
    }

    public TabRuntimeTransientSnapshot GetTransientSnapshot()
    {
        return _transientState.GetSnapshot();
    }

    public bool SetIsLoaded(bool value)
    {
        return _transientState.SetIsLoaded(value);
    }

    public bool SetIsLoading(bool value)
    {
        return _transientState.SetIsLoading(value);
    }

    public bool SetIsSuspended(bool value)
    {
        return _transientState.SetIsSuspended(value);
    }

    public bool SetIsMemoryReleased(bool value)
    {
        return _transientState.SetIsMemoryReleased(value);
    }

    public bool SetThumbnail(ImageSource? value)
    {
        return _transientState.SetThumbnail(value);
    }

    public bool SetFavicon(ImageSource? value)
    {
        return _transientState.SetFavicon(value);
    }

    public bool TryBeginInitialization(out long generation)
    {
        return _transientState.TryBeginInitialization(out generation);
    }

    public bool IsInitializationCurrent(long generation)
    {
        return _transientState.IsInitializationCurrent(generation);
    }

    public bool EndInitialization(long generation)
    {
        return _transientState.EndInitialization(generation);
    }

    public void CancelInitialization()
    {
        _transientState.CancelInitialization();
    }

    public Task WaitForInitializationAsync()
    {
        return _transientState.WaitForInitializationAsync();
    }

    public long CapturePendingOperationGeneration()
    {
        return _transientState.CapturePendingOperationGeneration();
    }

    public bool IsPendingOperationCurrent(long generation)
    {
        return _transientState.IsPendingOperationCurrent(generation);
    }

    public void InvalidatePendingOperations()
    {
        _transientState.InvalidatePendingOperations();
    }

    public void RegisterHostedContent(UIElement hostedContent)
    {
        ArgumentNullException.ThrowIfNull(hostedContent);

        lock (_gate)
        {
            WaitForResourceReleaseLocked();
            ObjectDisposedException.ThrowIf(_state == TabRuntimeState.Disposed, this);
            if (_hostedContent is not null)
            {
                if (ReferenceEquals(_hostedContent, hostedContent))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "既存のHostedContent資源を解放する前に別のHostedContentを登録できません。");
            }

            _hostedContent = hostedContent;
        }
    }

    public void RegisterWebView(WebView2 webView, string webMessageToken)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentException.ThrowIfNullOrWhiteSpace(webMessageToken);

        lock (_gate)
        {
            WaitForResourceReleaseLocked();
            ObjectDisposedException.ThrowIf(_state == TabRuntimeState.Disposed, this);
            if (_webView is not null)
            {
                if (ReferenceEquals(_webView, webView)
                    && string.Equals(_webMessageToken, webMessageToken, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "既存のWebView2資源を解放する前に別のWebView2を登録できません。");
            }

            _webView = webView;
            _webMessageToken = webMessageToken;
        }
    }

    public IDisposable RegisterEventSubscription(
        TabRuntimeEventSubscriptionKind kind,
        Guid ownerWindowId,
        object sourceIdentity,
        IDisposable subscription)
    {
        if (ownerWindowId == Guid.Empty)
        {
            throw new ArgumentException("所有Window IDが空です。", nameof(ownerWindowId));
        }

        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(subscription);

        EventSubscriptionEntry entry;
        lock (_gate)
        {
            WaitForResourceReleaseLocked();
            ObjectDisposedException.ThrowIf(_state == TabRuntimeState.Disposed, this);
            if (GetEventSubscriptionLocked(kind) is not null)
            {
                throw new InvalidOperationException($"{kind}のイベント購読は既に登録されています。");
            }

            entry = new EventSubscriptionEntry(
                kind,
                ownerWindowId,
                sourceIdentity,
                ++_eventSubscriptionGeneration,
                subscription);
            SetEventSubscriptionLocked(kind, entry);
        }

        return new EventSubscriptionLease(this, entry);
    }

    public void ReleaseWebViewResources()
    {
        WebView2? webView;
        EventSubscriptionEntry? webViewSubscription;
        EventSubscriptionEntry? coreSubscription;
        lock (_gate)
        {
            WaitForResourceReleaseLocked();
            BeginResourceReleaseLocked();
            webView = _webView;
            _webView = null;
            _webMessageToken = null;
            webViewSubscription = _webViewControlSubscription;
            coreSubscription = _coreWebViewSubscription;
            _webViewControlSubscription = null;
            _coreWebViewSubscription = null;
        }

        try
        {
            List<Exception>? exceptions = null;
            DisposeEntry(coreSubscription, ref exceptions);
            DisposeEntry(webViewSubscription, ref exceptions);
            if (webView is not null)
            {
                try
                {
                    if (webView.CoreWebView2 is { } core)
                    {
                        CoreWebViewDocumentScriptRegistry.Clear(core);
                    }
                }
                catch (Exception ex)
                {
                    (exceptions ??= []).Add(ex);
                }

                try
                {
                    webView.Dispose();
                }
                catch (Exception ex)
                {
                    (exceptions ??= []).Add(ex);
                }
            }

            ThrowIfReleaseFailed("TabRuntimeのWebView2資源を解放できませんでした。", exceptions);
        }
        finally
        {
            CompleteResourceRelease();
        }
    }

    public void ReleaseHostedContentResources()
    {
        UIElement? hostedContent;
        lock (_gate)
        {
            WaitForResourceReleaseLocked();
            BeginResourceReleaseLocked();
            hostedContent = _hostedContent;
            _hostedContent = null;
        }

        try
        {
            if (hostedContent is IDisposable disposableHostedContent)
            {
                disposableHostedContent.Dispose();
            }
        }
        finally
        {
            CompleteResourceRelease();
        }
    }

    public TabRuntimeTransitionResult Attach(Guid ownerWindowId)
    {
        if (ownerWindowId == Guid.Empty)
        {
            return Reject("owner-empty");
        }

        lock (_gate)
        {
            if (_state == TabRuntimeState.Disposed)
            {
                return RejectLocked("runtime-disposed");
            }

            if (_state == TabRuntimeState.Attached)
            {
                return _ownerWindowId == ownerWindowId
                    ? UnchangedLocked()
                    : RejectLocked("owner-mismatch-transfer-required");
            }

            if (_state != TabRuntimeState.Detached)
            {
                return RejectLocked("runtime-not-detached");
            }

            _state = TabRuntimeState.Attached;
            _ownerWindowId = ownerWindowId;
            return AppliedLocked();
        }
    }

    public TabRuntimeTransitionResult Detach(Guid ownerWindowId)
    {
        if (ownerWindowId == Guid.Empty)
        {
            return Reject("owner-empty");
        }

        lock (_gate)
        {
            if (_state == TabRuntimeState.Disposed)
            {
                return RejectLocked("runtime-disposed");
            }

            if (_state == TabRuntimeState.Detached)
            {
                return UnchangedLocked();
            }

            if (_state != TabRuntimeState.Attached)
            {
                return RejectLocked("runtime-not-attached");
            }

            if (_ownerWindowId != ownerWindowId)
            {
                return RejectLocked("source-owner-mismatch");
            }

            _state = TabRuntimeState.Detached;
            _ownerWindowId = null;
            return AppliedLocked();
        }
    }

    public TabRuntimeTransitionResult BeginTransfer(Guid sourceWindowId, Guid targetWindowId)
    {
        if (sourceWindowId == Guid.Empty || targetWindowId == Guid.Empty)
        {
            return Reject("owner-empty");
        }

        if (sourceWindowId == targetWindowId)
        {
            return Reject("same-transfer-owner");
        }

        lock (_gate)
        {
            if (_state == TabRuntimeState.Disposed)
            {
                return RejectLocked("runtime-disposed");
            }

            if (_state != TabRuntimeState.Attached)
            {
                return RejectLocked(_state == TabRuntimeState.Transferring
                    ? "transfer-already-active"
                    : "runtime-not-attached");
            }

            if (_ownerWindowId != sourceWindowId)
            {
                return RejectLocked("source-owner-mismatch");
            }

            _transferGeneration++;
            _state = TabRuntimeState.Transferring;
            _ownerWindowId = null;
            _transferSourceWindowId = sourceWindowId;
            _transferTargetWindowId = targetWindowId;
            var token = new TabRuntimeTransferToken(
                RuntimeId,
                sourceWindowId,
                targetWindowId,
                _transferGeneration);
            return new TabRuntimeTransitionResult(
                TabRuntimeTransitionStatus.Applied,
                CreateSnapshot(),
                TransferToken: token);
        }
    }

    public TabRuntimeTransitionResult CommitTransfer(TabRuntimeTransferToken token)
    {
        lock (_gate)
        {
            if (!IsCurrentTransfer(token))
            {
                return RejectLocked(_state == TabRuntimeState.Disposed
                    ? "runtime-disposed"
                    : "transfer-token-mismatch");
            }

            return CommitTransferLocked(token);
        }
    }

    public static TabRuntimeBatchTransitionResult CommitTransfersAtomically(
        IReadOnlyList<TabRuntimeTransferCommit> transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        if (transfers.Count == 0)
        {
            return new TabRuntimeBatchTransitionResult(true);
        }

        var duplicateRuntime = transfers
            .Select(static transfer => transfer.Runtime)
            .GroupBy(static runtime => runtime)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateRuntime is not null)
        {
            var failedIndex = Enumerable.Range(0, transfers.Count)
                .First(index => ReferenceEquals(transfers[index].Runtime, duplicateRuntime.Key));
            lock (duplicateRuntime.Key._gate)
            {
                return new TabRuntimeBatchTransitionResult(
                    false,
                    failedIndex,
                    duplicateRuntime.Key.RejectLocked("runtime-duplicate-in-batch"));
            }
        }

        var lockedRuntimes = transfers
            .Select(static transfer => transfer.Runtime)
            .OrderBy(static runtime => runtime.RuntimeId)
            .ToArray();
        var locksTaken = 0;
        try
        {
            foreach (var runtime in lockedRuntimes)
            {
                System.Threading.Monitor.Enter(runtime._gate);
                locksTaken++;
            }

            for (var index = 0; index < transfers.Count; index++)
            {
                var transfer = transfers[index];
                if (!transfer.Runtime.IsCurrentTransfer(transfer.Token))
                {
                    return new TabRuntimeBatchTransitionResult(
                        false,
                        index,
                        transfer.Runtime.RejectLocked(
                            transfer.Runtime._state == TabRuntimeState.Disposed
                                ? "runtime-disposed"
                                : "transfer-token-mismatch"));
                }
            }

            foreach (var transfer in transfers)
            {
                _ = transfer.Runtime.CommitTransferLocked(transfer.Token);
            }

            return new TabRuntimeBatchTransitionResult(true);
        }
        finally
        {
            for (var index = locksTaken - 1; index >= 0; index--)
            {
                System.Threading.Monitor.Exit(lockedRuntimes[index]._gate);
            }
        }
    }

    public TabRuntimeTransitionResult RollbackTransfer(TabRuntimeTransferToken token)
    {
        lock (_gate)
        {
            if (!IsCurrentTransfer(token))
            {
                return RejectLocked(_state == TabRuntimeState.Disposed
                    ? "runtime-disposed"
                    : "transfer-token-mismatch");
            }

            _state = TabRuntimeState.Attached;
            _ownerWindowId = token.SourceWindowId;
            ClearTransferOwners();
            return AppliedLocked();
        }
    }

    public TabRuntimeTransitionResult Terminate()
    {
        TabRuntimeTransitionResult result;
        lock (_gate)
        {
            if (_state == TabRuntimeState.Disposed)
            {
                result = UnchangedLocked();
            }
            else
            {
                _state = TabRuntimeState.Disposed;
                _ownerWindowId = null;
                ClearTransferOwners();
                result = AppliedLocked();
            }
        }

        List<Exception>? releaseExceptions = null;
        RunRelease(ReleaseWebViewResources, ref releaseExceptions);
        RunRelease(ReleaseHostedContentResources, ref releaseExceptions);
        RunRelease(_transientState.ClearForTermination, ref releaseExceptions);
        if (releaseExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "TabRuntimeは終端状態になりましたが、資源解放に失敗しました。",
                releaseExceptions.Count == 1
                    ? releaseExceptions[0]
                    : new AggregateException(releaseExceptions));
        }

        return result;
    }

    private static void RunRelease(Action release, ref List<Exception>? exceptions)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            (exceptions ??= []).Add(ex);
        }
    }

    private void ReleaseEventSubscription(EventSubscriptionEntry expected)
    {
        IDisposable? subscription = null;
        lock (_gate)
        {
            var current = GetEventSubscriptionLocked(expected.Kind);
            if (current is null
                || current.Generation != expected.Generation
                || current.OwnerWindowId != expected.OwnerWindowId
                || !ReferenceEquals(current.SourceIdentity, expected.SourceIdentity)
                || !ReferenceEquals(current.Subscription, expected.Subscription))
            {
                return;
            }

            SetEventSubscriptionLocked(expected.Kind, null);
            subscription = current.Subscription;
        }

        subscription.Dispose();
    }

    private void WaitForResourceReleaseLocked()
    {
        while (_resourceReleaseInProgress)
        {
            if (_resourceReleaseThreadId == Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("TabRuntime資源の解放中に同じRuntimeを再変更できません。");
            }

            Monitor.Wait(_gate);
        }
    }

    private void BeginResourceReleaseLocked()
    {
        _resourceReleaseInProgress = true;
        _resourceReleaseThreadId = Environment.CurrentManagedThreadId;
    }

    private void CompleteResourceRelease()
    {
        lock (_gate)
        {
            _resourceReleaseInProgress = false;
            _resourceReleaseThreadId = 0;
            Monitor.PulseAll(_gate);
        }
    }

    private EventSubscriptionEntry? GetEventSubscriptionLocked(TabRuntimeEventSubscriptionKind kind)
    {
        return kind == TabRuntimeEventSubscriptionKind.WebViewControl
            ? _webViewControlSubscription
            : _coreWebViewSubscription;
    }

    private void SetEventSubscriptionLocked(
        TabRuntimeEventSubscriptionKind kind,
        EventSubscriptionEntry? subscription)
    {
        if (kind == TabRuntimeEventSubscriptionKind.WebViewControl)
        {
            _webViewControlSubscription = subscription;
        }
        else
        {
            _coreWebViewSubscription = subscription;
        }
    }

    private static void DisposeEntry(
        EventSubscriptionEntry? entry,
        ref List<Exception>? exceptions)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            entry.Subscription.Dispose();
        }
        catch (Exception ex)
        {
            (exceptions ??= []).Add(ex);
        }
    }

    private static void ThrowIfReleaseFailed(
        string message,
        IEnumerable<EventSubscriptionEntry?> entries)
    {
        List<Exception>? exceptions = null;
        foreach (var entry in entries)
        {
            DisposeEntry(entry, ref exceptions);
        }

        ThrowIfReleaseFailed(message, exceptions);
    }

    private static void ThrowIfReleaseFailed(string message, List<Exception>? exceptions)
    {
        if (exceptions is null)
        {
            return;
        }

        if (exceptions.Count == 1)
        {
            throw exceptions[0];
        }

        throw new AggregateException(message, exceptions);
    }

    private TabRuntimeTransitionResult CommitTransferLocked(TabRuntimeTransferToken token)
    {
        _state = TabRuntimeState.Attached;
        _ownerWindowId = token.TargetWindowId;
        ClearTransferOwners();
        return AppliedLocked();
    }

    private bool IsCurrentTransfer(TabRuntimeTransferToken token)
    {
        return _state == TabRuntimeState.Transferring
            && token.RuntimeId == RuntimeId
            && token.Generation == _transferGeneration
            && token.SourceWindowId == _transferSourceWindowId
            && token.TargetWindowId == _transferTargetWindowId;
    }

    private void ClearTransferOwners()
    {
        _transferSourceWindowId = null;
        _transferTargetWindowId = null;
    }

    private TabRuntimeTransitionResult Reject(string reason)
    {
        lock (_gate)
        {
            return RejectLocked(reason);
        }
    }

    private TabRuntimeTransitionResult AppliedLocked()
    {
        return new TabRuntimeTransitionResult(
            TabRuntimeTransitionStatus.Applied,
            CreateSnapshot());
    }

    private TabRuntimeTransitionResult UnchangedLocked()
    {
        return new TabRuntimeTransitionResult(
            TabRuntimeTransitionStatus.Unchanged,
            CreateSnapshot());
    }

    private TabRuntimeTransitionResult RejectLocked(string reason)
    {
        return new TabRuntimeTransitionResult(
            TabRuntimeTransitionStatus.Rejected,
            CreateSnapshot(),
            reason);
    }

    private TabRuntimeSnapshot CreateSnapshot()
    {
        return new TabRuntimeSnapshot(
            RuntimeId,
            _state,
            _ownerWindowId,
            _transferSourceWindowId,
            _transferTargetWindowId,
            _transferGeneration);
    }

    private sealed record EventSubscriptionEntry(
        TabRuntimeEventSubscriptionKind Kind,
        Guid OwnerWindowId,
        object SourceIdentity,
        long Generation,
        IDisposable Subscription);

    private sealed class EventSubscriptionLease(
        TabRuntime runtime,
        EventSubscriptionEntry entry) : IDisposable
    {
        private TabRuntime? _runtime = runtime;

        public void Dispose()
        {
            Interlocked.Exchange(ref _runtime, null)?.ReleaseEventSubscription(entry);
        }
    }
}
