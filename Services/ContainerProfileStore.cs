using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ContainerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _storeFile;

    public ContainerProfileStore()
        : this(AppPaths.ContainerProfilesFile)
    {
    }

    public ContainerProfileStore(string storeFile)
    {
        _storeFile = storeFile;
    }

    public List<ContainerProfile> Load()
    {
        try
        {
            if (!File.Exists(_storeFile))
            {
                return Normalize(null);
            }

            using var stream = File.OpenRead(_storeFile);
            var profiles = JsonSerializer.Deserialize<List<ContainerProfile>>(stream, JsonOptions);
            return Normalize(profiles);
        }
        catch
        {
            return Normalize(null);
        }
    }

    public void Save(IEnumerable<ContainerProfile> profiles)
    {
        AtomicFileService.EnsureDirectory(_storeFile);
        var normalized = Normalize(profiles);
        AtomicFileService.Write(
            _storeFile,
            AtomicFileService.CreateBackupFilePath(_storeFile),
            stream => JsonSerializer.Serialize(stream, normalized, JsonOptions));
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
