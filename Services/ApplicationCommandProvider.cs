using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ApplicationCommandProvider
{
    public const string LaunchApplicationActionId = "application.launch";
    public const string RefreshApplicationsActionId = "application.refresh";

    public IReadOnlyList<CommandPaletteItem> CreateItems(
        IEnumerable<ApplicationLaunchItem> applications)
    {
        return applications.Select(application => new CommandPaletteItem
        {
            Id = "application:" + application.Id,
            Type = CommandPaletteItemType.Application,
            Title = application.DisplayName,
            Subtitle = application.SourceLabel,
            ActionId = LaunchApplicationActionId,
            Payload = application,
            Keywords =
            [
                application.LaunchTarget,
                application.SourceLabel,
                "app",
                "application",
                "アプリ"
            ]
        }).ToList();
    }

    public IReadOnlyList<CommandAction> CreateActions(
        Func<CommandActionContext, Task> launchApplication,
        Func<Task> refreshApplications)
    {
        return
        [
            new CommandAction(
                LaunchApplicationActionId,
                "アプリを起動",
                launchApplication,
                showInPalette: false),
            new CommandAction(
                RefreshApplicationsActionId,
                "アプリ一覧を更新",
                _ => refreshApplications(),
                ["app", "application", "refresh", "アプリ", "更新"])
        ];
    }
}
