using System.IO;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal interface IFolderTabOpenView
{
    string CurrentPath { get; }
    string ViewMode { get; }
    string SortMode { get; }
    string SortDirection { get; }
    Task NavigateToAsync(string folderPath);
}

internal interface IFolderTabOpenHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    IFolderTabOpenView? GetFolderView(BrowserTab tab);
    BrowserTab CreateFolderTab(string folderPath, string? initialViewMode = null,
        string? initialSortMode = null, string? initialSortDirection = null,
        FolderTabSessionState? initialState = null);
    IEnumerable<int> GetSplitViewGroupIndices(string splitViewId);
    void InsertPreservedTab(BrowserTab tab, int insertionIndex, bool activate);
    void QueueSplitOpen(Func<CancellationToken, Task> operation);
    Task StartSplitViewAsync(BrowserTab leftTab, BrowserTab rightTab, string orientation, CancellationToken cancellationToken);
    void OpenWindow(SessionTabState initialTab);
    void SetStatus(string message, bool autoClear);
    void SelectTab(BrowserTab tab);
    void QueueSelectedTabActivation();
    void QueueSessionSave();
    void QueueWorkspaceOpenTabsRefresh();
}

/// <summary>FolderTabの展開先・隣接挿入・分割開始と反対側ペインへの移動手順を所有する。</summary>
internal sealed class FolderTabOpenController(
    IFolderTabOpenHost host, TabOperationService tabOperationService, SplitViewService splitViewService)
{
    private readonly IFolderTabOpenHost _host = host;
    private readonly TabOperationService _tabOperationService = tabOperationService;
    private readonly SplitViewService _splitViewService = splitViewService;

    public void OpenShellFolderInTab(string folderPath)
    {
        try
        {
            var tab = _host.CreateFolderTab(folderPath);

            _host.Tabs.Add(tab);
            _host.SelectTab(tab);
            _host.QueueSelectedTabActivation();
            _host.QueueSessionSave();
            _host.QueueWorkspaceOpenTabsRefresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _host.SetStatus("フォルダータブを開けませんでした: " + ex.Message, autoClear: true);
        }
    }

    public void OpenFolderFromFolderTab(
        BrowserTab sourceTab,
        IFolderTabOpenView sourceView,
        FolderTabOpenRequestedEventArgs args)
    {
        if (!_host.Tabs.Contains(sourceTab)
            || !ReferenceEquals(_host.GetFolderView(sourceTab), sourceView))
        {
            return;
        }

        if (args.Disposition == FolderTabOpenDisposition.NewWindow)
        {
            OpenFolderInNewWindow(sourceView, args.FolderPath);
            return;
        }

        if (args.Disposition is FolderTabOpenDisposition.OppositeFolderPane
            or FolderTabOpenDisposition.LeftRightSplit
            or FolderTabOpenDisposition.TopBottomSplit)
        {
            _host.QueueSplitOpen(
                cancellationToken => OpenFolderInSplitFromFolderTabAsync(
                    sourceTab,
                    sourceView,
                    args,
                    cancellationToken));
            return;
        }

        OpenFolderInAdjacentTab(
            sourceTab,
            sourceView,
            args.FolderPath,
            activate: args.Disposition == FolderTabOpenDisposition.ForegroundTab);
    }

    public void OpenFolderInAdjacentTab(
        BrowserTab sourceTab,
        IFolderTabOpenView sourceView,
        string folderPath,
        bool activate,
        FolderTabSessionState? initialState = null)
    {
        try
        {
            CreateAdjacentFolderTab(sourceTab, sourceView, folderPath, activate, initialState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _host.SetStatus("フォルダータブを開けませんでした: " + ex.Message, autoClear: true);
        }
    }

    private BrowserTab CreateAdjacentFolderTab(
        BrowserTab sourceTab,
        IFolderTabOpenView sourceView,
        string folderPath,
        bool activate,
        FolderTabSessionState? initialState = null)
    {
        var tab = _host.CreateFolderTab(
            folderPath,
            initialViewMode: sourceView.ViewMode,
            initialSortMode: sourceView.SortMode,
            initialSortDirection: sourceView.SortDirection,
            initialState: initialState);
        var sourceIndex = _host.Tabs.IndexOf(sourceTab);
        var activeIndex = _host.ActiveTab is { } activeTab ? _host.Tabs.IndexOf(activeTab) : -1;
        var insertionIndex = _tabOperationService.GetAdjacentInsertionIndex(
            _host.Tabs.Count,
            sourceIndex,
            activeIndex);
        if (!string.IsNullOrWhiteSpace(sourceTab.SplitViewId))
        {
            insertionIndex = _tabOperationService.GetInsertionIndexAfterGroup(
                _host.Tabs.Count,
                insertionIndex,
                _host.GetSplitViewGroupIndices(sourceTab.SplitViewId));
        }

        _host.InsertPreservedTab(tab, insertionIndex, activate);
        return tab;
    }

    public FolderTabSplitOpenMode GetFolderTabSplitOpenMode(
        BrowserTab sourceTab,
        IFolderTabOpenView sourceView)
    {
        if (!_host.Tabs.Contains(sourceTab)
            || !ReferenceEquals(_host.GetFolderView(sourceTab), sourceView))
        {
            return FolderTabSplitOpenMode.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(sourceTab.SplitViewId))
        {
            return FolderTabSplitOpenMode.NewSplit;
        }

        var partnerTab = _splitViewService.GetPartnerTab(_host.Tabs, sourceTab);
        return partnerTab is { IsFolderTab: true }
            && _host.GetFolderView(partnerTab) is IFolderTabOpenView
                ? FolderTabSplitOpenMode.OppositeFolderPane
                : FolderTabSplitOpenMode.Unavailable;
    }

    private async Task OpenFolderInSplitFromFolderTabAsync(
        BrowserTab sourceTab,
        IFolderTabOpenView sourceView,
        FolderTabOpenRequestedEventArgs args,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var mode = GetFolderTabSplitOpenMode(sourceTab, sourceView);
        if (args.Disposition == FolderTabOpenDisposition.OppositeFolderPane)
        {
            var targetTab = _splitViewService.GetPartnerTab(_host.Tabs, sourceTab);
            if (mode != FolderTabSplitOpenMode.OppositeFolderPane
                || targetTab is not { IsFolderTab: true }
                || _host.GetFolderView(targetTab) is not IFolderTabOpenView targetView)
            {
                _host.SetStatus("反対側にFolderTabがありません", autoClear: true);
                return;
            }

            await targetView.NavigateToAsync(args.FolderPath);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (string.Equals(targetView.CurrentPath, args.FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                _host.SetStatus("反対側のFolderTabで開きました", autoClear: true);
            }

            return;
        }

        if (mode != FolderTabSplitOpenMode.NewSplit)
        {
            _host.SetStatus("このFolderTabでは新しい分割を開始できません", autoClear: true);
            return;
        }

        try
        {
            var rightTab = CreateAdjacentFolderTab(
                sourceTab,
                sourceView,
                args.FolderPath,
                activate: false);
            var orientation = args.Disposition == FolderTabOpenDisposition.TopBottomSplit
                ? SplitViewOrientation.TopBottom
                : SplitViewOrientation.LeftRight;
            await _host.StartSplitViewAsync(sourceTab, rightTab, orientation, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _host.SetStatus("フォルダーを分割表示で開けませんでした: " + ex.Message, autoClear: true);
        }
    }

    private void OpenFolderInNewWindow(IFolderTabOpenView sourceView, string folderPath)
    {
        try
        {
            var normalizedPath = FolderTabService.NormalizeExistingDirectory(folderPath);
            var initialTab = new SessionTabState
            {
                Url = normalizedPath,
                Title = FolderTabService.GetDisplayName(normalizedPath),
                IsFolderTab = true,
                FolderTabViewMode = sourceView.ViewMode,
                FolderTabSortMode = sourceView.SortMode,
                FolderTabSortDirection = sourceView.SortDirection
            };
            _host.OpenWindow(initialTab);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _host.SetStatus("フォルダーを新しいウィンドウで開けませんでした: " + ex.Message, autoClear: true);
        }
    }
}
