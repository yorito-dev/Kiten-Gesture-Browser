using System.IO;

namespace TabBrowser.Services;

public sealed class DownloadPathResolver
{
    public string GetDefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "Downloads");
        if (!string.IsNullOrWhiteSpace(downloads))
        {
            return downloads;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? AppPaths.Root
            : documents;
    }

    public string GetSuggestedFileName(string? resultFilePath, string? uri)
    {
        var fromResultPath = Path.GetFileName(resultFilePath);
        if (!string.IsNullOrWhiteSpace(fromResultPath))
        {
            return SanitizeFileName(fromResultPath);
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            var fromUrl = Path.GetFileName(Uri.UnescapeDataString(parsedUri.LocalPath));
            if (!string.IsNullOrWhiteSpace(fromUrl))
            {
                return SanitizeFileName(fromUrl);
            }
        }

        return "download.bin";
    }

    public string ResolveInitialDirectory(string? resultFilePath)
    {
        var directory = Path.GetDirectoryName(resultFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        return GetDefaultDownloadDirectory();
    }

    public string? ResolveExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : null;
    }

    public string ResolveUniquePath(string selectedPath)
    {
        var directory = Path.GetDirectoryName(selectedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = GetDefaultDownloadDirectory();
        }

        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(Path.GetFileName(selectedPath));
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10000; index++)
        {
            candidate = Path.Combine(directory, $"{nameWithoutExtension} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{nameWithoutExtension} ({Guid.NewGuid():N}){extension}");
    }

    public string SanitizeFileName(string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(fileName)
            ? "download.bin"
            : fileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        normalized = normalized.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(normalized)
            ? "download.bin"
            : normalized;
    }
}
