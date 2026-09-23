using System.Text;
using AngleSharp.Dom;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;

/// <summary>
///     MegaPeer-specific DOM extraction: bold-label facts with description/spoiler
///     handling, heading-table metadata, and hidden spoiler sections.
/// </summary>
public partial class BaseMegaPeer
{
    private static Dictionary<string, string> ExtractLabelledFacts(IElement content)
    {
        var result = LabelledFacts.NewDictionary();
        foreach (var labelElement in content.QuerySelectorAll("b"))
        {
            if (labelElement.Closest(".sp-wrap") is not null)
                continue;
            var label = TrackerText.NormalizeText(labelElement.TextContent).TrimEnd(':').Trim();
            if (string.IsNullOrWhiteSpace(label))
                continue;
            var description = string.Equals(label, "Описание", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            for (var node = labelElement.NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is IElement { LocalName: "b" })
                    break;
                if (node is IElement element && element.Matches(".sp-wrap"))
                    break;
                if (node is IElement { LocalName: "br" })
                {
                    if (!description)
                        break;
                    continue;
                }
                if (description && node is IElement { LocalName: "a" })
                    break;
                var value = TrackerText.NormalizeText(node.TextContent).TrimStart(':').Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(value);
            }
            var fact = string.Join(" ", parts).Trim();
            if (!string.IsNullOrWhiteSpace(fact))
                result[label] = fact;
        }
        return result;
    }

    private static (Dictionary<string, string> Metadata, Dictionary<string, string> Raw, IElement? CategoryLink)
        ExtractMetadata(IElement detailsTable)
    {
        var raw = LabelledFacts.NewDictionary();
        IElement? categoryLink = null;
        foreach (var row in detailsTable.QuerySelectorAll("tr"))
        {
            var heading = row.Children.FirstOrDefault(child => child.Matches("td.heading"));
            if (heading is null)
                continue;
            var valueCell = heading.NextElementSibling;
            if (valueCell is null)
                continue;
            var label = TrackerText.NormalizeText(heading.TextContent);
            var value = TrackerText.NormalizeText(valueCell.TextContent);
            if (!string.IsNullOrWhiteSpace(label))
                raw[label] = value;
            if (string.Equals(label, "Категория", StringComparison.OrdinalIgnoreCase))
                categoryLink = valueCell.QuerySelector("a[href^='/cat/']");
        }
        var metadata = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        if (categoryLink is not null)
            metadata["Категория"] = TrackerText.NormalizeText(categoryLink.TextContent);
        return (metadata, raw, categoryLink);
    }

    private static List<Dictionary<string, object?>> ExtractHiddenSections(IElement content, string baseUrl)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var section in content.QuerySelectorAll(".sp-wrap"))
        {
            var body = section.QuerySelector(".sp-body");
            if (body is null)
                continue;
            var links = body.QuerySelectorAll("a[href]")
                .Select(link => SafeUrl.ToSafePublicUrl(link.GetAttribute("href"), baseUrl))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var images = body.QuerySelectorAll("img[src], img[data-src]")
                .SelectMany(image => new[] { image.GetAttribute("src"), image.GetAttribute("data-src") })
                .Select(value => SafeUrl.ToSafePublicUrl(value, baseUrl))
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = TrackerText.NormalizeText(section.QuerySelector(".sp-head")?.TextContent ?? string.Empty),
                ["text"] = ExtractTextWithBreaks(body),
                ["links"] = links,
                ["images"] = images
            });
        }
        return result;
    }

    private static string ExtractTextWithBreaks(IElement element)
    {
        var builder = new StringBuilder();
        AppendText(element, builder);
        return string.Join('\n', builder.ToString().Split('\n')
            .Select(TrackerText.NormalizeText)
            .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void AppendText(INode node, StringBuilder builder)
    {
        if (node is IElement { LocalName: "br" })
        {
            builder.AppendLine();
            return;
        }
        if (node.NodeType == NodeType.Text)
            builder.Append(node.TextContent);
        foreach (var child in node.ChildNodes)
            AppendText(child, builder);
    }

    private static bool IsMegaPeerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, new Uri("https://megapeer.vip/").Host, StringComparison.OrdinalIgnoreCase);
}
