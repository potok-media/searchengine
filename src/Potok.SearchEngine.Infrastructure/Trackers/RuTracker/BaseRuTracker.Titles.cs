using System.Text.RegularExpressions;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     RuTracker list-title parsing: movie/serial/generic flavors extract name, original
///     name and release year from the rigid "Название / Original (режиссёр) [год, ...]"
///     forum title format.
/// </summary>
public partial class BaseRuTracker
{
    protected static (string? name, string? originalName, int releaseYear) ParseTitle(
        string title, CategoryInfo category)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null, 0);

        return category.Parser switch
        {
            CategoryParser.Movie => ParseMovie(title),
            CategoryParser.Serial => ParseSerial(title),
            CategoryParser.Generic => ParseGeneric(title),
            _ => (title.Trim(), null, TrackerText.ExtractYear(title))
        };
    }

    private static (string? name, string? originalName, int releaseYear) ParseMovie(string title)
    {
        var g = Regex.Match(title, "^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeMovie(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title, "^([^/\\(\\[]+) / ([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeMovie(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title, "^([^/\\(\\[]+) \\([^\\)]+\\) \\[([0-9]+), ").Groups;
        if (IsValidGroups(g, 1, 2))
            return NormalizeMovie(g[1].Value, null, g[2].Value);

        return (title.Trim(), null, TrackerText.ExtractYear(title));
    }

    private static (string? name, string? originalName, int releaseYear) ParseSerial(string title)
    {
        const string seasonMarker = "Сезон";
        var g = Regex.Match(title,
            $"^([^/\\(\\[]+) / [^/\\(\\[]+ / [^/\\(\\[]+ / ([^/\\(\\[]+) / {seasonMarker}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) / {seasonMarker}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / ([^/\\(\\[]+) / {seasonMarker}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            $"^([^/\\(\\[]+) / {seasonMarker}: [^/]+ / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2))
            return NormalizeSerial(g[1].Value, null, g[2].Value);

        g = Regex.Match(title,
            "^([^/\\(\\[]+) / [^/\\(\\[]+ / ([^/\\(\\[]+) / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        g = Regex.Match(title,
            "^([^/\\(\\[]+) / ([^/\\(\\[]+) / [^\\(\\[]+ \\([^\\)]+\\) \\[([0-9]+)(,|-)",
            RegexOptions.IgnoreCase).Groups;
        if (IsValidGroups(g, 1, 2, 3))
            return NormalizeSerial(g[1].Value, g[2].Value, g[3].Value);

        return (title.Trim(), null, TrackerText.ExtractYear(title));
    }

    private static (string? name, string? originalName, int releaseYear) ParseGeneric(string title)
    {
        var name = Regex.Match(title, "^([^/\\(\\[]+) ").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(name))
            return (title.Trim(), null, TrackerText.ExtractYear(title));

        if (Regex.IsMatch(name, "(Сезон|Серии)", RegexOptions.IgnoreCase))
            return (null, null, 0);

        var releaseYear = 0;
        var yearMatch = Regex.Match(title, " \\[([0-9]{4})(,|-) ");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsed))
            releaseYear = parsed;

        return (name.Trim(), null, releaseYear);
    }

    private static (string? name, string? originalName, int releaseYear) NormalizeMovie(
        string? name, string? original, string year)
    {
        var releaseYear = int.TryParse(year, out var parsed) ? parsed : 0;
        name = name?.Replace("в 3Д", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        original = original?.Replace(" in 3D", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" 3D", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return (name, original, releaseYear);
    }

    private static (string? name, string? originalName, int releaseYear) NormalizeSerial(
        string? name, string? original, string year)
    {
        var releaseYear = int.TryParse(year, out var parsed) ? parsed : 0;
        return (name?.Trim(), original?.Trim(), releaseYear);
    }

    private static bool IsValidGroups(GroupCollection groups, params int[] indices)
    {
        return indices.All(i => groups.Count > i && !string.IsNullOrWhiteSpace(groups[i].Value));
    }
}
