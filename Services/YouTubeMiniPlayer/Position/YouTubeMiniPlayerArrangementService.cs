using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

public enum YouTubeMiniPlayerArrangeMode
{
    Grid,
    Horizontal,
    Vertical,
    Random,
    Focus,
    Monitor,
    NormalAndShorts,
    Chat
}

public enum YouTubeMiniPlayerLinearPlacement
{
    First = 1,
    Second = 2,
    Third = 3
}

public sealed record YouTubeMiniPlayerArrangementItem(
    Guid Id,
    bool IsShorts,
    double Width,
    double Height,
    double? AspectRatio = null,
    bool HasChat = false,
    Size? MinimumSize = null);

public sealed record YouTubeMiniPlayerArrangementResult(
    Guid Id,
    Rect Bounds,
    Rect? ChatBounds = null)
{
    public YouTubeLiveChatDockSide? ChatDockSide { get; init; }
}

public static class YouTubeMiniPlayerArrangementService
{
    private const double DefaultSpacing = 12;

    public static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateLayout(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random? random = null,
        IReadOnlyCollection<Guid>? focusIds = null,
        YouTubeMiniPlayerLinearPlacement linearPlacement = YouTubeMiniPlayerLinearPlacement.Second,
        bool removeSpacing = false)
    {
        var layout = CreateLayoutCore(mode, workArea, items, random, focusIds, linearPlacement,
            removeSpacing ? 0 : DefaultSpacing);
        if (removeSpacing) return layout;
        var byId = items.ToDictionary(item => item.Id);
        return layout.Select(result =>
        {
            var item = byId[result.Id];
            if (item.MinimumSize is not { } minimum) return result;
            // 整列用セルや作業領域が小さい場合も、YouTubeの表示下限を優先する。
            var size = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
                result.Bounds.Width, result.Bounds.Height, false, GetAspectRatio(item), minimum);
            return result with
            {
                Bounds = new Rect(
                    Math.Clamp(result.Bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
                    Math.Clamp(result.Bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)),
                    size.Width, size.Height)
            };
        }).ToList();
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateLayoutCore(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random? random,
        IReadOnlyCollection<Guid>? focusIds,
        YouTubeMiniPlayerLinearPlacement linearPlacement,
        double spacing)
    {
        if (items.Count == 0 || workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return [];
        }

        var randomGenerator = random ?? System.Random.Shared;
        if (mode is YouTubeMiniPlayerArrangeMode.Horizontal or YouTubeMiniPlayerArrangeMode.Vertical)
        {
            var section = GetLinearPlacementArea(mode, workArea, linearPlacement);
            if (spacing == 0)
            {
                var horizontal = mode == YouTubeMiniPlayerArrangeMode.Horizontal;
                var layout = CreatePackedLine(section, items, horizontal, YouTubeMiniPlayerLinearPlacement.Second);
                return layout.Count > 0 ? layout : CreatePackedLine(workArea, items, horizontal, linearPlacement);
            }
            return CreateGroupLayout(mode, Deflate(section, spacing), items, randomGenerator, spacing);
        }

        var layoutArea = spacing == 0 ? workArea : Deflate(workArea, spacing);
        if (mode == YouTubeMiniPlayerArrangeMode.Focus)
        {
            return CreateFocusLayout(layoutArea, items, focusIds, spacing);
        }

        if (mode == YouTubeMiniPlayerArrangeMode.Monitor || (spacing == 0 && mode == YouTubeMiniPlayerArrangeMode.Grid))
        {
            return CreateGridLayout(layoutArea, items, spacing);
        }

        if (mode == YouTubeMiniPlayerArrangeMode.Chat)
        {
            return CreateChatLayout(layoutArea, items, spacing);
        }

        var normalItems = items.Where(static item => GetAspectRatio(item) >= 1).ToList();
        var shortsItems = items.Where(static item => GetAspectRatio(item) < 1).ToList();
        if (normalItems.Count == 0)
        {
            return CreateGroupLayout(mode, layoutArea, shortsItems, randomGenerator, spacing);
        }

        if (shortsItems.Count == 0)
        {
            return CreateGroupLayout(mode, layoutArea, normalItems, randomGenerator, spacing);
        }

        var normalWeight = normalItems.Count;
        var shortsWeight = shortsItems.Count * 0.65;
        var normalRatio = Math.Clamp(normalWeight / (normalWeight + shortsWeight), 0.45, 0.75);
        var usableWidth = Math.Max(1, layoutArea.Width - spacing);
        var normalWidth = usableWidth * normalRatio;
        var normalArea = new Rect(layoutArea.Left, layoutArea.Top, normalWidth, layoutArea.Height);
        var shortsArea = new Rect(
            normalArea.Right + spacing,
            layoutArea.Top,
            usableWidth - normalWidth,
            layoutArea.Height);

        return CreateGroupLayout(mode, normalArea, normalItems, randomGenerator, spacing)
            .Concat(CreateGroupLayout(mode, shortsArea, shortsItems, randomGenerator, spacing))
            .ToList();
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateFocusLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        IReadOnlyCollection<Guid>? focusIds,
        double spacing)
    {
        var requestedFocusIds = focusIds?.ToHashSet() ?? [];
        var focusItems = requestedFocusIds.Count > 0
            ? items.Where(item => requestedFocusIds.Contains(item.Id)).ToList()
            : [items[0]];
        if (focusItems.Count == 0)
        {
            focusItems.Add(items[0]);
        }

        var resolvedFocusIds = focusItems.Select(static item => item.Id).ToHashSet();
        var secondaryItems = items.Where(item => !resolvedFocusIds.Contains(item.Id)).ToList();
        if (secondaryItems.Count == 0)
        {
            var focusedLayout = CreateGridLayout(area, focusItems, spacing);
            return IsLayoutValid(focusedLayout, area) ? focusedLayout : [];
        }

        var primaryShare = GetFocusPrimaryShare(focusItems.Count, secondaryItems.Count);
        Rect focusArea;
        Rect secondaryArea;
        if (area.Width >= area.Height)
        {
            var usableWidth = Math.Max(1, area.Width - spacing);
            var focusWidth = usableWidth * primaryShare;
            focusArea = new Rect(
                area.Left,
                area.Top,
                focusWidth,
                area.Height);
            secondaryArea = new Rect(
                focusArea.Right + spacing,
                area.Top,
                Math.Max(1, usableWidth - focusWidth),
                area.Height);
        }
        else
        {
            var usableHeight = Math.Max(1, area.Height - spacing);
            var focusHeight = usableHeight * primaryShare;
            focusArea = new Rect(
                area.Left,
                area.Top,
                area.Width,
                focusHeight);
            secondaryArea = new Rect(
                area.Left,
                focusArea.Bottom + spacing,
                area.Width,
                Math.Max(1, usableHeight - focusHeight));
        }

        var focusLayout = focusItems.Count == 1
            ? CreateSingleFocusLayout(area, focusArea, focusItems[0])
            : CreateGridLayout(focusArea, focusItems, spacing);
        var secondaryLayout = CreateGridLayout(secondaryArea, secondaryItems, spacing);
        var layout = focusLayout
            .Concat(secondaryLayout)
            .ToList();
        return IsFocusLayoutValid(layout, resolvedFocusIds, area) ? layout : [];
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateSingleFocusLayout(
        Rect area,
        Rect focusArea,
        YouTubeMiniPlayerArrangementItem focusItem)
    {
        var focusSize = FitSize(
            focusItem,
            focusArea.Width,
            focusArea.Height,
            area.Width,
            area.Height);
        return [new YouTubeMiniPlayerArrangementResult(
            focusItem.Id,
            CenterAndClamp(focusArea, area, focusSize))];
    }

    private static double GetFocusPrimaryShare(int focusCount, int secondaryCount)
    {
        if (focusCount == 1)
        {
            return 0.82;
        }

        var focusWeight = Math.Max(1, focusCount) * 1.6;
        var secondaryWeight = Math.Max(1, secondaryCount) * 0.45;
        return Math.Clamp(
            focusWeight / (focusWeight + secondaryWeight),
            0.65,
            0.82);
    }

    private static bool IsFocusLayoutValid(
        IReadOnlyList<YouTubeMiniPlayerArrangementResult> layout,
        IReadOnlySet<Guid> focusIds,
        Rect area)
    {
        if (!IsLayoutValid(layout, area))
        {
            return false;
        }

        var focusBounds = layout
            .Where(item => focusIds.Contains(item.Id))
            .Select(static item => item.Bounds)
            .ToList();
        var secondaryBounds = layout
            .Where(item => !focusIds.Contains(item.Id))
            .Select(static item => item.Bounds)
            .ToList();
        if (focusBounds.Count == 0 || secondaryBounds.Count == 0)
        {
            return focusBounds.Count > 0;
        }

        var smallestFocusArea = focusBounds.Min(static bounds => bounds.Width * bounds.Height);
        var largestSecondaryArea = secondaryBounds.Max(static bounds => bounds.Width * bounds.Height);
        return smallestFocusArea >= largestSecondaryArea * 1.05;
    }

    private static bool IsLayoutValid(
        IReadOnlyList<YouTubeMiniPlayerArrangementResult> layout,
        Rect area)
    {
        for (var index = 0; index < layout.Count; index++)
        {
            var bounds = layout[index].Bounds;
            if (bounds.Left < area.Left - 0.5
                || bounds.Top < area.Top - 0.5
                || bounds.Right > area.Right + 0.5
                || bounds.Bottom > area.Bottom + 0.5)
            {
                return false;
            }

            for (var otherIndex = index + 1; otherIndex < layout.Count; otherIndex++)
            {
                var overlap = Rect.Intersect(bounds, layout[otherIndex].Bounds);
                if (!overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateChatLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        double spacing)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var (columns, rows) = GetMixedAspectGridDimensions(area, items, spacing, includeChat: true);
        var cellWidth = Math.Max(1, (area.Width - (spacing * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (spacing * (rows - 1))) / rows);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var cell = new Rect(
                area.Left + ((index % columns) * (cellWidth + spacing)),
                area.Top + ((index / columns) * (cellHeight + spacing)),
                cellWidth,
                cellHeight);
            var chatWidth = GetChatWidth(item, cell.Size, spacing);
            if (chatWidth == 0)
            {
                var fullSize = FitSize(item, cell.Width, cell.Height, area.Width, area.Height);
                results.Add(new YouTubeMiniPlayerArrangementResult(
                    item.Id,
                    CenterAndClamp(cell, area, fullSize)));
                continue;
            }

            var playerArea = new Rect(
                cell.Left,
                cell.Top,
                cell.Width - chatWidth - spacing,
                cell.Height);
            var chatBounds = new Rect(
                playerArea.Right + spacing,
                cell.Top,
                chatWidth,
                cell.Height);
            var playerSize = FitSize(
                item,
                playerArea.Width,
                playerArea.Height,
                area.Width,
                area.Height);
            results.Add(new YouTubeMiniPlayerArrangementResult(
                item.Id,
                CenterAndClamp(playerArea, area, playerSize),
                chatBounds));
        }

        return results;
    }

    private static double GetChatWidth(YouTubeMiniPlayerArrangementItem item, Size cell, double spacing)
    {
        var maximum = cell.Width - spacing - (item.MinimumSize?.Width ?? YouTubeMiniPlayerLayoutService.MinimumWidth);
        return !item.HasChat || maximum < YouTubeLiveChatWindowState.MinimumWidth
            || cell.Height < YouTubeLiveChatWindowState.MinimumHeight
            ? 0
            : Math.Clamp(cell.Width * 0.36, YouTubeLiveChatWindowState.MinimumWidth, maximum);
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateGroupLayout(
        YouTubeMiniPlayerArrangeMode mode,
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random random,
        double spacing)
    {
        return mode switch
        {
            YouTubeMiniPlayerArrangeMode.Horizontal => CreateCellsLayout(area, items, items.Count, 1, spacing),
            YouTubeMiniPlayerArrangeMode.Vertical => CreateCellsLayout(area, items, 1, items.Count, spacing),
            YouTubeMiniPlayerArrangeMode.Random => CreateRandomLayout(area, items, random, spacing),
            _ => CreateGridLayout(area, items, spacing)
        };
    }

    private static Rect GetLinearPlacementArea(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        YouTubeMiniPlayerLinearPlacement placement)
    {
        var placementIndex = placement switch
        {
            YouTubeMiniPlayerLinearPlacement.First => 0,
            YouTubeMiniPlayerLinearPlacement.Third => 2,
            _ => 1
        };

        if (mode == YouTubeMiniPlayerArrangeMode.Horizontal)
        {
            var sectionHeight = workArea.Height / 3;
            return new Rect(
                workArea.Left,
                workArea.Top + (sectionHeight * placementIndex),
                workArea.Width,
                sectionHeight);
        }

        var sectionWidth = workArea.Width / 3;
        return new Rect(
            workArea.Left + (sectionWidth * placementIndex),
            workArea.Top,
            sectionWidth,
            workArea.Height);
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateRandomLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random random,
        double spacing)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var (columns, rows) = GetBestGridDimensions(area, items, spacing);
        var cellWidth = Math.Max(1, (area.Width - (spacing * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (spacing * (rows - 1))) / rows);
        var cells = Enumerable.Range(0, items.Count)
            .Select(index => new Rect(
                area.Left + ((index % columns) * (cellWidth + spacing)),
                area.Top + ((index / columns) * (cellHeight + spacing)),
                cellWidth,
                cellHeight))
            .ToList();
        for (var index = cells.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (cells[index], cells[swapIndex]) = (cells[swapIndex], cells[index]);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var cell = cells[index];
            var maximumSize = FitSize(
                item,
                cell.Width,
                cell.Height,
                area.Width,
                area.Height);
            var aspectRatio = GetAspectRatio(item);
            var minimumSize = (spacing == 0 ? item.MinimumSize : null) ?? YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
                YouTubeMiniPlayerLayoutService.MinimumWidth,
                YouTubeMiniPlayerLayoutService.MinimumHeight,
                preferHeight: false,
                aspectRatio: aspectRatio);
            var minimumWidth = Math.Min(minimumSize.Width, maximumSize.Width);
            var width = minimumWidth + ((maximumSize.Width - minimumWidth) * random.NextDouble());
            var height = YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(
                width,
                aspectRatio);
            var left = cell.Left + (Math.Max(0, cell.Width - width) * random.NextDouble());
            var top = cell.Top + (Math.Max(0, cell.Height - height) * random.NextDouble());
            results.Add(new YouTubeMiniPlayerArrangementResult(
                item.Id,
                ClampToArea(new Rect(left, top, width, height), area)));
        }

        return results;
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateGridLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        double spacing)
    {
        if (spacing == 0) return CreatePackedGrid(area, items);
        var (bestColumns, bestRows) = GetMixedAspectGridDimensions(area, items, spacing, includeChat: false);
        return CreateCellsLayout(area, items, bestColumns, bestRows, spacing);
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreatePackedGrid(
        Rect area, IReadOnlyList<YouTubeMiniPlayerArrangementItem> items)
    {
        const double tolerance = 0.000001;
        var ratios = items.Select(GetAspectRatio).ToArray();
        var minimums = items.Select(GetMinimumFittedSize).ToArray();
        var inset = YouTubeMiniPlayerLayoutService.GetChromeInset();
        var bestArea = 0.0;
        var bestRows = Array.Empty<PackedRow>();
        var bestExpansion = 0.0;
        var maxRows = Math.Min(items.Count, Math.Floor((area.Height + tolerance) / minimums.Min(size => size.Height)));
        for (var rowCount = 1; rowCount <= maxRows; rowCount++)
        {
            var rows = new PackedRow[rowCount];
            var start = 0;
            for (var row = 0; row < rowCount; row++)
            {
                var count = items.Count / rowCount + (row < items.Count % rowCount ? 1 : 0);
                var minHeight = 0.0;
                var ratioSum = 0.0;
                for (var index = start; index < start + count; index++)
                {
                    minHeight = Math.Max(minHeight, minimums[index].Height);
                    ratioSum += ratios[index];
                }
                rows[row] = new(count, minHeight, (area.Width - count * inset) / ratioSum + inset, ratioSum);
                start += count;
            }

            var minTotal = rows.Sum(row => row.MinHeight);
            if (minTotal > area.Height + tolerance || rows.Any(row => row.MinHeight > row.MaxHeight + tolerance)) continue;
            // 行の幅か全体の高さに達するまで、各行を最小寸法から広げる。
            var expansion = GetExpansion(area.Height, minTotal, rows.Sum(row => row.MaxHeight));
            var usedArea = rows.Sum(row => row.GetWidth(expansion, inset) * row.GetHeight(expansion));
            if (usedArea <= bestArea + tolerance) continue;
            bestArea = usedArea;
            bestRows = rows;
            bestExpansion = expansion;
        }

        if (bestRows.Length == 0)
        {
            // 同じ行で高さをそろえられなくても、一列なら収まることがある。
            var line = CreatePackedLine(area, items, horizontal: true, YouTubeMiniPlayerLinearPlacement.Second);
            return line.Count > 0 ? line : CreatePackedLine(area, items, horizontal: false, YouTubeMiniPlayerLinearPlacement.Second);
        }

        var result = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var top = area.Top + Math.Max(0, area.Height - bestRows.Sum(row => row.GetHeight(bestExpansion))) / 2;
        foreach (var row in bestRows)
        {
            var height = row.GetHeight(bestExpansion);
            var left = area.Left + Math.Max(0, area.Width - row.GetWidth(bestExpansion, inset)) / 2;
            for (var column = 0; column < row.Count; column++)
            {
                var index = result.Count;
                var width = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(height, ratios[index]);
                result.Add(new(items[index].Id, new(left, top, width, height)));
                left += width;
            }
            top += height;
        }
        return result;
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreatePackedLine(
        Rect area, IReadOnlyList<YouTubeMiniPlayerArrangementItem> items, bool horizontal,
        YouTubeMiniPlayerLinearPlacement placement)
    {
        var length = horizontal ? area.Width : area.Height;
        var crossLength = horizontal ? area.Height : area.Width;
        var ratios = items.Select(GetAspectRatio).ToArray();
        var minimums = items.Select(GetMinimumFittedSize).ToArray();
        if (minimums.Any(size => (horizontal ? size.Height : size.Width) > crossLength + 0.000001)) return [];
        var minimumLengths = minimums.Select(size => horizontal ? size.Width : size.Height).ToArray();
        var minimumTotal = minimumLengths.Sum();
        if (minimumTotal > length + 0.000001) return [];
        var maximumLengths = ratios.Select(ratio => horizontal
            ? YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(crossLength, ratio)
            : YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(crossLength, ratio)).ToArray();
        var maximumTotal = maximumLengths.Sum();
        var expansion = GetExpansion(length, minimumTotal, maximumTotal);
        var alignment = placement switch
        {
            YouTubeMiniPlayerLinearPlacement.First => 0,
            YouTubeMiniPlayerLinearPlacement.Third => 1,
            _ => 0.5
        };
        var offset = Math.Max(0, length - Math.Min(length, maximumTotal)) / 2;
        var result = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var size = minimumLengths[index] + (maximumLengths[index] - minimumLengths[index]) * expansion;
            var crossSize = horizontal
                ? YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(size, ratios[index])
                : YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(size, ratios[index]);
            var crossOffset = Math.Max(0, crossLength - crossSize) * alignment;
            result.Add(new(items[index].Id, horizontal
                ? new(area.Left + offset, area.Top + crossOffset, size, crossSize)
                : new(area.Left + crossOffset, area.Top + offset, crossSize, size)));
            offset += size;
        }
        return result;
    }

    private static Size GetMinimumFittedSize(YouTubeMiniPlayerArrangementItem item)
        => YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(0, 0, false, GetAspectRatio(item), item.MinimumSize);

    private static double GetExpansion(double available, double minimum, double maximum)
        => maximum > minimum ? Math.Clamp((available - minimum) / (maximum - minimum), 0, 1) : 0;

    private readonly record struct PackedRow(int Count, double MinHeight, double MaxHeight, double RatioSum)
    {
        public double GetHeight(double expansion) => MinHeight + (MaxHeight - MinHeight) * expansion;
        public double GetWidth(double expansion, double inset) => RatioSum * (GetHeight(expansion) - inset) + Count * inset;
    }

    private static (int Columns, int Rows) GetMixedAspectGridDimensions(
        Rect area, IReadOnlyList<YouTubeMiniPlayerArrangementItem> items, double spacing, bool includeChat)
    {
        const double tolerance = 0.000001;
        var firstRatio = GetAspectRatio(items[0]);
        if (items.All(item => Math.Abs(GetAspectRatio(item) - firstRatio) <= tolerance))
        {
            return GetBestGridDimensions(area, items, spacing);
        }

        var bestColumns = 0;
        var bestSmallestVideoArea = 0d;
        var bestTotalVideoArea = 0d;
        for (var columns = 1; columns <= items.Count; columns++)
        {
            var rows = (int)Math.Ceiling(items.Count / (double)columns);
            var cell = new Size(
                Math.Max(1, (area.Width - spacing * (columns - 1)) / columns),
                Math.Max(1, (area.Height - spacing * (rows - 1)) / rows));
            var smallestVideoArea = double.PositiveInfinity;
            var totalVideoArea = 0d;
            foreach (var item in items)
            {
                var chatWidth = includeChat ? GetChatWidth(item, cell, spacing) : 0;
                var playerWidth = chatWidth > 0 ? cell.Width - chatWidth - spacing : cell.Width;
                var fittedSize = FitSize(item, playerWidth, cell.Height, area.Width, area.Height);
                if ((includeChat && item.HasChat && chatWidth == 0)
                    || fittedSize.Width > playerWidth + tolerance || fittedSize.Height > cell.Height + tolerance)
                {
                    // 動画や必要なチャットが収まる候補だけを比較する。
                    totalVideoArea = 0;
                    break;
                }

                var videoArea = YouTubeMiniPlayerLayoutService.GetVideoWidth(fittedSize.Width)
                    * YouTubeMiniPlayerLayoutService.GetVideoHeight(fittedSize.Height);
                smallestVideoArea = Math.Min(smallestVideoArea, videoArea);
                totalVideoArea += videoArea;
            }

            if (totalVideoArea > 0
                && (smallestVideoArea > bestSmallestVideoArea + tolerance
                    || (Math.Abs(smallestVideoArea - bestSmallestVideoArea) <= tolerance
                        && totalVideoArea > bestTotalVideoArea + tolerance)))
            {
                bestColumns = columns;
                bestSmallestVideoArea = smallestVideoArea;
                bestTotalVideoArea = totalVideoArea;
            }
        }

        return bestColumns > 0
            ? (bestColumns, (int)Math.Ceiling(items.Count / (double)bestColumns))
            : GetBestGridDimensions(area, items, spacing);
    }

    private static (int Columns, int Rows) GetBestGridDimensions(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        double spacing)
    {
        var bestColumns = 1;
        var bestArea = 0d;
        for (var columns = 1; columns <= items.Count; columns++)
        {
            var rows = (int)Math.Ceiling(items.Count / (double)columns);
            var cellWidth = Math.Max(1, (area.Width - (spacing * (columns - 1))) / columns);
            var cellHeight = Math.Max(1, (area.Height - (spacing * (rows - 1))) / rows);
            var size = FitSize(items[0], cellWidth, cellHeight, area.Width, area.Height);
            var score = size.Width * size.Height;
            if (score > bestArea)
            {
                bestArea = score;
                bestColumns = columns;
            }
        }

        var bestRows = (int)Math.Ceiling(items.Count / (double)bestColumns);
        return (bestColumns, bestRows);
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateCellsLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        int columns,
        int rows,
        double spacing)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var cellWidth = Math.Max(1, (area.Width - (spacing * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (spacing * (rows - 1))) / rows);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var column = index % columns;
            var row = index / columns;
            var cell = new Rect(
                area.Left + (column * (cellWidth + spacing)),
                area.Top + (row * (cellHeight + spacing)),
                cellWidth,
                cellHeight);
            var size = FitSize(item, cell.Width, cell.Height, area.Width, area.Height);
            results.Add(new YouTubeMiniPlayerArrangementResult(
                item.Id,
                CenterAndClamp(cell, area, size)));
        }

        return results;
    }

    private static Size FitSize(
        YouTubeMiniPlayerArrangementItem item,
        double maximumWidth,
        double maximumHeight,
        double areaWidth,
        double areaHeight)
    {
        var aspectRatio = GetAspectRatio(item);
        var width = maximumWidth;
        var height = YouTubeMiniPlayerLayoutService.GetOuterHeightForVideoWidth(
            width,
            aspectRatio);
        if (height > maximumHeight)
        {
            height = maximumHeight;
            width = YouTubeMiniPlayerLayoutService.GetOuterWidthForVideoHeight(
                height,
                aspectRatio);
        }

        var minimumSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
            YouTubeMiniPlayerLayoutService.MinimumWidth,
            YouTubeMiniPlayerLayoutService.MinimumHeight,
            preferHeight: false,
            aspectRatio: aspectRatio);
        if (item.MinimumSize is { } required)
            return YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(width, height, false, aspectRatio, required);
        if ((width < minimumSize.Width || height < minimumSize.Height)
            && minimumSize.Width <= areaWidth
            && minimumSize.Height <= areaHeight)
        {
            return minimumSize;
        }

        return new Size(
            Math.Max(1, Math.Min(width, areaWidth)),
            Math.Max(1, Math.Min(height, areaHeight)));
    }

    private static double GetAspectRatio(YouTubeMiniPlayerArrangementItem item)
    {
        return item.AspectRatio is { } ratio
            && double.IsFinite(ratio)
            && ratio > 0
                ? ratio
                : YouTubeMiniPlayerLayoutService.GetVideoAspectRatio(item.IsShorts);
    }

    private static Rect CenterAndClamp(Rect cell, Rect area, Size size)
    {
        return ClampToArea(
            new Rect(
                cell.Left + ((cell.Width - size.Width) / 2),
                cell.Top + ((cell.Height - size.Height) / 2),
                size.Width,
                size.Height),
            area);
    }

    private static Rect ClampToArea(Rect bounds, Rect area)
    {
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var maximumLeft = Math.Max(area.Left, area.Right - width);
        var maximumTop = Math.Max(area.Top, area.Bottom - height);
        return new Rect(
            Math.Clamp(bounds.Left, area.Left, maximumLeft),
            Math.Clamp(bounds.Top, area.Top, maximumTop),
            width,
            height);
    }

    private static Rect Deflate(Rect area, double margin)
    {
        var horizontalMargin = Math.Min(margin, area.Width / 4);
        var verticalMargin = Math.Min(margin, area.Height / 4);
        return new Rect(
            area.Left + horizontalMargin,
            area.Top + verticalMargin,
            Math.Max(1, area.Width - (horizontalMargin * 2)),
            Math.Max(1, area.Height - (verticalMargin * 2)));
    }
}
