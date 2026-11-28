using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class SplitViewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _storeFile;

    public SplitViewStore()
        : this(Path.Combine(AppPaths.Root, "split-views.json"))
    {
    }

    public SplitViewStore(string storeFile)
    {
        _storeFile = storeFile;
    }

    public List<SplitViewModel> Load()
    {
        Read(_storeFile, out var items);
        return items;
    }

    internal (IReadOnlyCollection<SplitViewModel> Current, IReadOnlyCollection<SplitViewModel> Backup) LoadForRestore()
    {
        if (Read(_storeFile, out var current) == ReadState.Loaded)
        {
            return (current, []);
        }

        Read(AtomicFileService.CreateBackupFilePath(_storeFile), out var backup);
        return ([], backup);
    }

    public bool Save(IEnumerable<SplitViewModel> splitViews, bool startupRestoreComplete = false)
    {
        var backupFile = AtomicFileService.CreateBackupFilePath(_storeFile);
        var state = Read(_storeFile, out _);
        var recovering = state is ReadState.Corrupt or ReadState.Unavailable
            || (state == ReadState.Missing && Read(backupFile, out _) != ReadState.Missing);
        // 起動時に復元できた一部の Window の状態で、復旧元を置換しない。
        if (recovering && !startupRestoreComplete)
        {
            return false;
        }

        if (state == ReadState.Unavailable)
        {
            throw new IOException("既存の分割表示の状態を確認できないため、保存を保留しました。");
        }

        var normalized = Normalize(splitViews);
        AtomicFileService.Write(
            _storeFile,
            recovering ? null : backupFile,
            stream => JsonSerializer.Serialize(stream, normalized, JsonOptions));
        return true;
    }

    private static ReadState Read(string path, out List<SplitViewModel> items)
    {
        items = [];
        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize<List<SplitViewModel>>(stream, JsonOptions) is not { } stored)
            {
                return ReadState.Corrupt;
            }

            items = Normalize(stored);
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

    private enum ReadState
    {
        Missing,
        Loaded,
        Corrupt,
        Unavailable
    }

    private static List<SplitViewModel> Normalize(IEnumerable<SplitViewModel>? splitViews)
    {
        return splitViews?
            .Select(SplitViewModelProfile.Normalize)
            .Where(static x => x is not null)
            .Select(static x => x!)
            .GroupBy(static x => x.Id)
            .Select(static group => group.First())
            .ToList() ?? [];
    }
}
