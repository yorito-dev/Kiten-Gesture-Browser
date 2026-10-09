using System.Windows;
using System.Windows.Threading;

namespace TabBrowser.Services;

internal static class SessionPersistenceDispatcherBridge
{
    private static readonly LatestOperationSequencer s_operationSequencer = new();
    private static DispatcherTimer? s_timer;
    private static Action? s_callback;

    public static void Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        InvokeOrPost(() =>
        {
            var timer = GetOrCreateTimer();
            timer.Stop();
            s_callback = callback;
            timer.Interval = delay;
            timer.Start();
        });
    }

    public static void Cancel()
    {
        InvokeOrPost(() =>
        {
            s_timer?.Stop();
            s_callback = null;
        });
    }

    public static Task<T> InvokeAsync<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var dispatcher = GetDispatcher();
        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(callback());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(() =>
        {
            try
            {
                completion.TrySetResult(callback());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private static DispatcherTimer GetOrCreateTimer()
    {
        if (s_timer is not null)
        {
            return s_timer;
        }

        s_timer = new DispatcherTimer(DispatcherPriority.Background, GetDispatcher());
        s_timer.Tick += (_, _) =>
        {
            s_timer.Stop();
            var callback = s_callback;
            s_callback = null;
            callback?.Invoke();
        };
        return s_timer;
    }

    private static void InvokeOrPost(Action callback)
    {
        var dispatcher = GetDispatcher();
        var applyIfLatest = s_operationSequencer.Create(callback);
        if (dispatcher.CheckAccess())
        {
            applyIfLatest();
            return;
        }

        _ = dispatcher.BeginInvoke(applyIfLatest);
    }

    private static Dispatcher GetDispatcher()
    {
        return Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }
}

internal sealed class LatestOperationSequencer
{
    private long _version;

    public Action Create(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var version = Interlocked.Increment(ref _version);
        return () =>
        {
            if (Volatile.Read(ref _version) == version)
            {
                callback();
            }
        };
    }
}
