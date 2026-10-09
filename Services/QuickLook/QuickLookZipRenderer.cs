using System.IO;
using System.IO.Compression;
using System.Text;
using TabBrowser.Models.QuickLook;

namespace TabBrowser.Services.QuickLook;

internal sealed class QuickLookZipRenderer : IQuickLookRenderer
{
    internal const int MaximumEntryCount = 5000;
    private const int MaximumPathSegmentCount = 32;
    private const int MaximumSegmentLength = 120;

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && request.PrimaryItem?.FilePath is { } path
            && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        var item = request.PrimaryItem!;
        if (item.FilePath is not { } path || !File.Exists(path))
        {
            return QuickLookInformationRenderer.CreateMissingResult(item);
        }

        try
        {
            return await Task.Run(
                () => RenderCore(item, path, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return CreateError(item, path, "ZIPが破損しているか、対応していない形式です。");
        }
        catch (UnauthorizedAccessException)
        {
            return CreateError(item, path, "ZIPを読み取る権限がありません。");
        }
        catch (IOException ex)
        {
            return CreateError(item, path, "ZIPを読み取れませんでした: " + ex.Message);
        }
    }

    public ValueTask UnloadAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static QuickLookRenderResult RenderCore(
        QuickLookItem item,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var totalCount = archive.Entries.Count;
        var displayCount = Math.Min(totalCount, MaximumEntryCount);
        var entries = archive.Entries
            .Take(displayCount)
            .Select(entry => new ZipEntryDisplay(
                SanitizeEntryPath(entry.FullName),
                entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'),
                entry.Length,
                entry.CompressedLength))
            .OrderBy(entry => entry.SafePath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var body = new StringBuilder();
        var emittedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendEntry(body, emittedDirectories, entry);
        }

        var remainingCount = Math.Max(0, totalCount - displayCount);
        if (remainingCount > 0)
        {
            body.AppendLine($"…残り {remainingCount:N0} 件（表示上限 {MaximumEntryCount:N0} 件）");
        }

        return new QuickLookRenderResult(
            QuickLookRenderKind.Text,
            item.DisplayName,
            $"ZIPアーカイブ · {totalCount:N0} 件",
            body.Length == 0 ? "空のZIPアーカイブです" : body.ToString().TrimEnd(),
            IsTruncated: remainingCount > 0,
            Detail: remainingCount > 0 ? $"残り {remainingCount:N0} 件は表示していません" : null,
            TypeLabel: "ZIPアーカイブ");
    }

    private static void AppendEntry(
        StringBuilder body,
        HashSet<string> emittedDirectories,
        ZipEntryDisplay entry)
    {
        var segments = entry.SafePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var directoryCount = entry.IsDirectory ? segments.Length : segments.Length - 1;
        var directoryPath = string.Empty;
        for (var index = 0; index < directoryCount; index++)
        {
            directoryPath = directoryPath.Length == 0
                ? segments[index]
                : directoryPath + "/" + segments[index];
            if (emittedDirectories.Add(directoryPath))
            {
                body.Append(' ', index * 2);
                body.Append("📁 ");
                body.AppendLine(segments[index] + "/");
            }
        }

        if (entry.IsDirectory)
        {
            return;
        }

        body.Append(' ', Math.Max(0, segments.Length - 1) * 2);
        body.Append("📄 ");
        body.Append(segments[^1]);
        body.Append("  ");
        body.Append(QuickLookMultipleSelectionRenderer.FormatSize(entry.Length));
        body.Append(" / 圧縮 ");
        body.AppendLine(QuickLookMultipleSelectionRenderer.FormatSize(entry.CompressedLength));
    }

    internal static string SanitizeEntryPath(string? entryPath)
    {
        var raw = (entryPath ?? string.Empty).Replace('\\', '/');
        var isAbsolute = raw.StartsWith("/", StringComparison.Ordinal)
            || raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':';
        var safeSegments = new List<string>();
        foreach (var rawSegment in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (safeSegments.Count >= MaximumPathSegmentCount)
            {
                safeSegments.Add("［深い階層を省略］");
                break;
            }

            var segment = rawSegment == ".."
                ? "［上位参照］"
                : rawSegment == "." ? "［現在位置］" : RemoveControlCharacters(rawSegment);
            if (segment.Length > MaximumSegmentLength)
            {
                segment = segment[..MaximumSegmentLength] + "…";
            }

            safeSegments.Add(string.IsNullOrWhiteSpace(segment) ? "［空の名前］" : segment);
        }

        var safePath = string.Join('/', safeSegments);
        return isAbsolute ? "［絶対パス］/" + safePath : safePath;
    }

    private static string RemoveControlCharacters(string value)
    {
        return new string(value.Select(character => char.IsControl(character) ? '�' : character).ToArray());
    }

    private static QuickLookRenderResult CreateError(QuickLookItem item, string path, string message)
    {
        return new QuickLookRenderResult(
            QuickLookRenderKind.Error,
            item.DisplayName,
            path,
            message,
            TypeLabel: "ZIPアーカイブ");
    }

    private sealed record ZipEntryDisplay(
        string SafePath,
        bool IsDirectory,
        long Length,
        long CompressedLength);
}
