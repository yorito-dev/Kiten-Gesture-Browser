namespace TabBrowser.Models;

public enum BrowsingHistorySourceKind
{
    WebTab,
    YouTubeMiniPlayer
}

public sealed record BrowsingHistoryEntry(
    long Id,
    string Url,
    string Title,
    string Host,
    string ContainerProfileId,
    BrowsingHistorySourceKind SourceKind,
    DateTimeOffset VisitedAt);

public sealed record BrowsingHistoryVisit(
    Guid SourceId,
    string Url,
    string Title,
    string ContainerProfileId,
    BrowsingHistorySourceKind SourceKind,
    DateTimeOffset VisitedAt);

public sealed record BrowsingHistoryQuery(
    string SearchText = "",
    string ContainerProfileId = "",
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Offset = 0,
    int Limit = 200);

public sealed record BrowsingHistoryDeleteSummary(
    int Count,
    DateTimeOffset? OldestVisitedAt,
    DateTimeOffset? NewestVisitedAt);

public enum BrowsingHistoryOpenMode
{
    CurrentTab,
    NewTab,
    BackgroundTab,
    Split,
    NewWindow
}
