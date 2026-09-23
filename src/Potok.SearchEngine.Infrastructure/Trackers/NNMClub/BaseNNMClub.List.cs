using System.Globalization;
using System.Text.RegularExpressions;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     NNMClub search-result list side: parses tracker.php rows into torrents with a complete
///     list-observation payload. Every yielded item carries a Source snapshot; detail fetches
///     happen later only for allowed video forums. Momentary telemetry (the transfer-speed
///     cell title) is deliberately not mapped; seeders/leechers are kept.
/// </summary>
public partial class BaseNNMClub
{
    protected IReadOnlyCollection<TorrentDetails> ParseTrackerPage(string html)
    {
        var list = new List<TorrentDetails>();
        var document = _parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("tr.prow1, tr.prow2");
        foreach (var row in rows)
        {
            var titleLink = row.QuerySelector("a.topictitle[href*='viewtopic.php?t='], a.topicpremod[href*='viewtopic.php?t=']") ??
                            row.QuerySelectorAll("a[href*='viewtopic.php?t=']").FirstOrDefault(link =>
                                (link.Closest("td")?.GetAttribute("title") ?? string.Empty)
                                .Contains("by:", StringComparison.OrdinalIgnoreCase));
            var topicId = MatchQueryId(titleLink?.GetAttribute("href"), "t");
            if (titleLink is null || topicId is null)
                continue;

            var now = DateTimeOffset.UtcNow;
            var sourceUrl = $"{Host}/forum/viewtopic.php?t={topicId}";
            var title = TrackerText.NormalizeText(titleLink.TextContent);
            var cells = row.QuerySelectorAll("td").ToList();
            var titleCell = titleLink.Closest("td");
            var categoryLink = row.QuerySelector("a[href*='tracker.php?c=']");
            var forumLink = row.QuerySelector("a[href*='tracker.php?f=']");
            var categoryId = MatchQueryId(categoryLink?.GetAttribute("href"), "c");
            var forumId = MatchQueryId(forumLink?.GetAttribute("href"), "f");
            var categoryName = TrackerText.NormalizeText(categoryLink?.TextContent ?? string.Empty)
                .Replace("Трекер:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var forumName = TrackerText.NormalizeText(forumLink?.TextContent ?? string.Empty);
            var types = MapTypes(forumId);

            var sizeCell = cells.FirstOrDefault(cell =>
                cell.QuerySelector("u") is not null &&
                Regex.IsMatch(TrackerText.NormalizeText(cell.TextContent), @"\b(?:TB|GB|MB|KB)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            long size = 0;
            string? sizeName = null;
            var sizeObserved = false;
            if (sizeCell is not null)
            {
                var bytesText = TrackerText.NormalizeText(sizeCell.QuerySelector("u")?.TextContent ?? string.Empty);
                var sizeText = TrackerText.NormalizeText(sizeCell.TextContent);
                var sizeMatch = Regex.Match(sizeText, @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>[KMGT]?B)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!long.TryParse(bytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) &&
                    sizeMatch.Success)
                    size = TrackerText.ParseSize(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
                if (sizeMatch.Success)
                    sizeName = $"{sizeMatch.Groups["value"].Value} {sizeMatch.Groups["unit"].Value}";
                sizeObserved = size > 0 || sizeMatch.Success;
            }

            var seedCell = row.QuerySelector("td.seedmed") ?? cells.FirstOrDefault(cell =>
                string.Equals(cell.GetAttribute("title"), "Seeders", StringComparison.OrdinalIgnoreCase));
            var leechCell = row.QuerySelector("td.leechmed") ?? cells.FirstOrDefault(cell =>
                string.Equals(cell.GetAttribute("title"), "Leechers", StringComparison.OrdinalIgnoreCase));
            var seed = ParseInteger(seedCell?.TextContent);
            var leech = ParseInteger(leechCell?.TextContent);
            var leechIndex = leechCell is null ? -1 : cells.IndexOf(leechCell);
            var statsCell = leechIndex >= 0 && leechIndex + 1 < cells.Count ? cells[leechIndex + 1] : null;
            var replies = ParseNullableInteger(statsCell?.TextContent);
            var views = ParseNullableInteger(statsCell?.GetAttribute("title"));

            var dateCell = cells.FirstOrDefault(cell =>
                (cell.GetAttribute("title") ?? string.Empty).Contains("добавлен", StringComparison.OrdinalIgnoreCase));
            var unixText = TrackerText.NormalizeText(dateCell?.QuerySelector("u")?.TextContent ?? string.Empty);
            var dateObserved = long.TryParse(unixText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix);
            var date = dateObserved ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : now.UtcDateTime;
            var publishedAtRaw = TrackerText.NormalizeText(dateCell?.TextContent ?? string.Empty);

            var normalized = ParseTitle(title);
            var observed = TorrentObservedFields.Title;
            if (sizeObserved) observed |= TorrentObservedFields.Size;
            if (seedCell is not null) observed |= TorrentObservedFields.Seeders;
            if (leechCell is not null) observed |= TorrentObservedFields.Leechers;
            if (dateObserved) observed |= TorrentObservedFields.PublishDate;
            if (normalized.Name is not null || normalized.OriginalName is not null)
                observed |= TorrentObservedFields.Names;
            if (normalized.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
            if (types.Length > 0) observed |= TorrentObservedFields.Types;
            if (normalized.Quality > 0) observed |= TorrentObservedFields.Quality;
            if (normalized.VideoType is not null) observed |= TorrentObservedFields.VideoType;
            if (normalized.Seasons.Count > 0) observed |= TorrentObservedFields.Seasons;

            var author = NormalizeAuthor(titleCell?.GetAttribute("title"));
            var status = TrackerText.NormalizeText(titleCell?.QuerySelector(".gensmall[title]")?.GetAttribute("title") ?? string.Empty);
            var uploaderGroup = TrackerText.NormalizeText(titleCell?.QuerySelector(".gensmall.opened")?.TextContent ?? string.Empty);
            var downloadLink = row.QuerySelector("a[href*='download.php?id=']");
            var downloadId = MatchQueryId(downloadLink?.GetAttribute("href"), "id");
            var downloadUrl = ToSafePublicUrl(downloadLink?.GetAttribute("href"), sourceUrl, stripQuery: false);
            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = topicId,
                ["sourceUrl"] = sourceUrl,
                ["title"] = title,
                ["topicClasses"] = titleLink.ClassList.Where(value =>
                    !string.Equals(value, "genmed", StringComparison.OrdinalIgnoreCase)).ToArray(),
                ["categoryId"] = categoryId,
                ["categoryName"] = NullIfEmpty(categoryName),
                ["forumId"] = forumId,
                ["forumName"] = NullIfEmpty(forumName),
                ["forumTaxonomyVersion"] = ForumTaxonomyVersion,
                ["author"] = author,
                ["status"] = NullIfEmpty(status),
                ["uploaderGroup"] = NullIfEmpty(uploaderGroup),
                ["downloadId"] = downloadId,
                ["downloadUrl"] = downloadUrl,
                ["sizeBytes"] = sizeObserved ? size : null,
                ["sizeLabel"] = sizeObserved ? sizeName : null,
                ["seeders"] = seedCell is null ? null : seed,
                ["leechers"] = leechCell is null ? null : leech,
                ["views"] = views,
                ["replies"] = replies,
                ["publishedAtUnix"] = dateObserved ? unix : null,
                ["publishedAtRaw"] = NullIfEmpty(publishedAtRaw),
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
                Types = types,
                Url = sourceUrl,
                Title = title,
                Sid = seed,
                Pir = leech,
                Size = size,
                SizeName = sizeName,
                CreateTime = date,
                UpdateTime = now.UtcDateTime,
                Name = normalized.Name,
                OriginalName = normalized.OriginalName,
                ReleaseYear = normalized.Year,
                Quality = normalized.Quality,
                VideoType = normalized.VideoType,
                Seasons = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                Source = new TorrentSourceSnapshot(
                    Tracker,
                    topicId,
                    sourceUrl,
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

    protected Dictionary<string, string> GetSearchParameters(string query)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "prev_sd", "0" },
            { "prev_a", "0" },
            { "prev_my", "0" },
            { "prev_n", "0" },
            { "prev_shc", "1" },
            { "prev_shf", "1" },
            { "prev_sha", "0" },
            { "prev_shs", "0" },
            { "prev_shr", "0" },
            { "prev_sht", "0" },
            { "f[]", "-1" },
            { "o", "10" },
            { "s", "2" },
            { "tm", "-1" },
            { "shc", "1" },
            { "shf", "1" },
            { "ta", "-1" },
            { "sns", "-1" },
            { "sds", "-1" },
            { "nm", query },
            { "pn", "" },
            { "submit", "Поиск" }
        };
    }

    private static string[] MapTypes(string? forumId) =>
        forumId is not null && ForumTypeMap.TryGetValue(forumId, out var types) ? types : [];

    private static (string? Name, string? OriginalName, int Year, int Quality, string? VideoType,
        HashSet<int> Seasons) ParseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null, 0, 0, null, []);

        var yearMatch = Regex.Match(title, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var year = yearMatch.Success && int.TryParse(yearMatch.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedYear) ? parsedYear : 0;
        var namesPart = yearMatch.Success ? title[..yearMatch.Index].Trim().TrimEnd('(').Trim() : title;
        var parts = Regex.Split(namesPart, @"\s+(?:/|\|)\s+")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        string? name = null;
        string? originalName = null;

        foreach (var part in parts)
        {
            var cleaned = CleanPart(part);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            if (Regex.IsMatch(cleaned, @"\p{IsCyrillic}"))
            {
                if (name == null)
                    name = cleaned;
            }
            else
            {
                if (originalName == null)
                    originalName = cleaned;
            }
        }

        if (name == null && originalName != null)
            name = originalName;

        var videoMatch = Regex.Match(title,
            @"\b(?:UHD\s+BDRemux|UHD\s+BDRip(?:-HEVC)?|BDRemux|BDRip(?:-HEVC)?|WEB-DL(?:Rip)?|WEBRip|HDRip(?:-AVC)?|DVDRip|HDTVRip?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var seasons = new HashSet<int>();
        foreach (Match match in Regex.Matches(title,
                     @"(?:\bS|сезон\s+)(?<from>\d{1,2})(?:-(?<to>\d{1,2}))?",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            var to = match.Groups["to"].Success
                ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
                : from;
            if (from is > 0 and < 100 && to >= from && to - from <= 30)
                for (var season = from; season <= to; season++) seasons.Add(season);
        }
        return (name, originalName, year, StringConvert.ParseQuality(title),
            videoMatch.Success ? videoMatch.Value : null, seasons);
    }

    private static string CleanPart(string part)
    {
        var indexBracket = part.IndexOf('[');
        if (indexBracket >= 0)
            part = part.Substring(0, indexBracket);

        var indexParen = part.IndexOf('(');
        if (indexParen >= 0)
            part = part.Substring(0, indexParen);

        return part.Trim();
    }
}
