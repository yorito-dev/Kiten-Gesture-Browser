using System.Diagnostics;

namespace TabBrowser.Services;

public static class StartupPerformanceStage
{
    public const string ApplicationStarted = "application-started";
    public const string WindowConstructorStarted = "window-constructor-started";
    public const string WindowShellInitialized = "window-shell-initialized";
    public const string WindowLoaded = "window-loaded";
    public const string WindowContentRendered = "window-content-rendered";
    public const string GestureScriptLoadStarted = "gesture-script-load-started";
    public const string GestureScriptLoadCompleted = "gesture-script-load-completed";
    public const string SessionLoadStarted = "session-load-started";
    public const string SessionLoadCompleted = "session-load-completed";
    public const string SessionRestoreStarted = "session-restore-started";
    public const string SessionRestoreCompleted = "session-restore-completed";
    public const string SelectedTabActivationStarted = "selected-tab-activation-started";
    public const string EnvironmentReady = "webview-environment-ready";
    public const string CoreWebViewReady = "webview-core-ready";
    public const string WebViewConfigured = "webview-configured";
    public const string SelectedTabRuntimeReady = "selected-tab-runtime-ready";
    public const string SelectedTabNavigationCompleted = "selected-tab-navigation-completed";
    public const string SecondaryWindowRestoreQueued = "secondary-window-restore-queued";
    public const string SecondaryWindowRestoreStarted = "secondary-window-restore-started";
    public const string SecondaryWindowShown = "secondary-window-shown";
    public const string BackgroundLoadStarted = "background-load-started";
    public const string BackgroundLoadCompleted = "background-load-completed";
    public const string ThumbnailWarmupStarted = "thumbnail-warmup-started";
    public const string ThumbnailWarmupCompleted = "thumbnail-warmup-completed";
    public const string StartupReady = "startup-ready";

    public static string GetDisplayName(string stage)
    {
        return stage switch
        {
            ApplicationStarted => "Application開始",
            WindowConstructorStarted => "Window構築開始",
            WindowShellInitialized => "基本UI初期化完了",
            WindowLoaded => "Window Loaded",
            WindowContentRendered => "基本UI描画完了",
            GestureScriptLoadStarted => "GestureHost読込開始",
            GestureScriptLoadCompleted => "GestureHost読込完了",
            SessionLoadStarted => "セッション読込開始",
            SessionLoadCompleted => "セッション読込完了",
            SessionRestoreStarted => "タブモデル復元開始",
            SessionRestoreCompleted => "タブモデル復元完了",
            SelectedTabActivationStarted => "選択タブ起動開始",
            EnvironmentReady => "WebView2 Environment準備完了",
            CoreWebViewReady => "CoreWebView2準備完了",
            WebViewConfigured => "WebView2設定完了",
            SelectedTabRuntimeReady => "選択タブRuntime準備完了",
            SelectedTabNavigationCompleted => "選択タブ表示完了",
            SecondaryWindowRestoreQueued => "追加Window復元待機",
            SecondaryWindowRestoreStarted => "追加Window復元開始",
            SecondaryWindowShown => "追加Window表示完了",
            BackgroundLoadStarted => "背景タブ読込開始",
            BackgroundLoadCompleted => "背景タブ読込完了",
            ThumbnailWarmupStarted => "サムネイル準備開始",
            ThumbnailWarmupCompleted => "サムネイル準備完了",
            StartupReady => "起動処理準備完了",
            _ => stage
        };
    }
}

public sealed record StartupPerformanceEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    long ElapsedMilliseconds,
    string Stage,
    int? WindowIndex,
    Guid? TabId,
    int? ItemCount);

public sealed class StartupPerformanceTracker
{
    public const int DefaultCapacity = 512;

    private readonly object _gate = new();
    private readonly Queue<StartupPerformanceEntry> _entries = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly int _capacity;
    private long _nextSequence;

    public StartupPerformanceTracker(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public StartupPerformanceEntry Mark(
        string stage,
        int? windowIndex = null,
        Guid? tabId = null,
        int? itemCount = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            var entry = new StartupPerformanceEntry(
                ++_nextSequence,
                occurredAt ?? DateTimeOffset.Now,
                (long)Math.Round(Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds),
                stage,
                windowIndex,
                tabId,
                itemCount);
            _entries.Enqueue(entry);
            return entry;
        }
    }

    public IReadOnlyList<StartupPerformanceEntry> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public bool HasStage(string stage, int? windowIndex = null)
    {
        lock (_gate)
        {
            return _entries.Any(entry =>
                entry.Stage.Equals(stage, StringComparison.Ordinal)
                && (!windowIndex.HasValue || entry.WindowIndex == windowIndex));
        }
    }

    public long? GetDurationMilliseconds(
        string startStage,
        string endStage,
        int? windowIndex = null)
    {
        lock (_gate)
        {
            var start = _entries.FirstOrDefault(entry =>
                entry.Stage.Equals(startStage, StringComparison.Ordinal)
                && (!windowIndex.HasValue || entry.WindowIndex == windowIndex));
            var end = _entries.FirstOrDefault(entry =>
                entry.Sequence >= (start?.Sequence ?? long.MaxValue)
                && entry.Stage.Equals(endStage, StringComparison.Ordinal)
                && (!windowIndex.HasValue || entry.WindowIndex == windowIndex));
            return start is null || end is null
                ? null
                : Math.Max(0, end.ElapsedMilliseconds - start.ElapsedMilliseconds);
        }
    }
}

public static class StartupPerformanceReportFormatter
{
    public static string Format(IReadOnlyList<StartupPerformanceEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "起動・復元計測はまだありません";
        }

        var lines = new List<string>
        {
            "TabBrowser 起動・復元パフォーマンス",
            $"イベント数: {entries.Count}"
        };
        foreach (var entry in entries.OrderBy(static entry => entry.Sequence))
        {
            var window = entry.WindowIndex.HasValue ? $" Window {entry.WindowIndex.Value}" : string.Empty;
            var tab = entry.TabId.HasValue ? $" Tab {entry.TabId.Value:N}" : string.Empty;
            var count = entry.ItemCount.HasValue ? $" 件数 {entry.ItemCount.Value}" : string.Empty;
            lines.Add($"+{entry.ElapsedMilliseconds,6} ms | {StartupPerformanceStage.GetDisplayName(entry.Stage)}{window}{tab}{count}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
