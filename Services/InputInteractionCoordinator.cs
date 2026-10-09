namespace TabBrowser.Services;

public enum InputInteractionKind
{
    PageContextMenu,
    PageGesture,
    WorkspaceGesture,
    MiniPlayerGesture,
    MiniPlayerHoldSeek,
    MiniPlayerContentTransform,
    RightWheelTabSwitch
}

public sealed record InputInteractionSession(
    long Id,
    InputInteractionKind Kind,
    Guid WindowId,
    Guid? TabId,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExpiresAt);

public readonly record struct InputInteractionTransition(
    bool Accepted,
    bool StartedNew,
    InputInteractionSession? Current,
    InputInteractionSession? Replaced);

public sealed class InputInteractionCoordinator
{
    private long _nextInteractionId;
    private InputInteractionSession? _current;

    internal InputInteractionSession? Current => _current;

    public InputInteractionSession? GetCurrent(DateTimeOffset now)
    {
        ExpireCurrent(now);
        return _current;
    }

    internal InputInteractionSession? Expire(DateTimeOffset now)
    {
        return ExpireCurrent(now);
    }

    public bool IsCurrent(long interactionId)
    {
        return _current?.Id == interactionId;
    }

    public bool IsCurrent(InputInteractionKind kind, Guid windowId)
    {
        return _current is { } current
            && current.Kind == kind
            && current.WindowId == windowId;
    }

    public bool IsActive(InputInteractionKind kind, Guid windowId, DateTimeOffset now)
    {
        ExpireCurrent(now);
        return _current is { } current
            && current.Kind == kind
            && current.WindowId == windowId;
    }

    public InputInteractionTransition TryBegin(
        InputInteractionKind kind,
        Guid windowId,
        Guid? tabId,
        DateTimeOffset now,
        TimeSpan? duration = null)
    {
        ExpireCurrent(now);
        DateTimeOffset? expiresAt = duration is { } value ? now.Add(value) : null;
        if (_current is { } current
            && current.Kind == kind
            && current.WindowId == windowId
            && current.TabId == tabId)
        {
            _current = current with { ExpiresAt = expiresAt ?? current.ExpiresAt };
            return new InputInteractionTransition(true, false, _current, null);
        }

        if (_current is { } blocking && GetPriority(blocking.Kind) > GetPriority(kind))
        {
            return new InputInteractionTransition(false, false, blocking, null);
        }

        var replaced = _current;
        _current = new InputInteractionSession(
            ++_nextInteractionId,
            kind,
            windowId,
            tabId,
            now,
            expiresAt);
        return new InputInteractionTransition(true, true, _current, replaced);
    }

    public bool TryComplete(long interactionId)
    {
        if (_current?.Id != interactionId)
        {
            return false;
        }

        _current = null;
        return true;
    }

    public bool TryComplete(InputInteractionKind kind, Guid windowId, Guid? tabId = null)
    {
        if (_current is not { } current
            || current.Kind != kind
            || current.WindowId != windowId
            || tabId is not null && current.TabId != tabId)
        {
            return false;
        }

        _current = null;
        return true;
    }

    public InputInteractionSession? CancelForTab(Guid windowId, Guid tabId)
    {
        if (_current is not { } current
            || current.WindowId != windowId
            || current.TabId != tabId)
        {
            return null;
        }

        _current = null;
        return current;
    }

    public InputInteractionSession? CancelForWindow(Guid windowId)
    {
        if (_current is not { } current || current.WindowId != windowId)
        {
            return null;
        }

        _current = null;
        return current;
    }

    private InputInteractionSession? ExpireCurrent(DateTimeOffset now)
    {
        if (_current?.ExpiresAt is { } expiresAt && now > expiresAt)
        {
            var expired = _current;
            _current = null;
            return expired;
        }

        return null;
    }

    private static int GetPriority(InputInteractionKind kind)
    {
        return kind switch
        {
            InputInteractionKind.MiniPlayerContentTransform => 4,
            InputInteractionKind.RightWheelTabSwitch => 3,
            InputInteractionKind.PageGesture
                or InputInteractionKind.WorkspaceGesture
                or InputInteractionKind.MiniPlayerGesture
                or InputInteractionKind.MiniPlayerHoldSeek => 2,
            InputInteractionKind.PageContextMenu => 1,
            _ => 0
        };
    }
}
