using System.Runtime.InteropServices;
using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class YouTubeControlService
{
    private readonly TabRuntimeCoordinator _runtimeCoordinator;
    private readonly Func<IEnumerable<BrowserTab>> _getTabs;
    private readonly Func<BrowserTab, bool> _containsTab;
    private readonly Func<BrowserTab, bool> _shouldCapturePeriodicPlaybackState;
    private readonly Func<BrowserTab, bool> _shouldUpdateSpeedDisplay;
    private readonly Func<BrowserTab, string?> _getCurrentPageUrl;
    private readonly Func<BrowserTab, string?> _getWebMessageToken;
    private readonly Func<BrowserTab, string, bool, Task<string?>> _executeScriptAsync;
    private readonly Action<string, bool> _setStatus;
    private readonly Action _queueSessionSave;
    private readonly Action<BrowserTab, double?> _updateSpeedDisplay;
    private readonly Action<BrowserTab, bool?> _updateLoopDisplay;
    private readonly Dictionary<Guid, string> _lastQualityAttemptUrls = [];
    private readonly Dictionary<Guid, YouTubePlaybackRestoreRequest> _pendingPlaybackRestoreRequests = [];
    private bool _isCapturingPlaybackState;

    public YouTubeControlService(
        Func<IEnumerable<BrowserTab>> getTabs,
        Func<BrowserTab, bool> containsTab,
        Func<BrowserTab, bool> shouldCapturePeriodicPlaybackState,
        Func<BrowserTab, bool> shouldUpdateSpeedDisplay,
        Func<BrowserTab, string?> getCurrentPageUrl,
        Func<BrowserTab, string?> getWebMessageToken,
        Func<BrowserTab, string, bool, Task<string?>> executeScriptAsync,
        Action<string, bool> setStatus,
        Action queueSessionSave,
        Action<BrowserTab, double?> updateSpeedDisplay,
        Action<BrowserTab, bool?> updateLoopDisplay)
        : this(
            getTabs,
            containsTab,
            shouldCapturePeriodicPlaybackState,
            shouldUpdateSpeedDisplay,
            getCurrentPageUrl,
            getWebMessageToken,
            executeScriptAsync,
            setStatus,
            queueSessionSave,
            updateSpeedDisplay,
            updateLoopDisplay,
            TabRuntimeCoordinator.Shared)
    {
    }

    internal YouTubeControlService(
        Func<IEnumerable<BrowserTab>> getTabs,
        Func<BrowserTab, bool> containsTab,
        Func<BrowserTab, bool> shouldCapturePeriodicPlaybackState,
        Func<BrowserTab, bool> shouldUpdateSpeedDisplay,
        Func<BrowserTab, string?> getCurrentPageUrl,
        Func<BrowserTab, string?> getWebMessageToken,
        Func<BrowserTab, string, bool, Task<string?>> executeScriptAsync,
        Action<string, bool> setStatus,
        Action queueSessionSave,
        Action<BrowserTab, double?> updateSpeedDisplay,
        Action<BrowserTab, bool?> updateLoopDisplay,
        TabRuntimeCoordinator runtimeCoordinator)
    {
        _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
        _getTabs = getTabs;
        _containsTab = containsTab;
        _shouldCapturePeriodicPlaybackState = shouldCapturePeriodicPlaybackState;
        _shouldUpdateSpeedDisplay = shouldUpdateSpeedDisplay;
        _getCurrentPageUrl = getCurrentPageUrl;
        _getWebMessageToken = getWebMessageToken;
        _executeScriptAsync = executeScriptAsync;
        _setStatus = setStatus;
        _queueSessionSave = queueSessionSave;
        _updateSpeedDisplay = updateSpeedDisplay;
        _updateLoopDisplay = updateLoopDisplay;
    }

    public async Task ApplyBestQualityForTabAsync(BrowserTab tab, bool showStatus)
    {
        var resultJson = await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildBestQualityScript(),
            showStatus);
        if (!showStatus || string.IsNullOrWhiteSpace(resultJson))
        {
            return;
        }

        if (TryReadScriptString(resultJson, "quality", out var quality) && !string.IsNullOrWhiteSpace(quality))
        {
            _setStatus($"YouTube 最高画質を試行しました: {quality}", true);
        }
        else
        {
            _setStatus("YouTube 最高画質を試行しました", true);
        }
    }

    public void QueueBestQualityAttempt(BrowserTab tab)
    {
        var url = _getCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(url) || !YouTubeUrlService.IsVideoUrl(url))
        {
            return;
        }

        if (_lastQualityAttemptUrls.TryGetValue(tab.Id, out var lastUrl)
            && string.Equals(lastUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastQualityAttemptUrls[tab.Id] = url;
        _ = EnsureBestQualityReadyBridgeAsync(tab, url);
    }

    public void QueuePlaybackStateBridgeAttempt(BrowserTab tab)
    {
        var url = _getCurrentPageUrl(tab);
        if (string.IsNullOrWhiteSpace(url) || !YouTubeUrlService.IsVideoUrl(url))
        {
            return;
        }

        _ = EnsurePlaybackStateBridgeAsync(tab, _shouldCapturePeriodicPlaybackState(tab));
    }

    public async Task<bool> CapturePlaybackStateForLoadedTabsAsync(
        bool queueSaveOnChange,
        bool activeOnly = true)
    {
        if (_isCapturingPlaybackState)
        {
            return false;
        }

        _isCapturingPlaybackState = true;
        try
        {
            var changed = false;
            foreach (var tab in _getTabs().ToList())
            {
                if (activeOnly && !_shouldCapturePeriodicPlaybackState(tab))
                {
                    continue;
                }

                changed |= await CapturePlaybackStateAsync(tab);
            }

            if (changed && queueSaveOnChange)
            {
                _queueSessionSave();
            }

            return changed;
        }
        finally
        {
            _isCapturingPlaybackState = false;
        }
    }

    public async Task<bool> CapturePlaybackStateAsync(BrowserTab tab)
    {
        try
        {
            if (!_containsTab(tab)
                || tab.IsWorkspaceTab
                || _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is null
                || _pendingPlaybackRestoreRequests.ContainsKey(tab.Id))
            {
                return false;
            }

            var url = _getCurrentPageUrl(tab);
            if (string.IsNullOrWhiteSpace(url) || !YouTubeUrlService.IsVideoUrl(url))
            {
                return false;
            }

            var resultJson = await _executeScriptAsync(
                tab,
                YouTubeControlScriptService.BuildPlaybackStateReadScript(),
                false);
            if (!YouTubePlaybackStateService.TryCreateCapturedStateUpdate(url, resultJson, out var update))
            {
                return false;
            }

            return YouTubePlaybackStateService.UpdatePlaybackState(
                tab,
                update.VideoId,
                update.PositionSeconds,
                update.Speed,
                update.IsPaused,
                update.IsLooping,
                update.IsTheaterMode,
                allowDefaultSpeedChange: false);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool ApplyPlaybackStateMessage(BrowserTab tab, JsonElement root)
    {
        if (!_containsTab(tab) || tab.IsWorkspaceTab)
        {
            return false;
        }

        if (_pendingPlaybackRestoreRequests.ContainsKey(tab.Id))
        {
            var isRestoredState = root.TryGetProperty("reason", out var reasonProperty)
                && reasonProperty.ValueKind == JsonValueKind.String
                && string.Equals(reasonProperty.GetString(), "restore", StringComparison.Ordinal);
            if (!isRestoredState)
            {
                return false;
            }

            _pendingPlaybackRestoreRequests.Remove(tab.Id);
        }

        return YouTubePlaybackStateService.ApplyPlaybackStateMessage(tab, root, _getCurrentPageUrl(tab));
    }

    public void QueuePlaybackRestoreAttempt(BrowserTab tab)
    {
        if (!_containsTab(tab) || tab.IsWorkspaceTab)
        {
            return;
        }

        var currentUrl = _getCurrentPageUrl(tab);
        if (!YouTubeUrlService.TryGetVideoId(currentUrl, out var currentVideoId))
        {
            _pendingPlaybackRestoreRequests.Remove(tab.Id);
            return;
        }

        if (!_pendingPlaybackRestoreRequests.TryGetValue(tab.Id, out var request)
            || !string.Equals(request.VideoId, currentVideoId, StringComparison.Ordinal))
        {
            if (!YouTubePlaybackStateService.TryCreateRestoreRequest(tab, currentUrl, out request))
            {
                _pendingPlaybackRestoreRequests.Remove(tab.Id);
                return;
            }

            _pendingPlaybackRestoreRequests[tab.Id] = request;
        }

        _ = InstallPlaybackStateRestoreReadyBridgeAsync(tab, request);
    }

    public async Task SetPlaybackStateBridgeActiveAsync(BrowserTab tab, bool isActive)
    {
        try
        {
            if (!_containsTab(tab)
                || tab.IsWorkspaceTab
                || _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is null)
            {
                return;
            }

            await _executeScriptAsync(
                tab,
                YouTubeControlScriptService.BuildSetPlaybackStateBridgeActiveScript(isActive),
                false);
        }
        catch
        {
            // ページ遷移中は、次回のブリッジ注入で現在の有効状態を反映します。
        }
    }

    public async Task ChangePlaybackSpeedAsync(BrowserTab tab, double delta)
    {
        var resultJson = await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildSpeedScript(delta, targetSpeed: null),
            true);
        var changed = ShowSpeedStatus(tab, resultJson);
        changed |= await CapturePlaybackStateAsync(tab);
        if (changed)
        {
            _queueSessionSave();
        }
    }

    public async Task SetPlaybackSpeedAsync(BrowserTab tab, double speed)
    {
        var resultJson = await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildSpeedScript(delta: null, speed),
            true);
        var changed = ShowSpeedStatus(tab, resultJson);
        changed |= await CapturePlaybackStateAsync(tab);
        if (changed)
        {
            _queueSessionSave();
        }
    }

    public async Task TogglePlaybackLoopAsync(BrowserTab tab)
    {
        var resultJson = await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildLoopScript(targetLooping: null),
            true);
        if (!string.IsNullOrWhiteSpace(resultJson)
            && TryReadScriptBoolean(resultJson, "looping", out var isLooping))
        {
            _updateLoopDisplay(tab, isLooping);
            _setStatus(isLooping ? "YouTube リピート再生を有効にしました" : "YouTube リピート再生を無効にしました", true);
        }
        else
        {
            _setStatus("YouTube リピート再生を切り替えました", true);
        }

        if (await CapturePlaybackStateAsync(tab))
        {
            _queueSessionSave();
        }
    }

    public async Task RefreshSpeedDisplayAsync(BrowserTab tab)
    {
        await RefreshControlDisplayAsync(tab);
    }

    public async Task RefreshControlDisplayAsync(BrowserTab tab)
    {
        if (!_shouldUpdateSpeedDisplay(tab))
        {
            return;
        }

        var resultJson = await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildPlaybackStateReadScript(),
            false);
        var playbackStateChanged = false;
        if (_shouldUpdateSpeedDisplay(tab)
            && !string.IsNullOrWhiteSpace(resultJson))
        {
            if (TryReadScriptDouble(resultJson, "speed", out var speed)
                && YouTubePlaybackStateService.ResolveReportedPlaybackSpeed(
                    tab.YouTubePlaybackSpeed,
                    speed,
                    allowDefaultSpeedChange: false) is { } displaySpeed)
            {
                if (YouTubeUrlService.TryGetVideoId(_getCurrentPageUrl(tab), out var videoId))
                {
                    playbackStateChanged = YouTubePlaybackStateService.UpdatePlaybackState(
                        tab,
                        videoId,
                        positionSeconds: null,
                        speed: displaySpeed,
                        isPaused: null,
                        isLooping: null,
                        isTheaterMode: null,
                        allowDefaultSpeedChange: false);
                    displaySpeed = tab.YouTubePlaybackSpeed ?? displaySpeed;
                }

                _updateSpeedDisplay(tab, displaySpeed);
            }

            if (TryReadScriptBoolean(resultJson, "looping", out var isLooping))
            {
                _updateLoopDisplay(tab, isLooping);
            }
        }

        if (playbackStateChanged)
        {
            _queueSessionSave();
        }
    }

    public async Task RequestFullscreenAsync(BrowserTab tab)
    {
        await _executeScriptAsync(
            tab,
            YouTubeControlScriptService.BuildFullscreenScript(),
            true);
    }

    public void RemoveTab(BrowserTab tab)
    {
        _lastQualityAttemptUrls.Remove(tab.Id);
        _pendingPlaybackRestoreRequests.Remove(tab.Id);
    }

    private async Task EnsureBestQualityReadyBridgeAsync(BrowserTab tab, string expectedUrl)
    {
        try
        {
            if (!_containsTab(tab)
                || tab.IsWorkspaceTab
                || _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is null
                || !string.Equals(
                    _getCurrentPageUrl(tab),
                    expectedUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_containsTab(tab)
                    && _lastQualityAttemptUrls.TryGetValue(tab.Id, out var currentUrl)
                    && string.Equals(currentUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _lastQualityAttemptUrls.Remove(tab.Id);
                }

                return;
            }

            var result = await _executeScriptAsync(
                tab,
                YouTubeControlScriptService.BuildBestQualityReadyBridgeScript(),
                false);
            if (string.IsNullOrWhiteSpace(result))
            {
                _lastQualityAttemptUrls.Remove(tab.Id);
            }
        }
        catch
        {
            if (_lastQualityAttemptUrls.TryGetValue(tab.Id, out var currentUrl)
                && string.Equals(currentUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
            {
                _lastQualityAttemptUrls.Remove(tab.Id);
            }
        }
    }

    private async Task EnsurePlaybackStateBridgeAsync(BrowserTab tab, bool isActive)
    {
        try
        {
            if (!_containsTab(tab)
                || tab.IsWorkspaceTab
                || _runtimeCoordinator.GetWebView(tab)?.CoreWebView2 is null)
            {
                return;
            }

            await _executeScriptAsync(
                tab,
                YouTubeControlScriptService.BuildPlaybackStateBridgeScript(
                    _getWebMessageToken(tab),
                    isActive),
                false);
        }
        catch
        {
            // 次回の NavigationCompleted / SourceChanged で再試行します。
        }
    }

    private async Task InstallPlaybackStateRestoreReadyBridgeAsync(
        BrowserTab tab,
        YouTubePlaybackRestoreRequest request)
    {
        try
        {
            if (!_containsTab(tab))
            {
                return;
            }

            var currentUrl = _getCurrentPageUrl(tab);
            if (!YouTubeUrlService.TryGetVideoId(currentUrl, out var currentVideoId)
                || !string.Equals(currentVideoId, request.VideoId, StringComparison.Ordinal))
            {
                return;
            }

            var resultJson = await _executeScriptAsync(
                tab,
                YouTubeControlScriptService.BuildPlaybackStateRestoreReadyBridgeScript(
                    _getWebMessageToken(tab),
                    request.VideoId,
                    request.PositionSeconds,
                    request.Speed,
                    request.IsPaused,
                    request.IsLooping,
                    request.IsTheaterMode),
                false);
            if (!string.IsNullOrWhiteSpace(resultJson)
                && TryReadScriptBoolean(resultJson, "applied", out var applied)
                && applied
                && _pendingPlaybackRestoreRequests.TryGetValue(tab.Id, out var pendingRequest)
                && pendingRequest.Equals(request))
            {
                _pendingPlaybackRestoreRequests.Remove(tab.Id);
            }
        }
        catch
        {
            // NavigationCompleted / SourceChanged の次回通知で再注入します。
        }
    }

    private bool ShowSpeedStatus(BrowserTab tab, string? resultJson)
    {
        if (!string.IsNullOrWhiteSpace(resultJson)
            && TryReadScriptDouble(resultJson, "speed", out var speed)
            && YouTubeControlScriptService.NormalizePlaybackSpeed(speed) is { } normalizedSpeed)
        {
            var changed = false;
            if (YouTubeUrlService.TryGetVideoId(_getCurrentPageUrl(tab), out var videoId))
            {
                changed = YouTubePlaybackStateService.UpdatePlaybackState(
                    tab,
                    videoId,
                    positionSeconds: null,
                    speed: normalizedSpeed,
                    isPaused: null,
                    isLooping: null,
                    isTheaterMode: null,
                    allowDefaultSpeedChange: true);
            }

            _updateSpeedDisplay(tab, normalizedSpeed);
            _setStatus($"YouTube 再生速度: {normalizedSpeed:0.##}x", true);
            return changed;
        }

        _setStatus("YouTube 再生速度を変更しました", true);
        return false;
    }

    private static bool TryReadScriptString(string resultJson, string propertyName, out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString() ?? string.Empty;
                return true;
            }
        }
        catch
        {
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadScriptDouble(string resultJson, string propertyName, out double value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadScriptBoolean(string resultJson, string propertyName, out bool value)
    {
        value = false;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }
        }
        catch
        {
            value = false;
            return false;
        }

        return false;
    }
}
