using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     RuTracker search-result list side: parses tracker.php rows (tr.hl-tr) into torrents
///     with a complete list-observation payload. Rows without a known video category are
///     skipped. Category map lives in BaseRuTracker.Categories.cs, title parsing in
///     BaseRuTracker.Titles.cs.
/// </summary>
public partial class BaseRuTracker
{
    protected static string BuildQueryUrl(string host, string query, int page)
    {
        var baseUrl = $"{host.TrimEnd('/')}/forum/tracker.php?nm={Uri.EscapeDataString(query)}&o=10&s=2";
        return page <= 0 ? baseUrl : $"{baseUrl}&start={page * 50}";
    }

    protected IReadOnlyCollection<TorrentDetails> ParseForumPage(
        string html,
        string categoryId,
        string host,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var results = new List<TorrentDetails>();
        var baseForumUri = new Uri(new Uri(host), "forum/");

        var document = _parser.ParseDocument(html);
        var rows = document.QuerySelectorAll("tr.hl-tr");

        foreach (var row in rows)
        {
            var linkElement = row.QuerySelector("a.tLink");
            if (linkElement == null)
                continue;

            var href = NormalizeHref(linkElement.GetAttribute("href"));
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var title = TrackerText.NormalizeText(linkElement.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var url = new Uri(baseForumUri, href).ToString();

            var sizeName = string.Empty;
            var sizeBytes = 0L;
            var sizeObserved = false;

            var sizeElement = row.QuerySelector("a.small.tr-dl");
            if (sizeElement != null)
            {
                if (TrackerText.TryParseSize(sizeElement.TextContent.Trim(), out var parsedSizeName, out var parsedSizeBytes))
                {
                    sizeObserved = true;
                    sizeName = parsedSizeName;
                    sizeBytes = parsedSizeBytes;
                }
            }
            else
            {
                var sizeTd = row.QuerySelector("td.tor-size");
                if (sizeTd != null && long.TryParse(sizeTd.GetAttribute("data-ts_text"), out var bytes))
                {
                    sizeObserved = true;
                    sizeBytes = bytes;
                    sizeName = TrackerText.FormatSize(bytes);
                }
            }

            var publishDateValue = TryParsePublishDate(row);
            var publishDate = publishDateValue ?? now;
            var publishElement = row.QuerySelectorAll("[data-ts_text]")
                .FirstOrDefault(element => !element.ClassList.Contains("tor-size") &&
                                           long.TryParse(element.GetAttribute("data-ts_text"), out _));

            var rowCategoryId = categoryId;
            if (!TryGetCategory(rowCategoryId, out var category))
            {
                rowCategoryId = ExtractCategoryId(row);
                if (!TryGetCategory(rowCategoryId, out category))
                    continue;
            }

            var (name, originalName, releaseYear) = ParseTitle(title, category);
            if (category.Parser == CategoryParser.Generic && SeasonMarkerRegex().IsMatch(title))
                continue;

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Regex.Split(title, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
            }

            var seedElement = row.QuerySelector("b.seedmed");
            var leechElement = row.QuerySelector("td.leechmed");
            var seedCell = row.QuerySelector("td.seedmed");

            var sid = TrackerText.ParseInt((seedElement ?? seedCell)?.TextContent);
            var pir = leechElement != null ? TrackerText.ParseInt(leechElement.TextContent) : 0;

            var sourceKey = ExtractSourceKey(url);
            var observed = TorrentObservedFields.Title |
                           TorrentObservedFields.Names |
                           TorrentObservedFields.Types;
            if (sizeObserved) observed |= TorrentObservedFields.Size;
            if (seedElement != null || seedCell != null)
                observed |= TorrentObservedFields.Seeders;
            if (leechElement != null) observed |= TorrentObservedFields.Leechers;
            if (publishDateValue.HasValue) observed |= TorrentObservedFields.PublishDate;
            if (releaseYear > 0) observed |= TorrentObservedFields.ReleaseYear;

            var categoryName = FindCategoryName(row, rowCategoryId);
            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = sourceKey,
                ["sourceUrl"] = url,
                ["categoryId"] = rowCategoryId,
                ["category"] = categoryName,
                ["title"] = title,
                ["sizeLabel"] = sizeObserved ? sizeName : null,
                ["sizeBytes"] = sizeObserved ? sizeBytes : null,
                ["seeders"] = (observed & TorrentObservedFields.Seeders) != 0 ? sid : null,
                ["seedersRaw"] = TrackerText.NormalizeText(seedElement?.TextContent ?? seedCell?.TextContent),
                ["leechers"] = (observed & TorrentObservedFields.Leechers) != 0 ? pir : null,
                ["leechersRaw"] = TrackerText.NormalizeText(leechElement?.TextContent),
                ["publishedAt"] = publishDateValue?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["publishedAtUnix"] = publishElement?.GetAttribute("data-ts_text"),
                ["publishedAtRaw"] = TrackerText.NormalizeText(publishElement?.TextContent),
                ["name"] = name,
                ["originalName"] = originalName,
                ["releaseYear"] = releaseYear > 0 ? releaseYear : null,
                ["types"] = category.Types
            };

            results.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Types = category.Types,
                Url = url,
                Title = title,
                Sid = sid,
                Pir = pir,
                SizeName = string.IsNullOrWhiteSpace(sizeName) ? null : sizeName,
                CreateTime = publishDate,
                UpdateTime = now,
                Name = name,
                OriginalName = originalName,
                ReleaseYear = releaseYear,
                Size = sizeBytes,
                Source = new TorrentSourceSnapshot(
                    Tracker,
                    sourceKey,
                    url,
                    observed,
                    TrackerDetailsState.Partial,
                    TrackerPayload.SchemaVersion,
                    ParserVersion,
                    TrackerPayload.Create(ParserVersion, listPayload, TrackerPayload.EmptyObject,
                        new Dictionary<string, object?>
                        {
                            ["categoryId"] = rowCategoryId,
                            ["category"] = categoryName
                        }, []),
                    new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
                    null,
                    null)
            });
        }

        return results;
    }

    private static string ExtractSourceKey(string url)
    {
        var match = Regex.Match(url, @"(?:\?|&)t=(?<id>\d+)(?:&|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : url;
    }

    private static string? FindCategoryName(IElement row, string categoryId)
    {
        foreach (var link in row.QuerySelectorAll("a"))
        {
            var href = link.GetAttribute("href") ?? string.Empty;
            if (href.Contains($"={categoryId}", StringComparison.OrdinalIgnoreCase))
                return TrackerText.NormalizeText(link.TextContent);
        }
        return null;
    }

    private static DateTime? TryParsePublishDate(IElement row)
    {
        foreach (var element in row.QuerySelectorAll("[data-ts_text]"))
        {
            if (element.ClassList.Contains("tor-size")) continue;

            if (!long.TryParse(element.GetAttribute("data-ts_text"), out var ts)) continue;

            // 2000 year = 946684800
            // 2100 year = 4102444800
            if (ts > 946684800L && ts < 4102444800L)
                return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        }

        return null;
    }

    private static string NormalizeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return string.Empty;

        href = WebUtility.HtmlDecode(href).Trim();
        if (href.StartsWith("./", StringComparison.Ordinal))
            href = href[2..];

        if (href.StartsWith("viewtopic.php", StringComparison.OrdinalIgnoreCase))
            return href;

        if (href.StartsWith("/forum/", StringComparison.OrdinalIgnoreCase))
            return href["/forum/".Length..];

        if (href.StartsWith("forum/", StringComparison.OrdinalIgnoreCase))
            return href["forum/".Length..];

        return href;
    }

    private static string ExtractCategoryId(IElement row)
    {
        foreach (var link in row.QuerySelectorAll("a"))
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var match = Regex.Match(href, @"tracker\.php\?f(?:%5B%5D|\[\])?=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;

            match = Regex.Match(href, @"viewforum\.php\?f=(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["id"].Value;
        }

        return string.Empty;
    }

    [GeneratedRegex("(Сезон|Серии)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonMarkerRegex();
}
