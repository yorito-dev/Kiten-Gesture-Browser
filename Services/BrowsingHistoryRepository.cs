using System.IO;
using Microsoft.Data.Sqlite;
using TabBrowser.Models;

namespace TabBrowser.Services;

public sealed class BrowsingHistoryRepository
{
    private const int MaximumQueryLimit = 500;
    private const string WebTabSourceKind = "web_tab";
    private const string YouTubeMiniPlayerSourceKind = "youtube_mini_player";
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public BrowsingHistoryRepository()
        : this(AppPaths.BrowsingHistoryDatabaseFile)
    {
    }

    public BrowsingHistoryRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task AddVisitsAsync(
        IReadOnlyList<BrowsingHistoryVisit> visits,
        CancellationToken cancellationToken = default)
    {
        if (visits.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var visit in visits)
            {
                if (!TryNormalizeVisit(visit, out var normalized))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO browsing_history (
                        url,
                        title,
                        host,
                        container_profile_id,
                        source_kind,
                        visited_at_utc)
                    VALUES (
                        $url,
                        $title,
                        $host,
                        $containerProfileId,
                        $sourceKind,
                        $visitedAtUtc);
                    """;
                command.Parameters.AddWithValue("$url", normalized.Url);
                command.Parameters.AddWithValue("$title", normalized.Title);
                command.Parameters.AddWithValue("$host", normalized.Host);
                command.Parameters.AddWithValue("$containerProfileId", normalized.ContainerProfileId);
                command.Parameters.AddWithValue("$sourceKind", ToStorageValue(normalized.SourceKind));
                command.Parameters.AddWithValue("$visitedAtUtc", normalized.VisitedAt.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<BrowsingHistoryEntry>> SearchAsync(
        BrowsingHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = BuildQueryWhere(command, query);
        command.CommandText = $"""
            SELECT id, url, title, host, container_profile_id, source_kind, visited_at_utc
            FROM browsing_history
            {where}
            ORDER BY visited_at_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, MaximumQueryLimit));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var result = new List<BrowsingHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public Task<BrowsingHistoryDeleteSummary> GetAllSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        return GetSummaryAsync(string.Empty, null, cancellationToken);
    }

    public Task<BrowsingHistoryDeleteSummary> GetHostSummaryAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        return GetSummaryAsync(
            "WHERE host = $host COLLATE NOCASE",
            command => command.Parameters.AddWithValue(
                "$host",
                BrowsingHistorySettingsProfile.NormalizeHost(host)),
            cancellationToken);
    }

    public Task<BrowsingHistoryDeleteSummary> GetRangeSummaryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return GetSummaryAsync(
            "WHERE visited_at_utc >= $from AND visited_at_utc < $to",
            command =>
            {
                command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToUnixTimeMilliseconds());
            },
            cancellationToken);
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteDeleteAsync("DELETE FROM browsing_history;", null, cancellationToken);
    }

    public Task<int> DeleteHostAsync(string host, CancellationToken cancellationToken = default)
    {
        return ExecuteDeleteAsync(
            "DELETE FROM browsing_history WHERE host = $host COLLATE NOCASE;",
            command => command.Parameters.AddWithValue(
                "$host",
                BrowsingHistorySettingsProfile.NormalizeHost(host)),
            cancellationToken);
    }

    public Task<int> DeleteRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return ExecuteDeleteAsync(
            "DELETE FROM browsing_history WHERE visited_at_utc >= $from AND visited_at_utc < $to;",
            command =>
            {
                command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToUnixTimeMilliseconds());
            },
            cancellationToken);
    }

    public Task<int> DeleteContainerAsync(
        string containerProfileId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string>
        {
            "container_profile_id = $containerProfileId COLLATE NOCASE"
        };
        return ExecuteDeleteAsync(
            BuildContainerDeleteCommandText(conditions, from, to),
            command =>
            {
                command.Parameters.AddWithValue(
                    "$containerProfileId",
                    ContainerProfile.NormalizeId(containerProfileId));
                if (from is { } fromValue)
                {
                    command.Parameters.AddWithValue(
                        "$from",
                        fromValue.ToUniversalTime().ToUnixTimeMilliseconds());
                }

                if (to is { } toValue)
                {
                    command.Parameters.AddWithValue(
                        "$to",
                        toValue.ToUniversalTime().ToUnixTimeMilliseconds());
                }
            },
            cancellationToken);
    }

    public async Task<int> DeleteIdsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = ids.Where(static id => id > 0).Distinct().ToList();
        if (normalizedIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var deleted = 0;
            foreach (var chunk in normalizedIds.Chunk(400))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                var parameterNames = new List<string>();
                for (var index = 0; index < chunk.Length; index++)
                {
                    var name = "$id" + index;
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, chunk[index]);
                }

                command.CommandText = $"DELETE FROM browsing_history WHERE id IN ({string.Join(", ", parameterNames)});";
                deleted += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<BrowsingHistoryDeleteSummary> GetSummaryAsync(
        string where,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*), MIN(visited_at_utc), MAX(visited_at_utc)
            FROM browsing_history
            {where};
            """;
        bind?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new BrowsingHistoryDeleteSummary(0, null, null);
        }

        var count = (int)Math.Min(int.MaxValue, reader.GetInt64(0));
        return new BrowsingHistoryDeleteSummary(
            count,
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
    }

    private async Task<int> ExecuteDeleteAsync(
        string commandText,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = commandText;
            bind?.Invoke(command);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string BuildContainerDeleteCommandText(
        List<string> conditions,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        if (from is not null)
        {
            conditions.Add("visited_at_utc >= $from");
        }

        if (to is not null)
        {
            conditions.Add("visited_at_utc < $to");
        }

        return "DELETE FROM browsing_history WHERE " + string.Join(" AND ", conditions) + ";";
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS browsing_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL,
                    host TEXT NOT NULL,
                    container_profile_id TEXT NOT NULL,
                    source_kind TEXT NOT NULL DEFAULT 'web_tab',
                    visited_at_utc INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_browsing_history_visited
                    ON browsing_history (visited_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_browsing_history_host_visited
                    ON browsing_history (host, visited_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_browsing_history_container_visited
                    ON browsing_history (container_profile_id, visited_at_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureSourceKindColumnAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 3000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSourceKindColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA table_info(browsing_history);";
        var hasSourceKind = false;
        await using (var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "source_kind", StringComparison.OrdinalIgnoreCase))
                {
                    hasSourceKind = true;
                    break;
                }
            }
        }

        if (hasSourceKind)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE browsing_history
            ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'web_tab';
            """;
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildQueryWhere(SqliteCommand command, BrowsingHistoryQuery query)
    {
        var conditions = new List<string>();
        var search = query.SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("""
                (url LIKE $search ESCAPE '\'
                 OR title LIKE $search ESCAPE '\'
                 OR host LIKE $search ESCAPE '\')
                """);
            command.Parameters.AddWithValue("$search", "%" + EscapeLike(search) + "%");
        }

        if (!string.IsNullOrWhiteSpace(query.ContainerProfileId))
        {
            conditions.Add("container_profile_id = $containerProfileId COLLATE NOCASE");
            command.Parameters.AddWithValue(
                "$containerProfileId",
                ContainerProfile.NormalizeId(query.ContainerProfileId));
        }

        if (query.From is { } from)
        {
            conditions.Add("visited_at_utc >= $from");
            command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToUnixTimeMilliseconds());
        }

        if (query.To is { } to)
        {
            conditions.Add("visited_at_utc < $to");
            command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToUnixTimeMilliseconds());
        }

        return conditions.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", conditions);
    }

    private static string EscapeLike(string value)
    {
        return value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    private static BrowsingHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new BrowsingHistoryEntry(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            FromStorageValue(reader.GetString(5)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
    }

    private static bool TryNormalizeVisit(
        BrowsingHistoryVisit visit,
        out BrowsingHistoryEntry normalized)
    {
        normalized = null!;
        if (!Uri.TryCreate(visit.Url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var url = uri.AbsoluteUri;
        if (url.Length > 8192)
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(visit.Title)
            ? uri.Host
            : visit.Title.Trim();
        if (title.Length > 512)
        {
            title = title[..512];
        }

        normalized = new BrowsingHistoryEntry(
            0,
            url,
            title,
            uri.IdnHost.ToLowerInvariant(),
            ContainerProfile.NormalizeId(visit.ContainerProfileId),
            Enum.IsDefined(visit.SourceKind)
                ? visit.SourceKind
                : BrowsingHistorySourceKind.WebTab,
            visit.VisitedAt == default ? DateTimeOffset.UtcNow : visit.VisitedAt.ToUniversalTime());
        return true;
    }

    private static string ToStorageValue(BrowsingHistorySourceKind sourceKind)
    {
        return sourceKind == BrowsingHistorySourceKind.YouTubeMiniPlayer
            ? YouTubeMiniPlayerSourceKind
            : WebTabSourceKind;
    }

    private static BrowsingHistorySourceKind FromStorageValue(string? value)
    {
        return string.Equals(value, YouTubeMiniPlayerSourceKind, StringComparison.OrdinalIgnoreCase)
            ? BrowsingHistorySourceKind.YouTubeMiniPlayer
            : BrowsingHistorySourceKind.WebTab;
    }
}
