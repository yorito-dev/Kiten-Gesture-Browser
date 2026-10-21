using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.DataMaintenance;
using TabBrowser.Services.Workspaces;
using Xunit;

namespace TabBrowser.Tests;

public sealed class KitenDataMaintenanceServiceTests
{
    [Fact]
    public async Task BackupAndRestorePreserveAllowedDataAndLeaveExcludedDataUntouched()
    {
        using var environment = new TestEnvironment();
        var original = await environment.WriteSampleDataAsync(environment.DataRoot, "保存時");
        environment.WriteExcludedFiles(environment.DataRoot, "残す秘密");
        Exception? capturedFailure = null;
        var service = environment.CreateService(failure: exception => capturedFailure = exception);

        var backup = service.CreateBackup(environment.BackupRequest());

        Assert.True(backup.Succeeded, backup.Message);
        var archivePath = Assert.IsType<string>(backup.OutputPath);
        Assert.True(File.Exists(archivePath));
        Assert.True(File.Exists(archivePath + ".sha256"));
        Assert.DoesNotContain(ReadArchiveEntryNames(archivePath), static name =>
            name.Contains("private", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
            || name.Contains("diagnostics", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("downloaded.bin", StringComparison.OrdinalIgnoreCase));

        await environment.WriteSampleDataAsync(environment.DataRoot, "復元前");
        environment.WriteExcludedFiles(environment.DataRoot, "復元前も維持");
        File.WriteAllText(Path.Combine(environment.DataRoot, "recent-items.json"), "[{\"title\":\"履歴は対象外\"}]");
        var restore = service.Restore(environment.RestoreRequest(archivePath));

        Assert.True(restore.Succeeded, capturedFailure?.ToString() ?? restore.Message);
        Assert.True(restore.ChangedData);
        Assert.NotNull(restore.RecoveryPath);
        Assert.True(Directory.Exists(restore.RecoveryPath));
        Assert.Contains("復元前も維持", File.ReadAllText(Path.Combine(environment.DataRoot, "private", "secret.txt")));
        Assert.Contains("履歴は対象外", File.ReadAllText(Path.Combine(environment.DataRoot, "recent-items.json")));

        var settings = new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load();
        Assert.Equal("保存時", settings.SearchEngine);
        var bookmarks = new BookmarkService(Path.Combine(environment.DataRoot, "bookmarks.json")).LoadData();
        Assert.Equal("保存時", Assert.Single(bookmarks.Bookmarks).Title);
        var profiles = new ContainerProfileStore(Path.Combine(environment.DataRoot, "container-profiles.json")).Load();
        Assert.Contains(profiles, profile => profile.Id == original.ContainerId && profile.Name == "保存時");
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        var workspace = Assert.Single(await repository.GetWorkspacesAsync());
        Assert.Equal(original.WorkspaceId, workspace.Id);
        var restoredItems = await repository.GetWorkspaceItemsAsync(workspace.Id);
        var note = Assert.IsType<NoteItem>(Assert.Single(restoredItems, item => item.Id == original.NoteId));
        Assert.Equal(original.NoteId, note.Id);
        Assert.Equal("保存時", note.Content);
        var sessionGroup = Assert.IsType<SessionGroupItem>(
            Assert.Single(restoredItems, item => item.Id == original.SessionGroupId));
        Assert.Equal([original.SessionTabId1, original.SessionTabId2], sessionGroup.Tabs.Select(tab => tab.Id));
        Assert.Equal([0, 1], sessionGroup.Tabs.Select(tab => tab.SortOrder));
        Assert.Equal([original.ContainerId, null], sessionGroup.Tabs.Select(tab => tab.ContainerProfileId));
        Assert.Equal(original.FolderId, note.ParentFolderId);
        Assert.Equal(["重要"], note.Tags);
        Assert.Equal(original.FolderId, sessionGroup.ParentFolderId);
        var restoredNamedSession = await new SessionService(
                Path.Combine(environment.DataRoot, "session.json"),
                Path.Combine(environment.DataRoot, "window-memory.json"),
                Path.Combine(environment.DataRoot, "session"))
            .LoadNamedRestorePointAsync(original.NamedRestorePointId);
        var restoredNamedTab = Assert.Single(Assert.Single(restoredNamedSession.Windows).Tabs);
        Assert.Equal("保存時 復元タブ", restoredNamedTab.Title);
        Assert.Equal(original.ContainerId, restoredNamedTab.ContainerProfileId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SupportedWorkspaceSchemaVersionsCanBeBackedUpAndRestored(int schemaVersion)
    {
        using var environment = new TestEnvironment();
        var original = await environment.WriteSampleDataAsync(environment.DataRoot, $"version-{schemaVersion}");
        await environment.SetWorkspaceSchemaVersionAsync(environment.DataRoot, schemaVersion);
        var service = environment.CreateService();

        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);

        await environment.WriteSampleDataAsync(environment.DataRoot, "復元前");
        var restore = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.True(restore.Succeeded, restore.Message);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(environment.DataRoot, "workspace.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)schemaVersion, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT note FROM workspace_items WHERE id=$id;";
        command.Parameters.AddWithValue("$id", original.NoteId.ToString("D"));
        Assert.Equal($"version-{schemaVersion}", await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT parent_folder_id FROM workspace_items WHERE id=$id;";
        command.Parameters.AddWithValue("$id", original.NoteId.ToString("D"));
        Assert.Equal(original.FolderId.ToString("D"), await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT tag FROM workspace_item_tags WHERE item_id=$id;";
        command.Parameters.AddWithValue("$id", original.NoteId.ToString("D"));
        Assert.Equal("重要", await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT tag FROM workspace_item_tags WHERE item_id=$id;";
        command.Parameters.AddWithValue("$id", original.SessionGroupId.ToString("D"));
        Assert.Equal("保存", await command.ExecuteScalarAsync());
        command.Parameters.Clear();
        command.CommandText = "SELECT favicon_png_base64, thumbnail_png_base64 FROM workspace_items WHERE id=$id;";
        command.Parameters.AddWithValue("$id", original.SessionGroupId.ToString("D"));
        await using (var imageReader = await command.ExecuteReaderAsync())
        {
            Assert.True(await imageReader.ReadAsync());
            Assert.Equal("aWNvbg==", imageReader.GetString(0));
            Assert.Equal("aW1hZ2U=", imageReader.GetString(1));
        }
        command.Parameters.Clear();
        command.CommandText = schemaVersion >= 1
            ? "SELECT id, title, sort_order, container_profile_id FROM workspace_session_tabs WHERE session_group_item_id=$id ORDER BY sort_order;"
            : "SELECT id, title, sort_order FROM workspace_session_tabs WHERE session_group_item_id=$id ORDER BY sort_order;";
        command.Parameters.AddWithValue("$id", original.SessionGroupId.ToString("D"));
        var tabs = new List<(Guid Id, string Title, int SortOrder, string? ContainerProfileId)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tabs.Add((
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    schemaVersion >= 1 && !reader.IsDBNull(3) ? reader.GetString(3) : null));
            }
        }
        Assert.Equal([original.SessionTabId1, original.SessionTabId2], tabs.Select(tab => tab.Id));
        Assert.Equal(["コンテナ付き", "コンテナ情報なし"], tabs.Select(tab => tab.Title));
        Assert.Equal([0, 1], tabs.Select(tab => tab.SortOrder));
        Assert.Equal(schemaVersion >= 1
                ? new string?[] { original.ContainerId, null }
                : new string?[] { null, null },
            tabs.Select(tab => tab.ContainerProfileId));
    }

    [Fact]
    public async Task VersionTwoTrashItemsAreBackedUpAndRestoredWithoutLosingDeletionMetadata()
    {
        using var environment = new TestEnvironment();
        var original = await environment.WriteSampleDataAsync(environment.DataRoot, "ごみ箱保存");
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        Assert.Contains(original.NoteId, await repository.DeleteItemAsync(original.NoteId));
        var before = Assert.Single(await repository.GetDeletedItemsAsync(), item => item.Id == original.NoteId);
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);

        await environment.WriteSampleDataAsync(environment.DataRoot, "復元前");
        var restore = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.True(restore.Succeeded, restore.Message);

        var restoredRepository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        var restored = Assert.IsType<NoteItem>(
            Assert.Single(await restoredRepository.GetDeletedItemsAsync(), item => item.Id == original.NoteId));
        Assert.Equal("ごみ箱保存", restored.Content);
        Assert.Equal(original.FolderId, restored.ParentFolderId);
        Assert.Equal(["重要"], restored.Tags);
        Assert.Equal(before.DeletedAt, restored.DeletedAt);
        Assert.Equal(original.NoteId, restored.DeletionRootId);
    }

    [Fact]
    public async Task EmptyBackupRecordsAbsenceAndRemovesOnlyManagedTargetsDuringRestore()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(environment.DataRoot);
        Exception? capturedFailure = null;
        var service = environment.CreateService(failure: exception => capturedFailure = exception);
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);

        await environment.WriteSampleDataAsync(environment.DataRoot, "後から作成");
        environment.WriteExcludedFiles(environment.DataRoot, "保持する");
        var restore = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.True(restore.Succeeded, capturedFailure?.ToString() ?? restore.Message);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "settings.json")));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "bookmarks.json")));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "workspace.db")));
        Assert.False(Directory.Exists(Path.Combine(environment.DataRoot, "session.named")));
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "private", "secret.txt")));
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "WebView2", "Cookies")));
    }

    [Fact]
    public void PresentEmptySessionCollectionIsRestoredAsAnEmptyDirectory()
    {
        using var environment = new TestEnvironment();
        var namedDirectory = Path.Combine(environment.DataRoot, "session.named");
        Directory.CreateDirectory(namedDirectory);
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);

        File.WriteAllText(
            Path.Combine(namedDirectory, "named-later.json"),
            "{\"version\":2,\"tabs\":[]}",
            new UTF8Encoding(false));
        var restore = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.True(restore.Succeeded, restore.Message);
        Assert.True(Directory.Exists(namedDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(namedDirectory));
    }

    [Fact]
    public void KnownAtomicSessionTemporaryFilesAreExcludedButUnknownFilesAreRejected()
    {
        using var environment = new TestEnvironment();
        var namedDirectory = Path.Combine(environment.DataRoot, "session.named");
        Directory.CreateDirectory(namedDirectory);
        var temporaryName = $"named-sample.json.1234.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(Path.Combine(namedDirectory, temporaryName), "途中", new UTF8Encoding(false));
        var service = environment.CreateService();

        var backup = service.CreateBackup(environment.BackupRequest());

        Assert.True(backup.Succeeded, backup.Message);
        Assert.DoesNotContain(
            ReadArchiveEntryNames(Assert.IsType<string>(backup.OutputPath)),
            name => name.Contains(temporaryName, StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(Path.Combine(namedDirectory, "unknown.tmp"), "不明", new UTF8Encoding(false));
        var rejected = service.CreateBackup(environment.BackupRequest());
        Assert.False(rejected.Succeeded);
        Assert.Contains("未対応", rejected.Message);
        File.Delete(Path.Combine(namedDirectory, "unknown.tmp"));

        var failedRestore = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new IOException("restore failure simulation");
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.False(failedRestore.Succeeded);
        Assert.False(failedRestore.ChangedData);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.True(File.Exists(Path.Combine(namedDirectory, temporaryName)));
    }

    [Fact]
    public async Task BackupFinalNameAppearsOnlyAfterProvisionalArchiveValidation()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "検証順序");
        var observedProvisional = false;
        var service = environment.CreateService(checkpoint =>
        {
            if (checkpoint != "backup.after-provisional-archive-validation")
            {
                return;
            }

            observedProvisional = true;
            Assert.Empty(Directory.GetFiles(environment.BackupDirectory, "*.zip"));
            Assert.Single(Directory.GetFiles(environment.BackupDirectory, "*.zip.partial"));
            throw new KitenSimulatedInterruptionException();
        });

        var result = service.CreateBackup(environment.BackupRequest());

        Assert.False(result.Succeeded);
        Assert.True(observedProvisional);
        Assert.Empty(Directory.GetFiles(environment.BackupDirectory, "*.zip"));
        Assert.Empty(Directory.GetFiles(environment.BackupDirectory, "*.partial"));
    }

    [Fact]
    public void BackupIncludesCommittedWalContentWithoutCopyingWalFiles()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(environment.DataRoot);
        var databasePath = Path.Combine(environment.DataRoot, "workspace.db");
        CreateCrashStyleWalDatabase(environment, databasePath);
        Assert.True(File.Exists(databasePath + "-wal"));
        var service = environment.CreateService();

        var backup = service.CreateBackup(environment.BackupRequest());

        Assert.True(backup.Succeeded, backup.Message);
        var archivePath = Assert.IsType<string>(backup.OutputPath);
        var names = ReadArchiveEntryNames(archivePath);
        Assert.Contains("data/workspace.db", names);
        Assert.DoesNotContain("data/workspace.db-wal", names);
        Assert.DoesNotContain("data/workspace.db-shm", names);

        var restoreRoot = environment.OtherDataRoot("wal-restore");
        var restored = service.Restore(environment.RestoreRequest(archivePath, restoreRoot));
        Assert.True(restored.Succeeded, restored.Message);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(restoreRoot, "workspace.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM wal_probe;";
        Assert.Equal("committed-in-wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task ManifestRecordsSourceVersionsPresenceHashesAndOptionalHistory()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "manifest");
        File.WriteAllText(
            Path.Combine(environment.DataRoot, "recent-items.json"),
            "[]",
            new UTF8Encoding(false));
        var downloadHistory = Path.Combine(environment.DataRoot, "downloads", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(downloadHistory)!);
        File.WriteAllText(downloadHistory, "[]", new UTF8Encoding(false));
        using (var history = new SqliteConnection(
                   $"Data Source={Path.Combine(environment.DataRoot, "browsing-history.db")};Pooling=False"))
        {
            history.Open();
            using var command = history.CreateCommand();
            command.CommandText = "PRAGMA user_version=0; CREATE TABLE history_probe(value TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        var releaseInformationPath = Path.Combine(environment.BasePath, "release-info.json");
        const string sourceCommit = "0123456789abcdef0123456789abcdef01234567";
        File.WriteAllText(
            releaseInformationPath,
            $"{{\"version\":\"0.1.0-preview.4\",\"sourceCommit\":\"{sourceCommit}\"}}",
            new UTF8Encoding(false));
        var service = environment.CreateService();

        var defaultBackup = service.CreateBackup(environment.BackupRequest() with
        {
            ReleaseInformationPath = releaseInformationPath
        });
        var defaultManifest = ReadManifest(Assert.IsType<string>(defaultBackup.OutputPath));
        Assert.Equal(1, defaultManifest.GetProperty("schemaVersion").GetInt32());
        Assert.False(defaultManifest.GetProperty("encrypted").GetBoolean());
        Assert.False(defaultManifest.GetProperty("includesBrowsingHistory").GetBoolean());
        Assert.False(defaultManifest.GetProperty("includesRecentItems").GetBoolean());
        Assert.False(defaultManifest.GetProperty("includesDownloadHistory").GetBoolean());
        Assert.Equal("0.1.0-preview.4", defaultManifest.GetProperty("sourceApplication").GetProperty("publicVersion").GetString());
        Assert.Equal(sourceCommit, defaultManifest.GetProperty("sourceApplication").GetProperty("sourceCommit").GetString());
        var defaultFiles = defaultManifest.GetProperty("files").EnumerateArray().ToList();
        Assert.DoesNotContain(defaultFiles, item => item.GetProperty("relativePath").GetString() == "recent-items.json");
        var settings = Assert.Single(defaultFiles, item => item.GetProperty("relativePath").GetString() == "settings.json");
        Assert.True(settings.GetProperty("present").GetBoolean());
        Assert.True(settings.GetProperty("size").GetInt64() > 0);
        Assert.Matches("^[0-9A-F]{64}$", settings.GetProperty("sha256").GetString()!);

        var historyBackup = service.CreateBackup(environment.BackupRequest() with
        {
            ReleaseInformationPath = releaseInformationPath,
            IncludeBrowsingHistory = true,
            IncludeRecentItems = true,
            IncludeDownloadHistory = true
        });
        var historyManifest = ReadManifest(Assert.IsType<string>(historyBackup.OutputPath));
        Assert.True(historyManifest.GetProperty("includesBrowsingHistory").GetBoolean());
        Assert.True(historyManifest.GetProperty("includesRecentItems").GetBoolean());
        Assert.True(historyManifest.GetProperty("includesDownloadHistory").GetBoolean());
        var historyFiles = historyManifest.GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(historyFiles, item => item.GetProperty("relativePath").GetString() == "recent-items.json"
            && item.GetProperty("present").GetBoolean());
        Assert.Contains(historyFiles, item => item.GetProperty("relativePath").GetString() == "downloads/history.json"
            && item.GetProperty("present").GetBoolean());
        Assert.Contains(historyFiles, item => item.GetProperty("relativePath").GetString() == "browsing-history.db"
            && item.GetProperty("present").GetBoolean());
    }

    [Fact]
    public void BackupAcceptsUtf8BomInReleaseInformation()
    {
        using var environment = new TestEnvironment();
        var releaseInformationPath = Path.Combine(environment.BasePath, "release-info.json");
        const string sourceCommit = "0123456789abcdef0123456789abcdef01234567";
        File.WriteAllText(
            releaseInformationPath,
            $"{{\"version\":\"0.1.0-preview.4\",\"sourceCommit\":\"{sourceCommit}\"}}",
            new UTF8Encoding(true));
        var service = environment.CreateService();

        var backup = service.CreateBackup(environment.BackupRequest() with
        {
            ReleaseInformationPath = releaseInformationPath
        });

        Assert.True(backup.Succeeded, backup.Message);
        var manifest = ReadManifest(Assert.IsType<string>(backup.OutputPath));
        Assert.Equal("0.1.0-preview.4", manifest.GetProperty("sourceApplication").GetProperty("publicVersion").GetString());
        Assert.Equal(sourceCommit, manifest.GetProperty("sourceApplication").GetProperty("sourceCommit").GetString());
    }

    [Fact]
    public async Task RestoreRemovesOldSqliteCompanionsInsteadOfMixingDatabaseGenerations()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var targetRoot = environment.OtherDataRoot("sqlite-family-target");
        Directory.CreateDirectory(targetRoot);
        var targetDatabase = Path.Combine(targetRoot, "workspace.db");
        CreateCrashStyleWalDatabase(environment, targetDatabase);
        Assert.True(File.Exists(targetDatabase + "-wal"));
        var companionsAbsentBeforeValidation = false;
        service = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.before-final-validation")
            {
                companionsAbsentBeforeValidation = !File.Exists(targetDatabase + "-wal")
                    && !File.Exists(targetDatabase + "-shm")
                    && !File.Exists(targetDatabase + "-journal");
            }
        });

        var restored = service.Restore(environment.RestoreRequest(
            Assert.IsType<string>(backup.OutputPath),
            targetRoot));

        Assert.True(restored.Succeeded, restored.Message);
        Assert.True(companionsAbsentBeforeValidation);
        Assert.True(File.Exists(targetDatabase));
    }

    [Fact]
    public void ExistingChannelMutexBlocksOperationAndHeldOperationBlocksRestart()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(environment.DataRoot);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new SingleInstanceService(environment.MutexName);
            Assert.True(mutex.TryAcquirePrimaryInstance());
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var blocked = environment.CreateService().CreateBackup(environment.BackupRequest());
            Assert.False(blocked.Succeeded);
            Assert.Contains("起動中", blocked.Message);
            Assert.Empty(Directory.Exists(environment.BackupDirectory)
                ? Directory.GetFiles(environment.BackupDirectory)
                : []);
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(5)));
        }

        bool? duplicateAcquired = null;
        var verified = environment.CreateService(checkpoint =>
        {
            if (checkpoint != "mutex.acquired")
            {
                return;
            }

            var duplicate = new Thread(() =>
            {
                using var mutex = new SingleInstanceService(environment.MutexName);
                duplicateAcquired = mutex.TryAcquirePrimaryInstance();
            });
            duplicate.Start();
            Assert.True(duplicate.Join(TimeSpan.FromSeconds(5)));
        }).CreateBackup(environment.BackupRequest());
        Assert.True(verified.Succeeded, verified.Message);
        Assert.False(duplicateAcquired ?? true);
    }

    [Theory]
    [InlineData("backup.after-collect", "access")]
    [InlineData("backup.before-archive-commit", "capacity")]
    public async Task BackupFailureDoesNotRemovePreviousVerifiedBackup(string checkpoint, string failureKind)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "正常");
        var first = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(first.Succeeded, first.Message);
        var originalArchive = Assert.IsType<string>(first.OutputPath);
        var originalHash = File.ReadAllText(originalArchive + ".sha256");
        var beforeFiles = Directory.GetFiles(environment.BackupDirectory).Order().ToArray();
        var failing = environment.CreateService(current =>
        {
            if (current == checkpoint)
            {
                if (failureKind == "access")
                {
                    throw new UnauthorizedAccessException();
                }

                throw new IOException("disk full simulation");
            }
        });

        var result = failing.CreateBackup(environment.BackupRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(beforeFiles, Directory.GetFiles(environment.BackupDirectory).Order());
        Assert.Equal(originalHash, File.ReadAllText(originalArchive + ".sha256"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(environment.BackupDirectory), static path =>
            Path.GetFileName(path).Contains(".partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CanceledBackupDoesNotCreateACompletedArchive()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "キャンセル");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = environment.CreateService().CreateBackup(environment.BackupRequest(), cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(environment.BackupDirectory)
            && Directory.EnumerateFiles(environment.BackupDirectory, "*.zip").Any());
    }

    [Fact]
    public async Task CanceledRestoreAfterMarkerRollsBackAndKeepsCurrentData()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        using var cancellation = new CancellationTokenSource();
        var service = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-marker")
            {
                cancellation.Cancel();
            }
        });

        var result = service.Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData, environment.FailureDetails(result.Message));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
    }

    [Fact]
    public async Task ValidArchiveRestoresOverCorruptCurrentDataAfterPreservingExactOriginalBytes()
    {
        using var environment = new TestEnvironment();
        var backupIds = await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);

        await environment.WriteSampleDataAsync(environment.DataRoot, "破損前");
        environment.WriteExcludedFiles(environment.DataRoot, "対象外は維持");
        var corruptSettings = new byte[] { 0x7B, 0x22, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22, 0x3A };
        var corruptSettingsBackup = Encoding.UTF8.GetBytes("null");
        File.WriteAllBytes(Path.Combine(environment.DataRoot, "settings.json"), corruptSettings);
        File.WriteAllBytes(Path.Combine(environment.DataRoot, "settings.backup.json"), corruptSettingsBackup);

        SqliteConnection.ClearAllPools();
        var workspacePath = Path.Combine(environment.DataRoot, "workspace.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(workspacePath + suffix);
        }
        CreateCrashStyleWalDatabase(environment, workspacePath);

        var originalBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings.json"] = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")),
            ["settings.backup.json"] = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.backup.json")),
            ["workspace.db"] = File.ReadAllBytes(workspacePath),
            ["workspace.db-wal"] = File.ReadAllBytes(workspacePath + "-wal"),
            ["workspace.db-shm"] = File.ReadAllBytes(workspacePath + "-shm")
        };
        var service = environment.CreateService();
        var archivePath = Assert.IsType<string>(backup.OutputPath);

        var plan = service.PlanRestore(environment.RestoreRequest(archivePath) with
        {
            Apply = false,
            Confirmation = null,
            ExpectedArchiveSha256 = null
        });
        Assert.True(plan.Succeeded, plan.Message);
        Assert.Contains("original-snapshot.json", plan.Message);

        var restored = service.Restore(environment.RestoreRequest(archivePath));

        Assert.True(restored.Succeeded, restored.Message);
        Assert.True(restored.ChangedData);
        Assert.Contains("破損", restored.Message);
        var recoveryPath = Assert.IsType<string>(restored.RecoveryPath);
        var rawDataRoot = Path.Combine(recoveryPath, "raw-current", "data");
        Assert.Equal(
            Path.Combine(recoveryPath, "raw-current", "original-snapshot.json"),
            Assert.IsType<string>(restored.OutputPath));
        foreach (var (relativePath, bytes) in originalBytes)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(rawDataRoot, relativePath)));
        }
        Assert.False(File.Exists(Path.Combine(rawDataRoot, "private", "secret.txt")));
        Assert.Contains("対象外は維持", File.ReadAllText(Path.Combine(
            environment.DataRoot,
            "private",
            "secret.txt")));
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(workspacePath + suffix))
            {
                Assert.False(originalBytes["workspace.db" + suffix]
                    .SequenceEqual(File.ReadAllBytes(workspacePath + suffix)));
            }
        }
        Assert.False(File.Exists(workspacePath + "-journal"));
        Assert.Equal(
            "バックアップ",
            new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(workspacePath);
        var workspace = Assert.Single(await repository.GetWorkspacesAsync());
        Assert.Equal(backupIds.WorkspaceId, workspace.Id);
        Assert.Equal("バックアップ", Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == backupIds.NoteId)).Content);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.backup.json")]
    [InlineData("workspace.db")]
    public async Task ValidArchiveRestoresWhenOneCurrentManagedFileIsCorrupt(string relativePath)
    {
        using var environment = new TestEnvironment();
        var backupIds = await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        SqliteConnection.ClearAllPools();
        var targetPath = Path.Combine(environment.DataRoot, relativePath);
        var corruptBytes = relativePath == "workspace.db"
            ? new byte[] { 0x4E, 0x4F, 0x54, 0x2D, 0x53, 0x51, 0x4C, 0x49, 0x54, 0x45 }
            : Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(targetPath, corruptBytes);

        var restored = environment.CreateService().Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.True(restored.Succeeded, restored.Message);
        Assert.True(restored.ChangedData);
        Assert.Equal(
            corruptBytes,
            File.ReadAllBytes(Path.Combine(
                Assert.IsType<string>(restored.RecoveryPath),
                "raw-current",
                "data",
                relativePath)));
        Assert.Equal(
            "バックアップ",
            new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        var workspace = Assert.Single(await repository.GetWorkspacesAsync());
        Assert.Equal(backupIds.WorkspaceId, workspace.Id);
        Assert.Equal("バックアップ", Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id),
            item => item.Id == backupIds.NoteId)).Content);
    }

    [Theory]
    [InlineData(5, "使用中")]
    [InlineData(6, "ロック中")]
    [InlineData(8, "アクセス")]
    [InlineData(10, "I/O障害")]
    [InlineData(13, "容量")]
    [InlineData(14, "アクセス")]
    [InlineData(1, "未分類")]
    public async Task SqliteEnvironmentalFailureStopsRestoreWithoutRawPreservation(
        int sqliteErrorCode,
        string expectedMessage)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json"));
        var workspaceBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db"));
        var service = environment.CreateService(currentDataValidationFailure: relativePath =>
            string.Equals(relativePath, "workspace.db", StringComparison.OrdinalIgnoreCase)
                ? new KitenDataValidationException(
                    KitenDataValidationStage.Transfer,
                    new SqliteException("environment failure simulation", sqliteErrorCode, sqliteErrorCode))
                : null);

        var result = service.Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains(expectedMessage, result.Message);
        Assert.Null(result.RecoveryPath);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")));
        Assert.Equal(workspaceBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db")));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public async Task DestinationContentStyleSqliteErrorDoesNotMisclassifyValidSourceAsCorrupt(int sqliteErrorCode)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json"));
        var workspaceBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db"));
        var service = environment.CreateService(currentDataValidationFailure: relativePath =>
            string.Equals(relativePath, "workspace.db", StringComparison.OrdinalIgnoreCase)
                ? new KitenDataValidationException(
                    KitenDataValidationStage.Transfer,
                    new SqliteException("destination failure simulation", sqliteErrorCode, sqliteErrorCode))
                : null);

        var result = service.Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("SQLite処理", result.Message);
        Assert.Null(result.RecoveryPath);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")));
        Assert.Equal(workspaceBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db")));
    }

    [Fact]
    public async Task FailedRestoreReturnsToCorruptOriginalBytesAndKeepsRawPreservation()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptBytes = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptBytes);
        var service = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new IOException("restore failure simulation");
            }
        });

        var result = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("破損原本", result.Message);
        Assert.Contains("正常利用可能とは確認していません", result.Message);
        Assert.Equal(corruptBytes, File.ReadAllBytes(settingsPath));
        var recoveryPath = Assert.IsType<string>(result.RecoveryPath);
        Assert.Equal(
            corruptBytes,
            File.ReadAllBytes(Path.Combine(recoveryPath, "raw-current", "data", "settings.json")));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task InterruptedRestoreCanExplicitlyReturnToCorruptOriginalBytes()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptBytes = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptBytes);
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        var recoveryPath = Assert.IsType<string>(interrupted.RecoveryPath);
        var rawSettingsPath = Path.Combine(recoveryPath, "raw-current", "data", "settings.json");
        Assert.Equal(corruptBytes, File.ReadAllBytes(rawSettingsPath));
        var plan = environment.CreateService().PlanRollbackInterruptedRestore(
            new KitenRecoveryRequest(ApplicationChannel.Stable, environment.DataRoot));
        Assert.True(plan.Succeeded, plan.Message);
        Assert.Contains("破損状態へ戻すだけ", plan.Message);

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Contains("正常データになったことは確認していません", recovered.Message);
        Assert.Equal(corruptBytes, File.ReadAllBytes(settingsPath));
        Assert.Equal(corruptBytes, File.ReadAllBytes(rawSettingsPath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Theory]
    [InlineData("restore.rollback.after-secure-target:settings.json")]
    [InlineData("restore.rollback.after-restore-original:settings.json")]
    public async Task InterruptedRawRollbackCanResumeFromPreservedOriginal(string rollbackCheckpoint)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptBytes = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptBytes);
        var interruptedRestore = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.False(interruptedRestore.Succeeded);
        Assert.True(interruptedRestore.ChangedData);

        var interruptedRollback = environment.CreateService(checkpoint =>
        {
            if (checkpoint == rollbackCheckpoint)
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));
        Assert.False(interruptedRollback.Succeeded);
        Assert.True(interruptedRollback.ChangedData);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.Equal(corruptBytes, File.ReadAllBytes(settingsPath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("access")]
    [InlineData("cancel")]
    public async Task RawPreservationFailureOrCancellationDoesNotChangeCurrentData(string failureKind)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptBytes = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptBytes);
        using var cancellation = new CancellationTokenSource();
        var service = environment.CreateService(checkpoint =>
        {
            if (checkpoint != "restore.raw-preservation.after-file:settings.json")
            {
                return;
            }

            if (failureKind == "cancel")
            {
                cancellation.Cancel();
            }
            else if (failureKind == "access")
            {
                throw new UnauthorizedAccessException("raw preservation access simulation");
            }
            else
            {
                throw new IOException("disk full simulation");
            }
        });

        var result = service.Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains(
            failureKind == "cancel"
                ? "キャンセル"
                : failureKind == "access" ? "アクセス" : "ファイル操作",
            result.Message);
        Assert.Equal(corruptBytes, File.ReadAllBytes(settingsPath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetDirectoryName(environment.DataRoot)!,
            ".kiten-restore-stable-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task UnknownFutureCurrentFormatIsNotReplacedByAnOlderBackup()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var futureBytes = Encoding.UTF8.GetBytes("{\"version\":999,\"searchEngine\":\"将来形式\"}");
        File.WriteAllBytes(settingsPath, futureBytes);

        var result = environment.CreateService().Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("未対応", result.Message);
        Assert.Equal(futureBytes, File.ReadAllBytes(settingsPath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));

        SettingsService.SaveToFile(settingsPath, new AppSettings { SearchEngine = "現在" });
        var workspacePath = Path.Combine(environment.DataRoot, "workspace.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspacePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            await command.ExecuteNonQueryAsync();
        }

        var sqliteResult = environment.CreateService().Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.False(sqliteResult.Succeeded);
        Assert.False(sqliteResult.ChangedData);
        Assert.Contains("未対応", sqliteResult.Message);
        await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspacePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verification.OpenAsync();
        using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(999L, await versionCommand.ExecuteScalarAsync());
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("named")]
    public async Task EarlierContentCorruptionDoesNotHideLaterUnsupportedFormat(string futureTarget)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptSettings = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptSettings);
        string futurePath;
        if (futureTarget == "workspace")
        {
            futurePath = Path.Combine(environment.DataRoot, "workspace.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = futurePath,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            futurePath = futureTarget == "session"
                ? Path.Combine(environment.DataRoot, "session.json")
                : Path.Combine(environment.DataRoot, "session.named", "future.json");
            Directory.CreateDirectory(Path.GetDirectoryName(futurePath)!);
            File.WriteAllText(
                futurePath,
                "{\"version\":999,\"tabs\":[]}",
                new UTF8Encoding(false));
        }

        var futureBytes = File.ReadAllBytes(futurePath);
        var request = environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath));
        var plan = environment.CreateService().PlanRestore(request with
        {
            Apply = false,
            Confirmation = null,
            ExpectedArchiveSha256 = null
        });
        var result = environment.CreateService().Restore(request);

        Assert.False(plan.Succeeded);
        Assert.Contains("未対応", plan.Message);
        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("未対応", result.Message);
        Assert.Equal(corruptSettings, File.ReadAllBytes(settingsPath));
        Assert.Equal(futureBytes, File.ReadAllBytes(futurePath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task UnsupportedFormatBeforeLaterContentCorruptionAlsoRejectsWithoutChanges()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var futureSettings = Encoding.UTF8.GetBytes("{\"version\":999,\"searchEngine\":\"将来\"}");
        File.WriteAllBytes(settingsPath, futureSettings);
        var workspacePath = Path.Combine(environment.DataRoot, "workspace.db");
        var corruptWorkspace = Encoding.UTF8.GetBytes("NOT-SQLITE");
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(workspacePath, corruptWorkspace);

        var result = environment.CreateService().Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("未対応", result.Message);
        Assert.Equal(futureSettings, File.ReadAllBytes(settingsPath));
        Assert.Equal(corruptWorkspace, File.ReadAllBytes(workspacePath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task MultipleContentCorruptionsWithoutBlockingFailureUseRawPreservation()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var bookmarksPath = Path.Combine(environment.DataRoot, "bookmarks.json");
        var corruptSettings = Encoding.UTF8.GetBytes("{\"version\":");
        var corruptBookmarks = Encoding.UTF8.GetBytes("[null]");
        File.WriteAllBytes(settingsPath, corruptSettings);
        File.WriteAllBytes(bookmarksPath, corruptBookmarks);

        var result = environment.CreateService().Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.True(result.Succeeded, result.Message);
        var rawDataRoot = Path.Combine(
            Assert.IsType<string>(result.RecoveryPath),
            "raw-current",
            "data");
        Assert.Equal(corruptSettings, File.ReadAllBytes(Path.Combine(rawDataRoot, "settings.json")));
        Assert.Equal(corruptBookmarks, File.ReadAllBytes(Path.Combine(rawDataRoot, "bookmarks.json")));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    public async Task ContentCorruptionDoesNotHideLaterEnvironmentalFailure(string failureKind)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var corruptSettings = Encoding.UTF8.GetBytes("{\"version\":");
        File.WriteAllBytes(settingsPath, corruptSettings);
        var workspacePath = Path.Combine(environment.DataRoot, "workspace.db");
        var workspaceBefore = File.ReadAllBytes(workspacePath);
        var service = environment.CreateService(currentDataValidationFailure: relativePath =>
            string.Equals(relativePath, "workspace.db", StringComparison.OrdinalIgnoreCase)
                ? failureKind == "access"
                    ? new UnauthorizedAccessException("access failure simulation")
                    : new IOException("I/O failure simulation")
                : null);

        var result = service.Restore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains(failureKind == "access" ? "アクセス" : "ファイル操作", result.Message);
        Assert.Equal(corruptSettings, File.ReadAllBytes(settingsPath));
        Assert.Equal(workspaceBefore, File.ReadAllBytes(workspacePath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task RestoreRechecksCurrentFormatsAfterSuccessfulPlan()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var request = environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath));
        var plan = environment.CreateService().PlanRestore(request with
        {
            Apply = false,
            Confirmation = null,
            ExpectedArchiveSha256 = null
        });
        Assert.True(plan.Succeeded, plan.Message);
        var settingsBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json"));
        var workspacePath = Path.Combine(environment.DataRoot, "workspace.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspacePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            await command.ExecuteNonQueryAsync();
        }

        var result = environment.CreateService().Restore(request);

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData);
        Assert.Contains("未対応", result.Message);
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")));
        await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspacePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await verification.OpenAsync();
        using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(999L, await versionCommand.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("restore.after-marker")]
    [InlineData("restore.after-target:settings.json")]
    [InlineData("restore.before-final-validation")]
    public async Task RestoreFailureAtEachLogicalChangeStageRollsBackCurrentData(string checkpoint)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var current = await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var failing = environment.CreateService(currentCheckpoint =>
        {
            if (currentCheckpoint == checkpoint)
            {
                throw new IOException("restore failure simulation");
            }
        });

        var result = failing.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(result.Succeeded);
        Assert.False(result.ChangedData, environment.FailureDetails(result.Message));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        var workspace = Assert.Single(await repository.GetWorkspacesAsync());
        Assert.Equal(current.WorkspaceId, workspace.Id);
        Assert.Equal("現在", Assert.IsType<NoteItem>(Assert.Single(
            await repository.GetWorkspaceItemsAsync(workspace.Id), item => item.Id == current.NoteId)).Content);
    }

    [Fact]
    public async Task PackageCleanupFailureDoesNotMisreportACommittedRestoreAsFailed()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        FileStream? cleanupBlocker = null;
        try
        {
            var service = environment.CreateService(checkpoint =>
            {
                if (checkpoint != "restore.before-package-cleanup")
                {
                    return;
                }

                var dataParent = Path.GetDirectoryName(environment.DataRoot)!;
                var workRoot = Assert.Single(Directory.EnumerateDirectories(
                    dataParent,
                    ".kiten-restore-stable-*",
                    SearchOption.TopDirectoryOnly));
                cleanupBlocker = new FileStream(
                    Path.Combine(workRoot, "package", "data", "settings.json"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            });

            var restored = service.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

            Assert.True(restored.Succeeded, restored.Message);
            Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
            Assert.Equal(
                "バックアップ",
                new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        }
        finally
        {
            cleanupBlocker?.Dispose();
        }
    }

    [Fact]
    public async Task InterruptedRestoreLeavesMarkerAndExplicitRecoveryRollsBack()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var current = await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        var blocked = environment.CreateService().PlanBackup(environment.BackupRequest());
        Assert.False(blocked.Succeeded);
        Assert.Contains("ROLLBACK", blocked.Message);

        var recoveryPlan = environment.CreateService().PlanRollbackInterruptedRestore(
            new KitenRecoveryRequest(ApplicationChannel.Stable, environment.DataRoot));
        Assert.True(recoveryPlan.Succeeded, environment.FailureDetails(recoveryPlan.Message));
        Assert.False(recoveryPlan.ChangedData);
        Assert.Contains("操作ID=", recoveryPlan.Message);
        Assert.Contains("journal=", recoveryPlan.Message);
        Assert.Contains("復元前バックアップ=", recoveryPlan.Message);

        var recovered = environment.CreateService().RollbackInterruptedRestore(
            new KitenRecoveryRequest(
                ApplicationChannel.Stable,
                environment.DataRoot,
                KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, recovered.Message);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        Assert.Equal(current.WorkspaceId, Assert.Single(await repository.GetWorkspacesAsync()).Id);
    }

    [Fact]
    public async Task RollbackValidationWorksWhenDataRootHasALongPath()
    {
        using var environment = new TestEnvironment();
        const int targetDataRootLength = 170;
        var paddingLength = targetDataRootLength
                            - environment.BasePath.Length
                            - "TabBrowser".Length
                            - 2;
        Assert.True(paddingLength > 0);
        var dataRoot = environment.OtherDataRoot(new string('d', paddingLength));
        Assert.Equal(targetDataRootLength, dataRoot.Length);
        await environment.WriteSampleDataAsync(dataRoot, "バックアップ");
        var backupRequest = environment.BackupRequest() with { DataRoot = dataRoot };
        var backup = environment.CreateService().CreateBackup(backupRequest);
        Assert.True(backup.Succeeded, backup.Message);
        var current = await environment.WriteSampleDataAsync(dataRoot, "現在");
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath), dataRoot));
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData, environment.FailureDetails(interrupted.Message));

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            dataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, environment.FailureDetails(recovered.Message));
        Assert.False(File.Exists(Path.Combine(dataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(dataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(Path.Combine(dataRoot, "workspace.db"));
        Assert.Equal(current.WorkspaceId, Assert.Single(await repository.GetWorkspacesAsync()).Id);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task RollbackRejectsChangedOrMissingActualSourceBeforeChangingCurrentData(
        bool rawPreservation,
        bool deleteSource,
        bool planFirst)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var originalSettings = Encoding.UTF8.GetBytes(rawPreservation ? "{\"version\":" : File.ReadAllText(
            Path.Combine(environment.DataRoot, "settings.json"), Encoding.UTF8));
        if (rawPreservation)
        {
            File.WriteAllBytes(Path.Combine(environment.DataRoot, "settings.json"), originalSettings);
        }

        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:workspace.db")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData);
        var recoveryPath = Assert.IsType<string>(interrupted.RecoveryPath);
        if (planFirst)
        {
            var plan = environment.CreateService().PlanRollbackInterruptedRestore(
                new KitenRecoveryRequest(ApplicationChannel.Stable, environment.DataRoot));
            Assert.True(plan.Succeeded, environment.FailureDetails(plan.Message));
        }

        var preservedSettingsPath = rawPreservation
            ? Path.Combine(recoveryPath, "raw-current", "data", "settings.json")
            : Path.Combine(recoveryPath, "original", "settings.json");
        if (deleteSource)
        {
            File.Delete(preservedSettingsPath);
        }
        else
        {
            File.WriteAllText(preservedSettingsPath, "tampered", new UTF8Encoding(false));
        }

        var currentSettingsBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json"));
        var currentWorkspaceBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db"));
        var result = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.False(result.Succeeded);
        Assert.True(result.ChangedData);
        Assert.Contains("検証", result.Message);
        Assert.Equal(currentSettingsBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")));
        Assert.Equal(currentWorkspaceBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "workspace.db")));
        Assert.Equal(!deleteSource, File.Exists(preservedSettingsPath));
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.True(File.Exists(Path.Combine(recoveryPath, "restore-journal.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackUsesVerifiedWorkCopyIfOriginalChangesAfterValidation(bool rawPreservation)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var settingsPath = Path.Combine(environment.DataRoot, "settings.json");
        var expectedOriginal = rawPreservation
            ? Encoding.UTF8.GetBytes("{\"version\":")
            : File.ReadAllBytes(settingsPath);
        if (rawPreservation)
        {
            File.WriteAllBytes(settingsPath, expectedOriginal);
        }

        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        var recoveryPath = Assert.IsType<string>(interrupted.RecoveryPath);
        var preservedSettingsPath = rawPreservation
            ? Path.Combine(recoveryPath, "raw-current", "data", "settings.json")
            : Path.Combine(recoveryPath, "original", "settings.json");
        var changedAfterValidation = Encoding.UTF8.GetBytes("changed-after-validation");
        var result = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.rollback.after-source-validation")
            {
                File.WriteAllBytes(preservedSettingsPath, changedAfterValidation);
            }
        }).RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(result.Succeeded, environment.FailureDetails(result.Message));
        Assert.Equal(expectedOriginal, File.ReadAllBytes(settingsPath));
        Assert.Equal(changedAfterValidation, File.ReadAllBytes(preservedSettingsPath));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task RollbackRejectsJournalAndPreservedSourceMismatchBeforeChangingCurrentData()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        var recoveryPath = Assert.IsType<string>(interrupted.RecoveryPath);
        var journalPath = Path.Combine(recoveryPath, "restore-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        var settingsEntry = journal["entries"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(entry => string.Equals(
                entry["relativePath"]!.GetValue<string>(),
                "settings.json",
                StringComparison.OrdinalIgnoreCase));
        settingsEntry["originalPresent"] = false;
        File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));
        var currentSettingsBefore = File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json"));

        var result = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.False(result.Succeeded);
        Assert.True(result.ChangedData);
        Assert.Contains("検証", result.Message);
        Assert.Equal(currentSettingsBefore, File.ReadAllBytes(Path.Combine(environment.DataRoot, "settings.json")));
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.True(File.Exists(Path.Combine(recoveryPath, "original", "settings.json")));
    }

    [Fact]
    public async Task VersionOneRecoveryJournalRemainsReadable()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));
        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData);

        var journalPath = Path.Combine(Assert.IsType<string>(interrupted.RecoveryPath), "restore-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        journal["schemaVersion"] = 1;
        journal.Remove("preRestoreStateKind");
        File.WriteAllText(journalPath, journal.ToJsonString(), new UTF8Encoding(false));

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, environment.FailureDetails(recovered.Message));
        Assert.Equal(
            "現在",
            new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task FailedAutomaticRollbackKeepsRecoveryStateAndNextRecoveryCanFinish()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var rollbackFailurePending = true;
        var failing = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new IOException("restore failure simulation");
            }

            if (checkpoint == "restore.rollback.before-target:settings.json" && rollbackFailurePending)
            {
                rollbackFailurePending = false;
                throw new IOException("rollback failure simulation");
            }
        });

        var failed = failing.Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(failed.Succeeded);
        Assert.True(failed.ChangedData);
        Assert.NotNull(failed.RecoveryPath);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.True(File.Exists(Path.Combine(failed.RecoveryPath!, "restore-journal.json")));
        var explicitFailure = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.rollback.before-target:settings.json")
            {
                throw new IOException("explicit rollback failure simulation");
            }
        }).RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));
        Assert.False(explicitFailure.Succeeded);
        Assert.True(explicitFailure.ChangedData);
        Assert.Equal(failed.RecoveryPath, explicitFailure.RecoveryPath);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));
        Assert.True(recovered.Succeeded, environment.FailureDetails(recovered.Message));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
    }

    [Theory]
    [InlineData("restore.rollback.after-secure-target:settings.json")]
    [InlineData("restore.rollback.after-restore-original:settings.json")]
    public async Task InterruptedRollbackCanResumeFromItsDurableJournal(string rollbackCheckpoint)
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "バックアップ");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var current = await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var interrupted = environment.CreateService(checkpoint =>
        {
            if (checkpoint == "restore.after-target:settings.json")
            {
                throw new IOException("restore failure simulation");
            }

            if (checkpoint == rollbackCheckpoint)
            {
                throw new KitenSimulatedInterruptionException();
            }
        }).Restore(environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)));

        Assert.False(interrupted.Succeeded);
        Assert.True(interrupted.ChangedData);
        Assert.True(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));

        var recovered = environment.CreateService().RollbackInterruptedRestore(new KitenRecoveryRequest(
            ApplicationChannel.Stable,
            environment.DataRoot,
            KitenDataMaintenanceService.RollbackConfirmation));

        Assert.True(recovered.Succeeded, environment.FailureDetails(recovered.Message));
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        var repository = new SQLiteWorkspaceRepository(Path.Combine(environment.DataRoot, "workspace.db"));
        Assert.Equal(current.WorkspaceId, Assert.Single(await repository.GetWorkspacesAsync()).Id);
    }

    [Fact]
    public async Task InvalidArchivesAreRejectedWithoutChangingCurrentData()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "維持");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var archive = Assert.IsType<string>(backup.OutputPath);
        var invalidArchives = new List<string>
        {
            CreateHashMismatchArchive(environment, archive),
            CreateCorruptArchiveWithMatchingHash(environment),
            CreateArchiveWithUnexpectedPath(environment, archive),
            CreateArchiveWithInvalidWindowsPath(environment, archive),
            CreateArchiveWithDuplicateEntry(environment, archive),
            CreateArchiveWithUnknownManifestVersion(environment, archive),
            CreateArchiveWithUnknownDataVersion(environment, archive)
        };

        foreach (var invalid in invalidArchives)
        {
            var before = File.ReadAllText(Path.Combine(environment.DataRoot, "settings.json"));
            var plan = environment.CreateService().PlanRestore(environment.RestoreRequest(invalid) with
            {
                Apply = false,
                Confirmation = null
            });
            Assert.False(plan.Succeeded);
            Assert.Equal(before, File.ReadAllText(Path.Combine(environment.DataRoot, "settings.json")));
            Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
        }

        var directRestore = environment.CreateService().Restore(environment.RestoreRequest(invalidArchives[0]));
        Assert.False(directRestore.Succeeded);
        Assert.Equal("維持", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task RestoreRequiresExplicitApplyAndConfirmationAndMarkerDirectoryBlocksMaintenance()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "確認前");
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var archivePath = Assert.IsType<string>(backup.OutputPath);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");

        var plan = service.PlanRestore(environment.RestoreRequest(archivePath) with
        {
            Apply = false,
            Confirmation = null
        });
        var noApply = service.Restore(environment.RestoreRequest(archivePath) with { Apply = false });
        var noConfirmation = service.Restore(environment.RestoreRequest(archivePath) with { Confirmation = null });

        Assert.True(plan.Succeeded, plan.Message);
        Assert.False(plan.ChangedData);
        Assert.Contains("settings.json", plan.Message);
        Assert.Contains("復元時点", plan.Message);
        Assert.Contains("バックアップ版=未取得", plan.Message);
        Assert.Contains("対象分類=基本対象", plan.Message);
        Assert.Contains("閲覧履歴=除外", plan.Message);
        Assert.Contains("最近項目=除外", plan.Message);
        Assert.Contains("ダウンロード履歴=除外", plan.Message);
        Assert.Equal(
            KitenDataMaintenanceArchive.ComputeSha256(archivePath),
            plan.VerifiedArchiveSha256,
            ignoreCase: true);
        Assert.False(noApply.Succeeded);
        Assert.False(noConfirmation.Succeeded);
        var noExpectedHash = service.Restore(
            environment.RestoreRequest(archivePath) with { ExpectedArchiveSha256 = null });
        Assert.False(noExpectedHash.Succeeded);
        Assert.Contains("SHA-256", noExpectedHash.Message);
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);

        Directory.CreateDirectory(Path.Combine(environment.DataRoot, "restore.in-progress"));
        var blocked = service.PlanBackup(environment.BackupRequest());
        Assert.False(blocked.Succeeded);
        Assert.Contains("完了していません", blocked.Message);
    }

    [Fact]
    public async Task RestoreRejectsArchiveReplacedAfterPlanEvenWhenReplacementHashIsValid()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "確認対象");
        var service = environment.CreateService();
        var firstBackup = service.CreateBackup(environment.BackupRequest());
        Assert.True(firstBackup.Succeeded, firstBackup.Message);
        var plannedArchive = Assert.IsType<string>(firstBackup.OutputPath);
        var plan = service.PlanRestore(environment.RestoreRequest(plannedArchive) with
        {
            Apply = false,
            Confirmation = null,
            ExpectedArchiveSha256 = null
        });
        var plannedHash = Assert.IsType<string>(plan.VerifiedArchiveSha256);

        await environment.WriteSampleDataAsync(environment.DataRoot, "別の正常バックアップ");
        var secondBackup = service.CreateBackup(environment.BackupRequest());
        Assert.True(secondBackup.Succeeded, secondBackup.Message);
        var replacementArchive = Assert.IsType<string>(secondBackup.OutputPath);
        File.Copy(replacementArchive, plannedArchive, overwrite: true);
        WriteArchiveHash(plannedArchive);
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在を維持");

        var restored = service.Restore(environment.RestoreRequest(plannedArchive) with
        {
            ExpectedArchiveSha256 = plannedHash
        });

        Assert.False(restored.Succeeded);
        Assert.False(restored.ChangedData);
        Assert.Contains("復元計画で確認したZIP", restored.Message);
        Assert.Equal(
            "現在を維持",
            new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        Assert.False(File.Exists(Path.Combine(environment.DataRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task ArchiveRejectsLinkEntriesAndRestoreRejectsWrongChannel()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "安全");
        var backup = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var archive = Assert.IsType<string>(backup.OutputPath);
        var linked = CopyArchive(environment, archive, "link-entry.zip");
        using (var zip = ZipFile.Open(linked, ZipArchiveMode.Update))
        {
            var entry = zip.CreateEntry("data/link.json");
            entry.ExternalAttributes = unchecked((int)0xA0000000);
        }
        WriteArchiveHash(linked);

        var linkResult = environment.CreateService().PlanRestore(environment.RestoreRequest(linked) with
        {
            Apply = false,
            Confirmation = null
        });
        var wrongRoot = environment.OtherDataRoot("development", ApplicationChannel.Development);
        var wrongChannel = environment.CreateService().PlanRestore(new KitenRestoreRequest(
            ApplicationChannel.Development,
            wrongRoot,
            archive));

        Assert.False(linkResult.Succeeded);
        Assert.False(wrongChannel.Succeeded);
        Assert.Contains("チャンネル", wrongChannel.Message);
    }

    [Fact]
    public async Task UnknownJsonFormatAndDestinationInsideDataRootAreRejected()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(environment.DataRoot);
        File.WriteAllText(Path.Combine(environment.DataRoot, "settings.json"), "{\"version\":999}");
        var formatResult = environment.CreateService().CreateBackup(environment.BackupRequest());
        Assert.False(formatResult.Succeeded);
        Assert.Contains("未対応", formatResult.Message);

        await environment.WriteSampleDataAsync(environment.DataRoot, "正常");
        var nestedResult = environment.CreateService().CreateBackup(environment.BackupRequest() with
        {
            DestinationDirectory = Path.Combine(environment.DataRoot, "backups")
        });
        Assert.False(nestedResult.Succeeded);
        Assert.Contains("データルート内", nestedResult.Message);
    }

    [Fact]
    public async Task InvalidStoreShapeAndIncompleteWorkspaceSchemaAreRejectedBeforeRestore()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "現在");
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        var sourceArchive = Assert.IsType<string>(backup.OutputPath);

        var invalidSettings = ReplaceArchiveData(
            environment,
            sourceArchive,
            "invalid-settings-shape.zip",
            "settings.json",
            Encoding.UTF8.GetBytes("[]"),
            formatVersion: null);
        var settingsResult = service.PlanRestore(
            environment.RestoreRequest(invalidSettings) with { Apply = false, Confirmation = null });
        Assert.False(settingsResult.Succeeded);
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);

        var unsupportedDatabasePath = Path.Combine(environment.BasePath, "unsupported-workspace.db");
        using (var connection = new SqliteConnection($"Data Source={unsupportedDatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999; CREATE TABLE unrelated(value TEXT);";
            command.ExecuteNonQuery();
        }

        var unsupportedWorkspace = ReplaceArchiveData(
            environment,
            sourceArchive,
            "unsupported-workspace-schema.zip",
            "workspace.db",
            File.ReadAllBytes(unsupportedDatabasePath),
            formatVersion: 999);
        var unsupportedResult = service.PlanRestore(
            environment.RestoreRequest(unsupportedWorkspace) with { Apply = false, Confirmation = null });
        Assert.False(unsupportedResult.Succeeded);
        Assert.Contains("未対応", unsupportedResult.Message);
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);

        var incompleteVersionTwoPath = Path.Combine(environment.BasePath, "incomplete-version-two-workspace.db");
        File.Copy(Path.Combine(environment.DataRoot, "workspace.db"), incompleteVersionTwoPath);
        using (var connection = new SqliteConnection($"Data Source={incompleteVersionTwoPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE workspace_items DROP COLUMN deleted_at; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }

        var incompleteVersionTwo = ReplaceArchiveData(
            environment,
            sourceArchive,
            "incomplete-version-two-workspace-schema.zip",
            "workspace.db",
            File.ReadAllBytes(incompleteVersionTwoPath),
            formatVersion: 2);
        var incompleteVersionTwoResult = service.PlanRestore(
            environment.RestoreRequest(incompleteVersionTwo) with { Apply = false, Confirmation = null });
        Assert.False(incompleteVersionTwoResult.Succeeded);
        Assert.Contains("schema", incompleteVersionTwoResult.Message);
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);

        foreach (var (tableName, columnName) in new[]
                 {
                     ("workspace_items", "note_font_size"),
                     ("workspace_session_tabs", "split_group_id")
                 })
        {
            var incompleteRuntimeColumnPath = Path.Combine(
                environment.BasePath,
                $"incomplete-version-two-{columnName}.db");
            File.Copy(Path.Combine(environment.DataRoot, "workspace.db"), incompleteRuntimeColumnPath);
            using (var connection = new SqliteConnection($"Data Source={incompleteRuntimeColumnPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE [{tableName}] DROP COLUMN [{columnName}]; PRAGMA user_version=2;";
                command.ExecuteNonQuery();
            }

            var incompleteRuntimeColumn = ReplaceArchiveData(
                environment,
                sourceArchive,
                $"incomplete-version-two-{columnName}.zip",
                "workspace.db",
                File.ReadAllBytes(incompleteRuntimeColumnPath),
                formatVersion: 2);
            var incompleteRuntimeColumnResult = service.PlanRestore(
                environment.RestoreRequest(incompleteRuntimeColumn) with { Apply = false, Confirmation = null });
            Assert.False(incompleteRuntimeColumnResult.Succeeded);
            Assert.Contains("schema", incompleteRuntimeColumnResult.Message);
            Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
        }

        var invalidDatabasePath = Path.Combine(environment.BasePath, "invalid-workspace.db");
        using (var connection = new SqliteConnection($"Data Source={invalidDatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=0; CREATE TABLE unrelated(value TEXT);";
            command.ExecuteNonQuery();
        }

        var invalidWorkspace = ReplaceArchiveData(
            environment,
            sourceArchive,
            "invalid-workspace-schema.zip",
            "workspace.db",
            File.ReadAllBytes(invalidDatabasePath),
            formatVersion: 0);
        var invalidResult = service.PlanRestore(
            environment.RestoreRequest(invalidWorkspace) with { Apply = false, Confirmation = null });
        Assert.False(invalidResult.Succeeded);
        Assert.Contains("schema", invalidResult.Message);
        Assert.Equal("現在", new SettingsService(Path.Combine(environment.DataRoot, "settings.json")).Load().SearchEngine);
    }

    [Fact]
    public async Task RestorePlanHonorsCancellationAndArchiveLimitsIncludeManifest()
    {
        using var environment = new TestEnvironment();
        await environment.WriteSampleDataAsync(environment.DataRoot, "キャンセル");
        var service = environment.CreateService();
        var backup = service.CreateBackup(environment.BackupRequest());
        Assert.True(backup.Succeeded, backup.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = service.PlanRestore(
            environment.RestoreRequest(Assert.IsType<string>(backup.OutputPath)) with
            {
                Apply = false,
                Confirmation = null
            },
            cancellation.Token);
        Assert.False(canceled.Succeeded);
        Assert.Contains("キャンセル", canceled.Message);

        var manifest = new KitenBackupManifest
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Channel = "stable",
            Files = Enumerable.Range(0, KitenDataMaintenanceArchive.MaximumArchiveEntries)
                .Select(index => new KitenBackupFileEntry
                {
                    RelativePath = $"session.snapshots/snapshot-{index:D4}.json",
                    Present = true,
                    Kind = KitenManagedFileKind.Json,
                    Sha256 = new string('A', 64)
                })
                .ToList(),
            Collections = KitenDataMaintenanceArchive.ManagedCollectionPaths
                .Select(path => new KitenBackupCollectionEntry
                {
                    RelativePath = path,
                    Present = true
                })
                .ToList()
        };
        var manifestPath = Path.Combine(environment.BasePath, "limit-manifest.json");
        KitenDataMaintenanceArchive.WriteManifest(manifestPath, manifest);
        Assert.Throws<InvalidDataException>(() =>
            KitenDataMaintenanceArchive.ValidateArchiveCreationLimits(manifest, manifestPath));
    }

    [Fact]
    public void ConflictingManagedPathTypesAndOrphanSqliteCompanionsAreRejected()
    {
        using var environment = new TestEnvironment();
        Directory.CreateDirectory(environment.DataRoot);
        var service = environment.CreateService();

        Directory.CreateDirectory(Path.Combine(environment.DataRoot, "settings.json"));
        var fileConflict = service.CreateBackup(environment.BackupRequest());
        Assert.False(fileConflict.Succeeded);
        Assert.Contains("同名のフォルダー", fileConflict.Message);
        Directory.Delete(Path.Combine(environment.DataRoot, "settings.json"));

        File.WriteAllText(Path.Combine(environment.DataRoot, "workspace.db-wal"), "orphan");
        var orphanCompanion = service.CreateBackup(environment.BackupRequest());
        Assert.False(orphanCompanion.Succeeded);
        Assert.Contains("SQLite本体", orphanCompanion.Message);
        File.Delete(Path.Combine(environment.DataRoot, "workspace.db-wal"));

        File.WriteAllText(Path.Combine(environment.DataRoot, "session.named"), "conflict");
        var directoryConflict = service.CreateBackup(environment.BackupRequest());
        Assert.False(directoryConflict.Succeeded);
        Assert.Contains("同名のファイル", directoryConflict.Message);
    }

    [Fact]
    public void SourceAndScriptsKeepStartupAndDistributionSafetyContracts()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        var project = File.ReadAllText(Path.Combine(root, "tools", "Kiten.DataTool", "Kiten.DataTool.csproj"));
        var backupScript = File.ReadAllText(Path.Combine(root, "tools", "Backup-KitenData.ps1"));
        var restoreScript = File.ReadAllText(Path.Combine(root, "tools", "Restore-KitenData.ps1"));

        Assert.Contains("RestoreInProgressMarkerFile", app);
        Assert.True(app.IndexOf("RestoreInProgressMarkerFile", StringComparison.Ordinal)
                    < app.IndexOf("_applicationStabilityService.StartRun()", StringComparison.Ordinal));
        Assert.DoesNotContain("MessageBox.Show", app);
        Assert.Contains("RestoreIncompleteWarningWindow", app);
        Assert.True(app.IndexOf("RestoreInProgressMarkerFile", StringComparison.Ordinal)
                    < app.IndexOf("_isPrimaryInstance = true", StringComparison.Ordinal));
        var restoreWarningXaml = File.ReadAllText(Path.Combine(root, "RestoreIncompleteWarningWindow.xaml"));
        Assert.Contains("DarkWindowBackgroundBrush", restoreWarningXaml);
        Assert.Contains("DarkDialogButtonStyle", restoreWarningXaml);
        Assert.Contains("Microsoft.Data.Sqlite", project);
        Assert.Contains("SQLitePCLRaw.bundle_e_sqlite3", project);
        Assert.DoesNotContain("dotnet ", backupScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlite3", backupScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read-Host", backupScript);
        Assert.Contains("BACKUP", backupScript);
        Assert.Contains("IncludeBrowsingHistory", backupScript);
        Assert.Contains("IncludeRecentItems", backupScript);
        Assert.Contains("IncludeDownloadHistory", backupScript);
        Assert.DoesNotContain("exit ", backupScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--plan", restoreScript);
        Assert.Contains("recover --channel $Channel --data-root $DataRoot --plan", restoreScript);
        Assert.Contains("RESTORE", restoreScript);
        Assert.Contains("確認済みZIP SHA-256", restoreScript);
        Assert.Contains("--expected-sha256", restoreScript);
        Assert.Contains("ROLLBACK", restoreScript);
        Assert.DoesNotContain("exit ", restoreScript, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadArchiveEntryNames(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(static entry => entry.FullName).ToList();
    }

    private static JsonElement ReadManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry("backup-info.json")!.Open();
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string CreateHashMismatchArchive(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "hash-mismatch.zip");
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(0x42);
        }
        return path;
    }

    private static string CreateCorruptArchiveWithMatchingHash(TestEnvironment environment)
    {
        var path = Path.Combine(environment.BasePath, "corrupt.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0x01]);
        WriteArchiveHash(path);
        return path;
    }

    private static string CreateArchiveWithUnexpectedPath(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "unexpected-path.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("data/../outside.json").Open());
            writer.Write("{}");
        }
        WriteArchiveHash(path);
        return path;
    }

    private static string CreateArchiveWithInvalidWindowsPath(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "invalid-windows-path.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("data/C:/outside.json").Open());
            writer.Write("{}");
        }
        WriteArchiveHash(path);
        return path;
    }

    private static string CreateArchiveWithDuplicateEntry(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "duplicate-entry.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("data/settings.json").Open());
            writer.Write("{}");
        }
        WriteArchiveHash(path);
        return path;
    }

    private static string CreateArchiveWithUnknownManifestVersion(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "unknown-version.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var manifest = archive.GetEntry("backup-info.json")!;
            manifest.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("backup-info.json").Open());
            writer.Write("{\"schemaVersion\":999,\"productName\":\"Kiten Gesture Browser\"}");
        }
        WriteArchiveHash(path);
        return path;
    }

    private static string CreateArchiveWithUnknownDataVersion(TestEnvironment environment, string source)
    {
        var path = CopyArchive(environment, source, "unknown-data-version.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var settingsEntry = archive.GetEntry("data/settings.json")!;
            settingsEntry.Delete();
            var bytes = Encoding.UTF8.GetBytes("{\"version\":999}");
            using (var stream = archive.CreateEntry("data/settings.json").Open())
            {
                stream.Write(bytes);
            }

            var manifestEntry = archive.GetEntry("backup-info.json")!;
            JsonObject manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonNode.Parse(stream)!.AsObject();
            }
            manifestEntry.Delete();
            var file = manifest["files"]!.AsArray()
                .Select(static node => node!.AsObject())
                .Single(item => item["relativePath"]!.GetValue<string>() == "settings.json");
            file["size"] = bytes.LongLength;
            file["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            file["formatVersion"] = 999;
            using var writer = new StreamWriter(archive.CreateEntry("backup-info.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        WriteArchiveHash(path);
        return path;
    }

    private static string CopyArchive(TestEnvironment environment, string source, string fileName)
    {
        var path = Path.Combine(environment.BasePath, fileName);
        File.Copy(source, path);
        WriteArchiveHash(path);
        return path;
    }

    private static string ReplaceArchiveData(
        TestEnvironment environment,
        string source,
        string fileName,
        string relativePath,
        byte[] bytes,
        int? formatVersion)
    {
        var path = CopyArchive(environment, source, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var dataEntry = archive.GetEntry("data/" + relativePath)!;
            dataEntry.Delete();
            using (var output = archive.CreateEntry("data/" + relativePath).Open())
            {
                output.Write(bytes);
            }

            var manifestEntry = archive.GetEntry("backup-info.json")!;
            JsonObject manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonNode.Parse(stream)!.AsObject();
            }

            manifestEntry.Delete();
            var file = manifest["files"]!.AsArray()
                .Select(static node => node!.AsObject())
                .Single(item => item["relativePath"]!.GetValue<string>() == relativePath);
            file["size"] = bytes.LongLength;
            file["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            file["formatVersion"] = formatVersion;
            using var writer = new StreamWriter(archive.CreateEntry("backup-info.json").Open());
            writer.Write(manifest.ToJsonString());
        }

        WriteArchiveHash(path);
        return path;
    }

    private static void WriteArchiveHash(string archivePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        File.WriteAllText(archivePath + ".sha256", $"{hash}  {Path.GetFileName(archivePath)}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private static void CreateCrashStyleWalDatabase(TestEnvironment environment, string targetPath)
    {
        var livePath = Path.Combine(environment.BasePath, "live-wal.db");
        _ = new SQLiteWorkspaceRepository(livePath).GetWorkspacesAsync().GetAwaiter().GetResult();
        using (var connection = new SqliteConnection($"Data Source={livePath};Pooling=False"))
        {
            connection.Open();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA user_version=0; CREATE TABLE wal_probe(value TEXT NOT NULL);";
                setup.ExecuteNonQuery();
            }
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO wal_probe(value) VALUES ('committed-in-wal');";
                insert.ExecuteNonQuery();
            }

            File.Copy(livePath, targetPath);
            File.Copy(livePath + "-wal", targetPath + "-wal");
            File.Copy(livePath + "-shm", targetPath + "-shm");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("TabBrowser.csprojが見つかりません。");
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly List<Exception> _failures = [];

        public TestEnvironment()
        {
            BasePath = Path.Combine(Path.GetTempPath(), "KitenDataMaintenanceTests", Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(BasePath, "source", "TabBrowser");
            BackupDirectory = Path.Combine(BasePath, "backups");
            MutexName = $@"Local\Kiten.DataMaintenance.Tests.{Guid.NewGuid():N}";
            Directory.CreateDirectory(BasePath);
        }

        public string BasePath { get; }

        public string DataRoot { get; }

        public string BackupDirectory { get; }

        public string MutexName { get; }

        public KitenDataMaintenanceService CreateService(
            Action<string>? checkpoint = null,
            Action<Exception>? failure = null,
            Func<string, Exception?>? currentDataValidationFailure = null) =>
            new(new KitenDataMaintenanceHooks
            {
                CreateMutex = _ => new SingleInstanceService(MutexName),
                Checkpoint = checkpoint,
                Failure = exception =>
                {
                    _failures.Add(exception);
                    failure?.Invoke(exception);
                },
                CurrentDataValidationFailure = currentDataValidationFailure
            });

        public string FailureDetails(string message) => _failures.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(
                Environment.NewLine + "---" + Environment.NewLine,
                _failures.Select(static exception => exception.ToString()));

        public KitenBackupRequest BackupRequest() => new(
            ApplicationChannel.Stable,
            DataRoot,
            BackupDirectory,
            ReleaseInformationPath: null,
            IncludeBrowsingHistory: false,
            IncludeRecentItems: false,
            IncludeDownloadHistory: false,
            KitenDataMaintenanceService.BackupConfirmation);

        public KitenRestoreRequest RestoreRequest(string archivePath, string? dataRoot = null) => new(
            ApplicationChannel.Stable,
            dataRoot ?? DataRoot,
            archivePath,
            Apply: true,
            KitenDataMaintenanceService.RestoreConfirmation,
            KitenDataMaintenanceArchive.ComputeSha256(archivePath));

        public string OtherDataRoot(string name, ApplicationChannel channel = ApplicationChannel.Stable)
        {
            var leaf = channel == ApplicationChannel.Development ? "TabBrowser-Dev" : "TabBrowser";
            return Path.Combine(BasePath, name, leaf);
        }

        public async Task<SampleIds> WriteSampleDataAsync(string root, string label)
        {
            if (Directory.Exists(root))
            {
                if (!root.StartsWith(BasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("テスト領域外は初期化できません。");
                }

                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);
            SettingsService.SaveToFile(
                Path.Combine(root, "settings.json"),
                new AppSettings { SearchEngine = label });
            new BookmarkService(Path.Combine(root, "bookmarks.json")).SaveData(new BookmarkData
            {
                Bookmarks = [new TabBrowser.Models.BookmarkItem { Title = label, Url = "https://example.test/" }]
            });
            var containerId = Guid.NewGuid().ToString("N");
            new ContainerProfileStore(Path.Combine(root, "container-profiles.json")).Save(
            [
                ContainerProfile.CreateDefault(),
                new ContainerProfile { Id = containerId, Name = label, Color = "#334455" }
            ]);
            new SplitViewStore(Path.Combine(root, "split-views.json")).Save([]);
            File.WriteAllText(Path.Combine(root, "youtube-mini-players.json"), "[]", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "session.json"),
                "{\"version\":1,\"tabs\":[],\"closedTabs\":[],\"closedYouTubeMiniPlayers\":[],\"closedWindows\":[],\"windows\":[]}",
                new UTF8Encoding(false));
            var repository = new SQLiteWorkspaceRepository(Path.Combine(root, "workspace.db"));
            var workspace = await repository.CreateWorkspaceAsync(new Workspace { Name = label, Index = 1 });
            var folder = await repository.AddFolderAsync(new FolderItem
            {
                WorkspaceId = workspace.Id,
                Title = $"{label} フォルダー"
            });
            var note = await repository.AddNoteAsync(new NoteItem
            {
                WorkspaceId = workspace.Id,
                ParentFolderId = folder.Id,
                Title = label,
                Content = label,
                Tags = ["重要"]
            });
            var firstTabId = Guid.NewGuid();
            var secondTabId = Guid.NewGuid();
            var sessionGroup = await repository.AddSessionGroupAsync(new SessionGroupItem
            {
                WorkspaceId = workspace.Id,
                ParentFolderId = folder.Id,
                Title = $"{label} セッション",
                Tags = ["保存"],
                FaviconPngBase64 = "aWNvbg==",
                ThumbnailPngBase64 = "aW1hZ2U=",
                Tabs =
                [
                    new SessionTab
                    {
                        Id = firstTabId,
                        Url = "https://example.test/contained",
                        Title = "コンテナ付き",
                        ContainerProfileId = containerId
                    },
                    new SessionTab
                    {
                        Id = secondTabId,
                        Url = "https://example.test/legacy",
                        Title = "コンテナ情報なし"
                    }
                ]
            });
            var namedRestorePointId = Guid.NewGuid();
            var sessionService = new SessionService(
                Path.Combine(root, "session.json"),
                Path.Combine(root, "window-memory.json"),
                Path.Combine(root, "session"));
            await sessionService.SaveNamedRestorePointAsync(new NamedSessionRestorePointRequest(
                namedRestorePointId,
                $"{label} 復元点",
                DateTimeOffset.UtcNow,
                new SessionData
                {
                    Version = 2,
                    Windows =
                    [
                        new SessionWindowState
                        {
                            Tabs =
                            [
                                new SessionTabState
                                {
                                    Url = "https://example.test/named",
                                    Title = $"{label} 復元タブ",
                                    ContainerProfileId = containerId
                                }
                            ]
                        }
                    ]
                }));
            return new SampleIds(
                containerId,
                workspace.Id,
                folder.Id,
                note.Id,
                sessionGroup.Id,
                firstTabId,
                secondTabId,
                namedRestorePointId);
        }

        public async Task SetWorkspaceSchemaVersionAsync(string root, int version)
        {
            if (version is < 0 or > SQLiteWorkspaceRepository.SchemaVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(version));
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "workspace.db"),
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            var downgrade = new StringBuilder();
            if (version == 0)
            {
                downgrade.AppendLine("ALTER TABLE workspace_session_tabs DROP COLUMN container_profile_id;");
                downgrade.AppendLine("ALTER TABLE workspace_items DROP COLUMN container_profile_id;");
            }
            if (version <= 1)
            {
                downgrade.AppendLine("ALTER TABLE workspace_items DROP COLUMN deleted_at;");
                downgrade.AppendLine("ALTER TABLE workspace_items DROP COLUMN deletion_root_id;");
            }
            downgrade.AppendLine($"PRAGMA user_version={version};");
            command.CommandText = downgrade.ToString();
            await command.ExecuteNonQueryAsync();
        }

        public void WriteExcludedFiles(string root, string content)
        {
            var files = new[]
            {
                Path.Combine(root, "private", "secret.txt"),
                Path.Combine(root, "WebView2", "Cookies"),
                Path.Combine(root, "diagnostics", "runtime.log"),
                Path.Combine(root, "downloads", "downloaded.bin")
            };
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(BasePath)
                && BasePath.StartsWith(Path.Combine(Path.GetTempPath(), "KitenDataMaintenanceTests"), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(BasePath, recursive: true);
            }
        }
    }

    private sealed record SampleIds(
        string ContainerId,
        Guid WorkspaceId,
        Guid FolderId,
        Guid NoteId,
        Guid SessionGroupId,
        Guid SessionTabId1,
        Guid SessionTabId2,
        Guid NamedRestorePointId);
}
