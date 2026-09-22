namespace Potok.SearchEngine.Core.Models;

/// <summary>
///     One flush of a streaming torrent search. <paramref name="Source"/> is
///     <c>cache</c> for a local/memory hit, otherwise the tracker name.
/// </summary>
public readonly record struct TorrentSearchBatch(
    string Source,
    IReadOnlyCollection<TorrentDetails> Results);
