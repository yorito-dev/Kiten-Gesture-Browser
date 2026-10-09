namespace TabBrowser.Models;

public enum ApplicationLaunchKind
{
    ShellPath,
    AppsFolder
}

public sealed class ApplicationLaunchItem
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string LaunchTarget { get; init; } = string.Empty;

    public ApplicationLaunchKind Kind { get; init; }

    public string SourceLabel { get; init; } = string.Empty;

    public string IconLookupPath => Kind == ApplicationLaunchKind.AppsFolder
        ? "shell:AppsFolder\\" + LaunchTarget
        : LaunchTarget;
}
