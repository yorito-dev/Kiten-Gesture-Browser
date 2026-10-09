using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TabBrowser.Models;
using TabBrowser.Services;

namespace TabBrowser.Views;

internal readonly record struct YouTubeLiveChatSource(
    YouTubeMiniPlayerRuntime Runtime, string Source, Func<string, Task<string>> ExecuteScriptAsync);

internal interface IYouTubeLiveChatHost
{
    bool IsClosingWindow { get; }
    bool ContainsState(YouTubeMiniPlayerState state);
    bool TryGetReadySource(Guid id, out YouTubeLiveChatSource source);
    bool TryGetRuntime(Guid id, out YouTubeMiniPlayerRuntime runtime);
    bool IsCurrentRuntime(Guid id, YouTubeMiniPlayerRuntime runtime);
    bool IsCurrentVideo(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, string videoId);
    void QueueAvailabilityRefresh(YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime);
    Task<YouTubeLiveChatAvailabilityResult?> RefreshAvailabilityAsync(YouTubeMiniPlayerState state, WebView2 webView);
    Task CaptureStateAsync(YouTubeMiniPlayerState state);
    void UpdateChatButtonAvailability(YouTubeMiniPlayerRuntime runtime, bool? available);
    void UpdateLiveViewerCount(YouTubeMiniPlayerRuntime runtime, bool isLive, string? text);
    Task<CoreWebView2Environment> EnsureEnvironmentAsync(string? containerProfileId);
    IYouTubeLiveChatWindowSurface CreateWindow(
        YouTubeMiniPlayerState state, YouTubeMiniPlayerRuntime runtime, CoreWebView2Environment environment,
        Uri uri, Func<Task<YouTubeMiniPlayerPlaybackStateSnapshot?>> capturePlayback);
    void QueueSave();
    void SetStatus(string message);
    void InvalidateArrangementPreviews(Guid id);
    void QueueRender(YouTubeMiniPlayerRuntime runtime, Action action);
    Rect GetVisibleWindowBounds(YouTubeMiniPlayerRuntime runtime);
}

internal sealed class YouTubeLiveChatWindowController(IYouTubeLiveChatHost host)
{
    private readonly IYouTubeLiveChatHost _host = host;
    private readonly Dictionary<Guid, IYouTubeLiveChatWindowSurface> _youTubeLiveChatWindows = [];
    private readonly HashSet<Guid> _openingYouTubeLiveChatWindows = [];

    public bool ContainsWindow(Guid id) => _youTubeLiveChatWindows.ContainsKey(id);

    public (int LoadedCount, int OrphanedWindowCount, int OrphanedPendingCount) GetDiagnosticSnapshot()
    {
        var orphanedWindows = _youTubeLiveChatWindows.Count(pair =>
            !pair.Value.IsLoaded
            || !_host.TryGetRuntime(pair.Key, out var runtime)
            || runtime.IsDisposed);
        var orphanedPending = _openingYouTubeLiveChatWindows.Count(id =>
            !_host.TryGetRuntime(id, out var runtime)
            || runtime.IsDisposed);
        return (_youTubeLiveChatWindows.Count(static pair => pair.Value.IsLoaded), orphanedWindows, orphanedPending);
    }

    public bool TryGetWindow(Guid id, out YouTubeLiveChatWindow window)
    {
        if (_youTubeLiveChatWindows.TryGetValue(id, out var surface) && surface.NativeWindow is { } nativeWindow)
        {
            window = nativeWindow;
            return true;
        }
        window = null!;
        return false;
    }

    public async Task OpenYouTubeLiveChatAsync(YouTubeMiniPlayerState state)
    {
        if (state.SourceKind != MiniPlayerSourceKind.YouTube)
        {
            return;
        }

        if (_host.TryGetReadySource(state.Id, out var currentSource)
            && YouTubeMiniPlayerNavigationService.TryApplyCurrentVideoUrl(
                state,
                currentSource.Source))
        {
            BeginYouTubeLiveChatVideoTransition(state);
            _host.QueueSave();
            _host.QueueAvailabilityRefresh(state, currentSource.Runtime);
        }

        if (_youTubeLiveChatWindows.TryGetValue(state.Id, out var existingWindow))
        {
            existingWindow.BringToFront();
            return;
        }

        if (!_openingYouTubeLiveChatWindows.Add(state.Id))
        {
            _host.SetStatus("ライブチャットを確認しています");
            return;
        }

        YouTubeMiniPlayerRuntime? runtime = null;
        IYouTubeLiveChatWindowSurface? openedWindow = null;
        string? requestedVideoId = null;
        var chatAvailability = true;
        try
        {
            if (!_host.TryGetReadySource(state.Id, out var source))
            {
                _host.SetStatus("先にミニプレイヤーの動画を読み込んでください");
                return;
            }

            runtime = source.Runtime;
            if (!YouTubeUrlService.TryGetVideoId(state.Url, out var videoId))
            {
                _host.SetStatus("チャットの動画IDを確認できません");
                return;
            }

            _host.UpdateChatButtonAvailability(runtime, null);
            string liveStatusJson;
            try
            {
                liveStatusJson = await source.ExecuteScriptAsync(
                    YouTubeMiniPlayerWebViewService.BuildLiveStatusScript(videoId));
            }
            catch
            {
                _host.SetStatus("ライブ配信状態を確認できませんでした");
                return;
            }

            if (!_host.IsCurrentVideo(state, runtime, videoId))
            {
                return;
            }
            requestedVideoId = videoId;

            if (!YouTubeMiniPlayerWebViewService.TryReadLiveChatStatusResult(
                    liveStatusJson,
                    out var isLive,
                    out var replayContinuation))
            {
                _host.SetStatus("動画の読み込み完了後にチャットを開いてください");
                return;
            }

            YouTubeMiniPlayerWebViewService.TryReadLiveViewerCountResult(
                liveStatusJson,
                out var viewerCountText);
            state.IsLiveBroadcast = isLive;
            if (!isLive)
            {
                state.IsAtLiveEdge = false;
            }

            state.LiveViewerCountText = isLive
                ? YouTubeMiniPlayerStateService.NormalizeLiveViewerCountText(viewerCountText)
                : null;
            _host.UpdateLiveViewerCount(runtime, isLive, state.LiveViewerCountText);

            var hasReplay = !string.IsNullOrWhiteSpace(replayContinuation);
            chatAvailability = isLive || hasReplay;
            _host.UpdateChatButtonAvailability(runtime, chatAvailability);
            if (!chatAvailability)
            {
                _host.SetStatus("この動画ではライブチャットまたはチャットリプレイを利用できません");
                return;
            }

            Uri chatUri;
            var hasChatUri = isLive
                ? YouTubeMiniPlayerWebViewService.TryCreateLiveChatUri(state.Url, out chatUri)
                : YouTubeMiniPlayerWebViewService.TryCreateLiveChatReplayUri(
                    state.Url,
                    replayContinuation,
                    out chatUri);
            if (!hasChatUri)
            {
                _host.SetStatus("チャットの表示先を確認できません");
                return;
            }

            var environment = await _host.EnsureEnvironmentAsync(state.ContainerProfileId);
            if (!_host.IsCurrentVideo(state, runtime, videoId))
            {
                return;
            }

            if (_youTubeLiveChatWindows.TryGetValue(state.Id, out existingWindow))
            {
                existingWindow.BringToFront();
                return;
            }

            var chatWindow = _host.CreateWindow(
                state, runtime, environment, chatUri,
                () => CaptureYouTubeLiveChatPlaybackStateAsync(state, runtime));
            openedWindow = chatWindow;
            chatWindow.Closed += (_, _) =>
            {
                _host.InvalidateArrangementPreviews(state.Id);
                if (_youTubeLiveChatWindows.TryGetValue(state.Id, out var current)
                    && ReferenceEquals(current, chatWindow))
                {
                    _youTubeLiveChatWindows.Remove(state.Id);
                }
            };
            chatWindow.LayoutChanged += (_, _) =>
                _host.InvalidateArrangementPreviews(state.Id);
            _youTubeLiveChatWindows[state.Id] = chatWindow;
            chatWindow.Show();
            UpdateYouTubeLiveChatFullScreenOverlay(runtime, runtime.IsFullScreen);
            _host.QueueSave();
            await chatWindow.InitializeAsync();
        }
        catch (Exception ex)
        {
            _host.SetStatus("ライブチャットWindowを開けませんでした: " + ex.Message);
            if (openedWindow is not null
                && _youTubeLiveChatWindows.TryGetValue(state.Id, out var currentWindow)
                && ReferenceEquals(currentWindow, openedWindow))
            {
                CloseYouTubeLiveChatWindow(state.Id);
            }
        }
        finally
        {
            _openingYouTubeLiveChatWindows.Remove(state.Id);
            if (runtime is not null
                && requestedVideoId is not null
                && _host.IsCurrentVideo(state, runtime, requestedVideoId))
            {
                _host.UpdateChatButtonAvailability(runtime, chatAvailability);
            }
        }
    }

    public void BeginYouTubeLiveChatVideoTransition(YouTubeMiniPlayerState state)
    {
        if (_youTubeLiveChatWindows.TryGetValue(state.Id, out var chatWindow)
            && YouTubeUrlService.TryGetVideoId(state.Url, out var videoId))
        {
            chatWindow.BeginVideoTransition(videoId);
        }
    }

    public async Task ApplyYouTubeLiveChatAvailabilityAsync(
        YouTubeMiniPlayerState state,
        YouTubeLiveChatAvailabilityResult availability)
    {
        if (_host.IsClosingWindow
            || !YouTubeUrlService.TryGetVideoId(state.Url, out var currentVideoId)
            || !currentVideoId.Equals(availability.VideoId, StringComparison.Ordinal)
            || !_youTubeLiveChatWindows.TryGetValue(state.Id, out var chatWindow))
        {
            return;
        }

        if (!availability.IsAvailable)
        {
            chatWindow.ShowUnavailable(availability.VideoId);
            return;
        }

        Uri chatUri;
        var hasChatUri = availability.IsLive
            ? YouTubeMiniPlayerWebViewService.TryCreateLiveChatUri(state.Url, out chatUri)
            : YouTubeMiniPlayerWebViewService.TryCreateLiveChatReplayUri(
                state.Url,
                availability.ReplayContinuation,
                out chatUri);
        if (!hasChatUri)
        {
            chatWindow.ShowUnavailable(
                availability.VideoId,
                "チャットの表示先を確認できません");
            return;
        }

        await chatWindow.SwitchVideoAsync(availability.VideoId, chatUri);
    }

    public async Task RefreshYouTubeLiveChatAvailabilityAsync(
        YouTubeMiniPlayerState state,
        WebView2 webView)
    {
        var availability = await _host.RefreshAvailabilityAsync(state, webView);
        if (availability is { } currentAvailability)
        {
            await ApplyYouTubeLiveChatAvailabilityAsync(state, currentAvailability);
        }
    }

    private async Task<YouTubeMiniPlayerPlaybackStateSnapshot?> CaptureYouTubeLiveChatPlaybackStateAsync(
        YouTubeMiniPlayerState state,
        YouTubeMiniPlayerRuntime runtime)
    {
        if (_host.IsClosingWindow
            || !_host.ContainsState(state)
            || !_host.IsCurrentRuntime(state.Id, runtime))
        {
            return null;
        }

        await _host.CaptureStateAsync(state);
        return _host.IsCurrentRuntime(state.Id, runtime)
            ? YouTubeMiniPlayerStateService.CreatePlaybackStateSnapshot(state)
            : null;
    }

    public bool HasActiveYouTubeLiveChatWindow()
    {
        return _youTubeLiveChatWindows.Values.Any(static window => window.IsActive);
    }

    public void SynchronizeYouTubeLiveChatWindow(Guid miniPlayerId)
    {
        if (!_youTubeLiveChatWindows.TryGetValue(miniPlayerId, out var chatWindow))
        {
            return;
        }

        if (_host.TryGetRuntime(miniPlayerId, out var runtime)
            && runtime.IsFullScreen)
        {
            UpdateYouTubeLiveChatFullScreenSourceBounds(runtime, chatWindow);
            return;
        }

        chatWindow.SynchronizeWithSourceWindow();
    }

    public void UpdateYouTubeLiveChatFullScreenOverlay(
        YouTubeMiniPlayerRuntime runtime,
        bool isFullScreen)
    {
        if (!_youTubeLiveChatWindows.TryGetValue(runtime.State.Id, out var chatWindow))
        {
            return;
        }

        if (!isFullScreen)
        {
            chatWindow.ExitFullScreenOverlay();
            return;
        }

        _host.QueueRender(runtime, () =>
            {
                if (!runtime.IsFullScreen
                    || !_host.IsCurrentRuntime(runtime.State.Id, runtime)
                    || !_youTubeLiveChatWindows.TryGetValue(runtime.State.Id, out var currentChatWindow)
                    || !ReferenceEquals(chatWindow, currentChatWindow))
                {
                    return;
                }

                currentChatWindow.EnterFullScreenOverlay(
                    _host.GetVisibleWindowBounds(runtime),
                    runtime.State.IsTopmost);
            });
    }

    private void UpdateYouTubeLiveChatFullScreenSourceBounds(
        YouTubeMiniPlayerRuntime runtime,
        IYouTubeLiveChatWindowSurface chatWindow)
    {
        _host.QueueRender(runtime, () =>
            {
                if (!runtime.IsFullScreen
                    || !_host.IsCurrentRuntime(runtime.State.Id, runtime)
                    || !_youTubeLiveChatWindows.TryGetValue(runtime.State.Id, out var currentChatWindow)
                    || !ReferenceEquals(chatWindow, currentChatWindow))
                {
                    return;
                }

                currentChatWindow.UpdateFullScreenSourceBounds(
                    _host.GetVisibleWindowBounds(runtime),
                    runtime.State.IsTopmost);
            });
    }

    public void BringYouTubeLiveChatWindowToFront(Guid miniPlayerId)
    {
        if (_youTubeLiveChatWindows.TryGetValue(miniPlayerId, out var chatWindow))
        {
            chatWindow.FollowSourceToFront();
        }
    }

    public void CloseYouTubeLiveChatWindow(Guid miniPlayerId)
    {
        _openingYouTubeLiveChatWindows.Remove(miniPlayerId);
        if (_youTubeLiveChatWindows.Remove(miniPlayerId, out var chatWindow))
        {
            chatWindow.Close();
        }
    }

    public void CloseAllYouTubeLiveChatWindows()
    {
        _openingYouTubeLiveChatWindows.Clear();
        foreach (var chatWindow in _youTubeLiveChatWindows.Values.ToList())
        {
            chatWindow.Close();
        }

        _youTubeLiveChatWindows.Clear();
    }
}
