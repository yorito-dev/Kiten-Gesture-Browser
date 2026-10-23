using System.Reflection;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Services;
using TabBrowser.Services.QuickLook;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class QuickLookHostPendingShowTests
{
    [Theory]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("already-cancelled")]
    [InlineData("newer")]
    [InlineData("success")]
    public void PendingPdfReleaseOnlyAllowsTheCurrentValidShow(string action)
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            using var owner = new WindowTaskSupervisor();
            using var host = new QuickLookHost();
            using var cancellation = new CancellationTokenSource();
            var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleted = new TaskCompletionSource<PrivateBrowsingDeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scope = new QuickLookPdfEnvironmentScope(true,
                () => new PrivateBrowsingContext(Guid.NewGuid(), "test-pending-pdf"), (_, _) => deleted.Task);
            var pdf = new QuickLookPdfView("test.pdf", scope, initialized.Task, owner);
            var pdfRenderer = new QuickLookPdfRenderer();
            Set(pdfRenderer, "_view", pdf);
            var nextRenderer = new RecordingRenderer();
            var service = Service(host);
            Set(service, "_renderers", new IQuickLookRenderer[] { nextRenderer });
            Set(service, "_currentRenderer", pdfRenderer);
            host.Close();
            if (action == "already-cancelled") cancellation.Cancel();
            var pending = host.ShowAsync(Request("B"), cancellation.Token);
            Task latest = Task.CompletedTask;
            try
            {
                if (action == "close") host.Close();
                if (action == "cancel") cancellation.Cancel();
                if (action == "newer") latest = host.ShowAsync(Request("C"));
                if (action != "success") await pending.WaitAsync(TimeSpan.FromSeconds(3));
                if (action == "newer") Assert.False(latest.IsCompleted);
                Assert.False(host.IsOpen);
                Assert.Empty(nextRenderer.Requests);
                Assert.False(pdf.ReleaseCompletion.IsCompleted);
                Assert.True((await owner.DrainAsync(TimeSpan.Zero)).TimedOut);
                initialized.TrySetResult();
                deleted.TrySetResult(new(PrivateBrowsingDeleteStatus.Deleted, 1));
                await pdf.ReleaseCompletion;
                await Task.WhenAll(pending, latest).WaitAsync(TimeSpan.FromSeconds(3));
                if (action is "success" or "newer")
                {
                    Assert.True(host.IsOpen);
                    Assert.Equal(new[] { action == "success" ? "B" : "C" }, nextRenderer.Requests);
                }
                else
                {
                    Assert.False(host.IsOpen);
                    Assert.False(host.GetDiagnosticSnapshot().HasRequest);
                    Assert.Empty(nextRenderer.Requests);
                }
                // ReleaseCompletion後に走る所有者の完了通知まで、製品と同じ期限で待つ。
                Assert.False((await owner.DrainAsync(TimeSpan.FromSeconds(3))).TimedOut);
            }
            finally
            {
                initialized.TrySetResult(); deleted.TrySetResult(new(PrivateBrowsingDeleteStatus.Deleted, 1));
                await pdf.ReleaseCompletion;
                await Task.WhenAll(pending, latest).WaitAsync(TimeSpan.FromSeconds(3));
                host.Close();
            }
        });

    [Fact]
    public void CancellationDuringSlowRenderDoesNotLeaveLoadingVisible()
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            using var host = new QuickLookHost();
            using var cancellation = new CancellationTokenSource();
            var rendered = new TaskCompletionSource<QuickLookRenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderer = new RecordingRenderer { PendingResult = rendered.Task };
            Set(Service(host), "_renderers", new IQuickLookRenderer[] { renderer });
            var show = host.ShowAsync(Request("B"), cancellation.Token);
            try
            {
                Assert.True(host.IsOpen);
                cancellation.Cancel();
                await show.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(host.IsOpen);
                Assert.False(host.GetDiagnosticSnapshot().HasRequest);
            }
            finally
            {
                rendered.TrySetResult(new(QuickLookRenderKind.Information, "B", "", ""));
                await show.WaitAsync(TimeSpan.FromSeconds(3));
                host.Close();
            }
        });

    private static QuickLookRequest Request(string id) => new([new() { DisplayName = id, SourceId = id }], id);
    private static QuickLookService Service(QuickLookHost host)
        => (QuickLookService)typeof(QuickLookHost).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class RecordingRenderer : IQuickLookRenderer
    {
        public List<string> Requests { get; } = [];
        public Task<QuickLookRenderResult>? PendingResult { get; init; }
        public bool CanRender(QuickLookRequest request) => true;
        public Task<QuickLookRenderResult> RenderAsync(QuickLookRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request.SourceId);
            return PendingResult ?? Task.FromResult(new QuickLookRenderResult(QuickLookRenderKind.Information, request.SourceId, "", ""));
        }
        public ValueTask UnloadAsync() => ValueTask.CompletedTask;
    }
}
