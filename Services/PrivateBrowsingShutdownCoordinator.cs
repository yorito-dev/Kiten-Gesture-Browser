using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed class PrivateBrowsingShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private Task<PrivateBrowsingDeleteResult>? _shutdownTask;
    private Task<PrivateBrowsingDeleteResult>? _timedOutDeletionTask;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    internal PrivateBrowsingShutdownCoordinator(
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _delayAsync = delayAsync ?? Task.Delay;
    }

    internal Task<PrivateBrowsingDeleteResult>? TimedOutDeletionTask
    {
        get
        {
            lock (_gate)
            {
                return _timedOutDeletionTask;
            }
        }
    }

    internal bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _shutdownStarted;
            }
        }
    }

    internal bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _shutdownCompleted;
            }
        }
    }

    internal bool TryBeginShutdown()
    {
        lock (_gate)
        {
            if (_shutdownStarted)
            {
                return false;
            }

            _shutdownStarted = true;
            return true;
        }
    }

    internal Task<PrivateBrowsingDeleteResult> ShutdownAsync(
        Action releaseWebViewResources,
        Func<CancellationToken, Task<PrivateBrowsingDeleteResult>> deleteContextAsync,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(releaseWebViewResources);
        ArgumentNullException.ThrowIfNull(deleteContextAsync);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_gate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _shutdownStarted = true;
            _shutdownTask = RunShutdownAsync(
                releaseWebViewResources,
                deleteContextAsync,
                timeout);
            return _shutdownTask;
        }
    }

    internal void MarkCompleted()
    {
        lock (_gate)
        {
            _shutdownStarted = true;
            _shutdownCompleted = true;
        }
    }

    private async Task<PrivateBrowsingDeleteResult> RunShutdownAsync(
        Action releaseWebViewResources,
        Func<CancellationToken, Task<PrivateBrowsingDeleteResult>> deleteContextAsync,
        TimeSpan timeout)
    {
        await Task.Yield();
        try
        {
            try
            {
                releaseWebViewResources();
            }
            catch (Exception ex)
            {
                return new PrivateBrowsingDeleteResult(
                    PrivateBrowsingDeleteStatus.Failed,
                    0,
                    $"resource-release-failed:{ex.GetType().Name}");
            }

            try
            {
                return await RunDeleteWithTimeoutAsync(deleteContextAsync, timeout)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                return new PrivateBrowsingDeleteResult(
                    PrivateBrowsingDeleteStatus.Failed,
                    0,
                    $"shutdown-failed:{ex.GetType().Name}");
            }
        }
        finally
        {
            MarkCompleted();
        }
    }

    private async Task<PrivateBrowsingDeleteResult> RunDeleteWithTimeoutAsync(
        Func<CancellationToken, Task<PrivateBrowsingDeleteResult>> deleteContextAsync,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource();
        Task<PrivateBrowsingDeleteResult> deletionTask;
        try
        {
            deletionTask = RunDeleteSafelyAsync(deleteContextAsync, cancellation.Token);
        }
        catch (Exception ex)
        {
            return new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                $"delete-start-failed:{ex.GetType().Name}");
        }

        var timeoutTask = _delayAsync(timeout, CancellationToken.None);
        if (await Task.WhenAny(deletionTask, timeoutTask).ConfigureAwait(true) == deletionTask)
        {
            return await deletionTask.ConfigureAwait(true);
        }

        cancellation.Cancel();
        lock (_gate)
        {
            _timedOutDeletionTask = deletionTask;
        }

        return new PrivateBrowsingDeleteResult(
            PrivateBrowsingDeleteStatus.Failed,
            0,
            "delete-timeout");
    }

    private static async Task<PrivateBrowsingDeleteResult> RunDeleteSafelyAsync(
        Func<CancellationToken, Task<PrivateBrowsingDeleteResult>> deleteContextAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await deleteContextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                "cancelled");
        }
        catch (Exception ex)
        {
            return new PrivateBrowsingDeleteResult(
                PrivateBrowsingDeleteStatus.Failed,
                0,
                $"delete-failed:{ex.GetType().Name}");
        }
    }
}
