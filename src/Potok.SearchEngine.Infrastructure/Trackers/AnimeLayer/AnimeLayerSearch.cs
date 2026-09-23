using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;

/// <summary>
///     AnimeLayer search entry point: one list request, typed classification of broken
///     surfaces (auth failures never masquerade as an empty result), then bounded detail
///     enrichment of the playable anime topics.
/// </summary>
public class AnimeLayerSearch : BaseAnimeLayer
{
    public AnimeLayerSearch(
        IOptions<Config> config,
        TrackerHttpClient httpService,
        ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.AnimeLayer.EnableSearch)
            return [];

        var url = $"{Host}/torrents/anime/?q={Uri.EscapeDataString(query)}";
        string html;
        try
        {
            html = await Get(url, url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Transport,
                "AnimeLayer search request failed.", exception);
        }

        var classified = TrackerResponseClassifier.Classify(html) ??
                         (IsAuthenticationResponse(html) ? TrackerSearchErrorCode.Authentication : null);
        if (classified is not null)
        {
            var message = classified.Value switch
            {
                TrackerSearchErrorCode.InvalidResponse => "AnimeLayer returned an empty search response.",
                TrackerSearchErrorCode.Challenge => "AnimeLayer returned a challenge page.",
                TrackerSearchErrorCode.Authentication => "AnimeLayer search requires authentication.",
                _ => "AnimeLayer search response was rejected."
            };
            throw new TrackerSearchException(Tracker, classified.Value, message);
        }
        if (!IsSearchSurface(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.ParserContract,
                "AnimeLayer search response did not contain the torrent result surface.");

        var torrents = ParseSearchPage(html);
        if (torrents.Count == 0)
            return [];
        return await EnrichVideoTopicsAsync(torrents, ct);
    }
}
