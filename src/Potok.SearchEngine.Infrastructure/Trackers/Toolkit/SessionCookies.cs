using System.Collections.Concurrent;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Set-Cookie helpers for tracker login flows.
/// </summary>
public static class SessionCookies
{
    /// <summary>
    ///     Keeps only the name=value pair of a Set-Cookie header (attributes like path,
    ///     expires or HttpOnly never belong in a Cookie header); returns null for empty
    ///     or deleting (empty-value) cookies.
    /// </summary>
    public static string? Pair(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0].Trim();
        var separator = pair.IndexOf('=');
        return separator > 0 && separator < pair.Length - 1 ? pair : null;
    }
}

/// <summary>
///     Session-cookie store for one tracker: the configured cache, plus a process-wide
///     fallback slot when the cache is disabled (<see cref="ICacheService.SetAsync"/>
///     no-ops there, which otherwise turns every request into a fresh login and gets
///     the account/IP escalated to permanent captcha).
/// </summary>
public sealed class SessionCookieStore(ICacheService cache, bool cacheEnabled, TimeSpan expiry, string key)
{
    private static readonly ConcurrentDictionary<string, (string Cookie, DateTimeOffset ExpiresAt)> Fallback =
        new(StringComparer.Ordinal);

    public bool TryGet(out string? cookie)
    {
        if (cache.TryGetValue(key, out cookie) && !string.IsNullOrWhiteSpace(cookie))
            return true;

        if (!cacheEnabled &&
            Fallback.TryGetValue(key, out var fallback) &&
            fallback.ExpiresAt > DateTimeOffset.UtcNow)
        {
            cookie = fallback.Cookie;
            return true;
        }

        cookie = null;
        return false;
    }

    public async Task StoreAsync(string cookie)
    {
        await cache.SetAsync(key, cookie, expiry);
        if (!cacheEnabled)
            Fallback[key] = (cookie, DateTimeOffset.UtcNow.Add(expiry));
    }
}
