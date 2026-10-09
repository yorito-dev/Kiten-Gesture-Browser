using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;

namespace TabBrowser.Services.QuickLook;

internal sealed class QuickLookMultipleSelectionRenderer : IQuickLookRenderer
{
    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count > 1;
    }

    public Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var knownSize = request.Items.Where(static item => item.Size is not null).Sum(static item => item.Size!.Value);
        var typeLines = request.Items
            .GroupBy(static item => item.Kind)
            .OrderByDescending(static group => group.Count())
            .Select(group => $"{GetKindLabel(group.Key)}: {group.Count():N0}件")
            .ToList();
        if (knownSize > 0)
        {
            typeLines.Add($"取得済みサイズ合計: {FormatSize(knownSize)}");
        }

        return Task.FromResult(new QuickLookRenderResult(
            QuickLookRenderKind.Information,
            $"{request.Items.Count:N0}件を選択中",
            request.SourceId,
            string.Join(Environment.NewLine, typeLines)));
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static string GetKindLabel(QuickLookItemKind kind)
    {
        return kind switch
        {
            QuickLookItemKind.File => "ファイル",
            QuickLookItemKind.Directory => "フォルダー",
            QuickLookItemKind.Text => "テキスト",
            QuickLookItemKind.Uri => "URL",
            _ => "その他"
        };
    }

    internal static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, size);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:N0} {units[unitIndex]}" : $"{value:N1} {units[unitIndex]}";
    }
}

internal sealed class QuickLookWebSummaryRenderer : IQuickLookRenderer
{
    private const int ThumbnailDecodePixelWidth = 640;
    private const int FaviconDecodePixelWidth = 64;

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && TryGetWebUri(request.PrimaryItem, out _);
    }

    public async Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        var item = request.PrimaryItem!;
        cancellationToken.ThrowIfCancellationRequested();
        var thumbnail = item.Thumbnail ?? DecodeImage(item.ThumbnailPngBase64, ThumbnailDecodePixelWidth);
        if (thumbnail is null
            && YouTubeUrlService.IsVideoUrl(item.Uri ?? string.Empty))
        {
            thumbnail = await YouTubeThumbnailImageService.Shared.GetAsync(
                item.Uri,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateResult(item, thumbnail, CreateFavicon(item));
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    internal static QuickLookRenderResult CreateCachedResult(QuickLookItem item)
    {
        var thumbnail = item.Thumbnail ?? DecodeImage(item.ThumbnailPngBase64, ThumbnailDecodePixelWidth);
        if (thumbnail is null
            && YouTubeUrlService.IsVideoUrl(item.Uri ?? string.Empty)
            && YouTubeThumbnailImageService.Shared.TryGetCached(item.Uri, out var cachedThumbnail))
        {
            thumbnail = cachedThumbnail;
        }

        return CreateResult(item, thumbnail, CreateFavicon(item));
    }

    internal static bool TryCreateImmediateResult(
        QuickLookRequest request,
        out QuickLookRenderResult? result)
    {
        if (request.Items.Count == 1
            && request.PrimaryItem is { } item
            && TryGetWebUri(item, out _))
        {
            result = CreateCachedResult(item);
            return true;
        }

        result = null;
        return false;
    }

    internal static bool NeedsAsyncRefresh(
        QuickLookRequest request,
        QuickLookRenderResult immediateResult)
    {
        return immediateResult.PreviewImage is null
            && YouTubeUrlService.IsVideoUrl(request.PrimaryItem?.Uri ?? string.Empty);
    }

    private static QuickLookRenderResult CreateResult(
        QuickLookItem item,
        ImageSource? thumbnail,
        ImageSource? favicon)
    {
        _ = TryGetWebUri(item, out var uri);
        var domain = uri?.Host ?? string.Empty;
        var url = item.Uri?.Trim() ?? string.Empty;
        var title = string.IsNullOrWhiteSpace(item.DisplayName)
            ? domain
            : item.DisplayName.Trim();
        return new QuickLookRenderResult(
            QuickLookRenderKind.WebSummary,
            title,
            domain,
            url,
            PreviewImage: thumbnail,
            Icon: favicon,
            Detail: NormalizeDetail(item.Description),
            TypeLabel: GetTypeLabel(url));
    }

    private static bool TryGetWebUri(QuickLookItem? item, out Uri? uri)
    {
        uri = null;
        return item?.Uri is { } address
            && Uri.TryCreate(address, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https";
    }

    private static ImageSource? CreateFavicon(QuickLookItem item)
    {
        return item.Favicon ?? DecodeImage(item.FaviconPngBase64, FaviconDecodePixelWidth);
    }

    private static ImageSource? DecodeImage(string? pngBase64, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(pngBase64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(pngBase64);
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is FormatException
                                          or ArgumentException
                                          or IOException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeDetail(string? description)
    {
        return string.IsNullOrWhiteSpace(description)
            ? null
            : description.Trim();
    }

    private static string GetTypeLabel(string url)
    {
        if (!YouTubeUrlService.IsYouTubeUrl(url))
        {
            return "Webサイト";
        }

        if (YouTubeUrlService.IsShortsUrl(url))
        {
            return "YouTube Shorts";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube ライブ";
        }

        return YouTubeUrlService.IsVideoUrl(url)
            ? "YouTube 動画"
            : "YouTube";
    }
}

internal sealed class QuickLookImageRenderer : IQuickLookRenderer
{
    private const int MaximumCacheEntries = 24;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, ImageCacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> CacheLru = [];
    private static long _cacheBytes;
    private readonly QuickLookSettings _settings;

    public QuickLookImageRenderer(QuickLookSettings settings)
    {
        _settings = settings;
        TrimCache(settings.ImageCacheLimitBytes);
    }

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && request.PrimaryItem?.FilePath is { } path
            && ImageExtensions.Contains(Path.GetExtension(path));
    }

    public async Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        var item = request.PrimaryItem!;
        var path = item.FilePath!;
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return QuickLookInformationRenderer.CreateMissingResult(item);
        }

        var cacheKey = $"{path}|{fileInfo.LastWriteTimeUtc.Ticks}|{fileInfo.Length}|{_settings.ImageDecodePixelWidth}";
        if (!TryGetCached(cacheKey, out var image))
        {
            image = await Task.Run(
                () => LoadImage(path, _settings.ImageDecodePixelWidth, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AddCached(cacheKey, image, _settings.ImageCacheLimitBytes);
        }

        return new QuickLookRenderResult(
            QuickLookRenderKind.Image,
            item.DisplayName,
            $"{image.PixelWidth:N0} × {image.PixelHeight:N0}  ·  {QuickLookMultipleSelectionRenderer.FormatSize(fileInfo.Length)}",
            path,
            PreviewImage: image);
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static BitmapSource LoadImage(string path, int maximumDecodePixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dimensions = ReadImageDimensions(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (dimensions.Width >= dimensions.Height)
        {
            image.DecodePixelWidth = Math.Min(dimensions.Width, maximumDecodePixels);
        }
        else
        {
            image.DecodePixelHeight = Math.Min(dimensions.Height, maximumDecodePixels);
        }

        image.StreamSource = stream;
        image.EndInit();
        cancellationToken.ThrowIfCancellationRequested();
        image.Freeze();
        return image;
    }

    private static (int Width, int Height) ReadImageDimensions(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4 * 1024,
            FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static bool TryGetCached(string key, out BitmapSource image)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var entry))
            {
                image = null!;
                return false;
            }

            CacheLru.Remove(entry.Node);
            CacheLru.AddFirst(entry.Node);
            image = entry.Image;
            return true;
        }
    }

    private static void AddCached(string key, BitmapSource image, long byteLimit)
    {
        var estimatedBytes = Math.Max(1L, (long)image.PixelWidth * image.PixelHeight * 4);
        lock (CacheGate)
        {
            if (Cache.Remove(key, out var existing))
            {
                CacheLru.Remove(existing.Node);
                _cacheBytes -= existing.EstimatedBytes;
            }

            var node = CacheLru.AddFirst(key);
            Cache[key] = new ImageCacheEntry(image, estimatedBytes, node);
            _cacheBytes += estimatedBytes;
            while ((Cache.Count > MaximumCacheEntries || _cacheBytes > byteLimit)
                   && CacheLru.Last is { } oldest)
            {
                if (Cache.Remove(oldest.Value, out var removed))
                {
                    _cacheBytes -= removed.EstimatedBytes;
                }

                CacheLru.RemoveLast();
            }
        }
    }

    private static void TrimCache(long byteLimit)
    {
        lock (CacheGate)
        {
            while ((Cache.Count > MaximumCacheEntries || _cacheBytes > byteLimit)
                   && CacheLru.Last is { } oldest)
            {
                if (Cache.Remove(oldest.Value, out var removed))
                {
                    _cacheBytes -= removed.EstimatedBytes;
                }

                CacheLru.RemoveLast();
            }
        }
    }

    private sealed record ImageCacheEntry(
        BitmapSource Image,
        long EstimatedBytes,
        LinkedListNode<string> Node);
}

internal sealed class QuickLookTextRenderer(QuickLookSettings settings) : IQuickLookRenderer
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".log", ".csv", ".cs", ".xaml", ".config",
        ".ini", ".yaml", ".yml", ".html", ".htm", ".css", ".js", ".ts", ".sql", ".ps1"
    };

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && request.PrimaryItem is { } item
            && (item.InlineText is not null
                || item.Kind == QuickLookItemKind.Text
                || item.FilePath is { } path && TextExtensions.Contains(Path.GetExtension(path)));
    }

    public async Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        var item = request.PrimaryItem!;
        if (item.InlineText is { } inlineText)
        {
            var maximumCharacters = (int)Math.Min(settings.TextMaxBytes, int.MaxValue);
            var inlineTruncated = inlineText.Length > maximumCharacters;
            var inlinePreview = inlineTruncated ? inlineText[..maximumCharacters] : inlineText;
            return CreateResult(item, inlinePreview, inlineTruncated, "Workspaceテキスト");
        }

        var path = item.FilePath!;
        if (!File.Exists(path))
        {
            return QuickLookInformationRenderer.CreateMissingResult(item);
        }

        var (bytes, truncated) = await ReadPrefixAsync(path, settings.TextMaxBytes, cancellationToken);
        var text = DecodeText(bytes);
        return CreateResult(item, text, truncated, path);
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static QuickLookRenderResult CreateResult(
        QuickLookItem item,
        string text,
        bool truncated,
        string subtitle)
    {
        return new QuickLookRenderResult(
            QuickLookRenderKind.Text,
            item.DisplayName,
            subtitle,
            text,
            IsTruncated: truncated);
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadPrefixAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var bufferLength = checked((int)Math.Min(maximumBytes + 1, int.MaxValue));
        var buffer = new byte[bufferLength];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var truncated = totalRead > maximumBytes;
        var resultLength = truncated ? checked((int)maximumBytes) : totalRead;
        return (buffer.AsSpan(0, resultLength).ToArray(), truncated);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}

internal sealed class QuickLookInformationRenderer : IQuickLookRenderer
{
    private readonly FolderTabIconService _iconService = new();

    public bool CanRender(QuickLookRequest request)
    {
        return true;
    }

    public async Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        var item = request.PrimaryItem;
        if (item is null)
        {
            return new QuickLookRenderResult(
                QuickLookRenderKind.Error,
                "プレビュー対象がありません",
                request.SourceId,
                "項目を選択してからもう一度実行してください。");
        }

        if (item.FilePath is not { } path)
        {
            return new QuickLookRenderResult(
                QuickLookRenderKind.Information,
                item.DisplayName,
                item.Uri ?? request.SourceId,
                item.Description ?? GetKindLabel(item.Kind));
        }

        var fileExists = File.Exists(path);
        var directoryExists = Directory.Exists(path);
        if (!fileExists && !directoryExists)
        {
            return CreateMissingResult(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var icon = await _iconService.GetIconAsync(path, cancellationToken);
        var lines = new List<string>
        {
            directoryExists ? "フォルダー" : GetFileTypeLabel(path),
            path
        };
        try
        {
            if (directoryExists)
            {
                var directory = new DirectoryInfo(path);
                lines.Add($"更新日時: {directory.LastWriteTime:yyyy/MM/dd HH:mm:ss}");
            }
            else
            {
                var file = new FileInfo(path);
                lines.Add($"サイズ: {QuickLookMultipleSelectionRenderer.FormatSize(file.Length)}");
                lines.Add($"更新日時: {file.LastWriteTime:yyyy/MM/dd HH:mm:ss}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add("詳細情報を取得できませんでした。");
        }

        return new QuickLookRenderResult(
            QuickLookRenderKind.Information,
            item.DisplayName,
            directoryExists ? "フォルダー" : GetFileTypeLabel(path),
            string.Join(Environment.NewLine, lines),
            Icon: icon);
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    internal static QuickLookRenderResult CreateMissingResult(QuickLookItem item)
    {
        return new QuickLookRenderResult(
            QuickLookRenderKind.Error,
            item.DisplayName,
            item.FilePath ?? string.Empty,
            "ファイルまたはフォルダーが見つかりません。");
    }

    private static string GetFileTypeLabel(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "PDF";
        }

        return string.IsNullOrWhiteSpace(extension)
            ? "ファイル"
            : $"{extension.TrimStart('.').ToUpperInvariant()} ファイル";
    }

    private static string GetKindLabel(QuickLookItemKind kind)
    {
        return kind switch
        {
            QuickLookItemKind.Directory => "フォルダー",
            QuickLookItemKind.Uri => "URL",
            QuickLookItemKind.Text => "テキスト",
            QuickLookItemKind.File => "ファイル",
            _ => "Workspace項目"
        };
    }
}
