using System.Text.Json;
using Dapper;
using Npgsql;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Serilog;

namespace Potok.SearchEngine.Infrastructure.Persistence.Repositories;

internal static class TorrentCanonicalProjection
{
    private const string Schema = DbSchema.SearchEngine;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task RecomputeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid torrentId,
        TimeSpan peerFreshness,
        ILogger logger,
        CancellationToken ct)
    {
        var sources = (await connection.QueryAsync<ProjectionRow>(new CommandDefinition($"""
            SELECT tracker AS Tracker,
                   source_url AS SourceUrl,
                   title AS Title,
                   name AS Name,
                   original_name AS OriginalName,
                   magnet_uri AS MagnetUri,
                   size_bytes AS SizeBytes,
                   seeders AS Seeders,
                   leechers AS Leechers,
                   publish_date AS PublishDate,
                   release_year AS ReleaseYear,
                   types AS Types,
                   quality AS Quality,
                   video_type AS VideoType,
                   languages AS Languages,
                   voices AS Voices,
                   seasons AS Seasons,
                   observed_fields AS ObservedFields,
                   details_state AS DetailsState,
                   source_updated_at AS SourceUpdatedAt,
                   fetched_at AS FetchedAt
            FROM {Schema}.torrent_observations
            WHERE torrent_id = @TorrentId
            """,
            new { TorrentId = torrentId },
            transaction,
            cancellationToken: ct))).AsList();
        if (sources.Count == 0)
            return;

        var selected = Rank(sources).First();
        var title = Select(sources, TorrentObservedFields.Title) ?? selected;
        var names = Select(sources, TorrentObservedFields.Names) ?? selected;
        var size = Select(sources, TorrentObservedFields.Size) ?? selected;
        var published = Select(sources, TorrentObservedFields.PublishDate) ?? selected;
        var year = Select(sources, TorrentObservedFields.ReleaseYear) ?? selected;
        var quality = Select(sources, TorrentObservedFields.Quality) ?? selected;
        var videoType = Select(sources, TorrentObservedFields.VideoType) ?? selected;
        var cutoff = DateTimeOffset.UtcNow - peerFreshness;
        var peerSources = sources.Where(x => x.FetchedAt >= cutoff).ToArray();
        if (peerSources.Length == 0)
            peerSources = sources.OrderByDescending(x => x.FetchedAt).Take(1).ToArray();
        var infoHash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT info_hash FROM {Schema}.torrents WHERE id = @TorrentId",
            new { TorrentId = torrentId },
            transaction,
            cancellationToken: ct)) ?? throw new InvalidDataException(
            $"Canonical torrent {torrentId} disappeared during projection.");

        var observedSizes = sources
            .Where(x => Has(x, TorrentObservedFields.Size) && x.SizeBytes > 0)
            .Select(x => x.SizeBytes!.Value)
            .ToArray();
        if (observedSizes.Length > 1)
        {
            var minSize = observedSizes.Min();
            var maxSize = observedSizes.Max();
            if (maxSize - minSize > maxSize / 100d)
                logger.Warning(
                    "size_conflict: torrent {TorrentId} source sizes {MinSizeBytes}..{MaxSizeBytes} differ by more than 1%, selected {SelectedSizeBytes}",
                    torrentId, minSize, maxSize, size.SizeBytes ?? 0);
        }

        var parsed = ParseTitle(title.Title ?? string.Empty);
        await connection.ExecuteAsync(new CommandDefinition($"""
            UPDATE {Schema}.torrents SET
                tracker_name = @TrackerName,
                title = @Title,
                url = @Url,
                size = @Size,
                magnet_uri = @Magnet,
                seeders = @Seeders,
                leechers = @Leechers,
                publish_date = @PublishDate,
                parsed_info = @ParsedInfo::jsonb,
                name = @Name,
                original_name = @OriginalName,
                release_year = @ReleaseYear,
                types = @Types,
                quality = @Quality,
                video_type = @VideoType,
                languages = @Languages,
                voices = @Voices,
                seasons = @Seasons,
                updated_at = now()
            WHERE id = @TorrentId
            """,
            new
            {
                TorrentId = torrentId,
                TrackerName = selected.Tracker,
                Title = title.Title ?? string.Empty,
                Url = selected.SourceUrl,
                Size = size.SizeBytes ?? 0,
                Magnet = MagnetBuilder.Build(infoHash, sourceMagnets: sources.Select(x => x.MagnetUri))
                         ?? MagnetBuilder.BuildCanonical(infoHash),
                Seeders = peerSources.Where(x => Has(x, TorrentObservedFields.Seeders))
                    .Select(x => x.Seeders ?? 0).DefaultIfEmpty().Max(),
                Leechers = peerSources.Where(x => Has(x, TorrentObservedFields.Leechers))
                    .Select(x => x.Leechers ?? 0).DefaultIfEmpty().Max(),
                PublishDate = published.PublishDate ?? selected.FetchedAt,
                ParsedInfo = JsonSerializer.Serialize(parsed, JsonOptions),
                Name = names.Name,
                OriginalName = names.OriginalName,
                ReleaseYear = year.ReleaseYear,
                Types = Union(sources, TorrentObservedFields.Types, x => x.Types),
                Quality = quality.Quality,
                VideoType = videoType.VideoType,
                Languages = Union(sources, TorrentObservedFields.Languages, x => x.Languages),
                Voices = Union(sources, TorrentObservedFields.Voices, x => x.Voices),
                Seasons = sources.Where(x => Has(x, TorrentObservedFields.Seasons))
                    .SelectMany(x => x.Seasons ?? [])
                    .Distinct()
                    .Order()
                    .ToArray()
            },
            transaction,
            cancellationToken: ct));
    }

    private static ParsedTorrentInfo ParseTitle(string title)
    {
        var parsed = new TorrentTitleParser.Torrent(title);
        return new ParsedTorrentInfo
        {
            Resolution = parsed.Resolution,
            Quality = parsed.Quality,
            Codec = parsed.Codec,
            Audio = parsed.Audio,
            Group = parsed.Group,
            Container = parsed.Container,
            Region = parsed.Region,
            Year = parsed.Year,
            Seasons = parsed.Season.HasValue ? [parsed.Season.Value] : null,
            Episodes = parsed.Episode.HasValue ? [parsed.Episode.Value] : null,
            IsComplete = parsed.Complete
        };
    }

    private static ProjectionRow? Select(
        IEnumerable<ProjectionRow> sources,
        TorrentObservedFields field) => Rank(sources.Where(x => Has(x, field))).FirstOrDefault();

    private static IOrderedEnumerable<ProjectionRow> Rank(IEnumerable<ProjectionRow> sources) =>
        sources.OrderBy(x => x.DetailsState switch
            {
                "fetched" => 0,
                "not_required" => 1,
                "partial" => 2,
                "failed" => 3,
                _ => 4
            })
            .ThenByDescending(x => x.SourceUpdatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.FetchedAt)
            .ThenBy(x => Enum.TryParse<TrackerType>(x.Tracker, true, out var tracker)
                ? (int)tracker
                : int.MaxValue);

    private static string[] Union(
        IEnumerable<ProjectionRow> sources,
        TorrentObservedFields field,
        Func<ProjectionRow, string[]?> selector) =>
        sources.Where(x => Has(x, field))
            .SelectMany(x => selector(x) ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool Has(ProjectionRow row, TorrentObservedFields field) =>
        (row.ObservedFields & checked((long)field)) != 0;

    private sealed class ProjectionRow
    {
        public string Tracker { get; init; } = string.Empty;
        public string SourceUrl { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string? Name { get; init; }
        public string? OriginalName { get; init; }
        public string? MagnetUri { get; init; }
        public long? SizeBytes { get; init; }
        public int? Seeders { get; init; }
        public int? Leechers { get; init; }
        public DateTimeOffset? PublishDate { get; init; }
        public int? ReleaseYear { get; init; }
        public string[]? Types { get; init; }
        public int? Quality { get; init; }
        public string? VideoType { get; init; }
        public string[]? Languages { get; init; }
        public string[]? Voices { get; init; }
        public int[]? Seasons { get; init; }
        public long ObservedFields { get; init; }
        public string DetailsState { get; init; } = string.Empty;
        public DateTimeOffset? SourceUpdatedAt { get; init; }
        public DateTimeOffset FetchedAt { get; init; }
    }
}
