using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class YouTubeMiniPlayerScriptExecutionService
{
    public static bool IsSuccessfulResult(string? resultJson)
    {
        return !string.IsNullOrWhiteSpace(resultJson)
            && TryReadScriptBoolean(resultJson, "ok", out var ok)
            && ok;
    }

    public static async Task<string?> ExecuteAsync(
        YouTubeMiniPlayerState state,
        IReadOnlyDictionary<Guid, YouTubeMiniPlayerRuntime> runtimes,
        string script,
        bool showStatus,
        Action<string> setStatus,
        bool treatAsUserGesture = false)
    {
        if (!runtimes.TryGetValue(state.Id, out var runtime))
        {
            if (showStatus)
            {
                setStatus("ミニプレイヤーをまだ操作できません");
            }

            return null;
        }

        try
        {
            if (YouTubeMiniPlayerRuntimeLifecycleService.TryGetCoreWebView2(runtime) is not { } core)
            {
                if (showStatus)
                {
                    setStatus("ミニプレイヤーをまだ操作できません");
                }

                return null;
            }

            var resultJson = treatAsUserGesture
                ? await WebViewUserGestureScriptService.ExecuteAsync(core, script)
                : await core.ExecuteScriptAsync(script);
            if (showStatus
                && !string.IsNullOrWhiteSpace(resultJson)
                && TryReadScriptBoolean(resultJson, "ok", out var ok)
                && !ok)
            {
                setStatus(YouTubeControlScriptService.IsAdvertisementBlockedResult(resultJson)
                    ? YouTubeControlScriptService.AdvertisementBlockedMessage
                    : "ミニプレイヤー内の動画を操作できません");
            }

            return resultJson;
        }
        catch (Exception ex)
        {
            if (showStatus)
            {
                setStatus("ミニプレイヤー操作に失敗しました: " + ex.Message);
            }

            return null;
        }
    }

    private static bool TryReadScriptBoolean(string resultJson, string propertyName, out bool value)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
            {
                value = property.GetBoolean();
                return true;
            }
        }
        catch
        {
        }

        value = false;
        return false;
    }
}
