using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace TabBrowser.Models;

public enum DownloadItemState
{
    Pending,
    InProgress,
    Paused,
    Completed,
    Failed,
    Canceled
}

public enum DownloadFileKind
{
    Other,
    Pdf,
    Image,
    Archive,
    Executable,
    Video
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _sourceUrl = string.Empty;
    private string _fileName = string.Empty;
    private string _filePath = string.Empty;
    private string _contentType = string.Empty;
    private string _contentDisposition = string.Empty;
    private string _safetyWarning = string.Empty;
    private string _errorText = string.Empty;
    private long _bytesReceived;
    private long _totalBytesToReceive;
    private double _bytesPerSecond;
    private double _remainingSeconds;
    private double? _progressPercentOverride;
    private bool _canResumeOperation;
    private bool _isDangerous;
    private DownloadItemState _state = DownloadItemState.Pending;
    private DownloadFileKind _kind = DownloadFileKind.Other;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private DateTimeOffset? _completedAt;
    private string _detailOverrideText = string.Empty;
    private int? _externalExitCode;
    private string _lastOutputText = string.Empty;
    private string _externalToolName = string.Empty;
    private string _failureReason = string.Empty;
    private string _failureSuggestion = string.Empty;
    private bool _isLiveStreamDownload;
    private bool _isFinalizingExternalSegment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceUrl
    {
        get => _sourceUrl;
        set => SetField(ref _sourceUrl, value ?? string.Empty);
    }

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetField(ref _fileName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsYtDlpDownload));
                OnPropertyChanged(nameof(CanSaveAndContinue));
            }
        }
    }

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetField(ref _filePath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(CanOpenFolder));
            }
        }
    }

    public string ContentType
    {
        get => _contentType;
        set => SetField(ref _contentType, value ?? string.Empty);
    }

    public string ContentDisposition
    {
        get => _contentDisposition;
        set => SetField(ref _contentDisposition, value ?? string.Empty);
    }

    public DownloadFileKind Kind
    {
        get => _kind;
        set
        {
            if (SetField(ref _kind, value))
            {
                OnPropertyChanged(nameof(KindText));
            }
        }
    }

    public DownloadItemState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanPauseOrResume));
                OnPropertyChanged(nameof(CanOpen));
                OnPropertyChanged(nameof(CanOpenFolder));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanRemoveHistory));
                OnPropertyChanged(nameof(PauseResumeButtonText));
                OnPropertyChanged(nameof(CanSaveAndContinue));
            }
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (SetField(ref _bytesReceived, Math.Max(0, value)))
            {
                OnProgressPropertyChanged();
            }
        }
    }

    public long TotalBytesToReceive
    {
        get => _totalBytesToReceive;
        set
        {
            if (SetField(ref _totalBytesToReceive, Math.Max(0, value)))
            {
                OnProgressPropertyChanged();
            }
        }
    }

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set
        {
            if (SetField(ref _bytesPerSecond, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(SpeedText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public double RemainingSeconds
    {
        get => _remainingSeconds;
        set
        {
            if (SetField(ref _remainingSeconds, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(RemainingTimeText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public double? ProgressPercentOverride
    {
        get => _progressPercentOverride;
        set
        {
            var normalized = value is null
                ? (double?)null
                : Math.Clamp(value.Value, 0, 100);
            if (SetField(ref _progressPercentOverride, normalized))
            {
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string DetailOverrideText
    {
        get => _detailOverrideText;
        set
        {
            if (SetField(ref _detailOverrideText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public bool CanResumeOperation
    {
        get => _canResumeOperation;
        set
        {
            if (SetField(ref _canResumeOperation, value))
            {
                OnPropertyChanged(nameof(CanPauseOrResume));
                OnPropertyChanged(nameof(PauseResumeButtonText));
            }
        }
    }

    public bool IsDangerous
    {
        get => _isDangerous;
        set => SetField(ref _isDangerous, value);
    }

    public string SafetyWarning
    {
        get => _safetyWarning;
        set => SetField(ref _safetyWarning, value ?? string.Empty);
    }

    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (SetField(ref _errorText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public DateTimeOffset StartedAt
    {
        get => _startedAt;
        set => SetField(ref _startedAt, value);
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    public int? ExternalExitCode
    {
        get => _externalExitCode;
        set => SetField(ref _externalExitCode, value);
    }

    public string LastOutputText
    {
        get => _lastOutputText;
        set => SetField(ref _lastOutputText, value ?? string.Empty);
    }

    public string ExternalToolName
    {
        get => _externalToolName;
        set
        {
            if (SetField(ref _externalToolName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsYtDlpDownload));
                OnPropertyChanged(nameof(CanSaveAndContinue));
            }
        }
    }

    public bool IsLiveStreamDownload
    {
        get => _isLiveStreamDownload;
        set
        {
            if (SetField(ref _isLiveStreamDownload, value))
            {
                OnPropertyChanged(nameof(CanSaveAndContinue));
            }
        }
    }

    public bool IsFinalizingExternalSegment
    {
        get => _isFinalizingExternalSegment;
        set
        {
            if (SetField(ref _isFinalizingExternalSegment, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanPauseOrResume));
                OnPropertyChanged(nameof(CanSaveAndContinue));
            }
        }
    }

    public string FailureReason
    {
        get => _failureReason;
        set
        {
            if (SetField(ref _failureReason, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public string FailureSuggestion
    {
        get => _failureSuggestion;
        set
        {
            if (SetField(ref _failureSuggestion, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (ProgressPercentOverride is { } progress)
            {
                return progress;
            }

            if (TotalBytesToReceive <= 0)
            {
                return State == DownloadItemState.Completed ? 100 : 0;
            }

            return Math.Clamp(BytesReceived * 100.0 / TotalBytesToReceive, 0, 100);
        }
    }

    public string StateText => IsFinalizingExternalSegment
        ? "保存処理中"
        : State switch
        {
            DownloadItemState.Pending => "待機中",
            DownloadItemState.InProgress => "ダウンロード中",
            DownloadItemState.Paused => "一時停止",
            DownloadItemState.Completed => "完了",
            DownloadItemState.Canceled => "キャンセル",
            DownloadItemState.Failed => string.IsNullOrWhiteSpace(ErrorText) ? "失敗" : "失敗: " + ErrorText,
            _ => State.ToString()
        };

    public string KindText => Kind switch
    {
        DownloadFileKind.Pdf => "PDF",
        DownloadFileKind.Image => "画像",
        DownloadFileKind.Archive => "圧縮",
        DownloadFileKind.Executable => "実行形式",
        DownloadFileKind.Video => "動画",
        _ => "その他"
    };

    public string ProgressText => ProgressPercentOverride is not null
        ? $"{ProgressPercent:0}%"
        : TotalBytesToReceive > 0
        ? $"{ProgressPercent:0}%"
        : State == DownloadItemState.Completed ? "100%" : "サイズ不明";

    public string SizeText => TotalBytesToReceive > 0
        ? $"{FormatBytes(BytesReceived)} / {FormatBytes(TotalBytesToReceive)}"
        : FormatBytes(BytesReceived);

    public string SpeedText => BytesPerSecond > 0
        ? $"{FormatBytes((long)BytesPerSecond)}/s"
        : "-";

    public string RemainingTimeText => RemainingSeconds > 0
        ? FormatRemainingTime(RemainingSeconds)
        : "-";

    public string DetailText => !string.IsNullOrWhiteSpace(DetailOverrideText)
        ? DetailOverrideText
        : $"{KindText}  {SizeText}  {SpeedText}  残り {RemainingTimeText}";

    public bool CanCancel => !IsFinalizingExternalSegment
        && State is DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused;

    public bool CanSaveAndContinue => false;

    public bool CanPauseOrResume => !IsFinalizingExternalSegment
        && (State == DownloadItemState.InProgress || (State == DownloadItemState.Paused && CanResumeOperation));

    public string PauseResumeButtonText => State == DownloadItemState.Paused ? "再開" : "一時停止";

    public bool CanOpen => State is DownloadItemState.Completed or DownloadItemState.Canceled
        && File.Exists(FilePath);

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(FilePath);

    public bool CanRetry => !IsYtDlpDownload
        && (State is DownloadItemState.Failed or DownloadItemState.Canceled);

    public bool IsYtDlpDownload => string.Equals(ExternalToolName, "yt-dlp", StringComparison.OrdinalIgnoreCase)
        || FileName.StartsWith("yt-dlp:", StringComparison.OrdinalIgnoreCase);

    public bool CanRemoveHistory => State is not DownloadItemState.Pending and not DownloadItemState.InProgress and not DownloadItemState.Paused;

    public DownloadHistoryEntry ToHistoryEntry()
    {
        return new DownloadHistoryEntry
        {
            Id = Id,
            SourceUrl = SourceUrl,
            ContainerProfileId = ContainerProfile.NormalizeId(ContainerProfileId),
            FileName = FileName,
            FilePath = FilePath,
            ContentType = ContentType,
            ContentDisposition = ContentDisposition,
            Kind = Kind,
            State = State,
            BytesReceived = BytesReceived,
            TotalBytesToReceive = TotalBytesToReceive,
            IsDangerous = IsDangerous,
            SafetyWarning = SafetyWarning,
            ErrorText = ErrorText,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            ProgressPercentOverride = ProgressPercentOverride,
            DetailOverrideText = DetailOverrideText,
            ExternalExitCode = ExternalExitCode,
            LastOutputText = LastOutputText,
            ExternalToolName = ExternalToolName,
            FailureReason = FailureReason,
            FailureSuggestion = FailureSuggestion
        };
    }

    public static DownloadItem FromHistoryEntry(DownloadHistoryEntry entry)
    {
        return new DownloadItem
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            SourceUrl = entry.SourceUrl,
            ContainerProfileId = ContainerProfile.NormalizeId(entry.ContainerProfileId),
            FileName = entry.FileName,
            FilePath = entry.FilePath,
            ContentType = entry.ContentType,
            ContentDisposition = entry.ContentDisposition,
            Kind = entry.Kind,
            State = entry.State is DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused
                ? DownloadItemState.Failed
                : entry.State,
            BytesReceived = entry.BytesReceived,
            TotalBytesToReceive = entry.TotalBytesToReceive,
            IsDangerous = entry.IsDangerous,
            SafetyWarning = entry.SafetyWarning,
            ErrorText = entry.State is DownloadItemState.Pending or DownloadItemState.InProgress or DownloadItemState.Paused
                ? "アプリ終了で中断"
                : entry.ErrorText,
            StartedAt = entry.StartedAt == default ? DateTimeOffset.Now : entry.StartedAt,
            CompletedAt = entry.CompletedAt,
            ProgressPercentOverride = entry.ProgressPercentOverride,
            DetailOverrideText = entry.DetailOverrideText,
            ExternalExitCode = entry.ExternalExitCode,
            LastOutputText = entry.LastOutputText,
            ExternalToolName = entry.ExternalToolName,
            FailureReason = entry.FailureReason,
            FailureSuggestion = entry.FailureSuggestion
        };
    }

    private void OnProgressPropertyChanged()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DetailText));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value} {units[unitIndex]}"
            : $"{displayValue:0.0} {units[unitIndex]}";
    }

    private static string FormatRemainingTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(1, seconds));
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}時間{time.Minutes}分";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{time.Minutes}分{time.Seconds}秒";
        }

        return $"{time.Seconds}秒";
    }
}

public sealed class DownloadHistoryEntry
{
    public Guid Id { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public string ContainerProfileId { get; set; } = ContainerProfile.DefaultId;

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string ContentDisposition { get; set; } = string.Empty;

    public DownloadFileKind Kind { get; set; } = DownloadFileKind.Other;

    public DownloadItemState State { get; set; } = DownloadItemState.Failed;

    public long BytesReceived { get; set; }

    public long TotalBytesToReceive { get; set; }

    public bool IsDangerous { get; set; }

    public string SafetyWarning { get; set; } = string.Empty;

    public string ErrorText { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public double? ProgressPercentOverride { get; set; }

    public string DetailOverrideText { get; set; } = string.Empty;

    public int? ExternalExitCode { get; set; }

    public string LastOutputText { get; set; } = string.Empty;

    public string ExternalToolName { get; set; } = string.Empty;

    public string FailureReason { get; set; } = string.Empty;

    public string FailureSuggestion { get; set; } = string.Empty;
}
