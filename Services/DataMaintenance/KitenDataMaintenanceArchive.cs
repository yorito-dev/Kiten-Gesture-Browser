using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TabBrowser.Services.DataMaintenance;

internal static class KitenDataMaintenanceArchive
{
    public const int ManifestSchemaVersion = 1;
    public const int MaximumArchiveEntries = 4096;
    public const long MaximumSingleFileBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    // Workspace Repositoryが検証済み移行で受け入れるversion 0 / 1と、現行version 2を扱う。
    // version 2を超える形式は旧ツールで推測復元しない。
    private const int MaximumWorkspaceSchemaVersion = 2;
    private const string ManifestEntryName = "backup-info.json";
    private const string DataEntryPrefix = "data/";
    private static readonly string[] SqliteCompanionSuffixes = [string.Empty, "-wal", "-shm", "-journal"];

    private static readonly Regex FullShaPattern = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex PublicVersionPattern = new(
        "^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly KitenManagedFileDefinition[] CoreFiles =
    [
        new("settings.json", KitenManagedFileKind.Json, 1),
        new("settings.backup.json", KitenManagedFileKind.Json, 1),
        new("bookmarks.json", KitenManagedFileKind.Json, 3),
        new("bookmarks.backup.json", KitenManagedFileKind.Json, 3),
        new("session.json", KitenManagedFileKind.Json, 2),
        new("session.backup.json", KitenManagedFileKind.Json, 2),
        new("window-memory.json", KitenManagedFileKind.Json, 2),
        new("window-memory.backup.json", KitenManagedFileKind.Json, 2),
        new("split-views.json", KitenManagedFileKind.Json, 0),
        new("split-views.backup.json", KitenManagedFileKind.Json, 0),
        new("container-profiles.json", KitenManagedFileKind.Json, 0),
        new("container-profiles.backup.json", KitenManagedFileKind.Json, 0),
        new("youtube-mini-players.json", KitenManagedFileKind.Json, 0),
        new("youtube-mini-players.backup.json", KitenManagedFileKind.Json, 0),
        new("workspace.db", KitenManagedFileKind.Sqlite, MaximumWorkspaceSchemaVersion)
    ];

    private static readonly KitenManagedFileDefinition[] BrowsingHistoryFiles =
    [
        new("browsing-history.db", KitenManagedFileKind.Sqlite, 0, IsHistory: true)
    ];

    private static readonly KitenManagedFileDefinition[] RecentItemFiles =
    [
        new("recent-items.json", KitenManagedFileKind.Json, 0, IsHistory: true),
        new("recent-items.backup.json", KitenManagedFileKind.Json, 0, IsHistory: true)
    ];

    private static readonly KitenManagedFileDefinition[] DownloadHistoryFiles =
    [
        new("downloads/history.json", KitenManagedFileKind.Json, 0, IsHistory: true),
        new("downloads/history.backup.json", KitenManagedFileKind.Json, 0, IsHistory: true)
    ];

    internal static readonly string[] ManagedCollectionPaths =
    [
        "session.snapshots",
        "session.protected",
        "session.named"
    ];

    internal static IReadOnlyList<KitenManagedFileDefinition> GetFixedDefinitions(
        bool includeBrowsingHistory,
        bool includeRecentItems,
        bool includeDownloadHistory)
    {
        var definitions = new List<KitenManagedFileDefinition>(CoreFiles);
        if (includeBrowsingHistory)
        {
            definitions.AddRange(BrowsingHistoryFiles);
        }

        if (includeRecentItems)
        {
            definitions.AddRange(RecentItemFiles);
        }

        if (includeDownloadHistory)
        {
            definitions.AddRange(DownloadHistoryFiles);
        }

        return definitions;
    }

    internal static IReadOnlyList<KitenManagedFileDefinition> GetFixedDefinitions(KitenBackupManifest manifest) =>
        GetFixedDefinitions(
            manifest.IncludesBrowsingHistory,
            manifest.IncludesRecentItems,
            manifest.IncludesDownloadHistory);

    internal static string ChannelName(ApplicationChannel channel) =>
        channel == ApplicationChannel.Development ? "development" : "stable";

    internal static string NormalizeDataRoot(string value, ApplicationChannel channel)
    {
        var fullPath = NormalizeAbsolutePath(value, "データルート");
        var expectedName = ApplicationChannelService.GetDataDirectoryName(channel);
        if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"対象チャンネルとデータルートが一致しません。末尾は {expectedName} である必要があります。");
        }

        if (File.Exists(fullPath))
        {
            throw new InvalidDataException("データルートにファイルは指定できません。");
        }

        EnsureExistingPathHasNoReparsePoint(fullPath);
        return fullPath;
    }

    internal static string NormalizeDestination(string value, string dataRoot)
    {
        var fullPath = NormalizeAbsolutePath(value, "保存先");
        if (File.Exists(fullPath))
        {
            throw new InvalidDataException("バックアップ保存先にはフォルダーを指定してください。");
        }

        if (IsSameOrDescendant(fullPath, dataRoot))
        {
            throw new InvalidDataException("バックアップ保存先をKitenのデータルート内には置けません。");
        }

        EnsureExistingPathHasNoReparsePoint(fullPath);
        return fullPath;
    }

    internal static string NormalizeArchivePath(string value)
    {
        var fullPath = NormalizeAbsolutePath(value, "バックアップZIP");
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("バックアップには.zipファイルを指定してください。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("バックアップZIPが見つかりません。", fullPath);
        }

        EnsureExistingPathHasNoReparsePoint(fullPath);
        return fullPath;
    }

    internal static string NormalizeAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{label}は絶対パスで指定してください。");
        }

        var fullPath = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath)
            || string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label}にドライブのルートは指定できません。");
        }

        return fullPath;
    }

    internal static string ResolveManagedPath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(fullPath, root) || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("対象パスが許可されたルート外を指しています。");
        }

        return fullPath;
    }

    internal static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.StartsWith('\\')
            || relativePath.Contains('\\')
            || relativePath.Contains(':'))
        {
            throw new InvalidDataException("バックアップ内に不正なパスがあります。");
        }

        var parts = relativePath.Split('/', StringSplitOptions.None);
        if (parts.Any(static part =>
                string.IsNullOrWhiteSpace(part)
                || part is "." or ".."
                || part.EndsWith(' ')
                || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(part)))
        {
            throw new InvalidDataException("バックアップ内にWindowsで使用できないパスがあります。");
        }
    }

    internal static void EnsureExistingPathHasNoReparsePoint(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("再解析ポイントを経由するパスは使用できません。");
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    internal static bool IsSameOrDescendant(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static KitenSourceApplicationInformation ReadSourceApplication(string? releaseInformationPath)
    {
        if (string.IsNullOrWhiteSpace(releaseInformationPath))
        {
            return new KitenSourceApplicationInformation();
        }

        var path = NormalizeAbsolutePath(releaseInformationPath, "release-info.json");
        if (!File.Exists(path))
        {
            return new KitenSourceApplicationInformation();
        }

        EnsureExistingPathHasNoReparsePoint(path);
        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("release-info.jsonが大きすぎます。");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, JsonDocumentOptions);
        var root = document.RootElement;
        var version = ReadString(root, "version");
        var sourceCommit = ReadString(root, "sourceCommit");
        return new KitenSourceApplicationInformation
        {
            PublicVersion = PublicVersionPattern.IsMatch(version ?? string.Empty) ? version! : "未取得",
            SourceCommit = FullShaPattern.IsMatch(sourceCommit ?? string.Empty)
                ? sourceCommit!.ToLowerInvariant()
                : "未取得"
        };
    }

    internal static int? CopyAndValidateDataFile(
        string sourcePath,
        string destinationPath,
        KitenManagedFileDefinition definition,
        CancellationToken cancellationToken)
    {
        EnsureExistingPathHasNoReparsePoint(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (definition.Kind == KitenManagedFileKind.Sqlite)
        {
            var sourceSnapshotPath = destinationPath + ".source-snapshot";
            try
            {
                CopySqliteFamily(sourcePath, sourceSnapshotPath, cancellationToken);
                BackupSqliteDatabase(sourceSnapshotPath, destinationPath, cancellationToken);
                return ReadAndValidateSqlite(
                    destinationPath,
                    definition.MaximumFormatVersion,
                    cancellationToken,
                    definition.RelativePath);
            }
            catch (KitenUnsupportedDataFormatException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or InvalidDataException)
            {
                throw new KitenDataValidationException(
                    KitenDataValidationStage.StagedValidation,
                    exception);
            }
            finally
            {
                DeleteSqliteFamily(sourceSnapshotPath);
            }
        }

        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            CopyWithLimit(source, destination, MaximumSingleFileBytes, cancellationToken);
            destination.Flush(flushToDisk: true);
        }

        try
        {
            return ReadAndValidateJson(
                destinationPath,
                definition.MaximumFormatVersion,
                definition.RelativePath);
        }
        catch (KitenUnsupportedDataFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new KitenDataValidationException(
                KitenDataValidationStage.StagedValidation,
                exception);
        }
    }

    internal static int? ValidateSourceDataFile(
        string sourcePath,
        string validationPath,
        KitenManagedFileDefinition definition,
        CancellationToken cancellationToken)
    {
        EnsureExistingPathHasNoReparsePoint(sourcePath);
        try
        {
            if (definition.Kind != KitenManagedFileKind.Sqlite)
            {
                return ReadAndValidateJson(
                    sourcePath,
                    definition.MaximumFormatVersion,
                    definition.RelativePath);
            }

            CopySqliteFamily(sourcePath, validationPath, cancellationToken);
            return ReadAndValidateSqlite(
                validationPath,
                definition.MaximumFormatVersion,
                cancellationToken,
                definition.RelativePath);
        }
        catch (KitenUnsupportedDataFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or SqliteException or InvalidDataException)
        {
            throw new KitenDataValidationException(KitenDataValidationStage.SourceRead, exception);
        }
        finally
        {
            if (definition.Kind == KitenManagedFileKind.Sqlite)
            {
                DeleteSqliteFamily(validationPath);
            }
        }
    }

    private static void CopySqliteFamily(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        foreach (var suffix in SqliteCompanionSuffixes)
        {
            var sourceFile = sourcePath + suffix;
            if (!File.Exists(sourceFile))
            {
                continue;
            }

            EnsureExistingPathHasNoReparsePoint(sourceFile);
            using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(
                destinationPath + suffix,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            CopyWithLimit(source, destination, MaximumSingleFileBytes, cancellationToken);
            destination.Flush(flushToDisk: true);
        }
    }

    private static void DeleteSqliteFamily(string path)
    {
        foreach (var suffix in SqliteCompanionSuffixes)
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    internal static KitenBackupFileEntry CreateFileEntry(
        string relativePath,
        bool present,
        KitenManagedFileKind kind,
        string? stagedPath,
        int? formatVersion,
        CancellationToken cancellationToken = default)
    {
        if (!present || stagedPath is null)
        {
            return new KitenBackupFileEntry
            {
                RelativePath = relativePath,
                Present = false,
                Kind = kind
            };
        }

        var file = new FileInfo(stagedPath);
        return new KitenBackupFileEntry
        {
            RelativePath = relativePath,
            Present = true,
            Kind = kind,
            Size = file.Length,
            Sha256 = ComputeSha256(stagedPath, cancellationToken),
            FormatVersion = formatVersion
        };
    }

    internal static void WriteManifest(string path, KitenBackupManifest manifest)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, manifest, JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    internal static string CreateProvisionalArchive(
        string stageRoot,
        KitenBackupManifest manifest,
        string destinationDirectory,
        string archiveFileName,
        CancellationToken cancellationToken)
    {
        var temporaryArchive = Path.Combine(destinationDirectory, archiveFileName + ".partial");
        var finalArchive = Path.Combine(destinationDirectory, archiveFileName);
        var temporaryHash = temporaryArchive + ".sha256";
        var finalHash = finalArchive + ".sha256";
        if (File.Exists(temporaryArchive)
            || File.Exists(finalArchive)
            || File.Exists(temporaryHash)
            || File.Exists(finalHash))
        {
            throw new IOException("同名のバックアップが既に存在します。既存ファイルは上書きしません。");
        }

        try
        {
            using (var output = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddFileToArchive(archive, Path.Combine(stageRoot, ManifestEntryName), ManifestEntryName, cancellationToken);
                foreach (var file in manifest.Files
                             .Where(static file => file.Present)
                             .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    AddFileToArchive(
                        archive,
                        ResolveManagedPath(Path.Combine(stageRoot, "data"), file.RelativePath),
                        DataEntryPrefix + file.RelativePath,
                        cancellationToken);
                }
            }

            using (var stream = new FileStream(temporaryArchive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            WriteArchiveHash(
                temporaryHash,
                ComputeSha256(temporaryArchive, cancellationToken),
                Path.GetFileName(temporaryArchive));
            return temporaryArchive;
        }
        catch
        {
            DeleteOwnedFile(temporaryArchive);
            DeleteOwnedFile(temporaryHash);
            throw;
        }
    }

    internal static string CommitVerifiedArchive(
        string provisionalArchive,
        string archiveFileName,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(provisionalArchive)
            ?? throw new InvalidDataException("バックアップの確定先を取得できません。");
        var finalArchive = Path.Combine(destinationDirectory, archiveFileName);
        var provisionalHash = provisionalArchive + ".sha256";
        var finalHash = finalArchive + ".sha256";
        var finalHashTemporary = finalHash + ".partial";
        if (File.Exists(finalArchive) || File.Exists(finalHash) || File.Exists(finalHashTemporary))
        {
            throw new IOException("同名のバックアップが既に存在します。既存ファイルは上書きしません。");
        }

        VerifyArchiveHash(provisionalArchive, cancellationToken);
        try
        {
            WriteArchiveHash(
                finalHashTemporary,
                ComputeSha256(provisionalArchive, cancellationToken),
                archiveFileName);
            File.Move(finalHashTemporary, finalHash);
            DeleteOwnedFile(provisionalHash);
            // 完成名のZIPが見える時点では、検証済みの内容と完成名用hashがそろっている。
            // 2ファイルを同時にrenameできないため、中断時は単独hashだけが残る順にする。
            File.Move(provisionalArchive, finalArchive);
            return finalArchive;
        }
        catch
        {
            DeleteOwnedFile(finalHashTemporary);
            if (!File.Exists(finalArchive))
            {
                DeleteOwnedFile(finalHash);
            }

            throw;
        }
    }

    internal static void ValidateArchiveCreationLimits(
        KitenBackupManifest manifest,
        string manifestPath)
    {
        var manifestLength = new FileInfo(manifestPath).Length;
        if (manifestLength is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("backup-info.jsonのサイズが上限外です。");
        }

        var presentFiles = manifest.Files.Where(static file => file.Present).ToList();
        if (presentFiles.Count + 1 > MaximumArchiveEntries)
        {
            throw new InvalidDataException("バックアップZIPのファイル数が上限を超えています。");
        }

        long expandedBytes = manifestLength;
        foreach (var file in presentFiles)
        {
            expandedBytes = checked(expandedBytes + file.Size);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("バックアップZIPの展開後サイズが上限を超えています。");
            }
        }
    }

    internal static KitenBackupManifest ValidateAndExtractArchive(
        string archivePath,
        string extractionRoot,
        CancellationToken cancellationToken)
        => ValidateAndExtractArchive(
            archivePath,
            extractionRoot,
            expectedArchiveSha256: null,
            out _,
            cancellationToken);

    internal static KitenBackupManifest ValidateAndExtractArchive(
        string archivePath,
        string extractionRoot,
        string? expectedArchiveSha256,
        out string verifiedArchiveSha256,
        CancellationToken cancellationToken)
    {
        using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        verifiedArchiveSha256 = VerifyArchiveHash(archivePath, archiveStream, cancellationToken);
        if (expectedArchiveSha256 is not null
            && (!Sha256Pattern.IsMatch(expectedArchiveSha256)
                || !string.Equals(
                    expectedArchiveSha256,
                    verifiedArchiveSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("復元計画で確認したZIPと、現在のZIPのSHA-256が一致しません。復元を中止しました。");
        }

        archiveStream.Position = 0;
        Directory.CreateDirectory(extractionRoot);
        EnsureExistingPathHasNoReparsePoint(extractionRoot);

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
        {
            throw new InvalidDataException("バックアップZIPのファイル数が上限外です。");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveEntryName(entry.FullName);
            if (entry.Name.Length == 0 || IsLinkEntry(entry) || !entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException("バックアップZIPにディレクトリ、リンク、または重複項目があります。");
            }

            if (entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
            {
                throw new InvalidDataException("バックアップZIP内のファイルサイズが上限外です。");
            }

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumExpandedBytes)
            {
                throw new InvalidDataException("バックアップZIPの展開後サイズが上限を超えています。");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry)
            || manifestEntry.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("backup-info.jsonがないか、大きすぎます。");
        }

        KitenBackupManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<KitenBackupManifest>(manifestStream, JsonOptions)
                ?? throw new InvalidDataException("backup-info.jsonを読み込めません。");
        }

        ValidateManifest(manifest);
        var expectedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestEntryName };
        foreach (var file in manifest.Files.Where(static file => file.Present))
        {
            expectedEntries.Add(DataEntryPrefix + file.RelativePath);
        }

        if (!expectedEntries.SetEquals(entries.Keys))
        {
            throw new InvalidDataException("バックアップZIPにmanifest外の項目、または不足項目があります。");
        }

        var dataRoot = Path.Combine(extractionRoot, "data");
        foreach (var file in manifest.Files.Where(static file => file.Present))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[DataEntryPrefix + file.RelativePath];
            if (entry.Length != file.Size)
            {
                throw new InvalidDataException("バックアップZIPのサイズ情報がmanifestと一致しません。");
            }

            var destination = ResolveManagedPath(dataRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyWithLimit(input, output, MaximumSingleFileBytes, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            var definition = ResolveDefinition(manifest, file.RelativePath);
            ValidateExtractedFile(destination, file, definition, cancellationToken);
        }

        // ZIPには空ディレクトリ項目を入れないため、manifest上で存在する空の保存フォルダーは
        // 検証済みの展開領域へ明示的に作る。復元側はこのフォルダーをそのまま移動できる。
        foreach (var collection in manifest.Collections.Where(static collection => collection.Present))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ResolveManagedPath(dataRoot, collection.RelativePath));
        }

        return manifest;
    }

    internal static void ValidateManifest(KitenBackupManifest manifest)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion
            || !string.Equals(manifest.ProductName, "Kiten Gesture Browser", StringComparison.Ordinal)
            || manifest.CreatedAtUtc == default
            || manifest.Encrypted)
        {
            throw new InvalidDataException("未対応または不正なバックアップ形式です。");
        }

        if (manifest.Channel is not ("stable" or "development"))
        {
            throw new InvalidDataException("バックアップのチャンネル情報が不正です。");
        }

        ValidateSourceApplication(manifest.SourceApplication);
        if (manifest.Files.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("manifestのファイル数が上限を超えています。");
        }

        var definitions = GetFixedDefinitions(manifest);
        var files = new Dictionary<string, KitenBackupFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.RelativePath);
            if (!files.TryAdd(file.RelativePath, file))
            {
                throw new InvalidDataException("manifestに重複パスがあります。");
            }

            if (!file.Present)
            {
                if (file.Size != 0 || file.Sha256.Length != 0 || file.FormatVersion is not null)
                {
                    throw new InvalidDataException("不存在項目のmanifest情報が不正です。");
                }
            }
            else if (file.Size < 0
                     || file.Size > MaximumSingleFileBytes
                     || !Sha256Pattern.IsMatch(file.Sha256))
            {
                throw new InvalidDataException("manifestのサイズまたはハッシュが不正です。");
            }
        }

        foreach (var definition in definitions)
        {
            if (!files.TryGetValue(definition.RelativePath, out var file) || file.Kind != definition.Kind)
            {
                throw new InvalidDataException("manifestに固定対象の記録がありません。");
            }

            ValidateFormatVersion(file.FormatVersion, definition.MaximumFormatVersion);
        }

        var collectionMap = new Dictionary<string, KitenBackupCollectionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in manifest.Collections)
        {
            ValidateRelativePath(collection.RelativePath);
            if (!ManagedCollectionPaths.Contains(collection.RelativePath, StringComparer.OrdinalIgnoreCase)
                || !collectionMap.TryAdd(collection.RelativePath, collection))
            {
                throw new InvalidDataException("manifestの保存フォルダー情報が不正です。");
            }
        }

        if (collectionMap.Count != ManagedCollectionPaths.Length)
        {
            throw new InvalidDataException("manifestの保存フォルダー情報が不足しています。");
        }

        var fixedPaths = definitions.Select(static definition => definition.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Values.Where(file => !fixedPaths.Contains(file.RelativePath)))
        {
            var collectionPath = Path.GetDirectoryName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar))?
                .Replace(Path.DirectorySeparatorChar, '/');
            if (collectionPath is null
                || !collectionMap.TryGetValue(collectionPath, out var collection)
                || !collection.Present
                || !file.Present
                || file.Kind != KitenManagedFileKind.Json
                || !string.Equals(Path.GetExtension(file.RelativePath), ".json", StringComparison.OrdinalIgnoreCase)
                || file.RelativePath[(collectionPath.Length + 1)..].Contains('/'))
            {
                throw new InvalidDataException("manifestに許可されていない項目があります。");
            }

            ValidateFormatVersion(file.FormatVersion, 2);
        }

        foreach (var collection in collectionMap.Values.Where(static value => !value.Present))
        {
            if (files.Keys.Any(path => path.StartsWith(collection.RelativePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("不存在フォルダーにファイルが記録されています。");
            }
        }
    }

    internal static void ValidateDataRootAgainstManifest(
        string dataRoot,
        KitenBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveManagedPath(dataRoot, file.RelativePath);
            if (file.Present != File.Exists(path))
            {
                throw new InvalidDataException("復元後の存在状態がbackup-info.jsonと一致しません。");
            }

            if (file.Present)
            {
                var definition = ResolveDefinition(manifest, file.RelativePath);
                if (definition.Kind == KitenManagedFileKind.Sqlite)
                {
                    EnsureSqliteCompanionsAreAbsent(path);
                }

                ValidateExtractedFile(path, file, definition, cancellationToken);
            }
        }

        foreach (var collection in manifest.Collections)
        {
            var path = ResolveManagedPath(dataRoot, collection.RelativePath);
            if (collection.Present != Directory.Exists(path))
            {
                throw new InvalidDataException("復元後の保存フォルダー状態がbackup-info.jsonと一致しません。");
            }
        }
    }

    internal static void ValidateCurrentDataRootAgainstSnapshot(
        string dataRoot,
        string validationRoot,
        KitenBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(validationRoot);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveManagedPath(dataRoot, file.RelativePath);
            if (file.Present != File.Exists(path))
            {
                throw new InvalidDataException("元データの存在状態を復旧できませんでした。");
            }

            if (!file.Present)
            {
                continue;
            }

            var definition = ResolveDefinition(manifest, file.RelativePath);
            if (definition.Kind == KitenManagedFileKind.Sqlite)
            {
                var snapshotPath = ResolveManagedPath(validationRoot, file.RelativePath);
                var formatVersion = CopyAndValidateDataFile(path, snapshotPath, definition, cancellationToken);
                var snapshot = CreateFileEntry(
                    file.RelativePath,
                    present: true,
                    definition.Kind,
                    snapshotPath,
                    formatVersion,
                    cancellationToken);
                if (snapshot.Size != file.Size
                    || snapshot.FormatVersion != file.FormatVersion
                    || !string.Equals(snapshot.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("元のSQLiteデータを復旧できませんでした。");
                }
            }
            else
            {
                ValidateExtractedFile(path, file, definition, cancellationToken);
            }
        }

        foreach (var collection in manifest.Collections)
        {
            var path = ResolveManagedPath(dataRoot, collection.RelativePath);
            if (collection.Present != Directory.Exists(path))
            {
                throw new InvalidDataException("元データの保存フォルダー状態を復旧できませんでした。");
            }

            if (!collection.Present)
            {
                continue;
            }

            EnsureExistingPathHasNoReparsePoint(path);
            var expectedFiles = manifest.Files
                .Where(file => file.Present
                               && file.RelativePath.StartsWith(
                                   collection.RelativePath + "/",
                                   StringComparison.OrdinalIgnoreCase))
                .Select(static file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Where(static filePath => !IsKnownAtomicJsonTemporaryFile(filePath))
                .Select(filePath => collection.RelativePath + "/" + Path.GetFileName(filePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedFiles.SetEquals(actualFiles)
                || Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidDataException("元データの保存フォルダー内容を復旧できませんでした。");
            }
        }
    }

    private static void EnsureSqliteCompanionsAreAbsent(string databasePath)
    {
        if (File.Exists(databasePath + "-wal")
            || File.Exists(databasePath + "-shm")
            || File.Exists(databasePath + "-journal"))
        {
            throw new InvalidDataException("復元後のSQLiteに旧WALまたはjournalが混在しています。");
        }
    }

    internal static string ComputeSha256(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var algorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            algorithm.AppendData(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(algorithm.GetHashAndReset());
    }

    internal static bool IsKnownAtomicJsonTemporaryFile(string path)
    {
        var parts = Path.GetFileName(path).Split('.', StringSplitOptions.None);
        return parts.Length >= 5
               && string.Equals(parts[^4], "json", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[^3], out var processId)
               && processId > 0
               && Guid.TryParseExact(parts[^2], "N", out _)
               && string.Equals(parts[^1], "tmp", StringComparison.OrdinalIgnoreCase);
    }

    internal static void DeleteOwnedDirectory(string path, string ownerRoot)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (!IsSameOrDescendant(path, ownerRoot) || string.Equals(path, ownerRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("所有していないフォルダーは削除できません。");
        }

        Directory.Delete(path, recursive: true);
    }

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256
    };

    private static int? ReadAndValidateJson(
        string path,
        int? maximumFormatVersion,
        string relativePath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, JsonDocumentOptions);
        ValidateJsonStoreShape(relativePath, document.RootElement);
        int? formatVersion = null;
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("version", out var versionElement))
        {
            if (!versionElement.TryGetInt32(out var version))
            {
                throw new InvalidDataException("JSONの形式versionが整数ではありません。");
            }

            formatVersion = version;
        }

        ValidateFormatVersion(formatVersion, maximumFormatVersion);
        return formatVersion;
    }

    private static void ValidateJsonStoreShape(string relativePath, JsonElement root)
    {
        var normalized = relativePath.Replace('\\', '/');
        var isSession = normalized.StartsWith("session.", StringComparison.OrdinalIgnoreCase)
                        || normalized.StartsWith("session/", StringComparison.OrdinalIgnoreCase)
                        || normalized.StartsWith("window-memory", StringComparison.OrdinalIgnoreCase);
        var isObjectStore = isSession
                            || normalized.StartsWith("settings", StringComparison.OrdinalIgnoreCase)
                            || normalized.StartsWith("bookmarks", StringComparison.OrdinalIgnoreCase);
        var expectedKind = isObjectStore ? JsonValueKind.Object : JsonValueKind.Array;
        if (root.ValueKind != expectedKind)
        {
            throw new InvalidDataException("保存データのJSON構造が対象Storeと一致しません。");
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.Object))
            {
                throw new InvalidDataException("保存データの一覧に不正な項目があります。");
            }

            return;
        }

        if (normalized.StartsWith("bookmarks", StringComparison.OrdinalIgnoreCase))
        {
            ValidateOptionalPropertyKind(root, "bookmarks", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "folders", JsonValueKind.Array);
        }
        else if (isSession)
        {
            ValidateOptionalPropertyKind(root, "tabs", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "closedTabs", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "closedYouTubeMiniPlayers", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "closedWindows", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "windows", JsonValueKind.Array);
        }
        else
        {
            ValidateOptionalPropertyKind(root, "searchEngine", JsonValueKind.String);
            ValidateOptionalPropertyKind(root, "searchUrlTemplate", JsonValueKind.String);
            ValidateOptionalPropertyKind(root, "searchAliases", JsonValueKind.Array);
            ValidateOptionalPropertyKind(root, "gestureMappings", JsonValueKind.Array);
        }
    }

    private static void ValidateOptionalPropertyKind(
        JsonElement root,
        string propertyName,
        JsonValueKind expectedKind)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind != expectedKind)
        {
            throw new InvalidDataException("保存データのJSON項目型が対象Storeと一致しません。");
        }
    }

    private static void BackupSqliteDatabase(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            try
            {
                source.Open();
            }
            catch (SqliteException exception)
            {
                throw new KitenDataValidationException(KitenDataValidationStage.SourceRead, exception);
            }

            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            try
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            catch (SqliteException exception)
            {
                throw new KitenDataValidationException(KitenDataValidationStage.Transfer, exception);
            }
        }

        using var stream = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static int ReadAndValidateSqlite(
        string path,
        int? maximumFormatVersion,
        CancellationToken cancellationToken,
        string relativePath)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();

        using (var quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            var results = new List<string>();
            using var reader = quickCheck.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SQLiteのquick_checkに失敗しました。");
            }
        }

        using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeyCheck.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException("SQLiteのforeign_key_checkに失敗しました。");
            }
        }

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        ValidateFormatVersion(version, maximumFormatVersion);
        ValidateSqliteStoreSchema(connection, relativePath, version);
        return version;
    }

    private static void ValidateSqliteStoreSchema(
        SqliteConnection connection,
        string relativePath,
        int version)
    {
        if (!string.Equals(relativePath, "workspace.db", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var workspaceItemColumns = new List<string>
        {
            "id", "workspace_id", "parent_folder_id", "type", "title", "url", "note",
            "x", "y", "width", "height", "z_index", "created_at", "updated_at"
        };
        var sessionTabColumns = new List<string>
        {
            "id", "session_group_item_id", "url", "title", "source_tab_id", "sort_order", "saved_at"
        };
        if (version >= 1)
        {
            workspaceItemColumns.Add("container_profile_id");
            sessionTabColumns.Add("container_profile_id");
        }

        if (version >= 2)
        {
            workspaceItemColumns.AddRange(
            [
                "note_font_size", "favicon_png_base64", "thumbnail_png_base64", "thumbnail_updated_at",
                "youtube_mini_player_state_json", "target_path", "shell_item_kind", "open_in_folder_tab"
            ]);
            workspaceItemColumns.Add("deleted_at");
            workspaceItemColumns.Add("deletion_root_id");
            sessionTabColumns.AddRange(
            [
                "favicon_png_base64", "thumbnail_png_base64", "is_folder_tab", "split_group_id",
                "split_pane", "split_orientation", "split_ratio", "split_active_pane"
            ]);
        }

        var requiredColumns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspaces"] = ["id", "name", "sort_order", "created_at", "updated_at"],
            ["workspace_items"] = workspaceItemColumns,
            ["workspace_item_tags"] = ["item_id", "tag"],
            ["workspace_session_tabs"] = sessionTabColumns
        };

        foreach (var (tableName, columns) in requiredColumns)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{tableName}]);";
            var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                actualColumns.Add(reader.GetString(1));
            }

            if (!columns.All(actualColumns.Contains))
            {
                throw new InvalidDataException("Workspace DBの必須schemaが不足しています。");
            }
        }
    }

    private static void ValidateExtractedFile(
        string path,
        KitenBackupFileEntry file,
        KitenManagedFileDefinition definition,
        CancellationToken cancellationToken)
    {
        EnsureExistingPathHasNoReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length != file.Size
            || !string.Equals(
                ComputeSha256(path, cancellationToken),
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ファイルのサイズまたはSHA-256がmanifestと一致しません。");
        }

        var actualVersion = definition.Kind == KitenManagedFileKind.Sqlite
            ? ReadAndValidateSqlite(
                path,
                definition.MaximumFormatVersion,
                cancellationToken,
                definition.RelativePath)
            : ReadAndValidateJson(path, definition.MaximumFormatVersion, definition.RelativePath);
        if (actualVersion != file.FormatVersion)
        {
            throw new InvalidDataException("ファイルの形式versionがmanifestと一致しません。");
        }
    }

    private static KitenManagedFileDefinition ResolveDefinition(
        KitenBackupManifest manifest,
        string relativePath)
    {
        var fixedDefinition = GetFixedDefinitions(manifest)
            .FirstOrDefault(definition => string.Equals(
                definition.RelativePath,
                relativePath,
                StringComparison.OrdinalIgnoreCase));
        return fixedDefinition
            ?? new KitenManagedFileDefinition(relativePath, KitenManagedFileKind.Json, 2);
    }

    private static void ValidateFormatVersion(int? version, int? maximumVersion)
    {
        if (version is < 0)
        {
            throw new InvalidDataException("未対応の新しいデータ形式です。");
        }

        if (maximumVersion is { } maximum && version is { } actual && actual > maximum)
        {
            throw new KitenUnsupportedDataFormatException("未対応の新しいデータ形式です。");
        }
    }

    private static string VerifyArchiveHash(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return VerifyArchiveHash(archivePath, archiveStream, cancellationToken);
    }

    private static string VerifyArchiveHash(
        string archivePath,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var hashPath = archivePath + ".sha256";
        if (!File.Exists(hashPath))
        {
            throw new InvalidDataException("バックアップZIPのSHA-256ファイルがありません。");
        }

        EnsureExistingPathHasNoReparsePoint(hashPath);
        var hashFile = new FileInfo(hashPath);
        if (hashFile.Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("SHA-256ファイルが不正です。");
        }

        var parts = File.ReadAllText(hashPath, Encoding.UTF8)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !Sha256Pattern.IsMatch(parts[0]))
        {
            throw new InvalidDataException("SHA-256ファイルの形式が不正です。");
        }

        if (parts.Length == 2 && !string.Equals(parts[1], Path.GetFileName(archivePath), StringComparison.Ordinal))
        {
            throw new InvalidDataException("SHA-256ファイルが別のZIPを指しています。");
        }

        var actualHash = ComputeSha256(archiveStream, cancellationToken);
        if (!string.Equals(parts[0], actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("バックアップZIPのSHA-256が一致しません。");
        }

        return actualHash;
    }

    private static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        using var algorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            algorithm.AppendData(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(algorithm.GetHashAndReset());
    }

    private static void WriteArchiveHash(string path, string hash, string archiveFileName)
    {
        using var writer = new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(hash);
        writer.Write("  ");
        writer.Write(archiveFileName);
        writer.WriteLine();
        writer.Flush();
        ((FileStream)writer.BaseStream).Flush(flushToDisk: true);
    }

    private static void ValidateArchiveEntryName(string name)
    {
        if (string.Equals(name, ManifestEntryName, StringComparison.Ordinal))
        {
            return;
        }

        if (!name.StartsWith(DataEntryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("バックアップZIPに対象外の項目があります。");
        }

        ValidateRelativePath(name[DataEntryPrefix.Length..]);
    }

    private static bool IsLinkEntry(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
            || unixMode == unixSymbolicLink;
    }

    private static void AddFileToArchive(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = entry.Open();
        CopyWithLimit(input, output, MaximumSingleFileBytes, cancellationToken);
    }

    private static void CopyWithLimit(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("ファイルサイズが上限を超えています。");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(propertyName, out var element)
               && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static void ValidateSourceApplication(KitenSourceApplicationInformation source)
    {
        if (source is null
            || !IsSafeManifestScalar(source.PublicVersion, 100)
            || !IsSafeManifestScalar(source.SourceCommit, 100)
            || source.PublicVersion != "未取得" && !PublicVersionPattern.IsMatch(source.PublicVersion)
            || source.SourceCommit != "未取得" && !FullShaPattern.IsMatch(source.SourceCommit))
        {
            throw new InvalidDataException("元アプリの版情報が不正です。");
        }
    }

    private static bool IsSafeManifestScalar(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(static character => character is '\r' or '\n' or '\0');

    private static bool IsReservedWindowsName(string part)
    {
        var stem = Path.GetFileNameWithoutExtension(part);
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void DeleteOwnedFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
