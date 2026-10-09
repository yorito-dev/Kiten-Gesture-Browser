using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal interface ICommandPaletteDataHost
{
    bool IsOpen { get; }
    IEnumerable<CommandPaletteItem> VisibleItems { get; }
    void RefreshItems();
    void SetStatus(string message);
}

internal sealed record CommandPaletteDataSources(
    Func<CancellationToken, Task<IReadOnlyList<WorkspaceCommandSource>>> Workspace,
    Func<CancellationToken, Task<IReadOnlyList<BrowsingHistoryEntry>>> BrowsingHistory,
    Func<CancellationToken, Task<IReadOnlyList<ApplicationLaunchItem>>> Applications,
    Func<string, CancellationToken, Task<ImageSource?>> ApplicationIcon,
    Action InvalidateApplicationCache);

// 候補データと取得処理を同じownerへ置き、表示と実コマンドの実行はhostへ残す。
internal sealed class CommandPaletteDataController : IDisposable
{
    private readonly ICommandPaletteDataHost _host;
    private readonly CommandPaletteDataSources _sources;
    private readonly CancellationToken _lifetimeToken;
    private readonly CommandPaletteBackgroundLoadCoordinator _backgroundLoad;
    private readonly ContextCommandProvider _contextProvider = new();
    private readonly HistoryCommandProvider _historyProvider = new();
    private readonly ApplicationCommandProvider _applicationProvider = new();
    private readonly List<CommandPaletteItem> _workspaceItems = [];
    private readonly List<CommandPaletteItem> _applicationItems = [];
    private readonly List<CommandPaletteItem> _browsingHistoryItems = [];
    private readonly HashSet<string> _applicationIconFailures = new(StringComparer.Ordinal);
    private bool _disposed;

    internal CommandPaletteDataController(
        WindowTaskSupervisor supervisor,
        ICommandPaletteDataHost host,
        CommandPaletteDataSources sources)
    {
        _host = host;
        _sources = sources;
        _lifetimeToken = supervisor.LifetimeToken;
        _backgroundLoad = new CommandPaletteBackgroundLoadCoordinator(supervisor);
    }

    internal IEnumerable<CommandPaletteItem> Items
        => _workspaceItems.Concat(_applicationItems).Concat(_browsingHistoryItems);

    internal void StartWorkspaceLoad()
    {
        _backgroundLoad.Start(CommandPaletteLoadLane.Workspace, _sources.Workspace, sources =>
        {
            _workspaceItems.Clear();
            _workspaceItems.AddRange(_contextProvider.CreateWorkspaceItems(sources));
            RefreshIfOpen();
        });
    }

    internal void StartBrowsingHistoryLoad()
    {
        _backgroundLoad.Start(CommandPaletteLoadLane.BrowsingHistory, _sources.BrowsingHistory, entries =>
        {
            _browsingHistoryItems.Clear();
            _browsingHistoryItems.AddRange(_historyProvider.CreateItems(entries));
            RefreshIfOpen();
        });
    }

    internal void StartApplicationLoad()
    {
        _backgroundLoad.Start(CommandPaletteLoadLane.Applications, _sources.Applications, applications =>
        {
            _applicationItems.Clear();
            _applicationItems.AddRange(_applicationProvider.CreateItems(applications));
            _applicationIconFailures.Clear();
            RefreshIfOpen();
        });
    }

    private void RefreshIfOpen()
    {
        if (_host.IsOpen)
        {
            _host.RefreshItems();
        }
    }

    internal void QueueApplicationIconLoad()
    {
        _backgroundLoad.Cancel(CommandPaletteLoadLane.ApplicationIcons);
        if (_disposed || !_host.IsOpen)
        {
            return;
        }

        var targets = _host.VisibleItems
            .Where(item => item.Type == CommandPaletteItemType.Application
                && item.Icon is null
                && !_applicationIconFailures.Contains(item.Id))
            .Take(CommandPaletteViewController.CommandPalettePageSelectionCount * 2)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _backgroundLoad.StartIcons(
            targets,
            static item => (item.Payload as ApplicationLaunchItem)?.IconLookupPath,
            _sources.ApplicationIcon,
            (item, icon) =>
            {
                if (icon is null)
                {
                    _applicationIconFailures.Add(item.Id);
                }
                else if (_host.IsOpen && _host.VisibleItems.Contains(item))
                {
                    item.Icon = icon;
                }
            });
    }

    internal async Task RefreshApplicationsAsync()
    {
        if (_disposed) return;
        _sources.InvalidateApplicationCache();
        _applicationItems.Clear();
        _applicationIconFailures.Clear();
        try
        {
            var applications = await _sources.Applications(_lifetimeToken);
            if (_disposed) return;
            _applicationItems.AddRange(_applicationProvider.CreateItems(applications));
            _host.SetStatus($"アプリ一覧を更新しました: {applications.Count} 件");
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _host.SetStatus("アプリ一覧を更新できません: " + ex.Message);
            }
        }
    }

    // Popupの自然closeはWorkspace取得を取り消さない。明示closeは全系統を取り消す。
    internal void CloseContext()
    {
        _backgroundLoad.Cancel(CommandPaletteLoadLane.Applications);
        _backgroundLoad.Cancel(CommandPaletteLoadLane.ApplicationIcons);
        _backgroundLoad.Cancel(CommandPaletteLoadLane.BrowsingHistory);
    }

    internal void CancelAll() => _backgroundLoad.CancelAll();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backgroundLoad.Dispose();
    }
}
