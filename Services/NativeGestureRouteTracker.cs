using System.Windows;

namespace TabBrowser.Services;

public readonly record struct NativeGestureCompletion(bool WasActive, string Route);

public sealed class NativeGestureRouteTracker
{
    private const double DirectionDominanceRatio = 1.25;
    private const double ReverseDirectionThresholdMultiplier = 1.5;

    private double _startThreshold;
    private double _directionThreshold;
    private Point _startPoint;
    private Point _lastDirectionPoint;

    public bool IsTracking { get; private set; }

    public bool IsActive { get; private set; }

    public double DistanceFromStart { get; private set; }

    public string Route { get; private set; } = string.Empty;

    public void Begin(Point startPoint, double startThreshold, double directionThreshold)
    {
        _startThreshold = Math.Max(0, startThreshold);
        _directionThreshold = Math.Max(0, directionThreshold);
        _startPoint = startPoint;
        _lastDirectionPoint = startPoint;
        Route = string.Empty;
        IsActive = false;
        DistanceFromStart = 0;
        IsTracking = true;
    }

    public void Update(Point point)
    {
        if (!IsTracking)
        {
            return;
        }

        DistanceFromStart = GetDistance(_startPoint, point);
        if (!IsActive && DistanceFromStart >= _startThreshold)
        {
            IsActive = true;
        }

        if (!IsActive)
        {
            return;
        }

        AppendDirection(point);
    }

    public NativeGestureCompletion Complete()
    {
        var completion = new NativeGestureCompletion(IsActive, Route);
        Reset();
        return completion;
    }

    public void Reset()
    {
        IsTracking = false;
        IsActive = false;
        DistanceFromStart = 0;
        Route = string.Empty;
        _startPoint = default;
        _lastDirectionPoint = default;
    }

    private void AppendDirection(Point point)
    {
        var deltaX = point.X - _lastDirectionPoint.X;
        var deltaY = point.Y - _lastDirectionPoint.Y;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);
        if (Math.Max(absoluteX, absoluteY) < _directionThreshold)
        {
            return;
        }

        var horizontal = absoluteX >= absoluteY;
        var primaryDistance = horizontal ? absoluteX : absoluteY;
        var secondaryDistance = horizontal ? absoluteY : absoluteX;
        if (primaryDistance < secondaryDistance * DirectionDominanceRatio)
        {
            return;
        }

        var direction = horizontal
            ? deltaX < 0 ? "←" : "→"
            : deltaY < 0 ? "↑" : "↓";
        var previousDirection = Route.Length > 0 ? Route[^1].ToString() : string.Empty;
        if (!string.IsNullOrEmpty(previousDirection)
            && IsOppositeDirection(previousDirection, direction)
            && primaryDistance < _directionThreshold * ReverseDirectionThresholdMultiplier)
        {
            return;
        }

        if (!previousDirection.Equals(direction, StringComparison.Ordinal))
        {
            Route += direction;
        }

        _lastDirectionPoint = point;
    }

    private static bool IsOppositeDirection(string first, string second)
    {
        return first is "←" && second is "→"
            || first is "→" && second is "←"
            || first is "↑" && second is "↓"
            || first is "↓" && second is "↑";
    }

    private static double GetDistance(Point first, Point second)
    {
        return (first - second).Length;
    }
}
