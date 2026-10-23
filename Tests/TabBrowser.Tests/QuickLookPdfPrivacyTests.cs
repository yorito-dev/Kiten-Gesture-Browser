using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class QuickLookPdfPrivacyTests
{
    [Fact]
    public async Task FailedInitializationStillCleansOnlyItsOwnPreviewWhileAnotherRemainsOpen()
    {
        var deleted = new List<Guid>();
        QuickLookPdfEnvironmentScope Create() => new(true,
            () => new PrivateBrowsingContext(Guid.NewGuid(), "test-only"),
            (context, _) =>
            {
                deleted.Add(context.Id);
                return Task.FromResult(new PrivateBrowsingDeleteResult(PrivateBrowsingDeleteStatus.Deleted, 1));
            });
        var first = Create();
        var second = Create();
        Assert.NotEqual(first.ProfileName, second.ProfileName);
        await first.ReleaseAsync(Task.FromException(new IOException("初期化失敗")), () => { });
        Assert.Equal(new[] { first.Context!.Id }, deleted);
        await second.ReleaseAsync(Task.CompletedTask, () => { });
        Assert.Equal(new[] { first.Context.Id, second.Context!.Id }, deleted);
    }

    [Fact]
    public async Task ClosingDuringInitializationReleasesResourcesBeforeDeletingOnlyItsOwnContext()
    {
        var owned = new PrivateBrowsingContext(Guid.NewGuid(), "test-owned-context");
        var events = new List<string>();
        var scope = new QuickLookPdfEnvironmentScope(true, () => owned, (context, _) =>
        {
            Assert.Same(owned, context);
            events.Add("delete");
            return Task.FromResult(new PrivateBrowsingDeleteResult(PrivateBrowsingDeleteStatus.Deleted, 1));
        });
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = scope.ReleaseAsync(initialization.Task, () => events.Add("release"));
        Assert.Same(closed, scope.ReleaseAsync(initialization.Task, () => throw new Exception("二重解放")));
        Assert.Empty(events);
        initialization.SetResult();
        await closed;
        Assert.Equal(new[] { "release", "delete" }, events);
    }

    [Fact]
    public async Task NormalPreviewNeverCreatesOrDeletesPrivateContext()
    {
        var scope = new QuickLookPdfEnvironmentScope(false,
            () => throw new Exception("通常プレビューに一時領域は不要"),
            (_, _) => throw new Exception("共有領域を削除してはいけない"));
        Assert.Null(scope.Context);
        Assert.Null(scope.ProfileName);
        var released = 0;
        await scope.ReleaseAsync(Task.CompletedTask, () => released++);
        Assert.Equal(1, released);
    }

    [Fact]
    public void PrivatePreviewRejectsNormalOrDifferentProfileBeforeNavigation()
    {
        var scope = new QuickLookPdfEnvironmentScope(true,
            () => new PrivateBrowsingContext(Guid.NewGuid(), "test-only"));
        Assert.Throws<InvalidOperationException>(() => scope.VerifyProfile(false, scope.ProfileName!));
        Assert.Throws<InvalidOperationException>(() => scope.VerifyProfile(true, "another-preview"));
        scope.VerifyProfile(true, scope.ProfileName!);
    }
}
