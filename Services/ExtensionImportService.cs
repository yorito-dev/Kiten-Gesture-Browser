using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabBrowser.Services;

internal static class ExtensionImportService
{
    internal const string VersionsFolderName = "KitenImportedExtensions";

    internal static (string Directory, string Id) CopyVersion(
        string source, string webViewDataFolder, string browserExtensionId)
    {
        if (!IsBrowserExtensionId(browserExtensionId))
            throw new IOException("拡張機能のIDを確認できません。");

        var sourceDirectory = Path.GetFullPath(source);
        var root = Path.Combine(Path.GetFullPath(webViewDataFolder), VersionsFolderName);
        var relative = Path.GetRelativePath(sourceDirectory, root);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("拡張機能のコピー元と管理先が重複しています。");

        var managedRoot = Directory.CreateDirectory(root);
        EnsureNotReparsePoint(managedRoot.FullName);
        var extensionRoot = Directory.CreateDirectory(Path.Combine(root, browserExtensionId));
        EnsureNotReparsePoint(extensionRoot.FullName);

        var versionId = Guid.NewGuid().ToString("N");
        var pending = Path.Combine(extensionRoot.FullName, "." + versionId + ".partial");
        var destination = Path.Combine(extensionRoot.FullName, versionId);
        if (File.Exists(pending) || Directory.Exists(pending))
            throw new IOException("拡張機能の作業フォルダーが既にあります。");

        Directory.CreateDirectory(pending);
        try
        {
            CopyDirectory(sourceDirectory, pending);
            var id = ReadManifestId(Path.Combine(pending, "manifest.json")) ?? GetPathId(destination);
            Directory.Move(pending, destination);
            return (destination, id);
        }
        catch
        {
            TryDiscardVersion(webViewDataFolder, browserExtensionId, pending);
            throw;
        }
    }

    internal static async Task<T?> RegisterCopiedVersionAsync<T>(
        string source, string webViewDataFolder, string browserExtensionId,
        Func<string, string, Task<T?>> register) where T : class
    {
        var staged = await Task.Run(() => CopyVersion(source, webViewDataFolder, browserExtensionId));
        T? registered = null;
        try
        {
            registered = await register(staged.Directory, staged.Id);
            return registered;
        }
        finally
        {
            if (registered is null)
                await Task.Run(() => TryDiscardVersion(webViewDataFolder, browserExtensionId, staged.Directory));
        }
    }

    internal static bool TryDiscardVersion(string webViewDataFolder, string browserExtensionId, string versionPath)
    {
        try
        {
            if (!IsBrowserExtensionId(browserExtensionId))
                return false;

            var root = Path.Combine(Path.GetFullPath(webViewDataFolder), VersionsFolderName, browserExtensionId);
            var version = new DirectoryInfo(versionPath);
            if (!string.Equals(version.Parent?.FullName, root, StringComparison.OrdinalIgnoreCase))
                return false;

            var name = version.Name;
            if (name.StartsWith('.') && name.EndsWith(".partial", StringComparison.Ordinal))
                name = name[1..^".partial".Length];
            if (!Guid.TryParseExact(name, "N", out _))
                return false;

            for (DirectoryInfo? directory = version; directory is not null; directory = directory.Parent)
                EnsureNotReparsePoint(directory.FullName);

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(version.FullName);
            while (pendingDirectories.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if ((EnsureNotReparsePoint(entry) & FileAttributes.Directory) != 0)
                        pendingDirectories.Push(entry);
                }
            }

            Directory.Delete(version.FullName, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsBrowserExtensionId(string value)
        => value.Length == 32 && value.All(static character => character is >= 'a' and <= 'p');

    internal static string GetDirectoryId(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        EnsureNotReparsePoint(directory);
        EnsureNotReparsePoint(manifestPath);
        return ReadManifestId(manifestPath) ?? GetPathId(directory);
    }

    private static string? ReadManifestId(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        using var document = JsonDocument.Parse(stream);
        return GetManifestId(document.RootElement);
    }

    internal static string? GetManifestId(JsonElement root)
    {
        if (!root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(key.GetString()))
            return null;

        var keyText = key.GetString()!;
        var keyData = PemEncoding.TryFind(keyText, out var pem)
            ? keyText.AsSpan()[pem.Base64Data].ToString()
            : keyText;
        return GetIdFromBytes(Convert.FromBase64String(keyData));
    }

    internal static string GetPathId(string directory)
    {
        // ChromiumはWindowsの展開済み拡張機能で、ドライブ文字だけを大文字にしたUTF-16のパスを使う。
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (fullPath.Length >= 2 && fullPath[1] == ':')
            fullPath = char.ToUpperInvariant(fullPath[0]) + fullPath[1..];
        return GetIdFromBytes(Encoding.Unicode.GetBytes(fullPath));
    }

    private static string GetIdFromBytes(byte[] source)
    {
        var hash = SHA256.HashData(source);
        return string.Create(32, hash, static (id, bytes) =>
        {
            for (var index = 0; index < 16; index++)
            {
                id[index * 2] = (char)('a' + (bytes[index] >> 4));
                id[index * 2 + 1] = (char)('a' + (bytes[index] & 15));
            }
        });
    }

    private static void CopyDirectory(string source, string destination)
    {
        EnsureNotReparsePoint(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = EnsureNotReparsePoint(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
                CopyDirectory(entry, target);
            else
                File.Copy(entry, target, overwrite: false);
        }
    }

    private static FileAttributes EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンクやジャンクションを含む拡張機能フォルダーはコピーできません。");
        return attributes;
    }
}
