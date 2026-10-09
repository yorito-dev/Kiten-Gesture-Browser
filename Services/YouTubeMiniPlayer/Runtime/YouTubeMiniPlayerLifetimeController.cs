using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record YouTubeMiniPlayerClosedRestoreOperations(
    Func<YouTubeMiniPlayerState, int> RestorePlaceholder,
    Action<Guid> BringToFront);

internal interface IYouTubeMiniPlayerLifetimeOwner
{
    int WindowIndex { get; }
    Guid SessionWindowId { get; }
    void BringToFront(Guid id);
    void QueueSave();
    YouTubeMiniPlayerClosedRestoreOperations CreateClosedRestoreOperations();
    void LoadLocalMedia(Guid id);
    Task LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri);
    void UpdateStateCaptureTimer();
    bool HasRuntime(Guid id);
    void CancelInput(Guid id);
    void CloseRuntime(Guid id);
}

internal interface IYouTubeMiniPlayerLifetimeHost : IYouTubeMiniPlayerLifetimeOwner
{
    bool IsPrivateWindow { get; }
    bool IsClosingWindow { get; }
    IReadOnlyList<YouTubeMiniPlayerState> States { get; }
    IEnumerable<Guid> GetRuntimeIds();
    IYouTubeMiniPlayerLifetimeOwner? FindOpenOwner(Guid id);
    IYouTubeMiniPlayerLifetimeOwner ResolveRestoreOwner(YouTubeMiniPlayerState state);
    void CancelRecovery(Guid id);
    void CloseLiveChat(Guid id);
    void CloseAllLiveChats();
    void ResetDiagnostic(Guid id);
    Task StopAudioPreviewAsync();
    Task RestoreAudioFocusAsync();
    void CloseHiddenHostIfEmpty();
    void ResumeDeferredBackgroundWork();
    void QueueSessionSave();
    void RefreshClosedHistory();
    void UpdateRestoreButton();
    Task RefreshListAsync();
    void SetStatus(string message);
}

// Runtime本体と履歴Storeの所有権は維持し、終了・履歴復元の順序と再入状態を所有する。
internal sealed class YouTubeMiniPlayerLifetimeController(
    IYouTubeMiniPlayerLifetimeHost host,
    YouTubeMiniPlayerClosedHistoryStore history,
    YouTubeMiniPlayerAudioSessionCoordinator audioSession,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    public bool IsRestoringClosedPlayer { get; private set; }

    public void RememberClosed(YouTubeMiniPlayerState state, Guid? ownerSessionWindowId = null)
    {
        history.Remember(state, ownerSessionWindowId ?? host.SessionWindowId, _utcNow());
        host.QueueSessionSave();
        host.RefreshClosedHistory();
    }

    public void Close(Guid id, bool shouldRememberClosedPlayers, Action<Guid> close)
    {
        host.CancelRecovery(id);
        host.CloseLiveChat(id);
        host.ResetDiagnostic(id);
        var closedState = shouldRememberClosedPlayers && !host.IsClosingWindow
            ? host.States.FirstOrDefault(state => state.Id == id)
            : null;
        closedState = closedState is null ? null : YouTubeMiniPlayerPersistenceController.CreatePersistentState(closedState, audioSession);
        var wasAudioFocusTarget = audioSession.IsFocusTarget(id);
        if (audioSession.PreviewTargetId == id)
        {
            _ = host.StopAudioPreviewAsync();
        }

        host.CancelInput(id);
        close(id);
        audioSession.RemoveFocusPlayer(id);
        if (wasAudioFocusTarget && !host.IsClosingWindow)
        {
            _ = host.RestoreAudioFocusAsync();
        }

        if (closedState is not null && host.States.All(state => state.Id != id))
        {
            RememberClosed(closedState);
        }

        host.CloseHiddenHostIfEmpty();
        host.ResumeDeferredBackgroundWork();
    }

    public void DisposeAll(bool shouldRememberClosedPlayers, Action disposeAll)
    {
        host.CloseAllLiveChats();
        var closedStates = shouldRememberClosedPlayers && !host.IsClosingWindow
            ? host.States.Select(state => YouTubeMiniPlayerPersistenceController.CreatePersistentState(state, audioSession)).ToList()
            : [];
        var disposingIds = host.GetRuntimeIds().ToArray();
        foreach (var id in disposingIds)
        {
            host.CancelInput(id);
            host.ResetDiagnostic(id);
        }

        disposeAll();
        audioSession.ClearPreview();
        foreach (var id in disposingIds)
        {
            audioSession.RemoveFocusPlayer(id);
        }

        foreach (var state in closedStates.Where(state => host.States.All(current => current.Id != state.Id)))
        {
            RememberClosed(state);
        }

        host.CloseHiddenHostIfEmpty();
        host.ResumeDeferredBackgroundWork();
    }

    public async Task RestoreClosedAsync(Guid? miniPlayerId = null)
    {
        if (host.IsPrivateWindow || IsRestoringClosedPlayer
            || !history.TryTake(miniPlayerId, out var closedState) || closedState is null)
        {
            host.UpdateRestoreButton();
            return;
        }

        IsRestoringClosedPlayer = true;
        host.QueueSessionSave();
        host.UpdateRestoreButton();
        IYouTubeMiniPlayerLifetimeOwner? restoreOwner = null;
        try
        {
            if (host.FindOpenOwner(closedState.Id) is { } existingOwner)
            {
                existingOwner.BringToFront(closedState.Id);
                existingOwner.QueueSave();
                host.SetStatus("対象のミニプレイヤーは既に開いているため前面に表示しました");
                return;
            }

            restoreOwner = host.ResolveRestoreOwner(closedState);
            closedState.WindowId = restoreOwner.WindowIndex;
            closedState.OwnerSessionWindowId = restoreOwner.SessionWindowId;
            closedState.IsMinimized = false;
            var navigationUrl = YouTubeMiniPlayerNavigationService.CreateNavigationUrl(closedState);
            Uri? uri = null;
            if (closedState.SourceKind == MiniPlayerSourceKind.YouTube
                && !YouTubeMiniPlayerOpenRequestService.TryCreateNavigationUri(navigationUrl, out uri))
            {
                RememberClosed(closedState, restoreOwner.SessionWindowId);
                host.SetStatus("閉じたYouTubeミニプレイヤーのURLを復元できません");
                return;
            }

            var operations = restoreOwner.CreateClosedRestoreOperations();
            var restoredCount = operations.RestorePlaceholder(closedState);
            if (restoredCount == 0)
            {
                RememberClosed(closedState, restoreOwner.SessionWindowId);
                host.SetStatus("閉じたYouTubeミニプレイヤーを復元できません");
                return;
            }

            if (closedState.SourceKind == MiniPlayerSourceKind.LocalFile)
            {
                restoreOwner.LoadLocalMedia(closedState.Id);
            }
            else if (uri is not null)
            {
                await restoreOwner.LoadRuntimeAsync(closedState, uri);
            }
            restoreOwner.UpdateStateCaptureTimer();
            restoreOwner.QueueSave();
            operations.BringToFront(closedState.Id);
            host.SetStatus("閉じたYouTubeミニプレイヤーを復元しました");
        }
        catch (Exception ex)
        {
            if (restoreOwner is not null && restoreOwner.HasRuntime(closedState.Id))
            {
                restoreOwner.CancelInput(closedState.Id);
                restoreOwner.CloseRuntime(closedState.Id);
                restoreOwner.UpdateStateCaptureTimer();
                restoreOwner.QueueSave();
            }

            RememberClosed(closedState, restoreOwner?.SessionWindowId ?? closedState.OwnerSessionWindowId);
            host.SetStatus("閉じたYouTubeミニプレイヤーを復元できませんでした: " + ex.Message);
        }
        finally
        {
            IsRestoringClosedPlayer = false;
            host.UpdateRestoreButton();
            host.RefreshClosedHistory();
            await host.RefreshListAsync();
        }
    }
}
