using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;

/// <summary>
///     Maps scrubbed MegaPeer facts/metadata onto the typed torrent fields.
/// </summary>
public partial class BaseMegaPeer
{
    private static void ApplyNormalizedDetails(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyCollection<Dictionary<string, object?>> hiddenSections,
        IElement? categoryLink,
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
            TrackerText.ExtractYear(yearText) is > 0 and var year)
        {
            torrent.ReleaseYear = year;
            observed |= TorrentObservedFields.ReleaseYear;
        }
        if (facts.TryGetValue("Качество видео", out var quality))
        {
            torrent.Quality = StringConvert.ParseQuality(quality);
            observed |= TorrentObservedFields.Quality;
        }

        var mediaInfo = hiddenSections.FirstOrDefault(section =>
            string.Equals(section["title"]?.ToString(), "MediaInfo", StringComparison.OrdinalIgnoreCase));
        var mediaInfoText = mediaInfo?["text"]?.ToString();
        var formatMatch = Regex.Match(mediaInfoText ?? string.Empty, @"(?:^|\n)Format:\s*(?<format>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (formatMatch.Success)
        {
            torrent.VideoType = formatMatch.Groups["format"].Value.Trim();
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
            }
            torrent.Languages = languages;
            observed |= TorrentObservedFields.Languages;
        }
        if (facts.TryGetValue("Перевод", out var translation))
        {
            torrent.Voices = [translation];
            observed |= TorrentObservedFields.Voices;
        }

        if (categoryLink is not null)
        {
            var category = categoryLink.GetAttribute("href")?.Trim('/').Split('/').LastOrDefault();
            torrent.Types = category is null ? [] : MapCategory(category);
            observed |= TorrentObservedFields.Types;
        }

        if (metadata.TryGetValue("Раздают", out var seedersText) &&
            int.TryParse(Regex.Match(seedersText, @"\d+").Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var seeders))
        {
            torrent.Sid = seeders;
            observed |= TorrentObservedFields.Seeders;
        }
        if (metadata.TryGetValue("Качают", out var leechersText) &&
            int.TryParse(Regex.Match(leechersText, @"\d+").Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var leechers))
        {
            torrent.Pir = leechers;
            observed |= TorrentObservedFields.Leechers;
        }
        if (metadata.TryGetValue("Размер", out var sizeText))
        {
            var bytesMatch = Regex.Match(sizeText, @"\((?<bytes>\d+)\s*Bytes\)", RegexOptions.IgnoreCase);
            if (bytesMatch.Success && long.TryParse(bytesMatch.Groups["bytes"].Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactBytes))
            {
                torrent.Size = exactBytes;
                observed |= TorrentObservedFields.Size;
            }
        }
        if (metadata.TryGetValue("Добавлен", out var addedText) && TryParseLongRussianDate(addedText, out var added))
        {
            torrent.CreateTime = added.UtcDateTime;
            observed |= TorrentObservedFields.PublishDate;
        }
    }

    private static bool TryParseLongRussianDate(string value, out DateTimeOffset result)
    {
        result = default;
        var match = Regex.Match(value,
            @"(?<day>\d{1,2})\s+(?<month>[а-яё]+)\s+(?<year>\d{4})(?:\s+в\s+(?<hour>\d{1,2}):(?<minute>\d{2})(?::(?<second>\d{2}))?)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["day"].Value, out var day) ||
            !int.TryParse(match.Groups["year"].Value, out var year))
            return false;
        var month = match.Groups["month"].Value.ToLowerInvariant() switch
        {
            "января" => 1, "февраля" => 2, "марта" => 3, "апреля" => 4,
            "мая" => 5, "июня" => 6, "июля" => 7, "августа" => 8,
            "сентября" => 9, "октября" => 10, "ноября" => 11, "декабря" => 12,
            _ => 0
        };
        if (month == 0)
            return false;
        var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value) : 0;
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : 0;
        var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value) : 0;
        try
        {
            result = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromHours(3)).ToUniversalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
