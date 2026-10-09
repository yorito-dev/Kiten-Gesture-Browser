using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TabBrowser.Services.DataMaintenance;

public sealed class KitenDataMaintenanceService
{
    public const string BackupConfirmation = "BACKUP";
    public const string RestoreConfirmation = "RESTORE";
    public const string RollbackConfirmation = "ROLLBACK";

    private static readonly string[] SqliteCompanionSuffixes = [string.Empty, "-wal", "-shm", "-journal"];
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly KitenDataMaintenanceHooks _hooks;

    public KitenDataMaintenanceService()
        : this(new KitenDataMaintenanceHooks())
    {
    }

    internal KitenDataMaintenanceService(KitenDataMaintenanceHooks hooks)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public KitenDataMaintenanceResult PlanBackup(KitenBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
        {
            var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
            var destination = KitenDataMaintenanceArchive.NormalizeDestination(
                request.DestinationDirectory,
                dataRoot);
            EnsureNoInterruptedRestore(dataRoot);
            ProbeManagedFiles(dataRoot, request);
            var presentCount = CountPresentManagedFiles(dataRoot, request);
            var history = DescribeOptionalData(request);
            return new KitenDataMaintenanceResult(
                true,
                false,
                $"バックアップ計画: チャンネル={KitenDataMaintenanceArchive.ChannelName(request.Channel)}、データルート={dataRoot}、保存先={destination}、存在する対象={presentCount}件、{history}。ZIPは暗号化されません。");
        }));
    }

    public KitenDataMaintenanceResult CreateBackup(
        KitenBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Confirmation, BackupConfirmation, StringComparison.Ordinal))
        {
            return KitenDataMaintenanceResult.Failure(
                "バックアップは実行していません。確認文字列BACKUPが必要です。");
        }

        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
        {
            var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
            var destination = KitenDataMaintenanceArchive.NormalizeDestination(
                request.DestinationDirectory,
                dataRoot);
            EnsureNoInterruptedRestore(dataRoot);
            ProbeManagedFiles(dataRoot, request);
            var archivePath = CreateBackupCore(
                request.Channel,
                dataRoot,
                destination,
                request.ReleaseInformationPath,
                request.IncludeBrowsingHistory,
                request.IncludeRecentItems,
                request.IncludeDownloadHistory,
                cancellationToken);
            return new KitenDataMaintenanceResult(
                true,
                false,
                "バックアップを作成し、manifest・SHA-256・SQLite整合性を検証しました。ZIPは暗号化されていません。",
                archivePath);
        }));
    }

    public KitenDataMaintenanceResult PlanRestore(
        KitenRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
        {
            var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
            EnsureNoInterruptedRestore(dataRoot);
            var archivePath = KitenDataMaintenanceArchive.NormalizeArchivePath(request.BackupArchivePath);
            var validationRoot = CreateTemporaryMaintenanceRoot();
            try
            {
                var manifest = KitenDataMaintenanceArchive.ValidateAndExtractArchive(
                    archivePath,
                    validationRoot,
                    expectedArchiveSha256: null,
                    out var verifiedArchiveSha256,
                    cancellationToken);
                EnsureManifestChannel(manifest, request.Channel);
                var currentDataCorrupt = CurrentDataRequiresRawPreservation(
                    request.Channel,
                    dataRoot,
                    Path.Combine(validationRoot, "current-data-check"),
                    manifest,
                    cancellationToken);
                var targetSummary = DescribeRestoreTargets(dataRoot, manifest);
                var historySummary = DescribeManifestHistory(manifest);
                var preservationSummary = currentDataCorrupt
                    ? "現在データに内容破損があります。実行時は管理対象の原本を復旧記録領域のoriginal-snapshot.jsonとdataフォルダーへバイト単位で保全・照合してから置き換えます。"
                    : "現在データは書込前に別の検証済みバックアップへ退避します。";
                return new KitenDataMaintenanceResult(
                    true,
                    false,
                    $"復元計画: チャンネル={manifest.Channel}、バックアップ版={manifest.SourceApplication.PublicVersion}、復元時点={manifest.CreatedAtUtc:O}、データルート={dataRoot}、対象分類=基本対象、{historySummary}。{targetSummary} 実行すると、この時点より後の変更は戻りません。{preservationSummary}",
                    VerifiedArchiveSha256: verifiedArchiveSha256);
            }
            finally
            {
                DeleteTemporaryMaintenanceRoot(validationRoot);
            }
        }));
    }

    public KitenDataMaintenanceResult Restore(
        KitenRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Apply || !string.Equals(request.Confirmation, RestoreConfirmation, StringComparison.Ordinal))
        {
            return KitenDataMaintenanceResult.Failure(
                "復元は実行していません。計画確認後、--applyと確認文字列RESTOREが必要です。");
        }

        if (request.ExpectedArchiveSha256 is not { Length: 64 } expectedHash
            || expectedHash.Any(static character => !Uri.IsHexDigit(character)))
        {
            return KitenDataMaintenanceResult.Failure(
                "復元は実行していません。復元計画で確認した64桁のZIP SHA-256が必要です。");
        }

        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
            RestoreCore(request, cancellationToken)));
    }

    public KitenDataMaintenanceResult RollbackInterruptedRestore(
        KitenRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Confirmation, RollbackConfirmation, StringComparison.Ordinal))
        {
            return KitenDataMaintenanceResult.Failure(
                "中断復旧は実行していません。確認文字列ROLLBACKが必要です。");
        }

        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
        {
            var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
            var state = LoadRecoveryState(dataRoot, request.Channel);
            KitenPreparedRollbackSource? preparedSource = null;
            try
            {
                preparedSource = PrepareRollbackSource(state.Journal, cancellationToken);
                _hooks.Checkpoint?.Invoke("restore.rollback.after-source-validation");
                RollbackJournal(state.Journal, preparedSource, cancellationToken);
                ValidateRollbackAgainstPreRestoreState(state.Journal, preparedSource, cancellationToken);
                state.Journal.Status = "rolledBack";
                WriteJsonDurable(state.JournalPath, state.Journal);
                DeleteRestoreMarker(dataRoot);
                var restoredRawOriginal = IsRawPreservation(state.Journal);
                return new KitenDataMaintenanceResult(
                    true,
                    true,
                    restoredRawOriginal
                        ? "中断した復元を、開始前の破損原本へバイト単位で戻しました。利用可能な正常データになったことは確認していません。Kitenを起動せず、正常なバックアップから復元を再実行してください。破損原本と復旧記録は残しています。"
                        : "中断した復元を、検証済みの復元開始前状態へ戻しました。復旧記録は確認のため残しています。",
                    RecoveryPath: state.Journal.WorkRoot);
            }
            catch (Exception rollbackError)
            {
                _hooks.Failure?.Invoke(rollbackError);
                try
                {
                    state.Journal.Status = "rollbackFailed";
                    WriteJsonDurable(state.JournalPath, state.Journal);
                }
                catch
                {
                }

                return new KitenDataMaintenanceResult(
                    false,
                    true,
                    $"中断復旧は完了していません。Kitenを起動せず、同じROLLBACKを再実行してください。{ClassifyFailure(rollbackError)}",
                    RecoveryPath: state.Journal.WorkRoot);
            }
            finally
            {
                if (preparedSource is not null)
                {
                    DeleteTemporaryMaintenanceRoot(preparedSource.WorkRoot);
                }
            }
        }));
    }

    public KitenDataMaintenanceResult PlanRollbackInterruptedRestore(
        KitenRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSafely(() => WithChannelMutex(request.Channel, () =>
        {
            var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
            var state = LoadRecoveryState(dataRoot, request.Channel);
            var preparedSource = PrepareRollbackSource(state.Journal, cancellationToken);
            DeleteTemporaryMaintenanceRoot(preparedSource.WorkRoot);
            var stateDescription = IsRawPreservation(state.Journal)
                ? $"破損原本={state.Journal.PreRestoreBackupPath}。ROLLBACKは開始前の破損状態へ戻すだけで、正常利用可能とは確認しません"
                : $"復元前バックアップ={state.Journal.PreRestoreBackupPath}";
            return new KitenDataMaintenanceResult(
                true,
                false,
                $"中断復旧計画: 操作ID={state.Journal.OperationId:N}、チャンネル={state.Journal.Channel}、データルート={state.Journal.DataRoot}、journal={state.JournalPath}、{stateDescription}、対象={state.Journal.Entries.Count}件。開始前の状態へ戻す場合だけROLLBACKを実行してください。",
                RecoveryPath: state.Journal.WorkRoot);
        }));
    }

    private KitenDataMaintenanceResult RestoreCore(
        KitenRestoreRequest request,
        CancellationToken cancellationToken)
    {
        var dataRoot = KitenDataMaintenanceArchive.NormalizeDataRoot(request.DataRoot, request.Channel);
        EnsureNoInterruptedRestore(dataRoot);
        var archivePath = KitenDataMaintenanceArchive.NormalizeArchivePath(request.BackupArchivePath);
        var workRoot = CreateRestoreWorkRoot(dataRoot, request.Channel);
        var packageRoot = Path.Combine(workRoot, "package");
        var markerWritten = false;
        KitenRestoreJournal? journal = null;
        var journalPath = Path.Combine(workRoot, "restore-journal.json");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = KitenDataMaintenanceArchive.ValidateAndExtractArchive(
                archivePath,
                packageRoot,
                request.ExpectedArchiveSha256,
                out _,
                cancellationToken);
            EnsureManifestChannel(manifest, request.Channel);
            ProbeManagedFiles(dataRoot, manifest);

            var preRestoreDirectory = Path.Combine(workRoot, "pre-restore");
            string preRestoreBackupPath;
            var preRestoreStateKind = KitenPreRestoreStateKind.VerifiedBackup;
            KitenRawPreservationManifest? rawPreservation = null;
            try
            {
                preRestoreBackupPath = CreateBackupCore(
                    request.Channel,
                    dataRoot,
                    preRestoreDirectory,
                    releaseInformationPath: null,
                    manifest.IncludesBrowsingHistory,
                    manifest.IncludesRecentItems,
                    manifest.IncludesDownloadHistory,
                    cancellationToken);
            }
            catch (KitenCurrentDataCorruptionException)
            {
                var rawResult = CreateRawPreservation(
                    request.Channel,
                    dataRoot,
                    workRoot,
                    manifest,
                    cancellationToken);
                preRestoreBackupPath = rawResult.ManifestPath;
                rawPreservation = rawResult.Manifest;
                preRestoreStateKind = KitenPreRestoreStateKind.RawPreservation;
            }
            _hooks.Checkpoint?.Invoke("restore.after-pre-restore-backup");

            journal = new KitenRestoreJournal
            {
                OperationId = _hooks.NewGuid(),
                Channel = KitenDataMaintenanceArchive.ChannelName(request.Channel),
                DataRoot = dataRoot,
                BackupArchivePath = archivePath,
                PreRestoreBackupPath = preRestoreBackupPath,
                PreRestoreStateKind = preRestoreStateKind,
                WorkRoot = workRoot
            };
            WriteJsonDurable(journalPath, journal);
            WriteRestoreMarker(dataRoot, journalPath, journal);
            markerWritten = true;
            _hooks.Checkpoint?.Invoke("restore.after-marker");

            ApplyManifest(
                dataRoot,
                Path.Combine(packageRoot, "data"),
                manifest,
                journalPath,
                journal,
                rawPreservation,
                cancellationToken);
            _hooks.Checkpoint?.Invoke("restore.before-final-validation");
            KitenDataMaintenanceArchive.ValidateDataRootAgainstManifest(dataRoot, manifest, cancellationToken);

            journal.Status = "completed";
            WriteJsonDurable(journalPath, journal);
            DeleteRestoreMarker(dataRoot);
            markerWritten = false;
            _hooks.Checkpoint?.Invoke("restore.before-package-cleanup");
            // 復元確定後の展開領域削除は後始末であり、失敗しても復元自体を失敗扱いにしない。
            // 復元前バックアップとjournalを含む作業領域は、利用者が確認できるよう元々残す。
            TryDeleteOwnedDirectory(packageRoot, workRoot);
            return new KitenDataMaintenanceResult(
                true,
                true,
                IsRawPreservation(journal)
                    ? "正常なバックアップを検証し、破損していた現在データの原本をバイト単位で保全・照合してから復元しました。破損原本とjournalは確認のため残しています。"
                    : "バックアップを検証し、現在データを検証済みバックアップへ退避して復元しました。復元前退避とjournalは確認のため残しています。",
                OutputPath: preRestoreBackupPath,
                RecoveryPath: workRoot);
        }
        catch (Exception restoreError)
        {
            _hooks.Failure?.Invoke(restoreError);
            if (!markerWritten || journal is null)
            {
                TryDeleteRestoreWorkRoot(workRoot, dataRoot);
                throw;
            }

            if (restoreError is KitenSimulatedInterruptionException)
            {
                journal.Status = "interrupted";
                WriteJsonDurable(journalPath, journal);
                return new KitenDataMaintenanceResult(
                    false,
                    true,
                    "復元が中断された状態を保持しました。Kitenを起動せず、復旧ツールでROLLBACKを実行してください。",
                    RecoveryPath: workRoot);
            }

            try
            {
                journal.Status = "rollingBack";
                WriteJsonDurable(journalPath, journal);
                var preparedSource = PrepareRollbackSource(journal, CancellationToken.None);
                try
                {
                    _hooks.Checkpoint?.Invoke("restore.rollback.after-source-validation");
                    RollbackJournal(journal, preparedSource, CancellationToken.None);
                    ValidateRollbackAgainstPreRestoreState(
                        journal,
                        preparedSource,
                        CancellationToken.None);
                }
                finally
                {
                    DeleteTemporaryMaintenanceRoot(preparedSource.WorkRoot);
                }
                journal.Status = "rolledBack";
                WriteJsonDurable(journalPath, journal);
                DeleteRestoreMarker(dataRoot);
                return new KitenDataMaintenanceResult(
                    false,
                    false,
                    IsRawPreservation(journal)
                        ? $"復元に失敗したため、開始前の破損原本へバイト単位で戻しました。正常利用可能とは確認していません。Kitenを起動せず正常なバックアップから再試行してください。{ClassifyFailure(restoreError)}"
                        : $"復元に失敗したため、検証済みの開始前状態へ戻しました。{ClassifyFailure(restoreError)}",
                    RecoveryPath: workRoot);
            }
            catch (Exception rollbackError)
            {
                _hooks.Failure?.Invoke(rollbackError);
                try
                {
                    journal.Status = "rollbackFailed";
                    WriteJsonDurable(journalPath, journal);
                }
                catch
                {
                }

                return new KitenDataMaintenanceResult(
                    false,
                    true,
                    $"復元と自動巻き戻しに失敗しました。Kitenを起動せず、復旧ツールでROLLBACKを実行してください。{ClassifyFailure(rollbackError)}",
                    RecoveryPath: workRoot);
            }
        }
    }

    private string CreateBackupCore(
        ApplicationChannel channel,
        string dataRoot,
        string destinationDirectory,
        string? releaseInformationPath,
        bool includeBrowsingHistory,
        bool includeRecentItems,
        bool includeDownloadHistory,
        CancellationToken cancellationToken)
    {
        var destination = KitenDataMaintenanceArchive.NormalizeDestination(destinationDirectory, dataRoot);
        Directory.CreateDirectory(destination);
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(destination);
        var operationId = _hooks.NewGuid();
        VerifyDestinationWritable(destination, operationId);
        var createdAt = _hooks.UtcNow().ToUniversalTime();
        var channelName = KitenDataMaintenanceArchive.ChannelName(channel);
        var operationSuffix = operationId.ToString("N")[..8];
        var archiveFileName = $"Kiten-DataBackup-{channelName}-{createdAt:yyyyMMdd'T'HHmmssfff'Z'}-{operationSuffix}.zip";
        var stageRoot = CreateTemporaryMaintenanceRoot();
        string? verificationRoot = null;
        string? archivePath = null;
        string? provisionalArchivePath = null;
        try
        {
            verificationRoot = CreateTemporaryMaintenanceRoot();
            var manifest = CollectBackup(
                channel,
                dataRoot,
                stageRoot,
                releaseInformationPath,
                includeBrowsingHistory,
                includeRecentItems,
                includeDownloadHistory,
                createdAt,
                cancellationToken);
            KitenDataMaintenanceArchive.ValidateManifest(manifest);
            var manifestPath = Path.Combine(stageRoot, "backup-info.json");
            KitenDataMaintenanceArchive.WriteManifest(manifestPath, manifest);
            KitenDataMaintenanceArchive.ValidateArchiveCreationLimits(manifest, manifestPath);
            VerifyRemainingCapacity(destination, manifest);
            _hooks.Checkpoint?.Invoke("backup.before-archive-commit");
            provisionalArchivePath = KitenDataMaintenanceArchive.CreateProvisionalArchive(
                stageRoot,
                manifest,
                destination,
                archiveFileName,
                cancellationToken);
            var verified = KitenDataMaintenanceArchive.ValidateAndExtractArchive(
                provisionalArchivePath,
                verificationRoot,
                cancellationToken);
            if (!string.Equals(verified.Channel, channelName, StringComparison.Ordinal)
                || verified.Files.Count != manifest.Files.Count)
            {
                throw new InvalidDataException("作成後のバックアップ検証結果が一致しません。");
            }

            _hooks.Checkpoint?.Invoke("backup.after-provisional-archive-validation");
            archivePath = KitenDataMaintenanceArchive.CommitVerifiedArchive(
                provisionalArchivePath,
                archiveFileName,
                cancellationToken);
            provisionalArchivePath = null;
            _hooks.Checkpoint?.Invoke("backup.after-archive-commit");

            return archivePath;
        }
        catch
        {
            if (archivePath is not null)
            {
                DeleteCreatedBackup(archivePath);
            }

            if (provisionalArchivePath is not null)
            {
                DeleteCreatedBackup(provisionalArchivePath);
            }

            throw;
        }
        finally
        {
            DeleteTemporaryMaintenanceRoot(stageRoot);
            if (verificationRoot is not null)
            {
                DeleteTemporaryMaintenanceRoot(verificationRoot);
            }
        }
    }

    private static void VerifyDestinationWritable(string destination, Guid operationId)
    {
        var probePath = Path.Combine(destination, $".write-probe-{operationId:N}.tmp");
        try
        {
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static void VerifyRemainingCapacity(string destination, KitenBackupManifest manifest)
    {
        const long safetyMargin = 16L * 1024 * 1024;
        var expandedBytes = manifest.Files.Where(static file => file.Present).Sum(static file => file.Size);
        var requiredBytes = checked((expandedBytes * 2) + safetyMargin);
        long? availableBytes = null;
        try
        {
            var root = Path.GetPathRoot(destination);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    availableBytes = drive.AvailableFreeSpace;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            // 空き容量を取得できない保存先では、後続の検証付き書込み結果を正とする。
        }

        if (availableBytes is { } available && available < requiredBytes)
        {
            throw new IOException("バックアップZIPと検証用展開領域の空き容量が不足しています。");
        }
    }

    private KitenBackupManifest CollectBackup(
        ApplicationChannel channel,
        string dataRoot,
        string stageRoot,
        string? releaseInformationPath,
        bool includeBrowsingHistory,
        bool includeRecentItems,
        bool includeDownloadHistory,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken,
        bool reportCheckpoint = true)
    {
        var manifest = new KitenBackupManifest
        {
            Channel = KitenDataMaintenanceArchive.ChannelName(channel),
            CreatedAtUtc = createdAt,
            Encrypted = false,
            IncludesBrowsingHistory = includeBrowsingHistory,
            IncludesRecentItems = includeRecentItems,
            IncludesDownloadHistory = includeDownloadHistory,
            SourceApplication = KitenDataMaintenanceArchive.ReadSourceApplication(releaseInformationPath)
        };
        var stagedDataRoot = Path.Combine(stageRoot, "data");
        var contentCorruptions = new List<KitenCurrentDataCorruptionException>();

        foreach (var definition in KitenDataMaintenanceArchive.GetFixedDefinitions(
                     includeBrowsingHistory,
                     includeRecentItems,
                     includeDownloadHistory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, definition.RelativePath);
            if (!File.Exists(sourcePath))
            {
                manifest.Files.Add(KitenDataMaintenanceArchive.CreateFileEntry(
                    definition.RelativePath,
                    present: false,
                    definition.Kind,
                    stagedPath: null,
                    formatVersion: null));
                continue;
            }

            if (new FileInfo(sourcePath).Length > KitenDataMaintenanceArchive.MaximumSingleFileBytes)
            {
                throw new InvalidDataException("バックアップ対象ファイルがサイズ上限を超えています。");
            }

            var stagedPath = KitenDataMaintenanceArchive.ResolveManagedPath(stagedDataRoot, definition.RelativePath);
            int? formatVersion;
            try
            {
                formatVersion = CopyAndValidateCurrentDataFile(
                    sourcePath, stagedPath, definition, cancellationToken);
            }
            catch (KitenCurrentDataCorruptionException exception)
            {
                contentCorruptions.Add(exception);
                continue;
            }
            manifest.Files.Add(KitenDataMaintenanceArchive.CreateFileEntry(
                definition.RelativePath,
                present: true,
                definition.Kind,
                stagedPath,
                formatVersion,
                cancellationToken));
        }

        foreach (var collectionPath in KitenDataMaintenanceArchive.ManagedCollectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirectory = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, collectionPath);
            var present = Directory.Exists(sourceDirectory);
            manifest.Collections.Add(new KitenBackupCollectionEntry
            {
                RelativePath = collectionPath,
                Present = present
            });
            if (!present)
            {
                continue;
            }

            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourceDirectory);
            var entries = Directory.EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var sourcePath in entries)
            {
                if (File.Exists(sourcePath)
                    && KitenDataMaintenanceArchive.IsKnownAtomicJsonTemporaryFile(sourcePath))
                {
                    continue;
                }

                if (!File.Exists(sourcePath)
                    || !string.Equals(Path.GetExtension(sourcePath), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("セッション復元点フォルダーに未対応の項目があります。");
                }

                var relativePath = collectionPath + "/" + Path.GetFileName(sourcePath);
                KitenDataMaintenanceArchive.ValidateRelativePath(relativePath);
                var definition = new KitenManagedFileDefinition(relativePath, KitenManagedFileKind.Json, 2);
                var stagedPath = KitenDataMaintenanceArchive.ResolveManagedPath(stagedDataRoot, relativePath);
                int? formatVersion;
                try
                {
                    formatVersion = CopyAndValidateCurrentDataFile(
                        sourcePath, stagedPath, definition, cancellationToken);
                }
                catch (KitenCurrentDataCorruptionException exception)
                {
                    contentCorruptions.Add(exception);
                    continue;
                }
                manifest.Files.Add(KitenDataMaintenanceArchive.CreateFileEntry(
                    relativePath,
                    present: true,
                    KitenManagedFileKind.Json,
                    stagedPath,
                    formatVersion,
                    cancellationToken));
            }
        }

        if (manifest.Files.Count > KitenDataMaintenanceArchive.MaximumArchiveEntries)
        {
            throw new InvalidDataException("バックアップ対象の件数が上限を超えています。");
        }

        if (contentCorruptions.Count > 0)
        {
            throw new KitenCurrentDataCorruptionException(contentCorruptions);
        }

        manifest.Files = manifest.Files
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (reportCheckpoint)
        {
            _hooks.Checkpoint?.Invoke("backup.after-collect");
        }
        return manifest;
    }

    private int? CopyAndValidateCurrentDataFile(
        string sourcePath,
        string stagedPath,
        KitenManagedFileDefinition definition,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_hooks.CurrentDataValidationFailure?.Invoke(definition.RelativePath) is { } injectedFailure)
            {
                throw injectedFailure;
            }

            return KitenDataMaintenanceArchive.CopyAndValidateDataFile(
                sourcePath,
                stagedPath,
                definition,
                cancellationToken);
        }
        catch (KitenUnsupportedDataFormatException)
        {
            throw;
        }
        catch (KitenDataValidationException exception)
        {
            ClassifyCurrentDataValidationFailure(
                sourcePath,
                stagedPath + ".source-validation",
                definition,
                exception,
                cancellationToken);
            throw new InvalidOperationException("到達しない検証分岐です。");
        }
    }

    private static void ClassifyCurrentDataValidationFailure(
        string sourcePath,
        string sourceValidationPath,
        KitenManagedFileDefinition definition,
        KitenDataValidationException failure,
        CancellationToken cancellationToken)
    {
        if (!CanRepresentCurrentDataCorruption(failure.InnerException))
        {
            Rethrow(failure.InnerException ?? failure);
        }

        try
        {
            _ = KitenDataMaintenanceArchive.ValidateSourceDataFile(
                sourcePath,
                sourceValidationPath,
                definition,
                cancellationToken);
        }
        catch (KitenUnsupportedDataFormatException)
        {
            throw;
        }
        catch (KitenDataValidationException sourceFailure)
        {
            if (sourceFailure.Stage == KitenDataValidationStage.SourceRead
                && CanRepresentCurrentDataCorruption(sourceFailure.InnerException))
            {
                throw new KitenCurrentDataCorruptionException(
                    definition.RelativePath,
                    sourceFailure.InnerException ?? sourceFailure);
            }

            Rethrow(sourceFailure.InnerException ?? sourceFailure);
        }

        Rethrow(failure.InnerException ?? failure);
    }

    private static bool CanRepresentCurrentDataCorruption(Exception? exception)
    {
        if (exception is JsonException or InvalidDataException)
        {
            return true;
        }

        if (exception is not SqliteException sqliteException)
        {
            return false;
        }

        var primaryCode = GetSqlitePrimaryErrorCode(sqliteException);
        return primaryCode is 11 or 26; // SQLITE_CORRUPT / SQLITE_NOTADB
    }

    private static int GetSqlitePrimaryErrorCode(SqliteException exception)
    {
        var directPrimaryCode = exception.SqliteErrorCode & 0xFF;
        var extendedPrimaryCode = exception.SqliteExtendedErrorCode & 0xFF;
        return extendedPrimaryCode == 0 || extendedPrimaryCode == directPrimaryCode
            ? directPrimaryCode
            : -1;
    }

    private static void Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private bool CurrentDataRequiresRawPreservation(
        ApplicationChannel channel,
        string dataRoot,
        string stageRoot,
        KitenBackupManifest restoreManifest,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(stageRoot);
            _ = CollectBackup(
                channel,
                dataRoot,
                stageRoot,
                releaseInformationPath: null,
                restoreManifest.IncludesBrowsingHistory,
                restoreManifest.IncludesRecentItems,
                restoreManifest.IncludesDownloadHistory,
                _hooks.UtcNow().ToUniversalTime(),
                cancellationToken,
                reportCheckpoint: false);
            return false;
        }
        catch (KitenCurrentDataCorruptionException)
        {
            return true;
        }
    }

    private (KitenRawPreservationManifest Manifest, string ManifestPath) CreateRawPreservation(
        ApplicationChannel channel,
        string dataRoot,
        string workRoot,
        KitenBackupManifest restoreManifest,
        CancellationToken cancellationToken)
    {
        var preservationRoot = Path.Combine(workRoot, "raw-current");
        var preservationDataRoot = Path.Combine(preservationRoot, "data");
        Directory.CreateDirectory(preservationDataRoot);
        var manifest = new KitenRawPreservationManifest
        {
            Channel = KitenDataMaintenanceArchive.ChannelName(channel),
            CreatedAtUtc = _hooks.UtcNow().ToUniversalTime(),
            IncludesBrowsingHistory = restoreManifest.IncludesBrowsingHistory,
            IncludesRecentItems = restoreManifest.IncludesRecentItems,
            IncludesDownloadHistory = restoreManifest.IncludesDownloadHistory
        };

        foreach (var definition in KitenDataMaintenanceArchive.GetFixedDefinitions(restoreManifest))
        {
            var suffixes = definition.Kind == KitenManagedFileKind.Sqlite
                ? SqliteCompanionSuffixes
                : [string.Empty];
            foreach (var suffix in suffixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreserveRawFile(
                    dataRoot,
                    preservationDataRoot,
                    definition.RelativePath + suffix,
                    manifest,
                    cancellationToken);
            }
        }

        foreach (var collectionPath in KitenDataMaintenanceArchive.ManagedCollectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirectory = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, collectionPath);
            var present = Directory.Exists(sourceDirectory);
            manifest.Collections.Add(new KitenRawPreservedDirectoryEntry
            {
                RelativePath = collectionPath,
                Present = present
            });
            if (!present)
            {
                continue;
            }

            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourceDirectory);
            Directory.CreateDirectory(KitenDataMaintenanceArchive.ResolveManagedPath(
                preservationDataRoot,
                collectionPath));
            if (Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidDataException("セッション復元点フォルダーに未対応のサブフォルダーがあります。");
            }

            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(Path.GetExtension(sourcePath), ".json", StringComparison.OrdinalIgnoreCase)
                    && !KitenDataMaintenanceArchive.IsKnownAtomicJsonTemporaryFile(sourcePath))
                {
                    throw new InvalidDataException("セッション復元点フォルダーに未対応の項目があります。");
                }

                PreserveRawFile(
                    dataRoot,
                    preservationDataRoot,
                    collectionPath + "/" + Path.GetFileName(sourcePath),
                    manifest,
                    cancellationToken);
            }
        }

        manifest.Files = manifest.Files
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        manifest.Collections = manifest.Collections
            .OrderBy(static collection => collection.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var manifestPath = Path.Combine(preservationRoot, "original-snapshot.json");
        _hooks.Checkpoint?.Invoke("restore.raw-preservation.before-manifest");
        WriteJsonDurable(manifestPath, manifest);
        ValidateRawPreservationManifest(manifestPath, manifest.Channel, cancellationToken);
        _hooks.Checkpoint?.Invoke("restore.raw-preservation.after-validation");
        return (manifest, manifestPath);
    }

    private void PreserveRawFile(
        string dataRoot,
        string preservationDataRoot,
        string relativePath,
        KitenRawPreservationManifest manifest,
        CancellationToken cancellationToken)
    {
        KitenDataMaintenanceArchive.ValidateRelativePath(relativePath);
        var sourcePath = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, relativePath);
        if (!File.Exists(sourcePath))
        {
            manifest.Files.Add(new KitenRawPreservedFileEntry
            {
                RelativePath = relativePath,
                Present = false
            });
            return;
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length > KitenDataMaintenanceArchive.MaximumSingleFileBytes)
        {
            throw new InvalidDataException("原本保全対象ファイルがサイズ上限を超えています。");
        }

        _hooks.Checkpoint?.Invoke("restore.raw-preservation.before-file:" + relativePath);
        var destinationPath = KitenDataMaintenanceArchive.ResolveManagedPath(preservationDataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            destination.Flush(flushToDisk: true);
        }

        var preservedInfo = new FileInfo(destinationPath);
        manifest.Files.Add(new KitenRawPreservedFileEntry
        {
            RelativePath = relativePath,
            Present = true,
            Size = preservedInfo.Length,
            Sha256 = KitenDataMaintenanceArchive.ComputeSha256(destinationPath, cancellationToken)
        });
        _hooks.Checkpoint?.Invoke("restore.raw-preservation.after-file:" + relativePath);
    }

    private static KitenRawPreservationManifest ValidateRawPreservationManifest(
        string manifestPath,
        string expectedChannel,
        CancellationToken cancellationToken)
    {
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(manifestPath);
        var manifest = ReadJsonFile<KitenRawPreservationManifest>(manifestPath, maximumBytes: 2 * 1024 * 1024);
        if (manifest.SchemaVersion != 1
            || manifest.Channel != expectedChannel
            || manifest.Files.Count > KitenDataMaintenanceArchive.MaximumArchiveEntries)
        {
            throw new InvalidDataException("破損原本の保全記録が不正です。");
        }

        var expectedCollections = KitenDataMaintenanceArchive.ManagedCollectionPaths
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in manifest.Collections)
        {
            KitenDataMaintenanceArchive.ValidateRelativePath(collection.RelativePath);
            if (!actualCollections.Add(collection.RelativePath)
                || !expectedCollections.Contains(collection.RelativePath))
            {
                throw new InvalidDataException("破損原本の保全記録に対象外または重複したフォルダーがあります。");
            }
        }

        if (!actualCollections.SetEquals(expectedCollections))
        {
            throw new InvalidDataException("破損原本の保全記録に必要なフォルダー情報がありません。");
        }

        var definitions = KitenDataMaintenanceArchive.GetFixedDefinitions(
                manifest.IncludesBrowsingHistory,
                manifest.IncludesRecentItems,
                manifest.IncludesDownloadHistory)
            .ToDictionary(static definition => definition.RelativePath, StringComparer.OrdinalIgnoreCase);
        var expectedFixedPaths = definitions.Values
            .SelectMany(definition => definition.Kind == KitenManagedFileKind.Sqlite
                ? SqliteCompanionSuffixes.Select(suffix => definition.RelativePath + suffix)
                : [definition.RelativePath])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        var preservationRoot = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("破損原本の保全先を確認できません。");
        var preservationDataRoot = Path.Combine(preservationRoot, "data");
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KitenDataMaintenanceArchive.ValidateRelativePath(file.RelativePath);
            if (!seenFiles.Add(file.RelativePath) || !IsAllowedRawPreservationPath(file.RelativePath, definitions))
            {
                throw new InvalidDataException("破損原本の保全記録に対象外または重複したファイルがあります。");
            }

            var path = KitenDataMaintenanceArchive.ResolveManagedPath(preservationDataRoot, file.RelativePath);
            ValidateRawFileAtPath(file, path, cancellationToken);
            if (file.Present)
            {
                totalSize = checked(totalSize + file.Size);
                if (totalSize > KitenDataMaintenanceArchive.MaximumExpandedBytes)
                {
                    throw new InvalidDataException("破損原本の保全サイズが上限を超えています。");
                }
            }
        }

        if (!expectedFixedPaths.IsSubsetOf(seenFiles))
        {
            throw new InvalidDataException("破損原本の保全記録に必要な管理対象ファイル情報がありません。");
        }

        foreach (var collection in manifest.Collections)
        {
            var directory = KitenDataMaintenanceArchive.ResolveManagedPath(
                preservationDataRoot,
                collection.RelativePath);
            var collectionFiles = manifest.Files
                .Where(file => file.RelativePath.StartsWith(
                    collection.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!collection.Present && collectionFiles.Count != 0)
            {
                throw new InvalidDataException("存在しない破損原本フォルダーにファイル情報があります。");
            }

            if (Directory.Exists(directory) != collection.Present)
            {
                throw new InvalidDataException("破損原本フォルダーの存在状態が保全記録と一致しません。");
            }

            if (!collection.Present)
            {
                continue;
            }

            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(directory);
            var expectedFiles = collectionFiles
                .Where(static file => file.Present)
                .Select(static file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => collection.RelativePath + "/" + Path.GetFileName(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedFiles.SetEquals(actualFiles)
                || Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidDataException("破損原本フォルダーの内容が保全記録と一致しません。");
            }
        }

        return manifest;
    }

    private static bool IsAllowedRawPreservationPath(
        string relativePath,
        IReadOnlyDictionary<string, KitenManagedFileDefinition> definitions)
    {
        if (definitions.ContainsKey(relativePath))
        {
            return true;
        }

        if (SqliteCompanionSuffixes.Skip(1).Any(suffix =>
                relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && definitions.TryGetValue(relativePath[..^suffix.Length], out var definition)
                && definition.Kind == KitenManagedFileKind.Sqlite))
        {
            return true;
        }

        return KitenDataMaintenanceArchive.ManagedCollectionPaths.Any(collection =>
        {
            if (!relativePath.StartsWith(collection + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fileName = relativePath[(collection.Length + 1)..];
            return !fileName.Contains('/')
                   && (string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase)
                       || KitenDataMaintenanceArchive.IsKnownAtomicJsonTemporaryFile(fileName));
        });
    }

    private static void ValidateRawFileAtPath(
        KitenRawPreservedFileEntry file,
        string path,
        CancellationToken cancellationToken)
    {
        if (!file.Present)
        {
            if (File.Exists(path) || Directory.Exists(path) || file.Size != 0 || !string.IsNullOrEmpty(file.Sha256))
            {
                throw new InvalidDataException("破損原本ファイルの不存在情報が一致しません。");
            }

            return;
        }

        if (!File.Exists(path)
            || file.Size < 0
            || file.Size > KitenDataMaintenanceArchive.MaximumSingleFileBytes
            || file.Sha256.Length != 64)
        {
            throw new InvalidDataException("破損原本ファイルの基本情報が不正です。");
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length != file.Size
            || !string.Equals(
                KitenDataMaintenanceArchive.ComputeSha256(path, cancellationToken),
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("破損原本ファイルのサイズまたはSHA-256が一致しません。");
        }
    }

    private static void ValidateRawTargetMatchesCurrent(
        string dataRoot,
        KitenRawPreservationManifest manifest,
        string relativePath,
        string targetKind,
        CancellationToken cancellationToken)
    {
        if (targetKind == "directory")
        {
            var collection = manifest.Collections.Single(item => string.Equals(
                item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            var targetDirectory = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, relativePath);
            if (Directory.Exists(targetDirectory) != collection.Present)
            {
                throw new IOException("原本保全後に現在データのフォルダー存在状態が変わりました。");
            }

            if (!collection.Present)
            {
                return;
            }

            var expectedFiles = manifest.Files
                .Where(file => file.Present && file.RelativePath.StartsWith(
                    relativePath + "/", StringComparison.OrdinalIgnoreCase))
                .Select(static file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => relativePath + "/" + Path.GetFileName(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedFiles.SetEquals(actualFiles)
                || Directory.EnumerateDirectories(targetDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new IOException("原本保全後に現在データのフォルダー内容が変わりました。");
            }

            foreach (var file in manifest.Files.Where(file => file.RelativePath.StartsWith(
                         relativePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                ValidateRawFileAtPath(
                    file,
                    KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, file.RelativePath),
                    cancellationToken);
            }

            return;
        }

        var paths = targetKind == "sqlite"
            ? SqliteCompanionSuffixes.Select(suffix => relativePath + suffix)
            : [relativePath];
        foreach (var path in paths)
        {
            var file = manifest.Files.Single(item => string.Equals(
                item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            ValidateRawFileAtPath(
                file,
                KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, path),
                cancellationToken);
        }
    }

    private static void ValidateCurrentDataRootAgainstRawPreservation(
        string dataRoot,
        KitenRawPreservationManifest manifest,
        CancellationToken cancellationToken)
    {
        var definitions = KitenDataMaintenanceArchive.GetFixedDefinitions(
            manifest.IncludesBrowsingHistory,
            manifest.IncludesRecentItems,
            manifest.IncludesDownloadHistory);
        foreach (var definition in definitions)
        {
            if (manifest.Files.Any(file => string.Equals(
                    file.RelativePath, definition.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                ValidateRawTargetMatchesCurrent(
                    dataRoot,
                    manifest,
                    definition.RelativePath,
                    definition.Kind == KitenManagedFileKind.Sqlite ? "sqlite" : "file",
                    cancellationToken);
            }
        }

        foreach (var collection in manifest.Collections)
        {
            ValidateRawTargetMatchesCurrent(
                dataRoot,
                manifest,
                collection.RelativePath,
                "directory",
                cancellationToken);
        }
    }

    private void ApplyManifest(
        string dataRoot,
        string packageDataRoot,
        KitenBackupManifest manifest,
        string journalPath,
        KitenRestoreJournal journal,
        KitenRawPreservationManifest? rawPreservation,
        CancellationToken cancellationToken)
    {
        var fixedDefinitions = KitenDataMaintenanceArchive.GetFixedDefinitions(manifest);
        foreach (var definition in fixedDefinitions)
        {
            var file = manifest.Files.Single(item => string.Equals(
                item.RelativePath,
                definition.RelativePath,
                StringComparison.OrdinalIgnoreCase));
            ApplyTarget(
                dataRoot,
                packageDataRoot,
                file.RelativePath,
                definition.Kind == KitenManagedFileKind.Sqlite ? "sqlite" : "file",
                file.Present,
                journalPath,
                journal,
                rawPreservation,
                cancellationToken);
        }

        foreach (var collection in manifest.Collections)
        {
            ApplyTarget(
                dataRoot,
                packageDataRoot,
                collection.RelativePath,
                "directory",
                collection.Present,
                journalPath,
                journal,
                rawPreservation,
                cancellationToken);
        }
    }

    private void ApplyTarget(
        string dataRoot,
        string packageDataRoot,
        string relativePath,
        string targetKind,
        bool sourcePresent,
        string journalPath,
        KitenRestoreJournal journal,
        KitenRawPreservationManifest? rawPreservation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hooks.Checkpoint?.Invoke("restore.before-target:" + relativePath);
        var targetPath = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, relativePath);
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(Path.GetDirectoryName(targetPath)!);
        var rollbackPath = KitenDataMaintenanceArchive.ResolveManagedPath(
            GetPreservedDataRoot(journal),
            relativePath);
        var originalPresent = targetKind == "directory"
            ? Directory.Exists(targetPath)
            : targetKind == "sqlite"
                ? SqliteCompanionSuffixes.Any(suffix => File.Exists(targetPath + suffix))
                : File.Exists(targetPath);
        var entry = new KitenRestoreJournalEntry
        {
            RelativePath = relativePath,
            TargetKind = targetKind,
            OriginalPresent = originalPresent,
            SourcePresent = sourcePresent
        };
        journal.Entries.Add(entry);
        journal.Status = "applying";
        WriteJsonDurable(journalPath, journal);

        if (rawPreservation is null)
        {
            SecureOriginalTarget(targetPath, rollbackPath, targetKind);
        }
        else
        {
            ValidateRawTargetMatchesCurrent(dataRoot, rawPreservation, relativePath, targetKind, cancellationToken);
            RemoveOriginalTarget(targetPath, targetKind);
        }
        entry.State = "secured";
        WriteJsonDurable(journalPath, journal);
        cancellationToken.ThrowIfCancellationRequested();

        if (sourcePresent)
        {
            var sourcePath = KitenDataMaintenanceArchive.ResolveManagedPath(packageDataRoot, relativePath);
            if (targetKind == "directory")
            {
                KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                CopyFileDurable(sourcePath, targetPath, journal.OperationId, cancellationToken);
            }
        }

        entry.State = "applied";
        WriteJsonDurable(journalPath, journal);
        _hooks.Checkpoint?.Invoke("restore.after-target:" + relativePath);
    }

    private static void SecureOriginalTarget(string targetPath, string rollbackPath, string targetKind)
    {
        if (targetKind == "directory")
        {
            if (!Directory.Exists(targetPath))
            {
                return;
            }

            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
            Directory.Move(targetPath, rollbackPath);
            return;
        }

        if (targetKind == "sqlite")
        {
            foreach (var suffix in SqliteCompanionSuffixes)
            {
                SecureOriginalFile(targetPath + suffix, rollbackPath + suffix);
            }

            return;
        }

        SecureOriginalFile(targetPath, rollbackPath);
    }

    private static void RemoveOriginalTarget(string targetPath, string targetKind)
    {
        if (targetKind == "directory")
        {
            if (Directory.Exists(targetPath))
            {
                KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(targetPath);
                Directory.Delete(targetPath, recursive: true);
            }

            return;
        }

        if (targetKind == "sqlite")
        {
            foreach (var suffix in SqliteCompanionSuffixes)
            {
                if (File.Exists(targetPath + suffix))
                {
                    File.Delete(targetPath + suffix);
                }
            }

            return;
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }
    }

    private static void SecureOriginalFile(string targetPath, string rollbackPath)
    {
        if (!File.Exists(targetPath))
        {
            return;
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
        File.Move(targetPath, rollbackPath);
    }

    private static void CopyFileDurable(
        string sourcePath,
        string targetPath,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = targetPath + $".{operationId:N}.restoretmp";
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, targetPath);
    }

    private void RollbackJournal(
        KitenRestoreJournal journal,
        KitenPreparedRollbackSource preparedSource,
        CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        var failedRoot = Path.Combine(journal.WorkRoot, "failed-restored");
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.State == "rolledBack")
            {
                continue;
            }

            _hooks.Checkpoint?.Invoke("restore.rollback.before-target:" + entry.RelativePath);

            var targetPath = KitenDataMaintenanceArchive.ResolveManagedPath(journal.DataRoot, entry.RelativePath);
            var rollbackPath = KitenDataMaintenanceArchive.ResolveManagedPath(
                preparedSource.DataRoot,
                entry.RelativePath);
            var failedPath = KitenDataMaintenanceArchive.ResolveManagedPath(failedRoot, entry.RelativePath);
            if (!preparedSource.RestoreEntries.Contains(entry.RelativePath))
            {
                entry.State = "rolledBack";
                WriteJsonDurable(Path.Combine(journal.WorkRoot, "restore-journal.json"), journal);
                continue;
            }

            var journalPath = Path.Combine(journal.WorkRoot, "restore-journal.json");
            if (entry.State is not ("securingFailedTarget" or "failedTargetSecured" or "restoringOriginal"))
            {
                entry.State = "securingFailedTarget";
                WriteJsonDurable(journalPath, journal);
            }

            if (entry.State == "securingFailedTarget")
            {
                MoveFailedRestoredTarget(targetPath, failedPath, entry.TargetKind, journal.OperationId);
                _hooks.Checkpoint?.Invoke("restore.rollback.after-secure-target:" + entry.RelativePath);
                entry.State = "failedTargetSecured";
                WriteJsonDurable(journalPath, journal);
            }

            if (entry.State == "failedTargetSecured")
            {
                entry.State = "restoringOriginal";
                WriteJsonDurable(journalPath, journal);
            }

            if (IsRawPreservation(journal))
            {
                ResetRawRollbackTarget(targetPath, entry.TargetKind, journal.OperationId);
            }

            RestoreOriginalTarget(
                rollbackPath,
                targetPath,
                entry.TargetKind,
                copyPreservedOriginal: true,
                journal.OperationId,
                cancellationToken);
            _hooks.Checkpoint?.Invoke("restore.rollback.after-restore-original:" + entry.RelativePath);
            EnsureOriginalTargetState(targetPath, entry);
            entry.State = "rolledBack";
            WriteJsonDurable(journalPath, journal);
        }
    }

    private KitenPreparedRollbackSource PrepareRollbackSource(
        KitenRestoreJournal journal,
        CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        var workRoot = CreateTemporaryMaintenanceRoot();
        try
        {
            return IsRawPreservation(journal)
                ? PrepareRawRollbackSource(journal, workRoot, cancellationToken)
                : PrepareVerifiedRollbackSource(journal, workRoot, cancellationToken);
        }
        catch
        {
            DeleteTemporaryMaintenanceRoot(workRoot);
            throw;
        }
    }

    private KitenPreparedRollbackSource PrepareRawRollbackSource(
        KitenRestoreJournal journal,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var manifest = ValidateRawPreservationManifest(
            journal.PreRestoreBackupPath,
            journal.Channel,
            cancellationToken);
        var sourceDataRoot = GetPreservedDataRoot(journal);
        var preparedPreservationRoot = Path.Combine(workRoot, "raw-current");
        var preparedDataRoot = Path.Combine(preparedPreservationRoot, "data");
        Directory.CreateDirectory(preparedDataRoot);
        var copyOperationId = _hooks.NewGuid();
        CopyFileDurable(
            journal.PreRestoreBackupPath,
            Path.Combine(preparedPreservationRoot, "original-snapshot.json"),
            copyOperationId,
            cancellationToken);
        foreach (var collection in manifest.Collections.Where(static item => item.Present))
        {
            Directory.CreateDirectory(KitenDataMaintenanceArchive.ResolveManagedPath(
                preparedDataRoot,
                collection.RelativePath));
        }

        foreach (var file in manifest.Files.Where(static item => item.Present))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFileDurable(
                KitenDataMaintenanceArchive.ResolveManagedPath(sourceDataRoot, file.RelativePath),
                KitenDataMaintenanceArchive.ResolveManagedPath(preparedDataRoot, file.RelativePath),
                copyOperationId,
                cancellationToken);
        }

        _ = ValidateRawPreservationManifest(
            Path.Combine(preparedPreservationRoot, "original-snapshot.json"),
            journal.Channel,
            cancellationToken);
        return new KitenPreparedRollbackSource(
            workRoot,
            preparedDataRoot,
            journal.Entries
                .Where(static entry => entry.State != "rolledBack")
                .Select(static entry => entry.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private KitenPreparedRollbackSource PrepareVerifiedRollbackSource(
        KitenRestoreJournal journal,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var expectedRoot = Path.Combine(workRoot, "expected");
        var manifest = KitenDataMaintenanceArchive.ValidateAndExtractArchive(
            journal.PreRestoreBackupPath,
            expectedRoot,
            cancellationToken);
        if (!string.Equals(manifest.Channel, journal.Channel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("復元前バックアップのチャンネルが復旧記録と一致しません。");
        }

        var preparedDataRoot = Path.Combine(workRoot, "data");
        Directory.CreateDirectory(preparedDataRoot);
        var originalRoot = Path.Combine(journal.WorkRoot, "original");
        var restoreEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copyOperationId = _hooks.NewGuid();
        foreach (var definition in KitenDataMaintenanceArchive.GetFixedDefinitions(manifest))
        {
            var expected = manifest.Files.Single(file => string.Equals(
                file.RelativePath,
                definition.RelativePath,
                StringComparison.OrdinalIgnoreCase));
            PrepareVerifiedRollbackTarget(
                journal,
                originalRoot,
                preparedDataRoot,
                expected.RelativePath,
                definition.Kind == KitenManagedFileKind.Sqlite ? "sqlite" : "file",
                expected.Present,
                restoreEntries,
                copyOperationId,
                cancellationToken);
        }

        foreach (var collection in manifest.Collections)
        {
            PrepareVerifiedRollbackTarget(
                journal,
                originalRoot,
                preparedDataRoot,
                collection.RelativePath,
                "directory",
                collection.Present,
                restoreEntries,
                copyOperationId,
                cancellationToken);
        }

        KitenDataMaintenanceArchive.ValidateCurrentDataRootAgainstSnapshot(
            preparedDataRoot,
            Path.Combine(workRoot, "validated-snapshot"),
            manifest,
            cancellationToken);
        return new KitenPreparedRollbackSource(workRoot, preparedDataRoot, restoreEntries);
    }

    private static void PrepareVerifiedRollbackTarget(
        KitenRestoreJournal journal,
        string originalRoot,
        string preparedDataRoot,
        string relativePath,
        string targetKind,
        bool expectedPresent,
        HashSet<string> restoreEntries,
        Guid copyOperationId,
        CancellationToken cancellationToken)
    {
        var entry = journal.Entries.SingleOrDefault(item => string.Equals(
            item.RelativePath,
            relativePath,
            StringComparison.OrdinalIgnoreCase));
        if (entry is not null && entry.OriginalPresent != expectedPresent)
        {
            throw new InvalidDataException("復元journalと復元前バックアップの存在状態が一致しません。");
        }

        var originalPath = KitenDataMaintenanceArchive.ResolveManagedPath(originalRoot, relativePath);
        var currentPath = KitenDataMaintenanceArchive.ResolveManagedPath(journal.DataRoot, relativePath);
        var originalExists = RollbackTargetExists(originalPath, targetKind);
        var currentExists = RollbackTargetExists(currentPath, targetKind);
        var sourcePath = currentPath;
        if (entry is not null && entry.State != "rolledBack")
        {
            var originalAlreadyReturned = entry.State == "restoringOriginal"
                                          && currentExists == expectedPresent;
            var originalNotSecured = entry.State == "planned" && !originalExists;
            if (!originalAlreadyReturned && !originalNotSecured)
            {
                sourcePath = originalPath;
                restoreEntries.Add(relativePath);
            }
        }

        if (RollbackTargetExists(sourcePath, targetKind) != expectedPresent)
        {
            throw new InvalidDataException("実際に使用する復元前原本の存在状態が記録と一致しません。");
        }

        if (!expectedPresent)
        {
            return;
        }

        var destinationPath = KitenDataMaintenanceArchive.ResolveManagedPath(preparedDataRoot, relativePath);
        CopyRollbackTarget(
            sourcePath,
            destinationPath,
            targetKind,
            copyOperationId,
            cancellationToken);
    }

    private static bool RollbackTargetExists(string path, string targetKind) => targetKind switch
    {
        "directory" => Directory.Exists(path),
        "sqlite" => SqliteCompanionSuffixes.Any(suffix => File.Exists(path + suffix)),
        _ => File.Exists(path)
    };

    private static void CopyRollbackTarget(
        string sourcePath,
        string destinationPath,
        string targetKind,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (targetKind == "directory")
        {
            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(sourcePath);
            Directory.CreateDirectory(destinationPath);
            foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                CopyFileDurable(
                    filePath,
                    Path.Combine(destinationPath, Path.GetFileName(filePath)),
                    operationId,
                    cancellationToken);
            }

            if (Directory.EnumerateDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidDataException("復元前原本の保存フォルダーに未対応の階層があります。");
            }

            return;
        }

        var suffixes = targetKind == "sqlite" ? SqliteCompanionSuffixes : [string.Empty];
        foreach (var suffix in suffixes)
        {
            if (File.Exists(sourcePath + suffix))
            {
                CopyFileDurable(
                    sourcePath + suffix,
                    destinationPath + suffix,
                    operationId,
                    cancellationToken);
            }
        }
    }

    private static void EnsureOriginalTargetState(string targetPath, KitenRestoreJournalEntry entry)
    {
        var present = entry.TargetKind == "directory"
            ? Directory.Exists(targetPath)
            : entry.TargetKind == "sqlite"
                ? SqliteCompanionSuffixes.Any(suffix => File.Exists(targetPath + suffix))
                : File.Exists(targetPath);
        if (present != entry.OriginalPresent)
        {
            throw new IOException("復元開始前の存在状態へ戻せませんでした。");
        }
    }

    private static void MoveFailedRestoredTarget(
        string targetPath,
        string failedPath,
        string targetKind,
        Guid operationId)
    {
        if (targetKind == "directory")
        {
            if (Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(failedPath)!);
                Directory.Move(targetPath, failedPath);
            }

            return;
        }

        if (targetKind == "sqlite")
        {
            foreach (var suffix in SqliteCompanionSuffixes)
            {
                MoveFailedRestoredFile(targetPath + suffix, failedPath + suffix);
            }
        }
        else
        {
            MoveFailedRestoredFile(targetPath, failedPath);
        }

        MoveFailedRestoredFile(
            targetPath + $".{operationId:N}.restoretmp",
            failedPath + $".{operationId:N}.restoretmp");
    }

    private static void MoveFailedRestoredFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
        {
            throw new IOException("復旧記録内で同名ファイルが衝突しました。");
        }

        File.Move(sourcePath, destinationPath);
    }

    private static void RestoreOriginalTarget(
        string rollbackPath,
        string targetPath,
        string targetKind,
        bool copyPreservedOriginal,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (targetKind == "directory")
        {
            if (Directory.Exists(rollbackPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (copyPreservedOriginal)
                {
                    Directory.CreateDirectory(targetPath);
                    foreach (var sourcePath in Directory.EnumerateFiles(rollbackPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        CopyFileDurable(
                            sourcePath,
                            Path.Combine(targetPath, Path.GetFileName(sourcePath)),
                            operationId,
                            cancellationToken);
                    }
                }
                else
                {
                    Directory.Move(rollbackPath, targetPath);
                }
            }

            return;
        }

        if (targetKind == "sqlite")
        {
            foreach (var suffix in SqliteCompanionSuffixes)
            {
                RestoreOriginalFile(
                    rollbackPath + suffix,
                    targetPath + suffix,
                    copyPreservedOriginal,
                    operationId,
                    cancellationToken);
            }

            return;
        }

        RestoreOriginalFile(
            rollbackPath,
            targetPath,
            copyPreservedOriginal,
            operationId,
            cancellationToken);
    }

    private static void ResetRawRollbackTarget(string targetPath, string targetKind, Guid operationId)
    {
        RemoveOriginalTarget(targetPath, targetKind);
        if (targetKind == "directory")
        {
            return;
        }

        var paths = targetKind == "sqlite"
            ? SqliteCompanionSuffixes.Select(suffix => targetPath + suffix)
            : [targetPath];
        foreach (var path in paths)
        {
            var temporaryPath = path + $".{operationId:N}.restoretmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RestoreOriginalFile(
        string rollbackPath,
        string targetPath,
        bool copyPreservedOriginal,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(rollbackPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (copyPreservedOriginal)
        {
            CopyFileDurable(rollbackPath, targetPath, operationId, cancellationToken);
        }
        else
        {
            File.Move(rollbackPath, targetPath);
        }
    }

    private void ValidateRollbackAgainstPreRestoreState(
        KitenRestoreJournal journal,
        KitenPreparedRollbackSource preparedSource,
        CancellationToken cancellationToken)
    {
        if (IsRawPreservation(journal))
        {
            var manifest = ValidateRawPreservationManifest(
                Path.Combine(
                    preparedSource.WorkRoot,
                    "raw-current",
                    "original-snapshot.json"),
                journal.Channel,
                cancellationToken);
            ValidateCurrentDataRootAgainstRawPreservation(
                journal.DataRoot,
                manifest,
                cancellationToken);
            return;
        }

        var verificationRoot = CreateTemporaryMaintenanceRoot();
        try
        {
            var manifest = KitenDataMaintenanceArchive.ValidateAndExtractArchive(
                journal.PreRestoreBackupPath,
                verificationRoot,
                cancellationToken);
            KitenDataMaintenanceArchive.ValidateCurrentDataRootAgainstSnapshot(
                journal.DataRoot,
                Path.Combine(verificationRoot, "current-snapshot"),
                manifest,
                cancellationToken);
        }
        finally
        {
            DeleteTemporaryMaintenanceRoot(verificationRoot);
        }
    }

    private static bool IsRawPreservation(KitenRestoreJournal journal) =>
        journal.SchemaVersion >= 2
        && string.Equals(
            journal.PreRestoreStateKind,
            KitenPreRestoreStateKind.RawPreservation,
            StringComparison.Ordinal);

    private static string GetPreservedDataRoot(KitenRestoreJournal journal)
    {
        if (!IsRawPreservation(journal))
        {
            return Path.Combine(journal.WorkRoot, "original");
        }

        var preservationRoot = Path.GetDirectoryName(journal.PreRestoreBackupPath)
            ?? throw new InvalidDataException("破損原本の保全先を確認できません。");
        return Path.Combine(preservationRoot, "data");
    }

    private static RecoveryState LoadRecoveryState(string dataRoot, ApplicationChannel channel)
    {
        var markerPath = Path.Combine(dataRoot, "restore.in-progress");
        if (!File.Exists(markerPath))
        {
            if (Directory.Exists(markerPath))
            {
                throw new InvalidDataException("中断復元markerと同名のフォルダーがあるため、安全に復旧できません。");
            }

            throw new InvalidDataException("中断した復元のmarkerがありません。");
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(markerPath);
        var marker = ReadJsonFile<KitenRestoreMarker>(markerPath, maximumBytes: 64 * 1024);
        if (marker.SchemaVersion != 1
            || marker.OperationId == Guid.Empty
            || marker.Channel != KitenDataMaintenanceArchive.ChannelName(channel))
        {
            throw new InvalidDataException("復元markerが不正、またはチャンネルが一致しません。");
        }

        var journalPath = KitenDataMaintenanceArchive.NormalizeAbsolutePath(marker.JournalPath, "復元journal");
        var workRoot = Path.GetDirectoryName(journalPath)
            ?? throw new InvalidDataException("復元journalの場所が不正です。");
        var expectedParent = Path.GetDirectoryName(dataRoot)
            ?? throw new InvalidDataException("データルートの親フォルダーを取得できません。");
        if (!string.Equals(Path.GetDirectoryName(workRoot), expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(workRoot).StartsWith(
                ".kiten-restore-" + marker.Channel + "-",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(journalPath), "restore-journal.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("復元journalが許可された作業領域外を指しています。");
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(journalPath);
        var journal = ReadJsonFile<KitenRestoreJournal>(journalPath, maximumBytes: 2 * 1024 * 1024);
        if (journal.OperationId != marker.OperationId
            || !string.Equals(journal.Channel, marker.Channel, StringComparison.Ordinal)
            || !string.Equals(journal.DataRoot, dataRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(journal.WorkRoot, workRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("復元markerとjournalが一致しません。");
        }

        ValidateJournal(journal);
        return new RecoveryState(markerPath, journalPath, journal);
    }

    private static void ValidateJournal(KitenRestoreJournal journal)
    {
        if (journal.SchemaVersion is not (1 or 2)
            || journal.OperationId == Guid.Empty
            || journal.Channel is not ("stable" or "development")
            || !Path.IsPathFullyQualified(journal.DataRoot)
            || !Path.IsPathFullyQualified(journal.WorkRoot)
            || !Path.IsPathFullyQualified(journal.PreRestoreBackupPath)
            || !KitenDataMaintenanceArchive.IsSameOrDescendant(journal.PreRestoreBackupPath, journal.WorkRoot))
        {
            throw new InvalidDataException("復元journalの基本情報が不正です。");
        }

        var preRestoreStateKind = journal.SchemaVersion == 1
            ? KitenPreRestoreStateKind.VerifiedBackup
            : journal.PreRestoreStateKind;
        if (preRestoreStateKind is not (KitenPreRestoreStateKind.VerifiedBackup
            or KitenPreRestoreStateKind.RawPreservation)
            || journal.SchemaVersion == 1
            && preRestoreStateKind != KitenPreRestoreStateKind.VerifiedBackup)
        {
            throw new InvalidDataException("復元journalの原本保全形式が不正です。");
        }

        if (preRestoreStateKind == KitenPreRestoreStateKind.RawPreservation
            && !string.Equals(
                Path.GetFileName(journal.PreRestoreBackupPath),
                "original-snapshot.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("復元journalの破損原本保全先が不正です。");
        }

        var allFixed = KitenDataMaintenanceArchive.GetFixedDefinitions(
                includeBrowsingHistory: true,
                includeRecentItems: true,
                includeDownloadHistory: true)
            .ToDictionary(static definition => definition.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            KitenDataMaintenanceArchive.ValidateRelativePath(entry.RelativePath);
            if (!seen.Add(entry.RelativePath)
                || entry.State is not ("planned" or "secured" or "applied" or "securingFailedTarget"
                    or "failedTargetSecured" or "restoringOriginal" or "rolledBack"))
            {
                throw new InvalidDataException("復元journalの対象または状態が不正です。");
            }

            var expectedKind = KitenDataMaintenanceArchive.ManagedCollectionPaths.Contains(
                entry.RelativePath,
                StringComparer.OrdinalIgnoreCase)
                ? "directory"
                : allFixed.TryGetValue(entry.RelativePath, out var definition)
                    ? definition.Kind == KitenManagedFileKind.Sqlite ? "sqlite" : "file"
                    : null;
            if (expectedKind is null || !string.Equals(entry.TargetKind, expectedKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("復元journalに対象外のパスがあります。");
            }
        }
    }

    private static void WriteRestoreMarker(string dataRoot, string journalPath, KitenRestoreJournal journal)
    {
        Directory.CreateDirectory(dataRoot);
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(dataRoot);
        var markerPath = Path.Combine(dataRoot, "restore.in-progress");
        if (File.Exists(markerPath) || Directory.Exists(markerPath))
        {
            throw new InvalidDataException("別の復元が未完了です。");
        }

        WriteJsonDurable(markerPath, new KitenRestoreMarker
        {
            OperationId = journal.OperationId,
            Channel = journal.Channel,
            JournalPath = journalPath
        });
    }

    private static void DeleteRestoreMarker(string dataRoot)
    {
        var markerPath = Path.Combine(dataRoot, "restore.in-progress");
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    private static void WriteJsonDurable<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JournalJsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private KitenDataMaintenanceResult WithChannelMutex(
        ApplicationChannel channel,
        Func<KitenDataMaintenanceResult> action)
    {
        using var mutex = _hooks.CreateMutex(channel);
        if (!mutex.TryAcquirePrimaryInstance())
        {
            return KitenDataMaintenanceResult.Failure(
                "対象チャンネルのKitenが起動中、または別の保守処理が実行中です。Kitenを通常終了してから再実行してください。");
        }

        _hooks.Checkpoint?.Invoke("mutex.acquired");
        return action();
    }

    private static void EnsureNoInterruptedRestore(string dataRoot)
    {
        var markerPath = Path.Combine(dataRoot, "restore.in-progress");
        if (File.Exists(markerPath) || Directory.Exists(markerPath))
        {
            throw new InvalidDataException(
                "前回の復元が完了していません。新しい操作を始めず、ROLLBACKで前の状態へ戻してください。");
        }
    }

    private static void EnsureManifestChannel(KitenBackupManifest manifest, ApplicationChannel channel)
    {
        if (!string.Equals(
                manifest.Channel,
                KitenDataMaintenanceArchive.ChannelName(channel),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("バックアップと復元先のチャンネルが一致しません。");
        }
    }

    private static void ProbeManagedFiles(string dataRoot, KitenBackupRequest request) =>
        ProbeManagedFiles(
            dataRoot,
            request.IncludeBrowsingHistory,
            request.IncludeRecentItems,
            request.IncludeDownloadHistory);

    private static void ProbeManagedFiles(string dataRoot, KitenBackupManifest manifest) =>
        ProbeManagedFiles(
            dataRoot,
            manifest.IncludesBrowsingHistory,
            manifest.IncludesRecentItems,
            manifest.IncludesDownloadHistory);

    private static void ProbeManagedFiles(
        string dataRoot,
        bool includeBrowsingHistory,
        bool includeRecentItems,
        bool includeDownloadHistory)
    {
        if (!Directory.Exists(dataRoot))
        {
            return;
        }

        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(dataRoot);
        var paths = new List<string>();
        foreach (var definition in KitenDataMaintenanceArchive.GetFixedDefinitions(
                     includeBrowsingHistory,
                     includeRecentItems,
                     includeDownloadHistory))
        {
            var path = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, definition.RelativePath);
            // 対象ファイルがまだ存在しなくても、既存の親junction等を経由してはならない。
            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(path);
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("管理対象ファイルと同名のフォルダーがあるため、安全に処理できません。");
            }

            if (definition.Kind == KitenManagedFileKind.Sqlite)
            {
                if (!File.Exists(path)
                    && SqliteCompanionSuffixes.Skip(1).Any(suffix => File.Exists(path + suffix)))
                {
                    throw new InvalidDataException("SQLite本体がない状態でWALまたはjournalが残っているため、安全に処理できません。");
                }

                paths.AddRange(SqliteCompanionSuffixes.Select(suffix => path + suffix));
            }
            else
            {
                paths.Add(path);
            }
        }

        foreach (var collection in KitenDataMaintenanceArchive.ManagedCollectionPaths)
        {
            var directory = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, collection);
            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(directory);
            if (File.Exists(directory))
            {
                throw new InvalidDataException("管理対象フォルダーと同名のファイルがあるため、安全に処理できません。");
            }

            if (!Directory.Exists(directory))
            {
                continue;
            }

            paths.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly));
        }

        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(path);
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                throw new IOException("対象データを使用中のプロセスがあります。Kitenを通常終了してから再実行してください。");
            }
        }
    }

    private static int CountPresentManagedFiles(string dataRoot, KitenBackupRequest request)
    {
        if (!Directory.Exists(dataRoot))
        {
            return 0;
        }

        var count = KitenDataMaintenanceArchive.GetFixedDefinitions(
                request.IncludeBrowsingHistory,
                request.IncludeRecentItems,
                request.IncludeDownloadHistory)
            .Count(definition => File.Exists(
                KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, definition.RelativePath)));
        foreach (var collection in KitenDataMaintenanceArchive.ManagedCollectionPaths)
        {
            var directory = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, collection);
            if (Directory.Exists(directory))
            {
                count += Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Count();
            }
        }

        return count;
    }

    private static string DescribeOptionalData(KitenBackupRequest request)
    {
        var included = new List<string>();
        if (request.IncludeBrowsingHistory)
        {
            included.Add("閲覧履歴");
        }

        if (request.IncludeRecentItems)
        {
            included.Add("最近項目");
        }

        if (request.IncludeDownloadHistory)
        {
            included.Add("ダウンロード履歴");
        }

        return included.Count == 0
            ? "履歴類は含みません"
            : string.Join("・", included) + "を含みます";
    }

    private static string DescribeManifestHistory(KitenBackupManifest manifest)
    {
        static string Inclusion(bool included) => included ? "含む" : "除外";

        return $"閲覧履歴={Inclusion(manifest.IncludesBrowsingHistory)}、"
               + $"最近項目={Inclusion(manifest.IncludesRecentItems)}、"
               + $"ダウンロード履歴={Inclusion(manifest.IncludesDownloadHistory)}";
    }

    private static string DescribeRestoreTargets(string dataRoot, KitenBackupManifest manifest)
    {
        var replace = manifest.Files
            .Where(static file => file.Present)
            .Select(static file => file.RelativePath)
            .Concat(manifest.Collections
                .Where(static collection => collection.Present)
                .Select(static collection => collection.RelativePath))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var definitions = KitenDataMaintenanceArchive.GetFixedDefinitions(manifest)
            .ToDictionary(static definition => definition.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remove = manifest.Files
            .Where(file => !file.Present && CurrentManagedFileExists(dataRoot, file.RelativePath, definitions))
            .Select(static file => file.RelativePath)
            .Concat(manifest.Collections
                .Where(collection => !collection.Present && Directory.Exists(
                    KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, collection.RelativePath)))
                .Select(static collection => collection.RelativePath))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return $"作成・置換={FormatRelativePathSummary(replace)}、不存在へ変更={FormatRelativePathSummary(remove)}。";
    }

    private static bool CurrentManagedFileExists(
        string dataRoot,
        string relativePath,
        IReadOnlyDictionary<string, KitenManagedFileDefinition> definitions)
    {
        var path = KitenDataMaintenanceArchive.ResolveManagedPath(dataRoot, relativePath);
        return definitions.TryGetValue(relativePath, out var definition)
               && definition.Kind == KitenManagedFileKind.Sqlite
            ? SqliteCompanionSuffixes.Any(suffix => File.Exists(path + suffix))
            : File.Exists(path);
    }

    private static string FormatRelativePathSummary(IReadOnlyList<string> paths)
    {
        const int maximumDisplayed = 12;
        if (paths.Count == 0)
        {
            return "なし";
        }

        var displayed = string.Join(", ", paths.Take(maximumDisplayed));
        return paths.Count <= maximumDisplayed
            ? $"{paths.Count}件 [{displayed}]"
            : $"{paths.Count}件 [{displayed}, ほか{paths.Count - maximumDisplayed}件]";
    }

    private string CreateRestoreWorkRoot(string dataRoot, ApplicationChannel channel)
    {
        var parent = Path.GetDirectoryName(dataRoot)
            ?? throw new InvalidDataException("データルートの親フォルダーを取得できません。");
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(parent);
        var workRoot = Path.Combine(
            parent,
            $".kiten-restore-{KitenDataMaintenanceArchive.ChannelName(channel)}-{_hooks.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        return workRoot;
    }

    private string CreateTemporaryMaintenanceRoot()
    {
        var basePath = KitenDataMaintenanceArchive.NormalizeAbsolutePath(Path.GetTempPath(), "一時フォルダー");
        KitenDataMaintenanceArchive.EnsureExistingPathHasNoReparsePoint(basePath);
        var path = Path.Combine(basePath, ".kiten-maintenance-" + _hooks.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryMaintenanceRoot(string path)
    {
        var basePath = KitenDataMaintenanceArchive.NormalizeAbsolutePath(Path.GetTempPath(), "一時フォルダー");
        TryDeleteOwnedDirectory(path, basePath);
    }

    private static void TryDeleteRestoreWorkRoot(string workRoot, string dataRoot)
    {
        var parent = Path.GetDirectoryName(dataRoot);
        if (parent is null)
        {
            return;
        }

        TryDeleteOwnedDirectory(workRoot, parent);
    }

    private static void TryDeleteOwnedDirectory(string path, string ownerRoot)
    {
        try
        {
            KitenDataMaintenanceArchive.DeleteOwnedDirectory(path, ownerRoot);
        }
        catch
        {
        }
    }

    private static void DeleteCreatedBackup(string archivePath)
    {
        try
        {
            if (File.Exists(archivePath + ".sha256"))
            {
                File.Delete(archivePath + ".sha256");
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
        catch
        {
        }
    }

    private static T ReadJsonFile<T>(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException("復旧情報ファイルのサイズが不正です。");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(stream, JournalJsonOptions)
            ?? throw new InvalidDataException("復旧情報ファイルを読み込めません。");
    }

    private KitenDataMaintenanceResult ExecuteSafely(Func<KitenDataMaintenanceResult> action)
    {
        try
        {
            return action();
        }
        catch (OperationCanceledException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure("操作をキャンセルしました。確定済みデータは変更していません。");
        }
        catch (UnauthorizedAccessException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure("アクセスが拒否されました。確定済みデータは変更していません。");
        }
        catch (KitenUnsupportedDataFormatException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(exception.Message);
        }
        catch (InvalidDataException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(exception.Message);
        }
        catch (SqliteException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(DescribeSqliteFailure(exception));
        }
        catch (IOException exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(
                "ファイル操作に失敗しました。確定済みデータと以前の正常なバックアップは変更していません。");
        }
        catch (Exception exception)
        {
            _hooks.Failure?.Invoke(exception);
            return KitenDataMaintenanceResult.Failure(
                "予期しないエラーで処理を中止しました。確定済みデータと以前の正常なバックアップは変更していません。");
        }
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        OperationCanceledException => "操作はキャンセルされました。",
        UnauthorizedAccessException => "アクセスが拒否されました。",
        KitenUnsupportedDataFormatException => "未対応のデータ形式です。",
        InvalidDataException => "検証に失敗しました。",
        SqliteException sqliteException => DescribeSqliteFailure(sqliteException),
        IOException => "ファイル操作に失敗しました。",
        _ => "予期しないエラーが発生しました。"
    };

    private static string DescribeSqliteFailure(SqliteException exception) =>
        GetSqlitePrimaryErrorCode(exception) switch
        {
            5 or 6 => "SQLiteデータが使用中またはロック中のため、処理を中止しました。確定済みデータは変更していません。",
            8 or 14 => "SQLiteデータまたは退避先へアクセスできないため、処理を中止しました。確定済みデータは変更していません。",
            10 => "SQLiteの読み書きでI/O障害が発生したため、処理を中止しました。確定済みデータは変更していません。",
            13 => "SQLiteの保存先容量が不足したため、処理を中止しました。確定済みデータは変更していません。",
            _ => "SQLite処理の環境障害または未分類のエラーで中止しました。確定済みデータは変更していません。"
        };

    private sealed record RecoveryState(
        string MarkerPath,
        string JournalPath,
        KitenRestoreJournal Journal);

    private sealed record KitenPreparedRollbackSource(
        string WorkRoot,
        string DataRoot,
        IReadOnlySet<string> RestoreEntries);
}
