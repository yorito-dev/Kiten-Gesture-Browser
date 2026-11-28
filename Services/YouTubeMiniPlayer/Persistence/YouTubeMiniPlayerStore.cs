using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class YouTubeMiniPlayerStore
{
    public const int ClosedHistoryLimit = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly object FileGate = new();

    private readonly string _storeFile;
    private readonly string _backupFile;
    private readonly HashSet<Guid> _explicitlyClosed = [];
    private bool _recoveryPending;
    private StartupRestoreState _startupRestoreState;
    private HashSet<Guid>? _unrestoredPlayerIds;

    public YouTubeMiniPlayerStore()
        : this(AppPaths.YouTubeMiniPlayersFile)
    {
    }

    public YouTubeMiniPlayerStore(string storeFile)
    {
        _storeFile = storeFile;
        _backupFile = AtomicFileService.CreateBackupFilePath(storeFile);
    }

    public List<YouTubeMiniPlayerState> Load() => Load(forStartup: false);

    internal List<YouTubeMiniPlayerState> LoadForStartup() => Load(forStartup: true);

    private List<YouTubeMiniPlayerState> Load(bool forStartup)
    {
        lock (FileGate)
        {
            if (forStartup && _startupRestoreState == StartupRestoreState.NotStarted)
            {
                _startupRestoreState = StartupRestoreState.Restoring;
            }

            var primaryState = Read(_storeFile, out var players);
            if (primaryState == ReadState.Loaded)
            {
                return players.Where(player => !_explicitlyClosed.Contains(player.Id)).ToList();
            }

            var backupState = Read(_backupFile, out var backup);
            _recoveryPending |= primaryState != ReadState.Missing || backupState != ReadState.Missing;
            return backup.Where(player => !_explicitlyClosed.Contains(player.Id)).ToList();
        }
    }

    internal void CompleteStartupRestore()
    {
        lock (FileGate)
        {
            _startupRestoreState = StartupRestoreState.Completed;
        }
    }

    internal void RecordExplicitClose(Guid id)
    {
        lock (FileGate)
        {
            _explicitlyClosed.Add(id);
        }
    }

    internal void RecordReopened(Guid id)
    {
        lock (FileGate)
        {
            _explicitlyClosed.Remove(id);
        }
    }

    public void Save(IEnumerable<YouTubeMiniPlayerState> players)
    {
        var normalized = Normalize(players);
        lock (FileGate)
        {
            var primaryState = Read(_storeFile, out var primary);
            if (primaryState == ReadState.Loaded)
            {
                WriteSnapshot(primary, normalized, _backupFile);
                // この保存で作ったバックアップに載る閉鎖だけを引き継ぐ。
                _explicitlyClosed.IntersectWith(primary.Select(static player => player.Id));
                _recoveryPending = false;
                return;
            }

            var backupState = Read(_backupFile, out var backup);
            if (primaryState == ReadState.Missing && backupState == ReadState.Missing && !_recoveryPending)
            {
                WriteSnapshot([], normalized, _backupFile);
                _explicitlyClosed.Clear();
                return;
            }

            _recoveryPending = true;
            if (primaryState == ReadState.Unavailable || backupState != ReadState.Loaded)
            {
                throw new IOException("既存のミニプレイヤー状態を確認できないため、保存を保留しました。");
            }

            WriteSnapshot(backup, normalized, backupFile: null);
            _explicitlyClosed.IntersectWith(backup.Select(static player => player.Id));
            _recoveryPending = false;
        }
    }

    private void WriteSnapshot(
        List<YouTubeMiniPlayerState> stored, List<YouTubeMiniPlayerState> snapshot, string? backupFile)
    {
        if (!_recoveryPending && _startupRestoreState == StartupRestoreState.NotStarted)
        {
            Write(snapshot, backupFile);
            return;
        }

        var accountedFor = snapshot.Select(static player => player.Id).ToHashSet();
        accountedFor.UnionWith(_explicitlyClosed);
        if (_startupRestoreState != StartupRestoreState.Completed)
        {
            if ((_recoveryPending && _startupRestoreState == StartupRestoreState.Restoring)
                || stored.Exists(player => !accountedFor.Contains(player.Id)))
            {
                throw new IOException("復元待ちのミニプレイヤーがあるため、保存を保留しました。");
            }

            Write(snapshot, backupFile);
            return;
        }

        // 復元先のない記録は、次の通常保存や終了前保存でも引き継ぐ。
        var unrestored = stored.Where(player => !accountedFor.Contains(player.Id)
            && (_recoveryPending || _unrestoredPlayerIds is null || _unrestoredPlayerIds.Contains(player.Id)))
            .ToList();
        snapshot.AddRange(unrestored);
        Write(snapshot, backupFile);
        _unrestoredPlayerIds = unrestored.Select(static player => player.Id).ToHashSet();
    }

    private void Write(List<YouTubeMiniPlayerState> players, string? backupFile)
        => AtomicFileService.Write(
            _storeFile,
            backupFile,
            stream => JsonSerializer.Serialize(stream, players, JsonOptions));

    private static ReadState Read(string path, out List<YouTubeMiniPlayerState> players)
    {
        players = [];
        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize<List<YouTubeMiniPlayerState>>(stream, JsonOptions) is not { } stored
                || stored.Any(static player => player is null))
            {
                return ReadState.Corrupt;
            }

            players = Normalize(stored);
            return ReadState.Loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadState.Missing;
        }
        catch (JsonException)
        {
            return ReadState.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReadState.Unavailable;
        }
    }

    private enum StartupRestoreState
    {
        NotStarted,
        Restoring,
        Completed
    }

    private enum ReadState
    {
        Missing,
        Loaded,
        Corrupt,
        Unavailable
    }

    public static List<YouTubeMiniPlayerState> NormalizeClosedHistory(
        IEnumerable<YouTubeMiniPlayerState>? players)
    {
        return Normalize(players)
            .OrderByDescending(static x => x.UpdatedAt)
            .Take(ClosedHistoryLimit)
            .OrderBy(static x => x.UpdatedAt)
            .ToList();
    }

    private static List<YouTubeMiniPlayerState> Normalize(IEnumerable<YouTubeMiniPlayerState>? players)
    {
        var normalized = players?
            .Where(static x => x.SourceKind == MiniPlayerSourceKind.LocalFile
                ? !string.IsNullOrWhiteSpace(x.LocalFilePath)
                : !string.IsNullOrWhiteSpace(x.Url))
            .Select(static x =>
            {
                x.Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id;
                x.ContainerProfileId = ContainerProfile.NormalizeId(x.ContainerProfileId);
                if (x.SourceKind == MiniPlayerSourceKind.LocalFile)
                {
                    x.LocalFilePath = x.LocalFilePath.Trim();
                    x.Url = string.Empty;
                    x.Title = string.IsNullOrWhiteSpace(x.Title)
                        ? Path.GetFileNameWithoutExtension(x.LocalFilePath)
                        : x.Title.Trim();
                    x.IsShorts = false;
                }
                else
                {
                    x.LocalFilePath = string.Empty;
                    x.Title = string.IsNullOrWhiteSpace(x.Title) ? "YouTube" : x.Title.Trim();
                    x.IsShorts |= YouTubeUrlService.IsShortsUrl(x.Url);
                }

                if (x.VideoAspectRatio is not { } ratio
                    || !double.IsFinite(ratio)
                    || ratio <= 0)
                {
                    x.VideoAspectRatio = null;
                }

                x.Width = Math.Max(260, x.Width);
                x.Height = Math.Max(170, x.Height);
                if (x.Volume.HasValue)
                {
                    x.Volume = Math.Max(0, Math.Min(1, x.Volume.Value));
                }

                x.AudioBoostDecibels = x.SourceKind == MiniPlayerSourceKind.YouTube
                    ? YouTubeMiniPlayerAudioBoostProfile.Normalize(x.AudioBoostDecibels)
                    : 0;

                if (x.PlaybackRate.HasValue)
                {
                    x.PlaybackRate = YouTubeControlScriptService.NormalizePlaybackSpeed(x.PlaybackRate);
                }

                if (x.LastPositionSeconds.HasValue)
                {
                    x.LastPositionSeconds = YouTubeControlScriptService.NormalizePlaybackPosition(x.LastPositionSeconds);
                }

                YouTubeMiniPlayerAbRepeatState.Normalize(x);
                YouTubeMiniPlayerContentTransformState.Normalize(x);
                YouTubeLiveChatWindowState.Normalize(x);

                return x;
            })
            .GroupBy(static x => x.Id)
            .Select(static group => group.First())
            .OrderBy(static x => x.ListOrder ?? int.MaxValue)
            .ThenBy(static x => x.CreatedAt)
            .ToList() ?? [];

        for (var index = 0; index < normalized.Count; index++)
        {
            normalized[index].ListOrder = index;
        }

        return normalized;
    }
}
