namespace TabBrowser.Models;

public sealed class ContainerProfile
{
    public const string DefaultId = "default";
    public const string DefaultName = "既定";
    public const string DefaultColor = "#6B7280";

    public string Id { get; set; } = DefaultId;

    public string Name { get; set; } = DefaultName;

    public string Color { get; set; } = DefaultColor;

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    public static ContainerProfile CreateDefault(DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        return new ContainerProfile
        {
            Id = DefaultId,
            Name = DefaultName,
            Color = DefaultColor,
            IsDefault = true,
            CreatedAt = now,
            LastUsedAt = now
        };
    }

    public static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return DefaultId;
        }

        var trimmed = id.Trim();
        if (string.Equals(trimmed, DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultId;
        }

        return Guid.TryParse(trimmed, out var parsedId) && parsedId != Guid.Empty
            ? parsedId.ToString("N")
            : DefaultId;
    }

    // 保存情報の欠落（旧形式）と、明示された不明IDを区別する。通常の設定正規化とは別の境界。
    public static bool TryResolveSavedId(string? savedId, Func<string, bool> exists, out string id)
    {
        id = DefaultId;
        if (savedId is null || string.Equals(savedId.Trim(), DefaultId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Guid.TryParse(savedId, out var parsed) || parsed == Guid.Empty) return false;
        id = parsed.ToString("N");
        return exists(id);
    }
}
