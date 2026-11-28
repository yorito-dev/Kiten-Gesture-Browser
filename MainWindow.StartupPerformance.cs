using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow : IStartupWindowRestoreHost, IStartupTabActivationHost
{
    private static readonly DiagnosticLogWriter s_diagnosticLogWriter = new(GetSessionDiagnosticDirectory, GetSessionDiagnosticLogPath);
    private static readonly WebMessageExceptionDiagnosticGate s_webMessageExceptionDiagnosticGate = new(() => DateTimeOffset.UtcNow);
    private static bool s_restoredAudioPanNoticeShown;
    private readonly StartupWindowRestoreController _startupWindowRestoreController;
    private readonly PerformanceDiagnosticLogController _performanceDiagnosticLogController;
    private OwnedDispatcherTimer? _performanceDiagnosticFlushTimer;

    private void InitializeStartupPerformanceTracking()
    {
        ContentRendered += MainWindow_ContentRenderedForStartupPerformance;
        _performanceDiagnosticFlushTimer = RegisterWindowShutdownTimer(new OwnedDispatcherTimer(
            DispatcherTimerOwner.MainWindow,
            DispatcherTimerTerminationPhase.OrderedWindowShutdown,
            2)
        {
            Interval = TimeSpan.FromMilliseconds(PerformanceDiagnosticLogController.FlushIntervalMilliseconds)
        });
        _performanceDiagnosticLogController.AttachTimer(_performanceDiagnosticFlushTimer);
    }

    private void MainWindow_ContentRenderedForStartupPerformance(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRenderedForStartupPerformance;
        MarkStartupPerformance(StartupPerformanceStage.WindowContentRendered);
    }

    private void MarkStartupPerformance(
        string stage,
        BrowserTab? tab = null,
        int? itemCount = null)
    {
        App.StartupPerformance.Mark(
            stage,
            _workspaceWindowIndex,
            tab?.Id,
            itemCount);
    }

    private void MarkSelectedTabNavigationCompleted(BrowserTab tab)
    {
        if (ActiveTab != tab
            || App.StartupPerformance.HasStage(
                StartupPerformanceStage.SelectedTabNavigationCompleted,
                _workspaceWindowIndex))
        {
            return;
        }

        MarkStartupPerformance(StartupPerformanceStage.SelectedTabNavigationCompleted, tab);
    }

    private void QueuePerformanceDiagnosticLogLine(IReadOnlyList<string> fields)
        => _performanceDiagnosticLogController.Queue(fields);

    internal void RecordKeyboardHookProbeDelay(uint milliseconds)
        => _performanceDiagnosticLogController.RecordKeyboardHookProbeDelay(milliseconds);

    private void FlushPerformanceDiagnosticLogLines()
        => _performanceDiagnosticLogController.FlushForShutdown();

    bool IStartupWindowRestoreHost.IsClosingWindow => _isClosingWindow;

    void IStartupWindowRestoreHost.MarkRestoreStarted(int tabCount)
        => MarkStartupPerformance(StartupPerformanceStage.SecondaryWindowRestoreStarted, itemCount: tabCount);

    void IStartupWindowRestoreHost.RestoreWindow(SessionWindowState state, TimeSpan activationDelay)
    {
        var window = new MainWindow(state, activationDelay, restoreFloatingMediaPlaceholders: true);
        try
        {
            window.Show();
        }
        catch
        {
            window.CompleteStartupFloatingMediaRestore();
            throw;
        }

        window.MarkStartupPerformance(StartupPerformanceStage.SecondaryWindowShown, itemCount: state.Tabs?.Count ?? 0);
    }

    void IStartupWindowRestoreHost.RecordRestoreFailure(Exception exception, int tabCount)
    {
        _sessionRestorationController.RecordWarning();
        AppendSessionDiagnostic("window-restore-failed", exception, $"windowTabs={tabCount}");
    }

    void IStartupWindowRestoreHost.ReportSkippedWindows(int count)
        => SetStatus($"{count} 件のウィンドウ復元をスキップしました", autoClear: true);

    bool IStartupTabActivationHost.IsClosingWindow => _isClosingWindow;
    TimeSpan IStartupTabActivationHost.StartupActivationDelay => _startupActivationDelay;
    BrowserTab? IStartupTabActivationHost.ActiveTab => ActiveTab;
    int IStartupTabActivationHost.TabCount => _tabs.Count;
    int IStartupTabActivationHost.RestoreWarningCount => _sessionRestorationController.WarningCount;
    Task IStartupTabActivationHost.ActivateSelectedTabAsync(CancellationToken cancellationToken)
        => ActivateSelectedTabAsync(cancellationToken);
    void IStartupTabActivationHost.MarkStartupPerformance(string stage, BrowserTab? tab, int? itemCount)
        => MarkStartupPerformance(stage, tab, itemCount);
    void IStartupTabActivationHost.AppendSessionDiagnostic(string operation, Exception exception)
        => AppendSessionDiagnostic(operation, exception);
    void IStartupTabActivationHost.SetStatus(string message, bool autoClear) => SetStatus(message, autoClear);
    void IStartupTabActivationHost.QueueBackgroundTabLoading() => QueueBackgroundTabLoading();
    void IStartupTabActivationHost.QueueBackgroundThumbnailWarmup() => QueueBackgroundThumbnailWarmup();
    void IStartupTabActivationHost.RestartFocusedViewingCpuSaverTimer() => RestartFocusedViewingCpuSaverTimer();
    void IStartupTabActivationHost.RestartTabMemoryManagementTimer() => RestartTabMemoryManagementTimer();
    string? IStartupTabActivationHost.CreateRestoredTabLoadFailureStatus() => CreateRestoredTabLoadFailureStatus();

    string? IStartupTabActivationHost.CreateRestoredAudioPanNotice()
    {
        if (s_restoredAudioPanNoticeShown
            || !_sessionRestorationController.ShouldShowRestoredAudioPanNotice(
                _settings, _restoreFloatingMediaPlaceholders, _isPrivateWindow))
        {
            return null;
        }

        // 各Windowの起動完了処理は同じDispatcherで実行される。
        s_restoredAudioPanNoticeShown = true;
        _settings.RestoredAudioPanNoticeShown = true;
        return "復元したタブの左右振り分けをWindowsで使うには「設定 → メディア → タブの音声位置」で許可してください。";
    }

    async Task IStartupTabActivationHost.SaveRestoredAudioPanNoticeAsync()
    {
        try
        {
            await SettingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("restore-audio-pan-notice-save-failed", ex);
        }
    }
}
