namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Bounded-parallel topic enrichment with partial results: candidates that were
///     enriched before the budget (cancellation token) ran out are still returned —
///     never all-or-nothing.
/// </summary>
public static class DetailEnricher
{
    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    ///     Runs <paramref name="fetchDetails"/> over <paramref name="candidates"/> with at most
    ///     <paramref name="maxConcurrency"/> concurrent topic fetches and returns the candidates
    ///     for which it returned true, in candidate order. A candidate that throws (other than
    ///     caller cancellation) is treated as failed. Caller cancellation stops scheduling and
    ///     returns whatever succeeded so far instead of throwing.
    /// </summary>
    public static async Task<IReadOnlyCollection<TorrentDetails>> EnrichTopicsAsync(
        IReadOnlyCollection<TorrentDetails> candidates,
        Func<TorrentDetails, CancellationToken, Task<bool>> fetchDetails,
        CancellationToken ct,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        if (candidates.Count == 0)
            return [];

        var ordered = candidates as IReadOnlyList<TorrentDetails> ?? candidates.ToArray();
        var enriched = new TorrentDetails?[ordered.Count];
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, ordered.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, maxConcurrency),
                    CancellationToken = ct
                },
                async (index, token) =>
                {
                    try
                    {
                        if (await fetchDetails(ordered[index], token))
                            enriched[index] = ordered[index];
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A failed candidate is simply not enriched.
                    }
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Budget exhausted: fall through to the partial results.
        }

        return enriched.OfType<TorrentDetails>().ToArray();
    }
}
