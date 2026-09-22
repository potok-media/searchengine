using System.Globalization;
using System.Text.RegularExpressions;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTor;

/// <summary>
///     Maps scrubbed RuTor facts/metadata onto the typed torrent fields.
/// </summary>
public partial class BaseRuTor
{
    private static void ApplyNormalizedFacts(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyDictionary<string, string> metadata,
        string? categoryHref,
        ref TorrentObservedFields observed)
    {
        var name = TrackerText.FindFact(facts, "Название");
        var originalName = TrackerText.FindFact(facts, "Оригинальное название");
        if (name is not null || originalName is not null)
        {
            torrent.Name = name;
            torrent.OriginalName = originalName;
            observed |= TorrentObservedFields.Names;
        }

        var year = TrackerText.ExtractYear(TrackerText.FindFact(facts, "Год выпуска", "Год выхода", "Год"));
        if (year > 0)
        {
            torrent.ReleaseYear = year;
            observed |= TorrentObservedFields.ReleaseYear;
        }

        var quality = TrackerText.FindFact(facts, "Качество", "Качество видео");
        if (quality is not null)
        {
            torrent.Quality = StringConvert.ParseQuality(quality);
            observed |= TorrentObservedFields.Quality;
        }
        var format = TrackerText.FindFact(facts, "Формат", "Контейнер", "Формат видео", "Кодек");
        if (format is not null)
        {
            torrent.VideoType = format;
            observed |= TorrentObservedFields.VideoType;
        }

        var translation = TrackerText.FindFact(facts, "Перевод", "Перевод(ы)", "Озвучивание");
        if (translation is not null)
        {
            torrent.Voices = translation.Split([';', '|'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            observed |= TorrentObservedFields.Voices;
        }

        var languages = ExtractLanguages(facts);
        if (languages.Count > 0)
        {
            torrent.Languages = languages;
            observed |= TorrentObservedFields.Languages;
        }

        var types = MapCategory(categoryHref ?? TrackerText.FindFact(metadata, "Категория") ?? string.Empty);
        if (types.Length > 0)
        {
            var keepEpisodicSubtype =
                (types[0] == "multfilm" && torrent.Types?.Contains("multserial") == true) ||
                (types[0] == "documovie" && torrent.Types?.Contains("docuserial") == true);
            if (!keepEpisodicSubtype)
                torrent.Types = types;
            observed |= TorrentObservedFields.Types;
        }

        if (TrackerText.TryReadInt(metadata, "Раздают", out var seeders))
        {
            torrent.Sid = seeders;
            observed |= TorrentObservedFields.Seeders;
        }
        if (TrackerText.TryReadInt(metadata, "Качают", out var leechers))
        {
            torrent.Pir = leechers;
            observed |= TorrentObservedFields.Leechers;
        }
        var sizeRaw = TrackerText.FindFact(metadata, "Размер");
        var byteMatch = Regex.Match(sizeRaw ?? string.Empty, @"\((?<bytes>\d+)\s*Bytes\)", RegexOptions.IgnoreCase);
        if (byteMatch.Success && long.TryParse(byteMatch.Groups["bytes"].Value, out var bytes))
        {
            torrent.Size = bytes;
            torrent.SizeName = sizeRaw;
            observed |= TorrentObservedFields.Size;
        }
        var publishedRaw = TrackerText.FindFact(metadata, "Добавлен");
        var dateMatch = Regex.Match(publishedRaw ?? string.Empty, @"\d{2}-\d{2}-\d{4}\s+\d{1,2}:\d{2}:\d{2}");
        if (dateMatch.Success && DateTime.TryParseExact(dateMatch.Value,
                ["dd-MM-yyyy H:mm:ss", "dd-MM-yyyy HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published))
        {
            torrent.CreateTime = published;
            observed |= TorrentObservedFields.PublishDate;
        }
    }

    private static HashSet<string> ExtractLanguages(IReadOnlyDictionary<string, string> facts)
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in facts.Where(pair =>
                     pair.Key.StartsWith("Аудио", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.Equals("Язык", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.Equals("Субтитры", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var part in pair.Value.Split([',', ';', '/', '|'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var match = Regex.Match(part,
                    @"^(Русск(?:ий|ие)|Английск(?:ий|ие)|Украинск(?:ий|ие)|Японск(?:ий|ие)|Немецк(?:ий|ие)|Французск(?:ий|ие))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                    languages.Add(match.Groups[1].Value);
            }
        }
        return languages;
    }

    private static string[] MapCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return [];

        if (category.Contains("nashi_seriali", StringComparison.OrdinalIgnoreCase))
            return ["serial"];
        if (category.Contains("seriali", StringComparison.OrdinalIgnoreCase))
            return ["serial"];
        if (category.Contains("anime", StringComparison.OrdinalIgnoreCase))
            return ["anime"];
        if (category.Contains("nauchno", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("науч", StringComparison.OrdinalIgnoreCase))
            return ["documovie"];
        if (category.Contains("sport", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("спорт", StringComparison.OrdinalIgnoreCase))
            return ["sport"];
        if (category.Contains("nashe_kino", StringComparison.OrdinalIgnoreCase))
            return ["movie"];
        if (category.Contains("kino", StringComparison.OrdinalIgnoreCase))
            return ["movie"];
        if (category.Contains("tv", StringComparison.OrdinalIgnoreCase))
            return ["tvshow"];
        if (category.Contains("multiki", StringComparison.OrdinalIgnoreCase))
            return ["multfilm"];

        return [];
    }
}
