using System.Globalization;
using System.Text.RegularExpressions;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     Maps scrubbed RuTracker labelled facts onto the typed torrent fields.
/// </summary>
public partial class BaseRuTracker
{
    private static void ApplyNormalizedFacts(
        TorrentDetails torrent,
        IReadOnlyDictionary<string, string> facts,
        ref TorrentObservedFields observed)
    {
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

        var videoType = TrackerText.FindFact(facts, "Формат видео", "Видео", "Формат");
        if (videoType is not null)
        {
            torrent.VideoType = videoType;
            observed |= TorrentObservedFields.VideoType;
        }

        var languages = TrackerText.FindFact(facts, "Язык", "Язык аудио", "Аудио");
        if (languages is not null)
        {
            torrent.Languages = TrackerText.SplitFactValues(languages);
            observed |= TorrentObservedFields.Languages;
        }

        var voices = TrackerText.FindFact(facts, "Перевод", "Озвучивание", "Звук");
        if (voices is not null)
        {
            torrent.Voices = TrackerText.SplitFactValues(voices);
            observed |= TorrentObservedFields.Voices;
        }

        var seasonRaw = TrackerText.FindFact(facts, "Сезон", "Сезоны");
        if (seasonRaw is not null)
        {
            var seasons = Regex.Matches(seasonRaw, @"(?<!\d)\d{1,3}(?!\d)")
                .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
                .Where(value => value is > 0 and < 1000)
                .ToHashSet();
            if (seasons.Count > 0)
            {
                torrent.Seasons = seasons;
                observed |= TorrentObservedFields.Seasons;
            }
        }

        var sizeRaw = TrackerText.FindFact(facts, "Размер");
        if (sizeRaw is not null && TrackerText.TryParseSize(sizeRaw, out var sizeLabel, out var sizeBytes))
        {
            torrent.Size = sizeBytes;
            torrent.SizeName = sizeLabel;
            observed |= TorrentObservedFields.Size;
        }
    }
}
