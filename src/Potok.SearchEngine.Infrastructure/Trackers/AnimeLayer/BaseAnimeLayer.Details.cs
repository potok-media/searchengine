using System.Net;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;

/// <summary>
///     AnimeLayer topic-page (detail) side: one document supplies identity (magnet +
///     optional explicit info hash), counters, dates, download link, facts, hidden
///     sections, external links and images. Secrets are scrubbed before persistence and
///     a magnet that fails identity validation downgrades the topic to Partial and drops
///     it from the playable results.
/// </summary>
public partial class BaseAnimeLayer
{
    private async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent.Source is null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;

        try
        {
            var html = await Get(torrent.Url, torrent.Url, ct);
            if (string.IsNullOrWhiteSpace(html))
            {
                UpdateSource(torrent, TrackerDetailsState.Failed, TrackerPayload.EmptyObject,
                    TrackerPayload.EmptyObject, ["detail_empty_response"], null);
                return false;
            }
            if (TrackerResponseClassifier.IsChallenge(html))
            {
                UpdateSource(torrent, TrackerDetailsState.Failed, TrackerPayload.EmptyObject,
                    TrackerPayload.EmptyObject, ["detail_challenge"], null);
                return false;
            }
            if (IsAuthenticationResponse(html))
            {
                UpdateSource(torrent, TrackerDetailsState.Partial, TrackerPayload.EmptyObject,
                    TrackerPayload.EmptyObject, ["detail_authentication_required"], null);
                return false;
            }

            var document = await _parser.ParseDocumentAsync(html, ct);
            var body = document.QuerySelector(".torrent-info, main, .torrent-details");
            var category = document.QuerySelector("a.category[href], a[href='/torrents/anime/']");
            if (body is null || !IsAnimeCategory(category))
            {
                UpdateSource(torrent, TrackerDetailsState.Failed, TrackerPayload.EmptyObject,
                    TrackerPayload.EmptyObject, ["detail_contract_missing"], null);
                return false;
            }

            var warnings = new List<string>();
            var facts = ExtractLabelledFacts(body);
            RedactAndRemoveTelemetry(facts, warnings);
            var hiddenSections = ExtractHiddenSections(body, warnings);
            var rawMagnet = WebUtility.HtmlDecode(
                document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(rawMagnet))
                rawMagnet = await FetchMagnetRedirectAsync(torrent.Url, ct) ?? string.Empty;
            var safeMagnet = MagnetBuilder.Sanitize(rawMagnet) ?? rawMagnet;
            if (!string.Equals(safeMagnet, rawMagnet, StringComparison.Ordinal))
                warnings.Add("secret_query_redacted");
            var explicitHash = FindFact(facts, "Инфо хеш", "Info hash", "InfoHash");

            var topicTitle = TrackerText.NormalizeText(body.QuerySelector("h1")?.TextContent ?? torrent.Title);
            var information = body.QuerySelectorAll(".info").FirstOrDefault(element =>
                element.QuerySelector(".s-icons-upload, .s-icons-download") is not null);
            var seedObserved = TryCounterAfterIcon(information, "s-icons-upload", out var seeders);
            var leechObserved = TryCounterAfterIcon(information, "s-icons-download", out var leechers);
            var sizeObserved = TryParseSize(information?.TextContent, out var size, out var sizeLabel);
            var updatedAt = ParseRussianDate(
                TrackerText.NormalizeText(body.QuerySelector(".date-updated")?.TextContent ?? string.Empty));
            var createdAt = ParseRussianDate(
                TrackerText.NormalizeText(body.QuerySelector(".date-created")?.TextContent ?? string.Empty));

            var download = document.QuerySelector("a[download][href], a.download[href]");
            var downloadFileName = NullIfEmpty(download?.GetAttribute("download")) ??
                                   NullIfEmpty(TrackerText.NormalizeText(download?.TextContent ?? string.Empty));
            var downloadUrl = ToSafePublicUrl(download?.GetAttribute("href"), torrent.Url);
            var externalLinks = body.QuerySelectorAll("a[href]")
                .Select(link => ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
                .Where(url => url is not null && !IsAnimeLayerUrl(url))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var images = document.QuerySelectorAll(".torrent-info img[src], .torrent-info img[data-original], .torrent-info img[data-zoom-image], .torrent-left img[src]")
                .SelectMany(image => new[]
                {
                    image.GetAttribute("data-zoom-image"), image.GetAttribute("data-original"), image.GetAttribute("src")
                })
                .Select(value => ToSafePublicUrl(value, torrent.Url))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var rating = NullIfEmpty(TrackerText.NormalizeText(document.QuerySelector(".stars-rating-count")?.TextContent ?? string.Empty));
            var statsText = TrackerText.NormalizeText(information?.TextContent ?? string.Empty);
            var views = TryCounterAfterIcon(information, "s-icons-eye", out var parsedViews)
                ? parsedViews
                : (int?)null;
            var completed = TryCounterAfterIcon(information, "s-icons-complete", out var parsedCompleted)
                ? parsedCompleted
                : (int?)null;
            var updateReason = MatchValue(TrackerText.NormalizeText(body.TextContent), @"Причина\s*:\s*(?<value>.+?)(?:$|Комментарии)");

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["topicTitle"] = NullIfEmpty(topicTitle),
                ["category"] = "аниме",
                ["magnet"] = NullIfEmpty(safeMagnet),
                ["magnetInfoHash"] = ExtractInfoHash(rawMagnet),
                ["explicitInfoHash"] = NullIfEmpty(explicitHash),
                ["downloadFileName"] = downloadFileName,
                ["downloadUrl"] = downloadUrl,
                ["sizeBytes"] = sizeObserved ? size : null,
                ["sizeLabel"] = sizeObserved ? sizeLabel : null,
                ["seeders"] = seedObserved ? seeders : null,
                ["leechers"] = leechObserved ? leechers : null,
                ["createdAtRaw"] = NullIfEmpty(TrackerText.NormalizeText(body.QuerySelector(".date-created")?.TextContent ?? string.Empty)),
                ["updatedAtRaw"] = NullIfEmpty(TrackerText.NormalizeText(body.QuerySelector(".date-updated")?.TextContent ?? string.Empty)),
                ["rating"] = rating,
                ["views"] = views,
                ["completedDownloads"] = completed,
                ["updateReason"] = NullIfEmpty(updateReason),
                ["facts"] = facts,
                ["hiddenSections"] = hiddenSections,
                ["externalLinks"] = externalLinks,
                ["images"] = images
            };
            var unmapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["breadcrumbs"] = new[] { "аниме" },
                ["stableStatsText"] = RemoveMomentaryTelemetry(statsText)
            };

            var observed = torrent.Source.ObservedFields;
            if (!string.IsNullOrWhiteSpace(topicTitle))
            {
                torrent.Title = topicTitle;
                observed |= TorrentObservedFields.Title;
            }
            if (seedObserved)
            {
                torrent.Sid = seeders;
                observed |= TorrentObservedFields.Seeders;
            }
            if (leechObserved)
            {
                torrent.Pir = leechers;
                observed |= TorrentObservedFields.Leechers;
            }
            if (sizeObserved)
            {
                torrent.Size = size;
                torrent.SizeName = sizeLabel;
                observed |= TorrentObservedFields.Size;
            }
            if (createdAt is not null)
            {
                torrent.CreateTime = createdAt.Value.UtcDateTime;
                observed |= TorrentObservedFields.PublishDate;
            }
            if (updatedAt is not null)
                torrent.UpdateTime = updatedAt.Value.UtcDateTime;

            ApplyNormalizedDetails(torrent, facts, ref observed);
            torrent.Magnet = NullIfEmpty(safeMagnet);
            torrent.InfoHash = NullIfEmpty(explicitHash);
            if (!TryResolveIdentity(torrent, out var normalizedHash, out var identityErrorCode))
            {
                torrent.Magnet = null;
                torrent.InfoHash = null;
                warnings.Add(identityErrorCode switch
                {
                    "missing_magnet" => "detail_magnet_missing",
                    "invalid_magnet" or "invalid_hash" => "detail_hash_invalid",
                    "hash_mismatch" => "detail_hash_mismatch",
                    _ => "detail_identity_invalid"
                });
                UpdateSource(torrent, TrackerDetailsState.Partial, details, unmapped, warnings,
                    DateTimeOffset.UtcNow, observed, updatedAt);
                return false;
            }

            torrent.InfoHash = normalizedHash;
            observed |= TorrentObservedFields.InfoHash | TorrentObservedFields.Magnet;
            UpdateSource(torrent, TrackerDetailsState.Fetched, details, unmapped, warnings,
                DateTimeOffset.UtcNow, observed, updatedAt);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            UpdateSource(torrent, TrackerDetailsState.Failed, TrackerPayload.EmptyObject,
                TrackerPayload.EmptyObject, ["detail_fetch_failed"], null);
            return false;
        }
    }

    /// <summary>
    ///     Topic pages no longer carry an inline magnet: it lives behind
    ///     /torrent/{id}/download/?type=magnet, which answers 302 with the magnet in the
    ///     Location header. The redirect is not followed (the target is a magnet: URI) and
    ///     the session cookie is refreshed once when a cached one no longer authorizes.
    /// </summary>
    private async Task<string?> FetchMagnetRedirectAsync(string topicUrl, CancellationToken ct)
    {
        var downloadUrl = $"{topicUrl.TrimEnd('/')}/download/?type=magnet";
        CacheService.TryGetValue(CookieKey, out string? cookie);
        var authorizedNow = false;
        if (string.IsNullOrWhiteSpace(cookie) && HasConfiguredCredentials())
        {
            cookie = await Authorize(ct: ct);
            authorizedNow = true;
        }

        var magnet = await RequestMagnetLocationAsync(downloadUrl, topicUrl, cookie, ct);
        if (magnet is not null || authorizedNow || !HasConfiguredCredentials())
            return magnet;

        cookie = await Authorize(reAuth: true, ct);
        if (string.IsNullOrWhiteSpace(cookie))
            return null;
        return await RequestMagnetLocationAsync(downloadUrl, topicUrl, cookie, ct);
    }

    private async Task<string?> RequestMagnetLocationAsync(
        string downloadUrl,
        string referer,
        string? cookie,
        CancellationToken ct)
    {
        using var response = await HttpService.PostResponseAsync(
            downloadUrl, content: null, cookie, referer, encoding: null,
            useProxy: true, allowRedirect: false, ct);
        if ((int)response.StatusCode is < 300 or >= 400)
            return null;
        var location = response.Headers.TryGetValues("Location", out var values)
            ? values.FirstOrDefault()
            : null;
        location = WebUtility.HtmlDecode(location ?? string.Empty).Trim();
        return location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? location : null;
    }

    private static void UpdateSource(
        TorrentDetails torrent,
        TrackerDetailsState state,
        object details,
        object unmapped,
        IReadOnlyCollection<string> warnings,
        DateTimeOffset? detailsFetchedAt,
        TorrentObservedFields? observed = null,
        DateTimeOffset? sourceUpdatedAt = null)
    {
        var snapshot = TrackerPayload.WithDetails(torrent, ParserVersion, state, details, unmapped,
            warnings, detailsFetchedAt, observed);
        if (sourceUpdatedAt is not null && sourceUpdatedAt != snapshot.SourceUpdatedAt)
            torrent.Source = snapshot with { SourceUpdatedAt = sourceUpdatedAt };
    }

    private static IReadOnlyCollection<Dictionary<string, object?>> ExtractHiddenSections(
        IElement body,
        ICollection<string> warnings)
    {
        var sections = new List<Dictionary<string, object?>>();
        foreach (var section in body.QuerySelectorAll(".spoiler"))
        {
            var title = TrackerText.NormalizeText(section.QuerySelector(".spoiler-title")?.TextContent ?? string.Empty);
            if (IsMomentaryTelemetryLabel(title) || IsSensitiveLabel(title))
                continue;
            var content = section.QuerySelector(".spoiler-content");
            if (content is null) continue;
            var facts = ExtractLabelledFacts(content);
            RedactAndRemoveTelemetry(facts, warnings);
            var text = string.Join('\n', ExtractTextWithBreaks(content).Split('\n')
                .Select(TrackerText.NormalizeText)
                .Where(line => !string.IsNullOrWhiteSpace(line) &&
                               !IsMomentaryTelemetryLabel(line) &&
                               !IsSensitiveLabel(line)));
            sections.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = NullIfEmpty(title),
                ["text"] = NullIfEmpty(text),
                ["facts"] = facts
            });
        }
        return sections;
    }

    private static void ApplyNormalizedDetails(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        ref TorrentObservedFields observed)
    {
        var normalized = ParseTitleAndFacts(torrent.Title, facts);
        if (normalized.Name is not null || normalized.OriginalName is not null)
        {
            torrent.Name = normalized.Name ?? torrent.Name;
            torrent.OriginalName = normalized.OriginalName ?? torrent.OriginalName;
            observed |= TorrentObservedFields.Names;
        }
        if (normalized.Year > 0)
        {
            torrent.ReleaseYear = normalized.Year;
            observed |= TorrentObservedFields.ReleaseYear;
        }
        if (normalized.Quality > 0)
        {
            torrent.Quality = normalized.Quality;
            observed |= TorrentObservedFields.Quality;
        }
        if (normalized.VideoType is not null)
        {
            torrent.VideoType = normalized.VideoType;
            observed |= TorrentObservedFields.VideoType;
        }
        if (normalized.Languages.Count > 0)
        {
            torrent.Languages = normalized.Languages;
            observed |= TorrentObservedFields.Languages;
        }
        if (normalized.Voices.Count > 0)
        {
            torrent.Voices = normalized.Voices;
            observed |= TorrentObservedFields.Voices;
        }
        if (normalized.Seasons.Count > 0)
        {
            torrent.Seasons = normalized.Seasons;
            observed |= TorrentObservedFields.Seasons;
        }
    }
}
