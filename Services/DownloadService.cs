using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class DownloadService
{
    private const int MaxDetailTextLength = 1000;
    private const int MaxErrorTextLength = 4000;
    private const int MaxOutputTextLength = 6000;
    private static readonly TimeSpan ExternalShutdownCompletionTimeout = TimeSpan.FromSeconds(65);

    private readonly DownloadStore _store;
    private readonly DownloadPolicy _policy;
    private readonly Dispatcher _dispatcher;
    private readonly bool _persistHistory;
    private readonly Dictionary<Guid, CoreWebView2DownloadOperation> _operations = [];
    private readonly Dictionary<Guid, long> _lastBytes = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastSamples = [];
    private readonly Dictionary<Guid, Process> _externalProcesses = [];
    private readonly Dictionary<Guid, Func<Process, Task>> _externalStopRequests = [];
    private readonly Dictionary<Guid, Task> _externalStopTasks = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _externalCompletionSignals = [];
    private readonly HashSet<Guid> _pauseRequested = [];
    private readonly HashSet<Guid> _cancelRequested = [];
    private int _historyRetentionLimit;
    private long _historySaveVersion;
    private long _persistedHistorySaveVersion;

    public DownloadService(
        DownloadStore store,
        DownloadPolicy policy,
        Dispatcher dispatcher,
        bool persistHistory = true)
    {
        _store = store;
        _policy = policy;
        _dispatcher = dispatcher;
        _persistHistory = persistHistory;

        foreach (var entry in (_persistHistory ? _store.Load() : [])
                     .OrderByDescending(static entry => entry.StartedAt))
        {
            Items.Add(DownloadItem.FromHistoryEntry(entry));
        }
    }

    public ObservableCollection<DownloadItem> Items { get; } = [];

    public event Action<DownloadItem>? ItemFinished;

    public bool HasActiveExternalDownloads => _externalProcesses.Count > 0;

    public void ApplyHistoryRetentionLimit(int limit)
    {
        _historyRetentionLimit = Math.Clamp(limit, 0, DownloadSettingsProfile.MaximumHistoryRetentionLimit);
        if (TrimHistoryIfNeeded())
        {
            SaveHistory();
        }
    }

    public DownloadItem TrackDownload(
        CoreWebView2DownloadOperation operation,
        string filePath,
        DownloadSafetyResult safety,
        string? containerProfileId = null)
    {
        var item = new DownloadItem
        {
            SourceUrl = operation.Uri,
            ContainerProfileId = ContainerProfile.NormalizeId(containerProfileId),
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            ContentType = operation.MimeType,
            ContentDisposition = operation.ContentDisposition,
            Kind = _policy.GetKind(filePath, operation.MimeType),
            State = DownloadItemState.InProgress,
            BytesReceived = operation.BytesReceived,
            TotalBytesToReceive = NormalizeByteCount(operation.TotalBytesToReceive),
            CanResumeOperation = operation.CanResume,
            IsDangerous = safety.ShouldWarn,
            SafetyWarning = safety.WarningText,
            StartedAt = DateTimeOffset.Now
        };

        Items.Insert(0, item);
        _operations[item.Id] = operation;
        _lastBytes[item.Id] = item.BytesReceived;
        _lastSamples[item.Id] = DateTimeOffset.Now;

        operation.BytesReceivedChanged += (_, _) => Dispatch(() => UpdateFromOperation(item, operation));
        operation.EstimatedEndTimeChanged += (_, _) => Dispatch(() => UpdateFromOperation(item, operation));
        operation.StateChanged += (_, _) => Dispatch(() => UpdateFromOperation(item, operation));

        SaveHistory();
        return item;
    }

    public DownloadItem TrackExternalDownload(
        string sourceUrl,
        string displayName,
        string initialPath,
        DownloadFileKind kind,
        string detailText,
        string externalToolName = "",
        string? containerProfileId = null)
    {
        var item = new DownloadItem
        {
            SourceUrl = sourceUrl,
            ContainerProfileId = ContainerProfile.NormalizeId(containerProfileId),
            FileName = displayName,
            FilePath = initialPath,
            Kind = kind,
            State = DownloadItemState.InProgress,
            ProgressPercentOverride = 0,
            DetailOverrideText = NormalizeText(detailText, MaxDetailTextLength),
            ExternalToolName = externalToolName,
            StartedAt = DateTimeOffset.Now
        };

        Items.Insert(0, item);
        SaveHistory();
        return item;
    }

    public void AttachExternalProcess(
        DownloadItem item,
        Process process,
        Func<Process, Task>? stopAsync = null)
    {
        _externalProcesses[item.Id] = process;
        _externalCompletionSignals[item.Id] = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopAsync is not null)
        {
            _externalStopRequests[item.Id] = stopAsync;
        }
    }

    public void UpdateExternalDownloadProgress(
        DownloadItem item,
        double? progressPercent,
        string detailText,
        string? filePath = null)
    {
        if (item.State is DownloadItemState.Completed or DownloadItemState.Canceled)
        {
            return;
        }

        if (progressPercent is not null)
        {
            item.ProgressPercentOverride = progressPercent;
        }

        if (!string.IsNullOrWhiteSpace(detailText))
        {
            item.DetailOverrideText = NormalizeText(detailText, MaxDetailTextLength);
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            item.FilePath = filePath;
            item.FileName = Path.GetFileName(filePath);
        }
    }

    public void CompleteExternalDownload(
        DownloadItem item,
        string? filePath,
        int? exitCode = null,
        string lastOutputText = "")
    {
        if (item.State == DownloadItemState.Canceled)
        {
            CompleteCanceledExternalDownload(item, filePath, exitCode, lastOutputText);
            return;
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            item.FilePath = filePath;
            item.FileName = Path.GetFileName(filePath);
        }

        item.State = DownloadItemState.Completed;
        item.ProgressPercentOverride = 100;
        item.DetailOverrideText = "yt-dlp 完了";
        item.ExternalExitCode = exitCode;
        item.LastOutputText = NormalizeText(
            ExternalDownloadDiagnosticSanitizer.Sanitize(lastOutputText),
            MaxOutputTextLength);
        item.CompletedAt = DateTimeOffset.Now;
        ReleaseExternalProcess(item.Id);
        SaveHistory();
        NotifyItemFinished(item);
    }

    public void FailExternalDownload(
        DownloadItem item,
        string errorText,
        int? exitCode = null,
        string lastOutputText = "",
        DownloadFailureInfo? failureInfo = null)
    {
        if (item.State == DownloadItemState.Canceled)
        {
            ReleaseExternalProcess(item.Id);
            SaveHistory();
            return;
        }

        item.State = DownloadItemState.Failed;
        item.ErrorText = string.IsNullOrWhiteSpace(errorText)
            ? "外部ツールの実行に失敗しました"
            : NormalizeText(errorText, MaxErrorTextLength);
        item.ExternalExitCode = exitCode;
        item.LastOutputText = NormalizeText(lastOutputText, MaxOutputTextLength);
        ApplyFailureInfo(
            item,
            failureInfo ?? new DownloadFailureInfo(
                "外部ツールの実行に失敗しました",
                "詳細画面で終了コードと最後の出力を確認してください。"));
        item.CompletedAt = DateTimeOffset.Now;
        ReleaseExternalProcess(item.Id);
        SaveHistory();
        NotifyItemFinished(item);
    }

    public void Cancel(DownloadItem item)
    {
        if (_externalProcesses.TryGetValue(item.Id, out var externalProcess))
        {
            RequestExternalProcessStop(item, externalProcess, saveHistory: true);
            return;
        }

        if (_operations.TryGetValue(item.Id, out var operation))
        {
            _cancelRequested.Add(item.Id);
            operation.Cancel();
            return;
        }

        item.State = DownloadItemState.Canceled;
        item.CompletedAt = DateTimeOffset.Now;
        SaveHistory();
        NotifyItemFinished(item);
    }

    public bool TryFinalizeExternalDownloadSegment(DownloadItem item)
    {
        if (item.State != DownloadItemState.InProgress
            || !_externalProcesses.TryGetValue(item.Id, out var externalProcess))
        {
            return false;
        }

        RequestExternalProcessStop(
            item,
            externalProcess,
            saveHistory: true,
            markCanceled: false,
            detailText: "ここまでのファイルを確定しています（完了後も継続します）");
        return true;
    }

    public Task<bool> StopActiveExternalDownloadsForShutdownAsync()
    {
        return StopActiveExternalDownloadsForShutdownAsync(ExternalShutdownCompletionTimeout);
    }

    internal async Task<bool> StopActiveExternalDownloadsForShutdownAsync(TimeSpan completionTimeout)
    {
        if (completionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTimeout));
        }

        var pendingProcesses = _externalProcesses.ToArray();
        if (pendingProcesses.Length == 0)
        {
            return true;
        }

        var completionTasks = new List<Task>(pendingProcesses.Length);
        foreach (var (itemId, process) in pendingProcesses)
        {
            if (!_externalCompletionSignals.TryGetValue(itemId, out var completionSignal))
            {
                completionSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _externalCompletionSignals[itemId] = completionSignal;
            }

            completionTasks.Add(completionSignal.Task);
            var item = Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is not null)
            {
                RequestExternalProcessStop(item, process, saveHistory: false);
            }
        }

        SaveHistory();
        try
        {
            await Task.WhenAll(completionTasks)
                .WaitAsync(completionTimeout)
                .ConfigureAwait(true);
            return true;
        }
        catch (TimeoutException)
        {
            FinalizeTimedOutExternalDownloads(pendingProcesses);
            return false;
        }
        catch
        {
            FinalizeTimedOutExternalDownloads(pendingProcesses);
            return false;
        }
    }

    public void CompleteCanceledExternalDownload(
        DownloadItem item,
        string? filePath,
        int? exitCode = null,
        string lastOutputText = "")
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            item.FilePath = filePath;
            item.FileName = Path.GetFileName(filePath);
        }

        item.IsFinalizingExternalSegment = false;
        item.State = DownloadItemState.Canceled;
        item.DetailOverrideText = File.Exists(item.FilePath)
            ? "途中まで保存しました"
            : "キャンセル";
        item.ExternalExitCode = exitCode;
        item.LastOutputText = NormalizeText(
            ExternalDownloadDiagnosticSanitizer.Sanitize(lastOutputText),
            MaxOutputTextLength);
        item.CompletedAt = DateTimeOffset.Now;
        ReleaseExternalProcess(item.Id);
        SaveHistory();
        NotifyItemFinished(item);
    }

    public void CompleteFinalizedExternalDownloadSegment(
        DownloadItem item,
        string? filePath,
        int? exitCode = null,
        string lastOutputText = "")
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            item.FilePath = filePath;
            item.FileName = Path.GetFileName(filePath);
        }

        item.IsFinalizingExternalSegment = false;
        item.State = DownloadItemState.Completed;
        item.ProgressPercentOverride = 100;
        item.DetailOverrideText = File.Exists(item.FilePath)
            ? "ここまで保存しました（次のファイルへ継続）"
            : "ここまでの保存確認が取れませんでした（継続処理中）";
        item.ExternalExitCode = exitCode;
        item.LastOutputText = NormalizeText(
            ExternalDownloadDiagnosticSanitizer.Sanitize(lastOutputText),
            MaxOutputTextLength);
        item.CompletedAt = DateTimeOffset.Now;
        ReleaseExternalProcess(item.Id);
        SaveHistory();
        NotifyItemFinished(item);
    }

    public void PauseOrResume(DownloadItem item)
    {
        if (!_operations.TryGetValue(item.Id, out var operation))
        {
            return;
        }

        if (item.State == DownloadItemState.Paused)
        {
            if (!operation.CanResume)
            {
                return;
            }

            _pauseRequested.Remove(item.Id);
            operation.Resume();
            item.State = DownloadItemState.InProgress;
            SaveHistory();
            return;
        }

        if (item.State == DownloadItemState.InProgress)
        {
            _pauseRequested.Add(item.Id);
            operation.Pause();
            item.State = DownloadItemState.Paused;
            SaveHistory();
        }
    }

    public void RemoveHistory(DownloadItem item)
    {
        _operations.Remove(item.Id);
        ReleaseExternalProcess(item.Id);
        _lastBytes.Remove(item.Id);
        _lastSamples.Remove(item.Id);
        _pauseRequested.Remove(item.Id);
        _cancelRequested.Remove(item.Id);
        Items.Remove(item);
        SaveHistory();
    }

    public void SaveHistory()
    {
        if (!_persistHistory)
        {
            return;
        }

        var saveVersion = Interlocked.Increment(ref _historySaveVersion);
        try
        {
            TrimHistoryIfNeeded();
            _store.Save(Items.Select(static item => item.ToHistoryEntry()));
            MarkHistorySavePersisted(saveVersion);
        }
        catch (UnauthorizedAccessException)
        {
            // 履歴保存に失敗してもブラウザ本体の操作を止めない。
        }
        catch (IOException)
        {
            // ファイルロックや一時的な権限問題では次回保存に任せる。
        }
    }

    internal Action CaptureHistorySaveOperation()
    {
        if (!_persistHistory) return static () => { };
        var saveVersion = Volatile.Read(ref _historySaveVersion);
        if (Volatile.Read(ref _persistedHistorySaveVersion) >= saveVersion)
        {
            return static () => { };
        }

        var snapshot = Items.Select(static item => item.ToHistoryEntry()).ToList();
        return () =>
        {
            _store.Save(snapshot);
            MarkHistorySavePersisted(saveVersion);
        };
    }

    private void MarkHistorySavePersisted(long saveVersion)
    {
        while (true)
        {
            var persisted = Volatile.Read(ref _persistedHistorySaveVersion);
            if (persisted >= saveVersion
                || Interlocked.CompareExchange(ref _persistedHistorySaveVersion, saveVersion, persisted) == persisted)
            {
                return;
            }
        }
    }

    public int DeleteHistory(
        string? containerProfileId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var normalizedContainerId = ContainerProfile.NormalizeId(containerProfileId);
        var targets = Items
            .Where(item =>
                item.CanRemoveHistory
                && ContainerProfile.NormalizeId(item.ContainerProfileId)
                    .Equals(normalizedContainerId, StringComparison.OrdinalIgnoreCase)
                && (from is null || item.StartedAt >= from.Value)
                && (to is null || item.StartedAt < to.Value))
            .ToList();
        foreach (var item in targets)
        {
            Items.Remove(item);
        }

        if (targets.Count > 0)
        {
            SaveHistory();
        }

        return targets.Count;
    }

    private void UpdateFromOperation(DownloadItem item, CoreWebView2DownloadOperation operation)
    {
        var now = DateTimeOffset.Now;
        var previousBytes = _lastBytes.GetValueOrDefault(item.Id, operation.BytesReceived);
        var previousSample = _lastSamples.GetValueOrDefault(item.Id, now);
        var elapsedSeconds = Math.Max(0.001, (now - previousSample).TotalSeconds);
        var bytesDelta = Math.Max(0, operation.BytesReceived - previousBytes);

        item.BytesReceived = operation.BytesReceived;
        item.TotalBytesToReceive = NormalizeByteCount(operation.TotalBytesToReceive);
        item.CanResumeOperation = operation.CanResume;
        item.BytesPerSecond = bytesDelta / elapsedSeconds;
        item.RemainingSeconds = item.BytesPerSecond > 0 && item.TotalBytesToReceive > item.BytesReceived
            ? (item.TotalBytesToReceive - item.BytesReceived) / item.BytesPerSecond
            : 0;

        _lastBytes[item.Id] = operation.BytesReceived;
        _lastSamples[item.Id] = now;

        switch (operation.State)
        {
            case CoreWebView2DownloadState.InProgress:
                item.State = DownloadItemState.InProgress;
                item.ErrorText = string.Empty;
                break;
            case CoreWebView2DownloadState.Completed:
                item.State = DownloadItemState.Completed;
                item.CompletedAt = now;
                item.BytesPerSecond = 0;
                item.RemainingSeconds = 0;
                ReleaseOperation(item.Id);
                SaveHistory();
                NotifyItemFinished(item);
                break;
            case CoreWebView2DownloadState.Interrupted:
                if (_pauseRequested.Contains(item.Id) && operation.CanResume)
                {
                    item.State = DownloadItemState.Paused;
                    item.ErrorText = string.Empty;
                }
                else if (_cancelRequested.Contains(item.Id))
                {
                    item.State = DownloadItemState.Canceled;
                    item.CompletedAt = now;
                    ReleaseOperation(item.Id);
                    NotifyItemFinished(item);
                }
                else
                {
                    item.State = DownloadItemState.Failed;
                    item.CompletedAt = now;
                    item.ErrorText = operation.InterruptReason.ToString();
                    ApplyFailureInfo(item, DownloadFailureClassifier.ClassifyWebView2(operation.InterruptReason));
                    if (!operation.CanResume)
                    {
                        ReleaseOperation(item.Id);
                    }

                    NotifyItemFinished(item);
                }

                SaveHistory();
                break;
        }
    }

    private void ReleaseOperation(Guid itemId)
    {
        _operations.Remove(itemId);
        _lastBytes.Remove(itemId);
        _lastSamples.Remove(itemId);
        _pauseRequested.Remove(itemId);
        _cancelRequested.Remove(itemId);
    }

    private static void ApplyFailureInfo(DownloadItem item, DownloadFailureInfo failure)
    {
        item.FailureReason = NormalizeText(failure.Reason, MaxDetailTextLength);
        item.FailureSuggestion = NormalizeText(failure.Suggestion, MaxDetailTextLength);
        item.DetailOverrideText = NormalizeText(DownloadFailureClassifier.BuildDetailText(failure), MaxDetailTextLength);
    }

    private void ReleaseExternalProcess(Guid itemId)
    {
        _externalProcesses.Remove(itemId);
        _externalStopRequests.Remove(itemId);
        _externalStopTasks.Remove(itemId);
        _cancelRequested.Remove(itemId);
        if (_externalCompletionSignals.Remove(itemId, out var completionSignal))
        {
            completionSignal.TrySetResult(true);
        }
    }

    private void RequestExternalProcessStop(
        DownloadItem item,
        Process process,
        bool saveHistory,
        bool markCanceled = true,
        string? detailText = null)
    {
        _cancelRequested.Add(item.Id);
        if (markCanceled)
        {
            item.State = DownloadItemState.Canceled;
        }
        else
        {
            item.IsFinalizingExternalSegment = true;
        }

        item.DetailOverrideText = string.IsNullOrWhiteSpace(detailText)
            ? "停止処理中（途中までのファイルを確定しています）"
            : detailText;
        if (saveHistory)
        {
            SaveHistory();
        }

        if (_externalStopTasks.ContainsKey(item.Id))
        {
            return;
        }

        _externalStopRequests.TryGetValue(item.Id, out var stopAsync);
        _externalStopTasks[item.Id] = Task.Run(() => StopExternalProcessAsync(process, stopAsync));
    }

    private void FinalizeTimedOutExternalDownloads(
        IReadOnlyList<KeyValuePair<Guid, Process>> pendingProcesses)
    {
        foreach (var (itemId, process) in pendingProcesses)
        {
            if (!_externalProcesses.ContainsKey(itemId))
            {
                continue;
            }

            ForceStopExternalProcess(process);
            var item = Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is not null)
            {
                item.IsFinalizingExternalSegment = false;
                item.State = DownloadItemState.Canceled;
                item.DetailOverrideText = File.Exists(item.FilePath)
                    ? "途中まで保存しました"
                    : "終了時に停止しました";
                item.CompletedAt = DateTimeOffset.Now;
            }

            ReleaseExternalProcess(itemId);
        }

        SaveHistory();
    }

    private static async Task StopExternalProcessAsync(
        Process process,
        Func<Process, Task>? stopAsync)
    {
        try
        {
            if (stopAsync is not null)
            {
                await stopAsync(process).ConfigureAwait(false);
                return;
            }
        }
        catch
        {
            // 正常停止要求に失敗した場合だけ従来の強制終了へ戻す。
        }

        try
        {
            ForceStopExternalProcess(process);
        }
        catch
        {
            // 終了済みプロセスとの競合は完了通知側に任せる。
        }
    }

    private static void ForceStopExternalProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 終了済みプロセスとの競合や権限エラーでアプリ終了を妨げない。
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void NotifyItemFinished(DownloadItem item)
    {
        try
        {
            ItemFinished?.Invoke(item);
        }
        catch
        {
            // 完了通知の購読側例外でダウンロード状態更新を壊さない。
        }
    }

    private static long NormalizeByteCount(ulong? value)
    {
        if (value is null)
        {
            return 0;
        }

        return value > long.MaxValue
            ? long.MaxValue
            : (long)value.Value;
    }

    private bool TrimHistoryIfNeeded()
    {
        if (_historyRetentionLimit <= 0)
        {
            return false;
        }

        var removable = Items
            .Where(static item => item.CanRemoveHistory)
            .OrderByDescending(static item => item.StartedAt)
            .Skip(_historyRetentionLimit)
            .ToList();
        foreach (var item in removable)
        {
            _operations.Remove(item.Id);
            ReleaseExternalProcess(item.Id);
            _lastBytes.Remove(item.Id);
            _lastSamples.Remove(item.Id);
            _pauseRequested.Remove(item.Id);
            _cancelRequested.Remove(item.Id);
            Items.Remove(item);
        }

        return removable.Count > 0;
    }

    private static string NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 1)] + "…";
    }
}
