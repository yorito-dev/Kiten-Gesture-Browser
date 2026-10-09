using System.IO;

namespace TabBrowser.Services;

internal static class AtomicFileService
{
    // callbackは書込みのみを担当し、streamのFlush・終了・置換はこの共通処理が所有する。
    public static void EnsureDirectory(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? AppPaths.Root);
    }

    public static string CreateBackupFilePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? AppPaths.Root;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(fileNameWithoutExtension))
        {
            return filePath + ".backup";
        }

        return Path.Combine(directory, $"{fileNameWithoutExtension}.backup{extension}");
    }

    public static async Task WriteAsync(
        string filePath,
        string? backupFilePath,
        Func<Stream, Task> writeAsync,
        bool overwrite = true)
    {
        EnsureDirectory(filePath);
        var temporaryPath = CreateTemporaryFilePath(filePath);
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await writeAsync(stream);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite) ReplaceFile(temporaryPath, filePath, backupFilePath);
            else File.Move(temporaryPath, filePath);
        }
        finally
        {
            DeleteFileIfExists(temporaryPath);
        }
    }

    public static void Write(
        string filePath,
        string? backupFilePath,
        Action<Stream> write)
    {
        EnsureDirectory(filePath);
        var temporaryPath = CreateTemporaryFilePath(filePath);
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            ReplaceFile(temporaryPath, filePath, backupFilePath);
        }
        finally
        {
            DeleteFileIfExists(temporaryPath);
        }
    }

    private static string CreateTemporaryFilePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? AppPaths.Root;
        var fileName = Path.GetFileName(filePath);
        return Path.Combine(directory, $"{fileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
    }

    private static void ReplaceFile(string temporaryPath, string filePath, string? backupFilePath)
    {
        if (File.Exists(filePath))
        {
            if (!string.IsNullOrWhiteSpace(backupFilePath))
            {
                EnsureDirectory(backupFilePath);
                File.Replace(temporaryPath, filePath, backupFilePath);
                return;
            }

            File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
            return;
        }

        File.Move(temporaryPath, filePath);
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
