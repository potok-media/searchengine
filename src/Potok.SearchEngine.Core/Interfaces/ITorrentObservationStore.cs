namespace Potok.SearchEngine.Core.Interfaces;

/// <summary>
///     Write-side of the torrent catalog: persists tracker observations emitted by
///     <see cref="ITrackerIngestion"/> into the canonical store.
/// </summary>
public interface ITorrentObservationStore
{
    Task<TorrentPersistResult> UpsertAsync(
        TorrentPersistRequest request,
        CancellationToken ct = default);
}
