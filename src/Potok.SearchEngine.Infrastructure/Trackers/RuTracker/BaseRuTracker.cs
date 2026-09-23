using System.Net;
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
/// </summary>
public partial class BaseRuTracker : BaseTrackerSearch
{
    private const string CookieKey = "rutracker:cookie";
    private const string ParserVersion = "rutracker/2026-09-22";
    private const string LoggedInMarker = "id=\"logged-in-username\"";

    private readonly HtmlParser _parser = new();

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
        if (!CacheService.TryGetValue(CookieKey, out string? cookie))
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
                "RuTracker authorization failed: no session cookie after re-login.");

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
                "RuTracker still returns the login page after re-login.");

        return html;
    }

    private async Task<string> Authorize(bool reAuth = false, CancellationToken ct = default)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "login_username", Config.RuTracker.Authorization.Login },
            { "login_password", Config.RuTracker.Authorization.Password },
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

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        var cookie = string.Join("; ", cookies);

        // FlareSolverr follows the login 302, so success is a session cookie, not only Found.
        if (response.StatusCode is not HttpStatusCode.Found && !LooksLikeRuTrackerSession(cookie))
        {
            if (reAuth)
                return string.Empty;

            return await Authorize(true, ct);
        }

        await CacheService.SetAsync(CookieKey, cookie, TimeSpan.FromDays(Config.Cache.AuthExpiry));

        return cookie;
    }

    private static bool LooksLikeRuTrackerSession(string cookie)
    {
        return cookie.Contains("bb_session", StringComparison.OrdinalIgnoreCase)
               || cookie.Contains("bbuserid", StringComparison.OrdinalIgnoreCase)
               || cookie.Contains("bb_data", StringComparison.OrdinalIgnoreCase);
    }
}
