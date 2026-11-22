using System.Windows;
using TabBrowser.Models;
using TabBrowser.Views;

namespace TabBrowser.Services;

internal enum YouTubeMiniPlayerArrangementAreaMode { Display, FreeSpace, SelectedRegion }

internal sealed record YouTubeMiniPlayerArrangementAreaRequest(
    YouTubeMiniPlayerArrangementAreaMode Mode,
    string DisplayKey,
    Rect? SelectedRegion,
    IReadOnlyList<Rect> Obstacles,
    bool IsAvailable = true)
{
    public IReadOnlyList<Rect> OutsideWorkAreas { get; init; } = [];
    public DpiScale? HostDpi { get; init; }
    public IReadOnlyList<Rect> PlacementExclusions => Mode == YouTubeMiniPlayerArrangementAreaMode.FreeSpace ? Obstacles : OutsideWorkAreas;
}

// 配置方式は既存計算へ渡し、配置先だけを選ぶ。画面・Window・保存には触れない。
internal static class YouTubeMiniPlayerArrangementAreaService
{
    private const double Tolerance = 0.01;

    public static Size GetRequiredMinimum(YouTubeMiniPlayerArrangementCandidate candidate)
        => candidate.MinimumSize ?? YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            YouTubeMiniPlayerLayoutService.MinimumWidth, YouTubeMiniPlayerLayoutService.MinimumHeight,
            false, GetAspectRatio(candidate));

    public static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateLayout(YouTubeMiniPlayerArrangeMode mode,
        Rect area, IReadOnlyList<YouTubeMiniPlayerArrangementCandidate> targets, Random? random,
        IReadOnlySet<Guid> focusIds, YouTubeMiniPlayerLinearPlacement placement, bool removeSpacing = false)
    {
        var chatGap = removeSpacing ? 0 : YouTubeLiveChatWindow.DefaultDockGap;
        var layoutMode = removeSpacing && mode == YouTubeMiniPlayerArrangeMode.Chat
            ? YouTubeMiniPlayerArrangeMode.Monitor : mode;
        bool IncludesChat(YouTubeMiniPlayerArrangementCandidate candidate)
            => mode == YouTubeMiniPlayerArrangeMode.Chat ? removeSpacing && candidate.HasChat : candidate.HasDockedChat;
        var items = targets.Select(candidate =>
        {
            var minimum = GetRequiredMinimum(candidate);
            var ratio = GetAspectRatio(candidate);
            if (IncludesChat(candidate))
            {
                var height = Math.Max(minimum.Height, YouTubeLiveChatWindowState.MinimumHeight);
                var width = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(height, ratio)
                    + chatGap + YouTubeLiveChatWindowState.MinimumWidth;
                minimum = new(width, height);
                ratio = width / height;
            }
            return new YouTubeMiniPlayerArrangementItem(candidate.Id, candidate.IsShorts, candidate.Width,
                candidate.Height, ratio, candidate.HasChat, minimum);
        }).ToList();
        var layout = YouTubeMiniPlayerArrangementService.CreateLayout(layoutMode, area, items, random, focusIds, placement, removeSpacing);
        if (!removeSpacing && mode == YouTubeMiniPlayerArrangeMode.Horizontal && !Fits(layout, area, [], false))
        {
            layout = CreateFittingRow(area, items, placement);
        }
        if (!removeSpacing && mode == YouTubeMiniPlayerArrangeMode.Chat) return layout;
        var byId = targets.ToDictionary(candidate => candidate.Id);
        return layout.Select(result =>
        {
            var candidate = byId[result.Id];
            if (!IncludesChat(candidate)) return result;
            var group = result.Bounds;
            var videoWidth = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(group.Height, GetAspectRatio(candidate));
            var chatWidth = group.Width - videoWidth - chatGap;
            var leftChat = mode != YouTubeMiniPlayerArrangeMode.Chat && candidate.ChatDockSide == YouTubeLiveChatDockSide.Left;
            return result with
            {
                Bounds = new(group.Left + (leftChat ? chatWidth + chatGap : 0), group.Top, videoWidth, group.Height),
                ChatBounds = chatWidth > 0
                    ? new Rect(leftChat ? group.Left : group.Left + videoWidth + chatGap, group.Top, chatWidth, group.Height)
                    : Rect.Empty,
                ChatDockSide = leftChat ? YouTubeLiveChatDockSide.Left : YouTubeLiveChatDockSide.Right
            };
        }).ToList();
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateFittingRow(
        Rect area, IReadOnlyList<YouTubeMiniPlayerArrangementItem> items, YouTubeMiniPlayerLinearPlacement placement)
    {
        const double preferredSpacing = 12;
        if (!IsValid(area) || items.Count == 0) return [];
        var minimums = items.Select(item => YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            0, 0, false, item.AspectRatio!.Value, item.MinimumSize)).ToArray();
        var remainingWidth = area.Width - minimums.Sum(size => size.Width);
        var remainingHeight = area.Height - minimums.Max(size => size.Height);
        if (remainingWidth < -Tolerance || remainingHeight < -Tolerance) return [];

        var spacing = Math.Clamp(remainingWidth / (items.Count + 1), 0, preferredSpacing);
        var extraWidth = Math.Max(0, remainingWidth - spacing * (items.Count + 1)) / items.Count;
        var padding = Math.Clamp(remainingHeight / 2, 0, preferredSpacing);
        var heightLimit = area.Height - 2 * padding;
        var alignment = placement switch
        {
            YouTubeMiniPlayerLinearPlacement.First => 0,
            YouTubeMiniPlayerLinearPlacement.Third => 1,
            _ => 0.5
        };
        var sizes = items.Select((item, index) =>
        {
            var ratio = item.AspectRatio!.Value;
            var widthLimit = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(heightLimit, ratio);
            var width = Math.Max(minimums[index].Width, Math.Min(minimums[index].Width + extraWidth, widthLimit));
            return new Size(width, YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(width, ratio));
        }).ToArray();
        var rowWidth = sizes.Sum(size => size.Width) + spacing * (items.Count - 1);
        var left = area.Left + Math.Max(0, area.Width - rowWidth) / 2;
        var layout = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var size = sizes[index];
            var top = area.Top + padding + Math.Max(0, heightLimit - size.Height) * alignment;
            layout.Add(new(items[index].Id, new(left, top, size.Width, size.Height)));
            left += size.Width + spacing;
        }
        return layout;
    }

    private static double GetAspectRatio(YouTubeMiniPlayerArrangementCandidate candidate)
        => candidate.VideoAspectRatio is { } ratio && double.IsFinite(ratio) && ratio > 0 ? ratio
            : YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(candidate.IsShorts);

    public static bool IsValid(Rect area) => !area.IsEmpty && area.Width > 0 && area.Height > 0
        && double.IsFinite(area.Left) && double.IsFinite(area.Top)
        && double.IsFinite(area.Right) && double.IsFinite(area.Bottom);

    public static Rect ToHostCoordinates(Rect physicalBounds, DpiScale dpi)
        => new(physicalBounds.Left / dpi.DpiScaleX, physicalBounds.Top / dpi.DpiScaleY,
            physicalBounds.Width / dpi.DpiScaleX, physicalBounds.Height / dpi.DpiScaleY);

    public static Rect ToPhysicalCoordinates(Rect hostBounds, DpiScale dpi)
        => new(hostBounds.Left * dpi.DpiScaleX, hostBounds.Top * dpi.DpiScaleY,
            hostBounds.Width * dpi.DpiScaleX, hostBounds.Height * dpi.DpiScaleY);

    public static Rect GetDesktopBounds(IEnumerable<Rect> workAreas)
        => workAreas.Where(IsValid).Aggregate(Rect.Empty, Rect.Union);

    public static IReadOnlyList<Rect> GetIntersectingWorkAreas(Rect region, IEnumerable<Rect> workAreas)
        => workAreas.Where(area => IsValid(Rect.Intersect(region, area))).Distinct()
            .OrderBy(area => area.Left).ThenBy(area => area.Top).ThenBy(area => area.Width).ThenBy(area => area.Height).ToArray();

    public static IReadOnlyList<Rect> GetOutsideWorkAreas(Rect region, IReadOnlyList<Rect> workAreas)
    {
        IReadOnlyList<Rect> remaining = IsValid(region) ? [region] : [];
        foreach (var workArea in workAreas)
        {
            remaining = remaining.SelectMany(area => Subtract(area, workArea)).ToArray();
        }
        return remaining;
    }

    private static IEnumerable<Rect> Subtract(Rect area, Rect workArea)
    {
        var intersection = Rect.Intersect(area, workArea);
        if (!IsValid(intersection))
        {
            yield return area;
            yield break;
        }
        if (intersection.Top > area.Top)
            yield return new(area.Left, area.Top, area.Width, intersection.Top - area.Top);
        if (intersection.Bottom < area.Bottom)
            yield return new(area.Left, intersection.Bottom, area.Width, area.Bottom - intersection.Bottom);
        if (intersection.Left > area.Left)
            yield return new(area.Left, intersection.Top, intersection.Left - area.Left, intersection.Height);
        if (intersection.Right < area.Right)
            yield return new(intersection.Right, intersection.Top, area.Right - intersection.Right, intersection.Height);
    }

    public static Rect SelectRegion(Point start, Point end, Rect workArea)
    {
        if (!IsValid(workArea)) return Rect.Empty;
        var a = new Point(Math.Clamp(start.X, workArea.Left, workArea.Right), Math.Clamp(start.Y, workArea.Top, workArea.Bottom));
        var b = new Point(Math.Clamp(end.X, workArea.Left, workArea.Right), Math.Clamp(end.Y, workArea.Top, workArea.Bottom));
        return new Rect(a, b);
    }

    public static IReadOnlyList<Rect> FindEmptyAreas(Rect workArea, IReadOnlyList<Rect> obstacles)
    {
        if (!IsValid(workArea)) return [];
        var clipped = obstacles.Where(IsValid).Select(area => Rect.Intersect(area, workArea))
            .Where(IsValid).Distinct().ToList();
        var edges = clipped.SelectMany(area => new[] { area.Left, area.Right })
            .Append(workArea.Left).Append(workArea.Right).Distinct().Order().ToArray();
        var candidates = new List<Rect>();
        // 障害物の左右端で区切った帯ごとに、上下の空きを列挙する。
        for (var left = 0; left < edges.Length - 1; left++)
        {
            for (var right = left + 1; right < edges.Length; right++)
            {
                var blocked = clipped.Where(area => area.Left < edges[right] && area.Right > edges[left])
                    .OrderBy(area => area.Top);
                var top = workArea.Top;
                foreach (var area in blocked)
                {
                    if (area.Top > top) AddCandidate(new(edges[left], top, edges[right] - edges[left], area.Top - top));
                    top = Math.Max(top, area.Bottom);
                }
                if (top < workArea.Bottom) AddCandidate(new(edges[left], top, edges[right] - edges[left], workArea.Bottom - top));
            }
        }
        return candidates.OrderByDescending(area => area.Width * area.Height).ThenBy(area => area.Top).ThenBy(area => area.Left).ToList();

        void AddCandidate(Rect area)
        {
            if (!IsValid(area) || candidates.Any(candidate => candidate.Contains(area))) return;
            candidates.RemoveAll(candidate => area.Contains(candidate));
            candidates.Add(area);
        }
    }

    public static bool Fits(IReadOnlyList<YouTubeMiniPlayerArrangementResult> layout, Rect area,
        IReadOnlyList<Rect> obstacles, bool allowPlayerOverlap, IReadOnlyDictionary<Guid, Size>? minimumSizes = null)
    {
        if (!IsValid(area) || layout.Count == 0) return false;
        if (minimumSizes is not null && layout.Any(item => minimumSizes.TryGetValue(item.Id, out var minimum)
            && (item.Bounds.Width + Tolerance < minimum.Width || item.Bounds.Height + Tolerance < minimum.Height))) return false;
        if (layout.Any(item => item.ChatBounds is { } chat && (chat.Width + Tolerance < YouTubeLiveChatWindowState.MinimumWidth
            || chat.Height + Tolerance < YouTubeLiveChatWindowState.MinimumHeight))) return false;
        var frames = layout.SelectMany(item => item.ChatBounds is { } chat
            ? new[] { item.Bounds, chat } : new[] { item.Bounds }).ToList();
        var toleratedArea = area;
        toleratedArea.Inflate(Tolerance, Tolerance);
        if (frames.Any(frame => !IsValid(frame) || !toleratedArea.Contains(frame)
            || obstacles.Any(obstacle => Overlaps(frame, obstacle)))) return false;
        if (!allowPlayerOverlap)
        {
            for (var i = 0; i < frames.Count; i++)
                for (var j = i + 1; j < frames.Count; j++)
                    if (Overlaps(frames[i], frames[j])) return false;
        }
        return true;
    }

    public static bool IsSameDestination(YouTubeMiniPlayerArrangementAreaRequest baseline,
        YouTubeMiniPlayerArrangementAreaRequest? current)
        => current is { IsAvailable: true } && baseline.Mode == current.Mode
            && string.Equals(baseline.DisplayKey, current.DisplayKey, StringComparison.OrdinalIgnoreCase)
            && baseline.SelectedRegion == current.SelectedRegion;

    private static bool Overlaps(Rect a, Rect b)
        => IsValid(b) && Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > Tolerance
            && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > Tolerance;
}
