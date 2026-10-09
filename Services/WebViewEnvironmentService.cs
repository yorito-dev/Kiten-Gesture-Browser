using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed record WebViewProcessDiagnosticInfo(
    int ProcessId,
    string Kind);

public sealed class WebViewEnvironmentService : IDisposable
{
    private static readonly WebViewEnvironmentTaskCache<CoreWebView2Environment> SharedEnvironmentCache = new();
    private readonly Dictionary<string, EnvironmentTaskRegistration> _environmentTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnvironmentSubscription> _environmentSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string? _userDataFolderOverride;
    private readonly Func<string, bool, Task<CoreWebView2Environment>> _createEnvironmentAsync;
    private bool _isDisposed;

    public WebViewEnvironmentService(string? userDataFolderOverride = null)
        : this(userDataFolderOverride, CreateEnvironmentAsync)
    {
    }

    internal WebViewEnvironmentService(
        string? userDataFolderOverride,
        Func<string, bool, Task<CoreWebView2Environment>> createEnvironmentAsync)
    {
        _userDataFolderOverride = string.IsNullOrWhiteSpace(userDataFolderOverride)
            ? null
            : userDataFolderOverride;
        _createEnvironmentAsync = createEnvironmentAsync
            ?? throw new ArgumentNullException(nameof(createEnvironmentAsync));
    }

    public event EventHandler<WebViewBrowserProcessExit>? BrowserProcessExited;

    public bool HasCreatedEnvironment
    {
        get
        {
            lock (_gate)
            {
                return _environmentTasks.Count > 0;
            }
        }
    }

    public bool HasCreatedEnvironmentFor(string? containerProfileId)
    {
        var normalizedId = _userDataFolderOverride is null
            ? ContainerProfile.NormalizeId(containerProfileId)
            : ContainerProfile.DefaultId;
        lock (_gate)
        {
            return _environmentTasks.ContainsKey(normalizedId);
        }
    }

    public IReadOnlyList<string> GetCreatedContainerProfileIds()
    {
        lock (_gate)
        {
            return _environmentTasks.Keys.ToList();
        }
    }

    public IReadOnlyList<string> GetCreatedBrowserVersionStrings()
    {
        List<CoreWebView2Environment> environments;
        lock (_gate)
        {
            environments = _environmentTasks.Values
                .Select(static registration => registration.Task)
                .Where(static task => task.IsCompletedSuccessfully)
                .Select(static task => task.Result)
                .Distinct()
                .ToList();
        }

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var environment in environments)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(environment.BrowserVersionString))
                {
                    versions.Add(environment.BrowserVersionString.Trim());
                }
            }
            catch
            {
                // 取得中にEnvironmentが終了しても、診断表示と通常のWebView2処理へ影響させない。
            }
        }

        return versions.OrderBy(static version => version, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<WebViewProcessDiagnosticInfo> GetProcessDiagnosticInfos()
    {
        List<CoreWebView2Environment> environments;
        lock (_gate)
        {
            environments = _environmentTasks.Values
                .Select(static registration => registration.Task)
                .Where(static task => task.IsCompletedSuccessfully)
                .Select(static task => task.Result)
                .Distinct()
                .ToList();
        }

        return GetProcessDiagnosticInfos(environments);
    }

    public static IReadOnlyList<WebViewProcessDiagnosticInfo> GetProcessDiagnosticInfos(
        CoreWebView2Environment? environment)
    {
        return environment is null
            ? []
            : GetProcessDiagnosticInfos([environment]);
    }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync(
        string? containerProfileId,
        bool enableBrowserExtensions)
    {
        var normalizedId = _userDataFolderOverride is null
            ? ContainerProfile.NormalizeId(containerProfileId)
            : ContainerProfile.DefaultId;
        var userDataFolder = _userDataFolderOverride ?? GetUserDataFolder(normalizedId);
        Task<CoreWebView2Environment> environmentTask;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_environmentTasks.TryGetValue(normalizedId, out var existingRegistration))
            {
                if (existingRegistration.EnableBrowserExtensions != enableBrowserExtensions)
                {
                    throw new WebViewEnvironmentConfigurationMismatchException(
                        normalizedId,
                        enableBrowserExtensions,
                        normalizedId,
                        existingRegistration.EnableBrowserExtensions);
                }

                environmentTask = existingRegistration.Task;
            }
            else
            {
                environmentTask = SharedEnvironmentCache.GetOrCreate(
                    userDataFolder,
                    normalizedId,
                    enableBrowserExtensions,
                    this,
                    () => _createEnvironmentAsync(userDataFolder, enableBrowserExtensions));
                _environmentTasks[normalizedId] = new EnvironmentTaskRegistration(
                    enableBrowserExtensions,
                    environmentTask);
            }
        }

        try
        {
            var environment = await environmentTask.ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                EnsureEnvironmentSubscriptionLocked(normalizedId, environment);
                return environment;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (_environmentTasks.TryGetValue(normalizedId, out var currentRegistration)
                    && ReferenceEquals(currentRegistration.Task, environmentTask))
                {
                    _environmentTasks.Remove(normalizedId);
                }
            }

            throw;
        }
    }

    public static string GetUserDataFolder(string? containerProfileId)
    {
        return AppPaths.GetContainerProfileWebView2Folder(ContainerProfile.NormalizeId(containerProfileId));
    }

    public void Dispose()
    {
        List<EnvironmentSubscription> subscriptions;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            subscriptions = _environmentSubscriptions.Values.ToList();
            _environmentSubscriptions.Clear();
            _environmentTasks.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Environment.BrowserProcessExited -= subscription.Handler;
        }

        SharedEnvironmentCache.ReleaseOwner(this);
        BrowserProcessExited = null;
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync(
        string userDataFolder,
        bool enableBrowserExtensions)
    {
        return CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder,
            options: WebViewLifecycleService.CreateEnvironmentOptions(enableBrowserExtensions));
    }

    private void EnsureEnvironmentSubscriptionLocked(
        string containerProfileId,
        CoreWebView2Environment environment)
    {
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs> handler = (_, args) =>
        {
            BrowserProcessExited?.Invoke(
                this,
                new WebViewBrowserProcessExit(
                    containerProfileId,
                    args.BrowserProcessId,
                    args.BrowserProcessExitKind,
                    DateTimeOffset.UtcNow));
        };

        if (_environmentSubscriptions.ContainsKey(containerProfileId))
        {
            return;
        }

        environment.BrowserProcessExited += handler;
        _environmentSubscriptions[containerProfileId] = new EnvironmentSubscription(environment, handler);
    }

    private static IReadOnlyList<WebViewProcessDiagnosticInfo> GetProcessDiagnosticInfos(
        IEnumerable<CoreWebView2Environment> environments)
    {
        var processes = new Dictionary<int, WebViewProcessDiagnosticInfo>();
        foreach (var environment in environments)
        {
            try
            {
                foreach (var processInfo in environment.GetProcessInfos())
                {
                    if (processInfo.ProcessId <= 0)
                    {
                        continue;
                    }

                    processes.TryAdd(
                        processInfo.ProcessId,
                        new WebViewProcessDiagnosticInfo(
                            processInfo.ProcessId,
                            processInfo.Kind.ToString()));
                }
            }
            catch
            {
                // 診断取得中にプロセスが終了しても通常のWebView2処理へ影響させない。
            }
        }

        return processes.Values.ToList();
    }

    private sealed record EnvironmentSubscription(
        CoreWebView2Environment Environment,
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs> Handler);

    private sealed record EnvironmentTaskRegistration(
        bool EnableBrowserExtensions,
        Task<CoreWebView2Environment> Task);
}
