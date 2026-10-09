using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class SessionService
{
    private const int MaxSessionSnapshotFiles = 6;
    private const int MaxProtectedSessionFiles = 12;
    private const long SessionDiagnosticLogMaxBytes = 256 * 1024;
    private const string SessionSnapshotDirectoryName = "session.snapshots";
    private const string SessionSnapshotFileSearchPattern = "session-*.json";
    private const string ProtectedSessionDirectoryName = "session.protected";
    private const string ProtectedSessionFileSearchPattern = "protected-session-*.json";
    private const string CorruptPrimaryDirectorySuffix = ".corrupt";
    private static readonly TimeSpan ProtectedSessionMinimumInterval = TimeSpan.FromMinutes(10);
    // 名前付き保存は低頻度の明示操作。同一プロセスの複数Windowからの保存・削除を直列化する。
    private static readonly SemaphoreSlim NamedRestorePointGate = new(1, 1);
    // MainWindowごとのSessionServiceも同じデータ領域を使う。読込・修復・保存を同一プロセス内で直列化する。
    private static readonly SemaphoreSlim SessionFileGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionFile;
    private readonly string _sessionBackupFile;
    private readonly string _sessionSnapshotDirectory;
    private readonly string _protectedSessionDirectory;
    private readonly string _windowMemoryFile;
    private readonly string _windowMemoryBackupFile;
    internal string NamedRestorePointDirectory => Path.Combine(Path.GetDirectoryName(_sessionFile) ?? AppPaths.Root, "session.named");
    private readonly string _diagnosticDirectory;
    private readonly SessionRecoveryDiagnosticState _recoveryDiagnosticState;
    private readonly Action<SessionPersistenceDiagnosticOperation>? _beforePersistenceOperation;
    private readonly Action? _beforePrimaryRepair;
    private readonly Action<CorruptPrimaryPreservationStage>? _beforeCorruptPrimaryPreservationStage;
    private string? _lastSavedSessionJson;
    private bool _preserveSessionBackupUntilPrimarySaved;
    private bool _preserveWindowMemoryBackupUntilPrimarySaved;
    private bool _sessionPrimaryVersionCheckedForSave;
    private bool _windowMemoryPrimaryVersionCheckedForSave;
    private bool _sessionSaveBlockedByUnsupportedVersion;
    private bool _windowMemorySaveBlockedByUnsupportedVersion;
    private byte[]? _sessionCorruptPrimaryBytes;
    private byte[]? _windowMemoryCorruptPrimaryBytes;
    private bool _sessionSaveBlockedByUnrecoverablePrimary;

    public SessionLoadInfo LastLoadInfo { get; private set; } = SessionLoadInfo.Empty;

    public SessionService()
        : this(AppPaths.SessionFile, AppPaths.WindowMemoryFile, diagnosticDirectory: null, new SessionRecoveryDiagnosticState())
    {
    }

    internal SessionService(SessionRecoveryDiagnosticState recoveryDiagnosticState)
        : this(AppPaths.SessionFile, AppPaths.WindowMemoryFile, diagnosticDirectory: null, recoveryDiagnosticState)
    {
    }

    public SessionService(string sessionFile, string windowMemoryFile, string? diagnosticDirectory = null)
        : this(sessionFile, windowMemoryFile, diagnosticDirectory, new SessionRecoveryDiagnosticState())
    {
    }

    internal SessionService(
        string sessionFile,
        string windowMemoryFile,
        string? diagnosticDirectory,
        SessionRecoveryDiagnosticState recoveryDiagnosticState,
        Action<SessionPersistenceDiagnosticOperation>? beforePersistenceOperation = null,
        Action? beforePrimaryRepair = null,
        Action<CorruptPrimaryPreservationStage>? beforeCorruptPrimaryPreservationStage = null)
    {
        _sessionFile = sessionFile;
        _sessionBackupFile = AtomicFileService.CreateBackupFilePath(sessionFile);
        _sessionSnapshotDirectory = CreateSessionSnapshotDirectory(sessionFile);
        _protectedSessionDirectory = CreateProtectedSessionDirectory(sessionFile);
        _windowMemoryFile = windowMemoryFile;
        _windowMemoryBackupFile = AtomicFileService.CreateBackupFilePath(windowMemoryFile);
        _diagnosticDirectory = diagnosticDirectory ?? Path.Combine(AppPaths.Root, "session");
        _recoveryDiagnosticState = recoveryDiagnosticState
            ?? throw new ArgumentNullException(nameof(recoveryDiagnosticState));
        _beforePersistenceOperation = beforePersistenceOperation;
        _beforePrimaryRepair = beforePrimaryRepair;
        _beforeCorruptPrimaryPreservationStage = beforeCorruptPrimaryPreservationStage;
    }

    public SessionRecoveryDiagnosticSnapshot CaptureRecoveryDiagnosticSnapshot()
        => _recoveryDiagnosticState.Capture();

    public Task<NamedSessionRestorePointInfo> SaveNamedRestorePointAsync(NamedSessionRestorePointRequest request,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await NamedRestorePointGate.WaitAsync(cancellationToken);
        try { return await SaveNamedRestorePointCoreAsync(request, cancellationToken); }
        finally { NamedRestorePointGate.Release(); }
    }, cancellationToken);

    private async Task<NamedSessionRestorePointInfo> SaveNamedRestorePointCoreAsync(NamedSessionRestorePointRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (request.Id == Guid.Empty || name.Length is 0 or > 120 || request.CreatedAt == default)
            throw new ArgumentException("復元点の名前を1～120文字で入力してください。");
        var session = CloneSession(request.Session);
        session.NamedRestorePoint = new NamedSessionRestorePointMetadata
        {
            Id = request.Id,
            Name = name,
            CreatedAt = request.CreatedAt
        };
        session.ClosedTabs = []; session.ClosedWindows = []; session.ClosedYouTubeMiniPlayers = [];
        session.WindowMemoryYouTubeMiniPlayers = null;
        foreach (var window in session.Windows) window.ClosedTabs = [];
        NormalizeNamedSession(session, request.Id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = NamedRestorePointPath(request.Id);
        if (File.Exists(path))
        {
            var existing = await ReadNamedRestorePointAsync(request.Id, cancellationToken);
            if (existing.Info.CanRestore && existing.Info.ContentHash == hash) return existing.Info;
            throw new IOException("同じ保存要求の復元点が既に存在します。上書きせず保持しました。一覧を更新してください。");
        }
        await AtomicFileService.WriteAsync(path, null, async stream =>
        {
            await stream.WriteAsync(bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }, overwrite: false);
        // 確定名を読み直して検証する。失敗しても他の正常な復元点を削除しない。
        var saved = await ReadNamedRestorePointAsync(request.Id, CancellationToken.None);
        if (!saved.Info.CanRestore || saved.Info.ContentHash != hash)
            throw new IOException("復元点の保存後検証に失敗しました。既存の復元点は保持されています。");
        return saved.Info;
    }

    public Task<IReadOnlyList<NamedSessionRestorePointInfo>> GetNamedRestorePointsAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<NamedSessionRestorePointInfo>>(async () =>
        {
            if (!Directory.Exists(NamedRestorePointDirectory)) return [];
            var result = new List<NamedSessionRestorePointInfo>();
            foreach (var path in Directory.EnumerateFiles(NamedRestorePointDirectory, "named-*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = Path.GetFileNameWithoutExtension(path)["named-".Length..];
                if (Guid.TryParseExact(token, "N", out var id))
                    result.Add((await ReadNamedRestorePointAsync(id, cancellationToken)).Info);
            }
            return result.OrderByDescending(x => x.CreatedAt).ToList();
        }, cancellationToken);

    public Task<SessionData> LoadNamedRestorePointAsync(Guid id, CancellationToken cancellationToken = default,
        string? expectedContentHash = null)
        => Task.Run(async () =>
        {
            var loaded = await ReadNamedRestorePointAsync(id, cancellationToken);
            if (expectedContentHash is not null && expectedContentHash != loaded.Info.ContentHash)
                throw new IOException("選択後に復元点が変わりました。現在のタブを変更せず保留しました。一覧を更新してください。");
            return loaded.Session ?? throw new InvalidDataException(loaded.Info.Error ?? "復元点を読み込めません。");
        }, cancellationToken);

    public Task DeleteNamedRestorePointAsync(Guid id, string expectedContentHash, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await NamedRestorePointGate.WaitAsync(cancellationToken);
            try
            {
                var path = NamedRestorePointPath(id);
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (expectedContentHash.Length == 0 || Convert.ToHexString(SHA256.HashData(bytes)) != expectedContentHash)
                    throw new IOException("確認後に復元点が変わりました。削除せず保持しました。一覧を更新してください。");
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
            }
            finally { NamedRestorePointGate.Release(); }
        }, cancellationToken);

    private string NamedRestorePointPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("復元点IDが不正です。");
        return Path.Combine(NamedRestorePointDirectory, $"named-{id:N}.json");
    }

    private async Task<(NamedSessionRestorePointInfo Info, SessionData? Session)> ReadNamedRestorePointAsync(Guid id,
        CancellationToken cancellationToken)
    {
        var hash = string.Empty;
        try
        {
            var bytes = await File.ReadAllBytesAsync(NamedRestorePointPath(id), cancellationToken);
            hash = Convert.ToHexString(SHA256.HashData(bytes));
            var session = ParseSessionData(bytes);
            NormalizeNamedSession(session, id);
            var metadata = session.NamedRestorePoint!;
            var windows = SessionSnapshotService.CreateRememberedWindowStates(session);
            return (new NamedSessionRestorePointInfo(id, metadata.Name, metadata.CreatedAt,
                windows.Sum(x => x.Tabs.Count), windows.Count, hash), session);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            var message = ex is InvalidDataException ? ex.Message : "破損またはアクセス不能の復元点です。現在のタブは変更しません。";
            return (new NamedSessionRestorePointInfo(id, $"読み込めない復元点 ({id.ToString("N")[..8]})", null, 0, 0, hash, message), null);
        }
    }

    internal static SessionData CloneSession(SessionData session)
        => JsonSerializer.Deserialize<SessionData>(JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions), JsonOptions)!;

    private static void NormalizeNamedSession(SessionData session, Guid id)
    {
        if (session.Version is not (1 or 2) || session.NamedRestorePoint is not { Version: 1, Scope: "normal-window-tabs" } metadata)
            throw new InvalidDataException("未対応のタブセッション復元点の形式です。変更せず保持しています。");
        if (metadata.Id != id || string.IsNullOrWhiteSpace(metadata.Name) || metadata.Name.Length > 120 || metadata.CreatedAt == default
            || session.Windows is null || session.Tabs is null || session.Windows.Any(x => x is null || x.Tabs is null))
            throw new InvalidDataException("復元点の情報が欠けています。現在のタブは変更しません。");
        var tabs = session.Tabs.Concat(session.Windows.SelectMany(x => x.Tabs)).ToList();
        if (tabs.Count == 0 || tabs.Any(x => x is null || string.IsNullOrWhiteSpace(x.Url)))
            throw new InvalidDataException("復元するタブがないか、タブ情報が壊れています。");
        // 通常セッションの既定補完で、明示された不明コンテナを既定へ置き換えない。
        var containers = tabs.Select(x => (Tab: x, Id: x.ContainerProfileId)).ToList();
        session.ClosedTabs = []; session.ClosedWindows = []; session.ClosedYouTubeMiniPlayers = [];
        session.WindowMemoryYouTubeMiniPlayers = null;
        foreach (var window in session.Windows) window.ClosedTabs = [];
        NormalizeLoadedSession(session);
        foreach (var pair in containers) pair.Tab.ContainerProfileId = pair.Id;
    }

    public async Task<SessionData> LoadAsync(bool recoverEmptySessionFromProtected = false)
    {
        await SessionFileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await LoadFromFileAsync(_sessionFile, _sessionBackupFile, _sessionSnapshotDirectory);
            var session = result.Session;
            if (recoverEmptySessionFromProtected && !HasRestorableSessionContent(session))
            {
                var protectedAttempt = await TryLoadNewestProtectedSessionAsync();
                if (protectedAttempt.Session is { } protectedSession)
                {
                    AppendSessionDiagnostic("load-recovered", $"source=protected;target={_sessionFile}");
                    var repair = await TryRepairFromBackupAsync(_sessionFile, protectedSession, "protected",
                        result.PrimaryBytes, result.Info.PrimaryLoadFailed);
                    session = protectedSession;
                    LastLoadInfo = new SessionLoadInfo(
                        "protected",
                        true,
                        result.Info.PrimaryLoadFailed,
                        result.Info.BackupLoadFailed,
                        !repair.Succeeded)
                    { CorruptPrimaryPreservation = repair.Preservation };
                    SetLoadedSessionPersistenceState(session, LastLoadInfo,
                        result.Info.PrimaryLoadFailed ? result.PrimaryBytes : null);
                    return session;
                }
            }

            LastLoadInfo = result.Info;
            SetLoadedSessionPersistenceState(session, LastLoadInfo,
                result.Info.PrimaryLoadFailed ? result.PrimaryBytes : null);
            await TryCreateInitialProtectedRecoveryPointAsync(session).ConfigureAwait(false);
            return session;
        }
        finally { SessionFileGate.Release(); }
    }

    public bool HasProtectedRecoveryPoint()
    {
        try
        {
            return Directory.Exists(_protectedSessionDirectory)
                && Directory.EnumerateFiles(
                    _protectedSessionDirectory,
                    ProtectedSessionFileSearchPattern).Any();
        }
        catch
        {
            return false;
        }
    }

    public async Task<SessionData?> LoadProtectedRecoveryPointAsync()
    {
        return (await TryLoadNewestProtectedSessionAsync()).Session;
    }

    public async Task<SessionData> LoadWindowMemoryAsync()
    {
        await SessionFileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await LoadFromFileAsync(_windowMemoryFile, _windowMemoryBackupFile);
            LastLoadInfo = result.Info;
            _preserveWindowMemoryBackupUntilPrimarySaved = result.Info.RepairFailed;
            _windowMemoryPrimaryVersionCheckedForSave = result.Info.Source == "primary"
                || (result.Info.Recovered && !result.Info.RepairFailed);
            _windowMemoryCorruptPrimaryBytes = result.Info.RepairFailed && result.Info.PrimaryLoadFailed
                ? result.PrimaryBytes : null;
            return result.Session;
        }
        finally { SessionFileGate.Release(); }
    }

    public bool HasWindowMemory()
    {
        AtomicFileService.EnsureDirectory(_windowMemoryFile);
        return File.Exists(_windowMemoryFile);
    }

    private async Task<SessionLoadResult> LoadFromFileAsync(
        string filePath,
        string? backupFilePath = null,
        string? snapshotDirectory = null)
    {
        AtomicFileService.EnsureDirectory(filePath);

        var primaryAttempt = await TryLoadFromFileAsync(filePath, "primary");
        if (primaryAttempt.Session is not null)
        {
            return new SessionLoadResult(primaryAttempt.Session, SessionLoadInfo.Primary, primaryAttempt.ObservedBytes);
        }

        var backupFailed = false;
        if (!string.IsNullOrWhiteSpace(backupFilePath))
        {
            var backupAttempt = await TryLoadFromFileAsync(backupFilePath, "backup");
            backupFailed = backupAttempt.Failed;
            if (backupAttempt.Session is not null)
            {
                AppendSessionDiagnostic("load-recovered", $"source=backup;target={filePath}");
                var repair = await TryRepairFromBackupAsync(filePath, backupAttempt.Session, "backup", primaryAttempt.ObservedBytes);
                return new SessionLoadResult(
                    backupAttempt.Session,
                    new SessionLoadInfo("backup", true, primaryAttempt.Failed, backupFailed, !repair.Succeeded)
                    { CorruptPrimaryPreservation = repair.Preservation }, primaryAttempt.ObservedBytes);
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            var snapshotAttempt = await TryLoadNewestSessionSnapshotAsync(snapshotDirectory);
            if (snapshotAttempt.Session is not null)
            {
                AppendSessionDiagnostic("load-recovered", $"source=snapshot;target={filePath}");
                var repair = await TryRepairFromBackupAsync(filePath, snapshotAttempt.Session, "snapshot", primaryAttempt.ObservedBytes);
                return new SessionLoadResult(
                    snapshotAttempt.Session,
                    new SessionLoadInfo("snapshot", true, primaryAttempt.Failed, backupFailed, !repair.Succeeded)
                    { CorruptPrimaryPreservation = repair.Preservation }, primaryAttempt.ObservedBytes);
            }
        }

        AppendSessionDiagnostic("load-empty", $"target={filePath}");
        return new SessionLoadResult(
            new SessionData(),
            new SessionLoadInfo("empty", false, primaryAttempt.Failed, backupFailed, false), primaryAttempt.ObservedBytes);
    }

    private async Task<SessionLoadAttempt> TryLoadFromFileAsync(string filePath, string source)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            var session = ParseSessionData(bytes);
            return new SessionLoadAttempt(
                NormalizeLoadedSession(session),
                false, ObservedBytes: source == "primary" ? bytes : null);
        }
        catch (FileNotFoundException) { return new SessionLoadAttempt(null, false); }
        catch (DirectoryNotFoundException) { return new SessionLoadAttempt(null, false); }
        catch (UnsupportedSessionVersionException ex) when (source == "primary")
        {
            if (string.Equals(filePath, _sessionFile, StringComparison.OrdinalIgnoreCase))
                _sessionSaveBlockedByUnsupportedVersion = true;
            else if (string.Equals(filePath, _windowMemoryFile, StringComparison.OrdinalIgnoreCase))
                _windowMemorySaveBlockedByUnsupportedVersion = true;
            LastLoadInfo = new SessionLoadInfo("unsupported", false, true, false, false);
            AppendSessionDiagnostic("load-unsupported", $"source={source};file={filePath}", ex);
            throw;
        }
        catch (UnsupportedSessionVersionException ex)
        {
            AppendSessionDiagnostic("load-unsupported", $"source={source};file={filePath}", ex);
            return new SessionLoadAttempt(null, true, ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            AppendSessionDiagnostic("load-failed", $"source={source};file={filePath}", ex);
            // 失敗時の原本は後の置換との一致照合に使う。読めない場合は破損扱いにしない。
            return new SessionLoadAttempt(null, true, ex, source == "primary" ? bytes : null);
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("load-io-failed", $"source={source};file={filePath}", ex);
            throw;
        }
    }

    public async Task SaveAsync(SessionData session)
    {
        ValidateSupportedSessionVersion(session);
        EnsureUniqueSessionTabIds(session);
        var serializedSession = SerializeSession(session);
        await SessionFileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureSessionPrimaryMayBeSaved();
            if (CanSkipSessionSave(serializedSession))
            {
                var skippedAttempt = _recoveryDiagnosticState.Begin(
                    SessionPersistenceDiagnosticOperation.NormalSave);
                _recoveryDiagnosticState.CompleteSkipped(skippedAttempt);
                return;
            }

            await TryProtectCurrentSessionBeforeOverwriteAsync(session).ConfigureAwait(false);
            var saveAttempt = _recoveryDiagnosticState.Begin(SessionPersistenceDiagnosticOperation.NormalSave);
            try
            {
                _beforePersistenceOperation?.Invoke(SessionPersistenceDiagnosticOperation.NormalSave);
                if (_sessionCorruptPrimaryBytes is not null)
                    EnsurePrimaryUnchanged(_sessionFile, _sessionCorruptPrimaryBytes);
                await SaveSerializedToFileAsync(_sessionFile, serializedSession,
                    _preserveSessionBackupUntilPrimarySaved ? null : _sessionBackupFile).ConfigureAwait(false);
                _preserveSessionBackupUntilPrimarySaved = false;
                _sessionCorruptPrimaryBytes = null;
                _lastSavedSessionJson = serializedSession;
                _recoveryDiagnosticState.CompleteSucceeded(saveAttempt);
            }
            catch (Exception ex)
            {
                _recoveryDiagnosticState.CompleteFailed(saveAttempt, ex);
                throw;
            }

            await TrySaveSessionSnapshotAsync(serializedSession).ConfigureAwait(false);
        }
        finally
        {
            SessionFileGate.Release();
        }
    }

    public async Task SaveWindowMemoryAsync(SessionData session)
    {
        ValidateSupportedSessionVersion(session);
        EnsureUniqueSessionTabIds(session);
        await SessionFileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var knownCorruptPrimary = _windowMemoryCorruptPrimaryBytes;
            EnsureSupportedPrimaryVersionForSave(_windowMemoryFile, ref _windowMemoryPrimaryVersionCheckedForSave,
                ref _windowMemorySaveBlockedByUnsupportedVersion, ref _preserveWindowMemoryBackupUntilPrimarySaved,
                ref _windowMemoryCorruptPrimaryBytes);
            if (knownCorruptPrimary is null && _windowMemoryCorruptPrimaryBytes is not null)
            {
                _windowMemoryCorruptPrimaryBytes = null;
                _windowMemoryPrimaryVersionCheckedForSave = false;
                throw new IOException("読み込み後に破損したウィンドウ構成を検出しました。上書きせず再読込してください。");
            }
            if (_windowMemoryCorruptPrimaryBytes is not null)
            {
                PreserveCorruptPrimary(_windowMemoryFile, _windowMemoryCorruptPrimaryBytes);
                EnsurePrimaryUnchanged(_windowMemoryFile, _windowMemoryCorruptPrimaryBytes);
            }
            await SaveToFileAsync(_windowMemoryFile, session,
                _preserveWindowMemoryBackupUntilPrimarySaved ? null : _windowMemoryBackupFile);
            _preserveWindowMemoryBackupUntilPrimarySaved = false;
            _windowMemoryCorruptPrimaryBytes = null;
        }
        finally { SessionFileGate.Release(); }
    }

    private static async Task SaveToFileAsync(string filePath, SessionData session, string? backupFilePath = null)
    {
        await AtomicFileService.WriteAsync(
            filePath,
            backupFilePath,
            async stream => await JsonSerializer.SerializeAsync(stream, session, JsonOptions));
    }

    private async Task<PrimaryRepairResult> TryRepairFromBackupAsync(
        string filePath, SessionData session, string source, byte[]? expectedPrimaryBytes,
        bool preserveCorruptPrimary = true)
    {
        var preservation = CorruptPrimaryPreservationStatus.NotNeeded;
        try
        {
            EnsurePrimaryUnchanged(filePath, expectedPrimaryBytes);
            if (preserveCorruptPrimary && expectedPrimaryBytes is not null)
            {
                try
                {
                    await PreserveCorruptPrimaryAsync(filePath, expectedPrimaryBytes).ConfigureAwait(false);
                    preservation = CorruptPrimaryPreservationStatus.Preserved;
                    AppendSessionDiagnostic("corrupt-primary-preserved", $"source={source};kind={Path.GetFileNameWithoutExtension(filePath)}");
                }
                catch (Exception ex)
                {
                    preservation = CorruptPrimaryPreservationStatus.Failed;
                    AppendSessionDiagnostic("corrupt-primary-preservation-failed", $"source={source};kind={Path.GetFileNameWithoutExtension(filePath)}", ex);
                    throw;
                }
            }
            _beforePrimaryRepair?.Invoke();
            EnsurePrimaryUnchanged(filePath, expectedPrimaryBytes);
            await SaveToFileAsync(filePath, session);
            return new PrimaryRepairResult(true, preservation);
        }
        catch (Exception ex)
        {
            AppendSessionDiagnostic("repair-failed", $"source={source};kind={Path.GetFileNameWithoutExtension(filePath)}", ex);
            return new PrimaryRepairResult(false, preservation);
        }
    }

    private static void EnsurePrimaryUnchanged(string filePath, byte[]? expectedBytes)
    {
        byte[]? current;
        try { current = File.ReadAllBytes(filePath); }
        catch (FileNotFoundException) { current = null; }
        catch (DirectoryNotFoundException) { current = null; }
        if (expectedBytes is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(expectedBytes))
            throw new IOException("確認後にセッション本体が変化しました。古い復旧結果では上書きしません。再読込してください。");
    }

    private void PreserveCorruptPrimary(string filePath, byte[] bytes)
        => PreserveCorruptPrimaryAsync(filePath, bytes).GetAwaiter().GetResult();

    private async Task PreserveCorruptPrimaryAsync(string filePath, byte[] bytes)
    {
        EnsurePrimaryUnchanged(filePath, bytes);
        var directory = Path.Combine(Path.GetDirectoryName(filePath) ?? AppPaths.Root,
            Path.GetFileNameWithoutExtension(filePath) + CorruptPrimaryDirectorySuffix);
        _beforeCorruptPrimaryPreservationStage?.Invoke(CorruptPrimaryPreservationStage.CreateDirectory);
        Directory.CreateDirectory(directory);
        var preservedPath = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + ".bin");
        if (File.Exists(preservedPath))
        {
            if (!(await File.ReadAllBytesAsync(preservedPath).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                throw new IOException("既存の破損原本保全ファイルが一致しません。上書きしません。");
            return;
        }

        var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            _beforeCorruptPrimaryPreservationStage?.Invoke(CorruptPrimaryPreservationStage.Copy);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                _beforeCorruptPrimaryPreservationStage?.Invoke(CorruptPrimaryPreservationStage.Flush);
                stream.Flush(flushToDisk: true);
            }
            _beforeCorruptPrimaryPreservationStage?.Invoke(CorruptPrimaryPreservationStage.Verify);
            if (!(await File.ReadAllBytesAsync(temporaryPath).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                throw new IOException("破損原本の保全後照合に失敗しました。");
            try { File.Move(temporaryPath, preservedPath); }
            catch (IOException) when (File.Exists(preservedPath))
            {
                if (!(await File.ReadAllBytesAsync(preservedPath).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                    throw;
            }
            if (!(await File.ReadAllBytesAsync(preservedPath).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                throw new IOException("確定した破損原本の照合に失敗しました。");
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task TrySaveSessionSnapshotAsync(string serializedSession)
    {
        var snapshotAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
        try
        {
            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
            await SaveSessionSnapshotAsync(serializedSession);
            _recoveryDiagnosticState.CompleteSucceeded(snapshotAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(snapshotAttempt, ex);
            // スナップショットは補助復旧用なので、通常セッション保存は失敗扱いにしない。
            return;
        }

        TryTrimSessionSnapshots();
    }

    private void TrySaveSessionSnapshot(string serializedSession)
    {
        var snapshotAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
        try
        {
            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
            SaveSessionSnapshot(serializedSession);
            _recoveryDiagnosticState.CompleteSucceeded(snapshotAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(snapshotAttempt, ex);
            // スナップショットは補助復旧用なので、通常セッション保存は失敗扱いにしない。
            return;
        }

        TryTrimSessionSnapshots();
    }

    private async Task SaveSessionSnapshotAsync(string serializedSession)
    {
        Directory.CreateDirectory(_sessionSnapshotDirectory);
        var snapshotFile = CreateSessionSnapshotFilePath(_sessionSnapshotDirectory);
        await SaveSerializedToFileAsync(snapshotFile, serializedSession);
    }

    private void SaveSessionSnapshot(string serializedSession)
    {
        Directory.CreateDirectory(_sessionSnapshotDirectory);
        var snapshotFile = CreateSessionSnapshotFilePath(_sessionSnapshotDirectory);
        SaveSerializedToFile(snapshotFile, serializedSession);
    }

    private void TryTrimSessionSnapshots()
    {
        var cleanupAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup);
        try
        {
            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.AutomaticSnapshotCleanup);
            TrimSessionSnapshots();
            _recoveryDiagnosticState.CompleteSucceeded(cleanupAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(cleanupAttempt, ex);
            // スナップショット整理に失敗しても通常保存は成功扱いにする。
        }
    }

    private void TrimSessionSnapshots()
    {
        if (!Directory.Exists(_sessionSnapshotDirectory))
        {
            return;
        }

        foreach (var file in Directory
            .EnumerateFiles(_sessionSnapshotDirectory, SessionSnapshotFileSearchPattern)
            .Select(static filePath => new FileInfo(filePath))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(MaxSessionSnapshotFiles))
        {
            file.Delete();
        }
    }

    private async Task<SessionLoadAttempt> TryLoadNewestSessionSnapshotAsync(string snapshotDirectory)
    {
        if (!Directory.Exists(snapshotDirectory))
        {
            return new SessionLoadAttempt(null, false);
        }

        var failed = false;
        foreach (var file in Directory
            .EnumerateFiles(snapshotDirectory, SessionSnapshotFileSearchPattern)
            .Select(static filePath => new FileInfo(filePath))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var attempt = await TryLoadFromFileAsync(file.FullName, "snapshot");
            failed |= attempt.Failed;
            if (attempt.Session is not null)
            {
                return attempt;
            }
        }

        return new SessionLoadAttempt(null, failed);
    }

    private static string CreateSessionSnapshotDirectory(string sessionFile)
    {
        return Path.Combine(
            Path.GetDirectoryName(sessionFile) ?? AppPaths.Root,
            SessionSnapshotDirectoryName);
    }

    private static string CreateSessionSnapshotFilePath(string snapshotDirectory)
    {
        return Path.Combine(
            snapshotDirectory,
            $"session-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
    }

    private async Task TryProtectCurrentSessionBeforeOverwriteAsync(SessionData incomingSession)
    {
        var protectedAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation);
        try
        {
            var latestProtectedFile = GetLatestProtectedSessionFile();
            if (HasRestorableSessionContent(incomingSession)
                && latestProtectedFile is not null
                && DateTime.UtcNow - latestProtectedFile.LastWriteTimeUtc < ProtectedSessionMinimumInterval)
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            var currentAttempt = await TryLoadFromFileAsync(_sessionFile, "protected-source").ConfigureAwait(false);
            if (currentAttempt.Failed)
            {
                _recoveryDiagnosticState.CompleteFailed(
                    protectedAttempt,
                    currentAttempt.Failure ?? new IOException("保護復元点の元セッションを読み込めませんでした。"));
                return;
            }

            if (currentAttempt.Session is not { } currentSession
                || !HasRestorableSessionContent(currentSession))
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            var serializedCurrentSession = SerializeSession(currentSession);
            if (latestProtectedFile is not null
                && string.Equals(
                    await File.ReadAllTextAsync(latestProtectedFile.FullName).ConfigureAwait(false),
                    serializedCurrentSession,
                    StringComparison.Ordinal))
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation);
            Directory.CreateDirectory(_protectedSessionDirectory);
            await SaveSerializedToFileAsync(
                CreateProtectedSessionFilePath(_protectedSessionDirectory),
                serializedCurrentSession).ConfigureAwait(false);
            _recoveryDiagnosticState.CompleteSucceeded(protectedAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(protectedAttempt, ex);
            // 保護復元点は補助復旧用なので、通常セッション保存は継続する。
            return;
        }

        TryTrimProtectedSessionFiles();
    }

    private async Task TryCreateInitialProtectedRecoveryPointAsync(SessionData session)
    {
        var protectedAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation);
        if (!HasRestorableSessionContent(session) || HasProtectedRecoveryPoint())
        {
            _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
            return;
        }

        try
        {
            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation);
            Directory.CreateDirectory(_protectedSessionDirectory);
            await SaveSerializedToFileAsync(
                CreateProtectedSessionFilePath(_protectedSessionDirectory),
                SerializeSession(session)).ConfigureAwait(false);
            _recoveryDiagnosticState.CompleteSucceeded(protectedAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(protectedAttempt, ex);
            // 保護復元点は補助復旧用なので、通常セッション読込は継続する。
            return;
        }

        TryTrimProtectedSessionFiles();
    }

    private void TryProtectCurrentSessionBeforeOverwrite(SessionData incomingSession)
    {
        var protectedAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation);
        try
        {
            var latestProtectedFile = GetLatestProtectedSessionFile();
            if (HasRestorableSessionContent(incomingSession)
                && latestProtectedFile is not null
                && DateTime.UtcNow - latestProtectedFile.LastWriteTimeUtc < ProtectedSessionMinimumInterval)
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            if (!File.Exists(_sessionFile))
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            var currentSession = NormalizeLoadedSession(ParseSessionData(File.ReadAllBytes(_sessionFile)));
            if (!HasRestorableSessionContent(currentSession))
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            var serializedCurrentSession = SerializeSession(currentSession);
            if (latestProtectedFile is not null
                && string.Equals(
                    File.ReadAllText(latestProtectedFile.FullName),
                    serializedCurrentSession,
                    StringComparison.Ordinal))
            {
                _recoveryDiagnosticState.CompleteSkipped(protectedAttempt);
                return;
            }

            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation);
            Directory.CreateDirectory(_protectedSessionDirectory);
            SaveSerializedToFile(
                CreateProtectedSessionFilePath(_protectedSessionDirectory),
                serializedCurrentSession);
            _recoveryDiagnosticState.CompleteSucceeded(protectedAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(protectedAttempt, ex);
            // 保護復元点は補助復旧用なので、通常セッション保存は継続する。
            return;
        }

        TryTrimProtectedSessionFiles();
    }

    private async Task<SessionLoadAttempt> TryLoadNewestProtectedSessionAsync()
    {
        if (!Directory.Exists(_protectedSessionDirectory))
        {
            return new SessionLoadAttempt(null, false);
        }

        var failed = false;
        foreach (var file in Directory
            .EnumerateFiles(_protectedSessionDirectory, ProtectedSessionFileSearchPattern)
            .Select(static filePath => new FileInfo(filePath))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var attempt = await TryLoadFromFileAsync(file.FullName, "protected").ConfigureAwait(false);
            failed |= attempt.Failed;
            if (attempt.Session is { } session && HasRestorableSessionContent(session))
            {
                return attempt;
            }
        }

        return new SessionLoadAttempt(null, failed);
    }

    private FileInfo? GetLatestProtectedSessionFile()
    {
        if (!Directory.Exists(_protectedSessionDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(_protectedSessionDirectory, ProtectedSessionFileSearchPattern)
            .Select(static filePath => new FileInfo(filePath))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void TryTrimProtectedSessionFiles()
    {
        var cleanupAttempt = _recoveryDiagnosticState.Begin(
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup);
        try
        {
            _beforePersistenceOperation?.Invoke(
                SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup);
            TrimProtectedSessionFiles();
            _recoveryDiagnosticState.CompleteSucceeded(cleanupAttempt);
        }
        catch (Exception ex)
        {
            _recoveryDiagnosticState.CompleteFailed(cleanupAttempt, ex);
            // 世代整理に失敗しても、作成済みの保護復元点と通常保存は成功扱いにする。
        }
    }

    private void TrimProtectedSessionFiles()
    {
        foreach (var file in Directory
            .EnumerateFiles(_protectedSessionDirectory, ProtectedSessionFileSearchPattern)
            .Select(static filePath => new FileInfo(filePath))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(MaxProtectedSessionFiles))
        {
            file.Delete();
        }
    }

    private static string CreateProtectedSessionDirectory(string sessionFile)
    {
        return Path.Combine(
            Path.GetDirectoryName(sessionFile) ?? AppPaths.Root,
            ProtectedSessionDirectoryName);
    }

    private static string CreateProtectedSessionFilePath(string protectedSessionDirectory)
    {
        return Path.Combine(
            protectedSessionDirectory,
            $"protected-session-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
    }

    private static bool HasRestorableSessionContent(SessionData session)
    {
        return session.Tabs.Any(static tab => !string.IsNullOrWhiteSpace(tab.Url))
            || session.Windows.Any(static window => window.Tabs.Any(tab => !string.IsNullOrWhiteSpace(tab.Url)))
            || session.ClosedTabs.Count > 0
            || session.ClosedWindows.Count > 0
            || session.ClosedYouTubeMiniPlayers.Count > 0;
    }

    private static SessionData NormalizeLoadedSession(SessionData session)
    {
        session.Tabs = NormalizeLoadedTabs(session.Tabs);
        session.ClosedTabs = NormalizeLoadedClosedTabs(session.ClosedTabs);
        session.ClosedWindows = NormalizeLoadedClosedWindows(session.ClosedWindows);
        session.ClosedYouTubeMiniPlayers = YouTubeMiniPlayerStore.NormalizeClosedHistory(
            session.ClosedYouTubeMiniPlayers);
        session.Windows ??= [];

        foreach (var window in session.Windows)
        {
            window.Tabs = NormalizeLoadedTabs(window.Tabs);
            window.ClosedTabs = NormalizeLoadedClosedTabs(window.ClosedTabs);
            if (window.LastActiveTabId is { } lastActiveTabId
                && !window.Tabs.Any(tab => tab.Id == lastActiveTabId))
            {
                window.LastActiveTabId = null;
            }
        }

        EnsureUniqueSessionTabIds(session);

        if (session.LastActiveTabId is { } sessionLastActiveTabId
            && session.Tabs.Count > 0
            && !session.Tabs.Any(tab => tab.Id == sessionLastActiveTabId))
        {
            session.LastActiveTabId = null;
        }

        var closedWindowIds = session.ClosedWindows.Select(static window => window.Id).ToHashSet();
        foreach (var closedTab in session.ClosedTabs)
        {
            if (closedTab.ClosedWindowId is not { } closedWindowId
                || !closedWindowIds.Contains(closedWindowId)
                || closedTab.SourceTabId is null)
            {
                closedTab.ClosedWindowId = null;
                closedTab.ClosedWindowTabIndex = null;
            }
        }

        return session;
    }

    private static void EnsureUniqueSessionTabIds(SessionData session)
    {
        session.LastActiveTabId = EnsureUniqueTabIds(
            session.Tabs,
            session.LastActiveTabId,
            new HashSet<Guid>(),
            out _);

        var usedIds = new HashSet<Guid>();
        var liveWindowRemaps = new Dictionary<Guid, IReadOnlyDictionary<Guid, Guid>>();
        foreach (var window in session.Windows)
        {
            window.LastActiveTabId = EnsureUniqueTabIds(
                window.Tabs,
                window.LastActiveTabId,
                usedIds,
                out var remappedIds);
            if (remappedIds.Count > 0)
            {
                liveWindowRemaps.TryAdd(window.WindowId, remappedIds);
            }
        }

        foreach (var closedWindow in session.ClosedWindows)
        {
            closedWindow.WindowState.LastActiveTabId = EnsureUniqueTabIds(
                closedWindow.WindowState.Tabs,
                closedWindow.WindowState.LastActiveTabId,
                usedIds,
                out _);
        }

        foreach (var closedTab in session.ClosedTabs)
        {
            if (closedTab.ClosedWindowId is not { } closedWindowId
                || closedTab.ClosedWindowTabIndex is not { } tabIndex)
            {
                continue;
            }

            var closedWindow = session.ClosedWindows.FirstOrDefault(window => window.Id == closedWindowId);
            if (closedWindow is not null
                && tabIndex >= 0
                && tabIndex < closedWindow.WindowState.Tabs.Count)
            {
                closedTab.SourceTabId = closedWindow.WindowState.Tabs[tabIndex].Id;
            }
        }

        foreach (var miniPlayer in session.WindowMemoryYouTubeMiniPlayers ?? [])
        {
            if (miniPlayer.OwnerSessionWindowId is { } ownerWindowId
                && liveWindowRemaps.TryGetValue(ownerWindowId, out var remappedIds)
                && remappedIds.TryGetValue(miniPlayer.SourceTabId, out var remappedTabId))
            {
                miniPlayer.SourceTabId = remappedTabId;
            }
        }
    }

    private static Guid? EnsureUniqueTabIds(
        IReadOnlyList<SessionTabState> tabs,
        Guid? lastActiveTabId,
        HashSet<Guid> usedIds,
        out IReadOnlyDictionary<Guid, Guid> remappedIds)
    {
        var remaps = new Dictionary<Guid, Guid>();
        var originalIdsInWindow = new HashSet<Guid>();
        Guid? normalizedLastActiveTabId = null;

        foreach (var tab in tabs)
        {
            var originalId = tab.Id;
            var isFirstOccurrenceInWindow = originalId != Guid.Empty && originalIdsInWindow.Add(originalId);
            var collidesWithPreviousWindow = isFirstOccurrenceInWindow && usedIds.Contains(originalId);
            if (originalId == Guid.Empty || !usedIds.Add(originalId))
            {
                do
                {
                    tab.Id = Guid.NewGuid();
                }
                while (!usedIds.Add(tab.Id));

                if (collidesWithPreviousWindow)
                {
                    remaps.TryAdd(originalId, tab.Id);
                }
            }

            if (normalizedLastActiveTabId is null && lastActiveTabId == originalId)
            {
                normalizedLastActiveTabId = tab.Id;
            }
        }

        remappedIds = remaps;
        return normalizedLastActiveTabId;
    }

    private static List<SessionTabState> NormalizeLoadedTabs(List<SessionTabState>? tabs)
    {
        if (tabs is null)
        {
            return [];
        }

        var result = new List<SessionTabState>();
        foreach (var tab in tabs)
        {
            if (NormalizeLoadedTab(tab))
            {
                result.Add(tab);
            }
        }

        return result;
    }

    private static List<ClosedTabState> NormalizeLoadedClosedTabs(List<ClosedTabState>? closedTabs)
    {
        if (closedTabs is null)
        {
            return [];
        }

        var result = new List<ClosedTabState>();
        foreach (var closedTab in closedTabs)
        {
            if (NormalizeLoadedClosedTab(closedTab))
            {
                result.Add(closedTab);
            }
        }

        return result;
    }

    private static List<ClosedWindowState> NormalizeLoadedClosedWindows(
        List<ClosedWindowState>? closedWindows)
    {
        foreach (var closedWindow in closedWindows ?? [])
        {
            closedWindow.WindowState ??= new SessionWindowState();
            closedWindow.WindowState.Tabs = NormalizeLoadedTabs(closedWindow.WindowState.Tabs);
            closedWindow.WindowState.ClosedTabs = [];
            if (closedWindow.WindowState.LastActiveTabId is { } lastActiveTabId
                && !closedWindow.WindowState.Tabs.Any(tab => tab.Id == lastActiveTabId))
            {
                closedWindow.WindowState.LastActiveTabId = null;
            }
        }

        return new TabOperationService().NormalizeClosedWindowHistory(closedWindows);
    }

    private static bool NormalizeLoadedTab(SessionTabState tab)
    {
        tab.Url = NormalizeLoadedUrl(tab.Url);
        if (string.IsNullOrWhiteSpace(tab.Url))
        {
            return false;
        }

        if (tab.Id == Guid.Empty)
        {
            tab.Id = Guid.NewGuid();
        }

        tab.NavigationHistory = NormalizeLoadedNavigationHistory(tab.NavigationHistory);
        tab.NavigationHistoryIndex = NormalizeLoadedNavigationHistoryIndex(
            tab.NavigationHistory,
            tab.NavigationHistoryIndex);
        if (!Enum.IsDefined(typeof(TabAudioPan), tab.AudioPan))
        {
            tab.AudioPan = TabAudioPan.Stereo;
        }

        tab.AudioBoostDecibels = AudioBoostProfile.Normalize(tab.AudioBoostDecibels);

        tab.ContainerProfileId = ContainerProfile.NormalizeId(tab.ContainerProfileId);
        tab.FolderTabViewMode = tab.IsFolderTab
            ? FolderTabViewMode.Normalize(tab.FolderTabViewMode)
            : null;
        tab.FolderTabSortMode = tab.IsFolderTab
            ? FolderTabSortMode.Normalize(tab.FolderTabSortMode)
            : null;
        tab.FolderTabSortDirection = tab.IsFolderTab
            ? FolderTabSortDirection.Normalize(tab.FolderTabSortDirection)
            : null;
        tab.FolderTabState = tab.IsFolderTab
            ? NormalizeLoadedFolderTabState(tab.FolderTabState)
            : null;
        tab.SplitViewId = NormalizeLoadedOptionalToken(tab.SplitViewId);
        if (tab.LastActivatedAt is { } lastActivatedAt
            && (lastActivatedAt < DateTimeOffset.UnixEpoch
                || lastActivatedAt > DateTimeOffset.UtcNow.AddDays(1)))
        {
            tab.LastActivatedAt = null;
        }
        tab.SplitViewOrientation = string.IsNullOrWhiteSpace(tab.SplitViewOrientation)
            ? null
            : SplitViewModelProfile.NormalizeOrientation(tab.SplitViewOrientation);
        tab.SplitViewRatio = tab.SplitViewRatio is { } ratio && double.IsFinite(ratio)
            ? SplitViewModelProfile.NormalizeRatio(ratio)
            : null;
        return true;
    }

    private static bool NormalizeLoadedClosedTab(ClosedTabState closedTab)
    {
        closedTab.Url = NormalizeLoadedUrl(closedTab.Url);
        if (string.IsNullOrWhiteSpace(closedTab.Url))
        {
            return false;
        }

        closedTab.NavigationHistory = NormalizeLoadedNavigationHistory(closedTab.NavigationHistory);
        closedTab.NavigationHistoryIndex = NormalizeLoadedNavigationHistoryIndex(
            closedTab.NavigationHistory,
            closedTab.NavigationHistoryIndex);
        if (!Enum.IsDefined(typeof(TabAudioPan), closedTab.AudioPan))
        {
            closedTab.AudioPan = TabAudioPan.Stereo;
        }

        closedTab.AudioBoostDecibels = AudioBoostProfile.Normalize(closedTab.AudioBoostDecibels);

        closedTab.ContainerProfileId = ContainerProfile.NormalizeId(closedTab.ContainerProfileId);
        closedTab.FolderTabViewMode = closedTab.IsFolderTab
            ? FolderTabViewMode.Normalize(closedTab.FolderTabViewMode)
            : null;
        closedTab.FolderTabSortMode = closedTab.IsFolderTab
            ? FolderTabSortMode.Normalize(closedTab.FolderTabSortMode)
            : null;
        closedTab.FolderTabSortDirection = closedTab.IsFolderTab
            ? FolderTabSortDirection.Normalize(closedTab.FolderTabSortDirection)
            : null;
        closedTab.FolderTabState = closedTab.IsFolderTab
            ? NormalizeLoadedFolderTabState(closedTab.FolderTabState)
            : null;
        return true;
    }

    private static FolderTabSessionState? NormalizeLoadedFolderTabState(FolderTabSessionState? state)
    {
        if (state is null)
        {
            return null;
        }

        state.NavigationHistory = NormalizeLoadedNavigationHistory(state.NavigationHistory);
        var navigationHistoryIndex = NormalizeLoadedNavigationHistoryIndex(
            state.NavigationHistory,
            state.NavigationHistoryIndex);
        FolderTabService.TrimNavigationHistory(
            state.NavigationHistory,
            ref navigationHistoryIndex);
        state.NavigationHistoryIndex = navigationHistoryIndex;
        state.SelectedPaths = NormalizeLoadedNavigationHistory(state.SelectedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.SelectionAnchorPath = NormalizeLoadedOptionalToken(state.SelectionAnchorPath);
        state.DetailScrollOffset = NormalizeScrollOffset(state.DetailScrollOffset);
        state.IconScrollOffset = NormalizeScrollOffset(state.IconScrollOffset);
        state.DetailColumnOrder = FolderTabService.NormalizeDetailColumnOrder(state.DetailColumnOrder);
        return state;
    }

    private static double NormalizeScrollOffset(double value)
    {
        return double.IsFinite(value) && value >= 0 ? value : 0;
    }

    private static string? NormalizeLoadedUrl(string? url)
    {
        return string.IsNullOrWhiteSpace(url)
            ? null
            : url.Trim();
    }

    private static string? NormalizeLoadedOptionalToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static List<string> NormalizeLoadedNavigationHistory(List<string>? navigationHistory)
    {
        return navigationHistory?
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Select(static url => url.Trim())
            .ToList() ?? [];
    }

    private static int NormalizeLoadedNavigationHistoryIndex(List<string> navigationHistory, int navigationHistoryIndex)
    {
        if (navigationHistory.Count == 0)
        {
            return -1;
        }

        if (navigationHistoryIndex < 0)
        {
            return -1;
        }

        return navigationHistoryIndex >= navigationHistory.Count
            ? navigationHistory.Count - 1
            : navigationHistoryIndex;
    }

    private void AppendSessionDiagnostic(string operation, string detail, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(_diagnosticDirectory);

            var logPath = Path.Combine(_diagnosticDirectory, "diagnostic.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > SessionDiagnosticLogMaxBytes)
            {
                File.Delete(logPath);
            }

            var fields = new List<string>
            {
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                "runner=session-service",
                $"operation={NormalizeSessionDiagnosticToken(operation)}",
                $"detail={NormalizeSessionDiagnosticToken(detail)}"
            };
            if (exception is not null)
            {
                fields.Add($"type={NormalizeSessionDiagnosticToken(exception.GetType().Name)}");
                fields.Add($"message={NormalizeSessionDiagnosticToken(exception.Message)}");
            }

            File.AppendAllText(logPath, string.Join(" | ", fields) + Environment.NewLine);
        }
        catch
        {
            // 診断ログの失敗でセッション復旧を止めない。
        }
    }

    private static string NormalizeSessionDiagnosticToken(string value)
    {
        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('|', '/')
            .Trim();
        return normalized.Length <= 500
            ? normalized
            : normalized[..500];
    }

    public void Save(SessionData session)
    {
        ValidateSupportedSessionVersion(session);
        EnsureUniqueSessionTabIds(session);
        var serializedSession = SerializeSession(session);
        SessionFileGate.Wait();
        try
        {
            EnsureSessionPrimaryMayBeSaved();
            if (CanSkipSessionSave(serializedSession))
            {
                var skippedAttempt = _recoveryDiagnosticState.Begin(
                    SessionPersistenceDiagnosticOperation.NormalSave);
                _recoveryDiagnosticState.CompleteSkipped(skippedAttempt);
                return;
            }

            TryProtectCurrentSessionBeforeOverwrite(session);
            var saveAttempt = _recoveryDiagnosticState.Begin(SessionPersistenceDiagnosticOperation.NormalSave);
            try
            {
                _beforePersistenceOperation?.Invoke(SessionPersistenceDiagnosticOperation.NormalSave);
                if (_sessionCorruptPrimaryBytes is not null)
                    EnsurePrimaryUnchanged(_sessionFile, _sessionCorruptPrimaryBytes);
                SaveSerializedToFile(_sessionFile, serializedSession,
                    _preserveSessionBackupUntilPrimarySaved ? null : _sessionBackupFile);
                _preserveSessionBackupUntilPrimarySaved = false;
                _sessionCorruptPrimaryBytes = null;
                _lastSavedSessionJson = serializedSession;
                _recoveryDiagnosticState.CompleteSucceeded(saveAttempt);
            }
            catch (Exception ex)
            {
                _recoveryDiagnosticState.CompleteFailed(saveAttempt, ex);
                throw;
            }

            TrySaveSessionSnapshot(serializedSession);
        }
        finally
        {
            SessionFileGate.Release();
        }
    }

    private static void SaveToFile(string filePath, SessionData session, string? backupFilePath = null)
    {
        AtomicFileService.Write(
            filePath,
            backupFilePath,
            stream => JsonSerializer.Serialize(stream, session, JsonOptions));
    }

    private bool CanSkipSessionSave(string serializedSession)
    {
        return File.Exists(_sessionFile)
            && string.Equals(_lastSavedSessionJson, serializedSession, StringComparison.Ordinal);
    }

    private void SetLoadedSessionPersistenceState(SessionData session, SessionLoadInfo info, byte[]? primaryBytes)
    {
        _preserveSessionBackupUntilPrimarySaved = info.RepairFailed;
        _sessionCorruptPrimaryBytes = info.RepairFailed ? primaryBytes : null;
        _sessionSaveBlockedByUnrecoverablePrimary = info.Source == "empty" && info.PrimaryLoadFailed;
        _sessionPrimaryVersionCheckedForSave = info.Source == "primary"
            || (info.Recovered && !info.RepairFailed);
        // 読み込めた内容ではなく、正常な本体の確認または本体への確定書き込みだけを省略条件にする。
        _lastSavedSessionJson = info.Source == "primary" || (info.Recovered && !info.RepairFailed)
            ? SerializeSession(session)
            : null;
    }

    private static SessionData ValidateSupportedSessionVersion(SessionData session)
    {
        // 旧形式の版1（version省略時を含む）と現行の版2だけを解釈する。
        if (session.Version is not (1 or 2))
            throw new UnsupportedSessionVersionException(session.Version);
        return session;
    }

    private static SessionData ParseSessionData(byte[] originalBytes)
    {
        // 退避・一致照合にはBOMを含む原本を使い、解析範囲だけBOMを除く。
        var bomLength = originalBytes.Length >= 3 && originalBytes[0] == 0xEF
            && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF ? 3 : 0;
        using var document = JsonDocument.Parse(originalBytes.AsMemory(bomLength));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("セッションのルート要素が不正です。");
        if (root.TryGetProperty("version", out var versionElement))
        {
            if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
                throw new InvalidDataException("セッションの版情報が不正です。");
            if (version is not (1 or 2))
                throw new UnsupportedSessionVersionException(version);
        }

        var session = root.Deserialize<SessionData>(JsonOptions)
            ?? throw new InvalidDataException("セッションの内容が null です。");
        ValidateSupportedSessionVersion(session);
        ValidateSessionStructure(session);
        return session;
    }

    private static void ValidateSessionStructure(SessionData session)
    {
        // 省略された配列やnullの配列は従来どおり補完するが、配列内のnull要素は
        // 復元対象を黙って失うため、正規化より前に明示的な内容不正として拒否する。
        if (session.Tabs?.Any(static tab => tab is null) == true
            || session.ClosedTabs?.Any(static tab => tab is null) == true
            || session.ClosedWindows?.Any(static window => window is null
                || window.WindowState?.Tabs?.Any(static tab => tab is null) == true
                || window.WindowState?.ClosedTabs?.Any(static tab => tab is null) == true) == true
            || session.Windows?.Any(static window => window is null
                || window.Tabs?.Any(static tab => tab is null) == true
                || window.ClosedTabs?.Any(static tab => tab is null) == true) == true
            || session.ClosedYouTubeMiniPlayers?.Any(static player => player is null) == true
            || session.WindowMemoryYouTubeMiniPlayers?.Any(static player => player is null) == true)
            throw new InvalidDataException("セッションの配列に不正な要素があります。");
    }

    private void EnsureSessionPrimaryMayBeSaved()
    {
        try
        {
            if (_sessionSaveBlockedByUnrecoverablePrimary)
                throw new IOException("有効な復旧元がない破損セッション本体を自動保存で上書きしません。原本を確認してください。");
            var knownCorruptPrimary = _sessionCorruptPrimaryBytes;
            EnsureSupportedPrimaryVersionForSave(_sessionFile, ref _sessionPrimaryVersionCheckedForSave,
                ref _sessionSaveBlockedByUnsupportedVersion, ref _preserveSessionBackupUntilPrimarySaved,
                ref _sessionCorruptPrimaryBytes);
            if (knownCorruptPrimary is null && _sessionCorruptPrimaryBytes is not null)
            {
                _sessionCorruptPrimaryBytes = null;
                _sessionPrimaryVersionCheckedForSave = false;
                throw new IOException("読み込み後に破損したセッションを検出しました。上書きせず再読込してください。");
            }
            if (_sessionCorruptPrimaryBytes is not null)
                PreserveCorruptPrimary(_sessionFile, _sessionCorruptPrimaryBytes);
        }
        catch (Exception ex)
        {
            var attempt = _recoveryDiagnosticState.Begin(SessionPersistenceDiagnosticOperation.NormalSave);
            _recoveryDiagnosticState.CompleteFailed(attempt, ex);
            throw;
        }
    }

    private static void EnsureSupportedPrimaryVersionForSave(string filePath, ref bool checkedForSave,
        ref bool blockedByUnsupportedVersion, ref bool preserveBackup, ref byte[]? corruptPrimaryBytes)
    {
        if (blockedByUnsupportedVersion)
            throw new UnsupportedSessionVersionException();
        if (checkedForSave) return;

        if (corruptPrimaryBytes is not null)
            EnsurePrimaryUnchanged(filePath, corruptPrimaryBytes);
        if (File.Exists(filePath))
        {
            var bytes = File.ReadAllBytes(filePath);
            try
            {
                ParseSessionData(bytes);
            }
            catch (UnsupportedSessionVersionException)
            {
                blockedByUnsupportedVersion = true;
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // 破損本体から保存を続ける場合でも、正常なbackupを破損本体で置換しない。
                preserveBackup = true;
                corruptPrimaryBytes = bytes;
            }
        }

        checkedForSave = true;
    }

    private sealed class UnsupportedSessionVersionException : IOException
    {
        public UnsupportedSessionVersionException(int version)
            : base($"未対応のセッション形式 (version={version}) です。元ファイルを保持して読み込みを中止しました。") { }

        public UnsupportedSessionVersionException()
            : base("未対応のセッション形式を検出したため、元ファイルを保持して保存を中止しました。") { }
    }

    private static string SerializeSession(SessionData session)
    {
        return JsonSerializer.Serialize(session, JsonOptions);
    }

    private static async Task SaveSerializedToFileAsync(
        string filePath,
        string serializedSession,
        string? backupFilePath = null)
    {
        await AtomicFileService.WriteAsync(
            filePath,
            backupFilePath,
            async stream =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(serializedSession);
            });
    }

    private static void SaveSerializedToFile(
        string filePath,
        string serializedSession,
        string? backupFilePath = null)
    {
        AtomicFileService.Write(
            filePath,
            backupFilePath,
            stream =>
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(serializedSession);
            });
    }

    private sealed record SessionLoadResult(SessionData Session, SessionLoadInfo Info, byte[]? PrimaryBytes = null);

    private sealed record PrimaryRepairResult(bool Succeeded, CorruptPrimaryPreservationStatus Preservation);

    private sealed record SessionLoadAttempt(
        SessionData? Session,
        bool Failed,
        Exception? Failure = null,
        byte[]? ObservedBytes = null);
}

internal enum CorruptPrimaryPreservationStage
{
    CreateDirectory,
    Copy,
    Flush,
    Verify
}

public enum CorruptPrimaryPreservationStatus
{
    NotNeeded,
    Preserved,
    Failed
}

public sealed record SessionLoadInfo(
    string Source,
    bool Recovered,
    bool PrimaryLoadFailed,
    bool BackupLoadFailed,
    bool RepairFailed)
{
    public CorruptPrimaryPreservationStatus CorruptPrimaryPreservation { get; init; }
    public static SessionLoadInfo Primary { get; } = new("primary", false, false, false, false);

    public static SessionLoadInfo Empty { get; } = new("empty", false, false, false, false);
}
