using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

// 汎用CDNや本文と同じ配信元を広く遮断せず、広告配信専用ホストに限定する。
internal static class AdBlockService
{
    internal static readonly string[] AdvertisingDomains =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "googletagservices.com",
        "adservice.google.com", "adservice.google.co.jp", "amazon-adsystem.com", "adnxs.com",
        "adsrvr.org", "advertising.com", "adtechjp.com", "adtech.de", "ads.yahoo.com",
        "yads.yahoo.co.jp", "yads.c.yimg.jp", "ads.pubmatic.com", "pubmatic.com",
        "rubiconproject.com", "openx.net", "openx.com", "criteo.com", "criteo.net",
        "casalemedia.com", "smartadserver.com", "adform.net", "bidswitch.net", "bidr.io",
        "media.net", "outbrain.com", "taboola.com", "teads.tv", "tremorhub.com",
        "yieldmo.com", "yieldone.com", "fluct.jp", "adingo.jp", "i-mobile.co.jp",
        "microad.jp", "microad.net", "ad-stir.com", "adroute.focas.jp", "nend.net",
        "ad-m.asia", "popin.cc", "logly.co.jp", "ad-generation.jp", "adlantis.jp",
        "ad-sp.jp", "ads.adjust-net.jp", "ads.gunosy.com", "ads.smartnews.com",
        "ad-delivery.net", "adnxs-simple.com", "demdex.net", "contextweb.com",
        "indexww.com", "lijit.com", "sovrn.com", "sharethrough.com", "spotxchange.com"
    ];
    private static readonly HashSet<string> Domains = new(AdvertisingDomains, StringComparer.Ordinal);

    internal static bool IsYouTubeUrl(string? url) => TryGetHost(url, out var host)
        && (IsHostOrSubdomain(host, "youtube.com") || IsHostOrSubdomain(host, "youtube-nocookie.com")
            || IsHostOrSubdomain(host, "youtu.be"));

    internal static bool TryGetHost(string? url, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.Length > 0;
    }

    internal static bool ShouldCollapseSpace(AdBlockingSettings settings, string? pageUrl, bool? privateExcluded = null) =>
        settings.IsEnabled && TryGetHost(pageUrl, out var host) && !IsYouTubeUrl(pageUrl)
        && !(privateExcluded ?? AdBlockingSettingsProfile.IsExcluded(settings, host));

    internal static bool ShouldBlock(AdBlockingSettings settings, string? pageUrl, string? requestUrl,
        bool isTopLevelDocument, bool excludeDocument, bool? privateExcluded = null,
        CoreWebView2WebResourceRequestSourceKinds sourceKind = CoreWebView2WebResourceRequestSourceKinds.Document,
        string? referrer = null, string? origin = null)
    {
        if (sourceKind != CoreWebView2WebResourceRequestSourceKinds.Document
            || !settings.IsEnabled || isTopLevelDocument
            || !TryGetHost(pageUrl, out var pageHost) || IsYouTubeUrl(pageUrl) || IsYouTubeUrl(requestUrl)
            || IsYouTubeUrl(referrer) || IsYouTubeUrl(origin)
            // 埋め込みがあっても、親ページと同じoriginからの要求は判定する。
            // Refererなし・別origin・矛盾するOriginはYouTube由来を否定できないため通す。
            || (excludeDocument && (!HasSameOrigin(pageUrl, referrer)
                || (origin is not null && !HasSameOrigin(pageUrl, origin))))
            || (privateExcluded ?? AdBlockingSettingsProfile.IsExcluded(settings, pageHost))
            || !TryGetHost(requestUrl, out var host) || host == pageHost) return false;
        for (var candidate = host; ;)
        {
            if (Domains.Contains(candidate)) return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0) return false;
            candidate = candidate[(dot + 1)..];
        }
    }

    private static bool HasSameOrigin(string? pageUrl, string? source) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
        && Uri.TryCreate(source, UriKind.Absolute, out var requestSource)
        && page.Scheme is "http" or "https" && requestSource.UserInfo.Length == 0
        && page.Scheme == requestSource.Scheme && page.Port == requestSource.Port
        && page.IdnHost.TrimEnd('.').Equals(requestSource.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsHostOrSubdomain(string host, string domain) => host == domain
        || host.EndsWith("." + domain, StringComparison.Ordinal);
}

// この状態はCoreWebView2と同じ寿命。Window間の移動でYouTube除外を失わない。
internal sealed class AdBlockPageState
{
    private static readonly ConditionalWeakTable<object, AdBlockPageState> States = new();
    internal static AdBlockPageState ForCore(object core) => States.GetValue(core, static _ => new());
    internal bool ExcludeDocument { get; private set; }
    internal int BlockedCount { get; private set; }
    internal List<WeakReference<CoreWebView2Frame>> Frames { get; } = [];
    internal SemaphoreSlim CosmeticGate { get; } = new(1, 1);
    internal void ObserveFrame(string? url)
    {
        if (AdBlockService.IsYouTubeUrl(url)) ExcludeDocument = true;
    }
    internal void ExcludeUnobservableFrame() => ExcludeDocument = true;
    internal void RecordBlocked() => BlockedCount++;
    internal void BeginDocument()
    {
        ExcludeDocument = false;
        BlockedCount = 0;
        Frames.Clear();
    }
}

internal sealed class AdBlockWebViewController : IDisposable
{
    private static readonly ConditionalWeakTable<CoreWebView2, AdBlockWebViewController> Bindings = new();
    private readonly CoreWebView2 _core;
    private readonly Func<AdBlockingSettings> _getSettings;
    private readonly Func<string, bool?> _getPrivateExcluded;
    private readonly AdBlockPageState _state;
    private readonly List<string> _filters = [];
    private readonly Dictionary<CoreWebView2Frame, Action> _frames = [];
    private string? _navigationTarget;
    private bool _disposed;
    private long _documentVersion;
    private readonly string _cosmeticOwner = Guid.NewGuid().ToString("N");
    internal Task CosmeticCompletion { get; private set; } = Task.CompletedTask;
    internal bool IsAvailable { get; private set; }

    internal static AdBlockWebViewController? Find(CoreWebView2? core) =>
        core is not null && Bindings.TryGetValue(core, out var controller) ? controller : null;
    internal int BlockedCount => _state.BlockedCount;
    internal bool ExcludesEmbeddedYouTube => _state.ExcludeDocument;

    internal AdBlockWebViewController(CoreWebView2 core, Func<AdBlockingSettings> getSettings,
        Func<string, bool?> getPrivateExcluded)
    {
        _core = core;
        _getSettings = getSettings;
        _getPrivateExcluded = getPrivateExcluded;
        if (Bindings.TryGetValue(core, out var previous)) previous.Dispose();
        _state = AdBlockPageState.ForCore(core);
        Bindings.Add(core, this);
        try
        {
            core.WebResourceRequested += ResourceRequested;
            core.NavigationStarting += NavigationStarting;
            core.ContentLoading += ContentLoading;
            core.FrameCreated += FrameCreated;
            core.DOMContentLoaded += DomContentLoaded;
            core.NavigationCompleted += NavigationCompleted;
            // 広告ホスト候補だけをネイティブ側で絞り、一般の通信をUIスレッドへ送らない。
            foreach (var domain in AdBlockService.AdvertisingDomains)
            {
                var filter = "*://*" + domain + "/*";
                core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.Document);
                _filters.Add(filter);
            }
            foreach (var reference in _state.Frames.ToArray())
                if (reference.TryGetTarget(out var frame)) AttachFrame(frame, remember: false);
            IsAvailable = true;
            _ = RefreshCosmeticAsync();
        }
        catch
        {
            // 対応しないRuntimeや破棄との競合でも、通常のページ表示を妨げない。
            Dispose();
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _navigationTarget = args.Uri;
        _documentVersion++;
    }
    private void DomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs args) => _ = RefreshCosmeticAsync();
    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) => _ = RefreshCosmeticAsync();
    internal Task RefreshCosmeticAsync() => _disposed ? CosmeticCompletion : CosmeticCompletion = UpdateCosmeticAsync(dispose: false);
    private async Task UpdateCosmeticAsync(bool dispose)
    {
        try
        {
            var version = _documentVersion;
            var url = _core.Source;
            await _state.CosmeticGate.WaitAsync();
            try
            {
                if (!dispose && (_disposed || Find(_core) != this || version != _documentVersion)) return;
                AdBlockService.TryGetHost(url, out var host);
                var settings = _getSettings();
                var enabled = !dispose && AdBlockService.ShouldCollapseSpace(settings, url, _getPrivateExcluded(host));
                await _core.ExecuteScriptAsync(AdSpaceCollapseScript.Build(url, _cosmeticOwner, enabled, dispose));
            }
            finally { _state.CosmeticGate.Release(); }
        }
        catch
        {
            // 破棄・移動・ページ側の失敗で通常表示を妨げず、本文やURLをログへ出さない。
        }
    }
    private void ContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs args)
    {
        if (_disposed) return;
        DetachFrames();
        _state.BeginDocument();
    }
    private void FrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args) => AttachFrame(args.Frame);
    private void AttachFrame(CoreWebView2Frame frame, bool remember = true)
    {
        if (_disposed || _frames.ContainsKey(frame)) return;
        EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (_, args) =>
        {
            if (!_disposed) _state.ObserveFrame(args.Uri);
        };
        EventHandler<CoreWebView2FrameCreatedEventArgs> created = (_, args) => AttachFrame(args.Frame);
        EventHandler<object> destroyed = (_, _) =>
        {
            // 既存の音声frame購読と同じく、Destroyed内で破棄済みCOMへ解除を呼ばない。
            _frames.Remove(frame);
        };
        void Detach()
        {
            TryDetach(() => frame.NavigationStarting -= starting);
            TryDetach(() => frame.FrameCreated -= created);
            TryDetach(() => frame.Destroyed -= destroyed);
        }
        try
        {
            if (frame.IsDestroyed() != 0) return;
            frame.NavigationStarting += starting;
            frame.FrameCreated += created;
            frame.Destroyed += destroyed;
            _frames.Add(frame, Detach);
            if (remember) _state.Frames.Add(new(frame));
        }
        catch
        {
            Detach();
            _state.ExcludeUnobservableFrame();
        }
    }
    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_disposed || !IsAvailable) return;
        try
        {
            var url = args.Request.Uri;
            var pageUrl = _core.Source;
            var topDocument = args.ResourceContext == CoreWebView2WebResourceContext.Document
                && (url == _navigationTarget || url == pageUrl);
            if (AdBlockService.IsYouTubeUrl(_navigationTarget)) return;
            string? referrer = null;
            string? origin = null;
            foreach (var name in new[] { "Referer", "Origin" })
            {
                if (!args.Request.Headers.Contains(name)) continue;
                var value = args.Request.Headers.GetHeader(name);
                if (name == "Referer") referrer = value;
                else origin = value;
                if (AdBlockService.IsYouTubeUrl(value))
                {
                    _state.ObserveFrame(value);
                    return;
                }
            }
            AdBlockService.TryGetHost(pageUrl, out var host);
            if (!AdBlockService.ShouldBlock(_getSettings(), pageUrl, url, topDocument,
                    _state.ExcludeDocument, _getPrivateExcluded(host), args.RequestedSourceKind, referrer, origin)) return;
            args.Response = _core.Environment.CreateWebResourceResponse(null, 204, "No Content", "Cache-Control: no-store\r\n");
            _state.RecordBlocked();
        }
        catch
        {
            // URL・ヘッダー・Cookie・本文を記録せず、判定できない要求はそのまま通す。
        }
    }
    private void DetachFrames()
    {
        foreach (var detach in _frames.Values.ToArray()) detach();
        _frames.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsAvailable = false;
        TryDetach(() => _core.WebResourceRequested -= ResourceRequested);
        TryDetach(() => _core.NavigationStarting -= NavigationStarting);
        TryDetach(() => _core.ContentLoading -= ContentLoading);
        TryDetach(() => _core.FrameCreated -= FrameCreated);
        TryDetach(() => _core.DOMContentLoaded -= DomContentLoaded);
        TryDetach(() => _core.NavigationCompleted -= NavigationCompleted);
        DetachFrames();
        foreach (var filter in _filters)
            TryDetach(() => _core.RemoveWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document));
        _filters.Clear();
        if (Bindings.TryGetValue(_core, out var current) && ReferenceEquals(current, this)) Bindings.Remove(_core);
        CosmeticCompletion = UpdateCosmeticAsync(dispose: true);
    }
    private static void TryDetach(Action detach)
    {
        try { detach(); }
        catch { /* WebView2の終了中でも他の購読の解除を続ける。 */ }
    }
}
