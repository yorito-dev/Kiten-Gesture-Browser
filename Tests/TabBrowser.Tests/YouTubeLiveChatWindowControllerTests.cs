using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;
using TabBrowser.Views;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeLiveChatWindowControllerTests
{
    private const string Live = "{\"ok\":true,\"isLive\":true,\"viewerCountText\":\"1,234 人が視聴中\"}";

    [Theory]
    [InlineData(null, "button")]
    [InlineData(false, "button")]
    [InlineData(null, "window")]
    [InlineData(false, "window")]
    [InlineData(null, "all")]
    [InlineData(false, "all")]
    [InlineData(true, "button")]
    [InlineData(true, "window")]
    [InlineData(true, "all")]
    public void OpenChatRemainsClosableAndClosingRestoresLatestAvailability(bool? available, string close)
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            var button = new Button();
            var host = new Host(button);
            var controller = new YouTubeLiveChatWindowController(host);
            try
            {
                await controller.ToggleYouTubeLiveChatAsync(host.State);
                host.UpdateChatButtonAvailability(host.Runtime, available);
                if (available == false)
                    await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", false, null));
                Assert.True(button.IsEnabled);
                Assert.Contains("閉じる", button.ToolTip?.ToString());
                if (close == "button") await controller.ToggleYouTubeLiveChatAsync(host.State);
                else if (close == "window") host.Created[0].Close();
                else controller.CloseAllYouTubeLiveChatWindows();

                Assert.False(controller.ContainsWindow(host.State.Id));
                Assert.Equal(available == true, button.IsEnabled);
                Assert.Equal(available switch
                {
                    true => "ライブチャット／リプレイを開く",
                    false => "この動画ではチャットを利用できません",
                    null => "チャット情報を確認中"
                }, button.ToolTip);
                Assert.Equal(1, host.ScriptCalls);
                host.UpdateChatButtonAvailability(host.Runtime, true);
                Assert.True(button.IsEnabled);
                await controller.ToggleYouTubeLiveChatAsync(host.State);
                Assert.Equal(2, host.Created.Count);
            }
            finally { controller.CloseAllYouTubeLiveChatWindows(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingDuringInitializationDoesNotRestoreStaleAvailability(bool fail)
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            var initialize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var button = new Button();
            var host = new Host(button) { NextSurface = new Surface { Initialize = () => initialize.Task } };
            var controller = new YouTubeLiveChatWindowController(host);
            var opening = controller.ToggleYouTubeLiveChatAsync(host.State);
            try
            {
                host.UpdateChatButtonAvailability(host.Runtime, false);
                Assert.True(button.IsEnabled);
                await controller.ToggleYouTubeLiveChatAsync(host.State);
                Assert.False(button.IsEnabled);
                if (fail) initialize.SetException(new InvalidOperationException("closed during initialization"));
                else initialize.SetResult();
                await opening;
                Assert.False(button.IsEnabled);
                Assert.False(controller.ContainsWindow(host.State.Id));
                Assert.Single(host.Created);
            }
            finally
            {
                initialize.TrySetResult();
                await opening;
                controller.CloseAllYouTubeLiveChatWindows();
            }
        });

    [Fact]
    public void ChatButtonsKeepEachMiniPlayerWindowAndAvailabilityIndependent()
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            var firstButton = new Button();
            var secondButton = new Button();
            var firstHost = new Host(firstButton);
            var secondHost = new Host(secondButton);
            var first = new YouTubeLiveChatWindowController(firstHost);
            var second = new YouTubeLiveChatWindowController(secondHost);
            try
            {
                await first.ToggleYouTubeLiveChatAsync(firstHost.State);
                await second.ToggleYouTubeLiveChatAsync(secondHost.State);
                firstHost.UpdateChatButtonAvailability(firstHost.Runtime, false);
                secondHost.UpdateChatButtonAvailability(secondHost.Runtime, null);
                await first.ToggleYouTubeLiveChatAsync(firstHost.State);
                Assert.False(firstButton.IsEnabled);
                Assert.True(secondButton.IsEnabled);
                Assert.True(second.ContainsWindow(secondHost.State.Id));
                Assert.DoesNotContain("close", secondHost.Created[0].Calls);
            }
            finally
            {
                first.CloseAllYouTubeLiveChatWindows();
                second.CloseAllYouTubeLiveChatWindows();
            }
        });

    [Fact]
    public void OldWindowClosedNotificationDoesNotDisableReplacementChat()
        => WorkspaceTrashWindowTests.RunInSta(async () =>
        {
            var button = new Button();
            var host = new Host(button);
            var controller = new YouTubeLiveChatWindowController(host);
            try
            {
                await controller.ToggleYouTubeLiveChatAsync(host.State);
                var old = host.Created[0];
                old.Close();
                await controller.ToggleYouTubeLiveChatAsync(host.State);
                host.UpdateChatButtonAvailability(host.Runtime, false);
                old.RaiseClosed();
                Assert.True(controller.ContainsWindow(host.State.Id));
                Assert.True(button.IsEnabled);
                Assert.DoesNotContain("close", host.Created[1].Calls);
            }
            finally { controller.CloseAllYouTubeLiveChatWindows(); }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatButtonTogglesLiveOrReplayWindowAndCanReopen(bool live)
    {
        var host = new Host
        {
            Script = _ => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                isLive = live,
                replayContinuation = live ? null : "replay"
            }))
        };
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.ToggleYouTubeLiveChatAsync(host.State);
        var first = Assert.Single(host.Created);
        Assert.True(controller.ContainsWindow(host.State.Id));

        await controller.ToggleYouTubeLiveChatAsync(host.State);

        Assert.False(controller.ContainsWindow(host.State.Id));
        Assert.Equal(1, first.Calls.Count(call => call == "close"));
        Assert.DoesNotContain("bring", first.Calls);
        Assert.Equal(1, host.ScriptCalls);
        Assert.Equal((0, 0, 0), controller.GetDiagnosticSnapshot());
        await controller.ToggleYouTubeLiveChatAsync(host.State);
        Assert.Equal(2, host.Created.Count);
        Assert.Equal(2, host.ScriptCalls);
        Assert.Contains("show", host.Created[1].Calls);
        controller.CloseAllYouTubeLiveChatWindows();
    }

    [Fact]
    public async Task ChatButtonClosesExistingWindowEvenWhenVideoIsNotReady()
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.ToggleYouTubeLiveChatAsync(host.State);
        host.Ready = false;

        await controller.ToggleYouTubeLiveChatAsync(host.State);

        Assert.False(controller.ContainsWindow(host.State.Id));
        Assert.Equal("close", host.Created[0].Calls.Last());
        Assert.Equal(1, host.ScriptCalls);
        Assert.Empty(host.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatButtonClosesDuringInitializationWithoutReopening(bool fail)
    {
        var initialize = new TaskCompletionSource();
        var host = new Host { NextSurface = new Surface { Initialize = () => initialize.Task } };
        var controller = new YouTubeLiveChatWindowController(host);
        var opening = controller.ToggleYouTubeLiveChatAsync(host.State);
        try
        {
            Assert.False(opening.IsCompleted);
            await controller.ToggleYouTubeLiveChatAsync(host.State);
            Assert.False(controller.ContainsWindow(host.State.Id));
            if (fail) initialize.SetException(new InvalidOperationException("closed during initialize"));
            else initialize.SetResult();
            await opening;

            Assert.False(controller.ContainsWindow(host.State.Id));
            var first = Assert.Single(host.Created);
            Assert.Equal(1, first.Calls.Count(call => call == "show"));
            Assert.Equal(1, first.Calls.Count(call => call == "close"));
            Assert.Equal((0, 0, 0), controller.GetDiagnosticSnapshot());
            await controller.ToggleYouTubeLiveChatAsync(host.State);
            Assert.Equal(2, host.Created.Count);
        }
        finally
        {
            initialize.TrySetResult();
            await opening;
            controller.CloseAllYouTubeLiveChatWindows();
        }
    }

    [Fact]
    public async Task ChatButtonCanOpenAfterChatWindowWasClosedDirectly()
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.ToggleYouTubeLiveChatAsync(host.State);
        host.Created[0].Close();
        Assert.False(controller.ContainsWindow(host.State.Id));

        await controller.ToggleYouTubeLiveChatAsync(host.State);

        Assert.Equal(2, host.Created.Count);
        Assert.True(controller.ContainsWindow(host.State.Id));
        controller.CloseAllYouTubeLiveChatWindows();
    }

    [Fact]
    public void NativeAdapterReleasesSubscriptionsAndHostKeepsExistingRuntimeConnections()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MainWindow.xaml.cs"))) root = root.Parent;
        Assert.NotNull(root);
        var surface = File.ReadAllText(Path.Combine(root.FullName, "Views", "YouTubeLiveChatWindowSurface.cs"));
        foreach (var subscription in new[] { "Closed", "LocationChanged", "SizeChanged", "StateChanged" })
        {
            Assert.Contains($"_window.{subscription} +=", surface);
            Assert.Contains($"_window.{subscription} -=", surface);
        }
        Assert.Contains("finally", surface);
        Assert.Contains("Closed = null;", surface);
        Assert.Contains("LayoutChanged = null;", surface);
        var main = File.ReadAllText(Path.Combine(root.FullName, "MainWindow.YouTubeMiniPlayer.cs"));
        Assert.Contains("new YouTubeLiveChatSource(runtime, core.Source, core.ExecuteScriptAsync)", main);
        Assert.Contains("new YouTubeLiveChatWindowSurface(new YouTubeLiveChatWindow(", main);
        Assert.Contains("DispatcherPriority.Render, action", main);
        Assert.Contains("=> IsCurrentYouTubeMiniPlayerRuntime(id, runtime)", main);
        Assert.Contains("=> IsCurrentYouTubeMiniPlayerVideo(state, runtime, videoId)", main);
        Assert.DoesNotContain("Dictionary<Guid, YouTubeLiveChatWindow>", main);
        Assert.DoesNotContain("HashSet<Guid> _openingYouTubeLiveChatWindows", main);
    }

    [Theory]
    [InlineData(true, null, "/live_chat")]
    [InlineData(false, "token+/=", "/live_chat_replay")]
    public async Task OpenUsesExistingUriPolicyAndReusesWindow(bool live, string? replay, string path)
    {
        var host = new Host();
        host.Script = _ => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = true,
            isLive = live,
            replayContinuation = replay,
            viewerCountText = "1,234 人が視聴中"
        }));
        var controller = new YouTubeLiveChatWindowController(host);
        host.State.IsAtLiveEdge = true;
        await controller.OpenYouTubeLiveChatAsync(host.State);
        var surface = Assert.Single(host.Created);
        Assert.True(controller.ContainsWindow(host.State.Id));
        Assert.Equal(path, host.Uri!.AbsolutePath);
        Assert.Contains("dark_theme=1", host.Uri.Query);
        Assert.Equal(live, host.State.IsLiveBroadcast);
        Assert.Equal(live, host.State.IsAtLiveEdge);
        Assert.Equal(live ? "1,234 人が視聴中" : null, host.State.LiveViewerCountText);
        Assert.Equal(new bool?[] { null, true }, host.Buttons);
        Assert.Equal(new[] { "show", "exit", "initialize" }, surface.Calls);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Single(host.Created);
        Assert.Equal(1, host.ScriptCalls);
        Assert.Equal("bring", surface.Calls.Last());
    }

    [Theory]
    [InlineData("unready", "先にミニプレイヤー")]
    [InlineData("invalid", "動画ID")]
    [InlineData("parse", "読み込み完了後")]
    [InlineData("unavailable", "利用できません")]
    [InlineData("exception", "状態を確認できません")]
    public async Task UnavailableAndFailedOpenReleasePendingRequest(string mode, string message)
    {
        var host = new Host();
        if (mode == "unready") host.Ready = false;
        if (mode == "invalid") host.State.Url = host.Source = "https://www.youtube.com/";
        if (mode == "parse") host.Script = _ => Task.FromResult("{}");
        if (mode == "unavailable") host.Script = _ => Task.FromResult("{\"ok\":true,\"isLive\":false}");
        if (mode == "exception") host.Script = _ => throw new InvalidOperationException();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Empty(host.Created);
        Assert.Contains(message, Assert.Single(host.Status));
        if (mode == "exception") Assert.Equal(new bool?[] { null }, host.Buttons);
        if (mode == "parse") Assert.Equal(new bool?[] { null, true }, host.Buttons);
        if (mode == "unavailable") Assert.Equal(new bool?[] { null, false, false }, host.Buttons);
        host.Ready = true;
        host.State.Url = host.Source = "https://www.youtube.com/watch?v=abc123";
        host.Script = _ => Task.FromResult(Live);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Single(host.Created);
    }

    [Fact]
    public async Task NonYouTubeDoesNotProbeOrCreate()
    {
        var host = new Host();
        host.State.SourceKind = MiniPlayerSourceKind.LocalFile;
        await new YouTubeLiveChatWindowController(host).OpenYouTubeLiveChatAsync(host.State);
        Assert.Equal(0, host.ScriptCalls);
        Assert.Empty(host.Created);
        Assert.Empty(host.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AwaitBoundariesRejectOldVideoOrRuntime(bool waitEnvironment, bool replaceRuntime)
    {
        var host = new Host();
        var probe = new TaskCompletionSource<string>();
        var environment = new TaskCompletionSource<CoreWebView2Environment>();
        if (waitEnvironment) host.Environment = () => environment.Task;
        else host.Script = _ => probe.Task;
        var controller = new YouTubeLiveChatWindowController(host);
        var opening = controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.False(opening.IsCompleted);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Contains("ライブチャットを確認しています", host.Status);
        Assert.Equal(1, host.ScriptCalls);
        if (replaceRuntime) host.Runtime = Host.CreateRuntime(host.State);
        else host.State.Url = host.Source = "https://www.youtube.com/watch?v=next123";
        if (waitEnvironment) environment.SetResult(null!);
        else probe.SetResult(Live);
        await opening;
        Assert.Empty(host.Created);
        Assert.Equal(waitEnvironment ? 2 : 1, host.Buttons.Count);
        host.Environment = () => Task.FromResult<CoreWebView2Environment>(null!);
        host.Script = _ => Task.FromResult(Live);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Single(host.Created);
    }

    [Fact]
    public async Task CurrentSourceIsAppliedBeforeExistingWindowIsRaised()
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        host.Source = "https://www.youtube.com/watch?v=next123";
        await controller.OpenYouTubeLiveChatAsync(host.State);
        Assert.Contains("next123", host.State.Url);
        Assert.Equal(1, host.RefreshQueued);
        Assert.Equal(1, host.ScriptCalls);
        Assert.Equal(new[] { "transition:next123", "bring" }, host.Created[0].Calls.TakeLast(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailureOnlyClosesItsOwnCurrentWindow(bool replaceWindow)
    {
        var host = new Host();
        var initialize = new TaskCompletionSource();
        host.NextSurface = new Surface { Initialize = () => initialize.Task };
        var controller = new YouTubeLiveChatWindowController(host);
        var opening = controller.OpenYouTubeLiveChatAsync(host.State);
        var old = Assert.Single(host.Created);
        if (replaceWindow)
        {
            controller.CloseYouTubeLiveChatWindow(host.State.Id);
            await controller.OpenYouTubeLiveChatAsync(host.State);
            old.RaiseClosed();
            Assert.True(controller.ContainsWindow(host.State.Id));
        }
        initialize.SetException(new InvalidOperationException("initialize failed"));
        await opening;
        Assert.Equal(replaceWindow, controller.ContainsWindow(host.State.Id));
        Assert.Equal(1, old.Calls.Count(call => call == "close"));
        if (replaceWindow) Assert.DoesNotContain("close", host.Created[1].Calls);
        Assert.Contains("initialize failed", host.Status.Last());
    }

    [Fact]
    public async Task AvailabilityFollowsCurrentVideoAndClosingGate()
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        var surface = host.Created[0];
        surface.Calls.Clear();
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("other", true, null));
        host.IsClosingWindow = true;
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", true, null));
        Assert.Empty(surface.Calls);
        host.IsClosingWindow = false;
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", false, null));
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", true, null));
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", false, "replay"));
        Assert.Equal(new[] { "unavailable:abc123", "switch:abc123:/live_chat", "switch:abc123:/live_chat_replay" }, surface.Calls);
        host.Availability = new("abc123", true, null);
        await controller.RefreshYouTubeLiveChatAvailabilityAsync(host.State, null!);
        Assert.Equal("switch:abc123:/live_chat", surface.Calls.Last());
        host.Availability = null;
        var count = surface.Calls.Count;
        await controller.RefreshYouTubeLiveChatAvailabilityAsync(host.State, null!);
        Assert.Equal(count, surface.Calls.Count);
        controller.CloseAllYouTubeLiveChatWindows();
        await controller.ApplyYouTubeLiveChatAvailabilityAsync(host.State, new("abc123", true, null));
        Assert.Equal("close", surface.Calls.Last());
    }

    [Theory]
    [InlineData("closing")]
    [InlineData("removed")]
    [InlineData("old")]
    [InlineData("replaced-during-capture")]
    [InlineData("current")]
    public async Task ReplayCaptureRequiresOwnedCurrentRuntime(string mode)
    {
        var host = new Host();
        await new YouTubeLiveChatWindowController(host).OpenYouTubeLiveChatAsync(host.State);
        if (mode == "closing") host.IsClosingWindow = true;
        if (mode == "removed") host.HasState = false;
        if (mode == "old") host.Runtime = Host.CreateRuntime(host.State);
        if (mode == "replaced-during-capture") host.Capture = () =>
        {
            host.Runtime = Host.CreateRuntime(host.State);
            return Task.CompletedTask;
        };
        var result = await host.CapturePlayback!();
        Assert.Equal(mode == "current", result is not null);
        Assert.Equal(mode is "current" or "replaced-during-capture" ? 1 : 0, host.Captures);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("exit")]
    [InlineData("runtime")]
    [InlineData("window")]
    public async Task RenderCallbacksRequireOriginalWindowAndCurrentFullScreenRuntime(string mode)
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        await controller.OpenYouTubeLiveChatAsync(host.State);
        var old = host.Created[0];
        host.Runtime.IsFullScreen = true;
        controller.UpdateYouTubeLiveChatFullScreenOverlay(host.Runtime, true);
        controller.SynchronizeYouTubeLiveChatWindow(host.State.Id);
        Assert.Equal(2, host.Render.Count);
        if (mode == "exit") host.Runtime.IsFullScreen = false;
        if (mode == "runtime") host.Runtime = Host.CreateRuntime(host.State);
        if (mode == "window")
        {
            controller.CloseYouTubeLiveChatWindow(host.State.Id);
            await controller.OpenYouTubeLiveChatAsync(host.State);
        }
        foreach (var action in host.Render.Take(2)) action();
        Assert.Equal(mode == "current", old.Calls.Contains("enter"));
        Assert.Equal(mode == "current", old.Calls.Contains("bounds"));
        if (mode == "current") Assert.Equal(host.Bounds, old.Bounds);
        host.Runtime.IsFullScreen = false;
        controller.SynchronizeYouTubeLiveChatWindow(host.State.Id);
        controller.BringYouTubeLiveChatWindowToFront(host.State.Id);
        Assert.Equal(new[] { "synchronize", "follow" }, host.Created.Last().Calls.TakeLast(2));
    }

    [Fact]
    public async Task DiagnosticsAndCloseAllTrackLoadedOwnedAndPendingSurfaces()
    {
        var host = new Host();
        var controller = new YouTubeLiveChatWindowController(host);
        Assert.Equal((0, 0, 0), controller.GetDiagnosticSnapshot());
        await controller.OpenYouTubeLiveChatAsync(host.State);
        var first = host.Created[0];
        first.IsActive = true;
        Assert.True(controller.HasActiveYouTubeLiveChatWindow());
        Assert.Equal((1, 0, 0), controller.GetDiagnosticSnapshot());
        first.IsLoaded = false;
        Assert.Equal((0, 1, 0), controller.GetDiagnosticSnapshot());
        first.IsLoaded = true;
        host.Runtime.IsDisposed = true;
        Assert.Equal((1, 1, 0), controller.GetDiagnosticSnapshot());
        var invalidations = host.Invalidations;
        first.RaiseLayoutChanged();
        Assert.Equal(invalidations + 1, host.Invalidations);
        controller.CloseAllYouTubeLiveChatWindows();
        Assert.False(controller.HasActiveYouTubeLiveChatWindow());
        Assert.Equal((0, 0, 0), controller.GetDiagnosticSnapshot());
        host.Runtime.IsDisposed = false;
        var probe = new TaskCompletionSource<string>();
        host.Script = _ => probe.Task;
        var pending = controller.OpenYouTubeLiveChatAsync(host.State);
        host.HasRuntime = false;
        Assert.Equal((0, 0, 1), controller.GetDiagnosticSnapshot());
        controller.CloseAllYouTubeLiveChatWindows();
        Assert.Equal((0, 0, 0), controller.GetDiagnosticSnapshot());
        probe.SetResult(Live);
        await pending;
        Assert.Single(host.Created);
    }

    private sealed class Host : IYouTubeLiveChatHost
    {
        public YouTubeMiniPlayerState State { get; } = new() { Url = "https://www.youtube.com/watch?v=abc123" };
        public YouTubeMiniPlayerRuntime Runtime { get; set; }
        public string Source { get; set; } = "https://www.youtube.com/watch?v=abc123";
        public bool Ready { get; set; } = true;
        public bool HasRuntime { get; set; } = true;
        public bool HasState { get; set; } = true;
        public bool IsClosingWindow { get; set; }
        public List<string> Status { get; } = [];
        public List<bool?> Buttons { get; } = [];
        public List<Surface> Created { get; } = [];
        public List<Action> Render { get; } = [];
        public Func<string, Task<string>> Script { get; set; } = _ => Task.FromResult(Live);
        public Func<Task<CoreWebView2Environment>> Environment { get; set; } = () => Task.FromResult<CoreWebView2Environment>(null!);
        public Func<Task> Capture { get; set; } = () => Task.CompletedTask;
        public Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>>? CapturePlayback { get; private set; }
        public YouTubeLiveChatAvailabilityResult? Availability { get; set; }
        public Surface? NextSurface { get; set; }
        public Uri? Uri { get; private set; }
        public Rect Bounds { get; } = new(10, 20, 800, 600);
        public int ScriptCalls { get; private set; }
        public int Captures { get; private set; }
        public int RefreshQueued { get; private set; }
        public int Invalidations { get; private set; }
        public Host(Button? chatButton = null) => Runtime = CreateRuntime(State, chatButton);

        // Controllerが参照する状態だけを持ち、WPF/WebView2を生成しない。
        public static YouTubeMiniPlayerRuntime CreateRuntime(YouTubeMiniPlayerState state, Button? chatButton = null) => new(
            state, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, chatButton!, null!, null!, null!, null!, null!, null!,
            null!, null!, null, null);
        public bool ContainsState(YouTubeMiniPlayerState state) => HasState && ReferenceEquals(State, state);
        public bool TryGetReadySource(Guid id, out YouTubeLiveChatSource source)
        {
            source = new(Runtime, Source, script => { ScriptCalls++; return Script(script); });
            return Ready && HasRuntime && id == State.Id;
        }
        public bool TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime)
        {
            runtime = Runtime;
            return HasRuntime && id == State.Id;
        }
        public bool IsCurrentRuntime(Guid id, YouTubeMiniPlayerRuntime runtime)
            => HasRuntime && id == State.Id && ReferenceEquals(Runtime, runtime) && !runtime.IsDisposed;
        public bool IsCurrentVideo(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, string videoId)
            => IsCurrentRuntime(state.Id, runtime) && YouTubeUrlService.TryGetVideoId(state.Url, out var current) && current == videoId;
        public void QueueAvailabilityRefresh(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime) => RefreshQueued++;
        public Task<YouTubeLiveChatAvailabilityResult?> RefreshAvailabilityAsync(YouTubeMiniPlayerState state, WebView2 webView)
            => Task.FromResult(Availability);
        public Task CaptureStateAsync(YouTubeMiniPlayerState state) { Captures++; return Capture(); }
        public void UpdateChatButtonAvailability(YouTubeMiniPlayerRuntime runtime, bool? available)
        {
            Buttons.Add(available);
            if (runtime.TitleBandChatButton is not null)
                YouTubeMiniPlayerChromeFactory.UpdateChatButtonAvailability(runtime, available);
        }
        public void UpdateChatWindowOpen(YouTubeMiniPlayerRuntime runtime, bool isOpen)
        {
            if (runtime.TitleBandChatButton is not null)
                YouTubeMiniPlayerChromeFactory.UpdateChatWindowOpen(runtime, isOpen);
        }
        public void UpdateLiveViewerCount(YouTubeMiniPlayerRuntime runtime, bool isLive, string? text) { }
        public Task<CoreWebView2Environment> EnsureEnvironmentAsync(string? containerProfileId) => Environment();
        public IYouTubeLiveChatWindowSurface CreateWindow(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime,
            CoreWebView2Environment environment, Uri uri, Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>> capturePlayback)
        {
            Uri = uri;
            CapturePlayback = capturePlayback;
            var surface = NextSurface ?? new Surface();
            NextSurface = null;
            Created.Add(surface);
            return surface;
        }
        public void QueueSave() { }
        public void SetStatus(string message) => Status.Add(message);
        public void InvalidateArrangementPreviews(Guid id) => Invalidations++;
        public void QueueRender(YouTubeMiniPlayerRuntime runtime, Action action) => Render.Add(action);
        public Rect GetVisibleWindowBounds(YouTubeMiniPlayerRuntime runtime) => Bounds;
    }

    private sealed class Surface : IYouTubeLiveChatWindowSurface
    {
        public YouTubeLiveChatWindow? NativeWindow => null;
        public bool IsActive { get; set; }
        public bool IsLoaded { get; set; } = true;
        public event EventHandler? Closed;
        public event EventHandler? LayoutChanged;
        public List<string> Calls { get; } = [];
        public Rect Bounds { get; private set; }
        public Func<Task> Initialize { get; set; } = () => Task.CompletedTask;
        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
        public void RaiseLayoutChanged() => LayoutChanged?.Invoke(this, EventArgs.Empty);
        public void Show() => Calls.Add("show");
        public void Close() { Calls.Add("close"); RaiseClosed(); }
        public void BringToFront() => Calls.Add("bring");
        public void FollowSourceToFront() => Calls.Add("follow");
        public void BeginVideoTransition(string videoId) => Calls.Add("transition:" + videoId);
        public void ShowUnavailable(string videoId, string message = "この動画ではチャットを利用できません") => Calls.Add("unavailable:" + videoId);
        public Task InitializeAsync() { Calls.Add("initialize"); return Initialize(); }
        public Task SwitchVideoAsync(string videoId, Uri uri) { Calls.Add($"switch:{videoId}:{uri.AbsolutePath}"); return Task.CompletedTask; }
        public void SynchronizeWithSourceWindow() => Calls.Add("synchronize");
        public void ExitFullScreenOverlay() => Calls.Add("exit");
        public void EnterFullScreenOverlay(Rect bounds, bool topmost) { Bounds = bounds; Calls.Add("enter"); }
        public void UpdateFullScreenSourceBounds(Rect bounds, bool topmost) { Bounds = bounds; Calls.Add("bounds"); }
    }
}
