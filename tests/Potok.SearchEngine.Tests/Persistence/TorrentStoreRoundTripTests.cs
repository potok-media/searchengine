using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Details;
using Potok.SearchEngine.Infrastructure.Persistence;
using Potok.SearchEngine.Infrastructure.Persistence.Migrations;
using Potok.SearchEngine.Infrastructure.Persistence.Repositories;
using Serilog.Core;
using Xunit;

namespace Potok.SearchEngine.Tests.Persistence;

public class TorrentStoreRoundTripTests : IAsyncLifetime
{
    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("POTOK_SEARCH_TEST_POSTGRES") ??
        "Host=localhost;Port=5432;Database=postgres;Username=***REMOVED***;Password=***REMOVED***";

    private readonly string _database = $"potok_searchtest_{Guid.NewGuid():N}";

    private string ConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _database };
            return builder.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {_database}");

        var services = new ServiceCollection();
        services.AddSearchEngineMigrations(ConnectionString);
        using var provider = services.BuildServiceProvider();
        provider.RunSearchEngineMigrations();
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    [Fact]
    public async Task Two_tracker_observations_round_trip_as_one_canonical_torrent_with_two_media_links()
    {
        var observations = CreateObservationStore();
        var catalog = CreateCatalogStore();
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var older = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(1);

        var first = Observation(
            TrackerType.Rutracker,
            "rt-42",
            "https://rutracker.org/forum/viewtopic.php?t=42",
            hash,
            "Дюна Dune 2021 BDRip",
            40,
            older,
            ["movie"],
            ["ru"],
            [1]);
        var second = Observation(
            TrackerType.Rutor,
            "rutor-17",
            "http://rutor.info/torrent/17",
            hash,
            "Dune Дюна 2021 WEB-DL",
            55,
            newer,
            ["film"],
            ["en"],
            [2]);

        var firstWrite = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", 438631, TrackerType.Rutracker, [first], TrackerIngestionReason.Interactive));
        var secondWrite = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", 999001, TrackerType.Rutor, [second], TrackerIngestionReason.Refresh));

        Assert.True(firstWrite.Succeeded);
        Assert.True(secondWrite.Succeeded);
        Assert.Equal(1, firstWrite.ObservationsWritten);
        Assert.Equal(1, secondWrite.ObservationsWritten);
        Assert.Equal(1, firstWrite.MediaLinksWritten);
        Assert.Equal(1, secondWrite.MediaLinksWritten);

        var byFirstMedia = await catalog.SearchAsync(new TorrentCatalogQuery(TmdbId: 438631));
        var bySecondMedia = await catalog.SearchAsync(new TorrentCatalogQuery(TmdbId: 999001));
        var canonical = Assert.Single(byFirstMedia);
        Assert.Equal(canonical.InfoHash, Assert.Single(bySecondMedia).InfoHash);
        Assert.Equal(hash, canonical.InfoHash);
        Assert.Equal("Dune Дюна 2021 WEB-DL", canonical.Title);
        Assert.Equal("rutor", canonical.TrackerName);
        Assert.Equal(55, canonical.Sid);
        Assert.Equal(["film", "movie"], canonical.Types!.Order().ToArray());
        Assert.Equal(["en", "ru"], canonical.Languages!.Order().ToArray());
        Assert.Equal([1, 2], canonical.Seasons!.Order().ToArray());

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var sources = (await connection.QueryAsync<StoredSource>($"""
            SELECT source_url AS SourceUrl, source_payload::text AS SourcePayload
            FROM {DbSchema.SearchEngine}.torrent_observations
            ORDER BY tracker, source_key
            """)).ToArray();
        Assert.Equal(2, sources.Length);
        Assert.Contains(sources, x =>
            x.SourceUrl == "https://rutracker.org/forum/viewtopic.php?t=42" &&
            JsonDocument.Parse(x.SourcePayload).RootElement
                .GetProperty("unmapped").GetProperty("catalogueCode").GetString() == "A-42");
        Assert.Contains(sources, x => x.SourceUrl == "http://rutor.info/torrent/17");
    }

    [Fact]
    public async Task Glued_russian_original_query_matches_by_individual_tokens()
    {
        var observations = CreateObservationStore();
        var catalog = CreateCatalogStore();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var item = Observation(
            TrackerType.Rutor,
            "rutor-99",
            "http://rutor.info/torrent/99",
            hash,
            "Дюна (2021) BDRip 1080p",
            10,
            new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero),
            ["movie"],
            null,
            null);

        var write = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", null, TrackerType.Rutor, [item], TrackerIngestionReason.Interactive));
        Assert.True(write.Succeeded);

        var found = Assert.Single(await catalog.SearchAsync(new TorrentCatalogQuery(Title: "Дюна Dune")));
        Assert.Equal(hash, found.InfoHash);
    }

    [Fact]
    public async Task Observed_null_clears_a_value_while_an_absent_field_preserves_it()
    {
        var observations = CreateObservationStore();
        var catalog = CreateCatalogStore();
        const string hash = "abcdef0123456789abcdef0123456789abcdef01";
        var at = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        var initial = Observation(
            TrackerType.Rutracker,
            "rt-clear",
            "https://rutracker.org/forum/viewtopic.php?t=99",
            hash,
            "Во все тяжкие Breaking Bad",
            10,
            at,
            ["series"],
            ["ru"],
            [1]);
        initial.Name = "Во все тяжкие";
        initial.OriginalName = "Breaking Bad";

        await observations.UpsertAsync(new TorrentPersistRequest(
            "Во все тяжкие Breaking Bad", 1396, TrackerType.Rutracker, [initial], TrackerIngestionReason.Interactive));

        var update = Observation(
            TrackerType.Rutracker,
            "rt-clear",
            "https://rutracker.org/forum/viewtopic.php?t=99",
            hash,
            "Во все тяжкие Breaking Bad remux",
            0,
            at.AddHours(1),
            null,
            null,
            null);
        update.Name = null;
        update.OriginalName = null;
        update.Source = update.Source! with
        {
            ObservedFields = TorrentObservedFields.InfoHash |
                             TorrentObservedFields.Title |
                             TorrentObservedFields.Names
        };

        await observations.UpsertAsync(new TorrentPersistRequest(
            "Во все тяжкие Breaking Bad", 1396, TrackerType.Rutracker, [update], TrackerIngestionReason.Refresh));

        var stored = Assert.Single(await catalog.SearchAsync(new TorrentCatalogQuery(TmdbId: 1396)));
        Assert.Equal("Во все тяжкие Breaking Bad remux", stored.Title);
        Assert.Null(stored.Name);
        Assert.Null(stored.OriginalName);
        Assert.Equal(["series"], stored.Types!);
        Assert.Equal(["ru"], stored.Languages);
        Assert.Equal([1], stored.Seasons);
        Assert.Equal(10, stored.Sid);
    }

    [Fact]
    public async Task Invalid_identity_is_rejected_without_writing_an_observation()
    {
        var observations = CreateObservationStore();
        var item = Observation(
            TrackerType.Rutracker,
            "rt-mismatch",
            "https://rutracker.org/forum/viewtopic.php?t=404",
            "0123456789abcdef0123456789abcdef01234567",
            "Дюна Dune mismatch",
            1,
            DateTimeOffset.UtcNow,
            ["movie"],
            null,
            null);
        item.InfoHash = "abcdef0123456789abcdef0123456789abcdef01";

        var result = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", 438631, TrackerType.Rutracker, [item], TrackerIngestionReason.Interactive));

        Assert.Equal("hash_mismatch", Assert.Single(result.Errors).Code);
        Assert.Equal(0, result.ObservationsWritten);
        Assert.Equal(0, result.CanonicalTorrentsWritten);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_observations"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrents"));
    }

    [Fact]
    public async Task Missing_magnet_is_rejected_even_when_an_explicit_hash_exists()
    {
        var observations = CreateObservationStore();
        var item = Observation(
            TrackerType.Rutracker,
            "rt-no-magnet",
            "https://rutracker.org/forum/viewtopic.php?t=405",
            "0123456789abcdef0123456789abcdef01234567",
            "Дюна Dune no magnet",
            1,
            DateTimeOffset.UtcNow,
            ["movie"],
            null,
            null);
        item.Magnet = null;

        var result = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", 438631, TrackerType.Rutracker, [item], TrackerIngestionReason.Interactive));

        Assert.Equal("missing_magnet", Assert.Single(result.Errors).Code);
        Assert.Equal(0, result.ObservationsWritten);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_observations"));
    }

    [Fact]
    public async Task Deleting_a_canonical_torrent_cascades_observations_and_media_links()
    {
        var observations = CreateObservationStore();
        const string hash = "3333333333333333333333333333333333333333";
        var item = Observation(
            TrackerType.Rutracker,
            "rt-cascade",
            "https://rutracker.org/forum/viewtopic.php?t=406",
            hash,
            "Дюна Dune cascade",
            1,
            DateTimeOffset.UtcNow,
            ["movie"],
            null,
            null);

        var result = await observations.UpsertAsync(new TorrentPersistRequest(
            "Дюна Dune", 438631, TrackerType.Rutracker, [item], TrackerIngestionReason.Interactive));
        Assert.True(result.Succeeded);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_observations"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_media_links"));

        await connection.ExecuteAsync(
            $"DELETE FROM {DbSchema.SearchEngine}.torrents WHERE info_hash = @Hash",
            new { Hash = hash });

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_observations"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_media_links"));
    }

    [Fact]
    public async Task Successful_refresh_keeps_individual_tracker_errors_for_diagnostics()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""
            INSERT INTO {DbSchema.SearchEngine}.queries (tmdb_id, query)
            VALUES (438631, 'Дюна Dune')
            """);
        var repository = new QueriesRepository(ConnectionString);

        await repository.RecordRefreshAttemptAsync(
            438631,
            true,
            "rutor:transport",
            CancellationToken.None);

        var row = await connection.QuerySingleAsync<RefreshAttemptRow>($"""
            SELECT last_refresh_time AS LastRefreshTime,
                   last_refresh_attempt_at AS LastRefreshAttemptAt,
                   last_refresh_error AS LastRefreshError
            FROM {DbSchema.SearchEngine}.queries
            WHERE tmdb_id = 438631
            """);
        Assert.NotNull(row.LastRefreshTime);
        Assert.NotNull(row.LastRefreshAttemptAt);
        Assert.Equal("rutor:transport", row.LastRefreshError);
    }

    [Fact]
    public async Task Sql_failure_rolls_back_the_entire_tracker_batch()
    {
        var observations = CreateObservationStore();
        var valid = Observation(
            TrackerType.Rutracker,
            "rt-valid",
            "https://rutracker.org/forum/viewtopic.php?t=501",
            "1111111111111111111111111111111111111111",
            "Valid",
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        var invalid = Observation(
            TrackerType.Rutracker,
            "rt-invalid",
            "https://rutracker.org/forum/viewtopic.php?t=502",
            "2222222222222222222222222222222222222222",
            "Invalid",
            -1,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);

        await Assert.ThrowsAsync<PostgresException>(() => observations.UpsertAsync(new TorrentPersistRequest(
            "rollback",
            1,
            TrackerType.Rutracker,
            [valid, invalid],
            TrackerIngestionReason.Refresh)));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrent_observations"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {DbSchema.SearchEngine}.torrents"));
    }

    private TorrentObservationStore CreateObservationStore() =>
        new(ConnectionString, TimeSpan.FromDays(7), Logger.None);

    private TorrentCatalogStore CreateCatalogStore() => new(ConnectionString);

    private static TorrentDetails Observation(
        TrackerType tracker,
        string sourceKey,
        string sourceUrl,
        string hash,
        string title,
        int seeders,
        DateTimeOffset at,
        string[]? types,
        string[]? languages,
        int[]? seasons)
    {
        var fields = TorrentObservedFields.InfoHash |
                     TorrentObservedFields.Magnet |
                     TorrentObservedFields.Title |
                     TorrentObservedFields.Size |
                     TorrentObservedFields.Seeders |
                     TorrentObservedFields.Leechers |
                     TorrentObservedFields.PublishDate |
                     TorrentObservedFields.Names |
                     TorrentObservedFields.ReleaseYear |
                     TorrentObservedFields.Quality |
                     TorrentObservedFields.VideoType |
                     TorrentObservedFields.SourceUpdatedAt;
        if (types is not null) fields |= TorrentObservedFields.Types;
        if (languages is not null) fields |= TorrentObservedFields.Languages;
        if (seasons is not null) fields |= TorrentObservedFields.Seasons;

        var trackerName = tracker == TrackerType.Rutracker ? "rutracker" : "rutor";
        return new TorrentDetails
        {
            TrackerName = trackerName,
            InfoHash = hash,
            Magnet = $"magnet:?xt=urn:btih:{hash}&tr={Uri.EscapeDataString($"https://announce.{trackerName}.test/announce")}",
            Url = sourceUrl,
            Title = title,
            Name = title.Split(' ')[0],
            OriginalName = "Original",
            Size = 7_654_321,
            SizeName = "7.3 MB",
            Sid = seeders,
            Pir = 3,
            CreateTime = at.UtcDateTime,
            UpdateTime = at.UtcDateTime,
            ReleaseYear = 2021,
            Types = types,
            Quality = 1080,
            VideoType = "BDRip",
            Languages = languages?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Voices = new HashSet<string>(["LostFilm"], StringComparer.OrdinalIgnoreCase),
            Seasons = seasons?.ToHashSet(),
            Source = new TorrentSourceSnapshot(
                tracker,
                sourceKey,
                sourceUrl,
                fields | TorrentObservedFields.Voices,
                TrackerDetailsState.Fetched,
                1,
                $"{trackerName}/test",
                JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    parserVersion = $"{trackerName}/test",
                    list = new { seeders },
                    details = new { title },
                    unmapped = new { catalogueCode = "A-42" },
                    warnings = Array.Empty<string>()
                }),
                at,
                at,
                at)
        };
    }

    private sealed class RefreshAttemptRow
    {
        public DateTimeOffset? LastRefreshTime { get; init; }
        public DateTimeOffset? LastRefreshAttemptAt { get; init; }
        public string? LastRefreshError { get; init; }
    }

    private sealed class StoredSource
    {
        public string SourceUrl { get; init; } = string.Empty;
        public string SourcePayload { get; init; } = "{}";
    }
}
