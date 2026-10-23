using Microsoft.Web.WebView2.Core;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class QuickLookPdfEnvironmentProviderTests
{
    [Fact]
    public async Task ConcurrentRequestsShareOneProcessCreationAttempt()
    {
        var creation = new TaskCompletionSource<CoreWebView2Environment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCallCount = 0;
        var provider = new QuickLookPdfEnvironmentProvider(() =>
        {
            factoryCallCount++;
            return creation.Task;
        });

        Assert.Equal(
            new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                QuickLookPdfEnvironmentState.Empty,
                0),
            provider.GetDiagnosticSnapshot());

        var first = provider.GetAsync();
        var second = provider.GetAsync();

        Assert.Equal(1, factoryCallCount);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(
            new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                QuickLookPdfEnvironmentState.Creating,
                1),
            provider.GetDiagnosticSnapshot());

        creation.SetResult(null!);
        Assert.Null(await first);
        Assert.Null(await second);
        Assert.Equal(QuickLookPdfEnvironmentState.Ready, provider.GetDiagnosticSnapshot().State);
    }

    [Fact]
    public async Task OlderFailedAwaiterCannotClearNewerCreationAttempt()
    {
        var firstCreation = new TaskCompletionSource<CoreWebView2Environment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCreation = new TaskCompletionSource<CoreWebView2Environment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCallCount = 0;
        var provider = new QuickLookPdfEnvironmentProvider(() =>
            ++factoryCallCount == 1 ? firstCreation.Task : secondCreation.Task);
        var firstAwaiter = provider.GetAsync();
        var delayedOldAwaiter = provider.GetAsync();

        firstCreation.SetException(new InvalidOperationException("first"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstAwaiter);
        var retry = provider.GetAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => delayedOldAwaiter);

        Assert.Equal(
            new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                QuickLookPdfEnvironmentState.Creating,
                2),
            provider.GetDiagnosticSnapshot());

        secondCreation.SetResult(null!);
        Assert.Null(await retry);
        Assert.Equal(QuickLookPdfEnvironmentState.Ready, provider.GetDiagnosticSnapshot().State);
    }

    [Fact]
    public async Task FailedCreationIsClearedByTaskIdentityAndNextRequestRetries()
    {
        var factoryCallCount = 0;
        var provider = new QuickLookPdfEnvironmentProvider(() =>
        {
            factoryCallCount++;
            return factoryCallCount == 1
                ? Task.FromException<CoreWebView2Environment>(new InvalidOperationException("first"))
                : Task.FromResult<CoreWebView2Environment>(null!);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(provider.GetAsync);
        Assert.Equal("first", exception.Message);
        Assert.Equal(
            new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                QuickLookPdfEnvironmentState.Faulted,
                1),
            provider.GetDiagnosticSnapshot());

        Assert.Null(await provider.GetAsync());
        Assert.Equal(2, factoryCallCount);
        Assert.Equal(
            new QuickLookPdfEnvironmentDiagnosticSnapshot(
                QuickLookPdfEnvironmentOwner.Process,
                QuickLookPdfEnvironmentState.Ready,
                2),
            provider.GetDiagnosticSnapshot());
    }

    [Fact]
    public void QuickLookViewUsesProviderAndProviderKeepsDedicatedEnvironmentPolicy()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "Views", "QuickLookPdfView.xaml.cs"));
        var provider = File.ReadAllText(Path.Combine(
            root,
            "Services",
            "QuickLookPdfEnvironmentProvider.cs"));

        Assert.Contains("_environmentScope.GetAsync()", view);
        Assert.Contains("_context is null ? QuickLookPdfEnvironmentProvider.Shared", provider);
        Assert.Contains("userDataFolder: _context.UserDataFolder", provider);
        Assert.Contains("options.IsInPrivateModeEnabled = true", provider);
        Assert.DoesNotContain("static Task<CoreWebView2Environment>", view);
        Assert.DoesNotContain("EnvironmentGate", view);
        Assert.Contains("AppPaths.QuickLookWebView2UserDataFolder", provider);
        Assert.Contains("enableBrowserExtensions: false", provider);
        Assert.Contains("ReferenceEquals(_environmentTask, environmentTask)", provider);
        var environmentAwait = view.IndexOf(
            "await _environmentScope.GetAsync()",
            StringComparison.Ordinal);
        var firstCancellationCheck = view.IndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            environmentAwait,
            StringComparison.Ordinal);
        var ensureCore = view.IndexOf(
            "await PdfWebView.EnsureCoreWebView2Async(environment, _environmentScope.CreateControllerOptions(environment));",
            firstCancellationCheck,
            StringComparison.Ordinal);
        var secondCancellationCheck = view.IndexOf(
            "cancellationToken.ThrowIfCancellationRequested();",
            ensureCore,
            StringComparison.Ordinal);
        Assert.True(environmentAwait < firstCancellationCheck);
        Assert.True(firstCancellationCheck < ensureCore);
        Assert.True(ensureCore < secondCancellationCheck);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
