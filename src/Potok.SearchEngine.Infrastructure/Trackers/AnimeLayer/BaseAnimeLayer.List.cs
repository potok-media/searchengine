using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;

/// <summary>
///     AnimeLayer search-result list side: parses li.torrent-item rows from
///     /torrents/anime/ into torrents with a complete list-observation payload.
///     Only rows in the anime category are kept; quality is resolved from the
///     "Качество"/"Разрешение" facts (1080p/720p → Quality).
/// </summary>
public partial class BaseAnimeLayer
{
    protected IReadOnlyCollection<TorrentDetails> ParseSearchPage(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var now = DateTimeOffset.UtcNow;
        var results = new List<TorrentDetails>();

        foreach (var row in document.QuerySelectorAll("li.torrent-item, article.torrent-item, div.torrent-item"))
        {
            var titleLink = row.QuerySelector("a[href*='/torrent/']");
            var idMatch = Regex.Match(titleLink?.GetAttribute("href") ?? string.Empty,
                @"/torrent/(?<id>[a-z0-9]+)/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var categoryLink = row.QuerySelector("a.category[href], a[href^='/torrents/']");
            if (titleLink is null || !idMatch.Success || !IsAnimeCategory(categoryLink))
                continue;

            var id = idMatch.Groups["id"].Value.ToLowerInvariant();
            var sourceUrl = $"{Host}/torrent/{id}/";
            var title = TrackerText.NormalizeText(titleLink.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var info = row.QuerySelector(".info");
            var seedObserved = TryCounterAfterIcon(info, "s-icons-upload", out var seeders);
            var leechObserved = TryCounterAfterIcon(info, "s-icons-download", out var leechers);
            var sizeObserved = TryParseSize(info?.TextContent, out var size, out var sizeLabel);
            var sourceUpdatedAt = ParseRussianDate(TrackerText.NormalizeText(info?.TextContent ?? string.Empty));
            var facts = ExtractLabelledFacts(row.QuerySelector(".description") ?? row);
            var warnings = new List<string>();
            RedactAndRemoveTelemetry(facts, warnings);
            var normalized = ParseTitleAndFacts(title, facts);
            var cover = row.QuerySelector("img[data-original], img[src]");
            var imageUrl = ToSafePublicUrl(
                cover?.GetAttribute("data-original") ?? cover?.GetAttribute("src"), sourceUrl);

            var observed = TorrentObservedFields.Title | TorrentObservedFields.Types;
            if (seedObserved) observed |= TorrentObservedFields.Seeders;
            if (leechObserved) observed |= TorrentObservedFields.Leechers;
            if (sizeObserved) observed |= TorrentObservedFields.Size;
            if (sourceUpdatedAt is not null) observed |= TorrentObservedFields.PublishDate | TorrentObservedFields.SourceUpdatedAt;
            if (normalized.Name is not null || normalized.OriginalName is not null) observed |= TorrentObservedFields.Names;
            if (normalized.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
            if (normalized.Quality > 0) observed |= TorrentObservedFields.Quality;
            if (normalized.VideoType is not null) observed |= TorrentObservedFields.VideoType;
            if (normalized.Languages.Count > 0) observed |= TorrentObservedFields.Languages;
            if (normalized.Voices.Count > 0) observed |= TorrentObservedFields.Voices;
            if (normalized.Seasons.Count > 0) observed |= TorrentObservedFields.Seasons;

            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = id,
                ["sourceUrl"] = sourceUrl,
                ["title"] = title,
                ["category"] = "аниме",
                ["sizeBytes"] = sizeObserved ? size : null,
                ["sizeLabel"] = sizeObserved ? sizeLabel : null,
                ["seeders"] = seedObserved ? seeders : null,
                ["leechers"] = leechObserved ? leechers : null,
                ["sourceUpdatedAtRaw"] = sourceUpdatedAt is null ? null : TrackerText.NormalizeText(info?.TextContent ?? string.Empty),
                ["name"] = normalized.Name,
                ["originalName"] = normalized.OriginalName,
                ["releaseYear"] = normalized.Year > 0 ? normalized.Year : null,
                ["quality"] = normalized.Quality > 0 ? normalized.Quality : null,
                ["videoType"] = normalized.VideoType,
                ["languages"] = normalized.Languages.Count > 0 ? normalized.Languages : null,
                ["voices"] = normalized.Voices.Count > 0 ? normalized.Voices : null,
                ["seasons"] = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                ["facts"] = facts,
                ["image"] = imageUrl
            };

            results.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Types = ["anime"],
                Url = sourceUrl,
                Title = title,
                Sid = seeders,
                Pir = leechers,
                Size = size,
                SizeName = sizeLabel,
                CreateTime = sourceUpdatedAt?.UtcDateTime ?? now.UtcDateTime,
                UpdateTime = sourceUpdatedAt?.UtcDateTime ?? now.UtcDateTime,
                Name = normalized.Name,
                OriginalName = normalized.OriginalName,
                ReleaseYear = normalized.Year,
                Quality = normalized.Quality,
                VideoType = normalized.VideoType,
                Languages = normalized.Languages.Count > 0 ? normalized.Languages : null,
                Voices = normalized.Voices.Count > 0 ? normalized.Voices : null,
                Seasons = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                Source = new TorrentSourceSnapshot(
                    Tracker,
                    id,
                    sourceUrl,
                    observed,
                    TrackerDetailsState.Partial,
                    TrackerPayload.SchemaVersion,
                    ParserVersion,
                    TrackerPayload.Create(ParserVersion, listPayload, TrackerPayload.EmptyObject,
                        TrackerPayload.EmptyObject, warnings),
                    now,
                    null,
                    sourceUpdatedAt)
            });
        }

        return results;
    }
}
