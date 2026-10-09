using System.Windows.Threading;

namespace TabBrowser.Services;

internal enum DispatcherTimerOwner
{
    MainWindow,
    CommandPalette,
    PageTools,
    FloatingMediaHost,
    MiniPlayerRuntime
}

internal enum DispatcherTimerTerminationPhase
{
    OrderedWindowShutdown,
    WindowShutdown,
    WindowClosed,
    DisposePageTools,
    StopFloatingMediaTimers,
    RuntimeDispose
}

internal sealed class OwnedDispatcherTimer : DispatcherTimer
{
    public OwnedDispatcherTimer(
        DispatcherTimerOwner owner,
        DispatcherTimerTerminationPhase terminationPhase,
        int? shutdownOrder = null)
    {
        if (terminationPhase == DispatcherTimerTerminationPhase.OrderedWindowShutdown)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(shutdownOrder ?? -1);
        }
        else if (shutdownOrder is not null)
        {
            throw new ArgumentException(
                "順序付きWindow終了タイマー以外に終了順は指定できません。",
                nameof(shutdownOrder));
        }

        Owner = owner;
        TerminationPhase = terminationPhase;
        ShutdownOrder = shutdownOrder;
    }

    public DispatcherTimerOwner Owner { get; }

    public DispatcherTimerTerminationPhase TerminationPhase { get; }

    public int? ShutdownOrder { get; }
}
