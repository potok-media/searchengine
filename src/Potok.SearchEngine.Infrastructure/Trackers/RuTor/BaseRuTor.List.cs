using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTor;

/// <summary>
///     RuTor search-result list side: parses #index rows into torrents with a complete
///     list-observation payload. Every yielded item carries a Source snapshot; rows without
///     a valid magnet identity are skipped.
/// </summary>
public partial class BaseRuTor
{
    protected static readonly IReadOnlyList<RuTorCategory> VideoCategories =
    [
        new(1, "movie"),
        new(5, "movie"),
        new(12, "documovie", "docuserial"),
        new(4, "serial"),
        new(16, "serial"),
        new(6, "tvshow"),
        new(7, "multfilm", "multserial"),
        new(10, "anime"),
        new(13, "sport")
    ];

    protected string BuildSearchUrl(RuTorCategory category, string query) =>
        $"{Host}search/0/{category.Id}/100/2/{Uri.EscapeDataString(query)}";

    protected IReadOnlyCollection<TorrentDetails> Parse(string html, RuTorCategory category)
    {
        var list = new List<TorrentDetails>();
        var document = new HtmlParser().ParseDocument(html);
        var rows = document.QuerySelectorAll("#index tr.gai, #index tr.tum");

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 4) continue;

            var dateCell = cells[0];
            var titleCell = cells[1];
            var sizeCell = cells.Length > 3 ? cells[^2] : null;
            var seedsPeersCell = cells.Length > 3 ? cells[^1] : null;

            if (cells.Length == 5)
            {
                titleCell = cells[1];
                sizeCell = cells[3];
                seedsPeersCell = cells[4];
            }

            var magnetLink = titleCell.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(magnetLink)) continue;

            var titleLink = titleCell.QuerySelector("a[href^='/torrent/']");
            if (titleLink == null) continue;

            var title = TrackerText.NormalizeText(titleLink.TextContent);
            var url = titleLink.GetAttribute("href");
            string? sourceKey = null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                var match = Regex.Match(url, @"^/torrent/\d+");
                if (match.Success)
                {
                    sourceKey = match.Value;
                    url = Host.TrimEnd('/') + sourceKey;
                }
                else if (!url.StartsWith("http"))
                {
                    url = Host.TrimEnd('/') + url;
                }
            }

            if (sourceKey is null || string.IsNullOrWhiteSpace(url))
                continue;

            var magnetRaw = WebUtility.HtmlDecode(magnetLink);
            var infoHash = MagnetBuilder.HashFromMagnet(magnetRaw);
            if (infoHash is null)
                continue;

            long size = 0;
            string? sizeName = null;
            var sizeObserved = false;
            if (sizeCell != null)
            {
                var sizeText = sizeCell.TextContent.Trim();
                var sizeParts = sizeText.Split([' ', '&', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
                if (sizeParts.Length >= 2)
                {
                    size = TrackerText.ParseSize(sizeParts[0], sizeParts[1]);
                    sizeName = $"{sizeParts[0]} {sizeParts[1]}";
                    sizeObserved = true;
                }
            }

            var seeds = 0;
            var peers = 0;
            IElement? seedsElement = null;
            IElement? peersElement = null;
            if (seedsPeersCell != null)
            {
                seedsElement = seedsPeersCell.QuerySelector("span.green");
                peersElement = seedsPeersCell.QuerySelector("span.red");

                if (seedsElement != null)
                {
                    var seedsMatch = Regex.Match(seedsElement.TextContent.Trim(), @"\d+");
                    if (seedsMatch.Success)
                        int.TryParse(seedsMatch.Value, out seeds);
                }

                if (peersElement != null)
                {
                    var peersMatch = Regex.Match(peersElement.TextContent.Trim(), @"\d+");
                    if (peersMatch.Success)
                        int.TryParse(peersMatch.Value, out peers);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var date = now.UtcDateTime;
            var dateObserved = false;
            var dateText = string.Empty;
            if (dateCell != null)
            {
                dateText = TrackerText.NormalizeText(dateCell.TextContent);
                var dateParts = dateText.Split([' ', '&', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
                if (dateParts.Length >= 3)
                {
                    date = DateTime.SpecifyKind(
                        TrackerText.ParseRuShortDate(dateParts[0], dateParts[1], dateParts[2]), DateTimeKind.Utc);
                    dateObserved = true;
                }
            }

            var normalized = ParseListTitle(title);
            var types = ResolveTypes(category, title, normalized.Seasons);

            var observed = TorrentObservedFields.Title | TorrentObservedFields.Magnet |
                           TorrentObservedFields.InfoHash | TorrentObservedFields.Types;
            if (sizeObserved) observed |= TorrentObservedFields.Size;
            if (seedsElement is not null) observed |= TorrentObservedFields.Seeders;
            if (peersElement is not null) observed |= TorrentObservedFields.Leechers;
            if (dateObserved) observed |= TorrentObservedFields.PublishDate;
            if (normalized.Name is not null) observed |= TorrentObservedFields.Names;
            if (normalized.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
            if (normalized.Quality > 0) observed |= TorrentObservedFields.Quality;
            if (normalized.VideoType is not null) observed |= TorrentObservedFields.VideoType;
            if (normalized.Seasons.Count > 0) observed |= TorrentObservedFields.Seasons;

            var comments = ReadComments(cells, titleCell, sizeCell);
            var listPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceKey"] = sourceKey,
                ["sourceUrl"] = url,
                ["title"] = title,
                ["magnet"] = magnetRaw,
                ["infoHash"] = infoHash,
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
                ["seasons"] = normalized.Seasons.Count > 0 ? normalized.Seasons : null,
                ["categoryId"] = category.Id,
                ["types"] = types
            };

            list.Add(new TorrentDetails
            {
                TrackerName = TrackerName,
                Title = title,
                Url = url,
                Magnet = magnetRaw,
                InfoHash = infoHash,
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
                    TrackerDetailsState.NotRequired,
                    TrackerPayload.SchemaVersion,
                    ParserVersion,
                    TrackerPayload.Create(ParserVersion, listPayload, TrackerPayload.EmptyObject,
                        new Dictionary<string, object?>
                        {
                            ["comments"] = comments
                        }, []),
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
        var yearMatch = Regex.Match(title, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var year = yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear) ? parsedYear : 0;
        var namesPart = yearMatch.Success ? title[..yearMatch.Index].Trim().TrimEnd('(').Trim() : title;
        var names = namesPart.Split(" / ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var videoMatch = Regex.Match(title,
            @"\b(?:UHD\s+BDRemux|UHD\s+BDRip(?:-HEVC)?|BDRemux|BDRip(?:-HEVC)?|WEB-DL(?:Rip)?|WEBRip|HDRip(?:-AVC)?|DVDRip)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var seasons = new HashSet<int>();
        foreach (Match match in Regex.Matches(title, @"\bS(?<from>\d{1,2})(?:-(?<to>\d{1,2}))?\b",
                     RegexOptions.IgnoreCase))
        {
            var from = int.Parse(match.Groups["from"].Value, CultureInfo.InvariantCulture);
            var to = match.Groups["to"].Success
                ? int.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture)
                : from;
            if (from is > 0 and < 100 && to >= from && to - from <= 30)
                for (var season = from; season <= to; season++)
                    seasons.Add(season);
        }
        return (names.FirstOrDefault(), names.Length > 1 ? names[^1] : null, year,
            StringConvert.ParseQuality(title), videoMatch.Success ? videoMatch.Value : null, seasons);
    }

    private static string[] ResolveTypes(RuTorCategory category, string title, IReadOnlyCollection<int> seasons)
    {
        if (category.EpisodicType is null)
            return [category.Type];
        var episodic = seasons.Count > 0 || Regex.IsMatch(title,
            @"\b(?:сезон|серии?|S\d{1,2}|E\d{1,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return [episodic ? category.EpisodicType : category.Type];
    }

    private static int? ReadComments(
        IHtmlCollection<IElement> cells,
        IElement titleCell,
        IElement? sizeCell)
    {
        if (sizeCell is null)
            return null;
        var materialized = cells.ToList();
        var titleIndex = materialized.IndexOf(titleCell);
        var sizeIndex = materialized.IndexOf(sizeCell);
        if (titleIndex < 0 || sizeIndex - titleIndex != 2)
            return null;
        return int.TryParse(TrackerText.NormalizeText(materialized[titleIndex + 1].TextContent),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var comments)
            ? comments
            : null;
    }

    protected sealed record RuTorCategory(int Id, string Type, string? EpisodicType = null);
}
