using TabBrowser.Models;

namespace TabBrowser.Services;

internal interface IStartupWindowRestoreHost
{
    bool IsClosingWindow { get; }
    void MarkRestoreStarted(int tabCount);
    void RestoreWindow(SessionWindowState state, TimeSpan activationDelay);
    void RecordRestoreFailure(Exception exception, int tabCount);
    void ReportSkippedWindows(int count);
}

internal interface IStartupTabActivationHost
{
    bool IsClosingWindow { get; }
    TimeSpan StartupActivationDelay { get; }
    BrowserTab? ActiveTab { get; }
    int TabCount { get; }
    int RestoreWarningCount { get; }
    Task ActivateSelectedTabAsync(CancellationToken cancellationToken);
    void MarkStartupPerformance(string stage, BrowserTab? tab, int? itemCount = null);
    void AppendSessionDiagnostic(string operation, Exception exception);
    void SetStatus(string message, bool autoClear = false);
    void QueueBackgroundTabLoading();
    void QueueBackgroundThumbnailWarmup();
    void RestartFocusedViewingCpuSaverTimer();
    void RestartTabMemoryManagementTimer();
    string? CreateRestoredTabLoadFailureStatus();
}

/// <summary>全Window枠の先行復元と、各Windowの選択タブ起動・完了報告を所有する。</summary>
internal sealed class StartupWindowRestoreController(
    IStartupWindowRestoreHost host,
    CancellationToken lifetimeToken,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    private readonly List<PendingWindowRestore> _pendingWindows = [];
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;
    private bool _isRestoring;

    public int PendingCount => _pendingWindows.Count;

    public void Queue(SessionWindowState state, TimeSpan activationDelay)
        => _pendingWindows.Add(new PendingWindowRestore(state, activationDelay));

    public void RestoreQueuedWindowShells()
    {
        if (_isRestoring || _pendingWindows.Count == 0)
        {
            return;
        }

        _isRestoring = true;
        // 実行時にsnapshotし、生成中に追加された項目は次回へ残す。
        var pendingWindows = _pendingWindows.ToList();
        _pendingWindows.Clear();
        var skippedWindowCount = 0;

        try
        {
            foreach (var pendingWindow in pendingWindows)
            {
                if (host.IsClosingWindow || lifetimeToken.IsCancellationRequested)
                {
                    return;
                }

                var tabCount = pendingWindow.State.Tabs?.Count ?? 0;
                host.MarkRestoreStarted(tabCount);

                try
                {
                    host.RestoreWindow(pendingWindow.State, pendingWindow.ActivationDelay);
                }
                catch (Exception ex)
                {
                    skippedWindowCount++;
                    host.RecordRestoreFailure(ex, pendingWindow.State.Tabs?.Count ?? 0);
                }
            }
        }
        finally
        {
            _isRestoring = false;
        }

        if (skippedWindowCount > 0 && !host.IsClosingWindow)
        {
            host.ReportSkippedWindows(skippedWindowCount);
        }
    }

    public async Task ActivateSelectedTabOnStartupAsync(
        IStartupTabActivationHost activationHost,
        SessionLoadInfo? sessionLoadInfo,
        int restoreWarningCountBefore,
        bool startupRestoreHadFailure,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || activationHost.IsClosingWindow)
        {
            return;
        }

        activationHost.MarkStartupPerformance(StartupPerformanceStage.SelectedTabActivationStarted, activationHost.ActiveTab);
        if (activationHost.StartupActivationDelay > TimeSpan.Zero)
        {
            try
            {
                await _delayAsync(activationHost.StartupActivationDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || activationHost.IsClosingWindow)
            {
                return;
            }
        }

        try
        {
            await activationHost.ActivateSelectedTabAsync(cancellationToken);
            activationHost.MarkStartupPerformance(StartupPerformanceStage.SelectedTabRuntimeReady, activationHost.ActiveTab);
        }
        catch (Exception ex)
        {
            startupRestoreHadFailure = true;
            activationHost.AppendSessionDiagnostic("startup-tab-activate-failed", ex);
            activationHost.SetStatus("復元したタブの読み込みに失敗しました: " + ex.Message, autoClear: true);
        }

        if (cancellationToken.IsCancellationRequested || activationHost.IsClosingWindow)
        {
            return;
        }

        activationHost.QueueBackgroundTabLoading();
        activationHost.QueueBackgroundThumbnailWarmup();
        activationHost.RestartFocusedViewingCpuSaverTimer();
        activationHost.RestartTabMemoryManagementTimer();
        startupRestoreHadFailure |= activationHost.RestoreWarningCount > restoreWarningCountBefore;
        var restoreWarningCount = activationHost.RestoreWarningCount - restoreWarningCountBefore;
        var startupSessionStatus = CreateStartupSessionStatus(sessionLoadInfo, restoreWarningCount);
        var restoredTabLoadFailureStatus = activationHost.CreateRestoredTabLoadFailureStatus();
        if (!string.IsNullOrWhiteSpace(restoredTabLoadFailureStatus)
            && !string.IsNullOrWhiteSpace(startupSessionStatus))
        {
            activationHost.SetStatus($"{startupSessionStatus} / {restoredTabLoadFailureStatus}");
        }
        else if (!string.IsNullOrWhiteSpace(restoredTabLoadFailureStatus))
        {
            activationHost.SetStatus(restoredTabLoadFailureStatus);
        }
        else if (!string.IsNullOrWhiteSpace(startupSessionStatus))
        {
            activationHost.SetStatus(startupSessionStatus, autoClear: true);
        }
        else if (!startupRestoreHadFailure)
        {
            activationHost.SetStatus("準備完了", autoClear: true);
        }

        activationHost.MarkStartupPerformance(StartupPerformanceStage.StartupReady, activationHost.ActiveTab, activationHost.TabCount);
    }

    internal static string? CreateStartupSessionStatus(SessionLoadInfo? loadInfo, int restoreWarningCount)
    {
        var messages = new List<string>();
        if (loadInfo is { Recovered: true })
        {
            messages.Add(loadInfo.Source switch
            {
                "backup" => "セッションをバックアップから復旧しました",
                "snapshot" => "セッションをスナップショットから復旧しました",
                "protected" => "異常終了前の保護復元点からセッションを復旧しました",
                _ => "セッションを復旧しました"
            });
        }
        else if (loadInfo is { Source: "empty", PrimaryLoadFailed: true })
        {
            messages.Add("セッションを復元できなかったため空の状態で開始しました");
        }

        if (loadInfo is { CorruptPrimaryPreservation: CorruptPrimaryPreservationStatus.Preserved })
            messages.Add("破損したセッション原本を別に保全しました");
        else if (loadInfo is { CorruptPrimaryPreservation: CorruptPrimaryPreservationStatus.Failed })
            messages.Add("破損したセッション原本を保全できませんでした");

        if (loadInfo is { RepairFailed: true })
        {
            messages.Add("復旧後の保存修復に失敗しました");
        }

        if (restoreWarningCount > 0)
        {
            messages.Add($"復元警告 {restoreWarningCount} 件");
        }

        return messages.Count == 0
            ? null
            : string.Join(" / ", messages);
    }

    internal static string? CreateRestoredTabLoadFailureStatus(int failedRestoredTabCount)
    {
        return failedRestoredTabCount == 0
            ? null
            : $"復元タブの読み込み失敗 {failedRestoredTabCount} 件。タブを開くと再読み込みできます";
    }

    private sealed record PendingWindowRestore(SessionWindowState State, TimeSpan ActivationDelay);
}
