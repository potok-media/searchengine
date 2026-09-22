using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Potok.SearchEngine.Core.Enums;
using Serilog;

namespace Potok.SearchEngine.Infrastructure.Search;

public sealed class TrackerIngestion : ITrackerIngestion
{
    internal const int TrackerSearchTimeoutSeconds = 20;

    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<TrackerType, ITrackerSearch> _trackers;
    private readonly ITorrentObservationStore _store;
    private static readonly Dictionary<TrackerType, ResiliencePipeline<IReadOnlyCollection<TorrentDetails>>> Pipelines = [];
    private static readonly Lock PipelineLock = new();

    public TrackerIngestion(
        IOptions<Config> config,
        IEnumerable<ITrackerSearch> trackers,
        ITorrentObservationStore store,
        ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
        _store = store;
        _trackers = trackers.ToDictionary(x => x.Tracker);
    }

    public async IAsyncEnumerable<TrackerIngestionBatch> IngestAsync(
        TrackerIngestionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var selected = (request.Trackers is { Count: > 0 }
                ? request.Trackers
                : _trackers.Keys)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var pending = selected
            .Select(tracker => IngestTrackerAsync(tracker, request, ct))
            .ToList();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            yield return await completed;
        }
    }

    private async Task<TrackerIngestionBatch> IngestTrackerAsync(
        TrackerType tracker,
        TrackerIngestionRequest request,
        CancellationToken callerToken)
    {
        var stopwatch = Stopwatch.StartNew();

        TrackerIngestionBatch Complete(
            TrackerIngestionStatus status,
            string? errorCode = null,
            IReadOnlyCollection<TorrentDetails>? results = null,
            TorrentPersistResult? persistence = null)
        {
            stopwatch.Stop();
            var batch = new TrackerIngestionBatch(
                tracker,
                status,
                results ?? [],
                persistence ?? TorrentPersistResult.Empty,
                errorCode);
            _logger.Information(
                "Tracker {Tracker} finished {Status} {ErrorCode} results={Count} elapsed={ElapsedMs}ms",
                tracker,
                status,
                errorCode ?? "none",
                batch.Results.Count,
                stopwatch.ElapsedMilliseconds);
            return batch;
        }

        if (!_trackers.TryGetValue(tracker, out var adapter) || !tracker.IsSearchEnabled(_config))
            return Complete(TrackerIngestionStatus.Disabled);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        budget.CancelAfter(TimeSpan.FromSeconds(TrackerSearchTimeoutSeconds));

        IReadOnlyCollection<TorrentDetails> results;
        try
        {
            results = await GetPipeline(tracker).ExecuteAsync(
                token => new ValueTask<IReadOnlyCollection<TorrentDetails>>(
                    adapter.SearchAsync(request.Query, token)),
                budget.Token);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(
                "Tracker {Tracker} hit the {Timeout}s search budget",
                tracker,
                TrackerSearchTimeoutSeconds);
            return Complete(TrackerIngestionStatus.TimedOut, "timeout");
        }
        catch (TrackerSearchException ex)
        {
            _logger.Warning(ex, "Tracker {Tracker} failed with {ErrorCode}", tracker, ex.Code);
            return Complete(TrackerIngestionStatus.Failed, ToCode(ex.Code));
        }
        catch (BrokenCircuitException)
        {
            _logger.Warning("Circuit is OPEN for tracker {Tracker}. Search skipped.", tracker);
            return Complete(TrackerIngestionStatus.Failed, "circuit_open");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Tracker {Tracker} failed", tracker);
            return Complete(TrackerIngestionStatus.Failed, "unexpected");
        }

        if (results.Count == 0)
            return Complete(TrackerIngestionStatus.Empty);

        var playableResults = new List<TorrentDetails>(results.Count);
        foreach (var result in results)
        {
            if (result.Source is null)
            {
                _logger.Warning(
                    "Tracker {Tracker} adapter returned result {Url} without a source snapshot; dropping it",
                    tracker,
                    result.Url);
                continue;
            }

            if (!TorrentIdentityResolver.TryResolve(
                    result,
                    out var normalizedHash,
                    out var errorCode,
                    out _))
            {
                _logger.Debug(
                    "Skipping unplayable result {SourceKey} from {Tracker}: {ErrorCode}",
                    result.Source.SourceKey,
                    tracker,
                    errorCode);
                continue;
            }

            result.InfoHash = normalizedHash;
            playableResults.Add(result);
        }

        if (playableResults.Count == 0)
            return Complete(TrackerIngestionStatus.Empty);

        TorrentPersistResult persistence;
        try
        {
            persistence = await _store.UpsertAsync(
                new TorrentPersistRequest(
                    request.Query,
                    request.TmdbId,
                    tracker,
                    playableResults,
                    request.Reason),
                callerToken);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Persistence failed for tracker {Tracker}", tracker);
            return Complete(TrackerIngestionStatus.Failed, "persistence");
        }

        if (persistence.Errors.Count == 0)
            return Complete(TrackerIngestionStatus.Succeeded, null, playableResults, persistence);

        var rejectedSourceKeys = persistence.Errors
            .Select(x => x.SourceKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
        var persistedResults = playableResults
            .Where(x => x.Source is not null && !rejectedSourceKeys.Contains(x.Source.SourceKey))
            .ToArray();
        var partiallyPersisted = persistence.ObservationsWritten > 0 && persistedResults.Length > 0;
        return Complete(
            partiallyPersisted ? TrackerIngestionStatus.Partial : TrackerIngestionStatus.Failed,
            persistence.Errors.FirstOrDefault()?.Code,
            partiallyPersisted ? persistedResults : null,
            persistence);
    }

    private static string ToCode(TrackerSearchErrorCode code) =>
        code.ToString().ToLowerInvariant();

    private ResiliencePipeline<IReadOnlyCollection<TorrentDetails>> GetPipeline(TrackerType tracker)
    {
        lock (PipelineLock)
        {
            if (Pipelines.TryGetValue(tracker, out var pipeline))
                return pipeline;
            pipeline = new ResiliencePipelineBuilder<IReadOnlyCollection<TorrentDetails>>()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<IReadOnlyCollection<TorrentDetails>>
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 3,
                    BreakDuration = TimeSpan.FromMinutes(1),
                    ShouldHandle = new PredicateBuilder<IReadOnlyCollection<TorrentDetails>>()
                        .Handle<TrackerSearchException>(),
                    OnOpened = _ =>
                    {
                        _logger.Error("Circuit Breaker OPENED for {Tracker} for 1 minute", tracker);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        _logger.Information("Circuit Breaker CLOSED for {Tracker}", tracker);
                        return default;
                    }
                })
                .Build();
            Pipelines.Add(tracker, pipeline);
            return pipeline;
        }
    }
}
