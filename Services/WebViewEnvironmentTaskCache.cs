using System.IO;

namespace TabBrowser.Services;

public sealed class WebViewEnvironmentConfigurationMismatchException : InvalidOperationException
{
    public WebViewEnvironmentConfigurationMismatchException(
        string containerProfileId,
        bool requestedBrowserExtensionsEnabled,
        string existingContainerProfileId,
        bool existingBrowserExtensionsEnabled)
        : base(CreateMessage(
            containerProfileId,
            requestedBrowserExtensionsEnabled,
            existingContainerProfileId,
            existingBrowserExtensionsEnabled))
    {
        ContainerProfileId = containerProfileId;
        RequestedBrowserExtensionsEnabled = requestedBrowserExtensionsEnabled;
        ExistingContainerProfileId = existingContainerProfileId;
        ExistingBrowserExtensionsEnabled = existingBrowserExtensionsEnabled;
    }

    public string ContainerProfileId { get; }

    public bool RequestedBrowserExtensionsEnabled { get; }

    public string ExistingContainerProfileId { get; }

    public bool ExistingBrowserExtensionsEnabled { get; }

    private static string CreateMessage(
        string containerProfileId,
        bool requestedBrowserExtensionsEnabled,
        string existingContainerProfileId,
        bool existingBrowserExtensionsEnabled)
    {
        return "WebView2環境の拡張機能設定が一致しません。"
            + $" コンテナID={containerProfileId},"
            + $" 要求モード={FormatMode(requestedBrowserExtensionsEnabled)},"
            + $" 既存コンテナID={existingContainerProfileId},"
            + $" 既存モード={FormatMode(existingBrowserExtensionsEnabled)}";
    }

    private static string FormatMode(bool enabled)
    {
        return enabled ? "有効" : "無効";
    }
}

internal sealed class WebViewEnvironmentTaskCache<TEnvironment>
    where TEnvironment : class
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Task<TEnvironment> GetOrCreate(
        string userDataFolder,
        string containerProfileId,
        bool enableBrowserExtensions,
        object owner,
        Func<Task<TEnvironment>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerProfileId);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(factory);

        var cacheKey = NormalizeUserDataFolder(userDataFolder);
        CacheEntry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(cacheKey, out var existingEntry))
            {
                if (existingEntry.EnableBrowserExtensions != enableBrowserExtensions)
                {
                    throw new WebViewEnvironmentConfigurationMismatchException(
                        containerProfileId,
                        enableBrowserExtensions,
                        existingEntry.ContainerProfileId,
                        existingEntry.EnableBrowserExtensions);
                }

                existingEntry.Owners.Add(owner);
                return existingEntry.Task;
            }

            var completion = new TaskCompletionSource<TEnvironment>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new CacheEntry(
                containerProfileId,
                enableBrowserExtensions,
                completion);
            entry.Owners.Add(owner);
            _entries.Add(cacheKey, entry);
        }

        _ = CompleteCreationAsync(cacheKey, entry, factory);
        return entry.Task;
    }

    public void ReleaseOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_gate)
        {
            foreach (var pair in _entries.ToList())
            {
                pair.Value.Owners.Remove(owner);
                if (pair.Value.IsCompleted && pair.Value.Owners.Count == 0)
                {
                    _entries.Remove(pair.Key);
                }
            }
        }
    }

    private static string NormalizeUserDataFolder(string userDataFolder)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataFolder));
    }

    private async Task CompleteCreationAsync(
        string cacheKey,
        CacheEntry entry,
        Func<Task<TEnvironment>> factory)
    {
        TEnvironment environment;
        try
        {
            environment = await factory().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(cacheKey, out var currentEntry)
                    && ReferenceEquals(currentEntry, entry))
                {
                    _entries.Remove(cacheKey);
                }
            }

            entry.Completion.TrySetException(ex);
            return;
        }

        lock (_gate)
        {
            entry.IsCompleted = true;
            if (entry.Owners.Count == 0
                && _entries.TryGetValue(cacheKey, out var currentEntry)
                && ReferenceEquals(currentEntry, entry))
            {
                _entries.Remove(cacheKey);
            }
        }

        entry.Completion.TrySetResult(environment);
    }

    private sealed class CacheEntry(
        string containerProfileId,
        bool enableBrowserExtensions,
        TaskCompletionSource<TEnvironment> completion)
    {
        public string ContainerProfileId { get; } = containerProfileId;

        public bool EnableBrowserExtensions { get; } = enableBrowserExtensions;

        public TaskCompletionSource<TEnvironment> Completion { get; } = completion;

        public Task<TEnvironment> Task => Completion.Task;

        public HashSet<object> Owners { get; } = new(ReferenceEqualityComparer.Instance);

        public bool IsCompleted { get; set; }
    }
}
