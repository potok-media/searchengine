using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Details;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Search;
using Serilog;
using Xunit;

namespace Potok.SearchEngine.Tests.Search;

public class TrackerIngestionTests
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Successful_tracker_batch_is_durable_before_it_is_yielded()
    {
        var result = Result(
            "12345",
            $"magnet:?xt=urn:btih:{ValidHash}",
            ValidHash);
        result.Title = "Дюна Dune";
        result.Sid = 41;
        var store = new RecordingObservationStore();
        var ingestion = new TrackerIngestion(
            Options.Create(new Config { RuTracker = { EnableSearch = true } }),
            [new FakeTrackerSearch([result])],
            store,
            Log.Logger);

        var batches = new List<TrackerIngestionBatch>();
        await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                           "Дюна Dune",
                           438631,
                           [TrackerType.Rutracker],
                           TrackerIngestionReason.Interactive)))
        {
            Assert.True(store.Committed);
            batches.Add(batch);
        }

        var persisted = Assert.Single(store.Requests);
        Assert.Equal(438631, persisted.TmdbId);
        Assert.Equal(TrackerType.Rutracker, persisted.Tracker);
        var yielded = Assert.Single(batches);
        Assert.Equal(TrackerIngestionStatus.Succeeded, yielded.Status);
        Assert.True(yielded.Persistence.Succeeded);
        Assert.Same(result, Assert.Single(yielded.Results));
    }

    [Fact]
    public async Task Results_without_a_valid_magnet_identity_are_not_persisted_or_yielded()
    {
        var valid = Result(
            "valid",
            $"magnet:?xt=urn:btih:{ValidHash}",
            ValidHash);
        var missingMagnet = Result("missing", null, ValidHash);
        var invalidMagnet = Result("invalid", "https://example.test/not-a-magnet", ValidHash);
        var mismatch = Result(
            "mismatch",
            $"magnet:?xt=urn:btih:{ValidHash}",
            "abcdef0123456789abcdef0123456789abcdef01");
        var store = new RecordingObservationStore();
        var ingestion = CreateIngestion([valid, missingMagnet, invalidMagnet, mismatch], store);

        var batches = new List<TrackerIngestionBatch>();
        await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                           "Дюна Dune",
                           438631,
                           [TrackerType.Rutracker],
                           TrackerIngestionReason.Interactive)))
            batches.Add(batch);

        var persisted = Assert.Single(store.Requests);
        Assert.Same(valid, Assert.Single(persisted.Observations));
        var yielded = Assert.Single(batches);
        Assert.Equal(TrackerIngestionStatus.Succeeded, yielded.Status);
        Assert.Same(valid, Assert.Single(yielded.Results));
    }

    [Fact]
    public async Task Results_without_a_source_snapshot_are_dropped()
    {
        var withoutSource = Result(
            "no-source",
            $"magnet:?xt=urn:btih:{ValidHash}",
            ValidHash);
        withoutSource.Source = null;
        var store = new RecordingObservationStore();
        var ingestion = CreateIngestion([withoutSource], store);

        var batches = new List<TrackerIngestionBatch>();
        await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                           "Дюна Dune",
                           438631,
                           [TrackerType.Rutracker],
                           TrackerIngestionReason.Interactive)))
            batches.Add(batch);

        Assert.Empty(store.Requests);
        var yielded = Assert.Single(batches);
        Assert.Equal(TrackerIngestionStatus.Empty, yielded.Status);
        Assert.Empty(yielded.Results);
    }

    [Fact]
    public async Task Search_with_only_unplayable_results_is_a_healthy_empty_batch()
    {
        var store = new RecordingObservationStore();
        var ingestion = CreateIngestion([Result("missing", null, ValidHash)], store);

        var batches = new List<TrackerIngestionBatch>();
        await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                           "Дюна Dune",
                           438631,
                           [TrackerType.Rutracker],
                           TrackerIngestionReason.Interactive)))
            batches.Add(batch);

        Assert.Empty(store.Requests);
        var yielded = Assert.Single(batches);
        Assert.Equal(TrackerIngestionStatus.Empty, yielded.Status);
        Assert.Empty(yielded.Results);
        Assert.Null(yielded.ErrorCode);
    }

    [Fact]
    public async Task Detail_failure_does_not_make_a_valid_persisted_batch_partial()
    {
        var store = new RecordingObservationStore();
        var result = Result(
            "valid-list-magnet",
            $"magnet:?xt=urn:btih:{ValidHash}",
            ValidHash,
            TrackerDetailsState.Failed);
        var ingestion = CreateIngestion([result], store);

        TrackerIngestionBatch? yielded = null;
        await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                           "Дюна Dune",
                           438631,
                           [TrackerType.Rutracker],
                           TrackerIngestionReason.Interactive)))
            yielded = batch;

        Assert.NotNull(yielded);
        Assert.Equal(TrackerIngestionStatus.Succeeded, yielded.Status);
        Assert.Same(result, Assert.Single(yielded.Results));
    }

    private static TrackerIngestion CreateIngestion(
        IReadOnlyCollection<TorrentDetails> results,
        ITorrentObservationStore store) =>
        new(
            Options.Create(new Config { RuTracker = { EnableSearch = true } }),
            [new FakeTrackerSearch(results)],
            store,
            Log.Logger);

    private static TorrentDetails Result(
        string key,
        string? magnet,
        string? infoHash,
        TrackerDetailsState detailsState = TrackerDetailsState.Fetched)
    {
        var observedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        return new TorrentDetails
        {
            TrackerName = "rutracker",
            Url = $"https://rutracker.org/forum/viewtopic.php?t={key}",
            Title = key,
            InfoHash = infoHash,
            Magnet = magnet,
            Source = new TorrentSourceSnapshot(
                TrackerType.Rutracker,
                key,
                $"https://rutracker.org/forum/viewtopic.php?t={key}",
                TorrentObservedFields.InfoHash | TorrentObservedFields.Magnet | TorrentObservedFields.Title,
                detailsState,
                1,
                "rutracker/test",
                JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    parserVersion = "rutracker/test",
                    list = new { },
                    details = new { },
                    unmapped = new { },
                    warnings = Array.Empty<string>()
                }),
                observedAt,
                detailsState == TrackerDetailsState.Fetched ? observedAt : null,
                null)
        };
    }

    private sealed class FakeTrackerSearch(IReadOnlyCollection<TorrentDetails> results) : ITrackerSearch
    {
        public TrackerType Tracker => TrackerType.Rutracker;
        public string TrackerName => "rutracker";
        public string Host => "https://rutracker.org/";

        public Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
            string query,
            CancellationToken ct = default) =>
            Task.FromResult(results);
    }

    private sealed class RecordingObservationStore : ITorrentObservationStore
    {
        public List<TorrentPersistRequest> Requests { get; } = [];
        public bool Committed { get; private set; }

        public Task<TorrentPersistResult> UpsertAsync(
            TorrentPersistRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            Committed = true;
            var count = request.Observations.Count;
            return Task.FromResult(new TorrentPersistResult(count, count, count, []));
        }
    }
}
