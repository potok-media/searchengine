using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Kinozal fact side: labelled-fact extraction from the topic body, hidden (spoiler)
///     sections, normalized-field application and the scrubbing helpers — sensitive labels,
///     momentary telemetry (never persisted), magnet/URL secret stripping.
/// </summary>
public partial class BaseKinozal
{
    private static Dictionary<string, string> ExtractLabelledFacts(IElement body)
    {
        var facts = LabelledFacts.NewDictionary();
        foreach (var row in body.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("th, td");
            if (cells.Length >= 2)
                LabelledFacts.AddFact(facts, cells[0].TextContent, cells[1].TextContent);
        }
        foreach (var labelElement in body.QuerySelectorAll("b, strong, dt"))
        {
            if (labelElement.Closest(".spoiler, .sp-wrap, .hidewrap") is not null) continue;
            var rawLabel = TrackerText.NormalizeText(labelElement.TextContent);
            var label = rawLabel.TrimEnd(':').Trim();
            if (string.IsNullOrWhiteSpace(label) || label.Length > 80) continue;
            string value;
            if (labelElement.LocalName == "dt" && labelElement.NextElementSibling?.LocalName == "dd")
                value = labelElement.NextElementSibling.TextContent;
            else
                value = ReadFollowingValue(labelElement);
            LabelledFacts.AddFact(facts, label, value);
        }
        return facts;
    }

    private static string ReadFollowingValue(IElement label)
    {
        var parts = new List<string>();
        for (var node = label.NextSibling; node is not null; node = node.NextSibling)
        {
            if (node is IElement element &&
                (element.LocalName == "br" || element.Matches("b, strong, dt")))
                break;
            var value = TrackerText.NormalizeText(node.TextContent).TrimStart(':').Trim();
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
        }
        return string.Join(' ', parts);
    }

    private static Dictionary<string, object?>[] ExtractHiddenSections(IElement body) =>
        body.QuerySelectorAll(".spoiler, .sp-wrap, .hidewrap").Select(section =>
        {
            var title = TrackerText.NormalizeText(
                section.QuerySelector(".sp-head, .spoiler-title, .hidehead")?.TextContent ?? string.Empty);
            var content = section.QuerySelector(".sp-body, .spoiler-body, .hidebody") ?? section;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = NullIfEmpty(title),
                ["text"] = NullIfEmpty(ExtractTextWithBreaks(content))
            };
        }).Where(section =>
            (section["title"] is not null || section["text"] is not null) &&
            !IsMomentaryTelemetryLabel(section["title"]?.ToString() ?? string.Empty) &&
            !IsSensitiveLabel(section["title"]?.ToString() ?? string.Empty)).ToArray();

    private static void ApplyNormalizedDetails(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyCollection<Dictionary<string, object?>> hiddenSections,
        IElement topicBody,
        ref TorrentObservedFields observed)
    {
        var name = TrackerText.FindFact(facts, "Название");
        var originalName = TrackerText.FindFact(facts, "Оригинальное название", "Original title");
        if (name is not null || originalName is not null)
        {
            torrent.Name = name ?? torrent.Name;
            torrent.OriginalName = originalName ?? torrent.OriginalName;
            observed |= TorrentObservedFields.Names;
        }

        var yearRaw = TrackerText.FindFact(facts, "Год выпуска", "Год выхода", "Год");
        var yearMatch = Regex.Match(yearRaw ?? string.Empty, @"(?:19|20)\d{2}");
        if (yearMatch.Success && int.TryParse(yearMatch.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var year))
        {
            torrent.ReleaseYear = year;
            observed |= TorrentObservedFields.ReleaseYear;
        }

        var quality = TrackerText.FindFact(facts, "Качество видео", "Качество");
        if (quality is not null)
        {
            torrent.Quality = StringConvert.ParseQuality(quality);
            observed |= TorrentObservedFields.Quality;
        }

        var mediaInfo = hiddenSections.FirstOrDefault(section =>
            string.Equals(section["title"]?.ToString(), "MediaInfo", StringComparison.OrdinalIgnoreCase));
        var mediaInfoText = mediaInfo?["text"]?.ToString() ?? string.Empty;
        var formatMatch = Regex.Match(mediaInfoText, @"(?:^|\n)Format\s*:\s*(?<value>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var container = formatMatch.Success
            ? formatMatch.Groups["value"].Value.Trim()
            : TrackerText.FindFact(facts, "Контейнер", "Формат");
        if (!string.IsNullOrWhiteSpace(container))
        {
            torrent.VideoType = container;
            observed |= TorrentObservedFields.VideoType;
        }

        var languageEvidence = facts.Where(pair =>
            pair.Key.StartsWith("Аудио", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pair.Key, "Субтитры", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pair.Key, "Язык", StringComparison.OrdinalIgnoreCase)).ToArray();
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
        var translation = TrackerText.FindFact(facts, "Перевод", "Озвучивание");
        if (translation is not null)
        {
            torrent.Voices = [translation];
            observed |= TorrentObservedFields.Voices;
        }

        var exactBytesRaw = topicBody.QuerySelector("[data-bytes]")?.GetAttribute("data-bytes");
        if (long.TryParse(exactBytesRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactBytes) &&
            exactBytes > 0)
        {
            torrent.Size = exactBytes;
            observed |= TorrentObservedFields.Size;
        }
    }

    private static bool IsSensitiveLabel(string label) =>
        Regex.IsMatch(label, @"cookie|authorization|password|парол|login|логин|token|session|passkey|proxy|user",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Momentary telemetry (transfer rates, last-seen seeds, online time) is never
    ///     persisted. Tracker-specific: also covers "Transfer rate", which the toolkit
    ///     telemetry regex does not.
    /// </summary>
    private static bool IsMomentaryTelemetryLabel(string label) =>
        Regex.IsMatch(label,
            @"скорост|(?:upload|download|transfer)\s*(?:speed|rate)|сид(?:ер)?\s*(?:был|замечен)|последн(?:ий|яя)?\s*сид|last\s*seed|seed(?:er)?\s*(?:last\s*)?seen|время\s*(?:онлайн|раздачи)|time\s*online",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string SanitizeMagnet(string magnet, ICollection<string> warnings)
    {
        var sanitized = MagnetBuilder.Sanitize(magnet);
        if (!string.Equals(sanitized, magnet, StringComparison.Ordinal))
            warnings.Add("secret_query_redacted");
        return sanitized ?? magnet;
    }

    /// <summary>
    ///     Resolves a topic-page link against the topic URL; unsafe schemes and user-info are
    ///     dropped, sensitive query parameters (passkey/token/...) are stripped, the rest kept.
    /// </summary>
    private static string? ToSafePublicUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(new Uri(baseUrl), WebUtility.HtmlDecode(value), out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrWhiteSpace(uri.UserInfo))
            return null;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (HasSensitiveQuery(uri.Query))
            builder.Query = SanitizeQuery(uri.Query);
        return builder.Uri.AbsoluteUri;
    }

    private static bool HasSensitiveQuery(string query) =>
        Regex.IsMatch(query,
            @"(?:^|[?&])(?:passkey|token|auth|authorization|cookie|session|password|user)=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string SanitizeQuery(string query) => string.Join('&',
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(pair =>
        {
            var separator = pair.IndexOf('=');
            var name = WebUtility.UrlDecode(separator < 0 ? pair : pair[..separator]);
            return !Regex.IsMatch(name,
                @"^(?:passkey|token|auth|authorization|cookie|session|password|user)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }));

    private static bool IsKinozalUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.EndsWith("kinozal.guru", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith("kinozal.me", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith("kinozal.tv", StringComparison.OrdinalIgnoreCase));

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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
