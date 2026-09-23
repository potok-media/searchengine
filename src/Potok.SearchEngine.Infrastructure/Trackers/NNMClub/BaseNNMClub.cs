using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     NNMClub topic-page (detail) side: fetches viewtopic.php, extracts the magnet/explicit
///     hash identity, labelled facts, hidden sections and links, scrubs secrets and momentary
///     telemetry, and folds normalized fields back into the torrent. Topics whose magnet and
///     explicit hash cannot be reconciled are reported as failed so ingestion drops them.
///     List parsing lives in BaseNNMClub.List.cs, fact folding in BaseNNMClub.Facts.cs,
///     shared detail helpers in BaseNNMClub.Detail.cs.
/// </summary>
public partial class BaseNNMClub : BaseTrackerSearch
{
    private const string ParserVersion = "nnmclub/2026-09-22";
    private const string ForumTaxonomyVersion = "nnmclub-forums/2026-09-22";
    private readonly HtmlParser _parser = new();

    private static readonly HashSet<string> AllowedVideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "serial", "anime", "multfilm", "multserial",
        "documovie", "docuserial", "tvshow", "sport"
    };

    public BaseNNMClub(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.NNMClub;
    public override string TrackerName => "nnmclub";
    public override string Host => "https://nnmclub.to";

    protected async Task<IReadOnlyCollection<TorrentDetails>> EnrichVideoTopicsAsync(
        IReadOnlyCollection<TorrentDetails> torrents,
        CancellationToken ct)
    {
        var candidates = torrents
            .Where(IsAllowedVideoResult)
            .GroupBy(torrent => torrent.Source?.SourceKey ?? torrent.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .ToArray();
        return await DetailEnricher.EnrichTopicsAsync(candidates, FetchDetailsAsync, ct);
    }

    private static bool IsAllowedVideoResult(TorrentDetails torrent) =>
        torrent.Types is { Length: > 0 } && torrent.Types.All(AllowedVideoTypes.Contains);

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;
        try
        {
            var html = await HttpService.GetStringAsync(
                torrent.Url,
                referer: torrent.Url,
                encoding: RuEncoding,
                useProxy: true,
                ct: ct);
            if (string.IsNullOrWhiteSpace(html))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_empty_response"], null);
                return false;
            }

            var document = await _parser.ParseDocumentAsync(html, ct);
            var body = document.QuerySelector(".postbody, .post_body, .message, .post-entry");
            var titleElement = document.QuerySelector(".maintitle");
            if (body is null && titleElement is null)
            {
                var warning = IsAuthenticationPage(document)
                    ? "detail_authentication_required"
                    : "detail_contract_missing";
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, [warning], null);
                return false;
            }

            var warnings = new List<string>();
            var observed = torrent.Source?.ObservedFields ?? TorrentObservedFields.None;
            var magnetRaw = WebUtility.HtmlDecode(
                document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href") ?? string.Empty);
            var magnet = MagnetBuilder.Sanitize(magnetRaw) ?? magnetRaw;
            if (!string.Equals(magnet, magnetRaw, StringComparison.Ordinal))
                warnings.Add("secret_query_redacted");
            var magnetHash = MagnetBuilder.HashFromMagnet(magnetRaw);
            var explicitHashRaw = ExtractExplicitHash(document);

            var facts = body is null
                ? LabelledFacts.NewDictionary()
                : LabelledFacts.ExtractInline(body);
            LabelledFacts.RedactSensitive(facts, warnings);
            LabelledFacts.RemoveTransientTelemetry(facts);
            var hiddenSections = body is null
                ? Array.Empty<Dictionary<string, object?>>()
                : ExtractHiddenSections(body);
            ApplyNormalizedDetails(torrent, facts, hiddenSections, ref observed);

            var externalLinks = body?.QuerySelectorAll("a[href]")
                .Select(link => ToSafePublicUrl(link.GetAttribute("href"), torrent.Url, stripQuery: true))
                .Where(url => url is not null && !IsNnmClubUrl(url))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            var images = body?.QuerySelectorAll("img[src], img[data-src]")
                .SelectMany(image => new[] { image.GetAttribute("src"), image.GetAttribute("data-src") })
                .Select(value => ToSafePublicUrl(value, torrent.Url, stripQuery: true))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            var breadcrumbs = document.QuerySelectorAll("a[href*='tracker.php?c='], a[href*='tracker.php?f=']")
                .Select(link => TrackerText.NormalizeText(link.TextContent).Replace("Трекер:", string.Empty,
                    StringComparison.OrdinalIgnoreCase).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var downloadLink = document.QuerySelector("a[href*='download.php?id=']");
            var downloadUrl = ToSafePublicUrl(downloadLink?.GetAttribute("href"), torrent.Url, stripQuery: false);
            var downloadId = MatchQueryId(downloadLink?.GetAttribute("href"), "id");

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["topicTitle"] = TrackerText.NormalizeText(titleElement?.TextContent ?? torrent.Title),
                ["magnet"] = string.IsNullOrWhiteSpace(magnetRaw) ? null : magnet,
                ["magnetInfoHash"] = magnetHash,
                ["explicitInfoHash"] = explicitHashRaw,
                ["downloadId"] = downloadId,
                ["downloadUrl"] = downloadUrl,
                ["facts"] = facts,
                ["externalLinks"] = externalLinks,
                ["images"] = images,
                ["hiddenSections"] = hiddenSections
            };
            var unmapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["breadcrumbs"] = breadcrumbs
            };

            torrent.Magnet = string.IsNullOrWhiteSpace(magnetRaw) ? null : magnet;
            torrent.InfoHash = explicitHashRaw;
            if (!TryResolveIdentity(torrent, out var normalizedHash, out var identityErrorCode))
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                warnings.Add(identityErrorCode switch
                {
                    "missing_magnet" => "detail_magnet_missing",
                    "invalid_magnet" => "detail_hash_invalid",
                    _ => identityErrorCode ?? "detail_identity_invalid"
                });
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Partial,
                    details, unmapped, warnings, DateTimeOffset.UtcNow, observed);
                return false;
            }

            torrent.InfoHash = normalizedHash;
            observed |= TorrentObservedFields.Magnet | TorrentObservedFields.InfoHash;
            TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Fetched,
                details, unmapped, warnings, DateTimeOffset.UtcNow, observed);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_fetch_failed"], null);
            return false;
        }
    }
}
