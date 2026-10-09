namespace TabBrowser.Services;

/// <summary>Window単位で性能診断を蓄積し、既存Timerまたは終了処理でまとめて出力する。</summary>
internal sealed class PerformanceDiagnosticLogController(Action<IReadOnlyList<string>> appendLines) : IDisposable
{
    internal const int FlushIntervalMilliseconds = 30000;
    private readonly object _gate = new();
    private readonly List<string> _pendingLines = [];
    private OwnedDispatcherTimer? _timer;
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

        if (_timer is { IsEnabled: false } timer)
        {
            timer.Start();
        }
    }

    public void Flush()
    {
        _timer?.Stop();
        List<string> lines;
        lock (_gate)
        {
            if (_pendingLines.Count == 0)
            {
                return;
            }

            lines = _pendingLines.ToList();
            _pendingLines.Clear();
        }

        appendLines(lines);
    }

    private void OnFlushTimerTick(object? sender, EventArgs e) => Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnFlushTimerTick;
            _timer = null;
        }

        lock (_gate)
        {
            _pendingLines.Clear();
        }
    }
}
