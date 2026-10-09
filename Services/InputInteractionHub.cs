namespace TabBrowser.Services;

public enum InputInteractionDiagnosticEvent
{
    Started,
    Continued,
    Replaced,
    Rejected,
    Completed,
    CancelledForTab,
    CancelledForWindow,
    Expired
}

public sealed record InputInteractionDiagnosticEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    InputInteractionDiagnosticEvent Event,
    InputInteractionKind Kind,
    Guid WindowId,
    Guid? TabId,
    long? InteractionId,
    long? RelatedInteractionId);

public sealed class InputInteractionHub
{
    public const int DiagnosticHistoryCapacity = 64;

    private readonly object _gate = new();
    private readonly InputInteractionCoordinator _coordinator = new();
    private readonly Dictionary<Guid, Action<InputInteractionSession>> _windowCancellationCallbacks = [];
    private readonly Queue<InputInteractionDiagnosticEntry> _diagnosticHistory = new();
    private long _nextDiagnosticSequence;

    public void RegisterWindow(Guid windowId, Action<InputInteractionSession> cancellationCallback)
    {
        ArgumentNullException.ThrowIfNull(cancellationCallback);
        lock (_gate)
        {
            _windowCancellationCallbacks[windowId] = cancellationCallback;
        }
    }

    public void UnregisterWindow(Guid windowId)
    {
        CancelForWindow(windowId);
        lock (_gate)
        {
            _windowCancellationCallbacks.Remove(windowId);
        }
    }

    public InputInteractionSession? GetCurrent(DateTimeOffset now)
    {
        lock (_gate)
        {
            RecordExpired(_coordinator.Expire(now), now);
            return _coordinator.Current;
        }
    }

    public bool IsCurrent(long interactionId)
    {
        lock (_gate)
        {
            return _coordinator.IsCurrent(interactionId);
        }
    }

    public bool IsCurrent(InputInteractionKind kind, Guid windowId)
    {
        lock (_gate)
        {
            return _coordinator.IsCurrent(kind, windowId);
        }
    }

    public bool IsActive(InputInteractionKind kind, Guid windowId, DateTimeOffset now)
    {
        lock (_gate)
        {
            RecordExpired(_coordinator.Expire(now), now);
            return _coordinator.Current is { } current
                && current.Kind == kind
                && current.WindowId == windowId;
        }
    }

    public IReadOnlyList<InputInteractionDiagnosticEntry> GetDiagnosticHistory()
    {
        lock (_gate)
        {
            return _diagnosticHistory.ToArray();
        }
    }

    public void ClearDiagnosticHistory()
    {
        lock (_gate)
        {
            _diagnosticHistory.Clear();
        }
    }

    public InputInteractionTransition TryBegin(
        InputInteractionKind kind,
        Guid windowId,
        Guid? tabId,
        DateTimeOffset now,
        TimeSpan? duration = null)
    {
        InputInteractionTransition transition;
        Action<InputInteractionSession>? cancellationCallback = null;
        lock (_gate)
        {
            RecordExpired(_coordinator.Expire(now), now);
            transition = _coordinator.TryBegin(kind, windowId, tabId, now, duration);
            if (transition.Replaced is { } replaced)
            {
                _windowCancellationCallbacks.TryGetValue(replaced.WindowId, out cancellationCallback);
            }

            RecordTransition(transition, kind, windowId, tabId, now);
        }

        if (transition.Replaced is { } replacedInteraction)
        {
            cancellationCallback?.Invoke(replacedInteraction);
        }

        return transition;
    }

    public bool TryComplete(long interactionId)
    {
        lock (_gate)
        {
            var current = _coordinator.Current;
            var completed = _coordinator.TryComplete(interactionId);
            if (completed && current is not null)
            {
                AddDiagnostic(
                    DateTimeOffset.Now,
                    InputInteractionDiagnosticEvent.Completed,
                    current,
                    relatedInteractionId: null);
            }

            return completed;
        }
    }

    public bool TryComplete(InputInteractionKind kind, Guid windowId, Guid? tabId = null)
    {
        lock (_gate)
        {
            var current = _coordinator.Current;
            var completed = _coordinator.TryComplete(kind, windowId, tabId);
            if (completed && current is not null)
            {
                AddDiagnostic(
                    DateTimeOffset.Now,
                    InputInteractionDiagnosticEvent.Completed,
                    current,
                    relatedInteractionId: null);
            }

            return completed;
        }
    }

    public InputInteractionSession? CancelForTab(Guid windowId, Guid tabId)
    {
        Action<InputInteractionSession>? cancellationCallback;
        InputInteractionSession? cancelled;
        lock (_gate)
        {
            cancelled = _coordinator.CancelForTab(windowId, tabId);
            _windowCancellationCallbacks.TryGetValue(windowId, out cancellationCallback);
            if (cancelled is not null)
            {
                AddDiagnostic(
                    DateTimeOffset.Now,
                    InputInteractionDiagnosticEvent.CancelledForTab,
                    cancelled,
                    relatedInteractionId: null);
            }
        }

        if (cancelled is { } cancelledInteraction)
        {
            cancellationCallback?.Invoke(cancelledInteraction);
        }

        return cancelled;
    }

    public InputInteractionSession? CancelForWindow(Guid windowId)
    {
        Action<InputInteractionSession>? cancellationCallback;
        InputInteractionSession? cancelled;
        lock (_gate)
        {
            cancelled = _coordinator.CancelForWindow(windowId);
            _windowCancellationCallbacks.TryGetValue(windowId, out cancellationCallback);
            if (cancelled is not null)
            {
                AddDiagnostic(
                    DateTimeOffset.Now,
                    InputInteractionDiagnosticEvent.CancelledForWindow,
                    cancelled,
                    relatedInteractionId: null);
            }
        }

        if (cancelled is { } cancelledInteraction)
        {
            cancellationCallback?.Invoke(cancelledInteraction);
        }

        return cancelled;
    }

    private void RecordTransition(
        InputInteractionTransition transition,
        InputInteractionKind requestedKind,
        Guid requestedWindowId,
        Guid? requestedTabId,
        DateTimeOffset now)
    {
        if (!transition.Accepted)
        {
            AddDiagnostic(new InputInteractionDiagnosticEntry(
                ++_nextDiagnosticSequence,
                now,
                InputInteractionDiagnosticEvent.Rejected,
                requestedKind,
                requestedWindowId,
                requestedTabId,
                InteractionId: null,
                RelatedInteractionId: transition.Current?.Id));
            return;
        }

        if (transition.Current is not { } current)
        {
            return;
        }

        AddDiagnostic(
            now,
            transition.StartedNew
                ? transition.Replaced is null
                    ? InputInteractionDiagnosticEvent.Started
                    : InputInteractionDiagnosticEvent.Replaced
                : InputInteractionDiagnosticEvent.Continued,
            current,
            transition.Replaced?.Id);
    }

    private void RecordExpired(InputInteractionSession? expired, DateTimeOffset now)
    {
        if (expired is null)
        {
            return;
        }

        AddDiagnostic(
            now,
            InputInteractionDiagnosticEvent.Expired,
            expired,
            relatedInteractionId: null);
    }

    private void AddDiagnostic(
        DateTimeOffset occurredAt,
        InputInteractionDiagnosticEvent diagnosticEvent,
        InputInteractionSession interaction,
        long? relatedInteractionId)
    {
        AddDiagnostic(new InputInteractionDiagnosticEntry(
            ++_nextDiagnosticSequence,
            occurredAt,
            diagnosticEvent,
            interaction.Kind,
            interaction.WindowId,
            interaction.TabId,
            interaction.Id,
            relatedInteractionId));
    }

    private void AddDiagnostic(InputInteractionDiagnosticEntry entry)
    {
        while (_diagnosticHistory.Count >= DiagnosticHistoryCapacity)
        {
            _diagnosticHistory.Dequeue();
        }

        _diagnosticHistory.Enqueue(entry);
    }
}
