using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Single browse.php search surface; every topic candidate is enriched with details.
///     Auth/challenge/contract failures surface as typed <see cref="TrackerSearchException"/>s.
/// </summary>
public class KinozalSearch : BaseKinozal
{
    public KinozalSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.Kinozal.EnableSearch)
            return [];

        var url = $"{Host}/browse.php?s={EncodeQuery(query)}&g=0&c=0&v=0&d=0&w=0&t=1&f=0";

        string html;
        try
        {
            html = await Get(url, RuEncoding, ct);
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
                "Kinozal search request failed.", exception);
        }

        if (string.IsNullOrWhiteSpace(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.InvalidResponse,
                "Kinozal returned an empty search response.");
        if (IsChallengeResponse(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Challenge,
                "Kinozal returned a challenge page.");
        if (IsAuthenticationResponse(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Authentication,
                "Kinozal search requires authentication.");
        if (!IsSearchSurface(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.ParserContract,
                "Kinozal search response did not contain the browse result surface.");

        var results = ParseBrowsePage(html, Host);
        if (results.Count == 0)
            return [];

        return await EnrichVideoTopicsAsync(results, ct);
    }
}
