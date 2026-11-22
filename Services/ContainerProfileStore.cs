using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public enum ContainerProfileLoadStatus
{
    Missing,
    Loaded,
    RecoveredFromBackup,
    Failed
}

public sealed record ContainerProfileLoadResult(ContainerProfileLoadStatus Status, List<ContainerProfile> Profiles)
{
    // バックアップには主ファイルの最新の追加・削除が反映されていない場合がある。
    internal bool IsAuthoritative => Status is ContainerProfileLoadStatus.Missing or ContainerProfileLoadStatus.Loaded;

    internal ContainerProfile? FindProfile(string id)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class ContainerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _storeFile;
    private readonly string _backupFile;

    public ContainerProfileStore()
        : this(AppPaths.ContainerProfilesFile)
    {
    }

    public ContainerProfileStore(string storeFile)
    {
        _storeFile = storeFile;
        _backupFile = AtomicFileService.CreateBackupFilePath(storeFile);
    }

    public ContainerProfileLoadResult Load()
    {
        var primary = ReadFile(_storeFile);
        if (primary.Status == ContainerProfileLoadStatus.Loaded)
        {
            return primary;
        }

        var backup = ReadFile(_backupFile);
        if (backup.Status == ContainerProfileLoadStatus.Loaded)
        {
            return backup with { Status = ContainerProfileLoadStatus.RecoveredFromBackup };
        }

        return primary.Status == ContainerProfileLoadStatus.Missing && backup.Status == ContainerProfileLoadStatus.Missing
            ? new ContainerProfileLoadResult(ContainerProfileLoadStatus.Missing, Normalize(null))
            : new ContainerProfileLoadResult(ContainerProfileLoadStatus.Failed, []);
    }

    public bool Save(IEnumerable<ContainerProfile> profiles)
    {
        // 編集中に読めなくなった場合も、主ファイルと復旧用のバックアップを保持する。
        if (!Load().IsAuthoritative)
        {
            return false;
        }

        var normalized = Normalize(profiles);
        AtomicFileService.Write(
            _storeFile,
            _backupFile,
            stream => JsonSerializer.Serialize(stream, normalized, JsonOptions));
        return true;
    }

    private static ContainerProfileLoadResult ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var profiles = JsonSerializer.Deserialize<List<ContainerProfile>>(stream, JsonOptions);
            return profiles is not null && !profiles.Any(static profile => profile is null)
                ? new ContainerProfileLoadResult(ContainerProfileLoadStatus.Loaded, Normalize(profiles))
                : new ContainerProfileLoadResult(ContainerProfileLoadStatus.Failed, []);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ContainerProfileLoadResult(ContainerProfileLoadStatus.Missing, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ContainerProfileLoadResult(ContainerProfileLoadStatus.Failed, []);
        }
    }

    private static List<ContainerProfile> Normalize(IEnumerable<ContainerProfile>? profiles)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = new List<ContainerProfile>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles ?? [])
        {
            var normalizedProfile = NormalizeProfile(profile, now);
            if (!seenIds.Add(normalizedProfile.Id))
            {
                continue;
            }

            normalized.Add(normalizedProfile);
        }

        var defaultProfile = normalized.FirstOrDefault(static profile => profile.IsDefault)
            ?? normalized.FirstOrDefault(static profile => string.Equals(
                profile.Id,
                ContainerProfile.DefaultId,
                StringComparison.OrdinalIgnoreCase));

        if (defaultProfile is null)
        {
            normalized.Insert(0, ContainerProfile.CreateDefault(now));
        }
        else
        {
            defaultProfile.Id = ContainerProfile.DefaultId;
            defaultProfile.Name = string.IsNullOrWhiteSpace(defaultProfile.Name)
                ? ContainerProfile.DefaultName
                : defaultProfile.Name.Trim();
            defaultProfile.Color = NormalizeColor(defaultProfile.Color);
            defaultProfile.IsDefault = true;
            defaultProfile.CreatedAt = NormalizeTimestamp(defaultProfile.CreatedAt, now);
            defaultProfile.LastUsedAt = NormalizeTimestamp(defaultProfile.LastUsedAt, defaultProfile.CreatedAt);

            normalized.Remove(defaultProfile);
            normalized.Insert(0, defaultProfile);
        }

        normalized = normalized
            .GroupBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        for (var index = 1; index < normalized.Count; index++)
        {
            normalized[index].IsDefault = false;
        }

        return normalized;
    }

    private static ContainerProfile NormalizeProfile(ContainerProfile profile, DateTimeOffset now)
    {
        var isDefault = profile.IsDefault
            || string.Equals(profile.Id, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase);
        var id = isDefault
            ? ContainerProfile.DefaultId
            : NormalizeCustomId(profile.Id);
        var name = string.IsNullOrWhiteSpace(profile.Name)
            ? (isDefault ? ContainerProfile.DefaultName : "コンテナ")
            : profile.Name.Trim();
        var createdAt = NormalizeTimestamp(profile.CreatedAt, now);

        return new ContainerProfile
        {
            Id = id,
            Name = name,
            Color = NormalizeColor(profile.Color),
            IsDefault = isDefault,
            CreatedAt = createdAt,
            LastUsedAt = NormalizeTimestamp(profile.LastUsedAt, createdAt)
        };
    }

    private static string NormalizeCustomId(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)
            && Guid.TryParse(id.Trim(), out var parsed)
            && parsed != Guid.Empty)
        {
            return parsed.ToString("N");
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return ContainerProfile.DefaultColor;
        }

        var trimmed = color.Trim();
        return IsHexColor(trimmed)
            ? trimmed
            : ContainerProfile.DefaultColor;
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        return value
            .Skip(1)
            .All(static character => Uri.IsHexDigit(character));
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset timestamp, DateTimeOffset fallback)
    {
        return timestamp == default
            ? fallback
            : timestamp;
    }
}
