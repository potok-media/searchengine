using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Kinozal detail side: fetches the topic page plus the get_srv_details.php service
///     fragment (the only source of info-hash/magnet), validates hash↔magnet identity and
///     folds everything into the source payload. Topics without a valid identity are
///     marked Partial/Failed and dropped by the enricher.
/// </summary>
public partial class BaseKinozal
{
    private async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent.Source is null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        try
        {
            var topicHtml = await Get(torrent.Url, RuEncoding, ct);
            if (string.IsNullOrWhiteSpace(topicHtml))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["topic_empty_response"], null);
                return false;
            }
            if (IsChallengeResponse(topicHtml))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["topic_challenge"], null);
                return false;
            }
            if (IsAuthenticationResponse(topicHtml))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["topic_authentication_required"], null);
                return false;
            }

            var topicDocument = await _parser.ParseDocumentAsync(topicHtml, ct);
            var topicBody = topicDocument.QuerySelector("#details, .release-details, .bx1, .postbody, main");
            if (topicBody is null)
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["topic_contract_missing"], null);
                return false;
            }

            var id = torrent.Source.SourceKey;
            var serviceUrl = $"{Host}/get_srv_details.php?id={id}&action=2";
            var serviceHtml = await Get(serviceUrl, RuEncoding, ct);
            if (string.IsNullOrWhiteSpace(serviceHtml))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["service_details_empty_response"], null);
                return false;
            }
            if (IsChallengeResponse(serviceHtml) || IsAuthenticationResponse(serviceHtml))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject,
                    [IsChallengeResponse(serviceHtml) ? "service_details_challenge" : "service_details_authentication_required"],
                    null);
                return false;
            }

            var warnings = new List<string>();
            var facts = ExtractLabelledFacts(topicBody);
            LabelledFacts.RedactSensitive(facts, warnings);
            foreach (var key in facts.Keys.Where(IsMomentaryTelemetryLabel).ToArray())
                facts.Remove(key);
            var hiddenSections = ExtractHiddenSections(topicBody);
            var breadcrumbs = topicDocument.QuerySelectorAll(".breadcrumbs a, .breadcrumb a, nav a[href*='browse.php?c=']")
                .Select(link => TrackerText.NormalizeText(link.TextContent))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var externalLinks = topicBody.QuerySelectorAll("a[href]")
                .Select(link => ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
                .Where(url => url is not null && !IsKinozalUrl(url))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var images = topicBody.QuerySelectorAll("img[src], img[data-src]")
                .SelectMany(image => new[] { image.GetAttribute("src"), image.GetAttribute("data-src") })
                .Select(value => ToSafePublicUrl(value, torrent.Url))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var downloadLink = topicBody.QuerySelector("a.download[href], a[href*='download.php?id=']");
            var downloadUrl = ToSafePublicUrl(downloadLink?.GetAttribute("href"), torrent.Url);
            var downloadFileName = TrackerText.NormalizeText(downloadLink?.TextContent ?? string.Empty);
            var topicTitle = TrackerText.NormalizeText(topicDocument.QuerySelector("h1, .topic-title, .bx1-title")
                ?.TextContent ?? string.Empty);
            var service = ParseServiceDetails(serviceHtml, warnings);
            if (string.IsNullOrWhiteSpace(service.ExplicitHash))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Partial,
                    BuildDetails(topicTitle, downloadUrl, downloadFileName, facts, externalLinks, images,
                        hiddenSections, service),
                    new Dictionary<string, object?> { ["breadcrumbs"] = breadcrumbs },
                    warnings.Append("service_details_hash_missing").ToArray(), DateTimeOffset.UtcNow);
                return false;
            }

            var candidateMagnet = !string.IsNullOrWhiteSpace(service.Magnet)
                ? service.Magnet
                : $"magnet:?xt=urn:btih:{service.ExplicitHash}";
            torrent.Magnet = candidateMagnet;
            torrent.InfoHash = service.ExplicitHash;
            var magnetHash = MagnetBuilder.HashFromMagnet(candidateMagnet);
            var explicitHash = MagnetBuilder.NormalizeHash(service.ExplicitHash);
            string? identityWarning = null;
            if (magnetHash is null || explicitHash is null)
                identityWarning = "service_details_hash_invalid";
            else if (!string.Equals(explicitHash, magnetHash, StringComparison.Ordinal))
                identityWarning = "service_details_hash_mismatch";
            if (identityWarning is not null)
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                warnings.Add(identityWarning);
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Partial,
                    BuildDetails(topicTitle, downloadUrl, downloadFileName, facts, externalLinks, images,
                        hiddenSections, service),
                    new Dictionary<string, object?> { ["breadcrumbs"] = breadcrumbs },
                    warnings, DateTimeOffset.UtcNow);
                return false;
            }

            torrent.InfoHash = magnetHash;
            torrent.Magnet = MagnetBuilder.BuildCanonical(magnetHash!);
            var observed = torrent.Source.ObservedFields |
                           TorrentObservedFields.InfoHash |
                           TorrentObservedFields.Magnet;
            ApplyNormalizedDetails(torrent, facts, hiddenSections, topicBody, ref observed);

            TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Fetched,
                BuildDetails(topicTitle, downloadUrl, downloadFileName, facts, externalLinks, images,
                    hiddenSections, service),
                new Dictionary<string, object?> { ["breadcrumbs"] = breadcrumbs },
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

    private static Dictionary<string, object?> BuildDetails(
        string topicTitle,
        string? downloadUrl,
        string downloadFileName,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyCollection<string> externalLinks,
        IReadOnlyCollection<string> images,
        IReadOnlyCollection<Dictionary<string, object?>> hiddenSections,
        ServiceDetails service) =>
        new(StringComparer.Ordinal)
        {
            ["topicTitle"] = NullIfEmpty(topicTitle),
            ["downloadUrl"] = downloadUrl,
            ["downloadFileName"] = NullIfEmpty(downloadFileName) ?? service.FileName,
            ["facts"] = facts,
            ["externalLinks"] = externalLinks,
            ["images"] = images,
            ["hiddenSections"] = hiddenSections,
            ["service"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["explicitInfoHash"] = service.ExplicitHash,
                ["magnet"] = service.Magnet,
                ["pieceSize"] = service.PieceSize,
                ["fileName"] = service.FileName
            }
        };

    private static ServiceDetails ParseServiceDetails(string html, ICollection<string> warnings)
    {
        var document = new HtmlParser().ParseDocument(html);
        var text = TrackerText.NormalizeText(document.Body?.TextContent ?? string.Empty);
        var hashMatch = Regex.Match(text,
            @"(?:Инфо\s*хеш|Info\s*hash)\s*:\s*(?<hash>[A-Fa-f0-9]{64}|[A-Fa-f0-9]{40}|[A-Za-z2-7]{32})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var pieceMatch = Regex.Match(text,
            @"Размер\s+части\s+торрента\s*:\s*(?<size>\d+(?:[.,]\d+)?\s*(?:ТБ|ГБ|МБ|КБ|TB|GB|MB|KB))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var fileName = TrackerText.NormalizeText(
            document.QuerySelector(".ing, .torrent-name")?.TextContent ?? string.Empty);
        var rawMagnet = WebUtility.HtmlDecode(
            document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href") ?? string.Empty);
        var magnet = string.IsNullOrWhiteSpace(rawMagnet) ? null : SanitizeMagnet(rawMagnet, warnings);
        return new ServiceDetails(
            hashMatch.Success ? hashMatch.Groups["hash"].Value : null,
            magnet,
            pieceMatch.Success ? pieceMatch.Groups["size"].Value : null,
            NullIfEmpty(fileName));
    }

    private sealed record ServiceDetails(
        string? ExplicitHash,
        string? Magnet,
        string? PieceSize,
        string? FileName);
}
