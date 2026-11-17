namespace TabBrowser.Models;

public sealed class AdBlockingSettings
{
    public bool IsEnabled { get; set; } = true;

    // ホスト単位の設定をJSONオブジェクトにし、別Windowの別サイトへの変更をマージする。
    public Dictionary<string, bool> ExcludedHosts { get; set; } = new(StringComparer.Ordinal);
}

public static class AdBlockingSettingsProfile
{
    public static AdBlockingSettings Normalize(AdBlockingSettings? settings) => new()
    {
        IsEnabled = settings?.IsEnabled ?? true,
        ExcludedHosts = (settings?.ExcludedHosts ?? [])
            .Where(pair => pair.Value && TryNormalizeHost(pair.Key, out _))
            .GroupBy(pair => NormalizeHost(pair.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, _ => true, StringComparer.Ordinal)
    };

    public static bool TryNormalizeHost(string? value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Contains('/') || text.Contains(':') || text.Contains('@') || text.Contains('*')) return false;
        if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out var uri)
            || Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Dns) return false;
        host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.Length > 0;
    }

    public static bool IsExcluded(AdBlockingSettings settings, string host) =>
        settings.ExcludedHosts?.GetValueOrDefault(host) == true;

    internal static bool TryBuildEditingResult(bool enabled, string text, out AdBlockingSettings result, out string invalidHost)
    {
        result = new() { IsEnabled = enabled };
        invalidHost = string.Empty;
        foreach (var value in text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeHost(value, out var host))
            {
                invalidHost = value;
                return false;
            }
            result.ExcludedHosts[host] = true;
        }
        return true;
    }

    private static string NormalizeHost(string value)
    {
        TryNormalizeHost(value, out var host);
        return host;
    }
}
