using System.IO;
using System.Text;

namespace TabBrowser.Services;

public sealed class ApplicationStabilityService
{
    private const int MaximumStackTraceLength = 16_000;

    private readonly string _markerFile;
    private readonly string _diagnosticsFolder;
    private readonly HashSet<string> _recordedSources = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _runStarted;
    private bool _runCompleted;

    public ApplicationStabilityService()
        : this(AppPaths.ActiveRunMarkerFile, AppPaths.RuntimeDiagnosticsFolder)
    {
    }

    public ApplicationStabilityService(string markerFile, string diagnosticsFolder)
    {
        _markerFile = markerFile;
        _diagnosticsFolder = diagnosticsFolder;
    }

    public static bool PreviousRunEndedUnexpectedly { get; private set; }

    internal static bool IsRecoverableWpfItemsControlAutomationException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsRecoverableWpfItemsControlAutomationException(
            exception.GetType(),
            exception.StackTrace);
    }

    internal static bool IsRecoverableWpfItemsControlAutomationException(
        Type exceptionType,
        string? stackTrace)
    {
        return exceptionType == typeof(KeyNotFoundException)
            && !string.IsNullOrWhiteSpace(stackTrace)
            && stackTrace.Contains(
                "MS.Internal.WeakDictionary`2.get_Item",
                StringComparison.Ordinal)
            && stackTrace.Contains(
                "System.Windows.Automation.Peers.ItemPeersStorage`1.get_Item",
                StringComparison.Ordinal)
            && stackTrace.Contains(
                "System.Windows.Automation.Peers.ItemsControlAutomationPeer.GetChildrenCore",
                StringComparison.Ordinal)
            && stackTrace.Contains(
                "System.Windows.ContextLayoutManager.fireAutomationEvents",
                StringComparison.Ordinal);
    }

    public bool StartRun()
    {
        lock (_gate)
        {
            if (_runStarted)
            {
                return PreviousRunEndedUnexpectedly;
            }

            _runStarted = true;
            PreviousRunEndedUnexpectedly = File.Exists(_markerFile);
            TryWriteMarker();
            return PreviousRunEndedUnexpectedly;
        }
    }

    public void RecordException(string source, Exception exception, bool isTerminating)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var normalizedSource = NormalizeSource(source);
        lock (_gate)
        {
            if (!_recordedSources.Add(normalizedSource))
            {
                return;
            }

            try
            {
                var diagnosticFile = Path.Combine(_diagnosticsFolder, $"latest-{normalizedSource}.log");
                AtomicFileService.Write(
                    diagnosticFile,
                    backupFilePath: null,
                    stream =>
                    {
                        using var writer = new StreamWriter(
                            stream,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            bufferSize: 1024,
                            leaveOpen: true);
                        writer.WriteLine($"captured_at={DateTimeOffset.Now:O}");
                        writer.WriteLine($"source={normalizedSource}");
                        writer.WriteLine($"terminating={isTerminating}");
                        writer.WriteLine($"process_id={Environment.ProcessId}");
                        writer.WriteLine($"exception_type={exception.GetType().FullName}");
                        writer.WriteLine($"hresult={exception.HResult}");
                        writer.WriteLine($"inner_types={FormatInnerExceptionTypes(exception)}");
                        writer.WriteLine("stack_trace=");
                        writer.Write(Truncate(exception.StackTrace ?? "利用できません", MaximumStackTraceLength));
                    });
            }
            catch (Exception writeException) when (writeException is not OutOfMemoryException
                                                    and not StackOverflowException
                                                    and not AccessViolationException)
            {
            }
        }
    }

    public void CompleteRun()
    {
        lock (_gate)
        {
            if (!_runStarted || _runCompleted)
            {
                return;
            }

            _runCompleted = true;
            try
            {
                if (File.Exists(_markerFile)
                    && File.ReadAllText(_markerFile).Contains(
                        $"process_id={Environment.ProcessId}",
                        StringComparison.Ordinal))
                {
                    File.Delete(_markerFile);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void TryWriteMarker()
    {
        try
        {
            AtomicFileService.Write(
                _markerFile,
                backupFilePath: null,
                stream =>
                {
                    using var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 1024,
                        leaveOpen: true);
                    writer.WriteLine($"started_at={DateTimeOffset.Now:O}");
                    writer.WriteLine($"process_id={Environment.ProcessId}");
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeSource(string source)
    {
        var normalized = new string((source ?? string.Empty)
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized[..Math.Min(40, normalized.Length)];
    }

    private static string FormatInnerExceptionTypes(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception.InnerException; current is not null && types.Count < 8; current = current.InnerException)
        {
            types.Add(current.GetType().FullName ?? current.GetType().Name);
        }

        return types.Count == 0 ? "none" : string.Join(",", types);
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}
