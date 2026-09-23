using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Kinozal browse-list side: windows-1251 query encoding, search-surface detection and
///     browse.php row parsing into torrents with a complete list-observation payload.
///     Rows outside the video-category allowlist are skipped entirely.
/// </summary>
public partial class BaseKinozal
{
    protected static bool IsSearchSurface(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelector("form[action*='browse.php'], table tr td.bt, td.nam a[href*='details.php?id=']")
               is not null;
    }

    /// <summary>windows-1251 percent-encoding: Kinozal does not understand UTF-8 queries.</summary>
    protected static string EncodeQuery(string query)
    {
        var bytes = RuEncoding.GetBytes(query);
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
        {
            var character = (char)value;
            if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'0' and <= (byte)'9' || character is '-' or '_' or '.' or '~')
                builder.Append(character);
            else if (character == ' ')
                builder.Append('+');
            else
                builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    protected static IReadOnlyCollection<TorrentDetails> ParseBrowsePage(string html, string host)
    {
        var list = new List<TorrentDetails>();
        var document = new HtmlParser().ParseDocument(html);
        var now = DateTimeOffset.UtcNow;

        foreach (var row in document.QuerySelectorAll("tr").Where(row => row.QuerySelector("td.bt") is not null))
        {
            var categoryElement = row.QuerySelector("td.bt [onclick*='cat(']");
            var catMatch = Regex.Match(categoryElement?.GetAttribute("onclick") ?? string.Empty,
                @"cat\(\s*(?<id>\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!catMatch.Success) continue;
            var catId = catMatch.Groups["id"].Value;
            var types = GetTypes(catId);
            if (types == null) continue;

            var titleLink = row.QuerySelector("td.nam a[href*='details.php?id='], a[href*='details.php?id=']");
            var idMatch = Regex.Match(WebUtility.HtmlDecode(titleLink?.GetAttribute("href") ?? string.Empty),
                @"(?:\?|&)id=(?<id>\d+)(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (titleLink is null || !idMatch.Success) continue;
            var id = idMatch.Groups["id"].Value;
            var title = TrackerText.NormalizeText(titleLink.TextContent);
            if (string.IsNullOrWhiteSpace(title)) continue;
            var url = $"{host}/details.php?id={id}";

            var cells = row.QuerySelectorAll("td").ToArray();
            var sizeCell = cells.Length >= 4 ? cells[3] : row.QuerySelectorAll("td.s").FirstOrDefault(cell =>
                Regex.IsMatch(TrackerText.NormalizeText(cell.TextContent), @"\b(?:ТБ|ГБ|МБ|КБ|TB|GB|MB|KB)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            var sizeText = TrackerText.NormalizeText(sizeCell?.TextContent ?? string.Empty);
            var sizeMatch = Regex.Match(sizeText,
                @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>ТБ|ГБ|МБ|КБ|TB|GB|MB|KB)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            long size = 0;
            string? sizeName = null;
            if (sizeMatch.Success)
            {
                sizeName = sizeMatch.Value;
                size = TrackerText.ParseSize(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
            }

            var seedCell = row.QuerySelector("td.sl_s") ?? (cells.Length >= 5 ? cells[4] : null);
            var peerCell = row.QuerySelector("td.sl_p") ?? (cells.Length >= 6 ? cells[5] : null);
            int seeds = 0, peers = 0;
            var seedObserved = TryParseInteger(seedCell?.TextContent, out seeds);
            var peerObserved = TryParseInteger(peerCell?.TextContent, out peers);

            var dateText = cells.Length >= 7 ? TrackerText.NormalizeText(cells[6].TextContent) : string.Empty;
            var dateObserved = TryParseDate(dateText, now, out var createTime);

            var normalized = ParseListTitle(title);
            var commentsCell = cells.Length >= 3 ? cells[2] : null;
            var comments = TryParseInteger(commentsCell?.TextContent, out var parsedComments)
                ? parsedComments
                : (int?)null;
            var categoryName = TrackerText.NormalizeText(categoryElement?.GetAttribute("alt") ??
                                                         categoryElement?.GetAttribute("title") ?? string.Empty);
            var observed = TorrentObservedFields.Title | TorrentObservedFields.Types;
            if (sizeMatch.Success) observed |= TorrentObservedFields.Size;
            if (seedObserved) observed |= TorrentObservedFields.Seeders;
            if (peerObserved) observed |= TorrentObservedFields.Leechers;
            if (dateObserved) observed |= TorrentObservedFields.PublishDate;
            if (normalized.Name is not null || normalized.OriginalName is not null)
                observed |= TorrentObservedFields.Names;
            if (normalized.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
            if (normalized.Quality > 0) observed |= TorrentObservedFields.Quality;
            if (normalized.VideoType is not null) observed |= TorrentObservedFields.VideoType;
            if (normalized.Seasons.Count > 0) observed |= TorrentObservedFields.Seasons;

            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = id,
                ["sourceUrl"] = url,
                ["title"] = title,
                ["categoryId"] = catId,
                ["categoryName"] = NullIfEmpty(categoryName),
                ["types"] = types,
                ["comments"] = comments,
                ["sizeBytes"] = sizeMatch.Success ? size : null,
                ["sizeLabel"] = sizeMatch.Success ? sizeName : null,
                ["seeders"] = seedObserved ? seeds : null,
                ["leechers"] = peerObserved ? peers : null,
                ["publishedAtRaw"] = NullIfEmpty(dateText),
                ["name"] = normalized.Name,
                ["originalName"] = normalized.OriginalName,
                ["releaseYear"] = normalized.Year > 0 ? normalized.Year : null,
                ["quality"] = normalized.Quality > 0 ? normalized.Quality : null,
                ["videoType"] = normalized.VideoType,
                ["seasons"] = normalized.Seasons.Count > 0 ? normalized.Seasons : null
            };

            list.Add(new TorrentDetails
            {
                TrackerName = "kinozal",
                Types = types,
                Url = url,
                Title = title,
                Sid = seeds,
                Pir = peers,
                Size = size,
                SizeName = sizeName,
                CreateTime = createTime,
                ReleaseYear = normalized.Year,
                Name = normalized.Name,
                OriginalName = normalized.OriginalName,
                Quality = normalized.Quality,
                VideoType = normalized.VideoType,
                Seasons = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                UpdateTime = now.UtcDateTime,
                Source = new TorrentSourceSnapshot(
                    TrackerType.Kinozal,
                    id,
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

    private static string[]? GetTypes(string cat)
    {
        return cat switch
        {
            "8" or "6" or "15" or "17" or "35" or "39" or "13" or "14" or "24" or "11" or "9" or "47"
                or "12" or "10" or "7" or "16" => ["movie"],
            "18" => ["documovie"],
            "37" => ["sport"],
            "45" or "46" => ["serial"],
            "38" or "48" or "49" or "50" => ["tvshow"],
            "21" or "22" => ["multfilm", "multserial"],
            "20" => ["anime"],
            _ => null
        };
    }

    private static (string? Name, string? OriginalName, int Year, int Quality, string? VideoType,
        HashSet<int> Seasons) ParseListTitle(string title)
    {
        var yearMatch = Regex.Match(title, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var year = yearMatch.Success && int.TryParse(yearMatch.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedYear) ? parsedYear : 0;
        var namesPart = yearMatch.Success ? title[..yearMatch.Index].Trim().TrimEnd('(').Trim() : title;
        var names = Regex.Split(namesPart, @"\s+/\s+")
            .Select(TrackerText.NormalizeText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var name = names.FirstOrDefault(value => Regex.IsMatch(value, @"\p{IsCyrillic}")) ?? names.FirstOrDefault();
        var originalName = names.FirstOrDefault(value => !Regex.IsMatch(value, @"\p{IsCyrillic}"));
        var videoMatch = Regex.Match(title,
            @"\b(?:UHD\s+BDRemux|UHD\s+BDRip(?:-HEVC)?|Blu-Ray\s+Remux|BDRemux|BDRip(?:-(?:HEVC|AVC))?|WEB-DL(?:Rip)?(?:-HEVC)?|WEBRip|HDRip(?:-AVC)?|DVDRip|HDTVRip?)\b",
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

    private static bool TryParseInteger(string? raw, out int value)
    {
        var match = Regex.Match(raw ?? string.Empty, @"\d+");
        return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDate(string raw, DateTimeOffset now, out DateTime value)
    {
        value = now.UtcDateTime;
        var normalized = TrackerText.NormalizeText(raw).Replace(" в ", " ", StringComparison.OrdinalIgnoreCase);
        if (DateTime.TryParseExact(normalized, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed))
        {
            value = parsed.ToUniversalTime();
            return true;
        }
        return false;
    }
}
