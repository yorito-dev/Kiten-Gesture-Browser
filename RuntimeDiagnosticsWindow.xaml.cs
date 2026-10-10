using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TabBrowser.Services;

namespace TabBrowser;

public partial class RuntimeDiagnosticsWindow : Window
{
    public static readonly TimeSpan SampleInterval = RuntimeDiagnosticMonitor.DefaultSampleInterval;

    private readonly Func<RuntimeDiagnosticSnapshot> _captureSnapshot;
    private readonly Func<IReadOnlyList<StartupPerformanceEntry>> _captureStartupPerformance;
    private readonly Func<ReleaseInformationSnapshot> _captureReleaseInformation;
    private readonly Func<string, Task<ClipboardWriteResult>> _copyTextAsync;
    private readonly Func<Uri, ExternalBrowserLaunchResult> _openPage;
    private readonly RuntimeDiagnosticMonitor _monitor = new();
    private readonly DispatcherTimer _sampleTimer = new() { Interval = SampleInterval };
    private RuntimeDiagnosticSnapshot? _currentSnapshot;
    private ReleaseInformationSnapshot? _releaseInformation;
    private RuntimeDiagnosticReport? _lastReport;

    public RuntimeDiagnosticsWindow(
        Func<RuntimeDiagnosticSnapshot> captureSnapshot,
        Func<IReadOnlyList<StartupPerformanceEntry>> captureStartupPerformance,
        Func<ReleaseInformationSnapshot> captureReleaseInformation,
        Func<string, Task<ClipboardWriteResult>> copyTextAsync,
        Func<Uri, ExternalBrowserLaunchResult> openPage)
    {
        _captureSnapshot = captureSnapshot;
        _captureStartupPerformance = captureStartupPerformance;
        _captureReleaseInformation = captureReleaseInformation;
        _copyTextAsync = copyTextAsync;
        _openPage = openPage;
        InitializeComponent();
        _sampleTimer.Tick += SampleTimer_Tick;
        Loaded += (_, _) =>
        {
            RefreshReleaseInformation();
            RefreshSnapshot(recordSample: false);
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshReleaseInformation();
        RefreshSnapshot(recordSample: _monitor.IsRunning);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor.IsRunning)
        {
            return;
        }

        _lastReport = null;
        _monitor.Start(DateTimeOffset.Now);
        RefreshSnapshot(recordSample: true);
        _sampleTimer.Start();
        UpdateMeasurementButtons();
        StatusText.Text = $"長時間計測中（{SampleInterval.TotalSeconds:0}秒間隔、最大{RuntimeDiagnosticMonitor.DefaultRetentionDuration.TotalHours:0}時間）";
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopMeasurement(captureFinalSample: true);
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var reportText = CreateCurrentReportText();
        if (string.IsNullOrWhiteSpace(reportText))
        {
            StatusText.Text = "コピーできる診断結果がありません";
            return;
        }

        var result = await TryCopyTextAsync(reportText);
        StatusText.Text = result.IsSuccess
            ? "Runtime診断結果をコピーしました"
            : "Runtime診断結果をコピーできませんでした";
    }

    private async void CopyBasicInformationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_releaseInformation is null)
        {
            RefreshReleaseInformation();
        }

        if (_releaseInformation is not { } snapshot)
        {
            StatusText.Text = "診断用基本情報を取得できませんでした";
            return;
        }

        var result = await ReleaseInformationService.CopyBasicInformationAsync(snapshot, _copyTextAsync);
        StatusText.Text = result.Message;
    }

    private void DistributionPageButton_Click(object sender, RoutedEventArgs e)
    {
        var result = ReleaseInformationService.OpenConfiguredPage(
            _releaseInformation?.Build.DistributionPageUri,
            "配布ページ",
            _openPage);
        ReleaseLinkStatusText.Text = result.Message;
    }

    private void IssueReportButton_Click(object sender, RoutedEventArgs e)
    {
        var result = ReleaseInformationService.OpenConfiguredPage(
            _releaseInformation?.Build.IssueReportUri,
            "不具合報告先",
            _openPage);
        ReleaseLinkStatusText.Text = result.Message;
    }

    private async Task<ClipboardWriteResult> TryCopyTextAsync(string text)
    {
        try
        {
            return await _copyTextAsync(text);
        }
        catch
        {
            return new ClipboardWriteResult(false, 0);
        }
    }

    private void RefreshReleaseInformation()
    {
        try
        {
            _releaseInformation = _captureReleaseInformation();
            ReleaseInformationGrid.ItemsSource = _releaseInformation.Fields;
            DistributionPageButton.IsEnabled = _releaseInformation.Build.DistributionPageUri is not null;
            IssueReportButton.IsEnabled = _releaseInformation.Build.IssueReportUri is not null;
            ReleaseLinkStatusText.Text = CreateReleaseLinkStatus(_releaseInformation.Build);
        }
        catch
        {
            _releaseInformation = null;
            ReleaseInformationGrid.ItemsSource = new[]
            {
                new ReleaseInformationField("版情報", ReleaseInformationService.UnknownValue)
            };
            DistributionPageButton.IsEnabled = false;
            IssueReportButton.IsEnabled = false;
            ReleaseLinkStatusText.Text = "配布ページ：この版では利用不可／不具合報告：この版では利用不可";
        }
    }

    private static string CreateReleaseLinkStatus(ReleaseBuildInformation build)
    {
        var distribution = build.DistributionPageUri is null ? "配布ページ：この版では利用不可" : "配布ページ設定済み";
        var issueReport = build.IssueReportUri is null ? "不具合報告：この版では利用不可" : "不具合報告先設定済み";
        return $"{distribution}／{issueReport}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var reportText = CreateCurrentReportText();
        if (string.IsNullOrWhiteSpace(reportText))
        {
            StatusText.Text = "保存できる診断結果がありません";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Runtime診断レポートを保存",
            Filter = "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            FileName = $"runtime-diagnostic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, reportText);
            StatusText.Text = "Runtime診断レポートを保存しました";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Runtime診断レポートを保存できません: " + ex.Message;
        }
    }

    private void OnlyProblemWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateWindowRows();
    }

    private void SampleTimer_Tick(object? sender, EventArgs e)
    {
        RefreshSnapshot(recordSample: true);
    }

    private void RefreshSnapshot(bool recordSample)
    {
        try
        {
            _currentSnapshot = _captureSnapshot();
            if (recordSample)
            {
                _monitor.TryAdd(_currentSnapshot);
            }

            UpdateDisplay();
            if (!_monitor.IsRunning)
            {
                StatusText.Text = $"{_currentSnapshot.CapturedAt:HH:mm:ss}  {RuntimeDiagnosticReportFormatter.FormatSeverity(_currentSnapshot.Severity)}  エラー {_currentSnapshot.ErrorCount} / 警告 {_currentSnapshot.WarningCount}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Runtime状態を取得できません: " + ex.Message;
        }
    }

    private void StopMeasurement(bool captureFinalSample)
    {
        if (!_monitor.IsRunning)
        {
            return;
        }

        if (captureFinalSample)
        {
            RefreshSnapshot(recordSample: true);
        }

        _sampleTimer.Stop();
        _lastReport = _monitor.Stop(DateTimeOffset.Now);
        UpdateMeasurementButtons();
        UpdateSampleRows();
        UpdateStartupRows();
        StatusText.Text = $"長時間計測を停止しました（{_lastReport.SampleCount}件）";
    }

    private void UpdateDisplay()
    {
        UpdateOverviewRows();
        UpdateWindowRows();
        UpdateIssueRows();
        UpdateInputRows();
        UpdateSampleRows();
        UpdateStartupRows();
    }

    private void UpdateOverviewRows()
    {
        if (_currentSnapshot is not { } snapshot)
        {
            OverviewGrid.ItemsSource = null;
            return;
        }

        var currentInput = snapshot.CurrentInput is null
            ? "なし"
            : $"{snapshot.CurrentInput.Kind} / {ShortId(snapshot.CurrentInput.WindowId)} / {snapshot.CurrentInput.Id}";
        var latestResourceInterval = _lastReport?.ResourceIntervals.LastOrDefault()
            ?? _monitor.ResourceIntervals.LastOrDefault();
        var cpuSummary = latestResourceInterval is null
            ? "計測開始後、2回目のサンプルから表示"
            : $"合計 {RuntimeDiagnosticReportFormatter.FormatCpuPercent(latestResourceInterval.TotalCpuPercent)}"
                + $" / 本体 {RuntimeDiagnosticReportFormatter.FormatCpuPercent(latestResourceInterval.HostCpuPercent)}"
                + $" / WebView2 {RuntimeDiagnosticReportFormatter.FormatCpuPercent(latestResourceInterval.WebViewCpuPercent)}";
        OverviewGrid.ItemsSource = new[]
        {
            new ValueRow("状態", RuntimeDiagnosticReportFormatter.FormatSeverity(snapshot.Severity)),
            new ValueRow("取得時刻", snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")),
            new ValueRow("Window", snapshot.WindowCount.ToString()),
            new ValueRow("タブ", $"{snapshot.TotalTabCount}（Workspace {snapshot.TotalWorkspaceTabCount}）"),
            new ValueRow("WebView2", snapshot.TotalWebViewCount.ToString()),
            new ValueRow("YouTubeミニプレイヤー", snapshot.TotalMiniPlayerCount.ToString()),
            new ValueRow("ライブチャットWindow", snapshot.TotalOpenLiveChatWindowCount.ToString()),
            new ValueRow("YouTube再生診断", snapshot.YouTubePlaybackDiagnostics.Count.ToString()),
            new ValueRow(
                "YouTube裏タブ軽量化",
                $"休止 {snapshot.TotalInactiveYouTubeTabSuspendCount} / 読込停止 {snapshot.TotalInactiveYouTubeTabLoadStopCount} / 復帰 {snapshot.TotalInactiveYouTubeTabResumeCount}"),
            new ValueRow(
                "YouTubeミニプレイヤー軽量化",
                $"休止 {snapshot.TotalInactiveYouTubeMiniPlayerSuspendCount} / 読込停止 {snapshot.TotalInactiveYouTubeMiniPlayerLoadStopCount} / 復帰 {snapshot.TotalInactiveYouTubeMiniPlayerResumeCount}"),
            new ValueRow("QuickLook表示中", snapshot.TotalOpenQuickLookCount.ToString()),
            new ValueRow("セッション復旧診断", "この起動中のみ（再起動前の結果は不明）"),
            new ValueRow(
                "通常セッション保存",
                RuntimeDiagnosticReportFormatter.FormatSessionPersistenceStatus(snapshot.SessionRecovery.NormalSave)),
            new ValueRow(
                "自動snapshot",
                RuntimeDiagnosticReportFormatter.FormatSessionPersistenceStatus(snapshot.SessionRecovery.AutomaticSnapshot)),
            new ValueRow(
                "保護復元点",
                RuntimeDiagnosticReportFormatter.FormatSessionPersistenceStatus(snapshot.SessionRecovery.ProtectedRecoveryPoint)),
            new ValueRow(
                "自動snapshotの世代整理",
                RuntimeDiagnosticReportFormatter.FormatSessionPersistenceStatus(snapshot.SessionRecovery.AutomaticSnapshotCleanup)),
            new ValueRow(
                "保護復元点の世代整理",
                RuntimeDiagnosticReportFormatter.FormatSessionPersistenceStatus(snapshot.SessionRecovery.ProtectedRecoveryPointCleanup)),
            new ValueRow(
                "QuickLook PDF Environment",
                $"{snapshot.ApplicationOwnership.QuickLookPdfEnvironmentOwner} / "
                + $"{snapshot.ApplicationOwnership.QuickLookPdfEnvironmentState} / "
                + $"生成試行 {snapshot.ApplicationOwnership.QuickLookPdfEnvironmentCreationAttemptCount}"),
            new ValueRow("モデルレスWindow", snapshot.ApplicationOwnership.RegisteredModelessWindowCount.ToString()),
            new ValueRow("独立Workspace Window", snapshot.ApplicationOwnership.IndependentWorkspaceWindowCount.ToString()),
            new ValueRow("入力所有者", currentInput),
            new ValueRow("CPU（全論理CPU基準）", cpuSummary),
            new ValueRow("Working Set", RuntimeDiagnosticReportFormatter.FormatMegabytes(snapshot.Process.WorkingSetBytes) + " MB"),
            new ValueRow("Private Memory", RuntimeDiagnosticReportFormatter.FormatMegabytes(snapshot.Process.PrivateMemoryBytes) + " MB"),
            new ValueRow("WebView2 Private Memory", RuntimeDiagnosticReportFormatter.FormatMegabytes(snapshot.WebViewProcesses.PrivateMemoryBytes) + " MB"),
            new ValueRow("合計 Private Memory", RuntimeDiagnosticReportFormatter.FormatMegabytes(snapshot.TotalPrivateMemoryBytes) + " MB"),
            new ValueRow("Thread", snapshot.Process.ThreadCount.ToString()),
            new ValueRow("Handle", snapshot.Process.HandleCount.ToString()),
            new ValueRow("検出事項", $"エラー {snapshot.ErrorCount} / 警告 {snapshot.WarningCount}")
        };
    }

    private void UpdateWindowRows()
    {
        if (_currentSnapshot is not { } snapshot)
        {
            WindowGrid.ItemsSource = null;
            return;
        }

        var problemWindowIndexes = snapshot.Issues
            .Where(static issue => issue.WindowIndex.HasValue)
            .Select(static issue => issue.WindowIndex!.Value)
            .ToHashSet();
        var windows = snapshot.Windows.AsEnumerable();
        if (OnlyProblemWindowsCheckBox.IsChecked == true)
        {
            windows = windows.Where(window => problemWindowIndexes.Contains(window.WindowIndex));
        }

        WindowGrid.ItemsSource = windows.Select(window => new WindowRow(
            window.WindowIndex.ToString(),
            $"{window.TabCount} / WS {window.WorkspaceTabCount}",
            $"{window.TotalWebViewCount}（Tab {window.TabWebViewCount} / Mini {window.MiniPlayerWebViewCount} / 補助 {window.AuxiliaryWebViewCount}）",
            $"{window.SplitViewCount} / 無効 {window.InvalidSplitReferenceCount}",
            $"{window.MiniPlayerCount} / 再読込 {window.MiniPlayerReloadRecommendedCount}",
            window.OpenPopupCount.ToString(),
            $"読込 {FormatBoolean(window.IsBackgroundLoading)} / Thumbnail {FormatBoolean(window.IsWarmingBackgroundThumbnails)}"
            + $" / QL {window.Ownership.OpenQuickLookCount}"
            + $" / FolderView {window.Ownership.FolderTabViewCount}"
            + $" / MediaHost {window.Ownership.FloatingMediaHostLifetimeState}"
            + (window.Ownership.IsHiddenFloatingMediaHostActive ? "（保持中）" : string.Empty),
            problemWindowIndexes.Contains(window.WindowIndex) ? "要確認" : window.IsClosing ? "終了中" : "正常"))
            .ToList();
    }

    private void UpdateIssueRows()
    {
        IssueGrid.ItemsSource = _currentSnapshot?.Issues.Select(issue => new IssueRow(
                RuntimeDiagnosticReportFormatter.FormatSeverity(issue.Severity),
                BuildIssueScope(issue),
                issue.Code,
                issue.Message))
            .ToList();
    }

    private void UpdateInputRows()
    {
        InputGrid.ItemsSource = _currentSnapshot?.InputHistory
            .OrderByDescending(static entry => entry.Sequence)
            .Select(entry => new InputRow(
                entry.OccurredAt.ToString("HH:mm:ss.fff"),
                entry.Event.ToString(),
                entry.Kind.ToString(),
                ShortId(entry.WindowId),
                entry.TabId.HasValue ? ShortId(entry.TabId.Value) : string.Empty,
                entry.InteractionId?.ToString() ?? string.Empty))
            .ToList();
    }

    private void UpdateSampleRows()
    {
        var samples = _monitor.Samples;
        var firstTotalPrivateMemoryBytes = samples.FirstOrDefault()?.TotalPrivateMemoryBytes ?? 0;
        var intervalsByCapture = _monitor.ResourceIntervals
            .GroupBy(static interval => interval.CapturedAt)
            .ToDictionary(static group => group.Key, static group => group.Last());
        SampleGrid.ItemsSource = samples
            .OrderByDescending(static sample => sample.CapturedAt)
            .Select(sample =>
            {
                intervalsByCapture.TryGetValue(sample.CapturedAt, out var interval);
                return new SampleRow(
                    sample.CapturedAt.ToString("HH:mm:ss"),
                    interval is null ? "-" : RuntimeDiagnosticReportFormatter.FormatCpuPercent(interval.TotalCpuPercent),
                    interval is null ? "-" : RuntimeDiagnosticReportFormatter.FormatCpuPercent(interval.HostCpuPercent),
                    interval is null ? "-" : RuntimeDiagnosticReportFormatter.FormatCpuPercent(interval.WebViewCpuPercent),
                    interval is null ? "-" : RuntimeDiagnosticReportFormatter.FormatCpuPercent(interval.WebViewRendererCpuPercent),
                    interval is null ? "-" : RuntimeDiagnosticReportFormatter.FormatCpuPercent(interval.WebViewGpuCpuPercent),
                    RuntimeDiagnosticReportFormatter.FormatMegabytes(sample.TotalPrivateMemoryBytes),
                    RuntimeDiagnosticReportFormatter.FormatSignedMegabytes(
                        sample.TotalPrivateMemoryBytes - firstTotalPrivateMemoryBytes),
                    sample.WindowCount,
                    sample.TotalTabCount,
                    sample.TotalWebViewCount,
                    sample.TotalMiniPlayerCount,
                    sample.TotalOpenLiveChatWindowCount,
                    sample.Process.HandleCount,
                    sample.ErrorCount,
                    sample.WarningCount);
            })
            .ToList();
    }

    private string CreateCurrentReportText()
    {
        string runtimeReport;
        if (_lastReport is not null)
        {
            runtimeReport = RuntimeDiagnosticReportFormatter.FormatReport(_lastReport);
        }
        else if (_monitor.IsRunning)
        {
            runtimeReport = RuntimeDiagnosticReportFormatter.FormatReport(_monitor.CreateReport(DateTimeOffset.Now));
        }
        else
        {
            runtimeReport = _currentSnapshot is null
                ? string.Empty
                : RuntimeDiagnosticReportFormatter.FormatSnapshot(_currentSnapshot);
        }

        var startupReport = StartupPerformanceReportFormatter.Format(_captureStartupPerformance());
        return string.IsNullOrWhiteSpace(runtimeReport)
            ? startupReport
            : runtimeReport + Environment.NewLine + Environment.NewLine + startupReport;
    }

    private void UpdateStartupRows()
    {
        StartupGrid.ItemsSource = _captureStartupPerformance()
            .OrderBy(static entry => entry.Sequence)
            .Select(entry => new StartupRow(
                $"{entry.ElapsedMilliseconds} ms",
                StartupPerformanceStage.GetDisplayName(entry.Stage),
                entry.WindowIndex?.ToString() ?? string.Empty,
                entry.TabId.HasValue ? ShortId(entry.TabId.Value) : string.Empty,
                entry.ItemCount?.ToString() ?? string.Empty))
            .ToList();
    }

    private void UpdateMeasurementButtons()
    {
        StartButton.IsEnabled = !_monitor.IsRunning;
        StopButton.IsEnabled = _monitor.IsRunning;
    }

    private static string BuildIssueScope(RuntimeDiagnosticIssue issue)
    {
        var window = issue.WindowIndex.HasValue ? $"Window {issue.WindowIndex.Value}" : string.Empty;
        var tab = issue.TabId.HasValue ? $"Tab {ShortId(issue.TabId.Value)}" : string.Empty;
        return string.Join(" / ", new[] { window, tab }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ShortId(Guid id)
    {
        return id.ToString("N")[..8];
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "動作中" : "停止";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopMeasurement(captureFinalSample: false);
        _sampleTimer.Stop();
        _sampleTimer.Tick -= SampleTimer_Tick;
        base.OnClosing(e);
    }

    private sealed record ValueRow(string Name, string Value);

    private sealed record WindowRow(
        string Window,
        string Tabs,
        string WebViews,
        string SplitViews,
        string MiniPlayers,
        string Popups,
        string Background,
        string State);

    private sealed record IssueRow(string Severity, string Scope, string Code, string Message);

    private sealed record InputRow(
        string Time,
        string Event,
        string Kind,
        string Window,
        string Target,
        string Interaction);

    private sealed record SampleRow(
        string Time,
        string TotalCpu,
        string HostCpu,
        string WebViewCpu,
        string RendererCpu,
        string GpuCpu,
        long TotalPrivateMemoryMb,
        string TotalPrivateMemoryDeltaMb,
        int Windows,
        int Tabs,
        int WebViews,
        int MiniPlayers,
        int LiveChats,
        int Handles,
        int Errors,
        int Warnings);

    private sealed record StartupRow(
        string Elapsed,
        string Stage,
        string Window,
        string Tab,
        string Count);
}
