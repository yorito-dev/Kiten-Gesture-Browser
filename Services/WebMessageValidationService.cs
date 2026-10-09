using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class WebMessageValidationService
{
    public const string TokenPropertyName = "tabBrowserToken";

    public static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public static bool HasValidToken(JsonElement message, string? expectedToken)
    {
        if (message.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(expectedToken)
            || !message.TryGetProperty(TokenPropertyName, out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || tokenElement.GetString() is not { } actualToken
            || actualToken.Length != expectedToken.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actualToken),
            Encoding.UTF8.GetBytes(expectedToken));
    }

    public static bool IsConfiguredGesture(
        JsonElement message,
        IEnumerable<GestureMappingSettings>? mappings)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var route = GetString(message, "route");
        var command = GetString(message, "command");
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return GestureSettingsCatalog.ResolveMappingsForScope(
                mappings,
                GestureCommandScope.Web)
            .Any(mapping =>
            string.Equals(mapping.Route, route, StringComparison.Ordinal)
            && string.Equals(mapping.Command, command, StringComparison.Ordinal));
    }

    public static bool RequiresInternalNewTab(string? messageType)
    {
        return messageType is "newTabSearch"
            or "openBookmark"
            or "deleteBookmark"
            or "showBookmarkPreview"
            or "hideBookmarkPreview";
    }

    private static string? GetString(JsonElement message, string propertyName)
    {
        return message.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
