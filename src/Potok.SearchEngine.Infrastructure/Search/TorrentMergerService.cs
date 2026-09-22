using Potok.SearchEngine.Core.Utils;

namespace Potok.SearchEngine.Infrastructure.Search;

public class TorrentMergerService : ITorrentMergerService
{
    public Task<List<TorrentDetails>> MergeAsync(IEnumerable<TorrentDetails> torrents)
    {
        var temp = new Dictionary<string, (TorrentDetails torrent, string? title, string? name, List<string> announceUrls)>();

        foreach (var torrent in torrents
                     .OrderByDescending(t => t.CreateTime)
                     .ThenBy(t => t.TrackerName == "selezen"))
        {
            var hex = MagnetBuilder.HashFromMagnet(torrent.Magnet);
            if (hex is null)
                continue;

            var name = MagnetName(torrent.Magnet);
            var announceUrls = MagnetBuilder.ExtractTrackers(torrent.Magnet).ToList();

            if (!temp.TryGetValue(hex, out var entry))
            {
                temp.Add(hex,
                    ((TorrentDetails)torrent.Clone(),
                        torrent.TrackerName == "kinozal" ? torrent.Title : null,
                        name,
                        announceUrls));
                continue;
            }

            // Объединяем имена трекеров
            if (!entry.torrent.TrackerName.Contains(torrent.TrackerName))
                entry.torrent.TrackerName += $", {torrent.TrackerName}";

            void UpdateMagnet()
            {
                var updated = BuildMagnet(hex, entry.name, entry.announceUrls);
                if (!string.IsNullOrWhiteSpace(updated))
                    entry.torrent.Magnet = updated;
            }

            // Обновляем имя из магнета, если его еще нет
            if (string.IsNullOrWhiteSpace(entry.name) && !string.IsNullOrWhiteSpace(name))
            {
                entry.name = name;
                temp[hex] = entry;
                UpdateMagnet();
            }

            // Добавляем новые анонс-урлы
            if (announceUrls.Count > 0)
            {
                foreach (var url in announceUrls)
                {
                    if (!entry.announceUrls.Contains(url))
                        entry.announceUrls.Add(url);
                }
                UpdateMagnet();
            }

            void UpdateTitle()
            {
                if (string.IsNullOrWhiteSpace(entry.title))
                    return;

                var title = entry.title;

                if (entry.torrent.Voices != null && entry.torrent.Voices.Count > 0)
                    title += $" | {string.Join(" | ", entry.torrent.Voices)}";

                entry.torrent.Title = title;
            }

            // Приоритет заголовка от kinozal
            if (torrent.TrackerName == "kinozal")
            {
                entry.title = torrent.Title;
                temp[hex] = entry;
                UpdateTitle();
            }

            // Объединяем голоса (озвучки)
            if (torrent.Voices != null && torrent.Voices.Count > 0)
            {
                if (entry.torrent.Voices == null)
                    entry.torrent.Voices = new HashSet<string>(torrent.Voices);
                else
                    foreach (var v in torrent.Voices)
                        entry.torrent.Voices.Add(v);

                UpdateTitle();
            }

            // Обновляем сидов/пиров (максимальное значение, кроме selezen)
            if (torrent.TrackerName != "selezen")
            {
                if (torrent.Sid > entry.torrent.Sid)
                    entry.torrent.Sid = torrent.Sid;

                if (torrent.Pir > entry.torrent.Pir)
                    entry.torrent.Pir = torrent.Pir;
            }

            if (torrent.CreateTime > entry.torrent.CreateTime)
                entry.torrent.CreateTime = torrent.CreateTime;

            // Объединяем языки
            if (torrent.Languages != null && torrent.Languages.Count > 0)
            {
                if (entry.torrent.Languages == null)
                    entry.torrent.Languages = new HashSet<string>();

                foreach (var v in torrent.Languages)
                    entry.torrent.Languages.Add(v);
            }

            temp[hex] = entry;
        }

        return Task.FromResult(temp.Select(i => i.Value.torrent).ToList());
    }

    private static string? BuildMagnet(string infoHash, string? name, List<string> announceUrls)
    {
        var magnet = MagnetBuilder.Build(infoHash, announceUrls);
        if (magnet is not null && !string.IsNullOrWhiteSpace(name))
            magnet += $"&dn={Uri.EscapeDataString(name)}";
        return magnet;
    }

    private static string? MagnetName(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) ||
            !Uri.TryCreate(magnet, UriKind.Absolute, out var uri))
            return null;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "dn", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }
}
