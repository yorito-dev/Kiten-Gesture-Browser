using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class DownloadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _historyFile;
    private readonly string _backupFile;

    public DownloadStore()
        : this(AppPaths.DownloadHistoryFile)
    {
    }

    public DownloadStore(string historyFile)
    {
        _historyFile = historyFile;
        _backupFile = AtomicFileService.CreateBackupFilePath(historyFile);
    }

    public IReadOnlyList<DownloadHistoryEntry> Load()
    {
        AtomicFileService.EnsureDirectory(_historyFile);
        if (!File.Exists(_historyFile))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_historyFile);
            return JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(stream, JsonOptions) ?? [];
        }
        catch
        {
            return TryLoadBackup() ?? [];
        }
    }

    public void Save(IEnumerable<DownloadHistoryEntry> entries)
    {
        AtomicFileService.Write(
            _historyFile,
            _backupFile,
            stream => JsonSerializer.Serialize(stream, entries.ToList(), JsonOptions));
    }

    private IReadOnlyList<DownloadHistoryEntry>? TryLoadBackup()
    {
        try
        {
            if (!File.Exists(_backupFile))
            {
                return null;
            }

            using var stream = File.OpenRead(_backupFile);
            return JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
