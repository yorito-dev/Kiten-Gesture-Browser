using System.IO;
using System.Text.Json.Serialization;

namespace TabBrowser.Services.DataMaintenance;

public sealed record KitenBackupRequest(
    ApplicationChannel Channel,
    string DataRoot,
    string DestinationDirectory,
    string? ReleaseInformationPath = null,
    bool IncludeBrowsingHistory = false,
    bool IncludeRecentItems = false,
    bool IncludeDownloadHistory = false,
    string? Confirmation = null);

public sealed record KitenRestoreRequest(
    ApplicationChannel Channel,
    string DataRoot,
    string BackupArchivePath,
    bool Apply = false,
    string? Confirmation = null,
    string? ExpectedArchiveSha256 = null);

public sealed record KitenRecoveryRequest(
    ApplicationChannel Channel,
    string DataRoot,
    string? Confirmation = null);

public sealed record KitenDataMaintenanceResult(
    bool Succeeded,
    bool ChangedData,
    string Message,
    string? OutputPath = null,
    string? RecoveryPath = null,
    string? VerifiedArchiveSha256 = null)
{
    public static KitenDataMaintenanceResult Failure(string message, string? recoveryPath = null) =>
        new(false, false, message, RecoveryPath: recoveryPath);
}

internal enum KitenManagedFileKind
{
    Json,
    Sqlite
}

internal enum KitenDataValidationStage
{
    SourceRead,
    Transfer,
    StagedValidation
}

internal sealed record KitenManagedFileDefinition(
    string RelativePath,
    KitenManagedFileKind Kind,
    int? MaximumFormatVersion,
    bool IsHistory = false);

internal sealed class KitenBackupManifest
{
    public int SchemaVersion { get; set; } = KitenDataMaintenanceArchive.ManifestSchemaVersion;

    public string ProductName { get; set; } = "Kiten Gesture Browser";

    public string Channel { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool Encrypted { get; set; }

    public bool IncludesBrowsingHistory { get; set; }

    public bool IncludesRecentItems { get; set; }

    public bool IncludesDownloadHistory { get; set; }

    public KitenSourceApplicationInformation SourceApplication { get; set; } = new();

    public List<KitenBackupFileEntry> Files { get; set; } = [];

    public List<KitenBackupCollectionEntry> Collections { get; set; } = [];
}

internal sealed class KitenSourceApplicationInformation
{
    public string PublicVersion { get; set; } = "未取得";

    public string SourceCommit { get; set; } = "未取得";
}

internal sealed class KitenBackupFileEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Present { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<KitenManagedFileKind>))]
    public KitenManagedFileKind Kind { get; set; }

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public int? FormatVersion { get; set; }
}

internal sealed class KitenBackupCollectionEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Present { get; set; }
}

internal sealed class KitenRestoreMarker
{
    public int SchemaVersion { get; set; } = 1;

    public Guid OperationId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string JournalPath { get; set; } = string.Empty;
}

internal sealed class KitenRestoreJournal
{
    public int SchemaVersion { get; set; } = 2;

    public Guid OperationId { get; set; }

    public string Channel { get; set; } = string.Empty;

    public string DataRoot { get; set; } = string.Empty;

    public string BackupArchivePath { get; set; } = string.Empty;

    public string PreRestoreBackupPath { get; set; } = string.Empty;

    public string PreRestoreStateKind { get; set; } = KitenPreRestoreStateKind.VerifiedBackup;

    public string WorkRoot { get; set; } = string.Empty;

    public string Status { get; set; } = "prepared";

    public List<KitenRestoreJournalEntry> Entries { get; set; } = [];
}

internal static class KitenPreRestoreStateKind
{
    public const string VerifiedBackup = "verifiedBackup";

    public const string RawPreservation = "rawPreservation";
}

internal sealed class KitenRawPreservationManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string Channel { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IncludesBrowsingHistory { get; set; }

    public bool IncludesRecentItems { get; set; }

    public bool IncludesDownloadHistory { get; set; }

    public List<KitenRawPreservedFileEntry> Files { get; set; } = [];

    public List<KitenRawPreservedDirectoryEntry> Collections { get; set; } = [];
}

internal sealed class KitenRawPreservedFileEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Present { get; set; }

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class KitenRawPreservedDirectoryEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Present { get; set; }
}

internal sealed class KitenRestoreJournalEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public string TargetKind { get; set; } = string.Empty;

    public bool OriginalPresent { get; set; }

    public bool SourcePresent { get; set; }

    public string State { get; set; } = "planned";
}

internal sealed class KitenDataMaintenanceHooks
{
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    public Func<Guid> NewGuid { get; init; } = static () => Guid.NewGuid();

    public Action<string>? Checkpoint { get; init; }

    public Action<Exception>? Failure { get; init; }

    public Func<string, Exception?>? CurrentDataValidationFailure { get; init; }

    public Func<ApplicationChannel, SingleInstanceService> CreateMutex { get; init; } = static channel =>
        new SingleInstanceService(SingleInstanceService.GetMutexName(channel));
}

internal sealed class KitenSimulatedInterruptionException : Exception
{
}

internal sealed class KitenUnsupportedDataFormatException(string message) : Exception(message)
{
}

internal sealed class KitenDataValidationException(
    KitenDataValidationStage stage,
    Exception innerException)
    : Exception("管理対象データの検証に失敗しました。", innerException)
{
    public KitenDataValidationStage Stage { get; } = stage;
}

internal sealed class KitenCurrentDataCorruptionException(
    string relativePath,
    Exception innerException)
    : Exception($"現在データの内容を検証できません: {relativePath}", innerException)
{
    public string RelativePath { get; } = relativePath;

    public IReadOnlyList<string> RelativePaths { get; } = [relativePath];

    public KitenCurrentDataCorruptionException(
        IReadOnlyList<KitenCurrentDataCorruptionException> failures)
        : this(
            failures.Count > 0 ? failures[0].RelativePath : "不明",
            failures.Count > 0 ? failures[0] : new InvalidDataException("内容破損の詳細がありません。"))
    {
        RelativePaths = failures
            .SelectMany(static failure => failure.RelativePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
