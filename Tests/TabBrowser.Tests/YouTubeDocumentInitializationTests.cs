using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeDocumentInitializationTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("timeout")]
    [InlineData("navigation")]
    [InlineData("disposed")]
    [InlineData("closing")]
    [InlineData("redirected")]
    public async Task PendingScriptSeparatesContinuationExpiryFromDocumentExpiry(string outcome)
    {
        var owner = new YouTubeMiniPlayerDocumentInitialization();
        owner.Begin(1);
        var query = new TaskCompletionSource<YouTubeContinuationCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = true;
        var initialization = 0;
        var savedState = 0;
        Task Apply(Func<bool> current)
        {
            Assert.True(current());
            initialization++;
            savedState++;
            return Task.CompletedTask;
        }
        var first = owner.CompleteAsync(1, () => available, () => query.Task, Apply);
        Assert.False(first.IsCompleted);
        // 完了通知が重なっても同じdocumentへ二重適用しない。
        await owner.CompleteAsync(1, () => available, () => throw new Exception("二重判定"), Apply);
        if (outcome == "navigation") owner.Begin(2);
        if (outcome is "disposed" or "closing") available = false;
        query.SetResult(outcome == "timeout" ? YouTubeContinuationCompletion.Expired
            : outcome == "redirected" ? YouTubeContinuationCompletion.Redirected : YouTubeContinuationCompletion.Ready);
        await first;
        Assert.Equal(outcome is "success" or "timeout" ? 1 : 0, initialization);
        Assert.Equal(initialization, savedState);
        if (outcome == "navigation")
        {
            await owner.CompleteAsync(2, () => true, () => Task.FromResult(YouTubeContinuationCompletion.Ready), Apply);
            Assert.Equal(1, initialization);
        }
    }

    [Fact]
    public async Task NewNavigationDuringInitializationInvalidatesRemainingSteps()
    {
        var owner = new YouTubeMiniPlayerDocumentInitialization(); owner.Begin(1);
        var correction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = 0;
        var run = owner.CompleteAsync(1, () => true, () => Task.FromResult(YouTubeContinuationCompletion.Ready), async current =>
        {
            await correction.Task;
            if (current()) applied++;
        });
        owner.Begin(2); correction.SetResult(); await run;
        Assert.Equal(0, applied);
    }
}