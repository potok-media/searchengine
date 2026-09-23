using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     NNMClub fact folding: hidden spoiler sections (.sp-wrap) extraction and mapping of the
///     labelled facts onto the normalized torrent fields. Momentary telemetry labels
///     (transfer speed, last-seen seed, online time) are dropped before persistence.
/// </summary>
public partial class BaseNNMClub
{
    private static Dictionary<string, object?>[] ExtractHiddenSections(IElement body) =>
        body.QuerySelectorAll(".sp-wrap").Select(section =>
        {
            var title = TrackerText.NormalizeText(section.QuerySelector(".sp-head")?.TextContent ?? string.Empty);
            var content = section.QuerySelector(".sp-body") ?? section;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = NullIfEmpty(title),
                ["text"] = NullIfEmpty(ExtractTextWithBreaks(content))
            };
        }).Where(section =>
            (section["title"] is not null || section["text"] is not null) &&
            !IsMomentaryTelemetryLabel(section["title"]?.ToString() ?? string.Empty)).ToArray();

    private static void ApplyNormalizedDetails(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyCollection<Dictionary<string, object?>> hiddenSections,
        ref TorrentObservedFields observed)
    {
        if (facts.TryGetValue("Название", out var name))
        {
            torrent.Name = name;
            observed |= TorrentObservedFields.Names;
        }
        if (facts.TryGetValue("Оригинальное название", out var originalName))
        {
            torrent.OriginalName = originalName;
            observed |= TorrentObservedFields.Names;
        }
        if (facts.TryGetValue("Год выпуска", out var yearText) &&
            Regex.Match(yearText, @"(?:19|20)\d{2}") is { Success: true } yearMatch &&
            int.TryParse(yearMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            torrent.ReleaseYear = year;
            observed |= TorrentObservedFields.ReleaseYear;
        }
        if (facts.TryGetValue("Качество видео", out var quality))
        {
            torrent.Quality = StringConvert.ParseQuality(quality);
            observed |= TorrentObservedFields.Quality;
            var videoMatch = Regex.Match(quality,
                @"\b(?:UHD\s+BDRemux|UHD\s+BDRip|BDRemux|BDRip|WEB-DL(?:Rip)?|WEBRip|HDRip|DVDRip|HDTVRip?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (videoMatch.Success)
            {
                torrent.VideoType = videoMatch.Value;
                observed |= TorrentObservedFields.VideoType;
            }
        }

        var mediaInfo = hiddenSections.FirstOrDefault(section =>
            string.Equals(section["title"]?.ToString(), "MediaInfo", StringComparison.OrdinalIgnoreCase));
        var mediaInfoText = mediaInfo?["text"]?.ToString();
        if (torrent.VideoType is null && Regex.Match(mediaInfoText ?? string.Empty,
                @"(?:^|\n)Format\s*:\s*(?<format>[^\r\n]+)", RegexOptions.IgnoreCase) is { Success: true } format)
        {
            torrent.VideoType = format.Groups["format"].Value.Trim();
            observed |= TorrentObservedFields.VideoType;
        }

        var languageEvidence = facts.Where(pair =>
            pair.Key.StartsWith("Аудио", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pair.Key, "Субтитры", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (languageEvidence.Length > 0)
        {
            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in languageEvidence.Select(pair => pair.Value))
            {
                if (value.Contains("рус", StringComparison.OrdinalIgnoreCase)) languages.Add("Русский");
                if (value.Contains("англ", StringComparison.OrdinalIgnoreCase)) languages.Add("Английский");
                if (value.Contains("укра", StringComparison.OrdinalIgnoreCase)) languages.Add("Украинский");
                if (value.Contains("япон", StringComparison.OrdinalIgnoreCase)) languages.Add("Японский");
            }
            torrent.Languages = languages;
            observed |= TorrentObservedFields.Languages;
        }
        if (facts.TryGetValue("Перевод", out var translation))
        {
            torrent.Voices = [translation];
            observed |= TorrentObservedFields.Voices;
        }
    }

    private static bool IsMomentaryTelemetryLabel(string label) =>
        Regex.IsMatch(
            label,
            @"скорост|(?:upload|download|transfer)\s*(?:speed|rate)|сид(?:ер)?\s*(?:был|замечен)|последн(?:ий|яя)?\s*сид|last\s*seed|seed(?:er)?\s*(?:last\s*)?seen|время\s*(?:онлайн|раздачи)|time\s*online",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ExtractTextWithBreaks(IElement element)
    {
        var builder = new StringBuilder();
        AppendText(element, builder);
        return string.Join('\n', builder.ToString().Split('\n')
            .Select(TrackerText.NormalizeText).Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void AppendText(INode node, StringBuilder builder)
    {
        if (node is IElement { LocalName: "br" })
        {
            builder.AppendLine();
            return;
        }
        if (node.NodeType == NodeType.Text) builder.Append(node.TextContent);
        foreach (var child in node.ChildNodes) AppendText(child, builder);
    }
}
