using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     Single tracker.php search (win-1251) followed by bounded-parallel topic enrichment.
///     Search-surface failures are typed: Transport on request failure, InvalidResponse on
///     an empty body, Challenge on an interstitial, Authentication when the session cannot
///     be re-established (thrown by <see cref="BaseRuTracker.Get"/>).
/// </summary>
public sealed class RuTrackerSearch : BaseRuTracker
{
    public RuTrackerSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.RuTracker.EnableSearch)
            return [];

        var url = BuildQueryUrl(Host, query, 0);

        string html;
        try
        {
            html = await Get(url, RuEncoding, url, true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TrackerSearchException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Transport,
                "RuTracker search request failed.", exception);
        }

        // Authentication is already enforced by Get (signed-in marker + re-login); here we
        // only reject empty bodies and challenge interstitials.
        var classified = TrackerResponseClassifier.Classify(html);
        if (classified is TrackerSearchErrorCode.InvalidResponse or TrackerSearchErrorCode.Challenge)
            throw new TrackerSearchException(Tracker, classified.Value,
                $"RuTracker search surface is unusable ({classified.Value}).");

        var parsed = ParseForumPage(html, string.Empty, Host, DateTime.UtcNow);
        if (parsed.Count == 0)
            return [];

        var results = new Dictionary<string, TorrentDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsed)
            results[item.Url] = item;

        return await EnrichVideoTopicsAsync(results.Values.ToList(), ct);
    }
}
