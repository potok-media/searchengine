using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     RuTracker topic-page (detail) side: bounded-parallel enrichment via
///     <see cref="DetailEnricher"/> — when the budget (cancellation token) runs out, the
///     topics enriched so far are still returned instead of losing every result. Each topic
///     fetch validates magnet↔explicit-hash identity, scrubs secrets and momentary
///     telemetry, and folds normalized facts back into the torrent.
/// </summary>
public partial class BaseRuTracker
{
    private static readonly HashSet<string> AllowedVideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "serial", "anime", "multfilm", "multserial",
        "documovie", "docuserial", "tvshow", "sport"
    };

    protected async Task<IReadOnlyCollection<TorrentDetails>> EnrichVideoTopicsAsync(
        IReadOnlyCollection<TorrentDetails> torrents,
        CancellationToken ct)
    {
        var candidates = torrents
            .Where(IsAllowedVideoResult)
            .OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .ToArray();

        return await DetailEnricher.EnrichTopicsAsync(candidates, EnrichFromTopicAsync, ct);
    }

    private static bool IsAllowedVideoResult(TorrentDetails torrent)
    {
        return torrent.Types is { Length: > 0 } && torrent.Types.All(AllowedVideoTypes.Contains);
    }

    private async Task<bool> EnrichFromTopicAsync(TorrentDetails torrent, CancellationToken ct)
    {
        try
        {
            var html = await Get(
                torrent.Url,
                RuEncoding,
                torrent.Url,
                false,
                ct);

            if (string.IsNullOrWhiteSpace(html))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_empty_response"], null);
                return false;
            }

            var document = await _parser.ParseDocumentAsync(html, ct);
            var magnet = document.QuerySelector("a.magnet-link")?.GetAttribute("href");
            var magnetHash = MagnetBuilder.HashFromMagnet(magnet is null ? null : WebUtility.HtmlDecode(magnet));
            var explicitHashRaw = ExtractExplicitHash(document);
            var explicitHash = MagnetBuilder.NormalizeHash(explicitHashRaw);
            var facts = LabelledFacts.ExtractPostBody(document);
            var warnings = new List<string>();
            LabelledFacts.RedactSensitive(facts, warnings);
            LabelledFacts.RemoveTransientTelemetry(facts);

            var topicTitle = TrackerText.NormalizeText(
                document.QuerySelector("a#topic-title")?.TextContent);
            var topicDateRaw = document.QuerySelectorAll("a.p-link.small")
                .FirstOrDefault(link =>
                    link.GetAttribute("href")?.Contains("viewtopic.php", StringComparison.OrdinalIgnoreCase) == true)
                ?.TextContent;
            var topicDate = TrackerText.ParseRuTopicDate(topicDateRaw ?? string.Empty);
            var breadcrumbs = document.QuerySelectorAll(".nav a, .breadcrumbs a, #breadcrumb a")
                .Select(link => TrackerText.NormalizeText(link.TextContent))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var externalLinks = document.QuerySelectorAll(".post_body a[href]")
                .Select(link => SafeUrl.ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
                .Where(url => url is not null && !url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["topicTitle"] = string.IsNullOrWhiteSpace(topicTitle) ? null : topicTitle,
                ["topicDateRaw"] = TrackerText.NormalizeText(topicDateRaw),
                ["magnet"] = magnet,
                ["magnetInfoHash"] = magnetHash,
                ["explicitInfoHash"] = explicitHashRaw,
                ["facts"] = facts,
                ["breadcrumbs"] = breadcrumbs,
                ["externalLinks"] = externalLinks
            };
            var unmapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["labelledFacts"] = facts,
                ["breadcrumbs"] = breadcrumbs,
                ["externalLinks"] = externalLinks
            };

            var observed = torrent.Source?.ObservedFields ?? TorrentObservedFields.None;
            var sidElement = document.QuerySelector("span.seed b, td.seedmed, b.seedmed");
            if (sidElement != null)
            {
                torrent.Sid = TrackerText.ParseInt(sidElement.TextContent);
                observed |= TorrentObservedFields.Seeders;
            }
            var pirElement = document.QuerySelector("span.leech b, td.leechmed");
            if (pirElement != null)
            {
                torrent.Pir = TrackerText.ParseInt(pirElement.TextContent);
                observed |= TorrentObservedFields.Leechers;
            }
            if (!string.IsNullOrWhiteSpace(topicTitle))
            {
                torrent.Title = topicTitle;
                observed |= TorrentObservedFields.Title;
            }
            if (topicDate != default)
            {
                torrent.CreateTime = DateTime.SpecifyKind(topicDate, DateTimeKind.Utc);
                observed |= TorrentObservedFields.PublishDate;
            }

            ApplyNormalizedFacts(torrent, facts, ref observed);

            if (string.IsNullOrWhiteSpace(magnet) || string.IsNullOrWhiteSpace(magnetHash))
            {
                warnings.Add(string.IsNullOrWhiteSpace(magnet) ? "detail_magnet_missing" : "detail_hash_invalid");
                var state = facts.Count == 0 ? TrackerDetailsState.Failed : TrackerDetailsState.Partial;
                TrackerPayload.WithDetails(torrent, ParserVersion, state, details, unmapped, warnings,
                    null, observed);
                return false;
            }

            if (explicitHashRaw is not null && explicitHash is null)
                warnings.Add("explicit_hash_invalid");

            if (explicitHash is not null && !string.Equals(magnetHash, explicitHash, StringComparison.Ordinal))
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                observed |= TorrentObservedFields.Magnet | TorrentObservedFields.InfoHash;
                warnings.Add("hash_mismatch");
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Partial, details, unmapped,
                    warnings, DateTimeOffset.UtcNow, observed);
                return false;
            }

            torrent.Magnet = magnet;
            torrent.InfoHash = magnetHash;
            observed |= TorrentObservedFields.Magnet | TorrentObservedFields.InfoHash;
            TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Fetched, details, unmapped,
                warnings, DateTimeOffset.UtcNow, observed);
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

    private static string? ExtractExplicitHash(IDocument document)
    {
        var fromAttribute = document.QuerySelector("[data-hash]")?.GetAttribute("data-hash");
        if (!string.IsNullOrWhiteSpace(fromAttribute))
            return fromAttribute.Trim();

        var match = ExplicitHashRegex().Match(
            document.QuerySelector(".post_body, .postbody, #tor-reged")?.TextContent ?? string.Empty);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    [GeneratedRegex(
        @"(?:инфо[ -]?хеш|info[ -]?hash|хеш)\s*:?[\s\[]*(?<hash>[a-f0-9]{40}|[a-f0-9]{64}|[a-z2-7]{32})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitHashRegex();
}
