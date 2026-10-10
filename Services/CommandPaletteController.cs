using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ICommandPaletteExecutionHost
{
    CommandInvocationContext CreateInvocationContext();
    void ClosePalette();
    void RecordUsage(CommandPaletteItem item);
    void SetStatus(string message);
}

internal interface ICommandPaletteNavigationHost
{
    IEnumerable<BookmarkItem> Bookmarks { get; }
    BrowserTab? ActiveTab { get; }
    Task OpenBookmarkAsync(BookmarkItem bookmark);
    Task RecordBookmarkOpenAsync(BookmarkItem bookmark);
    BrowserTab OpenTab(string url, bool activate, string? title, bool queueBackgroundLoading = true, string? containerProfileId = null);
    Task StartSplitAsync(BrowserTab tab);
    Task OpenUrlAsync(string url, string? title, string? containerProfileId = null);
    void SetStatus(string message);
}

public sealed class CommandPaletteController
{
    private readonly CommandRegistry _registry;
    private readonly CommandSearchIndex _searchIndex;
    private readonly TabCommandProvider _tabProvider;
    private readonly ClosedTabCommandProvider _closedTabProvider;
    private readonly BookmarkCommandProvider _bookmarkProvider;
    private readonly HistoryCommandProvider _historyProvider;
    private readonly ContainerCommandProvider _containerProvider;

    public CommandPaletteController(
        CommandRegistry registry,
        CommandSearchIndex searchIndex,
        TabCommandProvider tabProvider,
        ClosedTabCommandProvider closedTabProvider,
        BookmarkCommandProvider bookmarkProvider,
        HistoryCommandProvider historyProvider,
        ContainerCommandProvider containerProvider)
    {
        _registry = registry;
        _searchIndex = searchIndex;
        _tabProvider = tabProvider;
        _closedTabProvider = closedTabProvider;
        _bookmarkProvider = bookmarkProvider;
        _historyProvider = historyProvider;
        _containerProvider = containerProvider;
    }

    public IReadOnlyList<CommandPaletteItem> Search(
        IEnumerable<TabCommandSource> tabSources,
        IEnumerable<ClosedTabState> closedTabs,
        IEnumerable<BookmarkItem> bookmarks,
        IEnumerable<ContainerProfile> containerProfiles,
        BrowserTab? activeTab,
        string? query,
        IEnumerable<CommandPaletteUsageSettings>? usageEntries = null,
        IEnumerable<CommandPaletteItem>? additionalItems = null,
        CommandInvocationContext? invocationContext = null,
        bool includeUnavailable = false,
        IEnumerable<GestureMappingSettings>? gestureMappings = null)
    {
        var sources = tabSources.ToList();
        var items = _tabProvider
            .CreateItems(sources)
            .Concat(_closedTabProvider.CreateItems(closedTabs))
            .Concat(_historyProvider.CreateItems(sources.Select(static source => source.Tab)))
            .Concat(_registry.CreateCommandItems(
                invocationContext,
                includeUnavailable,
                gestureMappings))
            .Concat(_containerProvider.CreateItems(containerProfiles, activeTab))
            .Concat(_bookmarkProvider.CreateItems(bookmarks))
            .Concat(additionalItems ?? [])
            .ToList();
        return _searchIndex.Search(items, query, usageEntries);
    }

    internal async Task ExecuteSelectionAsync(
        CommandPaletteItem item,
        CommandPaletteExecutionMode executionMode,
        ICommandPaletteExecutionHost host,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var invocationContext = host.CreateInvocationContext();
        var availability = _registry.GetAvailability(item.ActionId, invocationContext);
        if (!item.IsAvailable || availability.State != CommandAvailabilityState.Available)
        {
            host.SetStatus(!string.IsNullOrWhiteSpace(availability.Reason)
                ? availability.Reason
                : item.UnavailableReason);
            return;
        }

        host.ClosePalette();
        try
        {
            await RunAsync(item, new CommandActionContext(
                invocationContext.TargetId, item, executionMode, invocationContext));
            if (cancellationToken.IsCancellationRequested) return;
            host.RecordUsage(item);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) return;
            host.SetStatus("コマンドを実行できません: " + ex.Message);
        }
    }

    internal async Task OpenBookmarkFromCommandPaletteAsync(CommandActionContext context, ICommandPaletteNavigationHost host)
    {
        if (context.SelectedItem?.Payload is not BookmarkItem payload)
        {
            host.SetStatus("開くブックマークを判定できません");
            return;
        }

        var bookmark = host.Bookmarks.FirstOrDefault(x => x.Id == payload.Id);
        if (bookmark is null)
        {
            host.SetStatus("ブックマークが見つかりません");
            return;
        }

        if (context.ExecutionMode == CommandPaletteExecutionMode.Default)
        {
            await host.OpenBookmarkAsync(bookmark);
        }
        else
        {
            await host.RecordBookmarkOpenAsync(bookmark);
            var openedTab = host.OpenTab(
                bookmark.Url,
                activate: context.ExecutionMode == CommandPaletteExecutionMode.NewTab,
                bookmark.Title,
                queueBackgroundLoading: context.ExecutionMode != CommandPaletteExecutionMode.Split);
            if (context.ExecutionMode == CommandPaletteExecutionMode.Split)
            {
                await host.StartSplitAsync(openedTab);
            }
        }

        host.SetStatus("ブックマークを開きました: " + (string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title));
    }

    internal async Task OpenHistoryFromCommandPaletteAsync(CommandActionContext context, ICommandPaletteNavigationHost host)
    {
        var selectedItem = context.SelectedItem;
        var historyEntry = selectedItem?.Payload as BrowsingHistoryEntry;
        var url = historyEntry?.Url ?? selectedItem?.Payload as string;
        if (selectedItem is null || string.IsNullOrWhiteSpace(url))
        {
            host.SetStatus("開く履歴URLを判定できません");
            return;
        }

        if (context.ExecutionMode == CommandPaletteExecutionMode.Default)
        {
            await host.OpenUrlAsync(url, selectedItem.Title);
        }
        else
        {
            var openedTab = host.OpenTab(
                url,
                activate: context.ExecutionMode == CommandPaletteExecutionMode.NewTab,
                selectedItem.Title,
                queueBackgroundLoading: context.ExecutionMode != CommandPaletteExecutionMode.Split,
                containerProfileId: historyEntry?.ContainerProfileId ?? host.ActiveTab?.ContainerProfileId);
            if (context.ExecutionMode == CommandPaletteExecutionMode.Split)
            {
                await host.StartSplitAsync(openedTab);
            }
        }

        host.SetStatus("履歴を開きました: " + url);
    }

    internal async Task OpenSearchAliasFromCommandPaletteAsync(CommandActionContext context, ICommandPaletteNavigationHost host)
    {
        if (context.SelectedItem?.Payload is not SearchAliasResolution resolution)
        {
            host.SetStatus("検索エイリアスを判定できません");
            return;
        }

        var title = $"{resolution.Alias}: {resolution.Query}";
        if (context.ExecutionMode == CommandPaletteExecutionMode.Default)
        {
            await host.OpenUrlAsync(
                resolution.Url,
                title,
                host.ActiveTab?.ContainerProfileId);
        }
        else
        {
            var openedTab = host.OpenTab(
                resolution.Url,
                activate: context.ExecutionMode == CommandPaletteExecutionMode.NewTab,
                title,
                queueBackgroundLoading: context.ExecutionMode != CommandPaletteExecutionMode.Split,
                containerProfileId: host.ActiveTab?.ContainerProfileId);
            if (context.ExecutionMode == CommandPaletteExecutionMode.Split)
            {
                await host.StartSplitAsync(openedTab);
            }
        }

        host.SetStatus(
            $"{resolution.Alias} で検索しました: {resolution.Query}");
    }

    public async Task RunAsync(CommandPaletteItem item, CommandActionContext context)
    {
        var result = await _registry.ExecuteAsync(item.ActionId, context);
        if (result.IsSuccess || result.Status == CommandExecutionStatus.Cancelled)
        {
            return;
        }

        throw new InvalidOperationException(result.Message, result.Exception);
    }
}
