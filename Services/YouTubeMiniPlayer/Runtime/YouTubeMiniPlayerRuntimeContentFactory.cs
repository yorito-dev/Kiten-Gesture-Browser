using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal readonly record struct YouTubeLiveChatAvailabilityResult(
    string VideoId,
    bool IsLive,
    string? ReplayContinuation)
{
    public bool IsAvailable => IsLive || !string.IsNullOrWhiteSpace(ReplayContinuation);
}

internal static class YouTubeMiniPlayerRuntimeContentFactory
{
    public static WebView2 CreateWebView(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        Action queueSave,
        Func<YouTubeMiniPlayerState, Task<long?>> applyWatchPageModeAsync,
        Func<YouTubeMiniPlayerState, long, Task> applySavedStateAsync,
        Action<string> setStatus,
        Action<YouTubeMiniPlayerState, string> requestAutoRecovery,
        Action<YouTubeMiniPlayerState, CoreWebView2ProcessFailedEventArgs> reportProcessFailure,
        Action<YouTubeMiniPlayerState> beginLiveChatTransition,
        Func<YouTubeMiniPlayerState, YouTubeLiveChatAvailabilityResult, Task> applyLiveChatAvailabilityAsync,
        Action<YouTubeMiniPlayerRuntime> recordBrowsingHistory,
        Action<YouTubeMiniPlayerState, bool> queueInactiveSuspension,
        Func<bool>? isClosing = null)
    {
        var documents = new YouTubeMiniPlayerDocumentInitialization();
        void ApplyCurrentSource(YouTubeMiniPlayerState currentState, WebView2 webView)
        {
            if (!IsCurrentWebView(currentState, webView, runtimes)
                || webView.CoreWebView2 is not { } core
                || !YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(currentState, core.Source)) return;
            var runtime = runtimes[currentState.Id];
            beginLiveChatTransition(currentState);
            runtime.SizeBasedQualityUpdateTimer?.Stop();
            runtime.SizeBasedQualityAppliedSignature = null;
            ResetLiveStatus(currentState, runtime);
            YouTubeMiniPlayerChromeFactory.UpdateChatButtonAvailability(runtime.TitleBandChatButton, null);
            queueSave();
        }

        return YouTubeMiniPlayerWebViewService.CreateWebView(
            state,
            (currentState, webView) =>
            {
                if (!IsCurrentWebView(currentState, webView, runtimes))
                {
                    return;
                }

                YouTubeMiniPlayerTitleService.AttachDocumentTitleHandler(
                    currentState,
                    runtimes,
                    queueSave);
                YouTubeMiniPlayerRuntimeLifecycleService.AttachFullScreenHandler(
                    runtimes[currentState.Id]);
            },
            (currentState, webView, args) =>
            {
                if (IsCurrentWebView(currentState, webView, runtimes))
                {
                    var runtime = runtimes[currentState.Id];
                    documents.Begin(args.NavigationId);
                    if (args.IsUserInitiated
                        || (runtime.LiveContinuationNavigation is { IsResolutionStarted: true, NavigationId: { } previousId }
                            && previousId != args.NavigationId))
                    {
                        ClearLiveContinuation(runtime);
                    }
                    if (runtime.LiveContinuationNavigation is { } continuation)
                    {
                        continuation.NavigationId = args.NavigationId;
                    }
                    runtime.InactiveSuspendGeneration++;
                    runtime.IsNavigationLoading = true;
                    runtime.IsSuspendedForInactivity = false;
                    runtime.IsLoadStoppedForInactivity = false;
                    runtimes[currentState.Id].DisplayHealth.Reset();
                    runtime.SizeBasedQualityUpdateTimer?.Stop();
                    runtime.SizeBasedQualityAppliedSignature = null;
                    ResetLiveStatus(currentState, runtime);
                    YouTubeMiniPlayerChromeFactory.UpdateChatButtonAvailability(
                        runtime.TitleBandChatButton,
                        null);
                    queueInactiveSuspension(currentState, true);
                }
            },
            (currentState, webView) =>
            {
                if (!IsCurrentWebView(currentState, webView, runtimes)
                    || webView.CoreWebView2 is not { } core)
                {
                    return;
                }

                var runtime = runtimes[currentState.Id];
                if (runtime.LiveContinuationNavigation is { } continuation)
                {
                    if (!continuation.TryReleaseForSourceChange(core.Source)) return;
                    ClearLiveContinuation(runtime);
                }
                ApplyCurrentSource(currentState, webView);
            },
            (currentState, webView) =>
            {
                if (!IsCurrentWebView(currentState, webView, runtimes)
                    || webView.CoreWebView2 is not { } core)
                {
                    return;
                }

                YouTubeMiniPlayerChromeFactory.UpdateNavigationHistoryButtons(
                    runtimes[currentState.Id],
                    core.CanGoBack,
                    core.CanGoForward);
            },
            async (currentState, webView, args) =>
            {
                if (!IsCurrentWebView(currentState, webView, runtimes))
                {
                    return;
                }

                var runtime = runtimes[currentState.Id];
                if (!documents.IsCurrent(args.NavigationId)) return;

                if (runtime.LiveContinuationNavigation is { NavigationId: { } navigationId }
                    && navigationId != args.NavigationId)
                {
                    return;
                }
                runtime.InactiveSuspendGeneration++;
                runtime.IsNavigationLoading = false;

                bool DocumentAvailable() => documents.IsCurrent(args.NavigationId)
                    && !runtime.IsDisposed && isClosing?.Invoke() != true
                    && !runtime.IsNavigationLoading && IsCurrentWebView(currentState, webView, runtimes);
                await documents.CompleteAsync(args.NavigationId, DocumentAvailable, async () =>
                {
                    if (runtime.LiveContinuationNavigation is { } continuation)
                    {
                        if (webView.CoreWebView2 is not { } continuationCore)
                        {
                            ClearLiveContinuation(runtime);
                            return YouTubeContinuationCompletion.Redirected;
                        }
                        if (continuation.NavigationId != args.NavigationId || !continuation.TryBeginResolution())
                        {
                            return YouTubeContinuationCompletion.Redirected;
                        }

                        var destination = continuationCore.Source;
                        string? destinationResult = null;
                        if (args.IsSuccess && runtime.IsLiveContinuationEnabled)
                        {
                            try
                            {
                                // 現在表示しているページの既存の状態取得を再利用する。別ページは取得しない。
                                string? destinationVideoId = null;
                                if (YouTubeMiniPlayerNavigationService.GetLiveContinuationDestinationKind(
                                        destination, continuation.ChannelId) == YouTubeLiveContinuationDestinationKind.ChannelLivePage)
                                {
                                    var identityResult = await continuationCore.ExecuteScriptAsync(
                                        YouTubeMiniPlayerWebViewService.BuildLiveContinuationVideoScript());
                                    if (continuation.TryReadCandidate(identityResult, out var currentVideoId))
                                        destinationVideoId = currentVideoId;
                                }
                                else if (YouTubeUrlService.TryGetVideoId(destination, out var watchVideoId))
                                {
                                    destinationVideoId = watchVideoId;
                                }
                                if (destinationVideoId is not null && !runtime.IsDisposed
                                    && ReferenceEquals(runtime.LiveContinuationNavigation, continuation))
                                {
                                    destinationResult = await continuationCore.ExecuteScriptAsync(
                                        YouTubeMiniPlayerWebViewService.BuildLiveStatusScript(destinationVideoId));
                                }
                            }
                            catch
                            {
                                // 確認できない移動先は、下で元の動画へ一度だけ戻す。
                            }
                        }

                        if (!DocumentAvailable()
                            || runtime.IsDisposed
                            || !ReferenceEquals(YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime), continuationCore)
                            || runtime.IsNavigationLoading || continuation.NavigationId != args.NavigationId)
                        {
                            return YouTubeContinuationCompletion.Redirected;
                        }

                        if (!ReferenceEquals(runtime.LiveContinuationNavigation, continuation))
                            return YouTubeContinuationCompletion.Expired;

                        if (!runtime.IsLiveContinuationEnabled)
                        {
                            destinationResult = null;
                        }
                        if (!continuation.TryResolve(destination, continuationCore.Source, destinationResult, out var resolution))
                        {
                            return YouTubeContinuationCompletion.Redirected;
                        }

                        ClearLiveContinuation(runtime);
                        if (resolution.Action == YouTubeLiveContinuationAction.ReleaseOnly)
                        {
                            ApplyCurrentSource(currentState, webView);
                            return YouTubeContinuationCompletion.Redirected;
                        }
                        if (resolution.Action == YouTubeLiveContinuationAction.ReturnToOriginal)
                        {
                            setStatus("続きのライブは見つかりませんでした");
                            try
                            {
                                YouTubeMiniPlayerWebViewService.NavigateWithYouTubeReferer(continuationCore, resolution.NavigationUri!);
                            }
                            catch
                            {
                                setStatus("元の動画へ戻れませんでした");
                            }
                            return YouTubeContinuationCompletion.Redirected;
                        }

                        currentState.IsPaused = false;
                        runtime.IsInitialLivePlaybackResolved = false;
                        if (YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(currentState,
                                resolution.NavigationUri?.AbsoluteUri ?? continuationCore.Source))
                        {
                            beginLiveChatTransition(currentState);
                            queueSave();
                        }
                        if (resolution.Action == YouTubeLiveContinuationAction.NavigateToWatch)
                        {
                            try
                            {
                                YouTubeMiniPlayerWebViewService.NavigateWithYouTubeReferer(continuationCore, resolution.NavigationUri!);
                            }
                            catch
                            {
                                setStatus("続きのライブは見つかりませんでした");
                            }
                            return YouTubeContinuationCompletion.Redirected;
                        }
                    }

                    return YouTubeContinuationCompletion.Ready;
                }, async isCurrentDocument =>
                {
                    if (!args.IsSuccess)
                    {
                        runtime.DisplayHealth.ReportFailure();
                        runtime.CompleteRecoveryNavigation(webView, isHealthy: false);
                        setStatus("YouTubeミニプレイヤーの読み込みに失敗しました: " + args.WebErrorStatus);
                        requestAutoRecovery(currentState, "navigation-failed");
                        return;
                    }

                    YouTubeMiniPlayerTitleService.ApplyDocumentTitle(
                        currentState,
                        runtimes,
                        queueSave);
                    recordBrowsingHistory(runtimes[currentState.Id]);
                    // 状態通知ブリッジが起動直後の0秒を通知しても、読み込み前の保存値を復元に使う。
                    var savedState = YouTubeMiniPlayerNavigationService.CloneState(currentState);
                    var displayGeneration = await applyWatchPageModeAsync(currentState);
                    if (!isCurrentDocument())
                    {
                        return;
                    }

                    if (displayGeneration is not { } currentDisplayGeneration)
                    {
                        return;
                    }

                    await applySavedStateAsync(savedState, currentDisplayGeneration);
                    if (!isCurrentDocument())
                    {
                        return;
                    }

                    var liveChatAvailability = await UpdateLiveChatButtonAvailabilityAsync(
                        currentState,
                        webView,
                        runtimes);
                    if (!isCurrentDocument())
                    {
                        return;
                    }

                    if (liveChatAvailability is { } currentLiveChatAvailability)
                    {
                        await applyLiveChatAvailabilityAsync(currentState, currentLiveChatAvailability);
                        if (!isCurrentDocument())
                        {
                            return;
                        }
                    }

                    var currentRuntime = runtimes[currentState.Id];
                    var isHealthy = currentRuntime.DisplayHealth.GetSnapshot().State
                        == YouTubeMiniPlayerDisplayHealthState.Healthy;
                    currentRuntime.CompleteRecoveryNavigation(webView, isHealthy);
                    if (!isHealthy)
                    {
                        requestAutoRecovery(currentState, "display-health");
                    }
                });
            },
            (currentState, webView, args) =>
            {
                if (!IsCurrentWebView(currentState, webView, runtimes))
                {
                    return;
                }

                reportProcessFailure(currentState, args);
                var action = WebViewProcessFailurePolicy.GetAction(args.ProcessFailedKind);
                if (action == WebViewProcessFailureAction.ObserveOnly)
                {
                    setStatus("YouTubeミニプレイヤーの補助プロセスをWebView2が自動復旧しています");
                    return;
                }

                runtimes[currentState.Id].DisplayHealth.ReportFailure();
                setStatus(action == WebViewProcessFailureAction.RevalidateMediaPlayback
                    ? "YouTubeミニプレイヤーの映像再生を確認しています"
                    : "YouTubeミニプレイヤーの描画プロセスで問題が発生しました: " + WebViewLifecycleService.FormatProcessFailure(args));
            });
    }

    internal static async Task<YouTubeLiveChatAvailabilityResult?> UpdateLiveChatButtonAvailabilityAsync(
        YouTubeMiniPlayerState state,
        WebView2 webView,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        if (!YouTubeUrlService.TryGetVideoId(state.Url, out var videoId))
        {
            if (runtimes.TryGetValue(state.Id, out var invalidRuntime))
            {
                ResetLiveStatus(state, invalidRuntime);
            }
            UpdateLiveChatButtonAvailability(state, webView, runtimes, false);
            return null;
        }

        if (!runtimes.TryGetValue(state.Id, out var runtime)
            || !ReferenceEquals(runtime.WebView, webView))
        {
            return null;
        }

        if (!string.Equals(
                runtime.LiveChatAvailabilityAttemptVideoId,
                videoId,
                StringComparison.Ordinal))
        {
            runtime.LiveChatAvailabilityAttemptVideoId = videoId;
            runtime.LiveChatAvailabilityAttemptCount = 0;
        }

        if (runtime.LiveChatAvailabilityAttemptCount >= 2)
        {
            return null;
        }

        var availability = await ReadLiveChatButtonAvailabilityAsync(
            state,
            webView,
            runtimes,
            videoId);
        if (availability.HasValue)
        {
            runtime.LiveChatAvailabilityAttemptCount++;
        }

        UpdateLiveChatButtonAvailability(state, webView, runtimes, availability?.IsAvailable);
        return availability;
    }

    internal static void StartLiveContinuationTimeout(YouTubeMiniPlayerRuntime runtime, Action queueSave)
    {
        if (runtime.LiveContinuationNavigation is not { } continuation) return;
        runtime.LiveContinuationTimeoutTimer?.Stop();
        runtime.LiveContinuationTimeoutTimer = new OwnedDispatcherTimer(
            DispatcherTimerOwner.MiniPlayerRuntime,
            DispatcherTimerTerminationPhase.RuntimeDispose)
        {
            Interval = YouTubeMiniPlayerLiveContinuationNavigation.Timeout
        };
        var timer = runtime.LiveContinuationTimeoutTimer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!runtime.IsDisposed && ReferenceEquals(runtime.LiveContinuationNavigation, continuation)
                && continuation.TryTimeout(YouTubeMiniPlayerLiveContinuationNavigation.Timeout))
            {
                ClearLiveContinuation(runtime);
                if (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is { } core
                    && YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(runtime.State, core.Source))
                {
                    ResetLiveStatus(runtime.State, runtime);
                    queueSave();
                }
            }
        };
        timer.Start();
    }

    internal static void ClearLiveContinuation(YouTubeMiniPlayerRuntime runtime)
    {
        runtime.LiveContinuationTimeoutTimer?.Stop();
        runtime.LiveContinuationTimeoutTimer = null;
        runtime.LiveContinuationNavigation = null;
    }

    private static async Task<YouTubeLiveChatAvailabilityResult?> ReadLiveChatButtonAvailabilityAsync(
        YouTubeMiniPlayerState state,
        WebView2 webView,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        string expectedVideoId)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return null;
        }

        try
        {
            var result = await core.ExecuteScriptAsync(
                YouTubeMiniPlayerWebViewService.BuildLiveStatusScript(expectedVideoId));
            if (!IsCurrentVideo(state, expectedVideoId)
                || !IsCurrentWebView(state, webView, runtimes))
            {
                return null;
            }

            if (!YouTubeMiniPlayerWebViewService.TryReadLiveChatStatusResult(
                    result,
                    out var isLive,
                    out var replayContinuation))
            {
                return null;
            }

            YouTubeMiniPlayerWebViewService.TryReadLiveViewerCountResult(
                result,
                out var viewerCountText);
            state.IsLiveBroadcast = isLive;
            if (!isLive)
            {
                state.IsAtLiveEdge = false;
            }

            state.LiveViewerCountText = isLive
                ? YouTubeMiniPlayerStateService.NormalizeLiveViewerCountText(viewerCountText)
                : null;
            YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(
                runtimes[state.Id],
                isLive,
                state.LiveViewerCountText);
            return new YouTubeLiveChatAvailabilityResult(
                expectedVideoId,
                isLive,
                replayContinuation);
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateLiveChatButtonAvailability(
        YouTubeMiniPlayerState state,
        WebView2 webView,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        bool? isAvailable)
    {
        if (!IsCurrentWebView(state, webView, runtimes))
        {
            return;
        }

        YouTubeMiniPlayerChromeFactory.UpdateChatButtonAvailability(
            runtimes[state.Id].TitleBandChatButton,
            isAvailable);
    }

    private static void ResetLiveStatus(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime)
    {
        state.IsLiveBroadcast = false;
        state.IsAtLiveEdge = false;
        state.LiveViewerCountText = null;
        runtime.LiveChatAvailabilityAttemptVideoId = null;
        runtime.LiveChatAvailabilityAttemptCount = 0;
        YouTubeMiniPlayerChromeFactory.UpdateLiveViewerCount(
            runtime,
            isLive: false,
            viewerCountText: null);
    }

    private static bool IsCurrentVideo(YouTubeMiniPlayerState state, string expectedVideoId)
    {
        return YouTubeUrlService.TryGetVideoId(state.Url, out var currentVideoId)
            && currentVideoId.Equals(expectedVideoId, StringComparison.Ordinal);
    }

    private static bool IsCurrentWebView(
        YouTubeMiniPlayerState state,
        WebView2 webView,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes)
    {
        return runtimes.TryGetValue(state.Id, out var runtime)
            && ReferenceEquals(runtime.WebView, webView);
    }

    public static FrameworkElement CreatePlaceholder(
        YouTubeMiniPlayerState state,
        Style buttonStyle,
        Func<YouTubeMiniPlayerState, Uri, Task> loadRuntimeAsync,
        Action<string> setStatus,
        Func<long?> tryBeginGestureInteraction,
        Action<long> completeGestureInteraction,
        Action close)
    {
        var placeholder = YouTubeMiniPlayerPlaceholderFactory.Create(state, buttonStyle);
        YouTubeMiniPlayerPlaceholderLoadService.AttachLoadHandler(
            placeholder,
            state,
            loadRuntimeAsync,
            setStatus);
        YouTubeMiniPlayerPlaceholderLoadService.AttachCloseGestureHandler(
            placeholder,
            tryBeginGestureInteraction,
            completeGestureInteraction,
            close);
        return placeholder.Root;
    }
}
