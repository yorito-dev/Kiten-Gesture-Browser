using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class ContainerCommandProvider
{
    public const string NewTabInContainerActionId = "container.newTab";
    public const string ReopenActiveTabInContainerActionId = "container.reopenActiveTab";
    public const string OpenContainerSettingsActionId = "container.openSettings";

    public IReadOnlyList<CommandPaletteItem> CreateItems(
        IEnumerable<ContainerProfile> profiles,
        BrowserTab? activeTab)
    {
        var normalizedProfiles = profiles
            .GroupBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        var items = new List<CommandPaletteItem>
        {
            new()
            {
                Id = "container:settings",
                Type = CommandPaletteItemType.Command,
                Title = "コンテナ設定を開く",
                Subtitle = "コンテナプロファイル",
                ActionId = OpenContainerSettingsActionId,
                Keywords = ["container", "profile", "settings", "コンテナ", "設定"]
            }
        };

        foreach (var profile in normalizedProfiles)
        {
            items.Add(CreateNewTabItem(profile));
        }

        if (activeTab is { IsWorkspaceTab: false })
        {
            foreach (var profile in normalizedProfiles.Where(profile => !string.Equals(
                         profile.Id,
                         activeTab.ContainerProfileId,
                         StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(CreateReopenItem(profile));
            }
        }

        return items;
    }

    private static CommandPaletteItem CreateNewTabItem(ContainerProfile profile)
    {
        var name = GetDisplayName(profile);
        return new CommandPaletteItem
        {
            Id = "container:newTab:" + profile.Id,
            Type = CommandPaletteItemType.Command,
            Title = $"新しいタブをコンテナで開く: {name}",
            Subtitle = "コンテナプロファイル",
            ActionId = NewTabInContainerActionId,
            Payload = profile.Id,
            Keywords = ["container", "profile", "new", "tab", "コンテナ", "新規タブ", name]
        };
    }

    private static CommandPaletteItem CreateReopenItem(ContainerProfile profile)
    {
        var name = GetDisplayName(profile);
        return new CommandPaletteItem
        {
            Id = "container:reopen:" + profile.Id,
            Type = CommandPaletteItemType.Command,
            Title = $"現在タブをコンテナで開き直す: {name}",
            Subtitle = "現在タブを同じURLで再読み込み",
            ActionId = ReopenActiveTabInContainerActionId,
            Payload = profile.Id,
            Keywords = ["container", "profile", "reopen", "reload", "tab", "コンテナ", "開き直す", name]
        };
    }

    private static string GetDisplayName(ContainerProfile profile)
    {
        if (profile.IsDefault || string.Equals(profile.Id, ContainerProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return "既定";
        }

        return string.IsNullOrWhiteSpace(profile.Name)
            ? "コンテナ"
            : profile.Name.Trim();
    }
}
