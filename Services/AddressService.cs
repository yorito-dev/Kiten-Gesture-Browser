using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record SearchAliasResolution(
    string Alias,
    string Query,
    string Url);

public sealed class AddressService
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyDictionary<string, string> _searchAliasTemplates;

    public AddressService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _searchAliasTemplates = SearchAliasSettingsProfile.Normalize(settings.SearchAliases)
            .ToDictionary(
                static alias => alias.Alias,
                static alias => alias.SearchUrlTemplate,
                StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSearchInput(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri)
            && IsAllowedScheme(absoluteUri.Scheme))
        {
            return false;
        }

        return !LooksLikeHost(value);
    }

    public string Resolve(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "about:blank";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri)
            && IsAllowedScheme(absoluteUri.Scheme))
        {
            return absoluteUri.ToString();
        }

        if (LooksLikeHost(value))
        {
            return "https://" + value;
        }

        if (ResolveSearchAlias(value) is { } aliasSearch)
        {
            return aliasSearch.Url;
        }

        return CreateSearchUrl(value);
    }

    public string CreateSearchUrl(string text)
    {
        return string.Format(
            _settings.SearchUrlTemplate,
            Uri.EscapeDataString(text.Trim()));
    }

    internal static string? NormalizePasteInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : string.Join(' ', lines);
    }

    public SearchAliasResolution? ResolveSearchAlias(string? input)
    {
        var value = input?.Trim() ?? string.Empty;
        var separatorIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                separatorIndex = index;
                break;
            }
        }

        if (separatorIndex <= 0)
        {
            return null;
        }

        var alias = value[..separatorIndex];
        var query = value[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(query)
            || !_searchAliasTemplates.TryGetValue(alias, out var template))
        {
            return null;
        }

        var searchUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template,
            Uri.EscapeDataString(query));
        return new SearchAliasResolution(
            alias.ToLowerInvariant(),
            query,
            searchUrl);
    }

    private static bool LooksLikeHost(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || value.Contains('.');
    }

    private static bool IsAllowedScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }
}
