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

    private readonly string _storeFile;

    public YouTubeMiniPlayerStore()
        : this(AppPaths.YouTubeMiniPlayersFile)
    {
    }

    public YouTubeMiniPlayerStore(string storeFile)
    {
        _storeFile = storeFile;
    }

    public List<YouTubeMiniPlayerState> Load()
    {
        try
        {
            if (!File.Exists(_storeFile))
            {
                return [];
            }

            using var stream = File.OpenRead(_storeFile);
            var items = JsonSerializer.Deserialize<List<YouTubeMiniPlayerState>>(stream, JsonOptions);
            return Normalize(items);
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<YouTubeMiniPlayerState> players)
    {
        AtomicFileService.EnsureDirectory(_storeFile);
        var normalized = Normalize(players);
        AtomicFileService.Write(
            _storeFile,
            AtomicFileService.CreateBackupFilePath(_storeFile),
            stream => JsonSerializer.Serialize(stream, normalized, JsonOptions));
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
