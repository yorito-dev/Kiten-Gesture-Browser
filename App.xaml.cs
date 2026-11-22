using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TabBrowser.Services;

namespace TabBrowser;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly ApplicationStabilityService _applicationStabilityService = new();
    private readonly SingleInstanceService _singleInstanceService = new();
    private readonly HashSet<Window> _keyboardShortcutWindows = [];
    private ApplicationKeyboardShortcutHook? _keyboardShortcutHook;
    private ApplicationHangWatchdog? _applicationHangWatchdog;
    private bool _isPrimaryInstance;
    private Task _privateBrowsingStartupCleanupTask = Task.CompletedTask;

    public static StartupPerformanceTracker StartupPerformance { get; } = new();

    public static ClipboardHistoryService ClipboardHistory { get; } = new();

    public static BrowsingHistoryService BrowsingHistory { get; } = new();

    public static RecentItemsService RecentItems { get; } = new();

    internal static BookmarkStateStore Bookmarks { get; } = new(new BookmarkStore(new BookmarkService()));

    internal bool IsSystemSessionEndApproved { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppContext.SetSwitch(
            "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering",
            false);

        if (!_singleInstanceService.TryAcquirePrimaryInstance())
        {
            SingleInstanceService.TryActivateExistingInstance();
            Shutdown();
            return;
        }

        if (File.Exists(AppPaths.RestoreInProgressMarkerFile)
            || Directory.Exists(AppPaths.RestoreInProgressMarkerFile))
        {
            new RestoreIncompleteWarningWindow().ShowDialog();
            Shutdown();
            return;
        }

        _isPrimaryInstance = true;
        _applicationStabilityService.StartRun();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        StartupPerformance.Mark(StartupPerformanceStage.ApplicationStarted);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ApplyWindowTheme));
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(WatchKeyboardShortcutWindow));
        EventManager.RegisterClassHandler(
            typeof(ButtonBase),
            ButtonBase.ClickEvent,
            new RoutedEventHandler(RecordButtonActivityForHangDiagnostics),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(MenuItem),
            MenuItem.ClickEvent,
            new RoutedEventHandler(RecordMenuActivityForHangDiagnostics),
            handledEventsToo: true);
        Action<uint>? recordKeyboardHookProbe = null;
        if (ApplicationChannelService.Current == ApplicationChannel.Development
            && Environment.GetEnvironmentVariable("KITEN_DEV_MEASURE_KEYBOARD_HOOK") == "1")
        {
            recordKeyboardHookProbe = delay => Windows.OfType<MainWindow>()
                .FirstOrDefault(static window => window.IsActive && !window.IsPrivateBrowsing)
                ?.RecordKeyboardHookProbeDelay(delay);
        }
        _keyboardShortcutHook = new ApplicationKeyboardShortcutHook(
            () => Windows.OfType<MainWindow>(), recordProbeDelay: recordKeyboardHookProbe);
        _keyboardShortcutHook.Start();
        base.OnStartup(e);
        _applicationHangWatchdog = new ApplicationHangWatchdog(Dispatcher);
        _privateBrowsingStartupCleanupTask = PrivateBrowsingDataService.CleanupStaleContextsAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isPrimaryInstance)
        {
            _keyboardShortcutHook?.Dispose();
            _keyboardShortcutHook = null;
            foreach (var window in _keyboardShortcutWindows)
            {
                window.Activated -= OnKeyboardShortcutWindowActivated;
                window.Closed -= OnKeyboardShortcutWindowClosed;
            }
            _keyboardShortcutWindows.Clear();
            ClipboardHistory.Dispose();
            if (!IsSystemSessionEndApproved)
            {
                RunExitCleanup(async () =>
                {
                    await BrowsingHistory.DisposeAsync().ConfigureAwait(false);
                    await RecentItems.DisposeAsync().ConfigureAwait(false);
                    try
                    {
                        await _privateBrowsingStartupCleanupTask
                            .WaitAsync(TimeSpan.FromSeconds(2))
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        PrivateBrowsingDataService.RecordStartupCleanupFailure(
                            "startup-cleanup-exit-timeout",
                            ex);
                    }
                });
            }
            _applicationHangWatchdog?.Dispose();
            _applicationHangWatchdog = null;
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
            _applicationStabilityService.CompleteRun();
        }

        base.OnExit(e);
        _singleInstanceService.Dispose();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // OS終了の通知内では、既存の保存状態だけを確かめる。
        var pendingWindows = Windows.OfType<MainWindow>()
            .Where(static window => !window.IsWindowShutdownCompleted).ToList();
        var mustDefer = pendingWindows.Count > 0 && !CanAllowSystemSessionEnd(pendingWindows);
        if (mustDefer)
        {
            e.Cancel = true;
            foreach (var window in pendingWindows) window.NotifySystemShutdownDeferred();
        }
        base.OnSessionEnding(e);
        e.Cancel |= mustDefer;
        IsSystemSessionEndApproved = pendingWindows.Count > 0 && !e.Cancel;
    }

    private bool CanAllowSystemSessionEnd(IReadOnlyList<MainWindow> pendingWindows)
    {
        try
        {
            return _privateBrowsingStartupCleanupTask.IsCompletedSuccessfully
                && !Windows.OfType<Window>().Any(static window => window is not TabBrowser.MainWindow && window.IsVisible)
                && System.Windows.Input.Mouse.Captured is null
                && Bookmarks.CanConfirmPersistedState()
                && BrowsingHistory.CanConfirmPersistedState()
                && RecentItems.CanConfirmPersistedState()
                && TabBrowser.MainWindow.CanEndSystemSession(pendingWindows);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Windows終了用の保存状態を確認できません: {0}", exception.GetType().Name);
            return false;
        }
    }

    internal static void RunExitCleanup(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        Task.Run(cleanup).GetAwaiter().GetResult();
    }

    internal void RecordUiOperationException(string operation, Exception exception, bool includeStackTrace = true)
        => _applicationStabilityService.RecordException($"ui-{operation}", exception, isTerminating: false, includeStackTrace: includeStackTrace);

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var isRecoverableWpfAutomationException =
            ApplicationStabilityService.IsRecoverableWpfItemsControlAutomationException(e.Exception);
        _applicationStabilityService.RecordException(
            isRecoverableWpfAutomationException ? "dispatcher-wpf-automation" : "dispatcher",
            e.Exception,
            isTerminating: !isRecoverableWpfAutomationException);
        if (isRecoverableWpfAutomationException)
        {
            e.Handled = true;
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _applicationStabilityService.RecordException("app-domain", exception, e.IsTerminating);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _applicationStabilityService.RecordException("unobserved-task", e.Exception, isTerminating: false);
    }

    private static void ApplyWindowTheme(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            WindowThemeService.ApplyDarkTitleBar(window);
        }
    }

    private void WatchKeyboardShortcutWindow(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && _keyboardShortcutWindows.Add(window))
        {
            window.Activated += OnKeyboardShortcutWindowActivated;
            window.Closed += OnKeyboardShortcutWindowClosed;
        }
    }

    private void OnKeyboardShortcutWindowActivated(object? sender, EventArgs e)
        => _keyboardShortcutHook?.Reinstall();

    private void OnKeyboardShortcutWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window && _keyboardShortcutWindows.Remove(window))
        {
            window.Activated -= OnKeyboardShortcutWindowActivated;
            window.Closed -= OnKeyboardShortcutWindowClosed;
        }
    }

    private void RecordButtonActivityForHangDiagnostics(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _applicationHangWatchdog?.RecordUiActivity("button", element);
        }
    }

    private void RecordMenuActivityForHangDiagnostics(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _applicationHangWatchdog?.RecordUiActivity("menu", element);
        }
    }
}
