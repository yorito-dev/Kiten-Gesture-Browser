using System.IO;
using System.Security;

namespace TabBrowser.Services;

public enum SessionPersistenceDiagnosticOperation
{
    NormalSave,
    AutomaticSnapshotCreation,
    ProtectedRecoveryPointCreation,
    InitialProtectedRecoveryPointCreation,
    AutomaticSnapshotCleanup,
    ProtectedRecoveryPointCleanup
}

public enum SessionPersistenceDiagnosticResult
{
    Unknown,
    Succeeded,
    Failed,
    Skipped
}

public enum SessionPersistenceFailureCategory
{
    None,
    Cancelled,
    AccessDenied,
    InputOutput,
    InvalidData,
    Unknown
}

public sealed record SessionPersistenceOperationDiagnostic(
    SessionPersistenceDiagnosticOperation Operation,
    SessionPersistenceDiagnosticResult LastResult,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    SessionPersistenceFailureCategory FailureCategory);

public sealed record SessionRecoveryDiagnosticSnapshot(
    SessionPersistenceOperationDiagnostic NormalSave,
    SessionPersistenceOperationDiagnostic AutomaticSnapshot,
    SessionPersistenceOperationDiagnostic ProtectedRecoveryPoint,
    SessionPersistenceOperationDiagnostic AutomaticSnapshotCleanup,
    SessionPersistenceOperationDiagnostic ProtectedRecoveryPointCleanup)
{
    public static SessionRecoveryDiagnosticSnapshot Unknown { get; } = new(
        CreateUnknown(SessionPersistenceDiagnosticOperation.NormalSave),
        CreateUnknown(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation),
        CreateUnknown(SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation),
        CreateUnknown(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup),
        CreateUnknown(SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup));

    public SessionPersistenceDiagnosticOperation? LastFailedOperation =>
        new[]
            {
                NormalSave,
                AutomaticSnapshot,
                ProtectedRecoveryPoint,
                AutomaticSnapshotCleanup,
                ProtectedRecoveryPointCleanup
            }
            .Where(static status => status.LastResult == SessionPersistenceDiagnosticResult.Failed)
            .OrderByDescending(static status => status.LastAttemptAt)
            .Select(static status => (SessionPersistenceDiagnosticOperation?)status.Operation)
            .FirstOrDefault();

    private static SessionPersistenceOperationDiagnostic CreateUnknown(
        SessionPersistenceDiagnosticOperation operation)
        => new(
            operation,
            SessionPersistenceDiagnosticResult.Unknown,
            LastAttemptAt: null,
            LastSuccessAt: null,
            SessionPersistenceFailureCategory.None);
}

internal readonly record struct SessionPersistenceDiagnosticAttempt(
    long Sequence,
    SessionPersistenceDiagnosticOperation Operation,
    DateTimeOffset StartedAt);

internal sealed class SessionRecoveryDiagnosticState
{
    private readonly object _gate = new();
    private readonly Action<SessionRecoveryDiagnosticSnapshot>? _stateChanged;
    private long _nextSequence;
    private StoredStatus _normalSave = StoredStatus.Unknown(SessionPersistenceDiagnosticOperation.NormalSave);
    private StoredStatus _automaticSnapshot = StoredStatus.Unknown(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
    private StoredStatus _protectedRecoveryPoint = StoredStatus.Unknown(SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation);
    private StoredStatus _automaticSnapshotCleanup = StoredStatus.Unknown(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup);
    private StoredStatus _protectedRecoveryPointCleanup = StoredStatus.Unknown(SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup);

    public SessionRecoveryDiagnosticState(Action<SessionRecoveryDiagnosticSnapshot>? stateChanged = null)
    {
        _stateChanged = stateChanged;
    }

    public SessionPersistenceDiagnosticAttempt Begin(SessionPersistenceDiagnosticOperation operation)
    {
        lock (_gate)
        {
            return new SessionPersistenceDiagnosticAttempt(++_nextSequence, operation, DateTimeOffset.Now);
        }
    }

    public void CompleteSucceeded(SessionPersistenceDiagnosticAttempt attempt)
        => Complete(attempt, SessionPersistenceDiagnosticResult.Succeeded, SessionPersistenceFailureCategory.None);

    public void CompleteSkipped(SessionPersistenceDiagnosticAttempt attempt)
        => Complete(attempt, SessionPersistenceDiagnosticResult.Skipped, SessionPersistenceFailureCategory.None);

    public void CompleteFailed(SessionPersistenceDiagnosticAttempt attempt, Exception exception)
        => Complete(attempt, SessionPersistenceDiagnosticResult.Failed, ClassifyFailure(exception));

    public SessionRecoveryDiagnosticSnapshot Capture()
    {
        lock (_gate)
        {
            return CreateSnapshotLocked();
        }
    }

    private void Complete(
        SessionPersistenceDiagnosticAttempt attempt,
        SessionPersistenceDiagnosticResult result,
        SessionPersistenceFailureCategory failureCategory)
    {
        SessionRecoveryDiagnosticSnapshot changedSnapshot;
        lock (_gate)
        {
            ref var current = ref GetStatus(attempt.Operation);
            if (attempt.Sequence < current.Sequence)
            {
                return;
            }

            current = new StoredStatus(
                attempt.Sequence,
                attempt.Operation,
                result,
                attempt.StartedAt,
                result == SessionPersistenceDiagnosticResult.Succeeded
                    ? DateTimeOffset.Now
                    : current.LastSuccessAt,
                failureCategory);
            changedSnapshot = CreateSnapshotLocked();
        }

        try
        {
            _stateChanged?.Invoke(changedSnapshot);
        }
        catch
        {
            // 診断表示側の失敗で、保存処理の成否を変えない。
        }
    }

    private ref StoredStatus GetStatus(SessionPersistenceDiagnosticOperation operation)
    {
        switch (operation)
        {
            case SessionPersistenceDiagnosticOperation.NormalSave:
                return ref _normalSave;
            case SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation:
                return ref _automaticSnapshot;
            case SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation:
            case SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation:
                return ref _protectedRecoveryPoint;
            case SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup:
                return ref _automaticSnapshotCleanup;
            case SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup:
                return ref _protectedRecoveryPointCleanup;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private SessionRecoveryDiagnosticSnapshot CreateSnapshotLocked()
        => new(
            _normalSave.ToPublic(),
            _automaticSnapshot.ToPublic(),
            _protectedRecoveryPoint.ToPublic(),
            _automaticSnapshotCleanup.ToPublic(),
            _protectedRecoveryPointCleanup.ToPublic());

    private static SessionPersistenceFailureCategory ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => SessionPersistenceFailureCategory.Cancelled,
            UnauthorizedAccessException or SecurityException => SessionPersistenceFailureCategory.AccessDenied,
            InvalidDataException or System.Text.Json.JsonException => SessionPersistenceFailureCategory.InvalidData,
            IOException => SessionPersistenceFailureCategory.InputOutput,
            _ => SessionPersistenceFailureCategory.Unknown
        };
    }

    private readonly record struct StoredStatus(
        long Sequence,
        SessionPersistenceDiagnosticOperation Operation,
        SessionPersistenceDiagnosticResult LastResult,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt,
        SessionPersistenceFailureCategory FailureCategory)
    {
        public static StoredStatus Unknown(SessionPersistenceDiagnosticOperation operation)
            => new(
                Sequence: 0,
                operation,
                SessionPersistenceDiagnosticResult.Unknown,
                LastAttemptAt: null,
                LastSuccessAt: null,
                SessionPersistenceFailureCategory.None);

        public SessionPersistenceOperationDiagnostic ToPublic()
            => new(Operation, LastResult, LastAttemptAt, LastSuccessAt, FailureCategory);
    }
}
