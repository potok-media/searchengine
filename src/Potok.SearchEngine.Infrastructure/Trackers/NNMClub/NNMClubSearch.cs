using System.Text;
using System.Web;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     Single POST to tracker.php with the windows-1251 form body, then bounded-parallel
///     detail enrichment for allowed video topics. Every failure mode surfaces as a typed
///     <see cref="TrackerSearchException"/>; caller cancellation is never wrapped.
/// </summary>
public class NNMClubSearch : BaseNNMClub
{
    public NNMClubSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.NNMClub.EnableSearch)
            return [];

        var parameters = GetSearchParameters(query);
        var url = $"{Host}/forum/tracker.php";

        var pairs = parameters.Select(kv =>
            $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value, RuEncoding)}");
        var formEncoded = string.Join("&", pairs);

        var content = new StringContent(formEncoded, Encoding.UTF8, "application/x-www-form-urlencoded");

        string html;
        try
        {
            html = await HttpService.PostStringAsync(url, content, null, url, RuEncoding, true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Transport,
                "NNMClub search request failed.", exception);
        }

        var classified = TrackerResponseClassifier.Classify(html);
        if (classified is not null)
        {
            var message = classified.Value switch
            {
                TrackerSearchErrorCode.InvalidResponse => "NNMClub returned an empty search response.",
                TrackerSearchErrorCode.Challenge => "NNMClub returned a challenge page.",
                _ => "NNMClub search requires authentication."
            };
            throw new TrackerSearchException(Tracker, classified.Value, message);
        }
        if (IsAuthenticationResponse(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Authentication,
                "NNMClub search requires authentication.");
        if (!IsSearchSurface(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.ParserContract,
                "NNMClub search response did not contain the tracker result surface.");

        var torrents = ParseTrackerPage(html);
        return await EnrichVideoTopicsAsync(torrents, ct);
    }
}
