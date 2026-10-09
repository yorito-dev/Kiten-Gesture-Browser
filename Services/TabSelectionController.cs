using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ITabSelectionHost
{
    IList<BrowserTab> Tabs { get; }
    BrowserTab? ActiveTab { get; }
    bool IsClosing { get; }
    bool IsRightWheelCycling { get; }
    void NoteExplorationActivity();
    void RestoreApplicationFullscreenBeforeTabSelectionChange();
    void SelectTab(BrowserTab tab);
    void UpdateRightWheelTabSelectionInformation(BrowserTab tab);
    void QueueSelectedTabActivation();
    void QueueActivationCheck(Action action);
    bool HasLoadedRuntime(BrowserTab tab);
    void ShowMemoryRestorePlaceholder(BrowserTab tab);
    void QueueSettledActivation(Func<CancellationToken, Task> operation);
    Task ActivateSelectedTabAsync(CancellationToken cancellationToken);
    void QueueBackgroundTabLoading();
    void QueueBackgroundThumbnailWarmup();
}

// 入力選択と右ホイールの遅延確定を調停する。native入力とTask所有権はWindowが接続する。
internal sealed class TabSelectionController(
    ITabSelectionHost host,
    TabOperationService tabOperationService,
    TabActivationWorkCoordinator activationWork,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly ITabSelectionHost _host = host;
    private readonly TabOperationService _tabOperationService = tabOperationService;
    private readonly TabActivationWorkCoordinator _activationWork = activationWork;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    internal void MoveTabSelection(int offset)
    {
        if (_host.Tabs.Count == 0 || _host.ActiveTab is null) return;
        _host.NoteExplorationActivity();
        var index = _host.Tabs.IndexOf(_host.ActiveTab);
        var nextIndex = _tabOperationService.GetCycledSelectionIndex(index, _host.Tabs.Count, offset);
        if (nextIndex < 0) return;

        _host.RestoreApplicationFullscreenBeforeTabSelectionChange();
        var nextTab = _host.Tabs[nextIndex];
        _host.SelectTab(nextTab);
        if (_host.IsRightWheelCycling)
        {
            _host.UpdateRightWheelTabSelectionInformation(nextTab);
            QueueRightWheelTabActivation(nextTab);
            return;
        }

        _activationWork.SupersedeRightWheelActivation();
        _host.QueueSelectedTabActivation();
    }

    private void QueueRightWheelTabActivation(BrowserTab tab)
    {
        var requestGeneration = _activationWork.BeginRightWheelActivation();
        _host.QueueActivationCheck(() =>
        {
            if (_host.IsClosing
                || !_activationWork.IsCurrentRightWheelActivation(requestGeneration)
                || !ReferenceEquals(_host.ActiveTab, tab))
            {
                _activationWork.TryCompleteRightWheelActivation(requestGeneration);
                return;
            }

            if (tab.IsNativeContentTab || _host.HasLoadedRuntime(tab))
            {
                _activationWork.TryCompleteRightWheelActivation(requestGeneration);
                _host.QueueSelectedTabActivation();
                return;
            }

            if (tab.IsMemoryReleased) _host.ShowMemoryRestorePlaceholder(tab);
            _host.QueueSettledActivation(token => ActivateRightWheelTabAfterSettleAsync(tab, requestGeneration, token));
        });
    }

    private async Task ActivateRightWheelTabAfterSettleAsync(
        BrowserTab tab, int requestGeneration, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(TimeSpan.FromMilliseconds(120), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || _host.IsClosing
            || !_activationWork.IsCurrentRightWheelActivation(requestGeneration)
            || !ReferenceEquals(_host.ActiveTab, tab)) return;

        _activationWork.TryCompleteRightWheelActivation(requestGeneration);
        await _host.ActivateSelectedTabAsync(cancellationToken);
    }

    internal void CompleteCycling(bool wasActive)
    {
        if (!wasActive) return;
        if (_activationWork.IsRightWheelActivationPending
            && !_host.IsClosing
            && _host.ActiveTab is not null)
        {
            _activationWork.SupersedeRightWheelActivation();
            _host.QueueSelectedTabActivation();
        }

        _host.QueueBackgroundTabLoading();
        _host.QueueBackgroundThumbnailWarmup();
    }
}
