using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services.Workspaces;

public sealed record WebThumbnailCropHint(
    double X,
    double Y,
    double Width,
    double Height,
    double ViewportWidth,
    double ViewportHeight);

public sealed record WebThumbnailCaptureResult(
    ImageSource? Thumbnail,
    string PageDescription);

public static class WebThumbnailCaptureService
{
    public const int ThumbnailWidth = 320;
    public const int ThumbnailHeight = 180;
    public const int ContentHeight = 148;
    public const int MinimumSettleMilliseconds = 1000;
    public const int MaximumSettleMilliseconds = 3500;
    public const int SettlePollMilliseconds = 250;

    private const int MaximumSourceDecodeWidth = 1280;
    private const int RequiredStableMatches = 3;
    private const double TargetContentAspectRatio = (double)ThumbnailWidth / ContentHeight;

    private static readonly Color InformationBandColor = Color.FromRgb(17, 22, 29);
    private static readonly Color InformationBandAccentColor = Color.FromRgb(47, 159, 143);

    private const string ContentBoundsScript = """
        (() => {
          const viewportWidth = Math.max(1, window.innerWidth || 0);
          const viewportHeight = Math.max(1, window.innerHeight || 0);
          const pageDescription = Array.from(document.querySelectorAll(
            'meta[property="og:description"], meta[name="description"], meta[name="twitter:description"]'))
            .map(meta => (meta.getAttribute('content') || '').replace(/\s+/g, ' ').trim())
            .find(value => value.length > 0)
            ?.slice(0, 600) || '';
          const collect = selector => Array.from(document.querySelectorAll(selector))
            .slice(0, 240)
            .map(element => {
              const style = window.getComputedStyle(element);
              if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) {
                return null;
              }

              const rect = element.getBoundingClientRect();
              const left = Math.max(0, rect.left);
              const top = Math.max(0, rect.top);
              const right = Math.min(viewportWidth, rect.right);
              const bottom = Math.min(viewportHeight, rect.bottom);
              const width = right - left;
              const height = bottom - top;
              if (width < Math.min(220, viewportWidth * 0.25) || height < 90) {
                return null;
              }

              const centerX = left + (width / 2);
              const centerY = top + (height / 2);
              const centerDistance = Math.hypot(
                (centerX - (viewportWidth / 2)) / viewportWidth,
                (centerY - (viewportHeight / 2)) / viewportHeight);
              return {
                x: left,
                y: top,
                width,
                height,
                score: (width * height) * (1.25 - Math.min(0.75, centerDistance))
              };
            })
            .filter(Boolean);

          const articles = collect('article');
          const candidates = articles.length > 0
            ? articles
            : collect('main, [role="main"]');
          if (candidates.length === 0) {
            return {
              x: 0,
              y: 0,
              width: 0,
              height: 0,
              viewportWidth,
              viewportHeight,
              pageDescription
            };
          }

          candidates.sort((left, right) => right.score - left.score);
          const best = candidates[0];
          return {
            x: best.x,
            y: best.y,
            width: best.width,
            height: best.height,
            viewportWidth,
            viewportHeight,
            pageDescription
          };
        })()
        """;

    private const string LayoutStateScript = """
        (() => {
          const viewportWidth = Math.max(1, window.innerWidth || 0);
          const viewportHeight = Math.max(1, window.innerHeight || 0);
          const visibleElements = Array.from(
            document.querySelectorAll('article, main, [role="main"]'))
            .slice(0, 240)
            .map(element => {
              const rect = element.getBoundingClientRect();
              const width = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
              const height = Math.max(0, Math.min(viewportHeight, rect.bottom) - Math.max(0, rect.top));
              return {
                x: Math.max(0, rect.left),
                y: Math.max(0, rect.top),
                width,
                height,
                area: width * height
              };
            })
            .filter(item => item.width >= 80 && item.height >= 60)
            .sort((left, right) => right.area - left.area);
          const primary = visibleElements[0] || { x: 0, y: 0, width: 0, height: 0 };
          const pendingVisibleImages = Array.from(document.images)
            .slice(0, 240)
            .filter(image => {
              if (image.complete) {
                return false;
              }

              const rect = image.getBoundingClientRect();
              return rect.right > 0
                && rect.bottom > 0
                && rect.left < viewportWidth
                && rect.top < viewportHeight;
            })
            .length;
          const body = document.body;
          const documentElement = document.documentElement;
          return [
            document.readyState,
            Math.round(body?.scrollWidth || documentElement?.scrollWidth || 0),
            Math.round(body?.scrollHeight || documentElement?.scrollHeight || 0),
            Math.round(primary.x),
            Math.round(primary.y),
            Math.round(primary.width),
            Math.round(primary.height),
            visibleElements.length,
            pendingVisibleImages,
            document.fonts?.status || 'unsupported',
            performance.getEntriesByType('resource').length
          ].join('|');
        })()
        """;

    public static async Task<ImageSource?> CaptureAsync(
        CoreWebView2 core,
        string? title,
        string? url,
        CancellationToken cancellationToken = default)
    {
        var result = await CaptureWithSummaryAsync(core, title, url, cancellationToken);
        return result.Thumbnail;
    }

    public static async Task<WebThumbnailCaptureResult> CaptureWithSummaryAsync(
        CoreWebView2 core,
        string? title,
        string? url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);

        await WaitForVisualStabilityAsync(core, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var captureData = await TryGetPageCaptureDataAsync(core);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length == 0)
        {
            return new WebThumbnailCaptureResult(
                Thumbnail: null,
                captureData?.PageDescription ?? string.Empty);
        }

        stream.Position = 0;
        return new WebThumbnailCaptureResult(
            CreateThumbnail(stream, captureData?.CropHint, title, url),
            captureData?.PageDescription ?? string.Empty);
    }

    public static async Task WaitForVisualStabilityAsync(
        CoreWebView2 core,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);

        var stopwatch = Stopwatch.StartNew();
        string? previousState = null;
        var stableMatches = 0;
        var failedSamples = 0;
        while (stopwatch.ElapsedMilliseconds < MaximumSettleMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentState = await TryGetLayoutStateAsync(core);
            if (currentState is null)
            {
                failedSamples++;
                stableMatches = 0;
                if (failedSamples >= 2
                    && stopwatch.ElapsedMilliseconds >= MinimumSettleMilliseconds)
                {
                    return;
                }
            }
            else
            {
                failedSamples = 0;
                if (string.Equals(previousState, currentState, StringComparison.Ordinal))
                {
                    stableMatches++;
                }
                else
                {
                    previousState = currentState;
                    stableMatches = 0;
                }

                if (stableMatches >= RequiredStableMatches
                    && stopwatch.ElapsedMilliseconds >= MinimumSettleMilliseconds)
                {
                    return;
                }
            }

            await Task.Delay(SettlePollMilliseconds, cancellationToken);
        }
    }

    public static ImageSource? CreateThumbnail(
        Stream sourceStream,
        WebThumbnailCropHint? cropHint,
        string? title,
        string? url)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);

        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.DecodePixelWidth = MaximumSourceDecodeWidth;
        source.StreamSource = sourceStream;
        source.EndInit();
        source.Freeze();
        if (CapturedTabSnapshot.IsMostlyBlackThumbnail(source))
        {
            return null;
        }

        var crop = cropHint is not null
            ? CalculateCropRect(source.PixelWidth, source.PixelHeight, cropHint)
            : FindInformationDenseCrop(source);
        var cropped = new CroppedBitmap(source, crop);
        cropped.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(11, 13, 17)),
                null,
                new Rect(0, 0, ThumbnailWidth, ThumbnailHeight));
            context.DrawImage(cropped, new Rect(0, 0, ThumbnailWidth, ContentHeight));
            context.DrawRectangle(
                new SolidColorBrush(InformationBandAccentColor),
                null,
                new Rect(0, ContentHeight, ThumbnailWidth, 2));
            context.DrawRectangle(
                new SolidColorBrush(InformationBandColor),
                null,
                new Rect(0, ContentHeight + 2, ThumbnailWidth, ThumbnailHeight - ContentHeight - 2));

            var host = GetDisplayHost(url);
            var displayTitle = string.IsNullOrWhiteSpace(title)
                ? host
                : title.Trim();
            context.DrawText(
                CreateText(
                    displayTitle,
                    10,
                    Color.FromRgb(243, 244, 246),
                    ThumbnailWidth - 20),
                new Point(10, ContentHeight + 3));
            context.DrawText(
                CreateText(
                    host,
                    8,
                    Color.FromRgb(148, 163, 184),
                    ThumbnailWidth - 20),
                new Point(10, ContentHeight + 17));
        }

        var thumbnail = new RenderTargetBitmap(
            ThumbnailWidth,
            ThumbnailHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        thumbnail.Render(visual);
        thumbnail.Freeze();
        return thumbnail;
    }

    public static Int32Rect CalculateCropRect(
        int pixelWidth,
        int pixelHeight,
        WebThumbnailCropHint cropHint)
    {
        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        ArgumentNullException.ThrowIfNull(cropHint);
        if (cropHint.ViewportWidth <= 0
            || cropHint.ViewportHeight <= 0
            || cropHint.Width <= 0
            || cropHint.Height <= 0)
        {
            return CreateCenteredCrop(pixelWidth, pixelHeight);
        }

        var scaleX = pixelWidth / cropHint.ViewportWidth;
        var scaleY = pixelHeight / cropHint.ViewportHeight;
        var marginX = Math.Max(4, 12 * scaleX);
        var marginY = Math.Max(4, 12 * scaleY);
        var left = Math.Max(0, (cropHint.X * scaleX) - marginX);
        var top = Math.Max(0, (cropHint.Y * scaleY) - marginY);
        var right = Math.Min(pixelWidth, ((cropHint.X + cropHint.Width) * scaleX) + marginX);
        var bottom = Math.Min(pixelHeight, ((cropHint.Y + cropHint.Height) * scaleY) + marginY);
        var width = right - left;
        var height = bottom - top;
        if (width < pixelWidth * 0.2 || height < pixelHeight * 0.15)
        {
            return CreateCenteredCrop(pixelWidth, pixelHeight);
        }

        var candidateAspect = width / height;
        if (candidateAspect > TargetContentAspectRatio)
        {
            var targetWidth = height * TargetContentAspectRatio;
            left += (width - targetWidth) / 2;
            width = targetWidth;
        }
        else
        {
            var targetHeight = width / TargetContentAspectRatio;
            top += (height - targetHeight) / 2;
            height = targetHeight;
        }

        return ClampCropRect(left, top, width, height, pixelWidth, pixelHeight);
    }

    public static bool HasInformationBand(ImageSource? thumbnail)
    {
        if (thumbnail is not BitmapSource bitmap
            || bitmap.PixelWidth < 16
            || bitmap.PixelHeight < 16)
        {
            return false;
        }

        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var footerY = Math.Min(
                converted.PixelHeight - 2,
                (int)Math.Round(converted.PixelHeight * 0.95));
            var accentY = Math.Min(
                converted.PixelHeight - 2,
                (int)Math.Round(
                    converted.PixelHeight * (double)ContentHeight / ThumbnailHeight));
            return IsColorNear(ReadPixel(converted, 2, footerY), InformationBandColor)
                && IsColorNear(
                    ReadPixel(converted, converted.PixelWidth - 3, footerY),
                    InformationBandColor)
                && IsColorNear(
                    ReadPixel(converted, converted.PixelWidth / 2, accentY),
                    InformationBandAccentColor);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasInformationBand(string? pngBase64)
    {
        if (string.IsNullOrWhiteSpace(pngBase64))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(pngBase64));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ThumbnailWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return HasInformationBand(image);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<WebPageCaptureData?> TryGetPageCaptureDataAsync(CoreWebView2 core)
    {
        try
        {
            var result = await core.ExecuteScriptAsync(ContentBoundsScript);
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var width = ReadNumber(root, "width");
            var height = ReadNumber(root, "height");
            var cropHint = width > 0 && height > 0
                ? new WebThumbnailCropHint(
                    ReadNumber(root, "x"),
                    ReadNumber(root, "y"),
                    width,
                    height,
                    ReadNumber(root, "viewportWidth"),
                    ReadNumber(root, "viewportHeight"))
                : null;
            var pageDescription = root.TryGetProperty("pageDescription", out var descriptionProperty)
                && descriptionProperty.ValueKind == JsonValueKind.String
                    ? NormalizePageDescription(descriptionProperty.GetString())
                    : string.Empty;
            return new WebPageCaptureData(cropHint, pageDescription);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetLayoutStateAsync(CoreWebView2 core)
    {
        try
        {
            var result = await core.ExecuteScriptAsync(LayoutStateScript);
            return JsonSerializer.Deserialize<string>(result);
        }
        catch
        {
            return null;
        }
    }

    private static double ReadNumber(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out var value)
            && double.IsFinite(value)
                ? value
                : 0;
    }

    internal static string NormalizePageDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 600 ? normalized : normalized[..600];
    }

    private sealed record WebPageCaptureData(
        WebThumbnailCropHint? CropHint,
        string PageDescription);

    private static Int32Rect FindInformationDenseCrop(BitmapSource source)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var bestCrop = CreateCenteredCrop(converted.PixelWidth, converted.PixelHeight);
            var bestScore = double.MinValue;
            foreach (var scale in new[] { 0.55, 0.7, 0.85, 1.0 })
            {
                var candidateWidth = Math.Max(1, (int)Math.Round(converted.PixelWidth * scale));
                var candidateHeight = Math.Max(
                    1,
                    (int)Math.Round(candidateWidth / TargetContentAspectRatio));
                if (candidateHeight > converted.PixelHeight)
                {
                    candidateHeight = converted.PixelHeight;
                    candidateWidth = Math.Min(
                        converted.PixelWidth,
                        (int)Math.Round(candidateHeight * TargetContentAspectRatio));
                }

                for (var yIndex = 0; yIndex <= 4; yIndex++)
                {
                    var y = (int)Math.Round(
                        (converted.PixelHeight - candidateHeight) * (yIndex / 4d));
                    for (var xIndex = 0; xIndex <= 4; xIndex++)
                    {
                        var x = (int)Math.Round(
                            (converted.PixelWidth - candidateWidth) * (xIndex / 4d));
                        var crop = new Int32Rect(x, y, candidateWidth, candidateHeight);
                        var score = CalculateInformationScore(
                            pixels,
                            stride,
                            crop,
                            converted.PixelWidth,
                            converted.PixelHeight,
                            scale);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCrop = crop;
                        }
                    }
                }
            }

            return bestCrop;
        }
        catch
        {
            return CreateCenteredCrop(source.PixelWidth, source.PixelHeight);
        }
    }

    private static double CalculateInformationScore(
        byte[] pixels,
        int stride,
        Int32Rect crop,
        int sourceWidth,
        int sourceHeight,
        double scale)
    {
        var stepX = Math.Max(1, crop.Width / 40);
        var stepY = Math.Max(1, crop.Height / 24);
        double luminanceTotal = 0;
        double luminanceSquaredTotal = 0;
        double edgeTotal = 0;
        double saturationTotal = 0;
        var sampleCount = 0;
        var edgeCount = 0;

        for (var y = crop.Y; y < crop.Y + crop.Height; y += stepY)
        {
            var previousLuminance = -1d;
            for (var x = crop.X; x < crop.X + crop.Width; x += stepX)
            {
                var offset = (y * stride) + (x * 4);
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
                luminanceTotal += luminance;
                luminanceSquaredTotal += luminance * luminance;
                saturationTotal += Math.Max(red, Math.Max(green, blue))
                    - Math.Min(red, Math.Min(green, blue));
                sampleCount++;
                if (previousLuminance >= 0)
                {
                    edgeTotal += Math.Abs(luminance - previousLuminance);
                    edgeCount++;
                }

                previousLuminance = luminance;
            }
        }

        if (sampleCount == 0)
        {
            return double.MinValue;
        }

        var average = luminanceTotal / sampleCount;
        var variance = Math.Max(0, (luminanceSquaredTotal / sampleCount) - (average * average));
        var edgeAverage = edgeCount == 0 ? 0 : edgeTotal / edgeCount;
        var saturationAverage = saturationTotal / sampleCount;
        var cropCenterX = crop.X + (crop.Width / 2d);
        var cropCenterY = crop.Y + (crop.Height / 2d);
        var centerDistance = Math.Sqrt(
            Math.Pow((cropCenterX - (sourceWidth / 2d)) / sourceWidth, 2)
            + Math.Pow((cropCenterY - (sourceHeight / 2d)) / sourceHeight, 2));
        var centerBonus = Math.Max(0, 1 - centerDistance) * 8;
        return (Math.Sqrt(variance) * 1.4)
            + (edgeAverage * 0.9)
            + (saturationAverage * 0.2)
            + centerBonus
            + (scale * 3);
    }

    private static Int32Rect CreateCenteredCrop(int pixelWidth, int pixelHeight)
    {
        var width = pixelWidth;
        var height = Math.Max(1, (int)Math.Round(width / TargetContentAspectRatio));
        if (height > pixelHeight)
        {
            height = pixelHeight;
            width = Math.Max(1, (int)Math.Round(height * TargetContentAspectRatio));
        }

        return new Int32Rect(
            Math.Max(0, (pixelWidth - width) / 2),
            Math.Max(0, (pixelHeight - height) / 2),
            Math.Min(width, pixelWidth),
            Math.Min(height, pixelHeight));
    }

    private static Int32Rect ClampCropRect(
        double left,
        double top,
        double width,
        double height,
        int pixelWidth,
        int pixelHeight)
    {
        var cropWidth = Math.Clamp((int)Math.Round(width), 1, pixelWidth);
        var cropHeight = Math.Clamp((int)Math.Round(height), 1, pixelHeight);
        var cropX = Math.Clamp((int)Math.Round(left), 0, pixelWidth - cropWidth);
        var cropY = Math.Clamp((int)Math.Round(top), 0, pixelHeight - cropHeight);
        return new Int32Rect(cropX, cropY, cropWidth, cropHeight);
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool IsColorNear(Color actual, Color expected)
    {
        const int tolerance = 6;
        return Math.Abs(actual.R - expected.R) <= tolerance
            && Math.Abs(actual.G - expected.G) <= tolerance
            && Math.Abs(actual.B - expected.B) <= tolerance;
    }

    private static string GetDisplayHost(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (!string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            if (uri.IsFile)
            {
                return "ローカルファイル";
            }
        }

        return string.IsNullOrWhiteSpace(url) ? "Webページ" : url.Trim();
    }

    private static FormattedText CreateText(
        string text,
        double fontSize,
        Color color,
        double maximumWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            new SolidColorBrush(color),
            1)
        {
            MaxTextWidth = Math.Max(1, maximumWidth),
            MaxTextHeight = fontSize * 1.25,
            Trimming = TextTrimming.CharacterEllipsis
        };
        return formatted;
    }
}
