using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Models.Workspaces;
using TabBrowser.Services;
using TabBrowser.Services.Workspaces;
using TabBrowser.Views;

namespace TabBrowser;

public partial class MainWindow : IYouTubeMiniPlayerListHost, IYouTubeMiniPlayerListPopupHost, IYouTubeMiniPlayerListActionsHost, IYouTubeMiniPlayerAudioHost, IYouTubeLiveChatHost, IYouTubeMiniPlayerActivityHost, IYouTubeMiniPlayerCommandHost, IYouTubeMiniPlayerRecoveryHost, IYouTubeMiniPlayerPersistenceHost, IYouTubeMiniPlayerLifetimeHost, ILocalVideoMiniPlayerHost, IYouTubeMiniPlayerArrangementHost, IYouTubeMiniPlayerArrangementOwner
{
    private const int YouTubeMiniPlayerToolbarHideDelayMilliseconds = 700;
    private const double YouTubeMiniPlayerMinimizedWidth = 320;

    private static readonly YouTubeMiniPlayerClosedHistoryStore s_youTubeMiniPlayerClosedHistoryStore = new();
    private static readonly Brush YouTubeMiniPlayerListSelectedWindowBrush =
        new SolidColorBrush(Color.FromRgb(34, 211, 238));
    private static readonly Brush YouTubeMiniPlayerListHoveredWindowBrush =
        new SolidColorBrush(Color.FromRgb(250, 204, 21));
    private static readonly YouTubeMiniPlayerAudioSessionCoordinator s_youTubeMiniPlayerAudioSessionCoordinator = new();

    private readonly YouTubeMiniPlayerArrangementController _youTubeMiniPlayerArrangementController;
    private readonly YouTubeMiniPlayerController _youTubeMiniPlayerController = new();
    private IReadOnlyList<YouTubeMiniPlayerState> _youTubeMiniPlayers =>
        _youTubeMiniPlayerController.States;
    private IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> _youTubeMiniPlayerRuntimes =>
        _youTubeMiniPlayerController.Runtimes;
    private readonly YouTubeMiniPlayerStore _youTubeMiniPlayerStore = new();
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerSaveTimer = new(
        DispatcherTimerOwner.FloatingMediaHost,
        DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerStateCaptureTimer = new(
        DispatcherTimerOwner.FloatingMediaHost,
        DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerListRefreshTimer = new(
        DispatcherTimerOwner.FloatingMediaHost,
        DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerListVolumeTimer = new(
        DispatcherTimerOwner.FloatingMediaHost,
        DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerHoverAudioPreviewTimer = new(
        DispatcherTimerOwner.FloatingMediaHost,
        DispatcherTimerTerminationPhase.StopFloatingMediaTimers);
    private readonly YouTubeMiniPlayerListViewController _youTubeMiniPlayerListViewController;
    private readonly YouTubeMiniPlayerListPopupController _youTubeMiniPlayerListPopupController;
    private readonly YouTubeMiniPlayerListActionsController _youTubeMiniPlayerListActionsController;
    private readonly YouTubeMiniPlayerAudioController _youTubeMiniPlayerAudioController;
    private readonly YouTubeLiveChatWindowController _youTubeLiveChatController;
    private readonly YouTubeMiniPlayerActivityController _youTubeMiniPlayerActivityController;
    private readonly YouTubeMiniPlayerCommandController _youTubeMiniPlayerCommandController;
    private readonly YouTubeMiniPlayerRecoveryController _youTubeMiniPlayerRecoveryController;
    private readonly YouTubeMiniPlayerPersistenceController _youTubeMiniPlayerPersistenceController;
    private IReadOnlyList<YouTubeMiniPlayerListItem> _youTubeMiniPlayerListItems =>
        _youTubeMiniPlayerListViewController.Items;
    private readonly YouTubeMiniPlayerStateSyncCoordinator _youTubeMiniPlayerStateSyncCoordinator = new();
    private YouTubeMiniPlayerListInteractionCoordinator _youTubeMiniPlayerListInteractionCoordinator =>
        _youTubeMiniPlayerListViewController.Interaction;
    private readonly YouTubeMiniPlayerArrangementSessionCoordinator _youTubeMiniPlayerArrangementSessionCoordinator = new();
    private readonly FloatingMediaHostLifetime _floatingMediaHostLifetime = new();
    private YouTubeMiniPlayerArrangementPreviewWindow? _youTubeMiniPlayerArrangementPreviewWindow;
    private Point? _lastYouTubeMiniPlayerHostScreenOrigin;
    private bool _floatingMediaHostLayoutUpdateQueued;
    private int _youTubeMiniPlayerZIndex = 40;
    private readonly YouTubeMiniPlayerLifetimeController _youTubeMiniPlayerLifetimeController;
    private readonly LocalVideoMiniPlayerController _localVideoMiniPlayerController;

    bool IYouTubeMiniPlayerListActionsHost.CanControlVolume(YouTubeMiniPlayerListItem item)
        => item.Owner._youTubeMiniPlayerRuntimes.TryGetValue(item.State.Id, out var runtime)
            && YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null;

    void IYouTubeMiniPlayerActivityHost.BringLiveChatToFront(Guid id) => BringYouTubeLiveChatWindowToFront(id);
    Task IYouTubeMiniPlayerActivityHost.RefreshLiveChatAsync(YouTubeMiniPlayerState state, WebView2 webView)
        => RefreshYouTubeLiveChatAvailabilityAsync(state, webView);
    void IYouTubeMiniPlayerActivityHost.QueueSave() => QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerActivityHost.MarkEventStateSavePending() => _youTubeMiniPlayerStateSyncCoordinator.MarkEventStateSavePending();
    void IYouTubeMiniPlayerActivityHost.ResumeDeferredBackgroundWork() => ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
    void IYouTubeMiniPlayerActivityHost.ReportPlaybackDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message)
    {
        if (message.VideoNaturalWidth is { } videoWidth
            && message.VideoNaturalHeight is { } videoHeight)
        {
            ApplyDetectedYouTubeMiniPlayerVideoDimensions(state, videoWidth, videoHeight);
        }

        HandleYouTubePlaybackDiagnostic("mini-player", state.Id, state.Url, message);
    }
    void IYouTubeMiniPlayerActivityHost.HandleRecoveryDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message)
        => _youTubeMiniPlayerRecoveryController.HandlePlaybackDiagnostic(state, message);

    int ILocalVideoMiniPlayerHost.WindowIndex => _workspaceWindowIndex;
    double ILocalVideoMiniPlayerHost.HostWidth => WebHost.ActualWidth;
    double ILocalVideoMiniPlayerHost.HostHeight => WebHost.ActualHeight;
    IReadOnlyList<YouTubeMiniPlayerState> ILocalVideoMiniPlayerHost.States => _youTubeMiniPlayers;
    YouTubeMiniPlayerRuntime ILocalVideoMiniPlayerHost.CreateRuntime(YouTubeMiniPlayerState state)
        => CreateYouTubeMiniPlayerRuntime(state, loadImmediately: false);
    void ILocalVideoMiniPlayerHost.Register(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime)
        => _youTubeMiniPlayerController.Register(state, runtime);
    void ILocalVideoMiniPlayerHost.SetPosition(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime)
        => CreateYouTubeMiniPlayerPositionCallbacks().SetPosition(state, runtime.Root);
    void ILocalVideoMiniPlayerHost.BringToFront(Guid id) => CreateYouTubeMiniPlayerCommandCallbacks().BringToFront(id, true);
    void ILocalVideoMiniPlayerHost.LoadMedia(YouTubeMiniPlayerRuntime runtime) => runtime.LocalVideoView?.LoadMedia();
    YouTubeMiniPlayerRuntime? ILocalVideoMiniPlayerHost.FindRuntime(Guid id)
        => _youTubeMiniPlayerRuntimes.TryGetValue(id, out var runtime) ? runtime : null;
    void ILocalVideoMiniPlayerHost.ClampToHost(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime)
        => CreateYouTubeMiniPlayerPositionCallbacks().ClampToHost(state, runtime.Root, true);
    void ILocalVideoMiniPlayerHost.UpdateTitle(YouTubeMiniPlayerState state) => UpdateLocalVideoMiniPlayerTitle(state);
    string? ILocalVideoMiniPlayerHost.SelectFile(string? initialPath) => SelectLocalVideoFile(initialPath);
    void ILocalVideoMiniPlayerHost.StartExplorer(string filePath)
    {
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(filePath);
        Process.Start(startInfo);
    }
    void ILocalVideoMiniPlayerHost.UpdateCaptureTimer() => UpdateYouTubeMiniPlayerStateCaptureTimer();
    void ILocalVideoMiniPlayerHost.QueueSave() => QueueYouTubeMiniPlayerSave();
    Task ILocalVideoMiniPlayerHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    void ILocalVideoMiniPlayerHost.RecordRecentFile(string filePath) => RecordRecentItem(RecentItemFactory.FromFile(filePath));
    void ILocalVideoMiniPlayerHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    int IYouTubeMiniPlayerLifetimeOwner.WindowIndex => _workspaceWindowIndex;
    Guid IYouTubeMiniPlayerLifetimeOwner.SessionWindowId => _sessionWindowId;
    void IYouTubeMiniPlayerLifetimeOwner.BringToFront(Guid id) => CreateYouTubeMiniPlayerCommandCallbacks().BringToFront(id, true);
    void IYouTubeMiniPlayerLifetimeOwner.QueueSave() => QueueYouTubeMiniPlayerSave();
    YouTubeMiniPlayerClosedRestoreOperations IYouTubeMiniPlayerLifetimeOwner.CreateClosedRestoreOperations()
    {
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        return new(
            state => _youTubeMiniPlayerController.RestorePlaceholders(
                [state], _workspaceWindowIndex,
                player => CreateYouTubeMiniPlayerRuntime(player, loadImmediately: false),
                positionCallbacks.ClampToHost,
                id => commandCallbacks.BringToFront(id, true), commandCallbacks.ApplyMinimizedState),
            id => commandCallbacks.BringToFront(id, true));
    }
    void IYouTubeMiniPlayerLifetimeOwner.LoadLocalMedia(Guid id) => _youTubeMiniPlayerRuntimes[id].LocalVideoView?.LoadMedia();
    Task IYouTubeMiniPlayerLifetimeOwner.LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri) => LoadYouTubeMiniPlayerRuntimeAsync(state, uri);
    void IYouTubeMiniPlayerLifetimeOwner.UpdateStateCaptureTimer() => UpdateYouTubeMiniPlayerStateCaptureTimer();
    bool IYouTubeMiniPlayerLifetimeOwner.HasRuntime(Guid id) => _youTubeMiniPlayerRuntimes.ContainsKey(id);
    void IYouTubeMiniPlayerLifetimeOwner.CancelInput(Guid id) => CancelYouTubeMiniPlayerInputScope(id);
    void IYouTubeMiniPlayerLifetimeOwner.CloseRuntime(Guid id) => _youTubeMiniPlayerController.TryClose(id);
    bool IYouTubeMiniPlayerLifetimeHost.IsPrivateWindow => _isPrivateWindow;
    bool IYouTubeMiniPlayerLifetimeHost.IsClosingWindow => _isClosingWindow;
    IReadOnlyList<YouTubeMiniPlayerState> IYouTubeMiniPlayerLifetimeHost.States => _youTubeMiniPlayers;
    IEnumerable<Guid> IYouTubeMiniPlayerLifetimeHost.GetRuntimeIds() => _youTubeMiniPlayerRuntimes.Keys;
    IYouTubeMiniPlayerLifetimeOwner? IYouTubeMiniPlayerLifetimeHost.FindOpenOwner(Guid id)
        => TryFindOpenYouTubeMiniPlayer(id, out var owner, out _) ? owner : null;
    IYouTubeMiniPlayerLifetimeOwner IYouTubeMiniPlayerLifetimeHost.ResolveRestoreOwner(YouTubeMiniPlayerState state)
        => state.OwnerSessionWindowId is not null
            ? GetClosedItemRestoreWindow(state.OwnerSessionWindowId)
            : GetYouTubeMiniPlayerOwnerWindows().FirstOrDefault(window => window._workspaceWindowIndex == state.WindowId) ?? this;
    void IYouTubeMiniPlayerLifetimeHost.CancelRecovery(Guid id) => _youTubeMiniPlayerRecoveryController.Cancel(id);
    void IYouTubeMiniPlayerLifetimeHost.CloseLiveChat(Guid id) => CloseYouTubeLiveChatWindow(id);
    void IYouTubeMiniPlayerLifetimeHost.CloseAllLiveChats() => CloseAllYouTubeLiveChatWindows();
    void IYouTubeMiniPlayerLifetimeHost.ResetDiagnostic(Guid id) => ResetYouTubePlaybackDiagnostic("mini-player", id);
    Task IYouTubeMiniPlayerLifetimeHost.StopAudioPreviewAsync() => StopYouTubeMiniPlayerHoverAudioPreviewAsync();
    Task IYouTubeMiniPlayerLifetimeHost.RestoreAudioFocusAsync() => RestoreYouTubeMiniPlayerAudioFocusAsync();
    void IYouTubeMiniPlayerLifetimeHost.CloseHiddenHostIfEmpty() => CloseHiddenYouTubeMiniPlayerHostIfEmpty();
    void IYouTubeMiniPlayerLifetimeHost.ResumeDeferredBackgroundWork() => ResumeDeferredAutomaticBackgroundWorkIfPlaybackStopped();
    void IYouTubeMiniPlayerLifetimeHost.QueueSessionSave() => QueueSessionSave();
    void IYouTubeMiniPlayerLifetimeHost.RefreshClosedHistory() => RefreshClosedTabHistoryViews();
    void IYouTubeMiniPlayerLifetimeHost.UpdateRestoreButton() => UpdateYouTubeMiniPlayerRestoreClosedButtonState();
    Task IYouTubeMiniPlayerLifetimeHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    void IYouTubeMiniPlayerLifetimeHost.SetStatus(string message) => SetStatus(message, autoClear: true);

    bool IYouTubeMiniPlayerPersistenceOwner.IsPrivateWindow => _isPrivateWindow;
    Guid IYouTubeMiniPlayerPersistenceOwner.SessionWindowId => _sessionWindowId;
    int IYouTubeMiniPlayerPersistenceOwner.WindowIndex => _workspaceWindowIndex;
    IReadOnlyList<YouTubeMiniPlayerState> IYouTubeMiniPlayerPersistenceOwner.States => _youTubeMiniPlayers;
    Task<bool> IYouTubeMiniPlayerPersistenceOwner.CaptureLoadedAsync() => CaptureYouTubeMiniPlayerStatesForLoadedAsync();
    int IYouTubeMiniPlayerPersistenceOwner.RestoreWindowMemoryPlaceholders(
        Guid savedSessionWindowId, IEnumerable<YouTubeMiniPlayerState> players, HashSet<Guid> unavailableIds)
        => _youTubeMiniPlayerPersistenceController.RestoreWindowMemoryYouTubeMiniPlayerPlaceholders(
            savedSessionWindowId, players, unavailableIds);
    bool IYouTubeMiniPlayerPersistenceHost.IsClosingWindow => _isClosingWindow;
    bool IYouTubeMiniPlayerPersistenceHost.IsInitialized => _isInitialized;
    bool IYouTubeMiniPlayerPersistenceHost.RestorePlaceholdersEnabled => _restoreFloatingMediaPlaceholders;
    IReadOnlyList<IYouTubeMiniPlayerPersistenceOwner> IYouTubeMiniPlayerPersistenceHost.GetOwners() => GetYouTubeMiniPlayerOwnerWindows();
    Func<IEnumerable<YouTubeMiniPlayerState>, int> IYouTubeMiniPlayerPersistenceHost.CreatePlaceholderRestorer()
    {
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        return players => _youTubeMiniPlayerController.RestorePlaceholders(
            players, _workspaceWindowIndex, player => CreateYouTubeMiniPlayerRuntime(player, loadImmediately: false),
            positionCallbacks.ClampToHost, id => commandCallbacks.BringToFront(id, true), commandCallbacks.ApplyMinimizedState);
    }
    IEnumerable<YouTubeMiniPlayerState> IYouTubeMiniPlayerPersistenceHost.LoadStoredPlayers() => _youTubeMiniPlayerStore.Load();
    void IYouTubeMiniPlayerPersistenceHost.SaveSnapshot(IReadOnlyList<YouTubeMiniPlayerState> players)
        => YouTubeMiniPlayerTimerService.SaveNow(
            _youTubeMiniPlayerSaveTimer, _youTubeMiniPlayerStore, players, _isClosingWindow,
            message => SetStatus(message, autoClear: true));
    bool IYouTubeMiniPlayerPersistenceHost.RemoveOpenPlayersFromClosedHistory() => RemoveOpenYouTubeMiniPlayersFromClosedHistory();
    void IYouTubeMiniPlayerPersistenceHost.QueueSessionSave() => QueueSessionSave();
    void IYouTubeMiniPlayerPersistenceHost.RefreshClosedHistory() => RefreshClosedTabHistoryViews();
    void IYouTubeMiniPlayerPersistenceHost.UpdateStateCaptureTimer() => UpdateYouTubeMiniPlayerStateCaptureTimer();
    bool IYouTubeMiniPlayerPersistenceHost.TryCloseRuntime(Guid id) => _youTubeMiniPlayerController.TryClose(id);
    void IYouTubeMiniPlayerPersistenceHost.UpdateAudioIndicators() => UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows();
    Task<bool> IYouTubeMiniPlayerPersistenceHost.CaptureLoadedStatesAsync()
        => YouTubeMiniPlayerStateCaptureService.CaptureLoadedAsync(_youTubeMiniPlayers, _youTubeMiniPlayerRuntimes);
    Task<bool> IYouTubeMiniPlayerPersistenceHost.CaptureFallbackStatesAsync(DateTimeOffset observedAt, TimeSpan eventStateFreshness)
        => YouTubeMiniPlayerStateCaptureService.CaptureFallbackAsync(
            _youTubeMiniPlayers, _youTubeMiniPlayerRuntimes, observedAt, eventStateFreshness);
    Task IYouTubeMiniPlayerPersistenceHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    void IYouTubeMiniPlayerPersistenceHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IYouTubeMiniPlayerPersistenceHost.AppendDiagnostic(string category, Exception exception, string detail)
        => AppendSessionDiagnostic(category, exception, detail);

    bool IYouTubeMiniPlayerRecoveryHost.IsClosingWindow => _isClosingWindow;
    CancellationToken IYouTubeMiniPlayerRecoveryHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;
    IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> IYouTubeMiniPlayerRecoveryHost.Runtimes => _youTubeMiniPlayerRuntimes;
    bool IYouTubeMiniPlayerRecoveryHost.HasOperationalCore(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null;
    bool IYouTubeMiniPlayerRecoveryHost.TryGetReloadTarget(
        YouTubeMiniPlayerRuntime runtime, out WebView2 webView, out Action reload)
    {
        if (runtime.WebView is { CoreWebView2: { } core } currentWebView)
        {
            webView = currentWebView;
            reload = core.Reload;
            return true;
        }

        webView = null!;
        reload = null!;
        return false;
    }
    Task<string?> IYouTubeMiniPlayerRecoveryHost.ExecuteHealthProbeAsync(YouTubeMiniPlayerState state, string script)
        => YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            state, _youTubeMiniPlayerRuntimes, script, showStatus: false, _ => { });
    Task<bool> IYouTubeMiniPlayerRecoveryHost.CaptureStateAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerStateCaptureService.CaptureAsync(state, _youTubeMiniPlayerRuntimes);
    void IYouTubeMiniPlayerRecoveryHost.ResetWebView(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.ResetWebView(runtime);
    Task IYouTubeMiniPlayerRecoveryHost.LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri)
        => LoadYouTubeMiniPlayerRuntimeAsync(state, uri);
    Task<bool> IYouTubeMiniPlayerRecoveryHost.ActivateRecoverySourceTabAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerSourceTabService.ActivateAsync(
            state, _tabs, _ => Task.CompletedTask, OpenYouTubeMiniPlayerSourceTab, SelectTab,
            ActivateFloatingMediaSourceTabAsync, QueueYouTubeMiniPlayerSave,
            message => SetStatus(message, autoClear: true), PrepareYouTubeMiniPlayerRecoverySourceTab,
            captureBeforeReopen: false);
    void IYouTubeMiniPlayerRecoveryHost.QueueInactiveSuspension(YouTubeMiniPlayerState state, bool stopStalledLoad)
        => QueueYouTubeMiniPlayerInactiveSuspension(state, stopStalledLoad);
    void IYouTubeMiniPlayerRecoveryHost.CloseWithoutHistory(Guid id)
        => CreateYouTubeMiniPlayerCommandCallbacks(rememberClosedPlayers: false).Close(id);
    void IYouTubeMiniPlayerRecoveryHost.QueueLayoutVerification(YouTubeMiniPlayerState state)
        => QueueYouTubeMiniPlayerLayoutVerification(state);
    void IYouTubeMiniPlayerRecoveryHost.QueueSave() => QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerRecoveryHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IYouTubeMiniPlayerRecoveryHost.QueueDiagnosticLogLine(IReadOnlyList<string> fields) => QueuePerformanceDiagnosticLogLine(fields);
    void IYouTubeMiniPlayerRecoveryHost.AppendDiagnostic(string category, Exception exception, string detail)
        => AppendSessionDiagnostic(category, exception, detail);
    void IYouTubeMiniPlayerRecoveryHost.RecordProcessGroupResult(string containerProfileId, uint browserProcessId, string result)
        => s_webViewProcessRecoveryCoordinator.RecordRecoveryResult(
            containerProfileId, browserProcessId, CoreWebView2ProcessFailedKind.BrowserProcessExited,
            WebViewProcessFailureAction.RecreateProcessGroup, result);

    IReadOnlyList<YouTubeMiniPlayerState> IYouTubeMiniPlayerCommandOwner.States => _youTubeMiniPlayers;
    bool IYouTubeMiniPlayerCommandOwner.TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime)
        => _youTubeMiniPlayerRuntimes.TryGetValue(id, out runtime!);
    bool IYouTubeMiniPlayerCommandOwner.IsRuntimeLoaded(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
            || runtime.LocalVideoView?.Controller?.IsLoaded == true;
    bool IYouTubeMiniPlayerCommandOwner.LoadLocalMedia(YouTubeMiniPlayerRuntime runtime) => runtime.LocalVideoView?.LoadMedia() == true;
    Task IYouTubeMiniPlayerCommandOwner.LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri) => LoadYouTubeMiniPlayerRuntimeAsync(state, uri);
    Task<bool> IYouTubeMiniPlayerCommandOwner.TogglePlayPauseAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerPlaybackCommandService.TogglePlayPauseAsync(state, _youTubeMiniPlayerRuntimes, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.ToggleMutedAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerPlaybackCommandService.ToggleMutedAsync(state, _youTubeMiniPlayerRuntimes, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.SeekRelativeAsync(YouTubeMiniPlayerState state, double deltaSeconds)
        => YouTubeMiniPlayerPlaybackCommandService.SeekRelativeAsync(state, _youTubeMiniPlayerRuntimes, deltaSeconds, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.SeekToLiveEdgeAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerPlaybackCommandService.SeekToLiveEdgeAsync(state, _youTubeMiniPlayerRuntimes, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.ToggleLoopAsync(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerPlaybackCommandService.ToggleLoopAsync(state, _youTubeMiniPlayerRuntimes, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.SetPlaybackRateAsync(YouTubeMiniPlayerState state, double speed)
        => YouTubeMiniPlayerPlaybackCommandService.SetPlaybackRateAsync(state, _youTubeMiniPlayerRuntimes, speed, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.ExecuteAbRepeatActionAsync(YouTubeMiniPlayerState state, YouTubeMiniPlayerAbRepeatAction action)
        => YouTubeMiniPlayerPlaybackCommandService.ExecuteAbRepeatActionAsync(state, _youTubeMiniPlayerRuntimes, action, message => SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerCommandOwner.SetPausedAsync(YouTubeMiniPlayerState state, bool isPaused)
        => YouTubeMiniPlayerPlaybackCommandService.SetPausedAsync(state, _youTubeMiniPlayerRuntimes, isPaused, message => SetStatus(message, autoClear: true));
    Task IYouTubeMiniPlayerCommandOwner.SetMutedAsync(YouTubeMiniPlayerState state, bool isMuted)
        => YouTubeMiniPlayerPlaybackCommandService.SetMutedAsync(state, _youTubeMiniPlayerRuntimes, isMuted);
    Task IYouTubeMiniPlayerCommandOwner.ExecuteAudioFocusAsync(
        YouTubeMiniPlayerAudioFocusMode mode, IReadOnlyCollection<Guid> ids, string message)
        => ExecuteYouTubeMiniPlayerAudioFocusAsync(mode, ids, message);
    Task<bool> IYouTubeMiniPlayerCommandOwner.RepairAsync(YouTubeMiniPlayerState state)
        => _youTubeMiniPlayerRecoveryController.RunYouTubeMiniPlayerAutoRecoveryAsync(state, "manual-repair", TimeSpan.Zero,
            requireProgressConfirmation: false, skipCurrentWebViewReload: false, allowSourceTabFallback: true);
    void IYouTubeMiniPlayerCommandOwner.ActivateSourceTab(YouTubeMiniPlayerState state)
        => CreateYouTubeMiniPlayerCommandCallbacks().ActivateSourceTab(state);
    void IYouTubeMiniPlayerCommandOwner.ToggleMinimized(YouTubeMiniPlayerState state)
        => CreateYouTubeMiniPlayerCommandCallbacks().ToggleMinimized(state);
    Action<YouTubeMiniPlayerState, bool> IYouTubeMiniPlayerCommandOwner.CreateSetMinimizedAction()
        => CreateYouTubeMiniPlayerCommandCallbacks().SetMinimized;
    void IYouTubeMiniPlayerCommandOwner.SetTopmost(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, bool isTopmost)
    {
        var overlayCallbacks = CreateYouTubeMiniPlayerOverlayPlacementCallbacks();
        YouTubeMiniPlayerRuntimeWiringService.SetTopmost(
            state, runtime, isTopmost, overlayCallbacks.UpdateTitleBandPlacement, overlayCallbacks.UpdateToolbarPlacement,
            QueueYouTubeMiniPlayerSave, message => SetStatus(message, autoClear: true));
    }
    void IYouTubeMiniPlayerCommandOwner.Close(Guid id) => CreateYouTubeMiniPlayerCommandCallbacks().Close(id);
    void IYouTubeMiniPlayerCommandOwner.DisposeAll() => CreateYouTubeMiniPlayerCommandCallbacks().DisposeAll();
    void IYouTubeMiniPlayerCommandOwner.QueueSave() => QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerCommandOwner.SetStatus(string message) => SetStatus(message, autoClear: true);
    IReadOnlyList<IYouTubeMiniPlayerCommandOwner> IYouTubeMiniPlayerCommandHost.GetOwners() => GetYouTubeMiniPlayerOwnerWindows();
    IEnumerable<GestureMappingSettings> IYouTubeMiniPlayerCommandHost.GestureMappings => _settings.GestureMappings;
    Task IYouTubeMiniPlayerCommandHost.StopHoverPreviewAsync() => StopYouTubeMiniPlayerHoverAudioPreviewAsync();
    Task IYouTubeMiniPlayerCommandHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    Task IYouTubeMiniPlayerCommandHost.ExecuteRegisteredCommandAsync(
        string actionId, CommandInvocationContext context, object? parameter, bool showFailure)
        => ExecuteRegisteredCommandAsync(actionId, context, parameter, showFailure);

    bool IYouTubeMiniPlayerActivityHost.IsQualityEnabled => _settings.EnableYouTubeMiniPlayerSizeBasedQuality;
    int IYouTubeMiniPlayerActivityHost.LowQualityWidthThreshold => _settings.YouTubeMiniPlayerQualityLowWidthThreshold;
    int IYouTubeMiniPlayerActivityHost.HighQualityWidthThreshold => _settings.YouTubeMiniPlayerQualityHighWidthThreshold;
    bool IYouTubeMiniPlayerActivityHost.IsCpuSaverEnabled => _settings.EnableFocusedViewingCpuSaver;
    TimeSpan IYouTubeMiniPlayerActivityHost.CpuSaverDelay => GetFocusedViewingCpuSaverDelay();
    bool IYouTubeMiniPlayerActivityHost.IsClosingWindow => _isClosingWindow;
    CancellationToken IYouTubeMiniPlayerActivityHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken;
    int IYouTubeMiniPlayerActivityHost.WindowIndex => _workspaceWindowIndex;
    bool IYouTubeMiniPlayerActivityHost.TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime)
        => _youTubeMiniPlayerRuntimes.TryGetValue(id, out runtime!);
    bool IYouTubeMiniPlayerActivityHost.HasReadyCore(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null;
    Func<string, Task<string>>? IYouTubeMiniPlayerActivityHost.TryGetScriptExecutor(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is { } core
            ? core.ExecuteScriptAsync
            : null;
    double IYouTubeMiniPlayerActivityHost.GetWebHostWidth(YouTubeMiniPlayerRuntime runtime) => runtime.WebHost.ActualWidth;
    double IYouTubeMiniPlayerActivityHost.GetTaskbarWindowWidth(YouTubeMiniPlayerRuntime runtime) => runtime.TaskbarWindow.ActualWidth;
    bool IYouTubeMiniPlayerActivityHost.IsChatVisible(Guid id)
        => _youTubeLiveChatController.TryGetWindow(id, out var window) && window.IsVisible;
    YouTubePlaybackActivitySnapshot? IYouTubeMiniPlayerActivityHost.GetPlaybackActivity(Guid id)
    {
        TryGetYouTubePlaybackActivity("mini-player", id, out var activity);
        return activity;
    }
    bool IYouTubeMiniPlayerActivityHost.StopLoadForInactivity(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.StopLoadForInactivity(runtime);
    Task<bool> IYouTubeMiniPlayerActivityHost.TrySuspendForInactivityAsync(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.TrySuspendForInactivityAsync(runtime);
    YouTubeMiniPlayerInactivityResumeResult IYouTubeMiniPlayerActivityHost.ResumeFromInactivity(YouTubeMiniPlayerRuntime runtime)
        => YouTubeMiniPlayerRuntimeLifecycleService.ResumeFromInactivity(runtime);
    void IYouTubeMiniPlayerActivityHost.QueueLayoutVerification(YouTubeMiniPlayerState state)
        => QueueYouTubeMiniPlayerLayoutVerification(state);
    void IYouTubeMiniPlayerActivityHost.CancelPendingRecovery(Guid id) => _youTubeMiniPlayerRecoveryController.CancelPending(id);
    void IYouTubeMiniPlayerActivityHost.QueueDiagnosticLogLine(IReadOnlyList<string> fields) => QueuePerformanceDiagnosticLogLine(fields);

    bool IYouTubeLiveChatHost.IsClosingWindow => _isClosingWindow;
    bool IYouTubeLiveChatHost.ContainsState(YouTubeMiniPlayerState state) => _youTubeMiniPlayers.Contains(state);
    bool IYouTubeLiveChatHost.TryGetReadySource(Guid id, out YouTubeLiveChatSource source)
    {
        if (_youTubeMiniPlayerRuntimes.TryGetValue(id, out var runtime)
            && !runtime.IsDisposed
            && YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is { } core)
        {
            source = new YouTubeLiveChatSource(runtime, core.Source, core.ExecuteScriptAsync);
            return true;
        }
        source = default;
        return false;
    }
    bool IYouTubeLiveChatHost.TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime)
        => _youTubeMiniPlayerRuntimes.TryGetValue(id, out runtime!);
    bool IYouTubeLiveChatHost.IsCurrentRuntime(Guid id, YouTubeMiniPlayerRuntime runtime)
        => IsCurrentYouTubeMiniPlayerRuntime(id, runtime);
    bool IYouTubeLiveChatHost.IsCurrentVideo(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, string videoId)
        => IsCurrentYouTubeMiniPlayerVideo(state, runtime, videoId);
    void IYouTubeLiveChatHost.QueueAvailabilityRefresh(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.WebView is { } currentWebView)
        {
            _ = RefreshYouTubeLiveChatAvailabilityAsync(state, currentWebView);
        }
    }
    Task<YouTubeLiveChatAvailabilityResult?> IYouTubeLiveChatHost.RefreshAvailabilityAsync(
        YouTubeMiniPlayerState state, WebView2 webView)
        => YouTubeMiniPlayerRuntimeContentFactory.UpdateLiveChatButtonAvailabilityAsync(state, webView, _youTubeMiniPlayerRuntimes);
    Task IYouTubeLiveChatHost.CaptureStateAsync(YouTubeMiniPlayerState state) => CaptureYouTubeMiniPlayerStateAsync(state);
    void IYouTubeLiveChatHost.UpdateChatButtonAvailability(YouTubeMiniPlayerRuntime runtime, bool? available)
        => YouTubeMiniPlayerChromeFactory.UpdateChatButtonAvailability(runtime.TitleBandChatButton, available);
    void IYouTubeLiveChatHost.UpdateLiveViewerCount(YouTubeMiniPlayerRuntime runtime, bool isLive, string? text)
        => YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(runtime, isLive, text);
    Task<CoreWebView2Environment> IYouTubeLiveChatHost.EnsureEnvironmentAsync(string? containerProfileId)
        => EnsureWebViewEnvironmentAsync(containerProfileId);
    IYouTubeLiveChatWindowSurface IYouTubeLiveChatHost.CreateWindow(
        YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, CoreWebView2Environment environment,
        Uri uri, Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>> capturePlayback)
        => new YouTubeLiveChatWindowSurface(new YouTubeLiveChatWindow(
            state, environment, uri, (Style)FindResource("ToolbarButtonStyle"), runtime.TaskbarWindow.Icon,
            () => GetYouTubeMiniPlayerWindowBounds(runtime.TaskbarWindow), capturePlayback, QueueYouTubeMiniPlayerSave));
    void IYouTubeLiveChatHost.QueueSave() => QueueYouTubeMiniPlayerSave();
    void IYouTubeLiveChatHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IYouTubeLiveChatHost.InvalidateArrangementPreviews(Guid id) => InvalidateYouTubeMiniPlayerArrangementPreviews(id);
    void IYouTubeLiveChatHost.QueueRender(YouTubeMiniPlayerRuntime runtime, Action action)
        => _ = runtime.TaskbarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Render, action);
    Rect IYouTubeLiveChatHost.GetVisibleWindowBounds(YouTubeMiniPlayerRuntime runtime)
        => GetYouTubeMiniPlayerVisibleWindowBounds(runtime);

    bool IYouTubeMiniPlayerAudioHost.IsHoverPreviewEnabled => _settings.EnableYouTubeMiniPlayerHoverAudioPreview;
    IReadOnlyList<MainWindow> IYouTubeMiniPlayerAudioHost.GetOwners() => GetYouTubeMiniPlayerOwnerWindows();
    Task IYouTubeMiniPlayerAudioHost.CaptureStatesAsync(MainWindow owner)
        => owner.CaptureYouTubeMiniPlayerStatesForLoadedAsync();
    IEnumerable<YouTubeMiniPlayerListItem> IYouTubeMiniPlayerAudioHost.GetLoadedItems(IReadOnlyList<MainWindow> owners)
        => owners.SelectMany(window => window._youTubeMiniPlayers
                .Where(state => window._youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime)
                    && !runtime.IsDisposed
                    && (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                        || runtime.LocalVideoView?.Controller?.IsLoaded == true))
                .Select(state => new YouTubeMiniPlayerListItem(window, state)));
    bool IYouTubeMiniPlayerAudioHost.IsItemLoaded(YouTubeMiniPlayerListItem item)
        => item.Owner._youTubeMiniPlayerRuntimes.TryGetValue(item.State.Id, out var runtime)
            && !runtime.IsDisposed
            && (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                || runtime.LocalVideoView?.Controller?.IsLoaded == true);
    IEnumerable<YouTubeMiniPlayerAudioSurface> IYouTubeMiniPlayerAudioHost.GetRuntimeAudioSurfaces()
        => _youTubeMiniPlayerRuntimes.Values.Select(runtime => new YouTubeMiniPlayerAudioSurface(
            runtime.State, runtime.AudioStatusBadge, runtime.AudioStatusText,
            YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                || runtime.LocalVideoView?.Controller?.IsLoaded == true));
    Task<bool> IYouTubeMiniPlayerAudioHost.SetMutedTransientAsync(YouTubeMiniPlayerListItem item, bool muted)
        => YouTubeMiniPlayerPlaybackCommandService.SetMutedTransientAsync(item.State, item.Owner._youTubeMiniPlayerRuntimes, muted);
    Task<bool> IYouTubeMiniPlayerAudioHost.SetPausedTransientAsync(YouTubeMiniPlayerListItem item, bool paused)
        => YouTubeMiniPlayerPlaybackCommandService.SetPausedTransientAsync(item.State, item.Owner._youTubeMiniPlayerRuntimes, paused);
    Task IYouTubeMiniPlayerAudioHost.SetMutedAsync(YouTubeMiniPlayerListItem item, bool muted)
        => YouTubeMiniPlayerPlaybackCommandService.SetMutedAsync(item.State, item.Owner._youTubeMiniPlayerRuntimes, muted);
    Task<bool> IYouTubeMiniPlayerAudioHost.SetPausedAsync(YouTubeMiniPlayerListItem item, bool paused)
        => YouTubeMiniPlayerPlaybackCommandService.SetPausedAsync(
            item.State, item.Owner._youTubeMiniPlayerRuntimes, paused,
            message => item.Owner.SetStatus(message, autoClear: true));
    void IYouTubeMiniPlayerAudioHost.QueueSave(MainWindow owner) => owner.QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerAudioHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IYouTubeMiniPlayerAudioHost.UpdateAllIndicators() => UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows();
    Task IYouTubeMiniPlayerAudioHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    Task<bool> IYouTubeMiniPlayerListActionsHost.SetVolumeAsync(YouTubeMiniPlayerListItem item, double volume)
        => YouTubeMiniPlayerPlaybackCommandService.SetVolumeAsync(
            item.State, item.Owner._youTubeMiniPlayerRuntimes, volume,
            message => item.Owner.SetStatus(message, autoClear: true));
    Task<bool> IYouTubeMiniPlayerListActionsHost.SetAudioBoostAsync(YouTubeMiniPlayerListItem item, int decibels)
        => YouTubeMiniPlayerPlaybackCommandService.SetAudioBoostAsync(
            item.State, item.Owner._youTubeMiniPlayerRuntimes, decibels,
            message => item.Owner.SetStatus(message, autoClear: true));
    void IYouTubeMiniPlayerListActionsHost.QueueSave(YouTubeMiniPlayerListItem item)
        => item.Owner.QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerListActionsHost.SetStatus(YouTubeMiniPlayerListItem item, string message)
        => item.Owner.SetStatus(message, autoClear: true);
    Task IYouTubeMiniPlayerListActionsHost.RefreshListAsync()
        => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    Task IYouTubeMiniPlayerListActionsHost.ExecuteCommandAsync(
        string actionId, YouTubeMiniPlayerListItem item, object? parameter)
        => ExecuteMiniPlayerCommandAsync(actionId, item, parameter);

    bool IYouTubeMiniPlayerListHost.IsClosingWindow => _isClosingWindow;
    bool IYouTubeMiniPlayerListHost.IsPreviewActive => s_youTubeMiniPlayerAudioSessionCoordinator.IsPreviewActive;
    Guid? IYouTubeMiniPlayerListHost.PreviewTargetId => s_youTubeMiniPlayerAudioSessionCoordinator.PreviewTargetId;
    bool IYouTubeMiniPlayerListHost.HasArrangementPreview => _youTubeMiniPlayerArrangementPreviewWindow is not null;
    bool IYouTubeMiniPlayerListHost.HasInitialArrangement => _youTubeMiniPlayerArrangementSessionCoordinator.HasInitialArrangement;
    bool IYouTubeMiniPlayerListHost.HasRestorablePlayer => !_isPrivateWindow && HasClosedYouTubeMiniPlayer();
    bool IYouTubeMiniPlayerListHost.IsRestoringClosedPlayer => _youTubeMiniPlayerLifetimeController.IsRestoringClosedPlayer;
    ModifierKeys IYouTubeMiniPlayerListHost.Modifiers => Keyboard.Modifiers;

    IReadOnlyList<YouTubeMiniPlayerListOwnerSource> IYouTubeMiniPlayerListHost.GetOwnerSources()
        => GetYouTubeMiniPlayerOwnerWindows()
            .Select(window => new YouTubeMiniPlayerListOwnerSource(window, window._youTubeMiniPlayers))
            .ToList();

    Task IYouTubeMiniPlayerListHost.CaptureStatesAsync(MainWindow owner)
        => owner.CaptureYouTubeMiniPlayerStatesForLoadedAsync();

    YouTubeMiniPlayerListRuntimeSnapshot IYouTubeMiniPlayerListHost.CaptureRuntimeSnapshot(
        YouTubeMiniPlayerListItem item)
    {
        item.Owner._youTubeMiniPlayerRuntimes.TryGetValue(item.State.Id, out var runtime);
        var isLocalVideoLoaded = runtime?.LocalVideoView?.Controller?.IsLoaded == true;
        if (isLocalVideoLoaded)
        {
            runtime?.LocalVideoView?.Controller?.CaptureState();
        }

        return new YouTubeMiniPlayerListRuntimeSnapshot(
            runtime?.DisplayHealth.GetSnapshot(),
            isLocalVideoLoaded,
            runtime?.LocalVideoView?.HasLoadFailed == true,
            YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null || isLocalVideoLoaded,
            s_youTubeMiniPlayerAudioSessionCoordinator.FocusMode,
            s_youTubeMiniPlayerAudioSessionCoordinator.IsFocusTarget(item.State.Id),
            s_youTubeMiniPlayerAudioSessionCoordinator.PreviewTargetId == item.State.Id);
    }

    bool IYouTubeMiniPlayerListHost.IsItemLoaded(YouTubeMiniPlayerListItem item) => IsYouTubeMiniPlayerListItemLoaded(item);
    bool IYouTubeMiniPlayerListHost.ShouldPlayAllLoadedPlayers() => ShouldPlayAllLoadedYouTubeMiniPlayers();
    void IYouTubeMiniPlayerListHost.RefreshLoadedWorkspaceOpenTabs() => RefreshLoadedWorkspaceOpenTabs();
    void IYouTubeMiniPlayerListHost.UpdateAudioFocusBar() => UpdateYouTubeMiniPlayerAudioFocusBar();
    void IYouTubeMiniPlayerListHost.UpdateWindowHighlights() => UpdateYouTubeMiniPlayerListWindowHighlights();
    void IYouTubeMiniPlayerListHost.UpdateAudioIndicators() => UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows();
    void IYouTubeMiniPlayerListHost.CancelArrangementPreview(bool showStatus) => CancelYouTubeMiniPlayerArrangementPreview(showStatus);
    void IYouTubeMiniPlayerListHost.QueueSave() => QueueYouTubeMiniPlayerSave();
    void IYouTubeMiniPlayerListHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    void IYouTubeMiniPlayerListHost.CancelPendingHoverPreview() => CancelPendingYouTubeMiniPlayerHoverAudioPreview();
    void IYouTubeMiniPlayerListHost.ScheduleHoverPreview(Guid id) => ScheduleYouTubeMiniPlayerHoverAudioPreview(id);
    Task IYouTubeMiniPlayerListHost.StopHoverPreviewAsync() => StopYouTubeMiniPlayerHoverAudioPreviewAsync();
    bool IYouTubeMiniPlayerListHost.TryGetCursorDevicePoint(out Point point) => TryGetCursorDevicePoint(out point);
    void IYouTubeMiniPlayerListHost.DoDragDrop(DependencyObject source, IDataObject data, DragDropEffects effects)
        => DragDrop.DoDragDrop(source, data, effects);

    void IYouTubeMiniPlayerListHost.RefreshOtherOpenLists()
    {
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows().Where(window =>
                     !ReferenceEquals(window, this)
                     && window.YouTubeMiniPlayerListPopup.IsOpen))
        {
            _ = window.RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
        }
    }

    void IYouTubeMiniPlayerListHost.RestoreAndBringToFront(YouTubeMiniPlayerListItem item)
    {
        var callbacks = item.Owner.CreateYouTubeMiniPlayerCommandCallbacks();
        callbacks.SetMinimized(item.State, false);
        callbacks.BringToFront(item.State.Id, true);
    }

    bool IYouTubeMiniPlayerListPopupHost.IsPreviewActive => s_youTubeMiniPlayerAudioSessionCoordinator.IsPreviewActive;
    bool IYouTubeMiniPlayerListPopupHost.IsArrangementEditMode => _youTubeMiniPlayerArrangementPreviewWindow?.IsEditMode == true;
    bool IYouTubeMiniPlayerListPopupHost.IsOverArrangementEditableControl(Point point)
        => _youTubeMiniPlayerArrangementPreviewWindow?.IsPointOverEditableControl(point) == true;
    Rect IYouTubeMiniPlayerListPopupHost.GetWorkAreaForDevicePoint(Point devicePoint)
        => GetDisplayWorkArea(devicePoint);
    string? IYouTubeMiniPlayerListPopupHost.GetTriggerTag(DependencyObject? source) => FindVisualParent<Button>(source)?.Tag as string;
    bool IYouTubeMiniPlayerListPopupHost.TryGetCursorDevicePoint(out Point point) => TryGetCursorDevicePoint(out point);
    void IYouTubeMiniPlayerListPopupHost.CancelArrangementPreview(bool showStatus) => CancelYouTubeMiniPlayerArrangementPreview(showStatus);
    void IYouTubeMiniPlayerListPopupHost.CancelPendingHoverPreview() => CancelPendingYouTubeMiniPlayerHoverAudioPreview();
    Task IYouTubeMiniPlayerListPopupHost.StopHoverPreviewAsync() => StopYouTubeMiniPlayerHoverAudioPreviewAsync();

    private static List<MainWindow> GetYouTubeMiniPlayerOwnerWindows()
    {
        return Application.Current.Windows
            .OfType<MainWindow>()
            .Where(static window => window.IsVisible || window.IsHiddenFloatingMediaHostActive())
            .OrderBy(static window => window._workspaceWindowIndex)
            .ToList();
    }

    private bool IsHiddenFloatingMediaHostActive()
    {
        return _floatingMediaHostLifetime.IsHiddenRetained
            && _youTubeMiniPlayerRuntimes.Count > 0;
    }

    private bool TryHideWindowForActiveFloatingMedia(CancelEventArgs e)
    {
        var hasOtherVisibleMainWindow = GetOpenMainWindows()
            .Any(window => !ReferenceEquals(window, this));
        if (!_floatingMediaHostLifetime.CanRetain(
            s_applicationWindowCloseCoordinator.IsClosingAllWindows,
            _youTubeMiniPlayerRuntimes.Count,
            hasOtherVisibleMainWindow))
        {
            return false;
        }

        e.Cancel = true;
        _ = GetYouTubeMiniPlayerHostScreenOrigin();
        SaveFloatingMediaOverlaysNow();
        if (!_floatingMediaHostLifetime.TryRetain(
            s_applicationWindowCloseCoordinator.IsClosingAllWindows,
            _youTubeMiniPlayerRuntimes.Count,
            hasOtherVisibleMainWindow))
        {
            return false;
        }

        _closeConfirmed = false;
        _skipSingleWindowSessionSaveOnClose = false;
        _recordCurrentWindowInClosedHistory = false;
        _statusPresentationController.ClosePopupImmediately();
        Hide();
        QueueWorkspaceOpenTabsRefreshForOtherWindows();
        return true;
    }

    private void RestoreHiddenYouTubeMiniPlayerHostWindow()
    {
        if (!_floatingMediaHostLifetime.IsHiddenRetained || IsVisible)
        {
            return;
        }

        if (!_floatingMediaHostLifetime.TryRestore())
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task ActivateFloatingMediaSourceTabAsync()
    {
        RestoreHiddenYouTubeMiniPlayerHostWindow();
        await ActivateSelectedTabAsync();
    }

    private void CloseHiddenYouTubeMiniPlayerHostIfEmpty()
    {
        if (!_floatingMediaHostLifetime.IsHiddenRetained
            || _youTubeMiniPlayerRuntimes.Count > 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_floatingMediaHostLifetime.IsHiddenRetained
                || _youTubeMiniPlayerRuntimes.Count > 0
                || IsVisible)
            {
                return;
            }

            if (!_floatingMediaHostLifetime.TryCommitCloseWhenEmpty(
                _youTubeMiniPlayerRuntimes.Count))
            {
                return;
            }

            _closeConfirmed = true;
            Close();
        });
    }

    private void InitializeYouTubeMiniPlayerTimers()
    {
        _youTubeMiniPlayerListPopupController.InitializeTracking();
        _youTubeMiniPlayerListViewController.InitializeItems();
        _youTubeMiniPlayerPersistenceController.InitializeTimers();

        _youTubeMiniPlayerListViewController.InitializeRefreshTimer();

        _youTubeMiniPlayerListActionsController.InitializeVolumeTimer();

        _youTubeMiniPlayerAudioController.InitializeHoverTimer();
    }

    private void InitializeFloatingMediaTimers()
    {
        InitializeYouTubeMiniPlayerTimers();
    }

    private async Task ApplyFloatingMediaSettingsAsync(AppSettings result)
    {
        _settings.EnableYouTubeMiniPlayerHoverAudioPreview =
            result.EnableYouTubeMiniPlayerHoverAudioPreview;
        _settings.EnableYouTubeMiniPlayerSizeBasedQuality =
            result.EnableYouTubeMiniPlayerSizeBasedQuality;
        var miniPlayerQualityThresholds = YouTubeMiniPlayerQualitySettingsProfile.Normalize(
            result.YouTubeMiniPlayerQualityLowWidthThreshold,
            result.YouTubeMiniPlayerQualityHighWidthThreshold);
        _settings.YouTubeMiniPlayerQualityLowWidthThreshold =
            miniPlayerQualityThresholds.LowWidthThreshold;
        _settings.YouTubeMiniPlayerQualityHighWidthThreshold =
            miniPlayerQualityThresholds.HighWidthThreshold;
        if (!_settings.EnableYouTubeMiniPlayerHoverAudioPreview)
        {
            CancelPendingYouTubeMiniPlayerHoverAudioPreview();
            await StopYouTubeMiniPlayerHoverAudioPreviewAsync();
        }

        foreach (var runtime in _youTubeMiniPlayerRuntimes.Values
                     .Where(static runtime => runtime.State.SourceKind == MiniPlayerSourceKind.YouTube))
        {
            QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime, immediate: true, force: true);
        }
    }

    private void QueueYouTubeMiniPlayerSizeBasedQualityUpdate(
        YouTubeMiniPlayerRuntime runtime,
        bool immediate = false,
        bool force = false)
        => _youTubeMiniPlayerActivityController.QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime, immediate, force);

    private void StopYouTubeMiniPlayerTimers()
    {
        _youTubeMiniPlayerRecoveryController.CancelAll();
        _youTubeMiniPlayerListPopupController.Dispose();
        _youTubeMiniPlayerPersistenceController.StopTracking();
        _youTubeMiniPlayerListViewController.StopRefreshTracking();
        _youTubeMiniPlayerListActionsController.StopVolumeTracking();
        _youTubeMiniPlayerAudioController.StopHoverTracking();
        CancelYouTubeMiniPlayerThumbnailLoads();
        CancelYouTubeMiniPlayerArrangementPreview(showStatus: false);
    }

    private void StopFloatingMediaTimers()
    {
        StopYouTubeMiniPlayerTimers();
    }

    private async void OpenTabInYouTubeMiniPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            await _youTubeMiniPlayerController.OpenFromTabAsync(
                tab,
                _tabs,
                _workspaceWindowIndex,
                WebHost.ActualWidth,
                WebHost.ActualHeight,
                CreateYouTubeMiniPlayerOpenWorkflowCallbacks());
        }
    }

    private void AddMiniPlayerLinkContextMenuItem(
        ContextMenu menu,
        BrowserTab sourceTab,
        string linkUri,
        string? linkText,
        string? miniPlayerLinkUri)
    {
        var youTubeMiniPlayerLinkUri = miniPlayerLinkUri ?? linkUri;
        if (!IsYouTubeUrl(youTubeMiniPlayerLinkUri))
        {
            return;
        }

        var canOpenInMiniPlayer = YouTubeMiniPlayerOpenRequestService.TryCreateFromLink(
            youTubeMiniPlayerLinkUri,
            linkText,
            out _);
        menu.Items.Add(CreateContextCommand(
            "ミニプレイヤーで開く",
            () => _ = _youTubeMiniPlayerController.OpenFromLinkAsync(
                sourceTab,
                youTubeMiniPlayerLinkUri,
                linkText,
                _tabs,
                _workspaceWindowIndex,
                WebHost.ActualWidth,
                WebHost.ActualHeight,
                CreateYouTubeMiniPlayerOpenWorkflowCallbacks()),
            canOpenInMiniPlayer));
    }

    private YouTubeMiniPlayerOpenWorkflowCallbacks CreateYouTubeMiniPlayerOpenWorkflowCallbacks()
    {
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks(rememberClosedPlayers: false);
        return new YouTubeMiniPlayerOpenWorkflowCallbacks(
            GetCurrentPageUrl,
            GetCurrentPageTitle,
            state => CreateYouTubeMiniPlayerRuntime(state, loadImmediately: false),
            CreateYouTubeMiniPlayerPositionCallbacks().SetPosition,
            id => commandCallbacks.BringToFront(id, true),
            LoadYouTubeMiniPlayerRuntimeAsync,
            QueueYouTubeMiniPlayerSave,
            commandCallbacks.Close,
            message => SetStatus(message, autoClear: true));
    }

    private YouTubeMiniPlayerRuntime CreateYouTubeMiniPlayerRuntime(YouTubeMiniPlayerState state, bool loadImmediately)
    {
        var compositionCallbacks = CreateYouTubeMiniPlayerRuntimeCompositionCallbacks();
        var runtime = YouTubeMiniPlayerRuntimeCompositionService.Create(
            state,
            loadImmediately,
            (Style)FindResource("ToolbarButtonStyle"),
            compositionCallbacks.CreateWebView,
            compositionCallbacks.CreatePlaceholder,
            compositionCallbacks.CreateLocalVideoView,
            _youTubeMiniPlayerRuntimes,
            compositionCallbacks.WiringCallbacks,
            AddYouTubeMiniPlayerResizeThumbs);
        if (state.SourceKind == MiniPlayerSourceKind.YouTube)
        {
            runtime.InteractiveResizeStateChanged = isActive =>
                SetYouTubeMiniPlayerInteractiveResize(runtime, isActive);
            runtime.TitleBandBackButton.Click += (_, e) =>
            {
                e.Handled = true;
                NavigateYouTubeMiniPlayerHistory(runtime, goForward: false);
            };
            runtime.TitleBandForwardButton.Click += (_, e) =>
            {
                e.Handled = true;
                NavigateYouTubeMiniPlayerHistory(runtime, goForward: true);
            };
            runtime.TitleBandLiveViewerCountButton.Click += async (_, e) =>
            {
                e.Handled = true;
                await SeekYouTubeMiniPlayerToLiveEdgeFromViewerCountAsync(runtime);
            };
            runtime.FullScreenStateChanged = isFullScreen =>
            {
                UpdateYouTubeLiveChatFullScreenOverlay(runtime, isFullScreen);
                QueueYouTubeMiniPlayerContentTransformReapplyAfterFullScreenChange(runtime);
                QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime, immediate: true, force: true);
            };
            runtime.TitleBandChatButton.Click += async (_, _) =>
                await OpenYouTubeLiveChatAsync(state);
            runtime.TaskbarWindow.LocationChanged += (_, _) =>
                SynchronizeYouTubeLiveChatWindow(state.Id);
            runtime.TaskbarWindow.SizeChanged += (_, _) =>
            {
                SynchronizeYouTubeLiveChatWindow(state.Id);
                if (!runtime.IsResizing)
                {
                    QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime);
                }
            };
        }

        runtime.TaskbarWindow.LocationChanged += (_, _) =>
            InvalidateYouTubeMiniPlayerArrangementPreviews(state.Id);
        runtime.TaskbarWindow.SizeChanged += (_, _) =>
            InvalidateYouTubeMiniPlayerArrangementPreviews(state.Id);
        runtime.TaskbarWindow.StateChanged += (_, _) =>
            InvalidateYouTubeMiniPlayerArrangementPreviews(state.Id);
        runtime.TaskbarWindow.Closed += (_, _) =>
            InvalidateYouTubeMiniPlayerArrangementPreviews(state.Id);

        CreateYouTubeMiniPlayerPositionCallbacks().ForceRuntimeNativePosition(runtime);
        return runtime;
    }

    private static void NavigateYouTubeMiniPlayerHistory(
        YouTubeMiniPlayerRuntime runtime,
        bool goForward)
    {
        if (runtime.IsDisposed
            || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        YouTubeMiniPlayerChromeFactory.UpdateNavigationHistoryButtons(
            runtime,
            core.CanGoBack,
            core.CanGoForward);
        if (goForward)
        {
            runtime.NavigationHistory.TryGoForward(core.GoForward);
        }
        else
        {
            runtime.NavigationHistory.TryGoBack(core.GoBack);
        }
    }

    private async Task SeekYouTubeMiniPlayerToLiveEdgeFromViewerCountAsync(
        YouTubeMiniPlayerRuntime runtime)
    {
        var state = runtime.State;
        if (runtime.IsDisposed
            || runtime.IsLiveEdgeSeekInProgress
            || !state.IsLiveBroadcast)
        {
            return;
        }

        runtime.IsLiveEdgeSeekInProgress = true;
        runtime.TitleBandLiveViewerCountButton.IsEnabled = false;
        try
        {
            await ExecuteRegisteredCommandAsync(
                MiniPlayerCommandProvider.SeekToLiveEdgeActionId,
                CreateMiniPlayerCommandContext(state.Id),
                parameter: null,
                showFailure: true);
        }
        finally
        {
            runtime.IsLiveEdgeSeekInProgress = false;
            if (!runtime.IsDisposed)
            {
                YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(
                    runtime,
                    state.IsLiveBroadcast,
                    state.LiveViewerCountText);
            }
        }
    }

    private Task OpenYouTubeLiveChatAsync(YouTubeMiniPlayerState state)
        => _youTubeLiveChatController.OpenYouTubeLiveChatAsync(state);

    private bool IsCurrentYouTubeMiniPlayerRuntime(Guid miniPlayerId, YouTubeMiniPlayerRuntime runtime)
    {
        return !runtime.IsDisposed
            && _youTubeMiniPlayerRuntimes.TryGetValue(miniPlayerId, out var current)
            && ReferenceEquals(current, runtime);
    }

    private bool IsCurrentYouTubeMiniPlayerVideo(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime,
        string expectedVideoId)
    {
        return IsCurrentYouTubeMiniPlayerRuntime(state.Id, runtime)
            && YouTubeUrlService.TryGetVideoId(state.Url, out var currentVideoId)
            && currentVideoId.Equals(expectedVideoId, StringComparison.Ordinal)
            && YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is { } core
            && YouTubeUrlService.TryGetVideoId(core.Source, out var sourceVideoId)
            && sourceVideoId.Equals(expectedVideoId, StringComparison.Ordinal);
    }

    private void BeginYouTubeLiveChatVideoTransition(YouTubeMiniPlayerState state)
        => _youTubeLiveChatController.BeginYouTubeLiveChatVideoTransition(state);

    private Task ApplyYouTubeLiveChatAvailabilityAsync(
        YouTubeMiniPlayerState state,
        YouTubeLiveChatAvailabilityResult availability)
        => _youTubeLiveChatController.ApplyYouTubeLiveChatAvailabilityAsync(state, availability);

    private Task RefreshYouTubeLiveChatAvailabilityAsync(
        YouTubeMiniPlayerState state,
        WebView2 webView)
        => _youTubeLiveChatController.RefreshYouTubeLiveChatAvailabilityAsync(state, webView);

    private bool HasActiveYouTubeMiniPlayerWindow()
    {
        return _youTubeMiniPlayerRuntimes.Values.Any(runtime =>
            !runtime.IsDisposed
            && runtime.TaskbarWindow.IsActive);
    }

    private bool HasActiveYouTubeLiveChatWindow()
        => _youTubeLiveChatController.HasActiveYouTubeLiveChatWindow();

    private static Rect GetYouTubeMiniPlayerWindowBounds(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        return bounds.IsEmpty
            ? new Rect(window.Left, window.Top, Math.Max(1, window.Width), Math.Max(1, window.Height))
            : bounds;
    }

    private void SynchronizeYouTubeLiveChatWindow(Guid miniPlayerId)
        => _youTubeLiveChatController.SynchronizeYouTubeLiveChatWindow(miniPlayerId);

    private void UpdateYouTubeLiveChatFullScreenOverlay(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
        => _youTubeLiveChatController.UpdateYouTubeLiveChatFullScreenOverlay(runtime, isFullScreen);

    private static Rect GetYouTubeMiniPlayerVisibleWindowBounds(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.Root.IsVisible
            && runtime.Root.ActualWidth > 0
            && runtime.Root.ActualHeight > 0
            && PresentationSource.FromVisual(runtime.Root) is not null)
        {
            var origin = YouTubeMiniPlayerPositionService.GetVisualScreenOrigin(runtime.Root);
            var bounds = new Rect(
                origin.X,
                origin.Y,
                runtime.Root.ActualWidth,
                runtime.Root.ActualHeight);
            if (!bounds.IsEmpty
                && double.IsFinite(bounds.Left)
                && double.IsFinite(bounds.Top)
                && double.IsFinite(bounds.Width)
                && double.IsFinite(bounds.Height))
            {
                return bounds;
            }
        }

        return GetYouTubeMiniPlayerWindowBounds(runtime.TaskbarWindow);
    }

    private void BringYouTubeLiveChatWindowToFront(Guid miniPlayerId)
        => _youTubeLiveChatController.BringYouTubeLiveChatWindowToFront(miniPlayerId);

    private void CloseYouTubeLiveChatWindow(Guid miniPlayerId)
        => _youTubeLiveChatController.CloseYouTubeLiveChatWindow(miniPlayerId);

    private void CloseAllYouTubeLiveChatWindows()
        => _youTubeLiveChatController.CloseAllYouTubeLiveChatWindows();

    private YouTubeMiniPlayerRuntimeCompositionCallbacks CreateYouTubeMiniPlayerRuntimeCompositionCallbacks()
    {
        return YouTubeMiniPlayerRuntimeCompositionCallbackFactory.Create(
            _youTubeMiniPlayerRuntimes,
            (Style)FindResource("ToolbarButtonStyle"),
            QueueYouTubeMiniPlayerSave,
            ApplyYouTubeMiniPlayerWatchPageModeAsync,
            ApplyYouTubeMiniPlayerSavedStateAsync,
            LoadYouTubeMiniPlayerRuntimeAsync,
            message => SetStatus(message, autoClear: true),
            TryBeginYouTubeMiniPlayerGestureInteraction,
            interactionId => s_inputInteractionHub.TryComplete(interactionId),
            CreateYouTubeMiniPlayerPositionCallbacks(),
            CreateYouTubeMiniPlayerCommandCallbacks(),
            CreateYouTubeMiniPlayerOverlayVisibilityCallbacks(),
            CreateYouTubeMiniPlayerOverlayPlacementCallbacks(),
            SetYouTubeSpeedResetContent,
            SetYouTubeLoopContent,
            CreateLocalVideoPlayerView,
            AttachYouTubeMiniPlayerDrag,
            YouTubeSpeedStep,
            QueueYouTubeMiniPlayerAutoRecovery,
            HandleYouTubeMiniPlayerProcessFailure,
            BeginYouTubeLiveChatVideoTransition,
            ApplyYouTubeLiveChatAvailabilityAsync,
            RecordYouTubeMiniPlayerBrowsingHistory,
            QueueYouTubeMiniPlayerInactiveSuspension);
    }

    private Views.LocalVideoPlayerView CreateLocalVideoPlayerView(YouTubeMiniPlayerState state)
    {
        var view = new Views.LocalVideoPlayerView();
        view.Initialize(state);
        view.ConfigureGestures(
            _settings,
            (command, _) => ExecuteYouTubeMiniPlayerGestureCommandAsync(state.Id, command),
            () => TryBeginYouTubeMiniPlayerGestureInteraction(state.Id),
            interactionId => s_inputInteractionHub.TryComplete(interactionId),
            () => TryBeginYouTubeMiniPlayerRightWheelInteraction(state.Id));
        view.PlaybackStateChanged += queueSave =>
        {
            UpdateYouTubeMiniPlayerStateCaptureTimer();
            UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows();
            if (queueSave)
            {
                QueueYouTubeMiniPlayerSave();
            }
        };
        view.VideoDimensionsAvailable += (width, height) =>
            ApplyLocalVideoAspectRatio(state, width, height);
        view.FullScreenRequested += () =>
        {
            if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
            {
                YouTubeMiniPlayerRuntimeLifecycleService.ToggleFullScreen(runtime);
            }
        };
        view.OverlayHoverRefreshRequested += () =>
        {
            if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
            {
                RefreshYouTubeMiniPlayerHoverFromCursor(runtime);
            }
        };
        view.RelinkRequested += () => RelinkLocalVideoMiniPlayer(state, view);
        view.LocalFileDropped += filePath => ReplaceLocalVideoMiniPlayerFile(state, view, filePath);
        view.CloseGestureRequested += () => CreateYouTubeMiniPlayerCommandCallbacks().Close(state.Id);
        return view;
    }

    private void ApplyLocalVideoAspectRatio(YouTubeMiniPlayerState state, double videoWidth, double videoHeight)
        => _localVideoMiniPlayerController.ApplyAspectRatio(state, videoWidth, videoHeight);

    private void RelinkLocalVideoMiniPlayer(YouTubeMiniPlayerState state, Views.LocalVideoPlayerView view)
        => _localVideoMiniPlayerController.Relink(state, view.ReplaceFile);

    private void ReplaceLocalVideoMiniPlayerFile(YouTubeMiniPlayerState state, Views.LocalVideoPlayerView view, string filePath)
        => _localVideoMiniPlayerController.ReplaceFile(state, view.ReplaceFile, filePath);

    private void UpdateLocalVideoMiniPlayerTitle(YouTubeMiniPlayerState state)
    {
        if (!_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
        {
            return;
        }

        runtime.TaskbarWindow.Title = state.Title;
        runtime.TaskbarTitleText.Text = state.Title;
        runtime.TitleBandTitleText.Text = state.Title;
    }

    private static string? SelectLocalVideoFile(string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ミニプレイヤーで再生する動画を選択",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "動画ファイル|*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.mkv;*.webm;*.mpeg;*.mpg;*.ts;*.mts;*.m2ts|すべてのファイル|*.*"
        };
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            var initialDirectory = Path.GetDirectoryName(initialPath);
            if (!string.IsNullOrWhiteSpace(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }
            dialog.FileName = Path.GetFileName(initialPath);
        }

        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    private void ShowLocalVideoInExplorer(YouTubeMiniPlayerState state)
        => _localVideoMiniPlayerController.ShowInExplorer(state);

    private bool OpenLocalVideoMiniPlayer(string filePath)
        => _localVideoMiniPlayerController.Open(filePath);

    private IReadOnlyList<string> GetDroppedLocalVideoPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        return _localVideoMiniPlayerController.NormalizeDroppedPaths(paths);
    }

    private async Task LoadYouTubeMiniPlayerRuntimeAsync(YouTubeMiniPlayerState state, Uri uri)
    {
        ResetYouTubePlaybackDiagnostic("mini-player", state.Id);
        if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    runtime.Root.UpdateLayout();
                    runtime.WebHost.UpdateLayout();
                },
                DispatcherPriority.Render);
        }

        var overlayVisibilityCallbacks = CreateYouTubeMiniPlayerOverlayVisibilityCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks();
        await YouTubeMiniPlayerRuntimeLoadService.LoadAsync(
            state,
            uri,
            _youTubeMiniPlayerRuntimes,
            YouTubeMiniPlayerRuntimeLoadCallbackFactory.Create(
                _youTubeMiniPlayerRuntimes,
                EnsureWebViewEnvironmentAsync,
                QueueYouTubeMiniPlayerSave,
                message => SetStatus(message, autoClear: true),
                ApplyYouTubeMiniPlayerWatchPageModeAsync,
                ApplyYouTubeMiniPlayerSavedStateAsync,
                commandCallbacks.BringToFront,
                overlayVisibilityCallbacks.SetToolbarVisibility,
                overlayVisibilityCallbacks.KeepToolbarVisible,
                GetCursorScreenPoint,
                IsLeftMouseButtonDown,
                (currentState, root, ensureVisible) =>
                    ClampAndSnapYouTubeMiniPlayerDuringDrag(
                        currentState,
                        root,
                        ensureVisible,
                        positionCallbacks),
                positionCallbacks.SyncPopupOffsets,
                ReapplyYouTubeMiniPlayerContentTransform,
                overlayVisibilityCallbacks.SetTitleBandVisibility,
                TryBeginYouTubeMiniPlayerGesture,
                HandleYouTubeMiniPlayerGesture,
                CompleteYouTubeMiniPlayerGesture,
                HandleYouTubeMiniPlayerSideButton,
                TryBeginYouTubeMiniPlayerHoldSeek,
                CompleteYouTubeMiniPlayerHoldSeek,
                HandleYouTubeMiniPlayerRightWheel,
                CompleteYouTubeMiniPlayerRightWheel,
                UpdateYouTubeMiniPlayerContentTransform,
                commandCallbacks.Close,
                HandleYouTubeMiniPlayerPlaybackDiagnostic,
                HandleYouTubeMiniPlayerProcessFailure,
                BeginYouTubeLiveChatVideoTransition,
                ApplyYouTubeLiveChatAvailabilityAsync,
                RecordYouTubeMiniPlayerBrowsingHistory,
                QueueYouTubeMiniPlayerAutoRecovery,
                QueueYouTubeMiniPlayerInactiveSuspension,
                UpdateYouTubeMiniPlayerStateCaptureTimer));
        UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows();
    }

    private void HandleYouTubeMiniPlayerPlaybackDiagnostic(YouTubeMiniPlayerState state, YouTubePlaybackDiagnosticMessage message)
        => _youTubeMiniPlayerActivityController.HandlePlaybackDiagnostic(state, message);

    private void RecordYouTubeMiniPlayerBrowsingHistory(YouTubeMiniPlayerRuntime runtime)
    {
        var state = runtime.State;
        if (state.SourceKind != MiniPlayerSourceKind.YouTube
            || string.Equals(
                runtime.LastRecordedBrowsingHistoryUrl,
                state.Url,
                StringComparison.OrdinalIgnoreCase)
            || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core
            || !YouTubeUrlService.TryGetVideoId(state.Url, out var expectedVideoId)
            || !YouTubeUrlService.TryGetVideoId(core.Source, out var loadedVideoId)
            || !string.Equals(expectedVideoId, loadedVideoId, StringComparison.Ordinal))
        {
            return;
        }

        runtime.LastRecordedBrowsingHistoryUrl = state.Url;
        App.BrowsingHistory.EnqueueVisit(
            new BrowsingHistoryVisit(
                state.Id,
                state.Url,
                state.Title,
                state.ContainerProfileId,
                BrowsingHistorySourceKind.YouTubeMiniPlayer,
                DateTimeOffset.UtcNow),
            isPrivate: _isPrivateWindow);
        RecordRecentItem(RecentItemFactory.FromWeb(state.Url, state.Title, containerProfileId: state.ContainerProfileId));
    }

    private void QueueYouTubeMiniPlayerInactiveSuspension(
        YouTubeMiniPlayerState state,
        bool stopStalledLoad)
        => _youTubeMiniPlayerActivityController.QueueYouTubeMiniPlayerInactiveSuspension(state, stopStalledLoad);

    private void ResumeYouTubeMiniPlayerForInteraction(YouTubeMiniPlayerRuntime runtime)
        => _youTubeMiniPlayerActivityController.ResumeYouTubeMiniPlayerForInteraction(runtime);

    private void HandleYouTubeMiniPlayerProcessFailure(
        YouTubeMiniPlayerState state,
        CoreWebView2ProcessFailedEventArgs args)
    {
        HandleYouTubePlaybackProcessFailure("mini-player", state.Id, state.Url, args);
        _youTubeMiniPlayerRecoveryController.HandleProcessFailure(state, args.ProcessFailedKind);
    }

    private void QueueYouTubeMiniPlayerAutoRecovery(YouTubeMiniPlayerState state, string reason)
        => _youTubeMiniPlayerRecoveryController.QueueYouTubeMiniPlayerAutoRecovery(state, reason);

    private BrowserTab OpenYouTubeMiniPlayerSourceTab(YouTubeMiniPlayerSourceTabOpenRequest request)
    {
        RestoreHiddenYouTubeMiniPlayerHostWindow();
        return OpenTab(
            request.Url,
            activate: false,
            title: request.Title,
            isMuted: request.IsMuted,
            youTubePlaybackVideoId: request.VideoId,
            youTubePlaybackPositionSeconds: request.PositionSeconds,
            youTubePlaybackSpeed: request.PlaybackSpeed,
            youTubePlaybackIsPaused: request.IsPaused,
            youTubePlaybackIsLooping: request.IsLooping,
            containerProfileId: request.ContainerProfileId,
            queueBackgroundLoading: false);
    }

    private void PrepareYouTubeMiniPlayerRecoverySourceTab(
        BrowserTab tab,
        YouTubeMiniPlayerState state)
    {
        if (YouTubeUrlService.TryGetVideoId(state.Url, out var videoId))
        {
            tab.YouTubePlaybackVideoId = videoId;
        }

        tab.YouTubePlaybackPositionSeconds = state.LastPositionSeconds;
        tab.YouTubePlaybackSpeed = state.PlaybackRate;
        tab.YouTubePlaybackIsPaused = state.IsPaused;
        tab.YouTubePlaybackIsLooping = state.IsLooping;
        tab.IsMuted = state.IsMuted;
        if (GetTabWebView(tab)?.CoreWebView2 is { } core)
        {
            core.IsMuted = state.IsMuted;
        }

        QueueYouTubePlaybackRestoreAttempt(tab);
    }

    private Task RecoverFloatingMediaProcessGroupAsync(string containerProfileId, uint browserProcessId)
        => _youTubeMiniPlayerRecoveryController.RecoverFloatingMediaProcessGroupAsync(containerProfileId, browserProcessId);

    private YouTubeMiniPlayerOverlayVisibilityCallbacks CreateYouTubeMiniPlayerOverlayVisibilityCallbacks()
    {
        var overlayPlacementCallbacks = CreateYouTubeMiniPlayerOverlayPlacementCallbacks();
        return YouTubeMiniPlayerOverlayVisibilityCallbackFactory.Create(
            _youTubeMiniPlayerRuntimes,
            TimeSpan.FromMilliseconds(YouTubeMiniPlayerToolbarHideDelayMilliseconds),
            overlayPlacementCallbacks.UpdateToolbarPlacement,
            current => _ = overlayPlacementCallbacks.UpdateToolbarPlacementAsync(current),
            overlayPlacementCallbacks.RefreshOverlayPopupPosition,
            overlayPlacementCallbacks.UpdateTitleBandPlacement);
    }

    private YouTubeMiniPlayerOverlayPlacementCallbacks CreateYouTubeMiniPlayerOverlayPlacementCallbacks()
    {
        return YouTubeMiniPlayerOverlayPlacementCallbackFactory.Create(
            _youTubeMiniPlayerRuntimes,
            YouTubeMiniPlayerMinimizedWidth,
            YouTubeControlPopupFallbackWidth,
            YouTubeControlPopupEdgeMargin,
            YouTubeControlPopupBottomMargin,
            YouTubeControlPopupFullscreenEdgeMargin,
            YouTubeControlPopupFullscreenBottomMargin,
            GetYouTubeControlPanelSize,
            TryGetYouTubeControlPopupPlacementCoreAsync);
    }

    private YouTubeMiniPlayerPositionCallbacks CreateYouTubeMiniPlayerPositionCallbacks()
    {
        return YouTubeMiniPlayerPositionCallbackFactory.Create(
            _youTubeMiniPlayerRuntimes,
            Dispatcher,
            GetYouTubeMiniPlayerHostScreenOrigin,
            GetVirtualScreenBounds,
            RefreshYouTubeMiniPlayerHoverFromCursor,
            CreateYouTubeMiniPlayerOverlayPlacementCallbacks());
    }

    private YouTubeMiniPlayerCommandCallbacks CreateYouTubeMiniPlayerCommandCallbacks(
        bool rememberClosedPlayers = true)
    {
        var shouldRememberClosedPlayers = rememberClosedPlayers && !_isPrivateWindow;
        var overlayPlacementCallbacks = CreateYouTubeMiniPlayerOverlayPlacementCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        var callbacks = _youTubeMiniPlayerController.CreateCommandCallbacks(
            _tabs,
            new YouTubeMiniPlayerCommandWorkflowDependencies(
                () => ++_youTubeMiniPlayerZIndex,
                overlayPlacementCallbacks.UpdateToolbarPlacement,
                overlayPlacementCallbacks.UpdateTitleBandPlacement,
                positionCallbacks.SetPosition,
                CaptureYouTubeMiniPlayerStateAsync,
                OpenYouTubeMiniPlayerSourceTab,
                SelectTab,
                ActivateFloatingMediaSourceTabAsync,
                QueueYouTubeMiniPlayerSave,
                UpdateYouTubeMiniPlayerStateCaptureTimer,
                message => SetStatus(message, autoClear: true)));

        return callbacks with
        {
            BringToFront = (miniPlayerId, reopenPopup) =>
                _youTubeMiniPlayerActivityController.BringToFront(miniPlayerId, reopenPopup, callbacks.BringToFront),
            ToggleMinimized = state =>
                _youTubeMiniPlayerActivityController.SetMinimized(state, !state.IsMinimized, callbacks.SetMinimized),
            SetMinimized = (state, isMinimized) =>
                _youTubeMiniPlayerActivityController.SetMinimized(state, isMinimized, callbacks.SetMinimized),
            ApplyMinimizedState = (runtime, activate) =>
                _youTubeMiniPlayerActivityController.ApplyMinimizedState(runtime, activate, callbacks.ApplyMinimizedState),
            ActivateSourceTab = state =>
            {
                if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
                {
                    ShowLocalVideoInExplorer(state);
                    return;
                }

                callbacks.ActivateSourceTab(state);
            },
            Close = miniPlayerId => _youTubeMiniPlayerLifetimeController.Close(miniPlayerId, shouldRememberClosedPlayers, callbacks.Close),
            DisposeAll = () => _youTubeMiniPlayerLifetimeController.DisposeAll(shouldRememberClosedPlayers, callbacks.DisposeAll)
        };
    }

    private bool HasPlayingFloatingMedia()
    {
        return _youTubeMiniPlayers.Any(state =>
            state.SourceKind == MiniPlayerSourceKind.YouTube
            && state.IsPaused == false
            && _youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime)
            && !runtime.IsDisposed
            && YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null);
    }

    private void TryBeginYouTubeMiniPlayerHoldSeek(YouTubeMiniPlayerRuntime runtime)
    {
        var transition = s_inputInteractionHub.TryBegin(
            InputInteractionKind.MiniPlayerHoldSeek,
            _workspaceWindowId,
            runtime.State.Id,
            DateTimeOffset.Now);
        if (!transition.Accepted || transition.Current is not { } current)
        {
            CancelYouTubeMiniPlayerRightInput(runtime, "holdSeek");
            return;
        }

        runtime.HoldSeekInteractionId = current.Id;
    }

    private void TryBeginYouTubeMiniPlayerGesture(YouTubeMiniPlayerRuntime runtime)
    {
        var interactionId = TryBeginYouTubeMiniPlayerGestureInteraction(runtime.State.Id);
        if (interactionId is null)
        {
            CancelYouTubeMiniPlayerRightInput(runtime, "gesture");
            return;
        }

        runtime.GestureInteractionId = interactionId;
    }

    private long? TryBeginYouTubeMiniPlayerGestureInteraction(Guid miniPlayerId)
    {
        var transition = s_inputInteractionHub.TryBegin(
            InputInteractionKind.MiniPlayerGesture,
            _workspaceWindowId,
            miniPlayerId,
            DateTimeOffset.Now);
        return transition.Accepted ? transition.Current?.Id : null;
    }

    private long? TryBeginYouTubeMiniPlayerRightWheelInteraction(Guid miniPlayerId)
    {
        var transition = s_inputInteractionHub.TryBegin(
            InputInteractionKind.MiniPlayerContentTransform,
            _workspaceWindowId,
            miniPlayerId,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(RightWheelTabCyclingTimeoutMilliseconds));
        return transition.Accepted ? transition.Current?.Id : null;
    }

    private void HandleYouTubeMiniPlayerGesture(
        YouTubeMiniPlayerRuntime runtime,
        string route)
    {
        _ = ExecuteYouTubeMiniPlayerGestureAsync(runtime, route);
    }

    private Task ExecuteYouTubeMiniPlayerGestureAsync(YouTubeMiniPlayerRuntime runtime, string route)
        => _youTubeMiniPlayerCommandController.ExecuteYouTubeMiniPlayerGestureAsync(runtime, route);

    private Task ExecuteYouTubeMiniPlayerGestureCommandAsync(Guid miniPlayerId, string? command)
        => _youTubeMiniPlayerCommandController.ExecuteYouTubeMiniPlayerGestureCommandAsync(miniPlayerId, command);

    private void HandleYouTubeMiniPlayerSideButton(
        YouTubeMiniPlayerRuntime runtime,
        YouTubeMiniPlayerSideButton sideButton)
    {
        var command = sideButton switch
        {
            YouTubeMiniPlayerSideButton.XButton1 => MiniPlayerCommandProvider.PlayPauseActionId,
            YouTubeMiniPlayerSideButton.XButton2 => MiniPlayerCommandProvider.ToggleMuteActionId,
            _ => null
        };
        if (command is not null)
        {
            _ = ExecuteYouTubeMiniPlayerGestureCommandAsync(runtime.State.Id, command);
        }
    }

    private void CompleteYouTubeMiniPlayerGesture(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.GestureInteractionId is not { } interactionId)
        {
            return;
        }

        runtime.GestureInteractionId = null;
        s_inputInteractionHub.TryComplete(interactionId);
    }

    private void CompleteYouTubeMiniPlayerHoldSeek(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.HoldSeekInteractionId is not { } interactionId)
        {
            return;
        }

        runtime.HoldSeekInteractionId = null;
        s_inputInteractionHub.TryComplete(interactionId);
    }

    private void HandleYouTubeMiniPlayerRightWheel(
        YouTubeMiniPlayerRuntime runtime,
        double delta,
        double anchorX,
        double anchorY)
    {
        if (delta == 0)
        {
            return;
        }

        var interactionId = TryBeginYouTubeMiniPlayerRightWheelInteraction(runtime.State.Id);
        if (interactionId is null)
        {
            CancelYouTubeMiniPlayerRightInput(runtime, "rightWheel");
            return;
        }

        runtime.RightWheelInteractionId = interactionId;
        const double wheelStepThreshold = 100;
        runtime.ContentZoomWheelDelta += delta;
        var stepCount = 0;
        while (runtime.ContentZoomWheelDelta <= -wheelStepThreshold)
        {
            stepCount++;
            runtime.ContentZoomWheelDelta += wheelStepThreshold;
        }

        while (runtime.ContentZoomWheelDelta >= wheelStepThreshold)
        {
            stepCount--;
            runtime.ContentZoomWheelDelta -= wheelStepThreshold;
        }

        if (stepCount == 0
            || !YouTubeMiniPlayerContentTransformState.ChangeZoomAtAnchor(
                runtime.State,
                stepCount,
                anchorX,
                anchorY))
        {
            return;
        }

        runtime.State.UpdatedAt = DateTimeOffset.UtcNow;
        YouTubeMiniPlayerChromeFactory.UpdateContentZoomButton(
            runtime.ToolbarZoomResetButton,
            runtime.State.ContentZoomFactor);
        _ = YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            runtime.State,
            _youTubeMiniPlayerRuntimes,
            YouTubeControlScriptService.BuildSetMiniPlayerContentTransformScript(
                runtime.WebMessageToken,
                runtime.State.ContentZoomFactor,
                runtime.State.ContentPanX,
                runtime.State.ContentPanY),
            showStatus: false,
            message => SetStatus(message, autoClear: true));
        QueueYouTubeMiniPlayerSave();
    }

    private void CompleteYouTubeMiniPlayerRightWheel(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.RightWheelInteractionId is not { } interactionId)
        {
            return;
        }

        runtime.RightWheelInteractionId = null;
        runtime.ContentZoomWheelDelta = 0;
        s_inputInteractionHub.TryComplete(interactionId);
    }

    private void UpdateYouTubeMiniPlayerContentTransform(
        YouTubeMiniPlayerRuntime runtime,
        double zoomFactor,
        double panX,
        double panY)
    {
        if (!YouTubeMiniPlayerContentTransformState.Set(
            runtime.State,
            zoomFactor,
            panX,
            panY))
        {
            return;
        }

        runtime.State.UpdatedAt = DateTimeOffset.UtcNow;
        YouTubeMiniPlayerChromeFactory.UpdateContentZoomButton(
            runtime.ToolbarZoomResetButton,
            runtime.State.ContentZoomFactor);
        QueueYouTubeMiniPlayerSave();
    }

    private void CancelYouTubeMiniPlayerInputScope(Guid miniPlayerId)
    {
        s_inputInteractionHub.CancelForTab(_workspaceWindowId, miniPlayerId);
        if (_youTubeMiniPlayerRuntimes.TryGetValue(miniPlayerId, out var runtime))
        {
            runtime.LocalVideoView?.CancelGesture();
            CancelYouTubeMiniPlayerRightInput(runtime);
        }
    }

    private void CancelYouTubeMiniPlayerRightInput(
        YouTubeMiniPlayerRuntime runtime,
        string inputKind = "all")
    {
        if (inputKind is "all" or "holdSeek")
        {
            runtime.HoldSeekInteractionId = null;
        }
        if (inputKind is "all" or "gesture")
        {
            runtime.GestureInteractionId = null;
        }
        if (inputKind is "all" or "rightWheel")
        {
            runtime.RightWheelInteractionId = null;
            runtime.ContentZoomWheelDelta = 0;
        }

        if (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core)
        {
            return;
        }

        _ = CancelYouTubeMiniPlayerRightInputAsync(core, runtime.WebMessageToken, inputKind);
    }

    private static async Task CancelYouTubeMiniPlayerRightInputAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string webMessageToken,
        string inputKind)
    {
        try
        {
            await core.ExecuteScriptAsync(
                YouTubeControlScriptService.BuildCancelMiniPlayerRightInputScript(
                    webMessageToken,
                    inputKind));
        }
        catch
        {
            // ページ遷移や WebView 破棄と重なった場合はネイティブ側の所有権だけを解放します。
        }
    }

    private async Task<long?> ApplyYouTubeMiniPlayerWatchPageModeAsync(YouTubeMiniPlayerState state)
    {
        return await YouTubeMiniPlayerStateRestoreService.ApplyWatchPageModeAsync(
            state,
            _youTubeMiniPlayerRuntimes,
            () => _isClosingWindow,
            message => SetStatus(message, autoClear: true),
            ApplyDetectedYouTubeMiniPlayerVideoDimensions,
            _settings.GestureMappings,
            _settings.GestureStartThreshold,
            _settings.GestureDirectionThreshold);
    }

    private async Task RefreshMediaWindowGestureSettingsAsync()
    {
        var runtimes = _youTubeMiniPlayerRuntimes.Values.ToArray();
        foreach (var runtime in runtimes)
        {
            runtime.LocalVideoView?.UpdateGestureSettings(_settings);
            if (runtime.IsDisposed
                || YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core)
            {
                continue;
            }

            try
            {
                await core.ExecuteScriptAsync(
                    YouTubeControlScriptService.BuildUpdateMiniPlayerGestureSettingsScript(
                        runtime.WebMessageToken,
                        _settings.GestureMappings,
                        _settings.GestureStartThreshold,
                        _settings.GestureDirectionThreshold));
            }
            catch
            {
                // 遷移中・破棄中の個体は次の表示補正時に最新設定を受け取ります。
            }
        }
    }

    private void ApplyDetectedYouTubeMiniPlayerVideoDimensions(
        YouTubeMiniPlayerState state,
        double videoWidth,
        double videoHeight)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube
            || !double.IsFinite(videoWidth)
            || !double.IsFinite(videoHeight)
            || videoWidth <= 0
            || videoHeight <= 0)
        {
            return;
        }

        var aspectRatio = Math.Clamp(videoWidth / videoHeight, 0.2, 5);
        var isVertical = YouTubeMiniPlayerLayoutService.IsVerticalVideoDimensions(videoWidth, videoHeight);
        if (state.VideoAspectRatio is { } currentRatio
            && Math.Abs(currentRatio - aspectRatio) < 0.001
            && (!isVertical || state.IsShorts))
        {
            return;
        }

        state.IsShorts |= isVertical;
        state.VideoAspectRatio = aspectRatio;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        var targetWidth = isVertical
            ? Math.Min(state.Width, YouTubeMiniPlayerLayoutService.ShortsDefaultWidth)
            : state.Width;
        var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            targetWidth,
            state.Height,
            preferHeight: false,
            aspectRatio);
        state.Width = size.Width;
        state.Height = size.Height;
        if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
        {
            CreateYouTubeMiniPlayerPositionCallbacks().ClampToHost(
                state,
                runtime.Root,
                true);
        }

        QueueYouTubeMiniPlayerSave();
    }

    private async Task ApplyYouTubeMiniPlayerSavedStateAsync(
        YouTubeMiniPlayerState state,
        long expectedDisplayGeneration)
    {
        await YouTubeMiniPlayerStateRestoreService.ApplySavedStateAsync(
            state,
            _youTubeMiniPlayerRuntimes,
            () => _isClosingWindow,
            expectedDisplayGeneration);
        if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
        {
            if (runtime.DisplayHealth.IsCurrentGeneration(expectedDisplayGeneration))
            {
                QueueYouTubeMiniPlayerLayoutVerification(state);
            }

            QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime, immediate: true, force: true);
        }
    }

    private void QueueYouTubeMiniPlayerLayoutVerification(YouTubeMiniPlayerState state)
    {
        if (_isClosingWindow || state.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            return;
        }

        _windowTaskSupervisor.TryRun(
            "youtube-mini-player-layout-verification",
            _ => VerifyYouTubeMiniPlayerLayoutAsync(state));
    }

    private async Task VerifyYouTubeMiniPlayerLayoutAsync(YouTubeMiniPlayerState state)
    {
        if (await YouTubeMiniPlayerStateRestoreService.RequestLayoutVerificationAsync(
                state,
                _youTubeMiniPlayerRuntimes)
            || _isClosingWindow)
        {
            return;
        }

        // 描画プロセス復帰でページ内ブリッジが失われていた場合だけ、既存の補正を再注入します。
        await ApplyYouTubeMiniPlayerWatchPageModeAsync(state);
    }

    private void AddYouTubeMiniPlayerResizeThumbs(
        Grid layout,
        YouTubeMiniPlayerState state,
        FrameworkElement root)
    {
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks();
        YouTubeMiniPlayerResizeThumbService.AddThumbs(
            layout,
            state,
            root,
            _youTubeMiniPlayerRuntimes,
            YouTubeMiniPlayerInteractionCallbackFactory.CreateResizeThumbCallbacks(
                commandCallbacks.BringToFront,
                GetCursorScreenPoint,
                positionCallbacks.ClampToHost,
                positionCallbacks.SyncPopupOffsets,
                QueueYouTubeMiniPlayerSave));
    }

    private void AttachYouTubeMiniPlayerDrag(UIElement dragSurface, YouTubeMiniPlayerState state)
    {
        var overlayVisibilityCallbacks = CreateYouTubeMiniPlayerOverlayVisibilityCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        var commandCallbacks = CreateYouTubeMiniPlayerCommandCallbacks();
        YouTubeMiniPlayerDragWiringService.Attach(
            dragSurface,
            state,
            _youTubeMiniPlayerRuntimes,
            YouTubeMiniPlayerInteractionCallbackFactory.CreateDragWiringCallbacks(
                commandCallbacks.BringToFront,
                GetCursorScreenPoint,
                overlayVisibilityCallbacks.KeepToolbarVisible,
                (currentState, root, ensureVisible) =>
                    ClampAndSnapYouTubeMiniPlayerDuringDrag(
                        currentState,
                        root,
                        ensureVisible,
                        positionCallbacks),
                positionCallbacks.SyncPopupOffsets,
                ReapplyYouTubeMiniPlayerContentTransform,
                QueueYouTubeMiniPlayerSave));
    }

    private void ReapplyYouTubeMiniPlayerContentTransform(YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsDisposed
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube
            || runtime.State.ContentZoomFactor <= YouTubeMiniPlayerContentTransformState.MinimumZoomFactor)
        {
            return;
        }

        _ = YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            runtime.State,
            _youTubeMiniPlayerRuntimes,
            YouTubeControlScriptService.BuildSetMiniPlayerContentTransformScript(
                runtime.WebMessageToken,
                runtime.State.ContentZoomFactor,
                runtime.State.ContentPanX,
                runtime.State.ContentPanY),
            showStatus: false,
            message => SetStatus(message, autoClear: true));
    }

    private void QueueYouTubeMiniPlayerContentTransformReapplyAfterFullScreenChange(
        YouTubeMiniPlayerRuntime runtime)
    {
        if (runtime.IsDisposed
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube
            || runtime.State.ContentZoomFactor <= YouTubeMiniPlayerContentTransformState.MinimumZoomFactor)
        {
            return;
        }

        _ = runtime.TaskbarWindow.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            new Action(() => ReapplyYouTubeMiniPlayerContentTransform(runtime)));
    }

    private void SetYouTubeMiniPlayerInteractiveResize(
        YouTubeMiniPlayerRuntime runtime,
        bool isActive)
    {
        if (runtime.IsDisposed
            || runtime.State.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            return;
        }

        var generation = ++runtime.InteractiveResizeGeneration;
        _ = YouTubeMiniPlayerScriptExecutionService.ExecuteAsync(
            runtime.State,
            _youTubeMiniPlayerRuntimes,
            YouTubeControlScriptService.BuildSetMiniPlayerInteractiveResizeScript(
                runtime.WebMessageToken,
                generation,
                isActive),
            showStatus: false,
            message => SetStatus(message, autoClear: true));
        if (!isActive)
        {
            QueueYouTubeMiniPlayerSizeBasedQualityUpdate(runtime);
        }
    }

    private void ClampYouTubeMiniPlayersToHost()
    {
    }

    private void ClampAndSnapYouTubeMiniPlayerDuringDrag(
        YouTubeMiniPlayerState state,
        FrameworkElement root,
        bool ensureVisible,
        YouTubeMiniPlayerPositionCallbacks positionCallbacks)
    {
        if (ensureVisible)
        {
            positionCallbacks.ClampToHost(state, root, true);
            return;
        }

        if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime)
            && runtime.IsResizing)
        {
            positionCallbacks.ClampToHost(state, root, false);
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var hostOrigin = positionCallbacks.GetHostOrigin();
            var currentBounds = YouTubeMiniPlayerPositionService.GetScreenBounds(state, hostOrigin);
            var snapBounds = GetVisibleTabBrowserWindowScreenBounds()
                .Concat(GetOtherYouTubeMiniPlayerScreenBounds(state.Id, hostOrigin))
                .ToList();
            var snapped = YouTubeMiniPlayerPositionService.CalculateMagneticallySnappedPosition(
                currentBounds,
                GetVirtualScreenBounds(),
                snapBounds);
            if (Math.Abs(snapped.X - currentBounds.Left) >= 0.5
                || Math.Abs(snapped.Y - currentBounds.Top) >= 0.5)
            {
                state.Left = snapped.X - hostOrigin.X;
                state.Top = snapped.Y - hostOrigin.Y;
                state.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        positionCallbacks.SetPosition(state, root);
    }

    private static IReadOnlyList<Rect> GetVisibleTabBrowserWindowScreenBounds()
    {
        var bounds = new List<Rect>();
        foreach (var window in GetOpenMainWindows())
        {
            if (window.WindowState == WindowState.Minimized
                || !YouTubeMiniPlayerPositionService.TryGetHostScreenOrigin(window, out var origin))
            {
                continue;
            }

            bounds.Add(new Rect(origin.X, origin.Y, window.ActualWidth, window.ActualHeight));
        }

        return bounds;
    }

    private IReadOnlyList<Rect> GetOtherYouTubeMiniPlayerScreenBounds(
        Guid movingStateId,
        Point movingHostOrigin)
    {
        var bounds = new List<Rect>();
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows())
        {
            var hostOrigin = movingHostOrigin;
            if (!ReferenceEquals(window, this)
                && !YouTubeMiniPlayerPositionService.TryGetHostScreenOrigin(window.WebHost, out hostOrigin))
            {
                if (window._lastYouTubeMiniPlayerHostScreenOrigin is not { } lastHostOrigin)
                {
                    continue;
                }

                hostOrigin = lastHostOrigin;
            }

            bounds.AddRange(window._youTubeMiniPlayerRuntimes.Values
                .Where(runtime =>
                    runtime.State.Id != movingStateId
                    && !runtime.IsDisposed
                    && !runtime.IsFullScreen
                    && !runtime.State.IsMinimized
                    && runtime.TaskbarWindow.IsVisible)
                .Select(runtime =>
                    YouTubeMiniPlayerPositionService.GetScreenBounds(runtime.State, hostOrigin)));
        }

        return bounds;
    }

    private void UpdateYouTubeMiniPlayersAfterHostSizeChanged()
    {
        QueueFloatingMediaHostLayoutUpdate();
    }

    private void UpdateFloatingMediaAfterHostSizeChanged()
    {
        QueueFloatingMediaHostLayoutUpdate();
    }

    private void UpdateYouTubeMiniPlayersAfterHostLayoutUpdated()
    {
        QueueFloatingMediaHostLayoutUpdate();
    }

    private void UpdateFloatingMediaAfterHostLayoutUpdated()
    {
        QueueFloatingMediaHostLayoutUpdate();
    }

    private void QueueFloatingMediaHostLayoutUpdate()
    {
        if (_isClosingWindow
            || _floatingMediaHostLayoutUpdateQueued
            || _youTubeMiniPlayerRuntimes.Count == 0)
        {
            return;
        }

        _floatingMediaHostLayoutUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _floatingMediaHostLayoutUpdateQueued = false;
            if (!_isClosingWindow && _youTubeMiniPlayerRuntimes.Count > 0)
            {
                KeepYouTubeMiniPlayersStableAfterHostLayoutChange();
            }
        });
    }

    private static Rect GetVirtualScreenBounds()
    {
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private void KeepYouTubeMiniPlayersStableAfterHostLayoutChange()
    {
        if (!TryGetYouTubeMiniPlayerHostScreenOrigin(out var currentOrigin))
        {
            return;
        }

        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        _lastYouTubeMiniPlayerHostScreenOrigin = YouTubeMiniPlayerHostLayoutService.UpdateStablePositionsAfterHostOriginChange(
            _youTubeMiniPlayerRuntimes.Values,
            _lastYouTubeMiniPlayerHostScreenOrigin,
            currentOrigin,
            positionCallbacks.SetPosition,
            positionCallbacks.SyncPopupOffsets);
    }

    private bool TryGetYouTubeMiniPlayerHostScreenOrigin(out Point origin)
    {
        return YouTubeMiniPlayerPositionService.TryGetHostScreenOrigin(WebHost, out origin);
    }

    private Point GetYouTubeMiniPlayerHostScreenOrigin()
    {
        if (TryGetYouTubeMiniPlayerHostScreenOrigin(out var origin))
        {
            _lastYouTubeMiniPlayerHostScreenOrigin = origin;
            return origin;
        }

        return _lastYouTubeMiniPlayerHostScreenOrigin ?? new Point(Left, Top);
    }

    private void RefreshYouTubeMiniPlayerHoverFromCursor(YouTubeMiniPlayerRuntime runtime)
    {
        var overlayVisibilityCallbacks = CreateYouTubeMiniPlayerOverlayVisibilityCallbacks();
        var positionCallbacks = CreateYouTubeMiniPlayerPositionCallbacks();
        var cursorPoint = GetCursorScreenPoint();
        var rootOrigin = runtime.IsFullScreen
            ? YouTubeMiniPlayerPositionService.GetVisualScreenOrigin(runtime.Root)
            : positionCallbacks.GetPopupScreenPosition(runtime.State);
        YouTubeMiniPlayerOverlayVisibilityService.RefreshFromCursor(
            runtime,
            cursorPoint,
            rootOrigin,
            YouTubeMiniPlayerInputService.IsToolbarHotZone,
            YouTubeMiniPlayerInputService.IsTitleBandHotZone,
            overlayVisibilityCallbacks.SetToolbarVisibility,
            overlayVisibilityCallbacks.SetTitleBandVisibility);
    }

    private void RestoreYouTubeMiniPlayerPlaceholders()
        => _youTubeMiniPlayerPersistenceController.RestoreYouTubeMiniPlayerPlaceholders();

    private void RestoreFloatingMediaPlaceholders()
    {
        RestoreYouTubeMiniPlayerPlaceholders();
    }

    private static Task AttachFloatingMediaWindowMemorySnapshotAsync(
        SessionData windowMemory,
        IReadOnlyList<MainWindow> windows)
        => YouTubeMiniPlayerPersistenceController.AttachFloatingMediaWindowMemorySnapshotAsync(
            windowMemory, windows, s_youTubeMiniPlayerAudioSessionCoordinator);

    private static void RestoreFloatingMediaWindowMemoryPlaceholders(
        SessionData windowMemory,
        IReadOnlyDictionary<Guid, MainWindow> restoredWindowsBySavedId)
        => YouTubeMiniPlayerPersistenceController.RestoreFloatingMediaWindowMemoryPlaceholders(
            windowMemory, restoredWindowsBySavedId, GetYouTubeMiniPlayerOwnerWindows);

    private void QueueYouTubeMiniPlayerSave()
        => _youTubeMiniPlayerPersistenceController.QueueYouTubeMiniPlayerSave();

    private void SaveYouTubeMiniPlayersNow()
        => _youTubeMiniPlayerPersistenceController.SaveYouTubeMiniPlayersNow();

    private static YouTubeMiniPlayerState CreatePersistentYouTubeMiniPlayerState(YouTubeMiniPlayerState state)
        => YouTubeMiniPlayerPersistenceController.CreatePersistentState(state, s_youTubeMiniPlayerAudioSessionCoordinator);

    private void SaveFloatingMediaOverlaysNow()
    {
        SaveYouTubeMiniPlayersNow();
    }

    private Task<bool> CaptureYouTubeMiniPlayerStatesForLoadedAsync()
        => _youTubeMiniPlayerPersistenceController.CaptureYouTubeMiniPlayerStatesForLoadedAsync();

    private async Task<bool> CaptureYouTubeMiniPlayerStateAsync(YouTubeMiniPlayerState state)
    {
        return await YouTubeMiniPlayerStateCaptureService.CaptureAsync(
            state,
            _youTubeMiniPlayerRuntimes);
    }

    private IReadOnlyList<YouTubeMiniPlayerWorkspaceState> CaptureWorkspaceMediaOverlayShelfItems()
        => _youTubeMiniPlayerPersistenceController.CaptureWorkspaceMediaOverlayShelfItems();

    private Task<IReadOnlyList<YouTubeMiniPlayerWorkspaceState>> CaptureWorkspaceMediaOverlaysAsync()
        => _youTubeMiniPlayerPersistenceController.CaptureWorkspaceMediaOverlaysAsync();

    private bool TryCloseWorkspaceMediaOverlay(Guid itemId)
    {
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows())
        {
            if (window._youTubeMiniPlayers.All(player => player.Id != itemId))
            {
                continue;
            }

            window.CreateYouTubeMiniPlayerCommandCallbacks().Close(itemId);
            return true;
        }

        return false;
    }

    private Task OpenWorkspaceMediaSetAsync(YouTubeMiniPlayerSetItem item)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => OpenWorkspaceMediaSetAsync(item)).Task.Unwrap();
        }

        return _youTubeMiniPlayerPersistenceController.OpenWorkspaceMediaSetAsync(item);
    }

    private void UpdateYouTubeMiniPlayerStateCaptureTimer()
    {
        YouTubeMiniPlayerTimerService.UpdateStateCaptureTimer(
            _youTubeMiniPlayerStateCaptureTimer,
            _isClosingWindow,
            _youTubeMiniPlayerRuntimes.Values);
    }

    private void DisposeAllYouTubeMiniPlayers()
    {
        CreateYouTubeMiniPlayerCommandCallbacks().DisposeAll();
    }

    private void DisposeFloatingMediaOverlays()
    {
        DisposeAllYouTubeMiniPlayers();
    }

    private static List<YouTubeMiniPlayerState> CreateClosedYouTubeMiniPlayerHistorySnapshot()
    {
        return s_youTubeMiniPlayerClosedHistoryStore.CreatePersistenceSnapshot();
    }

    private static List<YouTubeMiniPlayerState> CreateClosedMediaHistorySnapshot()
    {
        return CreateClosedYouTubeMiniPlayerHistorySnapshot();
    }

    private static bool RemoveOpenYouTubeMiniPlayersFromClosedHistory()
    {
        var openPlayers = GetYouTubeMiniPlayerOwnerWindows()
            .SelectMany(static window => window._youTubeMiniPlayers)
            .ToList();
        return s_youTubeMiniPlayerClosedHistoryStore.RemoveOpenPlayers(openPlayers);
    }

    private static bool TryFindOpenYouTubeMiniPlayer(
        Guid miniPlayerId,
        out MainWindow owner,
        out YouTubeMiniPlayerRuntime runtime)
    {
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows())
        {
            if (window._youTubeMiniPlayerRuntimes.TryGetValue(miniPlayerId, out runtime!)
                && !runtime.IsDisposed)
            {
                owner = window;
                return true;
            }
        }

        owner = null!;
        runtime = null!;
        return false;
    }

    private void RestoreSharedClosedYouTubeMiniPlayerHistory(
        IEnumerable<YouTubeMiniPlayerState>? closedPlayers)
    {
        if (_isPrivateWindow)
        {
            return;
        }

        s_youTubeMiniPlayerClosedHistoryStore.Replace(closedPlayers);

        RefreshClosedTabHistoryViews();
    }

    private void RestoreSupplementalClosedItemHistory(SessionData session)
    {
        RestoreSharedClosedWindowHistory(session.ClosedWindows);
        try
        {
            RestoreSharedClosedYouTubeMiniPlayerHistory(session.ClosedYouTubeMiniPlayers);
        }
        catch (Exception ex)
        {
            _sessionRestorationController.RecordWarning();
            AppendSessionDiagnostic("closed-mini-player-history-restore-failed", ex);
            RefreshClosedTabHistoryView();
        }
    }

    private static bool HasClosedYouTubeMiniPlayer()
    {
        return s_youTubeMiniPlayerClosedHistoryStore.HasAny;
    }

    private static IReadOnlyList<YouTubeMiniPlayerState> GetClosedYouTubeMiniPlayerHistory()
    {
        return s_youTubeMiniPlayerClosedHistoryStore.CreatePresentationSnapshot();
    }

    private void UpdateYouTubeMiniPlayerRestoreClosedButtonState()
        => _youTubeMiniPlayerListViewController.UpdateYouTubeMiniPlayerRestoreClosedButtonState();

    private void YouTubeMiniPlayerListButton_Click(object sender, RoutedEventArgs e)
    {
        YouTubeMiniPlayerListPopup.IsOpen = !YouTubeMiniPlayerListPopup.IsOpen;
    }

    private void YouTubeMiniPlayerArrangeMenuButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = false;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        if (YouTubeMiniPlayerArrangePopup.IsOpen)
        {
            YouTubeMiniPlayerArrangePopup.IsOpen = false;
            return;
        }

        UpdateYouTubeMiniPlayerArrangePopupPlacement();
        YouTubeMiniPlayerArrangePopup.IsOpen = true;
    }

    private void YouTubeMiniPlayerGlobalActionsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        YouTubeMiniPlayerArrangePopup.IsOpen = false;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        YouTubeMiniPlayerItemMorePopup.IsOpen = false;
        YouTubeMiniPlayerGlobalActionsPopup.IsOpen = !YouTubeMiniPlayerGlobalActionsPopup.IsOpen;
    }

    private void YouTubeMiniPlayerOpenLocalVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = SelectLocalVideoFile();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            OpenLocalVideoMiniPlayer(selectedPath);
        }
    }

    private void YouTubeMiniPlayerLocalVideo_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedLocalVideoPaths(e.Data).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void YouTubeMiniPlayerLocalVideo_Drop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedLocalVideoPaths(e.Data);
        foreach (var path in paths)
        {
            OpenLocalVideoMiniPlayer(path);
        }

        e.Handled = paths.Count > 0;
    }

    private async void YouTubeMiniPlayerListPopup_Opened(object sender, EventArgs e)
        => await _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopup_Opened(sender, e);

    private async void YouTubeMiniPlayerListPopup_Closed(object sender, EventArgs e)
        => await _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopup_Closed(sender, e);

    private void YouTubeMiniPlayerListPopupDragHandle_DragStarted(
        object sender,
        DragStartedEventArgs e)
        => _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopupDragHandle_DragStarted(sender, e);

    private void YouTubeMiniPlayerListPopupDragHandle_DragDelta(
        object sender,
        DragDeltaEventArgs e)
        => _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopupDragHandle_DragDelta(sender, e);

    private void YouTubeMiniPlayerListPopupDragHandle_DragCompleted(
        object sender,
        DragCompletedEventArgs e)
        => _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopupDragHandle_DragCompleted(sender, e);

    private void YouTubeMiniPlayerListPopupDragHandle_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
        => _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopupDragHandle_MouseDoubleClick(sender, e);

    private void YouTubeMiniPlayerListPopupRoot_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
        => _youTubeMiniPlayerListPopupController?.YouTubeMiniPlayerListPopupRoot_SizeChanged(sender, e);

    private void QueueYouTubeMiniPlayerListPopupPositionClamp()
        => _youTubeMiniPlayerListPopupController.QueueYouTubeMiniPlayerListPopupPositionClamp();

    private void ClampYouTubeMiniPlayerListPopupPosition()
        => _youTubeMiniPlayerListPopupController.ClampYouTubeMiniPlayerListPopupPosition();

    internal static Vector CalculateYouTubeMiniPlayerListPopupClampAdjustment(
        Rect workArea,
        Rect popupBounds,
        double edgeMargin)
        => YouTubeMiniPlayerListPopupController.CalculateYouTubeMiniPlayerListPopupClampAdjustment(workArea, popupBounds, edgeMargin);

    internal static Point CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
        Rect popupDeviceBounds,
        Point? preferredDevicePoint)
        => YouTubeMiniPlayerListPopupController.CalculateYouTubeMiniPlayerListPopupWorkAreaProbePoint(
            popupDeviceBounds,
            preferredDevicePoint);

    private void UpdateYouTubeMiniPlayerArrangePopupPlacement()
        => _youTubeMiniPlayerListPopupController.UpdateYouTubeMiniPlayerArrangePopupPlacement();

    internal static PlacementMode CalculateYouTubeMiniPlayerArrangePopupPlacement(
        Rect workArea,
        Rect listPopupBounds,
        double arrangePopupWidth,
        double gap,
        double edgeMargin)
        => YouTubeMiniPlayerListPopupController.CalculateYouTubeMiniPlayerArrangePopupPlacement(workArea, listPopupBounds, arrangePopupWidth, gap, edgeMargin);

    private void YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
        => _youTubeMiniPlayerListPopupController.YouTubeMiniPlayerListPopupContent_PreviewMouseLeftButtonDown(sender, e);

    private Task RefreshYouTubeMiniPlayerListAsync(bool capturePlaybackState)
        => _youTubeMiniPlayerListViewController.RefreshYouTubeMiniPlayerListAsync(capturePlaybackState);

    private void BeginYouTubeMiniPlayerThumbnailLoads()
        => _youTubeMiniPlayerListViewController.BeginYouTubeMiniPlayerThumbnailLoads();

    private void CancelYouTubeMiniPlayerThumbnailLoads()
        => _youTubeMiniPlayerListViewController.CancelYouTubeMiniPlayerThumbnailLoads();

    private void YouTubeMiniPlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerList_SelectionChanged(sender, e);

    private void YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonDown(sender, e);

    private void YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerListItem_PreviewMouseLeftButtonUp(sender, e);

    private void YouTubeMiniPlayerList_PreviewMouseMove(object sender, MouseEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerList_PreviewMouseMove(sender, e);

    private void YouTubeMiniPlayerList_PreviewDragOver(object sender, DragEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerList_PreviewDragOver(sender, e);

    private void YouTubeMiniPlayerList_Drop(object sender, DragEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerList_Drop(sender, e);

    private void ClearYouTubeMiniPlayerListDropCue()
        => _youTubeMiniPlayerListViewController.ClearYouTubeMiniPlayerListDropCue();

    private void ResetYouTubeMiniPlayerListDragState()
        => _youTubeMiniPlayerListViewController.ResetYouTubeMiniPlayerListDragState();

    private void YouTubeMiniPlayerListItem_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerListItem_MouseDoubleClick(sender, e);

    private void YouTubeMiniPlayerListItem_MouseEnter(object sender, MouseEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerListItem_MouseEnter(sender, e);

    private void YouTubeMiniPlayerListItem_MouseLeave(object sender, MouseEventArgs e)
        => _youTubeMiniPlayerListViewController.YouTubeMiniPlayerListItem_MouseLeave(sender, e);

    private void StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft(
        ListBoxItem? itemContainer = null)
        => _youTubeMiniPlayerListViewController.StopYouTubeMiniPlayerHoverAudioPreviewIfPointerLeft(itemContainer);

    private void ScheduleYouTubeMiniPlayerHoverAudioPreview(Guid targetId)
        => _youTubeMiniPlayerAudioController.ScheduleYouTubeMiniPlayerHoverAudioPreview(targetId);

    private void CancelPendingYouTubeMiniPlayerHoverAudioPreview()
        => _youTubeMiniPlayerAudioController.CancelPendingYouTubeMiniPlayerHoverAudioPreview();

    private Task StopYouTubeMiniPlayerHoverAudioPreviewAsync()
        => _youTubeMiniPlayerAudioController.StopYouTubeMiniPlayerHoverAudioPreviewAsync();

    private void UpdateYouTubeMiniPlayerAudioFocusBar()
        => _youTubeMiniPlayerAudioController.UpdateYouTubeMiniPlayerAudioFocusBar();

    private static void UpdateYouTubeMiniPlayerAudioIndicatorsForAllWindows()
    {
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows())
        {
            window._youTubeMiniPlayerAudioController.UpdateIndicators();
        }
    }

    internal static string GetYouTubeMiniPlayerAudioFocusTargetText(
        YouTubeMiniPlayerAudioFocusMode? mode)
        => YouTubeMiniPlayerAudioController.GetYouTubeMiniPlayerAudioFocusTargetText(mode);

    private void UpdateYouTubeMiniPlayerListWindowHighlights()
    {
        var windows = GetYouTubeMiniPlayerOwnerWindows();
        var listWindows = windows
            .Where(static window => window.YouTubeMiniPlayerListPopup.IsOpen)
            .ToList();
        var selectedIds = listWindows
            .SelectMany(window => window.YouTubeMiniPlayerList.SelectedItems
                .OfType<YouTubeMiniPlayerListItem>()
                .Select(static item => item.State.Id))
            .ToHashSet();
        var hoveredIds = listWindows
            .Where(static window => window._youTubeMiniPlayerListInteractionCoordinator.HoveredItemId.HasValue)
            .Select(static window => window._youTubeMiniPlayerListInteractionCoordinator.HoveredItemId!.Value)
            .ToHashSet();
        foreach (var window in windows)
        {
            foreach (var runtime in window._youTubeMiniPlayerRuntimes.Values)
            {
                ApplyYouTubeMiniPlayerListWindowHighlight(
                    runtime,
                    selectedIds.Contains(runtime.State.Id),
                    hoveredIds.Contains(runtime.State.Id));
            }
        }
    }

    private void ClearYouTubeMiniPlayerListWindowHighlights()
        => _youTubeMiniPlayerListViewController.ClearYouTubeMiniPlayerListWindowHighlights();

    private static void ApplyYouTubeMiniPlayerListWindowHighlight(
        YouTubeMiniPlayerRuntime runtime,
        bool isSelected,
        bool isHovered)
    {
        var highlightBorder = runtime.ListHighlightBorder;
        if (runtime.IsFullScreen || (!isSelected && !isHovered))
        {
            highlightBorder.Visibility = Visibility.Collapsed;
            return;
        }

        highlightBorder.BorderBrush = isHovered
            ? YouTubeMiniPlayerListHoveredWindowBrush
            : YouTubeMiniPlayerListSelectedWindowBrush;
        highlightBorder.BorderThickness = new Thickness(isHovered ? 5 : 4);
        highlightBorder.Visibility = Visibility.Visible;
    }

    private CommandInvocationContext CreateMiniPlayerCommandContext(Guid runtimeId)
        => YouTubeMiniPlayerCommandController.CreateMiniPlayerCommandContext(runtimeId);

    private Task ExecuteMiniPlayerCommandAsync(
        string actionId,
        YouTubeMiniPlayerListItem item,
        object? parameter = null)
        => _youTubeMiniPlayerCommandController.ExecuteMiniPlayerCommandAsync(actionId, item.State.Id, parameter);

    private async void YouTubeMiniPlayerItemMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.ToggleMinimizedActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteYouTubeMiniPlayerListPlayPauseAsync(item);
    }

    private async void YouTubeMiniPlayerItemThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteYouTubeMiniPlayerListPlayPauseAsync(item);
    }

    private Task ExecuteYouTubeMiniPlayerListPlayPauseAsync(YouTubeMiniPlayerListItem item)
        => _youTubeMiniPlayerCommandController.ExecuteYouTubeMiniPlayerListPlayPauseAsync(item.Owner, item.State);

    private async void YouTubeMiniPlayerItemMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.ToggleMuteActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemMuteButton_PreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemMuteButton_PreviewMouseRightButtonUp(sender, e);

    private void YouTubeMiniPlayerItemMoreButton_Click(object sender, RoutedEventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemMoreButton_Click(sender, e);

    private void YouTubeMiniPlayerItemMorePopup_Closed(object sender, EventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemMorePopup_Closed(sender, e);

    private async void YouTubeMiniPlayerItemSoloAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.SoloAudioActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemSoloPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.SoloPlaybackActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemVolumeButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemVolumeButton_Click(sender, e);

    private async void YouTubeMiniPlayerAudioBoostButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerAudioBoostButton_Click(sender, e);

    private void YouTubeMiniPlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_youTubeMiniPlayerListActionsController is null)
        {
            if (YouTubeMiniPlayerVolumeValueText is not null)
                YouTubeMiniPlayerVolumeValueText.Text = $"{e.NewValue:0}%";
            return;
        }
        _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerVolumeSlider_ValueChanged(sender, e);
    }

    private async void YouTubeMiniPlayerVolumePopup_Closed(object sender, EventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerVolumePopup_Closed(sender, e);

    private async void YouTubeMiniPlayerItemSeekBackButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemSeekBackButton_Click(sender, e);

    private async void YouTubeMiniPlayerItemSeekForwardButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemSeekForwardButton_Click(sender, e);

    private async void YouTubeMiniPlayerItemLiveEdgeButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemLiveEdgeButton_Click(sender, e);

    private async void YouTubeMiniPlayerItemLoopButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemLoopButton_Click(sender, e);

    private void YouTubeMiniPlayerItemAbRepeatMenuButton_Click(object sender, RoutedEventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemAbRepeatMenuButton_Click(sender, e);

    private async void YouTubeMiniPlayerAbRepeatActionButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerAbRepeatActionButton_Click(sender, e);

    private void YouTubeMiniPlayerAbRepeatPopup_Closed(object sender, EventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerAbRepeatPopup_Closed(sender, e);

    private void YouTubeMiniPlayerItemSpeedMenuButton_Click(object sender, RoutedEventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemSpeedMenuButton_Click(sender, e);

    private async void YouTubeMiniPlayerItemSpeedButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemSpeedButton_Click(sender, e);

    private void YouTubeMiniPlayerSpeedPopup_Closed(object sender, EventArgs e)
        => _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerSpeedPopup_Closed(sender, e);

    private async void YouTubeMiniPlayerItemReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.ReloadActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemSourceTabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.ActivateSourceTabActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemTopmostButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YouTubeMiniPlayerListItem item)
        {
            return;
        }

        e.Handled = true;
        await ExecuteMiniPlayerCommandAsync(
            MiniPlayerCommandProvider.ToggleTopmostActionId,
            item);
    }

    private async void YouTubeMiniPlayerItemCloseButton_Click(object sender, RoutedEventArgs e)
        => await _youTubeMiniPlayerListActionsController.YouTubeMiniPlayerItemCloseButton_Click(sender, e);

    private async void YouTubeMiniPlayerRestoreClosedButton_Click(object sender, RoutedEventArgs e)
    {
        await RestoreClosedYouTubeMiniPlayerAsync();
    }

    private Task RestoreClosedYouTubeMiniPlayerAsync(Guid? miniPlayerId = null)
        => _youTubeMiniPlayerLifetimeController.RestoreClosedAsync(miniPlayerId);

    private async void YouTubeMiniPlayerMinimizeRestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        var states = GetOpenYouTubeMiniPlayerStates();
        if (states.Count == 0)
        {
            return;
        }

        await ExecuteRegisteredCommandAsync(
            states.All(static state => state.IsMinimized)
                ? FeatureCommandProvider.RestoreAllMiniPlayersActionId
                : FeatureCommandProvider.MinimizeAllMiniPlayersActionId);
    }

    private async void YouTubeMiniPlayerMuteAllToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var states = GetOpenYouTubeMiniPlayerStates();
        if (states.Count == 0)
        {
            return;
        }

        await ExecuteRegisteredCommandAsync(
            states.All(static state => state.IsMuted)
                ? FeatureCommandProvider.UnmuteAllMiniPlayersActionId
                : FeatureCommandProvider.MuteAllMiniPlayersActionId);
    }

    private async void YouTubeMiniPlayerPlayPauseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldPlayAllLoadedYouTubeMiniPlayers())
        {
            await PlayAllLoadedYouTubeMiniPlayersAsync();
        }
        else
        {
            await ExecuteRegisteredCommandAsync(
                FeatureCommandProvider.PauseAllMiniPlayersActionId);
        }
    }

    private void UpdateYouTubeMiniPlayerPlayPauseAllButton()
        => _youTubeMiniPlayerListViewController.UpdateYouTubeMiniPlayerPlayPauseAllButton();

    private void UpdateYouTubeMiniPlayerListToolbarState(
        IReadOnlyList<YouTubeMiniPlayerListItem> items)
        => _youTubeMiniPlayerListViewController.UpdateYouTubeMiniPlayerListToolbarState(items);

    private static IReadOnlyList<YouTubeMiniPlayerState> GetOpenYouTubeMiniPlayerStates()
    {
        return GetYouTubeMiniPlayerOwnerWindows()
            .SelectMany(static window => window._youTubeMiniPlayers)
            .ToList();
    }

    private static bool IsYouTubeMiniPlayerListItemLoaded(YouTubeMiniPlayerListItem item)
    {
        return item.Owner._youTubeMiniPlayerRuntimes.TryGetValue(item.State.Id, out var runtime)
            && !runtime.IsDisposed
            && (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                || runtime.LocalVideoView?.Controller?.IsLoaded == true);
    }

    private bool ShouldPlayAllLoadedYouTubeMiniPlayers()
        => _youTubeMiniPlayerCommandController.ShouldPlayAllLoadedYouTubeMiniPlayers();

    private Task PlayAllLoadedYouTubeMiniPlayersAsync()
        => _youTubeMiniPlayerCommandController.PlayAllLoadedYouTubeMiniPlayersAsync();

    private Task PauseAllYouTubeMiniPlayersAsync()
        => _youTubeMiniPlayerCommandController.PauseAllYouTubeMiniPlayersAsync();

    private Task SetAllYouTubeMiniPlayersMutedAsync(bool isMuted)
        => _youTubeMiniPlayerCommandController.SetAllYouTubeMiniPlayersMutedAsync(isMuted);

    private async Task PlaySelectedYouTubeMiniPlayersOnlyAsync()
    {
        await ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(
            YouTubeMiniPlayerAudioFocusMode.SelectedPlayback,
            "選択したミニプレイヤーだけ再生しています");
    }

    private async Task PauseOutsideSelectedYouTubeMiniPlayersAsync()
    {
        await ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(
            YouTubeMiniPlayerAudioFocusMode.PauseOutsideSelection,
            "選択外のミニプレイヤーを一時停止しました");
    }

    private async Task MuteOutsideSelectedYouTubeMiniPlayersAsync()
    {
        await ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(
            YouTubeMiniPlayerAudioFocusMode.MuteOutsideSelection,
            "選択外のミニプレイヤーを一時ミュートしました");
    }

    private Task ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(
        YouTubeMiniPlayerAudioFocusMode mode,
        string successMessage)
        => _youTubeMiniPlayerAudioController.ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(mode, successMessage);

    private Task ExecuteYouTubeMiniPlayerAudioFocusAsync(
        YouTubeMiniPlayerAudioFocusMode mode,
        IReadOnlyCollection<Guid> targetIds,
        string successMessage)
        => _youTubeMiniPlayerAudioController.ExecuteYouTubeMiniPlayerAudioFocusAsync(mode, targetIds, successMessage);

    private Task RestoreYouTubeMiniPlayerAudioFocusAsync()
        => _youTubeMiniPlayerAudioController.RestoreYouTubeMiniPlayerAudioFocusAsync();

    private async void YouTubeMiniPlayerPlaySelectedOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.PlaySelectedMiniPlayersOnlyActionId);
    }

    private async void YouTubeMiniPlayerPauseOutsideSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.PauseOutsideSelectedMiniPlayersActionId);
    }

    private async void YouTubeMiniPlayerMuteOutsideSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.MuteOutsideSelectedMiniPlayersActionId);
    }

    private async void YouTubeMiniPlayerRestoreAudioFocusButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.RestoreMiniPlayerAudioFocusActionId);
    }

    private void SetAllYouTubeMiniPlayersMinimized(bool isMinimized)
        => _youTubeMiniPlayerCommandController.SetAllYouTubeMiniPlayersMinimized(isMinimized);

    private async void YouTubeMiniPlayerCloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.CloseAllMiniPlayersActionId);
    }

    private void CloseAllYouTubeMiniPlayers()
        => _youTubeMiniPlayerCommandController.CloseAllYouTubeMiniPlayers();

    private async void YouTubeMiniPlayerArrangeGridButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersGridActionId);
    }

    private void YouTubeMiniPlayerArrangeHorizontalButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleYouTubeMiniPlayerLinearPlacementPopup(
            sender,
            e,
            YouTubeMiniPlayerArrangeMode.Horizontal);
    }

    private void YouTubeMiniPlayerArrangeVerticalButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleYouTubeMiniPlayerLinearPlacementPopup(
            sender,
            e,
            YouTubeMiniPlayerArrangeMode.Vertical);
    }

    private void ToggleYouTubeMiniPlayerLinearPlacementPopup(
        object sender,
        RoutedEventArgs e,
        YouTubeMiniPlayerArrangeMode mode)
    {
        e.Handled = true;
        if (sender is not Button button)
        {
            return;
        }

        var closeOnly = YouTubeMiniPlayerLinearPlacementPopup.IsOpen
            && _youTubeMiniPlayerArrangementSessionCoordinator.LinearPlacementPopupMode == mode;
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = false;
        if (closeOnly)
        {
            return;
        }

        _youTubeMiniPlayerArrangementSessionCoordinator.OpenLinearPlacementPopup(mode);
        YouTubeMiniPlayerLinearPlacementPopup.PlacementTarget = button;
        UpdateYouTubeMiniPlayerLinearPlacementPopup(mode);
        YouTubeMiniPlayerLinearPlacementPopup.IsOpen = true;
    }

    private async void YouTubeMiniPlayerLinearPlacementOptionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        await PreviewYouTubeMiniPlayerLinearPlacementAsync(sender);
    }

    private async void YouTubeMiniPlayerLinearPlacementOptionButton_MouseEnter(
        object sender,
        MouseEventArgs e)
    {
        await PreviewYouTubeMiniPlayerLinearPlacementAsync(sender);
    }

    private async Task PreviewYouTubeMiniPlayerLinearPlacementAsync(object sender)
    {
        if (sender is not Button { Tag: string placementText }
            || !int.TryParse(placementText, out var placementNumber)
            || _youTubeMiniPlayerArrangementSessionCoordinator.LinearPlacementPopupMode is not { } mode)
        {
            return;
        }

        var placement = placementNumber switch
        {
            1 => YouTubeMiniPlayerLinearPlacement.First,
            3 => YouTubeMiniPlayerLinearPlacement.Third,
            _ => YouTubeMiniPlayerLinearPlacement.Second
        };
        if (!_youTubeMiniPlayerArrangementSessionCoordinator.TrySetLinearPlacement(mode, placement))
        {
            return;
        }

        if (_youTubeMiniPlayerArrangementPreviewWindow?.Plan is { } currentPlan
            && currentPlan.Mode == mode
            && currentPlan.LinearPlacement == placement)
        {
            return;
        }

        await ExecuteRegisteredCommandAsync(
            mode == YouTubeMiniPlayerArrangeMode.Horizontal
                ? FeatureCommandProvider.ArrangeMiniPlayersHorizontalActionId
                : FeatureCommandProvider.ArrangeMiniPlayersVerticalActionId);
    }

    private void YouTubeMiniPlayerLinearPlacementPopup_Closed(object? sender, EventArgs e)
    {
        _youTubeMiniPlayerArrangementSessionCoordinator.CloseLinearPlacementPopup();
        YouTubeMiniPlayerLinearPlacementPopup.PlacementTarget = null;
    }

    private void UpdateYouTubeMiniPlayerLinearPlacementPopup(YouTubeMiniPlayerArrangeMode mode)
    {
        var isHorizontal = mode == YouTubeMiniPlayerArrangeMode.Horizontal;
        YouTubeMiniPlayerLinearPlacementFirstButton.Content = isHorizontal ? "上段" : "左側";
        YouTubeMiniPlayerLinearPlacementSecondButton.Content = isHorizontal ? "中段" : "中央";
        YouTubeMiniPlayerLinearPlacementThirdButton.Content = isHorizontal ? "下段" : "右側";
    }

    private async void YouTubeMiniPlayerArrangeRandomButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersRandomActionId);
    }

    private async void YouTubeMiniPlayerArrangeFocusButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersFocusActionId);
    }

    private async void YouTubeMiniPlayerArrangeMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersMonitorActionId);
    }

    private async void YouTubeMiniPlayerArrangeNormalAndShortsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersNormalAndShortsActionId);
    }

    private async void YouTubeMiniPlayerArrangeChatButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.ArrangeMiniPlayersChatActionId);
    }

    IReadOnlyList<IYouTubeMiniPlayerArrangementOwner> IYouTubeMiniPlayerArrangementHost.GetOwners()
        => GetYouTubeMiniPlayerOwnerWindows();

    IReadOnlyList<IYouTubeMiniPlayerArrangementItem> IYouTubeMiniPlayerArrangementHost.CaptureItems()
    {
        var existingItems = _youTubeMiniPlayerListItems.ToDictionary(static item => (item.Owner, item.State.Id));
        return GetYouTubeMiniPlayerOwnerWindows()
            .SelectMany(owner => owner._youTubeMiniPlayers
                .Where(state => owner._youTubeMiniPlayerRuntimes.ContainsKey(state.Id))
                .Select(state => existingItems.TryGetValue((owner, state.Id), out var existing)
                    ? existing : new YouTubeMiniPlayerListItem(owner, state)))
            .ToList();
    }

    IReadOnlyList<Guid> IYouTubeMiniPlayerArrangementHost.GetSelectedIds()
        => YouTubeMiniPlayerList.SelectedItems.OfType<YouTubeMiniPlayerListItem>().Select(static item => item.State.Id).ToList();

    bool IYouTubeMiniPlayerArrangementHost.IncludeMinimized => YouTubeMiniPlayerArrangeIncludeMinimizedCheckBox.IsChecked == true;
    Rect IYouTubeMiniPlayerArrangementHost.GetWorkArea() => GetActiveDisplayWorkArea();

    void IYouTubeMiniPlayerArrangementHost.EnsureListOpen()
    {
        if (!YouTubeMiniPlayerListPopup.IsOpen)
        {
            YouTubeMiniPlayerListPopup.IsOpen = true;
        }
    }

    IYouTubeMiniPlayerArrangementPreview? IYouTubeMiniPlayerArrangementHost.Preview
    {
        get => _youTubeMiniPlayerArrangementPreviewWindow;
        set => _youTubeMiniPlayerArrangementPreviewWindow = (YouTubeMiniPlayerArrangementPreviewWindow?)value;
    }

    IYouTubeMiniPlayerArrangementPreview IYouTubeMiniPlayerArrangementHost.CreatePreview(
        YouTubeMiniPlayerArrangementPlan plan, IReadOnlyList<YouTubeMiniPlayerArrangementPreviewItem> items)
        => new YouTubeMiniPlayerArrangementPreviewWindow(plan, items);

    void IYouTubeMiniPlayerArrangementHost.ShowPreviewBar(bool canEdit, string text)
    {
        YouTubeMiniPlayerArrangementPreviewEditButton.IsEnabled = canEdit;
        YouTubeMiniPlayerArrangementPreviewResetButton.IsEnabled = canEdit;
        YouTubeMiniPlayerArrangementPreviewEditButton.Content = "調整";
        YouTubeMiniPlayerArrangementPreviewEditButton.ToolTip = canEdit
            ? "予告中の動画枠を移動・四隅リサイズ" : "チャット付き配置の手動調整は未対応です";
        YouTubeMiniPlayerArrangementPreviewText.Text = text;
        YouTubeMiniPlayerArrangementPreviewBar.Visibility = Visibility.Visible;
    }

    void IYouTubeMiniPlayerArrangementHost.HidePreviewBar(bool clearText)
    {
        YouTubeMiniPlayerArrangementPreviewBar.Visibility = Visibility.Collapsed;
        if (clearText)
        {
            YouTubeMiniPlayerArrangementPreviewText.Text = string.Empty;
            YouTubeMiniPlayerArrangementPreviewEditButton.Content = "調整";
        }
    }

    void IYouTubeMiniPlayerArrangementHost.SetStatus(string message) => SetStatus(message, autoClear: true);
    Task IYouTubeMiniPlayerArrangementHost.RefreshListAsync() => RefreshYouTubeMiniPlayerListAsync(capturePlaybackState: false);
    Guid IYouTubeMiniPlayerArrangementOwner.WindowId => _workspaceWindowId;
    IReadOnlyList<YouTubeMiniPlayerState> IYouTubeMiniPlayerArrangementOwner.States => _youTubeMiniPlayers;
    bool IYouTubeMiniPlayerArrangementOwner.HasRuntime(Guid id) => _youTubeMiniPlayerRuntimes.ContainsKey(id);
    IYouTubeMiniPlayerArrangementItem IYouTubeMiniPlayerArrangementOwner.CreateItem(YouTubeMiniPlayerState state)
        => new YouTubeMiniPlayerListItem(this, state);

    YouTubeMiniPlayerArrangementCandidate IYouTubeMiniPlayerArrangementOwner.CreateCandidate(YouTubeMiniPlayerState state)
    {
        var runtime = _youTubeMiniPlayerRuntimes[state.Id];
        return new(state.Id, state.IsMinimized, runtime.IsFullScreen, state.IsShorts, state.Width,
            state.Height, state.VideoAspectRatio, _youTubeLiveChatController.ContainsWindow(state.Id));
    }

    YouTubeMiniPlayerArrangementPreviewValidationItem IYouTubeMiniPlayerArrangementOwner.CaptureValidation(Guid id, bool requireRuntime)
    {
        YouTubeMiniPlayerRuntime runtime;
        if (requireRuntime)
        {
            runtime = _youTubeMiniPlayerRuntimes[id];
        }
        else if (!_youTubeMiniPlayerRuntimes.TryGetValue(id, out runtime!))
        {
            return new(id, null, false, 0, default, null, null, null);
        }
        _youTubeLiveChatController.TryGetWindow(id, out var chat);
        return new(id, runtime, runtime.IsFullScreen, (int)runtime.TaskbarWindow.WindowState,
            YouTubeMiniPlayerArrangementController.ToBounds(GetYouTubeMiniPlayerArrangementWindowBounds(runtime.TaskbarWindow)),
            chat, chat is null ? null : (int)chat.WindowState,
            chat is null ? null : YouTubeMiniPlayerArrangementController.ToBounds(GetYouTubeMiniPlayerArrangementWindowBounds(chat)));
    }

    YouTubeMiniPlayerArrangementSnapshot IYouTubeMiniPlayerArrangementOwner.CaptureArrangement(YouTubeMiniPlayerState state)
        => CreateYouTubeMiniPlayerArrangementSnapshot(state);
    Point IYouTubeMiniPlayerArrangementOwner.GetHostScreenOrigin() => GetYouTubeMiniPlayerHostScreenOrigin();

    void IYouTubeMiniPlayerArrangementOwner.ApplyPlayer(YouTubeMiniPlayerState state)
    {
        if (_youTubeMiniPlayerRuntimes.TryGetValue(state.Id, out var runtime))
        {
            CreateYouTubeMiniPlayerCommandCallbacks().ApplyMinimizedState(runtime, false);
        }
    }

    IYouTubeMiniPlayerArrangementChat? IYouTubeMiniPlayerArrangementOwner.FindChat(Guid id)
        => _youTubeLiveChatController.TryGetWindow(id, out var chat) ? new ArrangementChatAdapter(chat) : null;

    private sealed class ArrangementChatAdapter(YouTubeLiveChatWindow window) : IYouTubeMiniPlayerArrangementChat
    {
        public void Apply(Rect bounds, bool isDocked, YouTubeLiveChatDockSide dockSide)
            => window.ApplyArrangement(bounds, isDocked, dockSide);
        public void Synchronize() => window.SynchronizeWithSourceWindow();
    }

    bool IYouTubeMiniPlayerArrangementOwner.IsPlaybackLoaded(Guid id)
        => _youTubeMiniPlayerRuntimes.TryGetValue(id, out var runtime)
            && (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                || runtime.LocalVideoView?.Controller?.IsLoaded == true);
    Task<bool> IYouTubeMiniPlayerArrangementOwner.SeekToRatioAsync(YouTubeMiniPlayerState state, double ratio)
        => YouTubeMiniPlayerPlaybackCommandService.SeekToRatioAsync(state, _youTubeMiniPlayerRuntimes, ratio);
    void IYouTubeMiniPlayerArrangementOwner.QueueSave() => QueueYouTubeMiniPlayerSave();
    bool IYouTubeMiniPlayerArrangementOwner.CancelPreview(bool showStatus) => CancelYouTubeMiniPlayerArrangementPreview(showStatus);

    private void BeginYouTubeMiniPlayerArrangementPreview(YouTubeMiniPlayerArrangeMode mode)
        => _youTubeMiniPlayerArrangementController.BeginPreview(mode);

    private async void YouTubeMiniPlayerArrangementPreviewApplyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        await ApplyYouTubeMiniPlayerArrangementPreviewAsync();
    }

    private void YouTubeMiniPlayerArrangementPreviewEditButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        if (_youTubeMiniPlayerArrangementPreviewWindow is not { } previewWindow
            || !YouTubeMiniPlayerArrangementPreviewEditButton.IsEnabled)
        {
            return;
        }

        previewWindow.SetEditMode(!previewWindow.IsEditMode);
        YouTubeMiniPlayerArrangementPreviewEditButton.Content =
            previewWindow.IsEditMode ? "調整終了" : "調整";
        YouTubeMiniPlayerArrangementPreviewText.Text = previewWindow.IsEditMode
            ? "配置予告を調整中: 移動ハンドルで移動、四隅でリサイズ"
            : $"配置予告: {GetArrangementModeText(
                previewWindow.Plan.Mode,
                previewWindow.Plan.LinearPlacement)} / "
                + $"{previewWindow.Plan.Targets.Count}件（Escでキャンセル）";
    }

    private void YouTubeMiniPlayerArrangementPreviewResetButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        if (_youTubeMiniPlayerArrangementPreviewWindow is not { } previewWindow
            || !YouTubeMiniPlayerArrangementPreviewResetButton.IsEnabled)
        {
            return;
        }

        previewWindow.ResetPlayerBounds();
        SetStatus("配置予告を最初の配置案へ戻しました", autoClear: true);
    }

    private void YouTubeMiniPlayerArrangementPreviewCancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        e.Handled = true;
        CancelYouTubeMiniPlayerArrangementPreview(showStatus: true);
    }

    private Task ApplyYouTubeMiniPlayerArrangementPreviewAsync()
        => _youTubeMiniPlayerArrangementController.ApplyPreviewAsync();

    private static Rect GetYouTubeMiniPlayerArrangementWindowBounds(Window window)
    {
        if (window.WindowState != WindowState.Normal && !window.RestoreBounds.IsEmpty)
        {
            return window.RestoreBounds;
        }

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        return new Rect(window.Left, window.Top, Math.Max(1, width), Math.Max(1, height));
    }

    private static void InvalidateYouTubeMiniPlayerArrangementPreviews(Guid stateId)
    {
        foreach (var window in GetYouTubeMiniPlayerOwnerWindows())
        {
            if (window._youTubeMiniPlayerArrangementPreviewWindow?.Plan.Targets.Any(item =>
                    item.State.Id == stateId) == true)
            {
                window.CancelYouTubeMiniPlayerArrangementPreview(showStatus: true);
            }
        }
    }

    private bool CancelYouTubeMiniPlayerArrangementPreview(bool showStatus)
        => _youTubeMiniPlayerArrangementController.CancelPreview(showStatus);

    private bool CancelFloatingMediaArrangementPreview(bool showStatus)
    {
        return CancelYouTubeMiniPlayerArrangementPreview(showStatus);
    }

    private void CancelFloatingMediaArrangementPreviewOnWindowDeactivated()
    {
        if (_youTubeMiniPlayerArrangementPreviewWindow?.IsEditMode == true)
        {
            return;
        }

        CancelFloatingMediaArrangementPreview(showStatus: false);
    }

    private void CloseYouTubeMiniPlayerArrangementPreview()
        => _youTubeMiniPlayerArrangementController.ClosePreview();

    private async void YouTubeMiniPlayerRestoreArrangementButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteRegisteredCommandAsync(
            FeatureCommandProvider.RestoreMiniPlayerArrangementActionId);
    }

    private void RestoreYouTubeMiniPlayerArrangement()
        => _youTubeMiniPlayerArrangementController.RestoreArrangement();

    private YouTubeMiniPlayerArrangementSnapshot CreateYouTubeMiniPlayerArrangementSnapshot(
        YouTubeMiniPlayerState state)
    {
        return new YouTubeMiniPlayerArrangementSnapshot(
            _workspaceWindowId,
            state.Id,
            state.Left,
            state.Top,
            state.Width,
            state.Height,
            _youTubeLiveChatController.TryGetWindow(state.Id, out var chatWindow)
                ? new YouTubeLiveChatArrangementSnapshot(
                    chatWindow.Left,
                    chatWindow.Top,
                    chatWindow.Width,
                    chatWindow.Height,
                    state.IsLiveChatWindowDocked,
                    state.LiveChatWindowDockSide)
                : null);
    }

    private Rect GetActiveDisplayWorkArea()
    {
        const uint monitorDefaultToNearest = 2;
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, monitorDefaultToNearest);
        return GetDisplayWorkArea(monitor);
    }

    private Rect GetDisplayWorkArea(Point devicePoint)
    {
        const uint monitorDefaultToNearest = 2;
        var monitor = MonitorFromPoint(
            new YouTubeMiniPlayerMonitorPoint
            {
                X = (int)Math.Round(devicePoint.X),
                Y = (int)Math.Round(devicePoint.Y)
            },
            monitorDefaultToNearest);
        return GetDisplayWorkArea(monitor);
    }

    private Rect GetDisplayWorkArea(IntPtr monitor)
    {
        var monitorInfo = new YouTubeMiniPlayerMonitorInfo
        {
            Size = (uint)Marshal.SizeOf<YouTubeMiniPlayerMonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            monitorInfo.Work.Left / dpi.DpiScaleX,
            monitorInfo.Work.Top / dpi.DpiScaleY,
            (monitorInfo.Work.Right - monitorInfo.Work.Left) / dpi.DpiScaleX,
            (monitorInfo.Work.Bottom - monitorInfo.Work.Top) / dpi.DpiScaleY);
    }

    private static string GetArrangementModeText(
        YouTubeMiniPlayerArrangeMode mode,
        YouTubeMiniPlayerLinearPlacement linearPlacement)
        => YouTubeMiniPlayerArrangementController.GetModeText(mode, linearPlacement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(YouTubeMiniPlayerMonitorPoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref YouTubeMiniPlayerMonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct YouTubeMiniPlayerMonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect Work;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct YouTubeMiniPlayerMonitorPoint
    {
        public int X;

        public int Y;
    }
}

internal sealed class YouTubeMiniPlayerListItem(
    MainWindow owner,
    YouTubeMiniPlayerState state,
    YouTubeMiniPlayerDisplayHealthSnapshot? displayHealth = null,
    bool isLocalVideoLoaded = false,
    bool isRuntimeLoaded = false,
    YouTubeMiniPlayerAudioFocusMode? audioFocusMode = null,
    bool isAudioFocusTarget = false,
    bool isAudioPreviewTarget = false,
    bool hasLocalVideoLoadFailure = false) : INotifyPropertyChanged, IYouTubeMiniPlayerArrangementItem
{
    private static readonly Brush ActiveStateBrush = new SolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly Brush InactiveStateBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
    private static readonly Brush FocusStateBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238));
    private static readonly Brush PendingStateBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));
    private static readonly Brush WarningStateBrush = new SolidColorBrush(Color.FromRgb(250, 204, 21));
    private static readonly Brush ErrorStateBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    private YouTubeMiniPlayerDisplayHealthSnapshot? _displayHealth = displayHealth;
    private bool _isLocalVideoLoaded = isLocalVideoLoaded;
    private bool _hasLocalVideoLoadFailure = hasLocalVideoLoadFailure;
    private bool _isRuntimeLoaded = isRuntimeLoaded;
    private YouTubeMiniPlayerAudioFocusMode? _audioFocusMode = audioFocusMode;
    private bool _isAudioFocusTarget = isAudioFocusTarget;
    private bool _isAudioPreviewTarget = isAudioPreviewTarget;
    private ImageSource? _thumbnail;
    private string _thumbnailSourceKey = GetThumbnailSourceKey(state);
    private string? _thumbnailAttemptedSourceKey;
    private bool _isThumbnailLoading;
    private bool _isDropBefore;
    private bool _isDropAfter;
    private long _thumbnailLoadGeneration;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow Owner { get; } = owner;

    IYouTubeMiniPlayerArrangementOwner IYouTubeMiniPlayerArrangementItem.ArrangementOwner => Owner;

    public YouTubeMiniPlayerState State { get; } = state;

    public string Title => State.SourceKind == MiniPlayerSourceKind.LocalFile
        ? (string.IsNullOrWhiteSpace(State.Title)
            ? Path.GetFileNameWithoutExtension(State.LocalFilePath)
            : State.Title)
        : YouTubeMiniPlayerNavigationService.NormalizeTitle(State.Title, State.Url);

    public Brush IconBackground => State.SourceKind == MiniPlayerSourceKind.LocalFile
        ? new SolidColorBrush(Color.FromRgb(3, 105, 161))
        : new SolidColorBrush(Color.FromRgb(255, 0, 0));

    public string IconText => State.SourceKind == MiniPlayerSourceKind.LocalFile ? "映" : "▶";

    public ImageSource? Thumbnail => _thumbnail;

    public bool HasThumbnail => _thumbnail is not null;

    public bool IsDropBefore => _isDropBefore;

    public bool IsDropAfter => _isDropAfter;

    public string ThumbnailToolTip => _isRuntimeLoaded
        ? $"{PlayPauseButtonText}: {Title}"
        : $"読み込んで再生: {Title}";

    public string MuteIconGlyph => State.IsMuted ? "\uE74F" : "\uE767";

    public Visibility AudioIndicatorVisibility =>
        _isAudioPreviewTarget
            || _isAudioFocusTarget
            || (_isRuntimeLoaded && !State.IsMuted && State.IsPaused != true)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Brush AudioIndicatorBrush => _isAudioPreviewTarget
        ? new SolidColorBrush(Color.FromRgb(250, 204, 21))
        : _isAudioFocusTarget
            ? new SolidColorBrush(Color.FromRgb(34, 211, 238))
            : new SolidColorBrush(Color.FromRgb(74, 222, 128));

    public string AudioIndicatorToolTip => GetAudioIndicatorText();

    public string SourceButtonText => State.SourceKind == MiniPlayerSourceKind.LocalFile ? "場所" : "元タブ";

    public string ReloadToolTip => State.SourceKind == MiniPlayerSourceKind.LocalFile
        ? "動画ファイルを再読み込み"
        : "WebView2を再作成して描画を復旧";

    public string PrimaryPlaybackButtonText => State.SourceKind == MiniPlayerSourceKind.LocalFile
        && !_isLocalVideoLoaded
            ? "読込"
            : PlayPauseButtonText;

    public string PlaybackText => $"{GetPlaybackStateText(State.IsPaused)}  {FormatTime(GetDisplayPositionSeconds())} / {FormatTime(State.DurationSeconds)}";

    public string TimeText => $"{FormatTime(GetDisplayPositionSeconds())} / {FormatTime(State.DurationSeconds)}";

    public string PlaybackStateText => GetPlaybackStateText(State.IsPaused);

    public Brush PlaybackStateBrush => State.IsPaused == false
        ? ActiveStateBrush
        : InactiveStateBrush;

    public string AudioStateText => State.IsMuted ? "ミュート" : "音声";

    public Visibility AudioBoostBadgeVisibility =>
        State.SourceKind == MiniPlayerSourceKind.YouTube
        && YouTubeMiniPlayerAudioBoostProfile.Normalize(State.AudioBoostDecibels) > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Brush AudioStateBrush => State.IsMuted
        ? InactiveStateBrush
        : ActiveStateBrush;

    public string WindowStateText => State.IsMinimized ? "最小化" : string.Empty;

    public Visibility WindowStateVisibility => State.IsMinimized
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string AudioFocusText => _isAudioPreviewTarget
        ? "一時視聴"
        : _isAudioFocusTarget
            ? MainWindow.GetYouTubeMiniPlayerAudioFocusTargetText(_audioFocusMode)
            : string.Empty;

    public Visibility AudioFocusVisibility => _isAudioPreviewTarget || _isAudioFocusTarget
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Brush AudioFocusBrush => _isAudioPreviewTarget
        ? WarningStateBrush
        : FocusStateBrush;

    public string DisplayStateText
    {
        get
        {
            if (!_isRuntimeLoaded)
            {
                return _hasLocalVideoLoadFailure ? "読込失敗" : "読込待ち";
            }

            return _displayHealth?.State switch
            {
                YouTubeMiniPlayerDisplayHealthState.Applying => "読込中",
                YouTubeMiniPlayerDisplayHealthState.Suspected => "表示確認中",
                YouTubeMiniPlayerDisplayHealthState.ReloadRecommended => "表示異常",
                _ => string.Empty
            };
        }
    }

    public Visibility DisplayStateVisibility => string.IsNullOrEmpty(DisplayStateText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Brush DisplayStateBrush => _displayHealth?.State switch
    {
        YouTubeMiniPlayerDisplayHealthState.Suspected => WarningStateBrush,
        YouTubeMiniPlayerDisplayHealthState.ReloadRecommended => ErrorStateBrush,
        _ => _hasLocalVideoLoadFailure ? ErrorStateBrush : PendingStateBrush
    };

    public Visibility RepairButtonVisibility =>
        _hasLocalVideoLoadFailure
        || (State.SourceKind == MiniPlayerSourceKind.YouTube
            && _displayHealth?.State == YouTubeMiniPlayerDisplayHealthState.ReloadRecommended)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility LiveEdgeButtonVisibility =>
        State.SourceKind == MiniPlayerSourceKind.YouTube
        && _isRuntimeLoaded
        && State.IsLiveBroadcast
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool CanSeekToLiveEdge =>
        State.SourceKind == MiniPlayerSourceKind.YouTube
        && _isRuntimeLoaded
        && State.IsLiveBroadcast
        && !State.IsAtLiveEdge;

    public string StatusText
    {
        get
        {
            var status = State.SourceKind == MiniPlayerSourceKind.LocalFile
                ? $"{(State.IsMuted ? "ミュート" : "音声あり")} ・ {(State.IsMinimized ? "最小化" : "表示中")} ・ {(State.IsTopmost ? "常に最前面" : "通常表示")} ・ {(State.VideoAspectRatio is { } ratio && ratio < 1 ? "縦動画" : "横動画")}"
                : YouTubeStatusText;
            var audioStatus = GetAudioIndicatorText();
            return string.IsNullOrEmpty(audioStatus)
                ? status
                : $"{audioStatus} · {status}";
        }
    }

    private string YouTubeStatusText
    {
        get
        {
            var status = $"{(State.IsMuted ? "ミュート" : "音声あり")} · {(State.IsMinimized ? "最小化" : "表示中")} · {(State.IsTopmost ? "常に最前面" : "通常表示")} · {(State.IsShorts ? "Shorts" : "通常動画")}";
            return _displayHealth?.State switch
            {
                YouTubeMiniPlayerDisplayHealthState.Suspected => status + " · 表示確認中",
                YouTubeMiniPlayerDisplayHealthState.ReloadRecommended => status + " · 再読込推奨",
                _ => status
            };
        }
    }

    public string PlayPauseButtonText => State.IsPaused == false ? "一時停止" : "再生";

    public string MuteButtonText => State.IsMuted ? "ミュート解除" : "ミュート";

    public string MuteButtonToolTip =>
        $"{MuteButtonText}（右クリックで音量とブースト）";

    public string LoopButtonText => State.IsLooping == true ? "ループ解除" : "ループ";

    public string AbRepeatButtonText => State.IsAbRepeatEnabled
        ? "A-B中"
        : State.AbRepeatStartSeconds.HasValue
            ? "A設定"
            : "A-B";

    public string AbRepeatRangeText => YouTubeMiniPlayerAbRepeatState.HasValidRange(State)
        ? $"A {FormatTime(State.AbRepeatStartSeconds)} / B {FormatTime(State.AbRepeatEndSeconds)}"
        : State.AbRepeatStartSeconds.HasValue
            ? $"A {FormatTime(State.AbRepeatStartSeconds)} / B 未設定"
            : "A-B未設定";

    public bool CanSetAbRepeatPoint => State.SourceKind != MiniPlayerSourceKind.LocalFile
        || _isLocalVideoLoaded;

    public string MinimizeButtonText => State.IsMinimized ? "復帰" : "最小";

    public string TopmostButtonText => State.IsTopmost ? "最前面解除" : "最前面";

    private string GetAudioIndicatorText()
    {
        if (_isAudioPreviewTarget)
        {
            return "一時視聴中";
        }

        if (_isAudioFocusTarget)
        {
            return MainWindow.GetYouTubeMiniPlayerAudioFocusTargetText(_audioFocusMode);
        }

        return _isRuntimeLoaded && !State.IsMuted && State.IsPaused != true
            ? "音声出力中"
            : string.Empty;
    }

    private double? GetDisplayPositionSeconds()
    {
        if (State.LastPositionSeconds is not { } position
            || State.IsPaused != false
            || State.PlaybackStateObservedAt is not { } observedAt)
        {
            return State.LastPositionSeconds;
        }

        var elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - observedAt).TotalSeconds);
        var playbackRate = State.PlaybackRate is > 0 ? State.PlaybackRate.Value : 1;
        var estimatedPosition = position + (elapsedSeconds * playbackRate);
        return State.DurationSeconds is > 0
            ? Math.Min(estimatedPosition, State.DurationSeconds.Value)
            : estimatedPosition;
    }

    public void UpdateRuntimeState(
        YouTubeMiniPlayerDisplayHealthSnapshot? displayHealth,
        bool isLocalVideoLoaded,
        bool hasLocalVideoLoadFailure,
        bool isRuntimeLoaded,
        YouTubeMiniPlayerAudioFocusMode? audioFocusMode,
        bool isAudioFocusTarget,
        bool isAudioPreviewTarget)
    {
        _displayHealth = displayHealth;
        _isLocalVideoLoaded = isLocalVideoLoaded;
        _hasLocalVideoLoadFailure = hasLocalVideoLoadFailure;
        _isRuntimeLoaded = isRuntimeLoaded;
        _audioFocusMode = audioFocusMode;
        _isAudioFocusTarget = isAudioFocusTarget;
        _isAudioPreviewTarget = isAudioPreviewTarget;
        RefreshThumbnailSource();
        RaiseStatePropertiesChanged();
    }

    public void UpdateAudioStatus(
        bool isRuntimeLoaded,
        YouTubeMiniPlayerAudioFocusMode? audioFocusMode,
        bool isAudioFocusTarget,
        bool isAudioPreviewTarget)
    {
        if (_isRuntimeLoaded == isRuntimeLoaded
            && _audioFocusMode == audioFocusMode
            && _isAudioFocusTarget == isAudioFocusTarget
            && _isAudioPreviewTarget == isAudioPreviewTarget)
        {
            return;
        }

        _isRuntimeLoaded = isRuntimeLoaded;
        _audioFocusMode = audioFocusMode;
        _isAudioFocusTarget = isAudioFocusTarget;
        _isAudioPreviewTarget = isAudioPreviewTarget;
        RaiseStatePropertiesChanged();
    }

    public void AllowThumbnailRetry()
    {
        if (_thumbnail is null && !_isThumbnailLoading)
        {
            _thumbnailAttemptedSourceKey = null;
        }
    }

    public void SetDropCue(bool isBefore, bool isAfter)
    {
        if (_isDropBefore == isBefore && _isDropAfter == isAfter)
        {
            return;
        }

        _isDropBefore = isBefore;
        _isDropAfter = isAfter;
        OnPropertyChanged(nameof(IsDropBefore));
        OnPropertyChanged(nameof(IsDropAfter));
    }

    public bool TryBeginThumbnailLoad(out string sourceKey, out long generation)
    {
        RefreshThumbnailSource();
        sourceKey = _thumbnailSourceKey;
        generation = _thumbnailLoadGeneration;
        if (_thumbnail is not null
            || _isThumbnailLoading
            || string.IsNullOrWhiteSpace(sourceKey)
            || string.Equals(_thumbnailAttemptedSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _isThumbnailLoading = true;
        _thumbnailAttemptedSourceKey = sourceKey;
        generation = ++_thumbnailLoadGeneration;
        return true;
    }

    public void CompleteThumbnailLoad(
        string sourceKey,
        long generation,
        ImageSource? thumbnail)
    {
        if (_thumbnailLoadGeneration != generation
            || !string.Equals(_thumbnailSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isThumbnailLoading = false;
        _thumbnail = thumbnail;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    public void CancelThumbnailLoad(string? sourceKey = null, long? generation = null)
    {
        if ((sourceKey is not null
                && !string.Equals(_thumbnailSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            || (generation.HasValue && _thumbnailLoadGeneration != generation.Value))
        {
            return;
        }

        _thumbnailLoadGeneration++;
        _isThumbnailLoading = false;
        if (_thumbnail is null)
        {
            _thumbnailAttemptedSourceKey = null;
        }
    }

    private void RefreshThumbnailSource()
    {
        var sourceKey = GetThumbnailSourceKey(State);
        if (string.Equals(_thumbnailSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _thumbnailSourceKey = sourceKey;
        _thumbnailAttemptedSourceKey = null;
        _isThumbnailLoading = false;
        _thumbnailLoadGeneration++;
        _thumbnail = null;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    private static string GetThumbnailSourceKey(YouTubeMiniPlayerState state)
    {
        if (state.SourceKind == MiniPlayerSourceKind.LocalFile)
        {
            return string.IsNullOrWhiteSpace(state.LocalFilePath)
                ? string.Empty
                : "file:" + state.LocalFilePath.Trim();
        }

        return YouTubeUrlService.CreateThumbnailUri(state.Url)?.AbsoluteUri
            ?? (string.IsNullOrWhiteSpace(state.Url) ? string.Empty : "youtube:" + state.Url.Trim());
    }

    private void RaiseStatePropertiesChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ThumbnailToolTip));
        OnPropertyChanged(nameof(MuteIconGlyph));
        OnPropertyChanged(nameof(AudioIndicatorVisibility));
        OnPropertyChanged(nameof(AudioIndicatorBrush));
        OnPropertyChanged(nameof(AudioIndicatorToolTip));
        OnPropertyChanged(nameof(PrimaryPlaybackButtonText));
        OnPropertyChanged(nameof(PlaybackText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(PlaybackStateText));
        OnPropertyChanged(nameof(PlaybackStateBrush));
        OnPropertyChanged(nameof(AudioStateText));
        OnPropertyChanged(nameof(AudioStateBrush));
        OnPropertyChanged(nameof(AudioBoostBadgeVisibility));
        OnPropertyChanged(nameof(WindowStateText));
        OnPropertyChanged(nameof(WindowStateVisibility));
        OnPropertyChanged(nameof(AudioFocusText));
        OnPropertyChanged(nameof(AudioFocusVisibility));
        OnPropertyChanged(nameof(AudioFocusBrush));
        OnPropertyChanged(nameof(DisplayStateText));
        OnPropertyChanged(nameof(DisplayStateVisibility));
        OnPropertyChanged(nameof(DisplayStateBrush));
        OnPropertyChanged(nameof(RepairButtonVisibility));
        OnPropertyChanged(nameof(LiveEdgeButtonVisibility));
        OnPropertyChanged(nameof(CanSeekToLiveEdge));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(MuteButtonToolTip));
        OnPropertyChanged(nameof(LoopButtonText));
        OnPropertyChanged(nameof(AbRepeatButtonText));
        OnPropertyChanged(nameof(AbRepeatRangeText));
        OnPropertyChanged(nameof(CanSetAbRepeatPoint));
        OnPropertyChanged(nameof(MinimizeButtonText));
        OnPropertyChanged(nameof(TopmostButtonText));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string GetPlaybackStateText(bool? isPaused)
    {
        return isPaused switch
        {
            true => "一時停止",
            false => "再生中",
            _ => "状態不明"
        };
    }

    private static string FormatTime(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0)
        {
            return "--:--";
        }

        var time = TimeSpan.FromSeconds(value);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}
