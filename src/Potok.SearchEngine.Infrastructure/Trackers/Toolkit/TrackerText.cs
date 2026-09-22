using System.Globalization;
using System.Text.RegularExpressions;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Shared text/number/date parsing primitives for tracker adapters.
/// </summary>
public static partial class TrackerText
{
    /// <summary>Collapses all whitespace runs to single spaces and trims.</summary>
    public static string NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>Parses "8.18" + "GB" into bytes (1024-based, comma or dot decimals).</summary>
    public static long ParseSize(string val, string unit)
    {
        if (!double.TryParse(val.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return 0;

        var multiplier = unit.ToUpperInvariant() switch
        {
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "MB" => 1024d * 1024d,
            "KB" => 1024d,
            _ => 1d
        };

        return (long)(value * multiplier);
    }

    /// <summary>
    ///     Parses a free-form size string ("14.29 GB", "1,5 ГБ") into bytes and a
    ///     normalized label. Supports TB/GB/MB/KB and Russian equivalents.
    /// </summary>
    public static bool TryParseSize(string sizeText, out string sizeName, out long sizeBytes)
    {
        sizeName = string.Empty;
        sizeBytes = 0L;

        var match = SizeRegex().Match(sizeText);
        if (!match.Success)
            return false;

        var valueRaw = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(valueRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return false;

        sizeBytes = (long)Math.Round(value * UnitMultiplier(match.Groups["unit"].Value));
        sizeName = FormatSize(sizeBytes);
        return true;
    }

    public static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        double value = bytes;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    /// <summary>
    ///     RuTor-style short date: day + Russian month abbreviation + 2-digit year (20yy).
    ///     Falls back to <see cref="DateTime.UtcNow"/> for out-of-range components.
    /// </summary>
    public static DateTime ParseRuShortDate(string d, string m, string y)
    {
        if (!int.TryParse(d, out var day)) day = 1;
        if (!int.TryParse(y, out var year)) year = 0;

        year += 2000;

        var month = m.ToLowerInvariant() switch
        {
            "янв" => 1, "фев" => 2, "мар" => 3, "апр" => 4,
            "май" => 5, "июн" => 6, "июл" => 7, "авг" => 8,
            "сен" => 9, "окт" => 10, "ноя" => 11, "дек" => 12,
            _ => 1
        };

        try
        {
            return new DateTime(year, month, day);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Parses Russian forum dates ("22-Сен-26 03:19", "10.05.24 10:35") after month-name
    ///     normalization. Returns default when nothing parses.
    /// </summary>
    public static DateTime ParseRuTopicDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        var normalized = Regex.Replace(NormalizeRuDate(raw.Replace("-", " ")), "\\s+", " ").Trim();
        if (DateTime.TryParseExact(
                normalized,
                "dd.MM.yy HH:mm",
                CultureInfo.GetCultureInfo("ru-RU"),
                DateTimeStyles.None,
                out var parsed))
            return parsed;

        return DateTime.TryParse(
            normalized,
            CultureInfo.GetCultureInfo("ru-RU"),
            DateTimeStyles.None,
            out parsed)
            ? parsed
            : default;
    }

    /// <summary>Replaces Russian/English month names with ".MM." fragments.</summary>
    public static string NormalizeRuDate(string value)
    {
        value = ReplaceMonth(value, "янв", ".01.");
        value = ReplaceMonth(value, "февр?", ".02.");
        value = ReplaceMonth(value, "март?", ".03.");
        value = ReplaceMonth(value, "апр", ".04.");
        value = ReplaceMonth(value, "май", ".05.");
        value = ReplaceMonth(value, "июнь?", ".06.");
        value = ReplaceMonth(value, "июль?", ".07.");
        value = ReplaceMonth(value, "авг", ".08.");
        value = ReplaceMonth(value, "сент?", ".09.");
        value = ReplaceMonth(value, "окт", ".10.");
        value = ReplaceMonth(value, "нояб?", ".11.");
        value = ReplaceMonth(value, "дек", ".12.");

        value = ReplaceMonth(value, "январ(ь|я)?", ".01.");
        value = ReplaceMonth(value, "феврал(ь|я)?", ".02.");
        value = ReplaceMonth(value, "марта?", ".03.");
        value = ReplaceMonth(value, "апрел(ь|я)?", ".04.");
        value = ReplaceMonth(value, "май?я?", ".05.");
        value = ReplaceMonth(value, "июн(ь|я)?", ".06.");
        value = ReplaceMonth(value, "июл(ь|я)?", ".07.");
        value = ReplaceMonth(value, "августа?", ".08.");
        value = ReplaceMonth(value, "сентябр(ь|я)?", ".09.");
        value = ReplaceMonth(value, "октябр(ь|я)?", ".10.");
        value = ReplaceMonth(value, "ноябр(ь|я)?", ".11.");
        value = ReplaceMonth(value, "декабр(ь|я)?", ".12.");

        value = ReplaceMonth(value, "Jan", ".01.");
        value = ReplaceMonth(value, "Feb", ".02.");
        value = ReplaceMonth(value, "Mar", ".03.");
        value = ReplaceMonth(value, "Apr", ".04.");
        value = ReplaceMonth(value, "May", ".05.");
        value = ReplaceMonth(value, "Jun", ".06.");
        value = ReplaceMonth(value, "Jul", ".07.");
        value = ReplaceMonth(value, "Aug", ".08.");
        value = ReplaceMonth(value, "Sep", ".09.");
        value = ReplaceMonth(value, "Oct", ".10.");
        value = ReplaceMonth(value, "Nov", ".11.");
        value = ReplaceMonth(value, "Dec", ".12.");

        if (Regex.IsMatch(value, "^[0-9]\\.", RegexOptions.IgnoreCase))
            value = $"0{value}";

        return value;
    }

    public static int ParseInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>First standalone 19xx/20xx year in the string, 0 when absent.</summary>
    public static int ExtractYear(string? text)
    {
        var match = YearRegex().Match(text ?? string.Empty);
        return match.Success && int.TryParse(match.Value, out var year) ? year : 0;
    }

    /// <summary>First non-empty fact value among the given label aliases (case-insensitive).</summary>
    public static string? FindFact(IReadOnlyDictionary<string, string> facts, params string[] names)
    {
        foreach (var name in names)
            if (facts.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    /// <summary>Reads the first integer found in a fact value.</summary>
    public static bool TryReadInt(IReadOnlyDictionary<string, string> facts, string name, out int value)
    {
        value = 0;
        var match = Regex.Match(FindFact(facts, name) ?? string.Empty, @"\d+");
        return match.Success && int.TryParse(match.Value, out value);
    }

    /// <summary>Splits a fact value on , ; / | into a case-insensitive set.</summary>
    public static HashSet<string> SplitFactValues(string raw) => raw
        .Split([',', ';', '/', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static double UnitMultiplier(string unitRaw) => unitRaw.ToUpperInvariant() switch
    {
        "TB" or "ТБ" => 1024d * 1024d * 1024d * 1024d,
        "GB" or "ГБ" => 1024d * 1024d * 1024d,
        "MB" or "МБ" => 1024d * 1024d,
        "KB" or "КБ" => 1024d,
        _ => 1d
    };

    private static string ReplaceMonth(string value, string monthPattern, string replacement) =>
        Regex.Replace(value, $"\\s{monthPattern}\\.?(\\s|$)", $"{replacement} ", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<value>\d+(?:[\.,]\d+)?)\s*(?<unit>TB|GB|MB|KB|ТБ|ГБ|МБ|КБ)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex YearRegex();
}
