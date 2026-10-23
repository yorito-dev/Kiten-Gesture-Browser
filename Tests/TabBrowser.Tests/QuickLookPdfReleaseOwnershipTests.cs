using System.Reflection;
using System.Windows.Threading;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class QuickLookPdfReleaseOwnershipTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RendererAndOwningWindowWaitForInitializationAndDeletion(bool pendingInitialization, bool forceDeadline)
    {
        await InSta(async () =>
        {
            using var owner = new WindowTaskSupervisor();
            using var otherOwner = new WindowTaskSupervisor();
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleted = new TaskCompletionSource<PrivateBrowsingDeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deletionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = new PrivateBrowsingContext(Guid.NewGuid(), "test-owned-pdf");
            var scope = new QuickLookPdfEnvironmentScope(true, () => context, (actual, _) =>
            {
                Assert.Same(context, actual);
                deletionStarted.SetResult();
                return deleted.Task;
            });
            var view = new QuickLookPdfView("test.pdf", scope, pendingInitialization ? initialized.Task : Task.CompletedTask, owner);
            var renderer = new QuickLookPdfRenderer();
            typeof(QuickLookPdfRenderer).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, view);
            using var host = new QuickLookHost();
            var service = (QuickLookService)typeof(QuickLookHost).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            typeof(QuickLookService).GetField("_currentRenderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, renderer);
            var drain = host.DisposeForWindowShutdownAsync(owner, forceDeadline ? TimeSpan.Zero : Timeout.InfiniteTimeSpan);
            var unload = view.ReleaseCompletion;
            try
            {
                Assert.False(unload.IsCompleted);
                if (forceDeadline) Assert.True((await drain).TimedOut);
                else Assert.False(drain.IsCompleted);
                Assert.False((await otherOwner.DrainAsync(TimeSpan.Zero)).TimedOut);
                if (pendingInitialization) Assert.False(deletionStarted.Task.IsCompleted);
                initialized.TrySetResult();
                await deletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (forceDeadline) Assert.True((await drain).TimedOut);
                else Assert.False(drain.IsCompleted);
                deleted.SetResult(new(PrivateBrowsingDeleteStatus.Deleted, 1));
                await unload;
                Assert.Equal(forceDeadline, (await drain).TimedOut);
                Assert.True(view.ReleaseCompletion.IsCompletedSuccessfully);
            }
            finally
            {
                initialized.TrySetResult(); deleted.TrySetResult(new(PrivateBrowsingDeleteStatus.Deleted, 1));
                await view.ReleaseCompletion;
            }
        });
    }

    [Fact]
    public async Task HostDisposedWhileWaitingForCloseCannotReopenPreview()
    {
        await InSta(async () =>
        {
            using var host = new QuickLookHost();
            var service = (QuickLookService)typeof(QuickLookHost).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var renderer = new PendingRenderer();
            typeof(QuickLookService).GetField("_renderers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new IQuickLookRenderer[] { renderer });
            var request = new TabBrowser.Models.QuickLook.QuickLookRequest([new() { DisplayName = "test", SourceId = "test" }], "test");
            await host.ShowAsync(request);
            host.Close();
            var reopen = host.ShowAsync(request);
            host.Dispose();
            renderer.Unloaded.SetResult();
            await reopen;
            Assert.Equal(1, renderer.RenderCalls);
            Assert.False(host.IsOpen);
        });
    }

    [Fact]
    public async Task DisposalDuringPreviousPreviewUnloadCannotCreateAnotherPreview()
    {
        var service = new QuickLookService(new());
        var renderer = new PendingRenderer();
        typeof(QuickLookService).GetField("_renderers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new IQuickLookRenderer[] { renderer });
        var request = new TabBrowser.Models.QuickLook.QuickLookRequest([new() { DisplayName = "test", SourceId = "test" }], "test");
        await service.LoadAsync(request);
        var load = service.LoadAsync(request);
        service.Dispose();
        renderer.Unloaded.SetResult();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
            Assert.Equal(1, renderer.RenderCalls);
        }
        finally { await service.DisposeAsync(); }
    }

    private sealed class PendingRenderer : IQuickLookRenderer
    {
        public TaskCompletionSource Unloaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RenderCalls { get; private set; }
        public bool CanRender(TabBrowser.Models.QuickLook.QuickLookRequest request) => true;
        public Task<QuickLookRenderResult> RenderAsync(TabBrowser.Models.QuickLook.QuickLookRequest request, CancellationToken cancellationToken)
        {
            RenderCalls++;
            return Task.FromResult(new QuickLookRenderResult(QuickLookRenderKind.Information, "test", "", ""));
        }
        public ValueTask UnloadAsync() => new(Unloaded.Task);
    }

    private static async Task InSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }
}
