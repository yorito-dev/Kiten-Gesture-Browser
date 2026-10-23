using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerRecoveryControllerTests
{
    [Theory]
    [InlineData("closing")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    public async Task ManualRepairDoesNotStartDuringShutdownOrAfterDisposal(string state)
    {
        using var f = new Fixture();
        if (state == "closing") f.Host.IsClosingWindow = true;
        if (state == "disposed") f.Runtime.IsDisposed = true;
        if (state == "cancelled") f.Host.Cancellation.Cancel();
        Assert.False(await f.Controller.RunManualRepairAsync(f.State));
        Assert.Empty(f.Host.Trace);
    }

    [Fact]
    public async Task ManualRepairWorksAfterAutomaticLimitAndRepeatedClickDoesNotDuplicateRecovery()
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++) Assert.True(await f.Run());
        Assert.False(await f.Run());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Capture = _ => { started.TrySetResult(); return release.Task; };
        var manual = f.Controller.RunManualRepairAsync(f.State);
        await started.Task;
        Assert.False(await f.Controller.RunManualRepairAsync(f.State));
        release.SetResult(true);
        Assert.True(await manual);
        Assert.Equal(4, f.Host.ReloadCount);
        Assert.False(await f.Run());
        Assert.Equal(4, f.Host.ReloadCount);
    }

    [Fact]
    public async Task LocalFileNeverEntersYouTubeRecoveryOrRequiresVideoId()
    {
        using var f = new Fixture();
        f.State.SourceKind = MiniPlayerSourceKind.LocalFile;
        f.State.Url = string.Empty;
        Assert.False(await f.Controller.RunManualRepairAsync(f.State));
        Assert.Equal(0, f.Host.ProbeCount);
        Assert.Equal(0, f.Host.ReloadCount);
        var messages = new List<string>();
        Assert.False(await YouTubeMiniPlayerPlaybackCommandService.ReloadAsync(f.State,
            f.Host.Items, (_, _) => throw new InvalidOperationException("YouTubeへ遷移してはいけない"), messages.Add));
        Assert.Contains("ローカル動画を再読み込みできませんでした", messages);
    }

    [Theory]
    [InlineData("closing")]
    [InlineData("local")]
    [InlineData("missing")]
    public void QueueRejectsUnavailableTargets(string guard)
    {
        using var f = new Fixture();
        if (guard == "closing") f.Host.IsClosingWindow = true;
        if (guard == "local") f.State.SourceKind = MiniPlayerSourceKind.LocalFile;
        if (guard == "missing") f.Host.Items.Clear();
        f.Controller.QueueYouTubeMiniPlayerAutoRecovery(f.State, "test", TimeSpan.FromMinutes(1), true);
        Assert.False(f.Controller.CancelPending(f.State.Id));
        Assert.Empty(f.Host.Trace);
    }

    [Fact]
    public async Task MinimizedQueueCancelsPendingAndUsesInactiveSuspension()
    {
        using var f = new Fixture();
        var pending = f.Run(delay: TimeSpan.FromMinutes(1));
        f.State.IsMinimized = true;
        f.Controller.QueueYouTubeMiniPlayerAutoRecovery(f.State, "test");
        Assert.False(await pending);
        Assert.Equal([f.State.Id], f.Host.Inactive);
        Assert.False(f.Controller.CancelPending(f.State.Id));
        Assert.Empty(f.Host.Status);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("stalled")]
    [InlineData("emptied")]
    [InlineData("error")]
    public async Task MinimizedDiagnosticCancelsPendingWithoutStartingRecovery(string eventName)
    {
        using var f = new Fixture();
        var pending = f.Run(delay: TimeSpan.FromMinutes(1));
        f.State.IsMinimized = true;
        f.Controller.HandlePlaybackDiagnostic(f.State, Message(eventName));
        Assert.False(await pending);
        Assert.Equal([f.State.Id], f.Host.Inactive);
        Assert.Equal(eventName == "error", f.Runtime.DisplayHealth.GetSnapshot().State == YouTubeMiniPlayerDisplayHealthState.ReloadRecommended);
        Assert.False(f.Controller.CancelPending(f.State.Id));
    }

    [Theory]
    [InlineData("waiting", true)]
    [InlineData("stalled", true)]
    [InlineData("emptied", true)]
    [InlineData("error", true)]
    [InlineData("playing", false)]
    [InlineData("state", false)]
    public void DiagnosticOnlyQueuesExistingFailureEvents(string eventName, bool expected)
    {
        using var f = new Fixture();
        f.Controller.HandlePlaybackDiagnostic(f.State, Message(eventName));
        Assert.Equal(expected, f.Controller.CancelPending(f.State.Id));
        Assert.Empty(f.Host.Inactive);
    }

    [Theory]
    [InlineData("canplay")]
    [InlineData("playing")]
    public async Task HealthyPlaybackEventCancelsPendingStallRecovery(string eventName)
    {
        using var f = new Fixture();
        var pending = f.Run(delay: TimeSpan.FromMinutes(1));

        f.Controller.HandlePlaybackDiagnostic(f.State, Message(eventName));

        Assert.False(await pending);
        Assert.False(f.Controller.CancelPending(f.State.Id));
        Assert.Equal(0, f.Host.ReloadCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnconfirmedPlaybackStallDoesNotReload(bool firstProbeMissing)
    {
        using var f = new Fixture();
        var probes = firstProbeMissing
            ? new Queue<string?>([null])
            : new Queue<string?>([Probe(), null]);
        f.Host.Probe = () => Task.FromResult(probes.Dequeue());

        Assert.False(await f.Run(progress: true, allowSourceTabFallback: false));
        Assert.Equal(firstProbeMissing ? 1 : 2, f.Host.ProbeCount);
        Assert.Equal(0, f.Host.ReloadCount);
        Assert.Equal(0, f.Host.LoadCount);
        Assert.DoesNotContain("capture", f.Host.Trace);
        Assert.Empty(f.Host.RecoveryLines);
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, true)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, true)]
    public void ProcessFailureUsesExistingPolicy(CoreWebView2ProcessFailedKind kind, bool queues)
    {
        using var f = new Fixture();
        f.Controller.HandleProcessFailure(f.State, kind);
        Assert.Equal(queues, f.Controller.CancelPending(f.State.Id));
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("recreate")]
    [InlineData("source")]
    [InlineData("failed")]
    public async Task RecoveryUsesExistingStageChainAndFinalResultPolicy(string outcome)
    {
        using var f = new Fixture();
        f.Host.Reload = runtime => runtime.CompleteRecoveryNavigation(runtime.WebView!, outcome == "reload");
        f.Host.Load = (state, _) =>
        {
            var runtime = f.Host.Items[state.Id];
            runtime.WebView = WebViewIdentity();
            runtime.CompleteRecoveryNavigation(runtime.WebView, outcome == "recreate");
            return Task.CompletedTask;
        };
        f.Host.Source = _ => Task.FromResult(outcome == "source");

        Assert.Equal(outcome != "failed", await f.Run(allowSourceTabFallback: true));
        Assert.Equal(1, f.Host.ReloadCount);
        Assert.Equal(outcome == "reload" ? 0 : 1, f.Host.LoadCount);
        Assert.Equal(outcome is "source" or "failed" ? 1 : 0, f.Host.SourceCount);
        Assert.Contains(f.Host.RecoveryLines, line => line.Contains("operation=webview-reload", StringComparison.Ordinal));
        Assert.Equal(outcome == "reload" ? 0 : 1,
            f.Host.RecoveryLines.Count(line => line.Contains("operation=webview-recreate", StringComparison.Ordinal)));
        Assert.All(f.Host.RecoveryLines, line =>
        {
            Assert.Contains($"target={f.State.Id}", line, StringComparison.Ordinal);
            Assert.DoesNotContain("https:", line, StringComparison.Ordinal);
            Assert.DoesNotContain("video=", line, StringComparison.Ordinal);
        });
        Assert.Equal(outcome is "reload" or "recreate" ? 1 : 0, f.Host.SaveCount);
        Assert.Equal(outcome == "source" ? 1 : 0, f.Host.Closed.Count);
        if (outcome == "failed") Assert.Equal(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended, f.Runtime.DisplayHealth.GetSnapshot().State);
        Assert.All(f.Waits, wait => Assert.Equal(TimeSpan.FromSeconds(20), wait.Timeout));
        Assert.True(f.Host.Trace.IndexOf("capture") < f.Host.Trace.IndexOf("reload"));
        if (outcome is "reload" or "recreate") Assert.Equal("save", f.Host.Trace[^2]);
    }

    [Fact]
    public async Task DiagnosticWriteFailureDoesNotPreventRecovery()
    {
        using var f = new Fixture();
        f.Host.ThrowOnRecoveryLog = true;

        Assert.True(await f.Run());
        Assert.Equal(1, f.Host.ReloadCount);
        Assert.Equal(1, f.Host.SaveCount);
    }

    [Fact]
    public async Task AutomaticRecoveryWithoutPlaybackProgressKeepsMiniPlayer()
    {
        using var f = new Fixture();
        f.State.IsPaused = false;
        var probes = new Queue<string?>(
            [Probe(hasVideo: false), Probe(position: 20), Probe(position: 20.1), Probe(position: 20.1), Probe(position: 20.2)]);
        f.Host.Probe = () => Task.FromResult(probes.Dequeue());

        Assert.False(await f.Run(progress: true, allowSourceTabFallback: false));
        Assert.Equal(1, f.Host.ReloadCount);
        Assert.Equal(1, f.Host.LoadCount);
        Assert.Equal(0, f.Host.SourceCount);
        Assert.Empty(f.Host.Closed);
        Assert.Equal(0, f.Host.SaveCount);
        Assert.Equal(2, f.Delays.Count);
        Assert.All(f.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(2), delay.Delay));
        Assert.Equal(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended, f.Runtime.DisplayHealth.GetSnapshot().State);
        Assert.Contains("自動復旧できませんでした", f.Host.Status[^1]);
    }

    [Fact]
    public async Task NavigationSuccessWithPlaybackProgressCompletesRecovery()
    {
        using var f = new Fixture();
        f.State.IsPaused = false;
        var probes = new Queue<string?>(
            [null, Probe(position: 20), Probe(position: 20.5)]);
        f.Host.Probe = () => Task.FromResult(probes.Dequeue());

        Assert.True(await f.Run());
        Assert.Equal(1, f.Host.ReloadCount);
        Assert.Equal(0, f.Host.LoadCount);
        Assert.Equal(1, f.Host.SaveCount);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(f.Delays).Delay);
    }

    [Fact]
    public async Task PausedPlaybackRequiresHealthyMediaWithoutProgress()
    {
        using var f = new Fixture();
        f.State.IsPaused = true;
        var probes = new Queue<string?>([null, Probe(paused: true)]);
        f.Host.Probe = () => Task.FromResult(probes.Dequeue());

        Assert.True(await f.Run());
        Assert.Equal(1, f.Host.ReloadCount);
        Assert.Equal(1, f.Host.SaveCount);
        Assert.Empty(f.Delays);
    }

    [Fact]
    public async Task RepeatedRecoveryStopsAfterThreeAttemptsUntilWindowExpires()
    {
        using var f = new Fixture();

        Assert.True(await f.Run());
        Assert.True(await f.Run());
        Assert.True(await f.Run());
        Assert.False(await f.Run());
        Assert.Equal(3, f.Host.ReloadCount);
        Assert.Contains("自動復旧を繰り返したため停止しました", f.Host.Status[^1]);
        Assert.Equal(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended, f.Runtime.DisplayHealth.GetSnapshot().State);

        var other = f.Host.Add();
        Assert.True(await f.Controller.RunYouTubeMiniPlayerAutoRecoveryAsync(
            other.State,
            "test",
            TimeSpan.Zero,
            requireProgressConfirmation: false,
            skipCurrentWebViewReload: false));
        Assert.Equal(4, f.Host.ReloadCount);

        f.Now = f.Now.AddMinutes(2);

        Assert.True(await f.Run());
        Assert.Equal(5, f.Host.ReloadCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disposed")]
    [InlineData("no-core")]
    public async Task MissingRuntimeOrCoreSkipsProbeAndContinuesToRecovery(string condition)
    {
        using var f = new Fixture();
        if (condition == "missing") f.Host.Items.Clear();
        if (condition == "disposed") f.Runtime.IsDisposed = true;
        if (condition == "no-core") f.Host.HasCore = false;
        await f.Run();
        Assert.Equal(0, f.Host.ProbeCount);
        Assert.Contains("capture", f.Host.Trace);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task HealthyOrPausedConfirmationDoesNotStartRecovery(bool progress, bool paused, bool ended)
    {
        using var f = new Fixture();
        f.Host.Probe = () => Task.FromResult<string?>(Probe(paused: paused, ended: ended));
        Assert.False(await f.Run(progress));
        Assert.Equal(1, f.Host.ProbeCount);
        Assert.DoesNotContain("capture", f.Host.Trace);
        Assert.Empty(f.Delays);
        Assert.Empty(f.Host.Status);
        Assert.Contains("layout", f.Host.Trace);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("invalid", false)]
    [InlineData("definite", true)]
    [InlineData("display", true)]
    [InlineData("display-missing", true)]
    public async Task OnlyConfirmedFailureStartsStallRecoveryWithoutSecondProbe(string condition, bool recovers)
    {
        using var f = new Fixture();
        if (condition.StartsWith("display", StringComparison.Ordinal)) f.Runtime.DisplayHealth.ReportFailure();
        f.Host.Probe = () => Task.FromResult(condition switch
        {
            "null" => null,
            "invalid" => "{",
            "display-missing" => null,
            "definite" => Probe(hasVideo: false),
            _ => Probe()
        });
        Assert.Equal(recovers, await f.Run(progress: true));
        Assert.Equal(1, f.Host.ProbeCount);
        Assert.Empty(f.Delays);
    }

    [Theory]
    [InlineData("stalled", true)]
    [InlineData("advanced", false)]
    [InlineData("paused", false)]
    [InlineData("ended", false)]
    [InlineData("missing", false)]
    public async Task ProgressConfirmationWaitsTwoSecondsAndUsesExistingProbePolicy(string second, bool recovers)
    {
        using var f = new Fixture();
        var probes = new Queue<string?>([Probe(), second switch
        {
            "advanced" => Probe(position: 12.25),
            "paused" => Probe(paused: true),
            "ended" => Probe(ended: true),
            "missing" => null,
            _ => Probe(position: 12.1)
        }]);
        f.Host.Probe = () => Task.FromResult(probes.Dequeue());
        Assert.Equal(recovers, await f.Run(progress: true));
        Assert.Equal(2, f.Host.ProbeCount);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(f.Delays).Delay);
        Assert.Equal(recovers ? 1 : 0, f.Host.ReloadCount);
    }

    [Fact]
    public async Task ConfirmationKeepsCapturedRuntimeForDisplayCheckButResolvesNextProbeThroughHost()
    {
        using var f = new Fixture();
        var firstProbe = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Probe = () => { entered.SetResult(); return firstProbe.Task; };
        var run = f.Run();
        await entered.Task;
        f.Host.Items[f.State.Id] = CreateRuntime(f.State);
        f.Runtime.DisplayHealth.ReportFailure();
        firstProbe.SetResult(Probe());
        Assert.True(await run);
        Assert.Contains("capture", f.Host.Trace);
        Assert.Equal(1, f.Host.ReloadCount);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("probe")]
    [InlineData("delay")]
    [InlineData("capture")]
    [InlineData("source")]
    public async Task CancellationUsesWindowLifetimeAtExistingCheckpoints(string at)
    {
        using var f = new Fixture();
        if (at == "before") f.Host.Cancellation.Cancel();
        if (at == "probe") f.Host.Probe = () => { f.Host.Cancellation.Cancel(); return Task.FromResult<string?>(Probe()); };
        if (at == "delay")
        {
            f.Host.Probe = () => Task.FromResult<string?>(Probe());
            f.Delay = (_, token) => { f.Host.Cancellation.Cancel(); return Task.FromCanceled(token); };
        }
        if (at == "capture") f.Host.Capture = _ => { f.Host.Cancellation.Cancel(); return Task.FromResult(true); };
        if (at == "source")
        {
            f.State.Url = string.Empty;
            f.Host.CanReload = false;
            f.Host.Source = _ => { f.Host.Cancellation.Cancel(); return Task.FromResult(true); };
        }
        Assert.False(await f.Run(progress: at == "delay"));
        Assert.Equal(0, f.Host.SaveCount);
        Assert.Empty(f.Host.Closed);
        Assert.Empty(f.Host.Diagnostics);
        if (at == "before") Assert.Empty(f.Host.Trace);
        if (at != "source") Assert.Equal(0, f.Host.ReloadCount);
    }

    [Theory]
    [InlineData("success", 99)]
    [InlineData("unchanged", 42)]
    [InlineData("failure", 42)]
    [InlineData("unrelated-cancel", 42)]
    public async Task SnapshotFallsBackToPreCaptureStateExceptWhenCaptureSucceeds(string capture, double expected)
    {
        using var f = new Fixture();
        f.Host.Capture = state =>
        {
            state.LastPositionSeconds = 99;
            if (capture == "failure") throw new InvalidOperationException("capture-failed");
            if (capture == "unrelated-cancel") throw new OperationCanceledException();
            return Task.FromResult(capture == "success");
        };
        f.Host.Reload = runtime =>
        {
            Assert.Equal(expected, runtime.State.LastPositionSeconds);
            Assert.Equal(YouTubeMiniPlayerDisplayHealthState.NotInitialized, runtime.DisplayHealth.GetSnapshot().State);
            runtime.CompleteRecoveryNavigation(runtime.WebView!, true);
        };
        Assert.True(await f.Run());
        Assert.Equal(capture is "failure" or "unrelated-cancel" ? 1 : 0, f.Host.Diagnostics.Count);
        if (f.Host.Diagnostics.Count > 0) Assert.Contains("youtube-mini-player-recovery-capture-failed", f.Host.Diagnostics[0]);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("removed")]
    [InlineData("runtime")]
    [InlineData("webview")]
    public async Task NavigationSuccessRequiresSameLiveRuntimeAndWebView(string change)
    {
        using var f = new Fixture();
        f.State.Url = string.Empty;
        f.Host.Reload = runtime =>
        {
            runtime.CompleteRecoveryNavigation(runtime.WebView!, true);
            if (change == "disposed") runtime.IsDisposed = true;
            if (change == "removed") f.Host.Items.Clear();
            if (change == "runtime") f.Host.Items[f.State.Id] = CreateRuntime(f.State);
            if (change == "webview") runtime.WebView = WebViewIdentity();
        };
        Assert.False(await f.Run());
        Assert.Equal(0, f.Host.SaveCount);
        Assert.Equal(1, f.Host.SourceCount);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("reload-error")]
    [InlineData("cancel")]
    public async Task ReloadWaitAlwaysReleasesOwnNavigationAndOnlyCancellationStopsFallback(string failure)
    {
        using var f = new Fixture();
        f.State.Url = string.Empty;
        f.Host.Reload = _ => { if (failure == "reload-error") throw new InvalidOperationException("reload"); };
        f.Wait = (completion, _, token) =>
        {
            if (failure == "cancel") { f.Host.Cancellation.Cancel(); return Task.FromCanceled<bool>(token); }
            return Task.FromException<bool>(new TimeoutException());
        };
        Assert.False(await f.Run());
        Assert.Equal(failure == "cancel" ? 0 : 1, f.Host.SourceCount);
        if (f.Waits.Count > 0) Assert.True(f.Waits[0].Completion.IsCompletedSuccessfully);
        if (f.Waits.Count > 0) Assert.False(await f.Waits[0].Completion);
        var nextWait = f.Runtime.BeginRecoveryNavigation(f.Runtime.WebView);
        f.Runtime.CompleteRecoveryNavigation(f.Runtime.WebView!, true);
        Assert.True(await nextWait.Completion);
    }

    [Fact]
    public async Task OldReloadFinallyDoesNotCancelAReplacementNavigationWait()
    {
        using var f = new Fixture();
        YouTubeMiniPlayerRecoveryNavigationWait replacement = default;
        f.State.Url = string.Empty;
        f.Host.Reload = runtime => replacement = runtime.BeginRecoveryNavigation(runtime.WebView);
        Assert.False(await f.Run());
        Assert.False(replacement.Completion.IsCompleted);
        f.Runtime.CompleteRecoveryNavigation(f.Runtime.WebView!, true);
        Assert.True(await replacement.Completion);
    }

    [Theory]
    [InlineData("normal", true)]
    [InlineData("missing-webview", false)]
    [InlineData("load-error", false)]
    [InlineData("old-runtime", false)]
    public async Task RecreateSkipsReloadAndChecksOriginalRuntimeAfterLoad(string condition, bool success)
    {
        using var f = new Fixture();
        f.Host.Load = (state, _) =>
        {
            Assert.Equal(42, state.LastPositionSeconds);
            if (condition == "load-error") throw new InvalidOperationException("load");
            if (condition == "missing-webview") return Task.CompletedTask;
            var runtime = f.Runtime;
            runtime.WebView = WebViewIdentity();
            runtime.CompleteRecoveryNavigation(runtime.WebView, true);
            if (condition == "old-runtime") f.Host.Items[state.Id] = CreateRuntime(state);
            return Task.CompletedTask;
        };
        Assert.Equal(success, await f.Run(skipReload: true));
        Assert.Equal(0, f.Host.ReloadCount);
        Assert.Equal(1, f.Host.LoadCount);
        Assert.True(f.Host.Trace.IndexOf("reset") < f.Host.Trace.IndexOf("load"));
        Assert.Equal(success ? 0 : 1, f.Host.SourceCount);
    }

    [Fact]
    public async Task SourceFallbackDoesNotCloseARuntimeAlreadyRemovedDuringActivation()
    {
        using var f = new Fixture();
        f.State.Url = string.Empty;
        f.Host.CanReload = false;
        f.Host.Capture = state => { state.LastPositionSeconds = 91; return Task.FromResult(false); };
        f.Host.Source = state =>
        {
            Assert.Equal(42, state.LastPositionSeconds);
            f.Host.Items.Clear();
            return Task.FromResult(true);
        };
        Assert.True(await f.Run());
        Assert.Empty(f.Host.Closed);
        Assert.Equal(0, f.Host.SaveCount);
        Assert.Contains("通常のYouTubeタブで開きました", f.Host.Status[^1]);
    }

    [Fact]
    public async Task ConcurrentRequestIsIgnoredAndCancelAllReleasesActiveRecovery()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Probe = () => { entered.TrySetResult(); return probe.Task; };
        var first = f.Run();
        await entered.Task;
        Assert.False(await f.Run());
        Assert.False(f.Controller.CancelPending(Guid.NewGuid()));
        f.Controller.CancelAll();
        probe.SetResult(null);
        Assert.False(await first);
        Assert.Empty(f.Host.Status);
        Assert.Equal(0, f.Host.SaveCount);
    }

    [Fact]
    public async Task CancelPendingDoesNotAbortStateCaptureButExplicitCancelDoes()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Capture = _ => { entered.SetResult(); return capture.Task; };
        var run = f.Run();
        await entered.Task;
        Assert.False(f.Controller.CancelPending(f.State.Id));
        Assert.False(run.IsCompleted);
        f.Controller.Cancel(f.State.Id);
        capture.SetResult(true);
        Assert.False(await run);
        Assert.Equal(0, f.Host.ReloadCount);
        Assert.Equal(0, f.Host.SaveCount);
        Assert.Empty(f.Host.Diagnostics);
    }

    [Fact]
    public async Task ProcessGroupSnapshotsMatchingRuntimesAndStaggersEvenAfterLastTarget()
    {
        using var f = new Fixture();
        var second = f.Host.Add();
        f.Host.Add().State.ContainerProfileId = Guid.NewGuid().ToString("N");
        f.Host.Add().IsDisposed = true;
        f.Host.Add().WebView = null;
        f.State.ContainerProfileId = "invalid-profile";
        f.Host.Load = (state, _) =>
        {
            if (state.Id == f.State.Id)
            {
                f.Host.Add();
                second.IsDisposed = true;
            }
            return Task.CompletedTask;
        };
        await f.Controller.RecoverFloatingMediaProcessGroupAsync(ContainerProfile.DefaultId.ToUpperInvariant(), 17);
        Assert.Equal([f.State.Id, second.State.Id], f.Host.LoadedIds);
        Assert.Equal(2, f.Delays.Count);
        Assert.All(f.Delays, delay =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(350), delay.Delay);
            Assert.Equal(f.Host.LifetimeToken, delay.Token);
        });
        Assert.Equal(2, f.Host.GroupResults.Count);
        Assert.All(f.Host.GroupResults, result => Assert.Equal((ContainerProfile.DefaultId.ToUpperInvariant(), 17u, "mini-player-recreated"), result));
        Assert.Equal(0, f.Host.SaveCount);
    }

    [Fact]
    public async Task ProcessGroupSkipsInvalidUrlsAndContinuesAfterPerRuntimeFailure()
    {
        using var f = new Fixture();
        var invalid = f.Host.Add();
        invalid.State.Url = "";
        var last = f.Host.Add();
        f.Host.Load = (state, _) => state.Id == f.State.Id
            ? Task.FromException(new InvalidOperationException("load failed")) : Task.CompletedTask;
        await f.Controller.RecoverFloatingMediaProcessGroupAsync(ContainerProfile.DefaultId, 23);
        Assert.Equal([f.State.Id, last.State.Id], f.Host.LoadedIds);
        Assert.Equal(2, f.Delays.Count);
        Assert.Single(f.Host.Diagnostics);
        Assert.Equal(YouTubeMiniPlayerDisplayHealthState.ReloadRecommended, f.Runtime.DisplayHealth.GetSnapshot().State);
        Assert.Equal("mini-player-recreate-failed", f.Host.GroupResults[0].Result);
        Assert.Equal("mini-player-recreated", f.Host.GroupResults[1].Result);
    }

    [Theory]
    [InlineData("closing-before", 0)]
    [InlineData("cancel-before", 0)]
    [InlineData("closing-after-load", 1)]
    [InlineData("cancel-delay", 1)]
    public async Task ProcessGroupKeepsExistingTerminationCheckpoints(string condition, int loads)
    {
        using var f = new Fixture();
        f.Host.Add();
        if (condition == "closing-before") f.Host.IsClosingWindow = true;
        if (condition == "cancel-before") f.Host.Cancellation.Cancel();
        if (condition == "closing-after-load") f.Host.Load = (_, _) => { f.Host.IsClosingWindow = true; return Task.CompletedTask; };
        if (condition == "cancel-delay") f.Delay = (_, token) => { f.Host.Cancellation.Cancel(); return Task.FromCanceled(token); };
        await f.Controller.RecoverFloatingMediaProcessGroupAsync(ContainerProfile.DefaultId, 1);
        Assert.Equal(loads, f.Host.LoadCount);
        Assert.Equal(loads, f.Host.GroupResults.Count);
        Assert.Empty(f.Host.Diagnostics);
    }

    [Fact]
    public void MainWindowOnlyConnectsPrimitiveOperationsAndExistingLifetime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MainWindow.xaml.cs"))) root = root.Parent;
        Assert.NotNull(root);
        var main = File.ReadAllText(Path.Combine(root.FullName, "MainWindow.YouTubeMiniPlayer.cs"));
        var composition = File.ReadAllText(Path.Combine(root.FullName, "MainWindow.xaml.cs"));
        var controller = File.ReadAllText(Path.Combine(root.FullName, "Services", "YouTubeMiniPlayer", "Runtime", "YouTubeMiniPlayerRecoveryController.cs"));
        Assert.Contains("_youTubeMiniPlayerRecoveryController = new YouTubeMiniPlayerRecoveryController(this)", composition);
        Assert.Contains("_youTubeMiniPlayerRecoveryController.CancelAll()", main);
        Assert.Contains("IYouTubeMiniPlayerLifetimeHost.CancelRecovery(Guid id) => _youTubeMiniPlayerRecoveryController.Cancel(id)", main);
        Assert.Contains("_youTubeMiniPlayerRecoveryController.HandlePlaybackDiagnostic(state, message)", main);
        Assert.Contains("_youTubeMiniPlayerRecoveryController.HandleProcessFailure(state, args.ProcessFailedKind)", main);
        Assert.Contains("rememberClosedPlayers: false).Close(id)", main);
        Assert.Contains("captureBeforeReopen: false", main);
        Assert.Contains("reload = core.Reload", main);
        Assert.Contains("YouTubeMiniPlayerRuntimeLifecycleService.ResetWebView(runtime)", main);
        Assert.Contains("IYouTubeMiniPlayerRecoveryHost.LifetimeToken => _windowTaskSupervisor.LifetimeToken", main);
        Assert.Contains("IYouTubeMiniPlayerRecoveryHost.QueueLayoutVerification", main);
        Assert.Contains("YouTubeMiniPlayerStateRestoreService.RequestLayoutVerificationAsync", main);
        Assert.Contains("await ApplyYouTubeMiniPlayerWatchPageModeAsync(state)", main);
        Assert.DoesNotContain("private async Task<bool> ConfirmYouTubeMiniPlayerFailureAsync", main);
        Assert.DoesNotContain("_youTubeMiniPlayerRecoveryService", main);
        Assert.DoesNotContain("MainWindow", controller);
        Assert.Contains("YouTubeMiniPlayerRecoveryImmediateDelayMilliseconds = 250", controller);
        Assert.Contains("YouTubeMiniPlayerRecoveryStallDelayMilliseconds = 12000", controller);
        Assert.Contains("YouTubeMiniPlayerGpuRecoveryDelayMilliseconds = 3000", controller);
        Assert.Contains("YouTubeMiniPlayerRecoveryProcessDelayMilliseconds = 20000", controller);
        Assert.Contains("skipCurrentWebViewReload: action == WebViewProcessFailureAction.RecreateProcessGroup", controller);
    }

    private static string Probe(double position = 12, bool paused = false, bool ended = false, bool hasVideo = true)
        => JsonSerializer.Serialize(new
        {
            hasVideo,
            hasVisibleVideo = true,
            videoNaturalWidth = 1920,
            videoNaturalHeight = 1080,
            position,
            duration = 120,
            readyState = 2,
            networkState = 2,
            paused,
            ended,
            bufferedAhead = 0.05
        });

    private static YouTubePlaybackDiagnosticMessage Message(string eventName)
        => new(null, eventName, null, null, null, null, null, null, null, null, null, null,
            null, null, "visible", null, null, null, null);

    // 実Coreやcontrol初期化は行わず、復旧待機の参照同一性だけに使う。
    private static WebView2 WebViewIdentity()
    {
        var identity = (WebView2)RuntimeHelpers.GetUninitializedObject(typeof(WebView2));
        GC.SuppressFinalize(identity);
        return identity;
    }

    private static YouTubeMiniPlayerRuntime CreateRuntime(YouTubeMiniPlayerState state)
        => new(state, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, WebViewIdentity(), null);

    private sealed class Fixture : IDisposable
    {
        internal readonly Host Host = new();
        internal readonly YouTubeMiniPlayerRuntime Runtime;
        internal YouTubeMiniPlayerState State => Runtime.State;
        internal readonly YouTubeMiniPlayerRecoveryController Controller;
        internal readonly List<(TimeSpan Delay, CancellationToken Token)> Delays = [];
        internal readonly List<(Task<bool> Completion, TimeSpan Timeout, CancellationToken Token)> Waits = [];
        internal DateTimeOffset Now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        internal Func<TimeSpan, CancellationToken, Task> Delay = (_, _) => Task.CompletedTask;
        internal Func<Task<bool>, TimeSpan, CancellationToken, Task<bool>> Wait = (completion, timeout, token) => completion.WaitAsync(timeout, token);
        internal Fixture()
        {
            Runtime = Host.Add();
            Controller = new(Host,
                (delay, token) => { Delays.Add((delay, token)); return Delay(delay, token); },
                (completion, timeout, token) => { Waits.Add((completion, timeout, token)); return Wait(completion, timeout, token); },
                () => Now);
        }
        internal Task<bool> Run(
            bool progress = false,
            bool skipReload = false,
            TimeSpan? delay = null,
            bool allowSourceTabFallback = true)
            => Controller.RunYouTubeMiniPlayerAutoRecoveryAsync(
                State,
                "test",
                delay ?? TimeSpan.Zero,
                progress,
                skipReload,
                allowSourceTabFallback);
        public void Dispose() { Controller.CancelAll(); Host.Cancellation.Dispose(); }
    }

    private sealed class Host : IYouTubeMiniPlayerRecoveryHost
    {
        internal readonly Dictionary<Guid, YouTubeMiniPlayerRuntime> Items = [];
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly List<string> Trace = [];
        internal readonly List<string> Status = [];
        internal readonly List<string> Diagnostics = [];
        internal readonly List<string> RecoveryLines = [];
        internal readonly List<Guid> Closed = [];
        internal readonly List<Guid> Inactive = [];
        internal readonly List<Guid> LoadedIds = [];
        internal readonly List<(string Profile, uint Process, string Result)> GroupResults = [];
        internal bool HasCore = true;
        internal bool CanReload = true;
        internal bool ThrowOnRecoveryLog;
        internal int ProbeCount, ReloadCount, LoadCount, SourceCount, SaveCount;
        internal Func<Task<string?>> Probe = () => Task.FromResult<string?>(null);
        internal Func<YouTubeMiniPlayerState, Task<bool>> Capture = _ => Task.FromResult(true);
        internal Action<YouTubeMiniPlayerRuntime> Reload = runtime => runtime.CompleteRecoveryNavigation(runtime.WebView!, true);
        internal Func<YouTubeMiniPlayerState, Uri, Task> Load;
        internal Func<YouTubeMiniPlayerState, Task<bool>> Source = _ => Task.FromResult(false);
        internal Host()
        {
            Load = (state, _) =>
            {
                var runtime = Items[state.Id];
                runtime.WebView = WebViewIdentity();
                runtime.CompleteRecoveryNavigation(runtime.WebView, true);
                return Task.CompletedTask;
            };
        }
        internal YouTubeMiniPlayerRuntime Add()
        {
            var state = new YouTubeMiniPlayerState { Url = "https://www.youtube.com/watch?v=abcdefghijk", LastPositionSeconds = 42 };
            var runtime = CreateRuntime(state);
            Items.Add(state.Id, runtime);
            return runtime;
        }
        public bool IsClosingWindow { get; set; }
        public CancellationToken LifetimeToken => Cancellation.Token;
        public IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> Runtimes => Items;
        public bool HasOperationalCore(YouTubeMiniPlayerRuntime runtime) => HasCore;
        public bool TryGetReloadTarget(YouTubeMiniPlayerRuntime runtime, out WebView2 webView, out Action reload)
        {
            webView = runtime.WebView!;
            reload = () => { ReloadCount++; Trace.Add("reload"); Reload(runtime); };
            return CanReload && webView is not null;
        }
        public Task<string?> ExecuteHealthProbeAsync(YouTubeMiniPlayerState state, string script)
        {
            Assert.Equal(YouTubeControlScriptService.BuildPlaybackHealthProbeScript(), script);
            ProbeCount++;
            Trace.Add("probe");
            return Probe();
        }
        public Task<bool> CaptureStateAsync(YouTubeMiniPlayerState state) { Trace.Add("capture"); return Capture(state); }
        public void ResetWebView(YouTubeMiniPlayerRuntime runtime)
        {
            Trace.Add("reset");
            runtime.CancelRecoveryNavigation();
            runtime.DisplayHealth.Reset();
            runtime.WebView = null;
        }
        public Task LoadRuntimeAsync(YouTubeMiniPlayerState state, Uri uri)
        {
            LoadCount++;
            LoadedIds.Add(state.Id);
            Trace.Add("load");
            return Load(state, uri);
        }
        public Task<bool> ActivateRecoverySourceTabAsync(YouTubeMiniPlayerState state) { SourceCount++; Trace.Add("source"); return Source(state); }
        public void QueueInactiveSuspension(YouTubeMiniPlayerState state, bool stopStalledLoad) { Assert.True(stopStalledLoad); Inactive.Add(state.Id); }
        public void CloseWithoutHistory(Guid id) { Closed.Add(id); Trace.Add("close"); }
        public void QueueLayoutVerification(YouTubeMiniPlayerState state) { Assert.Same(Items[state.Id].State, state); Trace.Add("layout"); }
        public void QueueSave() { SaveCount++; Trace.Add("save"); }
        public void SetStatus(string message) { Status.Add(message); Trace.Add("status:" + message); }
        public void QueueDiagnosticLogLine(IReadOnlyList<string> fields)
        {
            if (ThrowOnRecoveryLog) throw new InvalidOperationException("diagnostic failed");
            RecoveryLines.Add(string.Join(" | ", fields));
        }
        public void AppendDiagnostic(string category, Exception exception, string detail) => Diagnostics.Add(category + ":" + detail);
        public void RecordProcessGroupResult(string containerProfileId, uint browserProcessId, string result)
            => GroupResults.Add((containerProfileId, browserProcessId, result));
    }
}
