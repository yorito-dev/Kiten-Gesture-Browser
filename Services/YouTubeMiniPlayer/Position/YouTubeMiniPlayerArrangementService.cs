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
    bool HasChat = false);

public sealed record YouTubeMiniPlayerArrangementResult(
    Guid Id,
    Rect Bounds,
    Rect? ChatBounds = null);

public static class YouTubeMiniPlayerArrangementService
{
    private const double OuterMargin = 12;
    private const double Gap = 12;

    public static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateLayout(
        YouTubeMiniPlayerArrangeMode mode,
        Rect workArea,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random? random = null,
        IReadOnlyCollection<Guid>? focusIds = null,
        YouTubeMiniPlayerLinearPlacement linearPlacement = YouTubeMiniPlayerLinearPlacement.Second)
    {
        if (items.Count == 0 || workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return [];
        }

        var randomGenerator = random ?? System.Random.Shared;
        if (mode is YouTubeMiniPlayerArrangeMode.Horizontal or YouTubeMiniPlayerArrangeMode.Vertical)
        {
            var linearArea = Deflate(
                GetLinearPlacementArea(mode, workArea, linearPlacement),
                OuterMargin);
            return CreateGroupLayout(mode, linearArea, items, randomGenerator);
        }

        var layoutArea = Deflate(workArea, OuterMargin);
        if (mode == YouTubeMiniPlayerArrangeMode.Focus)
        {
            return CreateFocusLayout(layoutArea, items, focusIds);
        }

        if (mode == YouTubeMiniPlayerArrangeMode.Monitor)
        {
            return CreateGridLayout(layoutArea, items);
        }

        if (mode == YouTubeMiniPlayerArrangeMode.Chat)
        {
            return CreateChatLayout(layoutArea, items);
        }

        var normalItems = items.Where(static item => GetAspectRatio(item) >= 1).ToList();
        var shortsItems = items.Where(static item => GetAspectRatio(item) < 1).ToList();
        if (normalItems.Count == 0)
        {
            return CreateGroupLayout(mode, layoutArea, shortsItems, randomGenerator);
        }

        if (shortsItems.Count == 0)
        {
            return CreateGroupLayout(mode, layoutArea, normalItems, randomGenerator);
        }

        var normalWeight = normalItems.Count;
        var shortsWeight = shortsItems.Count * 0.65;
        var normalRatio = Math.Clamp(normalWeight / (normalWeight + shortsWeight), 0.45, 0.75);
        var usableWidth = Math.Max(1, layoutArea.Width - Gap);
        var normalWidth = usableWidth * normalRatio;
        var normalArea = new Rect(layoutArea.Left, layoutArea.Top, normalWidth, layoutArea.Height);
        var shortsArea = new Rect(
            normalArea.Right + Gap,
            layoutArea.Top,
            usableWidth - normalWidth,
            layoutArea.Height);

        return CreateGroupLayout(mode, normalArea, normalItems, randomGenerator)
            .Concat(CreateGroupLayout(mode, shortsArea, shortsItems, randomGenerator))
            .ToList();
    }

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateFocusLayout(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        IReadOnlyCollection<Guid>? focusIds)
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
            var focusedLayout = CreateGridLayout(area, focusItems);
            return IsLayoutValid(focusedLayout, area) ? focusedLayout : [];
        }

        var primaryShare = GetFocusPrimaryShare(focusItems.Count, secondaryItems.Count);
        Rect focusArea;
        Rect secondaryArea;
        if (area.Width >= area.Height)
        {
            var usableWidth = Math.Max(1, area.Width - Gap);
            var focusWidth = usableWidth * primaryShare;
            focusArea = new Rect(
                area.Left,
                area.Top,
                focusWidth,
                area.Height);
            secondaryArea = new Rect(
                focusArea.Right + Gap,
                area.Top,
                Math.Max(1, usableWidth - focusWidth),
                area.Height);
        }
        else
        {
            var usableHeight = Math.Max(1, area.Height - Gap);
            var focusHeight = usableHeight * primaryShare;
            focusArea = new Rect(
                area.Left,
                area.Top,
                area.Width,
                focusHeight);
            secondaryArea = new Rect(
                area.Left,
                focusArea.Bottom + Gap,
                area.Width,
                Math.Max(1, usableHeight - focusHeight));
        }

        var focusLayout = focusItems.Count == 1
            ? CreateSingleFocusLayout(area, focusArea, focusItems[0])
            : CreateGridLayout(focusArea, focusItems);
        var secondaryLayout = CreateGridLayout(secondaryArea, secondaryItems);
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
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var (columns, rows) = GetBestGridDimensions(area, items);
        var cellWidth = Math.Max(1, (area.Width - (Gap * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (Gap * (rows - 1))) / rows);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var cell = new Rect(
                area.Left + ((index % columns) * (cellWidth + Gap)),
                area.Top + ((index / columns) * (cellHeight + Gap)),
                cellWidth,
                cellHeight);
            if (!item.HasChat
                || cell.Width < YouTubeMiniPlayerLayoutService.MinimumWidth
                    + Gap
                    + YouTubeLiveChatWindowState.MinimumWidth
                || cell.Height < YouTubeLiveChatWindowState.MinimumHeight)
            {
                var fullSize = FitSize(item, cell.Width, cell.Height, area.Width, area.Height);
                results.Add(new YouTubeMiniPlayerArrangementResult(
                    item.Id,
                    CenterAndClamp(cell, area, fullSize)));
                continue;
            }

            var chatWidth = Math.Clamp(
                cell.Width * 0.36,
                YouTubeLiveChatWindowState.MinimumWidth,
                cell.Width - Gap - YouTubeMiniPlayerLayoutService.MinimumWidth);
            var playerArea = new Rect(
                cell.Left,
                cell.Top,
                cell.Width - chatWidth - Gap,
                cell.Height);
            var chatBounds = new Rect(
                playerArea.Right + Gap,
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

    private static IReadOnlyList<YouTubeMiniPlayerArrangementResult> CreateGroupLayout(
        YouTubeMiniPlayerArrangeMode mode,
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items,
        System.Random random)
    {
        return mode switch
        {
            YouTubeMiniPlayerArrangeMode.Horizontal => CreateCellsLayout(area, items, items.Count, 1),
            YouTubeMiniPlayerArrangeMode.Vertical => CreateCellsLayout(area, items, 1, items.Count),
            YouTubeMiniPlayerArrangeMode.Random => CreateRandomLayout(area, items, random),
            _ => CreateGridLayout(area, items)
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
        System.Random random)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var (columns, rows) = GetBestGridDimensions(area, items);
        var cellWidth = Math.Max(1, (area.Width - (Gap * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (Gap * (rows - 1))) / rows);
        var cells = Enumerable.Range(0, items.Count)
            .Select(index => new Rect(
                area.Left + ((index % columns) * (cellWidth + Gap)),
                area.Top + ((index / columns) * (cellHeight + Gap)),
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
            var minimumSize = YouTubeMiniPlayerLayoutService.NormalizeSizeToVideo(
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
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items)
    {
        var (bestColumns, bestRows) = GetBestGridDimensions(area, items);
        return CreateCellsLayout(area, items, bestColumns, bestRows);
    }

    private static (int Columns, int Rows) GetBestGridDimensions(
        Rect area,
        IReadOnlyList<YouTubeMiniPlayerArrangementItem> items)
    {
        var bestColumns = 1;
        var bestArea = 0d;
        for (var columns = 1; columns <= items.Count; columns++)
        {
            var rows = (int)Math.Ceiling(items.Count / (double)columns);
            var cellWidth = Math.Max(1, (area.Width - (Gap * (columns - 1))) / columns);
            var cellHeight = Math.Max(1, (area.Height - (Gap * (rows - 1))) / rows);
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
        int rows)
    {
        var results = new List<YouTubeMiniPlayerArrangementResult>(items.Count);
        var cellWidth = Math.Max(1, (area.Width - (Gap * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (area.Height - (Gap * (rows - 1))) / rows);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var column = index % columns;
            var row = index / columns;
            var cell = new Rect(
                area.Left + (column * (cellWidth + Gap)),
                area.Top + (row * (cellHeight + Gap)),
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
