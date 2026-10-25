using System.Diagnostics;
using System.Text.Json;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeChannelRestoreTests
{
    private const string A = "abcdefghijk";
    private const string B = "lmnopqrstuv";
    private const string Channel = "UCabcdefghijklmnopqrstuv";
    private const string ChannelUrl = "https://www.youtube.com/channel/" + Channel + "/live";

    [Theory]
    [InlineData(false, B)]
    [InlineData(true, B)]
    [InlineData(true, "wxyz0123456")]
    public async Task ChannelUrlAfterTimeoutRestoresTheCurrentVideoInsteadOfThePreviousVideo(bool identityWasRead, string currentId)
    {
        var state = Original();
        var documents = new YouTubeMiniPlayerDocumentInitialization(); documents.Begin(1);
        var continuation = new YouTubeMiniPlayerLiveContinuationNavigation(A, Channel, new Uri(state.Url)) { NavigationId = 1 };
        Assert.True(continuation.TryBeginResolution());
        if (identityWasRead) Assert.True(continuation.TryReadCandidate(Identity(B), out _));
        var resolve = new TaskCompletionSource<YouTubeContinuationCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        YouTubeMiniPlayerState? restored = null;
        JsonElement applied = default;
        var initializations = 0;
        var changed = 0;
        var run = documents.CompleteAsync(1, () => true, () => resolve.Task, async current =>
        {
            initializations++;
            restored = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
                state, () => ChannelUrl, current, _ => identity.Task, () => changed++);
            if (restored is not null)
                applied = await ExecuteScript(YouTubeMiniPlayerStateRestoreService.BuildSavedStateScript(restored)!, ChannelUrl, currentId);
        });
        try
        {
            Assert.False(run.IsCompleted);
            Assert.True(continuation.TryTimeout(YouTubeMiniPlayerLiveContinuationNavigation.Timeout));
            resolve.SetResult(YouTubeContinuationCompletion.Expired);
            identity.SetResult(Identity(currentId));
            await run;
            Assert.True(applied.GetProperty("applied").GetBoolean());
            Assert.True(YouTubeUrlService.TryGetVideoId(state.Url, out var actualId));
            Assert.Equal(currentId, actualId);
            Assert.Equal(state.Url, restored!.Url);
            Assert.Null(restored.LastPositionSeconds);
            Assert.Null(restored.AbRepeatStartSeconds);
            Assert.Null(restored.AbRepeatEndSeconds);
            Assert.False(restored.IsAbRepeatEnabled);
            Assert.True(applied.GetProperty("muted").GetBoolean());
            Assert.Equal(0.4, applied.GetProperty("volume").GetDouble());
            Assert.Equal(1.5, applied.GetProperty("speed").GetDouble());
            Assert.DoesNotContain(applied.GetProperty("seeks").EnumerateArray(), value => value.GetDouble() == 42);
            Assert.Equal(JsonValueKind.Null, applied.GetProperty("abRepeat").ValueKind);
            Assert.Equal(1, changed);
            await documents.CompleteAsync(1, () => true, () => throw new Exception("二重判定"), _ => throw new Exception("二重初期化"));
            Assert.Equal(1, initializations);
        }
        finally
        {
            resolve.TrySetResult(YouTubeContinuationCompletion.Expired); identity.TrySetResult(null);
            await run;
        }
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("webview")]
    [InlineData("disposed")]
    [InlineData("closing")]
    [InlineData("source")]
    public async Task IdentityReturnedAfterDocumentInvalidationCannotChangeState(string invalidation)
    {
        var state = Original(); var source = ChannelUrl; var available = true;
        var documents = new YouTubeMiniPlayerDocumentInitialization(); documents.Begin(1);
        var identity = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        YouTubeMiniPlayerState? restored = null;
        var changed = 0;
        var run = documents.CompleteAsync(1, () => available,
            () => Task.FromResult(YouTubeContinuationCompletion.Expired), async current =>
            {
                restored = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
                    state, () => source, current, _ => identity.Task, () => changed++);
            });
        try
        {
            Assert.False(run.IsCompleted);
            if (invalidation == "navigation") documents.Begin(2);
            else if (invalidation == "source") source = "https://www.youtube.com/watch?v=" + B;
            else available = false;
            identity.SetResult(Identity(B));
            await run;
            Assert.Null(restored);
            Assert.Equal(0, changed);
            Assert.EndsWith(A, state.Url);
            Assert.Equal(42, state.LastPositionSeconds);
        }
        finally { identity.TrySetResult(null); await run; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"ok\":false}")]
    [InlineData("{\"ok\":true,\"videoId\":\"lmnopqrstuv\",\"channelId\":\"UC0000000000000000000000\"}")]
    public async Task UnknownOrDifferentChannelIdentityDoesNotCreateARestore(string? identity)
    {
        var state = Original();
        var result = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => ChannelUrl, () => true, _ => Task.FromResult(identity), () => throw new Exception("未確認の動画を採用"));
        Assert.Null(result);
        Assert.EndsWith(A, state.Url);
        Assert.Equal(42, state.LastPositionSeconds);
    }

    [Theory]
    [InlineData(B, B, true)]
    [InlineData(B, A, false)]
    [InlineData("", B, false)]
    public async Task ProductionIdentityScriptRequiresPlayerAndResponseToAgree(string playerId, string responseId, bool expected)
    {
        var read = await ExecuteScript(YouTubeMiniPlayerWebViewService.BuildCurrentDocumentVideoIdentityScript(),
            ChannelUrl, playerId, responseId: responseId);
        var identity = read.GetProperty("result");
        Assert.Equal(expected, identity.GetProperty("ok").GetBoolean());
        var state = Original();
        var snapshot = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => ChannelUrl, () => true, _ => Task.FromResult<string?>(identity.GetRawText()), () => { });
        Assert.Equal(expected, snapshot is not null);
    }

    [Theory]
    [InlineData("channel", B, true)]
    [InlineData("watch", B, true)]
    [InlineData("channel", A, false)]
    [InlineData("other-channel", B, false)]
    [InlineData("old-watch", B, false)]
    [InlineData("http", B, false)]
    [InlineData("other-host", B, false)]
    [InlineData("response-mismatch", B, false)]
    [InlineData("channel-mismatch", B, false)]
    public async Task RestoreBridgeChecksCurrentLocationPlayerAndChannel(string scenario, string playerId, bool expected)
    {
        var state = Original();
        var snapshot = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => ChannelUrl, () => true, _ => Task.FromResult<string?>(Identity(B)), () => { });
        var source = scenario switch
        {
            "watch" => "https://www.youtube.com/watch?v=" + B,
            "old-watch" => "https://www.youtube.com/watch?v=" + A,
            "other-channel" => ChannelUrl.Replace(Channel, "UC0000000000000000000000"),
            "http" => ChannelUrl.Replace("https:", "http:"),
            "other-host" => ChannelUrl.Replace("www.youtube.com", "other.example"),
            _ => ChannelUrl
        };
        var result = await ExecuteScript(YouTubeMiniPlayerStateRestoreService.BuildSavedStateScript(snapshot!)!, source, playerId,
            scenario == "channel-mismatch" ? "UC0000000000000000000000" : Channel,
            scenario == "response-mismatch" ? A : null);
        Assert.Equal(expected, result.GetProperty("applied").GetBoolean());
        if (!expected)
        {
            Assert.Equal(1, result.GetProperty("volume").GetDouble());
            Assert.Equal(1, result.GetProperty("speed").GetDouble());
            Assert.Empty(result.GetProperty("seeks").EnumerateArray());
        }
    }

    [Fact]
    public async Task IdentityChangeDuringApplicationStopsRemainingPlaybackChanges()
    {
        var state = Original();
        var snapshot = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => ChannelUrl, () => true, _ => Task.FromResult<string?>(Identity(B)), () => { });
        var result = await ExecuteScript(YouTubeMiniPlayerStateRestoreService.BuildSavedStateScript(snapshot!)!,
            ChannelUrl, B, switchAfterVolume: true);
        Assert.False(result.GetProperty("applied").GetBoolean());
        Assert.Equal(1, result.GetProperty("speed").GetDouble());
        Assert.Empty(result.GetProperty("seeks").EnumerateArray());
    }

    [Fact]
    public async Task APlainSnapshotCannotOptIntoChannelUrlRestoration()
    {
        var state = Original();
        var snapshot = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => ChannelUrl, () => true, _ => Task.FromResult<string?>(Identity(B)), () => { });
        var plain = YouTubeMiniPlayerNavigationService.CloneState(snapshot!);
        var result = await ExecuteScript(YouTubeMiniPlayerStateRestoreService.BuildSavedStateScript(plain)!, ChannelUrl, B);
        Assert.False(result.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task NormalWatchSnapshotKeepsItsOwnPositionAndAbRepeatWithoutQueryingIdentity()
    {
        var state = Original();
        var snapshot = await YouTubeMiniPlayerStateRestoreService.PrepareForDocumentAsync(
            state, () => state.Url, () => true, _ => throw new Exception("通常URLの不要な取得"), () => throw new Exception("同じ動画の変更"));
        Assert.Equal(state.Url, snapshot!.Url);
        Assert.Equal(42, snapshot.LastPositionSeconds);
        Assert.Equal(40, snapshot.AbRepeatStartSeconds);
        Assert.Equal(50, snapshot.AbRepeatEndSeconds);
        Assert.True(snapshot.IsAbRepeatEnabled);
    }

    private static YouTubeMiniPlayerState Original() => new()
    {
        Url = "https://www.youtube.com/watch?v=" + A,
        LastPositionSeconds = 42,
        AbRepeatStartSeconds = 40,
        AbRepeatEndSeconds = 50,
        IsAbRepeatEnabled = true,
        IsMuted = true,
        Volume = 0.4,
        PlaybackRate = 1.5
    };

    private static string Identity(string id, string channel = Channel)
        => JsonSerializer.Serialize(new { ok = true, videoId = id, channelId = channel });

    private static async Task<JsonElement> ExecuteScript(string script, string source, string playerId, string channel = Channel, string? responseId = null, bool switchAfterVolume = false)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(directory.FullName, "Tests", "TabBrowser.Tests", "YouTubeSavedState.fixture.cjs"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { script, source, playerId, channelId = channel, responseId, switchAfterVolume }));
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        using var result = JsonDocument.Parse(await output);
        return result.RootElement.Clone();
    }
}
