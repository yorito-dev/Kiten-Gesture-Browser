using TabBrowser.Models;

namespace TabBrowser.Services;

// 列挙時の実Windowを保持し、候補tabの所属は実行時に読み直す。
internal interface ITabMemoryEvaluationWindow
{
    IList<BrowserTab> Tabs { get; }
    bool IsRightWheelTabCyclingActive();
    bool HasActiveDownloadForMemoryManagement();
    IReadOnlyList<WebViewProcessDiagnosticInfo> GetProcessDiagnosticInfos();
    Task<bool> TrySuspendColdInactiveTabAsync(BrowserTab tab);
}

internal interface ITabMemoryEvaluationHost : ITabMemoryEvaluationWindow
{
    bool CanStartEvaluation();
    bool IsMemorySaverEnabled { get; }
    bool IsClosing { get; }
    BrowserTab? ActiveTab { get; }
    long TotalAvailableMemoryBytes { get; }
    IReadOnlyList<ITabMemoryEvaluationWindow> GetOpenWindows();
    IReadOnlyList<TabMemorySnapshot> CaptureGlobalTabMemorySnapshots();
    long CaptureProcessPrivateMemoryBytes(IEnumerable<WebViewProcessDiagnosticInfo> processes);
    bool IsInitializing(BrowserTab tab);
    bool HasLoadedRuntime(BrowserTab tab);
    bool IsVisibleSplitTab(BrowserTab tab);
    IReadOnlySet<Guid> GetGlobalHotTabIds();
    Task<bool> TrySuspendTabAsync(BrowserTab tab);
    bool ResumeTabIfSuspended(BrowserTab tab);
}

// 実際に使われている圧力評価とcold tab休止の手順を所有する。
// 起点Windowのprivate判定、Runtime資源と未使用のrelease経路はhost側に残す。
internal sealed class TabMemoryEvaluationController
{
    private readonly ITabMemoryEvaluationHost _host;
    private readonly TabMemoryManagementService _tabMemoryManagementService;
    private readonly Func<DateTimeOffset> _utcNow;

    internal TabMemoryEvaluationController(
        ITabMemoryEvaluationHost host,
        TabMemoryManagementService memoryManagementService,
        Func<DateTimeOffset>? utcNow = null)
    {
        _host = host;
        _tabMemoryManagementService = memoryManagementService;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task EvaluateAsync()
    {
        if (_tabMemoryManagementService.IsEvaluationRunning
            || !_host.CanStartEvaluation())
        {
            return;
        }

        var openWindows = _host.GetOpenWindows();
        if (openWindows.Any(static window => window.IsRightWheelTabCyclingActive()))
        {
            return;
        }

        if (!_tabMemoryManagementService.TryEnterEvaluation(out var evaluationLease))
        {
            return;
        }

        using (evaluationLease)
        {
            try
            {
                if (openWindows.Any(static window => window.HasActiveDownloadForMemoryManagement()))
                {
                    return;
                }

                var snapshots = _host.CaptureGlobalTabMemorySnapshots();
                var loadedTabCount = snapshots.Count(static tab => tab.IsLoaded && tab.HasRuntime);
                var privateMemoryBytes = _host.CaptureProcessPrivateMemoryBytes(
                    openWindows.SelectMany(static window =>
                        window.GetProcessDiagnosticInfos()));
                var totalAvailableMemoryBytes = _host.TotalAvailableMemoryBytes;
                if (totalAvailableMemoryBytes <= 0)
                {
                    totalAvailableMemoryBytes = 8L * 1024 * 1024 * 1024;
                }

                var pressure = _tabMemoryManagementService.EvaluatePressure(
                    totalAvailableMemoryBytes,
                    privateMemoryBytes,
                    loadedTabCount);
                if (pressure == TabMemoryPressureLevel.None)
                {
                    return;
                }

                var hotTabIds = _tabMemoryManagementService.SelectHotTabIds(snapshots);
                var candidateIds = _tabMemoryManagementService.SelectSuspendCandidateIds(
                    snapshots,
                    hotTabIds,
                    pressure,
                    _utcNow());
                foreach (var candidateId in candidateIds)
                {
                    var owner = openWindows.FirstOrDefault(window =>
                        window.Tabs.Any(tab => tab.Id == candidateId));
                    var tab = owner?.Tabs.FirstOrDefault(tab => tab.Id == candidateId);
                    if (owner is null || tab is null)
                    {
                        continue;
                    }

                    await owner.TrySuspendColdInactiveTabAsync(tab);
                }
            }
            catch
            {
                _tabMemoryManagementService.RecordReleaseSkip();
            }
        }
    }

    public async Task<bool> TrySuspendColdInactiveTabAsync(BrowserTab tab)
    {
        if (!IsTabMemorySuspendStillSafe(tab))
        {
            return false;
        }

        if (!await _host.TrySuspendTabAsync(tab))
        {
            return false;
        }

        if (IsTabMemorySuspendStillSafe(tab, allowSuspended: true))
        {
            return true;
        }

        _host.ResumeTabIfSuspended(tab);
        return false;
    }

    private bool IsTabMemorySuspendStillSafe(BrowserTab tab, bool allowSuspended = false)
    {
        return _host.IsMemorySaverEnabled
            && !_host.IsClosing
            && _host.Tabs.Contains(tab)
            && !ReferenceEquals(_host.ActiveTab, tab)
            && !tab.IsNativeContentTab
            && !tab.IsPinned
            && !tab.IsPlayingAudio
            && !tab.IsLoading
            && !_host.IsInitializing(tab)
            && !_host.IsRightWheelTabCyclingActive()
            && _host.HasLoadedRuntime(tab)
            && (allowSuspended || !tab.IsSuspended)
            && !_host.IsVisibleSplitTab(tab)
            && !_host.GetGlobalHotTabIds().Contains(tab.Id)
            && !_host.HasActiveDownloadForMemoryManagement();
    }

}
