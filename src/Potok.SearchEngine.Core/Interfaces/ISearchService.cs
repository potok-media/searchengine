namespace Potok.SearchEngine.Core.Interfaces;

public interface ISearchService
{
    /// <summary>
    ///     Универсальный поиск торрентов.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchTorrentsAsync(
        TorrentSearchQuery request,
        CancellationToken ct = default);

    /// <summary>
    ///     Same search as <see cref="SearchTorrentsAsync"/>, yielding local/cache
    ///     hits immediately and each tracker as it finishes.
    /// </summary>
    IAsyncEnumerable<TorrentSearchBatch> SearchTorrentsStreamAsync(
        TorrentSearchQuery request,
        CancellationToken ct = default);
}
