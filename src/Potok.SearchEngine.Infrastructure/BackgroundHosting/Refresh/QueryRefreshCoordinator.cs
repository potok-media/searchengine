namespace Potok.SearchEngine.Infrastructure.BackgroundHosting.Refresh;

/// <summary>
///     Re-ingests stale subscribed queries and records the outcome on the query row.
///     Every attempt is recorded (last_refresh_attempt_at advances on each try); the query
///     becomes fresh (last_refresh_time) only when at least one enabled tracker returned a
///     structurally valid response (Succeeded / Partial / Empty — an empty result is NOT an
///     error) and its persistence passed. TimedOut/Failed across all trackers = still stale.
///     Per-tracker failures go to last_refresh_error as "tracker:code" pairs; null on full success.
/// </summary>
public sealed class QueryRefreshCoordinator(
    IQueriesRepository queries,
    ITrackerIngestion ingestion)
{
    public async Task RefreshStaleAsync(
        TimeSpan olderThan,
        int limit,
        CancellationToken ct)
    {
        var stale = await queries.GetStaleSearchQueriesAsync(olderThan, limit);
        foreach (var query in stale)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var batches = new List<TrackerIngestionBatch>();
                await foreach (var batch in ingestion.IngestAsync(new TrackerIngestionRequest(
                                   query.Query,
                                   query.TmdbId,
                                   null,
                                   TrackerIngestionReason.Refresh), ct))
                    batches.Add(batch);

                var enabled = batches
                    .Where(x => x.Status != TrackerIngestionStatus.Disabled)
                    .ToArray();
                var succeeded = enabled.Any(IsHealthy);
                var error = BuildError(enabled);
                await queries.RecordRefreshAttemptAsync(query.TmdbId, succeeded, error, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await queries.RecordRefreshAttemptAsync(
                    query.TmdbId,
                    false,
                    $"unexpected:{ex.GetType().Name}",
                    ct);
            }
        }
    }

    private static bool IsHealthy(TrackerIngestionBatch batch) =>
        batch.Status is TrackerIngestionStatus.Succeeded
            or TrackerIngestionStatus.Partial
            or TrackerIngestionStatus.Empty
        && batch.Persistence.Succeeded;

    private static string? BuildError(IReadOnlyCollection<TrackerIngestionBatch> batches)
    {
        if (batches.Count == 0)
            return "no_enabled_trackers";
        var errors = batches
            .Where(x => !IsHealthy(x))
            .Select(x => $"{TrackerCode(x)}:{x.ErrorCode ?? x.Status.ToString().ToLowerInvariant()}")
            .Concat(batches.SelectMany(x => x.Persistence.Errors.Select(error =>
                $"{TrackerCode(x)}:{error.Code}")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (errors.Length > 0)
            return string.Join(',', errors);
        return batches.Any(IsHealthy) ? null : "no_healthy_trackers";
    }

    private static string TrackerCode(TrackerIngestionBatch batch) =>
        batch.Tracker.ToString().ToLowerInvariant();
}
