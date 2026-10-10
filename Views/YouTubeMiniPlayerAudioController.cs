using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal sealed record YouTubeMiniPlayerAudioElements(
    ListBox YouTubeMiniPlayerList,
    Border YouTubeMiniPlayerAudioFocusBar,
    Border YouTubeMiniPlayerActiveAudioFocusBar,
    TextBlock YouTubeMiniPlayerActiveAudioFocusText,
    TextBlock YouTubeMiniPlayerAudioFocusSelectionText,
    Button YouTubeMiniPlayerPlaySelectedOnlyButton,
    Button YouTubeMiniPlayerPauseOutsideSelectionButton,
    Button YouTubeMiniPlayerMuteOutsideSelectionButton,
    Button YouTubeMiniPlayerRestoreAudioFocusButton,
    Button YouTubeMiniPlayerActiveAudioFocusRestoreButton);

internal readonly record struct YouTubeMiniPlayerAudioSurface(
    YouTubeMiniPlayerState State, Border AudioStatusBadge, TextBlock AudioStatusText, bool IsLoaded);

internal interface IYouTubeMiniPlayerAudioHost
{
    bool IsHoverPreviewEnabled { get; }
    IReadOnlyList<MainWindow> GetOwners();
    Task CaptureStatesAsync(MainWindow owner);
    IEnumerable<YouTubeMiniPlayerListItem> GetLoadedItems(IReadOnlyList<MainWindow> owners);
    bool IsItemLoaded(YouTubeMiniPlayerListItem item);
    IEnumerable<YouTubeMiniPlayerAudioSurface> GetRuntimeAudioSurfaces();
    Task<bool> SetMutedTransientAsync(YouTubeMiniPlayerListItem item, bool muted);
    Task<bool> SetPausedTransientAsync(YouTubeMiniPlayerListItem item, bool paused);
    Task SetMutedAsync(YouTubeMiniPlayerListItem item, bool muted);
    Task<bool> SetPausedAsync(YouTubeMiniPlayerListItem item, bool paused);
    void QueueSave(MainWindow owner);
    void SetStatus(string message);
    void UpdateAllIndicators();
    Task RefreshListAsync();
}

internal sealed class YouTubeMiniPlayerAudioController
{
    private const int YouTubeMiniPlayerHoverAudioPreviewDelayMilliseconds = 350;
    private static readonly Brush YouTubeMiniPlayerAudioOutputBadgeBackgroundBrush =
        new SolidColorBrush(Color.FromArgb(224, 22, 101, 52));
    private static readonly Brush YouTubeMiniPlayerAudioOutputBadgeBorderBrush =
        new SolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly Brush YouTubeMiniPlayerAudioFocusBadgeBackgroundBrush =
        new SolidColorBrush(Color.FromArgb(232, 8, 79, 97));
    private static readonly Brush YouTubeMiniPlayerAudioFocusBadgeBorderBrush =
        new SolidColorBrush(Color.FromRgb(34, 211, 238));
    private static readonly Brush YouTubeMiniPlayerAudioPreviewBadgeBackgroundBrush =
        new SolidColorBrush(Color.FromArgb(232, 113, 63, 18));
    private static readonly Brush YouTubeMiniPlayerAudioPreviewBadgeBorderBrush =
        new SolidColorBrush(Color.FromRgb(250, 204, 21));
    private readonly YouTubeMiniPlayerAudioElements _elements;
    private readonly YouTubeMiniPlayerAudioSessionCoordinator _audioSession;
    private readonly YouTubeMiniPlayerListInteractionCoordinator _youTubeMiniPlayerListInteractionCoordinator;
    private readonly OwnedDispatcherTimer _youTubeMiniPlayerHoverAudioPreviewTimer;
    private readonly IYouTubeMiniPlayerAudioHost _host;
    private bool _isStopped;

    public YouTubeMiniPlayerAudioController(
        YouTubeMiniPlayerAudioElements elements,
        YouTubeMiniPlayerAudioSessionCoordinator audioSession,
        YouTubeMiniPlayerListInteractionCoordinator interaction,
        OwnedDispatcherTimer hoverTimer,
        IYouTubeMiniPlayerAudioHost host)
    {
        _elements = elements;
        _audioSession = audioSession;
        _youTubeMiniPlayerListInteractionCoordinator = interaction;
        _youTubeMiniPlayerHoverAudioPreviewTimer = hoverTimer;
        _host = host;
    }

    private ListBox YouTubeMiniPlayerList => _elements.YouTubeMiniPlayerList;
    private Border YouTubeMiniPlayerAudioFocusBar => _elements.YouTubeMiniPlayerAudioFocusBar;
    private Border YouTubeMiniPlayerActiveAudioFocusBar => _elements.YouTubeMiniPlayerActiveAudioFocusBar;
    private TextBlock YouTubeMiniPlayerActiveAudioFocusText => _elements.YouTubeMiniPlayerActiveAudioFocusText;
    private TextBlock YouTubeMiniPlayerAudioFocusSelectionText => _elements.YouTubeMiniPlayerAudioFocusSelectionText;
    private Button YouTubeMiniPlayerPlaySelectedOnlyButton => _elements.YouTubeMiniPlayerPlaySelectedOnlyButton;
    private Button YouTubeMiniPlayerPauseOutsideSelectionButton => _elements.YouTubeMiniPlayerPauseOutsideSelectionButton;
    private Button YouTubeMiniPlayerMuteOutsideSelectionButton => _elements.YouTubeMiniPlayerMuteOutsideSelectionButton;
    private Button YouTubeMiniPlayerRestoreAudioFocusButton => _elements.YouTubeMiniPlayerRestoreAudioFocusButton;
    private Button YouTubeMiniPlayerActiveAudioFocusRestoreButton => _elements.YouTubeMiniPlayerActiveAudioFocusRestoreButton;

    public void InitializeHoverTimer()
    {
        _youTubeMiniPlayerHoverAudioPreviewTimer.Interval =
            TimeSpan.FromMilliseconds(YouTubeMiniPlayerHoverAudioPreviewDelayMilliseconds);
        _youTubeMiniPlayerHoverAudioPreviewTimer.Tick += HoverTimer_Tick;
    }

    private async void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _youTubeMiniPlayerHoverAudioPreviewTimer.Stop();
        if (_youTubeMiniPlayerListInteractionCoordinator.PendingHoverPreviewId is { } targetId)
        {
            await StartYouTubeMiniPlayerHoverAudioPreviewAsync(targetId);
        }
    }

    public void StopHoverTracking()
    {
        _isStopped = true;
        _youTubeMiniPlayerHoverAudioPreviewTimer.Stop();
        _youTubeMiniPlayerHoverAudioPreviewTimer.Tick -= HoverTimer_Tick;
        _youTubeMiniPlayerListInteractionCoordinator.ClearPendingHoverPreview();
    }

    public void ScheduleYouTubeMiniPlayerHoverAudioPreview(Guid targetId)
    {
        if (_isStopped || !_host.IsHoverPreviewEnabled)
        {
            return;
        }

        _youTubeMiniPlayerListInteractionCoordinator.SetPendingHoverPreview(targetId);
        _youTubeMiniPlayerHoverAudioPreviewTimer.Stop();
        _youTubeMiniPlayerHoverAudioPreviewTimer.Start();
    }

    public void CancelPendingYouTubeMiniPlayerHoverAudioPreview()
    {
        _youTubeMiniPlayerHoverAudioPreviewTimer.Stop();
        _youTubeMiniPlayerListInteractionCoordinator.ClearPendingHoverPreview();
    }

    public async Task StartYouTubeMiniPlayerHoverAudioPreviewAsync(Guid targetId)
    {
        if (_isStopped || !_host.IsHoverPreviewEnabled
            || !_youTubeMiniPlayerListInteractionCoordinator.IsPendingHoverPreview(targetId))
        {
            return;
        }

        using (await _audioSession.EnterAsync())
        {
            await StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync();
            var items = await CaptureLoadedYouTubeMiniPlayerAudioItemsAsync();
            var states = items
                .Select(static item => new YouTubeMiniPlayerAudioFocusState(
                    item.State.Id,
                    item.State.IsMuted,
                    item.State.IsPaused))
                .ToList();
            var plan = _audioSession.StartPreview(states, targetId);
            if (plan.Count == 0)
            {
                return;
            }

            var itemsById = items.ToDictionary(static item => item.State.Id);
            var targetApplied = false;
            foreach (var planItem in plan.OrderBy(static item => item.DesiredMuted == true ? 0 : 1))
            {
                if (!itemsById.TryGetValue(planItem.Id, out var item))
                {
                    continue;
                }

                var applied = await ApplyYouTubeMiniPlayerHoverViewingPlanItemAsync(item, planItem);
                if (planItem.Id == targetId)
                {
                    targetApplied = applied;
                }
            }

            if (!targetApplied)
            {
                await StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync();
                return;
            }

            _host.UpdateAllIndicators();
        }

    }

    private async Task<bool> ApplyYouTubeMiniPlayerHoverViewingPlanItemAsync(
        YouTubeMiniPlayerListItem item,
        YouTubeMiniPlayerAudioFocusPlanItem planItem)
    {
        var mutedApplied = !planItem.DesiredMuted.HasValue
            || await _host.SetMutedTransientAsync(item, planItem.DesiredMuted.Value);
        var pausedApplied = !planItem.DesiredPaused.HasValue
            || await _host.SetPausedTransientAsync(item, planItem.DesiredPaused.Value);
        return mutedApplied && pausedApplied;
    }

    public async Task StopYouTubeMiniPlayerHoverAudioPreviewAsync()
    {
        using (await _audioSession.EnterAsync())
        {
            await StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync();
        }

    }

    private async Task StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync()
    {
        if (!_audioSession.IsPreviewActive)
        {
            return;
        }

        var restore = _audioSession.StopPreview();
        var items = _host.GetLoadedItems(_host.GetOwners())
            .ToDictionary(static item => item.State.Id);
        foreach (var planItem in restore)
        {
            if (items.TryGetValue(planItem.Id, out var item))
            {
                await ApplyYouTubeMiniPlayerHoverViewingPlanItemAsync(item, planItem);
            }
        }

        _host.UpdateAllIndicators();
    }

    public void UpdateYouTubeMiniPlayerAudioFocusBar()
    {
        var selectedCount = YouTubeMiniPlayerList.SelectedItems.Count;
        var hasFocus = _audioSession.IsFocusActive;
        YouTubeMiniPlayerAudioFocusBar.Visibility = selectedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        YouTubeMiniPlayerActiveAudioFocusBar.Visibility = selectedCount == 0 && hasFocus
            ? Visibility.Visible
            : Visibility.Collapsed;
        YouTubeMiniPlayerActiveAudioFocusText.Text = hasFocus
            ? $"音声フォーカス: {GetYouTubeMiniPlayerAudioFocusTargetText(_audioSession.FocusMode)}"
            : "音声フォーカス中";
        YouTubeMiniPlayerAudioFocusSelectionText.Text = selectedCount > 0
            ? $"選択 {selectedCount}件"
            : "選択なし";
        YouTubeMiniPlayerPlaySelectedOnlyButton.IsEnabled = selectedCount > 0;
        YouTubeMiniPlayerPauseOutsideSelectionButton.IsEnabled = selectedCount > 0;
        YouTubeMiniPlayerMuteOutsideSelectionButton.IsEnabled = selectedCount > 0;
        YouTubeMiniPlayerRestoreAudioFocusButton.IsEnabled = hasFocus;
        YouTubeMiniPlayerActiveAudioFocusRestoreButton.IsEnabled = hasFocus;
    }

    internal static string GetYouTubeMiniPlayerAudioFocusTargetText(
        YouTubeMiniPlayerAudioFocusMode? mode)
    {
        return mode switch
        {
            YouTubeMiniPlayerAudioFocusMode.SoloAudio => "ソロ音声",
            YouTubeMiniPlayerAudioFocusMode.SoloPlayback => "ソロ再生",
            YouTubeMiniPlayerAudioFocusMode.SelectedPlayback => "選択再生",
            YouTubeMiniPlayerAudioFocusMode.PauseOutsideSelection => "停止対象外",
            YouTubeMiniPlayerAudioFocusMode.MuteOutsideSelection => "ミュート対象外",
            _ => "対象"
        };
    }

    public async Task ExecuteSelectedYouTubeMiniPlayerAudioFocusAsync(
        YouTubeMiniPlayerAudioFocusMode mode,
        string successMessage)
    {
        var selectedIds = YouTubeMiniPlayerList.SelectedItems
            .OfType<YouTubeMiniPlayerListItem>()
            .Select(static item => item.State.Id)
            .ToHashSet();
        if (selectedIds.Count == 0)
        {
            _host.SetStatus("ミニプレイヤー一覧で対象を選択してください");
            return;
        }

        await ExecuteYouTubeMiniPlayerAudioFocusAsync(mode, selectedIds, successMessage);
    }

    public async Task ExecuteYouTubeMiniPlayerAudioFocusAsync(
        YouTubeMiniPlayerAudioFocusMode mode,
        IReadOnlyCollection<Guid> targetIds,
        string successMessage)
    {
        using (await _audioSession.EnterAsync())
        {
            await StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync();
            var items = await CaptureLoadedYouTubeMiniPlayerAudioItemsAsync();
            var availableTargetIds = targetIds
                .Where(id => items.Any(item => item.State.Id == id))
                .ToHashSet();
            if (availableTargetIds.Count == 0)
            {
                _host.SetStatus("操作できる読み込み済みミニプレイヤーがありません");
                return;
            }

            var states = items
                .Select(static item => new YouTubeMiniPlayerAudioFocusState(
                    item.State.Id,
                    item.State.IsMuted,
                    item.State.IsPaused))
                .ToList();
            var plan = _audioSession.CreateFocusApplyPlan(
                mode,
                states,
                availableTargetIds);
            var itemsById = items.ToDictionary(static item => item.State.Id);
            var successCount = 0;
            foreach (var planItem in plan.OrderBy(static item =>
                         item.DesiredMuted == false || item.DesiredPaused == false ? 1 : 0))
            {
                var (mutedApplied, pausedApplied) = await ApplyYouTubeMiniPlayerAudioFocusPlanItemAsync(
                    itemsById[planItem.Id],
                    planItem);
                _audioSession.CompleteFocusPlanItem(
                    planItem,
                    mutedApplied,
                    pausedApplied);
                if (mutedApplied && pausedApplied)
                {
                    successCount++;
                }
            }

            _host.SetStatus(
                plan.Count == 0 || successCount == plan.Count
                    ? successMessage
                    : $"{successMessage}（{successCount}/{plan.Count}件）");
            _host.UpdateAllIndicators();
        }

        await _host.RefreshListAsync();
    }

    public async Task RestoreYouTubeMiniPlayerAudioFocusAsync()
    {
        using (await _audioSession.EnterAsync())
        {
            await StopYouTubeMiniPlayerHoverAudioPreviewCoreAsync();
            if (!_audioSession.IsFocusActive)
            {
                _host.SetStatus("解除する音声フォーカスはありません");
                return;
            }

            var items = await CaptureLoadedYouTubeMiniPlayerAudioItemsAsync();
            var states = items
                .Select(static item => new YouTubeMiniPlayerAudioFocusState(
                    item.State.Id,
                    item.State.IsMuted,
                    item.State.IsPaused))
                .ToList();
            var plan = _audioSession.CreateFocusRestorePlan(states);
            var itemsById = items.ToDictionary(static item => item.State.Id);
            var successCount = 0;
            foreach (var planItem in plan)
            {
                if (!itemsById.TryGetValue(planItem.Id, out var item))
                {
                    continue;
                }

                var (mutedApplied, pausedApplied) = await ApplyYouTubeMiniPlayerAudioFocusPlanItemAsync(
                    item,
                    planItem);
                _audioSession.CompleteFocusPlanItem(
                    planItem,
                    mutedApplied,
                    pausedApplied);
                if (mutedApplied && pausedApplied)
                {
                    successCount++;
                }
            }

            _audioSession.ClearFocusIfFullyRestored();

            foreach (var owner in items.Select(static item => item.Owner).Distinct())
            {
                _host.QueueSave(owner);
            }

            _host.SetStatus(
                !_audioSession.IsFocusActive
                    ? "音声フォーカスを解除して元の状態へ戻しました"
                    : $"音声フォーカスを一部解除できませんでした（{successCount}/{plan.Count}件）");
            _host.UpdateAllIndicators();
        }

        await _host.RefreshListAsync();
    }

    private async Task<(bool MutedApplied, bool PausedApplied)>
        ApplyYouTubeMiniPlayerAudioFocusPlanItemAsync(
            YouTubeMiniPlayerListItem item,
            YouTubeMiniPlayerAudioFocusPlanItem planItem)
    {
        var mutedApplied = !planItem.DesiredMuted.HasValue;
        if (planItem.DesiredMuted is { } desiredMuted)
        {
            if (item.State.IsMuted != desiredMuted)
            {
                await _host.SetMutedAsync(item, desiredMuted);
            }

            mutedApplied = item.State.IsMuted == desiredMuted;
        }

        var pausedApplied = !planItem.DesiredPaused.HasValue;
        if (planItem.DesiredPaused is { } desiredPaused)
        {
            pausedApplied = item.State.IsPaused == desiredPaused
                || await _host.SetPausedAsync(item, desiredPaused);
        }

        return (mutedApplied, pausedApplied);
    }

    private async Task<List<YouTubeMiniPlayerListItem>> CaptureLoadedYouTubeMiniPlayerAudioItemsAsync()
    {
        var windows = _host.GetOwners();
        foreach (var window in windows)
        {
            await _host.CaptureStatesAsync(window);
        }

        return _host.GetLoadedItems(windows).ToList();
    }

    public void UpdateIndicators()
    {
        foreach (var runtime in _host.GetRuntimeAudioSurfaces())
        {
            var previewTargetId = _audioSession.PreviewTargetId;
            var isPreviewTarget = previewTargetId == runtime.State.Id;
            var isFocusTarget = !previewTargetId.HasValue
                && _audioSession.IsFocusTarget(runtime.State.Id);
            var isLoaded = runtime.IsLoaded;
            var isAudioOutput = !previewTargetId.HasValue
                && isLoaded
                && !runtime.State.IsMuted
                && runtime.State.IsPaused != true;
            if (!isPreviewTarget && !isFocusTarget && !isAudioOutput)
            {
                runtime.AudioStatusBadge.Visibility = Visibility.Collapsed;
                continue;
            }

            if (isPreviewTarget)
            {
                runtime.AudioStatusText.Text = "視聴";
                runtime.AudioStatusBadge.Background = YouTubeMiniPlayerAudioPreviewBadgeBackgroundBrush;
                runtime.AudioStatusBadge.BorderBrush = YouTubeMiniPlayerAudioPreviewBadgeBorderBrush;
            }
            else if (isFocusTarget)
            {
                runtime.AudioStatusText.Text = GetYouTubeMiniPlayerAudioFocusTargetText(
                    _audioSession.FocusMode);
                runtime.AudioStatusBadge.Background = YouTubeMiniPlayerAudioFocusBadgeBackgroundBrush;
                runtime.AudioStatusBadge.BorderBrush = YouTubeMiniPlayerAudioFocusBadgeBorderBrush;
            }
            else
            {
                runtime.AudioStatusText.Text = "音声";
                runtime.AudioStatusBadge.Background = YouTubeMiniPlayerAudioOutputBadgeBackgroundBrush;
                runtime.AudioStatusBadge.BorderBrush = YouTubeMiniPlayerAudioOutputBadgeBorderBrush;
            }

            runtime.AudioStatusBadge.Visibility = Visibility.Visible;
        }

        foreach (var item in YouTubeMiniPlayerList.Items
                     .OfType<YouTubeMiniPlayerListItem>())
        {
            var previewTargetId = _audioSession.PreviewTargetId;
            var isPreviewTarget = previewTargetId == item.State.Id;
            var isFocusTarget = !previewTargetId.HasValue
                && _audioSession.IsFocusTarget(item.State.Id);
            var isLoaded = _host.IsItemLoaded(item);
            item.UpdateAudioStatus(
                isLoaded,
                _audioSession.FocusMode,
                isFocusTarget,
                isPreviewTarget);
        }

        UpdateYouTubeMiniPlayerAudioFocusBar();
    }
}
