namespace Potok.SearchEngine.Core.Interfaces;

public interface ISubscribeService
{
    Task<bool> SubscribeAsync(long tmdbId, string media, string uid);
    Task<bool> UnSubscribeAsync(long tmdbId, string media, string uid);
    Task<bool> CheckSubscribeAsync(long tmdbId, string media, string uid);
    Task<IReadOnlyCollection<UserSubscriptionItem>> GetUserSubscriptionsAsync(string uid);
}
