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
    void Apply(Rect bounds, bool isDocked, YouTubeLiveChatDockSide dockSide);
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
    Rect GetWorkArea();
    void EnsureListOpen();
    IYouTubeMiniPlayerArrangementPreview? Preview { get; set; }
    IYouTubeMiniPlayerArrangementPreview CreatePreview(YouTubeMiniPlayerArrangementPlan plan, IReadOnlyList<YouTubeMiniPlayerArrangementPreviewItem> items);
    void ShowPreviewBar(bool canEdit, string text);
    void HidePreviewBar(bool clearText);
    void SetStatus(string message);
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
    IReadOnlyList<YouTubeMiniPlayerArrangementBaseline> Baselines);

internal sealed record YouTubeMiniPlayerArrangementBaseline(
    IYouTubeMiniPlayerArrangementOwner Owner,
    YouTubeMiniPlayerArrangementPreviewValidationItem Snapshot);

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
        var workArea = host.GetWorkArea();
        var candidates = allItems.Select(item => item.ArrangementOwner.CreateCandidate(item.State)).ToList();
        var result = YouTubeMiniPlayerArrangementPlanningService.CreatePlan(
            mode, workArea, candidates, selectedIds, includeMinimized, placement);
        if (result.FailureReason != YouTubeMiniPlayerArrangementPlanningFailureReason.None)
        {
            host.SetStatus(result.FailureReason switch
            {
                YouTubeMiniPlayerArrangementPlanningFailureReason.NoEligibleTargets => "整列できるYouTubeミニプレイヤーがありません",
                YouTubeMiniPlayerArrangementPlanningFailureReason.NoLayout when mode == YouTubeMiniPlayerArrangeMode.Focus => "選択した動画を大きく配置できる空き領域がありません",
                _ => "配置できる空き領域がありません"
            });
            return null;
        }

        var itemsById = allItems.ToDictionary(static item => item.State.Id);
        var targets = result.TargetIds.Select(id => itemsById[id]).ToList();
        var baselines = targets.Select(item => new YouTubeMiniPlayerArrangementBaseline(
            item.ArrangementOwner, item.ArrangementOwner.CaptureValidation(item.State.Id, requireRuntime: true))).ToList();
        return new(mode, placement, workArea, allItems, targets, result.Layout, result.FocusIds, baselines);
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
        ClosePreview();
        session.RecordInitialArrangement(
            plan.AllItems.Select(item => item.ArrangementOwner.CaptureArrangement(item.State)).ToList(),
            plan.Targets.Select(item => item.ArrangementOwner.CaptureArrangement(item.State)).ToList());
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
            item.State.LiveChatWindowDockSide = YouTubeLiveChatDockSide.Right;
            item.State.UpdatedAt = _utcNow();
            chat.Apply(bounds, true, YouTubeLiveChatDockSide.Right);
        }
        host.SetStatus($"YouTubeミニプレイヤーを{GetModeText(plan.Mode, plan.LinearPlacement)}で整列しました（{plan.Targets.Count}件）");
        await host.RefreshListAsync();
        if (plan.Mode == YouTubeMiniPlayerArrangeMode.Random)
        {
            await RandomizePlaybackPositionsAsync(plan.Targets);
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

    public async Task RandomizePlaybackPositionsAsync(IReadOnlyList<IYouTubeMiniPlayerArrangementItem> targets)
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
        host.SetStatus(successful.Count > 0
            ? $"YouTubeミニプレイヤーをランダム配置し、再生位置もランダムに変更しました（{successful.Count}/{targets.Count}件）"
            : "YouTubeミニプレイヤーをランダム配置しました（再生位置を変更できる動画はありません）");
        await host.RefreshListAsync();
    }

    public void RestoreArrangement()
    {
        var targets = new List<IYouTubeMiniPlayerArrangementItem>();
        var updatedAt = _utcNow();
        var owners = host.GetOwners();
        foreach (var snapshot in session.SnapshotInitialArrangement())
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
                chat.Apply(new Rect(savedChat.Left, savedChat.Top, savedChat.Width, savedChat.Height), savedChat.IsDocked, savedChat.DockSide);
            }
        }
        if (targets.Count == 0)
        {
            host.SetStatus("戻せる最初の整列前配置がありません");
            return;
        }
        ApplyPlayers(targets);
        foreach (var item in targets)
        {
            item.ArrangementOwner.FindChat(item.State.Id)?.Synchronize();
        }
        session.ClearInitialArrangement();
        host.SetStatus($"YouTubeミニプレイヤーを最初の整列前配置へ戻しました（{targets.Count}件）");
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
            items.Add(new(result.Id, result.Bounds, $"{label}: {item.Title}", role, YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(item.State)));
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
