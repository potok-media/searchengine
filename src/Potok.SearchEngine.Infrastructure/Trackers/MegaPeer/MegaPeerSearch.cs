using Microsoft.Extensions.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;
using Serilog;

namespace Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;

/// <summary>
///     One search request per video category; topic fetches are mandatory for every
///     candidate (the list carries no magnet) and run through <see cref="DetailEnricher"/>
///     with bounded concurrency. Every surfaced category that fails contributes its error
///     code, partial degradation is logged, and the search throws a typed
///     <see cref="TrackerSearchException"/> only when all fail.
/// </summary>
public class MegaPeerSearch : BaseMegaPeer
{
    private static readonly Serilog.ILogger Logger = Log.Logger.ForContext<MegaPeerSearch>();
    private static readonly string[] VideoCategories = ["80", "79", "5", "6", "55", "76"];

    public MegaPeerSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.MegaPeer.EnableSearch)
            return [];

        var encodedQuery = string.Join("", RuEncoding.GetBytes(query).Select(b => $"%{b:X2}"));

        var results = new Dictionary<string, TorrentDetails>(StringComparer.Ordinal);
        var errors = new List<TrackerSearchErrorCode>();
        var successfulSurfaces = 0;
        foreach (var category in VideoCategories)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{SearchUrl}?search={encodedQuery}&age=&cat={category}&stype=0&sort=3&ascdesc=0";
            string html;
            try
            {
                html = await HttpService.GetStringAsync(url, referer: url, encoding: RuEncoding, ct: ct);
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
                : errors.FirstOrDefault(TrackerSearchErrorCode.InvalidResponse);
            throw new TrackerSearchException(Tracker, code, "Every MegaPeer video-category search failed.");
        }

        if (errors.Count > 0)
            Logger.Warning(
                "MegaPeer search degraded: {FailedCategories}/{TotalCategories} category surfaces failed ({ErrorCodes})",
                errors.Count, VideoCategories.Length, errors);

        var candidates = results.Values.OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .ToArray();

        return await DetailEnricher.EnrichTopicsAsync(
            candidates,
            async (candidate, token) =>
                await FetchDetailsAsync(candidate, token) &&
                candidate.Types is { Length: > 0 } &&
                HasValidIdentity(candidate),
            ct);
    }
}
