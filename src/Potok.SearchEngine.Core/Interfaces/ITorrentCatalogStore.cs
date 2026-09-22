namespace Potok.SearchEngine.Core.Interfaces;

/// <summary>
///     Read-side of the torrent catalog: queries canonical torrents built from
///     persisted tracker observations.
/// </summary>
public interface ITorrentCatalogStore
{
    Task<IReadOnlyList<TorrentDetails>> SearchAsync(
        TorrentCatalogQuery query,
        CancellationToken ct = default);
}
