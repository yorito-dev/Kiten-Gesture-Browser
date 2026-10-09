using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct YouTubeMiniPlayerControllerSnapshot(
    int StateCount,
    int RuntimeCount,
    int StateWithoutRuntimeCount,
    int RuntimeWithoutStateCount);

internal sealed class YouTubeMiniPlayerController
{
    private readonly List<YouTubeMiniPlayerState> _states = [];
    private readonly Dictionary<Guid, YouTubeMiniPlayerRuntime> _runtimes = [];

    public IReadOnlyList<YouTubeMiniPlayerState> States => _states;

    public IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> Runtimes => _runtimes;

    public Task OpenFromTabAsync(
        BrowserTab sourceTab,
        IReadOnlyCollection<BrowserTab> tabs,
        int windowId,
        double hostWidth,
        double hostHeight,
        YouTubeMiniPlayerOpenWorkflowCallbacks callbacks)
    {
        return YouTubeMiniPlayerOpenWorkflowService.OpenFromTabAsync(
            sourceTab,
            tabs,
            windowId,
            hostWidth,
            hostHeight,
            this,
            callbacks);
    }

    public Task OpenFromLinkAsync(
        BrowserTab sourceTab,
        string linkUri,
        string? linkText,
        IReadOnlyCollection<BrowserTab> tabs,
        int windowId,
        double hostWidth,
        double hostHeight,
        YouTubeMiniPlayerOpenWorkflowCallbacks callbacks)
    {
        return YouTubeMiniPlayerOpenWorkflowService.OpenFromLinkAsync(
            sourceTab,
            linkUri,
            linkText,
            tabs,
            windowId,
            hostWidth,
            hostHeight,
            this,
            callbacks);
    }

    public YouTubeMiniPlayerCommandCallbacks CreateCommandCallbacks(
        IEnumerable<BrowserTab> tabs,
        YouTubeMiniPlayerCommandWorkflowDependencies dependencies)
    {
        return YouTubeMiniPlayerCommandWorkflowService.Create(this, tabs, dependencies);
    }

    public int RestorePlaceholders(
        IEnumerable<YouTubeMiniPlayerState> storedPlayers,
        int windowId,
        Func<YouTubeMiniPlayerState, YouTubeMiniPlayerRuntime> createRuntime,
        Action<YouTubeMiniPlayerState, FrameworkElement, bool> clampToHost,
        Action<Guid> bringToFront,
        Action<YouTubeMiniPlayerRuntime, bool> applyMinimizedState)
    {
        return YouTubeMiniPlayerRestoreService.RestorePlaceholders(
            storedPlayers,
            windowId,
            this,
            createRuntime,
            clampToHost,
            bringToFront,
            applyMinimizedState);
    }

    public void Register(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime)
    {
        if (_states.Any(current => current.Id == state.Id) || _runtimes.ContainsKey(state.Id))
        {
            throw new InvalidOperationException($"ミニプレイヤーID {state.Id:N} は既に登録されています");
        }

        YouTubeMiniPlayerRuntimeRegistryService.Register(_states, _runtimes, state, runtime);
    }

    public bool TryClose(Guid miniPlayerId)
    {
        return YouTubeMiniPlayerCloseService.TryClose(_states, _runtimes, miniPlayerId);
    }

    public int DisposeAll()
    {
        return YouTubeMiniPlayerCloseService.DisposeAll(_states, _runtimes);
    }

    public YouTubeMiniPlayerControllerSnapshot CaptureSnapshot()
    {
        var stateIds = _states.Select(static state => state.Id).ToHashSet();
        return new YouTubeMiniPlayerControllerSnapshot(
            _states.Count,
            _runtimes.Count,
            _states.Count(state => !_runtimes.ContainsKey(state.Id)),
            _runtimes.Keys.Count(id => !stateIds.Contains(id)));
    }
}
