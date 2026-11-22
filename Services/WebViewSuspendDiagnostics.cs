using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabBrowser.Services;

internal enum WebViewSuspendRoute
{
    InactiveYouTube,
    FocusedViewing,
    MemoryPressure,
    ClosedTab,
    Thumbnail,
    YouTubeMiniPlayer
}

internal enum ClosedTabSuspendOutcome
{
    Timeout,
    Discarded
}

internal readonly record struct WebViewSuspendVisibility(bool? Wpf, bool? Controller);

internal sealed class WebViewSuspendDiagnostics
{
    // WPF版SDKはControllerを公開していない。診断専用の読取りに限り、取得不能はunknownとする。
    private static readonly FieldInfo? s_webViewBase = typeof(WebView2).GetField(
        "m_webview2Base", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? s_controller = s_webViewBase?.FieldType.GetProperty(
        "CoreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dictionary<(WebViewSuspendRoute Route, WebViewSuspendVisibility Visibility), Counts> _counts = [];
    private int _closedTabTimeouts;
    private int _closedTabDiscards;

    public bool HasPending => _counts.Count > 0 || _closedTabTimeouts > 0 || _closedTabDiscards > 0;

    public static Task<bool> TrySuspendAsync(
        CoreWebView2 core,
        WebView2? webView,
        WebViewSuspendRoute route,
        PerformanceDiagnosticLogController? diagnostics)
        => diagnostics is null
            ? core.TrySuspendAsync()
            : MeasureAsync(route, CaptureVisibility(webView), core.TrySuspendAsync, diagnostics);

    internal static async Task<bool> MeasureAsync(
        WebViewSuspendRoute route,
        WebViewSuspendVisibility visibility,
        Func<Task<bool>> suspendAsync,
        PerformanceDiagnosticLogController diagnostics)
    {
        diagnostics.RecordWebViewSuspend(route, visibility);
        bool suspended;
        try
        {
            suspended = await suspendAsync();
        }
        catch (Exception exception)
        {
            diagnostics.RecordWebViewSuspend(route, visibility, exceptionType: exception.GetType().Name);
            throw;
        }

        diagnostics.RecordWebViewSuspend(route, visibility, apiResult: suspended);
        return suspended;
    }

    internal static WebViewSuspendVisibility CaptureVisibility(WebView2? webView)
    {
        if (webView is null || !webView.CheckAccess())
        {
            return default;
        }

        bool? controllerVisible = null;
        try
        {
            if (s_webViewBase?.GetValue(webView) is { } webViewBase
                && s_controller?.GetValue(webViewBase) is CoreWebView2Controller controller)
            {
                controllerVisible = controller.IsVisible;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException
                                                     or TargetInvocationException or MemberAccessException
                                                     or TargetException)
        {
            // 計測に失敗しても既存の休止処理は続ける。
        }

        return new WebViewSuspendVisibility(webView.IsVisible, controllerVisible);
    }

    public void Record(
        WebViewSuspendRoute route, WebViewSuspendVisibility visibility, bool? apiResult, string? exceptionType)
    {
        var key = (route, visibility);
        if (!_counts.TryGetValue(key, out var counts))
        {
            _counts.Add(key, counts = new Counts());
        }

        if (exceptionType is not null)
        {
            counts.Exceptions[exceptionType] = counts.Exceptions.GetValueOrDefault(exceptionType) + 1;
        }
        else if (apiResult is true)
        {
            counts.Success++;
        }
        else if (apiResult is false)
        {
            counts.ApiFalse++;
        }
        else
        {
            counts.Attempts++;
        }
    }

    public void RecordClosedTabOutcome(ClosedTabSuspendOutcome outcome)
    {
        if (outcome == ClosedTabSuspendOutcome.Timeout)
        {
            _closedTabTimeouts++;
        }
        else
        {
            _closedTabDiscards++;
        }
    }

    public void DrainInto(List<string> lines)
    {
        foreach (var ((route, visibility), counts) in _counts)
        {
            var exceptionTypes = string.Join(",", counts.Exceptions
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}:{pair.Value}"));
            lines.Add(DiagnosticLogWriter.BuildLine(
            [
                "runner=perf",
                "operation=webview-suspend-summary",
                $"route={route}",
                $"wpfVisible={FormatVisibility(visibility.Wpf)}",
                $"controllerVisible={FormatVisibility(visibility.Controller)}",
                $"attempts={counts.Attempts}",
                $"success={counts.Success}",
                $"apiFalse={counts.ApiFalse}",
                $"exceptions={counts.Exceptions.Values.Sum()}",
                counts.Exceptions.Count == 0 ? string.Empty : $"exceptionTypes={exceptionTypes}"
            ]));
        }

        if (_closedTabTimeouts > 0 || _closedTabDiscards > 0)
        {
            lines.Add(DiagnosticLogWriter.BuildLine(
            [
                "runner=perf",
                "operation=webview-suspend-summary",
                "route=ClosedTab",
                $"timeouts={_closedTabTimeouts}",
                $"discarded={_closedTabDiscards}"
            ]));
        }

        Clear();
    }

    public void Clear()
    {
        _counts.Clear();
        _closedTabTimeouts = 0;
        _closedTabDiscards = 0;
    }

    private static string FormatVisibility(bool? visible) => visible switch
    {
        true => "true",
        false => "false",
        null => "unknown"
    };

    private sealed class Counts
    {
        public int Attempts { get; set; }
        public int Success { get; set; }
        public int ApiFalse { get; set; }
        public Dictionary<string, int> Exceptions { get; } = new(StringComparer.Ordinal);
    }
}
