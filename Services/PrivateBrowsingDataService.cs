using System.Diagnostics;
using System.Globalization;
using System.IO;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class PrivateBrowsingDataService
{
    private static readonly PrivateBrowsingDataStore Store = CreateStore();
    private static readonly object DiagnosticLogGate = new();

    public static PrivateBrowsingContext CreateContext()
    {
        return Store.CreateContext();
    }

    public static Task<PrivateBrowsingDeleteResult> DeleteContextAsync(
        PrivateBrowsingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.Run(
            () => Store.DeleteContextAsync(context, cancellationToken),
            CancellationToken.None);
    }

    public static Task<IReadOnlyList<PrivateBrowsingDeleteResult>> CleanupStaleContextsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Store.CleanupStaleContextsAsync(cancellationToken),
            CancellationToken.None);
    }

    internal static void RecordCleanupFailure(
        PrivateBrowsingContext context,
        string reason,
        Exception? exception = null)
    {
        Store.RecordCleanupFailure(context, reason, exception);
    }

    internal static void RecordStartupCleanupFailure(string reason, Exception? exception = null)
    {
        Store.RecordCleanupFailure(
            new PrivateBrowsingContext(Guid.Empty, AppPaths.PrivateBrowsingFolder),
            reason,
            exception);
    }

    private static PrivateBrowsingDataStore CreateStore()
    {
        using var process = Process.GetCurrentProcess();
        return new PrivateBrowsingDataStore(
            AppPaths.PrivateBrowsingFolder,
            Environment.ProcessId,
            new DateTimeOffset(process.StartTime).ToUniversalTime(),
            Guid.NewGuid(),
            TryGetProcessStartTimeUtc,
            WriteDiagnostic);
    }

    private static DateTimeOffset? TryGetProcessStartTimeUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited
                ? null
                : new DateTimeOffset(process.StartTime).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDiagnostic(PrivateBrowsingDeleteDiagnostic diagnostic)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RuntimeDiagnosticsFolder);
            var logPath = Path.Combine(
                AppPaths.RuntimeDiagnosticsFolder,
                "private-browsing-cleanup.log");
            var line = string.Join(
                " | ",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                $"operation={diagnostic.Operation}",
                $"context={diagnostic.ContextId:N}",
                $"folder={diagnostic.FolderName}",
                $"reason={diagnostic.Reason}",
                $"exception={diagnostic.ExceptionType ?? "none"}");
            lock (DiagnosticLogGate)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 診断ログの失敗で終了処理を止めない。
        }
    }
}

internal sealed class PrivateBrowsingDataStore
{
    private const string MarkerFileName = "context.marker";
    private const int DeleteAttemptCount = 6;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _privateBrowsingFolder;
    private readonly int _currentProcessId;
    private readonly DateTimeOffset _currentProcessStartTimeUtc;
    private readonly Guid _launchId;
    private readonly Func<int, DateTimeOffset?> _getProcessStartTimeUtc;
    private readonly Action<PrivateBrowsingDeleteDiagnostic> _recordDiagnostic;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, IEnumerable<string>> _enumerateDirectories;
    private readonly Action<string, bool> _deleteDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    internal PrivateBrowsingDataStore(
        string privateBrowsingFolder,
        int currentProcessId,
        DateTimeOffset currentProcessStartTimeUtc,
        Guid launchId,
        Func<int, DateTimeOffset?> getProcessStartTimeUtc,
        Action<PrivateBrowsingDeleteDiagnostic>? recordDiagnostic = null,
        Func<string, bool>? directoryExists = null,
        Func<string, IEnumerable<string>>? enumerateDirectories = null,
        Action<string, bool>? deleteDirectory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateBrowsingFolder);
        ArgumentNullException.ThrowIfNull(getProcessStartTimeUtc);

        _privateBrowsingFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateBrowsingFolder));
        _currentProcessId = currentProcessId;
        _currentProcessStartTimeUtc = currentProcessStartTimeUtc.ToUniversalTime();
        _launchId = launchId;
        _getProcessStartTimeUtc = getProcessStartTimeUtc;
        _recordDiagnostic = recordDiagnostic ?? (_ => { });
        _directoryExists = directoryExists ?? Directory.Exists;
        _enumerateDirectories = enumerateDirectories ?? Directory.EnumerateDirectories;
        _deleteDirectory = deleteDirectory ?? Directory.Delete;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    internal PrivateBrowsingContext CreateContext()
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(_privateBrowsingFolder, $"{_currentProcessId}-{id:N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, MarkerFileName),
            string.Join(
                Environment.NewLine,
                $"process_id={_currentProcessId}",
                $"process_started_utc_ticks={_currentProcessStartTimeUtc.UtcDateTime.Ticks}",
                $"launch_id={_launchId:N}",
                $"context_id={id:N}",
                string.Empty));
        return new PrivateBrowsingContext(id, folder);
    }

    internal async Task<PrivateBrowsingDeleteResult> DeleteContextAsync(
        PrivateBrowsingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryValidateContextFolder(context, out var folder, out var validationFailure))
        {
            var invalidResult = new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                validationFailure);
            RecordCleanupFailure(context, validationFailure);
            return invalidResult;
        }

        for (var attempt = 1; attempt <= DeleteAttemptCount; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new PrivateBrowsingDeleteResult(
                    PrivateBrowsingDeleteStatus.Failed,
                    attempt - 1,
                    "cancelled");
            }

            try
            {
                if (!_directoryExists(folder))
                {
                    return new PrivateBrowsingDeleteResult(
                        PrivateBrowsingDeleteStatus.NotFound,
                        attempt - 1);
                }

                _deleteDirectory(folder, true);
                return new PrivateBrowsingDeleteResult(
                    PrivateBrowsingDeleteStatus.Deleted,
                    attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttemptCount)
                {
                    var failedResult = new PrivateBrowsingDeleteResult(
                        PrivateBrowsingDeleteStatus.Failed,
                        attempt,
                        "delete-retries-exhausted");
                    RecordCleanupFailure(context, failedResult.FailureReason!, ex);
                    return failedResult;
                }

                try
                {
                    await _delayAsync(
                        TimeSpan.FromTicks(RetryDelay.Ticks * attempt),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new PrivateBrowsingDeleteResult(
                        PrivateBrowsingDeleteStatus.Failed,
                        attempt,
                        "cancelled");
                }
            }
        }

        throw new UnreachableException();
    }

    internal async Task<IReadOnlyList<PrivateBrowsingDeleteResult>> CleanupStaleContextsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<PrivateBrowsingDeleteResult>();
        try
        {
            if (!_directoryExists(_privateBrowsingFolder))
            {
                return results;
            }

            foreach (var folder in _enumerateDirectories(_privateBrowsingFolder).ToList())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (IsOwnedByRunningProcess(folder))
                {
                    continue;
                }

                var contextId = TryGetContextIdFromFolderName(folder, out var parsedContextId)
                    ? parsedContextId
                    : Guid.Empty;
                results.Add(await DeleteContextAsync(
                    new PrivateBrowsingContext(contextId, folder),
                    cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var context = new PrivateBrowsingContext(Guid.Empty, _privateBrowsingFolder);
            RecordCleanupFailure(context, "enumeration-failed", ex);
            results.Add(new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                "enumeration-failed"));
        }

        return results;
    }

    internal void RecordCleanupFailure(
        PrivateBrowsingContext context,
        string reason,
        Exception? exception = null)
    {
        var folderName = string.Empty;
        try
        {
            folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(context.UserDataFolder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        _recordDiagnostic(new PrivateBrowsingDeleteDiagnostic(
            "delete-failed",
            context.Id,
            folderName,
            reason,
            exception?.GetType().Name));
    }

    private bool IsOwnedByRunningProcess(string folder)
    {
        try
        {
            if (!TryValidateDirectChild(folder, out var validatedFolder, out _))
            {
                return false;
            }

            var marker = Path.Combine(validatedFolder, MarkerFileName);
            if (!File.Exists(marker)
                || !TryReadMarker(marker, out var owner))
            {
                return false;
            }

            if (owner.ProcessId == _currentProcessId
                && owner.LaunchId == _launchId
                && owner.ProcessStartTimeUtc.UtcDateTime.Ticks
                    == _currentProcessStartTimeUtc.UtcDateTime.Ticks)
            {
                return true;
            }

            var runningProcessStartTime = _getProcessStartTimeUtc(owner.ProcessId);
            return runningProcessStartTime.HasValue
                && runningProcessStartTime.Value.UtcDateTime.Ticks
                    == owner.ProcessStartTimeUtc.UtcDateTime.Ticks;
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or ArgumentException
                                     or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryValidateContextFolder(
        PrivateBrowsingContext context,
        out string folder,
        out string failureReason)
    {
        if (!TryValidateDirectChild(context.UserDataFolder, out folder, out failureReason))
        {
            return false;
        }

        if (!TryGetContextIdFromFolderName(folder, out var folderContextId))
        {
            failureReason = "invalid-context-folder-name";
            return false;
        }

        if (context.Id != Guid.Empty && context.Id != folderContextId)
        {
            failureReason = "context-id-mismatch";
            return false;
        }

        try
        {
            if (_directoryExists(folder)
                && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "reparse-point-rejected";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failureReason = "folder-attributes-unavailable";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool TryValidateDirectChild(
        string candidate,
        out string folder,
        out string failureReason)
    {
        folder = string.Empty;
        try
        {
            if (_directoryExists(_privateBrowsingFolder)
                && (File.GetAttributes(_privateBrowsingFolder) & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "private-root-reparse-point-rejected";
                return false;
            }

            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var parent = Directory.GetParent(folder)?.FullName;
            if (parent is null
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    _privateBrowsingFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "outside-private-browsing-folder";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                     or NotSupportedException
                                     or PathTooLongException)
        {
            failureReason = "invalid-context-path";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TryGetContextIdFromFolderName(string folder, out Guid contextId)
    {
        contextId = Guid.Empty;
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var separatorIndex = folderName.IndexOf('-', StringComparison.Ordinal);
        return separatorIndex > 0
            && int.TryParse(folderName[..separatorIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            && processId > 0
            && Guid.TryParseExact(folderName[(separatorIndex + 1)..], "N", out contextId);
    }

    private static bool TryReadMarker(string marker, out PrivateBrowsingOwnerMarker owner)
    {
        owner = default;
        var values = File.ReadLines(marker)
            .Select(static line => line.Split('=', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("process_id", out var processValue)
            || !int.TryParse(processValue, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || processId <= 0
            || !values.TryGetValue("process_started_utc_ticks", out var startValue)
            || !long.TryParse(startValue, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks)
            || startTicks <= 0
            || !values.TryGetValue("launch_id", out var launchValue)
            || !Guid.TryParseExact(launchValue, "N", out var launchId)
            || !values.TryGetValue("context_id", out var contextValue)
            || !Guid.TryParseExact(contextValue, "N", out _))
        {
            return false;
        }

        try
        {
            owner = new PrivateBrowsingOwnerMarker(
                processId,
                new DateTimeOffset(new DateTime(startTicks, DateTimeKind.Utc)),
                launchId);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private readonly record struct PrivateBrowsingOwnerMarker(
        int ProcessId,
        DateTimeOffset ProcessStartTimeUtc,
        Guid LaunchId);
}

internal sealed record PrivateBrowsingDeleteDiagnostic(
    string Operation,
    Guid ContextId,
    string FolderName,
    string Reason,
    string? ExceptionType);
