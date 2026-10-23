using System.IO;
using System.Windows;
using TabBrowser.Models;

namespace TabBrowser.Services;

public static class YouTubeMiniPlayerStateFactory
{
    public static YouTubeMiniPlayerState Create(
        BrowserTab sourceTab,
        int windowId,
        string url,
        string? title,
        bool isShorts,
        Rect bounds,
        DateTimeOffset createdAt,
        double? positionSeconds = null)
    {
        return new YouTubeMiniPlayerState
        {
            WindowId = windowId,
            SourceTabId = sourceTab.Id,
            ContainerProfileId = ContainerProfile.NormalizeId(sourceTab.ContainerProfileId),
            Url = url,
            Title = string.IsNullOrWhiteSpace(title)
                ? YouTubeMiniPlayerNavigationService.GetFallbackTitle(url)
                : title.Trim(),
            IsShorts = isShorts,
            LastPositionSeconds = YouTubeControlScriptService.NormalizePlaybackPosition(positionSeconds),
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Volume = 0.7,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }

    public static YouTubeMiniPlayerState CreateLocalFile(
        int windowId,
        string filePath,
        Rect bounds,
        DateTimeOffset createdAt)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return new YouTubeMiniPlayerState
        {
            WindowId = windowId,
            SourceKind = MiniPlayerSourceKind.LocalFile,
            LocalFilePath = normalizedPath,
            Title = Path.GetFileNameWithoutExtension(normalizedPath),
            Width = bounds.Width,
            Height = bounds.Height,
            Left = bounds.Left,
            Top = bounds.Top,
            Volume = 0.7,
            PlaybackRate = 1,
            IsPaused = false,
            IsLooping = true,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }
}
