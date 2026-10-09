using System.Collections.Generic;
using System.Windows.Threading;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerTimerService
{
    public static bool CanQueueSave(bool isInitialized, bool isClosingWindow)
    {
        return isInitialized && !isClosingWindow;
    }

    public static void QueueSave(
        DispatcherTimer saveTimer,
        bool isInitialized,
        bool isClosingWindow)
    {
        if (!CanQueueSave(isInitialized, isClosingWindow))
        {
            return;
        }

        saveTimer.Stop();
        saveTimer.Start();
    }

    public static void SaveNow(
        DispatcherTimer saveTimer,
        YouTubeMiniPlayerStore store,
        IEnumerable<YouTubeMiniPlayerState> players,
        bool isClosingWindow,
        Action<string> setStatus)
    {
        saveTimer.Stop();
        try
        {
            YouTubeMiniPlayerPersistenceService.SaveSnapshot(store, players);
        }
        catch (Exception ex)
        {
            if (!isClosingWindow)
            {
                setStatus("YouTubeミニプレイヤー状態の保存に失敗しました: " + ex.Message);
            }
        }
    }

    public static bool ShouldRunStateCaptureTimer(
        bool isClosingWindow,
        IEnumerable<YouTubeMiniPlayerRuntime> runtimes)
    {
        if (isClosingWindow)
        {
            return false;
        }

        foreach (var runtime in runtimes)
        {
            if (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not null
                || runtime.LocalVideoView?.Controller?.IsLoaded == true)
            {
                return true;
            }
        }

        return false;
    }

    public static void UpdateStateCaptureTimer(
        DispatcherTimer stateCaptureTimer,
        bool isClosingWindow,
        IEnumerable<YouTubeMiniPlayerRuntime> runtimes)
    {
        if (!ShouldRunStateCaptureTimer(isClosingWindow, runtimes))
        {
            stateCaptureTimer.Stop();
            return;
        }

        if (!stateCaptureTimer.IsEnabled)
        {
            stateCaptureTimer.Start();
        }
    }
}
