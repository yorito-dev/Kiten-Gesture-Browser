using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TabBrowser.Services;

public sealed record ClipboardWriteResult(bool IsSuccess, int AttemptCount);

public sealed class ClipboardWriteService
{
    internal const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(120)
    ];

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _setText;
    private readonly Func<TimeSpan, Task> _delay;

    public ClipboardWriteService(Dispatcher dispatcher)
        : this(dispatcher, Clipboard.SetText, Task.Delay)
    {
    }

    internal ClipboardWriteService(
        Dispatcher dispatcher,
        Action<string> setText,
        Func<TimeSpan, Task> delay)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _setText = setText ?? throw new ArgumentNullException(nameof(setText));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task<ClipboardWriteResult> SetTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ClipboardWriteResult(false, 0);
        }

        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            var delay = RetryDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                await _delay(delay);
            }

            try
            {
                await SetTextOnDispatcherAsync(text);
                return new ClipboardWriteResult(true, attempt + 1);
            }
            catch (COMException ex) when (
                ex.HResult == ClipboardCannotOpenHResult
                && attempt + 1 < RetryDelays.Length)
            {
            }
            catch (Exception ex) when (IsExpectedClipboardException(ex))
            {
                return new ClipboardWriteResult(false, attempt + 1);
            }
        }

        return new ClipboardWriteResult(false, RetryDelays.Length);
    }

    private Task SetTextOnDispatcherAsync(string text)
    {
        if (_dispatcher.CheckAccess())
        {
            _setText(text);
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(() => _setText(text)).Task;
    }

    private static bool IsExpectedClipboardException(Exception exception)
    {
        return exception is ExternalException or ThreadStateException or ArgumentException;
    }
}
