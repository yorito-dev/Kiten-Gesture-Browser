using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct TabRuntimeResources(
    WebView2? WebView,
    string? WebMessageToken,
    UIElement? HostedContent);

internal readonly record struct TabRuntimeTransferRequest(
    BrowserTab Tab,
    TabRuntimeTransferToken Token);

/// <summary>
/// BrowserTabに対応するRuntimeの取得、状態変更、所有権遷移を集約します。
/// WPFの具体的なhost操作とイベントhandler生成はWindow側に残します。
/// </summary>
internal sealed class TabRuntimeCoordinator
{
    private readonly TabRuntimeRegistry _registry;
    private readonly TabRuntimeLifetimeService _lifetimeService;
    private readonly RebindableEventSubscriptionOwner<TabRuntime, object> _eventOwnership = new();

    public static TabRuntimeCoordinator Shared { get; } = new(TabRuntimeRegistry.Shared);

    internal TabRuntimeCoordinator(TabRuntimeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lifetimeService = new TabRuntimeLifetimeService(_registry);
    }

    public TabRuntimeResources GetResources(BrowserTab tab)
    {
        if (!TryGetRuntime(tab, out var runtime))
        {
            return default;
        }

        return new TabRuntimeResources(
            runtime!.WebView,
            runtime.WebMessageToken,
            runtime.HostedContent);
    }

    public WebView2? GetWebView(BrowserTab tab) =>
        TryGetRuntime(tab, out var runtime) ? runtime!.WebView : null;

    public string? GetWebMessageToken(BrowserTab tab) =>
        TryGetRuntime(tab, out var runtime) ? runtime!.WebMessageToken : null;

    public UIElement? GetHostedContent(BrowserTab tab) =>
        TryGetRuntime(tab, out var runtime) ? runtime!.HostedContent : null;

    public void RegisterWebView(BrowserTab tab, WebView2 webView, string webMessageToken)
    {
        GetRuntime(tab).RegisterWebView(webView, webMessageToken);
    }

    public void RegisterHostedContent(BrowserTab tab, UIElement hostedContent)
    {
        GetRuntime(tab).RegisterHostedContent(hostedContent);
    }

    public IDisposable RegisterEventSubscription(
        BrowserTab tab,
        TabRuntimeEventSubscriptionKind kind,
        Guid ownerWindowId,
        object sourceIdentity,
        IDisposable subscription)
    {
        return GetRuntime(tab).RegisterEventSubscription(
            kind,
            ownerWindowId,
            sourceIdentity,
            subscription);
    }

    public bool RebindEventOwnership(
        BrowserTab tab,
        object owner,
        object sourceIdentity,
        int sourceGeneration,
        Func<IDisposable> attach)
    {
        return _eventOwnership.Rebind(
            GetRuntime(tab),
            owner,
            sourceIdentity,
            sourceGeneration,
            attach);
    }

    public bool TryAugmentEventOwnership(
        BrowserTab tab,
        object owner,
        object sourceIdentity,
        int expectedGeneration,
        int newGeneration,
        Func<IDisposable> attach)
    {
        return _eventOwnership.TryAugment(
            GetRuntime(tab),
            owner,
            sourceIdentity,
            expectedGeneration,
            newGeneration,
            attach);
    }

    public bool DetachEventOwnership(BrowserTab tab, object owner)
    {
        return _registry.TryGet(tab, out var runtime)
            && runtime is not null
            && _eventOwnership.Detach(runtime, owner);
    }

    public bool TryGetEventOwner<TOwner>(BrowserTab tab, out TOwner? owner)
        where TOwner : class
    {
        if (_registry.TryGet(tab, out var runtime)
            && runtime is not null
            && _eventOwnership.TryGetOwner(runtime, out var current)
            && current is TOwner typedOwner)
        {
            owner = typedOwner;
            return true;
        }

        owner = null;
        return false;
    }

    public TabRuntimeSnapshot GetOwnershipSnapshot(BrowserTab tab)
    {
        return GetRuntime(tab).GetSnapshot();
    }

    public TabRuntimeResourceSnapshot GetResourceSnapshot(BrowserTab tab)
    {
        return GetRuntime(tab).GetResourceSnapshot();
    }

    public TabRuntimeTransientSnapshot GetTransientSnapshot(BrowserTab tab)
    {
        return GetRuntime(tab).GetTransientSnapshot();
    }

    public TabRuntimeTransitionResult Attach(BrowserTab tab, Guid ownerWindowId)
    {
        return GetRuntime(tab).Attach(ownerWindowId);
    }

    public TabRuntimeTransitionResult Detach(BrowserTab tab, Guid ownerWindowId)
    {
        return GetRuntime(tab).Detach(ownerWindowId);
    }

    public TabRuntimeTransitionResult BeginTransfer(
        BrowserTab tab,
        Guid sourceWindowId,
        Guid targetWindowId)
    {
        return GetRuntime(tab).BeginTransfer(sourceWindowId, targetWindowId);
    }

    public TabRuntimeBatchTransitionResult CommitTransfersAtomically(
        IReadOnlyList<TabRuntimeTransferRequest> transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        return TabRuntime.CommitTransfersAtomically(transfers
            .Select(transfer => new TabRuntimeTransferCommit(
                GetRuntime(transfer.Tab),
                transfer.Token))
            .ToList());
    }

    public TabRuntimeTransitionResult RollbackTransfer(
        BrowserTab tab,
        TabRuntimeTransferToken token)
    {
        return GetRuntime(tab).RollbackTransfer(token);
    }

    public bool CanUseInWindow(BrowserTab tab, Guid windowId, bool isTabInWindow)
    {
        if (!isTabInWindow)
        {
            return false;
        }

        var snapshot = GetOwnershipSnapshot(tab);
        return snapshot.State == TabRuntimeState.Attached
            && snapshot.OwnerWindowId == windowId;
    }

    public bool HasLoadedRuntime(BrowserTab tab)
    {
        return GetWebView(tab) is not null && tab.IsLoaded;
    }

    public bool CanPreserveRuntime(BrowserTab tab)
    {
        return !tab.IsNativeContentTab
            && GetWebView(tab)?.CoreWebView2 is not null
            && tab.IsLoaded;
    }

    public bool SetLoaded(BrowserTab tab, bool value) => GetRuntime(tab).SetIsLoaded(value);

    public bool SetLoading(BrowserTab tab, bool value) => GetRuntime(tab).SetIsLoading(value);

    public bool SetSuspended(BrowserTab tab, bool value) => GetRuntime(tab).SetIsSuspended(value);

    public bool SetMemoryReleased(BrowserTab tab, bool value) =>
        GetRuntime(tab).SetIsMemoryReleased(value);

    public bool SetThumbnail(BrowserTab tab, ImageSource? value) =>
        GetRuntime(tab).SetThumbnail(value);

    public bool SetFavicon(BrowserTab tab, ImageSource? value) =>
        GetRuntime(tab).SetFavicon(value);

    public bool TryBeginInitialization(BrowserTab tab, out long generation)
    {
        return GetRuntime(tab).TryBeginInitialization(out generation);
    }

    public bool IsInitializationCurrent(BrowserTab tab, long generation)
    {
        return GetRuntime(tab).IsInitializationCurrent(generation);
    }

    public bool EndInitialization(BrowserTab tab, long generation)
    {
        return GetRuntime(tab).EndInitialization(generation);
    }

    public void CancelInitialization(BrowserTab tab)
    {
        GetRuntime(tab).CancelInitialization();
    }

    public Task WaitForInitializationAsync(BrowserTab tab)
    {
        return GetRuntime(tab).WaitForInitializationAsync();
    }

    public long CapturePendingOperationGeneration(BrowserTab tab)
    {
        return GetRuntime(tab).CapturePendingOperationGeneration();
    }

    public bool IsPendingOperationCurrent(BrowserTab tab, long generation)
    {
        return GetRuntime(tab).IsPendingOperationCurrent(generation);
    }

    public void InvalidatePendingOperations(BrowserTab tab)
    {
        GetRuntime(tab).InvalidatePendingOperations();
    }

    public void ResetTransientState(BrowserTab tab)
    {
        var runtime = GetRuntime(tab);
        _ = runtime.SetIsLoading(false);
        _ = runtime.SetIsLoaded(false);
        _ = runtime.SetIsSuspended(false);
        _ = runtime.SetIsMemoryReleased(false);
        runtime.CancelInitialization();
        runtime.InvalidatePendingOperations();
    }

    public void ReleaseResources(
        BrowserTab tab,
        Action<TabRuntimeResources> prepare,
        Action? afterRelease = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        _lifetimeService.ReleaseResources(
            tab,
            runtime => PrepareAndReleaseWebView(runtime, prepare, afterRelease));
    }

    public void Terminate(
        BrowserTab tab,
        Action<TabRuntimeResources> prepare,
        Action? afterRelease = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        _lifetimeService.Terminate(
            tab,
            runtime => PrepareAndReleaseWebView(runtime, prepare, afterRelease));
    }

    private static void PrepareAndReleaseWebView(
        TabRuntime runtime,
        Action<TabRuntimeResources> prepare,
        Action? afterRelease)
    {
        List<Exception>? exceptions = null;
        var resources = new TabRuntimeResources(
            runtime.WebView,
            runtime.WebMessageToken,
            runtime.HostedContent);
        try
        {
            prepare(resources);
        }
        catch (Exception ex)
        {
            (exceptions ??= []).Add(ex);
        }

        try
        {
            runtime.ReleaseWebViewResources();
        }
        catch (Exception ex)
        {
            (exceptions ??= []).Add(ex);
        }

        try
        {
            afterRelease?.Invoke();
        }
        catch (Exception ex)
        {
            (exceptions ??= []).Add(ex);
        }

        if (exceptions is { Count: 1 })
        {
            throw exceptions[0];
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(
                "タブのRuntime資源をすべて解放できませんでした。",
                exceptions);
        }
    }

    private TabRuntime GetRuntime(BrowserTab tab)
    {
        return _registry.GetOrCreate(tab);
    }

    private bool TryGetRuntime(BrowserTab tab, out TabRuntime? runtime)
    {
        return _registry.TryGet(tab, out runtime) && runtime is not null;
    }
}
