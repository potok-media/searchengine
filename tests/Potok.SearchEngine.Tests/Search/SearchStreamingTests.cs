using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Api;
using Potok.SearchEngine.Core.Models.Details;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Cache;
using Potok.SearchEngine.Infrastructure.Search;
using Potok.SearchEngine.Tests.Helpers;
using Serilog;
using Xunit;

namespace Potok.SearchEngine.Tests.Search;

public class SearchStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forced_refresh_waits_for_tracker_batches_without_emitting_saved_results(bool primeMemoryCache)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var local = new LocalSearch([Result("Saved release", 'a')]);
        var ingestion = new ControlledIngestion();
        var service = CreateService(memory, local, ingestion);
        if (primeMemoryCache)
            Assert.Single(await CollectAsync(service.SearchTorrentsStreamAsync(Query(force: false))));

        await using var results = service.SearchTorrentsStreamAsync(Query(force: true)).GetAsyncEnumerator();
        var first = results.MoveNextAsync().AsTask();
        Assert.False(first.IsCompleted);

        // The same release must arrive with its fresh metadata, not be pre-filled from the catalog.
        var fresh = Result("Updated release", 'a');
        await ingestion.Batches.Writer.WriteAsync(Batch(TrackerType.Rutracker, fresh));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Rutracker", results.Current.Source);
        Assert.Same(fresh, Assert.Single(results.Current.Results));
        Assert.False(ingestion.Completed);

        var second = results.MoveNextAsync().AsTask();
        Assert.False(second.IsCompleted);
        var another = Result("Another release", 'b');
        await ingestion.Batches.Writer.WriteAsync(Batch(TrackerType.Rutor, another));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Rutor", results.Current.Source);
        Assert.Same(another, Assert.Single(results.Current.Results));
        Assert.False(ingestion.Completed);

        var completion = results.MoveNextAsync().AsTask();
        Assert.False(completion.IsCompleted);
        ingestion.Batches.Writer.Complete();
        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(ingestion.Completed);
    }

    [Fact]
    public async Task Empty_forced_refresh_does_not_fall_back_to_saved_results()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var local = new LocalSearch([Result("Saved release", 'a')]);
        var ingestion = new ControlledIngestion();
        ingestion.Batches.Writer.Complete();
        var service = CreateService(memory, local, ingestion);

        var batches = await CollectAsync(service.SearchTorrentsStreamAsync(Query(force: true)));

        Assert.Empty(batches);
        Assert.Equal(1, ingestion.Calls);
        Assert.True(ingestion.Completed);
    }

    [Fact]
    public async Task Normal_search_uses_saved_results_and_then_memory_cache_without_querying_trackers()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var local = new LocalSearch([Result("Saved release", 'a')]);
        var ingestion = new ControlledIngestion();
        ingestion.Batches.Writer.Complete();
        var service = CreateService(memory, local, ingestion);

        var localBatch = Assert.Single(await CollectAsync(service.SearchTorrentsStreamAsync(Query(force: false))));
        Assert.Equal("cache", localBatch.Source);
        Assert.Equal("Saved release", Assert.Single(localBatch.Results).Title);

        local.Results = [];
        var memoryBatch = Assert.Single(await CollectAsync(service.SearchTorrentsStreamAsync(Query(force: false))));
        Assert.Equal("cache", memoryBatch.Source);
        Assert.Equal("Saved release", Assert.Single(memoryBatch.Results).Title);
        Assert.Equal(1, local.Calls);
        Assert.Equal(0, ingestion.Calls);
    }

    private static SearchService CreateService(MemoryCache memory, LocalSearch local, ControlledIngestion ingestion)
    {
        var config = Options.Create(new Config { Cache = { Enable = true, Expiry = 10 } });
        return new SearchService(
            config,
            TrackerTestClients.CreateHttpClient(new ScriptedHttpMessageHandler()),
            new CacheService(memory, config),
            local,
            new TorrentMergerService(),
            new MediaResolver(),
            ingestion,
            [],
            Log.Logger);
    }

    private static TorrentSearchQuery Query(bool force) => new()
    {
        TmdbId = 85937,
        Title = "Demon Slayer",
        TitleOriginal = "Kimetsu no Yaiba",
        ForceSearch = force
    };

    private static TorrentDetails Result(string title, char hashCharacter) => new()
    {
        Title = title,
        TrackerName = "rutracker",
        InfoHash = new string(hashCharacter, 40),
        Magnet = $"magnet:?xt=urn:btih:{new string(hashCharacter, 40)}"
    };

    private static TrackerIngestionBatch Batch(TrackerType tracker, TorrentDetails result) =>
        new(tracker, TrackerIngestionStatus.Succeeded, [result], TorrentPersistResult.Empty, null);

    private static async Task<List<TorrentSearchBatch>> CollectAsync(IAsyncEnumerable<TorrentSearchBatch> stream)
    {
        var batches = new List<TorrentSearchBatch>();
        await foreach (var batch in stream)
            batches.Add(batch);
        return batches;
    }

    private sealed class LocalSearch(List<TorrentDetails> results) : ILocalSearchService
    {
        public List<TorrentDetails> Results { get; set; } = results;
        public int Calls { get; private set; }

        public Task<List<TorrentDetails>> SearchByTmdbIdAsync(long tmdbId)
        {
            Calls++;
            return Task.FromResult(Results);
        }

        public Task<List<TorrentDetails>> SearchByTitleAsync(
            string? title, string? originalTitle, int? year = null, int? mediaType = null, bool exact = false) =>
            throw new NotSupportedException();

        public Task<List<TorrentDetails>> SearchByQueryAsync(string? query, int? mediaType = null, bool exact = false) =>
            throw new NotSupportedException();
    }

    private sealed class MediaResolver : IMediaResolverService
    {
        public Task<(string? search, string? altname)> ResolveKpImdb(string? search, string? altname) =>
            Task.FromResult((search, altname));
    }

    private sealed class ControlledIngestion : ITrackerIngestion
    {
        public Channel<TrackerIngestionBatch> Batches { get; } = Channel.CreateUnbounded<TrackerIngestionBatch>();
        public int Calls { get; private set; }
        public bool Completed { get; private set; }

        public async IAsyncEnumerable<TrackerIngestionBatch> IngestAsync(
            TrackerIngestionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await foreach (var batch in Batches.Reader.ReadAllAsync(ct))
                yield return batch;
            Completed = true;
        }
    }
}
