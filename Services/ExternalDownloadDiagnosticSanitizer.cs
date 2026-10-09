using System.Text.RegularExpressions;

namespace TabBrowser.Services;

public static partial class ExternalDownloadDiagnosticSanitizer
{
    [GeneratedRegex("https?://[^\\s\\]\\[\\)\\(\\\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"(?i)(po[ _-]?token|visitor[ _-]?(?:data|id)|authorization|cookie)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValueRegex();

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = SensitiveValueRegex().Replace(value, "$1=[省略]");
        return UrlRegex().Replace(sanitized, "[URL省略]");
    }
}
