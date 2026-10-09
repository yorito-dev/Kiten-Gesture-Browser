using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.Services.Workspaces;

public sealed class WorkspaceShellItemService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int MediaThumbnailWidth = 320;
    private const int MediaThumbnailHeight = 180;
    private const int TransferBufferSize = 1024 * 128;
    // コピーUndoはUI側の同期入口を維持するため、内容照合の読取り量に上限を設ける。
    private const long MaxCopyUndoContentBytes = 64L * 1024 * 1024;
    private const string TransferStagingPrefix = ".tabbrowser-transfer-";
    public const long MaximumTextDocumentImportBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan TransferProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, Lazy<string>> FileTypeIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif"
    };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpeg", ".mpg", ".ts", ".mts", ".m2ts"
    };

    private readonly WorkspaceShellItemTransferOperations _transferOperations;

    public WorkspaceShellItemService()
        : this(new WorkspaceShellItemTransferOperations(
            static (sourcePath, destinationPath) => string.Equals(
                Path.GetPathRoot(sourcePath),
                Path.GetPathRoot(destinationPath),
                StringComparison.OrdinalIgnoreCase),
            DeleteMovedSource,
            static _ => { },
            static _ => { },
            static (_, _) => { }))
    {
    }

    internal WorkspaceShellItemService(WorkspaceShellItemTransferOperations transferOperations)
    {
        _transferOperations = transferOperations ?? throw new ArgumentNullException(nameof(transferOperations));
    }

    public ShellItemKind Classify(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (Directory.Exists(normalizedPath))
        {
            return ShellItemKind.Directory;
        }

        return Path.GetExtension(normalizedPath).ToLowerInvariant() switch
        {
            ".lnk" or ".url" => ShellItemKind.Shortcut,
            ".exe" or ".com" or ".bat" or ".cmd" => ShellItemKind.Application,
            _ => ShellItemKind.File
        };
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("パスが空です。", nameof(path));
        }

        return Path.GetFullPath(path.Trim());
    }

    public static bool IsSameLocationDrop(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var destinationPath = Path.TrimEndingDirectorySeparator(NormalizePath(destinationDirectory));
        var normalizedSources = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.TrimEndingDirectorySeparator(NormalizePath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSources.Length == 0)
        {
            return false;
        }

        if (normalizedSources.Any(path => string.Equals(
            path,
            destinationPath,
            StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return normalizedSources.All(path => string.Equals(
            Path.GetDirectoryName(path),
            destinationPath,
            StringComparison.OrdinalIgnoreCase));
    }

    public static string GetDisplayName(string path)
    {
        var normalizedPath = NormalizePath(path);
        var name = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
    }

    public static bool Exists(ShellItem item)
    {
        return item.Kind == ShellItemKind.Directory
            ? Directory.Exists(item.TargetPath)
            : File.Exists(item.TargetPath);
    }

    public static bool IsImagePath(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsVideoPath(string path)
    {
        return VideoExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsMediaPath(string path)
    {
        return IsImagePath(path) || IsVideoPath(path);
    }

    public static Task<BitmapSource?> GetMediaThumbnailImageAsync(
        string path,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsMediaPath(path))
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        var normalizedWidth = Math.Clamp(pixelWidth, 48, 512);
        var normalizedHeight = Math.Clamp(pixelHeight, 48, 512);
        return Task.Run(
            () => GetMediaThumbnailImage(path, normalizedWidth, normalizedHeight, cancellationToken),
            cancellationToken);
    }

    public static ProcessStartInfo CreateOpenStartInfo(ShellItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ProcessStartInfo(NormalizePath(item.TargetPath))
        {
            UseShellExecute = true
        };
    }

    public static ProcessStartInfo CreateShowInFolderStartInfo(ShellItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = NormalizePath(item.TargetPath);
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };

        if (item.Kind == ShellItemKind.Directory && Directory.Exists(path))
        {
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(path);
        }

        return startInfo;
    }

    public static ProcessStartInfo CreateOpenRecycleBinStartInfo()
    {
        return new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
        {
            UseShellExecute = true
        };
    }

    public void Open(ShellItem item)
    {
        if (!Exists(item))
        {
            throw new FileNotFoundException("対象が見つかりません。", item.TargetPath);
        }

        Process.Start(CreateOpenStartInfo(item));
    }

    public void OpenWith(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException("プログラムから開く対象が見つかりません。", normalizedPath);
        }

        Process.Start(CreateOpenWithStartInfo(normalizedPath));
    }

    public static ProcessStartInfo CreateOpenWithStartInfo(string path)
    {
        var normalizedPath = NormalizePath(path);
        var startInfo = new ProcessStartInfo("rundll32.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
        startInfo.ArgumentList.Add(normalizedPath);
        return startInfo;
    }

    public void ShowInFolder(ShellItem item)
    {
        Process.Start(CreateShowInFolderStartInfo(item));
    }

    public void OpenRecycleBin()
    {
        Process.Start(CreateOpenRecycleBinStartInfo());
    }

    public Task<string> GetIconPngBase64Async(ShellItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetIconPngBase64(item, cancellationToken), cancellationToken);
    }

    public Task<string> GetMediaThumbnailPngBase64Async(
        ShellItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != ShellItemKind.File || !IsMediaPath(item.TargetPath))
        {
            return Task.FromResult(string.Empty);
        }

        return Task.Run(() => GetMediaThumbnailPngBase64(item, cancellationToken), cancellationToken);
    }

    public ShellFileOperationResult CreateDirectory(string parentDirectory, string name)
    {
        var parentPath = RequireExistingDirectory(parentDirectory);
        var destinationPath = Path.Combine(parentPath, ValidateFileName(name));
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("同じ名前の項目が既に存在します。");
        }

        Directory.CreateDirectory(destinationPath);
        return CreateUndoableResult(ShellFileOperationKind.CreateDirectory, string.Empty, destinationPath);
    }

    public ShellFileOperationResult Rename(ShellItem item, string newName)
    {
        RequireExisting(item);
        var sourcePath = NormalizePath(item.TargetPath);
        var parentPath = Path.GetDirectoryName(sourcePath)
            ?? throw new IOException("親フォルダーを取得できません。");
        var destinationPath = Path.Combine(parentPath, ValidateFileName(newName));
        EnsureDestinationAvailable(sourcePath, destinationPath);
        MoveCore(item.Kind, sourcePath, destinationPath);
        return CreateUndoableResult(ShellFileOperationKind.Rename, sourcePath, destinationPath);
    }

    public ShellFileOperationResult Copy(ShellItem item, string destinationDirectory)
    {
        RequireExisting(item);
        var sourcePath = NormalizePath(item.TargetPath);
        var destinationPath = Path.Combine(
            RequireExistingDirectory(destinationDirectory),
            GetDisplayName(sourcePath));
        EnsureDestinationAvailable(sourcePath, destinationPath);

        if (item.Kind == ShellItemKind.Directory)
        {
            FileSystem.CopyDirectory(sourcePath, destinationPath, overwrite: false);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        return CreateUndoableResult(ShellFileOperationKind.Copy, sourcePath, destinationPath);
    }

    public ShellFileOperationResult Move(ShellItem item, string destinationDirectory)
    {
        RequireExisting(item);
        var sourcePath = NormalizePath(item.TargetPath);
        var destinationPath = Path.Combine(
            RequireExistingDirectory(destinationDirectory),
            GetDisplayName(sourcePath));
        EnsureDestinationAvailable(sourcePath, destinationPath);
        MoveCore(item.Kind, sourcePath, destinationPath);
        return CreateUndoableResult(ShellFileOperationKind.Move, sourcePath, destinationPath);
    }

    public ShellFileOperationResult MoveToRecycleBin(ShellItem item)
    {
        RequireExisting(item);
        var sourcePath = NormalizePath(item.TargetPath);
        if (item.Kind == ShellItemKind.Directory)
        {
            FileSystem.DeleteDirectory(
                sourcePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        else
        {
            FileSystem.DeleteFile(
                sourcePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }

        return new ShellFileOperationResult(ShellFileOperationKind.Recycle, sourcePath, string.Empty, CanUndo: false);
    }

    public ShellFileTransferPlan CreateTransferPlan(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool move)
    {
        var preparation = CreateTransferPreparationCore(
            sourcePaths,
            destinationDirectory,
            move,
            CancellationToken.None);
        if (preparation.Conflicts.Count > 0)
        {
            throw new IOException("移動先に同じ名前の項目があります。");
        }

        return new ShellFileTransferPlan(
            preparation.DestinationDirectory,
            preparation.Move,
            preparation.Items);
    }

    public Task<ShellFileTransferPreparation> PrepareTransferAsync(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var paths = sourcePaths.ToArray();
        return Task.Run(
            () => CreateTransferPreparationCore(paths, destinationDirectory, move, cancellationToken),
            cancellationToken);
    }

    public ShellFileTransferResolution ResolveTransferPreparation(
        ShellFileTransferPreparation preparation,
        IReadOnlyDictionary<string, ShellFileConflictResolution> conflictResolutions)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(conflictResolutions);
        var conflictPaths = preparation.Conflicts
            .Select(conflict => conflict.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedItems = new List<ShellFileTransferPlanItem>(preparation.Items.Count);
        var skippedCount = 0;
        foreach (var item in preparation.Items)
        {
            var destinationPath = item.DestinationPath;
            if (conflictPaths.Contains(item.SourcePath))
            {
                if (!conflictResolutions.TryGetValue(item.SourcePath, out var resolution))
                {
                    throw new InvalidOperationException("同名項目の処理方法が選択されていません。");
                }

                if (resolution == ShellFileConflictResolution.Skip)
                {
                    skippedCount++;
                    continue;
                }

                destinationPath = CreateUniqueDestinationPath(
                    destinationPath,
                    item.IsDirectory,
                    plannedDestinations);
            }

            EnsureDestinationAvailable(item.SourcePath, destinationPath);
            if (!plannedDestinations.Add(destinationPath))
            {
                throw new IOException("同じ名前になる項目が複数含まれています。");
            }

            resolvedItems.Add(item with { DestinationPath = destinationPath });
        }

        return new ShellFileTransferResolution(
            new ShellFileTransferPlan(
                preparation.DestinationDirectory,
                preparation.Move,
                resolvedItems),
            skippedCount);
    }

    private static ShellFileTransferPreparation CreateTransferPreparationCore(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        cancellationToken.ThrowIfCancellationRequested();
        var destinationPath = RequireExistingDirectory(destinationDirectory);
        var normalizedSources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSources.Length == 0)
        {
            throw new InvalidOperationException("コピーまたは移動する項目がありません。");
        }

        var sourceDirectories = normalizedSources
            .Where(Directory.Exists)
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
        foreach (var sourcePath in normalizedSources)
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                throw new FileNotFoundException("コピーまたは移動する項目が見つかりません。", sourcePath);
            }

            if (sourceDirectories.Any(directory => !string.Equals(directory, sourcePath, StringComparison.OrdinalIgnoreCase)
                && IsDescendantPath(sourcePath, directory)))
            {
                throw new IOException("親フォルダーとその中の項目を同時には処理できません。");
            }
        }

        var plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ShellFileTransferPlanItem>(normalizedSources.Length);
        var conflicts = new List<ShellFileTransferConflict>();
        foreach (var sourcePath in normalizedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(sourcePath);
            if (isDirectory && IsDescendantPath(destinationPath, sourcePath))
            {
                throw new IOException("フォルダーをそのフォルダー自身の中へコピーまたは移動できません。");
            }

            var itemDestinationPath = Path.Combine(destinationPath, GetDisplayName(sourcePath));
            var hasConflict = string.Equals(sourcePath, itemDestinationPath, StringComparison.OrdinalIgnoreCase)
                || File.Exists(itemDestinationPath)
                || Directory.Exists(itemDestinationPath)
                || !plannedDestinations.Add(itemDestinationPath);
            if (hasConflict)
            {
                conflicts.Add(new ShellFileTransferConflict(
                    sourcePath,
                    itemDestinationPath,
                    isDirectory));
            }

            items.Add(new ShellFileTransferPlanItem(sourcePath, itemDestinationPath, isDirectory));
        }

        return new ShellFileTransferPreparation(destinationPath, move, items, conflicts);
    }

    public async Task<ShellFileOperationResult> ExecuteTransferAsync(
        ShellFileTransferPlanItem item,
        bool move,
        IProgress<ShellFileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDestinationAvailable(item.SourcePath, item.DestinationPath);
        progress?.Report(new ShellFileTransferProgress(item.SourcePath, 0));

        if (!move)
        {
            await CopyPathAsync(
                item,
                progress,
                _transferOperations.CopyChunkCompleted,
                _transferOperations.DestinationReadyForPublication,
                cancellationToken).ConfigureAwait(false);
            return CreateUndoableResult(ShellFileOperationKind.Copy, item.SourcePath, item.DestinationPath);
        }

        if (_transferOperations.AreOnSameVolume(item.SourcePath, item.DestinationPath))
        {
            await Task.Run(
                () => MoveCore(
                    item.IsDirectory ? ShellItemKind.Directory : ShellItemKind.File,
                    item.SourcePath,
                    item.DestinationPath),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CopyPathAsync(
                item,
                progress,
                _transferOperations.CopyChunkCompleted,
                _transferOperations.DestinationReadyForPublication,
                cancellationToken).ConfigureAwait(false);
            _transferOperations.DestinationPublished(item);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(
                    () => _transferOperations.DeleteMovedSource(item),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"コピー先を保持しましたが、移動元を削除できなかったため移動は完了していません。移動元: {item.SourcePath} コピー先: {item.DestinationPath}",
                    ex);
            }
        }

        return CreateUndoableResult(ShellFileOperationKind.Move, item.SourcePath, item.DestinationPath);
    }

    public async Task<ShellFileTransferMeasurement> MeasureTransferAsync(
        ShellFileTransferPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return await Task.Run(
            () =>
            {
                var itemBytes = new List<long>(plan.Items.Count);
                var totalBytes = 0L;
                foreach (var item in plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requiresCopy = !plan.Move || !string.Equals(
                        Path.GetPathRoot(item.SourcePath),
                        Path.GetPathRoot(item.DestinationPath),
                        StringComparison.OrdinalIgnoreCase);
                    var bytes = requiresCopy
                        ? MeasureTransferItemBytes(item, cancellationToken)
                        : 0L;
                    itemBytes.Add(bytes);
                    totalBytes = AddBytesSaturating(totalBytes, bytes);
                }

                return new ShellFileTransferMeasurement(totalBytes, itemBytes);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void Undo(ShellFileOperationResult operation)
    {
        if (!operation.CanUndo)
        {
            throw new InvalidOperationException("この操作はアプリ内から元に戻せません。");
        }

        if (operation.UndoProof is not { } proof || !MatchesUndoProof(operation.DestinationPath, proof))
            throw new IOException("操作後に対象が変更・置換されたか同一性を確認できません。自動では元に戻しません。");

        switch (operation.Kind)
        {
            case ShellFileOperationKind.CreateDirectory:
                Directory.Delete(operation.DestinationPath, recursive: false);
                break;
            case ShellFileOperationKind.Copy:
                DeleteCopiedItemToRecycleBin(operation.DestinationPath);
                break;
            case ShellFileOperationKind.Rename:
            case ShellFileOperationKind.Move:
                EnsureDestinationAvailable(operation.DestinationPath, operation.SourcePath);
                if (Directory.Exists(operation.DestinationPath))
                {
                    Directory.Move(operation.DestinationPath, operation.SourcePath);
                }
                else if (File.Exists(operation.DestinationPath))
                {
                    File.Move(operation.DestinationPath, operation.SourcePath);
                }
                else
                {
                    throw new FileNotFoundException("元に戻す対象が見つかりません。", operation.DestinationPath);
                }

                break;
            default:
                throw new InvalidOperationException("この操作は元に戻せません。");
        }
    }

    private static ShellFileOperationResult CreateUndoableResult(
        ShellFileOperationKind kind, string sourcePath, string destinationPath)
    {
        var proof = TryCaptureUndoProof(destinationPath, kind == ShellFileOperationKind.Copy);
        return new ShellFileOperationResult(kind, sourcePath, destinationPath, CanUndo: proof is not null)
        {
            UndoProof = proof
        };
    }

    private static ShellUndoProof? TryCaptureUndoProof(string destinationPath, bool includeContents)
    {
        try
        {
            var entries = new Dictionary<string, ShellUndoEntry>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            long totalContentBytes = 0;
            pending.Push(destinationPath);
            while (pending.Count > 0)
            {
                if (entries.Count >= 10000) return null;
                var path = pending.Pop();
                var entry = ReadUndoEntry(path);
                if (entry is null) return null;
                if (includeContents && !entry.IsDirectory)
                {
                    if (entry.Length > MaxCopyUndoContentBytes - totalContentBytes) return null;
                    var hash = TryHashUndoFile(path, entry, MaxCopyUndoContentBytes - totalContentBytes);
                    if (hash is null) return null;
                    entry = entry with { ContentHash = hash };
                    totalContentBytes += entry.Length;
                }
                entries.Add(Path.GetRelativePath(destinationPath, path), entry);
                if (includeContents && entry.IsDirectory)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                }
            }

            return new ShellUndoProof(includeContents, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryHashUndoFile(string path, ShellUndoEntry expected, long remainingBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            TransferBufferSize, FileOptions.SequentialScan);
        if (stream.Length != expected.Length) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            long readTotal = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (read > remainingBytes - readTotal) return null;
                hash.AppendData(buffer, 0, read);
                readTotal += read;
            }

            var after = ReadUndoEntry(path);
            return readTotal == expected.Length && after is not null
                && after.Identity == expected.Identity && after.Length == expected.Length
                && after.LastWriteTimeUtc == expected.LastWriteTimeUtc
                ? Convert.ToHexString(hash.GetHashAndReset()) : null;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool MatchesUndoProof(string destinationPath, ShellUndoProof proof)
    {
        var current = TryCaptureUndoProof(destinationPath, proof.IncludeContents);
        if (current is null || current.Entries.Count != proof.Entries.Count) return false;
        foreach (var (path, expected) in proof.Entries)
        {
            if (!current.Entries.TryGetValue(path, out var actual)
                || actual.Identity != expected.Identity
                || proof.IncludeContents && (actual.Length != expected.Length
                    || actual.LastWriteTimeUtc != expected.LastWriteTimeUtc
                    || actual.ContentHash != expected.ContentHash))
                return false;
        }

        return true;
    }

    private static ShellUndoEntry? ReadUndoEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        using var handle = CreateFileForIdentity(path, 0x80,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            isDirectory ? 0x02000000u : 0u, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) return null;
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        if (fileIndex == 0) return null;
        var length = isDirectory ? 0 : new FileInfo(path).Length;
        var lastWrite = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        return new ShellUndoEntry(
            new ShellFileIdentity(information.VolumeSerialNumber, fileIndex),
            isDirectory, length, lastWrite);
    }

    private static string GetIconPngBase64(ShellItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = CreateIconLookupRequest(item);
        if (request.CacheKey is not { } cacheKey)
        {
            return LoadIconPngBase64(request, cancellationToken);
        }

        var cached = FileTypeIconCache.GetOrAdd(
            cacheKey,
            _ => new Lazy<string>(
                () => LoadIconPngBase64(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var iconPngBase64 = cached.Value;
            if (string.IsNullOrWhiteSpace(iconPngBase64)
                && FileTypeIconCache.TryGetValue(cacheKey, out var current)
                && ReferenceEquals(current, cached))
            {
                FileTypeIconCache.TryRemove(cacheKey, out _);
            }

            return iconPngBase64;
        }
        catch
        {
            if (FileTypeIconCache.TryGetValue(cacheKey, out var current)
                && ReferenceEquals(current, cached))
            {
                FileTypeIconCache.TryRemove(cacheKey, out _);
            }

            throw;
        }
    }

    public static bool IsTextDocumentPath(string path)
    {
        return string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ReadTextDocumentAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        if (!IsTextDocumentPath(normalizedPath))
        {
            throw new NotSupportedException(".txtファイルだけをWorkspaceメモへ取り込めます。");
        }

        var length = new FileInfo(normalizedPath).Length;
        if (length > MaximumTextDocumentImportBytes)
        {
            throw new InvalidDataException("8 MBを超えるテキストファイルはWorkspaceメモへ取り込めません。");
        }

        var bytes = await File.ReadAllBytesAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength > MaximumTextDocumentImportBytes)
        {
            throw new InvalidDataException("8 MBを超えるテキストファイルはWorkspaceメモへ取り込めません。");
        }

        return DecodeTextDocument(bytes);
    }

    internal static string DecodeTextDocument(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00
            && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetString(bytes[4..]);
        }

        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.UTF32.GetString(bytes[4..]);
        }

        if (bytes.Length >= 3
            && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    internal static WorkspaceShellIconLookupRequest CreateIconLookupRequest(ShellItem item)
    {
        var path = NormalizePath(item.TargetPath);
        if (item.Kind != ShellItemKind.File)
        {
            return new WorkspaceShellIconLookupRequest(path, 0, UseFileAttributes: false, CacheKey: null);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var lookupPath = string.IsNullOrWhiteSpace(extension) ? "file" : extension;
        return new WorkspaceShellIconLookupRequest(
            lookupPath,
            FileAttributeNormal,
            UseFileAttributes: true,
            $"file|{extension}");
    }

    private static string LoadIconPngBase64(
        WorkspaceShellIconLookupRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiSmallIcon;
        if (request.UseFileAttributes)
        {
            flags |= ShgfiUseFileAttributes;
        }

        var result = SHGetFileInfo(
            request.LookupPath,
            request.FileAttributes,
            ref info,
            (uint)Marshal.SizeOf<ShFileInfo>(),
            flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static string GetMediaThumbnailPngBase64(ShellItem item, CancellationToken cancellationToken)
    {
        var image = GetMediaThumbnailImage(
            item.TargetPath,
            MediaThumbnailWidth,
            MediaThumbnailHeight,
            cancellationToken);
        return image is null ? string.Empty : EncodePngBase64(image);
    }

    private static BitmapSource? GetMediaThumbnailImage(
        string targetPath,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(targetPath);
        if (!File.Exists(path))
        {
            return null;
        }

        if (IsImagePath(path))
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = pixelWidth;
                image.StreamSource = stream;
                image.EndInit();
                cancellationToken.ThrowIfCancellationRequested();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Windows Shellのサムネイル取得へフォールバックする。
            }
        }

        return GetShellThumbnail(path, pixelWidth, pixelHeight, cancellationToken);
    }

    private static BitmapSource? GetShellThumbnail(
        string path,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interfaceId = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var imageFactory);
        if (result < 0 || imageFactory is null)
        {
            return null;
        }

        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            result = imageFactory.GetImage(
                new ShellSize(pixelWidth, pixelHeight),
                ShellItemImageFlags.BiggerSizeOk | ShellItemImageFlags.ThumbnailOnly | ShellItemImageFlags.ScaleUp,
                out bitmapHandle);
            if (result < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            Marshal.FinalReleaseComObject(imageFactory);
        }
    }

    private static string EncodePngBase64(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void DeleteCopiedItemToRecycleBin(string path)
    {
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    private static async Task CopyPathAsync(
        ShellFileTransferPlanItem item,
        IProgress<ShellFileTransferProgress>? progress,
        Action<string, long> copyChunkCompleted,
        Action<ShellFileTransferPlanItem> destinationReadyForPublication,
        CancellationToken cancellationToken)
    {
        var stagingPath = CreateTransferStagingPath(item.DestinationPath);
        var ownsStagingPath = false;
        var bytesTransferred = 0L;
        var currentPath = item.SourcePath;
        var lastProgressTimestamp = Stopwatch.GetTimestamp();

        void ReportProgress(string path, long addedBytes, bool force)
        {
            currentPath = path;
            bytesTransferred = AddBytesSaturating(bytesTransferred, addedBytes);
            if (progress is null)
            {
                return;
            }

            var currentTimestamp = Stopwatch.GetTimestamp();
            if (!force
                && Stopwatch.GetElapsedTime(lastProgressTimestamp, currentTimestamp) < TransferProgressInterval)
            {
                return;
            }

            lastProgressTimestamp = currentTimestamp;
            progress.Report(new ShellFileTransferProgress(currentPath, bytesTransferred));
        }

        try
        {
            if (!item.IsDirectory)
            {
                await CopyFileAsync(
                    item.SourcePath,
                    stagingPath,
                    ReportProgress,
                    copyChunkCompleted,
                    () => ownsStagingPath = true,
                    cancellationToken).ConfigureAwait(false);
                destinationReadyForPublication(item);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingPath, item.DestinationPath);
                ownsStagingPath = false;
                ReportProgress(currentPath, 0, force: true);
                return;
            }

            var entries = await Task.Run(
                () => BuildDirectoryCopyPlan(item.SourcePath, stagingPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CreateDirectoryExclusive(stagingPath);
            ownsStagingPath = true;
            foreach (var directory in entries.Directories.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
            }

            foreach (var file in entries.Files)
            {
                await CopyFileAsync(
                    file.SourcePath,
                    file.DestinationPath,
                    ReportProgress,
                    copyChunkCompleted,
                    static () => { },
                    cancellationToken).ConfigureAwait(false);
            }

            destinationReadyForPublication(item);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingPath, item.DestinationPath);
            ownsStagingPath = false;
            ReportProgress(currentPath, 0, force: true);
        }
        catch
        {
            if (ownsStagingPath)
            {
                TryDeleteOwnedPartialCopy(stagingPath);
            }

            throw;
        }
    }

    private static string CreateTransferStagingPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath)
            ?? throw new IOException("コピー先の親フォルダーを取得できません。");
        return Path.Combine(parentPath, TransferStagingPrefix + Guid.NewGuid().ToString("N"));
    }

    private static void CreateDirectoryExclusive(string path)
    {
        if (CreateDirectoryNative(path, IntPtr.Zero))
        {
            return;
        }

        var error = new Win32Exception(Marshal.GetLastWin32Error());
        throw new IOException("コピー用の一時フォルダーを排他的に作成できませんでした: " + error.Message, error);
    }

    private static DirectoryCopyPlan BuildDirectoryCopyPlan(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException("シンボリックリンクまたはジャンクションはコピー対象にできません。");
        }

        var directories = new List<string> { destinationRoot };
        var files = new List<CopyFilePlan>();
        var pending = new Queue<(string Source, string Destination)>();
        pending.Enqueue((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new NotSupportedException(
                        $"シンボリックリンクまたはジャンクションを含むため処理できません: {GetDisplayName(entryPath)}");
                }

                var entryDestination = Path.Combine(current.Destination, GetDisplayName(entryPath));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(entryDestination);
                    pending.Enqueue((entryPath, entryDestination));
                }
                else
                {
                    files.Add(new CopyFilePlan(entryPath, entryDestination));
                }
            }
        }

        return new DirectoryCopyPlan(directories, files);
    }

    private static long MeasureTransferItemBytes(
        ShellFileTransferPlanItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!item.IsDirectory)
        {
            return new FileInfo(item.SourcePath).Length;
        }

        var entries = BuildDirectoryCopyPlan(item.SourcePath, item.DestinationPath, cancellationToken);
        var totalBytes = 0L;
        foreach (var file in entries.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes = AddBytesSaturating(totalBytes, new FileInfo(file.SourcePath).Length);
        }

        return totalBytes;
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        Action<string, long, bool> progress,
        Action<string, long> copyChunkCompleted,
        Action destinationCreated,
        CancellationToken cancellationToken)
    {
        progress(sourcePath, 0, false);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: TransferBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: TransferBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        destinationCreated();
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, TransferBufferSize),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copyChunkCompleted(sourcePath, read);
                progress(sourcePath, read, false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long AddBytesSaturating(long current, long added)
    {
        return added > long.MaxValue - current
            ? long.MaxValue
            : current + added;
    }

    private static void DeleteMovedSource(ShellFileTransferPlanItem item)
    {
        if (item.IsDirectory)
        {
            Directory.Delete(item.SourcePath, recursive: true);
        }
        else
        {
            File.Delete(item.SourcePath);
        }
    }

    private static void TryDeleteOwnedPartialCopy(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 排他的に取得した一時領域だけを後始末する。失敗時は競合先へ触れず一時データを残す。
        }
    }

    private static bool IsDescendantPath(string candidatePath, string parentPath)
    {
        var normalizedCandidate = NormalizePath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = NormalizePath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedCandidate.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveCore(ShellItemKind kind, string sourcePath, string destinationPath)
    {
        if (kind == ShellItemKind.Directory)
        {
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private static void RequireExisting(ShellItem item)
    {
        if (!Exists(item))
        {
            throw new FileNotFoundException("対象が見つかりません。", item.TargetPath);
        }
    }

    private static string RequireExistingDirectory(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException("保存先フォルダーが見つかりません。");
        }

        return normalizedPath;
    }

    private static string ValidateFileName(string name)
    {
        var value = name.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("使用できない名前です。", nameof(name));
        }

        return value;
    }

    private static void EnsureDestinationAvailable(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("移動元と移動先が同じです。");
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("移動先に同じ名前の項目があります。");
        }
    }

    private static string CreateUniqueDestinationPath(
        string desiredPath,
        bool isDirectory,
        IReadOnlySet<string> plannedDestinations)
    {
        var directoryPath = Path.GetDirectoryName(desiredPath)
            ?? throw new ArgumentException("保存先フォルダーを取得できません。", nameof(desiredPath));
        var extension = isDirectory ? string.Empty : Path.GetExtension(desiredPath);
        var baseName = isDirectory
            ? Path.GetFileName(desiredPath)
            : Path.GetFileNameWithoutExtension(desiredPath);
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidatePath = Path.Combine(directoryPath, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(candidatePath)
                && !Directory.Exists(candidatePath)
                && !plannedDestinations.Contains(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new IOException("別名を生成できませんでした。");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileForIdentity(
        string path, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ShellSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShellSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        BiggerSizeOk = 0x1,
        ThumbnailOnly = 0x8,
        ScaleUp = 0x100
    }
}

public enum ShellFileOperationKind
{
    CreateDirectory,
    Rename,
    Copy,
    Move,
    Recycle
}

public sealed record ShellFileOperationResult(
    ShellFileOperationKind Kind,
    string SourcePath,
    string DestinationPath,
    bool CanUndo = true)
{
    internal ShellUndoProof? UndoProof { get; init; }
}

internal sealed record ShellUndoProof(bool IncludeContents, IReadOnlyDictionary<string, ShellUndoEntry> Entries);

internal sealed record ShellUndoEntry(ShellFileIdentity Identity, bool IsDirectory, long Length,
    DateTime LastWriteTimeUtc, string? ContentHash = null);

internal readonly record struct ShellFileIdentity(uint VolumeSerialNumber, ulong FileIndex);

public sealed record ShellFileTransferPlan(
    string DestinationDirectory,
    bool Move,
    IReadOnlyList<ShellFileTransferPlanItem> Items);

public sealed record ShellFileTransferPlanItem(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory);

public sealed record ShellFileTransferPreparation(
    string DestinationDirectory,
    bool Move,
    IReadOnlyList<ShellFileTransferPlanItem> Items,
    IReadOnlyList<ShellFileTransferConflict> Conflicts);

public sealed record ShellFileTransferConflict(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory);

public enum ShellFileConflictResolution
{
    Skip,
    KeepBoth
}

public sealed record ShellFileTransferResolution(
    ShellFileTransferPlan Plan,
    int SkippedCount);

public sealed record ShellFileTransferMeasurement(
    long TotalBytes,
    IReadOnlyList<long> ItemBytes);

public sealed record ShellFileTransferProgress(
    string CurrentPath,
    long BytesTransferred);

internal sealed record CopyFilePlan(string SourcePath, string DestinationPath);

internal sealed record WorkspaceShellItemTransferOperations(
    Func<string, string, bool> AreOnSameVolume,
    Action<ShellFileTransferPlanItem> DeleteMovedSource,
    Action<ShellFileTransferPlanItem> DestinationReadyForPublication,
    Action<ShellFileTransferPlanItem> DestinationPublished,
    Action<string, long> CopyChunkCompleted);

internal sealed record WorkspaceShellIconLookupRequest(
    string LookupPath,
    uint FileAttributes,
    bool UseFileAttributes,
    string? CacheKey);

internal sealed record DirectoryCopyPlan(
    IReadOnlyList<string> Directories,
    IReadOnlyList<CopyFilePlan> Files);
