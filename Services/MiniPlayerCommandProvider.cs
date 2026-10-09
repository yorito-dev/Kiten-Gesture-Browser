using TabBrowser.Models;

namespace TabBrowser.Services;

public static class MiniPlayerCommandProvider
{
    public const string PlayPauseActionId = "miniPlayer.playPause";
    public const string ToggleMuteActionId = "miniPlayer.toggleMute";
    public const string SoloAudioActionId = "miniPlayer.soloAudio";
    public const string SoloPlaybackActionId = "miniPlayer.soloPlayback";
    public const string SeekRelativeActionId = "miniPlayer.seekRelative";
    public const string SeekToLiveEdgeActionId = "miniPlayer.seekToLiveEdge";
    public const string ToggleLoopActionId = "miniPlayer.toggleLoop";
    public const string SetSpeedActionId = "miniPlayer.setSpeed";
    public const string ExecuteAbRepeatActionId = "miniPlayer.executeAbRepeat";
    public const string ReloadActionId = "miniPlayer.reload";
    public const string ActivateSourceTabActionId = "miniPlayer.activateSourceTab";
    public const string ToggleMinimizedActionId = "miniPlayer.toggleMinimized";
    public const string ToggleTopmostActionId = "miniPlayer.toggleTopmost";
    public const string CloseActionId = "miniPlayer.close";

    public static IEnumerable<CommandAction> CreateActions(MiniPlayerCommandCallbacks callbacks)
    {
        yield return Create(
            PlayPauseActionId,
            "ミニプレイヤーを再生／一時停止",
            callbacks.PlayPauseAsync,
            true,
            "play",
            "pause",
            "再生",
            "一時停止");
        yield return Create(
            ToggleMuteActionId,
            "ミニプレイヤーのミュートを切り替える",
            callbacks.ToggleMuteAsync,
            true,
            "mute",
            "ミュート");
        yield return Create(
            SoloAudioActionId,
            "このミニプレイヤーだけ音声を出す",
            callbacks.SoloAudioAsync,
            true,
            "solo",
            "audio",
            "ソロ",
            "音声");
        yield return Create(
            SoloPlaybackActionId,
            "このミニプレイヤーだけ再生する",
            callbacks.SoloPlaybackAsync,
            true,
            "solo",
            "playback",
            "ソロ",
            "再生");
        yield return Create(
            SeekRelativeActionId,
            "ミニプレイヤーの再生位置を移動",
            callbacks.SeekRelativeAsync,
            false,
            "seek",
            "再生位置");
        yield return Create(
            SeekToLiveEdgeActionId,
            "ミニプレイヤーを現在のライブ位置へ移動",
            callbacks.SeekToLiveEdgeAsync,
            true,
            "live",
            "ライブ",
            "現在位置");
        yield return Create(
            ToggleLoopActionId,
            "ミニプレイヤーのループを切り替える",
            callbacks.ToggleLoopAsync,
            true,
            "loop",
            "ループ");
        yield return Create(
            SetSpeedActionId,
            "ミニプレイヤーの再生速度を変更",
            callbacks.SetSpeedAsync,
            false,
            "speed",
            "再生速度");
        yield return Create(
            ExecuteAbRepeatActionId,
            "ミニプレイヤーのA-Bリピートを操作",
            callbacks.ExecuteAbRepeatAsync,
            false,
            "repeat",
            "a-b",
            "リピート");
        yield return Create(
            ReloadActionId,
            "ミニプレイヤーを再読み込み",
            callbacks.ReloadAsync,
            true,
            "reload",
            "再読み込み");
        yield return Create(
            ActivateSourceTabActionId,
            "ミニプレイヤーの元タブを表示",
            callbacks.ActivateSourceTabAsync,
            true,
            "source",
            "tab",
            "元タブ");
        yield return Create(
            ToggleMinimizedActionId,
            "ミニプレイヤーの最小化を切り替える",
            callbacks.ToggleMinimizedAsync,
            true,
            "minimize",
            "restore",
            "最小化",
            "復帰");
        yield return Create(
            ToggleTopmostActionId,
            "ミニプレイヤーの常に最前面を切り替える",
            callbacks.ToggleTopmostAsync,
            true,
            "topmost",
            "最前面");
        yield return Create(
            CloseActionId,
            "ミニプレイヤーを閉じる",
            callbacks.CloseAsync,
            true,
            "close",
            "閉じる");
    }

    private static CommandAction Create(
        string id,
        string title,
        Func<CommandActionContext, Task> runAsync,
        bool showInPalette,
        params string[] keywords)
    {
        return new CommandAction(
            id,
            title,
            runAsync,
            keywords,
            showInPalette,
            category: "ミニプレイヤー",
            getAvailability: static context =>
                context.Surface == CommandSurface.MiniPlayer
                && context.TargetId.HasValue
                    ? CommandAvailability.Available
                    : CommandAvailability.Disabled(
                        "ミニプレイヤー一覧の対象を選択すると使用できます"));
    }
}

public sealed record MiniPlayerCommandCallbacks(
    Func<CommandActionContext, Task> PlayPauseAsync,
    Func<CommandActionContext, Task> ToggleMuteAsync,
    Func<CommandActionContext, Task> SoloAudioAsync,
    Func<CommandActionContext, Task> SoloPlaybackAsync,
    Func<CommandActionContext, Task> SeekRelativeAsync,
    Func<CommandActionContext, Task> SeekToLiveEdgeAsync,
    Func<CommandActionContext, Task> ToggleLoopAsync,
    Func<CommandActionContext, Task> SetSpeedAsync,
    Func<CommandActionContext, Task> ExecuteAbRepeatAsync,
    Func<CommandActionContext, Task> ReloadAsync,
    Func<CommandActionContext, Task> ActivateSourceTabAsync,
    Func<CommandActionContext, Task> ToggleMinimizedAsync,
    Func<CommandActionContext, Task> ToggleTopmostAsync,
    Func<CommandActionContext, Task> CloseAsync);
