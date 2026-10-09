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
        try
        {
            if (!File.Exists(_storeFile))
            {
                return [];
            }

            using var stream = File.OpenRead(_storeFile);
            var items = JsonSerializer.Deserialize<List<SplitViewModel>>(stream, JsonOptions);
            return Normalize(items);
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<SplitViewModel> splitViews)
    {
        AtomicFileService.EnsureDirectory(_storeFile);
        var normalized = Normalize(splitViews);
        AtomicFileService.Write(
            _storeFile,
            AtomicFileService.CreateBackupFilePath(_storeFile),
            stream => JsonSerializer.Serialize(stream, normalized, JsonOptions));
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
