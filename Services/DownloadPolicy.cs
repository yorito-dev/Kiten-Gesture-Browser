using System.IO;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class DownloadPolicy
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".msi",
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs",
        ".vbe",
        ".js",
        ".jse",
        ".wsf",
        ".scr",
        ".com",
        ".cpl",
        ".dll",
        ".reg",
        ".lnk",
        ".jar",
        ".hta",
        ".appx",
        ".msix",
        ".msixbundle"
    };

    private static readonly HashSet<string> SafeLookingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".txt",
        ".csv",
        ".doc",
        ".docx",
        ".xls",
        ".xlsx",
        ".ppt",
        ".pptx",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".mp4",
        ".webm",
        ".mov",
        ".zip"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".bmp",
        ".svg"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".gz"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".webm",
        ".mov",
        ".m4v"
    };

    public DownloadSafetyResult InspectFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var isDangerous = DangerousExtensions.Contains(extension);
        var hasBidiOverride = fileName.Any(IsBidirectionalControlCharacter);
        var hasExtensionSpoofing = hasBidiOverride || HasDoubleExtensionSpoof(fileName, extension);
        var warningText = string.Empty;
        if (isDangerous && hasExtensionSpoofing)
        {
            warningText = "危険な拡張子または拡張子偽装の可能性があります。信頼できる入手元か確認してください。";
        }
        else if (isDangerous)
        {
            warningText = "実行形式または危険な拡張子です。保存や開く前に信頼できる入手元か確認してください。";
        }
        else if (hasExtensionSpoofing)
        {
            warningText = "拡張子偽装の可能性があります。ファイル名と種類を確認してください。";
        }

        return new DownloadSafetyResult(isDangerous || hasExtensionSpoofing, isDangerous, hasExtensionSpoofing, warningText);
    }

    public DownloadFileKind GetKind(string fileName, string? mimeType)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadFileKind.Pdf;
        }

        if (ImageExtensions.Contains(extension) || StartsWithMimeType(mimeType, "image/"))
        {
            return DownloadFileKind.Image;
        }

        if (ArchiveExtensions.Contains(extension)
            || string.Equals(mimeType, "application/zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadFileKind.Archive;
        }

        if (DangerousExtensions.Contains(extension))
        {
            return DownloadFileKind.Executable;
        }

        if (VideoExtensions.Contains(extension) || StartsWithMimeType(mimeType, "video/"))
        {
            return DownloadFileKind.Video;
        }

        return DownloadFileKind.Other;
    }

    private static bool HasDoubleExtensionSpoof(string fileName, string finalExtension)
    {
        if (string.IsNullOrWhiteSpace(finalExtension))
        {
            return false;
        }

        var nameWithoutFinalExtension = Path.GetFileNameWithoutExtension(fileName);
        var previousExtension = Path.GetExtension(nameWithoutFinalExtension);
        return !string.IsNullOrWhiteSpace(previousExtension)
            && SafeLookingExtensions.Contains(previousExtension)
            && DangerousExtensions.Contains(finalExtension);
    }

    private static bool StartsWithMimeType(string? mimeType, string prefix)
    {
        return !string.IsNullOrWhiteSpace(mimeType)
            && mimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBidirectionalControlCharacter(char value)
    {
        return value is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069';
    }
}

public sealed record DownloadSafetyResult(
    bool ShouldWarn,
    bool IsDangerousExtension,
    bool HasExtensionSpoofing,
    string WarningText);
