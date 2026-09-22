using Potok.SearchEngine.Core.Enums;

namespace Potok.SearchEngine.Core.Interfaces;

/// <summary>
///     Поиск по конкретному трекеру.
/// </summary>
public interface ITrackerSearch
{
    TrackerType Tracker { get; }
    string TrackerName { get; }
    string Host { get; }

    /// <summary>
    ///     Выполняет поиск по строке запроса на выбранном трекере.
    /// </summary>
    Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default);
}
