using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabBrowser.Models;

namespace TabBrowser.Views;

internal sealed record FolderTabThumbnailEntry(
    string Name,
    bool IsDirectory,
    ImageSource? Icon);

// 取得済みのtab／folder情報を描画する。画像取得、保存、Runtimeの寿命は扱わない。
internal sealed class PreviewThumbnailRenderer(Func<double> getPixelsPerDip)
{
    public ImageSource CreateFallbackThumbnail(string titleText, string? urlText)
    {
        const int width = 320;
        const int height = 180;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(17, 20, 26)),
                null,
                new Rect(0, 0, width, height));
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.FromRgb(58, 64, 77)), 1),
                new Rect(0.5, 0.5, width - 1, height - 1));

            var title = CreatePreviewText(titleText, 14, Color.FromRgb(243, 244, 246), width - 32);
            context.DrawText(title, new Point(16, 54));

            if (!string.IsNullOrWhiteSpace(urlText))
            {
                var url = CreatePreviewText(urlText, 11, Color.FromRgb(156, 163, 175), width - 32);
                context.DrawText(url, new Point(16, 92));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private FormattedText CreatePreviewText(string text, double fontSize, Color color, double maxWidth)
    {
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            new SolidColorBrush(color),
            getPixelsPerDip())
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = fontSize * 2.8,
            Trimming = TextTrimming.CharacterEllipsis
        };

        return formattedText;
    }

    public ImageSource CreateFolderTabThumbnail(
        string title,
        string folderPath,
        IReadOnlyList<FolderTabThumbnailEntry> entries,
        int itemCount,
        bool isTruncated,
        string? statusText = null)
    {
        const int width = 320;
        const int height = 180;
        const double headerHeight = 42;
        const double tileWidth = 74;
        const double tileHeight = 58;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(12, 16, 22)),
                null,
                new Rect(0, 0, width, height));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(20, 26, 34)),
                null,
                new Rect(0, 0, width, headerHeight));
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.FromRgb(58, 64, 77)), 1),
                new Rect(0.5, 0.5, width - 1, height - 1));

            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(23, 70, 63)),
                new Pen(new SolidColorBrush(Color.FromRgb(47, 159, 143)), 1),
                new Rect(10, 9, 24, 20),
                3,
                3);
            context.DrawText(
                CreatePreviewText("F", 11, Color.FromRgb(201, 255, 245), 20),
                new Point(18, 11));
            context.DrawText(
                CreatePreviewText(title, 12, Color.FromRgb(243, 244, 246), 264),
                new Point(42, 6));
            context.DrawText(
                CreatePreviewText(folderPath, 8.5, Color.FromRgb(148, 163, 184), 264),
                new Point(42, 23));

            if (entries.Count == 0)
            {
                context.DrawText(
                    CreatePreviewText(
                        statusText ?? "フォルダーは空です",
                        11,
                        Color.FromRgb(148, 163, 184),
                        width - 32),
                    new Point(16, 86));
            }
            else
            {
                for (var index = 0; index < Math.Min(8, entries.Count); index++)
                {
                    var entry = entries[index];
                    var column = index % 4;
                    var row = index / 4;
                    var left = 8 + (column * (tileWidth + 4));
                    var top = headerHeight + 5 + (row * (tileHeight + 4));
                    context.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromRgb(17, 23, 31)),
                        new Pen(new SolidColorBrush(Color.FromRgb(42, 50, 62)), 1),
                        new Rect(left, top, tileWidth, tileHeight),
                        3,
                        3);

                    if (entry.Icon is not null)
                    {
                        context.DrawImage(entry.Icon, new Rect(left + 25, top + 5, 24, 24));
                    }
                    else
                    {
                        var iconColor = entry.IsDirectory
                            ? Color.FromRgb(209, 166, 72)
                            : Color.FromRgb(71, 104, 135);
                        context.DrawRoundedRectangle(
                            new SolidColorBrush(iconColor),
                            null,
                            new Rect(left + 27, top + 7, 20, 17),
                            2,
                            2);
                    }

                    context.DrawText(
                        CreatePreviewText(entry.Name, 8.5, Color.FromRgb(226, 232, 240), tileWidth - 8),
                        new Point(left + 4, top + 32));
                }
            }

            var countText = isTruncated ? $"{itemCount}件以上" : $"{itemCount}件";
            context.DrawText(
                CreatePreviewText(countText, 8.5, Color.FromRgb(125, 139, 158), 80),
                new Point(width - 58, height - 15));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public ImageSource CreateClosedWindowThumbnail(IReadOnlyList<SessionTabState> tabs)
    {
        const int width = 320;
        const int height = 180;
        const double headerHeight = 38;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(12, 16, 22)),
                null,
                new Rect(0, 0, width, height));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(24, 31, 43)),
                null,
                new Rect(0, 0, width, headerHeight));
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.FromRgb(58, 64, 77)), 1),
                new Rect(0.5, 0.5, width - 1, height - 1));
            context.DrawText(
                CreatePreviewText("WINDOW", 10, Color.FromRgb(191, 219, 254), 80),
                new Point(12, 10));
            context.DrawText(
                CreatePreviewText($"{tabs.Count} タブ", 10, Color.FromRgb(148, 163, 184), 80),
                new Point(width - 58, 10));

            for (var index = 0; index < Math.Min(6, tabs.Count); index++)
            {
                var tab = tabs[index];
                var title = !string.IsNullOrWhiteSpace(tab.Title)
                    ? tab.Title
                    : !string.IsNullOrWhiteSpace(tab.Url)
                        ? TryGetHost(tab.Url) ?? tab.Url
                        : "無題のタブ";
                var top = headerHeight + 7 + (index * 22);
                context.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(18, 24, 33)),
                    new Pen(new SolidColorBrush(Color.FromRgb(43, 52, 66)), 1),
                    new Rect(10, top, width - 20, 18),
                    2,
                    2);
                context.DrawText(
                    CreatePreviewText($"{index + 1}. {title}", 9, Color.FromRgb(226, 232, 240), width - 36),
                    new Point(16, top + 2));
            }

            if (tabs.Count > 6)
            {
                context.DrawText(
                    CreatePreviewText($"ほか {tabs.Count - 6} タブ", 8.5, Color.FromRgb(125, 139, 158), 100),
                    new Point(width - 92, height - 16));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string? TryGetHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
}
