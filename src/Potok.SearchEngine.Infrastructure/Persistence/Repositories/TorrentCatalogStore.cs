using System.Text.Json;
using Dapper;
using Npgsql;
using Potok.SearchEngine.Core.Utils;

namespace Potok.SearchEngine.Infrastructure.Persistence.Repositories;

public sealed class TorrentCatalogStore(string connectionString) : ITorrentCatalogStore
{
    private const string Schema = DbSchema.SearchEngine;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IReadOnlyList<TorrentDetails>> SearchAsync(
        TorrentCatalogQuery query,
        CancellationToken ct = default)
    {
        if (!query.TmdbId.HasValue && string.IsNullOrWhiteSpace(query.Title))
            return [];

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var rows = (await connection.QueryAsync<CanonicalRow>(new CommandDefinition($"""
            SELECT DISTINCT
                t.info_hash AS InfoHash,
                t.tracker_name AS TrackerName,
                t.title AS Title,
                t.url AS Url,
                t.size AS Size,
                t.magnet_uri AS MagnetUri,
                t.seeders AS Seeders,
                t.leechers AS Leechers,
                t.publish_date AS PublishDate,
                t.parsed_info::text AS ParsedInfo,
                t.updated_at AS UpdatedAt,
                t.name AS Name,
                t.original_name AS OriginalName,
                t.release_year AS ReleaseYear,
                t.types AS Types,
                t.quality AS Quality,
                t.video_type AS VideoType,
                t.languages AS Languages,
                t.voices AS Voices,
                t.seasons AS Seasons
            FROM {Schema}.torrents t
            LEFT JOIN {Schema}.torrent_media_links ml ON ml.torrent_id = t.id
            WHERE (@TmdbId::bigint IS NOT NULL AND ml.tmdb_id = @TmdbId)
               OR (@TmdbId::bigint IS NULL AND @Title::text IS NOT NULL AND t.title ILIKE @Title)
            ORDER BY t.seeders DESC
            LIMIT @Limit
            """,
            new
            {
                query.TmdbId,
                Title = string.IsNullOrWhiteSpace(query.Title) ? null : $"%{query.Title}%",
                Limit = Math.Clamp(query.Limit, 1, 2_000)
            },
            cancellationToken: ct))).AsList();
        if (rows.Count == 0)
            return [];

        return rows.Select(row => new TorrentDetails
        {
            InfoHash = row.InfoHash,
            TrackerName = row.TrackerName,
            Title = row.Title,
            Url = row.Url,
            Size = row.Size,
            SizeName = StringConvert.FormatSize(row.Size),
            Magnet = row.MagnetUri,
            Sid = row.Seeders,
            Pir = row.Leechers,
            CreateTime = row.PublishDate.UtcDateTime,
            UpdateTime = row.UpdatedAt.UtcDateTime,
            Name = row.Name,
            OriginalName = row.OriginalName,
            ReleaseYear = row.ReleaseYear ?? 0,
            Types = row.Types,
            Quality = row.Quality ?? 0,
            VideoType = row.VideoType,
            Languages = row.Languages?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Voices = row.Voices?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Seasons = row.Seasons?.ToHashSet(),
            ParsedInfo = string.IsNullOrWhiteSpace(row.ParsedInfo)
                ? null
                : JsonSerializer.Deserialize<ParsedTorrentInfo>(row.ParsedInfo, JsonOptions)
        }).ToArray();
    }

    private sealed class CanonicalRow
    {
        public string InfoHash { get; init; } = string.Empty;
        public string TrackerName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public long Size { get; init; }
        public string MagnetUri { get; init; } = string.Empty;
        public int Seeders { get; init; }
        public int Leechers { get; init; }
        public DateTimeOffset PublishDate { get; init; }
        public string? ParsedInfo { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string? Name { get; init; }
        public string? OriginalName { get; init; }
        public int? ReleaseYear { get; init; }
        public string[]? Types { get; init; }
        public int? Quality { get; init; }
        public string? VideoType { get; init; }
        public string[]? Languages { get; init; }
        public string[]? Voices { get; init; }
        public int[]? Seasons { get; init; }
    }
}
