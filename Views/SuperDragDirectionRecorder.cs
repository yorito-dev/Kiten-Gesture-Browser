using System.Windows;

namespace TabBrowser.Views;

// 設定画面の左ドラッグを、GestureHost.jsのsuperDragDirectionと同じ条件で記録する。
// 保存や操作の実行は行わず、有効な方向を確定したときだけ既存の編集行を選択する。
internal sealed class SuperDragDirectionRecorder
{
    internal const double StartDistance = 30;
    internal const double TurnDistance = 40;
    internal const double DominanceRatio = 1.25;
    private readonly List<string> _directions = [];
    private Point _origin;
    private Point _pivot;

    internal string Route { get; private set; } = "";
    internal bool IsInvalid { get; private set; }
    internal string Description => string.Concat(_directions.Select(direction => direction switch
    { "up" => "↑", "right" => "→", "down" => "↓", _ => "←" })) + "（"
        + string.Join(" → ", _directions.Select(direction => direction switch
        { "up" => "上", "right" => "右", "down" => "下", _ => "左" })) + "）";

    internal void Reset()
    {
        _directions.Clear();
        Route = "";
        IsInvalid = false;
    }

    internal void Begin(Point origin)
    {
        Reset();
        _origin = _pivot = origin;
    }

    internal void Move(Point point)
    {
        if (IsInvalid) return;
        if (_directions.Count == 0)
        {
            if (ReadDirection(point - _origin, StartDistance) is not { } first) return;
            AppendDirection(first);
            _pivot = point;
        }
        else
        {
            var last = _directions[^1];
            if (ReadDirection(point - _pivot, StartDistance) == last) _pivot = point;
            if (ReadDirection(point - _pivot, TurnDistance) is { } next && next != last)
            {
                AppendDirection(next);
                _pivot = point;
            }
        }
        if (!IsInvalid)
            Route = _directions.Count == 1 ? _directions[0] : _directions[0] + char.ToUpperInvariant(_directions[1][0]) + _directions[1][1..];
        if (_directions.Count == 1 && (point - _origin).Length < StartDistance) Route = "";
    }

    internal void AppendDirection(string direction)
    {
        if (IsInvalid || direction is not ("up" or "right" or "down" or "left")) return;
        if (_directions.Count > 0 && _directions[^1] == direction)
        {
            Route = _directions.Count == 1 ? direction : Route;
            return;
        }
        if (_directions.Count > 1 || (_directions.Count == 1 && Horizontal(_directions[0]) == Horizontal(direction)))
        {
            IsInvalid = true;
            Route = "";
            return;
        }
        _directions.Add(direction);
        Route = _directions.Count == 1 ? direction : _directions[0] + char.ToUpperInvariant(direction[0]) + direction[1..];
    }

    private static bool Horizontal(string direction) => direction is "right" or "left";

    private static string? ReadDirection(Vector delta, double minimum)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y) || delta.Length < minimum) return null;
        if (Math.Abs(delta.X) >= Math.Abs(delta.Y) * DominanceRatio) return delta.X > 0 ? "right" : "left";
        if (Math.Abs(delta.Y) >= Math.Abs(delta.X) * DominanceRatio) return delta.Y > 0 ? "down" : "up";
        return null;
    }
}
