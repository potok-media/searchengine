using System.Collections.Concurrent;
using System.Text;
using System.Web;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     RuTracker shared base: tracker identity plus the authenticated transport.
///     Session cookies are cached (<see cref="CacheService"/>, AuthExpiry) and every page
///     must carry the "logged-in-username" marker; a stale session triggers one re-login,
///     and a re-login that still lands on the login page throws a typed
///     <see cref="TrackerSearchErrorCode.Authentication"/> error instead of silently
///     returning the login HTML. List parsing lives in BaseRuTracker.List.cs, topic
///     details in BaseRuTracker.Details.cs, title/category helpers in their own partials.
///     Auth notes (verified live 2026-09-23): rutracker.org is behind a Cloudflare
///     challenge, so login goes through the FlareSolverr browser session, and password
///     login is additionally gated by an image captcha (cap_sid / cap_code_*) that we
///     cannot solve automatically — <c>authorization.cookie</c> (bb_session from a
///     browser) is the supported way in. When the configured cache is disabled
///     (CacheService.SetAsync no-ops), a process-wide fallback store keeps the session
///     cookie, otherwise every search re-logins and the tracker escalates to a permanent
///     captcha.
/// </summary>
public partial class BaseRuTracker : BaseTrackerSearch
{
    private const string CookieKey = "rutracker:cookie";
    private const string ParserVersion = "rutracker/2026-09-22";
    private const string LoggedInMarker = "id=\"logged-in-username\"";
    private const string CaptchaMarker = "name=\"cap_sid\"";
    private const string InvalidCredentialsMarker = "неверное/неактивное имя пользователя или неверный пароль";

    private static readonly ConcurrentDictionary<string, SessionCookie> FallbackSessions =
        new(StringComparer.Ordinal);

    private readonly HtmlParser _parser = new();
    private string? _authFailureReason;

    protected BaseRuTracker(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Rutracker;
    public override string TrackerName => "rutracker";
    public override string Host => "https://rutracker.org/";

    private string LoginUrl => Host + "forum/login.php";

    protected async Task<string> Get(
        string url,
        Encoding? encoding = null,
        string? referer = null,
        bool useProxy = false,
        CancellationToken ct = default)
    {
        if (!TryGetSessionCookie(out string? cookie))
            cookie = await Authorize(false, ct);

        var html = await HttpService.GetStringAsync(
            url,
            cookie: cookie,
            referer: referer,
            encoding: encoding ?? Encoding.UTF8,
            useProxy: useProxy,
            ct: ct);

        if (!string.IsNullOrWhiteSpace(html) && html.Contains(LoggedInMarker, StringComparison.Ordinal))
            return html;

        cookie = await Authorize(true, ct);
        if (string.IsNullOrWhiteSpace(cookie))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Authentication,
                $"RuTracker authorization failed: {_authFailureReason ?? "no session cookie after re-login."}");

        html = await HttpService.GetStringAsync(
            url,
            cookie: cookie,
            referer: referer,
            encoding: encoding ?? Encoding.UTF8,
            useProxy: useProxy,
            ct: ct);

        if (!string.IsNullOrWhiteSpace(html) &&
            !html.Contains(LoggedInMarker, StringComparison.Ordinal) &&
            TrackerResponseClassifier.IsAuthentication(html))
            throw new TrackerSearchException(Tracker, TrackerSearchErrorCode.Authentication,
                $"RuTracker still returns the login page after re-login. {_authFailureReason}".TrimEnd());

        return html;
    }

    private async Task<string> Authorize(bool reAuth = false, CancellationToken ct = default)
    {
        var authorization = Config.RuTracker.Authorization;

        // Explicit cookie auth: password login is gated by an image captcha, so a
        // bb_session cookie pasted from a browser session is the supported way in.
        if (!string.IsNullOrWhiteSpace(authorization.Cookie))
        {
            if (reAuth)
            {
                _authFailureReason =
                    "the configured authorization.cookie was rejected; paste a fresh bb_session from your browser.";
                return string.Empty;
            }

            var configured = authorization.Cookie.Trim();
            await StoreSessionCookieAsync(configured);
            return configured;
        }

        if (string.IsNullOrWhiteSpace(authorization.Login) ||
            string.IsNullOrWhiteSpace(authorization.Password))
        {
            _authFailureReason = "neither authorization.cookie nor login/password are configured.";
            return string.Empty;
        }

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "login_username", authorization.Login },
            { "login_password", authorization.Password },
            { "login", "Login" }
        };

        var formEncoded = string.Join("&",
            pairs.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}"));

        using var response = await HttpService.PostResponseAsync(
            LoginUrl,
            new StringContent(formEncoded, Encoding.Default, "application/x-www-form-urlencoded"),
            cookie: null,
            referer: null,
            encoding: null,
            useProxy: true,
            allowRedirect: false,
            ct: ct);

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        var cookie = string.Join("; ",
            setCookies.Select(SessionCookiePair).Where(pair => pair is not null));

        // Success is a session cookie: FlareSolverr follows the login 302 to a 200 page,
        // and a bare redirect without bb_session proves nothing.
        if (LooksLikeRuTrackerSession(cookie))
        {
            await StoreSessionCookieAsync(cookie);
            return cookie;
        }

        _authFailureReason = await ClassifyLoginFailureAsync(response, ct);

        // Captcha and rejected credentials will not heal by re-posting; only transient
        // answers (no body markers at all) earn one retry.
        if (reAuth || _authFailureReason is not null)
            return string.Empty;

        return await Authorize(true, ct);
    }

    /// <summary>
    ///     Reads the login response body and maps it to a human-readable failure reason,
    ///     or null when the body gave no definitive answer. Browser-fetched bodies are
    ///     already decoded unicode; direct bodies are win-1251.
    /// </summary>
    private async Task<string?> ClassifyLoginFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string html;
        try
        {
            if (response.Headers.Contains(TrackerHttpClient.BrowserFetchedHeader))
            {
                html = await response.Content.ReadAsStringAsync(ct);
            }
            else
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                html = RuEncoding.GetString(bytes);
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (html.Contains(CaptchaMarker, StringComparison.Ordinal) ||
            html.Contains("код подтверждения", StringComparison.OrdinalIgnoreCase))
            return "login requires an image captcha; configure rutracker.authorization.cookie " +
                   "from a browser session instead.";

        if (html.Contains(InvalidCredentialsMarker, StringComparison.OrdinalIgnoreCase))
            return "the tracker rejected the configured login/password.";

        return null;
    }

    private bool TryGetSessionCookie(out string? cookie)
    {
        if (CacheService.TryGetValue(CookieKey, out cookie) && !string.IsNullOrWhiteSpace(cookie))
            return true;

        if (!Config.Cache.Enable &&
            FallbackSessions.TryGetValue(CookieKey, out var fallback) &&
            fallback.ExpiresAt > DateTimeOffset.UtcNow)
        {
            cookie = fallback.Cookie;
            return true;
        }

        cookie = null;
        return false;
    }

    private async Task StoreSessionCookieAsync(string cookie)
    {
        var expiry = TimeSpan.FromDays(Config.Cache.AuthExpiry);
        await CacheService.SetAsync(CookieKey, cookie, expiry);
        if (!Config.Cache.Enable)
            FallbackSessions[CookieKey] = new SessionCookie(cookie, DateTimeOffset.UtcNow.Add(expiry));
    }

    /// <summary>
    ///     Keeps only the name=value pair of a Set-Cookie header (attributes like path,
    ///     expires or HttpOnly never belong in a Cookie header); returns null for empty
    ///     or deleting (empty-value) cookies.
    /// </summary>
    private static string? SessionCookiePair(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0].Trim();
        var separator = pair.IndexOf('=');
        return separator > 0 && separator < pair.Length - 1 ? pair : null;
    }

    private static bool LooksLikeRuTrackerSession(string cookie)
    {
        return cookie.Contains("bb_session", StringComparison.OrdinalIgnoreCase)
               || cookie.Contains("bbuserid", StringComparison.OrdinalIgnoreCase)
               || cookie.Contains("bb_data", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SessionCookie(string Cookie, DateTimeOffset ExpiresAt);
}
