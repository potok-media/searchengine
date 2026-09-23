using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;

/// <summary>
///     AnimeLayer text/fact helpers: colon-labelled fact extraction with duplicate
///     suffixing ("Аудио", "Аудио 2"), title/fact normalization (year, resolution-based
///     quality, languages, voices, seasons), counter/size/date readers and the
///     sensitive/momentary-telemetry scrub rules.
/// </summary>
public partial class BaseAnimeLayer
{
    /// <summary>
    ///     Colon-terminated strong/b labels bind to the following sibling text up to the
    ///     next label or a BR. Duplicate labels are suffixed ("Аудио 2"), never merged.
    /// </summary>
    private static Dictionary<string, string> ExtractLabelledFacts(IElement element)
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var labelElement in element.QuerySelectorAll("strong, b"))
        {
            var rawLabel = TrackerText.NormalizeText(labelElement.TextContent);
            if (!rawLabel.Contains(':')) continue;
            var label = rawLabel.Trim().TrimEnd(':').Trim();
            if (string.IsNullOrWhiteSpace(label)) continue;

            var builder = new StringBuilder();
            for (var node = labelElement.NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is IElement next && (next.LocalName == "br" || next.LocalName is "strong" or "b"))
                    break;
                builder.Append(node.TextContent);
            }
            var value = TrackerText.NormalizeText(builder.ToString());
            if (string.IsNullOrWhiteSpace(value)) continue;
            var key = label;
            for (var suffix = 2; facts.ContainsKey(key); suffix++)
                key = $"{label} {suffix}";
            facts[key] = value;
        }
        return facts;
    }

    private static ParsedAnimeFacts ParseTitleAndFacts(
        string title,
        IReadOnlyDictionary<string, string> facts)
    {
        var titleParts = Regex.Split(title, @"\s+/\s+")
            .Select(TrackerText.NormalizeText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var name = titleParts.FirstOrDefault(value => Regex.IsMatch(value, @"\p{IsCyrillic}"));
        var original = titleParts.FirstOrDefault(value => !Regex.IsMatch(value, @"\p{IsCyrillic}"));
        name = TrimTitleMetadata(name ?? titleParts.FirstOrDefault());
        original = TrimTitleMetadata(original);

        var yearRaw = FindFact(facts, "Год выхода", "Год выпуска", "Год") ?? title;
        var yearMatch = Regex.Match(yearRaw, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        var year = yearMatch.Success && int.TryParse(yearMatch.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedYear) ? parsedYear : 0;

        var resolution = FindFact(facts, "Разрешение", "Resolution") ??
                         string.Join(' ', facts.Where(pair => pair.Key.StartsWith("Видео", StringComparison.OrdinalIgnoreCase))
                             .Select(pair => pair.Value));
        var quality = StringConvert.ParseQuality(FindFact(facts, "Качество", "Качество видео"));
        if (quality == 0)
        {
            var resolutionMatch = Regex.Match(resolution, @"\d{3,4}\s*[xх]\s*(?<height>\d{3,4})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (resolutionMatch.Success)
                int.TryParse(resolutionMatch.Groups["height"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out quality);
        }
        var videoType = NullIfEmpty(FindFact(facts, "Формат", "Контейнер"));

        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in facts.Where(pair =>
                     pair.Key.StartsWith("Язык", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.StartsWith("Аудио", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.StartsWith("Субтитры", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
        {
            if (value.Contains("рус", StringComparison.OrdinalIgnoreCase)) languages.Add("Русский");
            if (value.Contains("англ", StringComparison.OrdinalIgnoreCase)) languages.Add("Английский");
            if (value.Contains("япон", StringComparison.OrdinalIgnoreCase)) languages.Add("Японский");
            if (value.Contains("укра", StringComparison.OrdinalIgnoreCase)) languages.Add("Украинский");
        }
        var voices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in facts.Where(pair =>
                     pair.Key.StartsWith("Перевод", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.StartsWith("Озвуч", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
            voices.Add(value);

        var seasons = new HashSet<int>();
        foreach (Match match in Regex.Matches(title,
                     @"(?:\bS|\[ТВ-?|\bсезон\s*)(?<season>\d{1,2})",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            if (int.TryParse(match.Groups["season"].Value, out var season) && season > 0)
                seasons.Add(season);

        return new ParsedAnimeFacts(name, original, year, quality, videoType, languages, voices, seasons);
    }

    private static string? TrimTitleMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return NullIfEmpty(Regex.Replace(value, @"\s*(?:\[|\().*$", string.Empty).Trim());
    }

    /// <summary>Exact or suffix-indexed ("Info hash 2") case-insensitive fact lookup.</summary>
    private static string? FindFact(IReadOnlyDictionary<string, string> facts, params string[] names)
    {
        foreach (var name in names)
        {
            var fact = facts.FirstOrDefault(pair =>
                pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(fact.Value)) return fact.Value;
        }
        return null;
    }

    private static bool TryCounterAfterIcon(IElement? root, string iconClass, out int value)
    {
        value = 0;
        var icon = root?.QuerySelector($".{iconClass}");
        if (icon is null) return false;
        for (var node = icon.NextSibling; node is not null; node = node.NextSibling)
        {
            var text = TrackerText.NormalizeText(node.TextContent);
            var match = Regex.Match(text, @"\d+");
            if (match.Success)
                return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            if (node is IElement element && element.ClassList.Contains("icon")) break;
        }
        return false;
    }

    private static bool TryParseSize(string? text, out long bytes, out string? label)
    {
        bytes = 0;
        label = null;
        var match = Regex.Match(text ?? string.Empty,
            @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>TB|GB|MB|KB|B)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        label = $"{match.Groups["value"].Value} {match.Groups["unit"].Value.ToUpperInvariant()}";
        bytes = TrackerText.ParseSize(match.Groups["value"].Value, match.Groups["unit"].Value);
        return true;
    }

    private static DateTimeOffset? ParseRussianDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text,
            @"(?<day>\d{1,2})\s+(?<month>[а-яё]+)\s+(?<year>\d{4})(?:\s+в\s+(?<hour>\d{1,2}):(?<minute>\d{2}))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["января"] = 1, ["февраля"] = 2, ["марта"] = 3, ["апреля"] = 4,
            ["мая"] = 5, ["июня"] = 6, ["июля"] = 7, ["августа"] = 8,
            ["сентября"] = 9, ["октября"] = 10, ["ноября"] = 11, ["декабря"] = 12
        };
        if (!months.TryGetValue(match.Groups["month"].Value, out var month) ||
            !int.TryParse(match.Groups["day"].Value, out var day) ||
            !int.TryParse(match.Groups["year"].Value, out var year)) return null;
        var hour = int.TryParse(match.Groups["hour"].Value, out var parsedHour) ? parsedHour : 0;
        var minute = int.TryParse(match.Groups["minute"].Value, out var parsedMinute) ? parsedMinute : 0;
        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractInfoHash(string magnet)
    {
        var match = Regex.Match(magnet,
            @"(?:[?&]xt=urn:btih:)(?<hash>[A-Fa-f0-9]{40}|[A-Za-z2-7]{32})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    private static void RedactAndRemoveTelemetry(IDictionary<string, string> facts, ICollection<string> warnings)
    {
        LabelledFacts.RedactSensitive(facts, warnings);
        foreach (var key in facts.Keys.Where(IsMomentaryTelemetryLabel).ToArray())
            facts.Remove(key);
    }

    private static bool IsSensitiveLabel(string label) =>
        Regex.IsMatch(label, @"cookie|authorization|password|парол|login|логин|token|session|passkey|proxy|user",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsMomentaryTelemetryLabel(string label) =>
        Regex.IsMatch(label,
            @"скорост|(?:upload|download|transfer)\s*(?:speed|rate)|сид(?:ер)?\s*(?:был|замечен)|последн(?:ий|яя)?\s*сид|last\s*seed|seed(?:er)?\s*(?:last\s*)?seen|время\s*(?:онлайн|раздачи)|time\s*online",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RemoveMomentaryTelemetry(string value) => string.Join(' ', value.Split('|')
        .Select(TrackerText.NormalizeText)
        .Where(part => !IsMomentaryTelemetryLabel(part)));

    /// <summary>
    ///     Resolves a topic-page link/image against the topic URL; magnet links, non-http
    ///     schemes and user-info are dropped, sensitive query parameters are stripped
    ///     (the URL itself is kept).
    /// </summary>
    private static string? ToSafePublicUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(new Uri(baseUrl), WebUtility.HtmlDecode(value), out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrWhiteSpace(uri.UserInfo))
            return null;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (HasSensitiveQuery(uri.Query)) builder.Query = SanitizeQuery(uri.Query);
        return builder.Uri.AbsoluteUri;
    }

    private static bool HasSensitiveQuery(string query) =>
        Regex.IsMatch(query, @"(?:^|[?&])(?:passkey|token|auth|authorization|cookie|session|password|user)=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string SanitizeQuery(string query) => string.Join('&',
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(pair =>
        {
            var separator = pair.IndexOf('=');
            var name = WebUtility.UrlDecode(separator < 0 ? pair : pair[..separator]);
            return !IsSensitiveLabel(name);
        }));

    private static bool IsAnimeLayerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.EndsWith("animelayer.ru", StringComparison.OrdinalIgnoreCase);

    private static string ExtractTextWithBreaks(IElement element)
    {
        var builder = new StringBuilder();
        AppendText(element, builder);
        return string.Join('\n', builder.ToString().Split('\n').Select(TrackerText.NormalizeText)
            .Where(line => !string.IsNullOrWhiteSpace(line)));
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

    private static string? MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NullIfEmpty(TrackerText.NormalizeText(match.Groups["value"].Value)) : null;
    }

    private sealed record ParsedAnimeFacts(
        string? Name,
        string? OriginalName,
        int Year,
        int Quality,
        string? VideoType,
        HashSet<string> Languages,
        HashSet<string> Voices,
        HashSet<int> Seasons);
}
