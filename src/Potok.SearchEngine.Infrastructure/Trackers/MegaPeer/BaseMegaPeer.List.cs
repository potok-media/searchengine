using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;

/// <summary>
///     MegaPeer search-result list side: parses #index rows into torrents with a complete
///     list-observation payload. Rows carry no magnet — the topic page is the only source
///     of magnet identity, so list items are marked DetailsState.Partial.
/// </summary>
public partial class BaseMegaPeer
{
    private static string[] MapCategory(string category)
    {
        return category switch
        {
            "80" => ["movie"],
            "79" => ["movie"],
            "5" => ["serial"],
            "6" => ["serial"],
            "55" => ["documovie", "docuserial"],
            "76" => ["multfilm", "multserial", "anime"],
            _ => []
        };
    }

    protected IReadOnlyCollection<TorrentDetails> Parse(string html, string categoryId)
    {
        var list = new List<TorrentDetails>();
        var document = _parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("#index tr.table_fon");
        var types = MapCategory(categoryId);
        if (types.Length == 0)
            return list;

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 4) continue;

            var titleLink = row.QuerySelector("a.url");
            if (titleLink == null) continue;

            var titleCell = titleLink.Closest("td");
            var peerCell = cells.FirstOrDefault(cell => cell.QuerySelector("font[color]") is not null);
            if (titleCell is null || peerCell is null) continue;
            var cellList = cells.ToList();
            var titleIndex = cellList.IndexOf(titleCell);
            var peerIndex = cellList.IndexOf(peerCell);
            if (titleIndex < 0 || peerIndex <= titleIndex + 1) continue;
            var sizeCell = cellList[peerIndex - 1];

            var title = TrackerText.NormalizeText(titleLink.TextContent);
            var sourceHref = titleLink.GetAttribute("href");
            var sourceMatch = Regex.Match(sourceHref ?? string.Empty, @"^/torrent/(?<id>\d+)(?:/[^?#]*)?",
                RegexOptions.CultureInvariant);
            if (!sourceMatch.Success) continue;
            var sourceKey = sourceMatch.Groups["id"].Value;
            var url = new Uri(new Uri(Host), sourceMatch.Value).ToString();

            long size = 0;
            string? sizeName = null;
            var sizeObserved = false;
            var sizeText = TrackerText.NormalizeText(sizeCell.TextContent);
            var sizeMatch = Regex.Match(sizeText, @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>[KMGT]?B)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (sizeMatch.Success)
            {
                size = TrackerText.ParseSize(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
                sizeName = $"{sizeMatch.Groups["value"].Value} {sizeMatch.Groups["unit"].Value}";
                sizeObserved = true;
            }

            var seeds = 0;
            var peers = 0;
            IElement? seedsElement = null;
            IElement? peersElement = null;
            foreach (var peerElement in peerCell.QuerySelectorAll("font[color]"))
            {
                var color = peerElement.GetAttribute("color");
                if (string.Equals(color, "#008000", StringComparison.OrdinalIgnoreCase))
                    seedsElement = peerElement;
                if (string.Equals(color, "#8b0000", StringComparison.OrdinalIgnoreCase))
                    peersElement = peerElement;
            }
            if (seedsElement is not null)
                int.TryParse(TrackerText.NormalizeText(seedsElement.TextContent), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out seeds);
            if (peersElement is not null)
                int.TryParse(TrackerText.NormalizeText(peersElement.TextContent), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out peers);

            var now = DateTimeOffset.UtcNow;
            var date = now.UtcDateTime;
            var dateText = TrackerText.NormalizeText(cells[0].TextContent);
            var dateParts = dateText.Split([' ', '&', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
            var dateObserved = dateParts.Length >= 3;
            if (dateObserved)
            {
                date = DateTime.SpecifyKind(
                    TrackerText.ParseRuShortDate(dateParts[0], dateParts[1], dateParts[2]), DateTimeKind.Utc);
            }

            int? comments = null;
            if (peerIndex - titleIndex == 3)
            {
                var commentMatch = Regex.Match(TrackerText.NormalizeText(cellList[titleIndex + 1].TextContent), @"\d+");
                if (commentMatch.Success && int.TryParse(commentMatch.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var parsedComments))
                    comments = parsedComments;
            }

            var downloadHref = titleCell.QuerySelector("a[href^='/download/']")?.GetAttribute("href");
            var downloadUrl = SafeUrl.ToSafePublicUrl(downloadHref, url);
            var normalized = ParseListTitle(title);
            var observed = TorrentObservedFields.Title | TorrentObservedFields.Types;
            if (sizeObserved) observed |= TorrentObservedFields.Size;
            if (seedsElement is not null) observed |= TorrentObservedFields.Seeders;
            if (peersElement is not null) observed |= TorrentObservedFields.Leechers;
            if (dateObserved) observed |= TorrentObservedFields.PublishDate;
            if (normalized.Name is not null || normalized.OriginalName is not null)
                observed |= TorrentObservedFields.Names;
            if (normalized.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
            if (normalized.Quality > 0) observed |= TorrentObservedFields.Quality;
            if (normalized.VideoType is not null) observed |= TorrentObservedFields.VideoType;
            if (normalized.Seasons.Count > 0) observed |= TorrentObservedFields.Seasons;

            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = sourceKey,
                ["sourceUrl"] = url,
                ["categoryId"] = categoryId,
                ["types"] = types,
                ["title"] = title,
                ["downloadUrl"] = downloadUrl,
                ["publishedAtRaw"] = dateText,
                ["sizeLabel"] = sizeObserved ? sizeName : null,
                ["sizeBytes"] = sizeObserved ? size : null,
                ["seeders"] = seedsElement is null ? null : seeds,
                ["leechers"] = peersElement is null ? null : peers,
                ["comments"] = comments,
                ["name"] = normalized.Name,
                ["originalName"] = normalized.OriginalName,
                ["releaseYear"] = normalized.Year > 0 ? normalized.Year : null,
                ["quality"] = normalized.Quality > 0 ? normalized.Quality : null,
                ["videoType"] = normalized.VideoType,
                ["seasons"] = normalized.Seasons.Count > 0 ? normalized.Seasons : null
            };

            list.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Title = title,
                Url = url,
                Size = size,
                SizeName = sizeName,
                Sid = seeds,
                Pir = peers,
                Name = normalized.Name,
                OriginalName = normalized.OriginalName,
                ReleaseYear = normalized.Year,
                CreateTime = date,
                UpdateTime = now.UtcDateTime,
                Types = types,
                Quality = normalized.Quality,
                VideoType = normalized.VideoType,
                Seasons = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                Source = new TorrentSourceSnapshot(
                    Tracker,
                    sourceKey,
                    url,
                    observed,
                    TrackerDetailsState.Partial,
                    TrackerPayload.SchemaVersion,
                    ParserVersion,
                    TrackerPayload.Create(ParserVersion, listPayload, TrackerPayload.EmptyObject,
                        TrackerPayload.EmptyObject, []),
                    now,
                    null,
                    null)
            });
        }

        return list;
    }

    private static (string? Name, string? OriginalName, int Year, int Quality, string? VideoType,
        HashSet<int> Seasons) ParseListTitle(string title)
    {
        var year = TrackerText.ExtractYear(title);
        var yearMatch = Regex.Match(title, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var namesPart = yearMatch.Success ? title[..yearMatch.Index].Trim().TrimEnd('(').Trim() : title;
        var names = namesPart.Split(" / ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var videoMatch = Regex.Match(title,
            @"\b(?:UHD\s+BDRemux|UHD\s+BDRip(?:-HEVC)?|Blu-Ray\s+Remux|BDRemux|BDRip(?:-(?:HEVC|AVC))?|WEB-DL(?:Rip)?(?:-HEVC)?|WEBRip|HDRip(?:-AVC)?|DVDRip)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var seasons = new HashSet<int>();
        foreach (Match match in Regex.Matches(title,
                     @"\[(?<from>\d{1,2})(?:-(?<to>\d{1,2}))?\s*сезон",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            AddSeasonRange(match, seasons);
        foreach (Match match in Regex.Matches(title,
                     @"\bS(?<from>\d{1,2})(?:-(?<to>\d{1,2}))?\b",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            AddSeasonRange(match, seasons);
        return (names.FirstOrDefault(), names.Length > 1 ? names[^1] : null, year,
            StringConvert.ParseQuality(title), videoMatch.Success ? videoMatch.Value : null, seasons);
    }

    private static void AddSeasonRange(Match match, HashSet<int> seasons)
    {
        var from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
        var to = match.Groups["to"].Success
            ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
            : from;
        if (from is <= 0 or >= 100 || to < from || to - from > 30)
            return;
        for (var season = from; season <= to; season++)
            seasons.Add(season);
    }
}
