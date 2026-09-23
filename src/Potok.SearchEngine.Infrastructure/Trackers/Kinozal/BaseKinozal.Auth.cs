using System.Text;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Kinozal;

/// <summary>
///     Kinozal auth side: session-cookie login flow against takelogin.php with cookie
///     caching, transparent re-auth on login-wall responses, and the response classifiers
///     (challenge / authentication / search surface) used by both search and detail fetches.
/// </summary>
public partial class BaseKinozal
{
    /// <summary>
    ///     GET with the cached session cookie; on a login-wall response and configured
    ///     credentials, re-authorizes once and retries the request.
    /// </summary>
    protected async Task<string> Get(string url, Encoding? encoding, CancellationToken ct)
    {
        if (!_sessionCookies.TryGet(out string? cookie))
            cookie = await Authorize(ct: ct);

        var html = await HttpService.GetStringAsync(url, cookie, url, encoding, true, ct);
        if (!IsAuthenticationResponse(html) ||
            string.IsNullOrWhiteSpace(Config.Kinozal.Authorization.Login) ||
            string.IsNullOrWhiteSpace(Config.Kinozal.Authorization.Password))
            return html;

        cookie = await Authorize(true, ct);
        if (string.IsNullOrWhiteSpace(cookie))
            return html;
        return await HttpService.GetStringAsync(url, cookie, url, encoding, true, ct);
    }

    private async Task<string> Authorize(bool reAuth = false, CancellationToken ct = default)
    {
        var login = Config.Kinozal.Authorization.Login;
        var password = Config.Kinozal.Authorization.Password;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return string.Empty;

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", login },
            { "password", password },
            { "returnto", "" }
        });

        var response = await HttpService.PostResponseAsync($"{Host}/takelogin.php", content, null, null, null, true,
            false, ct);

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var cookie = string.Join("; ", cookies.Select(SessionCookies.Pair).OfType<string>());
            if (string.IsNullOrWhiteSpace(cookie))
                return string.Empty;
            await _sessionCookies.StoreAsync(cookie);
            return cookie;
        }

        return string.Empty;
    }

    protected static bool IsChallengeResponse(string html) =>
        !string.IsNullOrWhiteSpace(html) && TrackerResponseClassifier.IsChallenge(html);

    protected static bool IsAuthenticationResponse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var document = new HtmlParser().ParseDocument(html);
        if (document.QuerySelector("form[action*='takelogin.php'], input[name='password'], input[name='username']")
            is not null)
            return true;
        var text = TrackerText.NormalizeText(document.Body?.TextContent ?? string.Empty);
        return text.Contains("Вход в систему", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("необходимо авторизоваться", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("требуется авторизация", StringComparison.OrdinalIgnoreCase);
    }
}
