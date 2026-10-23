using System.IO;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

public enum QuickLookPdfEnvironmentOwner
{
    Process
}

// PDFプレビュー単位の一時領域。通常利用の共有環境や他のWindowの領域を削除しない。
internal sealed class QuickLookPdfEnvironmentScope
{
    private readonly PrivateBrowsingContext? _context;
    private readonly QuickLookPdfEnvironmentProvider _provider;
    private readonly Func<PrivateBrowsingContext, CancellationToken, Task<PrivateBrowsingDeleteResult>> _delete;
    private readonly PrivateBrowsingShutdownCoordinator _shutdown = new();
    private Task? _releaseTask;

    public QuickLookPdfEnvironmentScope(bool isPrivate,
        Func<PrivateBrowsingContext>? createContext = null,
        Func<PrivateBrowsingContext, CancellationToken, Task<PrivateBrowsingDeleteResult>>? delete = null)
    {
        _delete = delete ?? PrivateBrowsingDataService.DeleteContextAsync;
        _context = isPrivate ? (createContext ?? PrivateBrowsingDataService.CreateContext)() : null;
        _provider = _context is null ? QuickLookPdfEnvironmentProvider.Shared
            : new QuickLookPdfEnvironmentProvider(() => CoreWebView2Environment.CreateAsync(
                userDataFolder: _context.UserDataFolder,
                options: WebViewLifecycleService.CreateEnvironmentOptions(enableBrowserExtensions: false)));
    }

    internal PrivateBrowsingContext? Context => _context;
    internal string? ProfileName => _context is null ? null : "KitenPrivatePdf-" + _context.Id.ToString("N");
    public Task<CoreWebView2Environment> GetAsync() => _provider.GetAsync();

    public CoreWebView2ControllerOptions? CreateControllerOptions(CoreWebView2Environment environment)
    {
        if (_context is null) return null;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        options.IsInPrivateModeEnabled = true;
        return options;
    }

    internal void VerifyProfile(bool isInPrivate, string profileName)
    {
        if (_context is not null && (!isInPrivate || profileName != ProfileName))
            throw new InvalidOperationException("PDFのプライベートプロファイルを確認できません。");
    }

    public Task ReleaseAsync(Task initialization, Action releaseResources)
        => _releaseTask ??= ReleaseCoreAsync(initialization, releaseResources);

    private async Task ReleaseCoreAsync(Task initialization, Action releaseResources)
    {
        // 初期化完了後に破棄する。初期化中に一時フォルダーを消して再作成させない。
        try { await initialization; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
        if (_context is null)
        {
            releaseResources();
            return;
        }
        var result = await _shutdown.ShutdownAsync(releaseResources,
            token => _delete(_context, token), TimeSpan.FromSeconds(3));
        if (!result.IsSuccess)
            PrivateBrowsingDataService.RecordCleanupFailure(_context, result.FailureReason ?? "pdf-cleanup-failed");
    }
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
