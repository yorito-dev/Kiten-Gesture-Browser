using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser;

public partial class MainWindow : IStartupWindowRestoreHost, IStartupTabActivationHost
{
    private static readonly DiagnosticLogWriter s_diagnosticLogWriter = new(GetSessionDiagnosticDirectory, GetSessionDiagnosticLogPath);
    private readonly StartupWindowRestoreController _startupWindowRestoreController;
    private readonly PerformanceDiagnosticLogController _performanceDiagnosticLogController = new(s_diagnosticLogWriter.AppendLines);
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

    private void FlushPerformanceDiagnosticLogLines()
        => _performanceDiagnosticLogController.Flush();

    bool IStartupWindowRestoreHost.IsClosingWindow => _isClosingWindow;

    void IStartupWindowRestoreHost.MarkRestoreStarted(int tabCount)
        => MarkStartupPerformance(StartupPerformanceStage.SecondaryWindowRestoreStarted, itemCount: tabCount);

    void IStartupWindowRestoreHost.RestoreWindow(SessionWindowState state, TimeSpan activationDelay)
    {
        var window = new MainWindow(state, activationDelay, restoreFloatingMediaPlaceholders: true);
        window.Show();
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
}
