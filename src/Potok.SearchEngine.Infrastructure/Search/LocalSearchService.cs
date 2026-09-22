using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;

namespace Potok.SearchEngine.Infrastructure.Search;

public class LocalSearchService : BaseSearchService, ILocalSearchService
{
    private readonly ITorrentCatalogStore _catalog;

    public LocalSearchService(
        IOptions<Config> config,
        TrackerHttpClient httpService,
        ICacheService cacheService,
        ITorrentCatalogStore catalog) : base(config.Value, httpService, cacheService)
    {
        _catalog = catalog;
    }

    public async Task<List<TorrentDetails>> SearchByTitleAsync(
        string? title,
        string? originalTitle,
        int? year = null,
        int? mediaType = null,
        bool exact = false)
    {
        // For now, we prefer SearchByTmdbId.
        // If only title is provided, we use a basic ILIKE search.
        if (string.IsNullOrWhiteSpace(title)) return [];

        return (await _catalog.SearchAsync(new TorrentCatalogQuery(Title: title))).ToList();
    }

    public async Task<List<TorrentDetails>> SearchByQueryAsync(string? query, int? mediaType = null, bool exact = false)
    {
        return await SearchByTitleAsync(query, null);
    }

    public async Task<List<TorrentDetails>> SearchByTmdbIdAsync(long tmdbId)
    {
        return (await _catalog.SearchAsync(new TorrentCatalogQuery(TmdbId: tmdbId))).ToList();
    }
}
