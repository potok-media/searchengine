using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Kinozal adapter core: identity members and bounded topic+service enrichment.
///     Cookie auth lives in BaseKinozal.Auth.cs, browse-list parsing in BaseKinozal.List.cs,
///     topic/service detail extraction in BaseKinozal.Detail.cs and fact scrubbing in
///     BaseKinozal.Facts.cs.
/// </summary>
public partial class BaseKinozal : BaseTrackerSearch
{
    private const string CookieKey = "kinozal:cookie";
    private const string ParserVersion = "kinozal/2026-09-22";
    private readonly HtmlParser _parser = new();
    private readonly SessionCookieStore _sessionCookies;

    protected BaseKinozal(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
        _sessionCookies = new SessionCookieStore(
            cacheService,
            Config.Cache.Enable,
            TimeSpan.FromDays(Config.Cache.AuthExpiry),
            CookieKey);
    }

    public override TrackerType Tracker => TrackerType.Kinozal;
    public override string TrackerName => "kinozal";
    public override string Host => "https://kinozal.guru";

    /// <summary>
    ///     Enriches every unique candidate (deduped by source key, most popular first) with
    ///     topic + service details; only successfully enriched topics are returned.
    /// </summary>
    protected Task<IReadOnlyCollection<TorrentDetails>> EnrichVideoTopicsAsync(
        IReadOnlyCollection<TorrentDetails> torrents,
        CancellationToken ct)
    {
        var candidates = torrents
            .OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .GroupBy(torrent => torrent.Source?.SourceKey ?? torrent.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return DetailEnricher.EnrichTopicsAsync(candidates, FetchDetailsAsync, ct);
    }
}
