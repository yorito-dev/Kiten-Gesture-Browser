namespace TabBrowser.Services;

public enum ApplicationChannel
{
    Stable,
    Development
}

public static class ApplicationChannelService
{
    internal const string DevelopmentWindowTitleMarker = "[開発版]";
    private const string ChannelArgument = "--channel";
    private const string ChannelArgumentPrefix = "--channel=";

    public static ApplicationChannel Current { get; } = Resolve(Environment.GetCommandLineArgs());

    public static string DisplayName => Current == ApplicationChannel.Development
        ? "開発版"
        : "常用版";

    public static string WindowTitleSuffix => Current == ApplicationChannel.Development
        ? $" {DevelopmentWindowTitleMarker}"
        : string.Empty;

    internal static ApplicationChannel Resolve(IEnumerable<string>? arguments)
    {
        var values = arguments?.ToArray() ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value.Equals(ChannelArgument, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < values.Length
                    ? Parse(values[index + 1])
                    : ApplicationChannel.Stable;
            }

            if (value.StartsWith(ChannelArgumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Parse(value[ChannelArgumentPrefix.Length..]);
            }
        }

        return ApplicationChannel.Stable;
    }

    internal static string GetDataDirectoryName(ApplicationChannel channel)
    {
        return channel == ApplicationChannel.Development
            ? "TabBrowser-Dev"
            : "TabBrowser";
    }

    private static ApplicationChannel Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "dev" or "development" => ApplicationChannel.Development,
            _ => ApplicationChannel.Stable
        };
    }
}
