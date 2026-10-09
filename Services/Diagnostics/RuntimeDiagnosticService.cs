using System.Diagnostics;

namespace TabBrowser.Services;

public enum RuntimeDiagnosticSeverity
{
    Normal,
    Warning,
    Error
}

public sealed record RuntimeDiagnosticIssue(
    RuntimeDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? WindowIndex = null,
    Guid? TabId = null);

public sealed record RuntimeYouTubePlaybackDiagnosticEntry(
    DateTimeOffset ObservedAt,
    int WindowIndex,
    string SourceKind,
    string TargetId,
    string EventName,
    string Operation,
    double? PositionSeconds,
    int? ReadyState,
    int? NetworkState,
    bool? IsPaused,
    double? BufferedAheadSeconds,
    long? TotalVideoFrames,
    long? DroppedVideoFrames,
    long? CorruptedVideoFrames,
    long? StallDurationMilliseconds);

public sealed record RuntimeMiniPlayerFrameSample(
    DateTimeOffset ObservedAt,
    int WindowIndex,
    string TargetId,
    long? TotalVideoFrames,
    long? DroppedVideoFrames,
    long? CorruptedVideoFrames);

public sealed record RuntimeDiagnosticTabState(
    Guid TabId,
    WebViewLifecycleState State,
    string? InvariantViolation);

public sealed record RuntimeWindowOwnershipState(
    int FolderTabViewCount = 0,
    int HostedFolderTabViewCount = 0,
    int OrphanedHostedFolderTabViewCount = 0,
    int DisposedRegisteredFolderTabViewCount = 0,
    int OpenQuickLookCount = 0,
    int HiddenQuickLookActiveLoadCount = 0,
    int DisposedActiveQuickLookCount = 0,
    int OrphanedPopupCount = 0,
    int OpenLiveChatWindowCount = 0,
    int OrphanedLiveChatWindowCount = 0,
    int OrphanedPendingLiveChatCount = 0)
{
    public string FloatingMediaHostLifetimeState { get; init; } = "Visible";

    public bool IsHiddenFloatingMediaHostActive { get; init; }
}

public sealed record RuntimeApplicationOwnershipState(
    int RegisteredModelessWindowCount = 0,
    int StaleModelessWindowCount = 0,
    int ClosedOwnerModelessWindowCount = 0,
    int IndependentWorkspaceWindowCount = 0,
    int IndependentOpenQuickLookCount = 0,
    int IndependentHiddenQuickLookActiveLoadCount = 0,
    int IndependentDisposedActiveQuickLookCount = 0)
{
    public QuickLookPdfEnvironmentOwner QuickLookPdfEnvironmentOwner { get; init; } =
        QuickLookPdfEnvironmentOwner.Process;

    public QuickLookPdfEnvironmentState QuickLookPdfEnvironmentState { get; init; } =
        QuickLookPdfEnvironmentState.Empty;

    public int QuickLookPdfEnvironmentCreationAttemptCount { get; init; }
}

public sealed record RuntimeDiagnosticWindowState(
    Guid WindowId,
    int WindowIndex,
    bool IsClosing,
    int TabCount,
    int WorkspaceTabCount,
    int TabWebViewCount,
    IReadOnlyList<RuntimeDiagnosticTabState> Tabs,
    int SplitViewCount,
    int InvalidSplitReferenceCount,
    int MiniPlayerCount,
    int MiniPlayerWebViewCount,
    int RegisteredDisposedMiniPlayerCount,
    int MiniPlayerHealthyCount,
    int MiniPlayerSuspectedCount,
    int MiniPlayerReloadRecommendedCount,
    int OpenPopupCount,
    int AuxiliaryWebViewCount,
    bool IsBackgroundLoading,
    bool IsWarmingBackgroundThumbnails)
{
    public RuntimeWindowOwnershipState Ownership { get; init; } = new();

    public int MiniPlayerStateWithoutRuntimeCount { get; init; }

    public int MiniPlayerRuntimeWithoutStateCount { get; init; }

    public int SuspendedTabCount { get; init; }

    public int MemoryReleasedTabCount { get; init; }

    public int UninitializedWebTabCount { get; init; }

    public int TabMemoryReleaseCount { get; init; }

    public int TabMemoryRestoreCount { get; init; }

    public int TabMemoryReleaseSkipCount { get; init; }

    public int InactiveYouTubeTabSuspendCount { get; init; }

    public int InactiveYouTubeTabLoadStopCount { get; init; }

    public int InactiveYouTubeTabResumeCount { get; init; }

    public int InactiveYouTubeMiniPlayerSuspendCount { get; init; }

    public int InactiveYouTubeMiniPlayerLoadStopCount { get; init; }

    public int InactiveYouTubeMiniPlayerResumeCount { get; init; }

    public int TotalWebViewCount => TabWebViewCount + MiniPlayerWebViewCount + AuxiliaryWebViewCount;
}

public sealed record RuntimeProcessMetrics(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    int HandleCount)
{
    public TimeSpan TotalProcessorTime { get; init; }
}

public sealed record RuntimeWebViewProcessSample(
    int ProcessId,
    string Kind,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    int HandleCount);

public sealed record RuntimeWebViewProcessMetrics(
    int ProcessCount = 0,
    int BrowserProcessCount = 0,
    int RendererProcessCount = 0,
    int UtilityProcessCount = 0,
    int GpuProcessCount = 0,
    int OtherProcessCount = 0,
    long WorkingSetBytes = 0,
    long PrivateMemoryBytes = 0,
    int ThreadCount = 0,
    int HandleCount = 0,
    int UnavailableProcessCount = 0)
{
    public TimeSpan TotalProcessorTime { get; init; }

    public IReadOnlyList<RuntimeWebViewProcessSample> ProcessSamples { get; init; } = [];
}

public sealed record RuntimeDiagnosticSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<RuntimeDiagnosticWindowState> Windows,
    InputInteractionSession? CurrentInput,
    IReadOnlyList<InputInteractionDiagnosticEntry> InputHistory,
    RuntimeProcessMetrics Process,
    IReadOnlyList<RuntimeDiagnosticIssue> Issues)
{
    public IReadOnlyList<WebViewProcessRecoveryDiagnosticEntry> WebViewProcessRecoveryHistory { get; init; } = [];

    public IReadOnlyList<RuntimeYouTubePlaybackDiagnosticEntry> YouTubePlaybackDiagnostics { get; init; } = [];

    public IReadOnlyList<RuntimeMiniPlayerFrameSample> YouTubeMiniPlayerFrameSamples { get; init; } = [];

    public RuntimeApplicationOwnershipState ApplicationOwnership { get; init; } = new();

    public RuntimeWebViewProcessMetrics WebViewProcesses { get; init; } = new();

    public SessionRecoveryDiagnosticSnapshot SessionRecovery { get; init; } =
        SessionRecoveryDiagnosticSnapshot.Unknown;

    public int WindowCount => Windows.Count;

    public int TotalTabCount => Windows.Sum(static window => window.TabCount);

    public int TotalWorkspaceTabCount => Windows.Sum(static window => window.WorkspaceTabCount);

    public int TotalWebViewCount => Windows.Sum(static window => window.TotalWebViewCount);

    public int TotalMiniPlayerCount => Windows.Sum(static window => window.MiniPlayerCount);

    public int TotalSuspendedTabCount => Windows.Sum(static window => window.SuspendedTabCount);

    public int TotalMemoryReleasedTabCount => Windows.Sum(static window => window.MemoryReleasedTabCount);

    public int TotalUninitializedWebTabCount => Windows.Sum(static window => window.UninitializedWebTabCount);

    public int TotalTabMemoryReleaseCount => Windows.Sum(static window => window.TabMemoryReleaseCount);

    public int TotalTabMemoryRestoreCount => Windows.Sum(static window => window.TabMemoryRestoreCount);

    public int TotalTabMemoryReleaseSkipCount => Windows.Sum(static window => window.TabMemoryReleaseSkipCount);

    public int TotalInactiveYouTubeTabSuspendCount =>
        Windows.Sum(static window => window.InactiveYouTubeTabSuspendCount);

    public int TotalInactiveYouTubeTabLoadStopCount =>
        Windows.Sum(static window => window.InactiveYouTubeTabLoadStopCount);

    public int TotalInactiveYouTubeTabResumeCount =>
        Windows.Sum(static window => window.InactiveYouTubeTabResumeCount);

    public int TotalInactiveYouTubeMiniPlayerSuspendCount =>
        Windows.Sum(static window => window.InactiveYouTubeMiniPlayerSuspendCount);

    public int TotalInactiveYouTubeMiniPlayerLoadStopCount =>
        Windows.Sum(static window => window.InactiveYouTubeMiniPlayerLoadStopCount);

    public int TotalInactiveYouTubeMiniPlayerResumeCount =>
        Windows.Sum(static window => window.InactiveYouTubeMiniPlayerResumeCount);

    public int TotalOpenQuickLookCount =>
        Windows.Sum(static window => window.Ownership.OpenQuickLookCount)
        + ApplicationOwnership.IndependentOpenQuickLookCount;

    public int TotalOpenLiveChatWindowCount =>
        Windows.Sum(static window => window.Ownership.OpenLiveChatWindowCount);

    public long TotalWorkingSetBytes => Process.WorkingSetBytes + WebViewProcesses.WorkingSetBytes;

    public long TotalPrivateMemoryBytes => Process.PrivateMemoryBytes + WebViewProcesses.PrivateMemoryBytes;

    public int ErrorCount => Issues.Count(static issue => issue.Severity == RuntimeDiagnosticSeverity.Error);

    public int WarningCount => Issues.Count(static issue => issue.Severity == RuntimeDiagnosticSeverity.Warning);

    public RuntimeDiagnosticSeverity Severity => ErrorCount > 0
        ? RuntimeDiagnosticSeverity.Error
        : WarningCount > 0
            ? RuntimeDiagnosticSeverity.Warning
            : RuntimeDiagnosticSeverity.Normal;
}

public sealed class RuntimeDiagnosticService
{
    public static readonly TimeSpan LongRunningInputWarningThreshold = TimeSpan.FromSeconds(30);

    public static RuntimeProcessMetrics CaptureCurrentProcessMetrics()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new RuntimeProcessMetrics(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count,
                process.HandleCount)
            {
                TotalProcessorTime = process.TotalProcessorTime
            };
        }
        catch
        {
            return new RuntimeProcessMetrics(0, 0, 0, 0);
        }
    }

    public static TimeSpan CaptureCurrentProcessUptime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return DateTime.Now - process.StartTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public RuntimeDiagnosticSnapshot CreateSnapshot(
        DateTimeOffset capturedAt,
        IEnumerable<RuntimeDiagnosticWindowState> windows,
        InputInteractionSession? currentInput,
        IEnumerable<InputInteractionDiagnosticEntry>? inputHistory,
        RuntimeProcessMetrics process,
        RuntimeApplicationOwnershipState? applicationOwnership = null,
        SessionRecoveryDiagnosticSnapshot? sessionRecovery = null)
    {
        var windowList = windows.ToList();
        var issues = new List<RuntimeDiagnosticIssue>();
        foreach (var window in windowList)
        {
            AddWindowIssues(window, issues);
        }

        if (currentInput is not null
            && capturedAt - currentInput.StartedAt > LongRunningInputWarningThreshold)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "input.long-running",
                $"入力所有権が{LongRunningInputWarningThreshold.TotalSeconds:0}秒を超えて継続しています",
                TabId: currentInput.TabId));
        }

        var normalizedApplicationOwnership = applicationOwnership ?? new RuntimeApplicationOwnershipState();
        var normalizedSessionRecovery = sessionRecovery ?? SessionRecoveryDiagnosticSnapshot.Unknown;
        AddApplicationOwnershipIssues(normalizedApplicationOwnership, issues);
        AddSessionRecoveryIssues(normalizedSessionRecovery, issues);
        return new RuntimeDiagnosticSnapshot(
            capturedAt,
            windowList,
            currentInput,
            inputHistory?.ToList() ?? [],
            process,
            issues)
        {
            ApplicationOwnership = normalizedApplicationOwnership,
            SessionRecovery = normalizedSessionRecovery
        };
    }

    public static RuntimeWebViewProcessMetrics CaptureWebViewProcessMetrics(
        IEnumerable<WebViewProcessDiagnosticInfo> processInfos)
    {
        var uniqueProcesses = processInfos
            .Where(static info => info.ProcessId > 0)
            .GroupBy(static info => info.ProcessId)
            .Select(static group => group.First())
            .ToList();
        var browserCount = 0;
        var rendererCount = 0;
        var utilityCount = 0;
        var gpuCount = 0;
        var otherCount = 0;
        long workingSetBytes = 0;
        long privateMemoryBytes = 0;
        var threadCount = 0;
        var handleCount = 0;
        var unavailableCount = 0;
        var totalProcessorTime = TimeSpan.Zero;
        var processSamples = new List<RuntimeWebViewProcessSample>(uniqueProcesses.Count);

        foreach (var processInfo in uniqueProcesses)
        {
            switch (processInfo.Kind)
            {
                case "Browser":
                    browserCount++;
                    break;
                case "Renderer":
                    rendererCount++;
                    break;
                case "Utility":
                    utilityCount++;
                    break;
                case "Gpu":
                    gpuCount++;
                    break;
                default:
                    otherCount++;
                    break;
            }

            try
            {
                using var process = Process.GetProcessById(processInfo.ProcessId);
                process.Refresh();
                workingSetBytes += process.WorkingSet64;
                privateMemoryBytes += process.PrivateMemorySize64;
                threadCount += process.Threads.Count;
                handleCount += process.HandleCount;
                totalProcessorTime += process.TotalProcessorTime;
                processSamples.Add(new RuntimeWebViewProcessSample(
                    processInfo.ProcessId,
                    processInfo.Kind,
                    process.TotalProcessorTime,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.Threads.Count,
                    process.HandleCount));
            }
            catch
            {
                unavailableCount++;
            }
        }

        return new RuntimeWebViewProcessMetrics(
            uniqueProcesses.Count,
            browserCount,
            rendererCount,
            utilityCount,
            gpuCount,
            otherCount,
            workingSetBytes,
            privateMemoryBytes,
            threadCount,
            handleCount,
            unavailableCount)
        {
            TotalProcessorTime = totalProcessorTime,
            ProcessSamples = processSamples
        };
    }

    private static void AddWindowIssues(
        RuntimeDiagnosticWindowState window,
        ICollection<RuntimeDiagnosticIssue> issues)
    {
        foreach (var tab in window.Tabs.Where(static tab =>
                     tab.State == WebViewLifecycleState.Inconsistent
                     || !string.IsNullOrWhiteSpace(tab.InvariantViolation)))
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "webview.inconsistent",
                string.IsNullOrWhiteSpace(tab.InvariantViolation)
                    ? "WebView2のライフサイクル状態が不整合です"
                    : tab.InvariantViolation,
                window.WindowIndex,
                tab.TabId));
        }

        if (window.InvalidSplitReferenceCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "split.invalid-reference",
                $"分割表示に無効なタブ参照があります（{window.InvalidSplitReferenceCount}件）",
                window.WindowIndex));
        }

        if (window.RegisteredDisposedMiniPlayerCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "mini-player.disposed-registered",
                $"破棄済みミニプレイヤーRuntimeが登録されています（{window.RegisteredDisposedMiniPlayerCount}件）",
                window.WindowIndex));
        }

        if (window.MiniPlayerStateWithoutRuntimeCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "mini-player.state-without-runtime",
                $"対応するRuntimeがないミニプレイヤー状態があります（{window.MiniPlayerStateWithoutRuntimeCount}件）",
                window.WindowIndex));
        }

        if (window.MiniPlayerRuntimeWithoutStateCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "mini-player.runtime-without-state",
                $"対応する状態がないミニプレイヤーRuntimeがあります（{window.MiniPlayerRuntimeWithoutStateCount}件）",
                window.WindowIndex));
        }

        if (window.IsClosing && (window.IsBackgroundLoading || window.IsWarmingBackgroundThumbnails))
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "window.closing-background-work",
                "終了中Windowでバックグラウンド処理が継続しています",
                window.WindowIndex));
        }

        if (window.MiniPlayerReloadRecommendedCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "mini-player.reload-recommended",
                $"再読み込み推奨のミニプレイヤーがあります（{window.MiniPlayerReloadRecommendedCount}件）",
                window.WindowIndex));
        }

        if (window.MiniPlayerSuspectedCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "mini-player.suspected",
                $"描画状態を確認中のミニプレイヤーがあります（{window.MiniPlayerSuspectedCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.OrphanedHostedFolderTabViewCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "folder.orphaned-view",
                $"所有タブがないFolderTabViewがホストに残っています（{window.Ownership.OrphanedHostedFolderTabViewCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.DisposedRegisteredFolderTabViewCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "folder.disposed-view-registered",
                $"破棄済みFolderTabViewがタブに登録されています（{window.Ownership.DisposedRegisteredFolderTabViewCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.HiddenQuickLookActiveLoadCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "quicklook.hidden-active-load",
                $"非表示のQuickLookで読み込みが継続しています（{window.Ownership.HiddenQuickLookActiveLoadCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.DisposedActiveQuickLookCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "quicklook.active-after-dispose",
                $"破棄済みQuickLookに表示または読み込み状態が残っています（{window.Ownership.DisposedActiveQuickLookCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.OrphanedPopupCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "popup.owner-unavailable",
                $"利用できないWindowにPopupが残っています（{window.Ownership.OrphanedPopupCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.OrphanedLiveChatWindowCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "mini-player.orphaned-chat",
                $"対応するミニプレイヤーがないチャットWindowが残っています（{window.Ownership.OrphanedLiveChatWindowCount}件）",
                window.WindowIndex));
        }

        if (window.Ownership.OrphanedPendingLiveChatCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "mini-player.orphaned-chat-open",
                $"終了済みミニプレイヤーのチャット初期化が残っています（{window.Ownership.OrphanedPendingLiveChatCount}件）",
                window.WindowIndex));
        }
    }

    private static void AddApplicationOwnershipIssues(
        RuntimeApplicationOwnershipState ownership,
        ICollection<RuntimeDiagnosticIssue> issues)
    {
        if (ownership.StaleModelessWindowCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "modeless.stale-window",
                $"終了済みのモデルレスWindowが登録されています（{ownership.StaleModelessWindowCount}件）"));
        }

        if (ownership.ClosedOwnerModelessWindowCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "modeless.owner-closed",
                $"終了済みOwnerに属するモデルレスWindowが残っています（{ownership.ClosedOwnerModelessWindowCount}件）"));
        }

        if (ownership.IndependentHiddenQuickLookActiveLoadCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "quicklook.independent-hidden-active-load",
                $"非表示のWorkspace QuickLookで読み込みが継続しています（{ownership.IndependentHiddenQuickLookActiveLoadCount}件）"));
        }

        if (ownership.IndependentDisposedActiveQuickLookCount > 0)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "quicklook.independent-active-after-dispose",
                $"破棄済みWorkspace QuickLookに状態が残っています（{ownership.IndependentDisposedActiveQuickLookCount}件）"));
        }
    }

    private static void AddSessionRecoveryIssues(
        SessionRecoveryDiagnosticSnapshot sessionRecovery,
        ICollection<RuntimeDiagnosticIssue> issues)
    {
        if (sessionRecovery.NormalSave.LastResult == SessionPersistenceDiagnosticResult.Failed)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Error,
                "session.normal-save.failed",
                "通常セッション保存の直近試行に失敗しました"));
        }

        if (sessionRecovery.AutomaticSnapshot.LastResult == SessionPersistenceDiagnosticResult.Failed)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "session.snapshot.failed",
                "通常保存は完了しましたが、自動snapshotの直近作成に失敗しました"));
        }

        if (sessionRecovery.ProtectedRecoveryPoint.LastResult == SessionPersistenceDiagnosticResult.Failed)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "session.protected-recovery.failed",
                "通常保存とは別の保護復元点の直近作成に失敗しました"));
        }

        if (sessionRecovery.AutomaticSnapshotCleanup.LastResult == SessionPersistenceDiagnosticResult.Failed)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "session.snapshot-cleanup.failed",
                "自動snapshotの世代整理に失敗しました。作成済みsnapshotの作成結果とは別です"));
        }

        if (sessionRecovery.ProtectedRecoveryPointCleanup.LastResult == SessionPersistenceDiagnosticResult.Failed)
        {
            issues.Add(new RuntimeDiagnosticIssue(
                RuntimeDiagnosticSeverity.Warning,
                "session.protected-recovery-cleanup.failed",
                "保護復元点の世代整理に失敗しました。作成済み復元点の作成結果とは別です"));
        }
    }
}

public sealed record RuntimeMiniPlayerFrameInterval(
    int WindowIndex,
    string TargetId,
    TimeSpan SampleDuration,
    long TotalVideoFramesDelta,
    long? DroppedVideoFramesDelta,
    long? CorruptedVideoFramesDelta,
    double? DroppedFrameRatePercent);

public sealed record RuntimeDiagnosticResourceInterval(
    DateTimeOffset CapturedAt,
    TimeSpan Duration,
    double HostCpuPercent,
    double WebViewCpuPercent,
    double TotalCpuPercent,
    double HostSingleCoreCpuPercent,
    double WebViewSingleCoreCpuPercent,
    double TotalSingleCoreCpuPercent,
    double WebViewBrowserCpuPercent,
    double WebViewRendererCpuPercent,
    double WebViewUtilityCpuPercent,
    double WebViewGpuCpuPercent,
    double WebViewOtherCpuPercent,
    long TotalWorkingSetBytes,
    long TotalPrivateMemoryBytes,
    long TotalPrivateMemoryDeltaBytes,
    int WebViewCount,
    int WebViewProcessCount,
    int MiniPlayerCount,
    int LiveChatWindowCount)
{
    public IReadOnlyList<RuntimeMiniPlayerFrameInterval> MiniPlayerFrameIntervals { get; init; } = [];
}

public sealed record RuntimeDiagnosticReport(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int SampleCount,
    RuntimeDiagnosticSnapshot? First,
    RuntimeDiagnosticSnapshot? Last,
    int PeakWindowCount,
    int PeakTabCount,
    int PeakWebViewCount,
    int PeakMiniPlayerCount,
    long PeakWorkingSetBytes,
    long PeakPrivateMemoryBytes,
    int PeakThreadCount,
    int PeakHandleCount,
    int PeakWebViewProcessCount,
    long PeakWebViewWorkingSetBytes,
    long PeakWebViewPrivateMemoryBytes,
    int PeakWebViewThreadCount,
    int PeakWebViewHandleCount,
    int MaximumErrorCount,
    int MaximumWarningCount)
{
    public int WebViewCountDelta => (Last?.TotalWebViewCount ?? 0) - (First?.TotalWebViewCount ?? 0);

    public long PrivateMemoryDeltaBytes => (Last?.Process.PrivateMemoryBytes ?? 0) - (First?.Process.PrivateMemoryBytes ?? 0);

    public int ThreadCountDelta => (Last?.Process.ThreadCount ?? 0) - (First?.Process.ThreadCount ?? 0);

    public int HandleCountDelta => (Last?.Process.HandleCount ?? 0) - (First?.Process.HandleCount ?? 0);

    public int WebViewProcessCountDelta =>
        (Last?.WebViewProcesses.ProcessCount ?? 0) - (First?.WebViewProcesses.ProcessCount ?? 0);

    public long WebViewPrivateMemoryDeltaBytes =>
        (Last?.WebViewProcesses.PrivateMemoryBytes ?? 0) - (First?.WebViewProcesses.PrivateMemoryBytes ?? 0);

    public int WebViewHandleCountDelta =>
        (Last?.WebViewProcesses.HandleCount ?? 0) - (First?.WebViewProcesses.HandleCount ?? 0);

    public IReadOnlyList<RuntimeDiagnosticResourceInterval> ResourceIntervals { get; init; } = [];

    public IReadOnlyList<string> ResourceObservations { get; init; } = [];

    public long TotalPrivateMemoryDeltaBytes =>
        (Last?.TotalPrivateMemoryBytes ?? 0) - (First?.TotalPrivateMemoryBytes ?? 0);

    public long PeakTotalPrivateMemoryBytes => ResourceIntervals.Count == 0
        ? Last?.TotalPrivateMemoryBytes ?? 0
        : Math.Max(
            First?.TotalPrivateMemoryBytes ?? 0,
            ResourceIntervals.Max(static interval => interval.TotalPrivateMemoryBytes));

    public double AverageHostCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Average(static interval => interval.HostCpuPercent);

    public double AverageWebViewCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Average(static interval => interval.WebViewCpuPercent);

    public double AverageTotalCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Average(static interval => interval.TotalCpuPercent);

    public double PeakHostCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Max(static interval => interval.HostCpuPercent);

    public double PeakWebViewCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Max(static interval => interval.WebViewCpuPercent);

    public double PeakTotalCpuPercent => ResourceIntervals.Count == 0
        ? 0
        : ResourceIntervals.Max(static interval => interval.TotalCpuPercent);
}

public sealed class RuntimeDiagnosticMonitor
{
    public const int DefaultCapacity = 720;

    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultRetentionDuration = TimeSpan.FromTicks(
        DefaultSampleInterval.Ticks * DefaultCapacity);

    private readonly int _capacity;
    private readonly int _logicalProcessorCount;
    private readonly Queue<RuntimeDiagnosticSnapshot> _samples = new();
    private DateTimeOffset _startedAt;

    public RuntimeDiagnosticMonitor(
        int capacity = DefaultCapacity,
        int? logicalProcessorCount = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _logicalProcessorCount = Math.Max(1, logicalProcessorCount ?? Environment.ProcessorCount);
    }

    public bool IsRunning { get; private set; }

    public IReadOnlyList<RuntimeDiagnosticSnapshot> Samples => _samples.ToArray();

    public IReadOnlyList<RuntimeDiagnosticResourceInterval> ResourceIntervals =>
        CreateResourceIntervals(_samples.ToArray(), _logicalProcessorCount);

    public void Start(DateTimeOffset startedAt)
    {
        _samples.Clear();
        _startedAt = startedAt;
        IsRunning = true;
    }

    public bool TryAdd(RuntimeDiagnosticSnapshot snapshot)
    {
        if (!IsRunning)
        {
            return false;
        }

        while (_samples.Count >= _capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(snapshot);
        return true;
    }

    public RuntimeDiagnosticReport Stop(DateTimeOffset endedAt)
    {
        IsRunning = false;
        return CreateReport(endedAt);
    }

    public RuntimeDiagnosticReport CreateReport(DateTimeOffset endedAt)
    {
        var samples = _samples.ToArray();
        var resourceIntervals = CreateResourceIntervals(samples, _logicalProcessorCount);
        return new RuntimeDiagnosticReport(
            _startedAt,
            endedAt,
            samples.Length,
            samples.FirstOrDefault(),
            samples.LastOrDefault(),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WindowCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.TotalTabCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.TotalWebViewCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.TotalMiniPlayerCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.Process.WorkingSetBytes),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.Process.PrivateMemoryBytes),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.Process.ThreadCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.Process.HandleCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WebViewProcesses.ProcessCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WebViewProcesses.WorkingSetBytes),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WebViewProcesses.PrivateMemoryBytes),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WebViewProcesses.ThreadCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WebViewProcesses.HandleCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.ErrorCount),
            samples.Length == 0 ? 0 : samples.Max(static sample => sample.WarningCount))
        {
            ResourceIntervals = resourceIntervals,
            ResourceObservations = CreateResourceObservations(samples, resourceIntervals)
        };
    }

    internal static IReadOnlyList<RuntimeDiagnosticResourceInterval> CreateResourceIntervals(
        IReadOnlyList<RuntimeDiagnosticSnapshot> samples,
        int logicalProcessorCount)
    {
        if (samples.Count < 2)
        {
            return [];
        }

        var normalizedProcessorCount = Math.Max(1, logicalProcessorCount);
        var intervals = new List<RuntimeDiagnosticResourceInterval>(samples.Count - 1);
        for (var index = 1; index < samples.Count; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            var duration = current.CapturedAt - previous.CapturedAt;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            var hostCpuTime = NonNegativeDifference(
                current.Process.TotalProcessorTime,
                previous.Process.TotalProcessorTime);
            var webViewCpuByKind = CalculateWebViewCpuTimeByKind(previous, current);
            var webViewCpuTime = webViewCpuByKind.Values.Aggregate(TimeSpan.Zero, static (sum, value) => sum + value);
            var hostSingleCoreCpuPercent = CalculateSingleCoreCpuPercent(hostCpuTime, duration, normalizedProcessorCount);
            var webViewSingleCoreCpuPercent = CalculateSingleCoreCpuPercent(webViewCpuTime, duration, normalizedProcessorCount);
            var hostCpuPercent = hostSingleCoreCpuPercent / normalizedProcessorCount;
            var webViewCpuPercent = webViewSingleCoreCpuPercent / normalizedProcessorCount;
            intervals.Add(new RuntimeDiagnosticResourceInterval(
                current.CapturedAt,
                duration,
                hostCpuPercent,
                webViewCpuPercent,
                Math.Clamp(hostCpuPercent + webViewCpuPercent, 0, 100),
                hostSingleCoreCpuPercent,
                webViewSingleCoreCpuPercent,
                Math.Clamp(
                    hostSingleCoreCpuPercent + webViewSingleCoreCpuPercent,
                    0,
                    normalizedProcessorCount * 100d),
                CalculateMachineCpuPercent(webViewCpuByKind, "Browser", duration, normalizedProcessorCount),
                CalculateMachineCpuPercent(webViewCpuByKind, "Renderer", duration, normalizedProcessorCount),
                CalculateMachineCpuPercent(webViewCpuByKind, "Utility", duration, normalizedProcessorCount),
                CalculateMachineCpuPercent(webViewCpuByKind, "Gpu", duration, normalizedProcessorCount),
                CalculateMachineCpuPercent(webViewCpuByKind, "Other", duration, normalizedProcessorCount),
                current.TotalWorkingSetBytes,
                current.TotalPrivateMemoryBytes,
                current.TotalPrivateMemoryBytes - samples[0].TotalPrivateMemoryBytes,
                current.TotalWebViewCount,
                current.WebViewProcesses.ProcessCount,
                current.TotalMiniPlayerCount,
                current.TotalOpenLiveChatWindowCount)
            {
                MiniPlayerFrameIntervals = CreateMiniPlayerFrameIntervals(previous, current)
            });
        }

        return intervals;
    }

    private static IReadOnlyList<RuntimeMiniPlayerFrameInterval> CreateMiniPlayerFrameIntervals(
        RuntimeDiagnosticSnapshot previous,
        RuntimeDiagnosticSnapshot current)
    {
        if (previous.YouTubeMiniPlayerFrameSamples.Count == 0
            || current.YouTubeMiniPlayerFrameSamples.Count == 0)
        {
            return [];
        }

        var previousByTarget = previous.YouTubeMiniPlayerFrameSamples
            .GroupBy(static sample => (sample.WindowIndex, sample.TargetId))
            .ToDictionary(static group => group.Key, static group => group.Last());
        var result = new List<RuntimeMiniPlayerFrameInterval>();
        foreach (var currentSample in current.YouTubeMiniPlayerFrameSamples)
        {
            if (!previousByTarget.TryGetValue(
                    (currentSample.WindowIndex, currentSample.TargetId),
                    out var previousSample)
                || currentSample.ObservedAt <= previousSample.ObservedAt
                || previousSample.TotalVideoFrames is not { } previousTotalFrames
                || currentSample.TotalVideoFrames is not { } currentTotalFrames
                || currentTotalFrames < previousTotalFrames)
            {
                continue;
            }

            var totalFramesDelta = currentTotalFrames - previousTotalFrames;
            var droppedFramesDelta = NonNegativeNullableDifference(
                currentSample.DroppedVideoFrames,
                previousSample.DroppedVideoFrames);
            var corruptedFramesDelta = NonNegativeNullableDifference(
                currentSample.CorruptedVideoFrames,
                previousSample.CorruptedVideoFrames);
            result.Add(new RuntimeMiniPlayerFrameInterval(
                currentSample.WindowIndex,
                currentSample.TargetId,
                currentSample.ObservedAt - previousSample.ObservedAt,
                totalFramesDelta,
                droppedFramesDelta,
                corruptedFramesDelta,
                totalFramesDelta > 0 && droppedFramesDelta.HasValue
                    ? Math.Clamp(droppedFramesDelta.Value * 100d / totalFramesDelta, 0, 100)
                    : null));
        }

        return result;
    }

    private static Dictionary<string, TimeSpan> CalculateWebViewCpuTimeByKind(
        RuntimeDiagnosticSnapshot previous,
        RuntimeDiagnosticSnapshot current)
    {
        var result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        if (previous.WebViewProcesses.ProcessSamples.Count == 0
            && current.WebViewProcesses.ProcessSamples.Count == 0)
        {
            result["Other"] = NonNegativeDifference(
                current.WebViewProcesses.TotalProcessorTime,
                previous.WebViewProcesses.TotalProcessorTime);
            return result;
        }

        if (previous.WebViewProcesses.ProcessSamples.Count == 0
            || current.WebViewProcesses.ProcessSamples.Count == 0)
        {
            return result;
        }

        var previousByProcessId = previous.WebViewProcesses.ProcessSamples
            .GroupBy(static sample => sample.ProcessId)
            .ToDictionary(static group => group.Key, static group => group.First());
        foreach (var currentProcess in current.WebViewProcesses.ProcessSamples)
        {
            if (!previousByProcessId.TryGetValue(currentProcess.ProcessId, out var previousProcess))
            {
                continue;
            }

            var cpuTime = NonNegativeDifference(
                currentProcess.TotalProcessorTime,
                previousProcess.TotalProcessorTime);
            var kind = NormalizeProcessKind(currentProcess.Kind);
            result[kind] = result.GetValueOrDefault(kind) + cpuTime;
        }

        return result;
    }

    private static string NormalizeProcessKind(string? kind)
    {
        return kind switch
        {
            "Browser" => "Browser",
            "Renderer" => "Renderer",
            "Utility" => "Utility",
            "Gpu" => "Gpu",
            _ => "Other"
        };
    }

    private static TimeSpan NonNegativeDifference(TimeSpan current, TimeSpan previous)
    {
        var difference = current - previous;
        return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
    }

    private static long? NonNegativeNullableDifference(long? current, long? previous)
    {
        return current.HasValue && previous.HasValue && current.Value >= previous.Value
            ? current.Value - previous.Value
            : null;
    }

    private static double CalculateSingleCoreCpuPercent(
        TimeSpan cpuTime,
        TimeSpan duration,
        int logicalProcessorCount)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Clamp(
            cpuTime.TotalSeconds / duration.TotalSeconds * 100d,
            0,
            logicalProcessorCount * 100d);
    }

    private static double CalculateMachineCpuPercent(
        IReadOnlyDictionary<string, TimeSpan> cpuTimeByKind,
        string kind,
        TimeSpan duration,
        int logicalProcessorCount)
    {
        return cpuTimeByKind.TryGetValue(kind, out var cpuTime)
            ? CalculateSingleCoreCpuPercent(cpuTime, duration, logicalProcessorCount) / logicalProcessorCount
            : 0;
    }

    private static IReadOnlyList<string> CreateResourceObservations(
        IReadOnlyList<RuntimeDiagnosticSnapshot> samples,
        IReadOnlyList<RuntimeDiagnosticResourceInterval> intervals)
    {
        if (samples.Count < 2 || intervals.Count == 0)
        {
            return [];
        }

        var observations = new List<string>();
        var averageHostCpu = intervals.Average(static interval => interval.HostCpuPercent);
        var averageWebViewCpu = intervals.Average(static interval => interval.WebViewCpuPercent);
        var averageTotalCpu = averageHostCpu + averageWebViewCpu;
        if (averageTotalCpu >= 5)
        {
            if (averageWebViewCpu / averageTotalCpu >= 0.7)
            {
                observations.Add("CPU負荷はWebView2子プロセス側が中心です。表示中の動画、Webページ、ライブチャットの数と状態を確認してください。");
            }
            else if (averageHostCpu / averageTotalCpu >= 0.7)
            {
                observations.Add("CPU負荷はTabBrowser本体側が中心です。UI操作、バックグラウンド処理、診断イベントの発生状況を確認してください。");
            }
            else
            {
                observations.Add("CPU負荷はTabBrowser本体とWebView2子プロセスの両方に分散しています。");
            }
        }

        var first = samples[0];
        var last = samples[^1];
        var measuredDuration = last.CapturedAt - first.CapturedAt;
        var totalPrivateMemoryDelta = last.TotalPrivateMemoryBytes - first.TotalPrivateMemoryBytes;
        var surfaceCountsAreStable = Math.Abs(last.TotalWebViewCount - first.TotalWebViewCount) <= 1
            && last.TotalMiniPlayerCount == first.TotalMiniPlayerCount
            && last.TotalOpenLiveChatWindowCount == first.TotalOpenLiveChatWindowCount;
        if (measuredDuration >= TimeSpan.FromMinutes(30)
            && totalPrivateMemoryDelta >= 512L * 1024 * 1024
            && surfaceCountsAreStable)
        {
            observations.Add("WebView2、ミニプレイヤー、チャットの件数がほぼ一定のまま合計Private Memoryが512 MB以上増えています。直ちにリークとは断定せず、同じ操作条件で長時間の再計測を推奨します。");
        }

        return observations;
    }
}

public static class RuntimeDiagnosticReportFormatter
{
    public static string FormatSnapshot(RuntimeDiagnosticSnapshot snapshot)
    {
        var lines = new List<string>
        {
            "TabBrowser Runtime診断",
            $"チャンネル: {ApplicationChannelService.DisplayName}",
            $"バージョン: {GetApplicationVersion()}",
            $"取得: {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"状態: {FormatSeverity(snapshot.Severity)}",
            $"Window: {snapshot.WindowCount}",
            $"タブ: {snapshot.TotalTabCount}（Workspace {snapshot.TotalWorkspaceTabCount}）",
            $"WebView2: {snapshot.TotalWebViewCount}",
            $"タブメモリ状態: 休止 {snapshot.TotalSuspendedTabCount} / 解放 {snapshot.TotalMemoryReleasedTabCount} / 未初期化 {snapshot.TotalUninitializedWebTabCount}",
            $"タブメモリ操作: 解放 {snapshot.TotalTabMemoryReleaseCount} / 復元 {snapshot.TotalTabMemoryRestoreCount} / 保護・見送り {snapshot.TotalTabMemoryReleaseSkipCount}",
            $"YouTube裏タブ軽量化: 休止 {snapshot.TotalInactiveYouTubeTabSuspendCount} / 読込停止 {snapshot.TotalInactiveYouTubeTabLoadStopCount} / 復帰 {snapshot.TotalInactiveYouTubeTabResumeCount}",
            $"YouTubeミニプレイヤー軽量化: 休止 {snapshot.TotalInactiveYouTubeMiniPlayerSuspendCount} / 読込停止 {snapshot.TotalInactiveYouTubeMiniPlayerLoadStopCount} / 復帰 {snapshot.TotalInactiveYouTubeMiniPlayerResumeCount}",
            $"YouTubeミニプレイヤー: {snapshot.TotalMiniPlayerCount}",
            $"YouTube再生診断イベント: {snapshot.YouTubePlaybackDiagnostics.Count}",
            $"ライブチャットWindow: {snapshot.TotalOpenLiveChatWindowCount}",
            $"QuickLook表示中: {snapshot.TotalOpenQuickLookCount}",
            $"モデルレスWindow: {snapshot.ApplicationOwnership.RegisteredModelessWindowCount}",
            "セッション復旧診断: この起動中のメモリ内状態（再起動前の結果は不明）",
            $"通常セッション保存: {FormatSessionPersistenceStatus(snapshot.SessionRecovery.NormalSave)}",
            $"自動snapshot: {FormatSessionPersistenceStatus(snapshot.SessionRecovery.AutomaticSnapshot)}",
            $"保護復元点: {FormatSessionPersistenceStatus(snapshot.SessionRecovery.ProtectedRecoveryPoint)}",
            $"自動snapshotの世代整理: {FormatSessionPersistenceStatus(snapshot.SessionRecovery.AutomaticSnapshotCleanup)}",
            $"保護復元点の世代整理: {FormatSessionPersistenceStatus(snapshot.SessionRecovery.ProtectedRecoveryPointCleanup)}",
            $"浮動メディアホスト: {FormatFloatingMediaHostStates(snapshot.Windows)}",
            $"Working Set: {FormatMegabytes(snapshot.Process.WorkingSetBytes)} MB",
            $"Private Memory: {FormatMegabytes(snapshot.Process.PrivateMemoryBytes)} MB",
            $"Thread: {snapshot.Process.ThreadCount}",
            $"Handle: {snapshot.Process.HandleCount}",
            $"WebView2プロセス: {snapshot.WebViewProcesses.ProcessCount}（Browser {snapshot.WebViewProcesses.BrowserProcessCount} / Renderer {snapshot.WebViewProcesses.RendererProcessCount} / Utility {snapshot.WebViewProcesses.UtilityProcessCount} / GPU {snapshot.WebViewProcesses.GpuProcessCount} / その他 {snapshot.WebViewProcesses.OtherProcessCount}）",
            $"WebView2 Working Set: {FormatMegabytes(snapshot.WebViewProcesses.WorkingSetBytes)} MB",
            $"WebView2 Private Memory: {FormatMegabytes(snapshot.WebViewProcesses.PrivateMemoryBytes)} MB",
            $"合計 Private Memory: {FormatMegabytes(snapshot.TotalPrivateMemoryBytes)} MB",
            $"WebView2 Thread: {snapshot.WebViewProcesses.ThreadCount}",
            $"WebView2 Handle: {snapshot.WebViewProcesses.HandleCount}",
            $"WebView2取得不能プロセス: {snapshot.WebViewProcesses.UnavailableProcessCount}",
            $"WebView2プロセス復旧履歴: {snapshot.WebViewProcessRecoveryHistory.Count}",
            $"問題: エラー {snapshot.ErrorCount} / 警告 {snapshot.WarningCount}"
        };
        AppendWebViewProcessRecoveryHistory(lines, snapshot.WebViewProcessRecoveryHistory);
        AppendYouTubePlaybackDiagnostics(lines, snapshot.YouTubePlaybackDiagnostics);
        AppendIssues(lines, snapshot.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatFloatingMediaHostStates(
        IReadOnlyList<RuntimeDiagnosticWindowState> windows)
    {
        if (windows.Count == 0)
        {
            return "なし";
        }

        return string.Join(
            ", ",
            windows.Select(static window =>
                $"Window {window.WindowIndex} {window.Ownership.FloatingMediaHostLifetimeState}"
                + (window.Ownership.IsHiddenFloatingMediaHostActive ? "（保持中）" : string.Empty)));
    }

    public static string FormatReport(RuntimeDiagnosticReport report)
    {
        var lines = new List<string>
        {
            "TabBrowser Runtime長時間計測レポート",
            $"チャンネル: {ApplicationChannelService.DisplayName}",
            $"バージョン: {GetApplicationVersion()}",
            $"開始: {report.StartedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"終了: {report.EndedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"計測時間: {FormatDuration(report.EndedAt - report.StartedAt)}",
            $"サンプル: {report.SampleCount}",
            $"最大Window: {report.PeakWindowCount}",
            $"最大タブ: {report.PeakTabCount}",
            $"最大WebView2: {report.PeakWebViewCount}",
            $"最大YouTubeミニプレイヤー: {report.PeakMiniPlayerCount}",
            $"最大Working Set: {FormatMegabytes(report.PeakWorkingSetBytes)} MB",
            $"最大Private Memory: {FormatMegabytes(report.PeakPrivateMemoryBytes)} MB",
            $"最大Thread: {report.PeakThreadCount}",
            $"最大Handle: {report.PeakHandleCount}",
            $"最大WebView2プロセス: {report.PeakWebViewProcessCount}",
            $"最大WebView2 Working Set: {FormatMegabytes(report.PeakWebViewWorkingSetBytes)} MB",
            $"最大WebView2 Private Memory: {FormatMegabytes(report.PeakWebViewPrivateMemoryBytes)} MB",
            $"最大合計 Private Memory: {FormatMegabytes(report.PeakTotalPrivateMemoryBytes)} MB",
            $"最大WebView2 Thread: {report.PeakWebViewThreadCount}",
            $"最大WebView2 Handle: {report.PeakWebViewHandleCount}",
            $"CPU平均（全論理CPU基準）: 合計 {FormatCpuPercent(report.AverageTotalCpuPercent)} / 本体 {FormatCpuPercent(report.AverageHostCpuPercent)} / WebView2 {FormatCpuPercent(report.AverageWebViewCpuPercent)}",
            $"CPU最大（全論理CPU基準）: 合計 {FormatCpuPercent(report.PeakTotalCpuPercent)} / 本体 {FormatCpuPercent(report.PeakHostCpuPercent)} / WebView2 {FormatCpuPercent(report.PeakWebViewCpuPercent)}",
            $"最大問題数: エラー {report.MaximumErrorCount} / 警告 {report.MaximumWarningCount}"
        };
        if (report.ResourceIntervals.Count > 0)
        {
            lines.Add(
                "WebView2 CPU平均内訳: "
                + $"Browser {FormatCpuPercent(report.ResourceIntervals.Average(static interval => interval.WebViewBrowserCpuPercent))} / "
                + $"Renderer {FormatCpuPercent(report.ResourceIntervals.Average(static interval => interval.WebViewRendererCpuPercent))} / "
                + $"Utility {FormatCpuPercent(report.ResourceIntervals.Average(static interval => interval.WebViewUtilityCpuPercent))} / "
                + $"GPU {FormatCpuPercent(report.ResourceIntervals.Average(static interval => interval.WebViewGpuCpuPercent))} / "
                + $"その他 {FormatCpuPercent(report.ResourceIntervals.Average(static interval => interval.WebViewOtherCpuPercent))}");
        }

        if (report.First is not null && report.Last is not null)
        {
            lines.Add($"開始→終了 WebView2: {report.First.TotalWebViewCount} → {report.Last.TotalWebViewCount}");
            lines.Add($"開始→終了 QuickLook表示中: {report.First.TotalOpenQuickLookCount} → {report.Last.TotalOpenQuickLookCount}");
            lines.Add($"開始→終了 モデルレスWindow: {report.First.ApplicationOwnership.RegisteredModelessWindowCount} → {report.Last.ApplicationOwnership.RegisteredModelessWindowCount}");
            lines.Add($"開始→終了 Working Set: {FormatMegabytes(report.First.Process.WorkingSetBytes)} → {FormatMegabytes(report.Last.Process.WorkingSetBytes)} MB");
            lines.Add($"開始→終了 Private Memory差分: {FormatSignedMegabytes(report.PrivateMemoryDeltaBytes)} MB");
            lines.Add($"開始→終了 Thread差分: {FormatSigned(report.ThreadCountDelta)}");
            lines.Add($"開始→終了 Handle: {report.First.Process.HandleCount} → {report.Last.Process.HandleCount}");
            lines.Add($"開始→終了 WebView2プロセス: {report.First.WebViewProcesses.ProcessCount} → {report.Last.WebViewProcesses.ProcessCount}");
            lines.Add($"開始→終了 WebView2 Private Memory差分: {FormatSignedMegabytes(report.WebViewPrivateMemoryDeltaBytes)} MB");
            lines.Add($"開始→終了 合計 Private Memory差分: {FormatSignedMegabytes(report.TotalPrivateMemoryDeltaBytes)} MB");
            lines.Add($"開始→終了 WebView2 Handle: {report.First.WebViewProcesses.HandleCount} → {report.Last.WebViewProcesses.HandleCount}");
            lines.Add("セッション復旧診断: この起動中のメモリ内状態（再起動前の結果は不明）");
            lines.Add($"通常セッション保存: {FormatSessionPersistenceStatus(report.Last.SessionRecovery.NormalSave)}");
            lines.Add($"自動snapshot: {FormatSessionPersistenceStatus(report.Last.SessionRecovery.AutomaticSnapshot)}");
            lines.Add($"保護復元点: {FormatSessionPersistenceStatus(report.Last.SessionRecovery.ProtectedRecoveryPoint)}");
            lines.Add($"自動snapshotの世代整理: {FormatSessionPersistenceStatus(report.Last.SessionRecovery.AutomaticSnapshotCleanup)}");
            lines.Add($"保護復元点の世代整理: {FormatSessionPersistenceStatus(report.Last.SessionRecovery.ProtectedRecoveryPointCleanup)}");
            AppendYouTubePlaybackDiagnostics(lines, report.Last.YouTubePlaybackDiagnostics);
            AppendIssues(lines, report.Last.Issues);
        }

        AppendResourceObservations(lines, report.ResourceObservations);
        AppendResourceIntervals(lines, report.ResourceIntervals);

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatSeverity(RuntimeDiagnosticSeverity severity)
    {
        return severity switch
        {
            RuntimeDiagnosticSeverity.Error => "エラー",
            RuntimeDiagnosticSeverity.Warning => "警告",
            _ => "正常"
        };
    }

    public static string FormatSessionPersistenceStatus(SessionPersistenceOperationDiagnostic status)
    {
        if (status.LastResult == SessionPersistenceDiagnosticResult.Unknown)
        {
            return "未実行／不明（この起動中の記録なし）";
        }

        var result = status.LastResult switch
        {
            SessionPersistenceDiagnosticResult.Succeeded => "成功",
            SessionPersistenceDiagnosticResult.Failed =>
                $"失敗（{FormatSessionPersistenceFailure(status.FailureCategory)}）",
            SessionPersistenceDiagnosticResult.Skipped => "見送り（成功日時は更新なし）",
            _ => "未実行／不明"
        };
        var attemptedAt = status.LastAttemptAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "不明";
        var succeededAt = status.LastSuccessAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "なし";
        return $"{result} / 処理 {FormatSessionPersistenceOperation(status.Operation)} / 最終試行 {attemptedAt} / 最終成功 {succeededAt}";
    }

    private static string FormatSessionPersistenceOperation(SessionPersistenceDiagnosticOperation operation)
    {
        return operation switch
        {
            SessionPersistenceDiagnosticOperation.NormalSave => "通常保存",
            SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation => "自動snapshot作成",
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation => "保護復元点作成",
            SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation => "初回保護復元点作成",
            SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup => "自動snapshot世代整理",
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup => "保護復元点世代整理",
            _ => "不明"
        };
    }

    private static string FormatSessionPersistenceFailure(SessionPersistenceFailureCategory category)
    {
        return category switch
        {
            SessionPersistenceFailureCategory.Cancelled => "キャンセル",
            SessionPersistenceFailureCategory.AccessDenied => "アクセス拒否",
            SessionPersistenceFailureCategory.InputOutput => "I/O",
            SessionPersistenceFailureCategory.InvalidData => "データ不正",
            SessionPersistenceFailureCategory.Unknown => "未分類",
            _ => "なし"
        };
    }

    private static string GetApplicationVersion()
    {
        return typeof(RuntimeDiagnosticReportFormatter).Assembly.GetName().Version?.ToString()
            ?? "不明";
    }

    public static long FormatMegabytes(long bytes)
    {
        return (long)Math.Round(bytes / 1024d / 1024d);
    }

    public static string FormatCpuPercent(double value)
    {
        return $"{Math.Max(0, value):0.0}%";
    }

    public static string FormatWebViewInitializationFailure(
        string stage,
        int hresult,
        TimeSpan processUptime,
        string environmentScope,
        RuntimeDiagnosticSnapshot snapshot,
        RuntimeWebViewProcessMetrics targetEnvironmentProcesses)
    {
        return string.Join(
            ";",
            $"stage={stage}",
            $"hresult=0x{hresult:X8}",
            $"uptimeSeconds={(long)Math.Max(0, processUptime.TotalSeconds)}",
            $"environment={environmentScope}",
            $"windows={snapshot.WindowCount}",
            $"tabs={snapshot.TotalTabCount}",
            $"webViews={snapshot.TotalWebViewCount}",
            $"miniPlayers={snapshot.TotalMiniPlayerCount}",
            $"hostWorkingSetMB={FormatMegabytes(snapshot.Process.WorkingSetBytes)}",
            $"hostPrivateMB={FormatMegabytes(snapshot.Process.PrivateMemoryBytes)}",
            $"hostThreads={snapshot.Process.ThreadCount}",
            $"hostHandles={snapshot.Process.HandleCount}",
            $"webViewProcesses={snapshot.WebViewProcesses.ProcessCount}",
            $"webViewRenderers={snapshot.WebViewProcesses.RendererProcessCount}",
            $"webViewWorkingSetMB={FormatMegabytes(snapshot.WebViewProcesses.WorkingSetBytes)}",
            $"webViewPrivateMB={FormatMegabytes(snapshot.WebViewProcesses.PrivateMemoryBytes)}",
            $"webViewThreads={snapshot.WebViewProcesses.ThreadCount}",
            $"webViewHandles={snapshot.WebViewProcesses.HandleCount}",
            $"webViewUnavailable={snapshot.WebViewProcesses.UnavailableProcessCount}",
            $"targetProcesses={targetEnvironmentProcesses.ProcessCount}",
            $"targetRenderers={targetEnvironmentProcesses.RendererProcessCount}",
            $"targetPrivateMB={FormatMegabytes(targetEnvironmentProcesses.PrivateMemoryBytes)}",
            $"targetThreads={targetEnvironmentProcesses.ThreadCount}",
            $"targetHandles={targetEnvironmentProcesses.HandleCount}",
            $"targetUnavailable={targetEnvironmentProcesses.UnavailableProcessCount}");
    }

    public static string FormatSignedMegabytes(long bytes)
    {
        return FormatSigned(FormatMegabytes(bytes));
    }

    private static string FormatSigned(long value)
    {
        return value > 0 ? $"+{value}" : value.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var normalized = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return $"{(int)normalized.TotalHours:00}:{normalized.Minutes:00}:{normalized.Seconds:00}";
    }

    private static void AppendIssues(
        ICollection<string> lines,
        IReadOnlyList<RuntimeDiagnosticIssue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("検出事項:");
        foreach (var issue in issues)
        {
            var window = issue.WindowIndex.HasValue ? $" Window {issue.WindowIndex.Value}" : string.Empty;
            var tab = issue.TabId.HasValue ? $" Tab {issue.TabId.Value:N}" : string.Empty;
            lines.Add($"- [{FormatSeverity(issue.Severity)}] {issue.Code}{window}{tab}: {issue.Message}");
        }
    }

    private static void AppendResourceObservations(
        ICollection<string> lines,
        IReadOnlyList<string> observations)
    {
        if (observations.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("資源使用の参考診断（自動制御なし）:");
        foreach (var observation in observations)
        {
            lines.Add("- " + observation);
        }
    }

    private static void AppendResourceIntervals(
        ICollection<string> lines,
        IReadOnlyList<RuntimeDiagnosticResourceInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("資源使用の時系列（各サンプル間の平均、CPUは全論理CPU基準）:");
        lines.Add("時刻 | CPU合計 | 本体 | WebView2 | Renderer | GPU | 1コア換算 | 合計Private MB | 開始比 MB | WebView | WebViewプロセス | Mini | Chat");
        foreach (var interval in intervals)
        {
            lines.Add(
                $"{interval.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} | "
                + $"{FormatCpuPercent(interval.TotalCpuPercent)} | "
                + $"{FormatCpuPercent(interval.HostCpuPercent)} | "
                + $"{FormatCpuPercent(interval.WebViewCpuPercent)} | "
                + $"{FormatCpuPercent(interval.WebViewRendererCpuPercent)} | "
                + $"{FormatCpuPercent(interval.WebViewGpuCpuPercent)} | "
                + $"{FormatCpuPercent(interval.TotalSingleCoreCpuPercent)} | "
                + $"{FormatMegabytes(interval.TotalPrivateMemoryBytes)} | "
                + $"{FormatSignedMegabytes(interval.TotalPrivateMemoryDeltaBytes)} | "
                + $"{interval.WebViewCount} | {interval.WebViewProcessCount} | "
                + $"{interval.MiniPlayerCount} | {interval.LiveChatWindowCount}");
        }

        var miniPlayerFrameIntervals = intervals
            .SelectMany(interval => interval.MiniPlayerFrameIntervals.Select(frame => (interval.CapturedAt, Frame: frame)))
            .ToList();
        if (miniPlayerFrameIntervals.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("YouTubeミニプレイヤーフレーム差分（新しい動画サンプルが得られた区間のみ）:");
        lines.Add("時刻 | Window | Mini | サンプル秒 | 描画 | ドロップ | 破損 | 区間ドロップ率");
        foreach (var item in miniPlayerFrameIntervals)
        {
            lines.Add(
                $"{item.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} | "
                + $"{item.Frame.WindowIndex} | {ShortDiagnosticTarget(item.Frame.TargetId)} | "
                + $"{Math.Max(0, item.Frame.SampleDuration.TotalSeconds):0.###} | "
                + $"{item.Frame.TotalVideoFramesDelta} | "
                + $"{item.Frame.DroppedVideoFramesDelta?.ToString() ?? "不明"} | "
                + $"{item.Frame.CorruptedVideoFramesDelta?.ToString() ?? "不明"} | "
                + (item.Frame.DroppedFrameRatePercent.HasValue
                    ? $"{item.Frame.DroppedFrameRatePercent.Value:0.###}%"
                    : "不明"));
        }
    }

    private static void AppendWebViewProcessRecoveryHistory(
        ICollection<string> lines,
        IReadOnlyList<WebViewProcessRecoveryDiagnosticEntry> history)
    {
        if (history.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("WebView2プロセス復旧（最新5件）:");
        foreach (var entry in history.TakeLast(5))
        {
            lines.Add($"- {entry.OccurredAt:HH:mm:ss} {entry.Scope} {entry.FailureKind} {entry.Action} {entry.Result}");
        }
    }

    private static void AppendYouTubePlaybackDiagnostics(
        ICollection<string> lines,
        IReadOnlyList<RuntimeYouTubePlaybackDiagnosticEntry> history)
    {
        if (history.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("YouTube再生診断（最新12件）:");
        foreach (var entry in history.TakeLast(12))
        {
            var frameText = entry.TotalVideoFrames is > 0
                ? $" frames={entry.TotalVideoFrames} dropped={entry.DroppedVideoFrames?.ToString() ?? "不明"}"
                    + $" droppedRate={FormatDroppedFrameRate(entry.TotalVideoFrames, entry.DroppedVideoFrames)}"
                : " frames=不明 dropped=不明 droppedRate=不明";
            var stallText = entry.StallDurationMilliseconds.HasValue
                ? $" stallMs={entry.StallDurationMilliseconds.Value}"
                : string.Empty;
            lines.Add(
                $"- {entry.ObservedAt:HH:mm:ss} Window {entry.WindowIndex}"
                + $" {entry.SourceKind}/{ShortDiagnosticTarget(entry.TargetId)}"
                + $" {entry.Operation} event={entry.EventName}"
                + $" position={FormatPlaybackMetric(entry.PositionSeconds)}"
                + $" readyState={FormatPlaybackMetric(entry.ReadyState)}"
                + $" networkState={FormatPlaybackMetric(entry.NetworkState)}"
                + $" paused={FormatPlaybackMetric(entry.IsPaused)}"
                + $" bufferedAhead={FormatPlaybackMetric(entry.BufferedAheadSeconds)}"
                + frameText
                + $" corrupted={entry.CorruptedVideoFrames?.ToString() ?? "不明"}"
                + stallText);
        }
    }

    private static string FormatDroppedFrameRate(long? totalFrames, long? droppedFrames)
    {
        if (totalFrames is not > 0 || droppedFrames is null)
        {
            return "不明";
        }

        return $"{Math.Clamp(droppedFrames.Value * 100d / totalFrames.Value, 0, 100):0.###}%";
    }

    private static string FormatPlaybackMetric<T>(T? value)
        where T : struct
    {
        return value switch
        {
            double number => number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => value?.ToString() ?? "不明"
        };
    }

    private static string ShortDiagnosticTarget(string targetId)
    {
        var separatorIndex = targetId.LastIndexOf(':');
        var value = separatorIndex >= 0 && separatorIndex < targetId.Length - 1
            ? targetId[(separatorIndex + 1)..]
            : targetId;
        return value.Length > 8 ? value[..8] : value;
    }
}
