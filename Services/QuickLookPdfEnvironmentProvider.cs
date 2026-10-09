using System.IO;
using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

public enum QuickLookPdfEnvironmentOwner
{
    Process
}

public enum QuickLookPdfEnvironmentState
{
    Empty,
    Creating,
    Ready,
    Faulted
}

public sealed record QuickLookPdfEnvironmentDiagnosticSnapshot(
    QuickLookPdfEnvironmentOwner Owner,
    QuickLookPdfEnvironmentState State,
    int CreationAttemptCount);

internal sealed class QuickLookPdfEnvironmentProvider
{
    private readonly object _gate = new();
    private readonly Func<Task<CoreWebView2Environment>> _environmentFactory;
    private Task<CoreWebView2Environment>? _environmentTask;
    private QuickLookPdfEnvironmentState _state;
    private int _creationAttemptCount;

    public QuickLookPdfEnvironmentProvider(Func<Task<CoreWebView2Environment>> environmentFactory)
    {
        ArgumentNullException.ThrowIfNull(environmentFactory);
        _environmentFactory = environmentFactory;
    }

    public static QuickLookPdfEnvironmentProvider Shared { get; } = new(CreateEnvironmentAsync);

    public async Task<CoreWebView2Environment> GetAsync()
    {
        Task<CoreWebView2Environment> environmentTask;
        lock (_gate)
        {
            if (_environmentTask is null)
            {
                _state = QuickLookPdfEnvironmentState.Creating;
                _creationAttemptCount++;
                _environmentTask = CreateEnvironmentTask();
            }

            environmentTask = _environmentTask;
        }

        try
        {
            var environment = await environmentTask.ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_environmentTask, environmentTask))
                {
                    _state = QuickLookPdfEnvironmentState.Ready;
                }
            }

            return environment;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_environmentTask, environmentTask))
                {
                    _environmentTask = null;
                    _state = QuickLookPdfEnvironmentState.Faulted;
                }
            }

            throw;
        }
    }

    public QuickLookPdfEnvironmentDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (_gate)
        {
            return new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                _state,
                _creationAttemptCount);
        }
    }

    private Task<CoreWebView2Environment> CreateEnvironmentTask()
    {
        try
        {
            return _environmentFactory();
        }
        catch (Exception ex)
        {
            return Task.FromException<CoreWebView2Environment>(ex);
        }
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        Directory.CreateDirectory(AppPaths.QuickLookWebView2UserDataFolder);
        return CoreWebView2Environment.CreateAsync(
            userDataFolder: AppPaths.QuickLookWebView2UserDataFolder,
            options: WebViewLifecycleService.CreateEnvironmentOptions(enableBrowserExtensions: false));
    }
}
