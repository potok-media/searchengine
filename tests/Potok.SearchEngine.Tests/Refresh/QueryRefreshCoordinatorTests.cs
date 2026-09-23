using System.Runtime.CompilerServices;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Details;
using Potok.SearchEngine.Infrastructure.BackgroundHosting.Refresh;
using Xunit;

namespace Potok.SearchEngine.Tests.Refresh;

public class QueryRefreshCoordinatorTests
{
    [Fact]
    public async Task Successful_refresh_marks_query_fresh_and_records_attempt()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Атака титанов Attack on Titan", TmdbId = 1429 }]);
        var ingestion = new RecordingIngestion(new TorrentPersistResult(1, 1, 1, []));
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var request = Assert.Single(ingestion.Requests);
        Assert.Equal(1429, request.TmdbId);
        Assert.Equal(TrackerIngestionReason.Refresh, request.Reason);
        var attempt = Assert.Single(queries.Attempts);
        Assert.Equal(1429, attempt.TmdbId);
        Assert.True(attempt.Succeeded);
        Assert.Null(attempt.Error);
    }

    [Fact]
    public async Task Attempt_is_recorded_even_when_ingestion_throws()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Дюна Dune", TmdbId = 438631 }]);
        var ingestion = new ThrowingIngestion();
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.Equal(438631, attempt.TmdbId);
        Assert.False(attempt.Succeeded);
        Assert.Equal("unexpected:InvalidOperationException", attempt.Error);
    }

    [Fact]
    public async Task Empty_tracker_response_is_healthy_and_advances_freshness()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Унесенные призраками Spirited Away", TmdbId = 129 }]);
        var ingestion = new RecordingIngestion(TorrentPersistResult.Empty, TrackerIngestionStatus.Empty);
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Null(attempt.Error);
    }

    [Fact]
    public async Task Failed_tracker_does_not_block_healthy_tracker_and_its_error_is_retained()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Дюна Dune", TmdbId = 438631 }]);
        var ingestion = new RecordingIngestion(
            Batch(
                TrackerType.Rutracker,
                TrackerIngestionStatus.Succeeded,
                new TorrentPersistResult(1, 1, 1, [])),
            Batch(
                TrackerType.Rutor,
                TrackerIngestionStatus.Failed,
                TorrentPersistResult.Empty,
                "transport"));
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal("rutor:transport", attempt.Error);
    }

    [Fact]
    public async Task All_failed_trackers_leave_query_stale_and_record_errors()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Дюна Dune", TmdbId = 438631 }]);
        var ingestion = new RecordingIngestion(
            Batch(
                TrackerType.Rutracker,
                TrackerIngestionStatus.TimedOut,
                TorrentPersistResult.Empty,
                "timeout"),
            Batch(
                TrackerType.Rutor,
                TrackerIngestionStatus.Failed,
                TorrentPersistResult.Empty,
                "authentication"));
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Equal("rutor:authentication,rutracker:timeout", attempt.Error);
    }

    [Fact]
    public async Task Partial_response_with_clean_persistence_is_fresh_even_without_writes()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Дюна Dune", TmdbId = 438631 }]);
        var ingestion = new RecordingIngestion(Batch(
            TrackerType.Rutracker,
            TrackerIngestionStatus.Partial,
            TorrentPersistResult.Empty));
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Null(attempt.Error);
    }

    [Fact]
    public async Task Persistence_failure_marks_attempt_unsuccessful_and_keeps_error()
    {
        var queries = new RecordingQueriesRepository(
            [new StaleQuery { Query = "Дюна Dune", TmdbId = 438631 }]);
        var ingestion = new RecordingIngestion(Batch(
            TrackerType.Rutracker,
            TrackerIngestionStatus.Succeeded,
            new TorrentPersistResult(
                1,
                1,
                1,
                [new TorrentPersistError("invalid_source", "rutracker", "broken", "broken")])));
        var coordinator = new QueryRefreshCoordinator(queries, ingestion);

        await coordinator.RefreshStaleAsync(TimeSpan.FromHours(2), 10, CancellationToken.None);

        var attempt = Assert.Single(queries.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Equal("rutracker:invalid_source,rutracker:succeeded", attempt.Error);
    }

    private static TrackerIngestionBatch Batch(
        TrackerType tracker,
        TrackerIngestionStatus status,
        TorrentPersistResult persistence,
        string? error = null) =>
        new(
            tracker,
            status,
            status is TrackerIngestionStatus.Succeeded or TrackerIngestionStatus.Partial
                ? [new TorrentDetails()]
                : [],
            persistence,
            error);

    private sealed class RecordingIngestion : ITrackerIngestion
    {
        private readonly IReadOnlyCollection<TrackerIngestionBatch> _batches;

        public RecordingIngestion(
            TorrentPersistResult persistence,
            TrackerIngestionStatus status = TrackerIngestionStatus.Succeeded)
            : this(Batch(
                TrackerType.Rutracker,
                status,
                persistence,
                status == TrackerIngestionStatus.Empty ? "empty" : null))
        {
        }

        public RecordingIngestion(params TrackerIngestionBatch[] batches)
        {
            _batches = batches;
        }

        public List<TrackerIngestionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<TrackerIngestionBatch> IngestAsync(
            TrackerIngestionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var batch in _batches)
            {
                await Task.Yield();
                yield return batch;
            }
        }
    }

    private sealed class ThrowingIngestion : ITrackerIngestion
    {
        public async IAsyncEnumerable<TrackerIngestionBatch> IngestAsync(
            TrackerIngestionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("ingestion blew up");
            #pragma warning disable CS0162 // unreachable — required to keep the async iterator shape
            yield break;
            #pragma warning restore CS0162
        }
    }

    private sealed class RecordingQueriesRepository(IReadOnlyCollection<StaleQuery> stale) : IQueriesRepository
    {
        public List<(long TmdbId, bool Succeeded, string? Error)> Attempts { get; } = [];

        public Task<IReadOnlyCollection<StaleQuery>> GetStaleSearchQueriesAsync(TimeSpan olderThan, int limit) =>
            Task.FromResult(stale);

        public Task RecordRefreshAttemptAsync(
            long tmdbId,
            bool succeeded,
            string? error,
            CancellationToken ct = default)
        {
            Attempts.Add((tmdbId, succeeded, error));
            return Task.CompletedTask;
        }

        public Task TrackSearchQueryAsync(long tmdbId, string query) => Task.CompletedTask;
        public Task RemoveQueryIfNoSubscriptionsAsync(long tmdbId) => Task.CompletedTask;
        public Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid) =>
            Task.FromResult<IReadOnlyCollection<UserSubscriptionItem>>([]);
    }
}
