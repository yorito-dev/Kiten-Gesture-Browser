using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SitePrivacyTargetCoordinatorTests
{
    [Fact]
    public void SwitchingOrRefreshingInvalidatesConfirmationEvenForSameContainer()
    {
        using var targets = new SitePrivacyTargetCoordinator<object>();
        var a = targets.BeginLoad();
        targets.SetTarget(a, "A", new object(), () => { });
        var b = targets.BeginLoad();
        targets.SetTarget(b, "B", new object(), () => { });
        Assert.Null(targets.Acquire(a, "A"));
        Assert.Null(targets.Acquire(b, "A"));
        using var valid = targets.Acquire(b, "B");
        Assert.NotNull(valid);
        var refreshed = targets.BeginLoad();
        targets.SetTarget(refreshed, "B", new object(), () => { });
        Assert.Null(targets.Acquire(b, "B"));
    }

    [Fact]
    public async Task DelayedLoadCannotReplaceNewContainerAndActiveOperationKeepsItsOwnerAlive()
    {
        using var targets = new SitePrivacyTargetCoordinator<object>();
        var a = targets.BeginLoad();
        var disposedA = false;
        var valueA = new object();
        targets.SetTarget(a, "A", valueA, () => disposedA = true);
        using var operation = targets.Acquire(a, "A")!;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ReadOldPermissions()
        {
            await pending.Task;
            Assert.Same(valueA, operation.Value);
            return targets.IsCurrent(operation);
        }
        var read = ReadOldPermissions();
        var b = targets.BeginLoad();
        targets.SetTarget(b, "B", new object(), () => { });
        Assert.False(disposedA);
        var disposedStale = false;
        Assert.False(targets.SetTarget(a, "A", new object(), () => disposedStale = true));
        Assert.True(disposedStale);
        pending.SetResult();
        Assert.False(await read);
        targets.Dispose();
        Assert.False(disposedA);
        operation.Dispose();
        Assert.True(disposedA);
    }
}
