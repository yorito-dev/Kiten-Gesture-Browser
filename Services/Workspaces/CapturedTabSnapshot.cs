using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabBrowser.Services.Workspaces;

public sealed class CapturedTabSnapshot
{
    public Guid TabId { get; init; }

    public Guid WindowId { get; init; }

    public string WindowGroupTitle { get; init; } = string.Empty;

    public int WindowSortOrder { get; init; }

    public bool IsForeignWindow { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public int TabIndex { get; init; }

    public bool IsActive { get; init; }

    public bool IsPinned { get; init; }

    public bool IsFolderTab { get; init; }

    public string? ContainerProfileId { get; init; }

    public string SplitGroupId { get; init; } = string.Empty;

    public string SplitPane { get; init; } = string.Empty;

    public string SplitOrientation { get; init; } = string.Empty;

    public double? SplitRatio { get; init; }

    public string SplitActivePane { get; init; } = string.Empty;

    public ImageSource? Favicon { get; init; }

    public ImageSource? Thumbnail { get; init; }

    public string FaviconPngBase64 { get; init; } = string.Empty;

    public bool HasFavicon => Favicon is not null || !string.IsNullOrWhiteSpace(FaviconPngBase64);

    public bool HasThumbnail => Thumbnail is not null;

    public bool IsInSplitView => !string.IsNullOrWhiteSpace(SplitGroupId);

    public string CreateThumbnailPngBase64()
    {
        return CreateThumbnailPngBase64(Thumbnail);
    }

    public static string CreateThumbnailPngBase64(ImageSource? thumbnail)
    {
        if (thumbnail is null)
        {
            return string.Empty;
        }

        try
        {
            const int width = 320;
            const int height = 180;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(thumbnail, new System.Windows.Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsMostlyBlackThumbnail(ImageSource? thumbnail)
    {
        if (thumbnail is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return false;
        }

        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var darkPixels = 0;
            var visiblePixels = 0;
            var minimumBlue = byte.MaxValue;
            var minimumGreen = byte.MaxValue;
            var minimumRed = byte.MaxValue;
            var maximumBlue = byte.MinValue;
            var maximumGreen = byte.MinValue;
            var maximumRed = byte.MinValue;
            long totalBlue = 0;
            long totalGreen = 0;
            long totalRed = 0;
            for (var index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] < 16)
                {
                    continue;
                }

                visiblePixels++;
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                minimumBlue = Math.Min(minimumBlue, blue);
                minimumGreen = Math.Min(minimumGreen, green);
                minimumRed = Math.Min(minimumRed, red);
                maximumBlue = Math.Max(maximumBlue, blue);
                maximumGreen = Math.Max(maximumGreen, green);
                maximumRed = Math.Max(maximumRed, red);
                totalBlue += blue;
                totalGreen += green;
                totalRed += red;
                if (pixels[index] <= 4 && pixels[index + 1] <= 4 && pixels[index + 2] <= 4)
                {
                    darkPixels++;
                }
            }

            if (visiblePixels == 0)
            {
                return false;
            }

            var isAlmostEntirelyBlack = darkPixels >= visiblePixels * 0.995;
            var isUniformDarkFrame = totalBlue / visiblePixels <= 32
                && totalGreen / visiblePixels <= 32
                && totalRed / visiblePixels <= 32
                && maximumBlue - minimumBlue <= 4
                && maximumGreen - minimumGreen <= 4
                && maximumRed - minimumRed <= 4;
            return isAlmostEntirelyBlack || isUniformDarkFrame;
        }
        catch
        {
            return false;
        }
    }
}
