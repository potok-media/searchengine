using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Serilog;

namespace Potok.SearchEngine.Infrastructure.Persistence.Repositories;

public sealed class TorrentObservationStore(
    string connectionString,
    TimeSpan peerFreshness,
    ILogger logger) : ITorrentObservationStore
{
    private const string Schema = DbSchema.SearchEngine;

    public async Task<TorrentPersistResult> UpsertAsync(
        TorrentPersistRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var errors = new List<TorrentPersistError>();
        var affectedTorrentIds = new HashSet<Guid>();
        var canonicalHashes = new HashSet<string>(StringComparer.Ordinal);
        var links = new HashSet<(Guid, long)>();
        var observationCount = 0;

        try
        {
            foreach (var item in request.Observations)
            {
                ct.ThrowIfCancellationRequested();
                var source = item.Source;
                var tracker = string.IsNullOrWhiteSpace(item.TrackerName)
                    ? request.Tracker.ToString().ToLowerInvariant()
                    : item.TrackerName.Trim().ToLowerInvariant();
                if (source is null)
                {
                    errors.Add(new TorrentPersistError(
                        "missing_source", tracker, item.Url ?? string.Empty,
                        "The tracker result did not include a source snapshot."));
                    continue;
                }
                if (source.Tracker != request.Tracker)
                {
                    errors.Add(new TorrentPersistError(
                        "tracker_mismatch", tracker, source.SourceKey,
                        "The source tracker does not match the persisted batch."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(source.SourceKey) ||
                    !Uri.TryCreate(source.SourceUrl, UriKind.Absolute, out _))
                {
                    errors.Add(new TorrentPersistError(
                        "invalid_source", tracker, source.SourceKey,
                        "SourceKey must be non-empty and SourceUrl must be absolute."));
                    continue;
                }

                if (!TorrentIdentity.TryResolve(
                        item,
                        out var normalizedHash,
                        out var identityErrorCode,
                        out var identityErrorMessage))
                {
                    errors.Add(new TorrentPersistError(
                        identityErrorCode!,
                        tracker,
                        source.SourceKey,
                        identityErrorMessage!));
                    continue;
                }

                item.InfoHash = normalizedHash;

                var previous = await connection.QuerySingleOrDefaultAsync<ExistingObservation>(
                    new CommandDefinition($"""
                        SELECT o.torrent_id AS TorrentId,
                               o.fetched_at AS FetchedAt,
                               o.source_payload::text AS SourcePayload,
                               t.info_hash AS InfoHash
                        FROM {Schema}.torrent_observations o
                        LEFT JOIN {Schema}.torrents t ON t.id = o.torrent_id
                        WHERE o.tracker = @Tracker AND o.source_key = @SourceKey
                        """,
                        new { Tracker = tracker, source.SourceKey },
                        transaction,
                        cancellationToken: ct));
                if (previous is not null && source.FetchedAt < previous.FetchedAt)
                    continue;

                var torrentId = await EnsureCanonicalAsync(
                    connection, transaction, normalizedHash, tracker, item, source, ct);
                canonicalHashes.Add(normalizedHash);
                affectedTorrentIds.Add(torrentId);

                if (previous?.TorrentId is { } previousTorrentId)
                {
                    affectedTorrentIds.Add(previousTorrentId);
                    if (previousTorrentId != torrentId)
                        logger.Information(
                            "source_hash_changed: {Tracker}/{SourceKey} relinked from {OldInfoHash} to {NewInfoHash}",
                            tracker, source.SourceKey, previous.InfoHash, normalizedHash);
                }

                observationCount += await UpsertObservationAsync(
                    connection,
                    transaction,
                    tracker,
                    torrentId,
                    item,
                    source,
                    MergePayload(source.SourcePayload, previous?.SourcePayload, source.DetailsState),
                    ct);

                if (request.TmdbId.HasValue)
                {
                    await connection.ExecuteAsync(new CommandDefinition($"""
                        INSERT INTO {Schema}.torrent_media_links (
                            torrent_id, tmdb_id, query, reason, first_seen_at, last_seen_at)
                        VALUES (@TorrentId, @TmdbId, @Query, @Reason, now(), now())
                        ON CONFLICT (torrent_id, tmdb_id) DO UPDATE SET
                            query = EXCLUDED.query,
                            reason = EXCLUDED.reason,
                            last_seen_at = EXCLUDED.last_seen_at
                        """,
                        new
                        {
                            TorrentId = torrentId,
                            TmdbId = request.TmdbId.Value,
                            request.Query,
                            Reason = request.Reason.ToString().ToLowerInvariant()
                        },
                        transaction,
                        cancellationToken: ct));
                    links.Add((torrentId, request.TmdbId.Value));
                }
            }

            foreach (var torrentId in affectedTorrentIds)
                await TorrentCanonicalProjection.RecomputeAsync(
                    connection,
                    transaction,
                    torrentId,
                    peerFreshness,
                    logger,
                    ct);

            await transaction.CommitAsync(ct);
            return new TorrentPersistResult(
                observationCount,
                canonicalHashes.Count,
                links.Count,
                errors);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<Guid> EnsureCanonicalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string infoHash,
        string tracker,
        TorrentDetails item,
        TorrentSourceSnapshot source,
        CancellationToken ct)
    {
        var created = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition($"""
            INSERT INTO {Schema}.torrents (
                info_hash, tracker_name, title, url, size, magnet_uri,
                seeders, leechers, publish_date, parsed_info, updated_at)
            VALUES (
                @InfoHash, @Tracker, @Title, @Url, @Size, @Magnet,
                @Seeders, @Leechers, @PublishDate, NULL, now())
            ON CONFLICT (info_hash) DO NOTHING
            RETURNING id
            """,
            new
            {
                InfoHash = infoHash,
                Tracker = tracker,
                Title = item.Title ?? string.Empty,
                Url = source.SourceUrl,
                Size = (long)Math.Max(0, item.Size),
                Magnet = MagnetBuilder.BuildCanonical(infoHash),
                Seeders = Math.Max(0, item.Sid),
                Leechers = Math.Max(0, item.Pir),
                PublishDate = item.CreateTime == default
                    ? source.FetchedAt
                    : new DateTimeOffset(item.CreateTime.ToUniversalTime())
            },
            transaction,
            cancellationToken: ct));
        return created ?? await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            $"SELECT id FROM {Schema}.torrents WHERE info_hash = @InfoHash",
            new { InfoHash = infoHash },
            transaction,
            cancellationToken: ct));
    }

    private static async Task<int> UpsertObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tracker,
        Guid torrentId,
        TorrentDetails item,
        TorrentSourceSnapshot source,
        string payload,
        CancellationToken ct)
    {
        var fields = source.ObservedFields |
                     TorrentObservedFields.InfoHash |
                     TorrentObservedFields.Magnet;
        return await connection.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO {Schema}.torrent_observations (
                torrent_id, tracker, source_key, source_url, title, name, original_name,
                magnet_uri, size_bytes, size_label, seeders, leechers, publish_date,
                release_year, types, quality, video_type, languages, voices, seasons,
                observed_fields, details_state, source_updated_at, fetched_at,
                details_fetched_at, payload_schema_version, parser_version, source_payload,
                created_at, updated_at)
            VALUES (
                @TorrentId, @Tracker, @SourceKey, @SourceUrl, @Title, @Name, @OriginalName,
                @Magnet, @SizeBytes, @SizeLabel, @Seeders, @Leechers, @PublishDate,
                @ReleaseYear, @Types, @Quality, @VideoType, @Languages, @Voices, @Seasons,
                @ObservedFields, @DetailsState, @SourceUpdatedAt, @FetchedAt,
                @DetailsFetchedAt, @PayloadSchemaVersion, @ParserVersion, @Payload::jsonb,
                now(), now())
            ON CONFLICT (tracker, source_key) DO UPDATE SET
                torrent_id = EXCLUDED.torrent_id,
                source_url = EXCLUDED.source_url,
                title = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Title)}) <> 0 THEN EXCLUDED.title ELSE torrent_observations.title END,
                name = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Names)}) <> 0 THEN EXCLUDED.name ELSE torrent_observations.name END,
                original_name = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Names)}) <> 0 THEN EXCLUDED.original_name ELSE torrent_observations.original_name END,
                magnet_uri = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Magnet)}) <> 0 THEN EXCLUDED.magnet_uri ELSE torrent_observations.magnet_uri END,
                size_bytes = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Size)}) <> 0 THEN EXCLUDED.size_bytes ELSE torrent_observations.size_bytes END,
                size_label = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Size)}) <> 0 THEN EXCLUDED.size_label ELSE torrent_observations.size_label END,
                seeders = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Seeders)}) <> 0 THEN EXCLUDED.seeders ELSE torrent_observations.seeders END,
                leechers = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Leechers)}) <> 0 THEN EXCLUDED.leechers ELSE torrent_observations.leechers END,
                publish_date = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.PublishDate)}) <> 0 THEN EXCLUDED.publish_date ELSE torrent_observations.publish_date END,
                release_year = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.ReleaseYear)}) <> 0 THEN EXCLUDED.release_year ELSE torrent_observations.release_year END,
                types = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Types)}) <> 0 THEN EXCLUDED.types ELSE torrent_observations.types END,
                quality = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Quality)}) <> 0 THEN EXCLUDED.quality ELSE torrent_observations.quality END,
                video_type = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.VideoType)}) <> 0 THEN EXCLUDED.video_type ELSE torrent_observations.video_type END,
                languages = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Languages)}) <> 0 THEN EXCLUDED.languages ELSE torrent_observations.languages END,
                voices = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Voices)}) <> 0 THEN EXCLUDED.voices ELSE torrent_observations.voices END,
                seasons = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.Seasons)}) <> 0 THEN EXCLUDED.seasons ELSE torrent_observations.seasons END,
                observed_fields = torrent_observations.observed_fields | EXCLUDED.observed_fields,
                details_state = EXCLUDED.details_state,
                source_updated_at = CASE WHEN (EXCLUDED.observed_fields & {Bit(TorrentObservedFields.SourceUpdatedAt)}) <> 0 THEN EXCLUDED.source_updated_at ELSE torrent_observations.source_updated_at END,
                fetched_at = EXCLUDED.fetched_at,
                details_fetched_at = EXCLUDED.details_fetched_at,
                payload_schema_version = EXCLUDED.payload_schema_version,
                parser_version = EXCLUDED.parser_version,
                source_payload = EXCLUDED.source_payload,
                updated_at = now()
            WHERE EXCLUDED.fetched_at >= torrent_observations.fetched_at
            """,
            new
            {
                TorrentId = torrentId,
                Tracker = tracker,
                source.SourceKey,
                source.SourceUrl,
                Title = Has(fields, TorrentObservedFields.Title) ? item.Title : null,
                Name = Has(fields, TorrentObservedFields.Names) ? item.Name : null,
                OriginalName = Has(fields, TorrentObservedFields.Names) ? item.OriginalName : null,
                Magnet = item.Magnet,
                SizeBytes = Has(fields, TorrentObservedFields.Size) ? (long?)Math.Max(0, item.Size) : null,
                SizeLabel = Has(fields, TorrentObservedFields.Size) ? item.SizeName : null,
                Seeders = Has(fields, TorrentObservedFields.Seeders) ? (int?)item.Sid : null,
                Leechers = Has(fields, TorrentObservedFields.Leechers) ? (int?)item.Pir : null,
                PublishDate = Has(fields, TorrentObservedFields.PublishDate) && item.CreateTime != default
                    ? new DateTimeOffset(item.CreateTime.ToUniversalTime())
                    : (DateTimeOffset?)null,
                ReleaseYear = Has(fields, TorrentObservedFields.ReleaseYear) ? (int?)item.ReleaseYear : null,
                Types = Has(fields, TorrentObservedFields.Types) ? item.Types : null,
                Quality = Has(fields, TorrentObservedFields.Quality) ? (int?)item.Quality : null,
                VideoType = Has(fields, TorrentObservedFields.VideoType) ? item.VideoType : null,
                Languages = Has(fields, TorrentObservedFields.Languages) ? item.Languages?.ToArray() : null,
                Voices = Has(fields, TorrentObservedFields.Voices) ? item.Voices?.ToArray() : null,
                Seasons = Has(fields, TorrentObservedFields.Seasons) ? item.Seasons?.ToArray() : null,
                ObservedFields = checked((long)fields),
                DetailsState = ToStorage(source.DetailsState),
                source.SourceUpdatedAt,
                FetchedAt = source.FetchedAt.ToUniversalTime(),
                DetailsFetchedAt = source.DetailsFetchedAt?.ToUniversalTime(),
                source.PayloadSchemaVersion,
                source.ParserVersion,
                Payload = payload
            },
            transaction,
            cancellationToken: ct));
    }

    private static string MergePayload(
        JsonElement incoming,
        string? existingJson,
        TrackerDetailsState state)
    {
        var node = incoming.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(incoming.GetRawText())?.AsObject()
            : null;
        node ??= new JsonObject();
        var retainDetails = state is TrackerDetailsState.Failed
            or TrackerDetailsState.Partial
            or TrackerDetailsState.BudgetExhausted;
        if (retainDetails && !string.IsNullOrWhiteSpace(existingJson))
        {
            var existing = JsonNode.Parse(existingJson)?.AsObject();
            if (existing?["details"] is { } details)
                node["details"] = details.DeepClone();
        }
        node["schemaVersion"] ??= 1;
        node["parserVersion"] ??= string.Empty;
        node["list"] ??= new JsonObject();
        node["details"] ??= new JsonObject();
        node["unmapped"] ??= new JsonObject();
        node["warnings"] ??= new JsonArray();
        return node.ToJsonString();
    }

    private static long Bit(TorrentObservedFields field) => checked((long)field);
    private static bool Has(TorrentObservedFields fields, TorrentObservedFields field) =>
        (fields & field) != 0;
    private static string ToStorage(TrackerDetailsState state) => state switch
    {
        TrackerDetailsState.NotRequired => "not_required",
        TrackerDetailsState.Fetched => "fetched",
        TrackerDetailsState.Partial => "partial",
        TrackerDetailsState.Failed => "failed",
        TrackerDetailsState.BudgetExhausted => "budget_exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private sealed class ExistingObservation
    {
        public Guid? TorrentId { get; init; }
        public DateTimeOffset FetchedAt { get; init; }
        public string SourcePayload { get; init; } = "{}";
        public string? InfoHash { get; init; }
    }
}
