using TabBrowser.Models;

namespace TabBrowser.Services;

public static class FeatureCommandProvider
{
    public const string OpenWorkspaceActionId = "workspace.open";
    public const string ToggleWorkspaceActionId = "workspace.toggle";
    public const string OpenWorkspaceWindowActionId = "workspace.openWindow";
    public const string OpenRecentItemsActionId = "workspace.openRecentItems";
    public const string FocusSplitLeftActionId = "split.focusLeft";
    public const string FocusSplitRightActionId = "split.focusRight";
    public const string SplitTabRightActionId = "split.openRight";
    public const string SplitTabBottomActionId = "split.openBottom";
    public const string SwapSplitPanesActionId = "split.swapPanes";
    public const string ToggleSplitOrientationActionId = "split.toggleOrientation";
    public const string DetachSplitActionId = "split.detach";
    public const string CloseSplitLeftActionId = "split.closeLeft";
    public const string CloseSplitRightActionId = "split.closeRight";
    public const string OpenMiniPlayerListActionId = "miniPlayer.openList";
    public const string OpenContextInMiniPlayerActionId = "miniPlayer.openContext";
    public const string MuteAllMiniPlayersActionId = "miniPlayer.muteAll";
    public const string UnmuteAllMiniPlayersActionId = "miniPlayer.unmuteAll";
    public const string PauseAllMiniPlayersActionId = "miniPlayer.pauseAll";
    public const string PlaySelectedMiniPlayersOnlyActionId = "miniPlayer.playSelectedOnly";
    public const string PauseOutsideSelectedMiniPlayersActionId = "miniPlayer.pauseOutsideSelection";
    public const string MuteOutsideSelectedMiniPlayersActionId = "miniPlayer.muteOutsideSelection";
    public const string RestoreMiniPlayerAudioFocusActionId = "miniPlayer.restoreAudioFocus";
    public const string MinimizeAllMiniPlayersActionId = "miniPlayer.minimizeAll";
    public const string RestoreAllMiniPlayersActionId = "miniPlayer.restoreAll";
    public const string CloseAllMiniPlayersActionId = "miniPlayer.closeAll";
    public const string ArrangeMiniPlayersGridActionId = "miniPlayer.arrangeGrid";
    public const string ArrangeMiniPlayersHorizontalActionId = "miniPlayer.arrangeHorizontal";
    public const string ArrangeMiniPlayersVerticalActionId = "miniPlayer.arrangeVertical";
    public const string ArrangeMiniPlayersRandomActionId = "miniPlayer.arrangeRandom";
    public const string ArrangeMiniPlayersFocusActionId = "miniPlayer.arrangeFocus";
    public const string ArrangeMiniPlayersMonitorActionId = "miniPlayer.arrangeMonitor";
    public const string ArrangeMiniPlayersNormalAndShortsActionId = "miniPlayer.arrangeNormalAndShorts";
    public const string ArrangeMiniPlayersChatActionId = "miniPlayer.arrangeChat";
    public const string RestoreMiniPlayerArrangementActionId = "miniPlayer.restoreArrangement";
    public const string OpenRuntimeDiagnosticsActionId = "runtimeDiagnostics.open";

    public static IEnumerable<CommandAction> CreateActions(
        Func<Task> openWorkspaceAsync,
        Func<Task> toggleWorkspaceAsync,
        Func<Task> openWorkspaceWindowAsync,
        Func<Task> focusSplitLeftAsync,
        Func<Task> focusSplitRightAsync,
        Func<CommandActionContext, Task> splitTabRightAsync,
        Func<CommandActionContext, Task> splitTabBottomAsync,
        Func<Task> swapSplitPanesAsync,
        Func<Task> toggleSplitOrientationAsync,
        Func<Task> detachSplitAsync,
        Func<Task> closeSplitLeftAsync,
        Func<Task> closeSplitRightAsync,
        Func<Task> openMiniPlayerListAsync,
        Func<CommandActionContext, Task> openContextInMiniPlayerAsync,
        Func<Task> muteAllMiniPlayersAsync,
        Func<Task> unmuteAllMiniPlayersAsync,
        Func<Task> pauseAllMiniPlayersAsync,
        Func<Task> playSelectedMiniPlayersOnlyAsync,
        Func<Task> pauseOutsideSelectedMiniPlayersAsync,
        Func<Task> muteOutsideSelectedMiniPlayersAsync,
        Func<Task> restoreMiniPlayerAudioFocusAsync,
        Func<Task> minimizeAllMiniPlayersAsync,
        Func<Task> restoreAllMiniPlayersAsync,
        Func<Task> closeAllMiniPlayersAsync,
        Func<Task> arrangeMiniPlayersGridAsync,
        Func<Task> arrangeMiniPlayersHorizontalAsync,
        Func<Task> arrangeMiniPlayersVerticalAsync,
        Func<Task> arrangeMiniPlayersRandomAsync,
        Func<Task> arrangeMiniPlayersFocusAsync,
        Func<Task> arrangeMiniPlayersMonitorAsync,
        Func<Task> arrangeMiniPlayersNormalAndShortsAsync,
        Func<Task> arrangeMiniPlayersChatAsync,
        Func<Task> restoreMiniPlayerArrangementAsync,
        Func<Task> openRuntimeDiagnosticsAsync)
    {
        yield return Create(
            OpenWorkspaceActionId,
            "Workspace OSを開く",
            openWorkspaceAsync,
            "Workspace",
            null,
            ["ShowWorkspace"],
            "workspace",
            "ワークスペース");
        yield return Create(
            ToggleWorkspaceActionId,
            "Workspace OSを開く／元のタブへ戻る",
            toggleWorkspaceAsync,
            "Workspace",
            null,
            ["ToggleWorkspace"],
            "workspace",
            "toggle",
            "ワークスペース",
            "切り替え",
            "元のタブ");
        yield return Create(
            OpenWorkspaceWindowActionId,
            "Workspace OSを別ウィンドウで開く",
            openWorkspaceWindowAsync,
            "Workspace",
            null,
            ["ShowWorkspaceWindow"],
            "workspace",
            "window",
            "別ウィンドウ");
        yield return CreateSplit(
            FocusSplitLeftActionId,
            "分割表示の左ペインへ移動",
            focusSplitLeftAsync,
            "split",
            "left",
            "分割");
        yield return CreateSplit(
            FocusSplitRightActionId,
            "分割表示の右ペインへ移動",
            focusSplitRightAsync,
            "split",
            "right",
            "分割");
        yield return CreatePageOrFolder(
            SplitTabRightActionId,
            "選択タブを右側に分割表示",
            splitTabRightAsync,
            "split",
            "right",
            "tab",
            "分割");
        yield return CreatePageOrFolder(
            SplitTabBottomActionId,
            "選択タブを下側に分割表示",
            splitTabBottomAsync,
            "split",
            "bottom",
            "tab",
            "分割");
        yield return CreateSplit(
            SwapSplitPanesActionId,
            "分割表示のペインを入れ替える",
            swapSplitPanesAsync,
            "split",
            "swap",
            "入れ替え");
        yield return CreateSplit(
            ToggleSplitOrientationActionId,
            "分割表示の向きを切り替える",
            toggleSplitOrientationAsync,
            "split",
            "orientation",
            "分割");
        yield return CreateSplit(
            DetachSplitActionId,
            "分割表示を解除",
            detachSplitAsync,
            "split",
            "detach",
            "分割");
        yield return CreateSplit(
            CloseSplitLeftActionId,
            "分割表示の左ペインを閉じる",
            closeSplitLeftAsync,
            "split",
            "close",
            "left",
            "分割");
        yield return CreateSplit(
            CloseSplitRightActionId,
            "分割表示の右ペインを閉じる",
            closeSplitRightAsync,
            "split",
            "close",
            "right",
            "分割");
        yield return CreateMiniPlayer(
            OpenMiniPlayerListActionId,
            "YouTubeミニプレイヤー一覧を開く",
            openMiniPlayerListAsync,
            "youtube",
            "mini",
            "player",
            "一覧");
        yield return new CommandAction(
            OpenContextInMiniPlayerActionId,
            "現在の動画またはリンクをミニプレイヤーで開く",
            openContextInMiniPlayerAsync,
            ["youtube", "mini", "player", "link", "動画", "リンク", "開く"],
            category: "ミニプレイヤー",
            getAvailability: static context => context.Surface == CommandSurface.Web
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("Webページ上で使用できます"));
        yield return CreateMiniPlayer(
            MuteAllMiniPlayersActionId,
            "YouTubeミニプレイヤーをすべてミュート",
            muteAllMiniPlayersAsync,
            "youtube",
            "mini",
            "mute",
            "ミュート");
        yield return CreateMiniPlayer(
            UnmuteAllMiniPlayersActionId,
            "YouTubeミニプレイヤーのミュートをすべて解除",
            unmuteAllMiniPlayersAsync,
            "youtube",
            "mini",
            "unmute",
            "ミュート解除");
        yield return CreateMiniPlayer(
            PauseAllMiniPlayersActionId,
            "YouTubeミニプレイヤーをすべて一時停止",
            pauseAllMiniPlayersAsync,
            "youtube",
            "mini",
            "pause",
            "一時停止");
        yield return CreateMiniPlayer(
            PlaySelectedMiniPlayersOnlyActionId,
            "選択したミニプレイヤーだけ再生",
            playSelectedMiniPlayersOnlyAsync,
            "youtube",
            "mini",
            "selected",
            "play",
            "選択",
            "再生");
        yield return CreateMiniPlayer(
            PauseOutsideSelectedMiniPlayersActionId,
            "選択外のミニプレイヤーを一時停止",
            pauseOutsideSelectedMiniPlayersAsync,
            "youtube",
            "mini",
            "selected",
            "pause",
            "選択外",
            "一時停止");
        yield return CreateMiniPlayer(
            MuteOutsideSelectedMiniPlayersActionId,
            "選択外のミニプレイヤーを一時ミュート",
            muteOutsideSelectedMiniPlayersAsync,
            "youtube",
            "mini",
            "selected",
            "mute",
            "選択外",
            "ミュート");
        yield return CreateMiniPlayer(
            RestoreMiniPlayerAudioFocusActionId,
            "ミニプレイヤーの音声フォーカスを解除",
            restoreMiniPlayerAudioFocusAsync,
            "youtube",
            "mini",
            "audio",
            "focus",
            "restore",
            "音声フォーカス",
            "解除");
        yield return CreateMiniPlayer(
            MinimizeAllMiniPlayersActionId,
            "YouTubeミニプレイヤーをすべて最小化",
            minimizeAllMiniPlayersAsync,
            "youtube",
            "mini",
            "minimize",
            "最小化");
        yield return CreateMiniPlayer(
            RestoreAllMiniPlayersActionId,
            "YouTubeミニプレイヤーをすべて復帰",
            restoreAllMiniPlayersAsync,
            "youtube",
            "mini",
            "restore",
            "復帰");
        yield return CreateMiniPlayer(
            CloseAllMiniPlayersActionId,
            "YouTubeミニプレイヤーをすべて閉じる",
            closeAllMiniPlayersAsync,
            "youtube",
            "mini",
            "close",
            "すべて閉じる");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersGridActionId,
            "YouTubeミニプレイヤーをグリッド整列",
            arrangeMiniPlayersGridAsync,
            "youtube",
            "mini",
            "arrange",
            "grid",
            "グリッド");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersHorizontalActionId,
            "YouTubeミニプレイヤーを横一列に整列",
            arrangeMiniPlayersHorizontalAsync,
            "youtube",
            "mini",
            "arrange",
            "horizontal",
            "横一列");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersVerticalActionId,
            "YouTubeミニプレイヤーを縦一列に整列",
            arrangeMiniPlayersVerticalAsync,
            "youtube",
            "mini",
            "arrange",
            "vertical",
            "縦一列");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersRandomActionId,
            "YouTubeミニプレイヤーをランダム配置",
            arrangeMiniPlayersRandomAsync,
            "youtube",
            "mini",
            "arrange",
            "random",
            "ランダム");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersFocusActionId,
            "YouTubeミニプレイヤーをフォーカス配置",
            arrangeMiniPlayersFocusAsync,
            "youtube",
            "mini",
            "arrange",
            "focus",
            "フォーカス");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersMonitorActionId,
            "YouTubeミニプレイヤーを監視配置",
            arrangeMiniPlayersMonitorAsync,
            "youtube",
            "mini",
            "arrange",
            "monitor",
            "監視");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersNormalAndShortsActionId,
            "YouTubeミニプレイヤーを通常＋Shorts配置",
            arrangeMiniPlayersNormalAndShortsAsync,
            "youtube",
            "mini",
            "arrange",
            "shorts",
            "通常",
            "縦動画");
        yield return CreateMiniPlayer(
            ArrangeMiniPlayersChatActionId,
            "YouTubeミニプレイヤーをチャット付き配置",
            arrangeMiniPlayersChatAsync,
            "youtube",
            "mini",
            "arrange",
            "chat",
            "チャット");
        yield return CreateMiniPlayer(
            RestoreMiniPlayerArrangementActionId,
            "YouTubeミニプレイヤーを最初の配置へ戻す",
            restoreMiniPlayerArrangementAsync,
            "youtube",
            "mini",
            "arrange",
            "restore",
            "最初に戻す");
        yield return Create(
            OpenRuntimeDiagnosticsActionId,
            "バージョン情報・Runtime診断を開く",
            openRuntimeDiagnosticsAsync,
            "管理",
            null,
            null,
            "version",
            "バージョン",
            "版情報",
            "runtime",
            "diagnostic",
            "診断",
            "長時間計測");
    }

    private static CommandAction Create(
        string id,
        string title,
        Func<Task> runAsync,
        string category,
        IEnumerable<string>? shortcuts,
        IEnumerable<string>? gestures,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            _ => runAsync(),
            keywords,
            category: category,
            shortcuts: shortcuts,
            gestureCommandIds: gestures);
    }

    private static CommandAction CreateSplit(
        string id,
        string title,
        Func<Task> runAsync,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            _ => runAsync(),
            keywords,
            category: "分割表示",
            getAvailability: static context => context.IsSplitView
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("分割表示中に使用できます"));
    }

    private static CommandAction CreateMiniPlayer(
        string id,
        string title,
        Func<Task> runAsync,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            _ => runAsync(),
            keywords,
            category: "ミニプレイヤー",
            getAvailability: static context => context.HasMiniPlayers
                ? CommandAvailability.Available
                : CommandAvailability.Disabled("起動中のミニプレイヤーがありません"));
    }

    private static CommandAction CreatePageOrFolder(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            runAsync,
            keywords,
            category: "分割表示",
            getAvailability: static context =>
                context.Surface is CommandSurface.Web or CommandSurface.FolderTab
                    ? CommandAvailability.Available
                    : CommandAvailability.Disabled("Webページまたはフォルダータブで使用できます"));
    }
}
