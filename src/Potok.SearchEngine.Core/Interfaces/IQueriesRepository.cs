namespace Potok.SearchEngine.Core.Interfaces;

public interface IQueriesRepository
{
    /// <summary>
    ///     Возвращает список запросов, которые требуют обновления (last_refresh_time < olderThan или null).
    /// </summary>
    Task<IReadOnlyCollection<StaleQuery>> GetStaleSearchQueriesAsync(TimeSpan olderThan, int limit);

    /// <summary>
    ///     Сохраняет или обновляет статистику по поисковому запросу.
    /// </summary>
    Task TrackSearchQueryAsync(long tmdbId, string query);

    /// <summary>
    ///     Records every refresh attempt and advances freshness only for a durable success.
    /// </summary>
    Task RecordRefreshAttemptAsync(
        long tmdbId,
        bool succeeded,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Удаляет поисковый запрос, если на него больше нет активных подписок.
    /// </summary>
    Task RemoveQueryIfNoSubscriptionsAsync(long tmdbId);

    /// <summary>
    ///     Возвращает список подписок пользователя с их временем обновления.
    /// </summary>
    Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid);
}
