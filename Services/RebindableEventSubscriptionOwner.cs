using System.Runtime.CompilerServices;

namespace TabBrowser.Services;

internal sealed class RebindableEventSubscriptionOwner<TSubject, TOwner>
    where TSubject : class
    where TOwner : class
{
    private readonly ConditionalWeakTable<TSubject, SubjectState> _states = new();

    public bool Rebind(
        TSubject subject,
        TOwner owner,
        object sourceIdentity,
        int sourceGeneration,
        Func<IDisposable> attach)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(attach);

        var state = _states.GetValue(subject, static _ => new SubjectState());
        Binding? previous;
        lock (state.Sync)
        {
            WaitForPendingOperation(state);
            previous = state.Binding;
            if (previous is not null
                && ReferenceEquals(previous.Owner, owner)
                && ReferenceEquals(previous.SourceIdentity, sourceIdentity)
                && previous.SourceGeneration == sourceGeneration)
            {
                return false;
            }

            state.OperationInProgress = true;
            state.OperationThreadId = Environment.CurrentManagedThreadId;
            state.Binding = null;
        }

        Binding? replacement = null;
        try
        {
            previous?.Subscription.Dispose();
            try
            {
                replacement = new Binding(
                    owner,
                    sourceIdentity,
                    sourceGeneration,
                    attach,
                    attach());
                return true;
            }
            catch (Exception attachException)
            {
                if (previous is null)
                {
                    throw;
                }

                try
                {
                    var restoredSubscription = previous.Attach();
                    replacement = new Binding(
                        previous.Owner,
                        previous.SourceIdentity,
                        previous.SourceGeneration,
                        previous.Attach,
                        restoredSubscription);
                }
                catch (Exception restoreException)
                {
                    throw new InvalidOperationException(
                        "イベント購読の移管と旧所有者への復元に失敗しました。",
                        new AggregateException(attachException, restoreException));
                }

                throw;
            }
        }
        finally
        {
            CompleteOperation(state, replacement);
        }
    }

    public bool Detach(TSubject subject, TOwner owner)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(owner);

        if (!_states.TryGetValue(subject, out var state))
        {
            return false;
        }

        Binding binding;
        lock (state.Sync)
        {
            WaitForPendingOperation(state);
            if (state.Binding is not { } current
                || !ReferenceEquals(current.Owner, owner))
            {
                return false;
            }

            binding = current;
            state.OperationInProgress = true;
            state.OperationThreadId = Environment.CurrentManagedThreadId;
            state.Binding = null;
        }

        try
        {
            binding.Subscription.Dispose();
            return true;
        }
        finally
        {
            CompleteOperation(state, replacement: null);
        }
    }

    public bool TryAugment(
        TSubject subject,
        TOwner owner,
        object sourceIdentity,
        int expectedGeneration,
        int newGeneration,
        Func<IDisposable> attach)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(attach);

        if (!_states.TryGetValue(subject, out var state))
        {
            return false;
        }

        Binding current;
        lock (state.Sync)
        {
            WaitForPendingOperation(state);
            if (state.Binding is not { } binding
                || !ReferenceEquals(binding.Owner, owner)
                || !ReferenceEquals(binding.SourceIdentity, sourceIdentity))
            {
                return false;
            }

            current = binding;
            if (current.SourceGeneration == newGeneration)
            {
                return true;
            }

            if (current.SourceGeneration != expectedGeneration)
            {
                return false;
            }

            state.OperationInProgress = true;
            state.OperationThreadId = Environment.CurrentManagedThreadId;
        }

        Binding replacement = current;
        try
        {
            var addedSubscription = attach();
            Func<IDisposable> combinedAttach = () => AttachCombined(current.Attach, attach);
            var combinedSubscription = new CompositeSubscription(
                current.Subscription,
                addedSubscription);
            replacement = new Binding(
                owner,
                sourceIdentity,
                newGeneration,
                combinedAttach,
                combinedSubscription);
            return true;
        }
        finally
        {
            CompleteOperation(state, replacement);
        }
    }

    public bool IsOwnedBy(TSubject subject, TOwner owner)
    {
        if (!_states.TryGetValue(subject, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            return state.Binding is { } binding
                && ReferenceEquals(binding.Owner, owner);
        }
    }

    public bool TryGetOwner(TSubject subject, out TOwner? owner)
    {
        if (!_states.TryGetValue(subject, out var state))
        {
            owner = null;
            return false;
        }

        lock (state.Sync)
        {
            if (state.Binding is { } binding)
            {
                owner = binding.Owner;
                return true;
            }

            owner = null;
            return false;
        }
    }

    private static IDisposable AttachCombined(
        Func<IDisposable> attachFirst,
        Func<IDisposable> attachSecond)
    {
        var firstSubscription = attachFirst();
        try
        {
            return new CompositeSubscription(firstSubscription, attachSecond());
        }
        catch (Exception attachException)
        {
            try
            {
                firstSubscription.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    "イベント購読の追加と追加失敗後の解除に失敗しました。",
                    new AggregateException(attachException, cleanupException));
            }

            throw;
        }
    }

    private static void WaitForPendingOperation(SubjectState state)
    {
        while (state.OperationInProgress)
        {
            if (state.OperationThreadId == Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("イベント購読の変更中に同じ対象を再変更できません。");
            }

            Monitor.Wait(state.Sync);
        }
    }

    private static void CompleteOperation(SubjectState state, Binding? replacement)
    {
        lock (state.Sync)
        {
            state.Binding = replacement;
            state.OperationInProgress = false;
            state.OperationThreadId = 0;
            Monitor.PulseAll(state.Sync);
        }
    }

    private sealed record Binding(
        TOwner Owner,
        object SourceIdentity,
        int SourceGeneration,
        Func<IDisposable> Attach,
        IDisposable Subscription);

    private sealed class SubjectState
    {
        public object Sync { get; } = new();

        public Binding? Binding { get; set; }

        public bool OperationInProgress { get; set; }

        public int OperationThreadId { get; set; }
    }

    private sealed class CompositeSubscription(
        IDisposable first,
        IDisposable second) : IDisposable
    {
        private IDisposable[]? _subscriptions = [first, second];

        public void Dispose()
        {
            var subscriptions = Interlocked.Exchange(ref _subscriptions, null);
            if (subscriptions is null)
            {
                return;
            }

            List<Exception>? exceptions = null;
            for (var index = subscriptions.Length - 1; index >= 0; index--)
            {
                try
                {
                    subscriptions[index].Dispose();
                }
                catch (Exception ex)
                {
                    exceptions ??= [];
                    exceptions.Add(ex);
                }
            }

            if (exceptions is { Count: 1 })
            {
                throw exceptions[0];
            }

            if (exceptions is { Count: > 1 })
            {
                throw new AggregateException("複数のイベント購読を解除できませんでした。", exceptions);
            }
        }
    }
}
