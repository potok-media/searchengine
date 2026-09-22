using System.Text;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTor;

/// <summary>
///     One search request per video category; detail fetches only for the most popular
///     candidate. Every surfaced category that fails contributes its error code, and the
///     search throws a typed <see cref="TrackerSearchException"/> only when all fail.
/// </summary>
public class RuTorSearch : BaseRuTor
{
    public RuTorSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.RuTor.EnableSearch)
            return [];

        var results = new Dictionary<string, TorrentDetails>(StringComparer.Ordinal);
        var errors = new List<TrackerSearchErrorCode>();
        var successfulSurfaces = 0;
        foreach (var category in VideoCategories)
        {
            ct.ThrowIfCancellationRequested();
            var url = BuildSearchUrl(category, query);
            string html;
            try
            {
                html = await HttpService.GetStringAsync(url, referer: url, encoding: Encoding.UTF8, ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                errors.Add(TrackerSearchErrorCode.Transport);
                continue;
            }

            var classified = TrackerResponseClassifier.Classify(html);
            if (classified is not null)
            {
                errors.Add(classified.Value);
                continue;
            }
            if (!html.Contains("id=\"index\"", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains("id='index'", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(TrackerSearchErrorCode.ParserContract);
                continue;
            }

            successfulSurfaces++;
            foreach (var torrent in Parse(html, category))
                results.TryAdd(torrent.Source!.SourceKey, torrent);
        }

        if (successfulSurfaces == 0)
        {
            var code = errors.Contains(TrackerSearchErrorCode.Challenge)
                ? TrackerSearchErrorCode.Challenge
                : errors.Count > 0
                    ? errors[0]
                    : TrackerSearchErrorCode.InvalidResponse;
            throw new TrackerSearchException(Tracker, code, "Every Rutor video-category search failed.");
        }

        var ordered = results.Values.OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var mostPopular = ordered.FirstOrDefault();
        if (mostPopular is not null)
            await FetchDetailsAsync(mostPopular, ct);
        return ordered.Where(HasValidIdentity).ToArray();
    }
}
