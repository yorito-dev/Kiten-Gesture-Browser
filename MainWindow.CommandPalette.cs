using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Services.Workspaces;
using TabBrowser.ViewModels;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : ICommandPaletteViewHost, ICommandPaletteDataHost, ICommandPaletteExecutionHost, ICommandPaletteNavigationHost
{
    bool ICommandPaletteDataHost.IsOpen => IsCommandPaletteOpen;

    IEnumerable<CommandPaletteItem> ICommandPaletteDataHost.VisibleItems => _commandPaletteViewController.Items;

    void ICommandPaletteDataHost.RefreshItems() => RefreshCommandPaletteItems();

    void ICommandPaletteDataHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    bool ICommandPaletteViewHost.IsQuickLookEnabled => _settings.QuickLook.IsEnabled && _settings.QuickLook.EnableInUnifiedSearch;

    List<CommandPaletteUsageSettings> ICommandPaletteViewHost.Usage
    {
        get => _settings.CommandPaletteUsage;
        set => _settings.CommandPaletteUsage = value;
    }

    CommandPaletteItem? ICommandPaletteViewHost.CreateSearchAliasItem(string? input) => CreateCommandPaletteSearchAliasItem(input);

    IReadOnlyList<CommandPaletteItem> ICommandPaletteViewHost.Search(string? query, bool includeUnavailable)
    {
        var containerProfiles = ReloadContainerProfiles(applyToTabs: false);
        var contextTab = GetCommandPaletteContextTab();
        var invocationContext = CreateCommandInvocationContext(contextTab);
        return _commandPaletteController.Search(
                     GetCommandPaletteTabSources(),
                     s_closedTabWindowHistoryStore.ClosedTabs,
                     _bookmarks,
                     containerProfiles,
                     contextTab,
                     query,
                     _settings.CommandPaletteUsage,
                     _commandPaletteDataController.Items
                         .Concat(
                             _contextCommandProvider.CreateMiniPlayerItems(GetCommandPaletteMiniPlayerSources()))
                         .Concat(_clipboardHistoryCommandProvider.CreateItems(App.ClipboardHistory.Items)),
                     invocationContext,
                     includeUnavailable,
                     _settings.GestureMappings);
    }

    void ICommandPaletteViewHost.QueueApplicationIconLoad() => QueueCommandPaletteApplicationIconLoad();

    void ICommandPaletteViewHost.QueueExecution(CommandPaletteExecutionMode mode, CommandPaletteItem item)
        => QueueCommandPaletteExecution(mode, item);

    void ICommandPaletteViewHost.ClosePalette() => CloseCommandPalette();

    void ICommandPaletteViewHost.ApplyShortcutWindowOrder() => ApplyCommandPaletteShortcutWindowOrder();

    void ICommandPaletteViewHost.CloseContext()
    {
        _commandPaletteDataController.CloseContext();
        _commandPaletteContextCoordinator.Close();
    }

    void ICommandPaletteViewHost.ResetShortcutWindowOrder() => _commandPaletteShortcutRequiresTopmost = false;

    void ICommandPaletteViewHost.SetFocus(IntPtr handle) => SetFocus(handle);

    void ICommandPaletteViewHost.SaveSettings() => SettingsService.Save(_settings);

    private void QueueCommandPaletteRefresh() => _commandPaletteViewController?.QueueCommandPaletteRefresh();

    private const string SearchAliasCommandActionId = "search-alias";

    private readonly CommandPaletteViewController _commandPaletteViewController;
    private readonly UrlThumbnailCaptureController _urlThumbnailCaptureController;
    private readonly CommandRegistry _commandRegistry = new();
    private readonly CommandSearchIndex _commandSearchIndex = new();
    private readonly TabCommandProvider _tabCommandProvider = new(s_tabRuntimeCoordinator);
    private readonly ClosedTabCommandProvider _closedTabCommandProvider = new();
    private readonly BookmarkCommandProvider _bookmarkCommandProvider = new();
    private readonly HistoryCommandProvider _historyCommandProvider = new();
    private readonly ContainerCommandProvider _containerCommandProvider = new();
    private readonly ContextCommandProvider _contextCommandProvider = new();
    private readonly ClipboardHistoryCommandProvider _clipboardHistoryCommandProvider = new();
    private readonly ApplicationCatalogService _applicationCatalogService = new();
    private readonly ApplicationCommandProvider _applicationCommandProvider = new();
    private readonly CommandPaletteController _commandPaletteController;
    private readonly CommandPaletteContextCoordinator _commandPaletteContextCoordinator = new();
    private readonly CommandPaletteDataController _commandPaletteDataController;

    private void CommandPaletteOverlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _commandPaletteViewController?.CommandPaletteOverlay_PreviewMouseDown(sender, e);

    private void CommandPaletteOverlay_Opened(object? sender, EventArgs e)
        => _commandPaletteViewController?.CommandPaletteOverlay_Opened(sender, e);

    private void CommandPaletteDragHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => _commandPaletteViewController?.CommandPaletteDragHandle_DragDelta(sender, e);

    private void CommandPaletteLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteLayout_SizeChanged(sender, e);

    private void CommandPaletteOverlay_Closed(object? sender, EventArgs e)
        => _commandPaletteViewController?.CommandPaletteOverlay_Closed(sender, e);

    private bool IsCommandPalettePanelHit(MouseButtonEventArgs e)
        => _commandPaletteViewController?.IsCommandPalettePanelHit(e) == true;

    private void InitializeCommandPalette()
    {
        _commandPaletteViewController.Initialize();
        _commandRegistry.Register(new CommandAction(
            TabCommandProvider.ActivateTabActionId,
            "タブへ移動",
            ActivateCommandPaletteTabAsync,
            ["tab", "switch"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            ClosedTabCommandProvider.RestoreClosedTabActionId,
            "閉じたタブを復元",
            RestoreClosedTabFromCommandPaletteAsync,
            ["closed", "restore", "reopen"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            BookmarkCommandProvider.OpenBookmarkActionId,
            "ブックマークを開く",
            OpenBookmarkFromCommandPaletteAsync,
            ["bookmark", "bookmarks"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            HistoryCommandProvider.OpenHistoryActionId,
            "履歴を開く",
            OpenHistoryFromCommandPaletteAsync,
            ["history", "visited"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            SearchAliasCommandActionId,
            "検索エイリアスで検索",
            OpenSearchAliasFromCommandPaletteAsync,
            ["search", "web"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            ContainerCommandProvider.NewTabInContainerActionId,
            "コンテナで新しいタブを開く",
            OpenNewTabInContainerFromCommandPaletteAsync,
            ["container", "profile", "new", "tab"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            ContainerCommandProvider.ReopenActiveTabInContainerActionId,
            "現在タブをコンテナで開き直す",
            ReopenActiveTabInContainerFromCommandPaletteAsync,
            ["container", "profile", "reopen", "tab"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            ContainerCommandProvider.OpenContainerSettingsActionId,
            "コンテナ設定を開く",
            OpenContainerSettingsFromCommandPaletteAsync,
            ["container", "profile", "settings"],
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            ContextCommandProvider.OpenWorkspaceItemActionId,
            "Workspace項目を開く",
            OpenWorkspaceItemFromCommandPaletteAsync,
            showInPalette: false));
        _commandRegistry.Register(new CommandAction(
            FeatureCommandProvider.OpenRecentItemsActionId,
            "最近使った項目を開く",
            OpenRecentItemsFromCommandPaletteAsync,
            ["recent", "workspace", "メモ", "ファイル", "動画"],
            category: "Workspace",
            description: "Workspaceを開き、右側に最近使った項目を表示します"));
        _commandRegistry.Register(new CommandAction(
            ContextCommandProvider.FocusMiniPlayerActionId,
            "YouTubeミニプレイヤーを前面化",
            FocusMiniPlayerFromCommandPaletteAsync,
            showInPalette: false));
        foreach (var action in _contextCommandProvider.CreateActions(new ContextCommandCallbacks(
                     CopyCommandPaletteSelectionAsync,
                     SearchCommandPaletteSelectionAsync,
                     TranslateCommandPaletteSelectionAsync,
                     OpenCommandPaletteLinkInNewTabAsync,
                     OpenCommandPaletteLinkInSplitAsync,
                     CopyCommandPaletteLinkUrlAsync,
                     SortCommandPaletteFolderAsync,
                     ApplyCommandPaletteFolderSortAsync,
                     ToggleCommandPaletteFolderSortDirectionAsync,
                     ShowCommandPaletteFolderQuickLookAsync,
                     ArrangeCommandPaletteWorkspaceSelectionAsync,
                     ApplyCommandPaletteWorkspaceArrangementAsync,
                     RestoreCommandPaletteWorkspaceArrangementAsync,
                     ShowCommandPaletteWorkspaceQuickLookAsync)))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in _applicationCommandProvider.CreateActions(
                     LaunchApplicationFromCommandPaletteAsync,
                     RefreshApplicationsFromCommandPaletteAsync))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in ClipboardCommandProvider.CreateActions(
                     CopyCurrentPageFromCommandPaletteAsync,
                     CopyAllTabsMarkdownFromCommandPaletteAsync))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in _clipboardHistoryCommandProvider.CreateActions(
                     OpenClipboardHistoryFromCommandPaletteAsync,
                     CopyClipboardHistoryItemFromCommandPaletteAsync,
                     ClearClipboardHistoryFromCommandPaletteAsync,
                     ToggleClipboardHistoryPauseFromCommandPaletteAsync))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in BrowserCommandProvider.CreateActions(
                     OpenDownloadsFromCommandPaletteAsync,
                     OpenBrowsingHistoryWindowAsync,
                     OpenUserGuideAsync,

                     OpenSettingsFromCommandPaletteAsync,
                     () => ExecuteBrowserCommandAsync("NewTab", null),
                     () => ExecuteBrowserCommandAsync("DuplicateTab", null),
                     () => ExecuteBrowserCommandAsync("Reload", null),
                     () => ExecuteBrowserCommandAsync("Back", null),
                     () => ExecuteBrowserCommandAsync("Forward", null),
                     () => ExecuteBrowserCommandAsync("PreviousTab", null),
                     () => ExecuteBrowserCommandAsync("NextTab", null),
                     () => ExecuteBrowserCommandAsync("RestoreClosedTab", null),
                     OpenPageFindFromCommandPaletteAsync,
                     () => ZoomPageFromCommandPaletteAsync(PageZoomStep),
                     () => ZoomPageFromCommandPaletteAsync(-PageZoomStep),
                     ResetPageZoomFromCommandPaletteAsync,
                     FocusLocationFromCommandPaletteAsync,
                     ExecuteBrowserCommandFromCommandPaletteAsync))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in FeatureCommandProvider.CreateActions(
                     OpenWorkspaceFromCommandPaletteAsync,
                     ToggleWorkspaceAsync,
                     OpenWorkspaceWindowFromCommandPaletteAsync,
                     () => FocusSplitPaneFromCommandPaletteAsync(SplitViewPane.Left),
                     () => FocusSplitPaneFromCommandPaletteAsync(SplitViewPane.Right),
                     context => SplitTabFromCommandPaletteAsync(
                         context,
                         SplitViewOrientation.LeftRight),
                     context => SplitTabFromCommandPaletteAsync(
                         context,
                         SplitViewOrientation.TopBottom),
                     SwapSplitPanesFromCommandPaletteAsync,
                     ToggleSplitOrientationFromCommandPaletteAsync,
                     DetachSplitFromCommandPaletteAsync,
                     () => CloseSplitPaneFromCommandPaletteAsync(SplitViewPane.Left),
                     () => CloseSplitPaneFromCommandPaletteAsync(SplitViewPane.Right),
                     OpenYouTubeMiniPlayerListFromCommandPaletteAsync,
                     OpenYouTubeMiniPlayerFromCommandContextAsync,
                     () => SetAllYouTubeMiniPlayersMutedAsync(true),
                     () => SetAllYouTubeMiniPlayersMutedAsync(false),
                     PauseAllYouTubeMiniPlayersAsync,
                     PlaySelectedYouTubeMiniPlayersOnlyAsync,
                     PauseOutsideSelectedYouTubeMiniPlayersAsync,
                     MuteOutsideSelectedYouTubeMiniPlayersAsync,
                     RestoreYouTubeMiniPlayerAudioFocusAsync,
                     () => SetAllYouTubeMiniPlayersMinimizedFromCommandPaletteAsync(true),
                     () => SetAllYouTubeMiniPlayersMinimizedFromCommandPaletteAsync(false),
                     CloseAllYouTubeMiniPlayersFromCommandPaletteAsync,
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Grid),
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Horizontal),
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Vertical),
                     ArrangeYouTubeMiniPlayersRandomFromCommandPaletteAsync,
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Focus),
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Monitor),
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.NormalAndShorts),
                     () => ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
                         YouTubeMiniPlayerArrangeMode.Chat),
                     RestoreYouTubeMiniPlayerArrangementFromCommandPaletteAsync,
                     OpenRuntimeDiagnosticsFromCommandPaletteAsync))
        {
            _commandRegistry.Register(action);
        }

        foreach (var action in MiniPlayerCommandProvider.CreateActions(
                     new MiniPlayerCommandCallbacks(
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerPlayPauseCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerToggleMuteCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerSoloAudioCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerSoloPlaybackCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerSeekRelativeCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerSeekToLiveEdgeCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerToggleLoopCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerSetSpeedCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerAbRepeatCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerReloadCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerActivateSourceTabCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerToggleMinimizedCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerToggleTopmostCommandAsync,
                         _youTubeMiniPlayerCommandController.ExecuteMiniPlayerCloseCommandAsync)))
        {
            _commandRegistry.Register(action);
        }
    }

    private bool IsCommandPaletteOpen => _commandPaletteViewController?.IsCommandPaletteOpen == true;

    private static bool IsCommandPaletteShortcut(KeyEventArgs e)
    {
        return CommandPaletteKeyboardService.IsOpenShortcut(e.Key, e.SystemKey, Keyboard.Modifiers);
    }

    private bool IsCommandPaletteInputOwnerActive()
    {
        return IsActive
            || HasActiveOwnedWindow()
            || HasActiveYouTubeMiniPlayerWindow()
            || HasActiveYouTubeLiveChatWindow();
    }

    private bool HasActiveOwnedWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsActive || ReferenceEquals(window, this))
            {
                continue;
            }

            for (var owner = window.Owner; owner is not null; owner = owner.Owner)
            {
                if (ReferenceEquals(owner, this))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryHandleCommandPaletteKey(KeyEventArgs e)
        => _commandPaletteViewController?.TryHandleCommandPaletteKey(e) == true;

    private void OpenCommandPalette()
    {
        OpenCommandPalette(null, null, null);
    }

    private void OpenCommandPalette(
        BrowserTab? explicitContextTab,
        string? linkUrl,
        string? linkText)
    {
        CloseCommandPalettesInOtherWindows();
        _commandPaletteViewController.PrepareOpen();
        var contextGeneration = _commandPaletteContextCoordinator.Begin(
            explicitContextTab?.Id,
            linkUrl,
            linkText);
        _commandPaletteViewController.Show();
        StartCommandPaletteWorkspaceLoad();
        StartCommandPaletteApplicationLoad();
        StartCommandPaletteBrowsingHistoryLoad();
        _ = LoadCommandPaletteSelectionContextAsync(
            GetCommandPaletteContextTab(),
            contextGeneration);
    }

    private void OpenCommandPaletteForLink(
        BrowserTab sourceTab,
        string linkUrl,
        string? linkText)
    {
        if (!_tabs.Contains(sourceTab) || !IsSplitViewLinkUrl(linkUrl))
        {
            return;
        }

        OpenCommandPalette(sourceTab, linkUrl, linkText);
    }

    private void CloseCommandPalettesInOtherWindows()
    {
        foreach (var window in Application.Current.Windows.OfType<MainWindow>())
        {
            if (!ReferenceEquals(window, this))
            {
                window.CloseCommandPalette();
            }
        }
    }

    private void QueueCommandPaletteSearchFocus()
        => _commandPaletteViewController?.QueueCommandPaletteSearchFocus();

    private void CloseCommandPalette()
    {
        _commandPaletteDataController.CancelAll();
        StopCommandPaletteUrlThumbnailPlayback();
        _commandPaletteViewController?.Close();
    }

    private void RefreshCommandPaletteItems()
        => _commandPaletteViewController?.RefreshCommandPaletteItems();

    private CommandPaletteItem? CreateCommandPaletteSearchAliasItem(string? input)
    {
        if (_addressService.ResolveSearchAlias(input) is not { } resolution)
        {
            return null;
        }

        var host = Uri.TryCreate(resolution.Url, UriKind.Absolute, out var uri)
            ? uri.Host
            : resolution.Url;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return new CommandPaletteItem
        {
            Id = $"search-alias:{resolution.Alias}",
            Type = CommandPaletteItemType.Command,
            Title = $"{resolution.Alias} で「{resolution.Query}」を検索",
            Subtitle = host,
            Url = resolution.Url,
            Keywords = [resolution.Alias, resolution.Query, "検索", "search"],
            ActionId = SearchAliasCommandActionId,
            Payload = resolution,
            Category = "Web検索"
        };
    }

    private BrowserTab? GetCommandPaletteContextTab()
    {
        var context = _commandPaletteContextCoordinator.CaptureSnapshot();
        return context.ExplicitContextTabId is { } explicitTabId
            ? _tabs.FirstOrDefault(tab => tab.Id == explicitTabId)
            : GetActiveBrowserCommandTarget();
    }

    private CommandInvocationContext CreateCommandInvocationContext(BrowserTab? tab = null)
    {
        var context = _commandPaletteContextCoordinator.CaptureSnapshot();
        tab ??= context.ExplicitContextTabId is { } explicitTabId
            ? _tabs.FirstOrDefault(current => current.Id == explicitTabId)
            : GetActiveBrowserCommandTarget();
        var surface = tab switch
        {
            { } folderTab when GetTabHostedContent(folderTab) is Views.FolderTabView =>
                CommandSurface.FolderTab,
            { IsWorkspaceTab: true } => CommandSurface.Workspace,
            { } webTab when GetTabWebView(webTab)?.CoreWebView2 is not null =>
                CommandSurface.Web,
            _ => CommandSurface.Global
        };
        var selectionCount = GetTabHostedContent(tab) switch
        {
            Views.FolderTabView folderTabView => folderTabView.SelectedItemCount,
            Views.WorkspaceOSView workspaceView => workspaceView.SelectedItemCount,
            _ => string.IsNullOrWhiteSpace(context.SelectedText) ? 0 : 1
        };
        var hasLink = tab is not null
            && context.ExplicitContextTabId == tab.Id
            && !string.IsNullOrWhiteSpace(context.LinkUrl);
        var hasMiniPlayers = GetOpenMainWindows()
            .Any(window => window._youTubeMiniPlayerRuntimes.Count > 0);
        return new CommandInvocationContext(
            surface,
            IsSplitView: tab is not null
                && _splitViewService.IsActive
                && _splitViewService.Contains(tab),
            HasSelection: surface == CommandSurface.Web
                && !string.IsNullOrWhiteSpace(context.SelectedText),
            HasLink: hasLink,
            HasMiniPlayers: hasMiniPlayers,
            SelectionCount: selectionCount,
            TargetId: tab?.Id,
            SelectedText: context.SelectedText,
            LinkUrl: hasLink ? context.LinkUrl : string.Empty,
            LinkText: hasLink ? context.LinkText : string.Empty,
            CanNavigateBack: CanNavigateBrowserHistory(tab, offset: -1),
            CanNavigateForward: CanNavigateBrowserHistory(tab, offset: 1));
    }

    private async Task LoadCommandPaletteSelectionContextAsync(
        BrowserTab? tab,
        int generation)
    {
        if (tab is null || GetTabWebView(tab)?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            var selectionJson = await core.ExecuteScriptAsync(
                "(() => { const selection = window.getSelection(); return selection ? selection.toString() : ''; })();");
            var selectedText = JsonSerializer.Deserialize<string>(selectionJson)?.Trim() ?? string.Empty;
            if (selectedText.Length > TranslateSelectedTextMaxLength)
            {
                selectedText = selectedText[..TranslateSelectedTextMaxLength];
            }

            if (_isClosingWindow
                || !_commandPaletteContextCoordinator.IsCurrent(generation)
                || !IsCommandPaletteOpen
                || !_tabs.Contains(tab)
                || !ReferenceEquals(GetCommandPaletteContextTab(), tab))
            {
                return;
            }

            if (_commandPaletteContextCoordinator.TrySetSelectedText(generation, selectedText))
            {
                RefreshCommandPaletteItems();
            }
        }
        catch (Exception)
        {
            // 選択状態の取得失敗は通常候補へ安全にフォールバックする。
        }
    }

    private void CommandPaletteShowAllCheckBox_Changed(object sender, RoutedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteShowAllCheckBox_Changed(sender, e);

    private void CommandPaletteCommandsOnlyCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteCommandsOnlyCheckBox_Changed(sender, e);

    private IReadOnlyList<MiniPlayerCommandSource> GetCommandPaletteMiniPlayerSources()
    {
        return GetOpenMainWindows()
            .SelectMany(window => window._youTubeMiniPlayers
                .Where(state => window._youTubeMiniPlayerRuntimes.ContainsKey(state.Id))
                .Select(state => new MiniPlayerCommandSource(
                    state.Id,
                    state.Title,
                    state.Url,
                    state.IsPaused == true,
                    state.IsMuted,
                    state.IsMinimized,
                    ReferenceEquals(window, this) ? string.Empty : $"別ウィンドウ {window._workspaceWindowIndex}")))
            .ToList();
    }

    private void StartCommandPaletteWorkspaceLoad()
        => _commandPaletteDataController.StartWorkspaceLoad();

    private void StartCommandPaletteBrowsingHistoryLoad()
        => _commandPaletteDataController.StartBrowsingHistoryLoad();

    private void StartCommandPaletteApplicationLoad()
        => _commandPaletteDataController.StartApplicationLoad();

    private void QueueCommandPaletteApplicationIconLoad()
        => _commandPaletteDataController.QueueApplicationIconLoad();

    private IReadOnlyList<TabCommandSource> GetCommandPaletteTabSources()
    {
        return GetOpenMainWindows()
            .SelectMany(window =>
            {
                var windowLabel = ReferenceEquals(window, this)
                    ? string.Empty
                    : $"別ウィンドウ {window._workspaceWindowIndex}";
                return window._tabs.Select(tab => new TabCommandSource(tab, windowLabel));
            })
            .ToList();
    }

    private void QueueCommandPaletteExecution(
        CommandPaletteExecutionMode executionMode = CommandPaletteExecutionMode.Default,
        CommandPaletteItem? item = null)
    {
        item ??= CommandPaletteList.SelectedItem as CommandPaletteItem;
        if (item is null)
        {
            return;
        }

        _windowTaskSupervisor.TryRun(
            "command-palette-execute",
            cancellationToken => ExecuteCommandPaletteItemAsync(item, executionMode, cancellationToken));
    }

    private Task ExecuteCommandPaletteItemAsync(
        CommandPaletteItem item,
        CommandPaletteExecutionMode executionMode = CommandPaletteExecutionMode.Default,
        CancellationToken cancellationToken = default)
        => _commandPaletteController.ExecuteSelectionAsync(item, executionMode, this, cancellationToken);

    CommandInvocationContext ICommandPaletteExecutionHost.CreateInvocationContext() => CreateCommandInvocationContext();
    void ICommandPaletteExecutionHost.ClosePalette() => CloseCommandPalette();
    void ICommandPaletteExecutionHost.RecordUsage(CommandPaletteItem item) => RecordCommandPaletteUsage(item);
    void ICommandPaletteExecutionHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    private void RecordCommandPaletteUsage(CommandPaletteItem item)
        => _commandPaletteViewController?.RecordCommandPaletteUsage(item);

    private void CommandPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteSearchBox_TextChanged(sender, e);

    private void CommandPaletteSearchBox_PreviewKeyUp(object sender, KeyEventArgs e)
        => _commandPaletteViewController?.CommandPaletteSearchBox_PreviewKeyUp(sender, e);

    private void CommandPaletteSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteSearchBox_LostKeyboardFocus(sender, e);

    private void CommandPaletteList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _commandPaletteViewController?.CommandPaletteList_PreviewMouseLeftButtonUp(sender, e);

    private void CommandPaletteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _commandPaletteViewController?.CommandPaletteList_SelectionChanged(sender, e);

    private void CommandPalettePreviewButton_Click(object sender, RoutedEventArgs e)
        => _commandPaletteViewController?.CommandPalettePreviewButton_Click(sender, e);

    private async void CommandPaletteGestureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandPaletteItem
            {
                CanConfigureGesture: true,
                GestureCommandId.Length: > 0
            } item)
        {
            return;
        }

        e.Handled = true;
        CloseCommandPalette();
        await OpenMouseGestureSettingsWindowAsync(item.GestureCommandId);
    }

    private void CommandPaletteItem_MouseEnter(object sender, MouseEventArgs e)
    {
        StartCommandPaletteThumbnailCapture((sender as FrameworkElement)?.DataContext as CommandPaletteItem);
    }

    private void CommandPaletteItem_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        StartCommandPaletteThumbnailCapture((sender as FrameworkElement)?.DataContext as CommandPaletteItem);
    }

    private void CommandPaletteItem_ToolTipClosing(object sender, ToolTipEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommandPaletteItem item && !item.IsTab)
        {
            StopCommandPaletteUrlThumbnailPlayback(item.Url);
        }
    }

    private void StartCommandPaletteThumbnailCapture(CommandPaletteItem? item)
    {
        if (item is null || !item.SupportsThumbnail || item.HasThumbnail)
        {
            return;
        }

        if (item.IsTab)
        {
            _windowTaskSupervisor.TryRun(
                "command-palette-tab-thumbnail",
                cancellationToken => CaptureCommandPaletteTabThumbnailAsync(item, cancellationToken));
        }
        else
        {
            _windowTaskSupervisor.TryRun(
                "command-palette-url-thumbnail",
                _ => CaptureCommandPaletteUrlThumbnailAsync(item));
        }
    }

    private async Task CaptureCommandPaletteTabThumbnailAsync(
        CommandPaletteItem item,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || !item.IsTab
            || item.HasThumbnail
            || item.Payload is not Guid tabId)
        {
            return;
        }

        var target = GetOpenMainWindows()
            .SelectMany(window => window._tabs.Select(tab => new { Window = window, Tab = tab }))
            .FirstOrDefault(entry => entry.Tab.Id == tabId);
        if (target is null)
        {
            return;
        }

        await target.Window
            .CaptureTabThumbnailAsync(target.Tab)
            .WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Tab.Thumbnail is { } thumbnail)
        {
            item.Thumbnail = thumbnail;
        }
    }

    private Task CaptureCommandPaletteUrlThumbnailAsync(CommandPaletteItem item)
        => _urlThumbnailCaptureController.CaptureItemAsync(item);

    private Task<ImageSource?> CaptureWorkspaceUrlThumbnailAsync(string url, string title)
        => _urlThumbnailCaptureController.CaptureWorkspaceAsync(url, title);

    private void DisposeCommandPaletteUrlThumbnailWebView()
        => _urlThumbnailCaptureController.ResetWebView();

    private void StopCommandPaletteUrlThumbnailPlayback(string? expectedUrl = null)
        => _urlThumbnailCaptureController.StopPlayback(expectedUrl);

    private async Task ActivateCommandPaletteTabAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is not Guid tabId)
        {
            SetStatus("移動先タブを判定できません", autoClear: true);
            return;
        }

        var target = GetOpenMainWindows()
            .Select(window => new
            {
                Window = window,
                Tab = window._tabs.FirstOrDefault(tab => tab.Id == tabId)
            })
            .FirstOrDefault(x => x.Tab is not null);
        if (target?.Tab is not { } tab)
        {
            SetStatus("移動先タブが見つかりません", autoClear: true);
            return;
        }

        if (context.ExecutionMode != CommandPaletteExecutionMode.Default)
        {
            var openedTab = OpenCommandPaletteTabCopy(tab, context.SelectedItem, activate: context.ExecutionMode == CommandPaletteExecutionMode.NewTab);
            if (context.ExecutionMode == CommandPaletteExecutionMode.Split)
            {
                await StartCommandPaletteSplitAsync(openedTab);
            }

            return;
        }

        var targetWindow = target.Window;
        if (targetWindow.WindowState == WindowState.Minimized)
        {
            targetWindow.WindowState = WindowState.Normal;
        }

        targetWindow.Activate();
        targetWindow.SelectTab(tab);
        await targetWindow.ActivateSelectedTabAsync();
        targetWindow.SetStatus("タブへ移動しました: " + tab.DisplayTitle, autoClear: true);
    }

    private async Task RestoreClosedTabFromCommandPaletteAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is not ClosedTabState closedTab)
        {
            SetStatus("復元する閉じたタブを判定できません", autoClear: true);
            return;
        }

        var splitLeftTab = context.ExecutionMode == CommandPaletteExecutionMode.Split ? ActiveTab : null;
        RestoreClosedTab(closedTab);
        if (splitLeftTab is not null && ActiveTab is { } restoredTab && restoredTab.Id != splitLeftTab.Id)
        {
            await StartSplitViewAsync(splitLeftTab, restoredTab);
        }
    }

    private Task OpenBookmarkFromCommandPaletteAsync(CommandActionContext context)
        => _commandPaletteController.OpenBookmarkFromCommandPaletteAsync(context, this);

    private Task OpenHistoryFromCommandPaletteAsync(CommandActionContext context)
        => _commandPaletteController.OpenHistoryFromCommandPaletteAsync(context, this);

    private Task OpenSearchAliasFromCommandPaletteAsync(CommandActionContext context)
        => _commandPaletteController.OpenSearchAliasFromCommandPaletteAsync(context, this);

    IEnumerable<BookmarkItem> ICommandPaletteNavigationHost.Bookmarks => _bookmarks;
    BrowserTab? ICommandPaletteNavigationHost.ActiveTab => ActiveTab;
    Task ICommandPaletteNavigationHost.OpenBookmarkAsync(BookmarkItem bookmark) => OpenBookmarkAsync(bookmark);
    Task ICommandPaletteNavigationHost.RecordBookmarkOpenAsync(BookmarkItem bookmark)
        => _bookmarkController.RecordOpenAsync(bookmark);
    BrowserTab ICommandPaletteNavigationHost.OpenTab(
        string url, bool activate, string? title, bool queueBackgroundLoading, string? containerProfileId)
        => OpenTab(url, activate, title, queueBackgroundLoading: queueBackgroundLoading, containerProfileId: containerProfileId);
    Task ICommandPaletteNavigationHost.StartSplitAsync(BrowserTab tab) => StartCommandPaletteSplitAsync(tab);
    Task ICommandPaletteNavigationHost.OpenUrlAsync(string url, string? title, string? containerProfileId)
        => OpenCommandPaletteUrlAsync(url, title, containerProfileId);
    void ICommandPaletteNavigationHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    private BrowserTab OpenCommandPaletteTabCopy(
        BrowserTab sourceTab,
        CommandPaletteItem? item,
        bool activate)
    {
        var title = item?.Title ?? sourceTab.Title;
        var url = NormalizeSessionTabUrl(
            string.IsNullOrWhiteSpace(item?.Url) ? sourceTab.Url : item.Url,
            title) ?? NewTabUrl;
        return OpenTab(
            url,
            activate,
            title,
            navigationHistory: sourceTab.NavigationHistory,
            navigationHistoryIndex: sourceTab.NavigationHistoryIndex,
            audioPan: sourceTab.AudioPan,
            isMuted: sourceTab.IsMuted,
            youTubePlaybackVideoId: sourceTab.YouTubePlaybackVideoId,
            youTubePlaybackPositionSeconds: sourceTab.YouTubePlaybackPositionSeconds,
            youTubePlaybackSpeed: sourceTab.YouTubePlaybackSpeed,
            youTubePlaybackIsPaused: sourceTab.YouTubePlaybackIsPaused,
            queueBackgroundLoading: activate,
            youTubePlaybackIsLooping: sourceTab.YouTubePlaybackIsLooping,
            youTubePlaybackIsTheaterMode: sourceTab.YouTubePlaybackIsTheaterMode,
            containerProfileId: sourceTab.ContainerProfileId);
    }

    private async Task StartCommandPaletteSplitAsync(BrowserTab rightTab)
    {
        if (ActiveTab is not { } leftTab || leftTab.Id == rightTab.Id || !CanOpenInSplitView(leftTab))
        {
            SelectTab(rightTab);
            await ActivateSelectedTabAsync();
            SetStatus("現在のタブでは分割表示を開始できません", autoClear: true);
            return;
        }

        await StartSplitViewAsync(leftTab, rightTab);
    }

    private Task OpenNewTabInContainerFromCommandPaletteAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is not string containerProfileId)
        {
            SetStatus("開くコンテナを判定できません", autoClear: true);
            return Task.CompletedTask;
        }

        var profile = GetContainerProfile(containerProfileId);
        AddNewTab(activate: true, profile.Id);
        SetStatus($"コンテナで新しいタブを開きました: {profile.Name}", autoClear: true);
        return Task.CompletedTask;
    }

    private async Task ReopenActiveTabInContainerFromCommandPaletteAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is not string containerProfileId)
        {
            SetStatus("切り替えるコンテナを判定できません", autoClear: true);
            return;
        }

        if (ActiveTab is not { IsWorkspaceTab: false } activeTab)
        {
            SetStatus("現在タブはコンテナで開き直せません", autoClear: true);
            return;
        }

        await ReopenTabInContainerAsync(activeTab, containerProfileId);
    }

    private async Task OpenContainerSettingsFromCommandPaletteAsync(CommandActionContext context)
    {
        await OpenSettingsDialogAsync();
    }

    private async Task OpenCommandPaletteUrlAsync(
        string url,
        string? title,
        string? containerProfileId = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("URLが空です", autoClear: true);
            return;
        }

        if (ActiveTab is { } activeTab && IsNewTabUrl(activeTab.Url))
        {
            await NavigateTabToUrlAsync(activeTab, url, title);
            return;
        }

        OpenTab(url, activate: true, title, containerProfileId: containerProfileId);
    }

    private Task ExecuteBrowserCommandFromCommandPaletteAsync(
        string command,
        CommandActionContext context)
    {
        return ExecuteBrowserCommandAsync(
            command,
            context.InvocationContext?.SelectedText,
            context.ActiveTabId);
    }

    private Task FocusLocationFromCommandPaletteAsync()
    {
        TryFocusActiveLocation();
        return Task.CompletedTask;
    }

    private Task ExecuteRegisteredCommandAsync(
        string actionId,
        BrowserTab? target = null,
        object? parameter = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var invocationContext = CreateCommandInvocationContext(
            target ?? GetActiveBrowserCommandTarget());
        return ExecuteRegisteredCommandAsync(
            actionId,
            invocationContext,
            parameter,
            showFailure: true,
            cancellationToken: cancellationToken);
    }

    private void QueueSearchSelectionCommand(BrowserTab target, string? selectedText)
    {
        _windowTaskSupervisor.TryRun(
            "search-selection-command",
            cancellationToken => ExecuteSearchSelectionCommandAsync(target, selectedText, cancellationToken));
    }

    private Task ExecuteSearchSelectionCommandAsync(
        BrowserTab target,
        string? selectedText,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var normalizedSelection = selectedText?.Trim() ?? string.Empty;
        var invocationContext = CreateCommandInvocationContext(target) with
        {
            HasSelection = normalizedSelection.Length > 0,
            SelectionCount = normalizedSelection.Length > 0 ? 1 : 0,
            SelectedText = normalizedSelection
        };
        return ExecuteRegisteredCommandAsync(
            ContextCommandProvider.SearchSelectionActionId,
            invocationContext,
            parameter: null,
            showFailure: true,
            cancellationToken: cancellationToken);
    }

    private async Task ExecuteRegisteredCommandAsync(
        string actionId,
        CommandInvocationContext invocationContext,
        object? parameter,
        bool showFailure,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var result = await _commandRegistry.ExecuteAsync(
            actionId,
            new CommandActionContext(
                invocationContext.TargetId,
                null,
                CommandPaletteExecutionMode.Default,
                invocationContext,
                parameter));
        if (cancellationToken.IsCancellationRequested
            || result.IsSuccess
            || result.Status == CommandExecutionStatus.Cancelled
            || !showFailure)
        {
            return;
        }

        SetStatus(result.Message, autoClear: true);
    }

    private async Task ExecuteGestureCommandAsync(string? command, string? selectedText)
    {
        await ExecuteGestureCommandForTargetAsync(
            command,
            selectedText,
            GetActiveBrowserCommandTarget());
    }

    private void QueueGestureCommandExecution(
        string? command,
        string? selectedText,
        BrowserTab? target,
        string? linkUrl = null,
        string? linkText = null)
    {
        _windowTaskSupervisor.TryRun(
            "gesture-command-execute",
            cancellationToken => ExecuteGestureCommandForTargetAsync(
                command,
                selectedText,
                target,
                linkUrl,
                linkText,
                cancellationToken));
    }

    private async Task ExecuteGestureCommandForTargetAsync(
        string? command,
        string? selectedText,
        BrowserTab? target,
        string? linkUrl = null,
        string? linkText = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!GestureCommandActionMap.TryGetActionId(command, out var actionId)
            && (string.IsNullOrWhiteSpace(command)
                || !_commandRegistry.TryGetAction(command, out _)))
        {
            await ExecuteBrowserCommandAsync(command, selectedText, target?.Id);
            return;
        }

        actionId = string.IsNullOrWhiteSpace(actionId) ? command! : actionId;
        var normalizedSelection = selectedText?.Trim() ?? string.Empty;
        var normalizedLinkUrl = linkUrl?.Trim() ?? string.Empty;
        var invocationContext = CreateCommandInvocationContext(target) with
        {
            HasSelection = normalizedSelection.Length > 0,
            HasLink = normalizedLinkUrl.Length > 0,
            SelectionCount = normalizedSelection.Length > 0 ? 1 : 0,
            SelectedText = normalizedSelection,
            LinkUrl = normalizedLinkUrl,
            LinkText = linkText?.Trim() ?? string.Empty
        };
        await ExecuteRegisteredCommandAsync(
            actionId,
            invocationContext,
            parameter: null,
            showFailure: false,
            cancellationToken: cancellationToken);
    }

    private Task OpenWorkspaceFromCommandPaletteAsync()
    {
        OpenWorkspaceTab();
        return Task.CompletedTask;
    }

    private Task ToggleWorkspaceAsync()
    {
        ToggleWorkspaceTab();
        return Task.CompletedTask;
    }

    private async Task OpenRecentItemsFromCommandPaletteAsync(CommandActionContext context)
    {
        var workspaceTab = EnsureWorkspaceTabOpen(activate: true);
        await ActivateSelectedTabAsync();
        if (GetTabHostedContent(workspaceTab) is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.ShowRecentItems();
        }
    }

    private Task OpenWorkspaceWindowFromCommandPaletteAsync()
    {
        ShowWorkspaceOSWindow();
        return Task.CompletedTask;
    }

    private Task FocusSplitPaneFromCommandPaletteAsync(string pane)
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            SetStatus("分割表示は開かれていません", autoClear: true);
            return Task.CompletedTask;
        }

        ActivateSplitPane(pane);
        return Task.CompletedTask;
    }

    private async Task SplitTabFromCommandPaletteAsync(
        CommandActionContext context,
        string orientation)
    {
        var target = context.ActiveTabId is { } targetId
            ? _tabs.FirstOrDefault(tab => tab.Id == targetId)
            : GetActiveBrowserCommandTarget();
        var anchor = context.Parameter is Guid anchorId
            ? _tabs.FirstOrDefault(tab => tab.Id == anchorId)
            : ActiveTab;
        if (target is null || anchor is null || target == anchor)
        {
            SetStatus("分割表示するタブを特定できません", autoClear: true);
            return;
        }

        await StartSplitViewWithRightTabAsync(target, anchor, orientation);
    }

    private Task ToggleSplitOrientationFromCommandPaletteAsync()
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            SetStatus("分割表示は開かれていません", autoClear: true);
            return Task.CompletedTask;
        }

        ToggleSplitOrientation();
        return Task.CompletedTask;
    }

    private Task SwapSplitPanesFromCommandPaletteAsync()
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            SetStatus("分割表示は開かれていません", autoClear: true);
            return Task.CompletedTask;
        }

        SwapSplitPanes();
        return Task.CompletedTask;
    }

    private Task DetachSplitFromCommandPaletteAsync()
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            SetStatus("分割表示は開かれていません", autoClear: true);
            return Task.CompletedTask;
        }

        DetachSplitView(ActiveTab);
        return Task.CompletedTask;
    }

    private Task CloseSplitPaneFromCommandPaletteAsync(string pane)
    {
        if (!_splitViewService.IsActive || SplitViewHost.Visibility != Visibility.Visible)
        {
            SetStatus("分割表示は開かれていません", autoClear: true);
            return Task.CompletedTask;
        }

        CloseSplitPane(pane);
        return Task.CompletedTask;
    }

    private async Task OpenYouTubeMiniPlayerListFromCommandPaletteAsync()
    {
        if (!YouTubeMiniPlayerListPopup.IsOpen)
        {
            YouTubeMiniPlayerListPopup.IsOpen = true;
            return;
        }

        await RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: true);
    }

    private async Task OpenYouTubeMiniPlayerFromCommandContextAsync(CommandActionContext context)
    {
        var sourceTab = GetCommandPaletteTargetTab(context);
        if (sourceTab is null || !_tabs.Contains(sourceTab))
        {
            SetStatus("ミニプレイヤー化するタブを判定できません", autoClear: true);
            return;
        }

        var linkUrl = context.InvocationContext?.LinkUrl;
        if (!string.IsNullOrWhiteSpace(linkUrl) && IsYouTubeUrl(linkUrl))
        {
            await _youTubeMiniPlayerController.OpenFromLinkAsync(
                sourceTab,
                linkUrl,
                context.InvocationContext?.LinkText,
                _tabs,
                _workspaceWindowIndex,
                WebHost.ActualWidth,
                WebHost.ActualHeight,
                CreateYouTubeMiniPlayerOpenWorkflowCallbacks());
            return;
        }

        await _youTubeMiniPlayerController.OpenFromTabAsync(
            sourceTab,
            _tabs,
            _workspaceWindowIndex,
            WebHost.ActualWidth,
            WebHost.ActualHeight,
            CreateYouTubeMiniPlayerOpenWorkflowCallbacks());
    }

    private Task SetAllYouTubeMiniPlayersMinimizedFromCommandPaletteAsync(bool isMinimized)
    {
        SetAllYouTubeMiniPlayersMinimized(isMinimized);
        return Task.CompletedTask;
    }

    private Task CloseAllYouTubeMiniPlayersFromCommandPaletteAsync()
    {
        CloseAllYouTubeMiniPlayers();
        return Task.CompletedTask;
    }

    private Task ArrangeYouTubeMiniPlayersFromCommandPaletteAsync(
        YouTubeMiniPlayerArrangeMode mode)
    {
        BeginYouTubeMiniPlayerArrangementPreview(mode);
        return Task.CompletedTask;
    }

    private Task ArrangeYouTubeMiniPlayersRandomFromCommandPaletteAsync()
    {
        BeginYouTubeMiniPlayerArrangementPreview(YouTubeMiniPlayerArrangeMode.Random);
        return Task.CompletedTask;
    }

    private Task RestoreYouTubeMiniPlayerArrangementFromCommandPaletteAsync()
    {
        RestoreYouTubeMiniPlayerArrangement();
        return Task.CompletedTask;
    }

    private Task LaunchApplicationFromCommandPaletteAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is ApplicationLaunchItem application)
        {
            _applicationCatalogService.Launch(application);
        }

        return Task.CompletedTask;
    }

    private Task RefreshApplicationsFromCommandPaletteAsync()
        => _commandPaletteDataController.RefreshApplicationsAsync();

    private async Task OpenWorkspaceItemFromCommandPaletteAsync(CommandActionContext context)
    {
        switch (context.SelectedItem?.Payload)
        {
            case TabBrowser.Models.Workspaces.NoteItem note:
                await EditWorkspaceNoteAsync(note.WorkspaceId, note.Id, this);
                break;
            case TabBrowser.Models.Workspaces.BookmarkItem bookmark:
                if (context.ExecutionMode == CommandPaletteExecutionMode.Default)
                {
                    await OpenWorkspaceUrlAsync(bookmark.Url);
                }
                else
                {
                    var openedTab = OpenTab(
                        bookmark.Url,
                        activate: context.ExecutionMode == CommandPaletteExecutionMode.NewTab,
                        title: bookmark.Title,
                        queueBackgroundLoading: context.ExecutionMode != CommandPaletteExecutionMode.Split,
                        containerProfileId: ActiveTab?.ContainerProfileId);
                    if (context.ExecutionMode == CommandPaletteExecutionMode.Split)
                    {
                        await StartCommandPaletteSplitAsync(openedTab);
                    }
                }

                break;
            case TabBrowser.Models.Workspaces.FolderItem folder:
                OpenWorkspaceFolderWindow(folder, selectedItemId: null);
                break;
            case TabBrowser.Models.Workspaces.SessionGroupItem session:
                await OpenWorkspaceSessionTabsAsync(session.Tabs);
                break;
            case TabBrowser.Models.Workspaces.YouTubeMiniPlayerSetItem set:
                await OpenWorkspaceMediaSetAsync(set);
                break;
            case TabBrowser.Models.Workspaces.ShellItem shellItem:
                try
                {
                    new TabBrowser.Services.Workspaces.WorkspaceShellItemService().Open(shellItem);
                }
                catch (Exception ex)
                {
                    SetStatus("ファイルを開けません: " + ex.Message, autoClear: true);
                }

                break;
        }
    }

    private Task FocusMiniPlayerFromCommandPaletteAsync(CommandActionContext context)
    {
        if (context.SelectedItem?.Payload is not Guid id)
        {
            return Task.CompletedTask;
        }

        var owner = GetOpenMainWindows().FirstOrDefault(window =>
            window._youTubeMiniPlayerRuntimes.ContainsKey(id)
            && window._youTubeMiniPlayers.Any(state => state.Id == id));
        var state = owner?._youTubeMiniPlayers.FirstOrDefault(candidate => candidate.Id == id);
        if (owner is null || state is null)
        {
            SetStatus("YouTubeミニプレイヤーが見つかりません", autoClear: true);
            return Task.CompletedTask;
        }

        var callbacks = owner.CreateYouTubeMiniPlayerCommandCallbacks();
        callbacks.SetMinimized(state, false);
        callbacks.BringToFront(id, true);
        return Task.CompletedTask;
    }

    private BrowserTab? GetCommandPaletteTargetTab(CommandActionContext context)
    {
        return context.InvocationContext?.TargetId is { } targetId
            ? _tabs.FirstOrDefault(tab => tab.Id == targetId)
            : GetActiveBrowserCommandTarget();
    }

    private Task CopyCommandPaletteSelectionAsync(CommandActionContext context)
    {
        CopySelectedText(context.InvocationContext?.SelectedText);
        return Task.CompletedTask;
    }

    private Task SearchCommandPaletteSelectionAsync(CommandActionContext context)
    {
        OpenSelectedTextSearch(
            GetCommandPaletteTargetTab(context),
            context.InvocationContext?.SelectedText);
        return Task.CompletedTask;
    }

    private Task TranslateCommandPaletteSelectionAsync(CommandActionContext context)
    {
        if (GetCommandPaletteTargetTab(context) is { } tab)
        {
            OpenContextMenuTranslation(
                tab,
                context.InvocationContext?.SelectedText,
                GetCurrentPageUrl(tab));
        }

        return Task.CompletedTask;
    }

    private Task OpenCommandPaletteLinkInNewTabAsync(CommandActionContext context)
    {
        var url = context.InvocationContext?.LinkUrl;
        if (GetCommandPaletteTargetTab(context) is { } tab && IsSplitViewLinkUrl(url))
        {
            OpenBackgroundTabNextTo(tab, NormalizeYouTubeShortsUrl(url!));
            SetStatus("リンクを右隣の裏タブで開きました", autoClear: true);
        }

        return Task.CompletedTask;
    }

    private async Task OpenCommandPaletteLinkInSplitAsync(CommandActionContext context)
    {
        var url = context.InvocationContext?.LinkUrl;
        if (GetCommandPaletteTargetTab(context) is { } tab && IsSplitViewLinkUrl(url))
        {
            await OpenLinkInRightSplitAsync(tab, url!);
        }
    }

    private Task CopyCommandPaletteLinkUrlAsync(CommandActionContext context)
    {
        CopyContextLink(
            LinkCopyFormat.Url,
            context.InvocationContext?.LinkUrl,
            context.InvocationContext?.LinkText,
            "リンクURLをコピーしました");
        return Task.CompletedTask;
    }

    private Task SortCommandPaletteFolderAsync(
        string sortMode,
        CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.FolderTabView folderTabView)
        {
            folderTabView.SetSortMode(sortMode);
        }

        return Task.CompletedTask;
    }

    private Task ToggleCommandPaletteFolderSortDirectionAsync(
        CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.FolderTabView folderTabView)
        {
            folderTabView.ToggleSortDirection();
        }

        return Task.CompletedTask;
    }

    private Task ApplyCommandPaletteFolderSortAsync(CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.FolderTabView folderTabView
            && context.Parameter is ValueTuple<string, string> sort)
        {
            folderTabView.SetSort(sort.Item1, sort.Item2);
        }

        return Task.CompletedTask;
    }

    private Task ShowCommandPaletteFolderQuickLookAsync(
        CommandActionContext context)
    {
        return GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.FolderTabView folderTabView
            ? folderTabView.ShowQuickLookForSelectionAsync()
            : Task.CompletedTask;
    }

    private Task ArrangeCommandPaletteWorkspaceSelectionAsync(
        CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.ArrangeSelectedCardsCompact();
        }

        return Task.CompletedTask;
    }

    private Task ShowCommandPaletteWorkspaceQuickLookAsync(
        CommandActionContext context)
    {
        return GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.WorkspaceOSView workspaceView
            ? workspaceView.ShowSelectedCardsQuickLookAsync()
            : Task.CompletedTask;
    }

    private Task ApplyCommandPaletteWorkspaceArrangementAsync(
        CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.WorkspaceOSView workspaceView
            && context.Parameter is WorkspaceArrangeRequest request)
        {
            workspaceView.ArrangeCards(request);
        }

        return Task.CompletedTask;
    }

    private Task RestoreCommandPaletteWorkspaceArrangementAsync(
        CommandActionContext context)
    {
        if (GetTabHostedContent(GetCommandPaletteTargetTab(context)) is Views.WorkspaceOSView workspaceView)
        {
            workspaceView.RestoreArrangement();
        }

        return Task.CompletedTask;
    }

    private bool TryHandleCommandPaletteThreadShortcut(MSG msg)
    {
        if (msg.message is not (WmKeyDown or WmSystemKeyDown))
        {
            return false;
        }

        if (!CommandPaletteKeyboardService.IsOpenShortcutVirtualKey(
                msg.wParam.ToInt32(),
                IsVirtualKeyDown(VirtualKeyControl),
                IsVirtualKeyDown(VirtualKeyShift),
                IsVirtualKeyDown(VirtualKeyMenu)))
        {
            return false;
        }

        ShowCommandPaletteFromShortcut();
        return true;
    }

    private bool TryHandleCommandPaletteThreadTextInput(MSG msg)
    {
        if (!IsCommandPaletteOpen
            || CommandPaletteSearchBox.IsKeyboardFocusWithin
            || msg.message is not (WmChar or WmImeChar or WmUniChar))
        {
            return false;
        }

        var codePoint = msg.wParam.ToInt32();
        switch (codePoint)
        {
            case '\b':
                BackspaceCommandPaletteSearchText();
                return true;
            case '\r':
            case '\n':
            case '\t':
            case 0x1B:
                return false;
        }

        if (codePoint <= 0 || codePoint > 0x10FFFF)
        {
            return false;
        }

        var text = char.ConvertFromUtf32(codePoint);
        if (text.Length == 1 && char.IsControl(text[0]))
        {
            return false;
        }

        InsertCommandPaletteSearchText(text);
        return true;
    }

    private void InsertCommandPaletteSearchText(string text)
        => _commandPaletteViewController?.QueueCommandPaletteTextInput(text);

    private void BackspaceCommandPaletteSearchText()
        => _commandPaletteViewController?.QueueCommandPaletteTextInput(null);

    private Task CopyCurrentPageFromCommandPaletteAsync(LinkCopyFormat format)
    {
        CopyCurrentPageLink(format);
        return Task.CompletedTask;
    }

    private Task CopyAllTabsMarkdownFromCommandPaletteAsync()
    {
        CopyAllTabsMarkdown();
        return Task.CompletedTask;
    }

    private Task OpenDownloadsFromCommandPaletteAsync()
    {
        _downloadShelfController?.Open();
        SetStatus("ダウンロード一覧を開きました", autoClear: true);
        return Task.CompletedTask;
    }

    private async Task OpenSettingsFromCommandPaletteAsync()
    {
        await OpenSettingsDialogAsync();
    }
}
