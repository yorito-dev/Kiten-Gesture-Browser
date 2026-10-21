using System.Text.Json;
using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeMiniPlayerNavigationServiceTests
{
    private const string Channel = "UCaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void LiveContinuationCreatesOnlyChannelLiveUrlAndPreservesSavedState()
    {
        var state = new YouTubeMiniPlayerState
        {
            Url = "https://www.youtube.com/watch?v=oldLive1234",
            IsMuted = true,
            Volume = 0.3,
            ContentZoomFactor = 2,
            IsPaused = true,
            LastPositionSeconds = 7200
        };
        Assert.True(YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
            state, state.Url, "oldLive1234", Channel, "token", "token", out var uri));
        Assert.Equal("https://www.youtube.com/channel/" + Channel + "/live", uri.AbsoluteUri);
        Assert.True(state.IsMuted); Assert.Equal(0.3, state.Volume); Assert.Equal(2, state.ContentZoomFactor);
        Assert.True(state.IsPaused); Assert.Equal(7200, state.LastPositionSeconds);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=changed1234", "UCaaaaaaaaaaaaaaaaaaaaaa", false, false)]
    [InlineData("https://example.com/watch?v=oldLive1234", "UCaaaaaaaaaaaaaaaaaaaaaa", false, false)]
    [InlineData("http://www.youtube.com/watch?v=oldLive1234", "UCaaaaaaaaaaaaaaaaaaaaaa", false, false)]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234", "bad&x=1", false, false)]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234", "UCshort", false, false)]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234", null, false, false)]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234", "UCaaaaaaaaaaaaaaaaaaaaaa", true, false)]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234", "UCaaaaaaaaaaaaaaaaaaaaaa", false, true)]
    public void LiveContinuationRejectsStaleInvalidAndRepeatingRequests(
        string source, string? channel, bool looping, bool abRepeat)
    {
        var state = new YouTubeMiniPlayerState
        {
            Url = "https://www.youtube.com/watch?v=oldLive1234",
            IsLooping = looping,
            IsAbRepeatEnabled = abRepeat
        };
        Assert.False(YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
            state, source, "oldLive1234", channel, "token", "token", out _));
    }

    [Theory]
    [InlineData("token", "wrong")]
    [InlineData("token", null)]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void LiveContinuationRequiresNonEmptyMatchingRuntimeToken(string? expected, string? token)
    {
        var state = new YouTubeMiniPlayerState { Url = "https://www.youtube.com/watch?v=oldLive1234" };
        Assert.False(YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
            state, state.Url, "oldLive1234", Channel, expected, token, out _));
    }

    [Fact]
    public void LiveContinuationRejectsLocalVideoAndChangedStateUrl()
    {
        var state = new YouTubeMiniPlayerState
        {
            SourceKind = MiniPlayerSourceKind.LocalFile,
            Url = "https://www.youtube.com/watch?v=oldLive1234"
        };
        Assert.False(YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
            state, state.Url, "oldLive1234", Channel, "token", "token", out _));
        state.SourceKind = MiniPlayerSourceKind.YouTube;
        state.Url = "https://www.youtube.com/watch?v=changed1234";
        Assert.False(YouTubeMiniPlayerNavigationService.TryCreateLiveContinuationUri(
            state, "https://www.youtube.com/watch?v=oldLive1234", "oldLive1234", Channel, "token", "token", out _));
    }

    [Fact]
    public void SameChannelDifferentLiveResolvesOnceWithoutReturning()
    {
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel,
            new Uri("https://www.youtube.com/watch?v=oldLive1234"));
        var json = JsonSerializer.Serialize(new
        {
            ok = true,
            isLive = true,
            isPlayable = true,
            isUpcoming = false,
            videoId = "newLive1234",
            channelId = Channel
        });
        var destination = "https://www.youtube.com/watch?v=newLive1234";
        Assert.True(attempt.TryBeginResolution());
        Assert.False(attempt.TryBeginResolution());
        Assert.True(attempt.TryResolve(destination, destination, json, out var resolution));
        Assert.Equal(YouTubeLiveContinuationAction.Continue, resolution.Action);
        Assert.Null(resolution.NavigationUri);
        Assert.False(attempt.TryResolve(destination, destination, null, out _));
        Assert.False(attempt.TryTimeout(TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("same-video")]
    [InlineData("not-live")]
    [InlineData("other-channel")]
    [InlineData("upcoming")]
    [InlineData("unplayable")]
    [InlineData("stale-player")]
    [InlineData("external")]
    [InlineData("http")]
    [InlineData("missing-result")]
    [InlineData("broken-result")]
    public void UnmatchedDestinationReturnsToOriginalOnlyOnce(string mode)
    {
        var original = new Uri("https://www.youtube.com/watch?v=oldLive1234&autoplay=1");
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel, original);
        var destination = mode switch
        {
            "channel-page" => "https://www.youtube.com/channel/" + Channel + "/live",
            "same-video" => original.AbsoluteUri,
            "external" => "https://example.com/watch?v=newLive1234",
            "http" => "http://www.youtube.com/watch?v=newLive1234",
            _ => "https://www.youtube.com/watch?v=newLive1234"
        };
        string? json = JsonSerializer.Serialize(new
        {
            ok = true,
            isLive = mode != "not-live",
            isPlayable = mode != "unplayable",
            isUpcoming = mode == "upcoming",
            videoId = mode == "stale-player" ? "oldLive1234" : "newLive1234",
            channelId = mode == "other-channel" ? "UCbbbbbbbbbbbbbbbbbbbbbb" : Channel
        });
        if (mode == "missing-result") json = null;
        if (mode == "broken-result") json = "{broken";
        Assert.True(attempt.TryResolve(destination, destination, json, out var resolution));
        Assert.Equal(YouTubeLiveContinuationAction.ReturnToOriginal, resolution.Action);
        Assert.Equal(original, resolution.NavigationUri);
        Assert.False(attempt.TryResolve(destination, destination, json, out _));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=newLive1234", "WatchPage")]
    [InlineData("https://www.youtube.com/watch?v=newLive1234&themeRefresh=1", "WatchPage")]
    [InlineData("https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live", "ChannelLivePage")]
    [InlineData("https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live?themeRefresh=1", "ChannelLivePage")]
    [InlineData("https://www.youtube.com/channel/UCbbbbbbbbbbbbbbbbbbbbbb/live", "Unsupported")]
    [InlineData("https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live/", "Unsupported")]
    [InlineData("http://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live", "Unsupported")]
    [InlineData("https://example.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live", "Unsupported")]
    [InlineData("https://youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live", "Unsupported")]
    [InlineData("https://www.youtube.com:444/channel/UCaaaaaaaaaaaaaaaaaaaaaa/live", "Unsupported")]
    [InlineData("https://www.youtube.com/watch?v=bad", "Unsupported")]
    public void ClassifiesOnlyRequestedHttpsLivePageOrWatchPage(string url, string expected)
    {
        Assert.Equal(expected, YouTubeMiniPlayerNavigationService.GetLiveContinuationDestinationKind(url, Channel).ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ChannelLivePageAndYoutubeWatchRewriteResolveTheSameLive(bool query, bool rewritten)
    {
        var original = new Uri("https://www.youtube.com/watch?v=oldLive1234");
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel, original);
        var livePage = "https://www.youtube.com/channel/" + Channel + "/live" + (query ? "?themeRefresh=1" : "");
        var current = rewritten ? "https://www.youtube.com/watch?v=newLive1234" : livePage;
        var json = LiveResult();
        Assert.True(attempt.TryBeginResolution());
        Assert.True(attempt.TryReadCandidate(json, out var id));
        Assert.Equal("newLive1234", id);
        Assert.False(attempt.TryReleaseForSourceChange(current));
        Assert.True(attempt.TryResolve(livePage, current, json, out var resolution));
        Assert.Equal(rewritten ? YouTubeLiveContinuationAction.Continue : YouTubeLiveContinuationAction.NavigateToWatch, resolution.Action);
        Assert.Equal(rewritten ? null : "https://www.youtube.com/watch?v=newLive1234&autoplay=1", resolution.NavigationUri?.AbsoluteUri);
        Assert.False(attempt.TryResolve(livePage, current, json, out _));
        Assert.False(attempt.TryBeginResolution());
    }

    [Theory]
    [InlineData("not-live")]
    [InlineData("upcoming")]
    [InlineData("unplayable")]
    [InlineData("same-video")]
    [InlineData("other-channel")]
    [InlineData("missing")]
    [InlineData("broken")]
    public void ChannelPageWithInvalidMetadataReturnsOnlyOnce(string mode)
    {
        var original = new Uri("https://www.youtube.com/watch?v=oldLive1234");
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel, original);
        var destination = "https://www.youtube.com/channel/" + Channel + "/live";
        var json = mode switch { "missing" => null, "broken" => "broken", _ => LiveResult(mode) };
        Assert.True(attempt.TryResolve(destination, destination, json, out var resolution));
        Assert.Equal(YouTubeLiveContinuationAction.ReturnToOriginal, resolution.Action);
        Assert.Equal(original, resolution.NavigationUri);
        Assert.False(attempt.TryResolve(destination, destination, json, out _));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=otherId1234")]
    [InlineData("https://www.youtube.com/watch?v=oldLive1234")]
    [InlineData("https://www.youtube.com/channel/UCbbbbbbbbbbbbbbbbbbbbbb/live")]
    [InlineData("https://example.com/")]
    public void SourceChangeDuringAwaitReleasesWithoutNavigationOrNotice(string current)
    {
        var destination = "https://www.youtube.com/channel/" + Channel + "/live";
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel,
            new Uri("https://www.youtube.com/watch?v=oldLive1234"));
        Assert.True(attempt.TryBeginResolution());
        Assert.True(attempt.TryReadCandidate(LiveResult(), out _));
        Assert.True(attempt.TryResolve(destination, current, LiveResult(), out var resolution));
        Assert.Equal(YouTubeLiveContinuationAction.ReleaseOnly, resolution.Action);
        Assert.Null(resolution.NavigationUri);
        Assert.False(attempt.TryBeginResolution());
    }

    [Fact]
    public void SourceRewriteDuringIdentityReadIsCheckedAgainstConfirmedVideo()
    {
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel,
            new Uri("https://www.youtube.com/watch?v=oldLive1234"));
        Assert.True(attempt.TryBeginResolution());
        Assert.False(attempt.TryReleaseForSourceChange("https://www.youtube.com/watch?v=newLive1234"));
        Assert.True(attempt.TryReadCandidate(LiveResult(), out _));
        Assert.False(attempt.TryReleaseForSourceChange("https://www.youtube.com/watch?v=newLive1234"));
        Assert.True(attempt.TryReleaseForSourceChange("https://www.youtube.com/watch?v=otherId1234"));
        Assert.False(attempt.TryResolve("https://www.youtube.com/channel/" + Channel + "/live",
            "https://www.youtube.com/watch?v=newLive1234", LiveResult(), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThirtySecondTimeoutReleasesEvenWithoutCompletionAndIgnoresLateResult(bool started)
    {
        var attempt = new YouTubeMiniPlayerLiveContinuationNavigation("oldLive1234", Channel,
            new Uri("https://www.youtube.com/watch?v=oldLive1234"));
        if (started) Assert.True(attempt.TryBeginResolution());
        Assert.False(attempt.TryTimeout(TimeSpan.FromMilliseconds(29999)));
        Assert.True(attempt.TryTimeout(TimeSpan.FromSeconds(30)));
        Assert.False(attempt.TryTimeout(TimeSpan.FromSeconds(31)));
        Assert.False(attempt.TryBeginResolution());
        Assert.False(attempt.TryResolve("https://www.youtube.com/channel/" + Channel + "/live",
            "https://www.youtube.com/watch?v=newLive1234", LiveResult(), out _));
    }

    private static string LiveResult(string mode = "live") => JsonSerializer.Serialize(new
    {
        ok = true,
        isLive = mode != "not-live",
        isPlayable = mode != "unplayable",
        isUpcoming = mode == "upcoming",
        videoId = mode == "same-video" ? "oldLive1234" : "newLive1234",
        channelId = mode == "other-channel" ? "UCbbbbbbbbbbbbbbbbbbbbbb" : Channel
    });

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", false)]
    [InlineData("https://www.youtube.com/shorts/abc123?feature=share", true)]
    public void TryCreateMiniPlayerUrlCreatesWatchAutoplayUrl(string url, bool expectedShorts)
    {
        var result = YouTubeMiniPlayerNavigationService.TryCreateMiniPlayerUrl(
            url,
            out var miniPlayerUrl,
            out var isShorts);

        Assert.True(result);
        Assert.Equal("https://www.youtube.com/watch?v=abc123&autoplay=1", miniPlayerUrl);
        Assert.Equal(expectedShorts, isShorts);
    }

    [Fact]
    public void CreateNavigationUrlUsesPausedAutoplaySettingAndNormalizesStateUrl()
    {
        var state = new YouTubeMiniPlayerState
        {
            Url = "https://www.youtube.com/shorts/abc123",
            LastPositionSeconds = 42.9,
            IsPaused = true
        };

        var result = YouTubeMiniPlayerNavigationService.CreateNavigationUrl(state);

        Assert.Equal("https://www.youtube.com/watch?v=abc123&t=42s&autoplay=0", result);
        Assert.Equal("https://www.youtube.com/watch?v=abc123&autoplay=1", state.Url);
    }

    [Fact]
    public void TryApplyCurrentVideoUrlUpdatesVideoSpecificStateOnlyForNextVideo()
    {
        var state = new YouTubeMiniPlayerState
        {
            SourceKind = MiniPlayerSourceKind.YouTube,
            Url = "https://www.youtube.com/watch?v=first&autoplay=1",
            IsShorts = true,
            VideoAspectRatio = 9.0 / 16.0,
            LastPositionSeconds = 120,
            DurationSeconds = 300,
            IsLiveBroadcast = true,
            IsAtLiveEdge = true,
            AbRepeatStartSeconds = 10,
            AbRepeatEndSeconds = 20,
            IsAbRepeatEnabled = true,
            Volume = 0.4,
            PlaybackRate = 1.5,
            ContentZoomFactor = 2
        };

        var result = YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(
            state,
            "https://www.youtube.com/watch?v=next&list=example");

        Assert.True(result);
        Assert.Equal("https://www.youtube.com/watch?v=next&autoplay=1", state.Url);
        Assert.False(state.IsShorts);
        Assert.Null(state.VideoAspectRatio);
        Assert.Null(state.LastPositionSeconds);
        Assert.Null(state.DurationSeconds);
        Assert.False(state.IsLiveBroadcast);
        Assert.False(state.IsAtLiveEdge);
        Assert.Null(state.AbRepeatStartSeconds);
        Assert.Null(state.AbRepeatEndSeconds);
        Assert.False(state.IsAbRepeatEnabled);
        Assert.Equal(0.4, state.Volume);
        Assert.Equal(1.5, state.PlaybackRate);
        Assert.Equal(2, state.ContentZoomFactor);
    }

    [Fact]
    public void TryApplyCurrentVideoUrlIgnoresQueryChangeForSameVideo()
    {
        var state = new YouTubeMiniPlayerState
        {
            SourceKind = MiniPlayerSourceKind.YouTube,
            Url = "https://www.youtube.com/watch?v=abc123&autoplay=1",
            VideoAspectRatio = 4.0 / 3.0,
            LastPositionSeconds = 42
        };

        var result = YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(
            state,
            "https://www.youtube.com/watch?v=abc123&t=42s");

        Assert.False(result);
        Assert.Equal(4.0 / 3.0, state.VideoAspectRatio);
        Assert.Equal(42, state.LastPositionSeconds);
    }

    [Fact]
    public void NavigationHistoryControllerExecutesOnlyAvailableDirectionOnce()
    {
        var controller = new YouTubeMiniPlayerNavigationHistoryController();
        var backCount = 0;
        var forwardCount = 0;
        controller.Update(canGoBack: true, canGoForward: false);

        var wentBack = controller.TryGoBack(() => backCount++);
        var wentForward = controller.TryGoForward(() => forwardCount++);

        Assert.True(wentBack);
        Assert.False(wentForward);
        Assert.Equal(1, backCount);
        Assert.Equal(0, forwardCount);
    }

    [Fact]
    public void NavigationHistoryControllerReflectsForwardHistoryAfterGoingBack()
    {
        var controller = new YouTubeMiniPlayerNavigationHistoryController();
        var backCount = 0;
        var forwardCount = 0;
        controller.Update(canGoBack: true, canGoForward: false);
        Assert.True(controller.TryGoBack(() => backCount++));

        controller.Update(canGoBack: false, canGoForward: true);
        var wentBackAgain = controller.TryGoBack(() => backCount++);
        var wentForward = controller.TryGoForward(() => forwardCount++);

        Assert.False(wentBackAgain);
        Assert.True(wentForward);
        Assert.Equal(1, backCount);
        Assert.Equal(1, forwardCount);
    }

    [Fact]
    public void CreateReturnTabUrlPreservesShortsAndPlaybackState()
    {
        var state = new YouTubeMiniPlayerState
        {
            IsShorts = true,
            IsPaused = true,
            LastPositionSeconds = 12.9
        };

        var result = YouTubeMiniPlayerNavigationService.CreateReturnTabUrl(state, "abc123");

        Assert.Equal("https://www.youtube.com/shorts/abc123?t=12s&autoplay=0", result);
    }

    [Fact]
    public void NormalizeTitleTrimsYoutubeSuffixAndFallsBackToVideoId()
    {
        Assert.Equal(
            "Example Title",
            YouTubeMiniPlayerNavigationService.NormalizeTitle("  Example   Title - YouTube  ", "https://www.youtube.com/watch?v=abc123"));
        Assert.Equal(
            "abc123",
            YouTubeMiniPlayerNavigationService.NormalizeTitle("   ", "https://www.youtube.com/watch?v=abc123"));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", "11111111111111111111111111111111", true)]
    [InlineData("https://www.youtube.com/shorts/abc123", "11111111111111111111111111111111", true)]
    [InlineData("https://www.youtube.com/watch?v=different", "11111111111111111111111111111111", false)]
    [InlineData("https://example.com/after-navigation", "11111111111111111111111111111111", false)]
    [InlineData("https://www.youtube.com/watch?v=abc123", "22222222222222222222222222222222", false)]
    public void IsMatchingSourceTabRequiresSameVideoAndContainer(
        string tabUrl,
        string tabContainerProfileId,
        bool expected)
    {
        var state = new YouTubeMiniPlayerState
        {
            Url = "https://www.youtube.com/watch?v=abc123&autoplay=1",
            ContainerProfileId = "11111111111111111111111111111111"
        };
        var tab = new BrowserTab
        {
            Url = tabUrl,
            ContainerProfileId = tabContainerProfileId
        };

        Assert.Equal(expected, YouTubeMiniPlayerNavigationService.IsMatchingSourceTab(state, tab));
    }

    [Fact]
    public void FindMatchingSourceTabIgnoresTrackedTabAfterNavigation()
    {
        var previousSourceTab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com/after-navigation",
            ContainerProfileId = "11111111111111111111111111111111"
        };
        var existingVideoTab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            Url = "https://www.youtube.com/shorts/abc123",
            ContainerProfileId = "11111111111111111111111111111111"
        };
        var state = new YouTubeMiniPlayerState
        {
            SourceTabId = previousSourceTab.Id,
            Url = "https://www.youtube.com/watch?v=abc123&autoplay=1",
            ContainerProfileId = "11111111111111111111111111111111"
        };

        var result = YouTubeMiniPlayerNavigationService.FindMatchingSourceTab(
            state,
            [previousSourceTab, existingVideoTab]);

        Assert.Same(existingVideoTab, result);
    }

    [Fact]
    public async Task RecoveryFallbackPreparesMatchingTabWithoutCapturingBrokenRuntime()
    {
        var tab = new BrowserTab
        {
            Id = Guid.NewGuid(),
            Url = "https://www.youtube.com/watch?v=abc123",
            ContainerProfileId = "work"
        };
        var state = new YouTubeMiniPlayerState
        {
            SourceTabId = tab.Id,
            Url = "https://www.youtube.com/watch?v=abc123&autoplay=1",
            ContainerProfileId = "work"
        };
        var captured = false;
        var opened = false;
        var prepared = false;
        var selected = false;
        var activated = false;

        var result = await YouTubeMiniPlayerSourceTabService.ActivateAsync(
            state,
            [tab],
            _ =>
            {
                captured = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                opened = true;
                return new BrowserTab();
            },
            selectedTab => selected = ReferenceEquals(tab, selectedTab),
            () =>
            {
                activated = true;
                return Task.CompletedTask;
            },
            () => { },
            _ => { },
            (preparedTab, preparedState) =>
                prepared = ReferenceEquals(tab, preparedTab) && ReferenceEquals(state, preparedState),
            captureBeforeReopen: false);

        Assert.True(result);
        Assert.False(captured);
        Assert.False(opened);
        Assert.True(prepared);
        Assert.True(selected);
        Assert.True(activated);
    }

    [Fact]
    public async Task RecoveryFallbackReopensMissingTabWithPlaybackState()
    {
        var state = new YouTubeMiniPlayerState
        {
            Url = "https://www.youtube.com/watch?v=abc123&autoplay=1",
            Title = "Title",
            ContainerProfileId = "work",
            IsMuted = true,
            LastPositionSeconds = 42,
            PlaybackRate = 1.5,
            IsPaused = true,
            IsLooping = false
        };
        YouTubeMiniPlayerSourceTabOpenRequest? request = null;
        var reopened = new BrowserTab
        {
            Url = state.Url,
            ContainerProfileId = state.ContainerProfileId
        };

        var result = await YouTubeMiniPlayerSourceTabService.ActivateAsync(
            state,
            [],
            _ => throw new InvalidOperationException("壊れたRuntimeを再取得してはいけません"),
            currentRequest =>
            {
                request = currentRequest;
                return reopened;
            },
            _ => { },
            () => Task.CompletedTask,
            () => { },
            _ => { },
            captureBeforeReopen: false);

        Assert.True(result);
        Assert.NotNull(request);
        Assert.Equal(42, request.Value.PositionSeconds);
        Assert.Equal(1.5, request.Value.PlaybackSpeed);
        Assert.True(request.Value.IsMuted);
        Assert.True(request.Value.IsPaused);
        Assert.False(request.Value.IsLooping);
        Assert.Equal("work", request.Value.ContainerProfileId);
    }

    [Fact]
    public void CloneStateCopiesMiniPlayerPersistenceFields()
    {
        var state = new YouTubeMiniPlayerState
        {
            Id = Guid.NewGuid(),
            WindowId = 3,
            OwnerSessionWindowId = Guid.NewGuid(),
            SourceTabId = Guid.NewGuid(),
            ContainerProfileId = "work",
            Url = "https://www.youtube.com/watch?v=abc123",
            Title = "Title",
            IsShorts = true,
            Left = 10,
            Top = 20,
            Width = 300,
            Height = 500,
            ContentZoomFactor = 2.5,
            ContentPanX = 0.4,
            ContentPanY = -0.3,
            IsMuted = true,
            Volume = 0.2,
            AudioBoostDecibels = 9,
            PlaybackRate = 1.25,
            LastPositionSeconds = 55,
            IsPaused = true,
            IsLooping = false,
            AbRepeatStartSeconds = 10,
            AbRepeatEndSeconds = 20,
            IsAbRepeatEnabled = true,
            IsMinimized = true,
            IsTopmost = true,
            ListOrder = 4,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var clone = YouTubeMiniPlayerNavigationService.CloneState(state);

        Assert.NotSame(state, clone);
        Assert.Equal(state.Id, clone.Id);
        Assert.Equal(state.WindowId, clone.WindowId);
        Assert.Equal(state.OwnerSessionWindowId, clone.OwnerSessionWindowId);
        Assert.Equal(state.SourceTabId, clone.SourceTabId);
        Assert.Equal(state.ContainerProfileId, clone.ContainerProfileId);
        Assert.Equal(state.Url, clone.Url);
        Assert.Equal(state.Title, clone.Title);
        Assert.Equal(state.IsShorts, clone.IsShorts);
        Assert.Equal(state.Left, clone.Left);
        Assert.Equal(state.Top, clone.Top);
        Assert.Equal(state.Width, clone.Width);
        Assert.Equal(state.Height, clone.Height);
        Assert.Equal(state.ContentZoomFactor, clone.ContentZoomFactor);
        Assert.Equal(state.ContentPanX, clone.ContentPanX);
        Assert.Equal(state.ContentPanY, clone.ContentPanY);
        Assert.Equal(state.IsMuted, clone.IsMuted);
        Assert.Equal(state.Volume, clone.Volume);
        Assert.Equal(state.AudioBoostDecibels, clone.AudioBoostDecibels);
        Assert.Equal(state.PlaybackRate, clone.PlaybackRate);
        Assert.Equal(state.LastPositionSeconds, clone.LastPositionSeconds);
        Assert.Equal(state.IsPaused, clone.IsPaused);
        Assert.Equal(state.IsLooping, clone.IsLooping);
        Assert.Equal(state.AbRepeatStartSeconds, clone.AbRepeatStartSeconds);
        Assert.Equal(state.AbRepeatEndSeconds, clone.AbRepeatEndSeconds);
        Assert.Equal(state.IsAbRepeatEnabled, clone.IsAbRepeatEnabled);
        Assert.Equal(state.IsMinimized, clone.IsMinimized);
        Assert.Equal(state.IsTopmost, clone.IsTopmost);
        Assert.Equal(state.ListOrder, clone.ListOrder);
        Assert.Equal(state.CreatedAt, clone.CreatedAt);
        Assert.Equal(state.UpdatedAt, clone.UpdatedAt);
    }
}
