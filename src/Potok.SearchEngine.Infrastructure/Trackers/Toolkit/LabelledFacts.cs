using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Extracts "Label: value" facts from topic HTML and scrubs them before persistence.
///     Two extraction flavors cover the ported trackers: inline bold labels (RuTor-style,
///     <see cref="ExtractInline"/>) and forum post bodies with table rows and
///     colon-terminated labels (RuTracker-style, <see cref="ExtractPostBody"/>).
///     Returned dictionaries are case-insensitive (OrdinalIgnoreCase).
/// </summary>
public static partial class LabelledFacts
{
    /// <summary>Adds or merges (" | "-joined) a labelled fact; empty labels/values are skipped.</summary>
    public static void AddFact(IDictionary<string, string> facts, string rawLabel, string rawValue)
    {
        var label = TrackerText.NormalizeText(rawLabel).TrimEnd(':').Trim();
        var value = NormalizeFactValue(rawValue);
        if (label.Length is 0 or > 80 || string.IsNullOrWhiteSpace(value))
            return;
        if (facts.TryGetValue(label, out var previous) && !previous.Contains(value, StringComparison.Ordinal))
            facts[label] = $"{previous} | {value}";
        else
            facts[label] = value;
    }

    /// <summary>
    ///     RuTor flavor: every b/strong inside <paramref name="content"/> is a label; the value
    ///     is the following sibling text up to the next label or a content boundary. A single
    ///     empty BR gap is crossed so "Описание:<br>text" still binds.
    /// </summary>
    public static Dictionary<string, string> ExtractInline(AngleSharp.Dom.IElement content, string labelSelector = "b, strong")
    {
        var facts = NewDictionary();
        foreach (var labelElement in content.QuerySelectorAll(labelSelector))
        {
            var label = TrackerText.NormalizeText(labelElement.TextContent).TrimEnd(':').Trim();
            if (label.Length is 0 or > 80)
                continue;
            AddFact(facts, label, ReadInlineValue(labelElement, labelSelector));
        }
        return facts;
    }

    /// <summary>Table rows with two+ th/td cells become label/value pairs.</summary>
    public static Dictionary<string, string> ExtractTableRows(AngleSharp.Dom.IElement root)
    {
        var facts = NewDictionary();
        foreach (var row in root.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("th, td");
            if (cells.Length < 2)
                continue;
            AddFact(facts, cells[0].TextContent, cells[1].TextContent);
        }
        return facts;
    }

    /// <summary>
    ///     RuTracker flavor: scans a post body — table rows plus colon-terminated
    ///     b/strong/span.post-b labels and dt/dd pairs.
    /// </summary>
    public static Dictionary<string, string> ExtractPostBody(
        IDocument document,
        string bodySelector = ".post_body, .postbody, #tor-reged, .message",
        string labelSelector = "b, strong, span.post-b, dt")
    {
        var facts = NewDictionary();
        var body = document.QuerySelector(bodySelector) ?? document.Body;
        if (body is null)
            return facts;

        foreach (var row in body.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("th, td");
            if (cells.Length < 2)
                continue;
            AddFact(facts, cells[0].TextContent, cells[1].TextContent);
        }

        foreach (var labelElement in body.QuerySelectorAll(labelSelector))
        {
            var rawLabel = TrackerText.NormalizeText(labelElement.TextContent);
            if (string.IsNullOrWhiteSpace(rawLabel))
                continue;
            var label = rawLabel.Trim().TrimEnd(':').Trim();
            if (label.Length is 0 or > 80)
                continue;
            var isDt = labelElement.TagName.Equals("DT", StringComparison.OrdinalIgnoreCase);
            if (!rawLabel.EndsWith(':') &&
                !labelElement.ClassList.Contains("post-b") &&
                !isDt)
                continue;

            string value;
            if (isDt && labelElement.NextElementSibling?.TagName.Equals("DD", StringComparison.OrdinalIgnoreCase) == true)
                value = labelElement.NextElementSibling.TextContent;
            else
                value = ReadPostValue(labelElement, labelSelector);
            AddFact(facts, label, value);
        }

        return facts;
    }

    /// <summary>
    ///     Removes facts whose label carries credentials or identity material
    ///     (cookie/password/token/session/passkey/login/uploader...). Adds the
    ///     "sensitive_fact_redacted" warning once when anything was removed.
    /// </summary>
    public static void RedactSensitive(IDictionary<string, string> facts, ICollection<string> warnings)
    {
        var sensitive = facts.Keys.Where(label => SensitiveLabelRegex().IsMatch(label)).ToArray();
        foreach (var key in sensitive)
            facts.Remove(key);
        if (sensitive.Length > 0 && !warnings.Contains("sensitive_fact_redacted"))
            warnings.Add("sensitive_fact_redacted");
    }

    /// <summary>
    ///     Removes momentary telemetry facts (transfer/upload/download speed, last-seen seed,
    ///     online time). Product rule: these are never persisted.
    /// </summary>
    public static void RemoveTransientTelemetry(IDictionary<string, string> facts)
    {
        var transient = facts.Keys.Where(label => TransientLabelRegex().IsMatch(label)).ToArray();
        foreach (var key in transient)
            facts.Remove(key);
    }

    public static Dictionary<string, string> NewDictionary() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeFactValue(string value) =>
        TrackerText.NormalizeText(value).TrimStart(':', ' ', '\u00A0');

    private static string ReadInlineValue(AngleSharp.Dom.IElement label, string labelSelector)
    {
        var parts = new List<string>();
        var crossedEmptyBreak = false;
        for (var node = label.NextSibling; node is not null; node = node.NextSibling)
        {
            if (node is AngleSharp.Dom.IElement element)
            {
                if (element.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(NormalizeFactValue(string.Join(" ", parts))))
                        break;
                    if (crossedEmptyBreak)
                        break;
                    parts.Clear();
                    crossedEmptyBreak = true;
                    continue;
                }
                if (element.Matches(labelSelector))
                    break;
            }
            var text = TrackerText.NormalizeText(node.TextContent);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }
        return NormalizeFactValue(string.Join(" ", parts));
    }

    private static string ReadPostValue(AngleSharp.Dom.IElement label, string labelSelector)
    {
        var parts = new List<string>();
        for (var node = label.NextSibling; node is not null; node = node.NextSibling)
        {
            if (node is AngleSharp.Dom.IElement element)
            {
                if (element.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                    break;
                if (element.Matches(labelSelector))
                    break;
            }
            var text = TrackerText.NormalizeText(node.TextContent);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }
        return string.Join(" ", parts);
    }

    [GeneratedRegex("cookie|authorization|password|парол|login|логин|token|session|passkey|залил|uploader|user|добавить в",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveLabelRegex();

    [GeneratedRegex(
        @"сид(?:ер)?\s*(?:был|замечен)|последн\w*\s+сид|seed(?:er)?\s*(?:last\s*)?seen|last\s*seed|скорост\w*|время\s+(?:онлайн|раздач)|(?:transfer|upload|download)\s*speed|time\s*online",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransientLabelRegex();
}
