namespace TabBrowser.Services;

public static class ExtensionToolbarPolicy
{
    private static readonly HashSet<string> BuiltInExtensionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Clipboard Extension",
        "Microsoft Edge DevTools Enhancements",
        "Microsoft Edge PDF Viewer",
        "Microsoft Edge Omnification Extension"
    };

    public static bool ShouldShowInToolbar(string? extensionName)
    {
        return !string.IsNullOrWhiteSpace(extensionName)
            && !BuiltInExtensionNames.Contains(extensionName.Trim());
    }
}
