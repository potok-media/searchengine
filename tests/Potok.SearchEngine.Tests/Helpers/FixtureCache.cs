using Potok.SearchEngine.Core.Interfaces;

namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>
///     Pass-through cache for tracker tests: factories always run, nothing is stored.
///     When <paramref name="stubSessionCookie"/> is provided, TryGetValue&lt;string&gt;
///     returns it — emulates a cached auth session for trackers with login.
/// </summary>
public sealed class FixtureCache(string? stubSessionCookie = null) : ICacheService
{
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null) => factory();

    public Task InvalidateAsync(string key) => Task.CompletedTask;

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null) => Task.CompletedTask;

    public bool TryGetValue<T>(string key, out T? value)
    {
        if (stubSessionCookie is not null && typeof(T) == typeof(string))
        {
            value = (T)(object)stubSessionCookie;
            return true;
        }
        value = default;
        return false;
    }
}
