using System.Windows.Media;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class UrlThumbnailCaptureCoordinator
{
    internal const int MaximumCachedThumbnails = 64;

    private readonly Dictionary<string, LinkedListNode<CachedThumbnail>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CachedThumbnail> _cacheUsage = [];
    private readonly Dictionary<string, List<CommandPaletteItem>> _pendingCommandPaletteItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _captureClaims = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    public bool TryGetCached(string url, out ImageSource thumbnail)
    {
        if (_cache.TryGetValue(url, out var node))
        {
            _cacheUsage.Remove(node);
            _cacheUsage.AddFirst(node);
            thumbnail = node.Value.Image;
            return true;
        }

        thumbnail = null!;
        return false;
    }

    public void StoreCached(string url, ImageSource thumbnail)
    {
        if (_cache.Remove(url, out var previous))
        {
            _cacheUsage.Remove(previous);
        }

        _cache.Add(url, _cacheUsage.AddFirst(new CachedThumbnail(url, thumbnail)));
        if (_cache.Count > MaximumCachedThumbnails && _cacheUsage.Last is { } oldest)
        {
            _cache.Remove(oldest.Value.Url);
            _cacheUsage.RemoveLast();
        }
    }

    public void TrackCommandPaletteItem(string url, CommandPaletteItem item)
    {
        if (!_pendingCommandPaletteItems.TryGetValue(url, out var items))
        {
            items = [];
            _pendingCommandPaletteItems[url] = items;
        }

        if (!items.Contains(item))
        {
            items.Add(item);
        }
    }

    public IReadOnlyList<CommandPaletteItem> CompleteCommandPaletteItems(
        string url,
        ImageSource thumbnail,
        bool cache = true)
    {
        if (cache)
        {
            StoreCached(url, thumbnail);
        }

        if (!_pendingCommandPaletteItems.Remove(url, out var items))
        {
            return [];
        }

        return items.Distinct().ToList();
    }

    public bool TryBeginCapture(string url)
    {
        return _captureClaims.Add(url);
    }

    public void EndCapture(string url)
    {
        _captureClaims.Remove(url);
    }

    public async Task<IDisposable> EnterCaptureAsync(CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken);
        return new CaptureLease(_captureGate);
    }

    public static string? NormalizeUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var url = rawUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || (uri.IsFile && !BrowserUrlRulesService.IsRemoteFileUrl(uri))
            ? url
            : null;
    }

    private sealed record CachedThumbnail(string Url, ImageSource Image);

    private sealed class CaptureLease(SemaphoreSlim captureGate) : IDisposable
    {
        private SemaphoreSlim? _captureGate = captureGate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _captureGate, null)?.Release();
        }
    }
}
