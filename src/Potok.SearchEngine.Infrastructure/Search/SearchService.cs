using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Serilog;

namespace Potok.SearchEngine.Infrastructure.Search;

public class SearchService : BaseSearchService, ISearchService
{
    private readonly ILocalSearchService _localSearch;
    private readonly ITorrentMergerService _merger;
    private readonly ITrackerIngestion _ingestion;
    private readonly IMediaResolverService _mediaResolver;
    private readonly IReadOnlyCollection<TrackerType> _supportedTrackers;
    private readonly ILogger _logger;
    private readonly Config _config;

    public SearchService(
        IOptions<Config> config,
        TrackerHttpClient httpService,
        ICacheService cacheService,
        ILocalSearchService localSearch,
        ITorrentMergerService merger,
        IMediaResolverService mediaResolver,
        ITrackerIngestion ingestion,
        IEnumerable<ITrackerSearch> trackers,
        ILogger logger) : base(config.Value, httpService, cacheService)
    {
        _localSearch = localSearch;
        _merger = merger;
        _mediaResolver = mediaResolver;
        _ingestion = ingestion;
        _supportedTrackers = trackers.Select(x => x.Tracker).Distinct().OrderBy(x => x).ToArray();
        _logger = logger;
        _config = config.Value;
    }

    public async Task<IReadOnlyCollection<TorrentDetails>> SearchTorrentsAsync(
        TorrentSearchQuery request,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(request);

        if (request.ForceSearch)
        {
            var torrents = await ExecuteUnifiedSearch(request, ct);
            await CacheService.SetAsync(cacheKey, torrents, TimeSpan.FromMinutes(_config.Cache.Expiry));
            return torrents;
        }

        return await CacheService.GetOrCreateAsync(cacheKey, async () =>
        {
            var torrents = await ExecuteUnifiedSearch(request, ct);
            return (IReadOnlyCollection<TorrentDetails>)torrents;
        }, TimeSpan.FromMinutes(_config.Cache.Expiry));
    }

    public async IAsyncEnumerable<TorrentSearchBatch> SearchTorrentsStreamAsync(
        TorrentSearchQuery request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(request);
        var expiry = TimeSpan.FromMinutes(_config.Cache.Expiry);

        if (!request.ForceSearch &&
            CacheService.TryGetValue(cacheKey, out IReadOnlyCollection<TorrentDetails>? cached) &&
            cached != null)
        {
            yield return new TorrentSearchBatch("cache", cached);
            yield break;
        }

        List<TorrentDetails> local = [];
        if (request.TmdbId.HasValue)
            local = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);

        List<TorrentDetails>? localFinal = null;
        if (local.Count > 0)
        {
            localFinal = await MergeFilterSortAsync(local, request);
            yield return new TorrentSearchBatch("cache", localFinal);
        }

        if (local.Count > 0 && !request.ForceSearch)
        {
            await CacheService.SetAsync(cacheKey, localFinal!, expiry);
            yield break;
        }

        var trackerQuery = await BuildTrackerQueryAsync(request);
        if (string.IsNullOrWhiteSpace(trackerQuery))
        {
            var finalized = await MergeFilterSortAsync(local, request);
            await CacheService.SetAsync(cacheKey, finalized, expiry);
            yield break;
        }

        var fetched = new List<TorrentDetails>();
        await foreach (var batch in IngestTrackersAsync(trackerQuery, request.TmdbId, ct))
        {
            fetched.AddRange(batch.Results);

            var filtered = ApplyFilters(
                batch.Results,
                request.Type,
                request.Tracker,
                request.IsSerial == 2 ? 0 : request.Year,
                request.Quality,
                request.VideoType,
                request.Voice,
                request.Season).ToList();

            if (filtered.Count > 0)
                yield return new TorrentSearchBatch(batch.Tracker.ToString(), filtered);
        }

        if (fetched.Count > 0)
        {
            if (request.TmdbId.HasValue)
                local = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);
            else
                local = await _localSearch.SearchByQueryAsync(trackerQuery);
        }

        await CacheService.SetAsync(cacheKey, await MergeFilterSortAsync(local, request), expiry);
    }

    private async Task<List<TorrentDetails>> ExecuteUnifiedSearch(
        TorrentSearchQuery request,
        CancellationToken ct)
    {
        List<TorrentDetails> torrents = new();

        // 1. Try Local Search by TmdbId first
        if (request.TmdbId.HasValue)
        {
            torrents = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);
        }

        // 2. If no results or ForceSearch, do Remote Search
        if (torrents.Count == 0 || request.ForceSearch)
        {
            var (search, altname) = await _mediaResolver.ResolveKpImdb(request.Title, request.TitleOriginal);
            var trackerQuery = StringConvert.ClearTitle($"{search} {altname}".Trim());

            if (!string.IsNullOrWhiteSpace(trackerQuery))
            {
                await foreach (var batch in IngestTrackersAsync(trackerQuery, request.TmdbId, ct))
                {
                    // Drain the ingestion; results are re-read from the catalog below.
                }

                // Re-fetch from local to get structured data
                if (request.TmdbId.HasValue)
                    torrents = await _localSearch.SearchByTmdbIdAsync(request.TmdbId.Value);
                else
                    torrents = await _localSearch.SearchByQueryAsync(trackerQuery);
            }
        }

        return await MergeFilterSortAsync(torrents, request);
    }

    private IAsyncEnumerable<TrackerIngestionBatch> IngestTrackersAsync(
        string trackerQuery,
        long? tmdbId,
        CancellationToken ct)
    {
        _logger.Information("Search '{Query}' on {Trackers} trackers", trackerQuery, _supportedTrackers);
        return _ingestion.IngestAsync(new TrackerIngestionRequest(
            trackerQuery,
            tmdbId,
            _supportedTrackers,
            TrackerIngestionReason.Interactive), ct);
    }

    private async Task<List<TorrentDetails>> MergeFilterSortAsync(
        IEnumerable<TorrentDetails> torrents,
        TorrentSearchQuery request)
    {
        var merged = await _merger.MergeAsync(torrents);
        var filtered = ApplyFilters(
            merged,
            request.Type,
            request.Tracker,
            request.IsSerial == 2 ? 0 : request.Year,
            request.Quality,
            request.VideoType,
            request.Voice,
            request.Season);
        return ApplySort(filtered, request.Sort).ToList();
    }

    private async Task<string> BuildTrackerQueryAsync(TorrentSearchQuery request)
    {
        var (search, altname) = await _mediaResolver.ResolveKpImdb(request.Title, request.TitleOriginal);
        return StringConvert.ClearTitle($"{search} {altname}".Trim());
    }

    private static string BuildCacheKey(TorrentSearchQuery request) =>
        CacheKeyBuilder.Build(
            "api",
            "v1.0",
            "torrents",
            request.TmdbId?.ToString() ?? "null",
            request.Query ?? "null",
            request.Type ?? "null",
            request.Year.ToString());
}
