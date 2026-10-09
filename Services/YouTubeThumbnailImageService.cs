using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabBrowser.Services;

public sealed class YouTubeThumbnailImageService
{
    private const int DefaultCacheCapacity = 96;
    private const int MaximumThumbnailBytes = 2 * 1024 * 1024;

    private static readonly Lazy<YouTubeThumbnailImageService> SharedInstance =
        new(() => new YouTubeThumbnailImageService(CreateHttpClient()));

    private readonly int _cacheCapacity;
    private readonly HttpClient _httpClient;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cacheOrder = [];
    private readonly Dictionary<string, Task<ImageSource?>> _pending = new(StringComparer.Ordinal);

    public YouTubeThumbnailImageService(
        HttpClient httpClient,
        int cacheCapacity = DefaultCacheCapacity)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheCapacity = Math.Max(1, cacheCapacity);
    }

    public static YouTubeThumbnailImageService Shared => SharedInstance.Value;

    public bool TryGetCached(string? videoUrl, out ImageSource? thumbnail)
    {
        thumbnail = null;
        var thumbnailUri = YouTubeUrlService.CreateThumbnailUri(videoUrl);
        if (thumbnailUri is null)
        {
            return false;
        }

        lock (_syncRoot)
        {
            return TryGetCachedThumbnail(thumbnailUri.AbsoluteUri, out thumbnail);
        }
    }

    public async Task<ImageSource?> GetAsync(
        string? videoUrl,
        CancellationToken cancellationToken = default)
    {
        var thumbnailUri = YouTubeUrlService.CreateThumbnailUri(videoUrl);
        if (thumbnailUri is null)
        {
            return null;
        }

        var cacheKey = thumbnailUri.AbsoluteUri;
        Task<ImageSource?> pendingTask;
        TaskCompletionSource<ImageSource?>? completionSource = null;
        lock (_syncRoot)
        {
            if (TryGetCachedThumbnail(cacheKey, out var cachedThumbnail))
            {
                return cachedThumbnail;
            }

            if (!_pending.TryGetValue(cacheKey, out pendingTask!))
            {
                completionSource = new TaskCompletionSource<ImageSource?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingTask = completionSource.Task;
                _pending[cacheKey] = pendingTask;
            }
        }

        if (completionSource is not null)
        {
            _ = DownloadAndCompleteAsync(
                thumbnailUri,
                cacheKey,
                completionSource,
                pendingTask);
        }

        return await pendingTask.WaitAsync(cancellationToken);
    }

    private async Task DownloadAndCompleteAsync(
        Uri thumbnailUri,
        string cacheKey,
        TaskCompletionSource<ImageSource?> completionSource,
        Task<ImageSource?> pendingTask)
    {
        ImageSource? thumbnail = null;
        try
        {
            thumbnail = await DownloadAsync(thumbnailUri);
            if (thumbnail is not null)
            {
                lock (_syncRoot)
                {
                    AddToCache(cacheKey, thumbnail);
                }
            }
        }
        catch
        {
            thumbnail = null;
        }
        finally
        {
            completionSource.TrySetResult(thumbnail);
            lock (_syncRoot)
            {
                if (_pending.TryGetValue(cacheKey, out var currentTask)
                    && ReferenceEquals(currentTask, pendingTask))
                {
                    _pending.Remove(cacheKey);
                }
            }
        }
    }

    private async Task<ImageSource?> DownloadAsync(Uri thumbnailUri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, thumbnailUri);
        request.Headers.Accept.ParseAdd("image/*");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType?.StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase) != true
            || response.Content.Headers.ContentLength is > MaximumThumbnailBytes)
        {
            return null;
        }

        var bytes = await ReadLimitedBytesAsync(
            response.Content,
            MaximumThumbnailBytes,
            timeout.Token);
        return CreateImage(bytes);
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                return [];
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static ImageSource? CreateImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 480;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetCachedThumbnail(string cacheKey, out ImageSource? thumbnail)
    {
        if (!_cache.TryGetValue(cacheKey, out var entry))
        {
            thumbnail = null;
            return false;
        }

        _cacheOrder.Remove(entry.OrderNode);
        _cacheOrder.AddLast(entry.OrderNode);
        thumbnail = entry.Thumbnail;
        return true;
    }

    private void AddToCache(string cacheKey, ImageSource thumbnail)
    {
        if (_cache.Remove(cacheKey, out var existingEntry))
        {
            _cacheOrder.Remove(existingEntry.OrderNode);
        }

        var orderNode = _cacheOrder.AddLast(cacheKey);
        _cache[cacheKey] = new CacheEntry(thumbnail, orderNode);
        while (_cache.Count > _cacheCapacity && _cacheOrder.First is { } oldestNode)
        {
            _cacheOrder.RemoveFirst();
            _cache.Remove(oldestNode.Value);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(7)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TabBrowser/1.0");
        return client;
    }

    private sealed record CacheEntry(ImageSource Thumbnail, LinkedListNode<string> OrderNode);
}
