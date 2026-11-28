namespace TabBrowser.Services;

internal enum MiniPlayerSuspendScheduleEvent
{
    Requested,
    Replaced,
    Merged,
    Suspended
}

/// <summary>Window単位で性能診断を蓄積し、既存Timerまたは終了処理でまとめて出力する。</summary>
internal sealed class PerformanceDiagnosticLogController(
    Action<IReadOnlyList<string>> appendLines,
    Action<IReadOnlyList<string>>? appendAudioPanLines = null) : IDisposable
{
    internal const int FlushIntervalMilliseconds = 30000;
    private readonly object _gate = new();
    private readonly List<string> _pendingLines = [];
    private readonly List<string> _pendingAudioPanLines = [];
    private readonly WebViewSuspendDiagnostics _webViewSuspend = new();
    private readonly int[] _keyboardHookProbeDelays = new int[5];
    private readonly Dictionary<bool, int[]> _miniPlayerSuspendScheduleCounts = [];
    private readonly Dictionary<int, (int Requests, int Available, int Changed)> _splitPanelPlacements = [];
    private const int MaxSplitPanelDiagnosticGenerations = 32;
    private int _keyboardHookProbeCount;
    private OwnedDispatcherTimer? _timer;
    private bool _isShuttingDown;
    private bool _disposed;

    public void AttachTimer(OwnedDispatcherTimer timer)
    {
        _timer = timer;
        _timer.Tick += OnFlushTimerTick;
    }

    public void Queue(IReadOnlyList<string> fields)
    {
        if (_disposed)
        {
            return;
        }

        var line = DiagnosticLogWriter.BuildLine(fields);
        lock (_gate)
        {
            _pendingLines.Add(line);
        }

        StartTimerIfNeeded();
    }

    public void QueueAudioPanLine(string line)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _pendingAudioPanLines.Add(line);
        }

        StartTimerIfNeeded();
    }

    public void RecordWebViewSuspend(
        WebViewSuspendRoute route,
        WebViewSuspendVisibility visibility,
        bool? apiResult = null,
        string? exceptionType = null)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _webViewSuspend.Record(route, visibility, apiResult, exceptionType);
        }

        StartTimerIfNeeded();
    }

    public void RecordClosedTabSuspendOutcome(ClosedTabSuspendOutcome outcome)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _webViewSuspend.RecordClosedTabOutcome(outcome);
        }

        StartTimerIfNeeded();
    }

    public void RecordKeyboardHookProbeDelay(uint milliseconds)
    {
        if (_disposed || milliseconds > int.MaxValue)
        {
            return;
        }

        var bucket = milliseconds switch { < 50 => 0, < 100 => 1, < 250 => 2, < 500 => 3, _ => 4 };
        lock (_gate)
        {
            _keyboardHookProbeCount++;
            _keyboardHookProbeDelays[bucket]++;
        }

        StartTimerIfNeeded();
    }

    public void RecordMiniPlayerSuspendSchedule(bool stopStalledLoad, MiniPlayerSuspendScheduleEvent scheduleEvent)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (!_miniPlayerSuspendScheduleCounts.TryGetValue(stopStalledLoad, out var counts))
            {
                _miniPlayerSuspendScheduleCounts.Add(stopStalledLoad, counts = new int[4]);
            }
            counts[(int)scheduleEvent]++;
        }

        StartTimerIfNeeded();
    }

    public void RecordSplitPanelPlacement(int generation, bool? available, bool changed)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (!_splitPanelPlacements.TryGetValue(generation, out var counts)
                && _splitPanelPlacements.Count >= MaxSplitPanelDiagnosticGenerations)
            {
                // 同じ出力間隔内に世代が集中した場合も、計測領域を増やし続けない。
                generation = 0;
                _splitPanelPlacements.TryGetValue(generation, out counts);
            }

            if (!available.HasValue) counts.Requests++;
            if (available == true) counts.Available++;
            if (changed) counts.Changed++;
            _splitPanelPlacements[generation] = counts;
        }

        StartTimerIfNeeded();
    }

    private void StartTimerIfNeeded()
    {
        if (!_isShuttingDown && _timer is { IsEnabled: false } timer)
        {
            timer.Start();
        }
    }

    public void FlushForShutdown()
    {
        _isShuttingDown = true;
        Flush();
    }

    public void Flush()
    {
        _timer?.Stop();
        List<string> lines;
        IReadOnlyList<string> audioPanLines;
        lock (_gate)
        {
            if (_pendingLines.Count == 0 && _pendingAudioPanLines.Count == 0
                && !_webViewSuspend.HasPending && _keyboardHookProbeCount == 0
                && _miniPlayerSuspendScheduleCounts.Count == 0 && _splitPanelPlacements.Count == 0)
            {
                return;
            }

            lines = _pendingLines.ToList();
            _pendingLines.Clear();
            audioPanLines = _pendingAudioPanLines.Count == 0 ? [] : _pendingAudioPanLines.ToList();
            _pendingAudioPanLines.Clear();
            _webViewSuspend.DrainInto(lines);
            foreach (var (stopStalledLoad, counts) in _miniPlayerSuspendScheduleCounts)
            {
                lines.Add(DiagnosticLogWriter.BuildLine(
                [
                    "runner=youtube-inactive",
                    "operation=mini-player-suspend-schedule",
                    stopStalledLoad ? "reason=stalled-load" : "reason=paused",
                    $"requested={counts[(int)MiniPlayerSuspendScheduleEvent.Requested]}",
                    $"replaced={counts[(int)MiniPlayerSuspendScheduleEvent.Replaced]}",
                    $"merged={counts[(int)MiniPlayerSuspendScheduleEvent.Merged]}",
                    $"suspended={counts[(int)MiniPlayerSuspendScheduleEvent.Suspended]}"
                ]));
            }
            _miniPlayerSuspendScheduleCounts.Clear();
            foreach (var (generation, counts) in _splitPanelPlacements)
            {
                lines.Add(DiagnosticLogWriter.BuildLine(
                [
                    "runner=youtube-control",
                    "operation=split-panel-placement-counts",
                    $"generation={generation}",
                    $"requests={counts.Requests}",
                    $"available={counts.Available}",
                    $"changed={counts.Changed}"
                ]));
            }
            _splitPanelPlacements.Clear();
            if (_keyboardHookProbeCount > 0)
            {
                lines.Add(DiagnosticLogWriter.BuildLine(
                [
                    "runner=perf",
                    "operation=keyboard-hook-probe",
                    $"received={_keyboardHookProbeCount}",
                    $"lt50ms={_keyboardHookProbeDelays[0]}",
                    $"from50to99ms={_keyboardHookProbeDelays[1]}",
                    $"from100to249ms={_keyboardHookProbeDelays[2]}",
                    $"from250to499ms={_keyboardHookProbeDelays[3]}",
                    $"ge500ms={_keyboardHookProbeDelays[4]}"
                ]));
                _keyboardHookProbeCount = 0;
                Array.Clear(_keyboardHookProbeDelays);
            }
        }

        if (lines.Count > 0)
        {
            appendLines(lines);
        }

        if (audioPanLines.Count > 0)
        {
            appendAudioPanLines?.Invoke(audioPanLines);
        }
    }

    private void OnFlushTimerTick(object? sender, EventArgs e) => Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isShuttingDown)
        {
            // drain中に完了した休止も、既存のClosedで書き出す。
            Flush();
        }

        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnFlushTimerTick;
            _timer = null;
        }

        lock (_gate)
        {
            _pendingLines.Clear();
            _pendingAudioPanLines.Clear();
            _webViewSuspend.Clear();
            _splitPanelPlacements.Clear();
            _miniPlayerSuspendScheduleCounts.Clear();
            _keyboardHookProbeCount = 0;
            Array.Clear(_keyboardHookProbeDelays);
        }
    }
}
