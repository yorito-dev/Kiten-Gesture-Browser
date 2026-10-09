using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace TabBrowser.Services;

internal static class WebViewUserGestureScriptService
{
    public static string BuildEvaluationParameters(string script)
    {
        return JsonSerializer.Serialize(new
        {
            expression = script,
            awaitPromise = true,
            returnByValue = true,
            userGesture = true
        });
    }

    public static async Task<string?> ExecuteAsync(CoreWebView2 core, string script)
    {
        var evaluationResultJson = await core.CallDevToolsProtocolMethodAsync(
            "Runtime.evaluate",
            BuildEvaluationParameters(script));
        return TryExtractEvaluationResult(evaluationResultJson, out var resultJson)
            ? resultJson
            : null;
    }

    public static bool TryExtractEvaluationResult(
        string? evaluationResultJson,
        out string? resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(evaluationResultJson ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("exceptionDetails", out _)
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("value", out var value))
            {
                resultJson = null;
                return false;
            }

            resultJson = value.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            resultJson = null;
            return false;
        }
    }
}
