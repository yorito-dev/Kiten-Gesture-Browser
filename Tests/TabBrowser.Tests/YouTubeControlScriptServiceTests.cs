using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class YouTubeControlScriptServiceTests
{
    [Theory]
    [InlineData("{\"ok\":false,\"skipped\":true,\"reason\":\"advertisement\"}", true)]
    [InlineData("{\"ok\":false,\"reason\":\"not-live\"}", false)]
    [InlineData("{\"ok\":true}", false)]
    [InlineData("{\"reason\":3}", false)]
    [InlineData("null", false)]
    [InlineData("broken", false)]
    [InlineData(null, false)]
    public void AdvertisementBlockedResultDistinguishesOtherFailures(string? json, bool expected)
    {
        Assert.Equal(expected, YouTubeControlScriptService.IsAdvertisementBlockedResult(json));
    }

    [Fact]
    public void AdvertisementGuardIsSharedByRestoreSpeedSeekAndAbRepeat()
    {
        var scripts = new[]
        {
            YouTubeControlScriptService.BuildPlaybackStateRestoreScript(30, 1.5, true, true, true),
            YouTubeControlScriptService.BuildSpeedScript(0.25, null),
            YouTubeControlScriptService.BuildSeekRelativeScript(10),
            YouTubeControlScriptService.BuildSeekToRatioScript(0.5),
            YouTubeControlScriptService.BuildSeekToLiveEdgeScript(),
            YouTubeControlScriptService.BuildAbRepeatScript(10, 20, true, false, "fixture-video")
        };
        Assert.All(scripts, script =>
        {
            Assert.Contains("contains(\"ad-showing\")", script);
            Assert.Contains("contains(\"ad-interrupting\")", script);
            Assert.DoesNotContain("{{AdvertisementPlayingExpression}}", script);
        });
        var saved = YouTubeControlScriptService.BuildMiniPlayerSavedStateReadyBridgeScript(
            "fixture-video", 30, 1.5, true, true, true, 0.4, null, null, false);
        Assert.Contains("if (playerVideoId !== expectedVideoId)", saved);
        Assert.Contains("attributeFilter: [\"class\"]", saved);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1.0, null)]
    [InlineData(0.0, 0.0)]
    [InlineData(12.5, 12.5)]
    public void NormalizePlaybackPositionKeepsOnlyNonNegativeFiniteValues(double? value, double? expected)
    {
        Assert.Equal(expected, YouTubeControlScriptService.NormalizePlaybackPosition(value));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(-1.0, null)]
    [InlineData(0.0, null)]
    [InlineData(0.1, 0.25)]
    [InlineData(1.25, 1.25)]
    [InlineData(3.0, 2.0)]
    public void NormalizePlaybackSpeedKeepsSpeedWithinSupportedRange(double? value, double? expected)
    {
        Assert.Equal(expected, YouTubeControlScriptService.NormalizePlaybackSpeed(value));
    }

    [Fact]
    public void ResolvePlaybackSpeedToggleTargetMovesFromNonDefaultSpeedToDefault()
    {
        var target = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(1.5, 1.25, out var previous);

        Assert.Equal(1.0, target);
        Assert.Equal(1.5, previous);
    }

    [Fact]
    public void ResolvePlaybackSpeedToggleTargetRestoresRememberedSpeedFromDefault()
    {
        var target = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(1.0, 1.75, out var previous);

        Assert.Equal(1.75, target);
        Assert.Equal(1.75, previous);
    }

    [Fact]
    public void ResolvePlaybackSpeedToggleTargetKeepsDefaultWithoutRememberedSpeed()
    {
        var target = YouTubeControlScriptService.ResolvePlaybackSpeedToggleTarget(1.0, null, out var previous);

        Assert.Equal(1.0, target);
        Assert.Null(previous);
    }

    [Fact]
    public void BuildSpeedScriptUsesConfiguredSpeedBounds()
    {
        var script = YouTubeControlScriptService.BuildSpeedScript(0.25, null);

        Assert.Contains("const minimum = 0.25;", script);
        Assert.Contains("const maximum = 2;", script);
        Assert.Contains("const delta = 0.25;", script);
    }

    [Fact]
    public void BuildPausedResumeGuardScriptEmbedsVideoIdAsJsonString()
    {
        var script = YouTubeControlScriptService.BuildPausedResumeGuardScript("abc\"123");

        Assert.Contains("""const expectedVideoId = "abc\u0022123";""", script);
        Assert.Contains("__tabBrowserYouTubePausedResumeGuard", script);
        Assert.Contains("if (event?.isTrusted && isExpectedVideo())", script);
        Assert.Contains("player.pauseVideo();", script);
        Assert.Contains("Promise.reject(new DOMException(", script);
        Assert.Contains("\"NotAllowedError\"", script);
        Assert.DoesNotContain("return Promise.resolve();", script);
    }

    [Fact]
    public void BuildPlaybackStateBridgeScriptTracksPositionWithoutContinuousPosting()
    {
        var script = YouTubeControlScriptService.BuildPlaybackStateBridgeScript("test-token", isActive: false);

        Assert.Contains("bridgeVersion = 8", script);
        Assert.Contains("const initialActive = false", script);
        Assert.Contains("minPositionUpdateIntervalMilliseconds: 1500", script);
        Assert.Contains("window.__tabBrowserPostYouTubePlaybackState", script);
        Assert.Contains("nativePost.call(window.chrome.webview", script);
        Assert.Contains("tabBrowserToken: webMessageToken", script);
        Assert.Contains("const webMessageToken = \"test-token\"", script);
        Assert.Contains("reason: \"untrusted-origin\"", script);
        Assert.True(
            script.IndexOf("const nativePost = window.chrome?.webview?.postMessage;", StringComparison.Ordinal)
            < script.IndexOf("const hostPost = window.__tabBrowserPostYouTubePlaybackState;", StringComparison.Ordinal));
        Assert.Contains("looping: Boolean(video.loop)", script);
        Assert.Contains("theaterMode: isTheaterMode()", script);
        Assert.Contains("""state.theaterObserver.observe(theaterElement, { attributes: true, attributeFilter: ["theater"] });""", script);
        Assert.Contains("if (state.active)", script);
        Assert.Contains("send(false, \"timeupdate\")", script);
        Assert.Contains("stopVideoDiscovery();", script);
        Assert.Contains("document.addEventListener(\"loadedmetadata\", onMediaReady, true);", script);
        Assert.Contains("document.addEventListener(\"yt-navigate-finish\", onNavigateFinish, true);", script);
        Assert.Contains("setActive", script);
        Assert.Contains("send(true, \"deactivate\")", script);
        Assert.DoesNotContain("state.observer = new MutationObserver", script);
        Assert.Contains("document.addEventListener(\"visibilitychange\", () => send(true, \"visibilitychange\"), true);", script);
        Assert.Contains("window.addEventListener(\"pagehide\", () => send(true, \"pagehide\"), true);", script);
        Assert.Contains("window.addEventListener(\"beforeunload\", () => send(true, \"beforeunload\"), true);", script);
    }

    [Fact]
    public void PlaybackStateScriptsPreferPlayerConfiguredSpeedAndUseActiveVideoAsFallback()
    {
        var readScript = YouTubeControlScriptService.BuildPlaybackStateReadScript();
        var bridgeScript = YouTubeControlScriptService.BuildPlaybackStateBridgeScript("test-token");

        Assert.Contains("const getActiveVideo = () => player?.querySelector(\"video.html5-main-video\")", readScript);
        Assert.Contains("|| player?.querySelector(\"video.video-stream\")", readScript);
        Assert.Contains("|| player?.querySelector(\"video\")", readScript);
        Assert.Contains("const video = getActiveVideo();", readScript);
        Assert.Contains("typeof player.getPlaybackRate === \"function\"", readScript);
        Assert.Contains("? Number(player.getPlaybackRate())", readScript);
        Assert.Contains("? playerSpeed", readScript);
        Assert.Contains("const videoSpeed = Number(video.playbackRate);", readScript);
        Assert.Contains("? videoSpeed", readScript);
        Assert.Contains(": null;", readScript);

        Assert.Contains("const getActiveVideo = () =>", bridgeScript);
        Assert.Contains("const video = getActiveVideo();", bridgeScript);
        Assert.Contains("const player = getPlayer();", bridgeScript);
        Assert.Contains("typeof player.getPlaybackRate === \"function\"", bridgeScript);
        Assert.Contains("? Number(player.getPlaybackRate())", bridgeScript);
        Assert.Contains("return playerSpeed;", bridgeScript);
        Assert.Contains("const videoSpeed = Number(video?.playbackRate);", bridgeScript);
        Assert.Contains("const attach = () =>", bridgeScript);
        Assert.Contains("if (event.target === getActiveVideo())", bridgeScript);
        Assert.DoesNotContain("attach(node)", bridgeScript);
        Assert.DoesNotContain("attach(video)", bridgeScript);
    }

    [Fact]
    public void BuildPlaybackStateRestoreReadyBridgeUsesMediaReadinessWithoutTimers()
    {
        var script = YouTubeControlScriptService.BuildPlaybackStateRestoreReadyBridgeScript(
            "test-token",
            "abc\"123",
            10,
            1.25,
            true,
            false,
            true);

        Assert.Contains("const expectedVideoId = \"abc\\u0022123\";", script);
        Assert.Contains("__tabBrowserYouTubePlaybackStateRestoreReadyBridge", script);
        Assert.Contains("readPlayerVideoId", script);
        Assert.Contains("video.readyState >= HTMLMediaElement.HAVE_METADATA", script);
        Assert.Contains("document.addEventListener(\"loadedmetadata\", state.attempt, true);", script);
        Assert.Contains("document.addEventListener(\"canplay\", state.attempt, true);", script);
        Assert.Contains("document.addEventListener(\"yt-navigate-finish\", state.attempt, true);", script);
        Assert.Contains("attributeFilter: [\"theater\"]", script);
        Assert.Contains("window.__tabBrowserPostYouTubePlaybackState", script);
        Assert.Contains("nativePost.call(window.chrome.webview", script);
        Assert.Contains("tabBrowserToken: webMessageToken", script);
        Assert.Contains("const webMessageToken = \"test-token\"", script);
        Assert.Contains("reason: \"untrusted-origin\"", script);
        Assert.True(
            script.IndexOf("const nativePost = window.chrome?.webview?.postMessage;", StringComparison.Ordinal)
            < script.IndexOf("const hostPost = window.__tabBrowserPostYouTubePlaybackState;", StringComparison.Ordinal));
        Assert.Contains("if (!postPlaybackState({", script);
        Assert.True(
            script.IndexOf("if (!postPlaybackState({", StringComparison.Ordinal)
            < script.IndexOf("state.applied = true;", StringComparison.Ordinal));
        Assert.Contains("reason: \"restore\"", script);
        Assert.Contains("typeof player.seekTo === \"function\"", script);
        Assert.Contains("player.seekTo(position, true);", script);
        Assert.DoesNotContain("setTimeout", script);
        Assert.DoesNotContain("setInterval", script);
    }

    [Fact]
    public void BuildMiniPlayerSavedStateReadyBridgeRestoresAllStateOnMediaReadiness()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerSavedStateReadyBridgeScript(
            "abc123",
            30,
            1.5,
            false,
            true,
            true,
            0.4,
            10,
            20,
            true);

        Assert.Contains("__tabBrowserYouTubeMiniPlayerSavedStateReadyBridge", script);
        Assert.Contains("video.readyState >= HTMLMediaElement.HAVE_METADATA", script);
        Assert.Contains("document.addEventListener(\"loadedmetadata\", state.attempt, true)", script);
        Assert.Contains("document.addEventListener(\"canplay\", state.attempt, true)", script);
        Assert.Contains("document.addEventListener(\"playing\", state.attempt, true)", script);
        Assert.Contains("respectMiniPlayerUserOverride", script);
        Assert.Contains("const unmuteOnVolumeChange = false;", script);
        Assert.Contains("const finalMuteResult = await", script);
        Assert.DoesNotContain("})(););", script);
        Assert.Contains("__tabBrowserYouTubeMiniPlayerAbRepeat", script);
        Assert.Contains("const targetPosition = 30;", script);
        Assert.Contains("player.seekTo(position, true);", script);
        Assert.DoesNotContain("setInterval", script);
    }

    [Fact]
    public void BuildPlaybackHealthProbeScriptReadsMediaHealthWithoutPolling()
    {
        var script = YouTubeControlScriptService.BuildPlaybackHealthProbeScript();

        Assert.Contains("document.querySelector(\"video\")", script);
        Assert.Contains("hasVisibleVideo", script);
        Assert.Contains("videoNaturalWidth", script);
        Assert.Contains("readyState", script);
        Assert.Contains("networkState", script);
        Assert.Contains("bufferedAhead", script);
        Assert.Contains("mediaError", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("setTimeout", script);
    }

    [Fact]
    public void BuildPlayerRectScriptCoalescesPointerWorkAndPostsLowFrequencyKeepalive()
    {
        var script = YouTubeControlScriptService.BuildPlayerRectScript("test-token")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("document.addEventListener(\"pointermove\", onPointerMove, true)", script);
        Assert.Contains("document.addEventListener(\"pointerover\", onPointerOver, true)", script);
        Assert.Contains("document.addEventListener(\"pointerout\", onPointerOut, true)", script);
        Assert.Contains("window.requestAnimationFrame(processPointerMove)", script);
        Assert.Contains("window.cancelAnimationFrame(state.pointerFrameRequestId)", script);
        Assert.Contains("let pendingPointerTarget = null", script);
        Assert.Contains("let shouldRecoverPointerTargetOnMove = true", script);
        Assert.Contains("const isYouTubeHost = host === \"youtube.com\"", script);
        Assert.Contains("host.endsWith(\".youtube-nocookie.com\")", script);
        Assert.Contains("pendingPointerTarget = event.target", script);
        Assert.Contains("!state.mouseOverPlayer && shouldRecoverPointerTargetOnMove", script);
        Assert.Contains("shouldRecoverPointerTargetOnMove = false", script);
        Assert.Contains("if (isPlayerTarget(pendingPointerTarget))", script);
        var navigationFinishedHandler = script.IndexOf("const onNavigateFinish = () => {", StringComparison.Ordinal);
        var recoveryReset = script.IndexOf(
            "shouldRecoverPointerTargetOnMove = true;",
            navigationFinishedHandler,
            StringComparison.Ordinal);
        var listenerSync = script.IndexOf("syncPointerListeners();", recoveryReset, StringComparison.Ordinal);
        Assert.True(navigationFinishedHandler >= 0 && recoveryReset > navigationFinishedHandler && listenerSync > recoveryReset);
        Assert.Contains("resumesAfterScroll ? \"pointer-resume\" : \"pointer-enter\"", script);
        Assert.Contains("state.mouseActivityAt = Date.now();", script);
        Assert.Contains("lastActivityPostedAt: Number.NEGATIVE_INFINITY", script);
        Assert.Contains("lastScrollActivityPostedAt: Number.NEGATIVE_INFINITY", script);
        Assert.Contains("now - state.lastActivityPostedAt < 2500", script);
        Assert.Contains("now - state.lastScrollActivityPostedAt < 400", script);
        Assert.Contains("postActivity(\"pointer-enter\", true)", script);
        Assert.Contains("postActivity(\"pointer-leave-player\", true)", script);
        Assert.Contains("resumesAfterScroll ? \"pointer-resume\" : \"pointer-keepalive\"", script);
        Assert.Contains("postActivity(\"page-scroll\", true)", script);
        Assert.Contains("type: \"youtubeControlActivity\"", script);
        Assert.Contains("tabBrowserToken: webMessageToken", script);
        Assert.Contains("const webMessageToken = \"test-token\";", script);
        Assert.Contains("window.addEventListener(\"resize\", postLayoutActivity, true)", script);
        Assert.Contains("document.addEventListener(\"fullscreenchange\", postLayoutActivity, true)", script);
        Assert.Contains("const isVideoPage = () =>", script);
        Assert.Contains("path === \"/watch\"", script);
        Assert.Contains("path.startsWith(\"/shorts/\")", script);
        Assert.Contains("const attachPointerListeners = () =>", script);
        Assert.Contains("const detachPointerListeners = () =>", script);
        Assert.Contains("const syncPointerListeners = () =>", script);
        Assert.Contains("document.addEventListener(\"yt-navigate-finish\", onNavigateFinish, true)", script);
        Assert.Contains("document.removeEventListener(\"yt-navigate-finish\", onNavigateFinish, true)", script);
        Assert.Contains("document.addEventListener(\"wheel\", onScrollActivity, { capture: true, passive: true })", script);
        Assert.Contains("document.addEventListener(\"scroll\", onScrollActivity, true)", script);
        Assert.Contains("document.removeEventListener(\"wheel\", onScrollActivity, true)", script);
        Assert.Contains("document.removeEventListener(\"scroll\", onScrollActivity, true)", script);
        Assert.Contains("state.installedVersion = 7", script);
        Assert.Contains("document.removeEventListener(\"pointermove\", onPointerMove, true)", script);
        Assert.Contains("document.removeEventListener(\"pointerover\", onPointerOver, true)", script);
        Assert.Contains("document.removeEventListener(\"pointerout\", onPointerOut, true)", script);
        Assert.DoesNotContain("document.addEventListener(\"mousemove\"", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("setTimeout", script);
        Assert.DoesNotContain("preventDefault", script);
        Assert.DoesNotContain("stopPropagation", script);
    }

    [Fact]
    public void BuildBestQualityReadyBridgeAppliesOncePerPreparedVideoWithoutFixedRetries()
    {
        var script = YouTubeControlScriptService.BuildBestQualityReadyBridgeScript();

        Assert.Contains("__tabBrowserYouTubeBestQualityReadyBridge", script);
        Assert.Contains("video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA", script);
        Assert.Contains("state.appliedVideoIdentity === videoIdentity", script);
        Assert.Contains("state.appliedVideoIdentity = videoIdentity", script);
        Assert.Contains("document.addEventListener(\"loadedmetadata\", tryApply, true)", script);
        Assert.Contains("document.addEventListener(\"canplay\", tryApply, true)", script);
        Assert.Contains("document.addEventListener(\"playing\", tryApply, true)", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("setTimeout", script);
    }

    [Theory]
    [InlineData(639, false, "hd720")]
    [InlineData(640, false, "hd1080")]
    [InlineData(1099, false, "hd1080")]
    [InlineData(1100, false, "hd1440")]
    [InlineData(320, true, "highres")]
    public void ResolveMiniPlayerQualityCapUsesDisplayWidthAndLeavesFullScreenUnrestricted(
        double displayWidth,
        bool isFullScreen,
        string expected)
    {
        Assert.Equal(
            expected,
            YouTubeControlScriptService.ResolveMiniPlayerQualityCap(displayWidth, isFullScreen));
    }

    [Theory]
    [InlineData(719, "hd720")]
    [InlineData(720, "hd1080")]
    [InlineData(1319, "hd1080")]
    [InlineData(1320, "hd1440")]
    public void ResolveMiniPlayerQualityCapUsesConfiguredThresholds(
        double displayWidth,
        string expected)
    {
        Assert.Equal(
            expected,
            YouTubeControlScriptService.ResolveMiniPlayerQualityCap(
                displayWidth,
                isFullScreen: false,
                lowWidthThreshold: 720,
                highWidthThreshold: 1320));
    }

    [Fact]
    public void BuildMiniPlayerSizeBasedQualityScriptUsesEventDrivenBridgeAndHonorsManualSelection()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerSizeBasedQualityScript(
            isEnabled: true,
            displayWidth: 800,
            isFullScreen: false,
            lowWidthThreshold: 720,
            highWidthThreshold: 1320);

        Assert.Contains("__tabBrowserYouTubeMiniPlayerSizeBasedQuality", script);
        Assert.Contains("const requestedQualityCap = \"hd1080\";", script);
        Assert.Contains("state.manualVideoIdentity === videoIdentity", script);
        Assert.Contains(".ytp-settings-menu", script);
        Assert.Contains("player.setPlaybackQualityRange(quality, quality)", script);
        Assert.Contains("document.addEventListener(\"loadedmetadata\", onMediaReady, true)", script);
        Assert.Contains("document.addEventListener(\"yt-navigate-finish\", onMediaReady, true)", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("setTimeout", script);
        Assert.DoesNotContain("MutationObserver", script);
    }

    [Fact]
    public void BuildMiniPlayerSizeBasedQualityScriptCanReleaseItsRangeWhenDisabled()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerSizeBasedQualityScript(
            isEnabled: false,
            displayWidth: 500,
            isFullScreen: false);

        Assert.Contains("const enabled = false;", script);
        Assert.Contains("existing.releaseRange?.();", script);
        Assert.Contains("existing?.dispose?.();", script);
        Assert.Contains("delete window[bridgeKey];", script);
    }

    [Fact]
    public void BuildMiniPlayerWatchPageScriptReappliesVideoLayoutAfterPlayerReplacementOrSizeDrift()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript("test-token");

        Assert.Contains("__tabBrowserYouTubeMiniPlayerLayoutBridge", script);
        Assert.Contains("layoutBridgeVersion = 20", script);
        Assert.Contains("(player || document).querySelectorAll(\"video\")", script);
        Assert.Contains("playingVideos.includes(activeVideo)", script);
        Assert.Contains("layoutState.activeVideo = video", script);
        Assert.Contains("getVideoLayoutParents(player, video)", script);
        Assert.Contains("parent !== player", script);
        Assert.Contains("layoutState.verificationAttemptsRemaining = 3", script);
        Assert.Contains("--layoutState.verificationAttemptsRemaining > 0", script);
        Assert.Contains("layoutState.layoutVerified = Boolean(getPlayer() && getVideo()) && !hasLayoutStyleDrift()", script);
        Assert.Contains("window.cancelAnimationFrame(layoutState.verificationFrame)", script);
        Assert.Contains("window.cancelAnimationFrame(layoutState.applyFrame)", script);
        Assert.Contains("for (const delay of [2000, 5000])", script);
        Assert.Contains("if (!layoutState.layoutVerified || hasLayoutStyleDrift())", script);
        Assert.Contains("clearDelayedVerifications();", script);
        Assert.DoesNotContain("tabbrowser-youtube-mini-player-active-layer", script);
        Assert.DoesNotContain("z-index: 2147483600", script);
        Assert.Contains("player.closest(\"ytd-player\")", script);
        Assert.Contains("setImportant(playerHost, \"position\", \"fixed\")", script);
        Assert.Contains("setImportant(playerHost, \"z-index\", \"1\")", script);
        Assert.Contains("setImportant(player, \"position\", \"absolute\")", script);
        Assert.DoesNotContain("while (layer && layer !== document.documentElement)", script);
        Assert.Contains("layoutState.playerParentObserver = new MutationObserver", script);
        Assert.Contains("getPlayer() !== layoutState.observedPlayer", script);
        Assert.Contains("layoutState.playerParentObserver.observe(playerParent, { childList: true })", script);
        Assert.DoesNotContain("layoutState.playerObserver", script);
        Assert.Contains("layoutState.flexyObserver = new MutationObserver(attachAndSchedule)", script);
        Assert.Contains("layoutState.discoveryObserver = new MutationObserver(attachAndSchedule)", script);
        Assert.Contains("player instanceof HTMLElement && video instanceof HTMLVideoElement", script);
        Assert.Contains("layoutState.appliedPlayer === player", script);
        Assert.Contains("layoutState.appliedVideo === video", script);
        Assert.Contains("&& !hasLayoutStyleDrift())", script);
        Assert.Contains(
            "const isContentAtOriginalSize = () => normalizeContentZoom(contentTransformState.zoomFactor) === 1",
            script);
        Assert.Contains("if (!isContentAtOriginalSize()", script);
        Assert.Contains("const previousZoomFactor = normalizeContentZoom(contentTransformState.zoomFactor)", script);
        Assert.Contains("if (previousZoomFactor > 1 && isContentAtOriginalSize())", script);
        Assert.Contains("window.__tabBrowserYouTubeMiniPlayerLayoutBridge?.scheduleApply?.()", script);
        Assert.Contains("layoutState.elementResizeObserver = new ResizeObserver(scheduleApply)", script);
        Assert.Contains("const resizeElements = [playerHost, player, videoContainer, video]", script);
        Assert.Contains("layoutState.elementResizeObserver.observe(element)", script);
        Assert.Contains("layoutState.elementResizeObserver?.disconnect?.()", script);
        Assert.Contains("layoutState.interactiveResize", script);
        Assert.Contains("layoutState.setInteractiveResize", script);
        Assert.Contains("__tabBrowserSetYouTubeMiniPlayerInteractiveResize", script);
        Assert.Contains("document.removeEventListener(eventName, handleVideoReady, true)", script);
        Assert.Contains("document.removeEventListener(\"yt-navigate-start\", handleNavigateStart, true)", script);
        Assert.DoesNotContain("observe(document.documentElement, {", script);
        Assert.DoesNotContain("setInterval(() =>", script);
        Assert.Contains("hoverBridgeVersion = 7", script);
        Assert.Contains("const isYouTubeNativeMenuOpen = () =>", script);
        Assert.Contains("type: \"youtubeMiniPlayerHideToolbar\"", script);
        Assert.Contains("!isYouTubeNativeMenuOpen()", script);
        Assert.Contains("document.addEventListener(\"pointerdown\", hideToolbarForSettings, true)", script);
        Assert.Contains("window.addEventListener(\"pointermove\", handleHoverMove, true)", script);
        Assert.DoesNotContain("window.addEventListener(\"mousemove\", handleHoverMove, true)", script);
        Assert.Contains("window.removeEventListener(\"pointermove\", handleHoverMove, true)", script);
        Assert.Contains("""setImportant(video, "display", "block");""", script);
        Assert.Contains("""const videoReadyEvents = ["loadedmetadata", "loadeddata", "canplay", "playing", "resize"];""", script);
        Assert.Contains("document.addEventListener(eventName, handleVideoReady, true)", script);
        Assert.Contains("document.addEventListener(\"yt-navigate-finish\", handleNavigateFinish, true)", script);
        Assert.Contains("hasVideo: Boolean(video)", script);
        Assert.Contains("bridgeInstalled: true", script);
        Assert.Contains("videoWidth: Number(videoRect?.width) || 0", script);
        Assert.Contains("videoNaturalWidth: Number(video?.videoWidth) || 0", script);
        Assert.Contains("videoNaturalHeight: Number(video?.videoHeight) || 0", script);
        Assert.Contains("isShortsPage", script);
        Assert.Contains("link[rel=\"canonical\"]", script);
        Assert.Contains("viewportWidth: window.innerWidth || document.documentElement.clientWidth || 0", script);
        Assert.Contains("window.addEventListener(\"dblclick\"", script);
        Assert.Contains("window.addEventListener(\"pointermove\", handleHoverMove, true)", script);
        Assert.DoesNotContain(
            "document.addEventListener(\"mousemove\", event => sendHover",
            script);
        Assert.Contains("event.isTrusted", script);
        Assert.Contains("youtubeMiniPlayerToggleFullScreen", script);
        Assert.Contains("const fullScreenMessageToken = \"test-token\";", script);
        Assert.Contains("const dragGestureThreshold = 6;", script);
        Assert.Contains("resizeMode: false", script);
        Assert.Contains("state.resizeMode = event.ctrlKey", script);
        Assert.Contains("resize: state.resizeMode", script);
        Assert.Contains("const isMiniPlayerSideButton = button => button === 3 || button === 4;", script);
        Assert.Contains("type: \"youtubeMiniPlayerSideButton\"", script);
        Assert.Contains("event.button === 3 ? \"xButton1\" : \"xButton2\"", script);
        Assert.Contains("pressedMiniPlayerSideButton === event.button", script);
        Assert.Contains("document.addEventListener(\"auxclick\"", script);
        Assert.Contains("deltaX < dragGestureThreshold && deltaY < dragGestureThreshold", script);
        Assert.Contains("const isYouTubeInteractiveTarget = target =>", script);
        Assert.Contains("const isYouTubeEndScreenTarget = target =>", script);
        Assert.Contains(".html5-endscreen", script);
        Assert.Contains(".ytp-endscreen-content", script);
        Assert.Contains(".ytp-ce-element", script);
        Assert.Contains(".ytp-videowall-still", script);
        Assert.Contains(".ytp-autonav-endscreen-upnext-container", script);
        Assert.Contains("const isYouTubeRightInputBlockedTarget = event =>", script);
        Assert.Contains(
            "!isYouTubeEndScreenTarget(event.target) && isYouTubeControlsTarget(event)",
            script);
        Assert.Contains("window.addEventListener(\"click\", suppressVideoSurfaceClick, true)", script);
        Assert.Contains("isYouTubeInteractiveTarget(event.target)", script);
        Assert.Contains("if (isYouTubeControlsTarget(event))", script);
        Assert.Contains("event.preventDefault();", script);
        Assert.Contains("event.stopImmediatePropagation();", script);
        var dragPointerDownStart = script.IndexOf(
            "document.addEventListener(\"pointerdown\", (event) =>",
            StringComparison.Ordinal);
        var dragPointerMoveStart = script.IndexOf(
            "document.addEventListener(\"pointermove\", (event) =>",
            dragPointerDownStart,
            StringComparison.Ordinal);
        var pointerCaptureStart = script.IndexOf(
            "document.documentElement.setPointerCapture(event.pointerId)",
            dragPointerDownStart,
            StringComparison.Ordinal);
        Assert.True(dragPointerDownStart >= 0);
        Assert.True(dragPointerMoveStart > dragPointerDownStart);
        Assert.True(pointerCaptureStart > dragPointerMoveStart);
        Assert.Contains("data-tabbrowser-mini-player-fullscreen", script);
        Assert.Contains(".ytp-chrome-bottom", script);
        Assert.Contains(".ytp-title", script);
        Assert.Contains("""html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-chrome-top""", script);
        Assert.DoesNotContain("""html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-chrome-bottom""", script);
        Assert.DoesNotContain("""html[data-tabbrowser-mini-player-fullscreen="true"] .ytp-gradient-bottom""", script);
        Assert.Contains("object-fit: contain", script);
        Assert.Contains("const holdSeekGestureThreshold = 24;", script);
        Assert.Contains("event.button !== 2", script);
        Assert.Contains("Math.max(2, holdSeekState.previousPlaybackRate)", script);
        Assert.Contains("video.currentTime - rewindStepSeconds", script);
        Assert.Contains("const formatPlaybackTime = value =>", script);
        Assert.Contains("formatPlaybackTime(video.duration)", script);
        Assert.Contains("String(minutes).padStart(2, \"0\")", script);
        Assert.Contains("\"巻き戻し\"", script);
        Assert.Contains("`${modeText}\\n${getHoldSeekPlaybackTimeText(video)}`", script);
        Assert.Contains("const applyHoldSeekDirection = direction =>", script);
        Assert.Contains("const switchHoldSeekDirection = (direction, event) =>", script);
        Assert.Contains("event.screenX - holdSeekState.directionAnchorX", script);
        Assert.Contains("holdSeekState.direction > 0 && directionDeltaX < 0", script);
        Assert.Contains("holdSeekState.direction < 0 && directionDeltaX > 0", script);
        Assert.Contains("switchHoldSeekDirection(directionDeltaX > 0 ? 1 : -1, event)", script);
        Assert.Contains("document.addEventListener(\"contextmenu\"", script);
        var contextMenuStart = script.IndexOf(
            "document.addEventListener(\"contextmenu\"",
            StringComparison.Ordinal);
        var contextMenuEnd = script.IndexOf("}, true);", contextMenuStart, StringComparison.Ordinal);
        Assert.True(contextMenuStart >= 0);
        Assert.True(contextMenuEnd > contextMenuStart);
        var contextMenuHandler = script[contextMenuStart..contextMenuEnd];
        Assert.Contains("holdSeekState.suppressContextMenu = false;", contextMenuHandler);
        Assert.Contains("event.preventDefault();", contextMenuHandler);
        Assert.Contains("event.stopImmediatePropagation();", contextMenuHandler);
        Assert.DoesNotContain("return;", contextMenuHandler);
        Assert.Contains("document.addEventListener(\"wheel\"", script);
        Assert.Contains("youtubeMiniPlayerHoldSeek", script);
        Assert.Contains("youtubeMiniPlayerRightWheel", script);
        Assert.Contains("youtubeMiniPlayerContentTransform", script);
        Assert.Contains("event.button !== 1", script);
        Assert.Contains("contentTransformState.zoomFactor <= 1", script);
        Assert.Contains("applyContentTransform(getVideo())", script);
        Assert.Contains("const contentPanThreshold = 6;", script);
        Assert.Contains("Math.hypot(deltaX, deltaY) < contentPanThreshold", script);
        Assert.Contains("event.key === \"Escape\" && contentPanState.active", script);
        Assert.Contains("tabbrowser-youtube-mini-player-content-transform-hud", script);
        Assert.Contains("top: \"52px\"", script);
        Assert.Contains("showContentTransformHud(true)", script);
        Assert.Contains("data-tabbrowser-mini-player-content-cursor", script);
        Assert.Contains("setContentPanCursor(\"grabbing\")", script);
        Assert.Contains("tabbrowser-youtube-mini-player-content-pan-edge-feedback", script);
        Assert.Contains("showContentPanEdgeFeedback();", script);
        Assert.Contains("miniPlayerGestureGuideDelayMilliseconds = 450", script);
        Assert.Contains("miniPlayerGestureGuideMovementTolerance = 4", script);
        Assert.Contains("右＋ホイール  ズーム", script);
        Assert.Contains("ホイールボタン＋ドラッグ  表示位置", script);
        Assert.Contains("scheduleMiniPlayerGestureGuide();", script);
        Assert.Contains("anchorX", script);
        Assert.Contains("anchorY", script);
        Assert.Contains("|| isYouTubeRightInputBlockedTarget(event))", script);
        Assert.Contains("(event.buttons & 2) !== 2 || isYouTubeRightInputBlockedTarget(event)", script);
        Assert.Contains("event.deltaMode === 1", script);
        Assert.Contains("event.deltaY * 40", script);
        Assert.Contains("youtubeMiniPlayerCloseGesture", script);
        Assert.Contains("deltaY >= holdSeekGestureThreshold", script);
        Assert.Contains("Math.abs(deltaY) >= Math.abs(deltaX) * 1.5", script);
        Assert.Contains("event?.type === \"pointerup\"", script);
        Assert.Contains("showHoldSeekIndicator(\"\\u9589\\u3058\\u308b\")", script);
        Assert.Contains("closeGestureCancelled: false", script);
        Assert.Contains("const closeDirectionDistance = Math.max(", script);
        Assert.Contains("const continuesCloseDirection = closeDirectionDeltaY >= holdSeekGestureThreshold", script);
        Assert.Contains("holdSeekState.closeGestureCancelled = true", script);
        Assert.Contains("const wasCloseGestureCancelled = holdSeekState.closeGestureCancelled", script);
        Assert.Contains("!wasCloseGestureCancelled", script);
        Assert.Contains("__tabBrowserYouTubeMiniPlayerCancelRightInput", script);
        Assert.Contains("isYouTubeControlsTarget(event)", script);
        Assert.Contains("__tabBrowserYouTubeMiniPlayerSavedStateRestoreGuard", script);
        Assert.Contains("savedStateRestoreGuardVersion = 1", script);
        Assert.Contains("savedStateRestoreGuard.audio = true", script);
        Assert.Contains("savedStateRestoreGuard.playback = true", script);
        Assert.Contains("document.addEventListener(\"keydown\", markKeyboardInteraction, true)", script);
    }

    [Fact]
    public void BuildMiniPlayerLayoutVerificationScriptReusesExistingBridgeWithoutPolling()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerLayoutVerificationScript();

        Assert.Contains("window.__tabBrowserYouTubeMiniPlayerLayoutBridge", script);
        Assert.Contains("typeof bridge.beginVerification !== \"function\"", script);
        Assert.Contains("bridge.beginVerification(true);", script);
        Assert.Contains("return { ok: true, requested: true };", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("setTimeout", script);
    }

    [Fact]
    public void MiniPlayerAudioBoostScriptUsesOneReusableAudioGraphWithoutPolling()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript(
            "test-token",
            audioBoostDecibels: 6);

        Assert.Contains("__tabBrowserYouTubeMiniPlayerAudioBoost", script);
        Assert.Contains("audioBoostBridgeVersion = 1", script);
        Assert.Contains("requestedAudioBoostDecibels = 6", script);
        Assert.Contains("entries: new WeakMap()", script);
        Assert.Contains("pendingEntries: new WeakMap()", script);
        Assert.Contains("context.createMediaElementSource(video)", script);
        Assert.Contains("context.createGain()", script);
        Assert.Contains("context.createDynamicsCompressor()", script);
        Assert.Contains("audioBoostState.entries.set(video, entry)", script);
        Assert.Contains("Promise.resolve(audioBoostState.applyToVideo(video))", script);
        Assert.Contains("token === fullScreenMessageToken", script);
        Assert.DoesNotContain("__TAB_BROWSER_YOUTUBE_MINI_PLAYER_AUDIO_BOOST_", script);
        Assert.DoesNotContain("setInterval(() =>", script);
    }

    [Theory]
    [InlineData(0, "0", "1")]
    [InlineData(6, "6", "1.9952623149688795")]
    [InlineData(8, "0", "1")]
    public void BuildSetMiniPlayerAudioBoostScriptNormalizesProfileAndAuthenticatesRequest(
        int requestedDecibels,
        string expectedDecibels,
        string expectedGain)
    {
        var script = YouTubeControlScriptService.BuildSetMiniPlayerAudioBoostScript(
            "token\"value",
            requestedDecibels);

        Assert.Contains("__tabBrowserSetYouTubeMiniPlayerAudioBoost", script);
        Assert.Contains("\"token\\u0022value\"", script);
        Assert.Contains($"{expectedDecibels},", script);
        Assert.Contains(expectedGain, script);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void BuildSetMiniPlayerInteractiveResizeScriptAuthenticatesAndPreservesGeneration(
        bool isActive,
        string serializedIsActive)
    {
        var script = YouTubeControlScriptService.BuildSetMiniPlayerInteractiveResizeScript(
            "token\"value",
            generation: 42,
            isActive);

        Assert.Contains("__tabBrowserSetYouTubeMiniPlayerInteractiveResize", script);
        Assert.Contains("\"token\\u0022value\"", script);
        Assert.Contains("42", script);
        Assert.Contains(serializedIsActive, script);
    }

    [Fact]
    public void MiniPlayerContentTransformScriptsEmbedSavedStateAndAuthenticatedSetter()
    {
        var watchPageScript = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript(
            "test-token",
            contentZoomFactor: 2.4,
            contentPanX: 0.5,
            contentPanY: -0.25);
        var updateScript = YouTubeControlScriptService.BuildSetMiniPlayerContentTransformScript(
            "token\"value",
            1.5,
            0.25,
            -0.5);

        Assert.Contains("setContentTransform(", watchPageScript);
        Assert.Contains("2.4", watchPageScript);
        Assert.Contains("0.5", watchPageScript);
        Assert.Contains("-0.25", watchPageScript);
        Assert.DoesNotContain("__TAB_BROWSER_YOUTUBE_MINI_PLAYER_CONTENT_", watchPageScript);
        Assert.Contains("transform-origin", watchPageScript);
        Assert.Contains("translate(${translateX}px, ${translateY}px) scale(${zoomFactor})", watchPageScript);
        Assert.Equal(
            "window.__tabBrowserYouTubeMiniPlayerSetContentTransform?.(\"token\\u0022value\", 1.5, 0.25, -0.5, true);",
            updateScript);
        Assert.Contains("const hasContentTransformDrift = video", watchPageScript);
        Assert.Contains("const hasVideoStyleDrift = video", watchPageScript);
        Assert.Contains("hasImportantVideoZeroStyleDrift(video, \"left\")", watchPageScript);
        Assert.Contains("hasImportantVideoZeroStyleDrift(video, \"top\")", watchPageScript);
        Assert.Contains("hasImportantVideoStyleDrift(video, \"width\", \"100%\")", watchPageScript);
        Assert.Contains("hasImportantVideoStyleDrift(video, \"height\", \"100%\")", watchPageScript);
        Assert.Contains("hasImportantVideoStyleDrift(video, \"object-fit\", \"contain\")", watchPageScript);
        Assert.Contains("contentTransformObserver: null", watchPageScript);
        Assert.Contains("attributeFilter: [\"style\", \"class\"]", watchPageScript);
        Assert.Contains("isContentAtOriginalSize()", watchPageScript);
        Assert.DoesNotContain("&& !isContentAtOriginalSize()", watchPageScript);
        Assert.DoesNotContain("|| isContentAtOriginalSize()", watchPageScript);
        Assert.Contains("!hasLayoutStyleDrift()", watchPageScript);
        Assert.Contains("layoutState.scheduleApply(true)", watchPageScript);
    }

    [Fact]
    public void MiniPlayerGestureScriptKeepsBuiltInsWithoutExplicitMappings()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript(
            "test-token",
            gestureMappings: []);

        Assert.Contains("mappings: []", script);
        Assert.Contains("activateHoldSeek(deltaX > 0 ? 1 : -1, event)", script);
        Assert.Contains("sendCloseGesture();", script);
        Assert.Contains("youtubeMiniPlayerRightWheel", script);
    }

    [Fact]
    public void MiniPlayerGestureScriptEmbedsOnlyRouteAndLabelForExplicitScope()
    {
        var mappings = new[]
        {
            new GestureMappingSettings
            {
                Route = "←↑",
                Command = MiniPlayerCommandProvider.ToggleMuteActionId,
                Label = "ミュート切り替え",
                Scope = GestureCommandScope.MiniPlayer
            }
        };

        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript(
            "test-token",
            mappings,
            gestureStartThreshold: 18,
            gestureDirectionThreshold: 26);
        var updateScript = YouTubeControlScriptService.BuildUpdateMiniPlayerGestureSettingsScript(
            "test-token",
            mappings,
            gestureStartThreshold: 18,
            gestureDirectionThreshold: 26);

        Assert.Contains("youtubeMiniPlayerGesture", script);
        Assert.Contains(@"\u2190\u2191", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"label\":", script);
        Assert.Contains("\"startThreshold\":18", script);
        Assert.Contains("\"directionThreshold\":26", script);
        Assert.DoesNotContain(MiniPlayerCommandProvider.ToggleMuteActionId, script);
        Assert.Contains("__tabBrowserYouTubeMiniPlayerUpdateGestureSettings", updateScript);
        Assert.DoesNotContain(MiniPlayerCommandProvider.ToggleMuteActionId, updateScript);
    }

    [Fact]
    public void BuildMiniPlayerWatchPageScriptDoesNotCreateFixedBlackOuterLayers()
    {
        var script = YouTubeControlScriptService.BuildMiniPlayerWatchPageScript("test-token");
        var outerLayoutStart = script.IndexOf("ytd-app,", StringComparison.Ordinal);
        var videoContainerStart = script.IndexOf(
            ".html5-video-container",
            outerLayoutStart,
            StringComparison.Ordinal);

        Assert.True(outerLayoutStart >= 0 && videoContainerStart > outerLayoutStart);
        var outerLayout = script[outerLayoutStart..videoContainerStart];
        Assert.DoesNotContain("position: fixed", outerLayout);
        Assert.DoesNotContain("inset: 0", outerLayout);
        Assert.DoesNotContain("background: #000", outerLayout);
        Assert.Contains("background: transparent", outerLayout);
    }

    [Fact]
    public void BuildCancelMiniPlayerRightInputScriptEmbedsTokenAndInputKindAsJson()
    {
        var script = YouTubeControlScriptService.BuildCancelMiniPlayerRightInputScript(
            "token\"value",
            "holdSeek");

        Assert.Equal(
            "window.__tabBrowserYouTubeMiniPlayerCancelRightInput?.(\"token\\u0022value\", \"holdSeek\");",
            script);
    }

    [Fact]
    public void BuildPlaybackStateRestoreScriptRestoresLooping()
    {
        var script = YouTubeControlScriptService.BuildPlaybackStateRestoreScript(10, 1.25, true, true, null);

        Assert.Contains("const targetLooping = true;", script);
        Assert.Contains("video.loop = targetLooping;", script);
        Assert.Contains("""video.setAttribute("loop", "");""", script);
        Assert.Contains("""player.setLoop(targetLooping);""", script);
        Assert.Contains("looping: Boolean(video.loop)", script);
    }

    [Fact]
    public void MiniPlayerSavedStateRestoreScriptsRespectImmediateUserOperations()
    {
        var playbackScript = YouTubeControlScriptService.BuildPlaybackStateRestoreScript(
            10,
            1.25,
            true,
            true,
            null,
            respectMiniPlayerUserOverride: true);
        var volumeScript = YouTubeControlScriptService.BuildVolumeScript(
            0.5,
            respectMiniPlayerUserOverride: true);
        var muteScript = YouTubeControlScriptService.BuildMuteScript(
            true,
            respectMiniPlayerUserOverride: true);

        Assert.Contains("const respectMiniPlayerUserOverride = true;", playbackScript);
        Assert.Contains("const userOverrodePlayback = respectMiniPlayerUserOverride", playbackScript);
        Assert.Contains("targetPosition !== null && targetPosition > 0 && !userOverrodePlayback", playbackScript);
        Assert.Contains("pauseAfterRestore && !userOverrodePlayback", playbackScript);
        Assert.Contains("skippedUserOverride: userOverrodePlayback", playbackScript);
        Assert.Contains("savedStateRestoreGuard?.audio", volumeScript);
        Assert.Contains("skippedUserOverride: true", volumeScript);
        Assert.Contains("savedStateRestoreGuard?.audio", muteScript);
        Assert.Contains("skippedUserOverride: true", muteScript);
    }

    [Fact]
    public void MiniPlayerDirectControlScriptsMarkUserOperations()
    {
        var playPauseScript = YouTubeControlScriptService.BuildPlayPauseScript();
        var pauseScript = YouTubeControlScriptService.BuildPauseScript();
        var volumeScript = YouTubeControlScriptService.BuildVolumeScript(
            0.5,
            markMiniPlayerUserOverride: true);
        var muteScript = YouTubeControlScriptService.BuildMuteScript(
            true,
            markMiniPlayerUserOverride: true);

        Assert.Contains("savedStateRestoreGuard.playback = true", playPauseScript);
        Assert.Contains("savedStateRestoreGuard.playback = true", pauseScript);
        Assert.Contains("const markMiniPlayerUserOverride = true;", volumeScript);
        Assert.Contains("guard.audio = true", volumeScript);
        Assert.Contains("const markMiniPlayerUserOverride = true;", muteScript);
        Assert.Contains("guard.audio = true", muteScript);
    }

    [Fact]
    public void BuildLoopScriptTogglesLooping()
    {
        var script = YouTubeControlScriptService.BuildLoopScript(null);

        Assert.Contains("const targetLooping = null;", script);
        Assert.Contains("!Boolean(video.loop)", script);
        Assert.Contains("video.loop = looping;", script);
        Assert.Contains("""video.setAttribute("loop", "");""", script);
        Assert.Contains("video.removeAttribute(\"loop\");", script);
        Assert.Contains("player.setLoop(looping);", script);
        Assert.Contains("const isEnded = Boolean(video.ended) || playerState === 0;", script);
        Assert.Contains("if (looping && isEnded)", script);
        Assert.Contains("savedStateRestoreGuard.playback = true;", script);
        Assert.Contains("pausedResumeGuard.enabled = false;", script);
        Assert.Contains("player.seekTo(0, true);", script);
        Assert.Contains("player.playVideo();", script);
        Assert.Contains("Promise.resolve(video.play()).catch(() => { });", script);
        Assert.True(
            script.IndexOf("video.loop = looping;", StringComparison.Ordinal)
            < script.IndexOf("if (looping && isEnded)", StringComparison.Ordinal));
        Assert.Contains("looping: Boolean(video.loop)", script);
        Assert.Contains("restarted", script);
    }

    [Fact]
    public void BuildPauseScriptNeverStartsPlayback()
    {
        var script = YouTubeControlScriptService.BuildPauseScript();

        Assert.Contains("player.pauseVideo();", script);
        Assert.Contains("video.pause();", script);
        Assert.DoesNotContain("player.playVideo();", script);
        Assert.Contains("paused: Boolean(video.paused)", script);
    }

    [Fact]
    public void BuildSetPausedTransientScriptTemporarilyBypassesPausedResumeGuard()
    {
        var playScript = YouTubeControlScriptService.BuildSetPausedTransientScript(isPaused: false);
        var restoreScript = YouTubeControlScriptService.BuildSetPausedTransientScript(isPaused: true);

        Assert.Contains("const managePausedResumeGuard = true;", playScript);
        Assert.Contains("pausedResumeGuard.enabled = false;", playScript);
        Assert.Contains("pausedResumeGuard[transientGuardStateKey] = Boolean(pausedResumeGuard.enabled);", playScript);
        Assert.Contains("pausedResumeGuard.enabled = Boolean(pausedResumeGuard[transientGuardStateKey]);", restoreScript);
        Assert.Contains("delete pausedResumeGuard[transientGuardStateKey];", restoreScript);
        Assert.Contains("(() => {", playScript);
        Assert.DoesNotContain("(async () => {", playScript);
        Assert.Contains("void video.play().catch(() => {});", playScript);
        Assert.DoesNotContain("await video.play();", playScript);

        var normalScript = YouTubeControlScriptService.BuildSetPausedScript(isPaused: false);
        Assert.Contains("const managePausedResumeGuard = false;", normalScript);
    }

    [Fact]
    public void BuildSeekRelativeScriptMovesByRequestedSecondsAndClampsPosition()
    {
        var script = YouTubeControlScriptService.BuildSeekRelativeScript(-10);

        Assert.Contains("const deltaSeconds = -10;", script);
        Assert.Contains("Math.max(0, Math.min(maximum", script);
        Assert.Contains("player.seekTo(position, true);", script);
        Assert.Contains("position,", script);
        Assert.Contains("duration,", script);
        Assert.DoesNotContain("liveBadge.click();", script);
    }

    [Fact]
    public void BuildSeekToRatioScriptUsesCurrentFiniteVideoDuration()
    {
        var script = YouTubeControlScriptService.BuildSeekToRatioScript(0.75);

        Assert.Contains("const positionRatio = 0.75;", script);
        Assert.Contains("Number.isFinite(duration)", script);
        Assert.Contains("duration - 0.25", script);
        Assert.Contains("const position = maximum * positionRatio;", script);
        Assert.Contains("player.seekTo(position, true);", script);
        Assert.Contains("duration-unavailable", script);
    }

    [Fact]
    public void PlaybackStateAndLiveEdgeScriptsUseCurrentLiveMetadataAndSeekableEnd()
    {
        var stateScript = YouTubeControlScriptService.BuildPlaybackStateReadScript();
        var seekScript = YouTubeControlScriptService.BuildSeekToLiveEdgeScript();

        Assert.Contains("player.getVideoData", stateScript);
        Assert.Contains("player.getPlayerResponse", stateScript);
        Assert.Contains("isLive", stateScript);
        Assert.Contains(".ytp-live-badge", stateScript);
        Assert.Contains("liveBadge.classList.contains(\"ytp-live-badge-is-livehead\")", stateScript);
        Assert.DoesNotContain("liveBadge.hasAttribute(\"disabled\")", stateScript);
        Assert.Contains("isAtLiveEdge", stateScript);
        Assert.DoesNotContain("liveEdgeToleranceSeconds", stateScript);
        Assert.Contains("Number.isFinite(video.duration)", stateScript);
        Assert.Contains("reason: \"not-live\"", seekScript);
        Assert.Contains("video.seekable", seekScript);
        Assert.Contains("seekable.end(seekable.length - 1)", seekScript);
        Assert.Contains("liveEdge - 0.25", seekScript);
        Assert.Contains("player.seekTo(position, true);", seekScript);
        Assert.Contains(".ytp-live-badge", seekScript);
        Assert.Contains("!liveBadge.classList.contains(\"ytp-live-badge-is-livehead\")", seekScript);
        Assert.Contains("liveBadge.click();", seekScript);
        Assert.Contains("isLive: true", seekScript);
        Assert.DoesNotContain("isAtLiveEdge: true", seekScript);
        Assert.DoesNotContain("setInterval", seekScript);
        Assert.DoesNotContain("localStorage", seekScript);
        Assert.DoesNotContain("sessionStorage", seekScript);
    }

    [Fact]
    public void InitialLivePlaybackSeeksToLiveEdgeAndStartsPlaybackUnlessUserOverrodeIt()
    {
        var script = YouTubeControlScriptService.BuildInitialLivePlaybackScript();

        Assert.Contains("savedStateRestoreGuard?.playback", script);
        Assert.Contains("skippedUserOverride: true", script);
        Assert.Contains("seekable.end(seekable.length - 1)", script);
        Assert.Contains("player.seekTo(position, true);", script);
        Assert.Contains(".ytp-live-badge", script);
        Assert.Contains("ytp-live-badge-is-livehead", script);
        Assert.Contains("liveBadge.click();", script);
        Assert.Equal(1, script.Split("liveBadge.click();", StringSplitOptions.None).Length - 1);
        Assert.Contains("player.playVideo();", script);
        Assert.Contains("await video.play();", script);
        Assert.Contains("initialLivePlaybackApplied: true", script);
        Assert.Contains("reason: \"playback-blocked\"", script);
        Assert.DoesNotContain("savedStateRestoreGuard.playback = true", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("localStorage", script);
        Assert.DoesNotContain("sessionStorage", script);
    }

    [Fact]
    public void BuildAbRepeatScriptUsesScopedEventDrivenBoundaryHandling()
    {
        var script = YouTubeControlScriptService.BuildAbRepeatScript(
            12.5,
            24.75,
            isEnabled: true,
            isWholeLooping: false,
            expectedVideoId: "abc123");

        Assert.Contains("const requestedStart = 12.5;", script);
        Assert.Contains("const requestedEnd = 24.75;", script);
        Assert.Contains("const expectedVideoId = \"abc123\";", script);
        Assert.Contains("requestedEnd - requestedStart >= 0.5", script);
        Assert.Contains("expectedVideoId !== currentVideoId", script);
        Assert.Contains("video.addEventListener(\"timeupdate\", bridge.onBoundary)", script);
        Assert.Contains("video.addEventListener(\"ended\", bridge.onBoundary)", script);
        Assert.Contains("video.removeEventListener(\"timeupdate\", bridge.onBoundary)", script);
        Assert.Contains("adPlayer?.classList?.contains(\"ad-showing\")", script);
        Assert.Contains("adPlayer?.classList?.contains(\"ad-interrupting\")", script);
        Assert.DoesNotContain("setInterval", script);
        Assert.DoesNotContain("localStorage", script);
        Assert.DoesNotContain("sessionStorage", script);
    }

    [Fact]
    public void BuildPlaybackStateRestoreScriptRestoresTheaterMode()
    {
        var script = YouTubeControlScriptService.BuildPlaybackStateRestoreScript(null, null, null, null, true);

        Assert.Contains("const targetTheaterMode = true;", script);
        Assert.Contains("""document.querySelector("ytd-watch-flexy[theater]")""", script);
        Assert.Contains("""document.querySelector(".ytp-size-button")""", script);
        Assert.Contains("sizeButton.click();", script);
        Assert.DoesNotContain("""theaterElement.setAttribute("theater", "");""", script);
        Assert.Contains("theaterMode: Boolean(document.querySelector(\"ytd-watch-flexy[theater]\"))", script);
    }
}
