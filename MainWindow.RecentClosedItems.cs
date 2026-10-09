using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IClosedItemThumbnailHost, IRecentClosedItemsHost
{
    private readonly ClosedItemThumbnailController _closedItemThumbnailController;
    private readonly RecentClosedItemsController _recentClosedItemsController;

    private void MergeProtectedClosedItemHistory(SessionData session)
    {
        ApplyClosedTabHistoryRetention(
            s_closedTabWindowHistoryStore.MergeProtectedHistory(session));

        var mergedMiniPlayers = CreateClosedYouTubeMiniPlayerHistorySnapshot();
        var miniPlayerIds = mergedMiniPlayers.Select(static player => player.Id).ToHashSet();
        foreach (var miniPlayer in YouTubeMiniPlayerStore.NormalizeClosedHistory(session.ClosedYouTubeMiniPlayers))
        {
            if (miniPlayerIds.Add(miniPlayer.Id))
            {
                mergedMiniPlayers.Add(miniPlayer);
            }
        }

        RestoreSharedClosedYouTubeMiniPlayerHistory(mergedMiniPlayers);
        RefreshClosedTabHistoryViews();
    }

    private void RememberClosedWindowAndTabs(bool queueSessionSave = true)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        _closedTabLifecycleController.RememberClosedWindowAndTabs(queueSessionSave);
    }

    private MainWindow GetClosedItemRestoreWindow(Guid? sourceWindowId)
    {
        var openWindows = GetOpenMainWindows()
            .Where(window => window._isPrivateWindow == _isPrivateWindow && !window._isClosingWindow)
            .ToList();
        var targetWindowId = _tabOperationService.ResolveClosedItemRestoreWindowId(
            sourceWindowId,
            _sessionWindowId,
            openWindows.Select(static window => window._sessionWindowId));
        return openWindows.FirstOrDefault(window => window._sessionWindowId == targetWindowId)
            ?? this;
    }

    private void RestoreSharedClosedWindowHistory(IEnumerable<ClosedWindowState>? closedWindows)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        _closedTabLifecycleController.ReplaceClosedWindowHistory(closedWindows);
    }

    private void RestoreClosedWindow(ClosedWindowState closedWindow)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        _closedTabLifecycleController.RestoreClosedWindow(closedWindow);
    }

    SessionWindowState IClosedTabLifecycleHost.CreateWindowSessionState() => CreateWindowSessionState();
    ClosedTabState IClosedTabLifecycleHost.CreateClosedTabState(SessionTabState tab, DateTimeOffset closedAt)
        => BrowserTabStateMapper.ToClosedTabState(tab, closedAt);
    void IClosedTabLifecycleHost.ShowRestoredWindow(SessionWindowState windowState)
    {
        var window = new MainWindow(windowState);
        window.Show();
    }
    void IClosedTabLifecycleHost.RecordClosedWindowCaptureFailure(Exception exception)
        => AppendSessionDiagnostic("closed-window-history-capture-failed", exception, $"window={_workspaceWindowIndex}");

    private void ClosedTabHistoryButton_Click(object sender, RoutedEventArgs e)
        => _recentClosedItemsController.OpenHistory();

    private async void ClosedTabHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await (_recentClosedItemsController?.RestoreSelectedItemAsync() ?? Task.CompletedTask);

    private void RecentClosedFilter_Checked(object sender, RoutedEventArgs e)
        => _recentClosedItemsController?.FilterChecked(sender);

    private void RecentClosedSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _recentClosedItemsController?.SearchChanged();

    private async void RecentClosedItem_ToolTipOpening(object sender, ToolTipEventArgs e)
        => await _recentClosedItemsController.OpenToolTipAsync(sender);

    private void RecentClosedItem_ToolTipClosing(object sender, ToolTipEventArgs e)
        => _recentClosedItemsController.CloseToolTip(sender);

    private void RefreshClosedTabHistoryView()
        => _recentClosedItemsController.Refresh();

    bool IRecentClosedItemsHost.HasClosedItems =>
        s_closedTabWindowHistoryStore.ClosedTabs.Count > 0
        || (!_isPrivateWindow && s_closedTabWindowHistoryStore.ClosedWindows.Count > 0)
        || (!_isPrivateWindow && HasClosedYouTubeMiniPlayer());
    IReadOnlyList<ClosedWindowState> IRecentClosedItemsHost.GetVisibleClosedWindows()
        => _isPrivateWindow ? [] : s_closedTabWindowHistoryStore.ClosedWindows;
    IReadOnlyList<YouTubeMiniPlayerState> IRecentClosedItemsHost.GetVisibleClosedMiniPlayers()
        => _isPrivateWindow ? [] : GetClosedYouTubeMiniPlayerHistory();
    void IRecentClosedItemsHost.TrimClosedTabHistory() => TrimClosedTabHistory();
    void IRecentClosedItemsHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IRecentClosedItemsHost.RestoreClosedWindow(ClosedWindowState state) => RestoreClosedWindow(state);
    void IRecentClosedItemsHost.RestoreClosedTab(ClosedTabState state) => RestoreClosedTab(state);
    Task IRecentClosedItemsHost.RestoreClosedYouTubeMiniPlayerAsync(Guid id) => RestoreClosedYouTubeMiniPlayerAsync(id);
    ImageSource IRecentClosedItemsHost.CreateClosedWindowThumbnail(ClosedWindowState state)
        => CreateClosedWindowThumbnail(state);
    Task<ImageSource> IRecentClosedItemsHost.GetOrCreateFolderTabThumbnailAsync(ClosedTabState state, string title)
        => GetOrCreateFolderTabThumbnailAsync(state, title);
    Func<Task<ImageSource?>>? IRecentClosedItemsHost.GetPreservedTabCapture(ClosedTabState state)
        => TryGetPreservedClosedTabRuntime(state, out var owner, out var tab)
            ? () => owner.GetOrCaptureClosedTabThumbnailAsync(tab)
            : null;
    void IRecentClosedItemsHost.StartCommandPaletteThumbnailCapture(CommandPaletteItem item)
        => StartCommandPaletteThumbnailCapture(item);
    void IRecentClosedItemsHost.StopCommandPaletteUrlThumbnailPlayback(string? url)
        => StopCommandPaletteUrlThumbnailPlayback(url);

    private static void RefreshClosedTabHistoryViews()
    {
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
        {
            window.RefreshClosedTabHistoryView();
            window.UpdateYouTubeMiniPlayerRestoreClosedButtonState();
            if (window.IsCommandPaletteOpen)
            {
                window.QueueCommandPaletteRefresh();
            }
        }
    }

    private bool HasSupplementalClosedItemHistory()
    {
        return !_isPrivateWindow
            && (s_closedTabWindowHistoryStore.ClosedWindows.Count > 0
                || HasClosedYouTubeMiniPlayer());
    }

    private static ImageSource? CreateClosedTabRuntimeThumbnail(BrowserTab tab)
    {
        if (GetTabHostedContent(tab) is FolderTabView folderTabView)
        {
            var entries = folderTabView.Items
                .Take(8)
                .Select(static item => new FolderTabThumbnailEntry(
                    item.Name,
                    item.IsDirectory,
                    item.Icon))
                .ToList();
            return CreateFolderTabThumbnail(
                tab.DisplayTitle,
                folderTabView.CurrentPath,
                entries,
                folderTabView.Items.Count,
                isTruncated: folderTabView.Items.Count > entries.Count);
        }

        return tab.Thumbnail;
    }

    private void BeginClosedTabThumbnailCapture(
        ClosedTabState closedTab,
        BrowserTab runtimeTab)
    {
        if (closedTab.RuntimeThumbnail is null)
        {
            _windowTaskSupervisor.TryRun(
                "closed-tab-thumbnail-capture",
                _ => CaptureClosedTabThumbnailForHistoryAsync(closedTab, runtimeTab));
        }
    }

    private Task CaptureClosedTabThumbnailForHistoryAsync(
        ClosedTabState closedTab,
        BrowserTab runtimeTab)
        => _closedItemThumbnailController.CaptureForHistoryAsync(closedTab, runtimeTab);

    private Task<ImageSource?> GetOrCaptureClosedTabThumbnailAsync(BrowserTab runtimeTab)
        => _closedItemThumbnailController.GetOrCaptureClosedTabThumbnailAsync(runtimeTab);

    Func<string, string?, CancellationToken, Task<ImageSource?>>? IClosedItemThumbnailHost.GetCaptureAction(BrowserTab tab)
        => GetTabWebView(tab)?.CoreWebView2 is { } core
            ? (title, url, token) => WebThumbnailCaptureService.CaptureAsync(core, title, url, token)
            : null;
    void IClosedItemThumbnailHost.SetThumbnail(BrowserTab tab, ImageSource image)
        => _ = s_tabRuntimeCoordinator.SetThumbnail(tab, image);
    ImageSource IClosedItemThumbnailHost.RenderFolder(
        string title, string folderPath, IReadOnlyList<FolderTabThumbnailEntry> entries,
        int itemCount, bool isTruncated, string? statusText)
        => s_previewThumbnailRenderer.CreateFolderTabThumbnail(title, folderPath, entries, itemCount, isTruncated, statusText);

    private static bool TryGetPreservedClosedTabRuntime(
        ClosedTabState closedTab,
        out MainWindow owner,
        out BrowserTab runtimeTab)
    {
        foreach (var window in Application.Current.Windows.OfType<MainWindow>())
        {
            if (window._closedTabLifecycleController.TryGetPreservedRuntime(closedTab, out var preservedTab))
            {
                owner = window;
                runtimeTab = preservedTab;
                return true;
            }
        }

        owner = null!;
        runtimeTab = null!;
        return false;
    }

    private Task<ImageSource> GetOrCreateFolderTabThumbnailAsync(ClosedTabState closedTab, string title)
        => _closedItemThumbnailController.GetOrCreateFolderTabThumbnailAsync(closedTab, title);

    private static ImageSource CreateFolderTabThumbnail(
        string title,
        string folderPath,
        IReadOnlyList<FolderTabThumbnailEntry> entries,
        int itemCount,
        bool isTruncated,
        string? statusText = null)
        => s_previewThumbnailRenderer.CreateFolderTabThumbnail(
            title, folderPath, entries, itemCount, isTruncated, statusText);

    private static ImageSource CreateClosedWindowThumbnail(ClosedWindowState closedWindow)
        => s_previewThumbnailRenderer.CreateClosedWindowThumbnail(closedWindow.WindowState.Tabs);

}
