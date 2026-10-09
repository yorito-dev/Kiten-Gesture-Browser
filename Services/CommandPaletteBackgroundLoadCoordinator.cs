using TabBrowser.Services.Workspaces;

namespace TabBrowser.Services;

internal enum CommandPaletteLoadLane
{
    Workspace,
    BrowsingHistory,
    Applications,
    ApplicationIcons
}

/// <summary>
/// Window内の4系統の最新要求を管理する。UI呼出元のコンテキストを保ち、
/// 実行中Taskの監督は既存WindowTaskSupervisorに任せる。表示実体やキャッシュは所有しない。
/// </summary>
internal sealed class CommandPaletteBackgroundLoadCoordinator(WindowTaskSupervisor supervisor) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<CommandPaletteLoadLane, Request> _requests = [];
    private bool _disposed;

    public bool Start<T>(CommandPaletteLoadLane lane,
        Func<CancellationToken, Task<T>> fetch, Action<T> publish)
        => StartCore(lane, async request =>
        {
            var result = await fetch(request.Token);
            if (IsCurrent(request))
            {
                publish(result);
            }
        });

    public bool StartIcons<TTarget, TIcon>(IReadOnlyList<TTarget> targets,
        Func<TTarget, string?> getPath,
        Func<string, CancellationToken, Task<TIcon>> fetch,
        Action<TTarget, TIcon> publish)
    {
        var snapshot = targets.ToArray();
        return StartCore(CommandPaletteLoadLane.ApplicationIcons, async request =>
        {
            foreach (var target in snapshot)
            {
                if (!IsCurrent(request))
                {
                    return;
                }

                if (getPath(target) is not { } path)
                {
                    continue;
                }

                var icon = await fetch(path, request.Token);
                if (IsCurrent(request))
                {
                    publish(target, icon);
                }
            }
        });
    }

    public static async Task<IReadOnlyList<WorkspaceCommandSource>> FetchWorkspaceSourcesAsync(CancellationToken token)
    {
        var service = new WorkspaceService();
        var workspaces = await service.GetWorkspacesAsync(token);
        var sources = new List<WorkspaceCommandSource>();
        foreach (var workspace in workspaces)
        {
            var items = await service.GetWorkspaceItemsAsync(workspace.Id, token);
            sources.AddRange(items.Select(item => new WorkspaceCommandSource(item, workspace.Name)));
        }

        return sources;
    }

    public void Cancel(CommandPaletteLoadLane lane)
    {
        Request? request;
        lock (_gate)
        {
            _requests.Remove(lane, out request);
        }

        CancelRequest(request);
    }

    public void CancelAll() => InvalidateAll(dispose: false);

    public void Dispose() => InvalidateAll(dispose: true);

    private bool StartCore(CommandPaletteLoadLane lane, Func<Request, Task> load)
    {
        Request request;
        Request? previous;
        lock (_gate)
        {
            if (_disposed || supervisor.LifetimeToken.IsCancellationRequested)
            {
                return false;
            }

            request = new Request(lane, CancellationTokenSource.CreateLinkedTokenSource(supervisor.LifetimeToken));
            _requests.TryGetValue(lane, out previous);
            _requests[lane] = request;
        }

        var accepted = false;
        try
        {
            // 外部キャンセルcallbackの再入で次の要求が始まっても、古い要求を実行しない。
            CancelRequest(previous);
            accepted = IsCurrent(request) && supervisor.TryRun(OperationName(lane), _ => RunAsync(request, load));
            return accepted;
        }
        finally
        {
            if (!accepted)
            {
                Complete(request);
            }
        }
    }

    private async Task RunAsync(Request request, Func<Request, Task> load)
    {
        try
        {
            if (IsCurrent(request))
            {
                await load(request);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch when (request.Lane != CommandPaletteLoadLane.ApplicationIcons)
        {
            // 一覧取得失敗時は既存候補を維持する。アイコンの例外は従来どおりSupervisorが観測する。
        }
        finally
        {
            Complete(request);
        }
    }

    private bool IsCurrent(Request request)
    {
        lock (_gate)
        {
            return !_disposed && !request.Token.IsCancellationRequested
                && _requests.TryGetValue(request.Lane, out var current) && ReferenceEquals(current, request);
        }
    }

    private void Complete(Request request)
    {
        lock (_gate)
        {
            if (_requests.TryGetValue(request.Lane, out var current) && ReferenceEquals(current, request))
            {
                _requests.Remove(request.Lane);
            }
        }

        // 次要求・close側はCancelのみ。旧awaitが完了するまでCTSをDisposeしない。
        request.Cancellation.Dispose();
    }

    private void InvalidateAll(bool dispose)
    {
        Request[] requests;
        lock (_gate)
        {
            _disposed |= dispose;
            requests = _requests.Values.ToArray();
            _requests.Clear();
        }

        foreach (var request in requests)
        {
            CancelRequest(request);
        }
    }

    private static void CancelRequest(Request? request)
    {
        try
        {
            request?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完了済み要求との競合では、辞書からの無効化だけで十分。
        }
    }

    private static string OperationName(CommandPaletteLoadLane lane) => lane switch
    {
        CommandPaletteLoadLane.Workspace => "command-palette-workspace-load",
        CommandPaletteLoadLane.BrowsingHistory => "command-palette-browsing-history-load",
        CommandPaletteLoadLane.Applications => "command-palette-application-load",
        CommandPaletteLoadLane.ApplicationIcons => "command-palette-application-icon-load",
        _ => throw new ArgumentOutOfRangeException(nameof(lane))
    };

    private sealed class Request(CommandPaletteLoadLane lane, CancellationTokenSource cancellation)
    {
        public CommandPaletteLoadLane Lane { get; } = lane;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
    }
}
