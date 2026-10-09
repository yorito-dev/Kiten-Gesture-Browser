using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface ITabAudioHost
{
    bool ContainsTab(BrowserTab tab);
    bool HasAudioDocument(BrowserTab tab);
    Task ApplyToFramesAsync(BrowserTab tab, string script, ICollection<string?>? results = null);
    Task ApplyNativePanAsync(BrowserTab tab, TabAudioPan audioPan);
    void SetStatus(string message);
}

// 音声適用・失敗復元・再試行の手順を所有する。実Coreは呼出元が操作開始時に固定する。
internal sealed class TabAudioController(
    ITabAudioHost host,
    Func<int, CancellationToken, Task>? delayAsync = null)
{
    private static readonly int[] RetryDelaysMilliseconds = [200, 1000, 2500, 5000];
    private readonly Func<int, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;

    public async Task<bool> SetBoostAsync(
        BrowserTab tab, int audioBoostDecibels, Func<string, Task<string?>> executeWithGestureAsync)
    {
        var normalizedDecibels = AudioBoostProfile.Normalize(audioBoostDecibels);
        var previousDecibels = AudioBoostProfile.Normalize(tab.AudioBoostDecibels);
        var script = TabAudioScriptService.BuildTabAudioPanScript(tab.AudioPan, normalizedDecibels);
        try
        {
            var resultJson = await executeWithGestureAsync(script);
            var frameResultJsons = new List<string?>();
            await host.ApplyToFramesAsync(tab, script, frameResultJsons);
            var isApplied = normalizedDecibels == 0
                ? TabAudioScriptService.IsSuccessfulTabAudioBoostResult(resultJson, normalizedDecibels)
                  || frameResultJsons.Any(result =>
                      TabAudioScriptService.IsSuccessfulTabAudioBoostResult(result, normalizedDecibels))
                : TabAudioScriptService.HasAppliedTabAudioBoostResult(resultJson, normalizedDecibels)
                  || frameResultJsons.Any(result =>
                      TabAudioScriptService.HasAppliedTabAudioBoostResult(result, normalizedDecibels));
            if (!isApplied)
            {
                await RestoreBoostAfterFailureAsync(tab, executeWithGestureAsync, previousDecibels);
                host.SetStatus(
                    "このサイトではブーストできる音声・動画要素を確認できませんでした");
                return false;
            }

            tab.AudioBoostDecibels = normalizedDecibels;
            if (normalizedDecibels > 0 || tab.AudioPan != TabAudioPan.Stereo)
            {
                tab.IsAudioPanScriptInjected = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            await RestoreBoostAfterFailureAsync(tab, executeWithGestureAsync, previousDecibels);
            host.SetStatus("このタブへ音量ブーストを適用できません: " + ex.Message);
            return false;
        }
    }

    private async Task RestoreBoostAfterFailureAsync(
        BrowserTab tab,
        Func<string, Task<string?>> executeWithGestureAsync,
        int audioBoostDecibels)
    {
        var restoreScript = TabAudioScriptService.BuildTabAudioPanScript(tab.AudioPan, audioBoostDecibels);
        try
        {
            await executeWithGestureAsync(restoreScript);
            await host.ApplyToFramesAsync(tab, restoreScript);
        }
        catch
        {
            // 適用失敗時の復元もページ遷移などと競合し得るため、保持済みモデルを次回再適用する。
        }
    }

    public async Task ApplyPanAsync(
        BrowserTab tab, Func<string, Task> executeAsync, Func<string, Task> executeWithGestureAsync)
    {
        var audioBoostDecibels = tab.AudioBoostDecibels;
        if (tab.AudioPan != TabAudioPan.Stereo
            || audioBoostDecibels > 0
            || tab.IsAudioPanScriptInjected)
        {
            var script = TabAudioScriptService.BuildTabAudioPanScript(tab.AudioPan, audioBoostDecibels);

            try
            {
                if (audioBoostDecibels > 0)
                {
                    await executeWithGestureAsync(script);
                }
                else
                {
                    await executeAsync(script);
                }

                if (tab.AudioPan != TabAudioPan.Stereo)
                {
                    tab.IsAudioPanScriptInjected = true;
                }
            }
            catch
            {
                // ページ遷移中などで実行できない場合は次回の NavigationCompleted で再適用する。
            }

            await host.ApplyToFramesAsync(
                tab,
                TabAudioScriptService.BuildTabAudioPanScript(tab.AudioPan, audioBoostDecibels));
        }

        await host.ApplyNativePanAsync(tab, tab.AudioPan);
    }

    public async Task RetryNativePanAsync(
        BrowserTab tab,
        TabAudioPan expectedPan,
        CancellationToken cancellationToken)
    {
        foreach (var delay in RetryDelaysMilliseconds)
        {
            await _delayAsync(delay, cancellationToken);
            if (!host.ContainsTab(tab)
                || !host.HasAudioDocument(tab)
                || tab.AudioPan != expectedPan)
            {
                return;
            }

            await host.ApplyNativePanAsync(tab, expectedPan);
        }
    }
}
