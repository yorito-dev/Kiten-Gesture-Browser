using System.Windows;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal interface IYouTubeMiniPlayerArrangementItem
{
    IYouTubeMiniPlayerArrangementOwner ArrangementOwner { get; }
    YouTubeMiniPlayerState State { get; }
    string Title { get; }
}

internal interface IYouTubeMiniPlayerArrangementOwner
{
    Guid WindowId { get; }
    IReadOnlyList<YouTubeMiniPlayerState> States { get; }
    bool HasRuntime(Guid id);
    IYouTubeMiniPlayerArrangementItem CreateItem(YouTubeMiniPlayerState state);
    YouTubeMiniPlayerArrangementCandidate CreateCandidate(YouTubeMiniPlayerState state);
    YouTubeMiniPlayerArrangementPreviewValidationItem CaptureValidation(Guid id, bool requireRuntime);
    YouTubeMiniPlayerArrangementSnapshot CaptureArrangement(YouTubeMiniPlayerState state);
    Point GetHostScreenOrigin();
    void ApplyPlayer(YouTubeMiniPlayerState state);
    IYouTubeMiniPlayerArrangementChat? FindChat(Guid id);
    bool IsPlaybackLoaded(Guid id);
    Task<bool> SeekToRatioAsync(YouTubeMiniPlayerState state, double ratio);
    void QueueSave();
    bool CancelPreview(bool showStatus);
}

internal interface IYouTubeMiniPlayerArrangementChat
{
    void Apply(Rect bounds, bool isDocked, YouTubeLiveChatDockSide dockSide, bool removeSpacing);
    void Synchronize();
}

internal interface IYouTubeMiniPlayerArrangementPreview
{
    YouTubeMiniPlayerArrangementPlan Plan { get; }
    bool HasEditablePlayerFrames { get; }
    IReadOnlyDictionary<Guid, Rect> GetPlayerBounds();
    void ShowWithoutActivation();
    void Close();
}

internal interface IYouTubeMiniPlayerArrangementHost
{
    IReadOnlyList<IYouTubeMiniPlayerArrangementOwner> GetOwners();
    IReadOnlyList<IYouTubeMiniPlayerArrangementItem> CaptureItems();
    IReadOnlyList<Guid> GetSelectedIds();
    bool IncludeMinimized { get; }
    bool RemoveSpacing => false;
    Rect GetWorkArea();
    YouTubeMiniPlayerArrangementAreaRequest? CapturePlacementArea(IReadOnlyList<Guid> targetIds, YouTubeMiniPlayerArrangeMode mode) => null;
    void EnsureListOpen();
    IYouTubeMiniPlayerArrangementPreview? Preview { get; set; }
    IYouTubeMiniPlayerArrangementPreview CreatePreview(YouTubeMiniPlayerArrangementPlan plan, IReadOnlyList<YouTubeMiniPlayerArrangementPreviewItem> items);
    void ShowPreviewBar(bool canEdit, string text);
    void HidePreviewBar(bool clearText);
    void SetArrangeMenuFailure(string? text);
    void SetStatus(string message, bool offerRestorePreviousArrangement = false);
    Task RefreshListAsync();
}

internal sealed record YouTubeMiniPlayerArrangementPlan(
    YouTubeMiniPlayerArrangeMode Mode,
    YouTubeMiniPlayerLinearPlacement LinearPlacement,
    Rect WorkArea,
    IReadOnlyList<IYouTubeMiniPlayerArrangementItem> AllItems,
    IReadOnlyList<IYouTubeMiniPlayerArrangementItem> Targets,
    IReadOnlyList<YouTubeMiniPlayerArrangementResult> Layout,
    IReadOnlySet<Guid> FocusIds,
    IReadOnlyList<YouTubeMiniPlayerArrangementBaseline> Baselines)
{
    public YouTubeMiniPlayerArrangementAreaRequest? AreaRequest { get; init; }
    public Rect? PlacementArea { get; init; }
    public bool RemoveSpacing { get; init; }
}

internal sealed record YouTubeMiniPlayerArrangementBaseline(
    IYouTubeMiniPlayerArrangementOwner Owner,
    YouTubeMiniPlayerArrangementPreviewValidationItem Snapshot,
    Size? MinimumSize = null);

// 配置の手順を所有する。実Windowと編集枠はhost／preview、元配置は既存sessionが所有する。
// 途中失敗時の適用済み状態と、保存・一覧更新の順序は既存動作を維持する。
internal sealed class YouTubeMiniPlayerArrangementController(
    IYouTubeMiniPlayerArrangementHost host,
    YouTubeMiniPlayerArrangementSessionCoordinator session,
    Func<DateTimeOffset>? utcNow = null,
    Func<double>? nextRatio = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<double> _nextRatio = nextRatio ?? Random.Shared.NextDouble;

    public void BeginPreview(YouTubeMiniPlayerArrangeMode mode)
    {
        foreach (var owner in host.GetOwners())
        {
            owner.CancelPreview(false);
        }

        host.EnsureListOpen();
        host.SetArrangeMenuFailure(null);
        var plan = CreatePlan(mode, session.GetLinearPlacement(mode));
        if (plan is null)
        {
            return;
        }

        IYouTubeMiniPlayerArrangementPreview? preview = null;
        try
        {
            preview = host.CreatePreview(plan, CreatePreviewItems(plan));
            host.Preview = preview;
            var canEdit = preview.HasEditablePlayerFrames && !plan.Layout.Any(static result => result.ChatBounds.HasValue);
            host.ShowPreviewBar(canEdit,
                $"配置予告: {GetModeText(mode, plan.LinearPlacement)} / {plan.Targets.Count}件（Escでキャンセル）");
            preview.ShowWithoutActivation();
            host.SetStatus("配置予告を表示しました。適用またはキャンセルを選んでください");
        }
        catch (Exception ex)
        {
            host.Preview = null;
            host.HidePreviewBar(clearText: false);
            try
            {
                preview?.Close();
            }
            catch
            {
                // 生成途中の予告Windowは閉じられない場合がある。
            }
            host.SetStatus("配置予告を表示できませんでした: " + ex.Message);
        }
    }

    public YouTubeMiniPlayerArrangementPlan? CreatePlan(YouTubeMiniPlayerArrangeMode mode, YouTubeMiniPlayerLinearPlacement placement)
    {
        var allItems = host.CaptureItems();
        var selectedIds = host.GetSelectedIds();
        var includeMinimized = host.IncludeMinimized;
        var removeSpacing = host.RemoveSpacing;
        var workArea = host.GetWorkArea();
        var candidates = allItems.Select(item => item.ArrangementOwner.CreateCandidate(item.State)).ToList();
        var targetIds = YouTubeMiniPlayerArrangementPlanningService.GetTargets(candidates, selectedIds, includeMinimized)
            .Select(candidate => candidate.Id).ToList();
        var areaRequest = host.CapturePlacementArea(targetIds, mode);
        var result = YouTubeMiniPlayerArrangementPlanningService.CreatePlan(
            mode, workArea, candidates, selectedIds, includeMinimized, placement, areaRequest: areaRequest, removeSpacing: removeSpacing);
        if (result.FailureReason != YouTubeMiniPlayerArrangementPlanningFailureReason.None)
        {
            var message = result.FailureReason switch
            {
                YouTubeMiniPlayerArrangementPlanningFailureReason.NoEligibleTargets => "整列できるYouTubeミニプレイヤーがありません",
                YouTubeMiniPlayerArrangementPlanningFailureReason.AreaUnavailable when areaRequest?.Mode == YouTubeMiniPlayerArrangementAreaMode.SelectedRegion
                    => "ディスプレイの配置が変わりました。「範囲を指定」から指定し直してください",
                YouTubeMiniPlayerArrangementPlanningFailureReason.AreaUnavailable => "配置先の画面・空き領域を確認できません。ディスプレイを選び直してください",
                YouTubeMiniPlayerArrangementPlanningFailureReason.RegionNotSelected => "「範囲を指定」で配置する場所を囲んでください",
                _ when areaRequest?.Mode == YouTubeMiniPlayerArrangementAreaMode.FreeSpace => "全件を配置できる空きスペースがありません。対象を減らすか範囲を指定してください",
                _ when areaRequest?.Mode == YouTubeMiniPlayerArrangementAreaMode.SelectedRegion => "指定した範囲に収まりません。範囲を広げるか対象を減らしてください",
                _ when removeSpacing => "全件がディスプレイの作業領域に収まりません。対象を減らすか配置を変更してください",
                YouTubeMiniPlayerArrangementPlanningFailureReason.NoLayout when mode == YouTubeMiniPlayerArrangeMode.Focus => "選択した動画を大きく配置できる空き領域がありません",
                _ => "配置できる空き領域がありません"
            };
            if (result.FailureReason == YouTubeMiniPlayerArrangementPlanningFailureReason.NoLayout
                && (removeSpacing || areaRequest?.Mode is YouTubeMiniPlayerArrangementAreaMode.FreeSpace or YouTubeMiniPlayerArrangementAreaMode.SelectedRegion))
            {
                host.SetArrangeMenuFailure($"配置不可：{GetModeText(mode, placement)}\n{message}");
            }
            host.SetStatus(message);
            return null;
        }

        var itemsById = allItems.ToDictionary(static item => item.State.Id);
        var targets = result.TargetIds.Select(id => itemsById[id]).ToList();
        var candidatesById = candidates.ToDictionary(static item => item.Id);
        var baselines = targets.Select(item => new YouTubeMiniPlayerArrangementBaseline(
            item.ArrangementOwner, item.ArrangementOwner.CaptureValidation(item.State.Id, requireRuntime: true),
            YouTubeMiniPlayerArrangementAreaService.GetRequiredMinimum(candidatesById[item.State.Id]))).ToList();
        return new(mode, placement, workArea, allItems, targets, result.Layout, result.FocusIds, baselines)
        { AreaRequest = areaRequest, PlacementArea = result.PlacementArea, RemoveSpacing = removeSpacing };
    }

    public async Task ApplyPreviewAsync()
    {
        if (host.Preview is not { } preview)
        {
            return;
        }
        var plan = preview.Plan;
        if (!IsPlanCurrent(plan))
        {
            CancelPreview(false);
            host.SetStatus("対象が変更されたため配置予告を解除しました");
            return;
        }

        var adjustedBounds = preview.GetPlayerBounds();
        var layout = plan.Layout.Select(result => adjustedBounds.TryGetValue(result.Id, out var bounds)
            ? result with { Bounds = bounds } : result).ToList();
        if (plan.RemoveSpacing || plan.AreaRequest is { Mode: not YouTubeMiniPlayerArrangementAreaMode.Display })
        {
            var current = host.CapturePlacementArea(plan.Targets.Select(item => item.State.Id).ToList(), plan.Mode);
            if ((plan.AreaRequest is { } area && !YouTubeMiniPlayerArrangementAreaService.IsSameDestination(area, current))
                || plan.PlacementArea is not { } placementArea)
            {
                CancelPreview(false);
                host.SetStatus("配置先が変わりました。整列を選び直してください");
                return;
            }

            var minimumSizes = plan.Targets.Select(item => item.ArrangementOwner.CreateCandidate(item.State))
                .ToDictionary(candidate => candidate.Id, YouTubeMiniPlayerArrangementAreaService.GetRequiredMinimum);
            if (!YouTubeMiniPlayerArrangementAreaService.Fits(layout, placementArea,
                current?.PlacementExclusions ?? [], plan.Mode == YouTubeMiniPlayerArrangeMode.Random, minimumSizes))
            {
                if (adjustedBounds.Count > 0)
                {
                    const string message = "調整した枠を適用できません。枠の重なり・位置・大きさを確認するか「配置案に戻す」を押してください";
                    host.SetArrangeMenuFailure(message);
                    host.SetStatus(message);
                    return;
                }
                CancelPreview(false);
                host.SetStatus("配置先が変化したか、枠内に収まりません。整列を選び直して予告を確認してください");
                return;
            }
        }
        host.SetArrangeMenuFailure(null);
        ClosePreview();
        var allSnapshots = plan.AllItems.Select(item => item.ArrangementOwner.CaptureArrangement(item.State)).ToList();
        var targetSnapshots = plan.Targets.Select(item => item.ArrangementOwner.CaptureArrangement(item.State)).ToList();
        session.RecordInitialArrangement(allSnapshots, targetSnapshots);
        var itemsById = plan.Targets.ToDictionary(static item => item.State.Id);
        var updatedAt = _utcNow();
        foreach (var result in layout)
        {
            var item = itemsById[result.Id];
            var origin = item.ArrangementOwner.GetHostScreenOrigin();
            item.State.Left = result.Bounds.Left - origin.X;
            item.State.Top = result.Bounds.Top - origin.Y;
            item.State.Width = result.Bounds.Width;
            item.State.Height = result.Bounds.Height;
            item.State.UpdatedAt = updatedAt;
        }
        ApplyPlayers(plan.Targets);
        foreach (var result in layout)
        {
            if (result.ChatBounds is not { } bounds || !itemsById.TryGetValue(result.Id, out var item)
                || item.ArrangementOwner.FindChat(result.Id) is not { } chat)
            {
                continue;
            }
            item.State.IsLiveChatWindowDocked = true;
            var dockSide = result.ChatDockSide ?? YouTubeLiveChatDockSide.Right;
            item.State.LiveChatWindowDockSide = dockSide;
            item.State.UpdatedAt = _utcNow();
            chat.Apply(bounds, true, dockSide, plan.RemoveSpacing);
        }
        session.RecordPreviousArrangement(targetSnapshots);
        host.SetStatus($"YouTubeミニプレイヤーを{GetModeText(plan.Mode, plan.LinearPlacement)}で整列しました（{plan.Targets.Count}件）",
            offerRestorePreviousArrangement: true);
        await host.RefreshListAsync();
        if (plan.Mode == YouTubeMiniPlayerArrangeMode.Random)
        {
            await RandomizePlaybackPositionsAsync(plan.Targets, targetSnapshots);
        }
    }

    public bool IsPlanCurrent(YouTubeMiniPlayerArrangementPlan plan)
    {
        var baseline = new YouTubeMiniPlayerArrangementPreviewValidationSnapshot(
            ToBounds(plan.WorkArea), plan.Baselines.Select(static item => item.Snapshot).ToList());
        var current = new YouTubeMiniPlayerArrangementPreviewValidationSnapshot(
            ToBounds(host.GetWorkArea()), plan.Baselines.Select(item =>
                item.Owner.CaptureValidation(item.Snapshot.StateId, requireRuntime: false)).ToList());
        return YouTubeMiniPlayerArrangementPreviewValidationService.IsCurrent(baseline, current);
    }

    public bool CancelPreview(bool showStatus)
    {
        if (host.Preview is null)
        {
            return false;
        }
        ClosePreview();
        if (showStatus)
        {
            host.SetStatus("配置予告をキャンセルしました");
        }
        return true;
    }

    public void ClosePreview()
    {
        var preview = host.Preview;
        host.Preview = null;
        host.HidePreviewBar(clearText: true);
        if (preview is null)
        {
            return;
        }
        try
        {
            preview.Close();
        }
        catch (InvalidOperationException)
        {
            // Window終了と競合した場合は、WPFの破棄を優先する。
        }
    }

    public async Task RandomizePlaybackPositionsAsync(IReadOnlyList<IYouTubeMiniPlayerArrangementItem> targets,
        IReadOnlyList<YouTubeMiniPlayerArrangementSnapshot>? appliedArrangement = null)
    {
        if (targets.Count == 0)
        {
            return;
        }
        var loaded = targets.Where(item => item.ArrangementOwner.IsPlaybackLoaded(item.State.Id)).ToList();
        var results = await Task.WhenAll(loaded.Select(item => item.ArrangementOwner.SeekToRatioAsync(item.State, _nextRatio())));
        var successful = loaded.Where((_, index) => results[index]).ToList();
        foreach (var owner in successful.Select(static item => item.ArrangementOwner).Distinct())
        {
            owner.QueueSave();
        }
        // 再生位置調整を待つ間に、整列が取り消されたり別の整列が適用されたりする。
        if (appliedArrangement is null || ReferenceEquals(appliedArrangement, session.PreviousArrangement))
        {
            host.SetStatus(successful.Count > 0
                ? $"YouTubeミニプレイヤーをランダム配置し、再生位置もランダムに変更しました（{successful.Count}/{targets.Count}件）"
                : "YouTubeミニプレイヤーをランダム配置しました（再生位置を変更できる動画はありません）",
                offerRestorePreviousArrangement: appliedArrangement is not null);
        }
        await host.RefreshListAsync();
    }

    public void RestoreArrangement() => RestoreArrangement(restorePrevious: false);

    public void RestorePreviousArrangement() => RestoreArrangement(restorePrevious: true);

    private void RestoreArrangement(bool restorePrevious)
    {
        var destination = restorePrevious ? "直前の配置" : "最初の整列前配置";
        var snapshots = restorePrevious ? session.PreviousArrangement : session.SnapshotInitialArrangement();
        var targets = new List<IYouTubeMiniPlayerArrangementItem>();
        var updatedAt = _utcNow();
        var owners = host.GetOwners();
        foreach (var snapshot in snapshots)
        {
            var owner = owners.FirstOrDefault(item => item.WindowId == snapshot.OwnerWindowId);
            var state = owner?.States.FirstOrDefault(state => state.Id == snapshot.StateId);
            if (owner is null || state is null || !owner.HasRuntime(snapshot.StateId))
            {
                continue;
            }
            state.Left = snapshot.Left;
            state.Top = snapshot.Top;
            state.Width = snapshot.Width;
            state.Height = snapshot.Height;
            state.UpdatedAt = updatedAt;
            targets.Add(owner.CreateItem(state));
            if (snapshot.Chat is { } savedChat && owner.FindChat(snapshot.StateId) is { } chat)
            {
                state.IsLiveChatWindowDocked = savedChat.IsDocked;
                state.LiveChatWindowDockSide = savedChat.DockSide;
                chat.Apply(new Rect(savedChat.Left, savedChat.Top, savedChat.Width, savedChat.Height), savedChat.IsDocked, savedChat.DockSide, savedChat.RemoveSpacing);
            }
        }
        if (targets.Count == 0)
        {
            host.SetStatus($"戻せる{destination}がありません");
            return;
        }
        ApplyPlayers(targets);
        foreach (var item in targets)
        {
            item.ArrangementOwner.FindChat(item.State.Id)?.Synchronize();
        }
        session.ClearPreviousArrangement();
        if (!restorePrevious)
        {
            session.ClearInitialArrangement();
        }
        host.SetStatus($"YouTubeミニプレイヤーを{destination}へ戻しました（{targets.Count}件）");
        _ = host.RefreshListAsync();
    }

    private static void ApplyPlayers(IReadOnlyList<IYouTubeMiniPlayerArrangementItem> targets)
    {
        foreach (var item in targets)
        {
            item.ArrangementOwner.ApplyPlayer(item.State);
        }
        foreach (var owner in targets.Select(static item => item.ArrangementOwner).Distinct())
        {
            owner.QueueSave();
        }
    }

    public static YouTubeMiniPlayerArrangementPreviewBounds ToBounds(Rect bounds)
        => new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    public static IReadOnlyList<YouTubeMiniPlayerArrangementPreviewItem> CreatePreviewItems(YouTubeMiniPlayerArrangementPlan plan)
    {
        var byId = plan.Targets.ToDictionary(static item => item.State.Id);
        var minimumSizes = plan.Baselines.ToDictionary(static item => item.Snapshot.StateId, static item => item.MinimumSize);
        var items = new List<YouTubeMiniPlayerArrangementPreviewItem>(plan.Layout.Count * 2);
        foreach (var result in plan.Layout)
        {
            if (!byId.TryGetValue(result.Id, out var item))
            {
                continue;
            }
            var role = plan.Mode == YouTubeMiniPlayerArrangeMode.Focus
                ? (plan.FocusIds.Contains(result.Id) ? YouTubeMiniPlayerArrangementPreviewRole.Focus : YouTubeMiniPlayerArrangementPreviewRole.Secondary)
                : YouTubeMiniPlayerArrangementPreviewRole.Standard;
            var label = item.State.SourceKind == MiniPlayerSourceKind.LocalFile ? "ローカル" : item.State.IsShorts ? "Shorts" : "YouTube";
            items.Add(new(result.Id, result.Bounds, $"{label}: {item.Title}", role,
                YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(item.State), minimumSizes.GetValueOrDefault(result.Id)));
            if (result.ChatBounds is { } bounds)
            {
                items.Add(new(null, bounds, $"チャット: {item.Title}", YouTubeMiniPlayerArrangementPreviewRole.Chat));
            }
        }
        return items;
    }

    public static string GetModeText(YouTubeMiniPlayerArrangeMode mode, YouTubeMiniPlayerLinearPlacement placement)
        => mode switch
        {
            YouTubeMiniPlayerArrangeMode.Horizontal => placement switch
            {
                YouTubeMiniPlayerLinearPlacement.First => "横一列 1（上段）",
                YouTubeMiniPlayerLinearPlacement.Third => "横一列 3（下段）",
                _ => "横一列 2（中段）"
            },
            YouTubeMiniPlayerArrangeMode.Vertical => placement switch
            {
                YouTubeMiniPlayerLinearPlacement.First => "縦一列 1（左側）",
                YouTubeMiniPlayerLinearPlacement.Third => "縦一列 3（右側）",
                _ => "縦一列 2（中央）"
            },
            YouTubeMiniPlayerArrangeMode.Random => "ランダム",
            YouTubeMiniPlayerArrangeMode.Focus => "フォーカス配置",
            YouTubeMiniPlayerArrangeMode.Monitor => "監視配置",
            YouTubeMiniPlayerArrangeMode.NormalAndShorts => "通常＋Shorts配置",
            YouTubeMiniPlayerArrangeMode.Chat => "チャット付き配置",
            _ => "グリッド"
        };
}
