using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;

/// <summary>
///     AnimeLayer shared side: identity, auth-cookie session (login/password via
///     /auth/login/, cached under "animelayer:cookies"), response-surface classification
///     and bounded topic enrichment. List parsing lives in BaseAnimeLayer.List.cs, the
///     topic (detail) fetch in BaseAnimeLayer.Details.cs and text/fact helpers in
///     BaseAnimeLayer.Facts.cs.
/// </summary>
public partial class BaseAnimeLayer : BaseTrackerSearch
{
    protected const string CookieKey = "animelayer:cookies";
    private const string ParserVersion = "animelayer/2026-09-22";
    private readonly HtmlParser _parser = new();
    private readonly SessionCookieStore _sessionCookies;

    protected BaseAnimeLayer(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
        _sessionCookies = new SessionCookieStore(
            cacheService,
            Config.Cache.Enable,
            TimeSpan.FromDays(Config.Cache.AuthExpiry),
            CookieKey);
    }

    public override TrackerType Tracker => TrackerType.AnimeLayer;
    public override string TrackerName => "animelayer";
    public override string Host => "https://animelayer.ru";

    /// <summary>
    ///     Keeps one candidate per topic (best seeders first) and enriches anime videos
    ///     through <see cref="DetailEnricher"/> — at most 4 concurrent fetches, partial
    ///     results when the budget runs out.
    /// </summary>
    protected async Task<IReadOnlyCollection<TorrentDetails>> EnrichVideoTopicsAsync(
        IReadOnlyCollection<TorrentDetails> torrents,
        CancellationToken ct)
    {
        var candidates = torrents
            .Where(IsAnimeVideo)
            .OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .GroupBy(torrent => torrent.Source?.SourceKey ?? torrent.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return await DetailEnricher.EnrichTopicsAsync(candidates, FetchDetailsAsync, ct);
    }

    private static bool IsAnimeVideo(TorrentDetails torrent) =>
        torrent.Types is ["anime"];

    /// <summary>
    ///     Auth-aware GET: attaches the cached session cookie (authorizing first when
    ///     credentials are configured) and re-authorizes once when the response is a
    ///     login page. The response charset is honored (encoding: null) — AnimeLayer
    ///     serves UTF-8, older authenticated mirrors were Windows-1251.
    /// </summary>
    protected async Task<string> Get(string url, string? referer = null, CancellationToken ct = default)
    {
        _sessionCookies.TryGet(out string? cookie);
        if (string.IsNullOrWhiteSpace(cookie) && HasConfiguredCredentials())
            cookie = await Authorize(ct: ct);

        var html = await HttpService.GetStringAsync(url, cookie, referer, encoding: null, useProxy: true, ct);
        if (!IsAuthenticationResponse(html) || !HasConfiguredCredentials())
            return html;

        cookie = await Authorize(reAuth: true, ct);
        if (string.IsNullOrWhiteSpace(cookie))
            return html;
        return await HttpService.GetStringAsync(url, cookie, referer, encoding: null, useProxy: true, ct);
    }

    private async Task<string> Authorize(bool reAuth = false, CancellationToken ct = default)
    {
        if (!reAuth && _sessionCookies.TryGet(out string? cachedCookie))
            return cachedCookie!;

        var login = Config.AnimeLayer.Authorization.Login;
        var password = Config.AnimeLayer.Authorization.Password;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return string.Empty;

        var url = $"{Host}/auth/login/";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = login,
            ["password"] = password
        });
        using var response = await HttpService.PostResponseAsync(
            url, content, cookie: null, referer: url, encoding: null, useProxy: true, allowRedirect: false, ct);

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var cookie = string.Join("; ", cookies.Select(SessionCookies.Pair).OfType<string>());
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                await _sessionCookies.StoreAsync(cookie);
                return cookie;
            }
        }

        return string.Empty;
    }

    protected static bool IsSearchSurface(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelector("#wrapper .torrents-list, form[action*='/torrents/anime/']") is not null;
    }

    /// <summary>
    ///     AnimeLayer login markers: the dedicated /auth/login/ form (only when it carries
    ///     a password input) or the "must register or log in" notice.
    /// </summary>
    protected static bool IsAuthenticationResponse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var document = new HtmlParser().ParseDocument(html);
        if (document.QuerySelector("form[action*='/auth/login/'] input[type='password'], form[action*='/auth/login/'] input[name='password']") is not null)
            return true;
        var text = TrackerText.NormalizeText(document.Body?.TextContent ?? string.Empty);
        return text.Contains("необходимо зарегистрироваться или войти", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasConfiguredCredentials() =>
        !string.IsNullOrWhiteSpace(Config.AnimeLayer.Authorization.Login) &&
        !string.IsNullOrWhiteSpace(Config.AnimeLayer.Authorization.Password);

    private static bool IsAnimeCategory(IElement? category)
    {
        if (category is null) return false;
        var href = category.GetAttribute("href") ?? string.Empty;
        var text = TrackerText.NormalizeText(category.TextContent);
        return href.StartsWith("/torrents/anime", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "аниме", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "anime", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
