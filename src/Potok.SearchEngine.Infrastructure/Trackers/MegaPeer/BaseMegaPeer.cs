using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;

/// <summary>
///     MegaPeer topic-page (detail) side: fetches table#details, validates magnet identity
///     against the explicit info-hash, extracts labelled facts and header metadata, scrubs
///     secrets and momentary telemetry, and folds normalized fields back into the torrent.
///     List parsing lives in BaseMegaPeer.List.cs, fact extraction in BaseMegaPeer.Facts.cs,
///     normalized mapping in BaseMegaPeer.Normalize.cs.
/// </summary>
public partial class BaseMegaPeer : BaseTrackerSearch
{
    private const string ParserVersion = "megapeer/2026-09-22.2";
    private readonly HtmlParser _parser = new();

    protected BaseMegaPeer(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Megapeer;
    public override string TrackerName => "megapeer";
    public override string Host => "https://megapeer.vip/";

    public string SearchUrl => $"{Host}browse.php";

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
                ct: ct);
            if (string.IsNullOrWhiteSpace(html))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_empty_response"], null);
                return false;
            }

            var document = await _parser.ParseDocumentAsync(html, ct);
            var detailsTable = document.QuerySelector("table#details");
            if (detailsTable is null)
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_contract_missing"], null);
                return false;
            }

            var warnings = new List<string>();
            var observed = torrent.Source?.ObservedFields ?? TorrentObservedFields.None;
            var magnetRaw = WebUtility.HtmlDecode(
                document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href") ?? string.Empty);
            var magnetInfoHash = MagnetBuilder.HashFromMagnet(magnetRaw);
            var infoHash = magnetInfoHash;
            if (!string.IsNullOrWhiteSpace(magnetRaw))
            {
                observed |= TorrentObservedFields.Magnet | TorrentObservedFields.InfoHash;
                if (infoHash is null)
                {
                    torrent.Magnet = null;
                    torrent.InfoHash = null;
                    warnings.Add("detail_hash_invalid");
                }
                else
                {
                    torrent.Magnet = magnetRaw;
                    torrent.InfoHash = infoHash;
                }
            }
            else
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                warnings.Add("detail_magnet_missing");
            }

            var content = detailsTable.QuerySelector("td#descr");
            var facts = content is null ? LabelledFacts.NewDictionary() : ExtractLabelledFacts(content);
            LabelledFacts.RedactSensitive(facts, warnings);
            var explicitHashRaw = TrackerText.FindFact(facts, "Инфо-хеш", "Инфо хеш", "Info hash", "Hash");
            var explicitHash = MagnetBuilder.NormalizeHash(explicitHashRaw);
            if (explicitHashRaw is not null && explicitHash is null)
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                infoHash = null;
                warnings.Add("explicit_hash_invalid");
            }
            else if (explicitHash is not null &&
                     !string.Equals(explicitHash, magnetInfoHash, StringComparison.Ordinal))
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                infoHash = null;
                warnings.Add("hash_mismatch");
            }

            var (metadata, metadataRaw, categoryLink) = ExtractMetadata(detailsTable);
            LabelledFacts.RedactSensitive(metadata, warnings);
            LabelledFacts.RedactSensitive(metadataRaw, warnings);
            LabelledFacts.RemoveTransientTelemetry(facts);
            LabelledFacts.RemoveTransientTelemetry(metadata);
            LabelledFacts.RemoveTransientTelemetry(metadataRaw);

            var hiddenSections = content is null ? [] : ExtractHiddenSections(content, torrent.Url);
            var externalLinks = content?.QuerySelectorAll("a[href]")
                .Select(link => SafeUrl.ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
                .Where(url => url is not null && !IsMegaPeerUrl(url))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            var tagLinks = content?.QuerySelectorAll("a[href^='/tag/']")
                .Select(link => SafeUrl.ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            var images = content?.QuerySelectorAll("img[src], img[data-src]")
                .SelectMany(image => new[] { image.GetAttribute("src"), image.GetAttribute("data-src") })
                .Select(value => SafeUrl.ToSafePublicUrl(value, torrent.Url))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];

            ApplyNormalizedDetails(torrent, facts, metadataRaw, hiddenSections, categoryLink, ref observed);

            var categoryBreadcrumbs = metadataRaw.TryGetValue("Категория", out var rawCategory)
                ? rawCategory.Split('»', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [];
            var categoryId = categoryLink?.GetAttribute("href")?.Trim('/').Split('/').LastOrDefault();
            var downloadLink = document.QuerySelector("#download a[href^='/download/']");
            var downloadUrl = SafeUrl.ToSafePublicUrl(downloadLink?.GetAttribute("href"), torrent.Url);
            var downloadFileName = TrackerText.NormalizeText(downloadLink?.TextContent ?? string.Empty);
            if (downloadFileName.StartsWith("Скачать ", StringComparison.OrdinalIgnoreCase))
                downloadFileName = downloadFileName[8..].Trim();

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["topicTitle"] = TrackerText.NormalizeText(document.QuerySelector("h1")?.TextContent ?? torrent.Title),
                ["magnet"] = string.IsNullOrWhiteSpace(magnetRaw) ? null : magnetRaw,
                ["magnetInfoHash"] = magnetInfoHash,
                ["explicitInfoHash"] = explicitHashRaw,
                ["downloadUrl"] = downloadUrl,
                ["downloadFileName"] = string.IsNullOrWhiteSpace(downloadFileName) ? null : downloadFileName,
                ["facts"] = facts,
                ["metadata"] = metadata,
                ["externalLinks"] = externalLinks,
                ["tagLinks"] = tagLinks,
                ["images"] = images,
                ["hiddenSections"] = hiddenSections
            };
            var unmapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["categoryId"] = categoryId,
                ["categoryBreadcrumbs"] = categoryBreadcrumbs,
                ["metadataRaw"] = metadataRaw
            };
            var state = infoHash is not null && torrent.Types is { Length: > 0 }
                ? TrackerDetailsState.Fetched
                : TrackerDetailsState.Partial;
            TrackerPayload.WithDetails(torrent, ParserVersion, state, details, unmapped,
                warnings, DateTimeOffset.UtcNow, observed);
            return state == TrackerDetailsState.Fetched;
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

    protected static bool HasValidIdentity(TorrentDetails torrent)
    {
        var magnetHash = MagnetBuilder.HashFromMagnet(torrent.Magnet);
        return magnetHash is not null &&
               string.Equals(magnetHash, torrent.InfoHash, StringComparison.Ordinal);
    }
}
