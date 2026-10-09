using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TabBrowser.Services;

internal sealed record ApplicationHangUiSnapshot(
    DateTimeOffset CapturedAt,
    int ManagedThreadId,
    uint NativeThreadId,
    int WindowCount,
    string ActiveWindowType,
    string FocusedElementType,
    string LastUiActivity,
    DateTimeOffset? LastUiActivityAt);

internal sealed record ApplicationHangIncident(
    DateTimeOffset DetectedAt,
    TimeSpan UnresponsiveDuration,
    int Sequence,
    ApplicationHangUiSnapshot LastUiSnapshot);

internal sealed class ApplicationHangEpisodeTracker
{
    private readonly TimeSpan _threshold;
    private readonly int _maximumIncidentCount;
    private bool _isHangActive;
    private int _incidentCount;

    public ApplicationHangEpisodeTracker(TimeSpan threshold, int maximumIncidentCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumIncidentCount, 0);
        _threshold = threshold;
        _maximumIncidentCount = maximumIncidentCount;
    }

    public bool TryBeginHang(TimeSpan unresponsiveDuration, out int sequence)
    {
        sequence = 0;
        if (unresponsiveDuration < _threshold
            || _isHangActive
            || _incidentCount >= _maximumIncidentCount)
        {
            return false;
        }

        _isHangActive = true;
        sequence = ++_incidentCount;
        return true;
    }

    public void MarkResponsive()
    {
        _isHangActive = false;
    }
}

public sealed class ApplicationHangWatchdog : IDisposable
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(10);
    internal const int MaximumIncidentCount = 3;

    private const string DiagnosticFileName = "latest-ui-hang.log";
    private readonly Dispatcher _dispatcher;
    private readonly string _diagnosticsFolder;
    private readonly ApplicationHangEpisodeTracker _episodeTracker = new(
        HangThreshold,
        MaximumIncidentCount);
    private readonly object _episodeGate = new();
    private readonly System.Threading.Timer _timer;
    private ApplicationHangUiSnapshot _lastUiSnapshot;
    private string _lastUiActivity = "none";
    private DateTimeOffset? _lastUiActivityAt;
    private long _lastHeartbeatTimestamp;
    private int _heartbeatQueued;
    private int _disposed;

    public ApplicationHangWatchdog(Dispatcher dispatcher)
        : this(dispatcher, AppPaths.RuntimeDiagnosticsFolder)
    {
    }

    internal ApplicationHangWatchdog(Dispatcher dispatcher, string diagnosticsFolder)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _diagnosticsFolder = diagnosticsFolder;
        _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _lastUiSnapshot = CaptureUiSnapshot();
        _timer = new System.Threading.Timer(
            static state => ((ApplicationHangWatchdog)state!).OnTimer(),
            this,
            HeartbeatInterval,
            HeartbeatInterval);
    }

    public void RecordUiActivity(string activityKind, FrameworkElement element)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var normalizedKind = NormalizeIdentifier(activityKind, "ui");
        var elementIdentifier = string.IsNullOrWhiteSpace(element.Name)
            ? element.GetType().Name
            : element.Name;
        _lastUiActivity = $"{normalizedKind}:{NormalizeIdentifier(elementIdentifier, "unnamed")}";
        _lastUiActivityAt = DateTimeOffset.Now;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Dispose();
    }

    internal static void WriteDiagnostic(
        string diagnosticsFolder,
        ApplicationHangIncident incident)
    {
        var diagnosticFile = Path.Combine(diagnosticsFolder, DiagnosticFileName);
        var backupFile = AtomicFileService.CreateBackupFilePath(diagnosticFile);
        AtomicFileService.Write(
            diagnosticFile,
            backupFile,
            stream =>
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                WriteIncident(writer, incident);
            });
    }

    private void OnTimer()
    {
        if (Volatile.Read(ref _disposed) != 0
            || _dispatcher.HasShutdownStarted
            || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        QueueHeartbeat();
        var unresponsiveDuration = Stopwatch.GetElapsedTime(
            Interlocked.Read(ref _lastHeartbeatTimestamp));
        int sequence;
        lock (_episodeGate)
        {
            if (!_episodeTracker.TryBeginHang(unresponsiveDuration, out sequence))
            {
                return;
            }
        }

        try
        {
            WriteDiagnostic(
                _diagnosticsFolder,
                new ApplicationHangIncident(
                    DateTimeOffset.Now,
                    unresponsiveDuration,
                    sequence,
                    Volatile.Read(ref _lastUiSnapshot)));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException)
        {
            // ハング診断の保存失敗でアプリ本体へ例外を返さない。
        }
    }

    private void QueueHeartbeat()
    {
        if (Interlocked.CompareExchange(ref _heartbeatQueued, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(AcknowledgeHeartbeat));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _heartbeatQueued, 0);
        }
    }

    private void AcknowledgeHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastUiSnapshot, CaptureUiSnapshot());
        Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _heartbeatQueued, 0);
        lock (_episodeGate)
        {
            _episodeTracker.MarkResponsive();
        }
    }

    private ApplicationHangUiSnapshot CaptureUiSnapshot()
    {
        var application = Application.Current;
        var windows = application?.Windows.OfType<Window>().ToList() ?? [];
        var activeWindow = windows.FirstOrDefault(static window => window.IsActive);
        return new ApplicationHangUiSnapshot(
            DateTimeOffset.Now,
            Environment.CurrentManagedThreadId,
            GetCurrentThreadId(),
            windows.Count,
            activeWindow?.GetType().Name ?? "none",
            Keyboard.FocusedElement?.GetType().Name ?? "none",
            _lastUiActivity,
            _lastUiActivityAt);
    }

    private static void WriteIncident(TextWriter writer, ApplicationHangIncident incident)
    {
        var snapshot = incident.LastUiSnapshot;
        writer.WriteLine($"captured_at={incident.DetectedAt:O}");
        writer.WriteLine("source=ui-hang-watchdog");
        writer.WriteLine($"process_id={Environment.ProcessId}");
        writer.WriteLine($"channel={ApplicationChannelService.Current}");
        writer.WriteLine($"application_version={typeof(ApplicationHangWatchdog).Assembly.GetName().Version}");
        writer.WriteLine($"hang_sequence={incident.Sequence}");
        writer.WriteLine($"unresponsive_ms={(long)incident.UnresponsiveDuration.TotalMilliseconds}");
        writer.WriteLine($"last_ui_heartbeat_at={snapshot.CapturedAt:O}");
        writer.WriteLine($"ui_managed_thread_id={snapshot.ManagedThreadId}");
        writer.WriteLine($"ui_native_thread_id={snapshot.NativeThreadId}");
        writer.WriteLine($"window_count={snapshot.WindowCount}");
        writer.WriteLine($"active_window_type={snapshot.ActiveWindowType}");
        writer.WriteLine($"focused_element_type={snapshot.FocusedElementType}");
        writer.WriteLine($"last_ui_activity={snapshot.LastUiActivity}");
        writer.WriteLine($"last_ui_activity_at={FormatTimestamp(snapshot.LastUiActivityAt)}");
        WriteProcessMetrics(writer, snapshot.NativeThreadId);
    }

    private static void WriteProcessMetrics(TextWriter writer, uint uiNativeThreadId)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            writer.WriteLine($"working_set_mb={ToMegabytes(process.WorkingSet64)}");
            writer.WriteLine($"private_memory_mb={ToMegabytes(process.PrivateMemorySize64)}");
            writer.WriteLine($"thread_count={process.Threads.Count}");
            writer.WriteLine($"handle_count={process.HandleCount}");
            writer.WriteLine($"process_cpu_ms={(long)process.TotalProcessorTime.TotalMilliseconds}");
            writer.WriteLine($"managed_heap_mb={ToMegabytes(GC.GetTotalMemory(forceFullCollection: false))}");

            var threadSnapshots = new List<(int Id, string State, string WaitReason, long CpuMilliseconds)>();
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    var state = thread.ThreadState.ToString();
                    var waitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                        ? thread.WaitReason.ToString()
                        : "none";
                    threadSnapshots.Add((
                        thread.Id,
                        state,
                        waitReason,
                        (long)thread.TotalProcessorTime.TotalMilliseconds));
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    thread.Dispose();
                }
            }

            var uiThread = threadSnapshots.FirstOrDefault(thread => thread.Id == uiNativeThreadId);
            writer.WriteLine($"ui_thread_state={uiThread.State ?? "unavailable"}");
            writer.WriteLine($"ui_thread_wait_reason={uiThread.WaitReason ?? "unavailable"}");
            var topThreads = threadSnapshots
                .OrderByDescending(static thread => thread.CpuMilliseconds)
                .Take(5)
                .Select(static thread =>
                    $"{thread.Id}:{thread.State}:{thread.WaitReason}:{thread.CpuMilliseconds}");
            writer.WriteLine($"top_cpu_threads={string.Join(',', topThreads)}");
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            writer.WriteLine("process_metrics=unavailable");
        }
    }

    private static string NormalizeIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = new string(value
            .Where(static character => char.IsAsciiLetterOrDigit(character)
                                       || character is '_' or '-')
            .Take(80)
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.ToString("O") ?? "none";
    }

    private static long ToMegabytes(long bytes)
    {
        return (long)Math.Round(bytes / 1024d / 1024d);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
